// SPDX-License-Identifier: AGPL-3.0-or-later
// Slice R — IntentStack + predicate DSL unit tests.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class IntentStackTests
{
    private const uint SelfGuid = 0x50000005;
    private const uint NpcGuid  = 0x90000010;
    private const uint MobGuid  = 0x90000020;
    private const uint ItemGuid = 0x80000030;

    // ---- IntentStack basics ----

    [Fact]
    public void EmptyStack_TopIsNull_DepthZero()
    {
        var s = new IntentStack();
        Assert.Null(s.Top);
        Assert.Null(s.Root);
        Assert.Equal(0, s.Depth);
        Assert.True(s.IsEmpty);
        Assert.Equal(0, s.Revision);
    }

    [Fact]
    public void TryPush_FillsToMaxDepth_ThenRefusesOverflow()
    {
        var s = new IntentStack(maxDepth: 3);
        var baseline = BuildBaseline();

        Assert.Equal(StackOpResult.Ok, s.TryPush(NewIntent("i-001", "root",       baseline)));
        Assert.Equal(StackOpResult.Ok, s.TryPush(NewIntent("i-002", "sub-1",      baseline)));
        Assert.Equal(StackOpResult.Ok, s.TryPush(NewIntent("i-003", "sub-2",      baseline)));
        Assert.Equal(StackOpResult.RefusedOverflow, s.TryPush(NewIntent("i-004", "sub-3", baseline)));

        Assert.Equal(3, s.Depth);
        Assert.Equal("sub-2", s.Top!.Kind);
        Assert.Equal("root",  s.Root!.Kind);
    }

    [Fact]
    public void PopTop_NeverPopsRoot()
    {
        var s = new IntentStack();
        var b = BuildBaseline();
        s.TryPush(NewIntent("i-001", "root", b));

        var r = s.PopTop(IntentLifecycle.Completed, "test");
        Assert.Equal(StackOpResult.RefusedRootPop, r);
        Assert.Equal(1, s.Depth);
        Assert.NotNull(s.Top);
    }

    [Fact]
    public void PopTop_ArchivesIntoHistoryWithFinalStatus()
    {
        var s = new IntentStack();
        var b = BuildBaseline();
        s.TryPush(NewIntent("i-001", "root", b));
        s.TryPush(NewIntent("i-002", "sub",  b));

        var preRev = s.Revision;
        var r = s.PopTop(IntentLifecycle.Completed, "predicate satisfied");

        Assert.Equal(StackOpResult.Ok, r);
        Assert.Equal(1, s.Depth);
        Assert.Equal("root", s.Top!.Kind);
        Assert.NotEqual(preRev, s.Revision);

        Assert.Single(s.History);
        Assert.Equal("sub", s.History[0].Kind);
        Assert.Equal(IntentLifecycle.Completed, s.History[0].Status);
        Assert.Equal("predicate satisfied", s.History[0].LastFailure);
    }

    [Fact]
    public void ReplaceTop_SwapsTop_ArchivesOldAsBlocked()
    {
        var s = new IntentStack();
        var b = BuildBaseline();
        s.TryPush(NewIntent("i-001", "root", b));
        s.TryPush(NewIntent("i-002", "buy-comps", b));

        var r = s.ReplaceTop(NewIntent("i-003", "buy-armor", b), "armor priced cheaper");
        Assert.Equal(StackOpResult.Ok, r);
        Assert.Equal("buy-armor", s.Top!.Kind);
        Assert.Equal(2, s.Depth);
        Assert.Single(s.History);
        Assert.Equal("buy-comps", s.History[0].Kind);
        Assert.Equal(IntentLifecycle.Blocked, s.History[0].Status);
    }

    [Fact]
    public void MarkTopBlocked_UpdatesTop_NoPop()
    {
        var s = new IntentStack();
        var b = BuildBaseline();
        s.TryPush(NewIntent("i-001", "root", b));
        s.TryPush(NewIntent("i-002", "give-letter", b));

        var r = s.MarkTopBlocked("3x TradeAiDoesntWant");
        Assert.Equal(StackOpResult.Ok, r);
        Assert.Equal(2, s.Depth);
        Assert.Equal(IntentLifecycle.Blocked, s.Top!.Status);
        Assert.Equal("3x TradeAiDoesntWant", s.Top.LastFailure);
    }

    [Fact]
    public void Revision_IncrementsOnEveryMutation()
    {
        var s = new IntentStack();
        var b = BuildBaseline();
        Assert.Equal(0, s.Revision);
        s.TryPush(NewIntent("i-001", "root", b));
        Assert.Equal(1, s.Revision);
        s.TryPush(NewIntent("i-002", "sub",  b));
        Assert.Equal(2, s.Revision);
        s.MarkTopBlocked("x");
        Assert.Equal(3, s.Revision);
        s.PopTop(IntentLifecycle.Completed);
        Assert.Equal(4, s.Revision);
    }

    [Fact]
    public void StackOps_RefusedRevision_DropsMutation()
    {
        var s = new IntentStack();
        var b = BuildBaseline();
        s.TryPush(NewIntent("i-001", "root", b));

        var rev = s.Revision;
        // Stale revision -> reject.
        Assert.Equal(StackOpResult.RefusedRevision,
                     s.TryPush(NewIntent("i-002", "sub", b), expectedRevision: rev - 1));
        Assert.Equal(1, s.Depth);

        // Correct revision -> accept.
        Assert.Equal(StackOpResult.Ok,
                     s.TryPush(NewIntent("i-002", "sub", b), expectedRevision: rev));
        Assert.Equal(2, s.Depth);
    }

    // ---- Auto-completion on tick check ----

    [Fact]
    public void CheckTopForCompletion_PredicateSatisfied_PopsAndArchives()
    {
        var s = new IntentStack();
        var world = BuildWorld(level: 1);
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);

        s.TryPush(NewIntent("i-001", "root",   baseline, new AlwaysFalsePredicate()));
        s.TryPush(NewIntent("i-002", "level5", baseline, new LevelAtLeastPredicate(5)));

        // Below threshold -> not yet complete.
        Assert.Null(s.CheckTopForCompletion(world, events, DateTime.UtcNow));
        Assert.Equal(2, s.Depth);

        // Reaches threshold.
        var leveled = world with { Self = world.Self with { Level = 5 } };
        var popped = s.CheckTopForCompletion(leveled, events, DateTime.UtcNow);
        Assert.NotNull(popped);
        Assert.Equal("level5", popped!.Kind);
        Assert.Equal(IntentLifecycle.Completed, popped.Status);
        Assert.Equal(1, s.Depth);
    }

    [Fact]
    public void CheckTopForCompletion_ChildPops_ParentResurfacesAsActiveTop()
    {
        // Criterion-2 turn-in shape: a parent "return-to-giver" intent with a
        // child "kill-count" on top of it. When the child's completion predicate
        // fires and it auto-pops, the parent must resurface as the ACTIVE top so
        // the bot drives the turn-in — it must NOT be left buried, Blocked, or
        // Completed. This locks the FILO transition the QUEST-DIALOG COMPILER
        // turn-in guidance (cp-2615) and recent-history rationale (cp-2614) rely
        // on. A satisfied LevelAtLeast predicate stands in for any auto-popping
        // completion (e.g. kill_count_*); the resurface behavior is identical.
        var s = new IntentStack();
        var world = BuildWorld(level: 1);
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);

        s.TryPush(NewIntent("i-001", "return-to-giver", baseline, new AlwaysFalsePredicate()));
        s.TryPush(NewIntent("i-002", "kill-count",      baseline, new LevelAtLeastPredicate(5)));

        var leveled = world with { Self = world.Self with { Level = 5 } };
        var popped = s.CheckTopForCompletion(leveled, events, DateTime.UtcNow);

        // The child popped...
        Assert.NotNull(popped);
        Assert.Equal("kill-count", popped!.Kind);
        // ...and the parent is now the top AND Active (ready to drive turn-in).
        Assert.Equal(1, s.Depth);
        Assert.Equal("return-to-giver", s.Top!.Kind);
        Assert.Equal(IntentLifecycle.Active, s.Top!.Status);
    }

    [Fact]
    public void CheckTopForCompletion_DeadlineElapsed_PopsAsExpired()
    {
        var s = new IntentStack();
        var world = BuildWorld(level: 1);
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        s.TryPush(NewIntent("i-001", "root", baseline));
        s.TryPush(NewIntent(
            "i-002", "talk-jonathan", baseline,
            new EventAfterPushPredicate(EventKind.NpcDialog, NameContains: "Jonathan"),
            deadline: DateTime.UtcNow.AddSeconds(30)));

        var future = DateTime.UtcNow.AddMinutes(2);
        var popped = s.CheckTopForCompletion(world, events, future);
        Assert.NotNull(popped);
        Assert.Equal(IntentLifecycle.Expired, popped!.Status);
        Assert.Equal(1, s.Depth);
    }

    [Fact]
    public void CheckTopForCompletion_RootExpires_StaysAsBlockedRoot_NotPopped()
    {
        var s = new IntentStack();
        var world = BuildWorld(level: 1);
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        s.TryPush(NewIntent(
            "i-001", "session-root", baseline,
            new AlwaysFalsePredicate(),
            deadline: DateTime.UtcNow.AddSeconds(5)));

        var future = DateTime.UtcNow.AddMinutes(1);
        var popped = s.CheckTopForCompletion(world, events, future);
        Assert.Null(popped);
        Assert.Equal(1, s.Depth);
        Assert.Equal(IntentLifecycle.Blocked, s.Top!.Status);
    }

    [Fact]
    public void CheckTopForCompletion_RootDeadlineElapsed_BumpsRevisionOnce_NotPerTick()
    {
        // Regression (stack-revision-runaway): once the ROOT intent's deadline
        // elapses, CheckTopForCompletion marks it Blocked. Blocked is NOT a
        // terminal status (the root stays at depth 1), so re-entry must NOT
        // re-mark + re-bump the revision every tick — otherwise the revision
        // churns ~4/sec and the echoed expectedRevision is perpetually stale, so
        // EVERY stack op is RefusedRevision and the stack can no longer be
        // mutated. The bump must happen exactly ONCE, on the Blocked transition.
        var s = new IntentStack();
        var world = BuildWorld(level: 1);
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);
        s.TryPush(NewIntent(
            "i-001", "session-root", baseline,
            new AlwaysFalsePredicate(),
            deadline: DateTime.UtcNow.AddSeconds(5)));

        var revBeforeDeadline = s.Revision;
        var future = DateTime.UtcNow.AddMinutes(1);

        // First post-deadline check: transition to Blocked, bump ONCE.
        s.CheckTopForCompletion(world, events, future);
        var revAfterFirst = s.Revision;
        Assert.Equal(revBeforeDeadline + 1, revAfterFirst);
        Assert.Equal(IntentLifecycle.Blocked, s.Top!.Status);

        // Many further post-deadline checks (simulating ticks during a slow LLM
        // call): the root is ALREADY Blocked, so the revision must not move.
        for (var i = 0; i < 20; i++)
            s.CheckTopForCompletion(world, events, future);

        Assert.Equal(revAfterFirst, s.Revision); // no per-tick churn
        Assert.Equal(1, s.Depth);
        Assert.Equal(IntentLifecycle.Blocked, s.Top!.Status);
    }

    // ---- Predicate evaluation ----

    [Fact]
    public void EventAfterPushPredicate_OnlyMatchesAppendsAfterBaseline()
    {
        var world = BuildWorld();
        var events = new EventStream();
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Jonathan", Text = "old" });
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);

        // Pre-push event must NOT satisfy.
        var ctx0 = new IntentEvalContext(world, events, baseline, DateTime.UtcNow);
        Assert.False(new EventAfterPushPredicate(EventKind.NpcDialog, NameContains: "Jonathan").IsSatisfied(ctx0));

        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.NpcDialog, Name = "Jonathan", Text = "new" });
        var ctx1 = new IntentEvalContext(world, events, baseline, DateTime.UtcNow);
        Assert.True(new EventAfterPushPredicate(EventKind.NpcDialog, NameContains: "Jonathan").IsSatisfied(ctx1));

        // Mismatched name filter must NOT satisfy.
        Assert.False(new EventAfterPushPredicate(EventKind.NpcDialog, NameContains: "Worcer").IsSatisfied(ctx1));
    }

    [Fact]
    public void KillCountSincePushPredicate_CountsOnlyAttackGoalCompletedAfterPush()
    {
        var world = BuildWorld();
        var events = new EventStream();
        // Pre-existing kill should not count.
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted,
            GoalId = Guid.NewGuid(), Text = "Attack{name=\"Sparring Golem\"}"
        });
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);

        // Three new kills (one of which is Talk, not Attack).
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "Attack{name=\"Sparring Golem\"}" });
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "Talk{name=\"Jonathan\"}" });
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.GoalCompleted, Text = "Attack{name=\"Sparring Golem\"}" });

        var ctx = new IntentEvalContext(world, events, baseline, DateTime.UtcNow);
        Assert.True(new KillCountSincePushAtLeastPredicate(2, "Sparring Golem").IsSatisfied(ctx));
        Assert.False(new KillCountSincePushAtLeastPredicate(3, "Sparring Golem").IsSatisfied(ctx));
        Assert.False(new KillCountSincePushAtLeastPredicate(2, "Drudge").IsSatisfied(ctx));
    }

    private static CombatHistoryEntry Ch(string name, uint wcid, int kills) =>
        new(name, wcid, Kills: kills, Deaths: 0, NearDeaths: 0, Fights: kills, LastOutcome: "kill");

    [Fact]
    public void KillCountSincePush_NameFiltered_UsesPerKindCombatHistoryDelta()
    {
        // cp-2384: a name-filtered kill_count_since_push must count PER-KIND
        // kills (combat-feel history) minus the per-kind baseline at push — NOT
        // the kind-agnostic lifetime total (which would falsely complete).
        var atPush = BuildWorld() with { CombatHistoryFull = new[] { Ch("Drudge Skulker", 19257u, 2) } };
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(atPush, events, DateTime.UtcNow);

        // 5 Drudge Skulker total (3 since push) + 8 Rabbit (must NOT count).
        var now = BuildWorld() with
        {
            CombatHistoryFull = new[] { Ch("Drudge Skulker", 19257u, 5), Ch("Black Rabbit", 2566u, 8) },
        };
        var ctx = new IntentEvalContext(now, events, baseline, DateTime.UtcNow) { Stats = new BotStatistics() };

        Assert.True(new KillCountSincePushAtLeastPredicate(3, "Drudge").IsSatisfied(ctx));
        Assert.False(new KillCountSincePushAtLeastPredicate(4, "Drudge").IsSatisfied(ctx));
    }

    [Fact]
    public void KillCountSincePush_NameFiltered_OtherKindKillsDoNotSatisfy()
    {
        // The exact bug: "kill 5 Drudges" must NOT complete after killing 10
        // Rabbits, even though the lifetime total is well over 5.
        var atPush = BuildWorld();
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(atPush, events, DateTime.UtcNow);
        var now = BuildWorld() with { CombatHistoryFull = new[] { Ch("Black Rabbit", 2566u, 10) } };
        var stats = new BotStatistics();
        var ctx = new IntentEvalContext(now, events, baseline, DateTime.UtcNow) { Stats = stats };

        Assert.False(new KillCountSincePushAtLeastPredicate(5, "Drudge").IsSatisfied(ctx));
    }

    [Fact]
    public void KillCountSincePush_NameFiltered_SubtractsPrePushKills_NoOvercount()
    {
        // The over-count guard (why CombatHistoryFull is used, not the capped
        // snapshot): a kind with many PRE-PUSH kills must subtract its true
        // pre-push count, so only kills SINCE push count — never the cumulative.
        var atPush = BuildWorld() with { CombatHistoryFull = new[] { Ch("Drudge Skulker", 19257u, 8) } };
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(atPush, events, DateTime.UtcNow);
        var now = BuildWorld() with { CombatHistoryFull = new[] { Ch("Drudge Skulker", 19257u, 10) } };
        var ctx = new IntentEvalContext(now, events, baseline, DateTime.UtcNow) { Stats = new BotStatistics() };

        Assert.True(new KillCountSincePushAtLeastPredicate(2, "Drudge").IsSatisfied(ctx));
        Assert.False(new KillCountSincePushAtLeastPredicate(3, "Drudge").IsSatisfied(ctx));
    }

    [Fact]
    public void KillCountSincePush_NameFiltered_DuplicateDisplayName_SubtractsBaselineOnce()
    {
        // Two rows share the display name "Drudge" (different wcids — the ledger
        // keys by wcid). The per-name baseline must be subtracted ONCE per name,
        // not once per matching row (which would under-count the delta).
        var atPush = BuildWorld() with
        {
            CombatHistoryFull = new[] { Ch("Drudge", 100u, 2), Ch("Drudge", 200u, 1) }, // baseline name total = 3
        };
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(atPush, events, DateTime.UtcNow);
        var now = BuildWorld() with
        {
            CombatHistoryFull = new[] { Ch("Drudge", 100u, 3), Ch("Drudge", 200u, 4) }, // current name total = 7
        };
        var ctx = new IntentEvalContext(now, events, baseline, DateTime.UtcNow) { Stats = new BotStatistics() };
        // Delta = 7 - 3 = 4 (baseline subtracted once), NOT (3-3)+(4-3)=1.
        Assert.True(new KillCountSincePushAtLeastPredicate(4, "Drudge").IsSatisfied(ctx));
        Assert.False(new KillCountSincePushAtLeastPredicate(5, "Drudge").IsSatisfied(ctx));
    }

    [Fact]
    public void KillCountSincePush_NameFiltered_SubstringSpansMultipleKinds()
    {
        var atPush = BuildWorld();
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(atPush, events, DateTime.UtcNow);
        // "Drudge" substring matches Skulker(3) + Slinker(2) = 5.
        var now = BuildWorld() with
        {
            CombatHistoryFull = new[] { Ch("Drudge Skulker", 19257u, 3), Ch("Drudge Slinker", 19258u, 2) },
        };
        var ctx = new IntentEvalContext(now, events, baseline, DateTime.UtcNow) { Stats = new BotStatistics() };

        Assert.True(new KillCountSincePushAtLeastPredicate(5, "Drudge").IsSatisfied(ctx));
        Assert.False(new KillCountSincePushAtLeastPredicate(6, "Drudge").IsSatisfied(ctx));
    }

    [Fact]
    public void InventoryHasNamePredicate_CaseInsensitiveSubstring()
    {
        var world = BuildWorld(inventory: new[]
        {
            new InventoryItemProjection { Guid = ItemGuid, Name = "Calling Stone", Wcid = 29336u },
        });
        var ctx = new IntentEvalContext(world, new EventStream(), BuildBaseline(), DateTime.UtcNow);
        Assert.True(new InventoryHasNamePredicate("calling").IsSatisfied(ctx));
        Assert.True(new InventoryHasNamePredicate("stone").IsSatisfied(ctx));
        Assert.False(new InventoryHasNamePredicate("scroll").IsSatisfied(ctx));
    }

    [Fact]
    public void LevelAtLeastPredicate_TriggersAtThreshold()
    {
        var ctx5 = new IntentEvalContext(BuildWorld(level: 5), new EventStream(), BuildBaseline(), DateTime.UtcNow);
        var ctx9 = new IntentEvalContext(BuildWorld(level: 9), new EventStream(), BuildBaseline(), DateTime.UtcNow);
        var ctx10 = new IntentEvalContext(BuildWorld(level: 10), new EventStream(), BuildBaseline(), DateTime.UtcNow);

        var p = new LevelAtLeastPredicate(10);
        Assert.False(p.IsSatisfied(ctx5));
        Assert.False(p.IsSatisfied(ctx9));
        Assert.True(p.IsSatisfied(ctx10));
    }

    [Fact]
    public void LandblockChangedFromPushPredicate_TriggersOnCrossing()
    {
        var academy = BuildWorld(landblock: 0x8602u);
        var events = new EventStream();
        var baseline = IntentBaseline.Capture(academy, events, DateTime.UtcNow);

        var stillThere = new IntentEvalContext(academy, events, baseline, DateTime.UtcNow);
        Assert.False(new LandblockChangedFromPushPredicate().IsSatisfied(stillThere));

        var holtburg = BuildWorld(landblock: 0xA9B4u);
        var crossed = new IntentEvalContext(holtburg, events, baseline, DateTime.UtcNow);
        Assert.True(new LandblockChangedFromPushPredicate().IsSatisfied(crossed));
    }

    [Fact]
    public void AllOf_RequiresAllChildren_AnyOf_RequiresOne()
    {
        var world = BuildWorld(level: 6);
        var ctx = new IntentEvalContext(world, new EventStream(), BuildBaseline(), DateTime.UtcNow);
        var hi = new LevelAtLeastPredicate(5);
        var lo = new LevelAtLeastPredicate(20);

        Assert.True( new AllOfPredicate(new IntentPredicate[] { hi }).IsSatisfied(ctx));
        Assert.False(new AllOfPredicate(new IntentPredicate[] { hi, lo }).IsSatisfied(ctx));
        Assert.True( new AnyOfPredicate(new IntentPredicate[] { hi, lo }).IsSatisfied(ctx));
        Assert.False(new AnyOfPredicate(new IntentPredicate[] { lo }).IsSatisfied(ctx));
        // Empty children: both false.
        Assert.False(new AllOfPredicate(Array.Empty<IntentPredicate>()).IsSatisfied(ctx));
        Assert.False(new AnyOfPredicate(Array.Empty<IntentPredicate>()).IsSatisfied(ctx));
    }

    [Fact]
    public void WithinDistancePredicate_RequiresVisibleTargetWithinRange()
    {
        var world = BuildWorld(visible: new[]
        {
            new VisibleObjectProjection { Guid = NpcGuid, Name = "Jonathan", Distance = 3.2f },
        });
        var ctx = new IntentEvalContext(world, new EventStream(), BuildBaseline(), DateTime.UtcNow);
        Assert.True( new WithinDistancePredicate(NpcGuid, 5f).IsSatisfied(ctx));
        Assert.False(new WithinDistancePredicate(NpcGuid, 2f).IsSatisfied(ctx));
        Assert.False(new WithinDistancePredicate(0x99999999u, 100f).IsSatisfied(ctx));
    }

    [Fact]
    public void NotVisibleSincePushPredicate_RequiresWasVisibleAtPushAndAbsentLongEnough()
    {
        var world = BuildWorld(visible: new[]
        {
            new VisibleObjectProjection { Guid = NpcGuid, Name = "Jonathan", Distance = 3f },
        });
        var events = new EventStream();
        var pushTime = DateTime.UtcNow;
        var baseline = IntentBaseline.Capture(world, events, pushTime);

        // Still visible right after push -> false.
        var stillThere = new IntentEvalContext(world, events, baseline, pushTime.AddSeconds(1));
        Assert.False(new NotVisibleSincePushForSecondsPredicate(NpcGuid, 30).IsSatisfied(stillThere));

        // Gone but not long enough -> false.
        var goneShort = new IntentEvalContext(BuildWorld(), events, baseline, pushTime.AddSeconds(5));
        Assert.False(new NotVisibleSincePushForSecondsPredicate(NpcGuid, 30).IsSatisfied(goneShort));

        // Gone and long enough -> true.
        var goneLong = new IntentEvalContext(BuildWorld(), events, baseline, pushTime.AddSeconds(45));
        Assert.True(new NotVisibleSincePushForSecondsPredicate(NpcGuid, 30).IsSatisfied(goneLong));

        // Wasn't visible at push -> always false.
        var emptyWorld = BuildWorld();
        var emptyBaseline = IntentBaseline.Capture(emptyWorld, events, pushTime);
        var ctxNeverThere = new IntentEvalContext(BuildWorld(), events, emptyBaseline, pushTime.AddSeconds(60));
        Assert.False(new NotVisibleSincePushForSecondsPredicate(NpcGuid, 30).IsSatisfied(ctxNeverThere));
    }

    [Fact]
    public void VisibleTagPredicate_MapsTagsToProjectionFlags()
    {
        var world = BuildWorld(visible: new[]
        {
            new VisibleObjectProjection { Guid = 0xAAAAAAAAu, Name = "Glowing Lifestone", IsLifestone = true },
            new VisibleObjectProjection { Guid = 0xBBBBBBBBu, Name = "Bob the Vendor",     IsVendor = true },
            new VisibleObjectProjection { Guid = 0xCCCCCCCCu, Name = "Corpse of Foo",      IsCorpse = true },
        });
        var ctx = new IntentEvalContext(world, new EventStream(), BuildBaseline(), DateTime.UtcNow);

        Assert.True( new VisibleTagPredicate("lifestone").IsSatisfied(ctx));
        Assert.True( new VisibleTagPredicate("vendor").IsSatisfied(ctx));
        Assert.True( new VisibleTagPredicate("corpse").IsSatisfied(ctx));
        Assert.False(new VisibleTagPredicate("monster").IsSatisfied(ctx));
        Assert.False(new VisibleTagPredicate("portal").IsSatisfied(ctx));
        Assert.False(new VisibleTagPredicate("unknown").IsSatisfied(ctx));
    }

    [Fact]
    public void InventoryAddedSincePushPredicate_CountsOnlyAddedAfterBaseline()
    {
        var world = BuildWorld();
        var events = new EventStream();
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemAdded, Name = "Pyreal", Wcid = 273u });
        var baseline = IntentBaseline.Capture(world, events, DateTime.UtcNow);

        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemAdded, Name = "Pyreal", Wcid = 273u });
        events.Append(new StreamEvent { Sequence = -1, Utc = DateTimeOffset.UtcNow, Kind = EventKind.InventoryItemAdded, Name = "Pyreal", Wcid = 273u });

        var ctx = new IntentEvalContext(world, events, baseline, DateTime.UtcNow);
        Assert.True( new InventoryAddedSincePushAtLeastPredicate(2, NameContains: "Pyreal").IsSatisfied(ctx));
        Assert.False(new InventoryAddedSincePushAtLeastPredicate(3, NameContains: "Pyreal").IsSatisfied(ctx));
        // wcid match.
        Assert.True( new InventoryAddedSincePushAtLeastPredicate(2, Wcid: 273u).IsSatisfied(ctx));
        // wrong wcid.
        Assert.False(new InventoryAddedSincePushAtLeastPredicate(1, Wcid: 999u).IsSatisfied(ctx));
    }

    [Fact]
    public void NotPredicate_NegatesChild()
    {
        var ctx = new IntentEvalContext(BuildWorld(level: 1), new EventStream(), BuildBaseline(), DateTime.UtcNow);
        Assert.True( new NotPredicate(new LevelAtLeastPredicate(10)).IsSatisfied(ctx));
        Assert.False(new NotPredicate(new AlwaysFalsePredicate()).IsSatisfied(ctx) is false);
    }

    [Fact]
    public void ElapsedSecondsPredicate_UsesBaselinePushTime()
    {
        var world = BuildWorld();
        var events = new EventStream();
        var pushTime = DateTime.UtcNow;
        var baseline = IntentBaseline.Capture(world, events, pushTime);

        Assert.False(new ElapsedSecondsAtLeastPredicate(60).IsSatisfied(
            new IntentEvalContext(world, events, baseline, pushTime.AddSeconds(30))));
        Assert.True( new ElapsedSecondsAtLeastPredicate(60).IsSatisfied(
            new IntentEvalContext(world, events, baseline, pushTime.AddSeconds(61))));
    }

    // ---- JSON round-trip ----

    [Fact]
    public void IntentPredicate_SerializesAndRoundTripsAcrossAllSubtypes()
    {
        IntentPredicate root = new AllOfPredicate(new IntentPredicate[]
        {
            new LevelAtLeastPredicate(10),
            new KillCountSincePushAtLeastPredicate(5, "Golem"),
            new AnyOfPredicate(new IntentPredicate[]
            {
                new InventoryHasNamePredicate("Token"),
                new LandblockEqualsPredicate(0x8602u),
            }),
            new NotPredicate(new HealthFractionAtMostPredicate(0.3f)),
            new WithinDistancePredicate(0xDEADBEEFu, 5f),
            new ElapsedSecondsAtLeastPredicate(30),
            new VisibleTagPredicate("vendor"),
            new NoMonstersVisiblePredicate(),
            new AlwaysFalsePredicate(),
        });

        var json = JsonSerializer.Serialize(root);
        var rt   = JsonSerializer.Deserialize<IntentPredicate>(json);
        Assert.NotNull(rt);
        Assert.Equal(root.Summary(), rt!.Summary());
    }

    // ---- Helpers ----

    private static Intent NewIntent(
        string id,
        string kind,
        IntentBaseline baseline,
        IntentPredicate? completion = null,
        DateTime? deadline = null) =>
        new()
        {
            Id = id,
            Kind = kind,
            Rationale = $"test:{kind}",
            Completion = completion ?? new AlwaysFalsePredicate(),
            Baseline = baseline,
            DeadlineUtc = deadline,
        };

    private static IntentBaseline BuildBaseline() =>
        IntentBaseline.Capture(BuildWorld(), new EventStream(), DateTime.UtcNow);

    private static WorldStateProjection BuildWorld(
        int? level = 1,
        uint? landblock = 0x8602u,
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
            },
            Visible = visible ?? Array.Empty<VisibleObjectProjection>(),
            Inventory = inventory ?? Array.Empty<InventoryItemProjection>(),
        };
}
