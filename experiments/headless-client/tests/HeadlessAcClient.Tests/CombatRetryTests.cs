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

    // IsSemanticAttackRefusal — a non-cancel AttackDone error (the bot cannot
    // connect with this target) vs the benign auto-repeat-loop cancel.

    [Fact]
    public void SemanticRefusal_OutOfRange_True()
        // A real "cannot connect" refusal (e.g. OutOfRange 0x0030).
        => Assert.True(CombatRetry.IsSemanticAttackRefusal(0x0030u));

    [Fact]
    public void SemanticRefusal_ActionCancelled_False()
        // The auto-repeat-loop cancel is recovered by a re-send, not a "cannot connect".
        => Assert.False(CombatRetry.IsSemanticAttackRefusal(CombatRetry.AttackDoneActionCancelled));

    [Fact]
    public void SemanticRefusal_NoError_False()
        // Error code 0 = the normal between-swings AttackDone (loop alive).
        => Assert.False(CombatRetry.IsSemanticAttackRefusal(0u));

    [Fact]
    public void SemanticRefusal_YoureTooBusy_False()
        // The transient self-induced loop-keeper collision (a re-sent attack hitting the
        // still-alive swing loop) recovers on its own — it is NOT "cannot connect", and
        // counting it would false-flee a winnable fight where the bot re-sends fast.
        => Assert.False(CombatRetry.IsSemanticAttackRefusal(CombatRetry.YoureTooBusy));

    [Fact]
    public void SemanticRefusal_NonTransientRefusalCodes_True()
    {
        // Real "cannot connect / cannot damage this target" codes count.
        Assert.True(CombatRetry.IsSemanticAttackRefusal(0x0406u)); // MagicTargetOutOfRange
        Assert.True(CombatRetry.IsSemanticAttackRefusal(0x0468u)); // SkillTooLow
        Assert.True(CombatRetry.IsSemanticAttackRefusal(0x0550u)); // MissileOutOfRange
    }

    [Fact]
    public void SurfacedRejectionCode_RemapsCancelToReserved_PassesOthersThrough()
    {
        // The swing-loop cancel (raw 0x0036) is remapped to the Motor-reserved
        // surfaced code so a combat-only consumer can single it out; the reserved
        // code must differ from the raw code and from the transport reserved
        // range (0xFFFC-0xFFFE) and the disengage code (0xFFFB).
        Assert.Equal(CombatRetry.SurfacedSwingLoopCancelCode,
            CombatRetry.SurfacedRejectionCode(CombatRetry.AttackDoneActionCancelled));
        Assert.NotEqual(CombatRetry.AttackDoneActionCancelled, CombatRetry.SurfacedSwingLoopCancelCode);
        Assert.DoesNotContain(CombatRetry.SurfacedSwingLoopCancelCode,
            new uint[] { 0xFFFBu, 0xFFFCu, 0xFFFDu, 0xFFFEu });
        // Every other (semantic) AttackDone error passes through unchanged so the
        // LLM still sees the real refusal code.
        Assert.Equal(0x001Du, CombatRetry.SurfacedRejectionCode(0x001Du)); // YoureTooBusy
        Assert.Equal(0x0010u, CombatRetry.SurfacedRejectionCode(0x0010u));
        Assert.Equal(0x046Au, CombatRetry.SurfacedRejectionCode(0x046Au));
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

    // --- ShouldAbandonStalemate (lands hits but target won't die) -----------
    private const int StaleMinSwings = 18;
    private const int StaleMinLanded = 4;
    private const double StaleMaxLost = 0.15;
    private const double StaleMaxAge = 6.0;   // health-sample freshness window
    private const double StaleFresh = 1.0;    // a fresh sample (1s old)

    [Fact]
    public void Stalemate_LandsHitsButTargetBarelyScratched_Abandons()
        // Sustained fight, the bot connects (8 landed), yet the target only
        // lost 5% — it out-tanks the bot's damage, so grinding is futile.
        => Assert.True(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 8, swingsEvaded: 14, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: 1.0, targetHealthNow: 0.95, StaleMaxLost,
            secondsSinceLastHealthObservation: StaleFresh, StaleMaxAge));

    [Fact]
    public void Stalemate_TargetHealthDroppingFast_DoesNotAbandon()
        // The target lost 50% — the bot IS making progress, so keep fighting.
        => Assert.False(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 8, swingsEvaded: 14, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: 1.0, targetHealthNow: 0.50, StaleMaxLost,
            secondsSinceLastHealthObservation: StaleFresh, StaleMaxAge));

    [Fact]
    public void Stalemate_TooFewLanded_DoesNotAbandon()
        // The all-evaded / can't-connect case is owned by ShouldAbandonUnbeatable;
        // this reflex requires the bot to demonstrably land hits first.
        => Assert.False(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 2, swingsEvaded: 20, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: 1.0, targetHealthNow: 1.0, StaleMaxLost,
            secondsSinceLastHealthObservation: StaleFresh, StaleMaxAge));

    [Fact]
    public void Stalemate_NotSustained_DoesNotAbandon()
        // Below the total-swing threshold — an early stretch is not conclusive.
        => Assert.False(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 5, swingsEvaded: 5, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: 1.0, targetHealthNow: 1.0, StaleMaxLost,
            secondsSinceLastHealthObservation: StaleFresh, StaleMaxAge));

    [Fact]
    public void Stalemate_TargetHealthUnknown_DoesNotAbandon()
        // Without an observed target-health trend we cannot tell a stalemate
        // from progress — stay quiet (conservative).
        => Assert.False(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 8, swingsEvaded: 14, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: null, targetHealthNow: 0.9, StaleMaxLost,
            secondsSinceLastHealthObservation: StaleFresh, StaleMaxAge));

    [Fact]
    public void Stalemate_JustWithinMaxLost_Abandons()
        // Lost ~0.14 (clearly within the 0.15 cap) — still a stalemate.
        => Assert.True(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 6, swingsEvaded: 16, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: 1.0, targetHealthNow: 0.86, StaleMaxLost,
            secondsSinceLastHealthObservation: StaleFresh, StaleMaxAge));

    [Fact]
    public void Stalemate_StaleHealthSample_DoesNotAbandon()
        // The last health sample is older than the freshness window — its small
        // observed loss may be stale (the target could actually be dropping
        // fast). Withhold the verdict rather than abort a possibly-winning fight.
        => Assert.False(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 8, swingsEvaded: 14, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: 1.0, targetHealthNow: 0.95, StaleMaxLost,
            secondsSinceLastHealthObservation: 30.0, StaleMaxAge));

    [Fact]
    public void Stalemate_NoHealthSampleEver_DoesNotAbandon()
        // No sample observed at all (age = MaxValue) — never fire.
        => Assert.False(CombatRetry.ShouldAbandonStalemate(
            swingsLanded: 8, swingsEvaded: 14, StaleMinSwings, StaleMinLanded,
            targetHealthAtStart: 1.0, targetHealthNow: 0.95, StaleMaxLost,
            secondsSinceLastHealthObservation: double.MaxValue, StaleMaxAge));
}
