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
//                            client sticks to the model that worked. A walled
//                            model is then skipped (cooled down) until its
//                            Retry-After elapses, so a sustained quota wall does
//                            not re-probe known-walled models every call. The
//                            policy only sees a 429 once EVERY candidate is walled.
//                            Rotation/all-walled events are logged ([llm-fallback])
//                            so the resilience is visible in live runs; single-model
//                            clients log nothing.
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

    // Default model rotation. GitHub Models is rate-limited per-model-per-day, so
    // the bot must lean on a CHAIN of models, not one. The primary is the most
    // capable generally-available model; on a 429 (daily quota) the client rotates
    // to the next candidate within a single CompleteAsync call and STICKS to
    // whatever answers, ending at a high-availability model as a last resort so the
    // client keeps returning answers even when every higher-quality model is
    // quota-walled. Override via AC_BOTS_LLM_MODEL /
    // AC_BOTS_LLM_FALLBACK_MODELS. (History: earlier single defaults of
    // openai/gpt-4o-mini, then meta/llama-3.3-70b-instruct, shipped with NO
    // fallback chain — so an unconfigured run used ONE model and degraded to weak
    // decisions the instant it 429'd. gpt-4o-mini was noted as chronically
    // 429-prone, which is exactly why a rotation, not a single fixed model, is the
    // right default.)
    internal const string DefaultModel = "openai/gpt-4o";
    // Diverse-by-PROVIDER so a per-model-per-day 429 wall on one provider's models
    // rotates onto a DIFFERENT provider's separate daily-quota bucket, not just the
    // next model in the same bucket. Spans OpenAI, Microsoft, Mistral, Cohere, Meta
    // (all probed 200 OK on the running server and accept the ~26KB bot prompt). See
    // the provider-diversity regression test that guards this from collapsing to one
    // bucket. Override the whole chain with AC_BOTS_LLM_FALLBACK_MODELS.
    internal const string DefaultFallbackModels =
        "openai/gpt-4.1-mini;microsoft/phi-4;mistral-ai/mistral-small-2503;cohere/cohere-command-a;meta/llama-3.3-70b-instruct";

    private readonly HttpClient _http;
    // Resolved per-attempt HTTP timeouts. _httpTimeout is the primary model's
    // full budget (also the shared HttpClient.Timeout ceiling); _fallbackHttpTimeout
    // is the tighter budget applied to a non-primary attempt so a stalled fallback
    // is abandoned quickly and rotation reaches a responsive model, instead of
    // burning the full primary budget on every stalled fallback. See
    // ResolveFallbackHttpTimeout and SendOnceAsync.
    private readonly TimeSpan _httpTimeout;
    private readonly TimeSpan _fallbackHttpTimeout;
    private readonly string _endpoint;
    private readonly string? _explicitApiKey;

    // Ordered candidate models, primary first. With no fallbacks configured
    // this is a single-element list and the client behaves exactly like a
    // fixed-model client. When AC_BOTS_LLM_FALLBACK_MODELS (or the ctor arg)
    // names extra models, a 429 from the active model rotates to the next
    // candidate within a single CompleteAsync call (see CompleteAsync).
    private readonly IReadOnlyList<string> _models;

    // Indices into _models of "reserved top-tier" models (parsed from the ctor arg or
    // AC_BOTS_TOP_TIER_RESERVE). When NON-empty, a ROUTINE CompleteAsync call (directed:
    // false, the default) SKIPS these so their scarce per-day quota is preserved for the
    // rare DIRECTED decision (directed:true), which tries them FIRST. Empty (unset — the
    // default) = reservation OFF and CompleteAsync behaves exactly as before. Pure
    // config-driven index bookkeeping; no game knowledge, no priority over game objects.
    private readonly IReadOnlyList<int> _reservedIndices;

    private readonly object _gate = new();
    // Index into _models of the currently-preferred model. Advances (sticks)
    // to whichever model last answered successfully, so once rotation lands on
    // a quota-fresh model the client keeps using it next call instead of
    // re-probing the exhausted one every time.
    private int _activeIndex;

    private readonly Func<DateTimeOffset> _now;

    // Diagnostic sink for fallback-rotation events (model walled / rotated /
    // all walled). Defaults to Console so live runs SHOW the 429-resilience
    // working; tests inject a capturing delegate. Only emitted for multi-model
    // clients, so a single-model client stays silent.
    private readonly Action<string> _log;

    // Per-model "do not retry before" set from a 429's Retry-After. Lets
    // rotation SKIP a model already known to be walled instead of re-probing it
    // every call during a sustained multi-model quota wall (the wasted
    // round-trips show up as the "bots are slow" symptom). Consulted ONLY when
    // more than one model is configured — a single-model client never cools
    // down, so its behaviour is unchanged. Bookkeeping TTL keyed on model name;
    // guarded by _gate.
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil =
        new(StringComparer.Ordinal);

    // Global circuit-breaker: set when the ENTIRE reachable roster fails one
    // call with an infrastructure error (transport/timeout/5xx) and nothing
    // succeeds. While set, CompleteAsync short-circuits (no probing) so the bot
    // uses its autonomous policy immediately instead of re-paying a roster-worth
    // of client timeouts every decision. Cleared on any success. Multi-model
    // only; guarded by _gate.
    private DateTimeOffset? _infraBackoffUntil;

    // TTL bounds for a model cooldown. A 429 with no Retry-After hint gets the
    // default; one with a hint is clamped to [min, max]. Not game constants —
    // pure rate-limit bookkeeping.
    private static readonly TimeSpan DefaultModelCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinModelCooldown = TimeSpan.FromSeconds(1);
    // Cap on a 429 cooldown (resolved per-client; see ResolveMaxModelCooldown +
    // AC_BOTS_LLM_MAX_MODEL_COOLDOWN_MINUTES). GitHub Models' per-day quota wall
    // returns a Retry-After that can be many hours (up to the daily reset).
    // Honouring it verbatim strands the bot on a weaker fallback for that whole
    // window even when the quota RECOVERS EARLIER than the stated reset (observed
    // live: capable models answered a direct probe ~6h after their 429 wall, yet
    // the bot stayed on a weak fallback because their recorded cooldown had not
    // expired). Capping the cooldown (default 1h) lets the existing
    // PrimaryReprobeInterval re-probe (which is skipped while a model is cooling)
    // re-try a walled model within the cap of any early recovery and pick the
    // preferred model back up. A model that is genuinely walled longer simply 429s
    // again on the re-probe and re-cools — at most one extra probe per cap-window
    // per model. Transient 429s with a Retry-After under the cap are unaffected.
    private readonly TimeSpan _maxModelCooldown;

    // How long to skip LLM probing after the whole roster fails one call with an
    // infrastructure error (see _infraBackoffUntil). Short, so a recovered
    // endpoint is re-probed quickly; long enough that a sustained outage doesn't
    // re-pay N client timeouts every decision. Pure rate-limit bookkeeping.
    private static readonly TimeSpan InfraOutageBackoff = TimeSpan.FromSeconds(20);

    // Periodic re-probe of the PREFERRED (primary, index 0) model after rotation
    // has SETTLED on a weaker fallback due to a TRANSIENT (non-quota) failure.
    // Without this a single primary timeout permanently downgrades the whole run to
    // a weaker fallback: "stick to what worked" (_activeIndex follows the last
    // success) never re-tries the primary once a fallback answers, so one blip on
    // the capable model strands the bot on a weak one for the rest of the session.
    // Every PrimaryReprobeInterval, the next call restarts its try-order at the
    // primary so a recovered preferred model is picked back up. A still-429-walled
    // primary is still skipped by the cooldown set below; a still-down one costs at
    // most one probe per interval (not per call). Paced from the last fallback
    // settle (see the success branch). Pure availability bookkeeping; guarded by
    // _gate. NOT model selection by quality — the operator's configured ORDER is
    // the preference; this only restores it after a transient blip.
    private DateTimeOffset _lastPrimaryReprobeAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan PrimaryReprobeInterval = TimeSpan.FromSeconds(45);

    // ── Persistent-failover cooldown ─────────────────────────────────────────
    // A model that fails over (transport/5xx) is normally NOT cooled — it may be a
    // transient blip, so it stays eligible next call. But a model that fails this
    // way REPEATEDLY is not transient: re-probing it every PrimaryReprobeInterval
    // re-pays its full fallback timeout as dead latency (a live run wasted a large
    // fraction of wall time re-probing one fallback that timed out on ~every
    // attempt). So after _failoverCooldownThreshold CONSECUTIVE failover failures a
    // model is cooled down for FailoverCooldown (skipped in rotation, like a 429),
    // re-probed once when it expires, and re-benched if it fails again; ANY success
    // resets its streak. Below the threshold it stays eligible, so a one-off blip
    // behaves exactly as before. Per-model streak keyed on model name; guarded by
    // _gate.
    private readonly Dictionary<string, int> _consecutiveFailovers =
        new(StringComparer.Ordinal);
    private readonly int _failoverCooldownThreshold;
    // Per-model "benched until" from consecutive failover failures — SEPARATE from
    // the 429 _cooldownUntil so the empty-roster path can tell a quota wall from a
    // transport bench. A benched model is skipped in rotation, EXCEPT a bench is
    // IGNORED when it would empty the WHOLE roster (no 429 in play): a bench is only
    // worth honouring to skip a persistent failer while a healthy model remains;
    // with none left, re-probe so the 20s infra circuit-breaker paces recovery
    // instead of a full FailoverCooldown LLM stall on an endpoint that may recover
    // much sooner. ANY success clears it (with the streak). Guarded by _gate.
    private readonly Dictionary<string, DateTimeOffset> _failoverBenchedUntil =
        new(StringComparer.Ordinal);
    // Bench span. Must exceed PrimaryReprobeInterval so a benched model is skipped
    // ACROSS re-probes rather than re-probed (and re-timed-out) every interval.
    private static readonly TimeSpan FailoverCooldown = TimeSpan.FromSeconds(300);

    // Models that rejected our non-default `temperature` with a 400
    // (invalid_request_error / param="temperature" — some provider families only
    // accept the default). We send our preferred temperature by default and, on
    // that specific 400, record the model here and retry the SAME call once WITHOUT
    // temperature. Sticky for the process so later calls skip temperature up front.
    // Self-healing: no hardcoded model list, so a future temperature-strict model is
    // handled automatically. Guarded by _gate. The preferred temperature is below.
    private readonly HashSet<string> _temperatureUnsupported =
        new(StringComparer.Ordinal);
    // The temperature we prefer for goal generation (low = more deterministic). Sent
    // to every model that accepts it; omitted for models in _temperatureUnsupported.
    private const double PreferredTemperature = 0.2;

    // Per-call HttpClient timeout for an LLM request. Default RAISED from a
    // hardcoded 30s after a live run showed a single 30s timeout on the capable
    // model rotate the whole session to a weaker fallback (the large ~26 KB prompt
    // plus endpoint latency legitimately exceeds 30s under load). Override with
    // AC_BOTS_LLM_HTTP_TIMEOUT_SECONDS.
    //
    // The clamp ceiling is held strictly BELOW the primary re-probe interval for
    // two reasons:
    //   1. _lastPrimaryReprobeAt is stamped at call START, so a timeout >= the
    //      interval lets a single slow primary call push the NEXT call past the
    //      interval, making every decision re-probe the slow primary (a 50s
    //      timeout vs the 45s interval did exactly that). Staying under the
    //      interval keeps re-probes spaced one-per-window as designed.
    //   2. The interval (45s) is itself under the policy's ~60s CTS backstop
    //      (LlmGoalPolicy.LlmCallTimeout). Only the HttpClient timeout triggers
    //      transport FAILOVER to the next model; a CTS cancellation is treated as
    //      caller-cancellation (no failover). Firing the HttpClient timeout first
    //      keeps failover working. A value above the CTS would silently disable it.
    // Pure request-infrastructure tuning; no game knowledge, model-agnostic.
    internal static TimeSpan ResolveHttpTimeout(string? envValue)
    {
        const int minSeconds = 10;
        var maxSeconds = (int)PrimaryReprobeInterval.TotalSeconds - 1;   // 44s: under the re-probe interval (and the ~60s CTS)
        var defaultSeconds = Math.Max(minSeconds, Math.Min(40, maxSeconds));
        return int.TryParse(envValue, out var s) && s >= minSeconds && s <= maxSeconds
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(defaultSeconds);
    }

    // Per-attempt timeout for a NON-PRIMARY (fallback) model. Held well under the
    // primary budget so a stalled fallback is abandoned quickly and rotation
    // reaches a responsive candidate, instead of paying the full primary budget on
    // every stall. In practice a healthy call completes far under this; a fallback
    // that has not answered by the deadline is not worth waiting for while the
    // chain still has candidates to try. Clamp: [minSeconds, primaryTimeout] — a
    // fallback is never given longer than the primary budget. Override with
    // AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS. Pure request-infrastructure
    // tuning; no game knowledge, model-agnostic.
    internal static TimeSpan ResolveFallbackHttpTimeout(string? envValue, TimeSpan primaryTimeout)
    {
        const int minSeconds = 1;
        var maxSeconds = (int)primaryTimeout.TotalSeconds;
        var defaultSeconds = Math.Max(minSeconds, Math.Min(18, maxSeconds));
        return int.TryParse(envValue, out var s) && s >= minSeconds && s <= maxSeconds
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(defaultSeconds);
    }

    // Consecutive failover failures (transport/5xx) on ONE model before it is
    // cooled down (benched) in rotation, so a PERSISTENTLY failing/slow fallback
    // stops being re-probed — and re-paying its per-attempt timeout — every
    // re-probe interval. Default 3: a one-off blip (below 3) still stays eligible,
    // preserving prior behaviour; <=0 disables (never cool on a failover). Override
    // with AC_BOTS_LLM_FAILOVER_COOLDOWN_THRESHOLD. Pure request-infrastructure
    // tuning; model-agnostic, no game knowledge.
    internal static int ResolveFailoverCooldownThreshold(string? envValue)
        => int.TryParse(envValue, out var n) ? n : 3;

    // Ceiling on a per-model 429 cooldown, in minutes, from
    // AC_BOTS_LLM_MAX_MODEL_COOLDOWN_MINUTES (default 60). Accept only
    // [1min, 24h]: below 1min a tiny cap would re-probe a walled model almost
    // every call; above 24h exceeds a full daily wall. An out-of-range or
    // unparseable value falls back to the default (it is NOT clamped to the
    // nearest bound). Pure rate-limit tuning; model-agnostic, no game knowledge.
    internal const int DefaultMaxModelCooldownMinutes = 60;
    internal static TimeSpan ResolveMaxModelCooldown(string? envValue)
    {
        const int minMinutes = 1;
        const int maxMinutes = 24 * 60;
        return int.TryParse(envValue, out var m) && m >= minMinutes && m <= maxMinutes
            ? TimeSpan.FromMinutes(m)
            : TimeSpan.FromMinutes(DefaultMaxModelCooldownMinutes);
    }

    /// <summary>Public for tests: lets a fake bearer token bypass `gh auth token`.</summary>
    public LlmGoalClient(
        HttpClient? http = null, string? endpoint = null, string? model = null,
        string? apiKey = null, string? fallbackModels = null, Func<DateTimeOffset>? now = null,
        Action<string>? log = null, int? failoverCooldownThreshold = null, string? reservedModels = null,
        TimeSpan? maxModelCooldown = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? (s => Console.WriteLine(s));
        _failoverCooldownThreshold = failoverCooldownThreshold
            ?? ResolveFailoverCooldownThreshold(
                Environment.GetEnvironmentVariable("AC_BOTS_LLM_FAILOVER_COOLDOWN_THRESHOLD"));
        _maxModelCooldown = maxModelCooldown
            ?? ResolveMaxModelCooldown(
                Environment.GetEnvironmentVariable("AC_BOTS_LLM_MAX_MODEL_COOLDOWN_MINUTES"));
        _httpTimeout = ResolveHttpTimeout(
            Environment.GetEnvironmentVariable("AC_BOTS_LLM_HTTP_TIMEOUT_SECONDS"));
        _fallbackHttpTimeout = ResolveFallbackHttpTimeout(
            Environment.GetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS"),
            _httpTimeout);
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = _httpTimeout,
        };

        _endpoint = endpoint
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_ENDPOINT")
            ?? DefaultEndpoint;
        // Resolve the primary model; remember whether it came from the built-in
        // DEFAULT (no explicit model arg and no AC_BOTS_LLM_MODEL). The default
        // fallback CHAIN is only auto-applied for a fully-unconfigured client — a
        // caller that names its own primary (even a blank one, as a degenerate
        // fixed-model config) keeps the old behaviour of NO implicit fallbacks
        // unless it also names them.
        var primaryFromConfig = model
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_MODEL");
        var primary = primaryFromConfig ?? DefaultModel;
        var fallbacks = fallbackModels
            ?? Environment.GetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_MODELS")
            ?? (primaryFromConfig is null ? DefaultFallbackModels : null);
        _models = BuildModelList(primary, fallbacks);
        _reservedIndices = ParseReservedIndices(
            _models,
            reservedModels ?? Environment.GetEnvironmentVariable("AC_BOTS_TOP_TIER_RESERVE"));
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

    // Parse the reserved "top-tier" model list (comma/semicolon-separated names) into
    // the indices of `models` that match, preserving model order. Blank/unset yields an
    // EMPTY list (reservation OFF). A named model not present in the chain is ignored.
    // Never reserves the ENTIRE roster: if every model would be reserved, returns empty
    // (a reservation that leaves no routine model would starve routine calls — treat that
    // misconfiguration as OFF). Pure; no side effects.
    internal static IReadOnlyList<int> ParseReservedIndices(IReadOnlyList<string> models, string? reserveCsv)
    {
        if (string.IsNullOrWhiteSpace(reserveCsv) || models.Count == 0)
            return System.Array.Empty<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in reserveCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.Length > 0) names.Add(t);
        }
        var idx = new List<int>();
        for (var i = 0; i < models.Count; i++)
            if (names.Contains(models[i])) idx.Add(i);
        // Reserving the whole roster would leave routine calls with no model — treat as OFF.
        if (idx.Count >= models.Count) return System.Array.Empty<int>();
        return idx.AsReadOnly();
    }

    // Apply the top-tier reservation to this call's already-built try-order (indices into
    // _models, cooldown/bench filtering already applied). `reserved` is the reserved-model
    // index set. When `reserved` is empty the order is returned UNCHANGED (reservation OFF
    // — byte-identical to the pre-reservation behavior). Otherwise:
    //   - directed==true  : reserved indices that survived to `order` are tried FIRST (to
    //                        prefer the scarce top-tier for the important decision), then
    //                        the routine ones — no candidate is dropped, so a fully-walled
    //                        top-tier still falls through to routine models.
    //   - directed==false : the reserved indices are REMOVED (routine calls never spend
    //                        top-tier quota); if that empties the order the caller's
    //                        existing all-cooling backoff handles it.
    // Order among same-tier entries is preserved. Pure; returns a new list.
    internal static List<int> PartitionOrderForReservation(
        IReadOnlyList<int> order, IReadOnlyList<int> reserved, bool directed)
    {
        if (reserved is null || reserved.Count == 0) return new List<int>(order);
        var reservedSet = new HashSet<int>(reserved);
        if (directed)
        {
            var result = new List<int>(order.Count);
            foreach (var i in order) if (reservedSet.Contains(i)) result.Add(i);
            foreach (var i in order) if (!reservedSet.Contains(i)) result.Add(i);
            return result;
        }
        var routine = new List<int>(order.Count);
        foreach (var i in order) if (!reservedSet.Contains(i)) routine.Add(i);
        return routine;
    }

    public async Task<LlmResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default, bool directed = false)
    {
        var token = _explicitApiKey ?? await ResolveGhAuthTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return new LlmResult(Ok: false, Content: "", RawResponse: "", LatencyMs: 0, Error: "no api key (set AC_BOTS_LLM_API_KEY or run `gh auth login`)");

        var now = _now();

        // Build this call's try-order: start at the currently-preferred model
        // and walk the list once, wrapping, SKIPPING any model still cooling
        // down from a recent 429 (multi-model only). Each model is tried at most
        // once per call, so a fully-walled roster fails fast instead of looping.
        List<int> order;
        DateTimeOffset? soonestCooling;
        DateTimeOffset? infraBackoff;
        int start;
        lock (_gate)
        {
            infraBackoff = _infraBackoffUntil;
            start = _activeIndex;
            // Periodic preferred-model re-probe: if rotation has settled on a
            // non-primary model and a re-probe is due, restart this call's try-order
            // at the primary so a preferred model that recovered from a transient
            // blip is picked back up. A primary still 429-walled is passed over by
            // the cooldown skip below; a still-down one is paid at most once per
            // interval. Skip when the infra circuit-breaker is active so this call's
            // early short-circuit cannot consume the re-probe without probing (which
            // would delay the next real primary probe by an interval). Reset the
            // timer so the next re-probe is one interval out.
            if (start != 0
                && now - _lastPrimaryReprobeAt >= PrimaryReprobeInterval
                && !(infraBackoff is { } breakerUntilForReprobe && now < breakerUntilForReprobe))
            {
                start = 0;
                _lastPrimaryReprobeAt = now;
            }
            var multi = _models.Count > 1;
            order = new List<int>(_models.Count);
            soonestCooling = null;
            var benchedSkipped = 0;   // models skipped ONLY because of a failover bench
            for (var step = 0; step < _models.Count; step++)
            {
                var idx = (start + step) % _models.Count;
                var m = _models[idx];
                if (multi && _cooldownUntil.TryGetValue(m, out var until) && now < until)
                {
                    // 429 quota cooldown: skip, and remember the soonest reset for backoff.
                    if (soonestCooling is null || until < soonestCooling) soonestCooling = until;
                    continue;
                }
                if (multi && _failoverBenchedUntil.TryGetValue(m, out var bench) && now < bench)
                {
                    // Persistent transport/5xx failer: skip WHILE a healthy model remains.
                    benchedSkipped++;
                    continue;
                }
                order.Add(idx);
            }
            // Top-tier reservation (config-gated; no-op when _reservedIndices is empty).
            // Applied to the freshly-built order BEFORE the emergency bench-reprobe below, so
            // the reprobe (which fires only when the order is empty) stays the final word and
            // a degraded roster is never left un-probed by reservation. A ROUTINE call
            // (directed:false) drops the reserved models (preserving their scarce per-day
            // quota); a DIRECTED call tries them FIRST. If reservation empties a routine order
            // it either falls through to the reprobe (a benched model may recover) or the
            // 429 backoff below (soonestCooling is set by the cooldown skips, so a
            // reservation-emptied order can never hit the 1s MinModelCooldown spin).
            if (_reservedIndices.Count > 0)
                order = PartitionOrderForReservation(order, _reservedIndices, directed);
            // A bench is only worth honouring while a viable NON-benched candidate
            // remains. If EVERY eligible model is failover-benched — whether the rest
            // of the roster is idle OR hard-walled by a 429 — re-probe the benched ones
            // anyway: a 429'd model is NOT a viable recovery path, so sleeping out its
            // (possibly hour-long) Retry-After while a benched model's endpoint may
            // recover in ~20s is wrong. The re-probes fail and arm the 20s infra
            // circuit-breaker, which paces recovery, instead of a full FailoverCooldown
            // (or 429 Retry-After) LLM stall. 429-cooled models stay skipped here; a
            // PURE-429 empty roster (no benches) still falls through to the 429 backoff.
            if (order.Count == 0 && benchedSkipped > 0)
            {
                for (var step = 0; step < _models.Count; step++)
                {
                    var idx = (start + step) % _models.Count;
                    if (multi && _cooldownUntil.TryGetValue(_models[idx], out var u) && now < u) continue;
                    order.Add(idx);
                }
                // The reprobe rescanned the whole roster, re-adding reserved models too;
                // re-apply reservation so a ROUTINE call still prefers a non-reserved recovery
                // candidate over spending reserved quota (and a DIRECTED call still tries
                // reserved first). If every recovered candidate is reserved, a routine call
                // re-empties and takes the bounded 429 backoff rather than dipping into
                // reserved quota; the reserved model's bench clears via its TTL or a directed
                // call.
                if (_reservedIndices.Count > 0)
                    order = PartitionOrderForReservation(order, _reservedIndices, directed);
            }
        }
        var multiModel = _models.Count > 1;

        // Infra circuit-breaker (multi-model only): the whole reachable roster
        // recently failed one call with a transport/timeout/5xx error and nothing
        // has succeeded since, so the endpoint is (still) down. Skip probing for
        // the backoff window — otherwise every decision re-pays up to a
        // roster-worth of client timeouts before falling back. The bot runs on
        // its autonomous policy meanwhile; any success clears the breaker.
        if (multiModel && infraBackoff is { } breakerUntil && now < breakerUntil)
        {
            var backoff = breakerUntil - now;
            SafeLog($"[llm-fallback] endpoint unreachable; infra backoff {backoff.TotalSeconds:F0}s (autonomous policy meanwhile)");
            return new LlmResult(false, "", "", 0, "llm endpoint unreachable (infra backoff)",
                RetryAfter: backoff, FailureKind: LlmFailureKind.Transport);
        }

        // Every model is cooling down from a recent 429: don't burn round-trips
        // re-probing known-walled models — surface a 429 whose Retry-After is the
        // soonest model's reset so the policy backs off exactly until then.
        if (order.Count == 0)
        {
            var wait = soonestCooling is { } exp && exp > now ? exp - now : MinModelCooldown;
            SafeLog($"[llm-fallback] all {_models.Count} models cooling down (429 quota); backing off {wait.TotalSeconds:F0}s");
            return new LlmResult(false, "", "", 0, "all models cooling down (429 quota)",
                StatusCode: (HttpStatusCode)429, RetryAfter: wait);
        }

        LlmResult? lastQuota = null;
        LlmResult? soonestQuota = null;
        LlmResult? lastFailover = null;
        foreach (var idx in order)
        {
            var model = _models[idx];
            // The configured primary (index 0) keeps the full HttpClient.Timeout
            // budget; every fallback gets the tighter per-attempt deadline so one
            // stalled fallback can't burn the whole primary budget before rotating.
            var attemptTimeout = idx == 0 ? (TimeSpan?)null : _fallbackHttpTimeout;
            var result = await SendOnceAsync(model, token, systemPrompt, userPrompt, attemptTimeout, ct).ConfigureAwait(false);

            if (result.Ok)
            {
                // Stick to the model that just worked, clear any stale cooldown
                // on it, and lift the infra circuit-breaker — the endpoint is
                // reachable again.
                lock (_gate) { _activeIndex = idx; _cooldownUntil.Remove(model); _consecutiveFailovers.Remove(model); _failoverBenchedUntil.Remove(model); _infraBackoffUntil = null; }
                if (multiModel && idx != start)
                    SafeLog($"[llm-fallback] rotated to {model} (now the active model)");
                return result;
            }

            // Rotate on a quota 429: cool the model down for its Retry-After
            // window (a sustained wall) and try the next candidate.
            if (result.StatusCode == (HttpStatusCode)429)
            {
                RecordCooldown(model, result.RetryAfter);
                if (multiModel)
                    SafeLog($"[llm-fallback] {model} returned 429 (quota); rotating to next candidate");
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

            // Rotate on an infrastructure failure too: a transport error /
            // client timeout (no clean HTTP status was reached) or a 5xx server
            // error. A daily-quota wall on a large prompt frequently surfaces as
            // a connection reset / stream-copy failure or a 30s timeout rather
            // than a clean 429, and a 5xx means that model's endpoint is down —
            // either way a healthy fallback model still answers. Unlike a 429
            // there is NO per-model cooldown: it may be a transient blip, so the
            // model stays eligible next call (and the "stick to what worked"
            // index naturally avoids re-probing it once a fallback succeeds).
            // 413 / other 4xx / parse failures are request/content problems
            // another model would hit identically, so they return as-is for the
            // adaptive prompt-ceiling and misconfiguration handling.
            if (multiModel && IsFailoverCandidate(result))
            {
                // Track consecutive failovers for this model; a PERSISTENT failer
                // (>= threshold in a row without a success) is benched like a 429 so
                // the periodic primary-re-probe stops re-paying its timeout every
                // interval. A one-off blip (below the threshold, or threshold <=0)
                // stays eligible next call exactly as before.
                int streak;
                lock (_gate)
                {
                    _consecutiveFailovers.TryGetValue(model, out var prev);
                    streak = prev + 1;
                    _consecutiveFailovers[model] = streak;
                }
                if (_failoverCooldownThreshold > 0 && streak >= _failoverCooldownThreshold)
                {
                    lock (_gate) _failoverBenchedUntil[model] = _now() + FailoverCooldown;
                    SafeLog($"[llm-fallback] {model} failed ({result.Error}); {streak} consecutive " +
                            $"failovers -> benched {FailoverCooldown.TotalSeconds:F0}s (skipped while a healthy model remains)");
                }
                else
                {
                    SafeLog($"[llm-fallback] {model} failed ({result.Error}); rotating to next candidate");
                }
                lastFailover = result;
                continue;
            }

            return result;
        }

        // Loop exhausted: every reachable candidate failed with a rotatable
        // outcome. If the whole probed roster failed purely on infrastructure
        // (transport/5xx) with NO quota involved, the endpoint is down — arm the
        // circuit-breaker so the next calls short-circuit instead of re-probing
        // (and re-paying client timeouts). A quota failure does NOT arm it: the
        // per-model 429 cooldowns already pace re-probing and a fresh model may
        // appear when a cooldown expires.
        if (multiModel && lastFailover is not null && lastQuota is null && soonestQuota is null)
            lock (_gate) _infraBackoffUntil = _now() + InfraOutageBackoff;

        // Prefer the soonest-resuming 429 hint so the policy's backoff window is
        // correct; else the last 429; else the last infra failure (at least one
        // is set, since order.Count >= 1 here and every non-returning branch
        // records one).
        if (multiModel)
            SafeLog($"[llm-fallback] all {order.Count} candidate models walled or unreachable (429 quota / transport)");
        return soonestQuota ?? lastQuota ?? lastFailover!;
    }

    // Mark a model as cooling down after a 429 (multi-model only): skip it in
    // rotation until its Retry-After elapses, so a sustained quota wall does not
    // re-probe known-walled models every call. A single-model client never cools
    // down — it always re-probes its only option, preserving prior behaviour.
    private void RecordCooldown(string model, TimeSpan? retryAfter)
    {
        if (_models.Count <= 1) return;
        TimeSpan span;
        if (retryAfter is { } ra)
        {
            if (ra < MinModelCooldown)
                span = MinModelCooldown;
            else if (ra > _maxModelCooldown)
            {
                // A daily-quota 429's Retry-After can far exceed the cap; capping it
                // lets the periodic primary re-probe re-try this model within the cap
                // of any early recovery instead of honouring the full (up-to-daily)
                // window. Log when the cap engages so the fix is observable.
                SafeLog($"[llm-cooldown] {model} 429 Retry-After {ra.TotalHours:F1}h exceeds cap; cooling {_maxModelCooldown.TotalHours:F1}h for early re-probe");
                span = _maxModelCooldown;
            }
            else
                span = ra;
        }
        else
            span = DefaultModelCooldown;
        // Anchor at the moment the 429 was OBSERVED, not the CompleteAsync-start
        // snapshot: the awaited HTTP round-trip can take seconds, and the
        // server's Retry-After is relative to when it answered — anchoring at
        // call-start would expire the cooldown early and re-probe too soon.
        lock (_gate) _cooldownUntil[model] = _now() + span;
    }

    // A failure worth trying the NEXT fallback model for: an infrastructure
    // failure another model would NOT hit identically. Two cases:
    //   - Transport: a transport error / client timeout that never reached a
    //     clean HTTP status (a quota wall on a large prompt often surfaces this
    //     way instead of a clean 429).
    //   - 5xx: the model's endpoint returned a server error — that endpoint is
    //     down, but a fallback model's may be up.
    // A quota 429 is handled separately (per-model cooldown). 413 / other 4xx
    // (request-specific), parse failures, and caller-cancellations are NOT
    // failover candidates — they return as-is so the policy's existing handling
    // (adaptive prompt-ceiling on 413, etc.) applies and we don't thrash models
    // on a request-specific problem.
    private static bool IsFailoverCandidate(LlmResult r) =>
        r.FailureKind == LlmFailureKind.Transport ||
        (r.FailureKind == LlmFailureKind.Http && r.StatusCode is { } sc && (int)sc >= 500);

    // A 400 body that specifically rejects the `temperature` parameter (an
    // invalid_request_error / unsupported_value for param "temperature"), vs any
    // OTHER 400 (e.g. a 413 oversize, a content-policy block, or a malformed
    // request). Matched loosely on the provider's error shape so a minor wording
    // change still triggers the retry-without-temperature path. Pure; no state.
    internal static bool IsTemperatureRejection(string? body) =>
        body is not null
        && body.Contains("temperature", StringComparison.OrdinalIgnoreCase)
        && (body.Contains("unsupported_value", StringComparison.OrdinalIgnoreCase)
            || body.Contains("does not support", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Only the default", StringComparison.OrdinalIgnoreCase));

    // Diagnostics must never alter the LLM call path: a throwing log sink is
    // swallowed so a bad logger can't turn a returned LlmResult into a thrown
    // exception.
    private void SafeLog(string line)
    {
        try { _log(line); }
        catch { /* diagnostics are best-effort */ }
    }

    // One HTTP round-trip to a specific model. Owns all the wire concerns:
    // headers, JSON body, status + Retry-After decode, latency, and response
    // parse. CompleteAsync orchestrates which model(s) this is called for.
    private async Task<LlmResult> SendOnceAsync(
        string model, string token, string systemPrompt, string userPrompt,
        TimeSpan? attemptTimeout, CancellationToken ct)
    {
        // Send our preferred temperature unless this model previously rejected it
        // (then omit it, so the JSON drops the field via WhenWritingNull). Read once
        // here and reused by the 400 retry branch below.
        bool includeTemperature;
        lock (_gate) includeTemperature = !_temperatureUnsupported.Contains(model);
        var payload = new ChatRequest
        {
            Model = model,
            ResponseFormat = new ResponseFormat { Type = "json_object" },
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user",   Content = userPrompt   },
            },
            Temperature = includeTemperature ? PreferredTemperature : (double?)null,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        // A non-primary (fallback) attempt gets a tighter per-attempt deadline via
        // a linked CTS so a stalled fallback is abandoned quickly and rotation
        // reaches a responsive model. When it fires the caller-supplied ct is NOT
        // signalled, so the catch below classifies it as a Transport failure
        // (failover) exactly like the shared HttpClient.Timeout ceiling — only a
        // real caller cancellation (ct signalled) is a no-failover exit. The
        // primary attempt passes attemptTimeout=null and runs under the shared
        // HttpClient.Timeout unchanged.
        using var attemptCts = attemptTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (attemptCts is not null) attemptCts.CancelAfter(attemptTimeout!.Value);
        var sendToken = attemptCts?.Token ?? ct;

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? resp = null;
        string raw;
        try
        {
            resp = await _http.SendAsync(req, sendToken).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(sendToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SendAsync may have returned a live response before
            // ReadAsStringAsync threw (e.g. a mid-body cancellation); dispose it
            // here since the using-scope below is never entered on this path.
            resp?.Dispose();
            // A caller-requested cancellation (bot shutting down) is NOT a
            // failover candidate — don't burn the remaining models on the way
            // out. A client TIMEOUT also surfaces as OperationCanceledException
            // but with ct NOT signalled; that IS a transport failure worth
            // failing over, as is any other send/read error.
            var callerCancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            return new LlmResult(false, "", "", (int)sw.ElapsedMilliseconds, $"http error: {ex.Message}",
                FailureKind: callerCancelled ? LlmFailureKind.None : LlmFailureKind.Transport);
        }
        sw.Stop();

        // A model that rejects our non-default temperature (400 with an
        // invalid_request_error for param "temperature") is retried ONCE without it:
        // mark it sticky, dispose this response, and re-issue the SAME call. The retry
        // reads the mark (includeTemperature == false) so it omits temperature and
        // cannot loop. This lets the roster include capable models that only accept the
        // default temperature, with no hardcoded model list. Only the FIRST such 400
        // per model logs. When temperature was already omitted (includeTemperature ==
        // false), a 400 is NOT a temperature issue and falls through to normal handling.
        if (includeTemperature
            && resp.StatusCode == HttpStatusCode.BadRequest
            && IsTemperatureRejection(raw))
        {
            resp.Dispose();
            bool firstMark;
            lock (_gate) firstMark = _temperatureUnsupported.Add(model);
            if (firstMark)
                SafeLog($"[llm] model '{model}' rejects a non-default temperature; " +
                        "retrying without it (sticky for this run)");
            return await SendOnceAsync(model, token, systemPrompt, userPrompt, attemptTimeout, ct)
                .ConfigureAwait(false);
        }

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
                    RetryAfter: retryAfter,
                    FailureKind: LlmFailureKind.Http);
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
                return new LlmResult(false, "", raw, (int)sw.ElapsedMilliseconds, $"parse error: {ex.Message}",
                    FailureKind: LlmFailureKind.Parse);
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

// How a non-Ok LlmResult failed — drives whether CompleteAsync fails over to
// the next fallback model. Transport (a transport error / client timeout that
// never reached a clean HTTP status) and an Http 5xx (server error) are
// infrastructure failures that trigger failover; an Http 429 is handled
// separately (cooldown + rotate); Http 413 / other 4xx and Parse are
// request/content problems returned as-is for the caller's existing handling.
internal enum LlmFailureKind
{
    None,
    Transport,
    Http,
    Parse,
}

internal sealed record LlmResult(
    bool Ok,
    string Content,
    string RawResponse,
    int LatencyMs,
    string? Error,
    HttpStatusCode? StatusCode = null,
    TimeSpan? RetryAfter = null,
    LlmFailureKind FailureKind = LlmFailureKind.None);
