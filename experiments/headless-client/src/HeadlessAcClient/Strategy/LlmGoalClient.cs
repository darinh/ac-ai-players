// SPDX-License-Identifier: AGPL-3.0-or-later
// LlmGoalClient — thin HTTP+JSON wrapper around an OpenAI-compatible
// chat-completions endpoint. Default config: GitHub Models
// (`https://models.github.ai/inference/chat/completions`) authed
// with the user's `gh auth token`. All knobs are env-overridable.
//
// Env vars (in order of precedence):
//   AC_BOTS_LLM_ENDPOINT  - chat-completions URL
//                            default: https://models.github.ai/inference/chat/completions
//   AC_BOTS_LLM_MODEL     - primary model name
//                            default: meta/llama-3.3-70b-instruct
//   AC_BOTS_LLM_FALLBACK_MODELS
//                          - OPTIONAL comma/semicolon-separated extra models to
//                            rotate to when the primary returns HTTP 429
//                            (per-model-per-day quota exhausted). Unset =>
//                            single-model behaviour as before (the only
//                            difference is the model name is trimmed of
//                            surrounding whitespace), so single-model behaviour
//                            comparisons are not silently confounded. When set,
//                            a 429 transparently rotates to the next quota-fresh
//                            candidate within one CompleteAsync call, and the
//                            client sticks to the model that worked. The policy
//                            only sees a 429 once EVERY candidate is walled.
//   AC_BOTS_LLM_API_KEY   - bearer token
//                            fallback: OPENAI_API_KEY
//                            final fallback: `gh auth token` invocation
//
// Response handling:
//   - Sends `response_format: { type: "json_object" }` so the
//     model returns a JSON document, not prose.
//   - Returns the assistant message's `content` string as-is.
//     LlmGoalPolicy parses it into a Goal.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessAcClient.Strategy;

internal sealed class LlmGoalClient
{
    private const string DefaultEndpoint = "https://models.github.ai/inference/chat/completions";

    // meta/llama-3.3-70b-instruct verified working on GitHub Models
    // (flexguid01-run-01 spike: 12/20 LLM kickoffs succeeded). The
    // previous default `openai/gpt-4o-mini` is chronically 429-rate-
    // limited on the same endpoint — every spike using it burned the
    // Slice T backoff window within the first call. Override via
    // AC_BOTS_LLM_MODEL env var if you need a specific model.
    private const string DefaultModel    = "meta/llama-3.3-70b-instruct";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _explicitApiKey;

    // Ordered candidate models, primary first. With no fallbacks configured
    // this is a single-element list and the client behaves exactly like a
    // fixed-model client. When AC_BOTS_LLM_FALLBACK_MODELS (or the ctor arg)
    // names extra models, a 429 from the active model rotates to the next
    // candidate within a single CompleteAsync call (see CompleteAsync).
    private readonly IReadOnlyList<string> _models;

    private readonly object _gate = new();
    // Index into _models of the currently-preferred model. Advances (sticks)
    // to whichever model last answered successfully, so once rotation lands on
    // a quota-fresh model the client keeps using it next call instead of
    // re-probing the exhausted one every time.
    private int _activeIndex;

    /// <summary>Public for tests: lets a fake bearer token bypass `gh auth token`.</summary>
    public LlmGoalClient(
        HttpClient? http = null, string? endpoint = null, string? model = null,
        string? apiKey = null, string? fallbackModels = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _endpoint = endpoint
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_ENDPOINT")
            ?? DefaultEndpoint;
        var primary = model
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_MODEL")
            ?? DefaultModel;
        var fallbacks = fallbackModels
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_MODELS");
        _models = BuildModelList(primary, fallbacks);
        _explicitApiKey = apiKey
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    /// <summary>The model the client tries first on the next call — the
    /// primary, or whichever fallback rotation last settled on.</summary>
    public string Model
    {
        get { lock (_gate) return _models[_activeIndex]; }
    }

    public string Endpoint => _endpoint;

    /// <summary>All candidate models in rotation order (primary first).
    /// Diagnostic accessor; the active one is <see cref="Model"/>.</summary>
    public IReadOnlyList<string> Models => _models;

    // Parse a comma/semicolon-separated fallback list and prepend the primary,
    // de-duplicating (ordinal) while preserving order. Blank entries are
    // dropped, so an empty/whitespace fallback list yields just [primary].
    private static IReadOnlyList<string> BuildModelList(string primary, string? fallbackCsv)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string m)
        {
            var t = m.Trim();
            if (t.Length > 0 && seen.Add(t)) ordered.Add(t);
        }
        Add(primary);
        if (!string.IsNullOrWhiteSpace(fallbackCsv))
            foreach (var part in fallbackCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                Add(part);
        // Never return an empty roster (e.g. a blank primary with no fallbacks):
        // keep the raw primary so CompleteAsync always has one model to try,
        // matching the original fixed-model behaviour.
        if (ordered.Count == 0) ordered.Add(primary);
        // Truly immutable so a caller reading Models can't break the
        // "at least one model" invariant CompleteAsync relies on.
        return ordered.AsReadOnly();
    }

