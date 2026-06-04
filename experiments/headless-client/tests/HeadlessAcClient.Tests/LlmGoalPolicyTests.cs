// SPDX-License-Identifier: AGPL-3.0-or-later
// LlmGoalPolicy / LlmGoalClient tests.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class LlmGoalPolicyTests
{
    // ---- TryParseGoal ----

    [Fact]
    public void TryParseGoal_GoodGivePayload_RoundTrips()
    {
        var json = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Jonathan" },
          "item":   { "name": "Academy Exit Token" },
          "priority": 8,
          "expires_in_seconds": 60,
          "rationale": "Exit Token short_desc says give to Jonathan."
        }
        """;
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Give, g!.Kind);
        Assert.Equal("Jonathan", g.Target.Name);
        Assert.Equal("Academy Exit Token", g.Item?.Name);
        Assert.Equal(8, g.Priority);
        Assert.Equal(60, g.ExpiresInSeconds);
    }

    [Fact]
    public void TryParseGoal_RejectsEmptyTarget()
    {
        var json = """{"kind":"Use","target":{},"rationale":"x","priority":3}""";
        Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out var err));
        Assert.Contains("target", err);
    }

    [Fact]
    public void TryParseGoal_GiveRequiresItem()
    {
        var json = """{"kind":"Give","target":{"name":"Jonathan"},"rationale":"x","priority":5}""";
        Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out var err));
        Assert.Contains("Give", err);
    }

    [Fact]
    public void TryParseGoal_RejectsGarbage()
    {
        Assert.False(LlmGoalPolicy.TryParseGoal("not json at all", out _, out _));
    }

    [Fact]
    public void TryParseGoal_AcceptsDashlessGuid_FromLlama()
    {
        // Regression: Llama-3.3-70B (and others) emit `goal_id` as a
        // 32-char dashless hex string. The default System.Text.Json
        // Guid converter rejects this, silently dropping every Attack
        // / Use / Talk goal the LLM emits. FlexibleGuidConverter on
        // Goal.Id widens parsing to accept Guid.Parse's full grammar
        // (D, N, B, P, X). Captured from a real failed response:
        // collision01 run-01 decisions-20260529-183543.jsonl entry
        // showing `goal_id: "d3c59293cfd04e2e8a587ca1a4c0af34"`.
        var json = """
        {
          "goal_id": "d3c59293cfd04e2e8a587ca1a4c0af34",
          "kind": "Attack",
          "target": { "name": "Sparring Golem" },
          "rationale": "Nearest monster in view",
          "priority": 6
        }
        """;
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Attack, g!.Kind);
        Assert.Equal(new System.Guid("d3c59293-cfd0-4e2e-8a58-7ca1a4c0af34"), g.Id);
        Assert.Equal("Sparring Golem", g.Target.Name);
    }

    [Fact]
    public void TryParseGoal_AcceptsBracedGuid()
    {
        var json = """
        {
          "goal_id": "{11111111-2222-3333-4444-555555555555}",
          "kind": "Talk",
          "target": { "name": "Greeter" },
          "rationale": "x",
          "priority": 3
        }
        """;
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(new System.Guid("11111111-2222-3333-4444-555555555555"), g!.Id);
    }

    [Fact]
    public void TryParseGoal_KeepsGoal_WhenGoalIdIsNonGuidSlug()
    {
        // Live regression (2026-06-03): Llama-3.3-70B emits a slug for the
        // id, e.g. `goal-001`, which is not any Guid format. Previously the
        // converter threw and TryParseGoal dropped the ENTIRE goal — so
        // every LLM Attack/Talk/Use goal was discarded and the bot silently
        // ran on the keyword fallback policy. The goal_id is only a
        // correlation handle, so a non-Guid id must NOT discard the goal: it
        // normalizes to Guid.Empty, and LlmGoalPolicy.ProposeGoal then
        // assigns a fresh unique id (preserving Goal.Id uniqueness even when
        // a model reuses the same slug).
        var json = """
        {
          "goal_id": "goal-001",
          "kind": "Talk",
          "target": { "name": "Greeter" },
          "rationale": "x",
          "priority": 3
        }
        """;
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Talk, g!.Kind);
        Assert.Equal("Greeter", g.Target.Name);
        // Normalized to Empty here; ProposeGoal assigns the real unique id.
        Assert.Equal(System.Guid.Empty, g.Id);
    }

    [Fact]
    public void FlexibleGuidConverter_MapsNonGuidToEmpty_AndKeepsValidGuids()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new FlexibleGuidConverter());
        // Slug shapes models emit normalize to Empty (caller regenerates).
        Assert.Equal(System.Guid.Empty, JsonSerializer.Deserialize<System.Guid>("\"goal-001\"", opts));
        Assert.Equal(System.Guid.Empty, JsonSerializer.Deserialize<System.Guid>("\"goal_1\"", opts));
        Assert.Equal(System.Guid.Empty, JsonSerializer.Deserialize<System.Guid>("\"\"", opts));
        // Valid Guid forms (dashless / dashed) still parse exactly — the
        // tolerance we already had is preserved, not removed.
        Assert.Equal(
            new System.Guid("d3c59293cfd04e2e8a587ca1a4c0af34"),
            JsonSerializer.Deserialize<System.Guid>("\"d3c59293cfd04e2e8a587ca1a4c0af34\"", opts));
        Assert.Equal(
            new System.Guid("11111111-2222-3333-4444-555555555555"),
            JsonSerializer.Deserialize<System.Guid>("\"11111111-2222-3333-4444-555555555555\"", opts));
    }

    // ---- LlmGoalClient with mock HTTP ----

    [Fact]
    public async Task LlmGoalClient_CompleteAsync_ReturnsContentOnHttp200()
    {
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = "{\"hello\":\"world\"}" } },
            },
        });
        var http = new HttpClient(new StubHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://test.example/chat", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var client = new LlmGoalClient(http, endpoint: "https://test.example/chat", model: "test-model", apiKey: "test-key");
        var r = await client.CompleteAsync("sys", "user");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("{\"hello\":\"world\"}", r.Content);
    }

    [Fact]
    public async Task LlmGoalClient_CompleteAsync_ReturnsErrorOnHttp401()
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") }));
        var client = new LlmGoalClient(http, endpoint: "https://test.example/chat", model: "test-model", apiKey: "bad");
        var r = await client.CompleteAsync("sys", "user");
        Assert.False(r.Ok);
        Assert.Contains("401", r.Error);
    }

    // ---- LlmGoalPolicy full path with mocked client + fallback ----

    [Fact]
    public async Task LlmGoalPolicy_FallsBackToInnerOnHttpError()
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var fallback = new NoQuestKnowledgePolicy();
        var policy = new LlmGoalPolicy(llm, fallback, new InMemoryWeenieRepo());

        var world = BuildHostileWorld();
        var events = new EventStream();
        var first = policy.ProposeGoal(world, events, null);
        Assert.Null(first); // call kicked off, no result yet

        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);

        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind); // fallback fired
        Assert.StartsWith("fallback:", goal.Source);
    }

    [Fact]
    public async Task LlmGoalPolicy_429_TripsBackoff_NoFurtherHttpCallsWithinWindow()
    {
        // Slice T — once we see HTTP 429 the policy must NOT issue
        // further LLM HTTP calls for the duration of the backoff
        // window. The fallback should drive the bot in the meantime.
        // Without this, a single rate-limit exhaustion burns all
        // subsequent recovery attempts (28 consecutive 429s observed
        // in spike13 on 2026-05-29).
        var httpCallCount = 0;
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref httpCallCount);
            return new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("Too Many Requests") };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            // Coalesce disabled so we'd otherwise issue back-to-back
            // calls; backoff must do the actual gating.
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildHostileWorld();
        var events = new EventStream();

        // First ProposeGoal: kicks off the (eventually-429) call.
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        // Consume the 429 result; backoff fires here. The fallback
        // drives this tick so the bot keeps acting.
        var afterFirst = policy.ProposeGoal(world, events, null);
        Assert.NotNull(afterFirst);
        Assert.StartsWith("fallback:", afterFirst!.Source);
        Assert.Equal(1, httpCallCount); // exactly one HTTP attempt

        // Subsequent ProposeGoal calls within the backoff window must
        // NOT trigger more HTTP calls. The fallback still drives the
        // bot when currentGoal is null.
        for (var i = 0; i < 5; i++)
        {
            var g = policy.ProposeGoal(world, events, null);
            Assert.NotNull(g);
            Assert.StartsWith("fallback:", g!.Source);
        }
        Assert.Equal(1, httpCallCount); // STILL 1 — no retries during backoff
    }

    [Fact]
    public async Task LlmGoalPolicy_429_PreservesCurrentGoalDuringBackoff()
    {
        // Slice T — when a currentGoal exists and we are in the 429
        // backoff window, we return the currentGoal unchanged (the
        // tactics layer keeps driving the existing plan). This is
        // the path that prevents a quota exhaustion from blanking
        // the bot's plan mid-action.
        var httpCallCount = 0;
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref httpCallCount);
            return new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("Too Many Requests") };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildHostileWorld();
        var events = new EventStream();
        var keepAlive = new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = "anywhere" } };

        // Trip the backoff.
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        policy.ProposeGoal(world, events, null); // consume + backoff
        Assert.Equal(1, httpCallCount);

        // Now with a currentGoal in hand, the policy must hand it
        // back unchanged — no HTTP, no fallback substitution.
        var g = policy.ProposeGoal(world, events, keepAlive);
        Assert.Same(keepAlive, g);
        Assert.Equal(1, httpCallCount);
    }

    [Fact]
    public async Task LlmGoalClient_429WithRetryAfterDelta_PopulatesLlmResult()
    {
        // The OpenAI-compatible providers (GitHub Models, OpenAI) emit
        // a Retry-After header on 429 responses indicating when the
        // client may retry. Delta form: an integer number of seconds.
        // LlmGoalClient must surface this as a TimeSpan? on LlmResult
        // so LlmGoalPolicy can honour the server's hint instead of
        // blindly applying its own 30s -> 5min exponential window.
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("Too Many Requests"),
            };
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return resp;
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var result = await llm.CompleteAsync("sys", "user");
        Assert.False(result.Ok);
        Assert.Equal((HttpStatusCode)429, result.StatusCode);
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
    }

    [Fact]
    public async Task LlmGoalClient_429WithoutRetryAfter_LeavesRetryAfterNull()
    {
        // Not every provider sends Retry-After. If absent, RetryAfter
        // must stay null so the policy falls back to its exponential
        // window rather than honouring a phantom value.
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("Too Many Requests") }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var result = await llm.CompleteAsync("sys", "user");
        Assert.False(result.Ok);
        Assert.Equal((HttpStatusCode)429, result.StatusCode);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task LlmGoalPolicy_429WithRetryAfter_HonorsShorterServerHint()
    {
        // When the server returns Retry-After: 2 (much smaller than
        // the default 30s initial backoff), the policy must honour
        // the hint -- a follow-up ProposeGoal a few seconds later
        // must be allowed to issue a fresh LLM call. Without this,
        // even one rate-limit blip burns a 30s gap on a server that
        // was telling us we only needed to wait a couple seconds.
        var httpCallCount = 0;
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref httpCallCount);
            var resp = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("Too Many Requests"),
            };
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return resp;
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };
        var world = BuildHostileWorld();
        var events = new EventStream();

        // First kickoff -> 429 with Retry-After: 2.
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var afterFirst = policy.ProposeGoal(world, events, null); // consume + arm backoff
        Assert.StartsWith("fallback:", afterFirst!.Source);
        Assert.Equal(1, httpCallCount);

        // Immediately after -- backoff window is still open (~2s).
        // Must NOT issue another HTTP call.
        Assert.NotNull(policy.ProposeGoal(world, events, null));
        Assert.Equal(1, httpCallCount);

        // Wait past the 2s server hint but well short of the 30s
        // default exponential window.
        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        // Now the backoff must be expired -- a fresh ProposeGoal
        // must kick off another LLM call. Without honouring
        // Retry-After this would still be gated for ~27 more seconds.
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        _ = policy.ProposeGoal(world, events, null); // drain
        Assert.Equal(2, httpCallCount);
    }
    [Fact]
    public async Task LlmGoalPolicy_UsesLlmResultWhenContentIsValidGoal()
    {
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Jonathan" },
          "item":   { "name": "Academy Exit Token" },
          "priority": 8,
          "rationale": "ShortDesc directive"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo());

        // First call kicks off the LLM Task and returns the (null) currentGoal.
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var first = policy.ProposeGoal(world, events, null);
        Assert.Null(first);

        // Drain the in-flight call and ask again — now the LLM result is consumed.
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);

        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Give, goal!.Kind);
        Assert.Equal("Jonathan", goal.Target.Name);
        Assert.Equal("Academy Exit Token", goal.Item?.Name);
        Assert.StartsWith("llm:", goal.Source);
    }

    [Fact]
    public async Task LlmGoalPolicy_FallsBackOnGarbageContent()
    {
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "this is not json" } } },
        });
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo());

        var world = BuildHostileWorld();
        var events = new EventStream();
        var first = policy.ProposeGoal(world, events, null);
        Assert.Null(first);

        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(goal);
        Assert.StartsWith("fallback:", goal!.Source);
    }

    // ---- Live smoke test (skipped by default; opt in via env) ----

    [Fact]
    public async Task LlmGoalClient_LiveSmoke_ReturnsValidJson()
    {
        // Opt-in: only run if the operator explicitly asks.
        if (Environment.GetEnvironmentVariable("AC_BOTS_LLM_LIVE_TEST") != "1")
            return; // soft-skip; xUnit Fact can't conditional-skip without a custom attribute

        var client = new LlmGoalClient();
        var r = await client.CompleteAsync(
            "You output a single JSON object with one field named 'ok' set to true.",
            "Output the JSON.");
        Assert.True(r.Ok, r.Error);
        using var doc = JsonDocument.Parse(r.Content);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    // ---- HasInflight: schema-vs-LLM race regression ----

    [Fact]
    public void NoQuestKnowledgePolicy_HasInflight_IsAlwaysFalse()
    {
        // Default-impl on IGoalPolicy; a synchronous policy never
        // has work in flight.
        IGoalPolicy policy = new NoQuestKnowledgePolicy();
        Assert.False(policy.HasInflight);
    }

    [Fact]
    public async Task LlmGoalPolicy_HasInflight_TrueDuringCall_FalseAfter()
    {
        // Use a TaskCompletionSource so the SendAsync Task does NOT
        // complete synchronously; otherwise the in-flight window
        // closes before the test can observe it. We complete the
        // TCS from a background thread after asserting HasInflight.
        var tcs = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new HttpClient(new AsyncStubHandler((_, _) => tcs.Task));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo());

        // Before any call: idle.
        Assert.False(policy.HasInflight);

        // Kicks off the async call; the TCS is uncompleted so the
        // policy's inner Task is pending.
        var first = policy.ProposeGoal(BuildHostileWorld(), new EventStream(), null);
        Assert.Null(first);
        Assert.True(policy.HasInflight);

        // Release and let the call complete, then consume.
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = "{\"kind\":\"Explore\",\"target\":{},\"rationale\":\"x\",\"priority\":3}" } },
            },
        });
        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(canned),
        });
        await policy.WaitForInFlightAsync();
        var afterDrain = policy.ProposeGoal(BuildHostileWorld(), new EventStream(), null);
        Assert.NotNull(afterDrain);

        // Post-consume: idle again.
        Assert.False(policy.HasInflight);
    }

    [Fact]
    public void TacticsExecutor_PolicyHasInflight_DelegatesToPolicy()
    {
        // The Motor's deferral gate reads this property. Verify the
        // pass-through against a fake policy whose flag we toggle.
        var fake = new ToggleablePolicy();
        var tactics = new HeadlessAcClient.Tactics.TacticsExecutor(
            fake, new InMemoryWeenieRepo(), training: null);

        Assert.False(tactics.PolicyHasInflight);
        fake.InflightFlag = true;
        Assert.True(tactics.PolicyHasInflight);
        fake.InflightFlag = false;
        Assert.False(tactics.PolicyHasInflight);
    }

    // ---- Stale-goal-on-teleport regression (racefix-run-01) ----

    [Fact]
    public void HasLandblockChangeSince_DetectsEventAboveFloor()
    {
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "p" });
        var floor = es.NextSequence;
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.LandblockChanged, Text = "lb=0xA9B4" });

        Assert.True(LlmGoalPolicy.HasLandblockChangeSince(es, floor));
        // Higher floor (after the landblock event) should miss it.
        Assert.False(LlmGoalPolicy.HasLandblockChangeSince(es, es.NextSequence));
    }

    [Fact]
    public async Task LlmGoalPolicy_LandblockChange_DropsStaleCurrentGoalFromPrompt()
    {
        // Two-call scenario:
        //   1) Initial deliberation produces a Give(Jonathan, Token) goal.
        //   2) After consume, push a LandblockChanged event and call
        //      ProposeGoal again with that goal in hand. The policy must
        //      kick off a fresh LLM call with currentGoal stripped from
        //      the prompt anchor (no "## Current goal" section). This is
        //      what stops the LLM from regurgitating the academy goal
        //      after a teleport to Holtburg.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Jonathan" },
          "item":   { "name": "Academy Exit Token" },
          "priority": 8,
          "rationale": "ShortDesc directive"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });

        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            // Disable rate-limit coalescing so the second call fires
            // immediately rather than getting deferred.
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();

        // Call 1: kick off, drain, consume → goal in hand.
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var firstGoal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(firstGoal);
        Assert.Single(requestBodies);
        // Sanity: the first call DID include currentGoal=null so no anchor.
        Assert.DoesNotContain("## Current goal", requestBodies[0]);

        // Now simulate a teleport: append a LandblockChanged event after
        // the prior call's _lastEventConsideredSequence floor was set.
        events.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.LandblockChanged,
            Text = "lb=0xA9B4 (Holtburg)",
        });

        // Call 2: with the stale goal in hand. Expect kick-off with
        // currentGoal stripped from the prompt (no anchor on Jonathan).
        var second = policy.ProposeGoal(world, events, firstGoal);
        // Returns the prior goal (kept until new result arrives), but
        // the HTTP call has been issued.
        Assert.Equal(2, requestBodies.Count);
        Assert.DoesNotContain("## Current goal", requestBodies[1]);
    }

    // ---- ActionRejected regression (stalefix-run-01) ----
    //
    // The bot was stuck in a loop emitting Give(Society Greeter,
    // Calling Stone) → server rejected with WeenieError 0x046A
    // (TradeAiDoesntWant) → LLM re-emitted the same goal forever
    // because the rejection never made it to the prompt and the
    // currentGoal anchor kept biasing the LLM. These tests cover
    // the wire path (HandshakeDriver appends ActionRejected) at
    // the policy level: salient detection + currentGoal drop +
    // dedicated "Recent rejections" section in the prompt.

    [Fact]
    public void HasRejectionSince_DetectsEventAboveFloor()
    {
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "p" });
        var floor = es.NextSequence;
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Society Greeter",
        });

        Assert.True(LlmGoalPolicy.HasRejectionSince(es, floor));
        // Higher floor (after the rejection) should miss it.
        Assert.False(LlmGoalPolicy.HasRejectionSince(es, es.NextSequence));
    }

    // ---- Stale-cascade narrowing (llama01 spike) ----
    //
    // Pre-fix, the predicate that decided "is this in-flight LLM
    // response stale?" used the SAME wide kind-set as "should the
    // LLM be woken?". The llama01 spike captured 2 of 8 LLM calls
    // discarded mid-flight by ServerMessage / NpcDialog firehose,
    // and the issue compounded badly during active combat (every
    // Attack goal got cancelled by its own damage-number stream).
    //
    // Fix: split the discard predicate to a narrow plan-invalidating
    // set — only events that genuinely obsolete the in-flight
    // response. Trigger set stays wide.

    [Fact]
    public void IsPlanInvalidatingKind_TrueForInvalidatingKinds()
    {
        var invalidating = new[]
        {
            EventKind.LandblockChanged,
            EventKind.InventoryItemRemoved,
            EventKind.ActionRejected,
            EventKind.GoalCompleted,
            EventKind.GoalFailed,
            EventKind.GoalExpired,
        };
        foreach (var kind in invalidating)
        {
            Assert.True(LlmGoalPolicy.IsPlanInvalidatingKind(kind),
                $"{kind} should be classified as plan-invalidating.");
        }
    }

    [Fact]
    public void IsPlanInvalidatingKind_FalseForNonInvalidatingKinds()
    {
        var nonInvalidating = new[]
        {
            EventKind.PopupString,
            EventKind.ServerMessage,
            EventKind.NpcDialog,
            EventKind.BookText,
            EventKind.InventoryItemAdded,
            EventKind.PickerActivityStarted,
            EventKind.PickerActivityCompleted,
            EventKind.PickerArrivedNoAction,
            EventKind.GoalEmitted,
            EventKind.HealthChanged,
        };
        foreach (var kind in nonInvalidating)
        {
            Assert.False(LlmGoalPolicy.IsPlanInvalidatingKind(kind),
                $"{kind} should NOT be classified as plan-invalidating " +
                "(it may wake the LLM but does not obsolete an in-flight response).");
        }
    }

    [Fact]
    public void HasPlanInvalidatingSince_IgnoresChattyFirehose()
    {
        // Simulate the llama01 spike's failure mode: in-flight LLM
        // call kicked off at 'floor', followed by a torrent of
        // chatty events. None should mark the in-flight response
        // as stale.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, GoalId = Guid.NewGuid() });
        var floor = es.NextSequence;

        for (int i = 0; i < 20; i++)
        {
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = $"You hit Sparring Golem for {i} damage." });
        }
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Bystander", Text = "Look at the fight!" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "Area entered." });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemAdded, Wcid = 1234, Name = "New Loot" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.BookText, Name = "Magic Tips", Text = "..." });

        Assert.False(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor));
    }

    [Fact]
    public void HasPlanInvalidatingSince_DetectsInvalidatingKindAboveFloor()
    {
        // Verify the predicate flips when an invalidating event of
        // each in-set kind appears above the floor, and resets when
        // the floor is bumped past the invalidating event.
        var invalidating = new[]
        {
            EventKind.LandblockChanged,
            EventKind.InventoryItemRemoved,
            EventKind.ActionRejected,
            EventKind.GoalCompleted,
            EventKind.GoalFailed,
            EventKind.GoalExpired,
        };
        foreach (var kind in invalidating)
        {
            var es = new EventStream();
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = "noise" });
            var floor = es.NextSequence;
            // Chatty event after floor should not yet flip the predicate.
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = "more noise" });
            Assert.False(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor),
                $"Plain ServerMessage above floor should not invalidate when probing {kind}.");

            es.Append(new StreamEvent
            {
                Sequence = -1,
                Utc = DateTimeOffset.UtcNow,
                Kind = kind,
                LandblockFrom = 0x8602,
                LandblockTo = 0xA9B4,
                Wcid = 9999,
                Name = "Letter From Home",
                ItemGuid = 0x8000047E,
                ErrorCode = 0x046A,
                ErrorLabel = "TradeAiDoesntWant",
                Text = "rejection text",
                GoalId = Guid.NewGuid(),
            });
            Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor),
                $"{kind} above floor should be detected.");
            Assert.False(LlmGoalPolicy.HasPlanInvalidatingSince(es, es.NextSequence),
                $"Floor above the invalidating {kind} should miss it.");
        }
    }

    [Fact]
    public void IsPlanInvalidatingKind_NoActivePlan_ExcludesGoalLifecycleChurn()
    {
        // Deliberation-race fix: when there was NO LLM plan at
        // call-start (an *establishment* call), the Goal* lifecycle
        // kinds are the autonomous fallback policy's own set-then-Clear
        // churn — NOT a real change to the prompt's world. They must
        // not invalidate the in-flight establishment response.
        var goalLifecycle = new[]
        {
            EventKind.GoalCompleted,
            EventKind.GoalFailed,
            EventKind.GoalExpired,
        };
        foreach (var kind in goalLifecycle)
        {
            Assert.False(LlmGoalPolicy.IsPlanInvalidatingKind(kind, hasActivePlan: false),
                $"{kind} should NOT invalidate an establishment call (no plan at call-start).");
            Assert.True(LlmGoalPolicy.IsPlanInvalidatingKind(kind, hasActivePlan: true),
                $"{kind} should still invalidate when a real plan was active at call-start.");
        }
    }

    [Fact]
    public void IsPlanInvalidatingKind_NoActivePlan_StillInvalidatesWorldMovement()
    {
        // World-movement kinds reflect the prompt no longer matching
        // reality. They invalidate regardless of whether a plan was
        // active at call-start.
        var worldMovement = new[]
        {
            EventKind.LandblockChanged,
            EventKind.InventoryItemRemoved,
            EventKind.ActionRejected,
        };
        foreach (var kind in worldMovement)
        {
            Assert.True(LlmGoalPolicy.IsPlanInvalidatingKind(kind, hasActivePlan: false),
                $"{kind} should invalidate even an establishment call (world moved past the prompt).");
            Assert.True(LlmGoalPolicy.IsPlanInvalidatingKind(kind, hasActivePlan: true),
                $"{kind} should invalidate when a plan was active too.");
        }
    }

    [Fact]
    public void HasPlanInvalidatingSince_NoActivePlan_IgnoresFallbackGoalChurn()
    {
        // Reproduce the object-rich-academy failure mode: a fresh L1
        // bot has no LLM plan, kicks off an establishment call at
        // 'floor', and the autonomous picker fallback set-then-Clears a
        // CurrentGoal (each Clear emitting GoalCompleted) every ~2s
        // while the ~7s LLM call is in flight. With hasActivePlan:false
        // those GoalCompleted events must NOT discard the response.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;

        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, GoalId = Guid.NewGuid() });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, GoalId = Guid.NewGuid() });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, GoalId = Guid.NewGuid() });

        // Establishment call (no plan at call-start): fallback churn is ignored.
        Assert.False(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: false),
            "Fallback GoalCompleted churn must not discard an establishment-call response.");
        // Same events WOULD invalidate a call that had a real plan to protect.
        Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: true),
            "With a real plan active at call-start, a GoalCompleted is a genuine invalidation.");
        // The zero-arg form keeps the conservative legacy behavior.
        Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor),
            "Backward-compat short form defaults to hasActivePlan:true.");
    }

    [Fact]
    public void HasPlanInvalidatingSince_NoActivePlan_StillCatchesWorldMovement()
    {
        // Even during an establishment call, a real world move
        // (landblock change from a teleport) must still discard the
        // now-stale response so the bot re-deliberates from the new
        // observations.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;

        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, GoalId = Guid.NewGuid() });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.LandblockChanged, LandblockFrom = 0x8602, LandblockTo = 0xA9B4 });

        Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: false),
            "A LandblockChanged during an establishment call must still invalidate it.");
    }

    [Fact]
    public void HasPlanInvalidatingSince_NoActivePlan_StillCatchesIntentStackCompletion()
    {
        // Strategic intent-stack completion emits GoalCompleted with NO
        // tactical GoalId (HandshakeDriver auto-pop path). That stales the
        // prompt's intent context even on an establishment call, so it
        // must still invalidate — only GoalId-stamped tactical churn is
        // ignored.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;

        // Tactical fallback churn (has GoalId) — ignored.
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, GoalId = Guid.NewGuid() });
        // Intent-stack completion (no GoalId) — invalidates.
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "IntentCompleted id=3 kind=ReachExit" });

        Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: false),
            "A GoalId-less GoalCompleted (intent-stack completion) must invalidate an establishment call.");
    }

    [Fact]
    public async Task LlmGoalPolicy_ActionRejected_DropsCurrentGoalAndAddsRejectionSection()
    {
        // Two-call scenario matching the LandblockChange test:
        //   1) First deliberation -> Give goal accepted, exposed via
        //      the next ProposeGoal as currentGoal.
        //   2) Push an ActionRejected event. Call ProposeGoal again
        //      with the goal in hand. The policy must:
        //        a) drop currentGoal from the prompt anchor
        //        b) include a "## Recent rejections" section so the
        //           LLM cannot miss the rejection signal.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Society Greeter" },
          "item":   { "name": "Calling Stone" },
          "priority": 8,
          "rationale": "ShortDesc directive"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });

        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var firstGoal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(firstGoal);
        Assert.Single(requestBodies);
        Assert.DoesNotContain("## Recent rejections", requestBodies[0]);

        // Simulate the server refusing the action.
        events.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A,
            ErrorLabel = "TradeAiDoesntWant",
            Text = "Society Greeter",
        });

        var second = policy.ProposeGoal(world, events, firstGoal);
        Assert.Equal(2, requestBodies.Count);
        // a) currentGoal dropped from the prompt anchor
        Assert.DoesNotContain("## Current goal", requestBodies[1]);
        // b) dedicated rejection section present + non-empty
        Assert.Contains("## Recent rejections", requestBodies[1]);
        Assert.Contains("TradeAiDoesntWant", requestBodies[1]);
        Assert.Contains("Society Greeter", requestBodies[1]);
        // c) the prompt rules instruct against retry
        Assert.Contains("ActionRejected", requestBodies[1]);
    }

    [Fact]
    public async Task LlmGoalPolicy_Prompt_IncludesProactiveLevelingDrive()
    {
        // Regression guard for the combat-engage-drive slice: the
        // compiled prompt must carry the PROACTIVE leveling value and
        // the combat-safety/pace guardrails, so the LLM treats gaining
        // experience as a first-class objective (seek monsters / Explore
        // toward them) rather than only reacting when attacked.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Explore",
          "target": { "name": "anywhere" },
          "priority": 5,
          "rationale": "seek combat experience"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.Single(requestBodies);

        Assert.Contains("LEVELING is core progress", requestBodies[0]);
        Assert.Contains("COMBAT SAFETY", requestBodies[0]);
        // hunt-excursion slice: the prompt must also carry the bounded
        // "leave a tapped-out safe zone to find monsters" excursion rule
        // so a combat-ready, quest-idle bot crosses out of a mob-free town.
        Assert.Contains("HUNT EXCURSION", requestBodies[0]);
    }

    [Fact]
    public async Task LlmGoalPolicy_EstablishmentCall_SurvivesFallbackGoalChurnMidCall()
    {
        // Deliberation-race regression guard. A fresh L1 bot in an
        // object-rich room has NO LLM plan. It kicks off an
        // establishment call; while that ~7s call is in flight the
        // autonomous picker fallback set-then-Clears a CurrentGoal
        // every ~2s, each Clear emitting GoalCompleted. Before the fix
        // every establishment response was discarded as "stale" by
        // that churn, trapping the bot in the picker fallback forever.
        // After the fix (call-start plan state threaded through the
        // in-flight tuple), the GoalCompleted churn no longer discards
        // an establishment response, so the LLM goal is accepted.
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Society Greeter" },
          "item":   { "name": "Calling Stone" },
          "priority": 8,
          "rationale": "ShortDesc directive"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });

        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            _ = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();

        // 1) Establishment kickoff: no plan at call-start.
        Assert.Null(policy.ProposeGoal(world, events, null));

        // 2) Fallback churn arrives DURING the in-flight call: the
        //    picker set-then-Clears goals, emitting GoalCompleted.
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, GoalId = Guid.NewGuid() });
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, GoalId = Guid.NewGuid() });

        await policy.WaitForInFlightAsync();

        // 3) Consume the result. The establishment response must be
        //    ACCEPTED despite the GoalCompleted churn.
        var established = policy.ProposeGoal(world, events, null);
        Assert.NotNull(established);
        Assert.Equal(GoalKind.Give, established!.Kind);
        Assert.Equal("Society Greeter", established.Target?.Name);
    }

    [Fact]
    public async Task LlmGoalPolicy_EstablishmentCall_StillDiscardedOnRealWorldMove()
    {
        // Counterpart to the churn-survival test: a LandblockChanged
        // (real teleport) during an establishment call still discards
        // the now-stale response, because the prompt described a world
        // the bot has since left.
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Society Greeter" },
          "item":   { "name": "Calling Stone" },
          "priority": 8,
          "rationale": "ShortDesc directive"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });

        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            _ = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.LandblockChanged, LandblockFrom = 0x8602, LandblockTo = 0xA9B4 });
        await policy.WaitForInFlightAsync();

        // Discarded: ProposeGoal returns the (null) currentGoal, not the
        // stale Give goal.
        var consumed = policy.ProposeGoal(world, events, null);
        Assert.Null(consumed);
    }

    // ---- Transport-failure rejections are not plan-invalidating ----
    //
    // A synthetic motor transport-failure ActionRejected (codes
    // 0xFFFC NoIndoorPath / 0xFFFD Blocked / 0xFFFE Unreachable) means
    // the bot could not WALK to a target — the object snapshot the LLM
    // reasoned about is unchanged. It must NOT discard an in-flight LLM
    // response (HasPlanInvalidatingSince) nor drop the current goal from
    // the prompt anchor (HasRejectionSince). Semantic server rejections
    // (real WeenieError) still do both. Same-target transport suppression
    // is owned by IsGoalRecentlyRejected. Live repro: transfix-live.log
    // lines 855-871 (picker walk-timeout staled an establishment call).

    [Theory]
    [InlineData(0xFFFCu)]
    [InlineData(0xFFFDu)]
    [InlineData(0xFFFEu)]
    public void HasPlanInvalidatingSince_TransportFailureRejection_DoesNotInvalidate(uint code)
    {
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;
        es.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = code,
            ErrorLabel = "Unreachable",
            Name = "Leather Leggings",
        });

        // Neither an establishment call nor an active-plan call should be
        // discarded by a transient could-not-walk failure.
        Assert.False(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: false),
            $"Transport-failure rejection 0x{code:X4} must not stale an establishment call.");
        Assert.False(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: true),
            $"Transport-failure rejection 0x{code:X4} must not stale an active-plan call.");
        Assert.False(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor),
            $"Transport-failure rejection 0x{code:X4} must not stale via the short form either.");
    }

    [Fact]
    public void HasPlanInvalidatingSince_SemanticRejection_StillInvalidates()
    {
        // A real server WeenieError (e.g. TradeAiDoesntWant 0x046A) means
        // the world refused the interaction — the prompt is obsolete.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;
        es.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A,
            ErrorLabel = "TradeAiDoesntWant",
            Text = "Society Greeter",
        });

        Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: false),
            "Semantic rejection must still invalidate an establishment call.");
        Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: true),
            "Semantic rejection must still invalidate an active-plan call.");
    }

    [Fact]
    public void HasPlanInvalidatingSince_TransportFailureWithLandblockChange_StillInvalidates()
    {
        // Independence check: a transport rejection returning false must
        // not swallow a genuine world move arriving in the same window —
        // .Any() evaluates each event independently.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorCode = 0xFFFEu, ErrorLabel = "Unreachable", Name = "Leather Leggings" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.LandblockChanged, LandblockFrom = 0x8602, LandblockTo = 0xA9B4 });

        Assert.True(LlmGoalPolicy.HasPlanInvalidatingSince(es, floor, hasActivePlan: false),
            "A LandblockChanged alongside a transport rejection must still invalidate.");
    }

    [Theory]
    [InlineData(0xFFFCu)]
    [InlineData(0xFFFDu)]
    [InlineData(0xFFFEu)]
    public void HasRejectionSince_TransportFailure_DoesNotDropAnchor(uint code)
    {
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorCode = code, ErrorLabel = "Unreachable", Name = "Leather Leggings" });

        Assert.False(LlmGoalPolicy.HasRejectionSince(es, floor),
            $"Transport-failure rejection 0x{code:X4} must not drop the current goal from the prompt anchor.");
    }

    [Fact]
    public void HasRejectionSince_SemanticRejection_DropsAnchor()
    {
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction, Name = "Door" });
        var floor = es.NextSequence;
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant", Text = "Society Greeter" });

        Assert.True(LlmGoalPolicy.HasRejectionSince(es, floor),
            "Semantic rejection must still drop the current goal anchor (Give-loop protection).");
    }

    [Fact]
    public async Task LlmGoalPolicy_EstablishmentCall_SurvivesTransportRejectionMidCall()
    {
        // The live deadlock fix (transfix-live.log): a fresh bot kicks off
        // an establishment call; while it is in flight the autonomous
        // picker's walk-to-candidate times out, emitting a transport
        // ActionRejected (Unreachable 0xFFFE). Before this fix the
        // establishment response was discarded as stale. After: accepted.
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Society Greeter" },
          "item":   { "name": "Calling Stone" },
          "priority": 8,
          "rationale": "ShortDesc directive"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });

        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            _ = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        // Transport failure during the in-flight establishment call.
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorCode = 0xFFFEu, ErrorLabel = "Unreachable", Name = "Leather Leggings" });
        await policy.WaitForInFlightAsync();

        var established = policy.ProposeGoal(world, events, null);
        Assert.NotNull(established);
        Assert.Equal(GoalKind.Give, established!.Kind);
        Assert.Equal("Society Greeter", established.Target?.Name);
    }

    [Fact]
    public async Task LlmGoalPolicy_EstablishmentCall_StillDiscardedOnSemanticRejection()
    {
        // Counterpart: a SEMANTIC ActionRejected during an establishment
        // call must still discard the now-stale response.
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Society Greeter" },
          "item":   { "name": "Calling Stone" },
          "priority": 8,
          "rationale": "ShortDesc directive"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });

        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            _ = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant", Text = "Society Greeter" });
        await policy.WaitForInFlightAsync();

        var consumed = policy.ProposeGoal(world, events, null);
        Assert.Null(consumed);
    }

    [Fact]
    public void StreamEvent_ActionRejected_FormatsCleanly()
    {
        var ev = new StreamEvent
        {
            Sequence = 7, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Society Greeter",
        };
        var s = ev.ToString();
        Assert.Contains("ActionRejected", s);
        Assert.Contains("0x046A", s);
        Assert.Contains("TradeAiDoesntWant", s);
        Assert.Contains("Society Greeter", s);
    }

    // ---- Slice N — programmatic rejection enforcement ----
    //
    // Spike8 confirmed the LLM violates the "do NOT retry the same
    // (kind, target, item) combo" prompt rule even when the rejection
    // is the most recent rejection event (decisions 51, 52, 55, 58
    // all emitted Give(Worcer, A List of Items) with a fresh
    // TradeAiDoesntWant rejection between every attempt). The policy
    // must enforce the rule itself, not rely on LLM compliance.

    [Fact]
    public void IsGoalRecentlyRejected_GiveTradeAiDoesntWant_MatchesByTargetText()
    {
        // Mirrors what HandshakeDriver appends when a Give is refused
        // (WeenieErrorWithString carries the NPC name in `Text`).
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Worcer",
        });

        var goal = new Goal
        {
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Worcer" },
            Item = new Selector { Name = "A List of Items" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(goal, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_Unreachable_MatchesByTargetName()
    {
        // Mirrors HandshakeDriver's walk-timeout rejection (Slice J)
        // which carries motionTarget.Name in Name and a longer
        // "Unreachable: 'X' (walk timeout ...)" string in Text.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
            Name = "Worcer",
            Text = "Unreachable: 'Worcer' (walk timeout 30s)",
            ItemGuid = 0x80001269u,
        });

        var goal = new Goal
        {
            Kind = GoalKind.Talk,
            Target = new Selector { Name = "Worcer" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(goal, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_Blocked_MatchesByTargetName()
    {
        // Slice S — server-physics-clamped motion rejection. The
        // walk-tick blocked-motion detector emits ActionRejected with
        // ErrorLabel="Blocked" + Name=<motionTarget.Name> + ItemGuid
        // when the bot fails to advance toward intent for N consecutive
        // ticks. Dedup must catch goals targeting the same name so
        // the LLM doesn't immediately re-pick a target it just learned
        // is geometrically unreachable from current position.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFD, ErrorLabel = "Blocked",
            Name = "Sparring Golem",
            Text = "Blocked: 'Sparring Golem' — server physics held bot in place (3 ticks, actualMove<25% of expected)",
            ItemGuid = 0x80001500u,
        });

        var attack = new Goal
        {
            Kind = GoalKind.Attack,
            Target = new Selector { Name = "Sparring Golem" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(attack, es));

        // Different verb (Talk) on same target should ALSO dedup —
        // the wall doesn't care which verb you intended.
        var talk = new Goal
        {
            Kind = GoalKind.Talk,
            Target = new Selector { Name = "Sparring Golem" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(talk, es));

        // Different target name should NOT dedup.
        var other = new Goal
        {
            Kind = GoalKind.Attack,
            Target = new Selector { Name = "Olthoi Drudge" },
        };
        Assert.False(LlmGoalPolicy.IsGoalRecentlyRejected(other, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_TransportFailure_StaleAfterArrival_DoesNotMatch()
    {
        // Deadlock repro (RaceFix26160 live run): the bot walk-timed-out
        // toward a pickup-eligible item (Unreachable), then the picker
        // SUBSEQUENTLY arrived in range of that same item. A later Pickup
        // of it must NOT be deduped — the transport failure is stale and
        // the bot is now standing on the item. Otherwise it loops forever.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
            Name = "Leather Leggings",
            Text = "Unreachable: 'Leather Leggings' (walk timeout 30s)",
            ItemGuid = 0x8000104Du,
        });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction,
            Name = "Leather Leggings",
            ItemGuid = 0x8000104Du,
            Text = "in-range: picker auto-lock without LLM verb goal",
        });

        var pickup = new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Leather Leggings" },
        };
        Assert.False(LlmGoalPolicy.IsGoalRecentlyRejected(pickup, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_TransportFailure_ArrivalAtDifferentTarget_StillMatches()
    {
        // Arrival at a DIFFERENT guid must not clear the transport
        // rejection for our target.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
            Name = "Leather Leggings",
            Text = "Unreachable: 'Leather Leggings' (walk timeout 30s)",
            ItemGuid = 0x8000104Du,
        });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction,
            Name = "Training Spadone",
            ItemGuid = 0x80005514u,
        });

        var pickup = new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Leather Leggings" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(pickup, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_TransportFailure_ArrivalBeforeRejection_StillMatches()
    {
        // Ordering matters: an arrival that PRECEDES the transport
        // rejection does not clear it (the bot reached, then later
        // walk-timed-out again on a re-approach).
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction,
            Name = "Leather Leggings",
            ItemGuid = 0x8000104Du,
        });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
            Name = "Leather Leggings",
            Text = "Unreachable: 'Leather Leggings' (walk timeout 30s)",
            ItemGuid = 0x8000104Du,
        });

        var pickup = new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Leather Leggings" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(pickup, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_SemanticReject_NotClearedByArrival()
    {
        // A server-side semantic refusal (TradeAiDoesntWant, real
        // WeenieError code) must stay blocking even after a later
        // arrival — arriving in range doesn't change that the NPC
        // refused the trade.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Name = "Worcer",
            Text = "Worcer doesn't want that.",
            ItemGuid = 0x80001269u,
        });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction,
            Name = "Worcer",
            ItemGuid = 0x80001269u,
        });

        var talk = new Goal
        {
            Kind = GoalKind.Talk,
            Target = new Selector { Name = "Worcer" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(talk, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_TransportFailure_ArrivalMatchedByName_WhenNoGuid()
    {
        // When the transport rejection carries no guid, arrival is
        // matched by name.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFC, ErrorLabel = "NoIndoorPath",
            Name = "Leather Cap",
            Text = "NoIndoorPath: 'Leather Cap' — indoor pathfinder found no walkable route (unknown)",
        });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction,
            Name = "Leather Cap",
            ItemGuid = 0x80001051u,
        });

        var pickup = new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Leather Cap" },
        };
        Assert.False(LlmGoalPolicy.IsGoalRecentlyRejected(pickup, es));
    }

    [Fact]
    public void IsTransportFailureRejection_DiscriminatesSyntheticFromServerCodes()
    {
        StreamEvent Reject(uint code) => new()
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected, ErrorCode = code,
        };
        Assert.True(LlmGoalPolicy.IsTransportFailureRejection(Reject(0xFFFE))); // Unreachable
        Assert.True(LlmGoalPolicy.IsTransportFailureRejection(Reject(0xFFFD))); // Blocked
        Assert.True(LlmGoalPolicy.IsTransportFailureRejection(Reject(0xFFFC))); // NoIndoorPath
        Assert.False(LlmGoalPolicy.IsTransportFailureRejection(Reject(0x046A))); // TradeAiDoesntWant
        Assert.False(LlmGoalPolicy.IsTransportFailureRejection(Reject(0x0035))); // server error
        // Non-rejection kind is never a transport failure.
        Assert.False(LlmGoalPolicy.IsTransportFailureRejection(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction, ErrorCode = 0xFFFE,
        }));
    }

    [Fact]
    public void IsGoalRecentlyRejected_Unreachable_NoArrival_StillMatches()
    {
        // Guard against the fix over-firing: a transport rejection with
        // NO subsequent arrival must STILL dedup (preserves the original
        // anti-thrash behavior when the bot truly can't reach the target).
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
            Name = "Distant Chest",
            Text = "Unreachable: 'Distant Chest' (walk timeout 30s)",
            ItemGuid = 0x80009999u,
        });

        var use = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Distant Chest" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(use, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_InventoryServerSaveFailed_MatchesByItemWcid()
    {
        // Mirrors HandshakeDriver's Slice J rejection for unreachable
        // landscape items (ItemGuid + Wcid + Name populated).
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x0035, ErrorLabel = "AcceptInventoryItemNotInWorld",
            Name = "Bruised Apple",
            Wcid = 29335u,
            ItemGuid = 0x800005A1u,
            Text = "Inventory action failed on 'Bruised Apple'",
        });

        var goalByWcid = new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Wcid = 29335u },
            Item = new Selector { Wcid = 29335u },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(goalByWcid, es));

        var goalByName = new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Bruised Apple" },
            Item = new Selector { Name = "Bruised Apple" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(goalByName, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_DifferentTarget_DoesNotMatch()
    {
        // Rejection targets Worcer; goal targets Jonathan — should pass.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Worcer",
        });

        var goal = new Goal
        {
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Jonathan" },
            Item = new Selector { Name = "Academy Exit Token" },
        };
        Assert.False(LlmGoalPolicy.IsGoalRecentlyRejected(goal, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_EmptyEvents_DoesNotMatch()
    {
        var es = new EventStream();
        var goal = new Goal
        {
            Kind = GoalKind.Talk,
            Target = new Selector { Name = "Worcer" },
        };
        Assert.False(LlmGoalPolicy.IsGoalRecentlyRejected(goal, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_OldRejection_OutsideWindow_DoesNotMatch()
    {
        // Slice O — widened the dedup lookback from 15 to 30 events.
        // Push the rejection then 35 unrelated events so it falls off
        // the window.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Worcer",
        });
        for (int i = 0; i < 35; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ServerMessage, Text = $"filler {i}",
            });
        }

        var goal = new Goal
        {
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Worcer" },
            Item = new Selector { Name = "A List of Items" },
        };
        Assert.False(LlmGoalPolicy.IsGoalRecentlyRejected(goal, es));
    }

    [Fact]
    public void IsGoalRecentlyRejected_ShortTargetName_SkipsSubstringMatch()
    {
        // Target name "Bob" (3 chars) is below the 4-char substring
        // gate, so a rejection text containing "Bob" should NOT match
        // unless it's an exact equality.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
            Text = "Unreachable: 'Bobblehead' (walk timeout 30s)",
        });

        var goal = new Goal
        {
            Kind = GoalKind.Talk,
            Target = new Selector { Name = "Bob" },
        };
        Assert.False(LlmGoalPolicy.IsGoalRecentlyRejected(goal, es));
    }

    // ---- Slice O — rejection diversity + widened dedup window ----
    //
    // Spike9 (Slice N validation) showed two prompt-side gaps:
    //   1) Recent rejections capped at Take(5); since every walk
    //      timeout emits an Unreachable, the 5-slot section was
    //      flooded with Unreachables and the rare-but-actionable
    //      TradeAiDoesntWant rejections were evicted within seconds.
    //   2) The dedup window (15 events) wasn't long enough to span
    //      a full observe/walk/timeout/retry loop; LLM re-emitted
    //      Give(Society Greeter, Calling Stone) 3 times with only
    //      one dedup hit.
    // Slice O: dedupe rejections by (label, target) and keep 8 of
    // the most-recent distinct combos; widen dedup window 15 → 30.

    [Fact]
    public void BuildUserPrompt_ManyUnreachables_DoesNotEvict_RareTradeAiDoesntWant()
    {
        var es = new EventStream();
        // Bury one TradeAiDoesntWant under 10 Unreachable rejections.
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Society Greeter",
        });
        for (int i = 0; i < 10; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ActionRejected,
                ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
                Name = $"Filler NPC {i}",
                Text = $"Unreachable: 'Filler NPC {i}' (walk timeout 30s)",
            });
        }

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        // Diversification: even though the TradeAiDoesntWant is the
        // OLDEST rejection, grouping by (label, target) preserves at
        // least one of each distinct combo. With Take(8) we still see
        // it plus a sampling of Unreachables.
        Assert.Contains("## Recent rejections", prompt);
        Assert.Contains("TradeAiDoesntWant", prompt);
        Assert.Contains("Society Greeter", prompt);
    }

    [Fact]
    public void BuildUserPrompt_DuplicateUnreachables_CollapseToOnePerTarget()
    {
        // Same NPC, same label → one row.
        var es = new EventStream();
        for (int i = 0; i < 5; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ActionRejected,
                ErrorCode = 0xFFFE, ErrorLabel = "Unreachable",
                Name = "Jonathan",
                Text = "Unreachable: 'Jonathan' (walk timeout 30s)",
            });
        }
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        // Find the rejection section.
        var start = prompt.IndexOf("## Recent rejections", StringComparison.Ordinal);
        Assert.True(start >= 0, "section header missing");
        // Count "Unreachable" occurrences after the header — should
        // collapse 5 duplicates into 1 line in the section.
        var section = prompt[start..];
        var nextHeader = section.IndexOf("\n## ", 1, StringComparison.Ordinal);
        if (nextHeader > 0) section = section[..nextHeader];
        var jonathanLines = section.Split('\n')
            .Count(l => l.Contains("Jonathan", StringComparison.Ordinal));
        Assert.Equal(1, jonathanLines);
    }

    [Fact]
    public void BuildUserPrompt_DwellEntry_RendersNumberWithoutLandblockChangedEvent()
    {
        // Regression for the town-stuck dwell bug: a bot that entered its
        // landblock via login/enter-world emits NO LandblockChanged event,
        // so the OLD event-window-only logic rendered the un-gateable
        // string "(no LandblockChanged event in retained window)" and the
        // town-stuck loop-break rule could never evaluate its `> 5` gate.
        // With a durable entry timestamp the prompt must render a NUMBER
        // even when the event stream holds NO LandblockChanged event.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "noise" });

        var entry = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(7);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), es, null, stack: null, pickerActivity: null,
            explorationCandidates: null, dwellEntryUtc: entry);

        Assert.DoesNotContain("no LandblockChanged event in retained window", prompt);
        var m = System.Text.RegularExpressions.Regex.Match(
            prompt, @"minutes in current landblock: (\d+\.\d)");
        Assert.True(m.Success, "dwell must render a numeric value");
        var dwell = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(dwell, 6.5, 7.5);
    }

    [Fact]
    public void BuildUserPrompt_DwellEntry_NullFallsBackToEventWindow()
    {
        // When no durable entry is supplied (e.g. unknown self-landblock),
        // the builder must preserve the prior event-window behaviour: with
        // no LandblockChanged event it renders the explicit "(no ...)"
        // string rather than fabricating a number.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "noise" });

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), es, null, stack: null, pickerActivity: null,
            explorationCandidates: null, dwellEntryUtc: null);

        Assert.Contains("minutes in current landblock: (no LandblockChanged event in retained window)", prompt);
    }

    [Fact]
    public void BuildUserPrompt_DwellEntry_ClampsNegativeToZero()
    {
        // A backward clock adjustment could put the entry stamp in the
        // future; the LLM must never see a negative dwell.
        var es = new EventStream();
        var futureEntry = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), es, null, stack: null, pickerActivity: null,
            explorationCandidates: null, dwellEntryUtc: futureEntry);

        Assert.Contains("minutes in current landblock: 0.0", prompt);
    }

    [Fact]
    public async Task LlmGoalPolicy_DwellTracking_RendersNumberWhenNoLandblockChangedEvent()
    {
        // End-to-end through ProposeGoal: with a known self-landblock and
        // NO LandblockChanged event in the stream, the durable tracker
        // stamps an entry on first observation so the prompt renders a
        // numeric dwell (the town-stuck gate becomes evaluable) instead of
        // the old un-gateable "(no ...)" string.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Explore",
          "target": { "name": "anywhere" },
          "item":   null,
          "priority": 4,
          "rationale": "exploring"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld(); // self landblock 0x8602
        var events = new EventStream();     // deliberately NO LandblockChanged
        events.Append(new StreamEvent { Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = "noise" });

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));

        using var doc = JsonDocument.Parse(requestBodies[0]);
        var prompt = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("no LandblockChanged event in retained window", prompt);
        Assert.Matches(@"minutes in current landblock: \d+\.\d", prompt);
    }

    [Fact]
    public void IsGoalRecentlyRejected_RejectionWithin30Events_StillMatches()
    {
        // Verify Slice O's widened window (was 15). Push 25 unrelated
        // events between rejection and check; under the old window
        // this would not match.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Society Greeter",
        });
        for (int i = 0; i < 25; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ServerMessage, Text = $"filler {i}",
            });
        }

        var goal = new Goal
        {
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Society Greeter" },
            Item = new Selector { Name = "Calling Stone" },
        };
        Assert.True(LlmGoalPolicy.IsGoalRecentlyRejected(goal, es));
    }

    // ---- Slice P (corpse-loot RULES bullet) ----
    //
    // The picker bumps unvisited corpses to priority bucket 0
    // (alongside NPCs) so the bot pivots to loot a fresh corpse
    // ahead of the next NPC. The LLM also needs a RULES bullet
    // teaching it to Use a corpse and then Pickup contents. This
    // test only asserts the bullet is present; the picker
    // behaviour itself is covered by live spike telemetry (no
    // unit-test seam without refactoring HandshakeDriver).

    [Fact]
    public void BuildUserPrompt_ContainsCorpseLootingRule()
    {
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("Looting:", prompt);
        Assert.Contains("corpse", prompt);
        Assert.Contains("Use{target: name=\"<corpse name>\"}", prompt);
        Assert.Contains("Pickup{target: name=\"<item>\"}", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RetainsEarlyExitPopup_UnderLaterPopupFlood()
    {
        // Codex review: newest-first Take(N) alone would let an early
        // one-time exit directive be crowded out by a flood of later
        // unique popups. The earliest-anchor bucket must retain it.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PopupString,
            Text = "Go talk to Jonathan in the next room. Once you leave you can never return.",
        });
        // 12 LATER distinct popups (well past any single Take bucket).
        for (int i = 0; i < 12; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.PopupString,
                Text = $"Cosmetic tutorial tip number {i}.",
            });
        }

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("## Server hints", prompt);
        Assert.Contains("Go talk to Jonathan in the next room", prompt);
    }

    [Fact]
    public void BuildUserPrompt_ContainsServerInstructionPrecedenceRule()
    {
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("SERVER-INSTRUCTION PRECEDENCE", prompt);
        Assert.Contains("you can never return", prompt);
    }

    [Fact]
    public void BuildUserPrompt_ContainsFinishMultiStepDirectiveRule()
    {
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("FINISH MULTI-STEP DIRECTIVES", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RetainsEarlyNpcDirective_UnderLaterNpcChatter()
    {
        // An NPC's early "give the token back to leave" instruction must
        // survive a later flood of unrelated NpcDialog (other tutors,
        // bystanders) — same durability requirement as PopupString.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Name = "Jonathan",
            Text = "If you want to skip your training and leave the Academy early, give this token back to me.",
        });
        for (int i = 0; i < 12; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.NpcDialog, Name = $"Tutor{i}",
                Text = $"Unrelated tutorial chatter number {i}.",
            });
        }

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("## Server hints", prompt);
        Assert.Contains("give this token back to me", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RendersPopupStringHint_Durably()
    {
        // A PopupString carrying an exit directive must survive in the
        // durable "## Server hints" section even after the 25-event
        // generic tail has been flooded with newer events — otherwise
        // the one-time "go talk to X to leave" instruction is lost.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PopupString,
            Text = "If you wish to skip this tutorial, go talk to Jonathan in the next room.",
        });
        // Bury it under 30 newer generic events (beyond the 25-tail).
        for (int i = 0; i < 30; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.HealthChanged,
                Text = $"move {i}",
            });
        }

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("## Server hints", prompt);
        Assert.Contains("PopupString:", prompt);
        Assert.Contains("go talk to Jonathan in the next room", prompt);
    }

    [Fact]
    public void BuildUserPrompt_DeduplicatesRepeatedPopupStrings()
    {
        var es = new EventStream();
        for (int i = 0; i < 5; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.PopupString,
                Text = "Double-click on an armor piece in your inventory in order to wear it.",
            });
        }

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        // The hint section renders each unique popup once as
        // `- PopupString: "..."`. (The raw text may also appear in the
        // generic Recent-events tail, so match the hint-line prefix.)
        var needle = "- PopupString: \"Double-click on an armor piece";
        var idx = prompt.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(idx >= 0, "expected the popup hint to render");
        var idx2 = prompt.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        Assert.True(idx2 < 0, "duplicate popup strings should be collapsed to one");
    }

    // ---- Slice G — server-hints prompt section regression ----
    //
    // In rejfix-run-01 the bot teleported to Holtburg, saw the Life
    // Stone, received ServerMessage "Double click the lifestone to
    // use it", and never emitted Use(Life Stone). Hypothesis: the
    // hint rolled off the Recent(15) window before the LLM was
    // re-triggered while the lifestone was still close. Slice G
    // bumps Recent → 25 AND adds a dedicated "## Server hints"
    // section pulling from the full event capacity.

    [Fact]
    public async Task LlmGoalPolicy_ServerHints_PersistAcrossEventWindow()
    {
        // Scenario:
        //   1) Append a salient ServerMessage with tutorial text.
        //   2) Append 30 more events of varied kinds (more than
        //      Recent(25) cap). This evicts the hint from the
        //      generic Recent tail.
        //   3) Trigger a fresh LLM call. The captured request body
        //      must:
        //        - contain "## Server hints" section
        //        - include the tutorial text inside that section
        //        - NOT contain the tutorial text inside the
        //          "## Recent events" section (too old)
        //        - dedupe exact-duplicate ServerMessages
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Use",
          "target": { "name": "Life Stone" },
          "item":   null,
          "priority": 7,
          "rationale": "Server told me to double-click the lifestone."
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });

        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildHoltburgLifestoneWorld();
        var events = new EventStream();

        // Tutorial hint arrives first.
        const string lifestoneHint = "Double click the lifestone to use it.";
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ServerMessage, ChatType = 0,
            Text = lifestoneHint,
        });
        // An exact-duplicate banner that should dedupe inside hints.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ServerMessage, ChatType = 0,
            Text = lifestoneHint,
        });

        // Push 30 unrelated events to evict the hint from Recent(25)
        // but stay well under the 256-event ring capacity.
        for (int i = 0; i < 30; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.NpcDialog, Name = $"Bystander{i}",
                Text = $"Idle chatter line {i} that should NOT eclipse the tutorial hint.",
            });
        }

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(goal);
        Assert.Single(requestBodies);

        var body = requestBodies[0];

        // The dedicated Server hints section must be present.
        // Match the exact section header — the RULES block also
        // mentions "## Server hints" so we have to disambiguate.
        const string hintsHeader  = "## Server hints (recent";
        const string recentHeader = "## Recent events (";
        Assert.Contains(hintsHeader, body);

        var hintsIdx  = body.IndexOf(hintsHeader, StringComparison.Ordinal);
        var recentIdx = body.IndexOf(recentHeader, StringComparison.Ordinal);
        Assert.True(hintsIdx >= 0);
        Assert.True(recentIdx > hintsIdx, "## Server hints must come before ## Recent events");

        var hintsBlock = body.Substring(hintsIdx, recentIdx - hintsIdx);
        var recentBlock = body.Substring(recentIdx);

        // Tutorial hint must be in the Server hints block.
        Assert.Contains(lifestoneHint, hintsBlock);

        // And the duplicate must have been deduped (appears once
        // inside the hints block).
        var hintsHits = System.Text.RegularExpressions.Regex.Matches(
            hintsBlock, System.Text.RegularExpressions.Regex.Escape(lifestoneHint)).Count;
        Assert.Equal(1, hintsHits);

        // It must NOT be in the Recent events block (was evicted).
        Assert.DoesNotContain(lifestoneHint, recentBlock);

        // Life Stone visible-nearby line still carries the lifestone tag.
        Assert.Contains("Life Stone", body);
        Assert.Contains("lifestone", body);
    }

    [Fact]
    public async Task LlmGoalPolicy_ServerHints_OrderingOldestFirst()
    {
        // The hints section now renders oldest-first (chronological) so a
        // multi-step directive reads in the order it was given. Append two
        // distinct hints in order then assert the first one appears earlier
        // (smaller offset) than the second within the section.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Use",
          "target": { "name": "Life Stone" },
          "item":   null,
          "priority": 7,
          "rationale": "tutorial"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildHoltburgLifestoneWorld();
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ServerMessage, ChatType = 0,
            Text = "FIRST-HINT older message",
        });
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ServerMessage, ChatType = 0,
            Text = "SECOND-HINT newer message",
        });

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));

        var body = requestBodies[0];
        var hintsIdx = body.IndexOf("## Server hints (recent", StringComparison.Ordinal);
        var endIdx = body.IndexOf("## Recent events (", StringComparison.Ordinal);
        Assert.True(hintsIdx >= 0 && endIdx > hintsIdx);
        var block = body.Substring(hintsIdx, endIdx - hintsIdx);

        var firstHintAt = block.IndexOf("FIRST-HINT", StringComparison.Ordinal);
        var secondHintAt = block.IndexOf("SECOND-HINT", StringComparison.Ordinal);
        Assert.True(firstHintAt > 0 && secondHintAt > 0);
        Assert.True(firstHintAt < secondHintAt, "older hint should appear earlier (oldest-first chronological)");
    }

    [Fact]
    public async Task LlmGoalPolicy_VisibleNearby_TagsMonsterVsNpc()
    {
        // Slice H — server-derived friend/foe classification must appear
        // as `monster` vs `npc` tags in the prompt's Visible nearby
        // section. Both tags come from wire data (IsAttackable +
        // HasRadarBlipColor), never from hardcoded wcid/name lists.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Attack",
          "target": { "name": "Sparring Golem" },
          "item":   null,
          "priority": 6,
          "rationale": "monster nearby"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildAcademyCombatWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));
        Assert.Single(requestBodies);

        var body = requestBodies[0];
        // Body is JSON-encoded — the user prompt sits inside
        // messages[1].content with newlines escaped. Decode it so we
        // can slice on real line boundaries.
        using var doc = JsonDocument.Parse(body);
        var prompt = doc.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        var visIdx = prompt.IndexOf("## Visible nearby", StringComparison.Ordinal);
        Assert.True(visIdx >= 0);
        // Slice the prompt to just the Visible nearby section so we
        // assert tags only where they belong (otherwise RULES line
        // mentioning `monster` would mask a missing tag bug).
        var afterVis = prompt.IndexOf("##", visIdx + 1, StringComparison.Ordinal);
        var visBlock = afterVis > visIdx ? prompt.Substring(visIdx, afterVis - visIdx) : prompt.Substring(visIdx);

        // Monster line: Sparring Golem must be tagged `monster`, not
        // generic `creature`, and not `npc`.
        var golemIdx = visBlock.IndexOf("Sparring Golem", StringComparison.Ordinal);
        Assert.True(golemIdx >= 0, "Sparring Golem missing from Visible nearby");
        var golemLineEnd = visBlock.IndexOf('\n', golemIdx);
        if (golemLineEnd < 0) golemLineEnd = visBlock.Length;
        var golemLine = visBlock.Substring(golemIdx, golemLineEnd - golemIdx);
        Assert.Contains("monster", golemLine);
        Assert.DoesNotContain(" npc", golemLine);

        // NPC line: Jonathan must be tagged `npc`, not `monster`.
        var jonIdx = visBlock.IndexOf("Jonathan", StringComparison.Ordinal);
        Assert.True(jonIdx >= 0, "Jonathan missing from Visible nearby");
        var jonathanLineEnd = visBlock.IndexOf('\n', jonIdx);
        if (jonathanLineEnd < 0) jonathanLineEnd = visBlock.Length;
        var jonathanLine = visBlock.Substring(jonIdx, jonathanLineEnd - jonIdx);
        Assert.Contains(" npc", jonathanLine);
        Assert.DoesNotContain("monster", jonathanLine);

        // Slice H RULES line is present (so the LLM knows what `monster` means).
        Assert.Contains("Combat: creatures tagged `monster`", prompt);
    }

    [Fact]
    public async Task LlmGoalPolicy_CombatReadiness_SectionReflectsState()
    {
        // Slice H — Combat readiness section must summarize weapon
        // status + nearest monster so the LLM has an at-a-glance
        // "should I fight now?" signal.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Attack",
          "target": { "name": "Sparring Golem" },
          "item":   null,
          "priority": 6,
          "rationale": "combat ready"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildAcademyCombatWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));

        var body = requestBodies[0];
        var crIdx = body.IndexOf("## Combat readiness", StringComparison.Ordinal);
        Assert.True(crIdx >= 0);
        var afterCr = body.IndexOf("##", crIdx + 1, StringComparison.Ordinal);
        var crBlock = afterCr > crIdx ? body.Substring(crIdx, afterCr - crIdx) : body.Substring(crIdx);

        // Weapon line — inventory has a wielded item so should say "wielded".
        Assert.Contains("weapon: wielded", crBlock);
        // Monster line — Sparring Golem is nearest monster in BuildAcademyCombatWorld.
        Assert.Contains("nearest monster: Sparring Golem", crBlock);
    }

    [Fact]
    public async Task LlmGoalPolicy_CombatReadiness_NoMonster_NoWeapon()
    {
        // Slice H — Combat readiness section must handle the empty
        // case cleanly (no wielded weapon, no monster in view) so the
        // LLM never sees malformed text and over-interprets a missing
        // line.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Jonathan" },
          "item":   { "name": "Academy Exit Token" },
          "priority": 8,
          "rationale": "ShortDesc"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        // BuildExitTokenWorld has Jonathan (npc, not monster), an
        // un-wielded inventory item — no weapon, no monster.
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));

        var body = requestBodies[0];
        var crIdx = body.IndexOf("## Combat readiness", StringComparison.Ordinal);
        Assert.True(crIdx >= 0);
        var afterCr = body.IndexOf("##", crIdx + 1, StringComparison.Ordinal);
        var crBlock = afterCr > crIdx ? body.Substring(crIdx, afterCr - crIdx) : body.Substring(crIdx);

        Assert.Contains("weapon: NOT wielded", crBlock);
        Assert.Contains("nearest monster: (none in view)", crBlock);
    }

    [Fact]
    public async Task LlmGoalPolicy_LocationRecency_LandblockDwellAndTalkCounts()
    {
        // Slice I — Location & recency section must surface (a) how
        // long the bot has been in the current landblock since the
        // most recent LandblockChanged event, and (b) per-NPC Talk
        // emission counts in the last 10 GoalEmitted events. Both
        // signals come from the EventStream — no hardcoded knowledge.
        // The LOOP-BREAK rule below references these counts directly.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Explore",
          "target": { "name": "anywhere" },
          "item":   null,
          "priority": 4,
          "rationale": "stuck talking"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();
        // Seed: LandblockChanged 8 minutes ago INTO the bot's current
        // landblock (0x8602 — matches BuildExitTokenWorld's self), so the
        // durable dwell tracker anchors entry to this observed transition.
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(8),
            Kind = EventKind.LandblockChanged,
            LandblockFrom = 0xA9B4u,
            LandblockTo = 0x8602u,
        });
        // Seed: 4 Talk goals to "Buckminster", 1 to "Alcott".
        for (var i = 0; i < 4; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(7 - i),
                Kind = EventKind.GoalEmitted,
                GoalId = Guid.NewGuid(),
                Text = "Talk target=name=\"Buckminster\" item= source=llm:openai/gpt-4o-mini",
            });
        }
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            Kind = EventKind.GoalEmitted,
            GoalId = Guid.NewGuid(),
            Text = "Talk target=name=\"Alcott\" item= source=llm:openai/gpt-4o-mini",
        });

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));

        var body = requestBodies[0];
        using var doc = JsonDocument.Parse(body);
        var prompt = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        // Section header present.
        var lrIdx = prompt.IndexOf("## Location & recency", StringComparison.Ordinal);
        Assert.True(lrIdx >= 0, "Location & recency section missing");
        var afterLr = prompt.IndexOf("##", lrIdx + 1, StringComparison.Ordinal);
        var lrBlock = afterLr > lrIdx ? prompt.Substring(lrIdx, afterLr - lrIdx) : prompt.Substring(lrIdx);

        // Dwell minutes — ~8 minutes (allow 7.5 to 8.5 for clock skew).
        Assert.Contains("minutes in current landblock:", lrBlock);
        var dwellMatch = System.Text.RegularExpressions.Regex.Match(lrBlock, @"minutes in current landblock: (\d+\.\d)");
        Assert.True(dwellMatch.Success, "dwell minutes line missing or malformed");
        var dwell = double.Parse(dwellMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(dwell, 7.5, 8.5);

        // Per-NPC Talk counts.
        Assert.Contains("recent Talk emissions", lrBlock);
        Assert.Contains("Buckminster: x4", lrBlock);
        Assert.Contains("Alcott: x1", lrBlock);

        // LOOP-BREAK rule references the dwell signal and Explore as
        // the escape hatch.
        Assert.Contains("LOOP-BREAK", prompt);
        Assert.Contains("Explore", prompt);
    }

    [Fact]
    public async Task LlmGoalPolicy_LocationRecency_WorldUseCounts()
    {
        // Open-world door-fixation guard — the Location & recency
        // section must surface per-target Use emission counts so the
        // LLM can see when it is re-Using the SAME world object (e.g. a
        // building door that opens but never transports it). The count
        // collapses repeated Uses of one target and keeps distinct
        // targets separate; the world-object USE loop-break rule
        // references it. Purely the bot's own emission history, counted
        // by structure — no server text or object-type knowledge.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Explore",
          "target": { "name": "anywhere" },
          "item":   null,
          "priority": 4,
          "rationale": "stuck on a door"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };

        var world = BuildExitTokenWorld();
        var events = new EventStream();
        // Seed: 4 Use goals against the SAME door — three carry guid+name,
        // one carries guid only — to prove canonical collapse across
        // selector variants. Plus 1 Use against a different object.
        for (var i = 0; i < 4; i++)
        {
            var sel = i == 3 ? "guid=0x7A9B4017" : "guid=0x7A9B4017 name=\"Door\"";
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(7 - i),
                Kind = EventKind.GoalEmitted,
                GoalId = Guid.NewGuid(),
                Text = $"Use target={sel} item= source=llm:openai/gpt-4o-mini",
            });
        }
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            Kind = EventKind.GoalEmitted,
            GoalId = Guid.NewGuid(),
            Text = "Use target=name=\"Lever\" item= source=llm:openai/gpt-4o-mini",
        });

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));

        var body = requestBodies[0];
        using var doc = JsonDocument.Parse(body);
        var prompt = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        var lrIdx = prompt.IndexOf("## Location & recency", StringComparison.Ordinal);
        Assert.True(lrIdx >= 0, "Location & recency section missing");
        var afterLr = prompt.IndexOf("##", lrIdx + 1, StringComparison.Ordinal);
        var lrBlock = afterLr > lrIdx ? prompt.Substring(lrIdx, afterLr - lrIdx) : prompt.Substring(lrIdx);

        // Per-target Use counts: the repeated door collapses to x4 keyed
        // by its emitted selector; the distinct Lever stays x1.
        Assert.Contains("recent Use emissions", lrBlock);
        Assert.Contains("x4", lrBlock);
        Assert.Contains("guid=0x7A9B4017", lrBlock);
        Assert.Contains("Lever", lrBlock);

        // The world-object USE loop-break rule must be present.
        Assert.Contains("LOOP-BREAK (world-object USE loop)", prompt);

        // The passage-opened-is-not-progress rule must be present so the
        // model does not treat "door opened" as a qualifying state change
        // that justifies re-Using the same door instead of moving through.
        Assert.Contains("PASSAGE-OPENED is not progress", prompt);
    }

    private sealed class ToggleablePolicy : IGoalPolicy
    {
        public bool InflightFlag;
        public string Source => "test:toggle";
        public bool HasInflight => InflightFlag;
        public Goal? ProposeGoal(WorldStateProjection world, EventStream events, Goal? currentGoal)
            => currentGoal;
    }

    // ---- Slice W.1 (#86) — picker activity bypasses coalesce ----

    [Fact]
    public void HasPickerActivityStartedSince_DetectsEvent()
    {
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ServerMessage,
            Text = "some chatter",
        });
        var floor = events.NextSequence;
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0x800001D4u,
            Name = "Samuel",
            Text = "in-range: nearest mechanically-eligible candidate",
        });
        Assert.True(LlmGoalPolicy.HasPickerActivityStartedSince(events, floor));
    }

    [Fact]
    public void HasPickerActivityStartedSince_ReturnsFalseWhenAbsent()
    {
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted, ItemGuid = 1u, Name = "Old", Text = "in-range",
        });
        var floor = events.NextSequence;
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ServerMessage, Text = "later chatter",
        });
        // Floor is AFTER the picker event — nothing salient since.
        Assert.False(LlmGoalPolicy.HasPickerActivityStartedSince(events, floor));
    }

    [Fact]
    public async Task LlmGoalPolicy_PickerActivityStarted_BypassesCoalesce()
    {
        // Slice W.1 (#86): without this bypass the picker can pick a
        // new target, walk to it, and dispatch an action all within
        // one MinCallInterval window — the LLM never gets to steer.
        // After this change a PickerActivityStarted event since the
        // last LLM look forces a fresh call even inside the coalesce
        // window. (currentGoal must be non-null so the coalesce gate
        // is the one being exercised, not the no-goal short-circuit.)
        var httpCallCount = 0;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = JsonSerializer.Serialize(new
                        {
                            goal_id = "11111111-2222-3333-4444-555555555555",
                            kind = "Explore",
                            target = new { name = "anywhere" },
                            priority = 5,
                            expires_in_seconds = 60,
                        }),
                    },
                },
            },
        });
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref httpCallCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        // MinCallInterval LARGE so the only way a second call goes
        // out within this test is via the picker-activity bypass.
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.FromHours(1),
        };

        var world = BuildHostileWorld();
        var events = new EventStream();

        // First call kicks off + completes the first LLM HTTP.
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var firstGoal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(firstGoal);
        Assert.Equal(1, httpCallCount);

        // Within coalesce window WITHOUT picker activity: no new call.
        // The same goal stays in play and the http counter is unchanged.
        var stayed = policy.ProposeGoal(world, events, firstGoal);
        Assert.Equal(1, httpCallCount);
        Assert.Equal(firstGoal, stayed);

        // Now publish a picker-activity-started event AFTER the last
        // LLM look. Same coalesce window. New call MUST go out.
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0x800001D4u,
            Name = "Samuel",
            Text = "in-range: nearest mechanically-eligible candidate",
        });
        var afterPicker = policy.ProposeGoal(world, events, firstGoal);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, httpCallCount);
    }

    // ---- reduce-llm-call-volume — picker-start coalesce + dedupe ----

    private static (LlmGoalClient llm, Func<int> count) CannedExploreLlm()
    {
        var httpCallCount = 0;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = JsonSerializer.Serialize(new
                        {
                            goal_id = "11111111-2222-3333-4444-555555555555",
                            kind = "Explore",
                            target = new { name = "anywhere" },
                            priority = 5,
                            expires_in_seconds = 60,
                        }),
                    },
                },
            },
        });
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref httpCallCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        return (llm, () => Volatile.Read(ref httpCallCount));
    }

    [Fact]
    public async Task LlmGoalPolicy_PickerStart_SameTargetWithinWindow_Suppressed()
    {
        // reduce-llm-call-volume: a NEW picker-start target wakes the LLM,
        // but a REPEAT start for the SAME target inside PickerStartCoalesce
        // must NOT burn another call — the autonomous picker churning on
        // one target should not keep waking the strategy layer.
        var (llm, count) = CannedExploreLlm();
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.FromHours(1),
            PickerStartCoalesce = TimeSpan.FromHours(1), // same-target never elapses in this test
        };
        var world = BuildHostileWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(goal);
        Assert.Equal(1, count());

        // First start for target A → wakes (new target).
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal);
        await policy.WaitForInFlightAsync();
        var goal2 = policy.ProposeGoal(world, events, goal); // consume the 2nd result
        Assert.Equal(2, count());

        // Second start for the SAME target A within the window → suppressed.
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        var stayed = policy.ProposeGoal(world, events, goal2);
        Assert.Equal(2, count());        // no new call
        Assert.Equal(goal2, stayed);     // keeps driving the current goal
    }

    [Fact]
    public async Task LlmGoalPolicy_PickerStart_DifferentTarget_Wakes()
    {
        // reduce-llm-call-volume: a start for a DIFFERENT target than the
        // last picker-start wake still wakes immediately — the LLM never
        // loses the chance to override a genuinely new autonomous pick.
        var (llm, count) = CannedExploreLlm();
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.FromHours(1),
            PickerStartCoalesce = TimeSpan.FromHours(1),
        };
        var world = BuildHostileWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.Equal(1, count());

        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal);
        await policy.WaitForInFlightAsync();
        var goal2 = policy.ProposeGoal(world, events, goal);
        Assert.Equal(2, count());

        // Different target B → wakes despite the coalesce window.
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xBBBB0002u, Name = "B", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal2);
        await policy.WaitForInFlightAsync();
        Assert.Equal(3, count());
    }

    [Fact]
    public async Task LlmGoalPolicy_PickerStart_SameTargetAfterWindow_Wakes()
    {
        // reduce-llm-call-volume: once PickerStartCoalesce elapses, a
        // repeat start for the same target is allowed to wake again (the
        // suppression is a rate limit, not a permanent block).
        var (llm, count) = CannedExploreLlm();
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.FromHours(1),
            PickerStartCoalesce = TimeSpan.FromMilliseconds(100),
        };
        var world = BuildHostileWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.Equal(1, count());

        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal);
        await policy.WaitForInFlightAsync();
        var goal2 = policy.ProposeGoal(world, events, goal);
        Assert.Equal(2, count());

        await Task.Delay(300); // exceed the 100ms coalesce window

        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal2);
        await policy.WaitForInFlightAsync();
        Assert.Equal(3, count());
    }

    [Fact]
    public void NewestPickerStartTargetKeySince_PrefersGuidThenNameThenSeq()
    {
        var events = new EventStream();
        // No picker-start → null.
        Assert.Null(LlmGoalPolicy.NewestPickerStartTargetKeySince(events, -1));

        var guidEv = events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0x1234ABCDu, Name = "Ignored",
        });
        Assert.Equal("0x1234ABCD", LlmGoalPolicy.NewestPickerStartTargetKeySince(events, -1));

        // Zero guid falls back to name.
        var nameEv = events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0u, Name = "OnlyName",
        });
        Assert.Equal("name:OnlyName", LlmGoalPolicy.NewestPickerStartTargetKeySince(events, -1));

        // Neither guid nor name falls back to the event's own sequence.
        var seqEv = events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0u, Name = null,
        });
        Assert.Equal($"seq:{seqEv.Sequence}", LlmGoalPolicy.NewestPickerStartTargetKeySince(events, -1));

        // Arrived events are not picker-START — ignored by this helper.
        var floorAfter = events.NextSequence;
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerArrivedNoAction,
            ItemGuid = 0x9999u, Name = "Arrived",
        });
        Assert.Null(LlmGoalPolicy.NewestPickerStartTargetKeySince(events, floorAfter));
    }

    [Fact]
    public async Task LlmGoalPolicy_SuppressedPickerStart_GoalClears_StickyReEmitsWithoutCall()
    {
        // reduce-llm-call-volume regression guard (rubber-duck finding):
        // a suppressed picker-start advances the event floor past itself,
        // so when the goal later clears the sticky-objective gate is NOT
        // tripped by the stale picker-start and re-emits the objective for
        // FREE (no LLM round trip).
        var (llm, count) = CannedExploreLlm();
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.FromHours(1),
            PickerStartCoalesce = TimeSpan.FromHours(1),
        };
        var world = BuildHostileWorld();
        var events = new EventStream();

        // Establish an LLM goal (sets _lastLlmGoal for the sticky path).
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(goal);
        Assert.Equal(1, count());

        // Target A wakes once.
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal);
        await policy.WaitForInFlightAsync();
        var goal2 = policy.ProposeGoal(world, events, goal);
        Assert.Equal(2, count());

        // SAME target A within the window → suppressed (advances the floor).
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal2);
        Assert.Equal(2, count());

        // Goal clears → sticky re-emit, no LLM call (the stale picker-start
        // was consumed by the suppression, so the gate is not tripped).
        var sticky = policy.ProposeGoal(world, events, null);
        Assert.NotNull(sticky);
        Assert.Equal(2, count());
    }

    [Fact]
    public async Task LlmGoalPolicy_SuppressedPickerStart_DoesNotHideInventoryRemoved()
    {
        // reduce-llm-call-volume regression guard (rubber-duck finding):
        // InventoryItemRemoved is EXTERNAL but not salient. A picker-start
        // sharing its window must NOT let the suppression advance the floor
        // past the removal — otherwise a completed Give would be hidden
        // from the sticky gate and the bot would wrongly re-drive it. When
        // the goal clears, the LLM MUST be consulted (no free sticky).
        var (llm, count) = CannedExploreLlm();
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.FromHours(1),
            PickerStartCoalesce = TimeSpan.FromHours(1),
        };
        var world = BuildHostileWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.Equal(1, count());

        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        policy.ProposeGoal(world, events, goal);
        await policy.WaitForInFlightAsync();
        var goal2 = policy.ProposeGoal(world, events, goal);
        Assert.Equal(2, count());

        // An external InventoryItemRemoved arrives alongside a same-target
        // picker-start. The picker-start alone would be suppressed, but the
        // removal must block the floor-advance.
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemRemoved,
            ItemGuid = 0xCAFE0001u, Name = "Calling Stone",
        });
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        // InventoryItemRemoved does not wake while a goal is active.
        policy.ProposeGoal(world, events, goal2);
        Assert.Equal(2, count());

        // Goal clears → the removal is still visible to the sticky gate →
        // NO free sticky re-emit → a real LLM call fires.
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(3, count());
    }

    // ---- Slice W.3 (#88) — arrived-no-action prompt + salience ----

    [Fact]
    public void BuildUserPrompt_PickerActivity_Investigating_RendersInvestigatingForm()
    {
        // Default (Arrived=false) — picker is en-route to the
        // target. Prompt should NOT claim arrival; the fallback note
        // about "Emit a goal to take control" stays.
        var world = BuildHostileWorld();
        var events = new EventStream();
        var activity = new PickerActivity
        {
            TargetGuid = 0x80000099u,
            TargetName = "Some Object",
            Source = "in-range",
            Reason = "test",
            StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-3),
            Arrived = false,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, currentGoal: null, stack: null, pickerActivity: activity);

        // Section header rendered (the RULES section above also
        // references the literal string inside backticks, so we
        // assert on the start-of-line form which only the actual
        // section uses — Environment.NewLine for cross-platform).
        var nl = Environment.NewLine;
        Assert.Contains($"{nl}## Autonomous picker activity{nl}", prompt);
        Assert.Contains("picker is investigating target 0x80000099", prompt);
        // The Arrived-form rendered line includes the specific guid
        // ("- picker has ARRIVED at target 0x{guid:X8}"); the RULES
        // bullet uses the placeholder "target X". Match on the
        // guid-suffix to discriminate.
        Assert.DoesNotContain("ARRIVED at target 0x80000099", prompt);
        // Old fallback note text stays for investigating form.
        Assert.Contains("Emit a goal to take control", prompt);
    }

    [Fact]
    public void BuildUserPrompt_PickerActivity_Arrived_RendersAwaitingVerbForm()
    {
        // Slice W.3: Arrived=true means motor parked next to target
        // and sent NO opcode. Prompt MUST switch wording so LLM
        // knows it's the ONLY thing keeping the bot from acting.
        var world = BuildHostileWorld();
        var events = new EventStream();
        var activity = new PickerActivity
        {
            TargetGuid = 0x800001CEu,
            TargetName = "Jonathan",
            Source = "in-range",
            Reason = "schema-only picker (nearest mechanically-eligible candidate)",
            StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-46),
            Arrived = true,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, currentGoal: null, stack: null, pickerActivity: activity);

        Assert.Contains("picker has ARRIVED at target 0x800001CE", prompt);
        Assert.Contains("Jonathan", prompt);
        Assert.Contains("awaiting a verb", prompt);
        // The arrived-form note must NOT keep the en-route "emit a
        // goal to take control" wording (that implies bot is still
        // moving). It MUST explicitly call out the parking window.
        Assert.Contains("parked", prompt);
        // Picker never auto-acts on Arrived — message must make
        // clear motor did NOT send an opcode.
        Assert.Contains("NOT sent any opcode", prompt);
        // Investigating wording must NOT appear when arrived.
        Assert.DoesNotContain("picker is investigating", prompt);
    }

    [Fact]
    public async Task LlmGoalPolicy_PickerArrivedNoAction_BypassesCoalesce()
    {
        // Slice W.3 (#88): when picker arrives without a goal, the
        // motor parks and emits PickerArrivedNoAction. The LLM MUST
        // wake immediately even with an existing currentGoal in
        // play — otherwise the 2s park-then-move-on window expires
        // before deliberation completes and the picker walks away
        // again, leaving the bot in a perpetual walking-but-not-
        // doing loop. Pattern mirrors PickerActivityStarted.
        var httpCallCount = 0;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = JsonSerializer.Serialize(new
                        {
                            goal_id = "11111111-2222-3333-4444-555555555555",
                            kind = "Use",
                            target = new { name = "Jonathan" },
                            priority = 8,
                            expires_in_seconds = 60,
                        }),
                    },
                },
            },
        });
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref httpCallCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.FromHours(1),
        };

        var world = BuildHostileWorld();
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var firstGoal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(firstGoal);
        Assert.Equal(1, httpCallCount);

        // Coalesce window holds with no new salient event.
        var stayed = policy.ProposeGoal(world, events, firstGoal);
        Assert.Equal(1, httpCallCount);
        Assert.Equal(firstGoal, stayed);

        // PickerArrivedNoAction MUST punch through the coalesce gate.
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction,
            ItemGuid = 0x800001CEu,
            Name = "Jonathan",
            Text = "picker walked to target with no verb goal in flight",
        });
        policy.ProposeGoal(world, events, firstGoal);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, httpCallCount);
    }

    [Fact]
    public void EventKind_PickerArrivedNoAction_IsDistinctFromStarted()
    {
        // Defensive: the two events must NOT collide. Started fires
        // on EVERY picker target switch (high frequency, noisy);
        // ArrivedNoAction fires only when the bot has WALKED to the
        // target and there was no verb to dispatch (rare, salient,
        // must wake LLM). Both are salient but downstream telemetry
        // distinguishes them by kind, so the enum values must differ.
        Assert.NotEqual(EventKind.PickerActivityStarted, EventKind.PickerArrivedNoAction);
        Assert.NotEqual(EventKind.PickerActivityCompleted, EventKind.PickerArrivedNoAction);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // ---- Helpers ----

    private const uint SelfGuid = 0x50000005;
    private const uint NpcGuid  = 0x90000010;
    private const uint MobGuid  = 0x90000020;
    private const uint ItemGuid = 0x80000030;

    private static WorldStateProjection BuildHostileWorld() => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u, CellId = 0x86020001u,
            PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = new[]
        {
            new VisibleObjectProjection
            {
                Guid = MobGuid, Name = "Sparring Golem", Wcid = 12698u,
                ItemType = 0x10u, Distance = 5f, IsCreature = true, ObservedHostile = true,
            },
        },
    };

    private static WorldStateProjection BuildExitTokenWorld() => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u, CellId = 0x86020001u,
            PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
        },
        Inventory = new[]
        {
            new InventoryItemProjection
            {
                Guid = ItemGuid, Name = "Academy Exit Token", Wcid = 29335u,
                ItemType = 0x800u, ShortDesc = "Give this token to Jonathan ...",
            },
        },
        Visible = new[]
        {
            new VisibleObjectProjection
            {
                Guid = NpcGuid, Name = "Jonathan", Wcid = 29324u, ItemType = 0x10u,
                Distance = 3f, IsCreature = true, ObservedHostile = false,
            },
        },
    };

    private const uint LifestoneGuid = 0x7A9B404Fu;

    private static WorldStateProjection BuildHoltburgLifestoneWorld() => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B4u, CellId = 0xA9B40019u,
            PositionX = 84f, PositionY = 7.1f, PositionZ = 94f, HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = new[]
        {
            new VisibleObjectProjection
            {
                Guid = LifestoneGuid, Name = "Life Stone", Wcid = 509u,
                ItemType = 0x10000000u, Distance = 2.42f,
                IsLifestone = true,
            },
            new VisibleObjectProjection
            {
                Guid = NpcGuid, Name = "Pathwarden Thorolf", Wcid = 30001u,
                ItemType = 0x10u, Distance = 6f, IsCreature = true,
            },
        },
    };

    // Slice H — academy view with one wielded weapon, one peaceful
    // NPC (Jonathan), and one Sparring Golem the bot can attack.
    // Sparring Golem flags: IsAttackable=true, HasRadarBlipColor=false
    // → IsMonster=true. Jonathan: IsAttackable=true, HasRadarBlipColor
    // =true (every civilian gets a custom minimap color) → IsMonster
    // =false. Mirrors what live ObjectCreate emits in the academy.
    private const uint WeaponGuid = 0x80000040;

    private static WorldStateProjection BuildAcademyCombatWorld() => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u, CellId = 0x86020001u,
            PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
        },
        Inventory = new[]
        {
            new InventoryItemProjection
            {
                Guid = WeaponGuid, Name = "Training Spadone", Wcid = 5104u,
                ItemType = 0x1u, WieldedAt = 0x1u,
            },
        },
        Visible = new[]
        {
            new VisibleObjectProjection
            {
                Guid = NpcGuid, Name = "Jonathan", Wcid = 29324u, ItemType = 0x10u,
                Distance = 3f, IsCreature = true, IsAttackable = true,
                HasRadarBlipColor = true, IsMonster = false,
            },
            new VisibleObjectProjection
            {
                Guid = MobGuid, Name = "Sparring Golem", Wcid = 12698u,
                ItemType = 0x10u, Distance = 7f, IsCreature = true,
                IsAttackable = true, HasRadarBlipColor = false, IsMonster = true,
            },
        },
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request, cancellationToken));
    }

    private sealed class AsyncStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _fn(request, cancellationToken);
    }

    private sealed class InMemoryWeenieRepo : IWeenieRepository
    {
        public WeenieStringRecord? TryGet(uint wcid) => null;
        public Task EnsureLoadedAsync(uint wcid, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- 2026-05-30 Inventory-USE dedup ----
    //
    // Stalenarrow01 spike captured the LLM emitting Use{Letter From
    // Home} 5 times in 3 min against a non-consumable tutorial letter
    // whose short_desc ("double-click to read") never goes away.
    // That ~55% of all LLM-driven goals crowded out Attack emission
    // against a visible Sparring Golem. Fix: record each inventory-
    // USE dispatch as EventKind.InventoryItemUsed, drop subsequent
    // Use goals against the same item in IsInventoryUseRecentlyDispatched,
    // surface the recency to the LLM via a new prompt section.

    private const uint LetterGuid = 0x8000047Eu;
    private const uint LetterWcid = 8326u;

    private static StreamEvent InvUsed(string name, uint wcid, uint guid) => new()
    {
        Sequence = -1, Utc = DateTimeOffset.UtcNow,
        Kind = EventKind.InventoryItemUsed,
        ItemGuid = guid, Wcid = wcid, Name = name,
    };

    [Fact]
    public void IsInventoryUseRecentlyDispatched_MatchesByItemWcid()
    {
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));

        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Headless" },
            Item = new Selector { Wcid = LetterWcid },
        };
        Assert.True(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_MatchesByItemName()
    {
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));

        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Headless" },
            Item = new Selector { Name = "letter from home" }, // case-insensitive
        };
        Assert.True(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_MatchesByTargetWhenLlmPutsItemAsTarget()
    {
        // The inventory-USE prompt path tells the LLM to use the item
        // as `target` (with self as implicit), so the goal may carry
        // the item under target.* rather than item.*. Dedup must match
        // either shape.
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));

        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Letter From Home" },
        };
        Assert.True(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_IgnoresNonUseGoals()
    {
        // A re-USE block on Pickup/Wield/Talk/Attack/Give would be
        // wrong; the dedup only fires for Use.
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));

        var pickup = new Goal
        {
            Kind = GoalKind.Pickup,
            Target = new Selector { Name = "Letter From Home" },
        };
        var wield = pickup with { Kind = GoalKind.Wield };
        var talk = pickup with { Kind = GoalKind.Talk };
        var attack = pickup with { Kind = GoalKind.Attack };
        var give = new Goal
        {
            Kind = GoalKind.Give,
            Target = new Selector { Name = "Jonathan" },
            Item = new Selector { Name = "Letter From Home" },
        };

        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(pickup, es));
        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(wield, es));
        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(talk, es));
        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(attack, es));
        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(give, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_DifferentItem_DoesNotMatch()
    {
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));

        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Calling Stone" }, // unrelated item
        };
        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_EmptyEvents_DoesNotMatch()
    {
        var es = new EventStream();
        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Letter From Home" },
        };
        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_OldUse_SurvivesMixedKindNoise()
    {
        // Regression for spike bot_invdedup01 (2026-05-30): the old
        // implementation used Recent(30) (mixed-kind), so 30+
        // intervening ServerMessage / LandblockChanged / NpcDialog
        // events between two Use{Letter From Home} attempts evicted
        // the InvUsed marker from the lookback window and the
        // second Use went through. Live spike captured two
        // successful Use{Letter From Home} dispatches with seven
        // LLM kickoffs (~25 strategy events) between them. The fix
        // uses RecentOfKind(InventoryItemUsed, 16) which is immune
        // to noise from high-volume kinds.
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));
        for (int i = 0; i < 35; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ServerMessage, Text = $"filler {i}",
            });
        }
        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Letter From Home" },
        };
        Assert.True(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_EvictedByLaterUseEvents_DoesNotMatch()
    {
        // The per-kind window IS bounded: 16 distinct InventoryItemUsed
        // events after the original push it out. This is the
        // intended behavior for consumables (potions, scrolls) —
        // after 16 USE dispatches against other items, the bot may
        // re-USE a consumable. Non-consumables (notes, letters) are
        // typically USE'd once total per character so this never
        // matters for them.
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));
        for (int i = 0; i < 20; i++)
        {
            es.Append(InvUsed($"Other Item {i}", 9000u + (uint)i, 0x80001000u + (uint)i));
        }
        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Letter From Home" },
        };
        Assert.False(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void InventoryItemUsed_IsNotPlanInvalidating()
    {
        // Self-emitted echo must not invalidate in-flight LLM calls.
        Assert.False(LlmGoalPolicy.IsPlanInvalidatingKind(EventKind.InventoryItemUsed));
    }

    [Fact]
    public void InventoryItemUsed_IsNotSalient()
    {
        // Self-emitted echo must not wake the LLM (would defeat the
        // dedup it exists to power).
        Assert.False(LlmGoalPolicy.IsSalientKind(EventKind.InventoryItemUsed));
    }

    [Fact]
    public void IsSalientKind_CoversExpectedSalientKinds()
    {
        // Mirror IsPlanInvalidatingKind_TrueForInvalidatingKinds —
        // pin the salient set against accidental shrinkage that
        // would break LLM deliberation triggering.
        var salient = new[]
        {
            EventKind.PopupString,
            EventKind.InventoryItemAdded,
            EventKind.LandblockChanged,
            EventKind.GoalCompleted,
            EventKind.GoalFailed,
            EventKind.GoalExpired,
            EventKind.NpcDialog,
            EventKind.ServerMessage,
            EventKind.ActionRejected,
            EventKind.BookText,
            EventKind.PickerActivityStarted,
            EventKind.PickerArrivedNoAction,
        };
        foreach (var kind in salient)
        {
            Assert.True(LlmGoalPolicy.IsSalientKind(kind),
                $"{kind} should be classified as salient.");
        }
    }

    [Fact]
    public void IsSalientKind_ExcludesNonSalientKinds()
    {
        // PickerActivityCompleted is bookkeeping (only Started churns
        // deliberation). InventoryItemUsed is a self-emitted echo.
        // InventoryItemRemoved is plan-invalidating but not by itself
        // a wakeup trigger (covered by ActionRejected / GoalFailed).
        var notSalient = new[]
        {
            EventKind.Unknown,
            EventKind.InventoryItemRemoved,
            EventKind.GoalEmitted,
            EventKind.HealthChanged,
            EventKind.PickerActivityCompleted,
            EventKind.InventoryItemUsed,
        };
        foreach (var kind in notSalient)
        {
            Assert.False(LlmGoalPolicy.IsSalientKind(kind),
                $"{kind} should NOT be classified as salient.");
        }
    }

    [Fact]
    public void BuildUserPrompt_RendersRecentInventoryUsesWithCountAndStillHeldMarker()
    {
        // Letter still in inventory → "still in inventory (not consumed)".
        var world = BuildExitTokenWorld() with
        {
            Inventory = new[]
            {
                new InventoryItemProjection
                {
                    Guid = LetterGuid, Name = "Letter From Home", Wcid = LetterWcid,
                    ItemType = 0x100u, ShortDesc = "A letter from home — double-click to read.",
                },
            },
        };
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);

        Assert.Contains("## Recently used inventory items", prompt);
        Assert.Contains("Letter From Home", prompt);
        Assert.Contains("used x3 recently", prompt);
        Assert.Contains("still in inventory (not consumed)", prompt);
        Assert.Contains("policy will drop", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RendersConsumedMarkerWhenItemGone()
    {
        // World inventory is empty (item was consumed after the
        // recorded uses) → "no longer in inventory" so the LLM knows
        // it's safe to retry-if-needed (e.g. consume another potion).
        var world = BuildExitTokenWorld() with
        {
            Inventory = Array.Empty<InventoryItemProjection>(),
        };
        var es = new EventStream();
        es.Append(InvUsed("Healing Potion", 9999u, 0x80000099u));

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);

        Assert.Contains("Healing Potion", prompt);
        Assert.Contains("no longer in inventory", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsRecencySection_WhenNoInventoryUses()
    {
        // The RULES bullet text mentions "## Recently used inventory
        // items" by name; assert on the rendered list's NOTE block
        // (only present when the section actually renders) and the
        // per-line "used x" count marker (never in RULES).
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);
        Assert.DoesNotContain("policy will drop", prompt);
        Assert.DoesNotContain("used x", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesInventoryUseLoopBreakRule()
    {
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);
        Assert.Contains("LOOP-BREAK (inventory-USE loop)", prompt);
    }

    // ---- Sticky LLM-objective (call-volume reduction) ----
    //
    // When the tactical goal clears (currentGoal == null) and the
    // world has not changed externally (only Goal* lifecycle churn),
    // the policy re-drives the last LLM objective WITHOUT another LLM
    // round-trip. A real EXTERNAL salient event (NpcDialog,
    // InventoryItemAdded, ActionRejected, ...) suppresses the re-emit
    // so the LLM decides fresh. A retry budget bounds spin on an
    // unreachable target.

    // Builds a policy whose LLM always returns the same Give(Jonathan,
    // Token) goal, tracking the number of HTTP calls made (== LLM
    // deliberations). MinCallInterval=0 and a large StuckTimeout so the
    // sticky gate is the only thing suppressing calls.
    private static (LlmGoalPolicy Policy, Func<int> HttpCalls) MakeStickyPolicy(int maxStickyReEmits = 3)
    {
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Jonathan" },
          "item":   { "name": "Academy Exit Token" },
          "priority": 8,
          "rationale": "directed pursuit"
        }
        """;
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var count = 0;
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref count);
            _ = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
            MaxStickyReEmits = maxStickyReEmits,
        };
        return (policy, () => count);
    }

    // Drives one establishment call to completion and returns the
    // policy with a remembered LLM objective.
    private static async Task<Goal> EstablishLlmGoalAsync(LlmGoalPolicy policy, WorldStateProjection world, EventStream events)
    {
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var goal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(goal);
        return goal!;
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_ReEmitsLastGoal_OnNullWithNoExternalSalient()
    {
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        var firstGoal = await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // The tactical goal cleared on its own (lifecycle churn only).
        // GoalCompleted is salient but is NOT an external signal, so it
        // must NOT suppress the sticky re-emit.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalCompleted, Text = "goal cleared",
        });

        var reEmitted = policy.ProposeGoal(world, events, null);

        Assert.NotNull(reEmitted);
        Assert.Equal(GoalKind.Give, reEmitted!.Kind);
        Assert.Equal("Jonathan", reEmitted.Target?.Name);
        // No new LLM call — the objective was re-driven for free.
        Assert.Equal(1, httpCalls());
        // Fresh instance (new Id) so the Motor re-pursues rather than
        // treating it as the already-completed goal.
        Assert.NotEqual(firstGoal.Id, reEmitted.Id);
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_ExternalSalientEvent_SuppressesReEmit_AndCallsLlm()
    {
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // A genuinely completed Talk/Give emits NpcDialog — an EXTERNAL
        // salient event. The sticky gate must defer to a fresh LLM call.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Text = "Jonathan: well done",
        });

        var next = policy.ProposeGoal(world, events, null);
        // A new LLM call WAS kicked off (returns the passed currentGoal,
        // i.e. null, while the call is in flight).
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_RetryBudgetExhaustion_FallsThroughToLlm()
    {
        var (policy, httpCalls) = MakeStickyPolicy(maxStickyReEmits: 2);
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // Re-clear the goal repeatedly with no external event. The first
        // two null-calls re-emit (budget 2); the third exhausts the
        // budget and forces a fresh LLM call.
        var r1 = policy.ProposeGoal(world, events, null);
        Assert.NotNull(r1);
        Assert.Equal(1, httpCalls()); // re-emit #1, no call

        var r2 = policy.ProposeGoal(world, events, null);
        Assert.NotNull(r2);
        Assert.Equal(1, httpCalls()); // re-emit #2, no call

        var r3 = policy.ProposeGoal(world, events, null);
        Assert.Null(r3);              // kickoff returns passed currentGoal (null)
        Assert.Equal(2, httpCalls()); // budget exhausted → LLM called
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_ActionRejected_SuppressesReEmit_AndCallsLlm()
    {
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // A semantic ActionRejected is an external salient event → the
        // bot must re-deliberate, not blindly re-pursue the same target.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant",
            Text = "Jonathan",
        });

        var next = policy.ProposeGoal(world, events, null);
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_InventoryItemRemoved_SuppressesReEmit_AndCallsLlm()
    {
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // A completed Give removes the item from inventory and may emit
        // NO NpcDialog. InventoryItemRemoved must count as an external
        // change so the bot re-deliberates rather than re-driving a Give
        // whose item is already gone.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.InventoryItemRemoved, Text = "Academy Exit Token",
        });

        var next = policy.ProposeGoal(world, events, null);
        Assert.Equal(2, httpCalls());
    }

    // Drives a busy-path picker-start wake for the given target so the
    // policy's _lastPickerStartWakeKey is set to it. After this returns, a
    // same-target picker-start while aimless is FLUTTER (pickerStartWake ==
    // false) within the PickerStartCoalesce window. Costs one LLM call.
    private static async Task PrimePickerWakeAsync(
        LlmGoalPolicy policy, WorldStateProjection world, EventStream events,
        Goal currentGoal, uint guid, string name)
    {
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = guid, Name = name, Text = "in-range",
        });
        policy.ProposeGoal(world, events, currentGoal); // busy-path wake → records the key
        await policy.WaitForInFlightAsync();
        policy.ProposeGoal(world, events, null);        // consume the result
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_NewPickerTarget_WhileAimless_CallsLlm()
    {
        // reduce-aimless-establishment-churn (rubber-duck/gemini blocking
        // finding): a genuinely NEW picker target while aimless must NOT be
        // swallowed by a free sticky re-emit — the LLM has to get a chance
        // to weigh the discovery. pickerStartWake is true for a new target,
        // which breaks the sticky gate and forces a fresh call. (Mirrors the
        // current-goal path, where a new picker target also wakes.)
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // Brand-new target (never woke the LLM before).
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });

        var next = policy.ProposeGoal(world, events, null);
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_SameTargetFlutter_WhileAimless_ReEmitsFree()
    {
        // reduce-aimless-establishment-churn core saving: a picker-start for
        // the SAME target that last woke the LLM, within the coalesce window
        // (pickerStartWake == false → flutter), is ignored while aimless and
        // the unfinished objective is re-driven for FREE.
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        var goal = await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // Prime target A as the last picker-start that woke the LLM.
        await PrimePickerWakeAsync(policy, world, events, goal, 0xAAAA0001u, "A");
        Assert.Equal(2, httpCalls());

        // Same target A re-fires while aimless → flutter → free re-emit.
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });

        var reEmitted = policy.ProposeGoal(world, events, null);
        Assert.NotNull(reEmitted);
        Assert.Equal(GoalKind.Give, reEmitted!.Kind);
        Assert.Equal("Jonathan", reEmitted.Target?.Name);
        Assert.Equal(2, httpCalls()); // no fresh call
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_PickerArrived_WhileAimless_CallsLlm()
    {
        // reduce-aimless-establishment-churn safety valve: a picker ARRIVAL
        // (parked next to a target with no opcode sent) is where naming a
        // verb matters, so it MUST break the sticky gate and defer to a
        // fresh LLM call — only same-target picker flutter is ignored.
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerArrivedNoAction,
            ItemGuid = 0xBBBB0001u, Name = "B", Text = "parked",
        });

        var next = policy.ProposeGoal(world, events, null);
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_Flutter_AdvancesFloor_NoLaterCall()
    {
        // reduce-aimless-establishment-churn regression guard (rubber-duck
        // blocking finding): a flutter sticky re-emit MUST advance the event
        // floor past the consumed picker-start. Otherwise the re-emitted goal
        // makes currentGoal non-null next tick and the lingering picker-start
        // would be re-evaluated and could start a real call.
        var (policy, httpCalls) = MakeStickyPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        var goal = await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        await PrimePickerWakeAsync(policy, world, events, goal, 0xAAAA0001u, "A");
        Assert.Equal(2, httpCalls());

        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });

        // Aimless flutter tick: free re-emit, floor advanced.
        var reEmitted = policy.ProposeGoal(world, events, null);
        Assert.NotNull(reEmitted);
        Assert.Equal(2, httpCalls());

        // Next tick: re-emitted goal current, no NEW event. The consumed
        // flutter picker-start must not wake a fresh call.
        var held = policy.ProposeGoal(world, events, reEmitted);
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_Flutter_BudgetStillBounds()
    {
        // reduce-aimless-establishment-churn: ignoring same-target flutter
        // does not remove the MaxStickyReEmits bound. After the budget is
        // spent the bot still falls through to a fresh LLM call, so an
        // unreachable objective cannot spin forever on free re-emits.
        var (policy, httpCalls) = MakeStickyPolicy(maxStickyReEmits: 2);
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        var goal = await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        await PrimePickerWakeAsync(policy, world, events, goal, 0xAAAA0001u, "A");
        Assert.Equal(2, httpCalls());

        for (var i = 0; i < 2; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = 0, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.PickerActivityStarted,
                ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
            });
            var r = policy.ProposeGoal(world, events, null);
            Assert.NotNull(r);
            Assert.Equal(2, httpCalls()); // free re-emit
        }

        // Budget exhausted: the next flutter tick forces a fresh LLM call.
        events.Append(new StreamEvent
        {
            Sequence = 0, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0xAAAA0001u, Name = "A", Text = "in-range",
        });
        var r3 = policy.ProposeGoal(world, events, null);
        Assert.Null(r3);
        Assert.Equal(3, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_ClearedAfterFailedDeliberation()
    {
        // First call succeeds (establishes the sticky objective); every
        // subsequent call returns HTTP 500 (failed deliberation). After
        // a failed fresh call, _lastLlmGoal must be cleared so a later
        // no-event tick does NOT re-drive the stale objective.
        var goalJson = """
        {
          "goal_id": "11111111-2222-3333-4444-555555555555",
          "kind": "Give",
          "target": { "name": "Jonathan" },
          "item":   { "name": "Academy Exit Token" },
          "priority": 8,
          "rationale": "directed pursuit"
        }
        """;
        var cannedOk = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = goalJson } } },
        });
        var count = 0;
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var n = Interlocked.Increment(ref count);
            _ = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            return n == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cannedOk) }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, count); // objective established

        // External event triggers a fresh (2nd) deliberation that fails.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Text = "Jonathan: ...",
        });
        policy.ProposeGoal(world, events, null); // kicks off call 2
        Assert.Equal(2, count);
        await policy.WaitForInFlightAsync();
        policy.ProposeGoal(world, events, null); // consume the failure → _lastLlmGoal cleared
        Assert.Equal(2, count); // consuming did not kick off a new call

        // A clean no-external-event tick: sticky must NOT fire (objective
        // was cleared by the failed deliberation) → a real LLM call.
        policy.ProposeGoal(world, events, null);
        Assert.Equal(3, count);
    }
}
