// SPDX-License-Identifier: AGPL-3.0-or-later
// Slice R wiring tests — exercises the integration glue:
//   - BotStatistics.Pump counters (kills, levels, units, corpses).
//   - IntentStackOpsApplier.TryApply atomic batch contract.
//   - LlmGoalPolicy.TryParseStackOps round-trips the LLM envelope.
//   - LlmGoalPolicy.BuildUserPrompt renders the stack section.
//   - JSON round-trip + IsSatisfied evaluation of the new predicates.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using Xunit;

namespace HeadlessAcClient.Tests;

public class IntentStackWiringTests
{
    private const uint SelfGuid = 0x50000005;

    // ---- BotStatistics ----

    [Fact]
    public void BotStatistics_PumpCountsKillsFromAttackGoalCompleted()
    {
        var stats = new BotStatistics();
        var events = new EventStream();
        var world = BuildWorld(level: 1);

        events.Append(NewEvent(EventKind.GoalCompleted, text: "Attack target=name=\"Drudge\""));
        events.Append(NewEvent(EventKind.GoalCompleted, text: "Talk target=name=\"Jonathan\""));
        events.Append(NewEvent(EventKind.GoalCompleted, text: "Attack target=name=\"Golem\""));

        stats.Pump(events, world);
        Assert.Equal(2, stats.Kills);

        // Idempotent.
        stats.Pump(events, world);
        Assert.Equal(2, stats.Kills);

        events.Append(NewEvent(EventKind.GoalCompleted, text: "Attack target=name=\"Mosswart\""));
        stats.Pump(events, world);
        Assert.Equal(3, stats.Kills);
    }

    [Fact]
    public void BotStatistics_LevelsGainedTracksDeltaFromFirstObservedLevel()
    {
        var stats = new BotStatistics();
        var events = new EventStream();

        stats.Pump(events, BuildWorld(level: 3));
        Assert.Equal(0, stats.LevelsGained);

        stats.Pump(events, BuildWorld(level: 5));
        Assert.Equal(2, stats.LevelsGained);

        stats.Pump(events, BuildWorld(level: 4));
        Assert.Equal(2, stats.LevelsGained);  // monotonic

        stats.Pump(events, BuildWorld(level: 8));
        Assert.Equal(5, stats.LevelsGained);
    }

    [Fact]
    public void BotStatistics_UnitsTraveledIntegratesXYDeltasAndZerosOnLandblockChange()
    {
        var stats = new BotStatistics();
        var events = new EventStream();

        stats.Pump(events, BuildWorldAt(landblock: 0x8602u, x: 0f, y: 0f));
        Assert.Equal(0, stats.UnitsTraveled);

        stats.Pump(events, BuildWorldAt(landblock: 0x8602u, x: 5f, y: 0f));
        Assert.InRange(stats.UnitsTraveled, 4.99, 5.01);

        stats.Pump(events, BuildWorldAt(landblock: 0x8602u, x: 5f, y: 5f));
        Assert.InRange(stats.UnitsTraveled, 9.99, 10.01);

        // Per-tick cap (>= 50u) -> no credit.
        stats.Pump(events, BuildWorldAt(landblock: 0x8602u, x: 200f, y: 200f));
        Assert.InRange(stats.UnitsTraveled, 9.99, 10.01);

        // Landblock crossing resets integrator.
        stats.Pump(events, BuildWorldAt(landblock: 0xA9B4u, x: 200f, y: 200f));
        Assert.InRange(stats.UnitsTraveled, 9.99, 10.01);

        stats.Pump(events, BuildWorldAt(landblock: 0xA9B4u, x: 203f, y: 200f));
        Assert.InRange(stats.UnitsTraveled, 12.99, 13.01);
    }

    [Fact]
    public void BotStatistics_IncrementCorpsesOpenedHook()
    {
        var stats = new BotStatistics();
        Assert.Equal(0, stats.CorpsesOpened);
        stats.IncrementCorpsesOpened();
        stats.IncrementCorpsesOpened();
        Assert.Equal(2, stats.CorpsesOpened);
    }

    // ---- Stats-backed predicates ----

