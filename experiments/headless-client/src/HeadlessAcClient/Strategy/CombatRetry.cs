// SPDX-License-Identifier: AGPL-3.0-or-later
// CombatRetry — pure timing decision for the melee auto-repeat
// "loop-keeper" re-send (Phase 7f.2 in HandshakeDriver).
//
// AC1 melee is a server-side auto-repeat loop: one TargetedMeleeAttack
// starts the swing loop and the server re-swings at weapon cadence
// until the target dies, the bot leaves range, or the loop drops. When
// the bot/target geometry drifts the server ends the loop and reports
// GameEventAttackDone with errCode ActionCancelled. The motor must then
// re-send a bare TargetedMeleeAttack to restart the loop (re-sending
// ChangeCombatMode would itself cancel, so only the bare attack is
// re-sent).
//
// HOWEVER, against a MOBILE target the server first moves the bot into
// melee range by sending the bot's own object a StickToObject motion
// (sticky = the target). During that approach the server-side Attacking
// flag is still FALSE, so its "if (Attacking) return;" no-op guard does
// NOT cover a re-sent TargetedMeleeAttack — the re-send CANCELS the
// in-progress move-to and restarts it, emitting another ActionCancelled.
// Left unchecked that becomes a perpetual self-cancellation loop and the
// swing never lands (0 damage, death). So while the server is actively
// sticking the bot to the active combat target, the re-send is
// SUPPRESSED: let the server complete its move-to and swing. The
// suppression lapses once the stick observation goes stale (the
// stickSettleSec window), after which the normal re-send resumes (by
// then the server is typically Attacking==true so the re-send is no-op'd
// server-side). A stationary target's move-to completes in one shot, so
// no sustained stick/cancel loop occurs and that path is unaffected.
//
// Two triggers re-send (when not stick-suppressed):
//   1. A periodic safety-net interval (the loop may have dropped for a
//      reason we did not observe).
//   2. The server explicitly reported the loop dropped (ActionCancelled)
//      — react fast instead of waiting out the full safety interval,
//      but throttle by a short minimum so a burst of cancels cannot spam
//      the socket.
//
// This is mechanical motor bookkeeping (timing only); it carries no
// game knowledge — no target selection, no object identity, no priority.

namespace HeadlessAcClient.Strategy;

