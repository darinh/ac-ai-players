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
}
