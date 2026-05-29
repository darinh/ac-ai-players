// SPDX-License-Identifier: AGPL-3.0-or-later
// LlmGoalClient — thin HTTP+JSON wrapper around an OpenAI-compatible
// chat-completions endpoint. Default config: GitHub Models
// (`https://models.github.ai/inference/chat/completions`) authed
// with the user's `gh auth token`. All knobs are env-overridable.
//
// Env vars (in order of precedence):
//   AC_BOTS_LLM_ENDPOINT  - chat-completions URL
//                            default: https://models.github.ai/inference/chat/completions
//   AC_BOTS_LLM_MODEL     - model name
//                            default: openai/gpt-4o-mini
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
using System.Diagnostics;
using System.IO;
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
    private readonly string _model;
    private readonly string? _explicitApiKey;

    /// <summary>Public for tests: lets a fake bearer token bypass `gh auth token`.</summary>
    public LlmGoalClient(HttpClient? http = null, string? endpoint = null, string? model = null, string? apiKey = null)
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
        _model = model
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_MODEL")
            ?? DefaultModel;
        _explicitApiKey = apiKey
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    public string Model => _model;
    public string Endpoint => _endpoint;

    public async Task<LlmResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var token = _explicitApiKey ?? await ResolveGhAuthTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return new LlmResult(Ok: false, Content: "", RawResponse: "", LatencyMs: 0, Error: "no api key (set AC_BOTS_LLM_API_KEY or run `gh auth login`)");

        var payload = new ChatRequest
        {
            Model = _model,
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
        HttpResponseMessage resp;
        string raw;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new LlmResult(false, "", "", (int)sw.ElapsedMilliseconds, $"http error: {ex.Message}");
        }
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            return new LlmResult(false, "", raw, (int)sw.ElapsedMilliseconds, $"http {(int)resp.StatusCode}: {resp.ReasonPhrase}");

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

internal sealed record LlmResult(bool Ok, string Content, string RawResponse, int LatencyMs, string? Error);