internal static class CombatRetry
{
    /// <summary>
    /// Decide whether to re-send the loop-keeper TargetedMeleeAttack.
    /// </summary>
    /// <param name="secondsSinceLastAttack">
    /// Seconds since the last attack send (the dispatch or a prior
    /// re-send). Negative values (clock skew) never trigger.
    /// </param>
    /// <param name="cancelRetryRequested">
    /// True when an AttackDone(ActionCancelled) for the active combat
    /// target was observed since the last send (server says the loop
    /// dropped).
    /// </param>
    /// <param name="normalIntervalSec">
    /// The periodic safety-net interval (always re-sends once elapsed).
    /// </param>
    /// <param name="fastMinIntervalSec">
    /// The minimum spacing for a cancel-driven fast re-send (anti-spam).
    /// Should be smaller than <paramref name="normalIntervalSec"/>.
    /// </param>
    /// <param name="secondsSinceServerStick">
    /// Seconds since the most recent server StickToObject motion that
    /// stuck the bot to the active combat target, or null if none has
    /// been observed for this target. While this is within
    /// <paramref name="stickSettleSec"/> the server is moving the bot
    /// into range and the re-send is suppressed (a re-send would cancel
    /// that in-progress move-to). Negative values (clock skew) are
    /// treated as not-active.
    /// </param>
    /// <param name="stickSettleSec">
    /// How long after a server stick-to-target the re-send stays
    /// suppressed.
    /// </param>
    /// <param name="secondsSinceServerCombatActivity">
    /// Seconds since the most recent SERVER signal that its auto-repeat
    /// swing loop is ALIVE for the active target — a normal
    /// GameEventAttackDone(None) (the between-swings power-refill signal)
    /// or an UpdateHealth for the target. Null if no such signal has been
    /// observed for this engagement. While this is within
    /// <paramref name="activityQuiescenceSec"/> the server is actively
    /// swinging, so a re-sent TargetedMeleeAttack is REJECTED with
    /// WeenieError.YoureTooBusy and the server cancels its own loop
    /// (GameEventAttackDone(ActionCancelled)) — the dominant cause of a
    /// stalled fight against a longer-lived target. Suppress the re-send
    /// until the loop has gone quiescent (genuinely dropped). Negative
    /// values (clock skew) are treated as not-active.
    /// </param>
    /// <param name="activityQuiescenceSec">
    /// How long the server auto-repeat loop must be SILENT (no normal
    /// AttackDone / no target health update) before the loop-keeper
    /// re-sends. Should exceed the slowest weapon swing cadence so a
    /// normal between-swings gap is not mistaken for a dropped loop.
    /// </param>
    public static bool ShouldReattack(
        double secondsSinceLastAttack,
        bool cancelRetryRequested,
        double normalIntervalSec,
        double fastMinIntervalSec,
        double? secondsSinceServerStick = null,
        double stickSettleSec = 0.0,
        double? secondsSinceServerCombatActivity = null,
        double activityQuiescenceSec = 0.0)
    {
        if (secondsSinceLastAttack < 0)
            return false;
        // The server is actively sticking us to the target (moving us
        // into melee range). Re-sending TargetedMeleeAttack now would
        // cancel that move-to and restart it; wait for it to settle.
        if (secondsSinceServerStick is double s && s >= 0 && s < stickSettleSec)
            return false;
        // The server's auto-repeat swing loop is demonstrably ALIVE
        // (recent normal AttackDone / target health update). A re-send now
        // is rejected YoureTooBusy and CANCELS the server loop — so let the
        // server keep swinging and only nudge once the loop is quiescent.
        if (secondsSinceServerCombatActivity is double a && a >= 0 && a < activityQuiescenceSec)
            return false;
        if (secondsSinceLastAttack >= normalIntervalSec)
            return true;
        return cancelRetryRequested && secondsSinceLastAttack >= fastMinIntervalSec;
    }

    /// <summary>
    /// AttackDone(ActionCancelled) wire code (0x0036) — the server's
    /// auto-repeat swing loop dropped. See the file header for the loop
    /// mechanics. NOTE: this raw code is NOT unique to combat — inventory
    /// paths also emit WeenieError 0x36 (ActionCancelled) — so consumers that
    /// must single out the COMBAT swing-loop cancel key on
    /// <see cref="SurfacedSwingLoopCancelCode"/> instead (see
    /// <see cref="SurfacedRejectionCode"/>).
    /// </summary>
    public const uint AttackDoneActionCancelled = 0x0036u;

    /// <summary>
    /// WeenieError.YoureTooBusy (0x001D) — the server's rejection of a re-sent attack
    /// that collided with its OWN still-alive auto-repeat swing loop. Like
    /// <see cref="AttackDoneActionCancelled"/> this is a TRANSIENT, self-induced
    /// loop-keeper collision that occurs in normal/winnable fights and is recovered by
    /// letting the loop run — it is NOT evidence the target cannot be reached or damaged,
    /// so <see cref="IsSemanticAttackRefusal"/> excludes it.
    /// </summary>
    public const uint YoureTooBusy = 0x001Du;

    /// <summary>
    /// Motor-reserved ActionRejected code stamped on the SURFACED combat
    /// swing-loop cancel event in place of the ambiguous raw wire code
    /// <see cref="AttackDoneActionCancelled"/> (0x0036). Mirrors the
    /// transport-failure reserved codes (0xFFFC-0xFFFE): real WeenieError
    /// codes are far smaller, so this high reserved value is an unambiguous
    /// discriminator that lets the combat chain recognise the swing-loop
    /// cancel WITHOUT misclassifying a same-coded inventory ActionCancelled.
    /// </summary>
    public const uint SurfacedSwingLoopCancelCode = 0xFFFAu;