    [Fact]
    public void KillCountTotalAtLeastPredicate_UsesLifetimeCounter()
    {
        var stats = new BotStatistics();
        var events = new EventStream();
        events.Append(NewEvent(EventKind.GoalCompleted, text: "Attack a"));
        events.Append(NewEvent(EventKind.GoalCompleted, text: "Attack b"));
        events.Append(NewEvent(EventKind.GoalCompleted, text: "Attack c"));
        stats.Pump(events, BuildWorld());

        var world = BuildWorld();
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow, stats);
        var ctx = new IntentEvalContext(world, events, baseline, DateTime.UtcNow) { Stats = stats };

        Assert.True( new KillCountTotalAtLeastPredicate(3).IsSatisfied(ctx));
        Assert.True( new KillCountTotalAtLeastPredicate(2).IsSatisfied(ctx));
        Assert.False(new KillCountTotalAtLeastPredicate(4).IsSatisfied(ctx));

        // Without Stats -> falsey.
        var ctxNoStats = new IntentEvalContext(world, events, baseline, DateTime.UtcNow);
        Assert.False(new KillCountTotalAtLeastPredicate(1).IsSatisfied(ctxNoStats));
    }

    [Fact]
    public void NumDeathsAtLeast_UsesServerProjection()
    {
        var world0 = BuildWorld(numDeaths: 0);
        var baseline = IntentBaseline.Capture(world0, new EventStream(), DateTime.UtcNow);
        var ctx0 = new IntentEvalContext(world0, new EventStream(), baseline, DateTime.UtcNow);
        Assert.False(new NumDeathsAtLeastPredicate(1).IsSatisfied(ctx0));

        var world1 = BuildWorld(numDeaths: 1);
        var ctx1 = new IntentEvalContext(world1, new EventStream(), baseline, DateTime.UtcNow);
        Assert.True( new NumDeathsAtLeastPredicate(1).IsSatisfied(ctx1));
        Assert.False(new NumDeathsAtLeastPredicate(2).IsSatisfied(ctx1));
    }

    [Fact]
    public void NumDeathsSincePushAtMost_FiresWhenBudgetExceeded()
    {
        var world = BuildWorld(numDeaths: 2);
        var baseline = IntentBaseline.Capture(world, new EventStream(), DateTime.UtcNow);

        // Same death count -> not exceeded.
        var same = new IntentEvalContext(world, new EventStream(), baseline, DateTime.UtcNow);
        Assert.False(new NumDeathsSincePushAtMostPredicate(2).IsSatisfied(same));

        // 2 deaths since push -> not yet exceeded (>2 needed).
        var died2 = new IntentEvalContext(BuildWorld(numDeaths: 4), new EventStream(), baseline, DateTime.UtcNow);
        Assert.False(new NumDeathsSincePushAtMostPredicate(2).IsSatisfied(died2));

        // 3 deaths since push -> exceeded.
        var died3 = new IntentEvalContext(BuildWorld(numDeaths: 5), new EventStream(), baseline, DateTime.UtcNow);
        Assert.True( new NumDeathsSincePushAtMostPredicate(2).IsSatisfied(died3));
    }

    [Fact]
    public void CoinValueAtLeast_UsesServerProjection()
    {
        var world = BuildWorld(coin: 500);
        var ctx = new IntentEvalContext(world, new EventStream(),
            IntentBaseline.Capture(world, new EventStream(), DateTime.UtcNow), DateTime.UtcNow);
        Assert.True( new CoinValueAtLeastPredicate(500).IsSatisfied(ctx));
        Assert.True( new CoinValueAtLeastPredicate(1).IsSatisfied(ctx));
        Assert.False(new CoinValueAtLeastPredicate(501).IsSatisfied(ctx));
    }

    [Fact]
    public void CoinGainSincePushAtLeast_DeltaFromBaseline()
    {
        var world0 = BuildWorld(coin: 100);
        var baseline = IntentBaseline.Capture(world0, new EventStream(), DateTime.UtcNow);

        var world1 = BuildWorld(coin: 350);
        var ctx = new IntentEvalContext(world1, new EventStream(), baseline, DateTime.UtcNow);
        Assert.True( new CoinGainSincePushAtLeastPredicate(250).IsSatisfied(ctx));
        Assert.False(new CoinGainSincePushAtLeastPredicate(251).IsSatisfied(ctx));
    }

    [Fact]
    public void UnitsTraveledSincePush_DeltaFromStatsBaseline()
    {
        var stats = new BotStatistics();
        var events = new EventStream();
        stats.Pump(events, BuildWorldAt(landblock: 0x8602u, x: 0f, y: 0f));
        stats.Pump(events, BuildWorldAt(landblock: 0x8602u, x: 10f, y: 0f));

        var world = BuildWorldAt(landblock: 0x8602u, x: 10f, y: 0f);
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow, stats);
        Assert.InRange(baseline.StatsAtPush.UnitsTraveled, 9.99, 10.01);

        stats.Pump(events, BuildWorldAt(landblock: 0x8602u, x: 30f, y: 0f));

        var ctx = new IntentEvalContext(world, events, baseline, DateTime.UtcNow) { Stats = stats };
        Assert.True( new UnitsTraveledSincePushAtLeastPredicate(20).IsSatisfied(ctx));
        Assert.False(new UnitsTraveledSincePushAtLeastPredicate(21).IsSatisfied(ctx));
    }

    // ---- IntentStackOpsApplier (atomic-batch contract) ----

    [Fact]
    public void Applier_TryApply_AllOrNothing_RejectsOnSecondOpInvalid()
    {
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "session-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));
        var preRev = stack.Revision;

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf(kind: "sub-a"), Reason = "ok" },
            new() { Op = IntentStackOpKind.Push, Intent = null, Reason = "boom" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, preRev, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.RejectedInvalid, outcome.Result);
        Assert.Equal(1, stack.Depth);
        Assert.Equal(preRev, stack.Revision);
    }

    [Fact]
    public void Applier_TryApply_RejectsOnRevisionMismatch()
    {
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("sub"), Reason = "x" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops,
            echoedRevision: stack.Revision + 10, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.RejectedRevision, outcome.Result);
        Assert.Equal(1, stack.Depth);
    }

    [Fact]
    public void Applier_TryApply_RejectsRootPop()
    {
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "k", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var ops = new IntentStackOp[] { new() { Op = IntentStackOpKind.PopTop, Reason = "root-attempt" } };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, null, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.RejectedRootPop, outcome.Result);
        Assert.Equal(1, stack.Depth);
    }

    [Fact]
    public void Applier_TryApply_RejectsOverflow()
    {
        var stack = new IntentStack(maxDepth: 2);
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        stack.TryPush(NewIntent("root", "k", b));
        stack.TryPush(NewIntent("sub",  "k", b));

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("would-overflow"), Reason = "x" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, null, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.RejectedOverflow, outcome.Result);
        Assert.Equal(2, stack.Depth);
    }

    [Fact]
    public void Applier_TryApply_SuccessfulMultiOpBatchCommits()
    {
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("k-sub1", targetName: "Jonathan"), Reason = "push sub1" },
            new() { Op = IntentStackOpKind.MarkTopBlocked, Reason = "stalled" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, null, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(2, stack.Depth);
        Assert.Equal(IntentLifecycle.Blocked, stack.Top!.Status);
        Assert.Equal(2, outcome.AppliedLog.Count);
    }

    // ---- Render block ----

    [Fact]
    public void RenderStackForPrompt_EmptyStack_ContainsRevisionAndHint()
    {
        var stack = new IntentStack();
        var s = IntentStackOpsApplier.RenderStackForPrompt(stack);
        Assert.Contains("revision=0", s);
        Assert.Contains("depth=0", s);
        Assert.Contains("(empty", s);
    }

    [Fact]
    public void RenderStackForPrompt_RendersTopInFullAndAncestorsCompact()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b));
        stack.TryPush(NewIntent("i-quest", "do-quest", b, completion: new LevelAtLeastPredicate(5)));

        var s = IntentStackOpsApplier.RenderStackForPrompt(stack);
        Assert.Contains("revision=2", s);
        Assert.Contains("ancestor[0]", s);
        Assert.Contains("TOP", s);
        Assert.Contains("id=i-quest", s);
        Assert.Contains("kind=do-quest", s);
    }

    // ---- LlmGoalPolicy parse + render ----

    [Fact]
    public void TryParseStackOps_HandlesAbsentFields()
    {
        var json = """{"goal_id":"00000000-0000-0000-0000-000000000001","kind":"Explore","target":{"name":"anywhere"},"rationale":"x","priority":3}""";
        var ok = LlmGoalPolicy.TryParseStackOps(json, out var rev, out var ops, out var err);
        Assert.True(ok, err);
        Assert.Null(rev);
        Assert.Null(ops);
        Assert.Null(err);
    }

    [Fact]
    public void TryParseStackOps_ParsesPushWithTypedCompletion()
    {
        var json = """
{
  "stack_revision": 7,
  "stack_ops": [
    {
      "op": "push",
      "intent": {
        "id": "i-005",
        "kind": "quest:reach-lifestone",
        "target_name": "Glowing Lifestone",
        "rationale": "attune",
        "completion": { "type": "visible_tag", "tag": "lifestone" }
      },
      "reason": "attune-step"
    }
  ],
  "goal_id": "11111111-1111-1111-1111-111111111111",
  "kind": "Explore",
  "target": {"name":"anywhere"},
  "rationale": "x",
  "priority": 3
}
""";
        var ok = LlmGoalPolicy.TryParseStackOps(json, out var rev, out var ops, out var err);
        Assert.True(ok, err);
        Assert.Equal(7L, rev);
        Assert.NotNull(ops);
        Assert.Single(ops!);
        Assert.Equal(IntentStackOpKind.Push, ops![0].Op);
        Assert.NotNull(ops[0].Intent);
        Assert.Equal("i-005", ops[0].Intent!.Id);
        Assert.IsType<VisibleTagPredicate>(ops[0].Intent!.Completion);
    }

    [Fact]
    public void TryParseStackOps_ParsesAllFourOpKinds()
    {
        var json = """
{
  "stack_ops": [
    {"op":"push","intent":{"kind":"a","completion":{"type":"always_false"}},"reason":"r"},
    {"op":"pop_top","reason":"done"},
    {"op":"replace_top","intent":{"kind":"b","completion":{"type":"always_false"}},"reason":"swap"},
    {"op":"mark_top_blocked","reason":"stuck"}
  ]
}
""";
        var ok = LlmGoalPolicy.TryParseStackOps(json, out var rev, out var ops, out var err);
        Assert.True(ok, err);
        Assert.Null(rev);
        Assert.NotNull(ops);
        Assert.Equal(4, ops!.Count);
        Assert.Equal(IntentStackOpKind.Push, ops[0].Op);
        Assert.Equal(IntentStackOpKind.PopTop, ops[1].Op);
        Assert.Equal(IntentStackOpKind.ReplaceTop, ops[2].Op);
        Assert.Equal(IntentStackOpKind.MarkTopBlocked, ops[3].Op);
    }

    [Fact]
    public void TryParseStackOps_HandlesMalformedGracefully()
    {
        var ok = LlmGoalPolicy.TryParseStackOps(
            "{ not-valid-json",
            out var rev, out var ops, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Null(rev);
        Assert.Null(ops);
    }

    [Fact]
    public void BuildUserPrompt_WithStack_RendersIntentStackSection()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b));

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack);
        Assert.Contains("## Intent stack", prompt);
        Assert.Contains("revision=1", prompt);
        Assert.Contains("stack_revision", prompt);
        Assert.Contains("stack_ops", prompt);
        Assert.Contains("predicate_request", prompt);
    }

    // ---- No-active-objective render fix + salience capsule (cp-2345) ----

    [Fact]
    public void RenderStackForPrompt_ActiveTop_LabelsActionable()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b));
        var s = IntentStackOpsApplier.RenderStackForPrompt(stack);
        Assert.Contains("act on this until its completion predicate fires", s);
    }

    [Fact]
    public void RenderStackForPrompt_TerminalTop_NotLabeledActionable()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        // Always-satisfied predicate -> CheckTopForCompletion marks the ROOT
        // Completed in place (depth stays 1).
        stack.TryPush(NewIntent("i-root", "arm-self", b, completion: new NotPredicate(new AlwaysFalsePredicate())));
        stack.CheckTopForCompletion(BuildWorld(), new EventStream(), DateTime.UtcNow);
        Assert.Equal(IntentLifecycle.Completed, stack.Top!.Status);

        var s = IntentStackOpsApplier.RenderStackForPrompt(stack);
        Assert.DoesNotContain("act on this until its completion predicate fires", s);
        Assert.Contains("NO LONGER active", s);
        Assert.Contains("status Completed", s);
    }

    [Fact]
    public void BuildUserPrompt_NoActiveObjectiveCapsule_RendersWhenStackEmpty()
    {
        var stack = new IntentStack(); // depth 0, no top
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack);
        Assert.Contains("## No active objective", prompt);
        Assert.Contains("current top: empty", prompt);
        Assert.Contains("stack_ops", prompt);
        Assert.Contains("raw fact, not a recommendation", prompt);
    }

    [Fact]
    public void BuildUserPrompt_NoActiveObjectiveCapsule_OmittedWhenTopActive()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b)); // Active top
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack);
        Assert.DoesNotContain("## No active objective", prompt);
    }

    [Fact]
    public void BuildUserPrompt_NoActiveObjectiveCapsule_RendersWhenTopTerminal()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "arm-self", b, completion: new NotPredicate(new AlwaysFalsePredicate())));
        stack.CheckTopForCompletion(BuildWorld(), new EventStream(), DateTime.UtcNow);
        Assert.Equal(IntentLifecycle.Completed, stack.Top!.Status);

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack);
        Assert.Contains("## No active objective", prompt);
        Assert.Contains("current top: Completed", prompt);
    }

    [Fact]
    public void BuildUserPrompt_NoActiveObjectiveCapsule_OmittedWhenNoStack()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null);
        Assert.DoesNotContain("## No active objective", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithoutStack_OmitsStackSchema()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null);
        Assert.DoesNotContain("## Intent stack", prompt);
        Assert.DoesNotContain("stack_ops", prompt);
    }

    // ---- JSON round-trip for new predicate types ----

    [Theory]
    [InlineData("""{"type":"kill_count_total_at_least","count":5}""",         typeof(KillCountTotalAtLeastPredicate))]
    [InlineData("""{"type":"levels_gained_total_at_least","count":3}""",      typeof(LevelsGainedTotalAtLeastPredicate))]
    [InlineData("""{"type":"num_deaths_at_least","count":1}""",               typeof(NumDeathsAtLeastPredicate))]
    [InlineData("""{"type":"num_deaths_since_push_at_most","count":2}""",     typeof(NumDeathsSincePushAtMostPredicate))]
    [InlineData("""{"type":"coin_value_at_least","count":1000}""",            typeof(CoinValueAtLeastPredicate))]
    [InlineData("""{"type":"coin_gain_since_push_at_least","count":500}""",   typeof(CoinGainSincePushAtLeastPredicate))]
    [InlineData("""{"type":"units_traveled_since_push_at_least","count":250}""", typeof(UnitsTraveledSincePushAtLeastPredicate))]
    public void Predicate_JsonRoundTrip_NewTypes(string json, Type expected)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<IntentPredicate>(json, opts);
        Assert.NotNull(parsed);
        Assert.IsType(expected, parsed!);
        var roundtrip = JsonSerializer.Serialize(parsed, opts);
        var twice = JsonSerializer.Deserialize<IntentPredicate>(roundtrip, opts);
        Assert.IsType(expected, twice!);
    }

    // ---- helpers ----

    private static StreamEvent NewEvent(EventKind k, string? text = null, string? name = null, uint? wcid = null) =>
        new()
        {
            Sequence = -1,
            Utc = DateTimeOffset.UtcNow,
            Kind = k,
            Text = text,
            Name = name,
            Wcid = wcid,
        };

    private static Intent NewIntent(string id, string kind, IntentBaseline baseline,
        IntentPredicate? completion = null, DateTime? deadline = null) =>
        new()
        {
            Id = id,
            Kind = kind,
            Rationale = $"test:{kind}",
            Completion = completion ?? new AlwaysFalsePredicate(),
            Baseline = baseline,
            DeadlineUtc = deadline,
        };

    private static IntentSpec SpecOf(string kind, string? targetName = null, IntentPredicate? completion = null) =>
        new()
        {
            Kind = kind,
            TargetName = targetName,
            Completion = completion ?? new AlwaysFalsePredicate(),
        };

    private static WorldStateProjection BuildWorld(
        int? level = 1,
        uint? landblock = 0x8602u,
        int? numDeaths = null,
        int? coin = null,
        IReadOnlyList<VisibleObjectProjection>? visible = null,
        IReadOnlyList<InventoryItemProjection>? inventory = null) =>
        new()
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid,
                Name = "Headless",
                Landblock = landblock,
                CellId = 0x86020001u,
                PositionX = 0, PositionY = 0, PositionZ = 0,
                Level = level,
                HealthFraction = 1.0f,
                NumDeaths = numDeaths,
                CoinValue = coin,
            },
            Visible = visible ?? Array.Empty<VisibleObjectProjection>(),
            Inventory = inventory ?? Array.Empty<InventoryItemProjection>(),
        };

    private static WorldStateProjection BuildWorldAt(uint landblock, float x, float y) =>
        new()
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid,
                Name = "Headless",
                Landblock = landblock,
                CellId = (landblock << 16) | 1u,
                PositionX = x, PositionY = y, PositionZ = 0,
                Level = 1,
                HealthFraction = 1.0f,
            },
            Visible = Array.Empty<VisibleObjectProjection>(),
            Inventory = Array.Empty<InventoryItemProjection>(),
        };

    // ---- Slice V — autonomous picker activity surface (#86) ----

    [Fact]
    public void BuildUserPrompt_WithPickerActivity_RendersActivityBlock()
    {
        var activity = new PickerActivity
        {
            TargetGuid   = 0x8000ABCDu,
            TargetName   = "Some Chest",
            Source       = "in-range",
            Reason       = "schema-only picker (type+visited+distance scoring within search radius)",
            StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-7),
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: activity);

        // The activity block header is followed by its first
        // bullet line. The RULES section references the header
        // inside backticks but never with this exact bullet
        // sequence.
        Assert.Contains("## Autonomous picker activity", prompt);
        Assert.Contains("picker is investigating target 0x8000ABCD", prompt);
        Assert.Contains("0x8000ABCD", prompt);
        Assert.Contains("Some Chest", prompt);
        Assert.Contains("in-range", prompt);
        Assert.Contains("schema-only picker", prompt);
        // RULES bullet teaches the LLM how to react to this block.
        Assert.Contains("AUTONOMOUS PICKER", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithoutPickerActivity_OmitsActivityBlock()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null);
        // The activity block header must not be rendered. The
        // RULES bullet that mentions "Autonomous picker activity"
        // inside backticks is ALWAYS present and is not the
        // section header we're checking for. The unique signal of
        // a rendered block is the "picker is investigating target"
        // line.
        Assert.DoesNotContain("picker is investigating target", prompt);
    }

    [Fact]
    public void BuildUserPrompt_LegacyOverloads_RemainCompatible()
    {
        // 3-arg overload (pre-Slice R) and 4-arg overload (Slice R)
        // must still build without picker activity wiring — Slice V
        // is additive. The activity section header should not be
        // rendered when no activity is supplied.
        var p3 = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null);
        var p4 = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack: null);
        Assert.DoesNotContain("picker is investigating target", p3);
        Assert.DoesNotContain("picker is investigating target", p4);
    }

    [Fact]
    public void EventStream_PickerActivityStartedAndCompleted_RoundtripWithKindNumbers()
    {
        // The wire-protocol stability contract: PickerActivityStarted
        // is 14 and PickerActivityCompleted is 15. Test sinks (the
        // training-log JSON) depend on these numeric kinds being
        // stable.
        Assert.Equal(14, (int)EventKind.PickerActivityStarted);
        Assert.Equal(15, (int)EventKind.PickerActivityCompleted);
    }

    // ---- Slice W.2 — exploration candidates surface (#87) ----

    [Fact]
    public void BuildUserPrompt_WithExplorationCandidates_RendersCandidateBlock()
    {
        var candidates = new List<ExplorationCandidate>
        {
            new() { Guid = 0x80001111u, Name = "Holtburg Door", Distance = 42.5f, CellId = 0x12340002u, Visited = false },
            new() { Guid = 0x80002222u, Name = "Entry Door",    Distance = 88.0f, CellId = 0x12340003u, Visited = true  },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: candidates);

        Assert.Contains("## Exploration candidates", prompt);
        Assert.Contains("0x80001111", prompt);
        Assert.Contains("Holtburg Door", prompt);
        Assert.Contains("dist=42.5u", prompt);
        Assert.Contains("0x80002222", prompt);
        Assert.Contains("Entry Door", prompt);
        Assert.Contains("VISITED", prompt);
        // RULES bullet teaches the LLM how to read it.
        Assert.Contains("EXPLORATION CANDIDATES", prompt);
    }

    [Fact]
    public void BuildUserPrompt_ExplorationCandidates_RenderWireDerivedKind()
    {
        // Kind is raw wire perception (mob/npc/object) so the LLM can
        // tell a creature candidate from inert scenery among off-screen
        // candidates. Mob/NPC render their token; everything else (and
        // the default) renders "object".
        var candidates = new List<ExplorationCandidate>
        {
            new() { Guid = 0x80001111u, Name = "Drudge",   Distance = 30f, CellId = 0x12340002u, Visited = false, Kind = EntityKind.Mob },
            new() { Guid = 0x80002222u, Name = "Merchant", Distance = 40f, CellId = 0x12340002u, Visited = false, Kind = EntityKind.NPC },
            new() { Guid = 0x80003333u, Name = "Apple",    Distance = 50f, CellId = 0x12340002u, Visited = false, Kind = EntityKind.Item },
            new() { Guid = 0x80004444u, Name = "Marker",   Distance = 60f, CellId = 0x12340002u, Visited = false /* Kind defaults to Unknown */ },
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            BuildWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: candidates);

        Assert.Contains("\"Drudge\" dist=30.0u cell=0x12340002 kind=mob", prompt);
        Assert.Contains("\"Merchant\" dist=40.0u cell=0x12340002 kind=npc", prompt);
        // Non-creature kinds (Item, and the Unknown default) collapse to "object".
        Assert.Contains("\"Apple\" dist=50.0u cell=0x12340002 kind=object", prompt);
        Assert.Contains("\"Marker\" dist=60.0u cell=0x12340002 kind=object", prompt);
        // The RULES bullet documents the new token vocabulary.
        Assert.Contains("kind=mob|npc|object", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithoutExplorationCandidates_OmitsCandidateBlock()
    {
        var p1 = LlmGoalPolicy.BuildUserPrompt(
            BuildWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: null);
        var p2 = LlmGoalPolicy.BuildUserPrompt(
            BuildWorld(), new EventStream(), currentGoal: null,
            stack: null, pickerActivity: null, explorationCandidates: new List<ExplorationCandidate>());

        // The RULES section references the header inside backticks
        // ("## Exploration candidates" appears there too), so we
        // can't check the bare header. The unique signal of a
        // rendered block is the parenthetical suffix on the
        // header AND the per-candidate "dist=" lines.
        Assert.DoesNotContain("(off-screen known objects", p1);
        Assert.DoesNotContain("(off-screen known objects", p2);
    }

    [Fact]
    public void BuildUserPrompt_LegacyOverloads_OmitCandidateBlock()
    {
        // 3/4/5-arg overloads should never render the new block —
        // it is opt-in via the 6-arg overload.
        var p3 = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null);
        var p4 = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack: null);
        var p5 = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack: null, pickerActivity: null);
        Assert.DoesNotContain("(off-screen known objects", p3);
        Assert.DoesNotContain("(off-screen known objects", p4);
        Assert.DoesNotContain("(off-screen known objects", p5);
    }
}
