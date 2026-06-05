// SPDX-License-Identifier: AGPL-3.0-or-later
// CombatRetry — pure timing decision for the melee auto-repeat
// "loop-keeper" re-send (Phase 7f.2 in HandshakeDriver).
//
// AC1 melee is a server-side auto-repeat loop: one TargetedMeleeAttack
// starts the swing loop and the server re-swings at weapon cadence
// until the target dies, the bot leaves range, or the loop drops. The
// headless bot is a non-FastTick player, so the server does NOT keep it
// stuck to the target during a swing; when the bot/target geometry
// drifts the server ends the loop and reports GameEventAttackDone with
// errCode ActionCancelled. The motor must then re-send a bare
// TargetedMeleeAttack to restart the loop (re-sending ChangeCombatMode
// would itself cancel, so only the bare attack is re-sent).
//
// Two triggers re-send:
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
    public static bool ShouldReattack(
        double secondsSinceLastAttack,
        bool cancelRetryRequested,
        double normalIntervalSec,
        double fastMinIntervalSec)
    {
        if (secondsSinceLastAttack < 0)
            return false;
        if (secondsSinceLastAttack >= normalIntervalSec)
            return true;
        return cancelRetryRequested && secondsSinceLastAttack >= fastMinIntervalSec;
    }
}