    /// <summary>
    /// The ErrorCode to stamp on a SURFACED AttackDone rejection StreamEvent.
    /// The combat swing-loop cancel (raw <see cref="AttackDoneActionCancelled"/>)
    /// is remapped to the Motor-reserved <see cref="SurfacedSwingLoopCancelCode"/>
    /// so a combat-only consumer can recognise it unambiguously; every other
    /// (semantic) AttackDone error passes through unchanged so the LLM still
    /// sees the real refusal code. Pure wire-code remap; no game knowledge.
    /// </summary>
    public static uint SurfacedRejectionCode(uint rawAttackDoneErrorCode) =>
        rawAttackDoneErrorCode == AttackDoneActionCancelled
            ? SurfacedSwingLoopCancelCode
            : rawAttackDoneErrorCode;

    /// <summary>
    /// Decide whether a non-zero <c>GameEventAttackDone</c> error should be
    /// surfaced to the LLM as an <c>ActionRejected</c> learning signal.
    /// </summary>
    /// <param name="attackDoneErrorCode">
    /// The non-zero error code from the AttackDone packet.
    /// </param>
    /// <param name="hasActiveCombatTarget">
    /// True when the motor still holds a combat target lock
    /// (combatTargetGuid is set).
    /// </param>
    /// <remarks>
    /// Semantic refusals (OutOfRange, YouCanNotAttackThisCreature, …) always
    /// surface so the LLM can pivot verb/target. The ONE exception is a
    /// trailing ActionCancelled (0x0036) that arrives AFTER the engagement
    /// already ended — i.e. with no active combat target. That cancel is the
    /// benign teardown of the server's swing loop following a kill or a
    /// disengage: it names no problem the LLM can act on (the target is
    /// already dead/gone), yet surfacing it appends a misleading
    /// "Attack rejected" event for a target the bot just killed AND, because
    /// a non-transport ActionRejected is plan-invalidating, discards the
    /// establishment LLM call the bot fires right after the kill — a wasted
    /// round-trip per kill. An ActionCancelled WHILE a target is still locked
    /// is part of the live loop-keeper signalling (Phase 7f.2) and is kept.
    /// Mechanical: keys only on the wire code and combat-lock bookkeeping —
    /// no target choice, no object identity.
    /// </remarks>
    public static bool ShouldSurfaceAttackDoneRejection(
        uint attackDoneErrorCode,
        bool hasActiveCombatTarget)
    {
        if (attackDoneErrorCode == AttackDoneActionCancelled && !hasActiveCombatTarget)
            return false;
        return true;
    }

    /// <summary>
    /// True when an AttackDone error code is a SEMANTIC swing refusal — the server
    /// rejected the swing for a reason that will NOT resolve by simply re-sending the
    /// attack or letting the server's swing loop run (e.g. out-of-range against a target
    /// the bot cannot reach, cannot-attack). This is ANY non-zero code EXCEPT the two
    /// TRANSIENT, self-induced loop-keeper collisions — <see cref="AttackDoneActionCancelled"/>
    /// (0x0036, the swing loop dropping, recovered by a re-send) and
    /// <see cref="YoureTooBusy"/> (0x001D, a re-sent attack colliding with the still-alive
    /// loop) — both of which occur in NORMAL, winnable fights and must NOT be read as
    /// "cannot connect". Used to count "the bot cannot connect with this target at all"
    /// toward the unwinnable-flee evidence, distinct from an evaded swing (which DID reach
    /// the target). Mechanical: keys only on the wire code — no object identity, no target
    /// choice, no game-content knowledge.
    /// </summary>
    public static bool IsSemanticAttackRefusal(uint attackDoneErrorCode)
        => attackDoneErrorCode != 0u
           && attackDoneErrorCode != AttackDoneActionCancelled
           && attackDoneErrorCode != YoureTooBusy;

