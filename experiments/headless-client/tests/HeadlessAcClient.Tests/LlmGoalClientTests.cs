// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for LlmGoalClient automatic fallback-model rotation.
//
// The bot's brain talks to GitHub Models, which is rate-limited
// per-model-per-day. With a single configured model, one HTTP 429 makes
// the policy back off (idle on the autonomous fallback) for the whole
// Retry-After window. These tests pin the opt-in rotation behaviour: when
// AC_BOTS_LLM_FALLBACK_MODELS (or the constructor arg) names extra
// candidates, a 429 from the active model transparently rotates to the next
// quota-fresh candidate WITHIN the same CompleteAsync call, so the policy
// only ever sees a 429 when EVERY candidate is walled. With no fallbacks
// configured the behaviour is byte-identical to a single-model client.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class LlmGoalClientTests
{
    private const string Endpoint = "https://test.example/chat";

    // ---- default fallback chain: provider diversity (quota-bucket) guard ----

    [Fact]
    public void DefaultFallbackChain_SpansMultipleProviders_ForSeparateQuotaBuckets()
    {
        // GitHub Models rate-limits per-model-per-day; models from the SAME provider
        // often share a bucket, so a fallback chain confined to one provider can wall
        // ALL at once. The default chain must span several distinct providers so a
        // 429 on one provider rotates onto a DIFFERENT daily-quota bucket. This guards
        // the documented regression where a single-provider default degraded the bot
        // to weak decisions the instant it 429'd.
        var chain = (LlmGoalClient.DefaultModel + ";" + LlmGoalClient.DefaultFallbackModels)
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(chain.Length >= 4, $"default chain has only {chain.Length} models");

        // Provider = the token before '/' (e.g. "openai" in "openai/gpt-4o"). Every
        // entry must be provider-qualified.
        foreach (var m in chain)
            Assert.Contains('/', m);

        var providers = chain
            .Select(m => m.Split('/', 2)[0].ToLowerInvariant())
            .Distinct()
            .ToArray();
        Assert.True(providers.Length >= 4,
            $"default chain spans only {providers.Length} providers ({string.Join(",", providers)}); " +
            "want >= 4 distinct daily-quota buckets");

        // The primary must be first and part of the chain.
        Assert.Equal(LlmGoalClient.DefaultModel, chain[0]);
    }

    // ---- single-model (no fallback) = unchanged behaviour ----

    [Fact]
    public async Task SingleModel_Success_ReturnsContent_AndModelIsPrimary()
    {
        var handler = new ModelRoutingHandler(model => Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "primary", "key");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-primary", result.Content);
        Assert.Equal("primary", llm.Model);
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
    }

    [Fact]
    public async Task SingleModel_429_ReturnsFailure_AndDoesNotRotate()
    {
        var handler = new ModelRoutingHandler(_ => TooMany());
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "primary", "key");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal((HttpStatusCode)429, result.StatusCode);
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
    }

    // ---- fallback rotation ----

    [Fact]
    public async Task Fallback_PrimaryReturns429_RotatesToFallback_Succeeds()
    {
        var handler = new ModelRoutingHandler(model =>
            model == "primary" ? TooMany() : Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-fallback-a", result.Content);
        Assert.Equal("fallback-a", llm.Model);
        Assert.Equal(new[] { "primary", "fallback-a" }, handler.RequestedModels);
    }

    [Fact]
    public async Task Fallback_PrimarySucceeds_FallbackNeverCalled()
    {
        var handler = new ModelRoutingHandler(model => Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a,fallback-b");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-primary", result.Content);
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
    }

    [Fact]
    public async Task Fallback_AllModelsReturn429_ReturnsAQuotaFailure_WithRetryAfterPreserved()
    {
        var retryAfter = TimeSpan.FromSeconds(42);
        var handler = new ModelRoutingHandler(model =>
            model == "fallback-b" ? TooMany(retryAfter) : TooMany());
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a,fallback-b");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal((HttpStatusCode)429, result.StatusCode);
        // Every candidate is tried exactly once, in order.
        Assert.Equal(new[] { "primary", "fallback-a", "fallback-b" }, handler.RequestedModels);
        // The only server Retry-After hint is surfaced so the policy backs off
        // for the right window once everything is walled.
        Assert.Equal(retryAfter, result.RetryAfter);
    }

    [Fact]
    public async Task Fallback_AllModelsReturn429_PrefersSoonestRetryAfter()
    {
        // primary says "wait an hour", fallback says "wait 30s". With every
        // model walled, the caller should resume as soon as the FIRST one frees,
        // so the soonest (30s) Retry-After wins, not the last/longest.
        var handler = new ModelRoutingHandler(model =>
            model == "primary" ? TooMany(TimeSpan.FromHours(1)) : TooMany(TimeSpan.FromSeconds(30)));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public async Task Fallback_AllModelsReturn429_KnownRetryAfterBeatsMissingHint()
    {
        // primary supplies a Retry-After; the (later) fallback does not. A 429
        // with no hint must not displace the known one.
        var handler = new ModelRoutingHandler(model =>
            model == "primary" ? TooMany(TimeSpan.FromSeconds(90)) : TooMany());
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RetryAfter);
    }

    [Fact]
    public void Models_AreImmutable()
    {
        var llm = new LlmGoalClient(
            new HttpClient(new ModelRoutingHandler(_ => TooMany())),
            Endpoint, "primary", "key", fallbackModels: "fallback-a");

        Assert.Equal(new[] { "primary", "fallback-a" }, llm.Models);
        // The roster backing CompleteAsync's "at least one model" invariant must
        // not be mutable through the public accessor.
        Assert.Throws<NotSupportedException>(() => ((IList<string>)llm.Models).Add("sneaky"));
    }

    [Fact]
    public async Task Fallback_RotationTriesEachModelAtMostOncePerCall()
    {
        var handler = new ModelRoutingHandler(_ => TooMany());
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "a,b");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal(3, handler.RequestedModels.Count);
        Assert.Equal(new[] { "primary", "a", "b" }, handler.RequestedModels);
    }

    [Fact]
    public async Task Fallback_AfterRotation_NextCallStartsAtWorkingModel_Sticky()
    {
        var handler = new ModelRoutingHandler(model =>
            model == "primary" ? TooMany() : Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var first = await llm.CompleteAsync("sys", "user");
        Assert.True(first.Ok);
        Assert.Equal("fallback-a", llm.Model);

        handler.RequestedModels.Clear();
        var second = await llm.CompleteAsync("sys", "user");

        Assert.True(second.Ok);
        Assert.Equal("content-from-fallback-a", second.Content);
        // The exhausted primary is NOT re-probed: the working model is sticky.
        Assert.Equal(new[] { "fallback-a" }, handler.RequestedModels);
    }

    // ---- non-quota errors must NOT trigger rotation ----

    [Fact]
    public async Task NonQuotaError_5xx_RotatesToFallback_Succeeds()
    {
        // A 5xx means the primary model's endpoint is down; a fallback model's
        // may be up, so it is an infrastructure failover candidate (like a
        // transport error) — unlike a request-specific 4xx.
        var handler = new ModelRoutingHandler(model =>
            model == "primary"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
                : Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-fallback-a", result.Content);
        Assert.Equal(new[] { "primary", "fallback-a" }, handler.RequestedModels);
    }

    [Fact]
    public async Task NonQuotaError_413_DoesNotRotate_ReturnsError()
    {
        var handler = new ModelRoutingHandler(model =>
            model == "primary"
                ? new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("too big") }
                : Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, result.StatusCode);
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
    }

    // ---- fallback-list parsing ----

    [Fact]
    public async Task FallbackModels_ParsedDeduped_PrimaryExcluded_OrderPreserved()
    {
        // Duplicates and the primary itself appear in the fallback list; the
        // effective rotation order must be [primary, a, b] with no repeats.
        var handler = new ModelRoutingHandler(model =>
            model == "b" ? Ok("content-from-b") : TooMany());
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key",
            fallbackModels: "primary, a , b, a, primary");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-b", result.Content);
        Assert.Equal(new[] { "primary", "a", "b" }, handler.RequestedModels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",  ,")]
    public async Task EmptyOrBlankFallbackList_BehavesAsSingleModel(string fallback)
    {
        var handler = new ModelRoutingHandler(_ => TooMany());
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: fallback);

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
    }

    [Fact]
    public async Task FallbackModels_SemicolonSeparatorSupported()
    {
        var handler = new ModelRoutingHandler(model =>
            model == "fallback-a" ? Ok("content-from-fallback-a") : TooMany());
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a; fallback-b");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-fallback-a", result.Content);
        Assert.Equal(new[] { "primary", "fallback-a" }, handler.RequestedModels);
    }

    [Fact]
    public async Task BlankPrimary_NoFallback_StillHasOneModel_DoesNotThrow()
    {
        // Degenerate config: a blank primary with no fallbacks must not produce
        // an empty roster (which would crash CompleteAsync); it keeps the raw
        // primary as the single candidate, matching fixed-model behaviour.
        var handler = new ModelRoutingHandler(_ => TooMany());
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "", "key");

        Assert.Single(llm.Models);
        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Single(handler.RequestedModels);
    }

    [Fact]
    public void DefaultClient_UsesCapableFirstFallbackChain()
    {
        // A fully UNCONFIGURED client (no model arg, no AC_BOTS_LLM_MODEL / no
        // AC_BOTS_LLM_FALLBACK_MODELS) must roster the built-in capable-first
        // rotation, NOT a single weak model — so an unconfigured run prefers the
        // most capable model and only degrades to a high-availability one as a
        // last resort when every capable model is quota-walled.
        var savedModel = Environment.GetEnvironmentVariable("AC_BOTS_LLM_MODEL");
        var savedFallback = Environment.GetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_MODELS");
        try
        {
            Environment.SetEnvironmentVariable("AC_BOTS_LLM_MODEL", null);
            Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_MODELS", null);
            var llm = new LlmGoalClient(new HttpClient(new ModelRoutingHandler(_ => TooMany())));

            Assert.True(llm.Models.Count >= 2, "default client must roster a fallback chain, not one model");
            Assert.Equal("openai/gpt-4o", llm.Models[0]);
            Assert.Contains("meta/llama-3.3-70b-instruct", llm.Models); // high-availability last resort present
        }
        finally
        {
            Environment.SetEnvironmentVariable("AC_BOTS_LLM_MODEL", savedModel);
            Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_MODELS", savedFallback);
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cooldown_AllWalled_NextCallSkipsHttp_AndReportsSoonestReset()
    {
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(_ => TooMany(TimeSpan.FromSeconds(100)));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B,C", now: clock.Get);

        var first = await llm.CompleteAsync("s", "u"); // probes A,B,C -> all 429, all cool 100s
        Assert.False(first.Ok);
        Assert.Equal(new[] { "A", "B", "C" }, handler.RequestedModels);

        clock.Now = T0.AddSeconds(10); // still within the 100s cooldown
        var second = await llm.CompleteAsync("s", "u");

        Assert.False(second.Ok);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        // No new HTTP — known-walled models are not re-probed.
        Assert.Equal(3, handler.RequestedModels.Count);
        // Back off only until the soonest model frees (~90s left).
        Assert.NotNull(second.RetryAfter);
        Assert.InRange(second.RetryAfter!.Value, TimeSpan.FromSeconds(89), TimeSpan.FromSeconds(91));
    }

    [Fact]
    public async Task PrimaryReprobe_RecoveredPrimary_PickedBackUp_AfterTransientFailover()
    {
        // A TRANSIENT (5xx) primary failure rotates to the fallback (no cooldown).
        // The next call re-probes the primary, so a recovered preferred model is
        // picked back up instead of the run staying stuck on the weaker fallback.
        var clock = new FakeClock(T0);
        var primaryDown = true;
        var handler = new ModelRoutingHandler(m =>
            m == "A" ? (primaryDown ? ServiceUnavailable() : Ok("ok-A")) : Ok("ok-B"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        var first = await llm.CompleteAsync("s", "u");   // A 503 -> B
        Assert.True(first.Ok);
        Assert.Equal("B", llm.Model);

        primaryDown = false;                              // primary recovered
        var second = await llm.CompleteAsync("s", "u");   // re-probe A -> ok
        Assert.True(second.Ok);
        Assert.Equal("ok-A", second.Content);
        Assert.Equal("A", llm.Model);                     // back on the preferred model
    }

    [Fact]
    public async Task PrimaryReprobe_NotRepeated_WithinInterval()
    {
        // After one re-probe that still fails, the primary is NOT re-probed again
        // until the interval elapses (the bot stays on the working fallback).
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(m => m == "A" ? ServiceUnavailable() : Ok("ok-B"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        await llm.CompleteAsync("s", "u");                // call1: A 503 -> B
        await llm.CompleteAsync("s", "u");                // call2: immediate re-probe A 503 -> B
        var aAfterCall2 = handler.RequestedModels.Count(m => m == "A");

        clock.Now = T0.AddSeconds(10);                    // within the 45s interval
        await llm.CompleteAsync("s", "u");               // call3: NO re-probe -> B only
        Assert.Equal(aAfterCall2, handler.RequestedModels.Count(m => m == "A"));

        clock.Now = T0.AddSeconds(70);                    // past the 45s interval
        await llm.CompleteAsync("s", "u");               // call4: re-probe A again
        Assert.True(handler.RequestedModels.Count(m => m == "A") > aAfterCall2);
    }

    [Theory]
    [InlineData(null, 40)]      // unset -> raised default (under the 45s re-probe interval)
    [InlineData("", 40)]        // blank -> default
    [InlineData("abc", 40)]     // unparseable -> default
    [InlineData("40", 40)]      // valid override
    [InlineData("10", 10)]      // min bound
    [InlineData("44", 44)]      // max bound (PrimaryReprobeInterval - 1)
    [InlineData("5", 40)]       // below min -> default
    [InlineData("45", 40)]      // == re-probe interval -> rejected (would break re-probe spacing)
    [InlineData("60", 40)]      // >= policy CTS -> rejected (would disable failover)
    [InlineData("999", 40)]     // absurd -> default
    public void ResolveHttpTimeout_DefaultsTo40_ClampedUnderReprobeInterval(string? env, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), LlmGoalClient.ResolveHttpTimeout(env));
    }

    [Theory]
    // (env, primarySeconds, expectedSeconds) — a fallback attempt's tighter budget.
    [InlineData(null, 40, 18)]   // unset -> default (well under the 40s primary)
    [InlineData("", 40, 18)]     // blank -> default
    [InlineData("abc", 40, 18)]  // unparseable -> default
    [InlineData("18", 40, 18)]   // valid override
    [InlineData("1", 40, 1)]     // min bound
    [InlineData("40", 40, 40)]   // max bound == primary budget
    [InlineData("0", 40, 18)]    // below min -> default
    [InlineData("41", 40, 18)]   // above the primary budget -> rejected -> default
    [InlineData("10", 40, 10)]   // valid mid override
    [InlineData(null, 10, 10)]   // small primary: default min(18,primary) -> primary
    [InlineData("10", 10, 10)]   // valid == small primary
    [InlineData("11", 10, 10)]   // above small primary -> rejected -> default(=primary)
    public void ResolveFallbackHttpTimeout_ClampedAtOrUnderPrimary(
        string? env, int primarySeconds, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            LlmGoalClient.ResolveFallbackHttpTimeout(env, TimeSpan.FromSeconds(primarySeconds)));
    }

    // ---- per-attempt fallback timeout: a stalled fallback rotates fast ----

    [Fact]
    public async Task FallbackAttemptTimeout_StalledFallback_RotatesToNextModel_AsFailover()
    {
        // A stalled fallback must be abandoned at the (short) per-attempt deadline
        // and rotated PAST as an infrastructure failover (not surfaced as a
        // caller-cancellation), so the chain reaches a responsive candidate quickly
        // instead of burning the full primary budget on the stall.
        Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS", "1");
        try
        {
            var handler = new DelayingModelHandler(model => model switch
            {
                "primary" => (TimeSpan.Zero, TooMany()),                 // rotate immediately
                "slow"    => (TimeSpan.FromSeconds(30), Ok("late")),     // stalls past the 1s deadline
                _         => (TimeSpan.Zero, Ok($"content-from-{model}")),
            });
            var llm = new LlmGoalClient(
                new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "slow;fast");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await llm.CompleteAsync("sys", "user");
            sw.Stop();

            Assert.True(result.Ok);
            Assert.Equal("content-from-fast", result.Content);
            Assert.Equal(new[] { "primary", "slow", "fast" }, handler.RequestedModels);
            // Abandoned near the 1s deadline, nowhere near the 30s stall.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
                $"stalled fallback was not abandoned promptly (took {sw.Elapsed.TotalSeconds:F1}s)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS", null);
        }
    }

    [Fact]
    public async Task FallbackAttemptTimeout_PrimaryIsExemptFromTheTighterBudget()
    {
        // The configured primary keeps its full budget: a primary that answers
        // slightly slower than the fallback deadline must NOT be cut off (that is
        // the whole reason the tighter budget is scoped to fallbacks only).
        Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS", "1");
        try
        {
            var handler = new DelayingModelHandler(model =>
                (TimeSpan.FromSeconds(2), Ok($"content-from-{model}")));  // slower than the 1s fallback budget
            var llm = new LlmGoalClient(
                new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

            var result = await llm.CompleteAsync("sys", "user");

            Assert.True(result.Ok);
            Assert.Equal("content-from-primary", result.Content);       // primary answered, was not abandoned
            Assert.Equal(new[] { "primary" }, handler.RequestedModels);  // never rotated to the fallback
        }
        finally
        {
            Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS", null);
        }
    }

    [Fact]
    public async Task FallbackAttemptTimeout_CallerCancellation_DoesNotStormThroughTheRoster()
    {
        // A real caller cancellation (bot shutting down) must remain a no-failover
        // exit even with the per-attempt CTS in place: it must not rotate through
        // the whole roster on the way out.
        Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS", "1");
        try
        {
            var handler = new DelayingModelHandler(_ =>
                (TimeSpan.FromSeconds(30), Ok("never")));   // would block if ever reached
            var llm = new LlmGoalClient(
                new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a;fallback-b");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await llm.CompleteAsync("sys", "user", cts.Token);

            Assert.False(result.Ok);
            Assert.True(handler.RequestedModels.Count <= 1,
                $"caller-cancel rotated through {handler.RequestedModels.Count} models");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AC_BOTS_LLM_FALLBACK_HTTP_TIMEOUT_SECONDS", null);
        }
    }

    [Fact]
    public async Task Cooldown_SkipsCoolingModel_EvenWhenRotationReachesIt()
    {
        var clock = new FakeClock(T0);
        var bWalled = false;
        var handler = new ModelRoutingHandler(m =>
            m == "A" ? TooMany(TimeSpan.FromSeconds(100))
            : (bWalled ? TooMany(TimeSpan.FromSeconds(50)) : Ok("ok-B")));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        var first = await llm.CompleteAsync("s", "u"); // A 429 (cool 100s), B ok, active=B
        Assert.True(first.Ok);

        clock.Now = T0.AddSeconds(10);
        bWalled = true; // B now walls too
        handler.RequestedModels.Clear();
        var second = await llm.CompleteAsync("s", "u"); // start at B; A still cooling -> skipped

        Assert.False(second.Ok);
        // Only B is probed; A (90s left on its cooldown) is skipped despite being
        // next in rotation order.
        Assert.Equal(new[] { "B" }, handler.RequestedModels);
    }

    [Fact]
    public async Task Cooldown_Expires_ModelIsReprobed()
    {
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(_ => TooMany(TimeSpan.FromSeconds(100)));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        await llm.CompleteAsync("s", "u"); // A,B 429, cool 100s
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);

        clock.Now = T0.AddSeconds(101); // cooldowns expired
        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u");

        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels); // both re-probed
    }

    [Fact]
    public async Task Cooldown_SingleModel_NeverCools_ReprobesEachCall()
    {
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(_ => TooMany(TimeSpan.FromSeconds(100)));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key", now: clock.Get);

        await llm.CompleteAsync("s", "u");
        var second = await llm.CompleteAsync("s", "u"); // same instant; single model -> still probes

        Assert.False(second.Ok);
        Assert.Equal(new[] { "A", "A" }, handler.RequestedModels); // no cooldown skip
    }

    [Fact]
    public async Task Cooldown_AnchoredAtObservationTime_NotCallStart()
    {
        // The 429 arrives only AFTER the awaited HTTP latency, so the cooldown
        // must be measured from when the 429 was OBSERVED, not from CompleteAsync
        // start — otherwise it expires early and the model is re-probed too soon.
        var clock = new FakeClock(T0);
        var bWalled = false;
        var handler = new ModelRoutingHandler(m =>
        {
            if (m == "A") { clock.Now = clock.Now.AddSeconds(8); return TooMany(TimeSpan.FromSeconds(10)); }
            return bWalled ? TooMany(TimeSpan.FromSeconds(10)) : Ok("ok-B");
        });
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        // A: clock advances to T0+8, then 429 r-a=10 => cools until T0+18; B ok, active=B.
        var first = await llm.CompleteAsync("s", "u");
        Assert.True(first.Ok);

        clock.Now = T0.AddSeconds(12); // past a call-start anchor (T0+10), before the correct one (T0+18)
        bWalled = true;
        handler.RequestedModels.Clear();
        var second = await llm.CompleteAsync("s", "u"); // order [B, A]; A must still be cooling -> skipped

        Assert.False(second.Ok);
        // A is NOT re-probed: its cooldown was anchored at observation (T0+8)+10=T0+18,
        // not call-start T0+10. A call-start anchor would wrongly re-probe A here.
        Assert.Equal(new[] { "B" }, handler.RequestedModels);
    }

    // ---- fallback diagnostics (observable in live logs) ----

    [Fact]
    public async Task Diagnostic_LogsRotation_WhenFallbackTakesOver()
    {
        var logs = new List<string>();
        var handler = new ModelRoutingHandler(m => m == "A" ? TooMany() : Ok("ok-B"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", log: logs.Add);

        var result = await llm.CompleteAsync("s", "u");

        Assert.True(result.Ok);
        Assert.Contains(logs, l => l.Contains("[llm-fallback]") && l.Contains("A") && l.Contains("429"));
        Assert.Contains(logs, l => l.Contains("[llm-fallback]") && l.Contains("rotated to B"));
    }

    [Fact]
    public async Task Diagnostic_LogsAllWalled_WhenEveryModelReturns429()
    {
        var logs = new List<string>();
        var handler = new ModelRoutingHandler(_ => TooMany());
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", log: logs.Add);

        await llm.CompleteAsync("s", "u");

        Assert.Contains(logs, l => l.Contains("[llm-fallback]") && l.Contains("all 2") && l.Contains("walled"));
    }

    [Fact]
    public async Task Diagnostic_LogsAllCooling_OnNoHttpShortCircuit()
    {
        var clock = new FakeClock(T0);
        var logs = new List<string>();
        var handler = new ModelRoutingHandler(_ => TooMany(TimeSpan.FromSeconds(100)));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get, log: logs.Add);

        await llm.CompleteAsync("s", "u"); // both 429, cool 100s
        clock.Now = T0.AddSeconds(10);
        logs.Clear();
        await llm.CompleteAsync("s", "u"); // all cooling -> synthetic 429, no HTTP

        Assert.Contains(logs, l => l.Contains("[llm-fallback]") && l.Contains("cooling down"));
    }

    [Fact]
    public async Task Diagnostic_SingleModel_LogsNothing()
    {
        var logs = new List<string>();
        var handler = new ModelRoutingHandler(_ => TooMany());
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key", log: logs.Add);

        await llm.CompleteAsync("s", "u");

        Assert.Empty(logs); // single-model client stays silent
    }

    [Fact]
    public async Task Diagnostic_NoRotationLog_WhenPrimarySucceedsFirstTry()
    {
        var logs = new List<string>();
        var handler = new ModelRoutingHandler(m => Ok($"ok-{m}"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", log: logs.Add);

        var result = await llm.CompleteAsync("s", "u");

        Assert.True(result.Ok);
        Assert.DoesNotContain(logs, l => l.Contains("rotated"));
    }

    [Fact]
    public async Task Diagnostic_ThrowingLogSink_DoesNotBreakTheCall()
    {
        // A bad logger must never turn a returned LlmResult into a thrown
        // exception — diagnostics are best-effort.
        var handler = new ModelRoutingHandler(m => m == "A" ? TooMany() : Ok("ok-B"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", log: _ => throw new InvalidOperationException("logger boom"));

        var result = await llm.CompleteAsync("s", "u");

        Assert.True(result.Ok);
        Assert.Equal("ok-B", result.Content);
    }

    // ---- transport errors / client timeouts trigger failover (NOT just 429) ----
    //
    // A daily-quota wall on a large prompt frequently surfaces as a connection
    // reset / stream-copy failure or a 30s client timeout rather than a clean
    // 429. The active model then "fails" with no HTTP status, and a healthy
    // fallback would still answer — so a transport failure must rotate too.

    [Fact]
    public async Task Fallback_PrimaryTransportError_RotatesToFallback_Succeeds()
    {
        var handler = new ModelRoutingHandler(model =>
            model == "primary"
                ? throw new HttpRequestException("Error while copying content to a stream.")
                : Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-fallback-a", result.Content);
        Assert.Equal("fallback-a", llm.Model);
        Assert.Equal(new[] { "primary", "fallback-a" }, handler.RequestedModels);
    }

    [Fact]
    public async Task Fallback_PrimaryClientTimeout_RotatesToFallback_Succeeds()
    {
        // HttpClient.Timeout surfaces as a TaskCanceledException with the
        // CALLER's token NOT signalled — that is a transport failure, not a
        // caller cancellation, so it must fail over.
        var handler = new ModelRoutingHandler(model =>
            model == "primary"
                ? throw new TaskCanceledException("HttpClient.Timeout of 30s elapsing")
                : Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.True(result.Ok);
        Assert.Equal("content-from-fallback-a", result.Content);
        Assert.Equal(new[] { "primary", "fallback-a" }, handler.RequestedModels);
    }

    [Fact]
    public async Task Fallback_AllModelsTransportError_ReturnsTransportFailure_AllProbed()
    {
        var handler = new ModelRoutingHandler(_ =>
            throw new HttpRequestException("connection reset"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "a,b");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        // Not a 429: a transport failure has no HTTP status.
        Assert.Null(result.StatusCode);
        Assert.Contains("http error", result.Error);
        // Every candidate is tried exactly once, in order.
        Assert.Equal(new[] { "primary", "a", "b" }, handler.RequestedModels);
    }

    [Fact]
    public async Task SingleModel_TransportError_ReturnsFailure_NoRotation()
    {
        var handler = new ModelRoutingHandler(_ =>
            throw new HttpRequestException("connection reset"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "primary", "key");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Null(result.StatusCode);
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
    }

    [Fact]
    public async Task TransportError_NoPerModelCooldown_BothReprobedAfterBackoff()
    {
        // Unlike a 429, a transport failure imposes NO lingering per-model
        // cooldown. (A whole-roster transport failure does arm a short GLOBAL
        // infra backoff — covered separately — so this advances past that window
        // to assert the per-model behaviour: both models are re-probed, neither
        // is individually benched.)
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(_ =>
            throw new HttpRequestException("connection reset"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        await llm.CompleteAsync("s", "u"); // A,B both transport-fail (no per-model cooldown)
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);

        clock.Now = T0.AddSeconds(21); // past the global infra backoff window
        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u");

        // Both re-probed — a 429 would have skipped a still-cooling model.
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);
    }

    // ---- persistent-failover cooldown: a repeatedly-failing model is benched ----

    [Fact]
    public async Task FailoverCooldown_PersistentFailer_BenchedAfterThreshold_ThenSkipped()
    {
        // A (primary) fails over on EVERY attempt (503); B answers. With threshold 2,
        // the first failover leaves A eligible (transient tolerance), but the second
        // consecutive failover benches A for the cooldown window, so the next
        // primary-re-probe SKIPS A instead of re-paying its timeout.
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(m => m == "A" ? ServiceUnavailable() : Ok("ok-B"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get, failoverCooldownThreshold: 2);

        await llm.CompleteAsync("s", "u");                 // A 503 (streak 1) -> B ok
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);

        clock.Now = T0.AddSeconds(46);                     // past the 45s re-probe interval
        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u");                 // re-probe A: 503 (streak 2) -> benched; B ok
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);

        clock.Now = T0.AddSeconds(92);                     // past re-probe interval, WITHIN A's 300s bench
        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u");                 // A benched -> skipped; only B probed
        Assert.Equal(new[] { "B" }, handler.RequestedModels);
    }

    [Fact]
    public async Task FailoverCooldown_ThresholdZero_Disabled_AlwaysReprobes()
    {
        // threshold <= 0 disables benching: a persistent failover-failer stays
        // eligible and is re-probed every interval — byte-identical to prior
        // behaviour (no per-model cooldown on a transport/5xx failure).
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(m => m == "A" ? ServiceUnavailable() : Ok("ok-B"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get, failoverCooldownThreshold: 0);

        await llm.CompleteAsync("s", "u");
        clock.Now = T0.AddSeconds(46);
        await llm.CompleteAsync("s", "u");
        clock.Now = T0.AddSeconds(92);
        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u");                 // A still re-probed (never benched)
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);
    }

    [Fact]
    public async Task FailoverCooldown_SuccessResetsStreak_NoPrematureBench()
    {
        // A success clears the streak: a fail -> success -> fail sequence must NOT
        // bench A (the post-success fail is only streak 1, below the threshold of 2).
        var clock = new FakeClock(T0);
        var aProbe = 0;
        var handler = new ModelRoutingHandler(m =>
        {
            if (m != "A") return Ok("ok-B");
            aProbe++;
            return aProbe == 2 ? Ok("ok-A") : ServiceUnavailable(); // 2nd A-probe succeeds
        });
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get, failoverCooldownThreshold: 2);

        await llm.CompleteAsync("s", "u");                 // A #1 503 (streak 1) -> B ok
        clock.Now = T0.AddSeconds(46);
        await llm.CompleteAsync("s", "u");                 // re-probe A #2 OK (streak reset, active A)
        clock.Now = T0.AddSeconds(92);
        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u");                 // A #3 503 (streak 1, NOT benched) -> B ok
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels); // A still eligible, re-probed

        clock.Now = T0.AddSeconds(138);
        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u");                 // A #4 503 (streak 2 -> benched now) -> B ok
        Assert.Contains("A", handler.RequestedModels);     // benched only AFTER 2 post-reset fails
    }

    [Fact]
    public async Task FailoverCooldown_WholeRosterBenched_RecoversViaInfraCadence_NotStalledForFullBench()
    {
        // Regression: if EVERY model gets failover-benched (a shared transport outage),
        // the client must NOT stall for the full 300s bench. A bench is only honoured
        // while a NON-benched candidate remains; with none left it re-probes, paced by
        // the 20s infra circuit-breaker, so a recovered endpoint is picked up in ~20s.
        var clock = new FakeClock(T0);
        var down = true;
        var handler = new ModelRoutingHandler(m => down ? ServiceUnavailable() : Ok($"ok-{m}"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get, failoverCooldownThreshold: 1);

        var first = await llm.CompleteAsync("s", "u"); // A,B both 503 -> both benched + infra-breaker armed
        Assert.False(first.Ok);
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);

        down = false;                         // endpoint recovers well before the 300s bench
        clock.Now = T0.AddSeconds(21);        // past the 20s infra backoff, deep inside the 300s bench
        handler.RequestedModels.Clear();
        var second = await llm.CompleteAsync("s", "u");

        // Re-probed despite the benches (not stalled a full 300s): recovers immediately.
        Assert.True(second.Ok);
        Assert.Contains("A", handler.RequestedModels);
    }

    [Fact]
    public async Task FailoverCooldown_MixedQuotaAndBench_ReprobesBenched_NotStalledForQuota()
    {
        // A hits a LONG 429 (1h Retry-After); B fails over and is benched. The client
        // must NOT sleep out A's 1h quota wall — a 429'd model is not a viable recovery
        // path — so the benched B is re-probed (paced by the infra-breaker) and recovers
        // as soon as its endpoint comes back, while A rides out its 429.
        var clock = new FakeClock(T0);
        var bDown = true;
        var handler = new ModelRoutingHandler(m =>
            m == "A" ? TooMany(TimeSpan.FromHours(1))            // hard 1h quota wall
                     : (bDown ? ServiceUnavailable() : Ok("ok-B"))); // B transport-fails, then recovers
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get, failoverCooldownThreshold: 1);

        await llm.CompleteAsync("s", "u"); // A 429 (cooled 1h); B 503 (benched, threshold 1)

        bDown = false;                      // B's endpoint recovers quickly
        clock.Now = T0.AddSeconds(21);      // A still 429-walled (1h), B still benched (300s)
        handler.RequestedModels.Clear();
        var second = await llm.CompleteAsync("s", "u");

        // B re-probed (bench ignored: A's 429 is not a viable candidate) -> recovers;
        // NOT stalled for A's 1h Retry-After.
        Assert.True(second.Ok);
        Assert.Equal(new[] { "B" }, handler.RequestedModels); // A skipped (429), only B re-probed
    }

    // ---- global infra circuit-breaker (whole roster down) ----

    [Fact]
    public async Task InfraBackoff_WholeRosterTransportFails_NextCallShortCircuits_NoProbing()
    {
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(_ =>
            throw new HttpRequestException("endpoint down"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        var first = await llm.CompleteAsync("s", "u"); // probes A,B -> all transport-fail -> arm breaker
        Assert.False(first.Ok);
        Assert.Equal(new[] { "A", "B" }, handler.RequestedModels);

        clock.Now = T0.AddSeconds(5); // within the 20s infra backoff
        handler.RequestedModels.Clear();
        var second = await llm.CompleteAsync("s", "u");

        Assert.False(second.Ok);
        Assert.Empty(handler.RequestedModels); // short-circuited: NO HTTP probing this call
        Assert.NotNull(second.RetryAfter);     // backs off until the window ends
    }

    [Fact]
    public async Task InfraBackoff_LiftedAfterWindow_AndClearedOnSuccess()
    {
        var clock = new FakeClock(T0);
        var down = true;
        var handler = new ModelRoutingHandler(m =>
            down ? throw new HttpRequestException("down") : Ok($"ok-{m}"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", now: clock.Get);

        await llm.CompleteAsync("s", "u"); // arm breaker
        down = false;                       // endpoint recovers, but we're still backed off

        clock.Now = T0.AddSeconds(5);
        handler.RequestedModels.Clear();
        var blocked = await llm.CompleteAsync("s", "u");
        Assert.False(blocked.Ok);
        Assert.Empty(handler.RequestedModels); // still short-circuited inside the window

        clock.Now = T0.AddSeconds(21);         // past the window
        var ok = await llm.CompleteAsync("s", "u");
        Assert.True(ok.Ok);                    // re-probes, succeeds, clears the breaker

        handler.RequestedModels.Clear();
        var ok2 = await llm.CompleteAsync("s", "u");
        Assert.True(ok2.Ok);                   // breaker stays cleared; sticky on the working model
        Assert.Equal(new[] { "A" }, handler.RequestedModels);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotFailOver_ReturnsImmediately()
    {
        // A caller-requested cancellation (bot shutting down) surfaces as an
        // OperationCanceledException WITH the caller token signalled. That must
        // NOT burn the remaining models on the way out.
        using var cts = new CancellationTokenSource();
        var handler = new ModelRoutingHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user", cts.Token);

        Assert.False(result.Ok);
        // Did NOT rotate to fallback-a despite the failure.
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
    }

    [Fact]
    public async Task Diagnostic_LogsRotation_OnTransportError()
    {
        var logs = new List<string>();
        var handler = new ModelRoutingHandler(m =>
            m == "A" ? throw new HttpRequestException("stream copy failed") : Ok("ok-B"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B", log: logs.Add);

        var result = await llm.CompleteAsync("s", "u");

        Assert.True(result.Ok);
        Assert.Contains(logs, l => l.Contains("[llm-fallback]") && l.Contains("failed") && l.Contains("rotating"));
        Assert.Contains(logs, l => l.Contains("[llm-fallback]") && l.Contains("rotated to B"));
    }

    // ---- top-tier reservation (AC_BOTS_TOP_TIER_RESERVE) ----

    [Fact]
    public void ParseReservedIndices_UnsetOrBlank_Empty()
    {
        var models = new[] { "a", "b", "c" };
        Assert.Empty(LlmGoalClient.ParseReservedIndices(models, null));
        Assert.Empty(LlmGoalClient.ParseReservedIndices(models, ""));
        Assert.Empty(LlmGoalClient.ParseReservedIndices(models, "   "));
    }

    [Fact]
    public void ParseReservedIndices_MatchesNamesToIndices_PreservesModelOrder_IgnoresUnknown()
    {
        var models = new[] { "gpt-4o", "gpt-4.1", "gpt-4.1-mini", "deepseek" };
        // Reserve gpt-4o + gpt-4.1 (names given out of order + an unknown one ignored).
        var idx = LlmGoalClient.ParseReservedIndices(models, "gpt-4.1 ; unknown-model , gpt-4o");
        Assert.Equal(new[] { 0, 1 }, idx); // model order (0=gpt-4o, 1=gpt-4.1), not arg order
    }

    [Fact]
    public void ParseReservedIndices_ReservingWholeRoster_TreatedAsOff()
    {
        var models = new[] { "a", "b" };
        Assert.Empty(LlmGoalClient.ParseReservedIndices(models, "a,b")); // would starve routine -> OFF
    }

    [Fact]
    public void PartitionOrderForReservation_NoReserved_Unchanged()
    {
        var order = new List<int> { 2, 0, 1 };
        var result = LlmGoalClient.PartitionOrderForReservation(order, System.Array.Empty<int>(), directed: false);
        Assert.Equal(new[] { 2, 0, 1 }, result);
        var result2 = LlmGoalClient.PartitionOrderForReservation(order, System.Array.Empty<int>(), directed: true);
        Assert.Equal(new[] { 2, 0, 1 }, result2);
    }

    [Fact]
    public void PartitionOrderForReservation_Routine_RemovesReserved()
    {
        // order = [0,1,2,3]; reserved = {0,1} (top-tier). Routine call -> only [2,3].
        var result = LlmGoalClient.PartitionOrderForReservation(
            new List<int> { 0, 1, 2, 3 }, new[] { 0, 1 }, directed: false);
        Assert.Equal(new[] { 2, 3 }, result);
    }

    [Fact]
    public void PartitionOrderForReservation_Directed_ReservedFirst_ThenRoutine_OrderPreserved()
    {
        // order = [2,3,0,1] (sticky landed mid-tier); reserved = {0,1}. Directed call ->
        // reserved (in their order-of-appearance) first, then routine: [0,1,2,3].
        var result = LlmGoalClient.PartitionOrderForReservation(
            new List<int> { 2, 3, 0, 1 }, new[] { 0, 1 }, directed: true);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result);
    }

    [Fact]
    public void PartitionOrderForReservation_Directed_DropsNoCandidate_FallsThroughToRoutine()
    {
        // A directed call must still reach routine models if the reserved ones are walled
        // (excluded from `order` upstream). Here order already lacks the reserved indices
        // (all cooling) -> directed returns just the routine ones (no candidate invented).
        var result = LlmGoalClient.PartitionOrderForReservation(
            new List<int> { 2, 3 }, new[] { 0, 1 }, directed: true);
        Assert.Equal(new[] { 2, 3 }, result);
    }

    [Fact]
    public void PartitionOrderForReservation_Routine_AllReservedInOrder_EmptiesOrder()
    {
        // If every surviving candidate is reserved, a routine call yields an EMPTY order
        // (caller's all-cooling backoff then applies; routine never spends top-tier quota).
        var result = LlmGoalClient.PartitionOrderForReservation(
            new List<int> { 0, 1 }, new[] { 0, 1 }, directed: false);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Reserve_Routine_ReservedHealthy_FallbacksBenched_ReprobesFallback_NoSpin()
    {
        // Regression (both reviewers, BLOCKING): with A reserved (top-tier) and the routine
        // fallbacks B/C failover-benched, a ROUTINE call must re-probe a benched fallback (the
        // emergency reprobe recovery) rather than letting the reservation empty the order into
        // the 1s-MinModelCooldown spin (soonestCooling is null after a pure-bench skip). A stays
        // reserved (never probed on a routine call).
        var clock = new FakeClock(T0);
        var down = true;
        var handler = new ModelRoutingHandler(m => down ? ServiceUnavailable() : Ok($"ok-{m}"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B;C", now: clock.Get, failoverCooldownThreshold: 1, reservedModels: "A");

        // Routine call: A is reserved-excluded, so only B and C are tried; both 503 -> benched;
        // infra breaker armed. A is never probed (reserved) and so is never benched.
        var first = await llm.CompleteAsync("s", "u"); // directed defaults false
        Assert.False(first.Ok);
        Assert.Equal(new[] { "B", "C" }, handler.RequestedModels);

        down = false;                          // fallbacks recover
        clock.Now = T0.AddSeconds(21);         // past the 20s infra backoff, within the 300s bench
        handler.RequestedModels.Clear();

        var second = await llm.CompleteAsync("s", "u"); // routine
        Assert.True(second.Ok);                          // recovered via the benched-fallback reprobe, no spin
        Assert.DoesNotContain("A", handler.RequestedModels); // A still reserved: not spent on a routine call
        Assert.Contains("B", handler.RequestedModels);       // a benched fallback re-probed instead
    }

    [Fact]
    public async Task Reserve_Directed_TriesReservedModelFirst_RoutineSkipsIt()
    {
        // A DIRECTED decision prefers the reserved model (tried FIRST); a ROUTINE call skips it.
        var clock = new FakeClock(T0);
        var handler = new ModelRoutingHandler(_ => Ok("ok"));
        var llm = new LlmGoalClient(new HttpClient(handler), Endpoint, "A", "key",
            fallbackModels: "B;C", now: clock.Get, reservedModels: "B");

        await llm.CompleteAsync("s", "u");                 // routine: B (reserved) dropped -> A first
        Assert.Equal("A", handler.RequestedModels[0]);
        Assert.DoesNotContain("B", handler.RequestedModels);

        handler.RequestedModels.Clear();
        await llm.CompleteAsync("s", "u", directed: true); // directed: B (reserved) tried FIRST
        Assert.Equal("B", handler.RequestedModels[0]);
    }

    // ---- test doubles ----

    private sealed class FakeClock
    {
        public DateTimeOffset Now;
        public FakeClock(DateTimeOffset start) => Now = start;
        public DateTimeOffset Get() => Now;
    }

    private static HttpResponseMessage Ok(string content)
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage TooMany(TimeSpan? retryAfter = null)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{\"error\":\"rate limited\"}", Encoding.UTF8, "application/json"),
        };
        if (retryAfter is { } ra)
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        return resp;
    }

    // A 5xx — a TRANSIENT (non-quota) failover candidate: it rotates but sets no
    // per-model cooldown, so the model stays eligible for the next re-probe.
    private static HttpResponseMessage ServiceUnavailable() =>
        new((HttpStatusCode)503)
        {
            Content = new StringContent("{\"error\":\"unavailable\"}", Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// Fake transport that routes each request to a response by the model name
    /// in the request body, and records the order of models requested.
    /// </summary>
    private sealed class ModelRoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _route;
        public List<string> RequestedModels { get; } = new();

        public ModelRoutingHandler(Func<string, HttpResponseMessage> route) => _route = route;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var model = ExtractModel(body);
            RequestedModels.Add(model);
            return _route(model);
        }

        private static string ExtractModel(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        }
    }

    /// <summary>
    /// Like <see cref="ModelRoutingHandler"/> but each route also carries a delay
    /// that HONORS the request cancellation token, so a per-attempt timeout (a
    /// linked-CTS CancelAfter) actually interrupts it — exercising the fallback
    /// attempt-timeout path. The model is recorded before the delay so a timed-out
    /// attempt still shows in RequestedModels.
    /// </summary>
    private sealed class DelayingModelHandler : HttpMessageHandler
    {
        private readonly Func<string, (TimeSpan delay, HttpResponseMessage resp)> _route;
        public List<string> RequestedModels { get; } = new();

        public DelayingModelHandler(Func<string, (TimeSpan, HttpResponseMessage)> route) => _route = route;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            var model = ExtractModel(body);
            RequestedModels.Add(model);
            var (delay, resp) = _route(model);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return resp;
        }

        private static string ExtractModel(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        }
    }
}
