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
    public void LlmGoalPolicy_FallsBackToInnerOnHttpError()
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var fallback = new NoQuestKnowledgePolicy();
        var policy = new LlmGoalPolicy(llm, fallback, new InMemoryWeenieRepo());

        var world = BuildHostileWorld();
        var goal = policy.ProposeGoal(world, new EventStream(), null);

        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Attack, goal!.Kind); // fallback fired
        Assert.StartsWith("fallback:", goal.Source);
    }

    [Fact]
    public void LlmGoalPolicy_UsesLlmResultWhenContentIsValidGoal()
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

        var goal = policy.ProposeGoal(BuildExitTokenWorld(), new EventStream(), null);

        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Give, goal!.Kind);
        Assert.Equal("Jonathan", goal.Target.Name);
        Assert.Equal("Academy Exit Token", goal.Item?.Name);
        Assert.StartsWith("llm:", goal.Source);
    }

    [Fact]
    public void LlmGoalPolicy_FallsBackOnGarbageContent()
    {
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "this is not json" } } },
        });
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo());

        var goal = policy.ProposeGoal(BuildHostileWorld(), new EventStream(), null);
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request, cancellationToken));
    }

    private sealed class InMemoryWeenieRepo : IWeenieRepository
    {
        public WeenieStringRecord? TryGet(uint wcid) => null;
        public Task EnsureLoadedAsync(uint wcid, CancellationToken ct = default) => Task.CompletedTask;
    }
}
