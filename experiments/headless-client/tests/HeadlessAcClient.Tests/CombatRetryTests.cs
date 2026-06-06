// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CombatRetry.ShouldReattack — the pure timing decision for
// the Phase 7f.2 melee loop-keeper re-send.

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatRetryTests
{
    private const double Normal = 5.0;
    private const double FastMin = 0.35;

    [Fact]
    public void NoCancel_BeforeNormalInterval_DoesNotReattack()
        => Assert.False(CombatRetry.ShouldReattack(2.0, cancelRetryRequested: false, Normal, FastMin));

    [Fact]
    public void NoCancel_AtNormalInterval_Reattacks()
        => Assert.True(CombatRetry.ShouldReattack(5.0, cancelRetryRequested: false, Normal, FastMin));

    [Fact]
    public void NoCancel_PastNormalInterval_Reattacks()
        => Assert.True(CombatRetry.ShouldReattack(7.5, cancelRetryRequested: false, Normal, FastMin));

    [Fact]
    public void Cancel_PastFastMin_ReattacksEarly()
        // A cancel arrived and the anti-spam window has passed — fire
        // well before the 5s safety net.
        => Assert.True(CombatRetry.ShouldReattack(0.4, cancelRetryRequested: true, Normal, FastMin));

    [Fact]
    public void Cancel_AtFastMin_Reattacks()
        => Assert.True(CombatRetry.ShouldReattack(0.35, cancelRetryRequested: true, Normal, FastMin));

    [Fact]
    public void Cancel_WithinFastMin_DoesNotSpam()
        // A burst of cancels must not re-send faster than the anti-spam
        // floor.
        => Assert.False(CombatRetry.ShouldReattack(0.1, cancelRetryRequested: true, Normal, FastMin));

    [Fact]
    public void NegativeElapsed_NeverReattacks()
    {
        Assert.False(CombatRetry.ShouldReattack(-1.0, cancelRetryRequested: true, Normal, FastMin));
        Assert.False(CombatRetry.ShouldReattack(-1.0, cancelRetryRequested: false, Normal, FastMin));
    }

    [Fact]
    public void Cancel_StillHonorsNormalIntervalWhenNoFastWindow()
    {
        // Even with fastMin == normal (degenerate config), a cancel does
        // not fire before the interval.
        Assert.False(CombatRetry.ShouldReattack(1.0, cancelRetryRequested: true, Normal, Normal));
        Assert.True(CombatRetry.ShouldReattack(5.0, cancelRetryRequested: true, Normal, Normal));
    }

    private const double StickSettle = 2.0;

    [Fact]
    public void ServerStickActive_SuppressesCancelDrivenFastRetry()
        // The server is mid move-into-range (stick observed 0.5s ago); a
        // cancel-driven re-send would cancel that move-to, so suppress it.
        => Assert.False(CombatRetry.ShouldReattack(
            0.5, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: 0.5, StickSettle));

    [Fact]
    public void ServerStickActive_SuppressesPeriodicReattack()
        // Even past the 5s safety net, an active stick (still chasing)
        // must not trigger a re-send that would cancel the server move-to.
        => Assert.False(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: 1.0, StickSettle));

    [Fact]
    public void ServerStickStale_AllowsReattackAgain()
        // The stick observation has aged past the settle window — the
        // server has stopped sticking us, so the normal re-send resumes.
        => Assert.True(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: 2.5, StickSettle));

    [Fact]
    public void ServerStickStale_AllowsCancelFastRetry()
        => Assert.True(CombatRetry.ShouldReattack(
            0.5, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: 3.0, StickSettle));

    [Fact]
    public void NoServerStickObserved_BehavesAsBefore()
    {
        // null stick == no suppression: identical to the legacy 4-arg form.
        Assert.True(CombatRetry.ShouldReattack(
            5.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle));
        Assert.True(CombatRetry.ShouldReattack(
            0.4, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle));
    }

    [Fact]
    public void NegativeStickElapsed_TreatedAsNotActive()
        // Clock skew on the stick timestamp must not be read as "active"
        // (which would suppress forever); fall through to normal logic.
        => Assert.True(CombatRetry.ShouldReattack(
            5.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: -1.0, StickSettle));

    private const double Quiescence = 4.0;

    [Fact]
    public void ServerLoopAlive_SuppressesCancelDrivenFastRetry()
        // The server's auto-repeat loop swung 0.5s ago (alive). A
        // cancel-driven re-send would throw YoureTooBusy and cancel the
        // server loop — suppress it.
        => Assert.False(CombatRetry.ShouldReattack(
            0.5, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: 0.5, Quiescence));

    [Fact]
    public void ServerLoopAlive_SuppressesPeriodicReattack()
        // Even past the 5s safety net, recent server combat activity
        // (3.5s ago, still < 4s quiescence) means the loop is alive — a
        // periodic re-send would cancel it, so suppress.
        => Assert.False(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: 3.5, Quiescence));

    [Fact]
    public void ServerLoopQuiescent_AllowsPeriodicReattack()
        // No server activity for 5s (> 4s quiescence) and past the normal
        // interval — the loop genuinely dropped, so re-acquire.
        => Assert.True(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: 5.0, Quiescence));

    [Fact]
    public void ServerLoopQuiescent_StillRespectsNormalInterval()
        // Quiescent server loop does NOT override the normal interval: a
        // re-send still waits for the interval (no busy-spam on silence).
        => Assert.False(CombatRetry.ShouldReattack(
            2.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: 10.0, Quiescence));

    [Fact]
    public void ServerLoopQuiescent_AllowsCancelFastRetry()
        // After the loop went quiescent, a cancel-driven fast re-send may
        // fire to restart it.
        => Assert.True(CombatRetry.ShouldReattack(
            0.5, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: 5.0, Quiescence));

    [Fact]
    public void NoServerActivityObserved_BehavesAsBefore()
    {
        // null activity == no suppression: identical to the 6-arg form.
        Assert.True(CombatRetry.ShouldReattack(
            5.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: null, Quiescence));
        Assert.True(CombatRetry.ShouldReattack(
            0.4, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: null, Quiescence));
    }

    [Fact]
    public void NegativeActivityElapsed_TreatedAsNotActive()
        // Clock skew on the activity timestamp must not suppress forever.
        => Assert.True(CombatRetry.ShouldReattack(
            5.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle,
            secondsSinceServerCombatActivity: -1.0, Quiescence));

    [Fact]
    public void StickAndActivity_EitherSuppresses()
    {
        // An active stick suppresses even if activity is stale.
        Assert.False(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: 0.5, StickSettle,
            secondsSinceServerCombatActivity: 10.0, Quiescence));
        // Active server combat suppresses even if the stick is stale.
        Assert.False(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: 3.0, StickSettle,
            secondsSinceServerCombatActivity: 1.0, Quiescence));
    }

    // ShouldSurfaceAttackDoneRejection — whether a non-zero AttackDone
    // error becomes an LLM-facing ActionRejected learning signal.

    [Fact]
    public void Cancel_NoActiveTarget_NotSurfaced()
        // Benign post-kill / post-disengage swing-loop teardown: the
        // ActionCancelled arrives with no combat lock. Suppress so it
        // neither pollutes the prompt nor discards the post-kill LLM call.
        => Assert.False(CombatRetry.ShouldSurfaceAttackDoneRejection(
            CombatRetry.AttackDoneActionCancelled, hasActiveCombatTarget: false));

    [Fact]
    public void Cancel_WithActiveTarget_Surfaced()
        // A cancel WHILE still engaged is live loop-keeper signalling —
        // keep it (preserves cp-2253 in-fight behavior).
        => Assert.True(CombatRetry.ShouldSurfaceAttackDoneRejection(
            CombatRetry.AttackDoneActionCancelled, hasActiveCombatTarget: true));

    [Fact]
    public void SemanticRefusal_NoActiveTarget_Surfaced()
        // A real WeenieError (e.g. OutOfRange 0x0030) must always reach the
        // LLM so it can pivot — even with no active target.
        => Assert.True(CombatRetry.ShouldSurfaceAttackDoneRejection(
            0x0030u, hasActiveCombatTarget: false));

    [Fact]
    public void SemanticRefusal_WithActiveTarget_Surfaced()
        => Assert.True(CombatRetry.ShouldSurfaceAttackDoneRejection(
            0x0030u, hasActiveCombatTarget: true));

    [Fact]
    public void OnlyActionCancelledIsGated_OtherCodesAlwaysSurface()
    {
        // The suppression is keyed to the exact ActionCancelled code; no
        // other refusal code is gated by the missing-target condition.
        Assert.True(CombatRetry.ShouldSurfaceAttackDoneRejection(
            0x001Du /* YoureTooBusy */, hasActiveCombatTarget: false));
        Assert.True(CombatRetry.ShouldSurfaceAttackDoneRejection(
            0x0001u, hasActiveCombatTarget: false));
        Assert.Equal(0x0036u, CombatRetry.AttackDoneActionCancelled);
    }

    // --- ShouldAbandonUnbeatable (early "cannot damage" abandon) ------------
    private const int MinEvaded = 12;

    [Fact]
    public void Unbeatable_AllEvadedPastThreshold_Abandons()
        // 12 swings, all evaded, 0 landed, 0 damage — conclusively cannot hit.
        => Assert.True(CombatRetry.ShouldAbandonUnbeatable(
            swingsLanded: 0, damageDealt: 0u, swingsEvaded: 12, MinEvaded));

    [Fact]
    public void Unbeatable_AllEvadedWellPastThreshold_Abandons()
        => Assert.True(CombatRetry.ShouldAbandonUnbeatable(
            swingsLanded: 0, damageDealt: 0u, swingsEvaded: 30, MinEvaded));

    [Fact]
    public void Unbeatable_BelowEvadeThreshold_DoesNotAbandon()
        // An unlucky early-evade streak short of the threshold is tolerated.
        => Assert.False(CombatRetry.ShouldAbandonUnbeatable(
            swingsLanded: 0, damageDealt: 0u, swingsEvaded: 11, MinEvaded));

    [Fact]
    public void Unbeatable_OneLanded_DoesNotAbandon()
        // A single landed hit proves the bot CAN damage this target — keep
        // fighting (let the 60s no-damage backstop own any later stall).
        => Assert.False(CombatRetry.ShouldAbandonUnbeatable(
            swingsLanded: 1, damageDealt: 0u, swingsEvaded: 20, MinEvaded));

    [Fact]
    public void Unbeatable_DamageDealtNoLandCount_DoesNotAbandon()
        // Damage recorded (even if the landed-swing counter lagged) means
        // progress is being made — do not abandon early.
        => Assert.False(CombatRetry.ShouldAbandonUnbeatable(
            swingsLanded: 0, damageDealt: 3u, swingsEvaded: 20, MinEvaded));

    [Fact]
    public void Unbeatable_NoSwingsYet_DoesNotAbandon()
        // Fresh engagement, nothing observed — never trip on zero data.
        => Assert.False(CombatRetry.ShouldAbandonUnbeatable(
            swingsLanded: 0, damageDealt: 0u, swingsEvaded: 0, MinEvaded));
}
