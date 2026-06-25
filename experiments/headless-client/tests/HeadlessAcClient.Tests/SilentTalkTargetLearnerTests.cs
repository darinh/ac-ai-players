// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for SilentTalkTargetLearner — the runtime-learned set of creature
// WCIDs that never answer a Talk with dialog.

using System;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SilentTalkTargetLearnerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const double Grace = 12.0;

    private static SilentTalkTargetLearner New(int threshold = 2)
        => new(graceWindowSeconds: Grace, silentConfirmThreshold: threshold);

    [Fact]
    public void Fresh_NothingSilent()
    {
        var l = New();
        Assert.False(l.IsSilent(22257u));
        Assert.Equal(0, l.SilentWcidCount);
    }

    [Fact]
    public void IsSilent_NullWcid_False()
        => Assert.False(New().IsSilent(null));

    [Fact]
    public void SingleSilentInstance_BelowThreshold_NotSilent()
    {
        var l = New(threshold: 2);
        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.False(l.IsSilent(22257u));
    }

    [Fact]
    public void TwoDistinctSilentInstances_ConcludesSilent()
    {
        var l = New(threshold: 2);
        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        l.RecordTalkDispatch(0xA002u, 22257u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.True(l.IsSilent(22257u));
        Assert.Equal(1, l.SilentWcidCount);
        Assert.Contains(22257u, l.SilentWcids);
    }

    [Fact]
    public void WithinGraceWindow_NotYetMatured_NotSilent()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        // Only half the grace window has elapsed.
        l.Evaluate(T0.AddSeconds(Grace / 2));
        Assert.False(l.IsSilent(22257u));
        // Now mature it.
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.True(l.IsSilent(22257u));
    }

    [Fact]
    public void Threshold1_OneSilentInstance_Silent()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.True(l.IsSilent(22257u));
    }

    [Fact]
    public void DialogFromGuid_ImmunisesWcid_NeverSilent()
    {
        var l = New(threshold: 1);
        // The kind answered dialog before any silent verdict.
        l.RecordDialogFrom(0xB001u, 714u);
        l.RecordTalkDispatch(0xB002u, 714u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.False(l.IsSilent(714u));
    }

    [Fact]
    public void DialogAfterDispatch_BeforeMaturity_DoesNotConcludeSilent()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0xB002u, 714u, T0);
        // Dialog arrives before the grace window elapses.
        l.RecordDialogFrom(0xB002u, 714u);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.False(l.IsSilent(714u));
    }

    [Fact]
    public void DialogAfterSilentVerdict_ClearsIt()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0xC001u, 9000u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.True(l.IsSilent(9000u));
        // A later instance of the same kind DID talk — clear the verdict.
        l.RecordDialogFrom(0xC002u, 9000u);
        Assert.False(l.IsSilent(9000u));
        Assert.Equal(0, l.SilentWcidCount);
    }

    [Fact]
    public void SameGuidReTalked_CountsOnceTowardThreshold()
    {
        var l = New(threshold: 2);
        // The SAME instance dispatched twice must not satisfy a 2-distinct
        // threshold on its own.
        l.RecordTalkDispatch(0xD001u, 5000u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        l.RecordTalkDispatch(0xD001u, 5000u, T0.AddSeconds(Grace + 2));
        l.Evaluate(T0.AddSeconds(2 * Grace + 4));
        Assert.False(l.IsSilent(5000u));
        // A genuinely DIFFERENT instance tips it over.
        l.RecordTalkDispatch(0xD002u, 5000u, T0.AddSeconds(2 * Grace + 5));
        l.Evaluate(T0.AddSeconds(3 * Grace + 7));
        Assert.True(l.IsSilent(5000u));
    }

    [Fact]
    public void NullWcidDispatch_Ignored()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0xE001u, null, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.Equal(0, l.SilentWcidCount);
    }

    [Fact]
    public void DialogWithNullWcid_UsesPendingProbeWcid_ToImmunise()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0xF001u, 714u, T0);
        // Source left view, so the dialog's wcid is unknown; the learner must
        // recover it from the pending probe and immunise wcid 714.
        l.RecordDialogFrom(0xF001u, null);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.False(l.IsSilent(714u));
    }

    [Fact]
    public void ImmunisedWcid_NotReProbed_StaysNonSilent()
    {
        var l = New(threshold: 1);
        l.RecordDialogFrom(0x1001u, 714u);
        // Subsequent dispatches of an immunised kind are no-ops; even after a
        // grace window it never becomes silent.
        l.RecordTalkDispatch(0x1002u, 714u, T0);
        l.RecordTalkDispatch(0x1003u, 714u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.False(l.IsSilent(714u));
    }

    [Fact]
    public void MultipleKinds_LearnedIndependently()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0x2001u, 22257u, T0);   // fishing hole — silent
        l.RecordTalkDispatch(0x2002u, 714u, T0);     // grocer — will talk
        l.RecordDialogFrom(0x2002u, 714u);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.True(l.IsSilent(22257u));
        Assert.False(l.IsSilent(714u));
        Assert.Single(l.SilentWcids);
    }

    // ---- RecordUnattributedDialog (PopupString correlation) ----

    [Fact]
    public void UnattributedDialog_WithinWindow_ImmunisesPendingTalkKind()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0x3001u, 714u, T0);
        // A popup lands a couple seconds after the Talk — the target replied.
        l.RecordUnattributedDialog(T0.AddSeconds(2));
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.False(l.IsSilent(714u));
    }

    [Fact]
    public void UnattributedDialog_NoPendingTalk_NoEffect()
    {
        var l = New(threshold: 1);
        // An unrelated popup with nothing pending must not immunise anything,
        // and a later silent Talk still matures.
        l.RecordUnattributedDialog(T0);
        l.RecordTalkDispatch(0x3002u, 22257u, T0.AddSeconds(1));
        l.Evaluate(T0.AddSeconds(Grace + 2));
        Assert.True(l.IsSilent(22257u));
    }

    [Fact]
    public void UnattributedDialog_OutsideWindow_DoesNotImmunise()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0x3003u, 22257u, T0);
        // The popup arrives well after the grace window — not a reply to THIS
        // Talk, so the kind still matures silent.
        l.RecordUnattributedDialog(T0.AddSeconds(Grace + 5));
        l.Evaluate(T0.AddSeconds(Grace + 6));
        Assert.True(l.IsSilent(22257u));
    }

    [Fact]
    public void UnattributedDialog_PicksMostRecentPendingProbe()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0x4001u, 100u, T0);             // older
        l.RecordTalkDispatch(0x4002u, 200u, T0.AddSeconds(3)); // most recent
        // The popup correlates to the most-recent Talk (wcid 200).
        l.RecordUnattributedDialog(T0.AddSeconds(4));
        l.Evaluate(T0.AddSeconds(Grace + 5));
        Assert.False(l.IsSilent(200u)); // immunised
        Assert.True(l.IsSilent(100u));  // the older, un-replied kind matures
    }

    // ---- Integration: NoQuestKnowledgePolicy fallback Talk filtering ----

    private static WorldStateProjection WorldWithOneCreature(uint guid, uint wcid)
        => new()
        {
            Self = new SelfProjection
            {
                Guid = 0x500000A6u, Name = "Headless", Landblock = 0xAAB5u,
                CellId = 0xAAB50003u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                // A non-monster, non-hostile creature (e.g. a "Fishing Hole").
                // Within the cp-2413 no-hunt detour bound so these tests isolate
                // the silent-learner behaviour from the distance bound.
                new VisibleObjectProjection
                {
                    Guid = guid, Name = "Fishing Hole", Wcid = wcid,
                    ItemType = 0x10u, Distance = 10f,
                    IsCreature = true, IsMonster = false, ObservedHostile = false,
                },
            },
        };

    [Fact]
    public void Policy_TalksNonMonsterCreature_WhenNotLearnedSilent()
    {
        var learner = New(threshold: 1);
        var policy = new NoQuestKnowledgePolicy(null, learner);
        var goal = policy.ProposeGoal(WorldWithOneCreature(0xF00u, 22257u), new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Talk, goal!.Kind);
        Assert.Equal(0xF00u, goal.Target.Guid);
    }

    [Fact]
    public void Policy_SkipsTalk_WhenWcidLearnedSilent_FallsThroughToExplore()
    {
        var learner = New(threshold: 1);
        learner.RecordTalkDispatch(0xAAAu, 22257u, T0);
        learner.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.True(learner.IsSilent(22257u));

        var policy = new NoQuestKnowledgePolicy(null, learner);
        var goal = policy.ProposeGoal(WorldWithOneCreature(0xF00u, 22257u), new EventStream(), null);
        // The only creature is learned-silent scenery — no Talk; the fallback
        // falls through to Explore instead of marching to it.
        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Talk, goal!.Kind);
        Assert.Equal(GoalKind.Explore, goal.Kind);
    }

    [Fact]
    public void Policy_NullLearner_TalksNormally()
    {
        // Back-compat: the learner is optional; a null one suppresses nothing.
        var policy = new NoQuestKnowledgePolicy(null, null);
        var goal = policy.ProposeGoal(WorldWithOneCreature(0xF00u, 22257u), new EventStream(), null);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Talk, goal!.Kind);
    }

    [Fact]
    public void Policy_DoesNotTalkVisiblePlayer_FallsThroughToExplore()
    {
        // A visible PLAYER shares the IsCreature wire class but is never a dialog NPC.
        // The autonomous fallback Talk picker must classify it out (IsDialogNpcCandidate
        // excludes players) and fall through to Explore rather than marching to Talk it.
        var policy = new NoQuestKnowledgePolicy(null, null);
        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = 0x500000A6u, Name = "Headless", Landblock = 0xAAB5u,
                CellId = 0xAAB50003u, PositionX = 0, PositionY = 0, PositionZ = 0,
                HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = new[]
            {
                new VisibleObjectProjection
                {
                    Guid = 0x500000B7u, Name = "Otherbot", Wcid = 1u,
                    ItemType = 0x10u, Distance = 10f,
                    IsCreature = true, IsMonster = false, ObservedHostile = false,
                    IsPlayer = true,
                },
            },
        };

        var goal = policy.ProposeGoal(world, new EventStream(), null);

        Assert.NotNull(goal);
        Assert.NotEqual(GoalKind.Talk, goal!.Kind);
        Assert.Equal(GoalKind.Explore, goal.Kind);
    }

    // ---- Observability: RecordTalkDispatch outcome + Evaluate conclusions ----

    [Fact]
    public void RecordTalkDispatch_FreshProbe_ReturnsRecorded()
        => Assert.Equal(
            TalkProbeOutcome.Recorded,
            New().RecordTalkDispatch(0xA001u, 22257u, T0));

    [Fact]
    public void RecordTalkDispatch_NullWcid_ReturnsIgnoredUnknownWcid()
        => Assert.Equal(
            TalkProbeOutcome.IgnoredUnknownWcid,
            New().RecordTalkDispatch(0xA001u, null, T0));

    [Fact]
    public void RecordTalkDispatch_ImmunisedWcid_ReturnsIgnoredImmuneWcid()
    {
        var l = New();
        l.RecordDialogFrom(0xB001u, 714u); // 714 is now a proven talker
        Assert.Equal(
            TalkProbeOutcome.IgnoredImmuneWcid,
            l.RecordTalkDispatch(0xB002u, 714u, T0));
    }

    [Fact]
    public void Evaluate_ReturnsWcid_WhenItCrossesThreshold()
    {
        var l = New(threshold: 2);
        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        l.RecordTalkDispatch(0xA002u, 22257u, T0);
        var concluded = l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.Equal(new[] { 22257u }, concluded);
    }

    [Fact]
    public void Evaluate_ReturnsEmpty_WhenNothingNewlyConcludes()
    {
        var l = New(threshold: 2);
        // Only one distinct instance matures — below the 2-distinct threshold.
        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        var concluded = l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.Empty(concluded);
    }

    [Fact]
    public void Evaluate_DoesNotReReportAnAlreadyConcludedWcid()
    {
        var l = New(threshold: 1);
        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        Assert.Equal(new[] { 22257u }, l.Evaluate(T0.AddSeconds(Grace + 1)));
        // A second distinct silent instance of the SAME already-concluded kind
        // must not be reported again (it is reported exactly once).
        l.RecordTalkDispatch(0xA002u, 22257u, T0.AddSeconds(Grace + 2));
        Assert.Empty(l.Evaluate(T0.AddSeconds(2 * Grace + 4)));
        Assert.True(l.IsSilent(22257u));
    }

    [Fact]
    public void DistinctSilentInstances_TracksProgressTowardThreshold()
    {
        var l = New(threshold: 3);
        Assert.Equal(0, l.DistinctSilentInstances(22257u));
        Assert.Equal(0, l.DistinctSilentInstances(null));

        l.RecordTalkDispatch(0xA001u, 22257u, T0);
        l.Evaluate(T0.AddSeconds(Grace + 1));
        Assert.Equal(1, l.DistinctSilentInstances(22257u));

        l.RecordTalkDispatch(0xA002u, 22257u, T0.AddSeconds(Grace + 2));
        l.Evaluate(T0.AddSeconds(2 * Grace + 4));
        Assert.Equal(2, l.DistinctSilentInstances(22257u));
        // Still below threshold 3, so not yet silent.
        Assert.False(l.IsSilent(22257u));
    }
}