    public async Task<LlmResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var token = _explicitApiKey ?? await ResolveGhAuthTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return new LlmResult(Ok: false, Content: "", RawResponse: "", LatencyMs: 0, Error: "no api key (set AC_BOTS_LLM_API_KEY or run `gh auth login`)");

        // Build the rotation order for this call: start at the currently-
        // preferred model and walk the list once, wrapping. Each model is
        // tried at most once per call, so a fully-walled roster fails fast
        // instead of looping.
        int start;
        lock (_gate) start = _activeIndex;
        var n = _models.Count;

        LlmResult? lastQuota = null;
        LlmResult? soonestQuota = null;
        for (var step = 0; step < n; step++)
        {
            var idx = (start + step) % n;
            var model = _models[idx];
            var result = await SendOnceAsync(model, token, systemPrompt, userPrompt, ct).ConfigureAwait(false);

            if (result.Ok)
            {
                // Stick to the model that just worked so the next call starts
                // here rather than re-probing an exhausted earlier model.
                lock (_gate) _activeIndex = idx;
                return result;
            }

            // Rotate ONLY on a quota 429. Every other outcome (transport error,
            // 5xx, 413, parse failure) is returned as-is so the policy's
            // existing non-429 handling (e.g. adaptive prompt-ceiling on 413)
            // still applies and we don't thrash across models on a transient
            // blip or a misconfiguration.
            if (result.StatusCode == (HttpStatusCode)429)
            {
                lastQuota = result;
                // Remember the 429 whose Retry-After expires SOONEST: when every
                // model is walled the caller should resume as early as the first
                // one frees, not wait on the slowest. A 429 with no Retry-After
                // hint never displaces a known one.
                if (result.RetryAfter is { } ra &&
                    (soonestQuota?.RetryAfter is not { } best || ra < best))
                    soonestQuota = result;
                continue;
            }

            return result;
        }

        // Every candidate is quota-exhausted. Prefer the soonest-resuming
        // Retry-After hint so the policy's backoff window is correct; fall back
        // to the last 429 when no model supplied a hint.
        return soonestQuota ?? lastQuota!;
    }

    // One HTTP round-trip to a specific model. Owns all the wire concerns:
    // headers, JSON body, status + Retry-After decode, latency, and response
    // parse. CompleteAsync orchestrates which model(s) this is called for.
    private async Task<LlmResult> SendOnceAsync(
        string model, string token, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var payload = new ChatRequest
        {
            Model = model,
            ResponseFormat = new ResponseFormat { Type = "json_object" },
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user",   Content = userPrompt   },
            },
            Temperature = 0.2,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? resp = null;
        string raw;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SendAsync may have returned a live response before
            // ReadAsStringAsync threw (e.g. a mid-body cancellation); dispose it
            // here since the using-scope below is never entered on this path.
            resp?.Dispose();
            return new LlmResult(false, "", "", (int)sw.ElapsedMilliseconds, $"http error: {ex.Message}");
        }
        sw.Stop();

        // Dispose the response on every path below — rotation can issue several
        // requests per CompleteAsync call, so leaking them adds up.
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                // Capture HTTP status + (for 429) the server's Retry-After
                // hint. The Retry-After header on a 429 may be either a
                // delta-seconds integer (`Retry-After: 8`) or an HTTP-date.
                // resp.Headers.RetryAfter exposes both via RetryConditionHeaderValue
                // (Delta vs Date). We translate to a TimeSpan if present and
                // non-negative; LlmGoalPolicy decides how to weight it.
                TimeSpan? retryAfter = null;
                var ra = resp.Headers.RetryAfter;
                if (ra is not null)
                {
                    if (ra.Delta is { } delta && delta > TimeSpan.Zero)
                        retryAfter = delta;
                    else if (ra.Date is { } when)
                    {
                        var diff = when - DateTimeOffset.UtcNow;
                        if (diff > TimeSpan.Zero) retryAfter = diff;
                    }
                }
                return new LlmResult(false, "", raw, (int)sw.ElapsedMilliseconds,
                    $"http {(int)resp.StatusCode}: {resp.ReasonPhrase}",
                    StatusCode: resp.StatusCode,
                    RetryAfter: retryAfter);
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";
                return new LlmResult(true, content, raw, (int)sw.ElapsedMilliseconds, null);
            }
            catch (Exception ex)
            {
                return new LlmResult(false, "", raw, (int)sw.ElapsedMilliseconds, $"parse error: {ex.Message}");
            }
        }
    }

    private static async Task<string?> ResolveGhAuthTokenAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("messages")] public required ChatMessage[] Messages { get; init; }
        [JsonPropertyName("response_format")] public ResponseFormat? ResponseFormat { get; init; }
        [JsonPropertyName("temperature")] public double? Temperature { get; init; }
    }
    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }
    private sealed class ResponseFormat
    {
        [JsonPropertyName("type")] public required string Type { get; init; }
    }
}

internal sealed record LlmResult(
    bool Ok,
    string Content,
    string RawResponse,
    int LatencyMs,
    string? Error,
    HttpStatusCode? StatusCode = null,
    TimeSpan? RetryAfter = null);
