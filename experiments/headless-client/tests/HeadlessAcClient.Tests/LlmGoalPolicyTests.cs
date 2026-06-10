// SPDX-License-Identifier: AGPL-3.0-or-later
// LlmGoalPolicy / LlmGoalClient tests.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using HeadlessAcClient.World;
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
    public void TryParseGoal_ParsesJsonWrappedInLabeledCodeFence()
    {
        // Some chat models (observed: deepseek-v3) wrap their goal JSON in a
        // Markdown ```json fence despite the prompt asking for raw JSON.
        var json = "```json\n" + """
        {
          "kind": "Attack",
          "target": { "name": "Drudge Slinker" },
          "rationale": "winnable",
          "priority": 7
        }
        """ + "\n```";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Attack, g!.Kind);
        Assert.Equal("Drudge Slinker", g.Target.Name);
    }

    [Fact]
    public void TryParseGoal_ParsesJsonWrappedInBareCodeFence()
    {
        var json = "```\n" + """
        {
          "kind": "Explore",
          "target": { "name": "anywhere" },
          "rationale": "scout",
          "priority": 4
        }
        """ + "\n```";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Explore, g!.Kind);
    }

    [Fact]
    public void TryParseGoal_ParsesSingleLineFencedJson()
    {
        var json = """```{"kind":"Recall","rationale":"escape","priority":9}```""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Recall, g!.Kind);
    }

    [Fact]
    public void StripJsonCodeFence_LeavesRawJsonUnchanged()
    {
        var raw = """{"kind":"Recall","rationale":"x","priority":3}""";
        Assert.Equal(raw, LlmGoalPolicy.StripJsonCodeFence(raw));
    }

    [Fact]
    public void StripJsonCodeFence_IsIdempotent()
    {
        var fenced = "```json\n{\"kind\":\"Recall\"}\n```";
        var once = LlmGoalPolicy.StripJsonCodeFence(fenced);
        var twice = LlmGoalPolicy.StripJsonCodeFence(once);
        Assert.Equal(once, twice);
        Assert.Equal("{\"kind\":\"Recall\"}", once);
    }

    [Fact]
    public void StripJsonCodeFence_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, LlmGoalPolicy.StripJsonCodeFence(null));
        Assert.Equal(string.Empty, LlmGoalPolicy.StripJsonCodeFence(""));
    }

    [Fact]
    public void StripJsonCodeFence_DoesNotEatBraceWhenTagLineCarriesJson()
    {
        // A fence whose first line already carries the object (no language tag)
        // must keep the braces intact.
        var fenced = "```{\"kind\":\"Recall\"}\n```";
        Assert.Equal("{\"kind\":\"Recall\"}", LlmGoalPolicy.StripJsonCodeFence(fenced));
    }

    [Fact]
    public void TryParseGoal_RecallParsesWithoutTarget()
    {
        // Recall is a self-action with no world target; it must parse even
        // though every other verb requires a non-empty target selector.
        var json = """{"kind":"Recall","rationale":"stuck on a ledge, nothing frees me","priority":9}""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Recall, g!.Kind);
        Assert.True(g.Target.IsEmpty);
    }

    [Fact]
    public void TryParseGoal_ExploreWithDirection_ParsesHeading()
    {
        // An Explore goal may carry an optional 8-way compass `direction` that
        // steers the outdoor frontier excursion.
        var json = """{"kind":"Explore","target":{"name":"anywhere"},"direction":"southeast","rationale":"barren north, try SE","priority":3}""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Explore, g!.Kind);
        Assert.Equal("southeast", g.Direction);
    }

    [Fact]
    public void TryParseGoal_ExploreWithoutDirection_DirectionNull()
    {
        // Direction is optional — a plain Explore leaves it null (undirected).
        var json = """{"kind":"Explore","target":{"name":"anywhere"},"rationale":"x","priority":3}""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Null(g!.Direction);
    }

    [Fact]
    public void TryParseGoal_NonRecallVerbStillRequiresTarget()
    {
        // The Recall exception must NOT relax target validation for other
        // verbs: an Attack with no target is still rejected.
        var json = """{"kind":"Attack","rationale":"x","priority":3}""";
        Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out var err));
        Assert.Contains("target", err);
    }

    [Fact]
    public void TryParseGoal_WieldItemOnly_ParsesWithoutTarget()
    {
        // The LLM legitimately emits a Wield with the weapon in `item` and a
        // null/absent target (the prompt schema never directs target=self for
        // Wield). The Motor's Wield dispatch reads goal.Item, so this must
        // parse instead of being discarded to the heuristic fallback.
        var json = """{"kind":"Wield","target":null,"item":{"name":"Acid Ken"},"rationale":"arm up","priority":10}""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Wield, g!.Kind);
        Assert.Equal("Acid Ken", g.Item!.Name);
        // Target is normalized to a non-null empty selector so downstream
        // `goal.Target.*` consumers never dereference null.
        Assert.NotNull(g.Target);
        Assert.True(g.Target.IsEmpty);
    }

    [Fact]
    public void TryParseGoal_WieldItemOnly_OmittedTarget_Parses()
    {
        // Same as above but the target field is omitted entirely (not explicit
        // null) — the canonical model output shape.
        var json = """{"kind":"Wield","item":{"name":"Shortbow"},"rationale":"arm up","priority":9}""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Wield, g!.Kind);
        Assert.Equal("Shortbow", g.Item!.Name);
    }

    [Fact]
    public void TryParseGoal_WieldWithEmptyTargetAndNoItem_Rejected()
    {
        // A Wield carrying neither a target nor an item has nothing to wield —
        // still rejected.
        var json = """{"kind":"Wield","target":{},"item":null,"rationale":"x","priority":5}""";
        Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out var err));
        Assert.Contains("target", err);
    }

    [Fact]
    public void TryParseGoal_WieldWithTargetOnly_StillParses()
    {
        // The canonical fallback shape (target=self/weapon, item null) must keep
        // working — the relaxation only ADDS the item-only path.
        var json = """{"kind":"Wield","target":{"name":"self"},"item":null,"rationale":"x","priority":6}""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.Wield, g!.Kind);
    }

    [Fact]
    public void TryParseGoal_NonWieldItemOnly_StillRejected()
    {
        // The item-only relaxation is Wield-scoped: a Use/Attack/Talk/Pickup
        // with an item but no target is still rejected (their primary object is
        // the target, not the item).
        foreach (var kind in new[] { "Use", "Attack", "Talk", "Pickup" })
        {
            var json = $$"""{"kind":"{{kind}}","target":null,"item":{"name":"Acid Ken"},"rationale":"x","priority":4}""";
            Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out var err), $"{kind} item-only should reject");
            Assert.Contains("target", err);
        }
    }

    [Fact]
    public void BuildUserPrompt_Schema_AdvertisesRecallVerb()
    {
        // The Recall verb must appear in the kind enum so the LLM may emit it —
        // always present, independent of the cp-2408-gated STUCK ESCAPE rule.
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildImmobileWorld(0), new EventStream(), null);
        Assert.Contains("\"Recall\"", prompt);
    }

    [Fact]
    public void BuildUserPrompt_StuckEscapeRule_RendersOnBlockedRejection_OmittedOtherwise()
    {
        // cp-2408: the STUCK ESCAPE (Recall last-resort) rule is paired with the
        // BLOCKED rule and gated on a recent Blocked/Unreachable geometry
        // rejection — the only situation in which Recall-to-escape is actionable.
        var blocked = new EventStream();
        blocked.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorLabel = "Blocked" });
        var stuck = LlmGoalPolicy.BuildUserPrompt(BuildImmobileWorld(0), blocked, null);
        Assert.Contains("STUCK ESCAPE", stuck);
        Assert.Contains("Recall{}", stuck);

        // Moving freely (no geometry rejection) -> the rule is gated off.
        Assert.DoesNotContain("STUCK ESCAPE",
            LlmGoalPolicy.BuildUserPrompt(BuildImmobileWorld(0), new EventStream(), null));
    }

    [Fact]
    public void CountUntalkedNpcsInView_CountsCivilianNpcNotInTalkedSet()
    {
        var world = BuildVisibleWorld(CivilianNpc(0x90000001u));

        var count = LlmGoalPolicy.CountUntalkedNpcsInView(world, new HashSet<uint>());

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountUntalkedNpcsInView_SkipsTalkedGuid()
    {
        var world = BuildVisibleWorld(CivilianNpc(0x90000001u));

        var count = LlmGoalPolicy.CountUntalkedNpcsInView(
            world, new HashSet<uint> { 0x90000001u });

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountUntalkedNpcsInView_SkipsMonsterCorpseAndHostile()
    {
        var world = BuildVisibleWorld(
            CivilianNpc(0x90000001u) with { IsMonster = true },
            CivilianNpc(0x90000002u) with { IsCorpse = true },
            CivilianNpc(0x90000003u) with { ObservedHostile = true });

        var count = LlmGoalPolicy.CountUntalkedNpcsInView(world, new HashSet<uint>());

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountUntalkedNpcsInView_EmptyWorldZero()
    {
        var count = LlmGoalPolicy.CountUntalkedNpcsInView(
            BuildVisibleWorld(), new HashSet<uint>());

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountUntalkedNpcsInView_SkipsTalkedName()
    {
        var world = BuildVisibleWorld(CivilianNpc(0x90000001u));

        // Name-keyed, case-insensitive match (the LLM may vary casing). The
        // CivilianNpc fixture names itself "Npc <GUID:X8>". This is the path
        // that matters in production: LLM Talk emissions are name-only.
        var count = LlmGoalPolicy.CountUntalkedNpcsInView(
            world,
            talkedNpcGuids: new HashSet<uint>(),
            talkedNpcNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "npc 90000001" });

        Assert.Equal(0, count);
    }

    [Fact]
    public void TryExtractTalkGoalTargetIdentity_NameOnlyEmission_CapturesNameNotGuid()
    {
        var ok = LlmGoalPolicy.TryExtractTalkGoalTargetIdentity(
            "Talk target=name=\"Npc One\" item=<empty> source=llm:test",
            out var guid, out var name);

        Assert.True(ok);
        Assert.Null(guid);
        Assert.Equal("Npc One", name);
    }

    [Fact]
    public void TryExtractTalkGoalTargetIdentity_GuidBearingEmission_CapturesBoth()
    {
        var ok = LlmGoalPolicy.TryExtractTalkGoalTargetIdentity(
            "Talk target=guid=0x90000001 name=\"Npc One\" item=<empty> source=fallback:x",
            out var guid, out var name);

        Assert.True(ok);
        Assert.Equal(0x90000001u, guid);
        Assert.Equal("Npc One", name);
    }

    [Fact]
    public void TryExtractTalkGoalTargetIdentity_NonTalkGoalReturnsFalse()
    {
        var ok = LlmGoalPolicy.TryExtractTalkGoalTargetIdentity(
            "Attack target=name=\"Npc One\" item=<empty> source=llm:test",
            out var guid, out var name);

        Assert.False(ok);
        Assert.Null(guid);
        Assert.Null(name);
    }

    [Fact]
    public void BuildUserPrompt_RendersUntalkedNpcCount()
    {
        var world = BuildVisibleWorld(CivilianNpc(0x90000001u), CivilianNpc(0x90000002u));

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), currentGoal: null, stack: null,
            pickerActivity: null, explorationCandidates: null,
            talkedNpcGuids: new HashSet<uint> { 0x90000001u });

        Assert.Contains("- untalked npcs in view: 1", prompt);
        Assert.Contains("Talk each once", prompt);
    }

    [Fact]
    public async Task LlmGoalPolicy_TalkedNpcPersistsAfterEmissionAgesOut()
    {
        // Production LLM Talk goals are NAME-ONLY (Selector.Guid is null and the
        // Motor's resolved guid is never written back into the emitted Goal), so
        // the session talked-set must persist by NAME. Use a name-only emission
        // whose name matches the visible NPC and assert it stays "talked" even
        // after the emission ages out of the bounded event ring.
        var requestBodies = new List<string>();
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{\"kind\":\"Explore\",\"target\":{\"name\":\"anywhere\"},\"rationale\":\"x\",\"priority\":3}" } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            requestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var policy = new LlmGoalPolicy(
            new LlmGoalClient(http, "https://test.example/chat", "test-model", "key"),
            new NoQuestKnowledgePolicy(),
            new InMemoryWeenieRepo())
        {
            MinCallInterval = TimeSpan.Zero,
        };
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalEmitted,
            Text = "Talk target=name=\"Npc 90000001\" item=<empty> source=llm:test",
        });

        var world = BuildVisibleWorld(CivilianNpc(0x90000001u));
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        var firstGoal = policy.ProposeGoal(world, events, null);
        Assert.NotNull(firstGoal);

        Assert.Single(requestBodies);
        Assert.Contains("- untalked npcs in view: 0", PromptFromRequest(requestBodies[0]));

        for (var i = 0; i < EventStream.DefaultCapacity + 20; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = -1,
                Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ServerMessage,
                Text = $"ambient event {i}",
            });
        }
        events.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorLabel = "Blocked",
            Text = "test",
        });

        _ = policy.ProposeGoal(world, events, firstGoal);
        await policy.WaitForInFlightAsync();

        Assert.Equal(2, requestBodies.Count);
        Assert.Contains("- untalked npcs in view: 0", PromptFromRequest(requestBodies[1]));
    }

    private static string PromptFromRequest(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
    }

    [Fact]
    public void TryParseGoal_ParsesRaiseAttributeWithAmount()
    {
        var json = """
        {
          "goal_id": "raise-001",
          "kind": "RaiseAttribute",
          "target": { "name": "endurance" },
          "amount": 12500,
          "priority": 6,
          "rationale": "80k unspent XP and only 3 max HP; invest in endurance."
        }
        """;
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.RaiseAttribute, g!.Kind);
        Assert.Equal("endurance", g.Target.Name);
        Assert.Equal(12500L, g.Amount);
    }

    [Fact]
    public void TryParseGoal_RaiseAttributeWithoutAmount_ParsesButAmountNull()
    {
        // A missing amount still parses (target is non-empty); the dispatch
        // layer rejects it (no source default) — proven in AttributeRaiseTests.
        var json = """{"kind":"RaiseAttribute","target":{"name":"strength"},"rationale":"x","priority":5}""";
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.RaiseAttribute, g!.Kind);
        Assert.Null(g.Amount);
    }

    [Fact]
    public void TryParseGoal_RaiseAttributeFractionalAmount_Rejected()
    {
        // The amount field is an integer; a fractional value is dropped at
        // deserialization so a nonsensical fractional XP never dispatches.
        var json = """{"kind":"RaiseAttribute","target":{"name":"endurance"},"amount":3.5,"rationale":"x","priority":5}""";
        Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out _));
    }

    [Fact]
    public void TryParseGoal_ParsesRaiseVitalWithAmount()
    {
        var json = """
        {
          "goal_id": "raise-vital-001",
          "kind": "RaiseVital",
          "target": { "name": "health" },
          "amount": 8000,
          "priority": 6,
          "rationale": "Unspent XP and low max HP; invest directly in max health."
        }
        """;
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.RaiseVital, g!.Kind);
        Assert.Equal("health", g.Target.Name);
        Assert.Equal(8000L, g.Amount);
    }

    [Fact]
    public void TryParseGoal_ParsesRaiseSkillWithAmount()
    {
        var json = """
        {
          "goal_id": "raise-skill-001",
          "kind": "RaiseSkill",
          "target": { "name": "war magic" },
          "amount": 5000,
          "priority": 6,
          "rationale": "Unspent XP; invest in my trained war magic skill."
        }
        """;
        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var g, out var err), err);
        Assert.Equal(GoalKind.RaiseSkill, g!.Kind);
        Assert.Equal("war magic", g.Target.Name);
        Assert.Equal(5000L, g.Amount);
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
    public void LlmGoalPolicy_Prompt_SpendXpRuleCarriesFullAttributeMechanics()
    {
        // Regression guard for the spend-xp-attribute-balance slice. Live
        // evidence (a level-9 bot at strength 10 / endurance 49 / everything
        // else 10) showed the LLM pouring ALL its XP into endurance — the only
        // attribute the old SPEND XP rule explained or exemplified — leaving it
        // tanky but unable to land melee hits, so it lost fights of attrition
        // and died. The fix surfaces the FULL attribute->effect mechanics as
        // FACTS so the LLM can balance, and drops the single endurance-anchoring
        // worked example. These assertions lock that in.
        // unspent XP > 0 so the (now unspent-gated) SPEND XP rule renders; this
        // test guards the rule's CONTENT, not its render condition.
        var world = BuildXpWorld(69296, 5475);
        var events = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null);

        // The SPEND XP rule is still present with all three raise verbs.
        Assert.Contains("SPEND XP", prompt);
        Assert.Contains("RaiseAttribute", prompt);
        Assert.Contains("RaiseVital", prompt);
        Assert.Contains("RaiseSkill", prompt);

        // Offensive + utility mechanics are now stated as facts, not just the
        // defensive endurance->MAX HEALTH mechanic.
        Assert.Contains("strength and coordination drive MELEE offense", prompt);
        Assert.Contains("focus and self power magic", prompt);
        Assert.Contains("quickness aids defense and missile play", prompt);
        Assert.Contains("endurance and health raise MAX HEALTH", prompt);

        // Anti-tunnel-vision + adaptive-allocation guidance (no fixed build).
        Assert.Contains("there is NO fixed build", prompt);
        Assert.Contains("Do NOT pour every point into ONE attribute", prompt);

        // The single endurance-anchoring worked example is gone: the
        // RaiseAttribute verb now uses a neutral <attribute> placeholder.
        Assert.Contains("RaiseAttribute{target: {name: \"<attribute>\"}", prompt);
        Assert.DoesNotContain("RaiseAttribute{target: {name: \"endurance\"}", prompt);
    }

    [Fact]
    public void LlmGoalPolicy_Prompt_SchemaDeclaresExploreDirectionField()
    {
        // cp-2351 added the Explore `direction` parser + Motor wiring and a prose
        // STEER A BARREN EXCURSION rule, but never declared `direction` in the
        // output JSON schema — and the prompt says "no extra fields", so the LLM
        // (obeying the schema) could not emit it (live: 0 directional Explores
        // across runs despite the rule rendering, the bot oscillating near the
        // safe zone). Declaring it in the schema unblocks the field. Guard it.
        var world = BuildXpWorld(69296, 0);
        var events = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null);

        Assert.Contains("\"direction\":", prompt);   // declared field
        Assert.Contains("\"northwest\"", prompt);    // the 8-way compass enum
        Assert.Contains("Explore only", prompt);     // scoped to the Explore verb
        // Schema documents the full accepted set, matching TryHeadingVector
        // (which also accepts the n/ne/.../nw abbreviations).
        Assert.Contains("short forms n/ne/e/se/s/sw/w/nw also accepted", prompt);
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
        // The corpse-looting rule now renders only when a corpse is visible
        // (cp-2331-loot section-presence gating), so seed one.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x404u, Name = "Corpse of a Drudge", Wcid = 21u, Distance = 5f,
                  IsCorpse = true, IsMonster = false },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);

        Assert.Contains("Looting:", prompt);
        Assert.Contains("corpse", prompt);
        Assert.Contains("Use{target: name=\"<corpse>\"}", prompt);
        Assert.Contains("Pickup{target: name=\"<item>\"}", prompt);
    }

    [Fact]
    public void BuildUserPrompt_CorpseLootingRule_OmittedWhenNoCorpse()
    {
        // No corpse visible -> the corpse-looting rule carries no information
        // and is omitted. BuildExitTokenWorld has no corpse.
        var p = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("NEVER skip a fresh corpse", p);
    }

    [Fact]
    public void BuildUserPrompt_ChestLootingRule_PresentWhenChestVisible()
    {
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x501u, Name = "Chest", Wcid = 9001u, Distance = 3f,
                  IsChest = true, IsOpenable = true },
            },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("NEVER skip an unopened chest", p);
    }

    [Fact]
    public void BuildUserPrompt_ChestLootingRule_OmittedWhenNoChest()
    {
        // No chest visible -> omit the chest-looting rule. BuildExitTokenWorld
        // has no chest.
        var p = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("NEVER skip an unopened chest", p);
    }

    // ---- ## Monsters in view end-capsule (cp-2366) ------------------------
    // Mirrors the `## Unspent XP` / `## Recent Talk` end-of-prompt salience
    // capsules: re-surface the already-computed visible-monster perception in
    // the most decision-proximate slot, on RAW PRESENCE, as a not-a-
    // recommendation fact. The capsule must render iff a non-corpse monster is
    // visible.

    private static WorldStateProjection BuildWorldWithMonsters(
        params VisibleObjectProjection[] visible)
        => new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = visible,
        };

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_RendersWhenMonsterVisible()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x600u, Name = "Sparring Golem", Wcid = 70u, Distance = 6.5f,
              IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Monsters in view", p);
        Assert.Contains("Sparring Golem", p);
        Assert.Contains("d=6.5u", p);
        Assert.Contains("raw fact, not a recommendation", p);
    }

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_OmittedWhenNoMonster()
    {
        // BuildExitTokenWorld has no monster -> the capsule carries no
        // information and must be absent.
        var p = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("## Monsters in view", p);
    }

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_OmittedWhenOnlyCorpseVisible()
    {
        // A corpse is not an attackable monster (IsCorpse excluded) -> omit.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x601u, Name = "Corpse of a Golem", Wcid = 70u, Distance = 4f,
              IsCorpse = true, IsMonster = false });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("## Monsters in view", p);
    }

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_GroupsKindsWithCount()
    {
        // Two of the same kind collapse to "<Name> x2"; the count and the
        // nearest distance reflect the full set.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x602u, Name = "Drudge Skulker", Wcid = 71u, Distance = 9f,
              IsMonster = true },
            new VisibleObjectProjection
            { Guid = 0x603u, Name = "Drudge Skulker", Wcid = 71u, Distance = 3f,
              IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Monsters in view", p);
        Assert.Contains("Drudge Skulker x2", p);
        Assert.Contains("2 attackable monster(s)", p);
        Assert.Contains("d=3.0u", p);
    }

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_NullDistanceRendersGracefully()
    {
        // A visible monster with no computed distance must not produce a
        // malformed "d=u"; it falls back to an explicit unknown-distance phrase.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x604u, Name = "Mosswart", Wcid = 72u, Distance = null, IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Monsters in view", p);
        Assert.Contains("Mosswart", p);
        Assert.DoesNotContain("d=u", p);
        Assert.Contains("unknown distance", p);
    }

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_BlankNameRendersUnknown()
    {
        // A visible monster with a blank name normalizes to "(unknown)" in both
        // the grouped list and the nearest-target fragment (no empty quotes).
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x605u, Name = "  ", Wcid = 73u, Distance = 5f, IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Monsters in view", p);
        Assert.Contains("(unknown)", p);
        Assert.Contains("nearest '(unknown)'", p);
        Assert.DoesNotContain("nearest ''", p);
    }

    // ---- ## Monsters in view: per-kind LEARNED record (cp-2390) -----------
    // The per-kind combat-feel record lives in the body `## Combat readiness`
    // section, which is hard-cut under the request ceiling in dense combat
    // scenes (live academy: cut in 93% of combat prompts). Re-surface each
    // in-view kind's OWN record inline in the protected `## Monsters in view`
    // capsule so the bot's learning survives the cut.

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_IncludesPerKindCombatRecord()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x600u, Name = "Sparring Golem", Wcid = 70u, Distance = 6.5f, IsMonster = true })
            with
            {
                CombatHistory = new[]
                {
                    new CombatHistoryEntry("Sparring Golem", 70u, Kills: 0, Deaths: 0,
                        NearDeaths: 0, Fights: 5, LastOutcome: "ineffective", Ineffective: 5),
                },
            };
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        var capsule = p.Substring(p.IndexOf("## Monsters in view", StringComparison.Ordinal));
        Assert.Contains("Sparring Golem x1", capsule);
        Assert.Contains("[your record:", capsule);
        Assert.Contains("kills 0", capsule);
        Assert.Contains("ineffective 5", capsule);
    }

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_NoRecordRowWhenNoHistory()
    {
        // No combat history -> no `[your record]` row, just the summary line
        // (preserves the prior capsule behavior for a never-fought kind).
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x600u, Name = "Sparring Golem", Wcid = 70u, Distance = 6.5f, IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Monsters in view", p);
        var capsule = p.Substring(p.IndexOf("## Monsters in view", StringComparison.Ordinal));
        Assert.DoesNotContain("[your record:", capsule);
    }

    [Fact]
    public void BuildUserPrompt_MonstersInViewCapsule_RecordSurvivesDenseSceneCut()
    {
        // CORE PROPERTY: in a scene dense enough to overflow the ceiling (so the
        // body `## Combat readiness` — which normally carries the record — is
        // hard-cut), the per-kind record still reaches the model via the
        // protected capsule, and the prompt stays within the hard ceiling.
        var dense = System.Linq.Enumerable.Range(0, 200)
            .Select(i => new VisibleObjectProjection
            {
                Guid = (uint)(0x900u + i),
                Name = $"Dense Scene Object {i:D3} occupying prompt budget space here",
                Wcid = (uint)(1000 + i),
                Distance = i + 50f,
            })
            .Append(new VisibleObjectProjection
            { Guid = 0x600u, Name = "Sparring Golem", Wcid = 70u, Distance = 6.5f, IsMonster = true })
            .ToArray();
        var world = BuildWorldWithMonsters(dense) with
        {
            CombatHistory = new[]
            {
                new CombatHistoryEntry("Sparring Golem", 70u, Kills: 0, Deaths: 0,
                    NearDeaths: 0, Fights: 8, LastOutcome: "ineffective", Ineffective: 8),
            },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Monsters in view", p);
        var capsule = p.Substring(p.IndexOf("## Monsters in view", StringComparison.Ordinal));
        Assert.Contains("[your record:", capsule);
        Assert.Contains("ineffective 8", capsule);
        // Prove the dense scene actually overflowed (farthest object dropped).
        Assert.DoesNotContain("Dense Scene Object 199", p);
        Assert.True(p.Length <= 26000, $"prompt length {p.Length} must respect the hard ceiling");
    }

    // ---- ## Nearest objects protected capsule (cp-2367) -------------------
    // The mid-prompt `## Visible nearby` section is trimmable; in an object-
    // dense scene the global request-size fitter can strip ALL its rows. The
    // `## Nearest objects` capsule re-surfaces the nearest few objects in the
    // PROTECTED salience tail so the LLM is never left blind to its closest
    // surroundings. Object-type-neutral (nearest by distance), self-bounded.

    [Fact]
    public void BuildUserPrompt_NearestObjectsCapsule_RendersWhenObjectsVisible()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x700u, Name = "Holtburg Door", Wcid = 80u, Distance = 8f, IsDoor = true },
            new VisibleObjectProjection
            { Guid = 0x701u, Name = "Calling Stone", Wcid = 81u, Distance = 3f });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Nearest objects", p);
        Assert.Contains("Holtburg Door", p);
        Assert.Contains("Calling Stone", p);
    }

    [Fact]
    public void BuildUserPrompt_NearestObjectsCapsule_OmittedWhenNothingVisible()
    {
        // Empty visible set -> the capsule carries no information and is omitted.
        var world = BuildWorldWithMonsters();
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("## Nearest objects", p);
    }

    [Fact]
    public void BuildUserPrompt_NearestObjectsCapsule_OrdersNearestFirst()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection { Guid = 0x710u, Name = "FarThing", Distance = 40f },
            new VisibleObjectProjection { Guid = 0x711u, Name = "NearThing", Distance = 2f });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        var section = p.Substring(p.IndexOf("## Nearest objects", System.StringComparison.Ordinal));
        var nearIdx = section.IndexOf("NearThing", System.StringComparison.Ordinal);
        var farIdx = section.IndexOf("FarThing", System.StringComparison.Ordinal);
        Assert.True(nearIdx >= 0 && farIdx >= 0, "both objects should be listed");
        Assert.True(nearIdx < farIdx, "nearer object must render before the farther one");
    }

    [Fact]
    public void BuildUserPrompt_NearestObjectsCapsule_SelfBoundedUnderManyObjects()
    {
        // 80 visible objects with long names must not bloat the capsule: its
        // rendered rows are self-bounded by a small total char budget.
        var many = System.Linq.Enumerable.Range(0, 80)
            .Select(i => new VisibleObjectProjection
            {
                Guid = (uint)(0x800u + i),
                Name = $"Verbose Object Number {i:D2} With A Long Descriptive Name",
                Wcid = (uint)(900 + i),
                Distance = i + 1f,
            })
            .ToArray();
        var world = BuildWorldWithMonsters(many);
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Nearest objects", p);
        // The capsule body (from its header to the next "##" section or end)
        // stays small — well under 800 chars including the header line.
        var start = p.IndexOf("## Nearest objects", System.StringComparison.Ordinal);
        var rest = p.Substring(start + "## Nearest objects".Length);
        var nextHdr = rest.IndexOf("\n## ", System.StringComparison.Ordinal);
        var capsule = nextHdr >= 0 ? rest.Substring(0, nextHdr) : rest;
        Assert.True(capsule.Length < 800, $"capsule body {capsule.Length} should be self-bounded");
        // The nearest object (Distance=1) is always included.
        Assert.Contains("Verbose Object Number 00", p);
    }

    [Fact]
    public void BuildUserPrompt_NearestObjectsCapsule_SurvivesGlobalTrimInDenseScene()
    {
        // CORE PROPERTY: in a scene dense enough to overflow the request ceiling
        // (so `## Visible nearby` is trimmed by the global fitter), the protected
        // `## Nearest objects` capsule still renders, and the prompt stays within
        // the hard ceiling.
        var dense = System.Linq.Enumerable.Range(0, 200)
            .Select(i => new VisibleObjectProjection
            {
                Guid = (uint)(0x900u + i),
                Name = $"Dense Scene Object {i:D3} occupying prompt budget space here",
                Wcid = (uint)(1000 + i),
                Distance = i + 1f,
            })
            .ToArray();
        var world = BuildWorldWithMonsters(dense);
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Nearest objects", p);
        Assert.Contains("Dense Scene Object 000", p); // nearest survives in the capsule
        // Prove the dense scene actually overflowed and the farthest objects were
        // dropped from the prompt entirely (so `## Visible nearby` could NOT have
        // carried them) — yet the protected capsule still keeps the nearest.
        Assert.DoesNotContain("Dense Scene Object 199", p);
        Assert.True(p.Length <= 26000, $"prompt length {p.Length} must respect the hard ceiling");
    }

    // ---- ## Held items protected capsule (cp-2389) ------------------------
    // The body `## Inventory` section is trimmed first when the prompt
    // overflows the request ceiling (live: in a dense academy scene it was
    // "omitted to fit prompt budget", so the bot could not see a server-given
    // quest item — the Academy Exit Token it had to give back to leave). A
    // compact held-items list re-surfaces the bot's OWN inventory in the
    // protected salience tail so it always survives the hard-cut.

    [Fact]
    public void BuildUserPrompt_HeldItemsCapsule_RendersHeldInventoryWithShortDesc()
    {
        var world = BuildInventoryWorld(new[]
        {
            new InventoryItemProjection
            {
                Guid = 0x4001u, Name = "Quest Token", Wcid = 29335u,
                ShortDesc = "Give this token back to the gatekeeper to leave.",
            },
        });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Held items", p);
        Assert.Contains("- Quest Token — Give this token back to the gatekeeper to leave.", p);
        // It rides the protected tail: the capsule renders AFTER the body
        // `## Inventory` section, so even when that body section is cut the
        // held item still reaches the model.
        Assert.True(p.IndexOf("## Held items", StringComparison.Ordinal)
            > p.IndexOf("## Self", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUserPrompt_HeldItemsCapsule_OmittedWhenInventoryEmpty()
    {
        var world = BuildInventoryWorld(System.Array.Empty<InventoryItemProjection>());
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("## Held items", p);
    }

    [Fact]
    public void BuildUserPrompt_HeldItemsCapsule_DedupesIdenticalRows()
    {
        // Two items with the same name + short_desc collapse to one row so a
        // duplicate stack cannot consume the protected char budget.
        var world = BuildInventoryWorld(new[]
        {
            new InventoryItemProjection { Guid = 0x10u, Name = "Healing Kit", Wcid = 1u, ShortDesc = "Restores health." },
            new InventoryItemProjection { Guid = 0x11u, Name = "Healing Kit", Wcid = 1u, ShortDesc = "Restores health." },
        });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        var capsuleStart = p.IndexOf("## Held items", StringComparison.Ordinal);
        Assert.True(capsuleStart >= 0);
        var capsule = p.Substring(capsuleStart);
        var occurrences = capsule.Split(new[] { "- Healing Kit" }, StringSplitOptions.None).Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void BuildUserPrompt_HeldItemsCapsule_SurvivesGlobalTrimInDenseScene()
    {
        // CORE PROPERTY: in a scene dense enough to overflow the request
        // ceiling (so the body `## Inventory`/`## Visible nearby` is trimmed by
        // the global fitter), the protected `## Held items` capsule still
        // carries the held quest item, and the prompt stays within the hard
        // ceiling.
        var dense = System.Linq.Enumerable.Range(0, 200)
            .Select(i => new VisibleObjectProjection
            {
                Guid = (uint)(0x900u + i),
                Name = $"Dense Scene Object {i:D3} occupying prompt budget space here",
                Wcid = (uint)(1000 + i),
                Distance = i + 1f,
            })
            .ToArray();
        var world = BuildInventoryWorld(
            new[]
            {
                new InventoryItemProjection
                {
                    Guid = 0x4002u, Name = "Exit Token", Wcid = 29335u,
                    ShortDesc = "Carry this back to the gatekeeper to leave.",
                },
            },
            dense);
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Held items", p);
        // The held item is carried by the protected capsule (after its header).
        var capsuleStart = p.IndexOf("## Held items", StringComparison.Ordinal);
        Assert.Contains("Exit Token", p.Substring(capsuleStart));
        // Prove the dense scene actually overflowed (farthest object dropped).
        Assert.DoesNotContain("Dense Scene Object 199", p);
        Assert.True(p.Length <= 26000, $"prompt length {p.Length} must respect the hard ceiling");
    }

    // ---- ## Early server directives protected capsule (cp-2383) -----------
    // A one-time server PopupString (login/exit directive) is persisted past
    // the EventStream ring (cp-2382) AND re-surfaced in the protected salience
    // tail so it survives even when the `## Server hints` section is itself
    // hard-cut by the request-size fitter in a dense scene.

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectivesCapsule_RendersPersistedPopup()
    {
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PopupString,
            Text = "Return the Academy Token to the Training Master in the Practice Area.",
        });
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(), es, null);

        Assert.Contains("## Early server directives", p);
        Assert.Contains("Return the Academy Token to the Training Master", p);
    }

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectivesCapsule_SurfacesLatestDirectivePastEarliestCap()
    {
        // cp-2393: the capsule must ALSO surface the most-recent directive so a
        // LATE "you are done, now do X" instruction (which the earliest-capped
        // store never captured) still reaches the LLM. Fill the earliest NpcDialog
        // store (cap 8), then add a late completion directive, and assert it is
        // rendered under the "most recent directed text" block.
        var es = new EventStream();
        for (int i = 0; i < 8; i++)
            es.Append(new StreamEvent
            { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Greeter", Text = $"early greeting line {i:D2}" });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Master",
            Text = "Excellent work! You have completed your training. You may now take the portal.",
        });
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(), es, null);

        Assert.Contains("## Early server directives", p);
        Assert.Contains("most recent directed text", p);
        Assert.Contains("You may now take the portal", p);
    }

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectivesCapsule_RemindsToPursueNamedTarget()
    {
        // cp-2387: when a directive is present the capsule re-surfaces the
        // pursue-the-named-target reminder (decision-proximate), gated on the
        // capsule rendering (a directive exists).
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PopupString,
            Text = "If you wish to skip this tutorial, go talk to the guide in the next room.",
        });
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(), es, null);

        var marker = "directed text the server/NPCs sent you earlier this session";
        var start = p.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(start >= 0, "the capsule must render");
        var rest = p.Substring(start);
        var nextHdr = rest.IndexOf("\n## ", System.StringComparison.Ordinal);
        var capsule = nextHdr >= 0 ? rest.Substring(0, nextHdr) : rest;
        // The reminder points at the existing rules and tells the LLM to NAME
        // the target rather than Explore generically.
        Assert.Contains("pursue it by NAMING that exact target", capsule);
        Assert.Contains("INSTEAD of Exploring", capsule);
    }

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectives_PursueReminder_OmittedWhenNoDirective()
    {
        // No persisted directive -> capsule absent -> no pursue reminder.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = "ambient" });
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(), es, null);
        Assert.DoesNotContain("pursue it by NAMING that exact target", p);
    }

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectivesCapsule_OmittedWhenNoPopups()
    {
        // No PopupString ever seen -> nothing to persist -> capsule omitted.
        // (The phrase "## Early server directives" also appears in the rules
        // prose, so assert on the capsule's unique body line instead.)
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = "ambient" });
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(), es, null);
        Assert.DoesNotContain("directed text the server/NPCs sent you earlier this session", p);
    }

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectivesCapsule_RendersPersistedNpcDirective()
    {
        // cp-2385: NPC-spoken directives (NpcDialog) are persisted past the ring
        // and re-surfaced in the same protected capsule, attributed to the NPC.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Name = "Society Greeter",
            Text = "Go talk to the trainer in the next room to continue.",
        });
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(), es, null);

        Assert.Contains("## Early server directives", p);
        Assert.Contains("from \"Society Greeter\"", p);
        Assert.Contains("Go talk to the trainer in the next room", p);
    }

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectivesCapsule_BoundedToEarliestN()
    {
        // More distinct popups than the cap -> only the EARLIEST render in the
        // capsule. (A late popup may still appear in `## Server hints` newest,
        // so scope the assertion to the capsule via its unique body line.)
        var es = new EventStream();
        for (int i = 0; i < 12; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.PopupString, Text = $"directive number {i:D2}" });
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(), es, null);

        var marker = "directed text the server/NPCs sent you earlier this session";
        var start = p.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(start >= 0, "the capsule must render");
        var rest = p.Substring(start);
        var nextHdr = rest.IndexOf("\n## ", System.StringComparison.Ordinal);
        var capsule = nextHdr >= 0 ? rest.Substring(0, nextHdr) : rest;
        // The EARLIEST sub-block is bounded to N: scope this assertion to the
        // text BEFORE the cp-2393 "most recent directed text" block.
        var recentMarker = capsule.IndexOf("most recent directed text", System.StringComparison.Ordinal);
        var earliestBlock = recentMarker >= 0 ? capsule.Substring(0, recentMarker) : capsule;
        Assert.Contains("directive number 00", earliestBlock); // earliest anchor
        Assert.DoesNotContain("directive number 11", earliestBlock); // beyond the earliest-N cap
        // cp-2393: the LATEST directive is now surfaced in the most-recent block.
        Assert.True(recentMarker >= 0, "the most-recent block must render");
        Assert.Contains("directive number 11", capsule);
    }

    [Fact]
    public void BuildUserPrompt_EarlyServerDirectivesCapsule_SurvivesHardCutAndRingEviction()
    {
        // END-TO-END (cp-2382 persistence + cp-2383 protected tail): an early
        // login directive that has BOTH aged out of the 256-event ring AND a
        // body large enough to force the hard-cut (which deletes `## Server
        // hints`) must STILL surface — via the persistent store rendered in the
        // protected salience tail — and the prompt must respect the ceiling.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PopupString,
            Text = "Skip the tutorial: talk to the exit guide in the next room to leave.",
        });
        for (int i = 0; i < EventStream.DefaultCapacity + 20; i++)
            es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ServerMessage, Text = $"ambient chatter {i}" });

        // Precondition: the popup is gone from the ring.
        Assert.DoesNotContain(es.Recent(EventStream.DefaultCapacity), e => e.Text is { } t && t.Contains("exit guide"));

        var dense = System.Linq.Enumerable.Range(0, 200)
            .Select(i => new VisibleObjectProjection
            {
                Guid = (uint)(0x900u + i),
                Name = $"Dense Scene Object {i:D3} occupying prompt budget space here",
                Wcid = (uint)(1000 + i),
                Distance = i + 1f,
            })
            .ToArray();
        var p = LlmGoalPolicy.BuildUserPrompt(BuildWorldWithMonsters(dense), es, null);

        Assert.Contains("## Early server directives", p);
        Assert.Contains("talk to the exit guide in the next room", p);
        Assert.True(p.Length <= 26000, $"prompt length {p.Length} must respect the hard ceiling");
    }

    // The HUNT EXCURSION / STEER A BARREN EXCURSION / LOOP-BREAK (town-stuck)
    // rules are entirely about the NO-monster-in-view case, so they are gated
    // OFF when a monster is visible (behaviour-preserving, frees ~2.5KB of
    // prompt budget in a combat scene). The NON-HOSTILE rule is the inverse.

    [Fact]
    public void BuildUserPrompt_ExcursionRules_OmittedWhenMonsterInView()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xB00u, Name = "Sparring Golem", Wcid = 70u, Distance = 5f, IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        // Inapplicable "go find monsters" rules are gated off...
        Assert.DoesNotContain("HUNT EXCURSION", p);
        Assert.DoesNotContain("STEER A BARREN EXCURSION", p);
        Assert.DoesNotContain("LOOP-BREAK (town-stuck)", p);
        // ...while the monster-present rule renders.
        Assert.Contains("NON-HOSTILE IS NOT NON-TARGET", p);
    }

    [Fact]
    public void BuildUserPrompt_ExcursionRules_PresentWhenNoMonsterInView()
    {
        // No monster visible (only a non-monster object) -> the excursion/town-
        // stuck rules render unchanged, and the NON-HOSTILE rule is omitted.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xB01u, Name = "Town Crier", Wcid = 90u, Distance = 4f, IsMonster = false });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("HUNT EXCURSION", p);
        Assert.Contains("STEER A BARREN EXCURSION", p);
        Assert.Contains("LOOP-BREAK (town-stuck)", p);
        Assert.DoesNotContain("NON-HOSTILE IS NOT NON-TARGET", p);
    }

    // cp-2406: the "Combat targets" rule is only actionable with a monster in
    // view (nothing to Attack otherwise), so it is gated on monsterInView like
    // the NON-HOSTILE rule.

    [Fact]
    public void BuildUserPrompt_CombatTargetsRule_PresentWhenMonsterInView()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xB00u, Name = "Sparring Golem", Wcid = 70u, Distance = 5f, IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("Combat targets:", p);
    }

    [Fact]
    public void BuildUserPrompt_CombatTargetsRule_OmittedWhenNoMonsterInView()
    {
        // No monster visible -> the monster-vs-npc targeting rule is moot
        // (Attack has no candidate) and is gated off to free prompt budget.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xB01u, Name = "Town Crier", Wcid = 90u, Distance = 4f, IsMonster = false });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("Combat targets:", p);
    }

    // cp-2409: TRANSITIONS (door/portal Use) + CLOSED DOORS (door-only) are
    // gated on door/portal visibility.

    [Fact]
    public void BuildUserPrompt_DoorRules_RenderWhenDoorVisible()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection { Guid = 0xD00u, Name = "Wooden Door", Distance = 3f, IsDoor = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("TRANSITIONS — doors and portals", p);
        Assert.Contains("CLOSED DOORS ARE BARRIERS", p);
    }

    [Fact]
    public void BuildUserPrompt_TransitionsRendersForPortal_ButClosedDoorsDoesNot()
    {
        // A portal (no door) makes TRANSITIONS actionable but not the door-only
        // CLOSED DOORS rule.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection { Guid = 0xD01u, Name = "Portal", Distance = 3f, IsPortal = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("TRANSITIONS — doors and portals", p);
        Assert.DoesNotContain("CLOSED DOORS ARE BARRIERS", p);
    }

    [Fact]
    public void BuildUserPrompt_DoorRules_OmittedWhenNoDoorOrPortalVisible()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection { Guid = 0xD02u, Name = "Town Crier", Distance = 4f, IsMonster = false });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("TRANSITIONS — doors and portals", p);
        Assert.DoesNotContain("CLOSED DOORS ARE BARRIERS", p);
    }

    [Fact]
    public void BuildUserPrompt_ExcursionRuleGating_ExcludesCorpsesAndCountsObservedHostile()
    {
        // A corpse is NOT a monster-in-view (excursion rules still render);
        // an ObservedHostile non-monster IS (excursion rules gated off) — the
        // gate uses the same `!IsCorpse && (IsMonster || ObservedHostile)` fact
        // as the rendered `monsters in view` line.
        var corpseOnly = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xB02u, Name = "Corpse of a Golem", Distance = 3f, IsCorpse = true, IsMonster = true });
        var pc = LlmGoalPolicy.BuildUserPrompt(corpseOnly, new EventStream(), null);
        Assert.Contains("HUNT EXCURSION", pc); // corpse excluded -> no monster in view
        Assert.DoesNotContain("NON-HOSTILE IS NOT NON-TARGET", pc);

        var hostile = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xB03u, Name = "Angry Critter", Distance = 3f, IsMonster = false, ObservedHostile = true });
        var ph = LlmGoalPolicy.BuildUserPrompt(hostile, new EventStream(), null);
        Assert.DoesNotContain("HUNT EXCURSION", ph); // observed-hostile -> monster in view
        Assert.Contains("NON-HOSTILE IS NOT NON-TARGET", ph);
    }

    // ---- COMBAT SAFETY rule gating on combat-relevance (cp-2369) ----------
    // The ~2KB COMBAT SAFETY & PACE rule is entirely about an in-progress or
    // imminent fight, so it renders only when a monster is in view OR a fight
    // is active (CurrentFight != null). Frees ~2KB of budget in non-combat
    // scenes; behaviour-preserving.

    private static WorldStateProjection BuildWorld_NoMonster_WithFight(CombatFightStatus? fight)
        => new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            CurrentFight = fight,
        };

    [Fact]
    public void BuildUserPrompt_CombatSafetyRule_PresentWhenMonsterInView()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xC00u, Name = "Sparring Golem", Wcid = 70u, Distance = 5f, IsMonster = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("COMBAT SAFETY & PACE", p);
        // Semantic canary (relocated from BuildUserPrompt_RulesRetainCriticalBehaviorClauses,
        // which now omits COMBAT SAFETY because it is conditional): the critical
        // trigger->action clauses must survive whenever the rule DOES render.
        Assert.Contains("DISENGAGE", p);
        Assert.Contains("AVOID re-attacking the same KIND", p);
        Assert.Contains("trust the ABSOLUTE HP", p);
        Assert.Contains("regenerating BELOW full strength", p);
    }

    [Fact]
    public void BuildUserPrompt_CombatSafetyRule_PresentDuringActiveFightEvenWithNoMonsterVisible()
    {
        // A fight that scrolled the foe out of view: CurrentFight persists, so
        // the disengage/safety guidance must still render.
        var world = BuildWorld_NoMonster_WithFight(
            new CombatFightStatus(0xABCDu, "Drudge Skulker", 0, 6, 0));
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("COMBAT SAFETY & PACE", p);
    }

    [Fact]
    public void BuildUserPrompt_CombatSafetyRule_OmittedWhenNoMonsterAndNoFight()
    {
        // No monster in view AND no active fight -> the rule is inapplicable
        // noise and is gated off to free prompt budget.
        var world = BuildWorld_NoMonster_WithFight(null);
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("COMBAT SAFETY & PACE", p);
    }

    // ---- Writables rule gating on sign/book visibility (cp-2370) ----------
    // The Writables rule applies only when a sign or book is visible (the
    // IsSign/IsBook projection flags), so it is gated on their presence (the
    // cp-2331 corpse/chest gating pattern). Unlike PASSAGE-OPENED it has no
    // temporal/recent-Use aspect, so visibility-gating is exact.

    [Fact]
    public void BuildUserPrompt_WritablesRule_PresentWhenSignVisible()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xD00u, Name = "WIELDING ITEMS", Wcid = 5101u, Distance = 4f, IsSign = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("- Writables:", p);
    }

    [Fact]
    public void BuildUserPrompt_WritablesRule_PresentWhenBookVisible()
    {
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0xD01u, Name = "Tinkering", Wcid = 21093u, Distance = 6f, IsBook = true });
        var p = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("- Writables:", p);
    }

    [Fact]
    public void BuildUserPrompt_WritablesRule_OmittedWhenNoSignOrBook()
    {
        // No sign/book visible -> the rule carries no information and is omitted.
        var p = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("- Writables:", p);
    }

    // ---- Inventory dedup + prompt-bound (cp-2334) -------------------------
    // A bloated bag of duplicate quest items was rendered one-row-per-item and
    // (being an early, non-trimmable section) pushed the later FIXED sections
    // (`## Combat readiness`, `## Visible nearby`) past the hard ceiling, where
    // the defensive cut deleted them — bricking the bot (it could not see it
    // was armed and looped Wield). These lock the collapse + the bound.

    private static WorldStateProjection BuildInventoryWorld(
        InventoryItemProjection[] inventory, VisibleObjectProjection[]? visible = null)
        => new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B4u, CellId = 0xA9B40001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = inventory,
            Visible = visible ?? System.Array.Empty<VisibleObjectProjection>(),
        };

    private static InventoryItemProjection ListItem(uint guid) => new InventoryItemProjection
    {
        Guid = guid, Name = "A List of Items", Wcid = 30491u,
        ShortDesc = "Worcer in Holtburg is requesting help retrieving these items from the Holtburg Redoubt.",
    };

    [Fact]
    public void BuildUserPrompt_Inventory_CollapsesDuplicateStacksWithCount()
    {
        var inv = Enumerable.Range(0, 30).Select(i => ListItem(0x1000u + (uint)i)).ToArray();
        var world = BuildInventoryWorld(inv);

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        // One collapsed row with the count, not 30 separate rows.
        Assert.Contains("- A List of Items (wcid=30491) x30", prompt);
        // The item line renders exactly once (the xN replaces the repeats).
        var rowOccurrences = System.Text.RegularExpressions.Regex.Matches(
            prompt, @"- A List of Items \(wcid=30491\)").Count;
        Assert.Equal(1, rowOccurrences);
        // short_desc renders once for the group, not 30 times. The protected
        // `## Held items` capsule (cp-2389) re-surfaces it once more by design
        // for cut-survival, so scope this body-dedup assertion to the body
        // (before the capsule).
        var bodyBeforeHeld = prompt.IndexOf("## Held items", StringComparison.Ordinal) is int hi && hi >= 0
            ? prompt.Substring(0, hi)
            : prompt;
        var sdOccurrences = System.Text.RegularExpressions.Regex.Matches(
            bodyBeforeHeld, "Holtburg Redoubt").Count;
        Assert.Equal(1, sdOccurrences);
    }

    [Fact]
    public void BuildUserPrompt_Inventory_DistinctItems_NoCountSuffix()
    {
        var inv = new[]
        {
            new InventoryItemProjection { Guid = 0x1u, Name = "Sack", Wcid = 166u },
            new InventoryItemProjection { Guid = 0x2u, Name = "Pathwarden Supply Key", Wcid = 33608u },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildInventoryWorld(inv), new EventStream(), null);

        Assert.Contains("- Sack (wcid=166)", prompt);
        Assert.Contains("- Pathwarden Supply Key (wcid=33608)", prompt);
        // No spurious xN count for singletons.
        Assert.DoesNotContain("- Sack (wcid=166) x", prompt);
        Assert.DoesNotContain("- Pathwarden Supply Key (wcid=33608) x", prompt);
    }

    [Fact]
    public void BuildUserPrompt_Inventory_WieldedAndBaggedSameItem_NotCollapsed()
    {
        // Same name+wcid but different wielded state are distinct facts and
        // must render as separate rows (one wielded@, one bagged).
        var inv = new[]
        {
            new InventoryItemProjection
            { Guid = 0x1u, Name = "Training Spadone", Wcid = 1u, ItemType = 0x1u, WieldedAt = 0x02000000u },
            new InventoryItemProjection
            { Guid = 0x2u, Name = "Training Spadone", Wcid = 1u, ItemType = 0x1u },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildInventoryWorld(inv), new EventStream(), null);

        Assert.Contains("- Training Spadone (wcid=1, wielded@0x2000000)", prompt);
        Assert.Contains("- Training Spadone (wcid=1)", prompt);
        // Neither collapsed into the other (the count suffix form is ") xN").
        Assert.DoesNotContain(") x2", prompt);
    }

    [Fact]
    public void BuildUserPrompt_BloatedInventory_FixedSectionsSurviveTruncation()
    {
        // THE regression: a bag flooded with duplicate quest notes plus a
        // wielded melee weapon and a visible monster. After the build, the
        // later FIXED sections MUST still be present and the whole prompt must
        // honor the hard ceiling. Before the dedup+bound fix the inventory ate
        // the budget and these were truncated away.
        var inv = new System.Collections.Generic.List<InventoryItemProjection>();
        for (uint i = 0; i < 60; i++) inv.Add(ListItem(0x1000u + i));
        inv.Add(new InventoryItemProjection
        { Guid = 0x9u, Name = "Training Spadone", Wcid = 1u, ItemType = 0x1u, WieldedAt = 0x02000000u });
        var visible = new[]
        {
            new VisibleObjectProjection { Guid = 0x404u, Name = "Cow", Wcid = 24937u, Distance = 6f, IsMonster = true },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildInventoryWorld(inv.ToArray(), visible), new EventStream(), null);

        Assert.True(prompt.Length <= 26000, $"prompt length {prompt.Length} exceeds ceiling");
        Assert.Contains("## Combat readiness", prompt);
        Assert.Contains("melee weapon wielded", prompt);
        Assert.Contains("## Visible nearby", prompt);
    }

    // ---- SELF-ARM rule gating on combat-effectiveness (cp-2374) ------------
    // The SELF-ARM rule applies only when the bot is not yet combat-effective
    // (no melee weapon wielded, no wielded missile weapon with ammo). Gated on
    // the same wire fact (WieldedAt + typed weapon/ammo masks) the combat-
    // readiness `weapon:` line uses (cp-2335 per-rule gating pattern).

    [Fact]
    public void BuildUserPrompt_SelfArmRule_PresentWhenUnarmed()
    {
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildInventoryWorld(System.Array.Empty<InventoryItemProjection>()), new EventStream(), null);
        Assert.Contains("SELF-ARM before fighting", p);
    }

    [Fact]
    public void BuildUserPrompt_SelfArmRule_OmittedWhenMeleeWielded()
    {
        var inv = new[]
        {
            new InventoryItemProjection
            { Guid = 0x1u, Name = "Spadone", Wcid = 1u, ItemType = 0x1u, WieldedAt = 0x02000000u },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(BuildInventoryWorld(inv), new EventStream(), null);
        Assert.DoesNotContain("SELF-ARM before fighting", p);
    }

    [Fact]
    public void BuildUserPrompt_SelfArmRule_PresentWhenMissileWieldedButNoAmmo()
    {
        // A wielded missile weapon with EMPTY ammo is NOT combat-effective —
        // the rule must render (it tells the bot to wield ammo).
        var inv = new[]
        {
            new InventoryItemProjection
            { Guid = 0x1u, Name = "Yumi", Wcid = 2u, ItemType = 0x100u, WieldedAt = 0x02000000u },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(BuildInventoryWorld(inv), new EventStream(), null);
        Assert.Contains("SELF-ARM before fighting", p);
    }

    [Fact]
    public void BuildUserPrompt_SelfArmRule_OmittedWhenMissileWieldedWithAmmo()
    {
        // Wielded missile weapon + wielded ammo (WieldedAt == MissileAmmoSlot) is
        // combat-effective -> the rule is moot and omitted.
        var inv = new[]
        {
            new InventoryItemProjection
            { Guid = 0x1u, Name = "Yumi", Wcid = 2u, ItemType = 0x100u, WieldedAt = 0x02000000u },
            new InventoryItemProjection
            { Guid = 0x2u, Name = "Arrows", Wcid = 3u, ItemType = 0x800u, WieldedAt = 0x00800000u },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(BuildInventoryWorld(inv), new EventStream(), null);
        Assert.DoesNotContain("SELF-ARM before fighting", p);
    }

    [Fact]
    public void FitPromptToCeiling_TrimsInventoryBeforeGuillotiningFixedSections()
    {
        // Directly exercise the cascade addition: a huge `## Inventory` of
        // DISTINCT rows (dedup can't help) followed by a FIXED section. The
        // cascade must shed inventory trailing rows so the fixed section that
        // renders AFTER it survives, rather than the hard-cut deleting it.
        var sb = new StringBuilder();
        sb.AppendLine("## Inventory");
        for (int i = 0; i < 400; i++) sb.AppendLine($"- Unique Item {i} (wcid={1000 + i})");
        sb.AppendLine("## Combat readiness");
        sb.AppendLine("- weapon: melee weapon wielded");
        var raw = sb.ToString();
        Assert.True(raw.Length > 600);

        var fitted = LlmGoalPolicy.FitPromptToCeiling(raw, ceiling: 600);

        Assert.True(fitted.Length <= 600, $"fitted length {fitted.Length} exceeds ceiling");
        Assert.Contains("## Combat readiness", fitted);
        Assert.Contains("- weapon: melee weapon wielded", fitted);
        Assert.Contains("## Inventory", fitted);
        Assert.Contains("omitted to fit prompt budget", fitted);
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
    public void BuildUserPrompt_RetainsEarlyExitPopup_AfterRingEviction()
    {
        // The bot may not be ready to act on the login exit directive until
        // hundreds of events later, by which point it has aged out of the
        // bounded 256-event ring entirely. The EventStream's persistent
        // distinct-popup store must still surface it in ## Server hints; the
        // ring-only sourcing could not.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PopupString,
            Text = "Go talk to Jonathan in the next room. Once you leave you can never return.",
        });
        // Flood well past the ring capacity so the login popup is evicted.
        for (int i = 0; i < EventStream.DefaultCapacity + 20; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ServerMessage,
                Text = $"ambient server chatter {i}",
            });
        }

        // Precondition: the login popup is truly gone from the ring.
        Assert.DoesNotContain(
            es.Recent(EventStream.DefaultCapacity),
            e => e.Text is { } t && t.Contains("Once you leave you can never return"));

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
        Assert.Contains("irreversible", prompt);
    }

    [Fact]
    public void BuildUserPrompt_PrecedenceRule_RecognizesOptionalSkipAdvanceDirective()
    {
        // cp-2391: live academy failure — gpt-4.1 reasoned "no active server
        // directive is pending" and grinded killable golems while an
        // optional-phrased skip directive ("if you wish to skip this tutorial,
        // go talk to <NPC>") was present at d=2.5u. The precedence rule must now
        // recognize SKIP/COMPLETE and that an optional framing or equivalent-
        // reward promise does NOT make the directive "absent" / "no directive
        // pending".
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        var precIdx = prompt.IndexOf("SERVER-INSTRUCTION PRECEDENCE", System.StringComparison.Ordinal);
        Assert.True(precIdx >= 0);
        var precLineEnd = prompt.IndexOf('\n', precIdx);
        var precLine = precLineEnd >= 0 ? prompt.Substring(precIdx, precLineEnd - precIdx) : prompt.Substring(precIdx);
        Assert.Contains("SKIP", precLine);
        Assert.Contains("no directive pending", precLine);
        Assert.Contains("if you wish", precLine);
    }

    [Fact]
    public void BuildUserPrompt_ContainsAreaCompleteMoveOnRule()
    {
        // cp-2394: live academy — after the Training Master says "you have
        // completed your combat training, take the portal", the bot grinds
        // respawning Sparring Golems ("attacking grants XP, no blocking
        // conditions") instead of taking the exit. A distinct rule must teach
        // that once an area reports COMPLETE, grinding its respawning monsters
        // is optional and the named exit/portal is the progression (WEIGH, not
        // a ban — respects valuing academy XP).
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        var idx = prompt.IndexOf("AREA COMPLETE means MOVE ON", System.StringComparison.Ordinal);
        Assert.True(idx >= 0, "the AREA COMPLETE rule must be present");
        var lineEnd = prompt.IndexOf('\n', idx);
        var line = lineEnd >= 0 ? prompt.Substring(idx, lineEnd - idx) : prompt.Substring(idx);
        Assert.Contains("respawning", line);
        Assert.Contains("WEIGH, not a hard ban", line);
        Assert.Contains("portal", line);
    }

    [Fact]
    public void BuildUserPrompt_DirectiveRules_PointAtEarlyServerDirectivesCapsule()
    {
        // cp-2384: the durable directive lives in `## Early server directives`
        // (the protected tail), because `## Server hints` is hard-cut in dense
        // scenes. The directive-locating rules must point the LLM at BOTH so it
        // does not look only at a section that may be absent.
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        // The precedence rule now references the durable capsule and extends its
        // precedence over optional grinding (not just re-looping local steps).
        var precIdx = prompt.IndexOf("SERVER-INSTRUCTION PRECEDENCE", System.StringComparison.Ordinal);
        Assert.True(precIdx >= 0, "precedence rule must be present");
        var precLineEnd = prompt.IndexOf('\n', precIdx);
        var precLine = precLineEnd >= 0 ? prompt.Substring(precIdx, precLineEnd - precIdx) : prompt.Substring(precIdx);
        Assert.Contains("## Early server directives", precLine);
        Assert.Contains("grinding", precLine);
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
    public void BuildUserPrompt_RepeatedIdenticalNpcReply_RendersRepeatCount()
    {
        // cp-2323 — the dedup collapses identical NPC replies to one line; the
        // repeat count must still surface so the LLM can tell a stuck Talk loop
        // (same reply N times) from a progressing dialog.
        var es = new EventStream();
        for (int i = 0; i < 12; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.NpcDialog, Name = "Jonathan",
                Text = "I already gave you an Exit Token.",
            });
        }

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("## Server hints", prompt);
        Assert.Contains("I already gave you an Exit Token.", prompt);
        Assert.Contains("(repeated x12)", prompt);
        // The neutral explanatory NOTE appears when at least one line repeated.
        Assert.Contains("\"(repeated xN)\"", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SingleNpcReply_NoRepeatSuffixOrNote()
    {
        // A reply seen ONCE must render without a count suffix, and the NOTE
        // must be omitted (no false "you are looping" signal).
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Name = "Jonathan",
            Text = "Welcome, adventurer.",
        });

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("Welcome, adventurer.", prompt);
        Assert.DoesNotContain("(repeated x", prompt);
        Assert.DoesNotContain("\"(repeated xN)\"", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RepeatCount_DistinguishesProgressingDialog()
    {
        // Two DIFFERENT replies from the same NPC must NOT be counted together;
        // a progressing multi-turn dialog should not be flagged as repeated.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Name = "Tutor",
            Text = "Step one: equip your weapon.",
        });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Name = "Tutor",
            Text = "Step two: attack the target.",
        });

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("Step one: equip your weapon.", prompt);
        Assert.Contains("Step two: attack the target.", prompt);
        Assert.DoesNotContain("(repeated x", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RepeatedPopupAndServerMessage_RenderRepeatCount()
    {
        // The count is uniform across all three deduped hint surfaces (no
        // object-type/NPC special-casing), so repeated PopupString and
        // ServerMessage lines surface their counts too.
        var es = new EventStream();
        for (int i = 0; i < 5; i++)
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.PopupString, Text = "Talk to Jonathan to leave.",
            });
        for (int i = 0; i < 3; i++)
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.ServerMessage, ChatType = 0,
                Text = "You are not in range.",
            });

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("Talk to Jonathan to leave.", prompt);
        Assert.Contains("(repeated x5)", prompt);
        Assert.Contains("You are not in range.", prompt);
        Assert.Contains("(repeated x3)", prompt);
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
        Assert.Contains("`monster`-tagged creatures", prompt);
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

        // Weapon line — inventory has a wielded MELEE weapon so should say so.
        Assert.Contains("weapon: melee weapon wielded", crBlock);
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

        Assert.Contains("weapon: NONE wielded - UNARMED", crBlock);
        Assert.Contains("nearest monster: (none in view)", crBlock);
    }

    [Fact]
    public async Task LlmGoalPolicy_SelfHealth_SurfacedInPrompt()
    {
        // Self-health perception: once HealthFraction is known it must
        // appear both in `## Self` and `## Combat readiness` so the LLM
        // can weigh survival (the existing COMBAT SAFETY rule references
        // health). Pure perception surface — no source-side threshold.
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

        var world = BuildHealthAwareCombatWorld(0.42f);
        var events = new EventStream();

        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        Assert.NotNull(policy.ProposeGoal(world, events, null));

        var body = requestBodies[0];
        var selfIdx = body.IndexOf("## Self", StringComparison.Ordinal);
        var crIdx = body.IndexOf("## Combat readiness", StringComparison.Ordinal);
        Assert.True(selfIdx >= 0 && crIdx >= 0);
        // 0.42 renders as "42 %" under the invariant P0 format.
        Assert.Contains("health: 42", body);
    }

    // Armed bot near a monster with a known health fraction — exercises
    // the self-health PERCEPTION surface (not any source-side gate).
    private static WorldStateProjection BuildHealthAwareCombatWorld(float healthFraction) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
            PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = healthFraction,
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
                Guid = MobGuid, Name = "Sparring Golem", Wcid = 12698u,
                ItemType = 0x10u, Distance = 7f, IsCreature = true,
                IsAttackable = true, HasRadarBlipColor = false, IsMonster = true,
            },
        },
    };

    [Fact]
    public void CombatReadiness_ArmorWielded_NotCountedAsWeapon()
    {
        // Load-bearing fix: a wielded ARMOR piece (Leather Cap,
        // ItemType MeleeWeapon bit CLEAR) must NOT read as a weapon.
        // The old `Any(WieldedAt != 0)` signal let the bot think it
        // was armed after equipping a hat.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                { Guid = 0x111u, Name = "Leather Cap", Wcid = 13239u, ItemType = 0x2u, WieldedAt = 0x1u },
            },
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("weapon: NONE wielded - UNARMED", prompt);
        Assert.DoesNotContain("melee weapon wielded", prompt);
    }

    // ── immobile-stuck telemetry ("## Movement" section) ─────────────────
    private static WorldStateProjection BuildImmobileWorld(int blockStops) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
            PositionX = 6.71f, PositionY = 66.30f, PositionZ = 66.52f, HealthFraction = 1.0f,
        },
        Inventory = System.Array.Empty<InventoryItemProjection>(),
        Visible = System.Array.Empty<VisibleObjectProjection>(),
        MovementBlockStopsSinceSelfMoved = blockStops,
    };

    [Fact]
    public void Movement_NoBlockStops_SectionOmitted()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildImmobileWorld(0), new EventStream(), null);
        Assert.DoesNotContain("## Movement", prompt);
    }

    [Fact]
    public void Movement_SingleBlockStop_RendersRawFact()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildImmobileWorld(1), new EventStream(), null);
        Assert.Contains("## Movement", prompt);
        Assert.Contains("1 consecutive move attempt(s) made no progress", prompt);
    }

    [Fact]
    public void Movement_RepeatedBlockStops_RendersTheRawCount()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildImmobileWorld(7), new EventStream(), null);
        Assert.Contains("## Movement", prompt);
        // The raw count is surfaced verbatim; source asserts no urgency label.
        Assert.Contains("7 consecutive move attempt(s) made no progress", prompt);
        Assert.Contains("same position each time", prompt);
    }

    [Fact]
    public void Movement_ProjectionCarriesRawCount_FromWorldState()
    {
        // The mutable WorldState field flows into the immutable projection
        // unchanged (the driver publishes it before each projection build).
        var world = new HeadlessAcClient.World.WorldState();
        world.MovementBlockStopsSinceSelfMoved = 4;
        Assert.Equal(4, world.MovementBlockStopsSinceSelfMoved);
        var proj = BuildImmobileWorld(4);
        Assert.Equal(4, proj.MovementBlockStopsSinceSelfMoved);
    }

    // ── fellowship perception ("## Fellowship" section) ──────────────────
    private static WorldStateProjection BuildFellowshipWorld(FellowshipProjection? fellow) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
            PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
        },
        Inventory = System.Array.Empty<InventoryItemProjection>(),
        Visible = System.Array.Empty<VisibleObjectProjection>(),
        Fellowship = fellow,
    };

    [Fact]
    public void Fellowship_NotInOne_SectionOmitted()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildFellowshipWorld(null), new EventStream(), null);
        Assert.DoesNotContain("## Fellowship", prompt);
    }

    [Fact]
    public void Fellowship_AsLeader_RendersMembersAndLeaderClause()
    {
        var fellow = new FellowshipProjection
        {
            Name = "Crew", AmLeader = true, LeaderName = "Headless", MemberCount = 2,
            Members = new[]
            {
                new FellowshipMemberProjection { Name = "Headless", Level = 10u, IsSelf = true, IsLeader = true },
                new FellowshipMemberProjection { Name = "Pal", Level = 12u, IsSelf = false, IsLeader = false },
            },
            ShareXp = true, EvenShare = false, Open = true, Locked = false,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildFellowshipWorld(fellow), new EventStream(), null);
        Assert.Contains("## Fellowship", prompt);
        Assert.Contains("you are in a fellowship \"Crew\"", prompt);
        Assert.Contains("2 member(s)", prompt);
        Assert.Contains("you are the leader", prompt);
        Assert.Contains("Headless (L10, you, leader)", prompt);
        Assert.Contains("Pal (L12)", prompt);
        Assert.Contains("shares XP yes", prompt);
        Assert.Contains("open yes", prompt);
        Assert.Contains("locked no", prompt);
    }

    [Fact]
    public void Fellowship_AsMember_RendersLedByLeaderName()
    {
        var fellow = new FellowshipProjection
        {
            Name = "Squad", AmLeader = false, LeaderName = "Boss", MemberCount = 2,
            Members = new[]
            {
                new FellowshipMemberProjection { Name = "Boss", Level = 20u, IsSelf = false, IsLeader = true },
                new FellowshipMemberProjection { Name = "Headless", Level = 8u, IsSelf = true, IsLeader = false },
            },
            ShareXp = false, EvenShare = true, Open = false, Locked = true,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildFellowshipWorld(fellow), new EventStream(), null);
        Assert.Contains("## Fellowship", prompt);
        Assert.Contains("led by Boss", prompt);
        Assert.DoesNotContain("you are the leader", prompt);
        Assert.Contains("Headless (L8, you)", prompt);
        Assert.Contains("Boss (L20, leader)", prompt);
        Assert.Contains("locked yes", prompt);
    }

    [Fact]
    public void Fellowship_FromWorldState_DerivesAmLeaderAndSelfFlags()
    {
        var ws = new HeadlessAcClient.World.WorldState();
        ws.SetSelf(SelfGuid);
        // Materialize the self snapshot so FromWorldState returns a projection.
        ws.Apply(new HeadlessAcClient.Protocol.GameMessages.PrivateUpdatePropertyIntMessage(
            Sequence: 1, Property: 25, Value: 5));
        ws.ApplyFellowshipFullUpdate(new HeadlessAcClient.Protocol.GameMessages.FellowshipFullUpdatePayload(
            new[]
            {
                new HeadlessAcClient.Protocol.GameMessages.FellowMember(SelfGuid, 5u, 0u, 0u, 0u, 0u, 0u, 0u, "Headless"),
                new HeadlessAcClient.Protocol.GameMessages.FellowMember(0x90000010u, 9u, 0u, 0u, 0u, 0u, 0u, 0u, "Pal"),
            },
            FellowshipName: "Crew", LeaderGuid: SelfGuid,
            ShareXp: true, EvenShare: false, Open: false, IsLocked: false));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.NotNull(proj!.Fellowship);
        Assert.Equal("Crew", proj.Fellowship!.Name);
        Assert.True(proj.Fellowship.AmLeader);
        Assert.Equal("Headless", proj.Fellowship.LeaderName);
        Assert.Equal(2, proj.Fellowship.MemberCount);
        var me = proj.Fellowship.Members.Single(m => m.IsSelf);
        Assert.Equal("Headless", me.Name);
        Assert.True(me.IsLeader);
        var pal = proj.Fellowship.Members.Single(m => !m.IsSelf);
        Assert.Equal("Pal", pal.Name);
        Assert.False(pal.IsLeader);
    }

    [Fact]
    public void Fellowship_FromWorldState_NoFellowship_NullProjection()
    {
        var ws = new HeadlessAcClient.World.WorldState();
        ws.SetSelf(SelfGuid);
        ws.Apply(new HeadlessAcClient.Protocol.GameMessages.PrivateUpdatePropertyIntMessage(
            Sequence: 1, Property: 25, Value: 5));
        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        Assert.Null(proj!.Fellowship);
    }

    // ── named-target search telemetry ("## Search progress" section) ─────
    private static WorldStateProjection BuildNamedSearchWorld(
        string? targetName, int probes, int distinctCells) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u, CellId = 0x860201ADu,
            PositionX = 1.0f, PositionY = 2.0f, PositionZ = 3.0f, HealthFraction = 1.0f,
        },
        Inventory = System.Array.Empty<InventoryItemProjection>(),
        Visible = System.Array.Empty<VisibleObjectProjection>(),
        NamedSearchTargetName = targetName,
        NamedSearchProbeCount = probes,
        NamedSearchDistinctCells = distinctCells,
    };

    [Fact]
    public void SearchProgress_BelowThreshold_SectionOmitted()
    {
        // A normal short walk-to-discover (1-2 probes) must not nag.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildNamedSearchWorld("Agent", 2, 2), new EventStream(), null);
        Assert.DoesNotContain("## Search progress", prompt);
    }

    [Fact]
    public void SearchProgress_NoTargetName_SectionOmitted()
    {
        // Probe count high but no named target ⇒ nothing to render.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildNamedSearchWorld(null, 9, 5), new EventStream(), null);
        Assert.DoesNotContain("## Search progress", prompt);
    }

    [Fact]
    public void SearchProgress_AtThreshold_RendersRawFacts()
    {
        // 3 probes over 3 distinct cells: searching, but NOT yet repeating.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildNamedSearchWorld("Agent", 3, 3), new EventStream(), null);
        Assert.Contains("## Search progress", prompt);
        Assert.Contains("the named target 'Agent' is still not visible after 3 discovery move(s)", prompt);
        Assert.Contains("3 distinct cell(s) tried", prompt);
        // probes == distinct ⇒ no "repeating cells" clause.
        Assert.DoesNotContain("repeating cells already tried", prompt);
    }

    [Fact]
    public void SearchProgress_RepeatingCells_RendersStalledClause()
    {
        // 28 probes but only 5 distinct cells: the search is revisiting ground
        // (the live academy "Talk Agent" loop) — surface the stall.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildNamedSearchWorld("Agent", 28, 5), new EventStream(), null);
        Assert.Contains("## Search progress", prompt);
        Assert.Contains("after 28 discovery move(s)", prompt);
        Assert.Contains("5 distinct cell(s) tried", prompt);
        Assert.Contains("repeating cells already tried", prompt);
        Assert.Contains("not reaching new ground", prompt);
    }

    [Fact]
    public void SearchProgress_ProjectionCarriesFields_FromWorldState()
    {
        var world = new HeadlessAcClient.World.WorldState();
        world.NamedSearchTargetName = "Agent";
        world.NamedSearchProbeCount = 7;
        world.NamedSearchDistinctCells = 4;
        Assert.Equal("Agent", world.NamedSearchTargetName);
        var proj = BuildNamedSearchWorld("Agent", 7, 4);
        Assert.Equal("Agent", proj.NamedSearchTargetName);
        Assert.Equal(7, proj.NamedSearchProbeCount);
        Assert.Equal(4, proj.NamedSearchDistinctCells);
    }

    [Fact]
    public void SearchProgress_LongTargetName_Truncated()
    {
        var longName = new string('x', 100);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildNamedSearchWorld(longName, 5, 5), new EventStream(), null);
        Assert.Contains("## Search progress", prompt);
        Assert.Contains(new string('x', 60), prompt);
        Assert.DoesNotContain(new string('x', 61), prompt);
    }

    [Fact]
    public void CombatReadiness_UnwieldedBagWeapon_SurfacesWieldAffordance()
    {
        // Unarmed but a melee weapon sits unwielded in the bag →
        // surface a Wield-to-arm affordance so the LLM can act.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                { Guid = 0x222u, Name = "Training Spadone", Wcid = 5104u, ItemType = 0x1u, WieldedAt = null },
            },
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("weapon: NONE wielded - UNARMED", prompt);
        Assert.Contains("melee weapon in your inventory (Wield it to arm): Training Spadone", prompt);
    }

    [Fact]
    public void CombatReadiness_VisibleGroundWeapon_SurfacesPickupAffordance()
    {
        // Unarmed, empty bag, but a melee weapon lies on the ground →
        // surface a Pickup-to-arm affordance (the live failure mode:
        // a grounded Training Spadone the bot never picked up).
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x333u, Name = "Hand Axe", Wcid = 303u, ItemType = 0x1u, Distance = 12f, IsMonster = false },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("melee weapon nearby (Pickup it to arm): Hand Axe", prompt);
    }

    [Fact]
    public void CombatReadiness_MeleeWielded_SuppressesArmAffordances()
    {
        // Already armed (melee weapon wielded) → no self-arm
        // affordances even if other weapons are in bag / on ground.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                { Guid = 0x222u, Name = "Training Spadone", Wcid = 5104u, ItemType = 0x1u, WieldedAt = 0x100000u },
            },
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x333u, Name = "Hand Axe", Wcid = 303u, ItemType = 0x1u, Distance = 12f, IsMonster = false },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("weapon: melee weapon wielded", prompt);
        Assert.DoesNotContain("Wield it to arm", prompt);
        Assert.DoesNotContain("Pickup it to arm", prompt);
    }

    [Fact]
    public void CombatReadiness_MultipleMonsters_RendersThreatCount()
    {
        // cp-2297: a cluster of monsters, two already attacking, must
        // surface a crisp `monsters in view` count so the COMBAT SAFETY
        // "pull singly" rule has a signal to act on (the bot previously
        // walked into a 4-mob cluster and died — cp-2296).
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x401u, Name = "Drudge Skulker", Wcid = 19257u, Distance = 8f,
                  IsAttackable = true, HasRadarBlipColor = false, IsMonster = true, ObservedHostile = true },
                new VisibleObjectProjection
                { Guid = 0x402u, Name = "Drudge Slinker", Wcid = 19258u, Distance = 11f,
                  IsAttackable = true, HasRadarBlipColor = false, IsMonster = true, ObservedHostile = true },
                new VisibleObjectProjection
                { Guid = 0x403u, Name = "Drudge Slinker", Wcid = 19258u, Distance = 15f,
                  IsAttackable = true, HasRadarBlipColor = false, IsMonster = true, ObservedHostile = false },
                // A corpse must NOT be counted as a live threat.
                new VisibleObjectProjection
                { Guid = 0x404u, Name = "Corpse of a Drudge", Wcid = 21u, Distance = 5f,
                  IsCorpse = true, IsMonster = false },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("monsters in view: 3 (2 actively HOSTILE (attacking you now))", prompt);
    }

    [Fact]
    public void CombatReadiness_HostileNonMonster_ThreatLineAgreesWithObservedHostile()
    {
        // Coherence guard: a non-monster creature that is actively attacking
        // (ObservedHostile but not flagged IsMonster — e.g. a hostile NPC) must
        // still be counted as a threat so the "monsters in view" line never
        // contradicts the "observed hostile" line. H must be >= 1 here.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x501u, Name = "Angry Townsperson", Wcid = 999u, Distance = 6f,
                  IsMonster = false, ObservedHostile = true },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("observed hostile: Angry Townsperson", prompt);
        Assert.Contains("monsters in view: 1 (1 actively HOSTILE (attacking you now))", prompt);
    }

    [Fact]
    public void CombatReadiness_HostileCorpse_NeitherObservedHostileNorThreatLine()
    {
        // Coherence guard, corpse edge: a corpse can still carry the
        // ObservedHostile wire flag (set independently of corpse status). A
        // corpse is not a live threat, so it must be excluded from BOTH the
        // "observed hostile" line and the threat count — otherwise the two
        // signals contradict (observed-hostile prints while H == 0).
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x502u, Name = "Corpse of Drudge", Wcid = 999u, Distance = 4f,
                  IsMonster = false, IsCorpse = true, ObservedHostile = true },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("observed hostile:", prompt);
        Assert.DoesNotContain("monsters in view:", prompt);
    }


    [Fact]
    public void CombatReadiness_MissileWieldedAmmoLoaded_ReadsArmed()
    {
        // combat-missile-attack: a wielded missile weapon (atlatl/bow,
        // ItemType MissileWeapon bit) with ammo loaded in the ammo slot
        // reads as armed missile, NOT UNARMED, and surfaces no self-arm
        // affordance.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                { Guid = 0x222u, Name = "Royal Atlatl", Wcid = 20640u, ItemType = 0x100u, WieldedAt = 0x400000u },
                new InventoryItemProjection
                { Guid = 0x223u, Name = "Dart", Wcid = 300u, ItemType = 0x100u, WieldedAt = 0x800000u },
            },
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("weapon: missile weapon wielded; missile ammo: loaded", prompt);
        Assert.DoesNotContain("weapon: NONE wielded - UNARMED", prompt);
        Assert.DoesNotContain("Wield it to arm", prompt);
    }

    [Fact]
    public void CombatReadiness_MissileWieldedAmmoEmpty_SurfacesBagAmmo()
    {
        // Atlatl wielded but no ammo in the ammo slot, with a dart sitting
        // unwielded in the bag (its ValidLocations includes the ammo slot)
        // → readiness reads EMPTY and surfaces a Wield-ammo affordance.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                { Guid = 0x222u, Name = "Royal Atlatl", Wcid = 20640u, ItemType = 0x100u, WieldedAt = 0x400000u },
                new InventoryItemProjection
                { Guid = 0x223u, Name = "Royal Dart", Wcid = 300u, ItemType = 0x100u, ValidLocations = 0x800000u, WieldedAt = null },
            },
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("weapon: missile weapon wielded; missile ammo: EMPTY", prompt);
        Assert.Contains("missile ammo in your inventory (Wield it to load): Royal Dart", prompt);
    }

    [Fact]
    public void CombatReadiness_CurrentFight_RendersLandedEvadedCounts()
    {
        // combat-damage-output: the live fight outcome (all swings evaded,
        // 0 landed, 0 damage) is surfaced verbatim so the LLM can judge it
        // is dealing no damage and disengage.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 0.4f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            CurrentFight = new CombatFightStatus(0xABCDu, "Drudge Skulker", 0, 6, 0),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains(
            "current fight vs \"Drudge Skulker\": swings landed 0, evaded 6, damage dealt 0",
            prompt);
    }

    [Fact]
    public void CombatReadiness_NoCurrentFight_OmitsFightLine()
    {
        // No active fight → no current-fight line.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            CurrentFight = null,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("current fight vs", prompt);
    }

    [Fact]
    public void CombatReadiness_CurrentFight_ZeroSwings_OmitsFightLine()
    {
        // A locked target with no swings yet (0 landed, 0 evaded) is not
        // informative — suppress the line until at least one swing resolves.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            CurrentFight = new CombatFightStatus(0xABCDu, "Rabbit", 0, 0, 0),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("current fight vs", prompt);
    }

    [Fact]
    public void CombatReadiness_CurrentFight_RendersTargetHealthTrajectory()
    {
        // combat-effectiveness: surface the target's health at fight start vs
        // now so the LLM can see it is barely denting an out-defending target
        // (e.g. many swings but health still ~89%) and disengage.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 0.4f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            CurrentFight = new CombatFightStatus(0xABCDu, "Auroch Bull", 3, 12, 17, 1.0f, 0.89f),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains(
            "current fight vs \"Auroch Bull\": swings landed 3, evaded 12, damage dealt 17; " +
            "target health now 89% (was 100% when this fight began)",
            prompt);
    }

    [Fact]
    public void CombatReadiness_CurrentFight_RendersCurrentHealthOnly_WhenNoFirstObservation()
    {
        // Only the current target-health fraction observed (no fight-start
        // baseline yet) → render the current value without a "was" clause.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            CurrentFight = new CombatFightStatus(0xABCDu, "Cow", 2, 1, 9, null, 0.5f),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("damage dealt 9; target health now 50%", prompt);
        Assert.DoesNotContain("now 50% (was", prompt);
    }

    [Fact]
    public void CombatReadiness_CurrentFight_OmitsTargetHealth_WhenNoneObserved()
    {
        // No target-health observed (both fractions null, the back-compat
        // default) → the fight line renders exactly as before, no health note.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            CurrentFight = new CombatFightStatus(0xABCDu, "Drudge Skulker", 0, 6, 0),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("current fight vs \"Drudge Skulker\": swings landed 0, evaded 6, damage dealt 0", prompt);
        Assert.DoesNotContain("damage dealt 0; target health", prompt);
    }

    [Fact]
    public void CombatReadiness_RecentInboundDamage_RendersRawHitsAndDamage()
    {
        // active-combat-telemetry: the rolling inbound-damage summary is
        // surfaced verbatim so the LLM can judge how fast it is taking damage
        // and decide to disengage/Recall. RAW counts only.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B6u, CellId = 0xA9B60001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 0.05f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            RecentInboundDamage = new RecentInboundDamage(4, 23u, 12.0),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains(
            "recent inbound damage: 4 hits taking 23 damage in the last ~12s",
            prompt);
    }

    [Fact]
    public void CombatReadiness_RecentInboundDamage_SingularHit()
    {
        // One hit reads "1 hit" (singular), not "1 hits".
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B6u, CellId = 0xA9B60001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 0.5f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            RecentInboundDamage = new RecentInboundDamage(1, 7u, 12.0),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("recent inbound damage: 1 hit taking 7 damage", prompt);
    }

    [Fact]
    public void CombatReadiness_NoRecentInboundDamage_OmitsLine()
    {
        // No recent inbound damage → no line (zero static-floor cost).
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B6u, CellId = 0xA9B60001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
            RecentInboundDamage = null,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("recent inbound damage:", prompt);
    }

    [Fact]
    public void FormatRecentInboundDamage_NullOrZeroHits_ReturnsNull()
    {
        Assert.Null(LlmGoalPolicy.FormatRecentInboundDamage(null));
        Assert.Null(LlmGoalPolicy.FormatRecentInboundDamage(
            new RecentInboundDamage(0, 0u, 12.0)));
    }

    [Fact]
    public void VisibleCorpse_OpenedByBot_AnnotatedYes()
    {
        // loot bookkeeping: a corpse the bot has itself opened is annotated
        // so the LLM does not re-pick it. Own-action bookkeeping; LLM decides.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B6u, CellId = 0xA9B60001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x404u, Name = "Corpse of a Drudge", Wcid = 21u, Distance = 2f,
                  IsCorpse = true, IsMonster = false },
            },
            OpenedCorpseGuids = new HashSet<uint> { 0x404u },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("Corpse of a Drudge", prompt);
        Assert.Contains("opened_by_bot_recently=yes", prompt);
        Assert.DoesNotContain("opened_by_bot_recently=no", prompt);
    }

    [Fact]
    public void VisibleCorpse_NotOpenedByBot_AnnotatedNo()
    {
        // A corpse the bot has NOT opened (null set) reads =no, so an un-looted
        // own kill the LLM might otherwise walk past is visible as such.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B6u, CellId = 0xA9B60001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x404u, Name = "Corpse of a Drudge", Wcid = 21u, Distance = 2f,
                  IsCorpse = true, IsMonster = false },
            },
            OpenedCorpseGuids = null,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("opened_by_bot_recently=no", prompt);
    }

    [Fact]
    public void VisibleCorpse_DifferentGuidOpened_AnnotatedNo()
    {
        // The annotation is per-GUID: a set that contains a DIFFERENT corpse's
        // GUID must not mark this corpse as opened.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B6u, CellId = 0xA9B60001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x404u, Name = "Corpse of a Drudge", Wcid = 21u, Distance = 2f,
                  IsCorpse = true, IsMonster = false },
            },
            OpenedCorpseGuids = new HashSet<uint> { 0x999u },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("opened_by_bot_recently=no", prompt);
    }

    [Fact]
    public void VisibleMonster_NeverGetsOpenedCorpseAnnotation()
    {
        // The annotation is corpse-only: a live monster row never carries
        // opened_by_bot_recently even if its GUID happens to be in the set.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B6u, CellId = 0xA9B60001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                { Guid = 0x401u, Name = "Drudge Skulker", Wcid = 19257u, Distance = 8f,
                  IsAttackable = true, HasRadarBlipColor = false, IsMonster = true },
            },
            OpenedCorpseGuids = new HashSet<uint> { 0x401u },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("Drudge Skulker", prompt);
        Assert.DoesNotContain("opened_by_bot_recently", prompt);
    }

    [Fact]
    public void IsSalientKind_IncludesCombatFeedback()
    {
        // The CombatFeedback "all swings evaded" event must wake the LLM so
        // it can disengage promptly instead of waiting for the 60s timeout.
        Assert.True(LlmGoalPolicy.IsSalientKind(EventKind.CombatFeedback));
    }

    private static WorldStateProjection RecallSelfWorld(
        uint landblock = 0xA9B3u,
        params VisibleObjectProjection[] visible)
        => new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = landblock,
                CellId = (landblock << 16) | 0x0001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = visible,
        };

    private static SightedRecallProjection Sighting(
        string name, uint? wcid, EntityKind kind, double ageSeconds,
        uint landblock = 0xA9B3u, float worldX = 0f, float worldY = 100f)
        => new SightedRecallProjection
        {
            Name = name, Wcid = wcid, Kind = kind, Landblock = landblock,
            WorldX = worldX, WorldY = worldY, AgeSeconds = ageSeconds,
        };

    private static string BuildPromptWithRecall(
        WorldStateProjection world, params SightedRecallProjection[] recall)
        => LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), null, null, null, null, null, recall);

    [Fact]
    public void RecentSightings_RendersMobOutOfView()
    {
        // Self at landblock 0xA9B3, local (0,0) → absolute origin
        // (0xA9*192, 0xB3*192). Place the sighting 100m due north of self in
        // ABSOLUTE coords (the frame NavGraph stores) so the row renders the
        // true relative bearing/distance.
        const float selfGX = 0xA9 * AcCoords.BlockLength;
        const float selfGY = 0xB3 * AcCoords.BlockLength;
        var world = RecallSelfWorld();
        var prompt = BuildPromptWithRecall(world,
            Sighting("The Chicken", 24937u, EntityKind.Mob, ageSeconds: 90,
                worldX: selfGX, worldY: selfGY + 100f));
        Assert.Contains("## Recently sighted (out of view)", prompt);
        Assert.Contains("The Chicken (kind=monster, last seen 90s ago, approx N ~100m)", prompt);
    }

    [Fact]
    public void RecentSightings_DistanceUsesAbsoluteFrame_NotWorldOrigin()
    {
        // Regression for the live frame-mismatch bug: self Position* is
        // landblock-LOCAL (0..192) but sightings are stored in ABSOLUTE
        // coords. The row must lift self into the absolute frame before
        // differencing; otherwise a nearby monster renders as tens of
        // thousands of metres (distance-from-world-origin, e.g. ~47525m).
        // Self sits at local (50,50) in 0xA9B3; a monster 30m due east must
        // read ~30m E, not the origin-relative magnitude.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
                PositionX = 50f, PositionY = 50f, PositionZ = 0f, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        const float selfGX = 0xA9 * AcCoords.BlockLength + 50f;
        const float selfGY = 0xB3 * AcCoords.BlockLength + 50f;
        var prompt = BuildPromptWithRecall(world,
            Sighting("Drudge Slinker", 19258u, EntityKind.Mob, ageSeconds: 25,
                worldX: selfGX + 30f, worldY: selfGY));
        Assert.Contains("Drudge Slinker (kind=monster, last seen 25s ago, approx E ~30m)", prompt);
    }

    [Fact]
    public void RecentSightings_NeutralPhrasing_NoPriorityLanguage()
    {
        var world = RecallSelfWorld();
        var prompt = BuildPromptWithRecall(world,
            Sighting("The Chicken", 24937u, EntityKind.Mob, ageSeconds: 30));
        Assert.Contains("Not recommendations", prompt);
        Assert.Contains("the bot assigns no priority", prompt);
        // No source-side urgency / hunting directive.
        Assert.DoesNotContain("go hunt", prompt);
        Assert.DoesNotContain("best monster", prompt);
        Assert.DoesNotContain("priority target", prompt);
    }

    [Fact]
    public void RecentSightings_ExcludesCurrentlyVisibleByWcid()
    {
        // The remembered monster is currently visible (same wcid) → it is
        // already in "## Visible nearby", so the recall section must not
        // re-advertise it → no header at all when nothing else remains.
        var world = RecallSelfWorld(visible: new VisibleObjectProjection
        { Guid = MobGuid, Name = "The Chicken", Wcid = 24937u, Distance = 5f, IsMonster = true });
        var prompt = BuildPromptWithRecall(world,
            Sighting("The Chicken", 24937u, EntityKind.Mob, ageSeconds: 20));
        Assert.DoesNotContain("## Recently sighted (out of view)", prompt);
    }

    [Fact]
    public void RecentSightings_ExcludesCurrentlyVisibleByName()
    {
        // Same identity by name when wcid is unknown on the sighting.
        var world = RecallSelfWorld(visible: new VisibleObjectProjection
        { Guid = MobGuid, Name = "Drudge Slinker", Wcid = 99u, Distance = 5f, IsMonster = true });
        var prompt = BuildPromptWithRecall(world,
            Sighting("Drudge Slinker", null, EntityKind.Mob, ageSeconds: 20));
        Assert.DoesNotContain("## Recently sighted (out of view)", prompt);
    }

    [Fact]
    public void RecentSightings_NpcRendersInOwnBlock_NotMonsterBlock()
    {
        // An NPC-kind remembered creature surfaces in the SEPARATE NPC recall
        // block (with its bearing, so the LLM can steer back to seek a
        // kill-task), NOT in the monster-recall block.
        const float selfGX = 0xA9 * AcCoords.BlockLength;
        const float selfGY = 0xB3 * AcCoords.BlockLength;
        var world = RecallSelfWorld();
        var prompt = BuildPromptWithRecall(world,
            Sighting("Town Crier", 1234u, EntityKind.NPC, ageSeconds: 20,
                worldX: selfGX, worldY: selfGY + 50f));
        Assert.DoesNotContain("Monsters you have seen", prompt); // monster block absent
        Assert.Contains("## Recently sighted NPCs (out of view)", prompt);
        Assert.Contains("Town Crier (kind=npc, last seen 20s ago, approx N ~50m)", prompt);
    }

    [Fact]
    public void RecentSightings_RendersBothBlocks_MonstersAndNpcsSeparately()
    {
        // Monsters and NPCs each get their own bounded block so an NPC-dense
        // town can never starve the monster recall.
        const float selfGX = 0xA9 * AcCoords.BlockLength;
        const float selfGY = 0xB3 * AcCoords.BlockLength;
        var world = RecallSelfWorld();
        var prompt = BuildPromptWithRecall(world,
            Sighting("The Chicken", 24937u, EntityKind.Mob, ageSeconds: 30,
                worldX: selfGX, worldY: selfGY + 100f),
            Sighting("Town Crier", 1234u, EntityKind.NPC, ageSeconds: 40,
                worldX: selfGX, worldY: selfGY - 80f));
        Assert.Contains("## Recently sighted (out of view)", prompt);
        Assert.Contains("The Chicken (kind=monster, last seen 30s ago, approx N ~100m)", prompt);
        Assert.Contains("## Recently sighted NPCs (out of view)", prompt);
        Assert.Contains("Town Crier (kind=npc, last seen 40s ago, approx S ~80m)", prompt);
    }

    [Fact]
    public void RecentSightings_DropsStaleBeyondTtl()
    {
        var world = RecallSelfWorld();
        var prompt = BuildPromptWithRecall(world,
            Sighting("The Chicken", 24937u, EntityKind.Mob, ageSeconds: 600)); // > 180s TTL
        Assert.DoesNotContain("## Recently sighted (out of view)", prompt);
    }

    [Fact]
    public void RecentSightings_DedupsByIdentity_KeepsMostRecent()
    {
        // Two sightings of the same identity (name+wcid+landblock) at
        // different ages collapse to one row, keeping the freshest.
        var world = RecallSelfWorld();
        var prompt = BuildPromptWithRecall(world,
            Sighting("The Chicken", 24937u, EntityKind.Mob, ageSeconds: 150),
            Sighting("The Chicken", 24937u, EntityKind.Mob, ageSeconds: 20));
        // Exactly one Chicken row, and it is the freshest (20s).
        var count = prompt.Split("The Chicken (kind=monster").Length - 1;
        Assert.Equal(1, count);
        Assert.Contains("last seen 20s ago", prompt);
        Assert.DoesNotContain("last seen 150s ago", prompt);
    }

    [Fact]
    public void RecentSightings_CapsRowCount()
    {
        var world = RecallSelfWorld();
        var many = Enumerable.Range(0, 9)
            .Select(i => Sighting($"Mob{i}", (uint)(1000 + i), EntityKind.Mob, ageSeconds: i + 1))
            .ToArray();
        var prompt = BuildPromptWithRecall(world, many);
        // Capped at 5 rows + an omission summary line.
        var rows = prompt.Split('\n').Count(l => l.Contains("(kind=monster"));
        Assert.Equal(5, rows);
        Assert.Contains("more remembered, not shown", prompt);
    }

    [Fact]
    public void RecentSightings_CrossLandblock_ShowsLandblock()
    {
        // A monster remembered in a DIFFERENT landblock surfaces its
        // landblock so the LLM can choose to travel there.
        var world = RecallSelfWorld(landblock: 0xA9B4u);
        var prompt = BuildPromptWithRecall(world,
            Sighting("Young Banderling", 22u, EntityKind.Mob, ageSeconds: 40, landblock: 0xA9B3u));
        Assert.Contains("Young Banderling (kind=monster", prompt);
        Assert.Contains("landblock 0xA9B3", prompt);
    }

    [Fact]
    public void RecentSightings_NullList_NoHeader()
    {
        var world = RecallSelfWorld();
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), null, null, null, null, null, null);
        Assert.DoesNotContain("## Recently sighted (out of view)", prompt);
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
    public async Task LlmGoalPolicy_LocationRecency_TalkCountsGuidFirstSelector()
    {
        // Regression (cp-2329): once the picker resolves a Talk target to a
        // concrete NPC the goal carries a guid, and Selector.ToString() prints
        // `guid=...` BEFORE `name="..."`, so the emitted Text is
        // `Talk target=guid=0x.. name="X" item= source=..`. The old name-only
        // regex (`target=name="X"`) required `target=` to be immediately
        // followed by `name="`, so it MISSED every guid-bearing Talk goal and
        // the section silently rendered `(none)` even during a real Talk loop.
        // The counter must extract the whole selector, key by guid, and still
        // DISPLAY the human name.
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
        // Seed 5 guid-FIRST Talk goals to the same NPC (picker-resolved shape).
        for (var i = 0; i < 5; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(6 - i),
                Kind = EventKind.GoalEmitted,
                GoalId = Guid.NewGuid(),
                Text = "Talk target=guid=0x80000B82 name=\"Flinrala Ryndmad\" item= source=llm:openai/gpt-4.1-mini",
            });
        }

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

        // The guid-bearing Talk loop must be COUNTED and displayed by NAME,
        // collapsed to one row — not silently dropped to (none).
        Assert.Contains("Flinrala Ryndmad: x5", lrBlock);
        Assert.DoesNotContain("recent Talk emissions: (none)", lrBlock);
        // Identity collapses by guid, so the guid token must NOT leak as a
        // separate row label when a name is available.
        Assert.DoesNotContain("guid=0x80000B82: x", lrBlock);
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
        Assert.Contains("(c) world-object USE", prompt);

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

    // ---- IsUnreachableTargetRepeat (cp-2274) ----

    private static WorldStateProjection BuildWorldWithVisible(
        params VisibleObjectProjection[] visible) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B3u, CellId = 0xA9B30001u,
            PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = visible,
    };

    private static void AppendNoLiveObjectFail(EventStream es, string targetName)
        => es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalFailed, GoalId = Guid.NewGuid(),
            Name = targetName, Text = $"Attack: selector resolved to no live object",
        });

    private static Goal AttackGoal(string name)
        => new() { Kind = GoalKind.Attack, Target = new Selector { Name = name } };

    [Fact]
    public void IsUnreachableTargetRepeat_TwoFailsOutOfPvs_Suppresses()
    {
        var es = new EventStream();
        AppendNoLiveObjectFail(es, "Drudge Skulker");
        AppendNoLiveObjectFail(es, "Drudge Skulker");
        var world = BuildWorldWithVisible(); // target not in view
        Assert.True(LlmGoalPolicy.IsUnreachableTargetRepeat(
            AttackGoal("Drudge Skulker"), world, es));
    }

    [Fact]
    public void IsUnreachableTargetRepeat_OneFail_AllowsRetry()
    {
        var es = new EventStream();
        AppendNoLiveObjectFail(es, "Drudge Skulker");
        var world = BuildWorldWithVisible();
        Assert.False(LlmGoalPolicy.IsUnreachableTargetRepeat(
            AttackGoal("Drudge Skulker"), world, es));
    }

    [Fact]
    public void IsUnreachableTargetRepeat_TargetInPvs_NeverSuppresses()
    {
        var es = new EventStream();
        AppendNoLiveObjectFail(es, "Drudge Skulker");
        AppendNoLiveObjectFail(es, "Drudge Skulker");
        var world = BuildWorldWithVisible(new VisibleObjectProjection
        {
            Guid = MobGuid, Name = "Drudge Skulker", Wcid = 7u,
            ItemType = 0x10u, Distance = 20f, IsCreature = true,
        });
        Assert.False(LlmGoalPolicy.IsUnreachableTargetRepeat(
            AttackGoal("Drudge Skulker"), world, es));
    }

    [Fact]
    public void IsUnreachableTargetRepeat_CombatDeferredReason_DoesNotCount()
    {
        var es = new EventStream();
        for (var i = 0; i < 3; i++)
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.GoalFailed, GoalId = Guid.NewGuid(),
                Name = "Drudge Skulker",
                Text = "Attack: combat deferred: self-health too low to re-engage — recover before attacking",
            });
        var world = BuildWorldWithVisible();
        Assert.False(LlmGoalPolicy.IsUnreachableTargetRepeat(
            AttackGoal("Drudge Skulker"), world, es));
    }

    [Fact]
    public void IsUnreachableTargetRepeat_DifferentTargetName_DoesNotLeak()
    {
        var es = new EventStream();
        AppendNoLiveObjectFail(es, "Young Banderling");
        AppendNoLiveObjectFail(es, "Young Banderling");
        var world = BuildWorldWithVisible();
        Assert.False(LlmGoalPolicy.IsUnreachableTargetRepeat(
            AttackGoal("Drudge Skulker"), world, es));
    }

    [Fact]
    public void IsUnreachableTargetRepeat_NonAttackKind_DoesNotFire()
    {
        var es = new EventStream();
        AppendNoLiveObjectFail(es, "Samuel");
        AppendNoLiveObjectFail(es, "Samuel");
        var world = BuildWorldWithVisible();
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Samuel" } };
        Assert.False(LlmGoalPolicy.IsUnreachableTargetRepeat(talk, world, es));
    }

    [Fact]
    public void IsUnreachableTargetRepeat_NoTargetName_DoesNotFire()
    {
        var es = new EventStream();
        AppendNoLiveObjectFail(es, "Drudge Skulker");
        AppendNoLiveObjectFail(es, "Drudge Skulker");
        var world = BuildWorldWithVisible();
        var goal = new Goal { Kind = GoalKind.Attack, Target = new Selector { Wcid = 7u } };
        Assert.False(LlmGoalPolicy.IsUnreachableTargetRepeat(goal, world, es));
    }

    [Fact]
    public void BuildUserPrompt_VisibleDoor_RendersClosedState()
    {
        var world = BuildWorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x71000001u, Name = "Training Area", Wcid = 200u,
            ItemType = 0x0u, Distance = 3.3f, IsDoor = true, IsOpenable = true,
            IsDoorOpen = false,
        });
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        // Anchor on the row signature (wcid=200) — the RULES text also
        // mentions "door open"/"door closed" generically, so assert on the row.
        Assert.Contains("(wcid=200 door closed", prompt);
        Assert.DoesNotContain("(wcid=200 door open", prompt);
    }

    [Fact]
    public void BuildUserPrompt_VisibleDoor_RendersOpenState()
    {
        var world = BuildWorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x71000001u, Name = "Training Area", Wcid = 200u,
            ItemType = 0x0u, Distance = 3.3f, IsDoor = true, IsOpenable = true,
            IsDoorOpen = true,
        });
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("(wcid=200 door open", prompt);
        Assert.DoesNotContain("(wcid=200 door closed", prompt);
    }

    [Fact]
    public void BuildUserPrompt_VisibleDoor_UnknownState_RendersNeitherOpenNorClosed()
    {
        var world = BuildWorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x71000001u, Name = "Mystery Door", Wcid = 200u,
            ItemType = 0x0u, Distance = 3.3f, IsDoor = true, IsOpenable = true,
            IsDoorOpen = null,
        });
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        // Row reads "... door open openable d=..." when open; the unknown row
        // reads "... door openable ..." (no open/closed token). "door openable"
        // contains "door open" as a substring, so anchor on the trailing space.
        Assert.Contains("(wcid=200 door openable", prompt);
        Assert.DoesNotContain("(wcid=200 door open openable", prompt);
        Assert.DoesNotContain("(wcid=200 door open d=", prompt);
        Assert.DoesNotContain("(wcid=200 door closed", prompt);
    }

    [Fact]
    public void BuildUserPrompt_NonDoorOpenable_NeverRendersDoorState()
    {
        // A chest with IsDoorOpen erroneously set must never read as a door.
        var world = BuildWorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x71000005u, Name = "Treasure Chest", Wcid = 9000u,
            ItemType = 0x200u, Distance = 4f, IsOpenable = true, IsChest = true,
            IsDoor = false, IsDoorOpen = true,
        });
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("(wcid=9000 door", prompt);
        Assert.DoesNotContain("door open", prompt[(prompt.IndexOf("wcid=9000", StringComparison.Ordinal))..]);
    }

    // ---- IsOptionalAttackOnBeatenKind (beaten-kind Attack veto) ----
    // The explicit LLM Attack path lacked the IsBeatenKind guard the autonomous
    // kill-commitment picker has, so a weak model could re-pick a KIND its own
    // ledger shows it loses to. These pin the veto AND its self-defense exempt.

    private static WorldStateProjection BuildWorldBeaten(
        IReadOnlyList<CombatHistoryEntry>? fullHistory, int? selfLevel,
        params VisibleObjectProjection[] visible)
    {
        var w = BuildWorldWithVisible(visible);
        return w with { CombatHistoryFull = fullHistory, Self = w.Self with { Level = selfLevel } };
    }

    private static CombatHistoryEntry[] LethalBeaten(string name, uint wcid)
        => new[] { new CombatHistoryEntry(name, wcid, Kills: 0, Deaths: 3, NearDeaths: 2,
            Fights: 5, LastOutcome: "death", Ineffective: 0) };

    [Fact]
    public void IsOptionalAttackOnBeatenKind_BeatenKindNotInView_Vetoes()
    {
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11);
        Assert.True(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Drudge Skulker"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_ActivelyHostileSameKind_Exempt()
    {
        // Self-defense: the beaten kind is in view AND attacking the bot now.
        // The veto must NOT fire — the Motor's flee/disengage reflexes own it.
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11,
            new VisibleObjectProjection
            {
                Guid = MobGuid, Name = "Drudge Skulker", Wcid = 7u,
                Distance = 3f, IsMonster = true, ObservedHostile = true,
            });
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Drudge Skulker"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_BeatenKindVisibleButNotHostile_Vetoes()
    {
        // In view but NOT hostile (a chosen engagement of a passive beaten
        // kind) is still optional -> veto.
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11,
            new VisibleObjectProjection
            {
                Guid = MobGuid, Name = "Drudge Skulker", Wcid = 7u,
                Distance = 6f, IsMonster = true, ObservedHostile = false,
            });
        Assert.True(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Drudge Skulker"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_NotBeatenKind_DoesNotFire()
    {
        // A kind the bot has killed (no losses) is not beaten -> never vetoed.
        var hist = new[] { new CombatHistoryEntry("Rabbit", 9u, Kills: 4, Deaths: 0,
            NearDeaths: 0, Fights: 4, LastOutcome: "kill", Ineffective: 0) };
        var world = BuildWorldBeaten(hist, selfLevel: 11);
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Rabbit"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_NoHistory_DoesNotFire()
    {
        var world = BuildWorldBeaten(fullHistory: null, selfLevel: 11);
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Drudge Skulker"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_NonAttackKind_DoesNotFire()
    {
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11);
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = "Drudge Skulker" } },
            world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_NoTargetName_DoesNotFire()
    {
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11);
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            new Goal { Kind = GoalKind.Attack, Target = new Selector { Name = "  " } }, world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_NonLethalLoss_RetestableWhenOutleveled()
    {
        // A NON-lethal beaten kind (no deaths, only near-deaths) becomes
        // re-testable once the bot out-levels its recorded max loss level —
        // delegated to IsBeatenKind. selfLevel 11 > MaxLossBotLevel 9 -> allowed.
        var hist = new[] { new CombatHistoryEntry("Mosswart", 8u, Kills: 0, Deaths: 0,
            NearDeaths: 2, Fights: 2, LastOutcome: "near-death", Ineffective: 0,
            MaxLossBotLevel: 9) };
        var world = BuildWorldBeaten(hist, selfLevel: 11);
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Mosswart"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_NonLethalLoss_StillBeatenWhenNotOutleveled()
    {
        // Same non-lethal record but the bot has NOT out-leveled it -> beaten.
        var hist = new[] { new CombatHistoryEntry("Mosswart", 8u, Kills: 0, Deaths: 0,
            NearDeaths: 2, Fights: 2, LastOutcome: "near-death", Ineffective: 0,
            MaxLossBotLevel: 11) };
        var world = BuildWorldBeaten(hist, selfLevel: 11);
        Assert.True(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Mosswart"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_LethalLoss_RetestableWhenOutleveled()
    {
        // A LETHAL beaten kind (deaths recorded) is re-testable on the EXPLICIT
        // LLM path once the bot out-levels the loss — the explicit order opts in
        // to the out-level re-test. selfLevel 12 > MaxLossBotLevel 9 -> allowed.
        var hist = new[] { new CombatHistoryEntry("Drudge Skulker", 7u, Kills: 0,
            Deaths: 3, NearDeaths: 1, Fights: 4, LastOutcome: "death", Ineffective: 0,
            MaxLossBotLevel: 9) };
        var world = BuildWorldBeaten(hist, selfLevel: 12);
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Drudge Skulker"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_LethalLoss_StillBeatenWhenNotOutleveled()
    {
        // Same lethal record but the bot has NOT out-leveled it (at or below the
        // loss level) -> still vetoed. Pins the at-level death loop.
        var hist = new[] { new CombatHistoryEntry("Drudge Skulker", 7u, Kills: 0,
            Deaths: 3, NearDeaths: 1, Fights: 4, LastOutcome: "death", Ineffective: 0,
            MaxLossBotLevel: 12) };
        var world = BuildWorldBeaten(hist, selfLevel: 11);
        Assert.True(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Drudge Skulker"), world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_WcidOnlySelector_Vetoes()
    {
        // A wcid-only Attack selector (no name) still matches the ledger by wcid
        // -> a beaten wcid is vetoed (closes the name-only bypass).
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11);
        var goal = new Goal { Kind = GoalKind.Attack, Target = new Selector { Wcid = 7u } };
        Assert.True(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(goal, world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_NameContainsSelector_Vetoes()
    {
        // A name_contains selector resolving to a beaten kind's name is vetoed
        // (closes the name-only bypass for the substring hook).
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11);
        var goal = new Goal
        {
            Kind = GoalKind.Attack,
            Target = new Selector { NameContains = "Drudge Skulker" },
        };
        Assert.True(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(goal, world));
    }

    [Fact]
    public void IsOptionalAttackOnBeatenKind_MixedVisibleSet_HostileAndPassiveSameName_Exempt()
    {
        // Name-only selectors cannot tell two identically named creatures apart:
        // if ANY same-name creature is actively hostile, the self-defense
        // exemption fires for the whole goal (documents the name-only limit).
        var world = BuildWorldBeaten(LethalBeaten("Drudge Skulker", 7u), selfLevel: 11,
            new VisibleObjectProjection
            {
                Guid = MobGuid, Name = "Drudge Skulker", Wcid = 7u,
                Distance = 3f, IsMonster = true, ObservedHostile = true,
            },
            new VisibleObjectProjection
            {
                Guid = MobGuid + 1, Name = "Drudge Skulker", Wcid = 7u,
                Distance = 9f, IsMonster = true, ObservedHostile = false,
            });
        Assert.False(LlmGoalPolicy.IsOptionalAttackOnBeatenKind(
            AttackGoal("Drudge Skulker"), world));
    }

    [Fact]
    public void IsBeatenKind_LethalLoss_PermanentForAutonomousButRetestableForExplicit()
    {
        // Default (autonomous) keeps a lethal kind beaten even when out-leveled;
        // the explicit opt-in re-tests it once the bot out-levels the loss.
        var hist = new[] { new CombatHistoryEntry("Drudge Skulker", 7u, Kills: 0,
            Deaths: 2, NearDeaths: 0, Fights: 2, LastOutcome: "death", Ineffective: 0,
            MaxLossBotLevel: 5) };
        Assert.True(LlmGoalPolicy.IsBeatenKind(hist, wcid: null, "Drudge Skulker",
            currentLevel: 20));
        Assert.False(LlmGoalPolicy.IsBeatenKind(hist, wcid: null, "Drudge Skulker",
            currentLevel: 20, lethalRetestableWhenOutleveled: true));
    }

    // ---- Helpers ----

    private const uint SelfGuid = 0x50000005;
    private const uint NpcGuid  = 0x90000010;
    private const uint MobGuid  = 0x90000020;
    private const uint ItemGuid = 0x80000030;

    private static WorldStateProjection BuildVisibleWorld(params VisibleObjectProjection[] visible) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0x8602u, CellId = 0x86020001u,
            PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = visible,
    };

    private static VisibleObjectProjection CivilianNpc(uint guid) => new()
    {
        Guid = guid,
        Name = $"Npc {guid:X8}",
        Distance = 3f,
        IsCreature = true,
    };

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
    public void IsInventoryUseRecentlyDispatched_ItemOnSelfShape_MatchesAfterEcho()
    {
        // cp-2417 contract: the exact live (gpt-4o) goal shape that looped 45x —
        // Use{ target = self (guid + name), item = "Letter From Home" }, dispatched
        // by the Motor as USEWITHTARGET(source=letter, target=self). That path now
        // emits the InventoryItemUsed echo (this test's es.Append stands in for
        // that Motor echo), so the dedup must drop the repeat by the ITEM name even
        // though the TARGET is the player, not the item.
        var es = new EventStream();
        es.Append(InvUsed("Letter From Home", LetterWcid, LetterGuid));

        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Guid = 0x500000A6u, Name = "Llama2394a" },
            Item = new Selector { Name = "Letter From Home" },
        };
        Assert.True(LlmGoalPolicy.IsInventoryUseRecentlyDispatched(goal, es));
    }

    [Fact]
    public void IsInventoryUseRecentlyDispatched_ItemOnSelfShape_NoEcho_DoesNotMatch()
    {
        // The pre-cp-2417 bug: with NO InventoryItemUsed echo (the USEWITHTARGET
        // path used to emit none), the same item-on-self goal is NOT deduped —
        // which is exactly why the bot looped. Guards that the dedup depends on the
        // Motor echo this slice adds.
        var es = new EventStream();

        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Guid = 0x500000A6u, Name = "Llama2394a" },
            Item = new Selector { Name = "Letter From Home" },
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

    // ---- 2026-06-04 Stationary world-object USE loop-break ----
    //
    // Holtburg door-loop: a fresh L1 bot looped Use{Door} 8x against an
    // indoor door the motor OPENS (UseDone ok) but cannot path the bot
    // THROUGH to the adjacent cell (indoor-nav 0 waypoints across the
    // cell boundary). The Use succeeds (no ActionRejected) and is not an
    // inventory item, so the rejection + inventory-USE guards both miss
    // it; the recency prompt section surfaces the repeat but a weak model
    // loops anyway. IsStationaryWorldUseRepeat tracks the bot's OWN Use
    // identity + self cell/position and drops a STATIONARY repeat so the
    // bot defers to the fallback instead of re-locking the dead target.

    private static LlmGoalPolicy MakeStationaryUsePolicy()
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("unused") }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        return new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo());
    }

    private static WorldStateProjection WorldAt(uint landblock, uint cell, float x, float y) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = landblock, CellId = cell,
            PositionX = x, PositionY = y, PositionZ = 0, HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = Array.Empty<VisibleObjectProjection>(),
    };

    // WorldAt with a single visible NPC of the given name+guid, for the roving
    // Talk-loop name-resolution tests (the bot stays in the same landblock/cell
    // but drifts in x,y while the visible NPC's guid stays stable).
    private static WorldStateProjection WorldWithNpc(float x, float y, string npcName, uint npcGuid) =>
        WorldAt(0x8602u, 0x860201B3u, x, y) with
        {
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = npcGuid, Name = npcName, Distance = 1.0f, IsCreature = true,
                },
            },
        };

    private static StreamEvent InvAdded(string name) => new()
    {
        Sequence = -1, Utc = DateTimeOffset.UtcNow,
        Kind = EventKind.InventoryItemAdded, Name = name,
    };

    [Fact]
    public void StationaryWorldUseRepeat_DropsThirdSameDoorUse_WhenBotHasNotMoved()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B40019u, 106.5f, 31.4f);
        var goal = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Door" } };

        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es)); // 1st seen
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es)); // 2nd seen
        Assert.True(policy.IsStationaryWorldUseRepeat(goal, world, es));  // 3rd -> stuck
        Assert.True(policy.IsStationaryWorldUseRepeat(goal, world, es));  // stays stuck until movement
    }

    [Fact]
    public void StationaryWorldUseRepeat_ResetsWhenBotChangesCell()
    {
        // Legit indoor corridor of doors all named "Door" (intra-landblock,
        // no LandblockChanged): the bot WALKS between them so its cell
        // changes each time -> never treated as stuck.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var goal = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Door" } };

        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es)); // moved cell
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B4001Bu, 0, 0), es)); // moved cell
    }

    [Fact]
    public void StationaryWorldUseRepeat_ResetsWhenBotMovesPastEpsilon()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var goal = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Door" } };

        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 0f, 0f), es));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 5f, 0f), es)); // moved > epsilon
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 10f, 0f), es));
    }

    [Fact]
    public void StationaryWorldUseRepeat_JitterWithinEpsilon_StillTrips()
    {
        // Sub-epsilon position jitter from server broadcasts must NOT
        // reset the count — the bot is effectively stationary.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var goal = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Door" } };

        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 0.0f, 0.0f), es));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 0.3f, 0.2f), es)); // <0.75u
        Assert.True(policy.IsStationaryWorldUseRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 0.1f, 0.4f), es));
    }

    [Fact]
    public void StationaryWorldUseRepeat_ExemptsWhenInventoryChanges()
    {
        // Looting a corpse/chest in place: the Use yields InventoryItemAdded
        // each time, so the bot IS making progress even though it has not
        // moved -> must never be suppressed.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B40019u, 0, 0);
        var goal = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Corpse" } };

        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
        es.Append(InvAdded("Loot 1"));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
        es.Append(InvAdded("Loot 2"));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
    }

    [Fact]
    public void StationaryWorldUseRepeat_IgnoresInventoryUseGoals()
    {
        // goal.Item set => inventory / use-with-target; owned by
        // IsInventoryUseRecentlyDispatched, not this guard.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B40019u, 0, 0);
        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Name = "Chest" },
            Item = new Selector { Name = "Key" },
        };

        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
    }

    [Fact]
    public void StationaryWorldUseRepeat_IgnoresNonUseGoals()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B40019u, 0, 0);
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "NPC" } };

        Assert.False(policy.IsStationaryWorldUseRepeat(talk, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(talk, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(talk, world, es));
    }

    [Fact]
    public void StationaryWorldUseRepeat_DistinctGuidTargets_DoNotCollapse()
    {
        // When the LLM emits guids, two distinct doors keep distinct keys
        // and alternating between them never trips either.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B40019u, 0, 0);
        var doorA = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x7A9B4019u } };
        var doorB = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x7A9B401Au } };

        Assert.False(policy.IsStationaryWorldUseRepeat(doorA, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(doorB, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(doorA, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(doorB, world, es));
    }

    [Fact]
    public void StationaryWorldUseRepeat_UnderspecifiedSelector_NotGuarded()
    {
        // name_contains / wcid / mask only -> no stable per-object identity
        // -> never guarded (returns false even when repeated in place).
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B40019u, 0, 0);
        var goal = new Goal { Kind = GoalKind.Use, Target = new Selector { NameContains = "oor" } };

        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
        Assert.False(policy.IsStationaryWorldUseRepeat(goal, world, es));
    }

    // ---- Landblock world-object USE churn loop-break (cp-2354) ----
    // The multi-door TOUR the stationary guard misses: the bot walks between
    // doors within ONE landblock (cell changes each time) and re-Uses them,
    // never egressing. The landblock-scoped per-target counter catches it.

    [Fact]
    public void LandblockWorldUseChurn_FiresOnThirdSameDoorUse_AcrossCellMoves()
    {
        // Live fixation: the bot re-Used Door guid=0x7A9B403A while touring the
        // sanctuary (cells change, landblock 0xA9B4 does not). The stationary
        // guard resets on each move; this guard counts across moves.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var doorA = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x7A9B403Au } };
        var doorB = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x7A9B4017u } };

        Assert.False(policy.IsLandblockWorldUseChurn(doorA, WorldAt(0xA9B4u, 0xA9B40170u, 0, 0), es)); // A #1
        Assert.False(policy.IsLandblockWorldUseChurn(doorB, WorldAt(0xA9B4u, 0xA9B40154u, 5, 0), es)); // B #1 (moved)
        Assert.False(policy.IsLandblockWorldUseChurn(doorA, WorldAt(0xA9B4u, 0xA9B40170u, 0, 0), es)); // A #2 (moved back)
        Assert.True(policy.IsLandblockWorldUseChurn(doorA, WorldAt(0xA9B4u, 0xA9B40170u, 0, 0), es));  // A #3 -> fire
    }

    [Fact]
    public void LandblockWorldUseChurn_DistinctDoorSequence_DoesNotFire_FirstUseForgiveness()
    {
        // A legitimate multi-door exit: D1 -> new cell -> D2 -> new cell -> D3.
        // Each DISTINCT door is Used once (count 1), so the guard never fires
        // even though all three are in the same landblock with no egress yet.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var d1 = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x1001u } };
        var d2 = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x1002u } };
        var d3 = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x1003u } };

        Assert.False(policy.IsLandblockWorldUseChurn(d1, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es));
        Assert.False(policy.IsLandblockWorldUseChurn(d2, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es));
        Assert.False(policy.IsLandblockWorldUseChurn(d3, WorldAt(0xA9B4u, 0xA9B4001Bu, 0, 0), es));
    }

    [Fact]
    public void LandblockWorldUseChurn_LatchesSuppressed_NoOneInNLeak()
    {
        // Once a target trips the threshold it LATCHES suppressed for the
        // episode, so a picker re-arrival + LLM re-emit cannot leak one Use per
        // cycle. A landblock change is the only way out.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var door = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x2001u } };

        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es)); // #1
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es)); // #2
        Assert.True(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Bu, 0, 0), es));  // #3 -> fire
        Assert.True(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Cu, 0, 0), es));  // stays suppressed
        Assert.True(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Du, 0, 0), es));  // stays suppressed
        // Landblock change = egress: episode resets, the door is fresh again.
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xB1C2u, 0xB1C20001u, 0, 0), es));
    }

    [Fact]
    public void LandblockWorldUseChurn_LandblockChange_ResetsEpisode()
    {
        // Reaching the threshold requires the SAME landblock throughout; a
        // landblock change mid-count (genuine egress) restarts the episode.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var door = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x3001u } };

        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es)); // #1 in 0xA9B4
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es)); // #2 in 0xA9B4
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xAAAAu, 0xAAAA0001u, 0, 0), es)); // egressed -> #1 in new lb
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xAAAAu, 0xAAAA0002u, 0, 0), es)); // #2 in new lb
    }

    [Fact]
    public void LandblockWorldUseChurn_InventoryChange_ResetsEpisode()
    {
        // A productive key-Use / loot (InventoryItemAdded/Removed) is progress,
        // so it resets the episode just like the stationary guard.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var door = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x4001u } };

        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es)); // #1
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es)); // #2
        es.Append(InvAdded("Key turned / loot"));
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Bu, 0, 0), es)); // reset -> #1
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Cu, 0, 0), es)); // #2
    }

    [Fact]
    public void LandblockWorldUseChurn_PersistentCount_LatchesSameObjectDespiteInterleavedInventoryChanges()
    {
        // cp-2371 loophole: re-Using the SAME object while interleaving an
        // UNRELATED productive Pickup between repeats kept resetting the
        // per-episode count below the threshold, so a barren same-object loop
        // (e.g. Use door, Use door, Pickup item, repeat) never broke. The
        // CUMULATIVE per-key count survives the inventory resets and latches the
        // object once it has been Used PersistentWorldUseChurnThreshold (5) times
        // in the landblock, regardless of the interleaved inventory churn.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var door = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x4001u } };
        var cell = WorldAt(0xA9B4u, 0xA9B40170u, 0, 0);

        Assert.False(policy.IsLandblockWorldUseChurn(door, cell, es)); // cumulative 1
        Assert.False(policy.IsLandblockWorldUseChurn(door, cell, es)); // cumulative 2
        es.Append(InvAdded("unrelated apple picked up"));              // resets per-episode count
        Assert.False(policy.IsLandblockWorldUseChurn(door, cell, es)); // cumulative 3
        Assert.False(policy.IsLandblockWorldUseChurn(door, cell, es)); // cumulative 4
        es.Append(InvAdded("another unrelated pickup"));               // resets per-episode count again
        Assert.True(policy.IsLandblockWorldUseChurn(door, cell, es));  // cumulative 5 -> latch
        es.Append(InvAdded("more unrelated pickups cannot un-latch")); // stays suppressed across resets
        Assert.True(policy.IsLandblockWorldUseChurn(door, cell, es));
    }

    [Fact]
    public void LandblockWorldUseChurn_PersistentCount_ResetsOnLandblockChange()
    {
        // The cumulative count is per-LANDBLOCK: genuine egress to a new
        // landblock wipes it, so a door Used in the next area starts fresh.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var door = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x4001u } };

        for (int i = 0; i < 4; i++)
            policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B40170u, 0, 0), es);
        // Egress to a NEW landblock -> cumulative count wiped.
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xB1C2u, 0xB1C20001u, 0, 0), es));
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xB1C2u, 0xB1C20001u, 0, 0), es));
    }

    [Fact]
    public void LandblockWorldUseChurn_NameOnlyDoor_DistinctCells_DoesNotFire()
    {
        // Reviewer-caught false-positive: the LLM commonly emits name-only
        // {"name":"Door"} (no guid). A LEGITIMATE corridor of DISTINCT doors all
        // named "Door" must NOT collapse onto one count. They are Used from
        // DISTINCT cells, so the name is disambiguated by self cell and each gets
        // first-use forgiveness — even past the threshold count of emissions.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var door = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Door" } };

        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es)); // door 1
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es)); // door 2
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Bu, 0, 0), es)); // door 3
        Assert.False(policy.IsLandblockWorldUseChurn(door, WorldAt(0xA9B4u, 0xA9B4001Cu, 0, 0), es)); // door 4
    }

    [Fact]
    public void LandblockWorldUseChurn_NameOnlyDoor_SameCell_Fires()
    {
        // The live bug: the LLM emits name-only {"name":"Door"} and the picker
        // parks the bot at the SAME door (same cell) to Use it again and again.
        // The name+cell key keeps the count, so the 3rd same-cell re-Use fires —
        // even though distinct-cell corridor doors (test above) never do.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var door = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Door" } };
        var cell = WorldAt(0xA9B4u, 0xA9B40170u, 0, 0);

        Assert.False(policy.IsLandblockWorldUseChurn(door, cell, es)); // #1
        Assert.False(policy.IsLandblockWorldUseChurn(door, cell, es)); // #2
        Assert.True(policy.IsLandblockWorldUseChurn(door, cell, es));  // #3 -> fire
        Assert.True(policy.IsLandblockWorldUseChurn(door, cell, es));  // stays suppressed
    }

    [Fact]
    public void LandblockWorldUseChurn_NotGuarded_ForNonUse_ItemUse_AndUnderspecifiedSelector()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B40019u, 0, 0);
        // Talk is not a Use.
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x5001u } };
        // Use WITH an item is owned by the inventory-Use dedup, not this guard.
        var itemUse = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x5002u }, Item = new Selector { Name = "Key" } };
        // Under-specified selector (no guid/name) has no stable identity.
        var vague = new Goal { Kind = GoalKind.Use, Target = new Selector { NameContains = "oor" } };

        for (int i = 0; i < 4; i++)
        {
            Assert.False(policy.IsLandblockWorldUseChurn(talk, world, es));
            Assert.False(policy.IsLandblockWorldUseChurn(itemUse, world, es));
            Assert.False(policy.IsLandblockWorldUseChurn(vague, world, es));
        }
    }

    // ---- Landblock DISTINCT-object world-Use churn (cp-2359) ----
    // A barren TOUR of MANY DIFFERENT world objects in one landblock (the live
    // chest tour: 10 distinct static chests Used, 0 egress) that the per-target
    // counter never catches because each object is Used only once or twice.

    [Fact]
    public void LandblockDistinctUseChurn_FiresOnFifthDistinctObject()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        // 4 DISTINCT objects each Used once in one landblock -> forgiveness, no fire.
        for (uint g = 0x6001u; g <= 0x6004u; g++)
            Assert.False(policy.IsLandblockWorldUseChurn(
                new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = g } },
                WorldAt(0xA9B4u, 0xA9B40000u + (g & 0xFFu), 0, 0), es));
        // 5th DISTINCT object -> distinct-tour trip.
        Assert.True(policy.IsLandblockWorldUseChurn(
            new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x6005u } },
            WorldAt(0xA9B4u, 0xA9B40055u, 0, 0), es));
        // 6th NEW distinct object -> episode latched, still dropped.
        Assert.True(policy.IsLandblockWorldUseChurn(
            new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x6006u } },
            WorldAt(0xA9B4u, 0xA9B40066u, 0, 0), es));
    }

    [Fact]
    public void LandblockDistinctUseChurn_DoesNotBlockNonUseWhenLatched()
    {
        // The latch defers world-object Use only — Attack/Pickup must still pass
        // (the bot can commit to a monster that appears mid-tour).
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        for (uint g = 0x6101u; g <= 0x6105u; g++) // trip the distinct latch
            policy.IsLandblockWorldUseChurn(
                new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = g } },
                WorldAt(0xA9B4u, 0xA9B40000u + (g & 0xFFu), 0, 0), es);
        // Attack is not a Use — never guarded, even with the latch active.
        Assert.False(policy.IsLandblockWorldUseChurn(
            new Goal { Kind = GoalKind.Attack, Target = new Selector { Guid = 0x61FFu } },
            WorldAt(0xA9B4u, 0xA9B401FFu, 0, 0), es));
    }

    [Fact]
    public void LandblockDistinctUseChurn_InventoryChangeResetsDistinctCount()
    {
        // A productive loot (InventoryItemAdded) mid-tour resets the episode, so a
        // real loot room of several PRODUCTIVE containers never latches.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        for (uint g = 0x7001u; g <= 0x7004u; g++) // 4 distinct, no fire
            Assert.False(policy.IsLandblockWorldUseChurn(
                new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = g } },
                WorldAt(0xA9B4u, 0xA9B40000u + (g & 0xFFu), 0, 0), es));
        es.Append(InvAdded("Looted gold"));
        // distinct count restarts; the next 4 distinct objects do NOT latch.
        for (uint g = 0x7005u; g <= 0x7008u; g++)
            Assert.False(policy.IsLandblockWorldUseChurn(
                new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = g } },
                WorldAt(0xA9B4u, 0xA9B40000u + (g & 0xFFu), 0, 0), es));
    }

    [Fact]
    public void LandblockDistinctUseChurn_LandblockChangeResetsDistinctCount()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        for (uint g = 0x8001u; g <= 0x8004u; g++) // 4 distinct in 0xA9B4
            Assert.False(policy.IsLandblockWorldUseChurn(
                new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = g } },
                WorldAt(0xA9B4u, 0xA9B40000u + (g & 0xFFu), 0, 0), es));
        // Egress to a new landblock resets; 4 distinct there do not latch.
        for (uint g = 0x8005u; g <= 0x8008u; g++)
            Assert.False(policy.IsLandblockWorldUseChurn(
                new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = g } },
                WorldAt(0xCCCCu, 0xCCCC0000u + (g & 0xFFu), 0, 0), es));
    }

    [Fact]
    public void BuildUserPrompt_LocalActivityCapsule_RendersWhenChurnLatched()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            localUseChurn: (Distinct: 6, Latched: true));
        Assert.Contains("## Local activity", prompt);
        Assert.Contains("6 distinct world objects", prompt);
        Assert.Contains("deferring further bare world-object Use", prompt);
    }

    [Fact]
    public void BuildUserPrompt_LocalActivityCapsule_OmittedWhenNotLatchedOrNull()
    {
        var pNull = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            localUseChurn: null);
        Assert.DoesNotContain("## Local activity", pNull);
        var pUnlatched = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            localUseChurn: (Distinct: 3, Latched: false));
        Assert.DoesNotContain("## Local activity", pUnlatched);
    }

    // ---- Stationary NPC Talk loop-break (exhausted conversation) ----
    //
    // A weak model re-emits Talk{same NPC} on a dead-end quest NPC whose
    // canned dialog never changes; the server replies identically each time
    // so no inventory/movement signals progress. IsExhaustedNpcTalkRepeat
    // tracks the bot's OWN Talk identity + self cell/position + inventory
    // events (NO dialog text), mirroring the world-object USE guard, and
    // drops the stationary repeat once it has fired NpcTalkRepeatThreshold(4)
    // times.

    [Fact]
    public void ExhaustedNpcTalk_DropsFourthSameTalk_WhenBotHasNotMoved()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 138.99f, 7.37f);
        var goal = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Apprentice" } };

        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es)); // 1st seen
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es)); // 2nd seen
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es)); // 3rd seen
        Assert.True(policy.IsExhaustedNpcTalkRepeat(goal, world, es));  // 4th -> stuck
        Assert.True(policy.IsExhaustedNpcTalkRepeat(goal, world, es));  // stays stuck until movement
    }

    [Fact]
    public void ExhaustedNpcTalk_ResetsWhenBotMovesCell()
    {
        // Walking between distinct NPCs (cell changes) never trips even if
        // both are named the same.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var goal = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Apprentice" } };

        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es)); // moved cell
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B4001Bu, 0, 0), es)); // moved cell
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B4001Cu, 0, 0), es)); // moved cell
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B4001Du, 0, 0), es)); // moved cell
    }

    [Fact]
    public void ExhaustedNpcTalk_ResetsWhenBotMovesPastEpsilon()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var goal = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Apprentice" } };

        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 0f, 0f), es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 5f, 0f), es)); // moved > epsilon
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 10f, 0f), es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, WorldAt(0xA9B4u, 0xA9B40019u, 15f, 0f), es));
    }

    [Fact]
    public void ExhaustedNpcTalk_ExemptsWhenInventoryChanges()
    {
        // A real quest turn-in: an inventory change each Talk (token
        // consumed / reward granted) -> progress -> must never be suppressed.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var goal = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Master" } };

        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        es.Append(InvAdded("Reward 1"));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        es.Append(InvAdded("Reward 2"));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        es.Append(InvAdded("Reward 3"));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        es.Append(InvAdded("Reward 4"));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
    }

    [Fact]
    public void ExhaustedNpcTalk_DistinctNpcTargets_DoNotCollapse()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var npcA = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002625u } };
        var npcB = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80001234u } };

        Assert.False(policy.IsExhaustedNpcTalkRepeat(npcA, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(npcB, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(npcA, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(npcB, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(npcA, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(npcB, world, es));
    }

    // ---- Multi-NPC Talk-churn loop-break (cp-2344) ----
    //
    // IsExhaustedNpcTalkRepeat resets on every target change, so a referral
    // PING-PONG between two NPCs (verified live: Scribe x5 / Renald x3, in
    // range, 0 inventory, ## Recent Talk capsule present but ignored) slips
    // past it forever. IsMultiNpcTalkChurn tracks a STATIONARY no-progress,
    // no-dialog-novelty cycle over <=2 distinct targets and fires on the
    // MultiNpcTalkChurnStaleThreshold'th stale repeat. Server progress
    // (inventory/landblock/self-progression), movement, a genuinely new line of
    // dialog, or a frontier that grows past 2 targets all reset/abandon it.

    [Fact]
    public void MultiNpcTalkChurn_FiresOnTwoNpcAlternation_WithNoProgress()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var npcA = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002625u } };
        var npcB = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80001234u } };

        Assert.False(policy.IsMultiNpcTalkChurn(npcA, world, es)); // episode start
        Assert.False(policy.IsMultiNpcTalkChurn(npcB, world, es)); // stale 1
        Assert.False(policy.IsMultiNpcTalkChurn(npcA, world, es)); // stale 2
        Assert.True(policy.IsMultiNpcTalkChurn(npcB, world, es));  // stale 3 -> fire
        Assert.True(policy.IsMultiNpcTalkChurn(npcA, world, es));  // stays fired
    }

    [Fact]
    public void MultiNpcTalkChurn_NeverFires_WhenDialogIsNovelEachTalk()
    {
        // QUESTING SAFETY: a legitimate referral chain advances the dialog each
        // Talk (new server text, even with no item or movement). New dialog
        // novelty resets the stale streak, so a productive chain is NEVER
        // suppressed — only a re-greeting cycle with no new content fires.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var npcA = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002625u } };
        var npcB = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80001234u } };

        var goals = new[] { npcA, npcB, npcA, npcB, npcA, npcB, npcA, npcB };
        for (var i = 0; i < goals.Length; i++)
        {
            // The server reply to the PREVIOUS Talk is in the buffer; each is a
            // genuinely new line (the conversation is advancing).
            es.Append(NpcDialog($"advancing dialogue line number {i}"));
            Assert.False(policy.IsMultiNpcTalkChurn(goals[i], world, es),
                $"must not fire on novel-dialog chain at step {i}");
        }
    }

    [Fact]
    public void MultiNpcTalkChurn_NeverFires_WhenRepeatedGreetingsButInventoryProgresses()
    {
        // A real turn-in chain: each Talk consumes/grants an item. Inventory
        // progress resets the episode; never suppressed.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var npcA = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002625u } };
        var npcB = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80001234u } };

        var goals = new[] { npcA, npcB, npcA, npcB, npcA, npcB };
        foreach (var g in goals)
        {
            es.Append(InvAdded("Quest Token"));
            Assert.False(policy.IsMultiNpcTalkChurn(g, world, es));
        }
    }

    [Fact]
    public void MultiNpcTalkChurn_DoesNotFire_ForSingleNpcRepeat()
    {
        // A single-NPC fixation is the OTHER guard's job; this one requires >=2
        // distinct targets so the two never double-count.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var npcA = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002625u } };

        for (var i = 0; i < 8; i++)
            Assert.False(policy.IsMultiNpcTalkChurn(npcA, world, es));
    }

    // ---- IsRovingNpcTalkLoop (cp-2365: roving single-NPC Talk loop) ----

    private static readonly uint SamuelGuid = 0x800076B6u;

    [Fact]
    public void RovingNpcTalkLoop_Fires_OnMovingSameNpcLoop_WithNoProgress()
    {
        // The bot walks up to the SAME NPC each Talk (position changes every
        // time) — which IsExhaustedNpcTalkRepeat (resets on movement) and
        // IsMultiNpcTalkChurn (needs >=2 targets) both miss. With no dialog
        // novelty and no progress, the roving guard fires at the stale
        // threshold (4 stale after the episode start).
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var npc = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = SamuelGuid } };

        Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 10, -30), es)); // start
        Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 12, -28), es)); // moved, stale 1
        Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 9, -31), es));  // moved, stale 2
        Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 14, -27), es)); // moved, stale 3
        Assert.True(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 11, -29), es));  // moved, stale 4 -> fire
    }

    [Fact]
    public void RovingNpcTalkLoop_NeverFires_WhenDialogIsNovelEachTalk()
    {
        // QUESTING SAFETY: an advancing single-NPC conversation emits a new line
        // each Talk; dialog novelty resets the stale streak, so a productive
        // multi-exchange dialog is NEVER suppressed even while the bot moves.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var npc = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = SamuelGuid } };

        for (var i = 0; i < 8; i++)
        {
            es.Append(NpcDialog($"new training instruction {i}"));
            Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, i, -i), es),
                $"must not fire on a novel-dialog single-NPC chain at step {i}");
        }
    }

    [Fact]
    public void RovingNpcTalkLoop_RawBackstop_Fires_OnLongNovelButUnproductiveLoop()
    {
        // Live (15x Worcer): an NPC that cycles VARIED canned lines keeps
        // StaleTalks at 0 (novel each Talk) and slips the stale guard, so the bot
        // re-Talks it for minutes until the slow dwell-egress. The raw backstop
        // fires after RovingNpcTalkLoopRawThreshold (8) consecutive same-NPC
        // Talks with NO inventory/landblock/self-progress (those reset the whole
        // streak), regardless of dialog novelty. The start call seeds the streak;
        // TotalTalks climbs from the next call, firing on the 9th consecutive.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var npc = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = SamuelGuid } };

        for (var i = 0; i < 8; i++)
        {
            es.Append(NpcDialog($"varied canned line {i}"));
            Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, i, -i), es),
                $"must not fire before the raw threshold at step {i}");
        }
        es.Append(NpcDialog("varied canned line 8"));
        Assert.True(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 8, -8), es),
            "raw backstop must fire on a long novel-but-unproductive single-NPC Talk loop");
    }

    [Fact]
    public void RovingNpcTalkLoop_RawBackstop_DoesNotFire_WhenProgressKeepsResetting()
    {
        // Each Talk grants an item (turn-in / reward) -> the streak resets every
        // time, so the raw backstop NEVER accumulates: a genuinely productive
        // long exchange is not suppressed.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var npc = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = SamuelGuid } };

        for (var i = 0; i < 12; i++)
        {
            es.Append(InvAdded("Reward Token"));
            Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, i, 0), es),
                $"raw backstop must not fire while progress resets the streak at step {i}");
        }
    }

    [Fact]
    public void RovingNpcTalkLoop_NeverFires_WhenInventoryProgresses()
    {
        // A turn-in / item-grant resets the episode — a productive single-NPC
        // exchange is never suppressed.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var npc = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = SamuelGuid } };

        for (var i = 0; i < 8; i++)
        {
            es.Append(InvAdded("Leather Leggings"));
            Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, i, 0), es));
        }
    }

    [Fact]
    public void RovingNpcTalkLoop_ResetsOnDifferentNpc()
    {
        // Switching to a different NPC restarts the streak (per-NPC), so an
        // A,A,A -> B sequence does not carry A's count into B.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var a = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = SamuelGuid } };
        var b = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x800076A4u } };

        Assert.False(policy.IsRovingNpcTalkLoop(a, WorldAt(0x8602u, 0x860201B3u, 1, 0), es)); // start A
        Assert.False(policy.IsRovingNpcTalkLoop(a, WorldAt(0x8602u, 0x860201B3u, 2, 0), es)); // A stale 1
        Assert.False(policy.IsRovingNpcTalkLoop(a, WorldAt(0x8602u, 0x860201B3u, 3, 0), es)); // A stale 2
        Assert.False(policy.IsRovingNpcTalkLoop(b, WorldAt(0x8602u, 0x860201B3u, 4, 0), es)); // switch -> restart on B
        Assert.False(policy.IsRovingNpcTalkLoop(b, WorldAt(0x8602u, 0x860201B3u, 5, 0), es)); // B stale 1 (not fired)
    }

    [Fact]
    public void RovingNpcTalkLoop_RecoversAfterFiring_NotPermanentlySuppressed()
    {
        // After firing, the episode resets so suppression is never PERMANENT: a
        // later re-attempt at the same NPC starts a fresh streak (the bot gets to
        // re-probe) and only re-fires if it loops again. A legitimately advancing
        // NPC would produce progress/novelty on the re-attempt and clear.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var npc = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = SamuelGuid } };

        // First loop: start + 4 stale -> fire on the 5th.
        for (var i = 0; i < 4; i++)
            Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, i, 0), es));
        Assert.True(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 9, 0), es)); // fire #1

        // Immediately after firing, the NEXT same-NPC Talk is a fresh re-probe,
        // NOT an instant re-drop (recovery path; no permanent suppression).
        Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 10, 0), es));
        Assert.False(policy.IsRovingNpcTalkLoop(npc, WorldAt(0x8602u, 0x860201B3u, 11, 0), es)); // stale 1 again
    }

    [Fact]
    public void RovingNpcTalkLoop_DoesNotFire_NameOnlyTarget_WhenNoVisibleMatch()
    {
        // A name-only Talk target that matches NO visible object cannot be
        // resolved to a stable guid, so the guard stays out — the bot is not
        // standing at the NPC (e.g. it is still walking toward it).
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var nameOnly = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Town Crier" } };

        for (var i = 0; i < 10; i++)
            Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldAt(0x8602u, 0x860201B3u, i, 0), es));
    }

    [Fact]
    public void RovingNpcTalkLoop_Fires_NameOnlyTarget_ResolvedToVisibleNpc()
    {
        // cp-2412: an LLM Talk goal is NAME-only (the Motor resolves the guid
        // downstream), which made this guard sit out every LLM Talk loop. It now
        // re-keys the name to the nearest visible object of that name, so a
        // roving loop on ONE silent NPC fires at the stale threshold instead of
        // slipping past (live: the bot re-Talked one silent NPC ~10x). The bot
        // drifts (x,y change) but the resolved guid is stable, so the streak
        // accrues across movement just like the guid-backed case.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var nameOnly = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Alcott" } };
        const uint alcottGuid = 0x8000ACE8u;

        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(10, -30, "Alcott", alcottGuid), es)); // start
        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(12, -28, "Alcott", alcottGuid), es)); // stale 1
        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(9, -31, "Alcott", alcottGuid), es));  // stale 2
        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(14, -27, "Alcott", alcottGuid), es)); // stale 3
        Assert.True(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(11, -29, "Alcott", alcottGuid), es));  // stale 4 -> fire
    }

    [Fact]
    public void RovingNpcTalkLoop_NameOnly_ResetsWhenNearestInstanceChanges()
    {
        // Distinct same-named NPC instances must NOT be conflated into a bogus
        // loop: when the resolved nearest guid CHANGES (the bot moved to a
        // different instance), the streak restarts — so a legitimate greet of
        // several same-named NPCs once each is never falsely suppressed.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var nameOnly = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Town Guard" } };

        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(1, 0, "Town Guard", 0x1001u), es)); // start A
        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(2, 0, "Town Guard", 0x1001u), es)); // A stale 1
        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(3, 0, "Town Guard", 0x1001u), es)); // A stale 2
        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(4, 0, "Town Guard", 0x2002u), es)); // switch to B -> restart
        Assert.False(policy.IsRovingNpcTalkLoop(nameOnly, WorldWithNpc(5, 0, "Town Guard", 0x2002u), es)); // B stale 1 (not fired)
    }

    [Fact]
    public void TalkLoopTtl_NotSuppressed_BeforeAnyRecord()
    {
        var policy = MakeStationaryUsePolicy();
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Scribe" } };
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(policy.IsTalkLoopTtlSuppressed(talk, WorldWithNpc(1, 0, "Scribe", 0x5001u), t0));
    }

    [Fact]
    public void TalkLoopTtl_Suppressed_WithinTtl_ThenReprobesAfterExpiry()
    {
        // cp-2415: after the roving guard records a suppression, re-Talks to the
        // SAME resolved NPC are dropped for the TTL (90s) so the bot moves on;
        // after the TTL the NPC is re-probed (never permanently blocked).
        var policy = MakeStationaryUsePolicy();
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Scribe" } };
        var world = WorldWithNpc(1, 0, "Scribe", 0x5001u);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        policy.RecordTalkLoopSuppression(talk, world, t0);
        Assert.True(policy.IsTalkLoopTtlSuppressed(talk, world, t0.AddSeconds(30)));  // within 90s
        Assert.True(policy.IsTalkLoopTtlSuppressed(talk, world, t0.AddSeconds(89)));  // still within
        Assert.False(policy.IsTalkLoopTtlSuppressed(talk, world, t0.AddSeconds(91))); // expired -> re-probe
        // The expired entry is pruned, so a later check stays false.
        Assert.False(policy.IsTalkLoopTtlSuppressed(talk, world, t0.AddSeconds(120)));
    }

    [Fact]
    public void TalkLoopTtl_NotSuppressed_DifferentNpc()
    {
        // Suppressing NPC A (one resolved guid) must not suppress a Talk that
        // resolves to a DIFFERENT visible NPC guid.
        var policy = MakeStationaryUsePolicy();
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Scribe" } };
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        policy.RecordTalkLoopSuppression(talk, WorldWithNpc(1, 0, "Scribe", 0x5001u), t0);
        Assert.False(policy.IsTalkLoopTtlSuppressed(talk, WorldWithNpc(1, 0, "Scribe", 0x5002u), t0.AddSeconds(10)));
    }

    [Fact]
    public void TalkLoopTtl_NotSuppressed_NonTalkGoal()
    {
        // A non-Talk goal to the same NPC (e.g. a Use/Give turn-in) is NEVER
        // talk-loop-suppressed — only Talk re-greets are.
        var policy = MakeStationaryUsePolicy();
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Scribe" } };
        var world = WorldWithNpc(1, 0, "Scribe", 0x5001u);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        policy.RecordTalkLoopSuppression(talk, world, t0);
        var use = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Scribe" } };
        Assert.False(policy.IsTalkLoopTtlSuppressed(use, world, t0.AddSeconds(10)));
    }

    [Fact]
    public void TalkLoopTtl_PrunesExpiredEntries_OnRecord_NoUnboundedGrowth()
    {
        // The map must not accumulate expired entries that are never re-queried:
        // recording a new NPC opportunistically prunes any already-expired keys.
        var policy = MakeStationaryUsePolicy();
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Scribe" } };
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Record NPC A, then NPC B 100s later (A's 90s TTL has expired and A is
        // never re-queried) — the record of B must evict the stale A entry.
        policy.RecordTalkLoopSuppression(talk, WorldWithNpc(1, 0, "Scribe", 0x5001u), t0);
        Assert.Equal(1, policy.TalkLoopSuppressionEntryCount);
        policy.RecordTalkLoopSuppression(talk, WorldWithNpc(1, 0, "Scribe", 0x5002u), t0.AddSeconds(100));
        Assert.Equal(1, policy.TalkLoopSuppressionEntryCount); // A pruned, only B remains
    }

    [Fact]
    public void MultiNpcTalkChurn_ResetsWhenBotMoves()
    {
        // Walking between NPCs in different cells is traversal, not a cycle.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var npcA = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002625u } };
        var npcB = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80001234u } };

        Assert.False(policy.IsMultiNpcTalkChurn(npcA, WorldAt(0xA9B4u, 0xA9B40019u, 0, 0), es));
        Assert.False(policy.IsMultiNpcTalkChurn(npcB, WorldAt(0xA9B4u, 0xA9B4001Au, 0, 0), es));
        Assert.False(policy.IsMultiNpcTalkChurn(npcA, WorldAt(0xA9B4u, 0xA9B4001Bu, 0, 0), es));
        Assert.False(policy.IsMultiNpcTalkChurn(npcB, WorldAt(0xA9B4u, 0xA9B4001Cu, 0, 0), es));
        Assert.False(policy.IsMultiNpcTalkChurn(npcA, WorldAt(0xA9B4u, 0xA9B4001Du, 0, 0), es));
    }

    [Fact]
    public void MultiNpcTalkChurn_AbandonsWhenFrontierExceedsTwoTargets()
    {
        // Three-plus distinct NPCs looks like exploration/traversal across a
        // crowded area, not a tight 2-node cycle — abandon, never fire.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var a = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002625u } };
        var b = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80001234u } };
        var c = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80009999u } };

        var goals = new[] { a, b, c, a, b, c, a, b, c };
        foreach (var g in goals)
            Assert.False(policy.IsMultiNpcTalkChurn(g, world, es));
    }

    [Fact]
    public void MultiNpcTalkChurn_IgnoresNonTalkGoals()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var attack = new Goal { Kind = GoalKind.Attack, Target = new Selector { Guid = 0x80002625u } };
        var pickup = new Goal { Kind = GoalKind.Pickup, Target = new Selector { Guid = 0x80001234u } };

        for (var i = 0; i < 8; i++)
        {
            Assert.False(policy.IsMultiNpcTalkChurn(attack, world, es));
            Assert.False(policy.IsMultiNpcTalkChurn(pickup, world, es));
        }
    }

    [Fact]
    public void ExhaustedNpcTalk_IgnoresNonTalkGoals()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var use = new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "Door" } };

        Assert.False(policy.IsExhaustedNpcTalkRepeat(use, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(use, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(use, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(use, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(use, world, es));
    }

    [Fact]
    public void ExhaustedNpcTalk_UnderspecifiedSelector_NotGuarded()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B4u, 0xA9B4014Du, 0, 0);
        var goal = new Goal { Kind = GoalKind.Talk, Target = new Selector { NameContains = "prentice" } };

        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
        Assert.False(policy.IsExhaustedNpcTalkRepeat(goal, world, es));
    }

    // ---- Cross-kind interaction fixation loop-break (emptied corpse) ----
    //
    // After a kill, a weak model fixates on the resulting EMPTY corpse,
    // alternating Use{Corpse} and Pickup{Corpse} forever. The per-kind Use
    // and Talk guards each count only their own GoalKind, so the alternation
    // slips past both. IsStationaryInteractFixation counts ACROSS the interact
    // kinds (world-object Use + Pickup) on the same stationary target and
    // drops the 4th no-progress repeat. A real loot (InventoryItemAdded /
    // Removed) or any movement resets the streak.

    [Fact]
    public void InteractFixation_DropsFourthMixedUsePickup_OnSameCorpse()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B3u, 0xA9B3000Cu, 39.7f, 78.5f);
        var corpse = new Selector { Guid = 0x80002688u };
        var use = new Goal { Kind = GoalKind.Use, Target = corpse };
        var pickup = new Goal { Kind = GoalKind.Pickup, Target = corpse };

        Assert.False(policy.IsStationaryInteractFixation(use, world, es));    // 1
        Assert.False(policy.IsStationaryInteractFixation(pickup, world, es)); // 2
        Assert.False(policy.IsStationaryInteractFixation(use, world, es));    // 3
        Assert.True(policy.IsStationaryInteractFixation(pickup, world, es));  // 4 -> stuck
        Assert.True(policy.IsStationaryInteractFixation(use, world, es));     // stays stuck
    }

    [Fact]
    public void InteractFixation_DropsFourthAllPickup_OnSameCorpse()
    {
        // Pickup is unguarded by the per-kind guards; the cross-kind guard
        // catches a pure Pickup loop too.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B3u, 0xA9B3000Cu, 39.7f, 78.5f);
        var goal = new Goal { Kind = GoalKind.Pickup, Target = new Selector { Name = "Corpse of Chicken" } };

        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.True(policy.IsStationaryInteractFixation(goal, world, es));
    }

    [Fact]
    public void InteractFixation_ExemptsWhenInventoryChanges()
    {
        // A non-empty corpse: each interaction adds loot, so the bot IS making
        // progress even though it has not moved -> never suppressed.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B3u, 0xA9B3000Cu, 39.7f, 78.5f);
        var corpse = new Selector { Guid = 0x80002688u };
        var use = new Goal { Kind = GoalKind.Use, Target = corpse };
        var pickup = new Goal { Kind = GoalKind.Pickup, Target = corpse };

        Assert.False(policy.IsStationaryInteractFixation(use, world, es));
        es.Append(InvAdded("Mana Potion"));
        Assert.False(policy.IsStationaryInteractFixation(pickup, world, es));
        es.Append(InvAdded("Pyreal"));
        Assert.False(policy.IsStationaryInteractFixation(use, world, es));
        es.Append(InvAdded("Leather"));
        Assert.False(policy.IsStationaryInteractFixation(pickup, world, es));
    }

    [Fact]
    public void InteractFixation_ResetsWhenBotMovesCell()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var corpse = new Selector { Guid = 0x80002688u };
        var use = new Goal { Kind = GoalKind.Use, Target = corpse };
        var pickup = new Goal { Kind = GoalKind.Pickup, Target = corpse };

        Assert.False(policy.IsStationaryInteractFixation(use, WorldAt(0xA9B3u, 0xA9B3000Cu, 0, 0), es));
        Assert.False(policy.IsStationaryInteractFixation(pickup, WorldAt(0xA9B3u, 0xA9B3000Du, 0, 0), es)); // moved cell
        Assert.False(policy.IsStationaryInteractFixation(use, WorldAt(0xA9B3u, 0xA9B3000Eu, 0, 0), es));    // moved cell
        Assert.False(policy.IsStationaryInteractFixation(pickup, WorldAt(0xA9B3u, 0xA9B3000Fu, 0, 0), es)); // moved cell
    }

    [Fact]
    public void InteractFixation_JitterWithinEpsilon_StillTrips()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var corpse = new Selector { Guid = 0x80002688u };
        var use = new Goal { Kind = GoalKind.Use, Target = corpse };
        var pickup = new Goal { Kind = GoalKind.Pickup, Target = corpse };

        Assert.False(policy.IsStationaryInteractFixation(use, WorldAt(0xA9B3u, 0xA9B3000Cu, 0.0f, 0.0f), es));
        Assert.False(policy.IsStationaryInteractFixation(pickup, WorldAt(0xA9B3u, 0xA9B3000Cu, 0.3f, 0.2f), es)); // <0.75u
        Assert.False(policy.IsStationaryInteractFixation(use, WorldAt(0xA9B3u, 0xA9B3000Cu, 0.1f, 0.4f), es));
        Assert.True(policy.IsStationaryInteractFixation(pickup, WorldAt(0xA9B3u, 0xA9B3000Cu, 0.2f, 0.1f), es));
    }

    [Fact]
    public void InteractFixation_DistinctCorpses_DoNotCollapse()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B3u, 0xA9B3000Cu, 0, 0);
        var corpseA = new Goal { Kind = GoalKind.Use, Target = new Selector { Guid = 0x80002688u } };
        var corpseB = new Goal { Kind = GoalKind.Pickup, Target = new Selector { Guid = 0x80002689u } };

        Assert.False(policy.IsStationaryInteractFixation(corpseA, world, es));
        Assert.False(policy.IsStationaryInteractFixation(corpseB, world, es));
        Assert.False(policy.IsStationaryInteractFixation(corpseA, world, es));
        Assert.False(policy.IsStationaryInteractFixation(corpseB, world, es));
    }

    [Fact]
    public void InteractFixation_IgnoresInventoryUseGoals()
    {
        // goal.Item set => use-with-target / inventory; owned by
        // IsInventoryUseRecentlyDispatched, not this guard.
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B3u, 0xA9B3000Cu, 0, 0);
        var goal = new Goal
        {
            Kind = GoalKind.Use,
            Target = new Selector { Guid = 0x80002688u },
            Item = new Selector { Name = "Key" },
        };

        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
    }

    [Fact]
    public void InteractFixation_IgnoresNonInteractGoals()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B3u, 0xA9B3000Cu, 0, 0);
        var attack = new Goal { Kind = GoalKind.Attack, Target = new Selector { Guid = 0x80002688u } };
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Guid = 0x80002688u } };

        Assert.False(policy.IsStationaryInteractFixation(attack, world, es));
        Assert.False(policy.IsStationaryInteractFixation(talk, world, es));
        Assert.False(policy.IsStationaryInteractFixation(attack, world, es));
        Assert.False(policy.IsStationaryInteractFixation(talk, world, es));
    }

    [Fact]
    public void InteractFixation_UnderspecifiedSelector_NotGuarded()
    {
        var policy = MakeStationaryUsePolicy();
        var es = new EventStream();
        var world = WorldAt(0xA9B3u, 0xA9B3000Cu, 0, 0);
        var goal = new Goal { Kind = GoalKind.Pickup, Target = new Selector { NameContains = "orpse" } };

        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
        Assert.False(policy.IsStationaryInteractFixation(goal, world, es));
    }
    //
    // Pure decision behind the mechanical backstop: when the bot is
    // demonstrably stuck in a tapped-out, monster-free safe zone the policy
    // substitutes a targetless Explore for a social Talk/Give the LLM keeps
    // emitting against the existing prompt rules. dwell threshold = 5min,
    // no-progress grace = 2min. ComputeEgressActive is a sticky latch (stays
    // engaged across landblock seams so the bot leaves the town cluster
    // instead of ping-ponging); IsEgressOverridableVerb gates which goal
    // kinds get substituted while the latch is engaged.

    private static readonly TimeSpan StuckGrace = TimeSpan.FromMinutes(3);

    [Fact]
    public void HuntEgress_EngagesWhenStuckPastThreshold()
    {
        Assert.True(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 6.0, sinceMaterialProgress: StuckGrace));
    }

    [Theory]
    [InlineData((int)GoalKind.Talk, true)]
    [InlineData((int)GoalKind.Give, true)]
    [InlineData((int)GoalKind.Use, false)]
    [InlineData((int)GoalKind.Pickup, false)]
    [InlineData((int)GoalKind.Wield, false)]
    [InlineData((int)GoalKind.Attack, false)]
    [InlineData((int)GoalKind.Explore, false)]
    public void HuntEgress_OnlyOverridesSocialVerbs(int kind, bool expected)
    {
        // Use can be a door/portal transition (the egress action itself);
        // Pickup can be self-arming; Attack/Explore are already progress.
        Assert.Equal(expected, LlmGoalPolicy.IsEgressOverridableVerb((GoalKind)kind));
    }

    [Fact]
    public void HuntEgress_SuppressedWhenUnarmed()
    {
        // A weaponless bot keeps its full town grace (not ready to hunt).
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: false, monsterInView: false,
            dwellMinutes: 6.0, sinceMaterialProgress: StuckGrace));
    }

    [Fact]
    public void HuntEgress_SuppressedWhenMonsterInView()
    {
        // A monster is engageable here — do not flee the hunt.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: true,
            dwellMinutes: 6.0, sinceMaterialProgress: StuckGrace));
    }

    // --- stuck-loop egress gate (cp-2266) -----------------------------------
    // When a fixation guard has detected a proven no-progress interaction loop,
    // ShouldEscapeStuckLoop decides whether to send a tapped-out, combat-ready,
    // unthreatened bot away with Explore instead of deferring to the fallback
    // (which re-picks the same dead-end class of stationary object).

    [Fact]
    public void StuckLoop_EscapesWhenCombatReadyTappedOutAndNoMonster()
    {
        Assert.True(LlmGoalPolicy.ShouldEscapeStuckLoop(
            combatReady: true, tappedOut: true, monsterInView: false));
    }

    [Fact]
    public void StuckLoop_SuppressedWhenUnarmed()
    {
        // An UNARMED bot may legitimately need to Use objects to progress —
        // do not send it wandering off.
        Assert.False(LlmGoalPolicy.ShouldEscapeStuckLoop(
            combatReady: false, tappedOut: true, monsterInView: false));
    }

    [Fact]
    public void StuckLoop_SuppressedBeforeTappedOut()
    {
        // Early in a zone a Use loop may be a genuine progress attempt.
        Assert.False(LlmGoalPolicy.ShouldEscapeStuckLoop(
            combatReady: true, tappedOut: false, monsterInView: false));
    }

    [Fact]
    public void StuckLoop_SuppressedWhenMonsterInView()
    {
        // A monster is in view (hostile OR non-hostile — the caller passes
        // AnyAttackableMonsterInView): the egress exists to LEAVE and find
        // monsters, so when one is already in view the bot should engage it
        // (defend/flee a hostile, or fight a non-hostile XP target), never wander
        // off to find a monster it can already see.
        Assert.False(LlmGoalPolicy.ShouldEscapeStuckLoop(
            combatReady: true, tappedOut: true, monsterInView: true));
    }

    // --- silent-NPC Talk-loop early egress (cp-2328) ------------------------
    // A combat-ready bot in a monster-free safe zone Talk-loops a SILENT NPC
    // (no dialog) for minutes because the actual Explore that physically moves
    // it is gated behind a 5-min tapped-out clock. ShouldEarlyEscapeTalkLoop
    // breaks the loop the instant the stationary fixation is proven (Talk loop
    // kind only), and IsTalkLoopEgressActive keeps substituting the re-emitted
    // Talk/Give with Explore until the bot actually leaves the landblock.

    [Fact]
    public void EarlyTalkLoopEgress_FiresForTalkLoopWhenSafe()
    {
        Assert.True(LlmGoalPolicy.ShouldEarlyEscapeTalkLoop(
            loopKind: "NPC Talk", monsterInView: false, freshDirective: false));
    }

    [Fact]
    public void EarlyTalkLoopEgress_SuppressedForOtherLoopKinds()
    {
        // The Talk-loop early-escape is Talk-only; a world-object Use loop has
        // its OWN escape (ShouldEscapeWorldUseLoop, cp-2372), so this Talk
        // predicate correctly does not fire for it.
        Assert.False(LlmGoalPolicy.ShouldEarlyEscapeTalkLoop(
            loopKind: "Use", monsterInView: false, freshDirective: false));
    }

    [Fact]
    public void EarlyTalkLoopEgress_SuppressedWhenMonsterInView()
    {
        // A monster is in view (hostile OR non-hostile — the caller passes
        // AnyAttackableMonsterInView): engage the visible XP target instead of
        // wandering off to break the talk loop (cp-2378/cp-2379 principle).
        Assert.False(LlmGoalPolicy.ShouldEarlyEscapeTalkLoop(
            loopKind: "NPC Talk", monsterInView: true, freshDirective: false));
    }

    [Fact]
    public void EarlyTalkLoopEgress_SuppressedWhenFreshDirective()
    {
        // The server is actively guiding the bot — let it follow the directive.
        Assert.False(LlmGoalPolicy.ShouldEarlyEscapeTalkLoop(
            loopKind: "NPC Talk", monsterInView: false, freshDirective: true));
    }

    // --- world-object Use-loop egress (cp-2372) ----------------------------
    // A confirmed bare world-object Use churn (the cp-2354 churn guard already
    // fired) Explores to travel through/past the looped object instead of
    // deferring to the fallback. NOT gated on freshDirective (re-Using one
    // object cannot be "finishing guided training"); only a hostile suppresses.

    [Fact]
    public void WorldUseLoopEgress_FiresForUseChurnWhenSafe()
    {
        Assert.True(LlmGoalPolicy.ShouldEscapeWorldUseLoop(
            loopKind: "world-object Use", monsterInView: false));
    }

    [Fact]
    public void WorldUseLoopEgress_SuppressedWhenMonsterInView()
    {
        // A monster is in view (hostile OR non-hostile — the caller passes
        // AnyAttackableMonsterInView): engage the visible XP target instead of
        // wandering off to "find monsters" the bot can already see.
        Assert.False(LlmGoalPolicy.ShouldEscapeWorldUseLoop(
            loopKind: "world-object Use", monsterInView: true));
    }

    [Fact]
    public void WorldUseLoopEgress_SuppressedForOtherLoopKinds()
    {
        // Only the world-object Use churn kind uses this escape; a Talk loop has
        // its own (freshDirective-gated) path.
        Assert.False(LlmGoalPolicy.ShouldEscapeWorldUseLoop(
            loopKind: "NPC Talk", monsterInView: false));
    }

    // --- AnyAttackableMonsterInView: the broadened egress-defer predicate -----
    // The stuck-loop / use-loop Explore-egress exists to LEAVE and find monsters,
    // so it must defer when a monster is already in view. Before this widening the
    // gate only checked ObservedHostile, so a fresh combat-ready bot near visible
    // but non-hostile training monsters got Explored AWAY from them. The predicate
    // now mirrors the `## Monsters in view` capsule (cp-2335/2366): any non-corpse
    // monster OR observed-hostile creature counts.

    private static WorldStateProjection WorldWithVisible(params VisibleObjectProjection[] visible)
        => BuildExitTokenWorld() with { Visible = visible };

    [Fact]
    public void AnyAttackableMonsterInView_TrueForNonHostileMonster()
    {
        var world = WorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x80008064u, Name = "Sparring Golem", Wcid = 12698u,
            Distance = 40f, IsMonster = true, ObservedHostile = false, IsCorpse = false,
        });
        Assert.True(LlmGoalPolicy.AnyAttackableMonsterInView(world));
    }

    [Fact]
    public void AnyAttackableMonsterInView_TrueForObservedHostile()
    {
        var world = WorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x80008065u, Name = "Drudge", Wcid = 99u,
            Distance = 5f, IsMonster = false, ObservedHostile = true, IsCorpse = false,
        });
        Assert.True(LlmGoalPolicy.AnyAttackableMonsterInView(world));
    }

    [Fact]
    public void AnyAttackableMonsterInView_FalseForCorpseMonster()
    {
        var world = WorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x80008066u, Name = "Sparring Golem", Wcid = 12698u,
            Distance = 3f, IsMonster = true, ObservedHostile = false, IsCorpse = true,
        });
        Assert.False(LlmGoalPolicy.AnyAttackableMonsterInView(world));
    }

    [Fact]
    public void AnyAttackableMonsterInView_FalseWhenNoMonsters()
    {
        var world = WorldWithVisible(new VisibleObjectProjection
        {
            Guid = 0x8000814Fu, Name = "Society Greeter", Wcid = 30991u,
            Distance = 2f, IsMonster = false, ObservedHostile = false, IsCorpse = false,
        });
        Assert.False(LlmGoalPolicy.AnyAttackableMonsterInView(world));
    }

    [Fact]
    public void TalkLoopEgressActive_WhileInWindowAndSameLandblock()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(LlmGoalPolicy.IsTalkLoopEgressActive(
            nowUtc: now, until: now.AddSeconds(30),
            latchLandblock: 0xA9B4u, currentLandblock: 0xA9B4u,
            monsterInView: false, freshDirective: false));
    }

    [Fact]
    public void TalkLoopEgressActive_InactiveAfterTimeout()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(LlmGoalPolicy.IsTalkLoopEgressActive(
            nowUtc: now, until: now.AddSeconds(-1),
            latchLandblock: 0xA9B4u, currentLandblock: 0xA9B4u,
            monsterInView: false, freshDirective: false));
    }

    [Fact]
    public void TalkLoopEgressActive_InactiveWhenLandblockChanged()
    {
        // Leaving the landblock means the loop is already broken — stop overriding.
        var now = DateTimeOffset.UtcNow;
        Assert.False(LlmGoalPolicy.IsTalkLoopEgressActive(
            nowUtc: now, until: now.AddSeconds(30),
            latchLandblock: 0xA9B4u, currentLandblock: 0xA9B2u,
            monsterInView: false, freshDirective: false));
    }

    [Fact]
    public void TalkLoopEgressActive_InactiveWhenMonsterOrDirective()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(LlmGoalPolicy.IsTalkLoopEgressActive(
            nowUtc: now, until: now.AddSeconds(30),
            latchLandblock: 0xA9B4u, currentLandblock: 0xA9B4u,
            monsterInView: true, freshDirective: false));
        Assert.False(LlmGoalPolicy.IsTalkLoopEgressActive(
            nowUtc: now, until: now.AddSeconds(30),
            latchLandblock: 0xA9B4u, currentLandblock: 0xA9B4u,
            monsterInView: false, freshDirective: true));
    }

    [Fact]
    public void TalkLoopEgressActive_InactiveWhenNoLatchRecorded()
    {
        // Default state (no loop ever detected) never reports active.
        var now = DateTimeOffset.UtcNow;
        Assert.False(LlmGoalPolicy.IsTalkLoopEgressActive(
            nowUtc: now, until: DateTimeOffset.MinValue,
            latchLandblock: null, currentLandblock: 0xA9B4u,
            monsterInView: false, freshDirective: false));
    }

    [Fact]
    public void HuntEgress_SuppressedBeforeDwellThreshold()
    {
        // Just arrived / brief visit — let the bot work the area first.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 4.9, sinceMaterialProgress: StuckGrace));
    }

    [Fact]
    public void HuntEgress_SuppressedWhileMaterialProgressRecent()
    {
        // A quest actively handing over items keeps its grace.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 9.0, sinceMaterialProgress: TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void HuntEgress_EngagesExactlyAtThresholdBoundaries()
    {
        // dwell == 5min and sinceProgress == 2min are both "stuck enough".
        Assert.True(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 5.0, sinceMaterialProgress: TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void HuntEgress_StaysEngagedAcrossSeamDespiteDwellReset()
    {
        // Already egressing; bot just crossed a seam so dwell reset to 0.
        // The sticky latch must keep egress engaged so it keeps leaving the
        // town cluster instead of reverting to Talk and pathing back.
        Assert.True(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: true, combatReady: true, monsterInView: false,
            dwellMinutes: 0.0, sinceMaterialProgress: StuckGrace));
    }

    [Fact]
    public void HuntEgress_StickyCancelledByMonster()
    {
        // Reached the hunt zone — disengage egress so the bot can fight.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: true, combatReady: true, monsterInView: true,
            dwellMinutes: 0.0, sinceMaterialProgress: StuckGrace));
    }

    [Fact]
    public void HuntEgress_StickyCancelledByRecentProgress()
    {
        // Inventory changed mid-egress (looted / received item) — yield to
        // whatever the LLM wants to do next.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: true, combatReady: true, monsterInView: false,
            dwellMinutes: 0.0, sinceMaterialProgress: TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void HuntEgress_StickyCancelledByDisarm()
    {
        // Lost the weapon mid-egress — no longer hunt-ready.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: true, combatReady: false, monsterInView: false,
            dwellMinutes: 0.0, sinceMaterialProgress: StuckGrace));
    }

    [Fact]
    public void HuntEgress_TappedOut_BypassesLootGrace()
    {
        // cp-2260 live regression: a tapped-out bot re-farming trivial mobs
        // loots a corpse every <2min, so sinceMaterialProgress never reaches
        // the 2min grace and egress would never engage. When tapped out, the
        // grace is bypassed (the bot's own 0-levels signal is the authority),
        // so egress engages despite very recent inventory churn.
        Assert.True(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 6.0, sinceMaterialProgress: TimeSpan.Zero, tappedOut: true));
    }

    [Fact]
    public void HuntEgress_NotTappedOut_LootGraceStillApplies()
    {
        // Same recent-loot churn but NOT tapped out (e.g. still leveling here)
        // → the grace is preserved, egress defers.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 6.0, sinceMaterialProgress: TimeSpan.Zero, tappedOut: false));
    }

    [Fact]
    public void HuntEgress_TappedOut_StillCancelledByMonster()
    {
        // Tapped-out bypasses only the loot grace — an engageable (unfarmed/
        // hostile) monster still cancels egress so the bot fights it.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: true,
            dwellMinutes: 6.0, sinceMaterialProgress: TimeSpan.Zero, tappedOut: true));
    }

    [Fact]
    public void HuntEgress_TappedOut_StillRequiresCombatReady()
    {
        // Tapped-out does not override the disarmed cancel.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: false, monsterInView: false,
            dwellMinutes: 6.0, sinceMaterialProgress: TimeSpan.Zero, tappedOut: true));
    }

    // ---- Seam-independent barren-stall first-trigger (cp-2263 oscillation) ----
    // A combat-ready bot oscillating between two adjacent safe landblocks resets
    // per-landblock dwell at every seam, so dwellMinutes never reaches the
    // threshold and the dwell first-trigger can never fire. sinceMaterialProgress
    // does NOT reset at seams, so a long no-progress span ENGAGES egress even at
    // dwell == 0. Threshold = 2x dwell (10min).

    [Fact]
    public void HuntEgress_BarrenStall_EngagesAtDwellZeroWhenNoProgressPastTimeout()
    {
        // The exact live loophole: dwell keeps resetting (0), not yet egressing,
        // armed, monster-free, no material progress for 10min → engage.
        Assert.True(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 0.0, sinceMaterialProgress: TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void HuntEgress_BarrenStall_DefersJustBelowTimeout()
    {
        // 9.9min < 10min barren-stall timeout AND dwell below threshold → defer.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 0.0, sinceMaterialProgress: TimeSpan.FromMinutes(9.9)));
    }

    [Fact]
    public void HuntEgress_BarrenStall_CancelledByMonsterEvenPastTimeout()
    {
        // An engageable monster still cancels — the bot reached a hunt.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: true,
            dwellMinutes: 0.0, sinceMaterialProgress: TimeSpan.FromMinutes(10)));
    }

    // ---- fresh-directive egress veto (cp-2285 academy-completion) ----------
    // A low-level bot still being actively guided by the server (a fresh,
    // DISTINCT tutorial PopupString it has not acted on) must finish that
    // training before the dwell/stall egress fires it out to hunt. Wired as a
    // top-tier ComputeEgressActive cancel fed by the RecentFreshDirective
    // freshness gate (PopupString-only, distinct-text, ages out so it can't
    // deadlock). Answers the user question "should it value the academy XP?".

    [Fact]
    public void HuntEgress_FreshDirective_VetoesEvenPastDwellAndTappedOut()
    {
        // The exact academy bailout case: combat-ready, no monster, dwell well
        // past threshold, tappedOut (no level gained yet) — would normally
        // ENGAGE — but a fresh server directive is active, so egress is vetoed.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 20.0, sinceMaterialProgress: TimeSpan.FromMinutes(20),
            tappedOut: true, recentFreshDirective: true));
    }

    [Fact]
    public void HuntEgress_FreshDirective_CancelsInProgressEgress()
    {
        // The veto is top-tier: it even cancels an egress already latched on.
        Assert.False(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: true, combatReady: true, monsterInView: false,
            dwellMinutes: 20.0, sinceMaterialProgress: TimeSpan.FromMinutes(20),
            tappedOut: true, recentFreshDirective: true));
    }

    [Fact]
    public void HuntEgress_NoFreshDirective_StillEngagesPastDwell()
    {
        // Regression guard: with no fresh directive, behaviour is unchanged —
        // a tapped-out bot past the dwell threshold still egresses.
        Assert.True(LlmGoalPolicy.ComputeEgressActive(
            currentlyEgressing: false, combatReady: true, monsterInView: false,
            dwellMinutes: 6.0, sinceMaterialProgress: TimeSpan.FromMinutes(10),
            tappedOut: true, recentFreshDirective: false));
    }

    // ---- RecentFreshDirective freshness gate (cp-2285) ---------------------
    // Distinct-text + ages-out semantics that keep the veto from deadlocking.

    private static LlmGoalPolicy NewBarePolicy()
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("x") }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        return new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo());
    }

    [Fact]
    public void RecentFreshDirective_FirstDistinctPopup_CreditsAndAgesOut()
    {
        var policy = NewBarePolicy();
        var es = new EventStream();
        var t0 = DateTimeOffset.UtcNow;
        es.Append(new StreamEvent { Sequence = -1, Utc = t0, Kind = EventKind.PopupString, Text = "Use the bow." });
        Assert.True(policy.RecentFreshDirective(es, t0));
        // Still held a minute later with no new events (within the 2min grace).
        Assert.True(policy.RecentFreshDirective(es, t0.AddMinutes(1)));
        // Ages out just past the grace — so it can never deadlock.
        Assert.False(policy.RecentFreshDirective(es, t0.AddMinutes(2.01)));
    }

    [Fact]
    public void RecentFreshDirective_RepeatedIdenticalPopup_DoesNotReExtend()
    {
        var policy = NewBarePolicy();
        var es = new EventStream();
        var t0 = DateTimeOffset.UtcNow;
        es.Append(new StreamEvent { Sequence = -1, Utc = t0, Kind = EventKind.PopupString, Text = "Talk to the trainer." });
        Assert.True(policy.RecentFreshDirective(es, t0));
        // A REPEAT of the same text (the anti-idle loop) must NOT reset the grace,
        es.Append(new StreamEvent { Sequence = -1, Utc = t0, Kind = EventKind.PopupString, Text = "Talk to the trainer." });
        // so by t0+2.01min the veto has aged out and egress is allowed again.
        Assert.False(policy.RecentFreshDirective(es, t0.AddMinutes(2.01)));
    }

    [Fact]
    public void RecentFreshDirective_NewDistinctPopup_ReExtends()
    {
        var policy = NewBarePolicy();
        var es = new EventStream();
        var t0 = DateTimeOffset.UtcNow;
        es.Append(new StreamEvent { Sequence = -1, Utc = t0, Kind = EventKind.PopupString, Text = "Step one." });
        Assert.True(policy.RecentFreshDirective(es, t0));
        // A genuinely NEW instruction that ARRIVES later (its own Utc is t1)
        // re-extends the grace from its own emit time.
        var t1 = t0.AddMinutes(1.5);
        es.Append(new StreamEvent { Sequence = -1, Utc = t1, Kind = EventKind.PopupString, Text = "Step two." });
        Assert.True(policy.RecentFreshDirective(es, t1));
        Assert.True(policy.RecentFreshDirective(es, t1.AddMinutes(1.0)));
    }

    [Fact]
    public void RecentFreshDirective_StalePopup_DoesNotCredit()
    {
        // A popup whose OWN timestamp is older than the grace (e.g. an old
        // login popup seen on the first whole-buffer scan, or one processed
        // after a delay) must NOT grant a fresh veto — freshness is anchored to
        // the directive's emit time, not the processing time.
        var policy = NewBarePolicy();
        var es = new EventStream();
        var t0 = DateTimeOffset.UtcNow;
        es.Append(new StreamEvent { Sequence = -1, Utc = t0, Kind = EventKind.PopupString, Text = "Old login popup." });
        Assert.False(policy.RecentFreshDirective(es, t0.AddMinutes(3)));
    }

    [Fact]
    public void RecentFreshDirective_NonPopupEvents_DoNotCredit()
    {
        var policy = NewBarePolicy();
        var es = new EventStream();
        var t0 = DateTimeOffset.UtcNow;
        // NpcDialog is deliberately NOT credited (a town full of NPCs would
        // otherwise pin the bot forever); ServerMessage broadcast is ignored too.
        es.Append(new StreamEvent { Sequence = -1, Utc = t0, Kind = EventKind.NpcDialog, Name = "Trainer", Text = "Welcome, adventurer." });
        es.Append(new StreamEvent { Sequence = -1, Utc = t0, Kind = EventKind.ServerMessage, Text = "Someone says hi." });
        Assert.False(policy.RecentFreshDirective(es, t0));
    }

    // ---- IsEgressOverridableStationaryUse (cp-2263 forge fixation) ----
    // While egressing, a Use of a STATIONARY non-transit world object extends the
    // dwell like Talk/Give and is substituted; transit/interactive affordances
    // (door/portal/corpse/openable) are preserved so the bot can still leave/loot.

    private static WorldStateProjection StationaryUseWorld(
        params VisibleObjectProjection[] visible)
        => new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0x8602u, CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = visible,
        };

    private static Goal UseGoal(string name)
        => new Goal { Kind = GoalKind.Use, Target = new Selector { Name = name } };

    [Fact]
    public void StationaryUse_OverridesPlainStationaryObject()
    {
        var world = StationaryUseWorld(new VisibleObjectProjection
        { Guid = 0x9u, Name = "Fletching Forge", Distance = 6f });
        Assert.True(LlmGoalPolicy.IsEgressOverridableStationaryUse(
            UseGoal("Fletching Forge"), world));
    }

    [Theory]
    [InlineData("door")]
    [InlineData("portal")]
    [InlineData("openable")]
    [InlineData("corpse")]
    public void StationaryUse_PreservesTransitAndInteractiveAffordances(string flag)
    {
        var v = new VisibleObjectProjection
        {
            Guid = 0x9u, Name = "Thing", Distance = 6f,
            IsDoor = flag == "door",
            IsPortal = flag == "portal",
            IsOpenable = flag == "openable",
            IsCorpse = flag == "corpse",
        };
        Assert.False(LlmGoalPolicy.IsEgressOverridableStationaryUse(
            UseGoal("Thing"), StationaryUseWorld(v)));
    }

    [Fact]
    public void StationaryUse_DoesNotOverrideNonUseKind()
    {
        var world = StationaryUseWorld(new VisibleObjectProjection
        { Guid = 0x9u, Name = "Fletching Forge", Distance = 6f });
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Fletching Forge" } };
        Assert.False(LlmGoalPolicy.IsEgressOverridableStationaryUse(talk, world));
    }

    [Fact]
    public void StationaryUse_DoesNotOverrideUnresolvedTarget()
    {
        // Target not in view → conservative: pass through (could be a transit
        // object the bot is walking toward).
        var world = StationaryUseWorld(new VisibleObjectProjection
        { Guid = 0x9u, Name = "Something Else", Distance = 6f });
        Assert.False(LlmGoalPolicy.IsEgressOverridableStationaryUse(
            UseGoal("Fletching Forge"), world));
    }

    [Fact]
    public void StationaryUse_DoesNotOverrideEmptySelector()
    {
        var world = StationaryUseWorld(new VisibleObjectProjection
        { Guid = 0x9u, Name = "Fletching Forge", Distance = 6f });
        var goal = new Goal { Kind = GoalKind.Use, Target = new Selector() };
        Assert.False(LlmGoalPolicy.IsEgressOverridableStationaryUse(goal, world));
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

    private static StreamEvent WorldObjInteracted(string name, uint wcid, uint guid) => new()
    {
        Sequence = -1, Utc = DateTimeOffset.UtcNow,
        Kind = EventKind.WorldObjectInteracted,
        ItemGuid = guid, Wcid = wcid, Name = name,
    };

    private static WorldStateProjection WorldWithVisibleChest(uint guid, string name) =>
        BuildExitTokenWorld() with
        {
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = guid, Name = name, Wcid = 4321u,
                    ItemType = 0x10u, Distance = 12f, IsChest = true, IsOpenable = true,
                },
            },
        };

    [Fact]
    public void BuildUserPrompt_RendersRecentlyInteractedObjects_WhenStillVisible()
    {
        // cp-2290 tempo loop: the bot Used the same visible chest 3x. The
        // self-emitted WorldObjectInteracted echoes must surface as a
        // "## Recently interacted objects" block so the LLM stops re-picking.
        const uint chestGuid = 0x7A9B400Bu;
        var world = WorldWithVisibleChest(chestGuid, "Treasure Chest");
        var es = new EventStream();
        es.Append(WorldObjInteracted("Treasure Chest", 4321u, chestGuid));
        es.Append(WorldObjInteracted("Treasure Chest", 4321u, chestGuid));
        es.Append(WorldObjInteracted("Treasure Chest", 4321u, chestGuid));

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);

        Assert.Contains("## Recently interacted objects", prompt);
        Assert.Contains("Treasure Chest", prompt);
        Assert.Contains("interacted x3 recently", prompt);
        Assert.Contains($"guid=0x{chestGuid:X8}", prompt);
        Assert.Contains("unlikely to produce a different outcome", prompt);
    }

    private static WorldStateProjection WorldWithVisiblePickup(uint guid, string name) =>
        BuildExitTokenWorld() with
        {
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    // ItemType 0x2 (armor bit) is in the Pickup mask -> pickup-eligible.
                    Guid = guid, Name = name, Wcid = 5555u, ItemType = 0x2u, Distance = 5f,
                },
            },
        };

    [Fact]
    public void BuildUserPrompt_RecentlyInteracted_AnnotatesFailedPickup_WhenPickupItemStillVisible()
    {
        // cp-2375: the bot interacted with a pickup-eligible ground item that is
        // STILL visible -> the pickup did not stick -> annotate it as not-acquired
        // so the LLM stops re-trying an un-acquirable ground item.
        const uint itemGuid = 0x7A9B5001u;
        var world = WorldWithVisiblePickup(itemGuid, "Leather Cap");
        var es = new EventStream();
        es.Append(WorldObjInteracted("Leather Cap", 5555u, itemGuid));
        es.Append(WorldObjInteracted("Leather Cap", 5555u, itemGuid));

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);

        Assert.Contains("## Recently interacted objects", prompt);
        Assert.Contains("Leather Cap", prompt);
        Assert.Contains("STILL on the ground", prompt);
        Assert.Contains("did NOT enter your bag", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentlyInteracted_DoesNotAnnotateNonPickupAsFailedPickup()
    {
        // A non-pickup-eligible interacted object (a chest = Use target, ItemType
        // 0x10 not in the Pickup mask) must NOT get the failed-pickup annotation.
        const uint chestGuid = 0x7A9B400Bu;
        var world = WorldWithVisibleChest(chestGuid, "Treasure Chest");
        var es = new EventStream();
        es.Append(WorldObjInteracted("Treasure Chest", 4321u, chestGuid));
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);
        Assert.Contains("Treasure Chest", prompt);
        Assert.DoesNotContain("STILL on the ground", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsRecentlyInteracted_WhenObjectNoLongerVisible()
    {
        // Interaction echo exists but the object is NOT in the current
        // Visible set → not actionable context, so the block is omitted.
        var world = WorldWithVisibleChest(0x1111_2222u, "Some Other Object");
        var es = new EventStream();
        es.Append(WorldObjInteracted("Treasure Chest", 4321u, 0x7A9B400Bu));

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);

        Assert.DoesNotContain("## Recently interacted objects", prompt);
        Assert.DoesNotContain("interacted x", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsRecentlyInteractedSection_WhenNoInteractions()
    {
        // No WorldObjectInteracted events → section absent (so it adds
        // nothing to the static-floor prompt budget).
        var es = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);
        Assert.DoesNotContain("## Recently interacted objects", prompt);
        Assert.DoesNotContain("interacted x", prompt);
    }

    [Fact]
    public void WorldObjectInteracted_IsNotSalient_AndNotPlanInvalidating()
    {
        // Pure self-emitted bookkeeping echo: must never wake the LLM nor
        // drop the current plan (mirrors InventoryItemUsed).
        Assert.False(LlmGoalPolicy.IsSalientKind(EventKind.WorldObjectInteracted));
        Assert.False(LlmGoalPolicy.IsPlanInvalidatingKind(EventKind.WorldObjectInteracted));
    }

    [Fact]
    public void BuildUserPrompt_IncludesInventoryUseLoopBreakRule()
    {
        // cp-2401: the main LOOP-BREAK rule (incl. its (b) inventory-USE
        // sub-case) is now gated on an observed Talk/Use repeat, so seed a Use
        // repeat to render it.
        var es = new EventStream();
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Use target=name=\"Door\" item= source=llm:test" });
        es.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Use target=name=\"Door\" item= source=llm:test" });
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);
        Assert.Contains("(b) inventory-USE", prompt);
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

    // Untargeted-Explore sticky-budget exemption (call-volume reduction).
    // A policy whose LLM always returns the SAME Explore goal with the given
    // target JSON. A bare Explore{anywhere} is a Motor-owned traversal that is
    // exempt from MaxStickyReEmits; a targeted Explore is not.
    private static (LlmGoalPolicy Policy, Func<int> HttpCalls) MakeStickyExplorePolicy(
        string targetJson, int maxStickyReEmits = 3, TimeSpan? stuckTimeout = null)
    {
        var goalJson = $$"""
        {
          "goal_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "kind": "Explore",
          "target": {{targetJson}},
          "priority": 4,
          "rationale": "wander"
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
            StuckTimeout = stuckTimeout ?? TimeSpan.FromMinutes(10),
        };
        return (policy, () => count);
    }

    [Fact]
    public async Task LlmGoalPolicy_UntargetedExplore_ReDrivesPastStickyBudget_NoExtraCall()
    {
        // A bare Explore{anywhere} is a non-interactive traversal with no
        // object target, so it must re-drive for free PAST MaxStickyReEmits
        // (a hunt excursion crosses many ticks) — no extra LLM call while the
        // only churn is goal lifecycle.
        var (policy, httpCalls) = MakeStickyExplorePolicy("{ \"name\": \"anywhere\" }", maxStickyReEmits: 2);
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // Re-clear far more than the budget (2). Each clear emits only the
        // lifecycle churn that GoalCompleted represents.
        for (var i = 0; i < 6; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.GoalCompleted, Text = "explore leg done",
            });
            var reEmitted = policy.ProposeGoal(world, events, null);
            Assert.NotNull(reEmitted);
            Assert.Equal(GoalKind.Explore, reEmitted!.Kind);
        }
        // Budget would have forced a call after 2 with a targeted goal; the
        // untargeted Explore stayed free the whole way.
        Assert.Equal(1, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_TargetedExplore_ObeysStickyBudget_CallsLlmAfterBudget()
    {
        // An Explore NAMING a concrete target IS subject to the budget — it
        // could be an unreachable pursuit, so it must fall through to a fresh
        // LLM decision after MaxStickyReEmits re-clears.
        var (policy, httpCalls) = MakeStickyExplorePolicy("{ \"name\": \"the trainer\" }", maxStickyReEmits: 2);
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "leg" });
        Assert.NotNull(policy.ProposeGoal(world, events, null)); // re-emit #1
        Assert.Equal(1, httpCalls());

        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "leg" });
        Assert.NotNull(policy.ProposeGoal(world, events, null)); // re-emit #2
        Assert.Equal(1, httpCalls());

        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "leg" });
        Assert.Null(policy.ProposeGoal(world, events, null));    // budget exhausted → LLM call
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_UntargetedExplore_ExternalSalientEvent_StillBreaks_CallsLlm()
    {
        // The exemption lifts ONLY the retry budget. A genuine external
        // change (e.g. NpcDialog) must still wake the LLM.
        var (policy, httpCalls) = MakeStickyExplorePolicy("{ \"name\": \"anywhere\" }");
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Text = "A villager: greetings",
        });
        policy.ProposeGoal(world, events, null);
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_UntargetedExplore_NewPickerTarget_StillBreaks_CallsLlm()
    {
        // The bot is NOT blinded to discoveries while free-exploring: a new
        // picker target (the discovery wake for a real object coming into
        // range) must still break the gate and call the LLM.
        var (policy, httpCalls) = MakeStickyExplorePolicy("{ \"name\": \"anywhere\" }");
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.PickerActivityStarted,
            ItemGuid = 0x800001D4u, Name = "Drudge", Text = "in-range: nearest candidate",
        });
        policy.ProposeGoal(world, events, null);
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_UntargetedExplore_StuckTimeout_StillBreaks_CallsLlm()
    {
        // The stuck-timeout liveness backstop is unchanged: even with the
        // budget exemption, a real LLM call fires once the stuck window
        // elapses since the last call.
        var (policy, httpCalls) = MakeStickyExplorePolicy(
            "{ \"name\": \"anywhere\" }", stuckTimeout: TimeSpan.FromMilliseconds(1));
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        await Task.Delay(30);
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "leg" });
        policy.ProposeGoal(world, events, null);
        Assert.Equal(2, httpCalls());
    }

    [Theory]
    [InlineData("anywhere", true)]
    [InlineData("ANYWHERE", true)]
    [InlineData("  anywhere  ", true)]
    [InlineData("the trainer", false)]
    [InlineData("Drudge", false)]
    public void IsUntargetedExploreGoal_NameSentinel(string name, bool expected)
    {
        var goal = new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = name } };
        Assert.Equal(expected, LlmGoalPolicy.IsUntargetedExploreGoal(goal));
    }

    [Fact]
    public void IsUntargetedExploreGoal_EmptyAndDefaultTarget_AreUntargeted()
    {
        Assert.True(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Explore, Target = new Selector() }));
        // Target defaults to an empty Selector when omitted.
        Assert.True(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Explore }));
    }

    [Fact]
    public void IsUntargetedExploreGoal_ConcreteSelectorFields_AreTargeted()
    {
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Explore, Target = new Selector { Guid = 5u } }));
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Explore, Target = new Selector { Wcid = 42u } }));
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Explore, Target = new Selector { NameContains = "any" } }));
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Explore, Target = new Selector { ItemTypeMask = 1u } }));
        // anywhere name but ALSO a concrete field → targeted (not the pure sentinel).
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = "anywhere", Guid = 5u } }));
    }

    [Fact]
    public void IsUntargetedExploreGoal_NonExploreOrNull_AreNotUntargeted()
    {
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Use, Target = new Selector { Name = "anywhere" } }));
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(new Goal { Kind = GoalKind.Attack }));
        Assert.False(LlmGoalPolicy.IsUntargetedExploreGoal(null));
    }

    // break-sticky-on-self-interact: a policy whose LLM always returns the
    // same Use{Door guid=56528} goal. Same wiring as MakeStickyPolicy but a
    // spatial Use verb whose Target matches a WorldObjectInteracted echo.
    private static (LlmGoalPolicy Policy, Func<int> HttpCalls) MakeStickyUseDoorPolicy()
    {
        var goalJson = """
        {
          "goal_id": "99999999-8888-7777-6666-555555555555",
          "kind": "Use",
          "target": { "guid": 56528, "name": "Door" },
          "priority": 6,
          "rationale": "open the door"
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
            MaxStickyReEmits = 3,
        };
        return (policy, () => count);
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_SelfInteractedWithGoalTarget_BreaksReEmit_AndCallsLlm()
    {
        // cp-2304 live loop: the LLM picked Use{Door}; the door opened in
        // place (server UseDone(ok)) with NO external salient event, so the
        // sticky gate re-drove the same Use{Door} for free. The Motor's own
        // WorldObjectInteracted echo proves the objective was ALREADY
        // attempted, so a real LLM call must fire (it then re-reads the
        // `## Recently interacted objects` telemetry and picks differently).
        var (policy, httpCalls) = MakeStickyUseDoorPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // The Motor dispatched the Use opcode against the door (guid 56528,
        // name "Door") and the action cycle completed — a self-emitted,
        // NON-salient, NON-external echo.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.WorldObjectInteracted,
            ItemGuid = 56528u, Name = "Door",
        });

        var next = policy.ProposeGoal(world, events, null);
        // No free sticky re-emit — a fresh LLM call fired instead.
        Assert.Equal(2, httpCalls());
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_SelfInteractedWithDifferentObject_StillReEmitsForFree()
    {
        // The self-interaction echo must NOT break the sticky gate when it
        // is a DIFFERENT object than the goal target (selector AND semantics:
        // guid and name both diverge). Re-driving the unfinished objective
        // for free is still correct.
        var (policy, httpCalls) = MakeStickyUseDoorPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        var firstGoal = await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        // Interacted with a different object (guid + name both mismatch).
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.WorldObjectInteracted,
            ItemGuid = 4242u, Name = "Chest",
        });

        var reEmitted = policy.ProposeGoal(world, events, null);
        Assert.NotNull(reEmitted);
        Assert.Equal(GoalKind.Use, reEmitted!.Kind);
        Assert.Equal("Door", reEmitted.Target?.Name);
        Assert.Equal(1, httpCalls()); // free re-emit, no new call
        Assert.NotEqual(firstGoal.Id, reEmitted.Id);
    }

    [Fact]
    public async Task LlmGoalPolicy_Sticky_SelfInteractedNameMatchGuidMismatch_BreaksReEmit()
    {
        // Selector AND semantics with a partial identity: the goal target has
        // BOTH guid and name. A respawned/re-id'd door with the SAME name but
        // a DIFFERENT guid must NOT count as the same object — the populated
        // guid field diverges, so the sticky gate still re-drives for free.
        var (policy, httpCalls) = MakeStickyUseDoorPolicy();
        var world = BuildExitTokenWorld();
        var events = new EventStream();

        await EstablishLlmGoalAsync(policy, world, events);
        Assert.Equal(1, httpCalls());

        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.WorldObjectInteracted,
            ItemGuid = 7777u, Name = "Door", // name matches, guid does not
        });

        var reEmitted = policy.ProposeGoal(world, events, null);
        Assert.NotNull(reEmitted);   // guid mismatch ⇒ not the same object ⇒ free re-emit
        Assert.Equal(1, httpCalls());
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

    // Prompt-size cap: in object-dense areas the `## Visible nearby` body
    // must stay bounded.
    // The `## Visible nearby` body is bounded and NEUTRAL: rows are included
    // NEAREST-FIRST (pure distance) up to a soft cap, with NO per-type
    // priority. A far tagged object is dropped in favor of nearer objects of
    // any kind; the dropped set is summarized factually by decoded flag.
    [Fact]
    public void AppendVisibleNearby_IncludesNearestFirst_AndSummarizesOmitted()
    {
        var list = new System.Collections.Generic.List<VisibleObjectProjection>();
        // Three tagged objects, deliberately far away. Under nearest-first they
        // are NOT privileged over nearer plain rows — they get omitted.
        list.Add(new VisibleObjectProjection { Guid = 0x1001u, Name = "FarGolem", Wcid = 1u, ItemType = 0x10u, Distance = 95f, IsCreature = true, IsMonster = true });
        list.Add(new VisibleObjectProjection { Guid = 0x1002u, Name = "FarDoor", Wcid = 2u, ItemType = 0x10u, Distance = 90f, IsDoor = true });
        list.Add(new VisibleObjectProjection { Guid = 0x1003u, Name = "FarGreeter", Wcid = 3u, ItemType = 0x10u, Distance = 85f, IsCreature = true });
        // 120 plain (untagged) objects at increasing distance 1..120.
        for (int i = 0; i < 120; i++)
            list.Add(new VisibleObjectProjection { Guid = (uint)(0x2000 + i), Name = $"Plain{i:D3}", Wcid = (uint)(1000 + i), ItemType = 0x4u, Distance = i + 1f });

        var sb = new StringBuilder();
        LlmGoalPolicy.AppendVisibleNearby(sb, list);
        var text = sb.ToString();

        // Nearest rows present; far rows (plain OR tagged) truncated (cap=50).
        Assert.Contains("Plain000", text);
        Assert.DoesNotContain("Plain119", text);
        // No type-priority: the far tagged objects are NOT rescued over nearer
        // plain rows — distance alone decides.
        Assert.DoesNotContain("FarGolem", text);
        Assert.DoesNotContain("FarDoor", text);
        Assert.DoesNotContain("FarGreeter", text);
        // 123 objects, 50 shown -> 73 omitted, summarized by decoded flag:
        // the 3 tagged (monster/door/npc) + 70 plain ("other").
        Assert.Contains("+73 more distant objects not shown due to prompt budget", text);
        Assert.Contains("monster=1", text);
        Assert.Contains("door=1", text);
        Assert.Contains("npc=1", text);
        Assert.Contains("other=70", text);
    }

    // Backstop: even when objects are numerous, the section stays within
    // budget by truncating nearest-first, summarized by decoded flag.
    [Fact]
    public void AppendVisibleNearby_NearestFirstBackstop_BoundsSectionAndSummarizesKinds()
    {
        var list = new System.Collections.Generic.List<VisibleObjectProjection>();
        for (int i = 0; i < 600; i++)
            list.Add(new VisibleObjectProjection { Guid = (uint)(0x3000 + i), Name = $"Monster{i:D3}", Wcid = (uint)(5000 + i), ItemType = 0x10u, Distance = i + 1f, IsCreature = true, IsMonster = true });

        var sb = new StringBuilder();
        LlmGoalPolicy.AppendVisibleNearby(sb, list);
        var text = sb.ToString();

        // Section is strictly bounded (600 uncapped rows would be ~18KB+).
        Assert.True(text.Length <= 10000, $"section was {text.Length} chars");
        Assert.Contains("more distant objects not shown due to prompt budget", text);
        Assert.Contains("monster=", text);
    }

    // A single pathologically long row (e.g. a huge object name) must not
    // blow the budget: the always-emit-the-nearest-row guarantee clamps the
    // row so the section stays bounded.
    [Fact]
    public void AppendVisibleNearby_PathologicalRow_IsClampedAndBounded()
    {
        var list = new System.Collections.Generic.List<VisibleObjectProjection>
        {
            new VisibleObjectProjection { Guid = 0x4001u, Name = new string('X', 50000), Wcid = 1u, ItemType = 0x10u, Distance = 1f, IsCreature = true, IsMonster = true },
        };

        var sb = new StringBuilder();
        LlmGoalPolicy.AppendVisibleNearby(sb, list);
        var text = sb.ToString();

        Assert.True(text.Length <= 10000, $"section was {text.Length} chars");
        Assert.Contains("\u2026", text); // ellipsis marks the clamp
    }

    // cp-2313: a MONSTER row in `## Visible nearby` carries the bot's own
    // recorded combat-feel outcomes against that KIND inline, so the LLM
    // sees "you have died to this kind" at the decision point without
    // cross-referencing the aggregate `combat history` block.
    [Fact]
    public void AppendVisibleNearby_MonsterRow_CarriesCombatRecordInline()
    {
        var list = new System.Collections.Generic.List<VisibleObjectProjection>
        {
            new VisibleObjectProjection { Guid = 0x5001u, Name = "Creeper Mosswart", Wcid = 19261u, ItemType = 0x10u, Distance = 12f, IsCreature = true, IsMonster = true },
        };
        var history = new System.Collections.Generic.List<CombatHistoryEntry>
        {
            new CombatHistoryEntry("Creeper Mosswart", 19261u, Kills: 0, Deaths: 1, NearDeaths: 0, Fights: 1, LastOutcome: "death"),
        };

        var sb = new StringBuilder();
        LlmGoalPolicy.AppendVisibleNearby(sb, list, history);
        var text = sb.ToString();

        Assert.Contains("Creeper Mosswart", text);
        Assert.Contains("[your record:", text);
        Assert.Contains("deaths 1", text);
    }

    // The inline record is gated on the MONSTER wire flag: an npc/object row
    // is NEVER annotated, even if a same-name history row exists (the record
    // is about combat KINDS, not arbitrary same-named objects).
    [Fact]
    public void AppendVisibleNearby_NonMonsterRow_IsNeverAnnotated()
    {
        var list = new System.Collections.Generic.List<VisibleObjectProjection>
        {
            // Same display name as a history row, but flagged as a civilian npc.
            new VisibleObjectProjection { Guid = 0x5002u, Name = "Creeper Mosswart", Wcid = 19261u, ItemType = 0x10u, Distance = 5f, IsCreature = true, IsMonster = false },
        };
        var history = new System.Collections.Generic.List<CombatHistoryEntry>
        {
            new CombatHistoryEntry("Creeper Mosswart", 19261u, Kills: 0, Deaths: 1, NearDeaths: 0, Fights: 1, LastOutcome: "death"),
        };

        var sb = new StringBuilder();
        LlmGoalPolicy.AppendVisibleNearby(sb, list, history);
        var text = sb.ToString();

        Assert.Contains("npc", text);
        Assert.DoesNotContain("[your record:", text);
    }

    // A monster with no matching history row gets NO annotation (the helper
    // returns empty), so unfought kinds read cleanly.
    [Fact]
    public void AppendVisibleNearby_MonsterRow_NoHistory_NoAnnotation()
    {
        var list = new System.Collections.Generic.List<VisibleObjectProjection>
        {
            new VisibleObjectProjection { Guid = 0x5003u, Name = "Unknown Beast", Wcid = 40000u, ItemType = 0x10u, Distance = 8f, IsCreature = true, IsMonster = true },
        };
        var history = new System.Collections.Generic.List<CombatHistoryEntry>
        {
            new CombatHistoryEntry("Creeper Mosswart", 19261u, Kills: 0, Deaths: 1, NearDeaths: 0, Fights: 1, LastOutcome: "death"),
        };

        var sb = new StringBuilder();
        LlmGoalPolicy.AppendVisibleNearby(sb, list, history);
        var text = sb.ToString();

        Assert.Contains("Unknown Beast", text);
        Assert.DoesNotContain("[your record:", text);
    }

    // The new optional combatHistory arg is null-safe: the 2-arg call (and a
    // null history) must render monster rows with no annotation, unchanged.
    [Fact]
    public void AppendVisibleNearby_NullHistory_RendersMonsterRowUnannotated()
    {
        var list = new System.Collections.Generic.List<VisibleObjectProjection>
        {
            new VisibleObjectProjection { Guid = 0x5004u, Name = "Drudge Slinker", Wcid = 19258u, ItemType = 0x10u, Distance = 3f, IsCreature = true, IsMonster = true },
        };

        var sb = new StringBuilder();
        LlmGoalPolicy.AppendVisibleNearby(sb, list); // 2-arg: combatHistory defaults null
        var text = sb.ToString();

        Assert.Contains("Drudge Slinker", text);
        Assert.Contains("monster", text);
        Assert.DoesNotContain("[your record:", text);
    }

    // Prompt-floor compaction: the static RULES + schema text dominates the
    // user prompt and drives gpt-4.1-mini's http-413 in dense areas. Lock the
    // floor in with a near-empty world so it cannot SILENTLY regrow. Budget
    // bumped 13000 -> 13300 (cp-2282) for the third XP-spend advancement verb
    // RaiseSkill (its schema enum entry + the inline SPEND XP example). Bumped
    // 13300 -> 13500 (cp-2297) for the COMBAT SAFETY threat-count clause that
    // points the LLM at the new `monsters in view` cluster signal. Bumped
    // 13500 -> 14500 (recall-lifestone-escape-verb) for the STUCK ESCAPE rule
    // (+ the `Recall` schema enum entry in both kind blocks) that gives the LLM
    // a lifestone-recall verb to escape a physical-immobilization wedge. Bumped
    // 14500 -> 15200 (spend-xp-attribute-balance) for the rewritten SPEND XP
    // rule: live evidence showed the LLM dumping all XP into endurance (the only
    // attribute the old rule explained), so the rule now states the FULL
    // attribute->effect mechanics (strength/coordination melee, focus/self
    // magic, quickness defense/missile, endurance/health max HP) plus an
    // adaptive no-fixed-build caution. Bumped 15200 -> 16000 (xp-spend-salience)
    // for the SPEND XP action-selection lead-in (3 live gpt-4.1-mini runs showed
    // the bot HOARDING thousands of unspent XP, 0 Raise verbs) + the
    // fight/loot/invest-XP priority-band clause + the conditional `## Self`
    // unspent-XP "invest NOW" cue. All are intentional, reviewed additions,
    // not silent regrowth; the delta does not move the runtime 413 risk, which
    // is driven by dense per-tick WORLD/visible sections, not the static floor.
    // Bumped 16000 -> 17000 (door-open-state-projection) for the CLOSED DOORS
    // ARE BARRIERS rule: live evidence showed the LLM pursuing a Talk{Agent}
    // directive behind a closed door it never opened (0 Use{Door} in a run); the
    // rule pairs with a new wire-decoded `door open`/`door closed` row token so
    // the LLM connects "named target unreachable + closed door here -> Use door".
    // Bumped 17000 -> 18000 (npc-repeat-exhaustion-pivot) for the NPC REPEAT
    // EXHAUSTION rule: live evidence showed a level-1 bot that killed one monster
    // then Talk-looped an NPC (which rotated 2-3 canned lines, defeating the
    // identical-repeat Motor loop-break) ~8x while killable monsters stayed in
    // view; the rule keys off the existing neutral `(repeated xN)` telemetry and
    // tells the LLM to pivot to a NON-Talk verb (Attack a visible monster only
    // after exhaustion, else Use/Give/Pickup/Explore).
    [Fact]
    public void BuildUserPrompt_StaticFloor_StaysWithinBudget()
    {
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null);
        Assert.True(prompt.Length <= 18000,
            $"static prompt floor grew to {prompt.Length} chars (budget 18000)");
    }

    // ---- XP-spend salience (xp-spend-salience) ----
    //
    // Three live gpt-4.1-mini natural-play runs showed the bot HOARDING
    // thousands of unspent XP and emitting 0 Raise* verbs even when the SPEND
    // XP rule rendered correctly and outcome-evidence pointed at the fix. This
    // slice raises XP-spend SALIENCE in the prompt (no source-side decision, no
    // numeric threshold): a fight/loot/invest-XP priority-band clause, a
    // first-class action-selection lead-in on the SPEND XP rule, and a
    // conditional `## Self` "invest NOW" cue shown only when unspent XP > 0.
    private static WorldStateProjection BuildXpWorld(long total, long unspent) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xA9B4u, CellId = 0xA9B40019u,
            PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
            Level = 9, TotalExperience = total, AvailableExperience = unspent,
        },
        Inventory = System.Array.Empty<InventoryItemProjection>(),
        Visible = System.Array.Empty<VisibleObjectProjection>(),
    };

    [Fact]
    public void BuildUserPrompt_UnspentXpCapsule_SurfacesSwingAccuracyWhenSwingsThrown()
    {
        // cp-2427: the offense-mechanism evidence that disambiguates the
        // failure mode. Across models the bot poured XP into endurance while
        // its swings kept evading, because the capsule surfaced deaths/max-HP
        // (survival) but never the raw melee hit/evade split. With resolved
        // swings present, the capsule must state the bot's own landed-vs-evaded
        // counts so the SPEND XP rule's accuracy->attribute mapping has the
        // evidence it needs. Raw fact, no recommendation.
        var world = BuildXpWorld(69296, 5475) with
        {
            CumulativeSwingsLanded = 12,
            CumulativeSwingsEvaded = 80,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Unspent XP", prompt);
        Assert.Contains(
            "your melee swings this session have landed 12 time(s) and been evaded 80 time(s)",
            prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpCapsule_OmitsSwingAccuracyBeforeAnySwing()
    {
        // RAW-PRESENCE gate: before any swing resolves (landed + evaded == 0)
        // the accuracy fact would be noise ("landed 0, evaded 0"), so it must
        // not render. Mirrors the any-positive gate the other spend facts use.
        var world = BuildXpWorld(69296, 5475); // cumulative swings default to 0
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        Assert.Contains("## Unspent XP", prompt);
        Assert.DoesNotContain("your melee swings this session have landed", prompt);
    }

    [Fact]
    public void BuildUserPrompt_PriorityBand_IncludesInvestUnspentXp()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 5475), new EventStream(), null);
        Assert.Contains("fight/loot/invest unspent XP", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SpendXpRule_HasFirstClassActionLeadIn()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 5475), new EventStream(), null);
        Assert.Contains("SPEND XP is a FIRST-CLASS action", prompt);
        // Spending stays OPTIONAL — danger still outranks it.
        Assert.Contains("no `HOSTILE` is on you", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SpendXpRule_StatesFullUnspentAmountRange()
    {
        // Tempo (reduce-llm-call-volume): live evidence (cp2352-livefire) showed the
        // LLM dribbling an XP hoard out in many tiny RaiseAttribute decisions — 10
        // confirmed raises + 2 timeouts to drain ~1188 XP, 12 of 28 LLM calls that
        // run, each a multi-second deliberation cycle. The rule never stated the
        // amount's valid RANGE, so the LLM may have assumed small increments. State
        // the neutral mechanics fact: amount can be up to the full unspent balance in
        // a single raise. Granularity stays the model's judgment (no prescription).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 5475), new EventStream(), null);
        Assert.Contains("up to your full unspent balance", prompt);
        Assert.Contains("invest your entire unspent total in a single action", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SpendXpRule_AmountRangeFact_OmittedWhenNoUnspentXp()
    {
        // The amount-range fact lives inside the unspent-gated SPEND XP rule, so it
        // disappears with the rule when there is nothing to invest (no prompt-byte
        // cost in the common zero-unspent case).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("up to your full unspent balance", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SpendXpRule_OmittedWhenNoUnspentXp()
    {
        // Unspent XP == 0: the ~1.1KB SPEND XP rule is inapplicable (nothing to
        // invest), so it is gated out to trim the prompt and stop it burying the
        // combat rules. The rule still renders when unspent > 0 (covered above).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("SPEND XP is a FIRST-CLASS action", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_RendersWhenUnspentPositive()
    {
        // cp-2336: re-surface the unspent-XP fact in the most decision-proximate
        // slot (end of prompt) so the Raise* verbs compete with the parked local
        // affordance. Facts only; the amount is echoed and the verbs named.
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 5475), new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.Contains("5475 unspent experience available this tick", prompt);
        Assert.Contains("RaiseAttribute, RaiseVital, and RaiseSkill are executable right now", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_SurfacesSurvivalFacts_WhenMaxHpAndDeathsKnown()
    {
        // Survivability slice: co-locate the bot's OWN max-HP + death facts with
        // the spend decision (cp-2336 burial pattern) so the SPEND XP rule can
        // weigh survivability. RAW facts only — the max HP value + death count
        // are echoed verbatim; no threshold, no "raise endurance" recommendation.
        var world = BuildXpWorld(69296, 5475) with
        {
            Self = BuildXpWorld(69296, 5475).Self with { HealthObservedPeak = 7, NumDeaths = 5 },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.Contains("peaked at 7 HP this session", prompt);
        Assert.Contains("you have died 5 times", prompt);
        // The survival fact must sit INSIDE the decision-proximate capsule.
        var capsuleIdx = prompt.IndexOf("## Unspent XP", System.StringComparison.Ordinal);
        var survivalIdx = prompt.IndexOf("peaked at 7 HP", System.StringComparison.Ordinal);
        Assert.True(survivalIdx > capsuleIdx, "survival fact should render within the ## Unspent XP capsule");
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_SurvivalFact_FallsBackToCurrentHpWhenNoPeak()
    {
        var world = BuildXpWorld(69296, 100) with
        {
            Self = BuildXpWorld(69296, 100).Self with { HealthObservedPeak = null, HealthCurrent = 6 },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("peaked at 6 HP this session", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_SurvivalFact_RendersOnRawPresence_NoMagnitudeGate()
    {
        // cp-2337/audit: the fact renders whenever the value is KNOWN, with NO
        // source-side magnitude gate (no `> 0` significance filter). A known 0
        // still renders as a raw fact.
        var world = BuildXpWorld(69296, 100) with
        {
            Self = BuildXpWorld(69296, 100).Self with { HealthObservedPeak = 0, HealthCurrent = 0 },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("peaked at 0 HP this session", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_OmitsSurvivalFacts_WhenUnknown()
    {
        // BuildXpWorld sets neither max HP nor deaths -> no survival line, but
        // the capsule itself still renders (gated only on unspent > 0).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 5475), new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.DoesNotContain("peaked at", prompt);
        Assert.DoesNotContain("you have died", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_NoSurvivalFacts_WhenNoUnspentXp()
    {
        // No unspent XP -> the whole capsule (incl. survival facts) is omitted,
        // so survival facts never leak outside the spend context.
        var world = BuildXpWorld(69296, 0) with
        {
            Self = BuildXpWorld(69296, 0).Self with { HealthObservedPeak = 7, NumDeaths = 5 },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("## Unspent XP", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_OffenseFact_RendersWhenIneffectiveKindNoKill()
    {
        // cp-2410/cp-2411: a monster kind the bot fought but never killed is
        // the can't-win-fights bottleneck — it renders beside the spend
        // decision so the SPEND XP rule can weigh offense, not just survival.
        // An `ineffective` stalemate (cp-2410's original case) still qualifies.
        var world = BuildXpWorld(69296, 5475) with
        {
            CombatHistory = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 1,
                    NearDeaths: 0, Fights: 3, LastOutcome: "death", Ineffective: 2),
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.Contains("monster kind(s) you have not killed", prompt);
        Assert.Contains("in recent combat you have 0 kill(s)", prompt);
        // Exact full clause (guards the middle "and have fought N" half too).
        Assert.Contains(
            "in recent combat you have 0 kill(s) and have fought 1 monster kind(s) you have not killed",
            prompt);
        var capsuleIdx = prompt.IndexOf("## Unspent XP", System.StringComparison.Ordinal);
        var offenseIdx = prompt.IndexOf("monster kind(s) you have not killed", System.StringComparison.Ordinal);
        Assert.True(offenseIdx > capsuleIdx, "offense fact should render within the ## Unspent XP capsule");
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_OffenseFact_RendersWhenDiedToKindNoIneffective()
    {
        // cp-2411: the kinds that WALL the live bot record a `death` (or
        // `near-death`), NOT `ineffective` — cp-2410's `Ineffective > 0` gate
        // MISSED them. A died-to kind with 0 kills and 0 ineffective must now
        // surface as the can't-win-fights bottleneck beside the spend decision.
        var world = BuildXpWorld(69296, 5475) with
        {
            CombatHistory = new[]
            {
                new CombatHistoryEntry("Mite Scion", 22600u, Kills: 0, Deaths: 2,
                    NearDeaths: 1, Fights: 4, LastOutcome: "death", Ineffective: 0),
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.Contains("1 monster kind(s) you have not killed", prompt);
        Assert.Contains("in recent combat you have 0 kill(s)", prompt);
        Assert.Contains(
            "in recent combat you have 0 kill(s) and have fought 1 monster kind(s) you have not killed",
            prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_OffenseFact_OmittedWhenKindHasKills()
    {
        // A kind the bot HAS killed is not a bottleneck -> no can't-win fact.
        var world = BuildXpWorld(69296, 5475) with
        {
            CombatHistory = new[]
            {
                new CombatHistoryEntry("Rabbit", 48u, Kills: 3, Deaths: 0,
                    NearDeaths: 0, Fights: 3, LastOutcome: "kill", Ineffective: 0),
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.DoesNotContain("monster kind(s) you have not killed", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_InlinesAttributeValues()
    {
        // cp-2419: the bot's RAW attribute values render INSIDE the ## Unspent XP
        // capsule (not only pointed-to in ## Self), so the WHICH-to-raise decision
        // sees e.g. a low coordination beside the spendable XP + offense facts.
        var baseWorld = BuildXpWorld(69296, 1000);
        var world = baseWorld with
        {
            Self = baseWorld.Self with
            {
                Attributes = new[]
                {
                    new SelfAttributeProjection { Name = "strength", Base = 47 },
                    new SelfAttributeProjection { Name = "coordination", Base = 10 },
                    new SelfAttributeProjection { Name = "endurance", Base = 31 },
                },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.Contains("your attributes are strength 47, coordination 10, endurance 31", prompt);
        var capsuleIdx = prompt.IndexOf("## Unspent XP", System.StringComparison.Ordinal);
        var attrIdx = prompt.IndexOf("your attributes are strength 47", System.StringComparison.Ordinal);
        Assert.True(attrIdx > capsuleIdx, "attribute values should render within the ## Unspent XP capsule");
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_NoAttributes_NoCrashNoAttrLine()
    {
        // Attributes unknown (null): the capsule still renders and just omits the
        // attribute fact (no crash, no dangling "your attributes are").
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 1000), new EventStream(), null);
        Assert.Contains("## Unspent XP", prompt);
        Assert.DoesNotContain("your attributes are", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_AttributesOmittedWhenNoUnspentXp()
    {
        // The whole capsule is gated on unspent > 0, so the inlined attributes do
        // not render when there is nothing to spend.
        var baseWorld = BuildXpWorld(69296, 0);
        var world = baseWorld with
        {
            Self = baseWorld.Self with
            {
                Attributes = new[] { new SelfAttributeProjection { Name = "coordination", Base = 10 } },
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("## Unspent XP", prompt);
        Assert.DoesNotContain("your attributes are", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_OffenseFact_OmittedWhenNoUnspentXp()
    {
        // No unspent XP -> the whole capsule (incl. the can't-win fact) is omitted.
        var world = BuildXpWorld(69296, 0) with
        {
            CombatHistory = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 19257u, Kills: 0, Deaths: 1,
                    NearDeaths: 0, Fights: 3, LastOutcome: "death", Ineffective: 2),
            },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.DoesNotContain("monster kind(s) you have not killed", prompt);
    }

    [Fact]
    public void BuildUserPrompt_FreshKillCorpseCapsule_RendersWhenPresent()
    {
        // cp-2357: after a kill the picker abandons the corpse before the LLM
        // latency lands and the hunt-excursion re-drives away, so fresh kills go
        // unlooted. Surface the bot's own fresh, unlooted kill corpse as a
        // decision-proximate loot opportunity (fact + Use->Pickup affordance).
        var corpses = new[] { new FreshKillCorpse("Corpse of Cow", 0x800u, 5.0f) };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            freshKillCorpses: corpses);
        Assert.Contains("## Fresh kill to loot", prompt);
        Assert.Contains("Corpse of Cow", prompt);
        Assert.Contains("not yet looted", prompt);
        Assert.Contains("Use it to reveal its loot, then Pickup", prompt);
    }

    [Fact]
    public void BuildUserPrompt_FreshKillCorpseCapsule_OmittedWhenNoneOrNull()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            freshKillCorpses: null);
        Assert.DoesNotContain("## Fresh kill to loot", prompt);
        var empty = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            freshKillCorpses: System.Array.Empty<FreshKillCorpse>());
        Assert.DoesNotContain("## Fresh kill to loot", empty);
    }

    [Fact]
    public void BuildUserPrompt_AlreadyLootedCapsule_RendersWhenPresent()
    {
        // cp-2358: after emptying its own kill corpse the bot keeps re-Use/
        // Pickup-ing it (the recent-Use section still names it as executable).
        // State the loot OUTCOME as a fact so the model can stop.
        var looted = new[] { new LootedCorpse("husk-alpha", 0x900u) };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            lootedEmptyCorpses: looted);
        Assert.Contains("## Already looted", prompt);
        Assert.Contains("husk-alpha", prompt);
        Assert.Contains("no contents remained to take", prompt);
        Assert.Contains("raw fact, not a recommendation", prompt);
    }

    [Fact]
    public void BuildUserPrompt_AlreadyLootedCapsule_OmittedWhenNullOrEmpty()
    {
        var pNull = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            lootedEmptyCorpses: null);
        Assert.DoesNotContain("## Already looted", pNull);
        var pEmpty = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            lootedEmptyCorpses: System.Array.Empty<LootedCorpse>());
        Assert.DoesNotContain("## Already looted", pEmpty);
    }

    [Fact]
    public void BuildUserPrompt_AlreadyLootedCapsule_ExcludesNameSharedWithFreshKill()
    {
        // Disambiguation: if a fresh UNLOOTED corpse shares the name with an
        // emptied one, the fresh-kill capsule wins and the looted note drops
        // the name (never tell the LLM a name is empty while also offering it
        // as a fresh kill). Here the only looted name matches the only fresh
        // name, so the looted capsule is omitted entirely.
        var fresh = new[] { new FreshKillCorpse("husk-twin", 0x801u, 5.0f) };
        var looted = new[] { new LootedCorpse("husk-twin", 0x900u) };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            freshKillCorpses: fresh, lootedEmptyCorpses: looted);
        Assert.Contains("## Fresh kill to loot", prompt);
        Assert.DoesNotContain("## Already looted", prompt);
    }


    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_OmittedWhenNoUnspent()
    {
        // unspent == 0: the verbs are not executable, so the capsule is omitted
        // (same mechanical gate as the SPEND XP rule).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("## Unspent XP", prompt);
    }

    [Fact]
    public void BuildUserPrompt_UnspentXpEndcap_RendersAtEndAfterVisibleSection()
    {
        // The salience value depends on the capsule being the LAST thing the LLM
        // reads (after the bulky preamble + dynamic world sections). Assert it
        // appears after `## Self` and after the Visible-nearby section so it sits
        // in the decision-proximate end slot.
        var world = BuildXpWorld(69296, 5475) with
        {
            Visible = new[] { new VisibleObjectProjection { Guid = 0x701u, Name = "Jonathan", Wcid = 1u, ItemType = 0x10u, Distance = 3f, IsCreature = true } },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        var capsuleIdx = prompt.IndexOf("## Unspent XP", System.StringComparison.Ordinal);
        var selfIdx = prompt.IndexOf("## Self", System.StringComparison.Ordinal);
        var visibleIdx = prompt.IndexOf("## Visible nearby", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx > selfIdx && selfIdx >= 0, "capsule should render after ## Self");
        Assert.True(capsuleIdx > visibleIdx && visibleIdx >= 0, "capsule should render after ## Visible nearby");
    }

    // ---- Recent-Talk salience end-capsule (talk-repeat-endcap, cp-2337) ----
    //
    // Live mistral runs showed the bot re-Talking the SAME town NPC 6-7 times
    // with rationales that never mention the repeat count ("Wilomine HAS the
    // directions") — the per-NPC counts render mid-prompt in `## Location &
    // recency` but are out-competed by the parked local affordance, the same
    // burial pattern the `## Unspent XP` capsule fixed. This re-surfaces the
    // SAME counts at the decision-proximate end slot whenever any recent Talk
    // exists (no source-side significance threshold — the LLM judges).
    private static EventStream BuildTalkStream(string npcName, int times)
    {
        var events = new EventStream();
        for (var i = 0; i < times; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(times - i),
                Kind = EventKind.GoalEmitted,
                GoalId = Guid.NewGuid(),
                Text = $"Talk target=name=\"{npcName}\" item= source=llm:test",
            });
        }
        return events;
    }

    [Fact]
    public void BuildUserPrompt_RecentTalkEndcap_RendersWhenTalkPresent()
    {
        // Any recent Talk re-surfaces the count facts-only in the
        // decision-proximate slot (no significance threshold in source).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), BuildTalkStream("Wilomine", 3), null);
        Assert.Contains("## Recent Talk", prompt);
        Assert.Contains("in your last 10 emitted goals you emitted Talk to: Wilomine x3", prompt);
        Assert.Contains("raw fact, not a recommendation", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentTalkEndcap_RendersForSingleTalk()
    {
        // No threshold: even a single recent Talk renders the raw count.
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), BuildTalkStream("Wilomine", 1), null);
        Assert.Contains("## Recent Talk", prompt);
        Assert.Contains("Wilomine x1", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentTalkEndcap_OmittedWhenNoTalk()
    {
        // No Talk emissions at all: capsule omitted (nothing to re-surface).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("## Recent Talk", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentTalkEndcap_RendersAtEndAfterLocationRecency()
    {
        // The salience value depends on the capsule sitting AFTER the mid-prompt
        // `## Location & recency` section where the same counts first render.
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), BuildTalkStream("Wilomine", 4), null);
        var capsuleIdx = prompt.IndexOf("## Recent Talk", System.StringComparison.Ordinal);
        var lrIdx = prompt.IndexOf("## Location & recency", System.StringComparison.Ordinal);
        Assert.True(lrIdx >= 0, "## Location & recency section missing");
        Assert.True(capsuleIdx > lrIdx, "capsule should render after ## Location & recency");
    }

    [Fact]
    public void BuildUserPrompt_RecentTalkEndcap_DisambiguatesSameNameDifferentGuid()
    {
        // Two DISTINCT NPCs sharing a display name (different guids) must NOT
        // collapse to one indistinguishable label — each gets a guid suffix,
        // mirroring the `## Location & recency` disambiguation.
        var events = new EventStream();
        foreach (var (guid, n) in new[] { ("0x80000AAA", 2), ("0x80000BBB", 1) })
        {
            for (var i = 0; i < n; i++)
            {
                events.Append(new StreamEvent
                {
                    Sequence = 0,
                    Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                    Kind = EventKind.GoalEmitted,
                    GoalId = Guid.NewGuid(),
                    Text = $"Talk target=guid={guid} name=\"Town Guard\" item= source=llm:test",
                });
            }
        }
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), events, null);
        var capsuleIdx = prompt.IndexOf("## Recent Talk", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx >= 0, "capsule missing");
        var capsule = prompt.Substring(capsuleIdx);
        Assert.Contains("Town Guard (guid=0x80000AAA) x2", capsule);
        Assert.Contains("Town Guard (guid=0x80000BBB) x1", capsule);
    }

    // ---- Recent-Use salience end-capsule (recent-use-endcap, cp-2341) ----
    //
    // Live mistral runs showed the bot re-Using the SAME world object (a
    // "Portal to Town Network") 5 times with rationales that never mention
    // the repeat ("Use the portal to move to a new area") even though each
    // Use teleported it back to a landblock it just left. The per-target Use
    // counts render mid-prompt in `## Location & recency` but are out-competed
    // by the parked local affordance, the same burial pattern the `## Unspent
    // XP` and `## Recent Talk` capsules fixed. This re-surfaces the SAME
    // counts at the decision-proximate end slot whenever any recent Use
    // exists (no source-side significance threshold — the LLM judges).
    private static EventStream BuildUseStream(string targetSelector, int times)
    {
        var events = new EventStream();
        for (var i = 0; i < times; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(times - i),
                Kind = EventKind.GoalEmitted,
                GoalId = Guid.NewGuid(),
                Text = $"Use target={targetSelector} item= source=llm:test",
            });
        }
        return events;
    }

    [Fact]
    public void BuildUserPrompt_RecentUseEndcap_RendersWhenUsePresent()
    {
        // Any recent Use re-surfaces the count facts-only in the
        // decision-proximate slot (no significance threshold in source).
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), BuildUseStream("name=\"Portal to Town Network\"", 5), null);
        Assert.Contains("## Recent Use", prompt);
        Assert.Contains("in your last 10 emitted goals you emitted Use on: Portal to Town Network x5", prompt);
        Assert.Contains("raw fact, not a recommendation", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentUseEndcap_RendersForSingleUse()
    {
        // No threshold: even a single recent Use renders the raw count.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), BuildUseStream("name=\"Old Wooden Door\"", 1), null);
        Assert.Contains("## Recent Use", prompt);
        Assert.Contains("Old Wooden Door x1", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentUseEndcap_OmittedWhenNoUse()
    {
        // No Use emissions at all: capsule omitted (nothing to re-surface).
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("## Recent Use", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentUseEndcap_RendersAtEndAfterLocationRecency()
    {
        // The salience value depends on the capsule sitting AFTER the mid-prompt
        // `## Location & recency` section where the same counts first render.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), BuildUseStream("name=\"Portal to Town Network\"", 4), null);
        var capsuleIdx = prompt.IndexOf("## Recent Use", System.StringComparison.Ordinal);
        var lrIdx = prompt.IndexOf("## Location & recency", System.StringComparison.Ordinal);
        Assert.True(lrIdx >= 0, "## Location & recency section missing");
        Assert.True(capsuleIdx > lrIdx, "capsule should render after ## Location & recency");
    }

    [Fact]
    public void BuildUserPrompt_RecentUseEndcap_PrefersNameWhenGuidPresent()
    {
        // A picker-resolved Use goal reads `target=guid=0x.. name="X"`; the
        // capsule keys identity by the guid but DISPLAYS the human name.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0),
            BuildUseStream("guid=0x7A9B4080 name=\"Portal to Town Network\"", 5), null);
        var capsuleIdx = prompt.IndexOf("## Recent Use", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx >= 0, "capsule missing");
        var capsule = prompt.Substring(capsuleIdx);
        Assert.Contains("Portal to Town Network x5", capsule);
    }

    [Fact]
    public void BuildUserPrompt_RecentUseEndcap_DisambiguatesSameNameDifferentGuid()
    {
        // Two DISTINCT objects sharing a display name (different guids) must NOT
        // collapse to one indistinguishable label — each gets a guid suffix,
        // mirroring the `## Recent Talk` and `## Location & recency` renders.
        var events = new EventStream();
        foreach (var (guid, n) in new[] { ("0x80000AAA", 2), ("0x80000BBB", 1) })
        {
            for (var i = 0; i < n; i++)
            {
                events.Append(new StreamEvent
                {
                    Sequence = 0,
                    Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                    Kind = EventKind.GoalEmitted,
                    GoalId = Guid.NewGuid(),
                    Text = $"Use target=guid={guid} name=\"Old Wooden Door\" item= source=llm:test",
                });
            }
        }
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), events, null);
        var capsuleIdx = prompt.IndexOf("## Recent Use", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx >= 0, "capsule missing");
        var capsule = prompt.Substring(capsuleIdx);
        Assert.Contains("Old Wooden Door (guid=0x80000AAA) x2", capsule);
        Assert.Contains("Old Wooden Door (guid=0x80000BBB) x1", capsule);
    }

    // ── ## Recent Give endcap (mirrors ## Recent Use) ────────────────
    // Live academy runs show the LLM re-emitting Give of the SAME item to the
    // SAME recipient many times after the give already succeeded and the item
    // left inventory. This re-surfaces the raw recent-Give counts (item →
    // recipient) at the decision-proximate end slot.
    private static EventStream BuildGiveStream(string itemSelector, string recipientSelector, int times)
    {
        var events = new EventStream();
        for (var i = 0; i < times; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(times - i),
                Kind = EventKind.GoalEmitted,
                GoalId = Guid.NewGuid(),
                Text = $"Give target={recipientSelector} item={itemSelector} source=llm:test",
            });
        }
        return events;
    }

    [Fact]
    public void BuildUserPrompt_RecentGiveEndcap_RendersWhenGivePresent()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0),
            BuildGiveStream("name=\"Calling Stone\"", "name=\"Society Greeter\"", 5), null);
        Assert.Contains("## Recent Give", prompt);
        Assert.Contains("in your last 10 emitted goals you emitted Give on: Calling Stone to Society Greeter x5", prompt);
        Assert.Contains("raw fact, not a recommendation", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentGiveEndcap_RendersForSingleGive()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0),
            BuildGiveStream("name=\"Oil of Rendering\"", "name=\"Jonathan\"", 1), null);
        Assert.Contains("## Recent Give", prompt);
        Assert.Contains("Oil of Rendering to Jonathan x1", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentGiveEndcap_OmittedWhenNoGive()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("## Recent Give", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentGiveEndcap_KeysByItemGuidDisplaysName()
    {
        // A resolved Give goal reads `item=guid=0x.. name="X"`; the capsule keys
        // identity by the item guid but DISPLAYS the human name and recipient.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0),
            BuildGiveStream("guid=0x80007FD8 name=\"Calling Stone\"", "guid=0x8000814F name=\"Society Greeter\"", 4), null);
        var capsuleIdx = prompt.IndexOf("## Recent Give", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx >= 0, "capsule missing");
        var capsule = prompt.Substring(capsuleIdx);
        Assert.Contains("Calling Stone to Society Greeter x4", capsule);
    }

    [Fact]
    public void BuildUserPrompt_RecentGiveEndcap_IndependentOfRecentUse()
    {
        // A Give emission must NOT show up under ## Recent Use and vice versa —
        // the two capsules parse distinct verbs.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0),
            BuildGiveStream("name=\"Calling Stone\"", "name=\"Society Greeter\"", 3), null);
        Assert.Contains("## Recent Give", prompt);
        Assert.DoesNotContain("## Recent Use", prompt);
    }

    // ── ## Recent Pickup endcap (mirrors ## Recent Use) ──────────────
    // Live academy runs (gpt-4o-mini) show the LLM re-emitting Pickup of the SAME
    // un-acquirable ground item many times (0 inventory add). cp-2375's failed-
    // pickup annotation does NOT cover it (Pickup emits no WorldObjectInteracted
    // echo). This re-surfaces the raw recent-Pickup counts at the decision slot.
    private static EventStream BuildPickupStream(string itemSelector, int times)
    {
        var events = new EventStream();
        for (var i = 0; i < times; i++)
        {
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(times - i),
                Kind = EventKind.GoalEmitted,
                GoalId = Guid.NewGuid(),
                Text = $"Pickup target={itemSelector} item= source=llm:test",
            });
        }
        return events;
    }

    [Fact]
    public void BuildUserPrompt_RecentPickupEndcap_RendersWhenPickupPresent()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), BuildPickupStream("name=\"Bruised Apple\"", 6), null);
        Assert.Contains("## Recent Pickup", prompt);
        Assert.Contains("in your last 10 emitted goals you emitted Pickup on: Bruised Apple x6", prompt);
        Assert.Contains("raw fact, not a recommendation", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentPickupEndcap_RendersForSinglePickup()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), BuildPickupStream("name=\"Leather Cap\"", 1), null);
        Assert.Contains("## Recent Pickup", prompt);
        Assert.Contains("Leather Cap x1", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentPickupEndcap_OmittedWhenNoPickup()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("## Recent Pickup", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentPickupEndcap_KeysByItemGuidDisplaysName()
    {
        // A picker-resolved Pickup goal reads `target=guid=0x.. name="X"`; the
        // capsule keys by the guid but DISPLAYS the human name.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0),
            BuildPickupStream("guid=0x80008203 name=\"Bruised Apple\"", 5), null);
        var capsuleIdx = prompt.IndexOf("## Recent Pickup", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx >= 0, "capsule missing");
        Assert.Contains("Bruised Apple x5", prompt.Substring(capsuleIdx));
    }

    [Fact]
    public void BuildUserPrompt_RecentPickupEndcap_IndependentOfRecentUseAndGive()
    {
        // A Pickup emission must parse ONLY under ## Recent Pickup.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildXpWorld(69296, 0), BuildPickupStream("name=\"Bruised Apple\"", 3), null);
        Assert.Contains("## Recent Pickup", prompt);
        Assert.DoesNotContain("## Recent Use", prompt);
        Assert.DoesNotContain("## Recent Give", prompt);
    }

    [Fact]
    public void BuildUserPrompt_PriorityBand_OmitsInvestWhenNoUnspentXp()
    {
        // The priority-band phrase tracks the same unspent>0 gate as the rule and
        // the `## Self` cue, so a zero-unspent prompt carries no dangling
        // "invest unspent XP" reference to a rule that is no longer rendered.
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.DoesNotContain("invest unspent XP", prompt);
        Assert.Contains("5-6 fight/loot;", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SelfLine_CuesInvestWhenUnspentPositive()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 5475), new EventStream(), null);
        Assert.Contains("5475 unspent (available to invest NOW", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SelfLine_NoInvestCueWhenUnspentZero()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildXpWorld(69296, 0), new EventStream(), null);
        Assert.Contains("0 unspent", prompt);
        Assert.DoesNotContain("available to invest NOW", prompt);
    }

    // ---- Recent goal outcomes section (cp-2299) ----
    //
    // The bot's own GoalCompleted/GoalFailed lifecycle events get evicted
    // from the 25-event "## Recent events" tail by observe-firehose noise in
    // busy areas, so the LLM repeats long failed engagements (e.g. chasing a
    // fleeing/far mob whose Attack keeps timing out). Distill them into a
    // dedicated section, dedup by (kind, target), so a repeatedly-failing
    // goal surfaces once and clearly. Pure echo of own bookkeeping; the LLM
    // decides whether to retry.

    [Fact]
    public void BuildUserPrompt_RendersRecentGoalOutcomes_FailedWithTargetAndReason()
    {
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalFailed,
            Name = "The Chicken",
            Text = "Attack: selector resolved to no live object",
        });
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("## Recent goal outcomes", prompt);
        Assert.Contains("[FAILED]", prompt);
        Assert.Contains("target=\"The Chicken\"", prompt);
        Assert.Contains("Attack: selector resolved to no live object", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentGoalOutcomes_DedupsRepeatedIdenticalFailures()
    {
        // The same Attack on the same target failing three times in a row
        // must collapse to a single line (dedup by (Kind, Name)) so a stuck
        // engagement reads as one clear signal, not a flood.
        var es = new EventStream();
        for (int i = 0; i < 3; i++)
        {
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.GoalFailed,
                Name = "Young Mosswart",
                Text = "Attack: chase timed out",
            });
        }
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        var section = prompt.Substring(prompt.IndexOf("## Recent goal outcomes", StringComparison.Ordinal));
        // exactly one Young Mosswart failure line in the section
        var occurrences = section.Split("Young Mosswart").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void BuildUserPrompt_OmitsRecentGoalOutcomes_WhenNoLifecycleEvents()
    {
        // No GoalCompleted/GoalFailed events → section absent → zero
        // static-floor budget cost.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Text = "Jonathan: hi",
        });
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);
        Assert.DoesNotContain("## Recent goal outcomes", prompt);
    }

    [Fact]
    public void BuildUserPrompt_RecentGoalOutcomes_ShowsBothDoneAndFailedForSameTarget()
    {
        // A target that failed once then succeeded keys differently
        // ((GoalFailed, name) vs (GoalCompleted, completion-text)) so both
        // a [FAILED] and a [done] line appear.
        var es = new EventStream();
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalFailed,
            Name = "The Chicken",
            Text = "Attack: chase timed out",
        });
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalCompleted,
            Text = "Attack: action cycle done on 'The Chicken'",
        });
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), es, null);

        Assert.Contains("## Recent goal outcomes", prompt);
        Assert.Contains("[FAILED]", prompt);
        Assert.Contains("[done]", prompt);
        Assert.Contains("action cycle done on 'The Chicken'", prompt);
    }

    // ---- Current goal progress telemetry (cp-2300) ----
    //
    // Neutral own-geometry bookkeeping: surface the bot-to-target distance
    // trend of the active goal's pursued object so the LLM can judge whether to
    // continue the pursuit. The section renders raw numbers only — no judgment
    // label, no object-type knowledge, no autonomous behavior change.

    [Theory]
    [InlineData((int)GoalKind.Give, true)]
    [InlineData((int)GoalKind.Use, true)]
    [InlineData((int)GoalKind.Attack, true)]
    [InlineData((int)GoalKind.Pickup, true)]
    [InlineData((int)GoalKind.Wield, true)]
    [InlineData((int)GoalKind.GoTo, true)]
    [InlineData((int)GoalKind.Talk, true)]
    [InlineData((int)GoalKind.Wait, false)]
    [InlineData((int)GoalKind.Explore, false)]
    [InlineData((int)GoalKind.RaiseAttribute, false)]
    [InlineData((int)GoalKind.RaiseVital, false)]
    [InlineData((int)GoalKind.RaiseSkill, false)]
    [InlineData((int)GoalKind.Unknown, false)]
    public void IsObjectPursuitKind_OnlyTrueForWorldObjectPursuitVerbs(int kind, bool expected)
    {
        Assert.Equal(expected, LlmGoalPolicy.IsObjectPursuitKind((GoalKind)kind));
    }

    [Fact]
    public async Task ProposeGoal_DoesNotTrackProgress_ForExploreGoalMatchingVisibleObject()
    {
        // Regression (correctness review): Explore/Raise* carry a non-empty
        // target selector, so an IsEmpty-only gate would wrongly track them.
        // An Explore goal whose target name matches a visible object must NOT
        // produce a distance trend.
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = JsonSerializer.Serialize(new
        {
            goal_id = Guid.NewGuid().ToString(), kind = "Wait",
            target = new { name = "self" }, rationale = "stub", priority = 5,
        });
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

        VisibleObjectProjection Anywhere(float dist) => new()
        {
            Guid = 0x0000B001u, Name = "anywhere", Wcid = 1u, ItemType = 0x10u, Distance = dist,
        };
        // Explore goal whose target name collides with a visible object name.
        var goal = new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = "anywhere" } };
        var events = new EventStream();

        policy.ProposeGoal(BuildExitTokenWorld() with { Visible = new[] { Anywhere(40f) } }, events, goal);
        await policy.WaitForInFlightAsync();
        policy.ProposeGoal(BuildExitTokenWorld() with { Visible = new[] { Anywhere(40f) } }, events, goal);

        await Task.Delay(1700);

        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Text = "Someone: hi",
        });
        policy.ProposeGoal(BuildExitTokenWorld() with { Visible = new[] { Anywhere(45f) } }, events, goal);
        await policy.WaitForInFlightAsync();

        Assert.NotEmpty(requestBodies);
        Assert.DoesNotContain("## Current goal progress", requestBodies[^1]);
    }


    [Fact]
    public void BuildUserPrompt_RendersCurrentGoalProgress_IncreasingTrendPositiveNet()
    {
        var snap = new GoalProgressSnapshot("Chicken (guid=0x00009001)", new[] { 40f, 42f, 45f }, 12.0);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), null,
            stack: null, pickerActivity: null, explorationCandidates: null, goalProgress: snap);

        Assert.Contains("## Current goal progress", prompt);
        Assert.Contains("40.0u -> 42.0u -> 45.0u", prompt);
        Assert.Contains("net +5.0u", prompt);
        Assert.Contains("3 samples", prompt);
        Assert.Contains("Chicken (guid=0x00009001)", prompt);
    }

    [Fact]
    public void BuildUserPrompt_CurrentGoalProgress_DecreasingTrendNegativeNet()
    {
        var snap = new GoalProgressSnapshot("Drudge (guid=0x0000A001)", new[] { 30f, 20f, 10f }, 8.0);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), null,
            stack: null, pickerActivity: null, explorationCandidates: null, goalProgress: snap);

        Assert.Contains("## Current goal progress", prompt);
        Assert.Contains("30.0u -> 20.0u -> 10.0u", prompt);
        Assert.Contains("net -20.0u", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsCurrentGoalProgress_WhenNull()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("## Current goal progress", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsCurrentGoalProgress_WhenFewerThanTwoSamples()
    {
        // A single sample is not a trend — the section must stay hidden so it
        // adds zero prompt cost until a real trend exists.
        var snap = new GoalProgressSnapshot("X (guid=0x00000001)", new[] { 40f }, 0.0);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), null,
            stack: null, pickerActivity: null, explorationCandidates: null, goalProgress: snap);
        Assert.DoesNotContain("## Current goal progress", prompt);
    }

    [Fact]
    public async Task ProposeGoal_SamplesGoalTargetDistance_AcrossTicks_RendersTrend()
    {
        // End-to-end: two ProposeGoal ticks on an Attack goal whose target
        // moves 40u -> 45u away across a >1.5s sampling window must render the
        // distance trend in the next LLM prompt. Validates the full wire-up
        // (per-tick sampling -> guid-lock -> snapshot -> render).
        var requestBodies = new System.Collections.Generic.List<string>();
        var goalJson = JsonSerializer.Serialize(new
        {
            goal_id = Guid.NewGuid().ToString(),
            kind = "Wait",
            target = new { name = "self" },
            rationale = "stub",
            priority = 5,
        });
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

        const uint chickenGuid = 0x00009001u;
        VisibleObjectProjection Chicken(float dist) => new()
        {
            Guid = chickenGuid, Name = "Chicken", Wcid = 24937u, ItemType = 0x10u,
            Distance = dist, IsCreature = true, IsMonster = true, ObservedHostile = false,
        };
        var goal = new Goal { Kind = GoalKind.Attack, Target = new Selector { Name = "Chicken" } };

        var events = new EventStream();
        var world40 = BuildExitTokenWorld() with { Visible = new[] { Chicken(40f) } };

        // Tick 1 kicks the first LLM call; only one distance sample exists so
        // far, so body 1 has NO progress section yet.
        policy.ProposeGoal(world40, events, goal);
        await policy.WaitForInFlightAsync();
        policy.ProposeGoal(world40, events, goal); // consume call-1 result
        Assert.Single(requestBodies);
        Assert.DoesNotContain("## Current goal progress", requestBodies[0]);

        await Task.Delay(1700); // exceed GoalProgressMinSampleInterval (1.5s)

        // Tick 2: salient event wakes the LLM; target now 45u away -> 2nd
        // sample -> body 2 renders the trend. Same Attack goal is passed so the
        // tracking key (and the guid lock) stays stable across ticks.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.NpcDialog, Text = "Someone: hi",
        });
        var world45 = BuildExitTokenWorld() with { Visible = new[] { Chicken(45f) } };
        policy.ProposeGoal(world45, events, goal);
        await policy.WaitForInFlightAsync();

        Assert.Equal(2, requestBodies.Count);
        // The captured body is JSON, so ">" and "+" are unicode-escaped; assert
        // on the unescaped substrings (both distances, label, sample count).
        var prompt = requestBodies[^1];
        Assert.Contains("## Current goal progress", prompt);
        Assert.Contains("40.0u", prompt);
        Assert.Contains("45.0u", prompt);
        Assert.Contains("2 samples", prompt);
        Assert.Contains("Chicken (guid=0x00009001)", prompt);
    }

    // RAW per-kind counts only — NO danger/safe label, NO avoidance advice
    // baked in by source (the LLM owns the avoidance decision via the
    // COMBAT SAFETY rule). It renders nothing when there is no history.
    [Fact]
    public void BuildUserPrompt_CombatHistory_RendersRawCountsNoLabel()
    {
        var world = BuildExitTokenWorld() with
        {
            CombatHistory = new[]
            {
                new CombatHistoryEntry("Drudge Skulker", 12345u, Kills: 0, Deaths: 2,
                    NearDeaths: 1, Fights: 3, LastOutcome: "death"),
                new CombatHistoryEntry("Chicken", 24937u, Kills: 4, Deaths: 0,
                    NearDeaths: 0, Fights: 4, LastOutcome: "kill"),
            },
        };
        var events = new EventStream();
        var p = LlmGoalPolicy.BuildUserPrompt(world, events, null);

        Assert.Contains("combat history (your own outcomes", p);
        Assert.Contains("Drudge Skulker", p);
        Assert.Contains("deaths 2", p);
        Assert.Contains("near-deaths 1", p);
        Assert.Contains("Chicken", p);
        Assert.Contains("kills 4", p);
        // No source-side danger/safety LABEL leaked into the render.
        Assert.DoesNotContain("DANGEROUS", p);
        Assert.DoesNotContain("dangerous", p);
        Assert.DoesNotContain("SAFE to", p);
    }

    [Fact]
    public void BuildUserPrompt_NoCombatHistory_RendersNothing()
    {
        var world = BuildExitTokenWorld() with { CombatHistory = null };
        var events = new EventStream();
        var p = LlmGoalPolicy.BuildUserPrompt(world, events, null);
        Assert.DoesNotContain("combat history (your own outcomes", p);
    }

    // death-recency: the deaths line carries how long ago the most recent
    // in-session death was (when known), so the LLM can tell a fresh respawn
    // from an old cumulative count. Raw telemetry — no urgency baked in.
    [Fact]
    public void BuildUserPrompt_DeathRecency_ShownWhenProvided()
    {
        var world = BuildExitTokenWorld();
        world = world with { Self = world.Self with { NumDeaths = 3 } };
        var p = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), currentGoal: null, stack: null,
            pickerActivity: null, explorationCandidates: null,
            secondsSinceLastDeath: 15);
        Assert.Contains("deaths (server-tracked): 3 (most recent observed ~15s ago)", p);
        // No source-side urgency/recommendation leaked into the render.
        Assert.DoesNotContain("should", p.Split('\n').First(l => l.Contains("deaths (server-tracked)")));
    }

    [Fact]
    public void BuildUserPrompt_DeathRecency_OmittedWhenNull()
    {
        var world = BuildExitTokenWorld();
        world = world with { Self = world.Self with { NumDeaths = 3 } };
        var p = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), currentGoal: null, stack: null,
            pickerActivity: null, explorationCandidates: null,
            secondsSinceLastDeath: null);
        Assert.Contains("deaths (server-tracked): 3", p);
        Assert.DoesNotContain("most recent observed", p);
    }

    [Fact]
    public void BuildUserPrompt_DeathRecency_NoDeathsLineWhenNumDeathsNull()
    {
        // NumDeaths unknown (null) → no deaths line at all, regardless of recency.
        var world = BuildExitTokenWorld(); // Self.NumDeaths is null by default
        var p = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), currentGoal: null, stack: null,
            pickerActivity: null, explorationCandidates: null,
            secondsSinceLastDeath: 10);
        Assert.DoesNotContain("deaths (server-tracked)", p);
    }

    // death-recency tracking logic (the stateful half, reflected directly so
    // the anchoring/increment-only/no-retro-stamp invariants are asserted
    // without going through the async LLM HTTP path or prompt coalescing).
    private static LlmGoalPolicy MakeBarePolicyForTracking()
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        return new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo());
    }

    private static void InvokeUpdateDeathRecency(LlmGoalPolicy policy, int? numDeaths, DateTimeOffset nowUtc)
    {
        var m = typeof(LlmGoalPolicy).GetMethod(
            "UpdateDeathRecencyTracking",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(policy, new object?[] { numDeaths, nowUtc });
    }

    private static int? InvokeSecondsSinceLastOwnDeath(LlmGoalPolicy policy, DateTimeOffset nowUtc)
    {
        var m = typeof(LlmGoalPolicy).GetMethod(
            "SecondsSinceLastOwnDeath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (int?)m.Invoke(policy, new object?[] { nowUtc });
    }

    [Fact]
    public void DeathRecency_FirstObservation_AnchorsOnly_NoRecency()
    {
        // A bot that logs in with a pre-existing cumulative NumDeaths (from
        // prior sessions) must NOT be treated as having just died.
        var policy = MakeBarePolicyForTracking();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        InvokeUpdateDeathRecency(policy, 5, t0);
        Assert.Null(InvokeSecondsSinceLastOwnDeath(policy, t0));
        Assert.Null(InvokeSecondsSinceLastOwnDeath(policy, t0.AddSeconds(10)));
    }

    [Fact]
    public void DeathRecency_Increment_Stamps_RecencyCounts()
    {
        var policy = MakeBarePolicyForTracking();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        InvokeUpdateDeathRecency(policy, 5, t0);              // anchor
        InvokeUpdateDeathRecency(policy, 6, t0.AddSeconds(30)); // real death
        Assert.Equal(0, InvokeSecondsSinceLastOwnDeath(policy, t0.AddSeconds(30)));
        Assert.Equal(60, InvokeSecondsSinceLastOwnDeath(policy, t0.AddSeconds(90)));
    }

    [Fact]
    public void DeathRecency_EqualDecreaseNull_DoNotStamp_NorCorruptAnchor()
    {
        var policy = MakeBarePolicyForTracking();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        InvokeUpdateDeathRecency(policy, 5, t0);                 // anchor at 5
        InvokeUpdateDeathRecency(policy, 5, t0.AddSeconds(10));  // equal -> no stamp
        Assert.Null(InvokeSecondsSinceLastOwnDeath(policy, t0.AddSeconds(10)));
        InvokeUpdateDeathRecency(policy, 4, t0.AddSeconds(20));  // decrease -> no stamp
        Assert.Null(InvokeSecondsSinceLastOwnDeath(policy, t0.AddSeconds(20)));
        InvokeUpdateDeathRecency(policy, null, t0.AddSeconds(30)); // null -> early return
        Assert.Null(InvokeSecondsSinceLastOwnDeath(policy, t0.AddSeconds(30)));
        // The null tick must not have corrupted the anchor: a later increment
        // over the last real count (4) still stamps a fresh death.
        InvokeUpdateDeathRecency(policy, 6, t0.AddSeconds(40));
        Assert.Equal(0, InvokeSecondsSinceLastOwnDeath(policy, t0.AddSeconds(40)));
    }

    // combat-record-nearest: the nearest-monster line in "## Combat
    // readiness" is annotated INLINE with the bot's own raw record for
    // that monster KIND, matched by exact identity (wcid or normalized
    // name). Raw counts only — no danger label.
    [Fact]
    public void BuildUserPrompt_NearestMonster_AnnotatedWithOwnRecord_ByWcid()
    {
        var world = BuildAcademyCombatWorld() with
        {
            CombatHistory = new[]
            {
                // Same wcid as the visible Sparring Golem (12698).
                new CombatHistoryEntry("Sparring Golem", 12698u, Kills: 1, Deaths: 2,
                    NearDeaths: 0, Fights: 3, LastOutcome: "death"),
            },
        };
        var events = new EventStream();
        var p = LlmGoalPolicy.BuildUserPrompt(world, events, null);

        // The nearest-monster line carries the inline record.
        Assert.Contains("nearest monster: Sparring Golem", p);
        Assert.Contains("your record: fights 3, kills 1, deaths 2, near-deaths 0, ineffective 0, last death", p);
    }

    [Fact]
    public void BuildUserPrompt_NearestMonster_NoMatch_NoAnnotation()
    {
        var world = BuildAcademyCombatWorld() with
        {
            CombatHistory = new[]
            {
                // Different kind — must NOT annotate the Sparring Golem.
                new CombatHistoryEntry("Drudge Skulker", 7u, Kills: 0, Deaths: 1,
                    NearDeaths: 0, Fights: 1, LastOutcome: "death"),
            },
        };
        var events = new EventStream();
        var p = LlmGoalPolicy.BuildUserPrompt(world, events, null);
        Assert.Contains("nearest monster: Sparring Golem", p);
        // No record annotation appended to the nearest-monster line.
        var line = p.Split('\n').First(l => l.Contains("nearest monster: Sparring Golem"));
        Assert.DoesNotContain("your record", line);
    }

    // ---- FindCombatRecord pure matcher ----

    [Fact]
    public void FindCombatRecord_MatchesByWcid()
    {
        var hist = new[]
        {
            new CombatHistoryEntry("Sparring Golem", 12698u, 1, 2, 0, 3, "death"),
        };
        var rec = LlmGoalPolicy.FindCombatRecord(hist, 12698u, "Totally Different Name");
        Assert.NotNull(rec);
        Assert.Equal("Sparring Golem", rec!.Name);
    }

    [Fact]
    public void FindCombatRecord_MatchesByNormalizedName_WhenNoWcidEitherSide()
    {
        var hist = new[]
        {
            new CombatHistoryEntry("Drudge Skulker", null, 0, 1, 0, 1, "death"),
        };
        var rec = LlmGoalPolicy.FindCombatRecord(hist, null, "  drudge   skulker ");
        Assert.NotNull(rec);
        Assert.Equal("Drudge Skulker", rec!.Name);
    }

    [Fact]
    public void FindCombatRecord_MatchesByName_OnWcidVsNameOnlyAsymmetry()
    {
        // cp-2275: history keyed by wcid; the visible row carries the same
        // display name but no wcid. The wire DOES assign different wcids to
        // same-named variants (aggro vs no-aggro), and the LLM reasons by
        // name, so this MUST surface the death record (was deliberately
        // omitted before, which orphaned hard-won death memory).
        var hist = new[]
        {
            new CombatHistoryEntry("Drudge Skulker", 7u, 0, 1, 0, 1, "death"),
        };
        var rec = LlmGoalPolicy.FindCombatRecord(hist, null, "Drudge Skulker");
        Assert.NotNull(rec);
        Assert.Equal(1, rec!.Deaths);
    }

    [Fact]
    public void FindCombatRecord_AggregatesAcrossWcidVariants_SharingName()
    {
        // The live cp-2275 scenario: died to the aggro "Drudge Skulker"
        // (wcid 7) and killed the no-aggro one (wcid 19257) twice; both
        // share the display name. A visible no-aggro Skulker must see the
        // COMBINED record (incl. the death) so the LLM is warned.
        var hist = new[]
        {
            new CombatHistoryEntry("Drudge Skulker", 19257u, 2, 0, 0, 2, "kill"),
            new CombatHistoryEntry("Drudge Skulker", 7u, 0, 1, 1, 1, "death"),
        };
        var rec = LlmGoalPolicy.FindCombatRecord(hist, 19257u, "Drudge Skulker");
        Assert.NotNull(rec);
        Assert.Equal(2, rec!.Kills);
        Assert.Equal(1, rec.Deaths);
        Assert.Equal(1, rec.NearDeaths);
        Assert.Equal(3, rec.Fights);
        // LastOutcome comes from the FIRST (most-recent) matched row.
        Assert.Equal("kill", rec.LastOutcome);
    }

    [Fact]
    public void FindCombatRecord_DoesNotAggregate_DifferentNames()
    {
        // A sibling Drudge with a DIFFERENT name must NOT fold into the
        // record (no name-family/substring matching).
        var hist = new[]
        {
            new CombatHistoryEntry("Drudge Slinker", 19258u, 0, 1, 0, 1, "death"),
            new CombatHistoryEntry("Drudge Skulker", 7u, 0, 1, 0, 1, "death"),
        };
        var rec = LlmGoalPolicy.FindCombatRecord(hist, 99999u, "Drudge Skulker");
        Assert.NotNull(rec);
        Assert.Equal(1, rec!.Deaths); // only the Skulker row, not the Slinker
    }

    [Fact]
    public void FindCombatRecord_NoSubstringMatch()
    {
        var hist = new[]
        {
            new CombatHistoryEntry("Drudge", null, 1, 0, 0, 1, "kill"),
        };
        Assert.Null(LlmGoalPolicy.FindCombatRecord(hist, null, "Drudge Skulker"));
    }

    [Fact]
    public void FindCombatRecord_NullWhenNoHistoryOrNoIdentity()
    {
        Assert.Null(LlmGoalPolicy.FindCombatRecord(null, 7u, "X"));
        var hist = new[] { new CombatHistoryEntry("X", 7u, 1, 0, 0, 1, "kill") };
        Assert.Null(LlmGoalPolicy.FindCombatRecord(hist, null, null));
        Assert.Null(LlmGoalPolicy.FindCombatRecord(hist, null, "(unknown)"));
    }

    [Fact]
    public void FormatCombatRecordFor_RendersAggregatedCounts()
    {
        var hist = new[]
        {
            new CombatHistoryEntry("Drudge Skulker", 19257u, 2, 0, 0, 2, "kill"),
            new CombatHistoryEntry("Drudge Skulker", 7u, 0, 1, 0, 1, "death"),
        };
        var s = LlmGoalPolicy.FormatCombatRecordFor(hist, 19257u, "Drudge Skulker");
        Assert.Equal(" [your record: fights 3, kills 2, deaths 1, near-deaths 0, ineffective 0, last kill]", s);
    }

    [Fact]
    public void FormatCombatRecordFor_RendersIneffectiveCount()
    {
        // A kind the bot fought but never killed (out-defended, abandoned)
        // surfaces its raw ineffective count so the LLM can avoid it up front.
        var hist = new[]
        {
            new CombatHistoryEntry("Auroch Bull", 20u, 0, 0, 0, 2, "ineffective", Ineffective: 2),
        };
        var s = LlmGoalPolicy.FormatCombatRecordFor(hist, 20u, "Auroch Bull");
        Assert.Contains("kills 0", s);
        Assert.Contains("ineffective 2", s);
    }

    [Fact]
    public void FormatCombatRecordFor_EmptyWhenNoMatch()
    {
        var hist = new[] { new CombatHistoryEntry("Cow", 14u, 1, 0, 0, 1, "kill") };
        Assert.Equal("", LlmGoalPolicy.FormatCombatRecordFor(hist, 7u, "Drudge Skulker"));
    }

    // ---- IsBeatenKind shared verdict (fallback skip + frontier mob-bias) ----

    [Fact]
    public void IsBeatenKind_FalseWhenNoRecord()
    {
        Assert.False(LlmGoalPolicy.IsBeatenKind(null, 7u, "X", 5));
        var hist = new[] { new CombatHistoryEntry("Cow", 14u, 1, 0, 0, 1, "kill") };
        Assert.False(LlmGoalPolicy.IsBeatenKind(hist, 7u, "Drudge Skulker", 5)); // no match
    }

    [Fact]
    public void IsBeatenKind_FalseWhenHasKill()
    {
        var hist = new[] { new CombatHistoryEntry("Cow", 14u, 3, 1, 0, 4, "kill") };
        Assert.False(LlmGoalPolicy.IsBeatenKind(hist, 14u, "Cow", 5)); // Kills>0 => not beaten
    }

    [Fact]
    public void IsBeatenKind_TrueWhenLostNoKill_RegardlessOfLevelForDeath()
    {
        // A death loss stays beaten at any level (no MaxLossBotLevel re-test).
        var hist = new[] { new CombatHistoryEntry("Drudge Skulker", 19257u, 0, 2, 1, 3, "death", MaxLossBotLevel: 3) };
        Assert.True(LlmGoalPolicy.IsBeatenKind(hist, 19257u, "Drudge Skulker", 99));
    }

    [Fact]
    public void IsBeatenKind_NonLethalLoss_FalseAfterLevelUp_TrueBefore()
    {
        // Deaths==0, only near-death/ineffective, lost at level 3.
        var hist = new[] { new CombatHistoryEntry("Mite Scion", 22600u, 0, 0, 3, 3, "near-death", MaxLossBotLevel: 3) };
        Assert.False(LlmGoalPolicy.IsBeatenKind(hist, 22600u, "Mite Scion", 8)); // 8 > 3 -> re-test
        Assert.True(LlmGoalPolicy.IsBeatenKind(hist, 22600u, "Mite Scion", 3));  // 3 not > 3 -> beaten
        Assert.True(LlmGoalPolicy.IsBeatenKind(hist, 22600u, "Mite Scion", null)); // unknown level -> beaten
    }

    [Fact]
    public void IsBeatenKind_AggregatesSameName_KillOnSiblingUnbeats()
    {
        // Same display name, different wcids: a loss on the visible wcid but a
        // kill on a sibling -> aggregate Kills>0 -> not beaten.
        var hist = new[]
        {
            new CombatHistoryEntry("Drudge Skulker", 19257u, 0, 2, 0, 2, "death", MaxLossBotLevel: 3),
            new CombatHistoryEntry("Drudge Skulker", 7u, 5, 0, 0, 5, "kill"),
        };
        Assert.False(LlmGoalPolicy.IsBeatenKind(hist, 19257u, "Drudge Skulker", 3));
    }

    [Fact]
    public void FormatThreatSummary_NullWhenNoMonsters()
    {
        Assert.Null(LlmGoalPolicy.FormatThreatSummary(0, 0));
        Assert.Null(LlmGoalPolicy.FormatThreatSummary(-1, 0));
    }

    [Fact]
    public void FormatThreatSummary_SingleNonHostile()
    {
        // Non-hostile monsters are still valid XP targets; the line reads
        // "0 attacking you now" (a neutral count) rather than the old
        // "none currently hostile" which the LLM mis-read as "no target".
        Assert.Equal(
            "- monsters in view: 1 (0 attacking you now)",
            LlmGoalPolicy.FormatThreatSummary(1, 0));
    }

    [Fact]
    public void FormatThreatSummary_ClusterWithHostiles()
    {
        // The cp-2296 death scenario: 4 monsters clustered, 3 already
        // attacking. The LLM should read this as "swarmed -> disengage".
        Assert.Equal(
            "- monsters in view: 4 (3 actively HOSTILE (attacking you now))",
            LlmGoalPolicy.FormatThreatSummary(4, 3));
    }

    [Fact]
    public void FormatThreatSummary_SingleHostile()
    {
        Assert.Equal(
            "- monsters in view: 1 (1 actively HOSTILE (attacking you now))",
            LlmGoalPolicy.FormatThreatSummary(1, 1));
    }

    [Fact]
    public void FormatThreatSummary_MultipleNonHostile_ReadsZeroAttacking()
    {
        // The academy Sparring-Golem case (cp-2326): several non-hostile
        // monsters in view must render "0 attacking you now", never the
        // old reassuring "none currently hostile" wording.
        Assert.Equal(
            "- monsters in view: 7 (0 attacking you now)",
            LlmGoalPolicy.FormatThreatSummary(7, 0));
        Assert.DoesNotContain(
            "none currently hostile",
            LlmGoalPolicy.FormatThreatSummary(7, 0));
    }

    [Fact]
    public void BuildUserPrompt_NonHostileRule_RendersWhenMonsterVisible()
    {
        // cp-2326: the prompt must explicitly tell the LLM that a visible
        // non-hostile monster is still a valid XP target, so it stops
        // exploring "to find monsters" while monsters are already in view.
        // cp-2335: the rule renders ONLY when a monster is actually in view
        // (the wire fact it references). A non-hostile Mob suffices.
        var world = BuildExitTokenWorld() with
        {
            Visible = new[] { Mob(0x901u, "Black Rabbit", 2566u) },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);
        Assert.Contains("NON-HOSTILE IS NOT NON-TARGET", prompt);
        Assert.Contains("0 attacking you now", prompt);
    }

    [Fact]
    public void BuildUserPrompt_NonHostileRule_CountersExploreForNewObjectiveWhenMonsterVisible()
    {
        // cp-2395 (criterion 2): live open-world failure — with monsters in view
        // AND no active objective, the bot Explored "to find a new objective/
        // area" PAST killable Drudges instead of hunting them. The rule must now
        // state that hunting a visible monster IS a valid objective, so being
        // objective-less is not a reason to wander past it.
        var world = BuildExitTokenWorld() with
        {
            Visible = new[] { Mob(0x901u, "Drudge Skulker", 19257u) },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null);

        var idx = prompt.IndexOf("NON-HOSTILE IS NOT NON-TARGET", System.StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var lineEnd = prompt.IndexOf('\n', idx);
        var line = lineEnd >= 0 ? prompt.Substring(idx, lineEnd - idx) : prompt.Substring(idx);
        Assert.Contains("NO active objective", line);
        Assert.Contains("to find a new objective", line);
        Assert.Contains("HUNTING it", line);
    }

    [Fact]
    public void BuildUserPrompt_NonHostileRule_OmittedWhenNoMonsterVisible()
    {
        // cp-2335: with no monster (or observed-hostile) in view the rule
        // references absent `nearest monster`/`monsters in view` telemetry,
        // so it is omitted to shrink the static preamble. BuildExitTokenWorld
        // shows only Jonathan (an npc, IsMonster unset).
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("NON-HOSTILE IS NOT NON-TARGET", prompt);
    }

    [Fact]
    public void BuildUserPrompt_StaticPreamble_CarriesNpcRepeatExhaustionRule()
    {
        // post-cp-2326: a level-1 bot that killed one monster then Talk-looped
        // an NPC (which rotated 2-3 canned lines) ~8x while killable monsters
        // stayed in view. The prompt must tell the LLM that a repeating/rotating
        // conversation is exhausted and to pivot to a NON-Talk verb (Attack a
        // visible monster, Use/Give/Pickup, or Explore) rather than re-Talking.
        // cp-2400: the rule is now GATED on an observed Talk-goal repeat (it is
        // only actionable once the bot re-Talks the SAME NPC), so seed two Talk
        // emissions for the same NPC to render it.
        var events = new EventStream();
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Talk target=name=\"Buckminster\" item= source=llm:test" });
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Talk target=name=\"Buckminster\" item= source=llm:test" });
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), events, null);
        Assert.Contains("NPC REPEAT EXHAUSTION", prompt);
        // keys off the existing neutral repeated-count telemetry, not a name/wcid
        Assert.Contains("tags an NPC's dialog `repeated xN`", prompt);
        // cp-2328: also cites the recency Talk-emission signal so a SILENT NPC
        // (which emits no dialog, so `repeated xN` can never fire) is covered
        Assert.Contains("recent Talk emissions: <that NPC> xN", prompt);
        // the pivot must NOT be satisfied by re-Talking the same NPC
        Assert.Contains("Re-Talking the same NPC is NEVER", prompt);
        // Attack is an allowed pivot, but only AFTER exhaustion
        Assert.Contains("only AFTER the conversation is exhausted", prompt);
    }

    [Fact]
    public void BuildUserPrompt_NpcRepeatExhaustionRule_OmittedWhenNoTalkRepeat()
    {
        // cp-2400: with no repeated Talk in the recent emissions the rule is
        // inapplicable preamble noise, so it is omitted (the Motor's mechanical
        // talk-loop guards backstop loop-breaking regardless). A SINGLE Talk of
        // an NPC is not yet a repeat; a fresh stream has none at all.
        var single = new EventStream();
        single.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Talk target=name=\"Buckminster\" item= source=llm:test" });
        Assert.DoesNotContain("NPC REPEAT EXHAUSTION",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), single, null));
        Assert.DoesNotContain("NPC REPEAT EXHAUSTION",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null));
    }

    [Fact]
    public void BuildUserPrompt_MainLoopBreakRule_RendersOnTalkRepeat()
    {
        // cp-2401: the action-repeat LOOP-BREAK rule renders when the SAME Talk
        // target repeats. Assert a clause UNIQUE to the main rule (the town-stuck
        // LOOP-BREAK, gated on !monsterInView, also renders here but lacks it).
        var events = new EventStream();
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Talk target=name=\"Buckminster\" item= source=llm:test" });
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Talk target=name=\"Buckminster\" item= source=llm:test" });
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), events, null);
        Assert.Contains("an `ActionRejected` told you to retry", prompt);
        Assert.Contains("not-yet-talked visible NPC", prompt);
    }

    [Fact]
    public void BuildUserPrompt_MainLoopBreakRule_RendersOnUseRepeat()
    {
        // A world-object Use repeat (cp-2401's (c) sub-case) also renders it.
        var events = new EventStream();
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Use target=name=\"Door\" item= source=llm:test" });
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Use target=name=\"Door\" item= source=llm:test" });
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), events, null);
        Assert.Contains("an `ActionRejected` told you to retry", prompt);
    }

    [Fact]
    public void BuildUserPrompt_MainLoopBreakRule_OmittedWhenNoActionRepeat()
    {
        // cp-2401: with no repeated Talk/Use the action-repeat LOOP-BREAK is
        // omitted (its 3 sub-cases are all Motor-backstopped). A single Use or
        // a Talk+Use of the same target (distinct verbs) is not a repeat.
        var single = new EventStream();
        single.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Use target=name=\"Door\" item= source=llm:test" });
        single.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalEmitted, Text = "Talk target=name=\"Door\" item= source=llm:test" });
        Assert.DoesNotContain("an `ActionRejected` told you to retry",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), single, null));
        Assert.DoesNotContain("an `ActionRejected` told you to retry",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null));
    }

    [Fact]
    public void BuildUserPrompt_BlockedTargetsRule_RendersOnBlockedOrUnreachableRejection()
    {
        // cp-2402: the BLOCKED-targets rule renders when a recent ActionRejected
        // carries the "Blocked" or "Unreachable" ErrorLabel (the Motor's geometry
        // rejection), the only situation in which the rule is actionable.
        var blocked = new EventStream();
        blocked.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorLabel = "Blocked" });
        Assert.Contains("BLOCKED targets", LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), blocked, null));

        var unreachable = new EventStream();
        unreachable.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorLabel = "Unreachable" });
        Assert.Contains("BLOCKED targets", LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), unreachable, null));
    }

    [Fact]
    public void BuildUserPrompt_BlockedTargetsRule_OmittedWhenNoBlockedRejection()
    {
        // No rejection at all, and an UNRELATED rejection label, both omit the
        // rule (the Motor routes around blocked geometry mechanically anyway).
        Assert.DoesNotContain("BLOCKED targets",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null));
        var other = new EventStream();
        other.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorLabel = "OutOfRange" });
        Assert.DoesNotContain("BLOCKED targets",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), other, null));
    }

    // cp-2407: the reactive ActionRejected-recovery guidance is gated on ANY
    // recent rejection (HasRecentActionRejected, broader than the cp-2402
    // Blocked/Unreachable gate); the pre-emptive double-click-self guidance
    // stays always-on.

    [Fact]
    public void BuildUserPrompt_ActionRejectedRecoveryRule_RendersOnAnyRecentRejection()
    {
        // Any ActionRejected label (even an unrelated one) makes the recovery
        // guidance actionable — it is broader than the BLOCKED gate.
        var rej = new EventStream();
        rej.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorLabel = "OutOfRange" });
        Assert.Contains("Do NOT immediately retry the same combo",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), rej, null));
    }

    [Fact]
    public void BuildUserPrompt_ActionRejectedRecoveryRule_OmittedWhenNoRejection()
        => Assert.DoesNotContain("Do NOT immediately retry the same combo",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null));

    [Fact]
    public void BuildUserPrompt_ActionRejectedRecoveryRule_DecaysAfterWindow()
    {
        // A rejection older than the recovery window has decayed -> the reactive
        // guidance is gated off again so it stops costing prompt bytes.
        var stale = new EventStream();
        stale.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(120),
            Kind = EventKind.ActionRejected,
            ErrorLabel = "OutOfRange",
        });
        Assert.DoesNotContain("Do NOT immediately retry the same combo",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), stale, null));
    }

    [Fact]
    public void BuildUserPrompt_DoubleClickSelfGuidance_AlwaysPresent()
    {
        // Pre-emptive: it must render even with NO rejection (the bot should
        // self-Use an activatable item BEFORE the Give/Talk it gates).
        Assert.Contains("Use'd on yourself FIRST",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null));
        var rej = new EventStream();
        rej.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.ActionRejected, ErrorLabel = "OutOfRange" });
        Assert.Contains("Use'd on yourself FIRST",
            LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), rej, null));
    }

    // Semantic canary: compaction must remove RATIONALE/duplication only, NOT
    // the concrete trigger->action clauses or forbidden-action guidance that
    // each RULES bullet encodes (every one was added to fix an observed bot
    // failure). Assert distinctive clauses, not just section headings, so a
    // trim that drops the actual instruction fails the build.
    [Fact]
    public void BuildUserPrompt_RulesRetainCriticalBehaviorClauses()
    {
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var p = LlmGoalPolicy.BuildUserPrompt(world, events, null);

        // anti-hallucination + selector preference
        Assert.Contains("Reason ONLY from the observed world", p);
        Assert.Contains("NAME selectors over wcid", p);
        // short_desc clue + Give arity
        Assert.Contains("short_desc", p);
        // rejection handling + blocked-combo prerequisite + self-use unlock
        Assert.Contains("Do NOT", p);
        Assert.Contains("double-click", p);
        // combat target discrimination + proactive leveling
        Assert.Contains("LEVELING is core progress", p);
        Assert.Contains("monster", p);
        // self-arming before optional combat
        Assert.Contains("SELF-ARM before fighting", p);
        Assert.Contains("UNARMED", p);
        // NOTE: the COMBAT SAFETY & PACE rule (disengage / avoid the killer
        // kind / absolute-HP interpretation) is now conditional — it renders
        // only when a monster is in view OR a fight is active (cp-2369), which
        // BuildExitTokenWorld has neither of, so its clauses are not asserted
        // here. Its present/absent rendering + critical clauses are covered by
        // the dedicated BuildUserPrompt_CombatSafetyRule_* tests.
        // NOTE: the corpse-looting rule ("NEVER skip a fresh corpse") is now
        // conditional — it renders only when a corpse is visible (cp-2331-loot),
        // which BuildExitTokenWorld has none of, so it is not asserted here. Its
        // present/absent rendering is covered by dedicated tests above.
        // door / passage traversal
        Assert.Contains("PASSAGE-OPENED is not progress", p);
        // loop-break + town-stuck + hunt excursion
        Assert.Contains("LOOP-BREAK", p);
        // NOTE: the NPC REPEAT EXHAUSTION rule (post-cp-2326) is now conditional
        // — cp-2400 gates it on an observed Talk-goal REPEAT in the recent
        // emissions (it is only actionable once the bot re-Talks the SAME NPC),
        // which this fresh-EventStream world has none of, so its clauses
        // ("NPC REPEAT EXHAUSTION", "Re-Talking the same NPC is NEVER") are not
        // asserted here. Its present/absent rendering is covered by the dedicated
        // BuildUserPrompt_*NpcRepeatExhaustion* tests.
        // NOTE: the MAIN LOOP-BREAK rule (action-repeat sub-cases a/b/c) is now
        // conditional too — cp-2401 gates it on an observed Talk-OR-Use goal
        // repeat, which this fresh-EventStream world has none of, so its clauses
        // ("not-yet-talked visible NPC", "an ActionRejected told you to retry")
        // are not asserted here. The string "LOOP-BREAK" above still renders via
        // the separate town-stuck LOOP-BREAK rule (gated on !monsterInView, which
        // BuildExitTokenWorld satisfies). The main rule's present/absent rendering
        // is covered by the dedicated BuildUserPrompt_*LoopBreak* tests.
        Assert.Contains("town-stuck", p);
        Assert.Contains("HUNT EXCURSION", p);
        Assert.Contains("KEEP emitting it", p);
        // tapped-out: corrected leveling steer (cp-2270) — prefer beatable, no "tougher for XP"
        Assert.Contains("monsters you can DEFEAT", p);
        Assert.Contains("do NOT chase `tougher` monsters for more XP", p);
        // NOTE: the BLOCKED-targets rule is now conditional — cp-2402 gates it
        // on a recent ActionRejected `Blocked`/`Unreachable` (it only tells the
        // LLM how to react to one), which this fresh-EventStream world has none
        // of, so "BLOCKED targets" is not asserted here. Its present/absent
        // rendering is covered by the dedicated BuildUserPrompt_*BlockedTargets*
        // tests.
        // pursue-unseen, server precedence
        Assert.Contains("PURSUE UNSEEN OBJECTIVES", p);
        Assert.Contains("SERVER-INSTRUCTION PRECEDENCE", p);
        Assert.Contains("FINISH MULTI-STEP DIRECTIVES", p);
        // NOTE: the AUTONOMOUS PICKER and EXPLORATION CANDIDATES rules are now
        // conditional — they render only when their `## ...` section is present
        // (BuildExitTokenWorld supplies neither), so they are intentionally NOT
        // asserted in this always-on canary. Their present/absent rendering is
        // covered by the dedicated tests below.
    }

    [Fact]
    public void BuildUserPrompt_AutonomousPickerRule_OmittedWhenNoPickerActivity()
    {
        // With no picker activity the section is absent, so the rule that
        // explains it carries no information and must be omitted (prompt-size +
        // salience). BuildExitTokenWorld supplies a null pickerActivity.
        var p = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("AUTONOMOUS PICKER", p);
    }

    [Fact]
    public void BuildUserPrompt_AutonomousPickerRule_PresentWhenPickerActivity()
    {
        var activity = new PickerActivity
        {
            TargetGuid = 0x80000099u,
            TargetName = "Some Object",
            Source = "in-range",
            Reason = "test",
            StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-3),
            Arrived = false,
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: activity);
        Assert.Contains("AUTONOMOUS PICKER", p);
    }

    [Fact]
    public void BuildUserPrompt_ExplorationCandidatesRule_OmittedWhenNone()
    {
        // No exploration candidates -> no `## Exploration candidates` section ->
        // omit the rule that explains it.
        var p = LlmGoalPolicy.BuildUserPrompt(BuildExitTokenWorld(), new EventStream(), null);
        Assert.DoesNotContain("EXPLORATION CANDIDATES", p);
    }

    [Fact]
    public void BuildUserPrompt_ExplorationCandidatesRule_PresentWhenCandidates()
    {
        var candidates = new List<ExplorationCandidate>
        {
            new()
            {
                Guid = 0x80000123u,
                Name = "Distant Door",
                Distance = 42.0f,
                CellId = 0x86020100u,
                Visited = false,
            },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: candidates);
        Assert.Contains("EXPLORATION CANDIDATES", p);
    }

    [Fact]
    public void BuildUserPrompt_ExplorationCandidate_RendersPickedNameCountWhenPositive()
    {
        // picker-name-respawn-audit: a candidate the bot has picked
        // before surfaces the factual tally so the LLM (not the Motor)
        // decides whether to re-collect a duplicate.
        var candidates = new List<ExplorationCandidate>
        {
            new()
            {
                Guid = 0x80000200u,
                Name = "Apple",
                Distance = 5.0f,
                CellId = 0x86020100u,
                Visited = false,
                PickedNameCount = 3,
            },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: candidates);
        Assert.Contains("picked_name_count=3", p);
    }

    [Fact]
    public void BuildUserPrompt_ExplorationCandidate_OmitsPickedNameCountWhenZero()
    {
        // Never-picked candidate: the annotation is absent (no
        // `picked_name_count=0` noise on every fresh object).
        var candidates = new List<ExplorationCandidate>
        {
            new()
            {
                Guid = 0x80000201u,
                Name = "Distant Door",
                Distance = 42.0f,
                CellId = 0x86020100u,
                Visited = false,
                PickedNameCount = 0,
            },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: candidates);
        Assert.DoesNotContain("picked_name_count", p);
    }

    // ---- Server-refused interaction targets end-capsule (cp-2340) ----
    // The cp-2338 InteractUnreachableTracker is a Motor-only guard; the LLM
    // was blind to which interaction-target guids the resolver currently
    // drops, so it kept re-emitting a goal that resolved only to a suppressed
    // target. This capsule surfaces the SAME suppression set in the
    // decision-proximate end slot. Rendered whenever any guid is suppressed
    // (raw-presence gate); facts only, no valuation.

    [Fact]
    public void BuildUserPrompt_UnreachableTargetsEndcap_RendersWhenSuppressed()
    {
        var targets = new List<UnreachableTargetProjection>
        {
            new() { Guid = 0x7A9B401Cu, Name = "Door", RemainingCooldownSeconds = 42.4 },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            unreachableTargets: targets);
        Assert.Contains("## Server-refused interaction targets", p);
        Assert.Contains("Door (guid=0x7A9B401C)", p);
        Assert.Contains("refused by the server as out-of-reach", p);
        Assert.Contains("expires in about 42s", p);
    }

    [Fact]
    public void BuildUserPrompt_UnreachableTargetsEndcap_GuidOnlyWhenNameMissing()
    {
        // Object left the world projection -> render the guid alone (the
        // mechanical fact still applies; never store a name in the tracker).
        var targets = new List<UnreachableTargetProjection>
        {
            new() { Guid = 0x7A9B401Cu, Name = null, RemainingCooldownSeconds = 10 },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            unreachableTargets: targets);
        Assert.Contains("## Server-refused interaction targets", p);
        Assert.Contains("- guid=0x7A9B401C:", p);
        Assert.DoesNotContain("(guid=0x7A9B401C)", p); // no "name (guid=...)" form
    }

    [Fact]
    public void BuildUserPrompt_UnreachableTargetsEndcap_OmittedWhenNoneOrNull()
    {
        // Empty list and null both omit the capsule (a stale set must never
        // mislead the LLM; the driver publishes/clears it every tick).
        var pEmpty = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            unreachableTargets: new List<UnreachableTargetProjection>());
        Assert.DoesNotContain("## Server-refused interaction targets", pEmpty);

        var pNull = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            unreachableTargets: null);
        Assert.DoesNotContain("## Server-refused interaction targets", pNull);
    }

    [Fact]
    public void BuildUserPrompt_UnreachableTargetsEndcap_RendersAtEndAfterVisibleSection()
    {
        var targets = new List<UnreachableTargetProjection>
        {
            new() { Guid = 0x7A9B401Cu, Name = "Door", RemainingCooldownSeconds = 30 },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            unreachableTargets: targets);
        var capsuleIdx = p.IndexOf("## Server-refused interaction targets", System.StringComparison.Ordinal);
        var selfIdx = p.IndexOf("## Self", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx > selfIdx && selfIdx >= 0, "capsule should render after ## Self");
    }

    // ---- Approach-distance-history salience end-capsule (cp-2342) ----
    // The Motor measures self->target distance at every interaction lock, but
    // the LLM cannot tell across ticks whether its repeated selections of the
    // SAME target are reducing the distance (live repro: nine Talk locks at a
    // constant 27.47u). This capsule re-surfaces the recent raw distance
    // samples in the decision-proximate end slot. Rendered whenever the
    // driver supplies a projection with >=2 samples (data-availability gate,
    // the driver already applied the freshness + still-outside-arrival-radius
    // gates); raw measurements only, no valuation.

    [Fact]
    public void BuildUserPrompt_ApproachDistanceEndcap_RendersWhenSamplesPresent()
    {
        var ap = new ApproachDistanceProjection
        {
            Guid = 0x80003068u,
            Name = "Worcer",
            DistanceSamplesUnits = new[] { 99.7, 27.5, 27.5, 27.5 },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            approachDistance: ap);
        Assert.Contains("## Approach distance history", p);
        Assert.Contains("Worcer (guid=0x80003068)", p);
        Assert.Contains("99.7u, 27.5u, 27.5u, 27.5u", p);
        Assert.Contains("last 4 interaction locks", p);
        Assert.Contains("raw fact, not a recommendation", p);
    }

    [Fact]
    public void BuildUserPrompt_ApproachDistanceEndcap_GuidOnlyWhenNameMissing()
    {
        var ap = new ApproachDistanceProjection
        {
            Guid = 0x80003068u,
            Name = null,
            DistanceSamplesUnits = new[] { 27.5, 27.5 },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            approachDistance: ap);
        Assert.Contains("## Approach distance history", p);
        Assert.Contains("guid=0x80003068", p);
        Assert.DoesNotContain("(guid=0x80003068)", p); // no "name (guid=...)" form
    }

    [Fact]
    public void BuildUserPrompt_ApproachDistanceEndcap_OmittedWhenNullOrSingleSample()
    {
        var pNull = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            approachDistance: null);
        Assert.DoesNotContain("## Approach distance history", pNull);

        // A single sample is below the >=2 data-availability floor: omitted.
        var pOne = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            approachDistance: new ApproachDistanceProjection
            {
                Guid = 0x80003068u, Name = "Worcer", DistanceSamplesUnits = new[] { 27.5 },
            });
        Assert.DoesNotContain("## Approach distance history", pOne);
    }

    [Fact]
    public void BuildUserPrompt_ApproachDistanceEndcap_RendersAtEndAfterSelf()
    {
        var ap = new ApproachDistanceProjection
        {
            Guid = 0x80003068u,
            Name = "Worcer",
            DistanceSamplesUnits = new[] { 30.0, 27.5 },
        };
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            approachDistance: ap);
        var capsuleIdx = p.IndexOf("## Approach distance history", System.StringComparison.Ordinal);
        var selfIdx = p.IndexOf("## Self", System.StringComparison.Ordinal);
        Assert.True(capsuleIdx > selfIdx && selfIdx >= 0, "capsule should render after ## Self");
    }

    // ---- cp-2352 "## Recent outdoor coverage" capsule ----
    // Rolling-window summary of the bot's own outdoor visited-node + sighting
    // memory so an LLM steering a hunt excursion (Explore direction, cp-2351)
    // has raw facts behind its bearing choice. Render-gated on a recent Explore
    // emission so it never clutters town/quest/combat prompts; raw counts + a
    // compass bearing only, no recommendation.

    private static EventStream RecentExploreEmissionStream()
    {
        var ev = new EventStream();
        ev.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalEmitted,
            Text = "Explore target=name=\"anywhere\" item=<empty> source=llm",
        });
        return ev;
    }

    private static ExcursionCoverageProjection Coverage(int landblocks, float dx, float dy, int mobs) =>
        new()
        {
            WindowMinutes = 15.0,
            DistinctOutdoorLandblocks = landblocks,
            NetTravelDx = dx,
            NetTravelDy = dy,
            MobSightingsInWindow = mobs,
        };

    [Fact]
    public void BuildUserPrompt_ExcursionCoverage_RendersWithRecentExplore()
    {
        // net travel (-x,+y) = NW.
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), RecentExploreEmissionStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            excursionCoverage: Coverage(8, -100f, 100f, 0));
        Assert.Contains("## Recent outdoor coverage", p);
        Assert.Contains("8 distinct outdoor landblock", p);
        Assert.Contains("points NW", p);
        Assert.Contains("0 monster sighting", p);
        Assert.Contains("raw fact, not a recommendation", p);
    }

    [Fact]
    public void BuildUserPrompt_ExcursionCoverage_OmittedWithoutRecentExplore()
    {
        // Coverage is set but there is no recent Explore emission → suppressed
        // so the capsule never clutters a town/quest/combat decision.
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            excursionCoverage: Coverage(8, -100f, 100f, 0));
        Assert.DoesNotContain("## Recent outdoor coverage", p);
    }

    [Fact]
    public void BuildUserPrompt_ExcursionCoverage_OmittedWhenNull()
    {
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), RecentExploreEmissionStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            excursionCoverage: null);
        Assert.DoesNotContain("## Recent outdoor coverage", p);
    }

    [Fact]
    public void BuildUserPrompt_ExcursionCoverage_IsFactualWithNoImperativeOrRecommendation()
    {
        var p = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), RecentExploreEmissionStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            excursionCoverage: Coverage(3, 100f, 0f, 2));
        var idx = p.IndexOf("## Recent outdoor coverage", System.StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var capsule = p.Substring(idx);
        foreach (var banned in new[] { "barren", "opposite", "you should", "you must", "avoid", "dead end", "better direction" })
            Assert.DoesNotContain(banned, capsule, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUserPrompt_ExcursionCoverage_RendersBearingFromNetTravel()
    {
        // net travel (+x,0) = E; (0,-y) = S.
        var east = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), RecentExploreEmissionStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            excursionCoverage: Coverage(2, 100f, 0f, 0));
        Assert.Contains("points E;", east);

        var south = LlmGoalPolicy.BuildUserPrompt(
            BuildExitTokenWorld(), RecentExploreEmissionStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null,
            excursionCoverage: Coverage(2, 0f, -100f, 0));
        Assert.Contains("points S;", south);
    }

    // ---- Intent-stack completion-predicate schema accuracy ----
    // The prompt teaches the LLM the JSON shape of completion predicates.
    // It MUST match the actual System.Text.Json polymorphic contract on
    // IntentPredicate (discriminator "type", names all_of/any_of/
    // always_false, etc). A drift here silently breaks every LLM-pushed
    // intent: the malformed completion throws during deserialization, so
    // TryParseStackOps fails and the ENTIRE stack_ops batch is dropped.

    [Fact]
    public void StackOps_OldDollarTypeDiscriminator_FailsToParse()
    {
        // This is the exact shape the pre-fix prompt taught ($type +
        // "never"). It must NOT deserialize — proving why intents pushed
        // under the old prompt silently vanished.
        var json = """
        { "stack_ops": [ { "op": "push", "intent": {
            "kind": "hunt", "rationale": "x",
            "completion": { "$type": "never" } } } ] }
        """;
        var ok = LlmGoalPolicy.TryParseStackOps(json, out _, out var ops, out _);
        Assert.False(ok && ops is { Count: > 0 });
    }

    [Fact]
    public void StackOps_CorrectTypeDiscriminator_Parses()
    {
        var json = """
        { "stack_ops": [ { "op": "push", "intent": {
            "kind": "hunt", "rationale": "x",
            "completion": { "type": "always_false" } } } ] }
        """;
        var ok = LlmGoalPolicy.TryParseStackOps(json, out _, out var ops, out var err);
        Assert.True(ok, err);
        Assert.NotNull(ops);
        Assert.Single(ops!);
        Assert.IsType<AlwaysFalsePredicate>(ops![0].Intent!.Completion);
    }

    [Fact]
    public void StackOps_HuntExcursionCompletion_ParsesToAnyOf()
    {
        // The canonical hunt-excursion completion documented in the
        // prompt: complete when the bot leaves its current landblock OR a
        // monster comes into view.
        var json = """
        { "stack_ops": [ { "op": "push", "intent": {
            "kind": "hunt-excursion", "rationale": "leave town to find monsters",
            "completion": { "type": "any_of", "children": [
                { "type": "landblock_changed_from_push" },
                { "type": "visible_tag", "tag": "monster" } ] } } } ] }
        """;
        var ok = LlmGoalPolicy.TryParseStackOps(json, out _, out var ops, out var err);
        Assert.True(ok, err);
        var anyOf = Assert.IsType<AnyOfPredicate>(ops![0].Intent!.Completion);
        Assert.Equal(2, anyOf.Children.Count);
        Assert.IsType<LandblockChangedFromPushPredicate>(anyOf.Children[0]);
        Assert.IsType<VisibleTagPredicate>(anyOf.Children[1]);
    }

    [Fact]
    public void StackPrompt_DocumentsActualPredicateSchema()
    {
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null, new IntentStack());

        // Correct discriminator + names + hunt-relevant predicates present.
        Assert.Contains("\"type\":\"always_false\"", prompt);
        Assert.Contains("landblock_changed_from_push", prompt);
        Assert.Contains("visible_tag", prompt);
        Assert.Contains("any_of", prompt);

        // The old, non-deserializable tokens must be gone.
        Assert.DoesNotContain("\"$type\":\"never\"", prompt);
        Assert.DoesNotContain("\"$type\":\"and\"", prompt);
        Assert.DoesNotContain("\"$type\":\"or\"", prompt);
        Assert.DoesNotContain("inventory_contains_at_least", prompt);
    }

    [Fact]
    public void StackPrompt_TeachesPersistHuntExcursionPush()
    {
        // When a stack is present the prompt must instruct the LLM to
        // PERSIST a hunt excursion by pushing a "hunt-excursion" intent
        // (so the decision survives across ticks instead of being
        // re-decided and abandoned each cycle). Audit-safe: the LLM
        // authors the push; source never branches on this kind.
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null, new IntentStack());

        Assert.Contains("PERSIST A HUNT EXCURSION", prompt);
        Assert.Contains("\"hunt-excursion\"", prompt);
        // It must couple the push to the monster-sighting completion plus
        // a liveness deadline (NOT mere landblock change, which can land in
        // another monster-free town and pop the excursion prematurely).
        Assert.Contains("visible_tag", prompt);
        Assert.Contains("deadline_seconds", prompt);
    }

    [Fact]
    public void StackPrompt_PersistHuntExcursion_AbsentWhenNoStack()
    {
        // The persist directive is stack-gated — it must NOT appear (and
        // must not bloat the static floor) when no stack is configured.
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, currentGoal: null, stack: null);

        Assert.DoesNotContain("PERSIST A HUNT EXCURSION", prompt);
    }

    [Fact]
    public void StackPrompt_PersistHuntExcursion_OmittedWhenMonsterInView()
    {
        // cp-2392: PERSIST A HUNT EXCURSION teaches how to START/maintain an
        // excursion to FIND monsters; it is moot once a monster is already in
        // view (completing the cp-2368 monster-in-view gating set), which frees
        // ~1KB of static preamble in exactly the combat scenes where the dynamic
        // perception sections are hard-cut. The mechanical auto-pop on monster
        // sighting is predicate-driven, not prompt-driven, so this is
        // behavior-preserving.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x600u, Name = "Sparring Golem", Wcid = 70u, Distance = 6.5f, IsMonster = true });
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null, new IntentStack());

        Assert.DoesNotContain("PERSIST A HUNT EXCURSION", prompt);
        // The completion semantics it relied on remain stated in COMPLETION
        // PREDICATES (always-on in the stack block).
        Assert.Contains("COMPLETION PREDICATES", prompt);
    }

    [Fact]
    public void StackPrompt_TeachesGrindAsKillCountIntent_WhenMonsterInView()
    {
        // reduce-llm-call-volume: when a monster is in view (a combat scene) and
        // a stack is configured, the prompt teaches the LLM to express a WINNING
        // grind as a typed kill-count intent, so the Motor's autonomous
        // decomposition (cp-2426) can mint the repeats without a per-monster LLM
        // round-trip. Audit-safe: the LLM authors the push and decides
        // whether/what to grind; source never branches on a kind.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x600u, Name = "Sparring Golem", Wcid = 70u, Distance = 6.5f, IsMonster = true });
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null, new IntentStack());

        Assert.Contains("COMMIT A WINNING GRIND", prompt);
        Assert.Contains("kill_count_since_push_at_least", prompt);
        Assert.Contains("deadline_seconds", prompt);
    }

    [Fact]
    public void StackPrompt_GrindAsKillCount_OmittedWhenNoMonsterInView()
    {
        // Gated on monsterInView (the complement of PERSIST A HUNT EXCURSION):
        // a grind rule is moot with nothing to Attack, so it must not bloat the
        // non-combat preamble.
        var world = BuildExitTokenWorld();
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null, new IntentStack());

        Assert.DoesNotContain("COMMIT A WINNING GRIND", prompt);
    }

    [Fact]
    public void StackPrompt_GrindAsKillCount_AbsentWhenNoStack()
    {
        // Stack-gated: with no IntentStack there is no stack-ops guidance at all.
        var world = BuildWorldWithMonsters(
            new VisibleObjectProjection
            { Guid = 0x600u, Name = "Sparring Golem", Wcid = 70u, Distance = 6.5f, IsMonster = true });
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), currentGoal: null, stack: null);

        Assert.DoesNotContain("COMMIT A WINNING GRIND", prompt);
    }

    // ---- ## Unseen objective target (phantom named-target capsule) ----

    private static IntentStack StackWithTopIntent(
        WorldStateProjection world, EventStream events,
        string? targetName, uint? targetGuid, double pushedSecondsAgo)
    {
        var stack = new IntentStack();
        stack.TryPush(new Intent
        {
            Id = "i-001",
            Kind = "reach-objective",
            TargetName = targetName,
            TargetGuid = targetGuid,
            Completion = new AlwaysFalsePredicate(),
            Baseline = IntentBaseline.Capture(
                world, events, DateTime.UtcNow.AddSeconds(-pushedSecondsAgo)),
        });
        return stack;
    }

    [Fact]
    public void BuildUserPrompt_RendersUnseenObjectiveTarget_WhenNamedTargetNeverObserved()
    {
        // The top intent names a target that has never entered the world model
        // (a dialog-only phantom) and has been pursued past the settle window.
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var stack = StackWithTopIntent(world, events, "Agent", targetGuid: null, pushedSecondsAgo: 60);

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null, stack);

        Assert.Contains("## Unseen objective target", prompt);
        Assert.Contains("no object named 'Agent' has been observed", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsUnseenObjectiveTarget_WhenTargetWasObserved()
    {
        // Same intent, but the target name HAS been observed as a real object —
        // the bot just has not reached it yet; do not flag it as a phantom.
        var world = BuildExitTokenWorld() with
        {
            EverObservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Agent" },
        };
        var events = new EventStream();
        var stack = StackWithTopIntent(world, events, "Agent", targetGuid: null, pushedSecondsAgo: 60);

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null, stack);

        Assert.DoesNotContain("## Unseen objective target", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsUnseenObjectiveTarget_WithinSettleWindow()
    {
        // Freshly pushed objective (within the grace window): the bot may still
        // be travelling to a not-yet-loaded room, so do not flag it yet.
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var stack = StackWithTopIntent(world, events, "Agent", targetGuid: null, pushedSecondsAgo: 1);

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null, stack);

        Assert.DoesNotContain("## Unseen objective target", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsUnseenObjectiveTarget_WhenTargetGuidResolved()
    {
        // The intent carries a resolved TargetGuid — it is bound to a concrete
        // object, not a free-floating name, so the phantom signal must not fire.
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var stack = StackWithTopIntent(world, events, "Agent", targetGuid: 0x80001234u, pushedSecondsAgo: 60);

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null, stack);

        Assert.DoesNotContain("## Unseen objective target", prompt);
    }

    [Fact]
    public void BuildUserPrompt_OmitsUnseenObjectiveTarget_WhenTopIntentHasNoNamedTarget()
    {
        // A top intent with no target_name (e.g. a generic hunt) is never a
        // phantom-named-target chase.
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        var stack = StackWithTopIntent(world, events, targetName: null, targetGuid: null, pushedSecondsAgo: 60);

        var prompt = LlmGoalPolicy.BuildUserPrompt(world, events, null, stack);

        Assert.DoesNotContain("## Unseen objective target", prompt);
    }

    //
    // When the LLM emits an inert Explore goal AND pushes a new TOP
    // intent that carries a liveness deadline, the policy captures that
    // (intent-id, Explore-goal) pair and RE-DRIVES the Explore on later
    // ticks WITHOUT a fresh LLM call, until a MECHANICAL break condition
    // fires (top intent left, landblock change, semantic rejection,
    // stuck, or the reinstall budget). Ambient salient chatter (NpcDialog
    // etc.) must NOT break the commitment — that is the whole point.
    //
    // Discriminator: the re-drive gate sits BEFORE the wake/kickoff
    // logic, so when it is armed it SUPPRESSES an ambient salient event
    // (NpcDialog) that the normal sticky/wake path would otherwise turn
    // into a fresh LLM call. So "armed" ⇒ no new HTTP request on a
    // NpcDialog tick; "not armed / broken" ⇒ a new request fires.

    private const string RedrivePushExploreDeadlineJson = """
    {
      "goal_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
      "kind": "Explore",
      "target": { "name": "open country" },
      "rationale": "begin hunt excursion",
      "stack_ops": [ { "op": "push", "intent": {
        "kind": "hunt-excursion", "rationale": "leave town to hunt",
        "deadline_seconds": 600,
        "completion": { "type": "visible_tag", "tag": "monster" } } } ]
    }
    """;

    private static (LlmGoalPolicy policy, WorldStateProjection world, EventStream events,
        System.Collections.Generic.List<string> reqs, IntentStack stack)
        SetupRedrive(string cannedContent, int maxRedrive = 12, bool seedRoot = true)
    {
        var reqs = new System.Collections.Generic.List<string>();
        var canned = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = cannedContent } } },
        });
        var http = new HttpClient(new AsyncStubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            reqs.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned) };
        }));
        var llm = new LlmGoalClient(http, "https://test.example/chat", "test-model", "key");
        var stack = new IntentStack();
        var world = BuildExitTokenWorld();
        var events = new EventStream();
        // Seed a root intent so the LLM-pushed hunt-excursion lands at
        // depth 2 (poppable) — mirrors production where the operator pushes
        // an initial Hunt intent before the LLM ever runs. A push onto an
        // EMPTY stack would become the sacred root and never auto-pop, which
        // is exactly why capture refuses unless Depth > 1.
        if (seedRoot)
        {
            stack.TryPush(new Intent
            {
                Id = "root-operator",
                Kind = "Hunt",
                Completion = new AlwaysFalsePredicate(),
                Baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow),
            });
        }
        var policy = new LlmGoalPolicy(llm, new NoQuestKnowledgePolicy(), new InMemoryWeenieRepo(), stack: stack)
        {
            MinCallInterval = TimeSpan.Zero,
            MaxRedriveReinstalls = maxRedrive,
        };
        return (policy, world, events, reqs, stack);
    }

    /// <summary>Run call-1 kickoff + drain + consume so the stack op is
    /// applied and re-drive provenance (if eligible) is captured.</summary>
    private static async Task<Goal?> ConsumeFirstAsync(
        LlmGoalPolicy policy, WorldStateProjection world, EventStream events)
    {
        Assert.Null(policy.ProposeGoal(world, events, null));
        await policy.WaitForInFlightAsync();
        return policy.ProposeGoal(world, events, null);
    }

    private static StreamEvent NpcDialog(string text = "hello") => new()
    {
        Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Text = text,
    };

    [Fact]
    public async Task Redrive_PushExploreWithDeadline_Captures_SuppressesAmbientNpcDialog()
    {
        var (policy, world, events, reqs, stack) = SetupRedrive(RedrivePushExploreDeadlineJson);

        var g = await ConsumeFirstAsync(policy, world, events);
        Assert.Equal(GoalKind.Explore, g!.Kind);
        Assert.Single(reqs);            // only the kickoff call
        Assert.Equal(2, stack.Depth);   // root + the pushed hunt-excursion

        // Ambient NpcDialog would normally wake a fresh LLM call; re-drive
        // suppresses it and re-emits the SAME Explore for free.
        events.Append(NpcDialog());
        var g2 = policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Single(reqs);            // NO new call — re-drive suppressed it
        Assert.Equal(GoalKind.Explore, g2!.Kind);
    }

    [Fact]
    public async Task Redrive_PushTalk_DoesNotCapture_NpcDialogWakesLlm()
    {
        // Same push, but the goal is Talk (interactive) — must NOT capture.
        var json = """
        {
          "goal_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "kind": "Talk",
          "target": { "name": "Greeter" },
          "rationale": "x",
          "stack_ops": [ { "op": "push", "intent": {
            "kind": "hunt-excursion", "rationale": "x",
            "deadline_seconds": 600,
            "completion": { "type": "visible_tag", "tag": "monster" } } } ]
        }
        """;
        var (policy, world, events, reqs, stack) = SetupRedrive(json);

        var g = await ConsumeFirstAsync(policy, world, events);
        Assert.Equal(GoalKind.Talk, g!.Kind);
        Assert.Single(reqs);

        events.Append(NpcDialog());
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // not armed → NpcDialog woke a fresh call
    }

    [Fact]
    public async Task Redrive_PushExploreNoDeadline_DoesNotCapture()
    {
        // Explore + push but the intent has NO deadline (no liveness
        // guarantee) — refuse to capture.
        var json = """
        {
          "goal_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "kind": "Explore",
          "target": { "name": "open country" },
          "rationale": "x",
          "stack_ops": [ { "op": "push", "intent": {
            "kind": "hunt-excursion", "rationale": "x",
            "completion": { "type": "visible_tag", "tag": "monster" } } } ]
        }
        """;
        var (policy, world, events, reqs, _) = SetupRedrive(json);

        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        events.Append(NpcDialog());
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // no deadline → not armed → call fired
    }

    [Fact]
    public async Task Redrive_ExploreWithoutPush_DoesNotCapture()
    {
        var json = """
        { "goal_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "kind": "Explore", "target": { "name": "open country" }, "rationale": "x" }
        """;
        var (policy, world, events, reqs, stack) = SetupRedrive(json);

        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);
        Assert.Equal(1, stack.Depth);   // only the seeded root; nothing pushed

        events.Append(NpcDialog());
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // no push → not armed → call fired
    }

    [Fact]
    public async Task Redrive_PreservesActiveCurrentGoal_DoesNotClobber()
    {
        var (policy, world, events, reqs, _) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        // An active (in-flight) goal must be preserved verbatim, not
        // replaced by a fresh re-drive copy.
        var active = new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = "anywhere" }, Source = "motor" };
        events.Append(NpcDialog());
        var result = policy.ProposeGoal(world, events, active);
        await policy.WaitForInFlightAsync();
        Assert.Single(reqs);                 // still suppressed
        Assert.Same(active, result);         // exact same instance returned
    }

    [Fact]
    public async Task Redrive_LandblockChange_EndsRedrive()
    {
        var (policy, world, events, reqs, _) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.LandblockChanged, Text = "lb=0xA9B3",
        });
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // landblock change broke the commitment
    }

    [Fact]
    public async Task Redrive_SemanticRejection_EndsRedrive()
    {
        var (policy, world, events, reqs, _) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0x046A, ErrorLabel = "TradeAiDoesntWant", Text = "Greeter",
        });
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // semantic rejection broke the commitment
    }

    [Fact]
    public async Task Redrive_TransportRejection_DoesNotEndRedrive()
    {
        var (policy, world, events, reqs, _) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        // A synthetic motor transport-failure (could-not-walk) must NOT
        // break the commitment — the route failed, the objective did not.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.ActionRejected,
            ErrorCode = 0xFFFEu, ErrorLabel = "Unreachable", Text = "monster",
        });
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Single(reqs);            // still suppressed — transport ignored
    }

    [Fact]
    public async Task Redrive_TopIntentPopped_EndsRedrive()
    {
        var (policy, world, events, reqs, stack) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);
        Assert.Equal(2, stack.Depth);   // root + hunt-excursion

        // Simulate the intent completing (auto-pop). The re-drive intent
        // is no longer TOP, so the gate must not fire.
        stack.PopTop(IntentLifecycle.Completed);
        Assert.Equal(1, stack.Depth);   // back to just the root

        events.Append(NpcDialog());
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // top changed → re-drive inert → call
    }

    [Fact]
    public async Task Redrive_ReinstallBudgetExhausted_ForcesRealLlmCall()
    {
        // The reinstall budget is a TRUE "force a fresh LLM re-think"
        // backstop: once exhausted it must NOT leak into the sticky-objective
        // path (which would re-emit the same Explore for free). Prove it with
        // NO external event at all — only the budget ends re-drive.
        var (policy, world, events, reqs, _) = SetupRedrive(RedrivePushExploreDeadlineJson, maxRedrive: 1);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        // Reinstall #1 (count 0 -> 1): suppressed, no call.
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Single(reqs);

        // Budget (1 >= 1) exhausted, no external event: must fire a real call
        // rather than sticky-re-emitting.
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);
    }

    [Fact]
    public async Task Redrive_PushOntoEmptyStack_DoesNotArm()
    {
        // A push onto an empty stack becomes the sacred un-poppable ROOT,
        // which CheckTopForCompletion can never auto-pop. Capture must refuse
        // (Depth > 1 guard) so re-drive cannot outlive the intent's
        // completion. Falls back to prompt-only persistence.
        var (policy, world, events, reqs, stack) = SetupRedrive(RedrivePushExploreDeadlineJson, seedRoot: false);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);
        Assert.Equal(1, stack.Depth);   // the push IS the root

        events.Append(NpcDialog());
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // not armed → NpcDialog woke a call
    }

    [Fact]
    public async Task Redrive_InventoryChange_EndsRedrive()
    {
        var (policy, world, events, reqs, _) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        // A durable inventory change is a mechanical state change (unlike
        // ambient dialog) — it must end the commitment and not be hidden by
        // the floor-advance.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.InventoryItemAdded, Text = "picked up something",
        });
        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);
    }

    [Fact]
    public async Task Redrive_TopMarkedBlockedInPlace_ForcesRealLlmCall()
    {
        // If the top intent is marked Blocked in place (same id, still TOP),
        // the gate's Status-Active check must end re-drive AND force a real
        // LLM call — NOT let the sticky path re-emit the same Explore for
        // free. No external event is appended, so the redriveEndedMustCallLlm
        // flag is the ONLY thing that can produce the call.
        var (policy, world, events, reqs, stack) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);

        stack.MarkTopBlocked("simulated block");

        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // top inactive → ended → real call (not sticky)
    }

    [Fact]
    public async Task Redrive_CapturedIntentLeavesTop_ForcesRealLlmCall()
    {
        // The captured intent auto-pops (LLM-authored completion / deadline)
        // BEFORE the next ProposeGoal, so Top.Id no longer matches. Re-drive
        // must notice it left TOP, clear provenance, and force a real LLM
        // re-deliberation — NOT skip the gate, leave stale provenance, and let
        // the sticky path re-emit the old Explore for free (which would ignore
        // the intent's own completion). No external event is appended, so the
        // forced call proves the left-top handling, not an ambient wake.
        var (policy, world, events, reqs, stack) = SetupRedrive(RedrivePushExploreDeadlineJson);
        await ConsumeFirstAsync(policy, world, events);
        Assert.Single(reqs);
        Assert.Equal(2, stack.Depth);   // armed at nested depth 2

        stack.PopTop(IntentLifecycle.Completed, "simulated completion");
        Assert.Equal(1, stack.Depth);   // captured intent left TOP

        policy.ProposeGoal(world, events, null);
        await policy.WaitForInFlightAsync();
        Assert.Equal(2, reqs.Count);    // intent-left-top → ended → real call
    }

    // ---- HuntTappedOutFact (coldstart hunt-zone discovery perception) ----

    [Fact]
    public void HuntTappedOutFact_NotCombatReady_ReturnsNull()
    {
        Assert.Null(LlmGoalPolicy.HuntTappedOutFact(
            combatReady: false, currentLevel: 3, levelAtLandblockEntry: 3,
            dwellMinutes: 10.0, dwellThresholdMinutes: 5.0));
    }

    [Fact]
    public void HuntTappedOutFact_UnknownLevel_ReturnsNull()
    {
        Assert.Null(LlmGoalPolicy.HuntTappedOutFact(
            combatReady: true, currentLevel: null, levelAtLandblockEntry: 3,
            dwellMinutes: 10.0, dwellThresholdMinutes: 5.0));
    }

    [Fact]
    public void HuntTappedOutFact_UnknownEntryLevel_ReturnsNull()
    {
        Assert.Null(LlmGoalPolicy.HuntTappedOutFact(
            combatReady: true, currentLevel: 3, levelAtLandblockEntry: null,
            dwellMinutes: 10.0, dwellThresholdMinutes: 5.0));
    }

    [Fact]
    public void HuntTappedOutFact_DwellBelowThreshold_ReturnsNull()
    {
        Assert.Null(LlmGoalPolicy.HuntTappedOutFact(
            combatReady: true, currentLevel: 3, levelAtLandblockEntry: 3,
            dwellMinutes: 4.9, dwellThresholdMinutes: 5.0));
    }

    [Fact]
    public void HuntTappedOutFact_UnknownDwell_ReturnsNull()
    {
        Assert.Null(LlmGoalPolicy.HuntTappedOutFact(
            combatReady: true, currentLevel: 3, levelAtLandblockEntry: 3,
            dwellMinutes: null, dwellThresholdMinutes: 5.0));
    }

    [Fact]
    public void HuntTappedOutFact_LeveledHere_ReturnsNull()
    {
        Assert.Null(LlmGoalPolicy.HuntTappedOutFact(
            combatReady: true, currentLevel: 4, levelAtLandblockEntry: 3,
            dwellMinutes: 10.0, dwellThresholdMinutes: 5.0));
    }

    [Fact]
    public void HuntTappedOutFact_TappedOut_ReturnsFact()
    {
        var fact = LlmGoalPolicy.HuntTappedOutFact(
            combatReady: true, currentLevel: 3, levelAtLandblockEntry: 3,
            dwellMinutes: 7.0, dwellThresholdMinutes: 5.0);
        Assert.NotNull(fact);
        Assert.Contains("tapped out", fact);
        Assert.Contains("7 min", fact);
        Assert.Contains("level", fact);
        // Raw self-data only — no verb directive embedded (audit finding #1).
        Assert.DoesNotContain("Explore", fact);
    }

    [Fact]
    public void BuildUserPrompt_TappedOut_SurfacesFactInCombatReadiness()
    {
        // Combat-ready (melee wielded), dwelled > threshold, no level gained
        // since entry → the tapped-out fact must appear under Combat readiness.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA8B4u, CellId = 0xA8B40006u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
                Level = 3,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                { Guid = 0x222u, Name = "Training Spadone", Wcid = 5104u, ItemType = 0x1u, WieldedAt = 0x100000u },
            },
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        var entry = DateTimeOffset.UtcNow.AddMinutes(-7);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), null, stack: null, pickerActivity: null,
            explorationCandidates: null, dwellEntryUtc: entry, recentSightings: null,
            levelAtLandblockEntry: 3);
        Assert.Contains("tapped out: level", prompt);
    }

    [Fact]
    public void BuildUserPrompt_LeveledHere_OmitsTappedOutFact()
    {
        // Same dwell, but the bot gained a level here → fact suppressed.
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA8B4u, CellId = 0xA8B40006u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f,
                Level = 4,
            },
            Inventory = new[]
            {
                new InventoryItemProjection
                { Guid = 0x222u, Name = "Training Spadone", Wcid = 5104u, ItemType = 0x1u, WieldedAt = 0x100000u },
            },
            Visible = System.Array.Empty<VisibleObjectProjection>(),
        };
        var entry = DateTimeOffset.UtcNow.AddMinutes(-7);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), null, stack: null, pickerActivity: null,
            explorationCandidates: null, dwellEntryUtc: entry, recentSightings: null,
            levelAtLandblockEntry: 3);
        Assert.DoesNotContain("tapped out: level", prompt);
    }

    // ---- cp-2260 cold-start trivial-farm egress override helpers ----

    private static VisibleObjectProjection Mob(
        uint guid, string name, uint? wcid, bool corpse = false, bool hostile = false)
        => new VisibleObjectProjection
        {
            Guid = guid, Name = name, Wcid = wcid, ItemType = 0x10u, Distance = 2f,
            IsCreature = true, IsMonster = true, IsCorpse = corpse, ObservedHostile = hostile,
        };

    [Fact]
    public void IsFarmedHere_NotTappedOut_False()
    {
        var v = Mob(0x1u, "Chicken", 10u);
        Assert.False(LlmGoalPolicy.IsFarmedHere(
            v, new HashSet<string> { "w:10" }, tappedOut: false));
    }

    [Fact]
    public void IsFarmedHere_ObservedHostile_False()
    {
        var v = Mob(0x1u, "Chicken", 10u, hostile: true);
        Assert.False(LlmGoalPolicy.IsFarmedHere(
            v, new HashSet<string> { "w:10" }, tappedOut: true));
    }

    [Fact]
    public void IsFarmedHere_NullOrEmptyKilledSet_False()
    {
        var v = Mob(0x1u, "Chicken", 10u);
        Assert.False(LlmGoalPolicy.IsFarmedHere(v, null, tappedOut: true));
        Assert.False(LlmGoalPolicy.IsFarmedHere(
            v, new HashSet<string>(), tappedOut: true));
    }

    [Fact]
    public void IsFarmedHere_KindInSet_True()
    {
        var v = Mob(0x1u, "Chicken", 10u);
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"));
        Assert.NotNull(key);
        Assert.True(LlmGoalPolicy.IsFarmedHere(
            v, new HashSet<string> { key! }, tappedOut: true));
    }

    [Fact]
    public void IsFarmedHere_UnknownKind_False()
    {
        var v = Mob(0x1u, "Drudge", 99u);
        Assert.False(LlmGoalPolicy.IsFarmedHere(
            v, new HashSet<string> { "w:10" }, tappedOut: true));
    }

    [Fact]
    public void ComputeEffectiveMonsterInView_AllFarmed_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var visible = new[] { Mob(0x1u, "Chicken", 10u), Mob(0x2u, "Chicken", 10u) };
        Assert.False(LlmGoalPolicy.ComputeEffectiveMonsterInView(
            visible, new HashSet<string> { key }, tappedOut: true));
    }

    [Fact]
    public void ComputeEffectiveMonsterInView_UnknownKindPresent_True()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var visible = new[] { Mob(0x1u, "Chicken", 10u), Mob(0x2u, "Drudge", 99u) };
        Assert.True(LlmGoalPolicy.ComputeEffectiveMonsterInView(
            visible, new HashSet<string> { key }, tappedOut: true));
    }

    [Fact]
    public void ComputeEffectiveMonsterInView_FarmedButCorpse_IgnoredAndNotEffective()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var visible = new[] { Mob(0x1u, "Chicken", 10u, corpse: true) };
        Assert.False(LlmGoalPolicy.ComputeEffectiveMonsterInView(
            visible, new HashSet<string> { key }, tappedOut: true));
    }

    [Fact]
    public void ComputeEffectiveMonsterInView_FarmedKindAttackingBot_True()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        // Same farmed kind, but it's HOSTILE (attacking) → still counts.
        var visible = new[] { Mob(0x1u, "Chicken", 10u, hostile: true) };
        Assert.True(LlmGoalPolicy.ComputeEffectiveMonsterInView(
            visible, new HashSet<string> { key }, tappedOut: true));
    }

    // ---- ignored-kind liveness backstop (visible-but-unengaged) ----

    [Fact]
    public void IsIgnoredHere_NotTappedOut_False()
    {
        var v = Mob(0x1u, "Cow", 14u);
        Assert.False(LlmGoalPolicy.IsIgnoredHere(
            v, new HashSet<string> { "w:14" }, tappedOut: false));
    }

    [Fact]
    public void IsIgnoredHere_ObservedHostile_False()
    {
        var v = Mob(0x1u, "Cow", 14u, hostile: true);
        Assert.False(LlmGoalPolicy.IsIgnoredHere(
            v, new HashSet<string> { "w:14" }, tappedOut: true));
    }

    [Fact]
    public void IsIgnoredHere_NullOrEmptySet_False()
    {
        var v = Mob(0x1u, "Cow", 14u);
        Assert.False(LlmGoalPolicy.IsIgnoredHere(v, null, tappedOut: true));
        Assert.False(LlmGoalPolicy.IsIgnoredHere(v, new HashSet<string>(), tappedOut: true));
    }

    [Fact]
    public void IsIgnoredHere_KindInSet_True()
    {
        var v = Mob(0x1u, "Cow", 14u);
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(14u, "Cow"))!;
        Assert.True(LlmGoalPolicy.IsIgnoredHere(v, new HashSet<string> { key }, tappedOut: true));
    }

    [Fact]
    public void ComputeEffectiveMonsterInView_IgnoredKind_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(14u, "Cow"))!;
        var visible = new[] { Mob(0x1u, "Cow", 14u) };
        // Not in the KILLED set, but in the IGNORED set → no longer effective.
        Assert.True(LlmGoalPolicy.ComputeEffectiveMonsterInView(
            visible, null, tappedOut: true));
        Assert.False(LlmGoalPolicy.ComputeEffectiveMonsterInView(
            visible, null, tappedOut: true, ignoredThisDwell: new HashSet<string> { key }));
    }

    [Fact]
    public void ComputeEffectiveMonsterInView_IgnoredSetButHostile_True()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(14u, "Cow"))!;
        var visible = new[] { Mob(0x1u, "Cow", 14u, hostile: true) };
        Assert.True(LlmGoalPolicy.ComputeEffectiveMonsterInView(
            visible, null, tappedOut: true, ignoredThisDwell: new HashSet<string> { key }));
    }

    private static readonly System.Collections.Generic.IReadOnlySet<string> NoEngaged =
        new HashSet<string>();

    [Fact]
    public void UpdateIgnoredKindExposure_NotEligible_ClearsAndEmpty()
    {
        var dict = new Dictionary<string, DateTimeOffset>
        {
            ["w:14"] = DateTimeOffset.UnixEpoch,
        };
        var now = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var result = LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, NoEngaged,
            eligibleContext: false, now, TimeSpan.FromMinutes(5));
        Assert.Empty(result);
        Assert.Empty(dict); // tracker cleared when not eligible
    }

    [Fact]
    public void UpdateIgnoredKindExposure_DefersBeforeTimeout_ThenIgnoresAtTimeout()
    {
        var dict = new Dictionary<string, DateTimeOffset>();
        var t0 = DateTimeOffset.UnixEpoch;
        // First eligible observation stamps the clock, not yet ignored.
        var r1 = LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, NoEngaged, true, t0, TimeSpan.FromMinutes(5));
        Assert.Empty(r1);
        // Just before timeout → still deferred.
        var r2 = LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, NoEngaged, true, t0.AddMinutes(4.9), TimeSpan.FromMinutes(5));
        Assert.Empty(r2);
        // At/after timeout → ignored.
        var r3 = LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, NoEngaged, true, t0.AddMinutes(5), TimeSpan.FromMinutes(5));
        Assert.Contains("w:14", r3);
    }

    [Fact]
    public void UpdateIgnoredKindExposure_AbsenceResetsContinuity()
    {
        var dict = new Dictionary<string, DateTimeOffset>();
        var t0 = DateTimeOffset.UnixEpoch;
        LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, NoEngaged, true, t0, TimeSpan.FromMinutes(5));
        // Kind leaves PVS for a tick → dropped from tracker.
        LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, System.Array.Empty<(string, bool)>(), NoEngaged, true, t0.AddMinutes(4), TimeSpan.FromMinutes(5));
        Assert.DoesNotContain("w:14", dict.Keys);
        // Reappears → clock restarts; 4.9 min after FIRST sighting is < timeout from the restart.
        var r = LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, NoEngaged, true, t0.AddMinutes(4.9), TimeSpan.FromMinutes(5));
        Assert.Empty(r);
    }

    [Fact]
    public void UpdateIgnoredKindExposure_HostileNeverAccrues()
    {
        var dict = new Dictionary<string, DateTimeOffset>();
        var t0 = DateTimeOffset.UnixEpoch;
        var r = LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", true) }, NoEngaged, true, t0.AddMinutes(100), TimeSpan.FromMinutes(5));
        Assert.Empty(r);
        Assert.Empty(dict);
    }

    [Fact]
    public void UpdateIgnoredKindExposure_EngagedKindDropped()
    {
        var dict = new Dictionary<string, DateTimeOffset>();
        var t0 = DateTimeOffset.UnixEpoch;
        LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, NoEngaged, true, t0, TimeSpan.FromMinutes(5));
        // Bot now Attacks this kind → it is engaged, so dropped from the tracker.
        var engaged = (System.Collections.Generic.IReadOnlySet<string>)new HashSet<string> { "w:14" };
        var r = LlmGoalPolicy.UpdateIgnoredKindExposure(
            dict, new[] { ("w:14", false) }, engaged, true, t0.AddMinutes(10), TimeSpan.FromMinutes(5));
        Assert.Empty(r);
        Assert.Empty(dict);
    }

    private static WorldStateProjection EgressWorld(
        IReadOnlyList<VisibleObjectProjection> visible,
        IReadOnlySet<string>? killed,
        CombatFightStatus? fight = null)
        => new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "H", Landblock = 0xA8B4u, CellId = 0xA8B40006u,
                PositionX = 0, PositionY = 0, PositionZ = 0, HealthFraction = 1.0f, Level = 3,
            },
            Inventory = System.Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            KilledKindsThisDwell = killed,
            CurrentFight = fight,
        };

    private static Goal AttackGoal(Selector target) => new Goal
    {
        Kind = GoalKind.Attack, Target = target, Source = "llm",
    };

    [Fact]
    public void IsTappedOutRepeatKillAttack_NotTappedOut_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var world = EgressWorld(new[] { Mob(0x1u, "Chicken", 10u) }, new HashSet<string> { key });
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector { Name = "Chicken" }), world, tappedOut: false));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_NonAttackGoal_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var world = EgressWorld(new[] { Mob(0x1u, "Chicken", 10u) }, new HashSet<string> { key });
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Chicken" }, Source = "llm" };
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(talk, world, tappedOut: true));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_EmptySelector_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var world = EgressWorld(new[] { Mob(0x1u, "Chicken", 10u) }, new HashSet<string> { key });
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector()), world, tappedOut: true));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_NoKillsHere_False()
    {
        var world = EgressWorld(new[] { Mob(0x1u, "Chicken", 10u) }, null);
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector { Name = "Chicken" }), world, tappedOut: true));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_HostileInView_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        // A different mob is attacking the bot → self-defense outranks egress.
        var world = EgressWorld(
            new[] { Mob(0x1u, "Chicken", 10u), Mob(0x2u, "Drudge", 99u, hostile: true) },
            new HashSet<string> { key });
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector { Name = "Chicken" }), world, tappedOut: true));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_MidFight_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var world = EgressWorld(
            new[] { Mob(0x1u, "Chicken", 10u) }, new HashSet<string> { key },
            fight: new CombatFightStatus(0x1u, "Chicken", 0, 0, 0));
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector { Name = "Chicken" }), world, tappedOut: true));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_AllMatchesFarmed_True()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        var world = EgressWorld(
            new[] { Mob(0x1u, "Chicken", 10u), Mob(0x2u, "Chicken", 10u) },
            new HashSet<string> { key });
        Assert.True(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector { Name = "Chicken" }), world, tappedOut: true));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_NoVisibleMatch_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Chicken"))!;
        // Killed a Chicken here, but the Attack selector names a Drudge not in view.
        var world = EgressWorld(new[] { Mob(0x1u, "Chicken", 10u) }, new HashSet<string> { key });
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector { Name = "Drudge" }), world, tappedOut: true));
    }

    [Fact]
    public void IsTappedOutRepeatKillAttack_MixedMatchUnfarmed_False()
    {
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(10u, "Rat"))!;
        // Two visible "Rat" by NAME selector: one farmed wcid 10, one fresh wcid 99.
        var world = EgressWorld(
            new[] { Mob(0x1u, "Rat", 10u), Mob(0x2u, "Rat", 99u) },
            new HashSet<string> { key });
        Assert.False(LlmGoalPolicy.IsTappedOutRepeatKillAttack(
            AttackGoal(new Selector { Name = "Rat" }), world, tappedOut: true));
    }

    // ---- FitPromptToCeiling (request-size ceiling; prevents HTTP 413) ----

    [Fact]
    public void FitPromptToCeiling_UnderCeiling_ReturnsUnchanged()
    {
        var p = "PREAMBLE\n## Visible nearby\n- nearest\n- farther\n## Combat readiness\n- ready\n";
        Assert.Equal(p, LlmGoalPolicy.FitPromptToCeiling(p, ceiling: 10_000));
    }

    [Fact]
    public void FitPromptToCeiling_TrimsVisibleTail_KeepsNearest_LaterSectionsIntact()
    {
        var sb = new StringBuilder();
        sb.Append("PREAMBLE-FIXED\n");
        sb.Append("## Visible nearby\n");
        for (int i = 0; i < 200; i++)
            sb.Append("- obj").Append(i).Append(" dist=").Append(i).Append("u\n");
        sb.Append("## Combat readiness\n- monsters in view: 0\n");

        var result = LlmGoalPolicy.FitPromptToCeiling(sb.ToString(), ceiling: 1_000);

        Assert.True(result.Length <= 1_000, $"len={result.Length}");
        Assert.Contains("- obj0 dist=0u", result);            // nearest retained
        Assert.DoesNotContain("- obj199 dist=199u", result);  // farthest dropped
        Assert.Contains("omitted to fit prompt budget", result);
        Assert.Contains("## Combat readiness", result);        // section after the trim intact
        Assert.Contains("- monsters in view: 0", result);
    }

    [Fact]
    public void FitPromptToCeiling_OverageExceedsVisible_CascadesThroughSightingsAndEvents()
    {
        // Regression for the rubber-duck blocking issue: when the overage is
        // larger than the whole `## Visible nearby` section, trimming Visible
        // alone cannot fit — the cascade must also shed the lower-value
        // `## Recently sighted` and `## Recent events` row-sections while
        // leaving the fixed sections intact.
        var sb = new StringBuilder();
        sb.Append("PREAMBLE-FIXED-KEEP\n");
        sb.Append("## Recently sighted (out of view)\n");
        for (int i = 0; i < 100; i++) sb.Append("- sight").Append(i).Append('\n');
        sb.Append("## Visible nearby\n- vis0\n- vis1\n");
        sb.Append("## Combat readiness\n- ready-line\n");
        sb.Append("## Recent events (newest first)\n");
        for (int i = 0; i < 100; i++) sb.Append("- event").Append(i).Append('\n');
        sb.Append("## Recent rejections\n- keep-this\n");

        var result = LlmGoalPolicy.FitPromptToCeiling(sb.ToString(), ceiling: 400);

        Assert.True(result.Length <= 400, $"len={result.Length}");
        Assert.Contains("PREAMBLE-FIXED-KEEP", result);   // fixed preamble intact
        Assert.Contains("## Combat readiness", result);   // fixed section intact
        Assert.Contains("- ready-line", result);
        Assert.Contains("- keep-this", result);           // section after events intact
    }

    [Fact]
    public void FitPromptToCeiling_TrimsNpcRecallBlock_InCascade()
    {
        // cp-2423: the NPC recall block must be in PromptTrimOrder so it sheds
        // trailing rows under budget pressure, before the fixed sections.
        var sb = new StringBuilder();
        sb.Append("PREAMBLE-FIXED-KEEP\n");
        sb.Append("## Recently sighted NPCs (out of view)\n");
        for (int i = 0; i < 100; i++) sb.Append("- npc").Append(i).Append('\n');
        sb.Append("## Combat readiness\n- ready-line\n");

        var result = LlmGoalPolicy.FitPromptToCeiling(sb.ToString(), ceiling: 200);

        Assert.True(result.Length <= 200, $"len={result.Length}");
        Assert.Contains("PREAMBLE-FIXED-KEEP", result);   // fixed preamble intact
        Assert.Contains("## Combat readiness", result);   // fixed section intact
        Assert.Contains("- ready-line", result);
        Assert.DoesNotContain("- npc99", result);         // NPC recall tail trimmed
    }

    [Fact]
    public void FitPromptToCeiling_HandlesCrlf_AndAbsentTrimSections()
    {
        var sb = new StringBuilder();
        sb.Append("PREAMBLE-FIXED\r\n");
        sb.Append("## Visible nearby\r\n");
        for (int i = 0; i < 200; i++)
            sb.Append("- obj").Append(i).Append("\r\n");
        sb.Append("## Combat readiness\r\n- ready\r\n");

        var result = LlmGoalPolicy.FitPromptToCeiling(sb.ToString(), ceiling: 1_000);

        Assert.True(result.Length <= 1_000, $"len={result.Length}");
        Assert.Contains("\r\n", result);                  // CRLF preserved
        Assert.Contains("- obj0\r\n", result);
        Assert.Contains("omitted to fit prompt budget", result);
        Assert.Contains("## Combat readiness", result);
    }

    [Fact]
    public void FitPromptToCeiling_NoTrimmableSections_StillHardCappedByBackstop()
    {
        // Pathological: over ceiling but NONE of the cascade headers are
        // present. The defensive backstop must still guarantee <= ceiling.
        var sb = new StringBuilder();
        sb.Append("## Self\n");
        for (int i = 0; i < 5_000; i++) sb.Append("fixed-content-line\n");
        var prompt = sb.ToString();
        Assert.True(prompt.Length > 2_000);

        var result = LlmGoalPolicy.FitPromptToCeiling(prompt, ceiling: 2_000);

        Assert.True(result.Length <= 2_000, $"len={result.Length}");
        Assert.Contains("hard-truncated to fit request budget", result);
    }

    // ---- FitPromptToCeiling protected-suffix overload (cp-2343) ----
    // The salience capsules render at the very end of the prompt. A verified
    // live failure showed the single-arg hard-cut guillotining that entire
    // tail when a rich context overflowed the ceiling. The protected-suffix
    // overload reserves the capsule bytes and hard-cuts only the BODY, so the
    // capsules always survive AND the fixed ## Combat readiness (which renders
    // far above the capsules) is never cut — preserving the cp-2334 invariant.

    [Fact]
    public void FitPromptToCeiling_ProtectedSuffix_SurvivesWhenBodyOverflows()
    {
        var bodySb = new StringBuilder();
        bodySb.Append("PREAMBLE-FIXED-KEEP\n");
        bodySb.Append("## Combat readiness\n- monsters in view: 0\n");
        bodySb.Append("## Visible nearby\n");
        for (int i = 0; i < 300; i++) bodySb.Append("- obj").Append(i).Append("\n");
        bodySb.Append("## Recent goal outcomes (your own recent goals)\n");
        for (int i = 0; i < 300; i++) bodySb.Append("- outcome").Append(i).Append("\n");

        var suffix =
            "\n## Unspent XP\n- unspent: 100\n" +
            "## Recent Talk\n- talked\n" +
            "## Recent Use\n- used\n" +
            "## Server-refused interaction targets\n- refused\n" +
            "## Approach distance history\n- 27.5u, 27.5u\n";

        var result = LlmGoalPolicy.FitPromptToCeiling(bodySb.ToString(), suffix, ceiling: 900);

        Assert.True(result.Length <= 900, $"len={result.Length}");
        Assert.EndsWith(suffix, result);                 // capsule tail intact at the end
        Assert.Contains("## Unspent XP", result);
        Assert.Contains("## Recent Talk", result);
        Assert.Contains("## Recent Use", result);
        Assert.Contains("## Server-refused interaction targets", result);
        Assert.Contains("## Approach distance history", result);
        Assert.Contains("## Combat readiness", result);  // fixed section preserved
    }

    [Fact]
    public void FitPromptToCeiling_ProtectedSuffix_PreservesCombatReadiness_WhenBodyHardCut()
    {
        // The duck's critical cp-2334-regression case: the body has NO
        // trimmable sections, so it MUST be hard-cut. ## Combat readiness
        // renders early; a huge non-trimmable section follows. The body
        // hard-cut must eat the trailing non-trimmable rows, NOT the early
        // fixed section, and the protected capsule suffix must survive.
        var bodySb = new StringBuilder();
        bodySb.Append("## Combat readiness\n- ready-CRITICAL\n");
        bodySb.Append("## Recent goal outcomes (your own recent goals)\n");
        for (int i = 0; i < 2_000; i++) bodySb.Append("- outcome").Append(i).Append("\n");

        var suffix = "\n## Unspent XP\n- unspent: 42\n## Approach distance history\n- 9.9u, 9.9u\n";

        var result = LlmGoalPolicy.FitPromptToCeiling(bodySb.ToString(), suffix, ceiling: 500);

        Assert.True(result.Length <= 500, $"len={result.Length}");
        Assert.Contains("## Combat readiness", result);
        Assert.Contains("- ready-CRITICAL", result);
        Assert.EndsWith(suffix, result);                 // capsules survive the body hard-cut
        Assert.Contains("hard-truncated to fit request budget", result);
    }

    [Fact]
    public void FitPromptToCeiling_ProtectedSuffix_EmptyBehavesLikeSingleArg()
    {
        var p = "PREAMBLE\n## Visible nearby\n- a\n- b\n## Combat readiness\n- ready\n";
        Assert.Equal(
            LlmGoalPolicy.FitPromptToCeiling(p, ceiling: 10_000),
            LlmGoalPolicy.FitPromptToCeiling(p, "", ceiling: 10_000));
    }

    [Fact]
    public void FitPromptToCeiling_ProtectedSuffix_UnderCeiling_ReturnsBodyPlusSuffix()
    {
        var body = "PREAMBLE\n## Combat readiness\n- ready\n";
        var suffix = "\n## Unspent XP\n- unspent: 5\n";
        Assert.Equal(body + suffix, LlmGoalPolicy.FitPromptToCeiling(body, suffix, ceiling: 10_000));
    }

    [Fact]
    public void FitPromptToCeiling_ProtectedSuffix_PathologicalHugeSuffix_StillCapped()
    {
        // Impossible-by-construction (each capsule caps its rows) but defended:
        // a protected suffix that alone meets/exceeds the ceiling must not
        // break the ceiling invariant — fall back to whole-string hard-cut.
        var body = "## Combat readiness\n- ready\n";
        var suffix = new string('x', 300);
        var result = LlmGoalPolicy.FitPromptToCeiling(body, suffix, ceiling: 100);
        Assert.True(result.Length <= 100, $"len={result.Length}");
    }

    [Fact]
    public void FitPromptToCeiling_SingleArg_TinyCeilingBelowMarkerLength_StillCapped()
    {
        // Root-cause regression: the hard-cut marker is ~48 chars; with a
        // ceiling below that, `result[..cut] + marker` alone exceeds the
        // ceiling unless the final clamp fires.
        var prompt = new string('y', 500);
        var result = LlmGoalPolicy.FitPromptToCeiling(prompt, ceiling: 20);
        Assert.True(result.Length <= 20, $"len={result.Length}");
    }

    [Fact]
    public void FitPromptToCeiling_ProtectedSuffix_LargeSuffix_TinyInnerCeiling_StillCapped()
    {
        // The codex-review counterexample: a protected suffix that is large
        // but still < ceiling leaves an inner body budget (ceiling − suffix)
        // below the marker length. The overload must still honour the OUTER
        // ceiling once the body is truncated and the suffix re-appended.
        var body = new string('b', 5_000);
        var suffix = "\n## Unspent XP\n" + new string('s', 70); // < ceiling, big
        var result = LlmGoalPolicy.FitPromptToCeiling(body, suffix, ceiling: 100);
        Assert.True(result.Length <= 100, $"len={result.Length}");
        Assert.EndsWith(suffix, result); // capsule suffix still intact
    }

    // ---- ResolvePromptCeiling (AC_BOTS_PROMPT_CEILING deploy-time override) ----

    [Theory]
    [InlineData(null)]         // unset
    [InlineData("")]           // empty
    [InlineData("   ")]        // whitespace only
    [InlineData("abc")]        // unparseable
    [InlineData("24000abc")]   // trailing garbage -> TryParse fails
    [InlineData("0")]          // below min
    [InlineData("9999")]       // just below min bound
    [InlineData("26001")]      // just above the hard ceiling
    [InlineData("40000")]      // well above the hard ceiling
    [InlineData("-24000")]     // negative
    public void ResolvePromptCeiling_InvalidOrOutOfRange_FallsBackToDefault(string? envValue)
    {
        Assert.Equal(26000, LlmGoalPolicy.ResolvePromptCeiling(envValue));
    }

    [Theory]
    [InlineData("24000", 24000)]   // the gpt-4o-fitting deploy value
    [InlineData("10000", 10000)]   // inclusive lower bound
    [InlineData("26000", 26000)]   // inclusive upper bound (the default)
    [InlineData("  24500  ", 24500)] // int.TryParse tolerates surrounding whitespace
    [InlineData("18000", 18000)]
    public void ResolvePromptCeiling_ValidInRange_IsHonoured(string envValue, int expected)
    {
        Assert.Equal(expected, LlmGoalPolicy.ResolvePromptCeiling(envValue));
    }

    [Fact]
    public void ResolvePromptCeiling_MinBound_IsAccepted()
    {
        // Guards the documented [MinConfigurablePromptCeilingChars, 26000] window:
        // the min itself resolves to itself, one below it falls back to default.
        var min = LlmGoalPolicy.MinConfigurablePromptCeilingChars;
        Assert.Equal(min, LlmGoalPolicy.ResolvePromptCeiling(min.ToString()));
        Assert.Equal(26000, LlmGoalPolicy.ResolvePromptCeiling((min - 1).ToString()));
    }

    // ---- LowerCeilingOnPayloadTooLarge (adaptive ceiling auto-lowers on 413) ----

    [Fact]
    public void LowerCeilingOnPayloadTooLarge_On413Status_StepsDownNotStraightToFloor()
    {
        // A single 413 must NOT crater the whole session to the floor: it steps
        // the ceiling DOWN by the backoff factor, preserving most of the context
        // budget so the next call (and any model the fallback rotation lands on)
        // keeps a usable prompt.
        var floor = LlmGoalPolicy.MinConfigurablePromptCeilingChars;
        var lowered = LlmGoalPolicy.LowerCeilingOnPayloadTooLarge(
            26000, System.Net.HttpStatusCode.RequestEntityTooLarge, error: null, floor);
        Assert.Equal(20800, lowered);          // 26000 * 0.8
        Assert.True(lowered > floor, "one 413 must not collapse straight to the floor");
        Assert.True(lowered < 26000, "the ceiling must step down");
    }

    [Fact]
    public void LowerCeilingOnPayloadTooLarge_On413ErrorString_StepsDown()
    {
        // The structured status may be absent; the "http 413" error string is the
        // fallback signal (mirrors the 429 detection's belt-and-braces check).
        var floor = LlmGoalPolicy.MinConfigurablePromptCeilingChars;
        var lowered = LlmGoalPolicy.LowerCeilingOnPayloadTooLarge(
            26000, status: null, "http 413: Payload Too Large", floor);
        Assert.Equal(20800, lowered);
        Assert.True(lowered > floor);
    }

    [Fact]
    public void LowerCeilingOnPayloadTooLarge_NonPayloadFailure_LeavesCeilingUnchanged()
    {
        // A 429 (or any non-413) must NOT lower the ceiling — only a payload
        // rejection adapts the request size; rate limits are handled by backoff.
        var floor = LlmGoalPolicy.MinConfigurablePromptCeilingChars;
        Assert.Equal(26000, LlmGoalPolicy.LowerCeilingOnPayloadTooLarge(
            26000, (System.Net.HttpStatusCode)429, "http 429: Too Many Requests", floor));
        Assert.Equal(26000, LlmGoalPolicy.LowerCeilingOnPayloadTooLarge(
            26000, status: null, error: null, floor));
    }

    [Fact]
    public void LowerCeilingOnPayloadTooLarge_RepeatedPayloadFailures_WalkDownToFloorNeverBelow()
    {
        // Repeated 413s walk the ceiling down step by step and converge to the
        // floor, never below it; once at the floor it stays there (one-way).
        var floor = LlmGoalPolicy.MinConfigurablePromptCeilingChars;
        var ceiling = 26000;
        var prev = ceiling;
        for (var i = 0; i < 20; i++)
        {
            ceiling = LlmGoalPolicy.LowerCeilingOnPayloadTooLarge(
                ceiling, System.Net.HttpStatusCode.RequestEntityTooLarge, error: null, floor);
            Assert.True(ceiling >= floor, $"ceiling {ceiling} dropped below floor {floor}");
            Assert.True(ceiling <= prev, "ceiling must be monotonically non-increasing");
            prev = ceiling;
        }
        Assert.Equal(floor, ceiling);
    }

    [Fact]
    public void LowerCeilingOnPayloadTooLarge_StepBelowFloor_ClampsToFloor()
    {
        // When one backoff step would land below the floor, it clamps to the
        // floor (never below) rather than overshooting.
        var floor = LlmGoalPolicy.MinConfigurablePromptCeilingChars;
        Assert.Equal(floor, LlmGoalPolicy.LowerCeilingOnPayloadTooLarge(
            floor + 2000, System.Net.HttpStatusCode.RequestEntityTooLarge, error: null, floor));
    }

    [Fact]
    public void LowerCeilingOnPayloadTooLarge_OneWay_AtFloorStaysAtFloor()
    {
        // Already at the floor: a 413 keeps it at the floor (never raises, never
        // drops below the configured minimum).
        var floor = LlmGoalPolicy.MinConfigurablePromptCeilingChars;
        Assert.Equal(floor, LlmGoalPolicy.LowerCeilingOnPayloadTooLarge(
            floor, System.Net.HttpStatusCode.RequestEntityTooLarge, error: null, floor));
    }
}
