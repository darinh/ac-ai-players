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
}
