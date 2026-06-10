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
    public async Task NonQuotaError_5xx_DoesNotRotate_ReturnsError()
    {
        var handler = new ModelRoutingHandler(model =>
            model == "primary"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
                : Ok($"content-from-{model}"));
        var llm = new LlmGoalClient(
            new HttpClient(handler), Endpoint, "primary", "key", fallbackModels: "fallback-a");

        var result = await llm.CompleteAsync("sys", "user");

        Assert.False(result.Ok);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        // Fallback is reserved for quota (429); a 5xx is returned as-is so the
        // policy's existing non-429 handling applies and we don't thrash models.
        Assert.Equal(new[] { "primary" }, handler.RequestedModels);
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

    // ---- test doubles ----

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