    /// <summary>
    /// Cap on consecutive <see cref="AttackDoneActionCancelled"/> swing-loop
    /// cancels — counted since the last real progress (a landed/evaded swing or
    /// an AttackDone(None) between-swings keep-alive, both of which reset the
    /// count) — before the loop-keeper stops arming the FAST re-send.
    ///
    /// A single cancel normally means the server's auto-repeat swing loop
    /// briefly dropped, and ONE fast re-send restarts it. But when the same
    /// cancel recurs back-to-back this many times with no intervening progress,
    /// the re-send is futile: the cause is durable (not the transient drop the
    /// fast-retry was designed for), and continuing to fast-retry just spins a
    /// tight cancel/re-send loop (observed live: 200+ back-to-back cancel pairs
    /// against a single target, 0 damage dealt, while the bot kept taking hits).
    /// Past the cap the motor falls back to the slow <c>CombatRetryIntervalSec</c>
    /// safety-net re-send and lets the existing no-damage / low-health reflexes
    /// end the engagement. Pure bookkeeping threshold; no game knowledge.
    /// </summary>
    public const int MaxConsecutiveSwingLoopCancels = 6;

    /// <summary>
    /// True once consecutive swing-loop cancels have reached
    /// <paramref name="cap"/> — i.e. the <see cref="AttackDoneActionCancelled"/>
    /// is PERSISTENT (a durable cause) rather than the transient loop-drop a
    /// re-send recovers. Callers stop arming the fast re-send at this point.
    /// Mechanical counter threshold; no game knowledge, no target choice.
    /// </summary>
    /// <param name="consecutiveCancels">
    /// Swing-loop cancels observed back-to-back since the last real progress.
    /// </param>
    /// <param name="cap">
    /// The threshold, normally <see cref="MaxConsecutiveSwingLoopCancels"/>.
    /// </param>
    public static bool IsPersistentSwingLoopCancel(int consecutiveCancels, int cap)
        => consecutiveCancels >= cap;

    /// <summary>
    /// True when the server's melee auto-repeat swing loop is demonstrably
    /// ACTIVE — a normal AttackDone(None) between-swings keep-alive, a
    /// landed/evaded swing, or a target-health drop was observed within
    /// <paramref name="quiescenceSec"/> (the same signal
    /// <see cref="ShouldReattack"/> uses to suppress a re-send). While active,
    /// the bot is in swing range: it should neither re-send the attack (which
    /// the server rejects YoureTooBusy and cancels its own loop) nor take a
    /// further approach step (which cancels the in-progress swing). Null (no
    /// activity observed yet) or a negative age (clock skew) is treated as NOT
    /// active. Mechanical timing predicate; no game knowledge.
    /// </summary>
    /// <param name="secondsSinceServerCombatActivity">
    /// Seconds since the last observed server combat-loop signal, or null if
    /// none has been observed for this engagement.
    /// </param>
    /// <param name="quiescenceSec">
    /// How long the loop must be silent before it is considered dropped.
    /// </param>
    public static bool IsSwingLoopActive(double? secondsSinceServerCombatActivity, double quiescenceSec)
        => secondsSinceServerCombatActivity is double s && s >= 0 && s < quiescenceSec;

    /// <summary>
    /// Decide whether to abandon the current melee target EARLY because the
    /// bot demonstrably cannot damage it: every swing this fight has been
    /// evaded and zero damage has been dealt. This is a liveness/tempo guard
    /// in the same family as the absolute no-damage watchdog
    /// (<c>AbandonOnNoDamageSec</c>) — it just trips sooner once the bot has
    /// swung enough times to make "0 landed" conclusive rather than unlucky,
    /// so the bot stops wasting the full watchdog window on a target that
    /// out-defends it and is free to try a different one.
    /// </summary>
    /// <param name="swingsLanded">Swings that landed a hit this fight.</param>
    /// <param name="damageDealt">Total damage dealt to the target this fight.</param>
    /// <param name="swingsEvaded">Swings the target evaded this fight.</param>
    /// <param name="minEvadedSwings">
    /// Minimum number of all-evaded swings (with zero landed and zero damage)
    /// before the bot concludes it cannot damage this target. Chosen high
    /// enough that a winnable fight's unlucky early-evade streak does not trip
    /// it.
    /// </param>
    /// <remarks>
    /// Mechanical: keys ONLY on the bot's own swing outcomes (landed/evaded)
    /// and its own damage dealt — no monster KIND, name, wcid, landblock, or
    /// server text, and it never chooses a new target. It mirrors the prompt's
    /// COMBAT SAFETY guidance ("many evaded with 0 landed means you cannot win
    /// — disengage") as a fast motor reflex, because the LLM round-trip is too
    /// slow to break a live engagement.
    /// </remarks>
    public static bool ShouldAbandonUnbeatable(
        int swingsLanded, uint damageDealt, int swingsEvaded, int minEvadedSwings)
        => swingsLanded == 0 && damageDealt == 0u && swingsEvaded >= minEvadedSwings;

