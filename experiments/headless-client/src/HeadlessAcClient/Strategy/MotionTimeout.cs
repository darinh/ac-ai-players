namespace HeadlessAcClient.Strategy;

/// <summary>
/// Pure helper that selects the wall-clock motion timeout based on whether the
/// current motion is doing something productive (walking to a locked target, a
/// remembered sighting, or following an explored route) versus an unproductive
/// "no-lock" standstill.
///
/// cp-2272 (motor tempo): when the LLM-goal pre-emptor fails to resolve an
/// out-of-PVS target (no live snapshot, no sighting-memory match, no available
/// exploration frontier) it falls through to the schema picker, which finds no
/// in-range candidate and sends a stationary AutonomousPosition. That no-lock
/// motion used to burn the FULL 30s wall-clock safety timeout before the bot
/// re-deliberated — pure waste that dominated cold-start tempo. A no-lock
/// motion has nothing to walk toward, so it should time out fast and let the
/// Strategy layer pick a new goal.
///
/// This is mechanical motor timing only — it carries no game knowledge.
/// </summary>
public static class MotionTimeout
{
    /// <summary>
    /// The timeout (seconds) that applies to the current motion. A motion with
    /// any productive destination state keeps the long safety timeout; a
    /// no-lock standstill uses the short one.
    /// </summary>
    public static int EffectiveSeconds(bool hasProductiveLock, int lockedSec, int noLockSec)
        => hasProductiveLock ? lockedSec : noLockSec;

    /// <summary>
    /// True when <paramref name="now"/> is past the effective timeout measured
    /// from <paramref name="startedAt"/>.
    /// </summary>
    public static bool IsExpired(
        System.DateTime startedAt,
        System.DateTime now,
        bool hasProductiveLock,
        int lockedSec,
        int noLockSec)
        => (now - startedAt).TotalSeconds
           > EffectiveSeconds(hasProductiveLock, lockedSec, noLockSec);
}
