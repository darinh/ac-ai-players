// SPDX-License-Identifier: AGPL-3.0-or-later
// CorpseRecovery — the bot's last death location record plus the pure
// render-gate predicate and env TTL that decide when it is projected. No
// decision and no game content live here.

using System;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Last death location. <see cref="WorldX"/>/<see cref="WorldY"/> are global
/// meters (the frame <see cref="AcCoords.ToGlobalXY(uint, float, float)"/>
/// produces); <see cref="Landblock"/> is <c>cellId &gt;&gt; 16</c>;
/// <see cref="At"/> is when the death was detected.
/// </summary>
internal readonly record struct DeathLocation(float WorldX, float WorldY, uint Landblock, DateTimeOffset At);

internal static class CorpseRecovery
{
    /// <summary>Age cap after which a recorded death location is no longer
    /// surfaced. Tunable via AC_BOTS_CORPSE_TTL_SECONDS.</summary>
    internal static readonly TimeSpan CorpseTtl =
        ResolveCorpseTtl(Environment.GetEnvironmentVariable("AC_BOTS_CORPSE_TTL_SECONDS"));

    // Parse AC_BOTS_CORPSE_TTL_SECONDS. A positive integer of seconds is used
    // (clamped to [60, 3600]); anything else (unset/blank/unparseable/<60) falls
    // back to 600.
    internal static TimeSpan ResolveCorpseTtl(string? envValue)
    {
        const int DefaultSeconds = 600;
        const int Min = 60;
        const int Max = 3600;
        if (int.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= Min)
            return TimeSpan.FromSeconds(Math.Min(v, Max));
        return TimeSpan.FromSeconds(DefaultSeconds);
    }

    /// <summary>
    /// Radius (game units) within which the bot is treated as having REACHED its
    /// corpse: inside it the return bearing is suppressed and the recorded death
    /// location is cleared. Deliberately TIGHT — far smaller than the 120u
    /// perception radius — so the bearing keeps directing the bot toward an
    /// out-of-view corpse across the whole approach, and a transient pass (or a
    /// respawn near the recorded death location) within perception range does NOT
    /// permanently clear the death location. Once the corpse enters perception, the
    /// in-view cue takes over.
    /// </summary>
    internal const float CorpseReachedRadiusUnits = 10f;

    /// <summary>
    /// True when a recorded death location should render: it is present, its age
    /// is in <c>[0, ttl)</c>, and the bot (<paramref name="currentXY"/>) is NOT
    /// within <paramref name="visibleRadius"/> of it. A null
    /// <paramref name="currentXY"/> (position unknown) renders. Pure predicate.
    /// </summary>
    internal static bool ShouldSurfaceCorpse(
        DeathLocation? death, (float Gx, float Gy)? currentXY, TimeSpan age, TimeSpan ttl, float visibleRadius)
    {
        if (death is not { } d) return false;
        if (age < TimeSpan.Zero || age >= ttl) return false;
        if (currentXY is { } c && WithinReach(c, d, visibleRadius)) return false;
        return true;
    }

    /// <summary>True when <paramref name="currentXY"/> is within
    /// <paramref name="radius"/> meters of <paramref name="death"/> (squared
    /// compare). Pure geometry.</summary>
    internal static bool WithinReach((float Gx, float Gy) currentXY, DeathLocation death, float radius)
    {
        var dx = currentXY.Gx - death.WorldX;
        var dy = currentXY.Gy - death.WorldY;
        return (dx * dx + dy * dy) <= radius * radius;
    }
}
