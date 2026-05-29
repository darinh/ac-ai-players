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

    private sealed class ToggleablePolicy : IGoalPolicy
    {
        public bool InflightFlag;
        public string Source => "test:toggle";
        public bool HasInflight => InflightFlag;
        public Goal? ProposeGoal(WorldStateProjection world, EventStream events, Goal? currentGoal)
            => currentGoal;
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
