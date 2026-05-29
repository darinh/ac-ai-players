// SPDX-License-Identifier: AGPL-3.0-or-later
// LlmGoalPolicy / LlmGoalClient tests.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    public void TryParseGoal_RejectsMalformedGuid()
    {
        // A clearly-malformed guid (too short / non-hex) should still
        // be rejected — we widened tolerance, not removed it.
        var json = """
        {
          "goal_id": "not-a-guid-at-all",
          "kind": "Talk",
          "target": { "name": "Greeter" },
          "rationale": "x",
          "priority": 3
        }
        """;
        Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out var err));
        Assert.Contains("Guid", err, System.StringComparison.OrdinalIgnoreCase);
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
    public async Task LlmGoalPolicy_ServerHints_OrderingNewestFirst()
    {
        // The hints section is newest-first. Append two distinct
        // hints in order then assert the second one appears earlier
        // (smaller offset) than the first within the section.
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
        Assert.True(secondHintAt < firstHintAt, "newer hint should appear earlier (newest-first)");
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
        // Seed: LandblockChanged 8 minutes ago.
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(8),
            Kind = EventKind.LandblockChanged,
            LandblockFrom = 0x8602u,
            LandblockTo = 0xA9B4u,
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
}
