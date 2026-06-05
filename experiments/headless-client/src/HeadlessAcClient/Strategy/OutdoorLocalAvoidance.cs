using System;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Pure geometry for reactive outdoor obstacle avoidance. When a straight-
/// line outdoor walk is BLOCKED (the server reports ~0 movement against a
/// meaningful expected step) the motor asks this helper for a bounded
/// sequence of short DETOUR waypoints at alternate local headings, to slip
/// past the obstacle before falling back to a fast Blocked failure.
///
/// This is mechanical locomotion only — the geometric analogue of physics
/// collision response. It encodes NO game knowledge: no NPC/quest names,
/// no wcids, no landblock ids, no map layout, no text parsing. The caller
/// supplies its own global-XY self/target; this helper only rotates the
/// heading and projects a short waypoint. The Strategy layer (LLM) still
/// decides WHERE to go; this only changes HOW the motor walks there when
/// physics blocks the straight line.
/// </summary>
internal static class OutdoorLocalAvoidance
{
    /// <summary>
    /// Detour heading offsets (radians), tried in this order. Every offset
    /// is within ±90° of the target bearing, so each detour keeps
    /// non-negative forward progress toward the target
    /// (dot(detourDir, targetDir) = cos(offset) >= 0): the bot never
    /// sidesteps backward, away from the goal.
    /// </summary>
    public static readonly float[] DetourOffsetsRad =
    {
        +MathF.PI / 4f,  // +45°
        -MathF.PI / 4f,  // -45°
        +MathF.PI / 2f,  // +90°
        -MathF.PI / 2f,  // -90°
    };

    /// <summary>
    /// Number of bounded detour attempts before the caller must give up and
    /// fast-fail Blocked (so Strategy re-deliberates a different DIRECTION,
    /// not just another equally-blocked cell from the same pocket).
    /// </summary>
    public static int MaxAttempts => DetourOffsetsRad.Length;

    /// <summary>
    /// Default short detour distance (world units). Deliberately small so a
    /// detour probes locally around the obstacle rather than committing to a
    /// long off-course leg.
    /// </summary>
    public const float DefaultDetourDistance = 8.0f;

    /// <summary>
    /// Choose the detour waypoint (in global XY) for a given 0-based attempt.
    /// Returns false when <paramref name="attemptIndex"/> is out of range
    /// (the caller must then fast-fail Blocked) or when self and target
    /// coincide (no meaningful bearing to offset from). On success the
    /// waypoint is exactly <paramref name="detourDistance"/> units from self
    /// along the offset bearing, and is guaranteed forward-progressing.
    /// </summary>
    public static bool TryChooseDetour(
        float selfGx, float selfGy,
        float targetGx, float targetGy,
        int attemptIndex,
        float detourDistance,
        out float detourGx, out float detourGy)
    {
        detourGx = selfGx;
        detourGy = selfGy;

        if (attemptIndex < 0 || attemptIndex >= DetourOffsetsRad.Length)
            return false;

        var dx = targetGx - selfGx;
        var dy = targetGy - selfGy;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f)
            return false;

        var targetBearing = MathF.Atan2(dy, dx);
        var bearing = targetBearing + DetourOffsetsRad[attemptIndex];
        var d = detourDistance > 0f ? detourDistance : DefaultDetourDistance;

        detourGx = selfGx + MathF.Cos(bearing) * d;
        detourGy = selfGy + MathF.Sin(bearing) * d;
        return true;
    }

    /// <summary>
    /// True if a proposed detour keeps non-negative progress toward the
    /// target. Compares the NORMALIZED dot (cosine of the angle between the
    /// detour and target directions) against a small negative epsilon, so a
    /// pure-lateral (90°) wall-slip detour — whose cosine is 0 but rounds to
    /// a hair below it in float — is accepted, while a genuinely backward
    /// detour (cosine near -1) is rejected. All built-in offsets satisfy this
    /// by construction; this is a defensive guard for the motor to apply
    /// before committing to any detour waypoint.
    /// </summary>
    public static bool IsForwardProgress(
        float selfGx, float selfGy,
        float targetGx, float targetGy,
        float detourGx, float detourGy)
    {
        var tx = targetGx - selfGx;
        var ty = targetGy - selfGy;
        var dirx = detourGx - selfGx;
        var diry = detourGy - selfGy;
        var tlen = MathF.Sqrt(tx * tx + ty * ty);
        var dlen = MathF.Sqrt(dirx * dirx + diry * diry);
        if (tlen < 1e-6f || dlen < 1e-6f)
            return false;
        var cos = (tx * dirx + ty * diry) / (tlen * dlen);
        return cos >= -1e-4f;
    }
}
