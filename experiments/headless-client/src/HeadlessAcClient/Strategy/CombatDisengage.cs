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
    /// True when the bot should break off NOW because the current engagement
    /// is BOTH unwinnable AND costing health: it has landed zero hits and
    /// dealt zero damage across enough swings for "cannot damage" to be
    /// conclusive (not an unlucky early streak), AND its own health has fallen
    /// at least <paramref name="healthLostFraction"/> of max below this
    /// engagement's high-water mark (it is taking inbound damage it cannot
    /// answer). This trips well BEFORE the critical low-health reflex
    /// (<see cref="ShouldDisengage"/>) so the bot flees while it still has a
    /// safety margin, instead of dying mid-swing against a foe it cannot hurt.
    ///
    /// A fight in which the bot has landed ANY hit or dealt ANY damage never
    /// trips this (it is not unwinnable), and a fight in which the bot is
    /// taking no net damage never trips it either (a harmless 0-damage
    /// stalemate is a tempo concern owned by the no-damage watchdog, not a
    /// death risk). Mechanical: keys ONLY on the bot's own swing outcomes and
    /// its own health vital — no monster KIND, name, wcid, landblock, or
    /// server text, and it never chooses a target.
    /// </summary>
    /// <param name="inCombat">True when a combat target is locked.</param>
    /// <param name="swingsLanded">Swings that landed a hit this fight.</param>
    /// <param name="damageDealt">Total damage dealt to the target this fight.</param>
    /// <param name="swingsEvaded">Swings the target evaded this fight.</param>
    /// <param name="minEvadedSwings">
    /// Minimum all-evaded swing count (zero landed, zero damage) before
    /// "cannot damage" is conclusive. May be lower than the no-damage
    /// abandon's count because this reflex ALSO requires active health loss.
    /// </param>
    /// <param name="healthCurrent">Current health points.</param>
    /// <param name="healthMax">Maximum health points.</param>
    /// <param name="peakHealthFraction">
    /// The highest self health fraction observed during THIS engagement (its
    /// high-water mark), or null if not yet sampled. Health LOST is measured
    /// against this so a fight entered already below full still counts only
    /// the damage taken since the engagement began.
    /// </param>
    /// <param name="healthLostFraction">
    /// Fraction of max health that must have been LOST since the high-water
    /// mark before fleeing an unwinnable fight.
    /// </param>
    public static bool ShouldDisengageUnwinnableLosing(
        bool inCombat,
        int swingsLanded, uint damageDealt, int swingsEvaded, int minEvadedSwings,
        uint healthCurrent, uint healthMax,
        double? peakHealthFraction, double healthLostFraction)
    {
        if (!inCombat) return false;
        if (healthMax == 0u) return false;       // health not yet synced
        if (healthCurrent == 0u) return false;   // death/respawn path owns this
        // Unwinnable: zero landed, zero damage, enough all-evaded swings.
        if (swingsLanded != 0 || damageDealt != 0u || swingsEvaded < minEvadedSwings)
            return false;
        // Losing: own health has dropped meaningfully below the fight's
        // high-water mark (taking inbound damage we cannot answer).
        if (peakHealthFraction is not double peak) return false;
        var current = (double)healthCurrent / healthMax;
        return (peak - current) >= healthLostFraction;
    }

    /// <summary>
    /// Combined disengage decision: returns a short reason tag for WHY the bot
    /// should break off combat this tick, or null to keep fighting. The
    /// critical low-health reflex (<see cref="ShouldDisengage"/>) takes
    /// precedence ("low-health"); otherwise the unwinnable-and-losing early
    /// flee (<see cref="ShouldDisengageUnwinnableLosing"/>) may fire
    /// ("unwinnable-losing"). Pure: same inputs as the two underlying
    /// decisions; the tag exists only so the caller can log which reflex
    /// fired.
    /// </summary>
    public static string? DisengageReason(
        uint healthCurrent, uint healthMax, bool inCombat,
        double disengageFraction, uint criticalHpFloor,
        int swingsLanded, uint damageDealt, int swingsEvaded, int minEvadedSwings,
        double? peakHealthFraction, double healthLostFraction)
    {
        if (ShouldDisengage(healthCurrent, healthMax, inCombat, disengageFraction, criticalHpFloor))
            return "low-health";
        if (ShouldDisengageUnwinnableLosing(
                inCombat, swingsLanded, damageDealt, swingsEvaded, minEvadedSwings,
                healthCurrent, healthMax, peakHealthFraction, healthLostFraction))
            return "unwinnable-losing";
        return null;
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
