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
    public void Applier_TryApply_RejectsRevisionMismatch_WhenBatchHasNonPushOp()
    {
        // A batch carrying a top-identity op (replace/pop/mark) stays strictly
        // revision-guarded: an intervening auto-pop could have changed the top,
        // so a stale echoed revision must reject the whole batch.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.ReplaceTop, Intent = SpecOf("sub"), Reason = "x" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops,
            echoedRevision: stack.Revision + 10, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.RejectedRevision, outcome.Result);
        Assert.Equal(1, stack.Depth);
        Assert.False(outcome.StaleRevisionTolerated);
    }

    [Fact]
    public void Applier_TryApply_FullStackWithTerminalRoot_ReapsThenPushSucceeds()
    {
        // Regression (council d8c4ca5): the LLM path goes through TryApply's
        // dry-run mirror, which must simulate IntentStack.ReapTerminalFrames —
        // otherwise a full stack with a buried Completed root permanently
        // RejectedOverflow's every push (incl. compiling a quest), even though a
        // finished frame is reclaimable. Direct IntentStack.TryPush already reaps;
        // this proves the applier mirror reaps in lockstep.
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        var alloc = new IntentIdAllocator();
        var stack = new IntentStack(maxDepth: 3);
        stack.TryPush(new Intent { Id = "i-001", Kind = "done-root", Status = IntentLifecycle.Completed,
            Completion = new AlwaysFalsePredicate(), Baseline = b });
        stack.TryPush(new Intent { Id = "i-002", Kind = "live-a", Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b });
        stack.TryPush(new Intent { Id = "i-003", Kind = "live-b", Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b }); // full at depth 3

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf(kind: "new-quest"), Reason = "compile quest" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);

        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(3, stack.Depth);
        Assert.Equal("new-quest", stack.Top!.Kind);
        Assert.DoesNotContain("done-root", stack.Frames.Select(f => f.Kind));
    }

    [Fact]
    public void Applier_TryApply_FullStackAllActive_StillRejectsOverflow()
    {
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        var alloc = new IntentIdAllocator();
        var stack = new IntentStack(maxDepth: 3);
        stack.TryPush(new Intent { Id = "i-001", Kind = "a", Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b });
        stack.TryPush(new Intent { Id = "i-002", Kind = "b", Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b });
        stack.TryPush(new Intent { Id = "i-003", Kind = "c", Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b });

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf(kind: "d"), Reason = "x" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);

        // Nothing terminal to reclaim -> the overflow guard still rejects the batch.
        Assert.Equal(BatchApplyResult.RejectedOverflow, outcome.Result);
        Assert.Equal(3, stack.Depth);
    }

    [Fact]
    public void Applier_TryApply_FullStackAllActive_EvictOn_AdmitsPushViaEviction()
    {
        // With overflow eviction enabled, the dry-run mirror and the real TryPush
        // both delegate to IntentStackReclaim, so an all-Active full stack admits a
        // new push by evicting the oldest non-root frame — and they agree (no
        // mirror/real divergence: the batch applies and the stack ends at maxDepth).
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        var alloc = new IntentIdAllocator();
        var stack = new IntentStack(maxDepth: 3, evictNonTerminalOnOverflow: true);
        stack.TryPush(new Intent { Id = "i-001", Kind = "root",  Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b });
        stack.TryPush(new Intent { Id = "i-002", Kind = "sub-1", Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b });
        stack.TryPush(new Intent { Id = "i-003", Kind = "sub-2", Status = IntentLifecycle.Active,
            Completion = new AlwaysFalsePredicate(), Baseline = b });

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf(kind: "new-top"), Reason = "x" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);

        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(3, stack.Depth);
        Assert.Equal("root",    stack.Root!.Kind);
        Assert.Equal("new-top", stack.Top!.Kind);
        Assert.DoesNotContain("sub-1", stack.Frames.Select(f => f.Kind)); // oldest non-root evicted
    }

    [Fact]
    public void Applier_TryApply_PushOnlyBatch_ToleratesStaleRevision_FromRootDeadlineBlock()
    {
        // Faithfully reproduce the ONLY stale-revision source that reaches the
        // applier in the live caller: a child auto-pop / root-completion both emit
        // a GoalCompleted event that LlmGoalPolicy discards before apply, so the
        // stale revision that survives is the no-event one — a root whose deadline
        // elapsed, marked Blocked IN PLACE (revision bumped, NOT popped). The LLM,
        // having rendered while the root was still Active, emits a PUSH with the
        // stale render-time revision. The push is applied above the Blocked root
        // (which resurfaces for REPLACE_TOP once the child completes).
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        stack.TryPush(NewIntent("root", "k-root", b, deadline: DateTime.UtcNow.AddMilliseconds(-1)));
        var renderRev = stack.Revision;          // what the LLM "saw" (root Active)

        // Root deadline elapsed during LLM latency: CheckTopForCompletion marks the
        // root Blocked in place, bumps the revision, and returns null (no pop, no
        // GoalCompleted event — so the caller would NOT discard the response).
        var popped = stack.CheckTopForCompletion(world, events, DateTime.UtcNow);
        Assert.Null(popped);
        Assert.Equal(IntentLifecycle.Blocked, stack.Top!.Status);
        Assert.NotEqual(renderRev, stack.Revision);

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("compiled-objective"), Reason = "compile" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops,
            echoedRevision: renderRev, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.True(outcome.StaleRevisionTolerated);
        Assert.Equal(2, stack.Depth);
        Assert.Equal("compiled-objective", stack.Top!.Kind);
        Assert.Equal(IntentLifecycle.Blocked, stack.Frames[0].Status); // root stays Blocked ancestor
    }

    [Fact]
    public void Applier_TryApply_PushOnlyBatch_StaleRevision_AutoAllocatesCollidingId()
    {
        // The LLM commonly reuses a literal example id ("i-001") that already
        // names a frame. Such a push is NOT rejected (rejecting silently drops
        // the turn's strategic decision and loops) — a fresh unique id is
        // allocated and the (genuinely different) intent is pushed. Holds on
        // the tolerated stale-revision path too.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("dup-id", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var dupSpec = SpecOf("sub");                  // different kind => a genuinely new intent
        dupSpec = dupSpec with { Id = "dup-id" };     // ...mislabelled with an id already on the stack
        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = dupSpec, Reason = "x" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops,
            echoedRevision: stack.Revision + 1, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.True(outcome.StaleRevisionTolerated);
        Assert.Equal(2, stack.Depth);
        Assert.Equal("sub", stack.Top!.Kind);
        Assert.NotEqual("dup-id", stack.Top!.Id);     // collision => fresh id
        Assert.Equal(0, outcome.SuppressedCount);      // different kind => not redundant
    }

    [Fact]
    public void Applier_TryApply_MixedBatch_StaleRevision_Rejected()
    {
        // A push followed by a top-identity op is NOT push-only, so a stale
        // revision rejects the whole batch (all-or-nothing).
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("sub"), Reason = "x" },
            new() { Op = IntentStackOpKind.MarkTopBlocked, Reason = "stall" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops,
            echoedRevision: stack.Revision + 5, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.RejectedRevision, outcome.Result);
        Assert.Equal(1, stack.Depth);
    }

    [Fact]
    public void Applier_TryApply_PushOnlyBatch_StaleRevision_StillEnforcesDepthOverflow()
    {
        // Tolerating a stale revision does NOT skip the dry-run: the push is still
        // re-validated against the LIVE stack, so an overflow is caught.
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
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops,
            echoedRevision: stack.Revision + 1, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.RejectedOverflow, outcome.Result);
        Assert.Equal(2, stack.Depth);
    }

    [Fact]
    public void Applier_TryApply_PushOnlyBatch_MatchingRevision_NotMarkedTolerated()
    {
        // The tolerance flag only sets on the stale path; a clean revision match
        // applies normally with StaleRevisionTolerated = false.
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
            echoedRevision: stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.False(outcome.StaleRevisionTolerated);
        Assert.Equal(2, stack.Depth);
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

    [Fact]
    public void Applier_TryApply_RedundantPush_SameKindAndTarget_SuppressedAsNoop()
    {
        // The LLM re-derives and re-pushes an intent already live (it has no
        // memory of its prior turn). A push whose (kind,target) matches an
        // Active frame is an idempotent no-op: not pushed, not rejected.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("i-001", "quest:recover", IntentBaseline.Capture(world, events, DateTime.UtcNow)));
        var preRev = stack.Revision;

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("quest:recover"), Reason = "re-derived" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, preRev, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.SuppressedCount);
        Assert.Equal(1, stack.Depth);            // not pushed
        Assert.Equal(preRev, stack.Revision);    // no mutation
    }

    [Fact]
    public void Applier_TryApply_StaleBuriedActivePastDeadline_StillSuppressed_NoDuplicate()
    {
        // A same-key re-push is suppressed even when the buried matching frame's own
        // deadline has elapsed: the overflow policy cannot be guaranteed to evict
        // THIS exact frame (it may be the root, or tier-1 terminal reclaim may free
        // room first), so allowing the push through would create a duplicate. The
        // stale frame is instead reclaimed by a later push of a DIFFERENT key.
        var now = new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc);
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, now);
        var alloc = new IntentIdAllocator();
        var stack = new IntentStack(maxDepth: 4, evictNonTerminalOnOverflow: true);
        stack.TryPush(NewIntent("i-001", "root", b), utcNow: now);
        stack.TryPush(NewIntent("i-002", "quest:recover", b, deadline: now.AddSeconds(-1)), utcNow: now); // buried, expired

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("quest:recover"), Reason = "re-derived" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, now);

        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.SuppressedCount);   // suppressed -> no duplicate
        Assert.Equal(2, stack.Depth);
        Assert.Equal(1, stack.Frames.Count(f => f.Kind == "quest:recover")); // exactly one
    }

    [Fact]
    public void Applier_TryApply_RedundantPush_MatchIsCaseAndWhitespaceInsensitive()
    {
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));
        var ops1 = new IntentStackOp[] { new() { Op = IntentStackOpKind.Push, Intent = SpecOf("Hunt", targetName: "Bob"), Reason = "a" } };
        IntentStackOpsApplier.TryApply(stack, alloc, ops1, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(2, stack.Depth);

        var ops2 = new IntentStackOp[] { new() { Op = IntentStackOpKind.Push, Intent = SpecOf("  hUNT ", targetName: " bob "), Reason = "b" } };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops2, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.SuppressedCount);
        Assert.Equal(2, stack.Depth);
    }

    [Fact]
    public void Applier_TryApply_RedundantPush_NotSuppressedAgainstBlockedFrame()
    {
        // Suppression fires only against an ACTIVE frame; a same-(kind,target)
        // push when the matching frame is Blocked is a legitimate retry.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        stack.TryPush(NewIntent("root", "k-root", b));
        stack.TryPush(NewIntent("q", "quest", b));
        stack.MarkTopBlocked("stalled");           // top "quest" now Blocked
        Assert.Equal(IntentLifecycle.Blocked, stack.Top!.Status);

        var ops = new IntentStackOp[] { new() { Op = IntentStackOpKind.Push, Intent = SpecOf("quest"), Reason = "retry" } };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(0, outcome.SuppressedCount);
        Assert.Equal(3, stack.Depth);
        Assert.Equal(IntentLifecycle.Active, stack.Top!.Status);
    }

    [Fact]
    public void Applier_TryApply_CollidingId_DifferentIntent_AutoAllocatesFreshId()
    {
        // A reused id that names a DIFFERENT intent (kind differs) is not
        // redundant: it is pushed with a freshly allocated unique id.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("i-001", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var spec = SpecOf("quest:fetch", targetName: "Apples") with { Id = "i-001" };
        var ops = new IntentStackOp[] { new() { Op = IntentStackOpKind.Push, Intent = spec, Reason = "compile" } };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(0, outcome.SuppressedCount);
        Assert.Equal(2, stack.Depth);
        Assert.Equal("quest:fetch", stack.Top!.Kind);
        Assert.NotEqual("i-001", stack.Top!.Id);
    }

    [Fact]
    public void Applier_TryApply_AutoAllocatedId_SkipsIdsAlreadyOnStack()
    {
        // The allocator shares the "i-NNN" namespace with LLM ids. If its next
        // id is already on the stack, ResolveUniqueId loops until unique.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();         // next allocate => "i-001"
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("i-001", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("new-objective"), Reason = "x" }, // blank id
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(2, stack.Depth);
        Assert.NotEqual("i-001", stack.Top!.Id);     // i-001 taken => allocator skipped past it
    }

    [Fact]
    public void Applier_TryApply_WithinBatch_SecondIdenticalPush_Suppressed()
    {
        // Two identical pushes in one batch: the first lands, the second is a
        // redundant no-op (the dry-run mirror tracks the just-added frame).
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        stack.TryPush(NewIntent("root", "k-root", IntentBaseline.Capture(world, events, DateTime.UtcNow)));

        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("hunt", targetName: "Drudge"), Reason = "a" },
            new() { Op = IntentStackOpKind.Push, Intent = SpecOf("hunt", targetName: "Drudge"), Reason = "b" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(1, outcome.SuppressedCount);
        Assert.Equal(2, stack.Depth);             // root + one hunt
    }

    [Fact]
    public void Applier_TryApply_ReplaceTop_CollidingIdWithAncestor_AutoAllocatesFreshId()
    {
        // replace_top ids are display labels too. A supplied id that collides
        // with an ANCESTOR frame's id must be reassigned, never duplicated.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        stack.TryPush(NewIntent("i-001", "k-root", b));      // root id = i-001
        stack.TryPush(NewIntent("i-002", "k-child", b));     // top  id = i-002

        // Replace the top but (mistakenly) reuse the root's id "i-001".
        var spec = SpecOf("k-replacement") with { Id = "i-001" };
        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.ReplaceTop, Intent = spec, Reason = "refine" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(2, stack.Depth);
        Assert.Equal("k-replacement", stack.Top!.Kind);
        Assert.NotEqual("i-001", stack.Top!.Id);             // would-be duplicate reassigned
        var ids = stack.Frames.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());     // no duplicate ids on the stack
    }

    [Fact]
    public void Applier_TryApply_ReplaceTop_ReusingOwnTopId_KeepsLabel()
    {
        // Refining the top while keeping its own id is a unique label (the
        // outgoing top is freed first), so the label is preserved — not churned.
        var stack = new IntentStack();
        var alloc = new IntentIdAllocator();
        var world = BuildWorld();
        var events = new EventStream();
        var b = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        stack.TryPush(NewIntent("i-001", "k-root", b));
        stack.TryPush(NewIntent("i-007", "k-child", b));     // top id = i-007

        var spec = SpecOf("k-child-refined") with { Id = "i-007" };
        var ops = new IntentStackOp[]
        {
            new() { Op = IntentStackOpKind.ReplaceTop, Intent = spec, Reason = "refine" },
        };
        var outcome = IntentStackOpsApplier.TryApply(stack, alloc, ops, stack.Revision, world, events, DateTime.UtcNow);
        Assert.Equal(BatchApplyResult.Ok, outcome.Result);
        Assert.Equal(2, stack.Depth);
        Assert.Equal("i-007", stack.Top!.Id);                // own id kept
        Assert.Equal("k-child-refined", stack.Top!.Kind);
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

    [Fact]
    public void RenderStackForPrompt_Ancestor_IncludesRationale()
    {
        // A paused ancestor's rationale (where the LLM records a follow-up step
        // it plans to run once the active child completes) must survive into the
        // prompt; Intent.ToString() omits Rationale, so the renderer adds it.
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b));
        stack.TryPush(NewIntent("i-quest", "do-quest", b, completion: new LevelAtLeastPredicate(5)));

        var s = IntentStackOpsApplier.RenderStackForPrompt(stack);
        // "test:play-game" is the ROOT (ancestor) rationale; the TOP renders its
        // own "test:do-quest", so matching the ancestor's proves ancestor
        // rationale is surfaced specifically.
        Assert.Contains("ancestor[0]", s);
        Assert.Contains("rationale=\"test:play-game\"", s);
    }

    [Fact]
    public void RenderStackForPrompt_HistoryFrame_IncludesRationale()
    {
        // A popped frame's rationale is where the LLM recorded any follow-up it
        // intended once that frame finished. It must survive into the recent-
        // history tail so the deliberation right after the pop still sees it
        // (e.g. when a completion-predicate auto-pops a frame). ToString() drops
        // Rationale, so the renderer adds it back for history frames too.
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b));
        stack.TryPush(NewIntent("i-killcount", "kill-then-return", b));
        Assert.Equal(StackOpResult.Ok, stack.PopTop(IntentLifecycle.Completed, "predicate satisfied"));

        var s = IntentStackOpsApplier.RenderStackForPrompt(stack);
        // "test:kill-then-return" can only appear via the history frame's
        // rationale (the live TOP is now the root, "test:play-game").
        Assert.Contains("recent history", s);
        Assert.Contains("rationale=\"test:kill-then-return\"", s);
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

    // ---- Quest-dialog compiler rule + directive-check capsule (cp-2346) ----

    private static StreamEvent Dialog(string text, string from = "Someone") => new()
    {
        Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = from, Text = text,
    };

    [Fact]
    public void BuildUserPrompt_QuestDialogCompiler_RendersWhenDialogPresentWithStack()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b));
        var es = new EventStream();
        es.Append(Dialog("a task was assigned to you"));

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), es, currentGoal: null, stack);
        Assert.Contains("QUEST-DIALOG COMPILER", prompt);
        Assert.Contains("## Recent directive check", prompt);
        Assert.Contains("do not invent a task, target, count, NPC, or location", prompt);
        // cp-2615: the rule must tell the LLM to preserve the task-giver +
        // turn-in across a kill-count auto-pop (push a return-to-giver intent
        // under the kill intent, or record the giver in the rationale that the
        // stack now surfaces from recent history, cp-2614).
        Assert.Contains("return-to-giver", prompt);
    }

    [Fact]
    public void BuildUserPrompt_QuestDialogCompiler_OmittedWhenNoDialog()
    {
        var stack = new IntentStack();
        var b = IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);
        stack.TryPush(NewIntent("i-root", "play-game", b));

        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), new EventStream(), currentGoal: null, stack);
        Assert.DoesNotContain("QUEST-DIALOG COMPILER", prompt);
        Assert.DoesNotContain("## Recent directive check", prompt);
    }

    [Fact]
    public void BuildUserPrompt_QuestDialogCompiler_OmittedWhenNoStack()
    {
        // Both the rule and the capsule are gated on a stack being enabled.
        var es = new EventStream();
        es.Append(Dialog("a task was assigned to you"));
        var prompt = LlmGoalPolicy.BuildUserPrompt(BuildWorld(), es, currentGoal: null);
        Assert.DoesNotContain("QUEST-DIALOG COMPILER", prompt);
        Assert.DoesNotContain("## Recent directive check", prompt);
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
