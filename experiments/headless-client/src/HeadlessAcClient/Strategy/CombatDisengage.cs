// SPDX-License-Identifier: AGPL-3.0-or-later
// CombatDisengage — pure self-preservation reflex helpers for breaking
// off a melee engagement when the bot's OWN health is critically low.
//
// This is a mechanical motor safety reflex, in the same family as the
// existing unconditional recovery ticks (dead-cell / soft-stuck /
// barren-area). It carries NO game knowledge: it never names a monster,
// never reads a wcid / landblock / English text, never assigns priority
// to an object type, and never CHOOSES a target. It only reacts to the
// bot's own health vital against a configurable threshold and computes a
// retreat coordinate directly away from a threat the LLM already chose
// to attack. WHAT to fight stays the LLM's decision; only "do not die
// mid-swing" is reflexive — a 3s LLM round-trip cannot save a 5-HP bot.
//
// Three pure decisions live here:
//   1. ShouldDisengage  — break off NOW (in combat + health critical).
//   2. IsCombatSuppressed — refuse to (re)engage until health recovers
//      past a higher re-engage threshold (hysteresis prevents
//      oscillation: disengage low, re-allow only once meaningfully
//      healed).
//   3. ComputeFleeDestination — a retreat point away from the threat.

using System;
using System.Numerics;

namespace HeadlessAcClient.Strategy;

internal static class CombatDisengage
{
    /// <summary>
    /// True when the bot should break off the current melee engagement
    /// because its own health is critically low. Fires only while
    /// actively in combat. Uses BOTH a fraction-of-max threshold and an
    /// absolute HP floor, because a low-level character has so few max
    /// HP that a fraction alone can round below a single hit (e.g. 35%
    /// of 5 max = 1.75 HP — already one hit from death); the absolute
    /// floor gives the reflex a chance to fire one hit earlier.
    /// </summary>
    /// <param name="healthCurrent">Current health points.</param>
    /// <param name="healthMax">Maximum health points.</param>
    /// <param name="inCombat">True when a combat target is locked.</param>
    /// <param name="disengageFraction">
    /// Fraction of max health at or below which to disengage (0..1).
    /// </param>
    /// <param name="criticalHpFloor">
    /// Absolute HP at or below which to disengage regardless of fraction.
    /// </param>
    public static bool ShouldDisengage(
        uint healthCurrent, uint healthMax, bool inCombat,
        double disengageFraction, uint criticalHpFloor)
    {
        if (!inCombat) return false;
        // Unknown health (not yet synced) — do not act on garbage.
        if (healthMax == 0u) return false;
        // Already at zero: the death/respawn path owns this, not flee.
        if (healthCurrent == 0u) return false;
        if (healthCurrent <= criticalHpFloor) return true;
        return healthCurrent <= healthMax * disengageFraction;
    }

    /// <summary>
    /// While true, the motor must NOT dispatch a new melee Attack — the
    /// bot is too hurt to safely (re)engage. Returns false once health
    /// has recovered to at least the re-engage fraction of max. The
    /// re-engage fraction should be strictly higher than the disengage
    /// fraction so a bot that heals just past the disengage point does
    /// not immediately re-engage and drop back below it (anti-oscillation
    /// hysteresis). Pure self-state; no target involved.
    /// </summary>
    public static bool IsCombatSuppressed(
        uint healthCurrent, uint healthMax, double reengageFraction)
    {
        if (healthMax == 0u) return false;
        return healthCurrent < healthMax * reengageFraction;
    }

    /// <summary>
    /// Compute a retreat destination: a point <paramref name="fleeDistance"/>
    /// units from the bot, in the horizontal (XY) direction pointing from
    /// the threat toward the bot (i.e. directly away from the threat). Z
    /// is preserved — the walk-tick / MeleeApproachZ owns vertical motion.
    /// If the bot and threat coincide in XY (degenerate), falls back to a
    /// fixed +X direction so the bot still moves rather than standing
    /// still under aggro.
    /// </summary>
    public static Vector3 ComputeFleeDestination(
        Vector3 selfPos, Vector3 threatPos, float fleeDistance)
    {
        var dx = selfPos.X - threatPos.X;
        var dy = selfPos.Y - threatPos.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f)
        {
            dx = 1f;
            dy = 0f;
            len = 1f;
        }
        var ux = dx / len;
        var uy = dy / len;
        return new Vector3(
            selfPos.X + ux * fleeDistance,
            selfPos.Y + uy * fleeDistance,
            selfPos.Z);
    }
}
