using System;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Pure helpers for outdoor melee-approach vertical (Z) convergence.
///
/// Outdoor self Z is effectively client-authoritative — the motor's
/// walk-tick steps in XY and PRESERVES the bot's Z (it samples no
/// terrain height), and the server adopts whatever Z the
/// AutonomousPosition claims outdoors. On flat indoor cells self and
/// target share a Z so this never mattered, but outdoors a target on
/// elevated terrain reports its true surface Z while the bot stays
/// frozen below it. The melee approach stops on a 2D XY radius, so the
/// bot "arrives" ~1u away in XY yet far away in 3D and its 3D-gated
/// melee never connects.
///
/// These helpers compute, with NO game knowledge (no names / wcids /
/// landblocks / per-type rules), how to step the claimed Z toward the
/// already-selected target's surface Z. The strategy/LLM has already
/// chosen the Attack target; this is purely the 3D form of "reach the
/// coordinate that was chosen".
/// </summary>
internal static class MeleeApproachZ
{
    /// <summary>
    /// True when the motor should converge the bot's claimed Z toward
    /// the target's Z this tick: an outdoor-to-outdoor Attack approach
    /// (not an intermediate waypoint) whose vertical gap exceeds the
    /// tolerance. Encodes no object-type knowledge — only the already-
    /// chosen goal kind, the cells' indoor/outdoor bit, and geometry.
    /// </summary>
    public static bool ShouldConverge(
        bool aimingAtWaypoint,
        bool isAttackGoal,
        uint selfCell,
        uint targetCell,
        float selfZ,
        float targetZ,
        float toleranceUnits)
    {
        if (aimingAtWaypoint || !isAttackGoal) return false;
        if (AcCoords.IsIndoor(selfCell) || AcCoords.IsIndoor(targetCell)) return false;
        return MathF.Abs(targetZ - selfZ) > toleranceUnits;
    }

    /// <summary>
    /// Step the claimed Z toward the target Z by at most
    /// <paramref name="maxZStep"/> (the caller clamps this to walk
    /// speed so vertical velocity never trips the server z-jump
    /// anti-cheat). Never overshoots the target Z; a non-positive
    /// step preserves the current Z.
    /// </summary>
    public static float StepToward(float selfZ, float targetZ, float maxZStep)
    {
        if (maxZStep <= 0f) return selfZ;
        var dz = targetZ - selfZ;
        return selfZ + MathF.Sign(dz) * MathF.Min(MathF.Abs(dz), maxZStep);
    }
}