    /// <summary>
    /// True when the bot should abandon a target whose defenses fully ABSORB its
    /// hits: it has LANDED at least <paramref name="minLandedSwings"/> swings yet
    /// dealt zero total damage. This is the complement of
    /// <see cref="ShouldAbandonUnbeatable"/> (which owns the 0-landed "every swing
    /// evaded" case): here the bot DOES connect, but its hits are mitigated to 0,
    /// so the fight is unwinnable for the same reason — it cannot reduce the
    /// target's health.
    /// </summary>
    /// <param name="swingsLanded">Swings that landed a hit this fight.</param>
    /// <param name="damageDealt">Total damage dealt to the target this fight.</param>
    /// <param name="minLandedSwings">
    /// Minimum number of LANDED swings (with zero total damage) before the bot
    /// concludes its hits are fully absorbed. Chosen high enough that a winnable
    /// fight's unlucky early low rolls do not trip it.
    /// </param>
    /// <remarks>
    /// Distinct from <see cref="ShouldAbandonStalemate"/>: that path keys on the
    /// target's OBSERVED health barely moving, but a 0-damage exchange produces no
    /// server health-change updates, so the health sample goes STALE and the
    /// stalemate verdict is withheld — leaving only the slow absolute no-damage
    /// watchdog (<c>AbandonOnNoDamageSec</c>). This trips sooner on the bot's OWN
    /// swing/damage tally, which needs no health observation. Mechanical: keys ONLY
    /// on the bot's own landed-swing count and its own damage dealt — no monster
    /// KIND, name, wcid, landblock, or server text, and it never chooses a new
    /// target. It mirrors the prompt's COMBAT SAFETY guidance (many swings land but
    /// the target's health holds ⇒ you cannot out-damage its defense ⇒ disengage)
    /// as a fast motor reflex, because the LLM round-trip is too slow to break a
    /// live engagement.
    /// </remarks>
    public static bool ShouldAbandonArmorAbsorbed(
        int swingsLanded, uint damageDealt, int minLandedSwings)
        => swingsLanded >= minLandedSwings && damageDealt == 0u;

    /// <summary>
    /// True when a just-abandoned fight is SWUNG-ZERO-DAMAGE: the bot SWUNG at the
    /// target at least once (a landed OR evaded swing — so it reached melee) yet
    /// dealt zero TOTAL damage over the whole fight. This is the precise
    /// can't-hurt-this-KIND signal the combat-feel ledger records (and the
    /// out-defended veto keys on), distinct from two NON-signals:
    /// - a no-swing CAN'T-CLOSE abandon (0 swings — a pathing miss against one
    ///   individual, not evidence about the KIND), excluded by the swing check; and
    /// - a fight in which the bot dealt SOME damage then stalled (the absolute
    ///   no-damage watchdog abandons on "no damage RECENTLY", not "0 damage this
    ///   fight"), excluded by the total-damage check.
    /// Mechanical: keys ONLY on the bot's own swing outcomes and its own total
    /// damage dealt — no monster KIND, name, wcid, landblock, or server text.
    /// </summary>
    /// <param name="swingsLanded">Swings that landed a hit this fight.</param>
    /// <param name="swingsEvaded">Swings the target evaded this fight.</param>
    /// <param name="damageDealt">Total damage dealt to the target this fight.</param>
    public static bool IsSwungZeroDamageFight(int swingsLanded, int swingsEvaded, uint damageDealt)
        => (swingsLanded + swingsEvaded) > 0 && damageDealt == 0u;

    /// <summary>
    /// True when the bot should abandon a NO-PROGRESS STALEMATE: it IS landing
    /// hits on the target (so the all-evaded <see cref="ShouldAbandonUnbeatable"/>
    /// does NOT apply) over a sustained run of swings, yet the target's OBSERVED
    /// health has barely moved — it has dropped at most
    /// <paramref name="maxTargetHealthLostFraction"/> since the fight began. That
    /// means the bot's damage cannot kill this target in a reasonable time (it
    /// out-tanks or regenerates faster than the bot whittles it down), so
    /// grinding it is futile; break off so the bot picks a winnable target or
    /// pursues a directive instead of swinging forever. This is the NON-danger,
    /// NON-all-evaded counterpart to the existing abandons: the bot is neither
    /// being beaten (it takes no lethal damage — the danger reflexes own that)
    /// nor whiffing (it connects); it simply cannot out-damage the target's
    /// defense.
    ///
    /// Conservative gates so a slow-but-winnable trade is never aborted: a
    /// minimum of LANDED hits (the bot demonstrably connects), a minimum total
    /// swing count (sustained, not an unlucky burst), and the target STILL
    /// barely scratched after all of it. Target health unknown (not yet
    /// observed) ⇒ does NOT fire. Mechanical: keys ONLY on the bot's own swing
    /// outcomes and the target's OBSERVED health fraction — no monster KIND,
    /// name, wcid, landblock, or server text, and it never chooses a new target.
    /// </summary>
    /// <param name="swingsLanded">Swings that landed a hit this fight.</param>
    /// <param name="swingsEvaded">Swings the target evaded this fight.</param>
    /// <param name="minSwings">
    /// Minimum total swings (landed + evaded) before the stalemate verdict is
    /// conclusive.
    /// </param>
    /// <param name="minLanded">
    /// Minimum LANDED hits required, so this fires only when the bot
    /// demonstrably connects (the all-evaded case is owned by
    /// <see cref="ShouldAbandonUnbeatable"/>).
    /// </param>
    /// <param name="targetHealthAtStart">Target health fraction at fight start, or null.</param>
    /// <param name="targetHealthNow">Target's latest observed health fraction, or null.</param>
    /// <param name="maxTargetHealthLostFraction">
    /// The most the target may have lost (start − now) and still count as a
    /// stalemate; above this the bot IS making progress, so it stays quiet.
    /// </param>
    /// <param name="secondsSinceLastHealthObservation">
    /// Wall-clock age of the most recent target-health sample. The verdict is
    /// withheld on a STALE reading (see <paramref name="maxHealthObservationAgeSec"/>):
    /// a winning fight whose UpdateHealth lagged would otherwise show only a
    /// small loss in its last sample and be wrongly abandoned. Pass a large
    /// value (e.g. double.MaxValue) when no sample has been observed.
    /// </param>
    /// <param name="maxHealthObservationAgeSec">
    /// Maximum age of the health sample for the trend to be trusted.
    /// </param>
    public static bool ShouldAbandonStalemate(
        int swingsLanded, int swingsEvaded, int minSwings, int minLanded,
        double? targetHealthAtStart, double? targetHealthNow,
        double maxTargetHealthLostFraction,
        double secondsSinceLastHealthObservation, double maxHealthObservationAgeSec)
    {
        if (swingsLanded < minLanded) return false;
        if (swingsLanded + swingsEvaded < minSwings) return false;
        // The trend must come from a CURRENT reading — a stale sample could
        // understate the loss and abort a winning fight whose UpdateHealth
        // lagged.
        if (secondsSinceLastHealthObservation > maxHealthObservationAgeSec) return false;
        if (targetHealthAtStart is not double tStart) return false;
        if (targetHealthNow is not double tNow) return false;
        return (tStart - tNow) <= maxTargetHealthLostFraction;
    }
}
