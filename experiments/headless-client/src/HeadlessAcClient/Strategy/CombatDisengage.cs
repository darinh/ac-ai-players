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
    /// dealt zero damage across enough no-progress swings for "cannot damage" to
    /// be conclusive (not an unlucky early streak), AND its own health has fallen
    /// at least <paramref name="healthLostFraction"/> of max below this
    /// engagement's high-water mark (it is taking inbound damage it cannot
    /// answer). A no-progress swing is one the target EVADED (it reached the
    /// target, which dodged) OR one the server REFUSED (the bot could not connect
    /// at all — out of range / cannot-attack — e.g. a target it cannot reach);
    /// either, in enough volume, proves "cannot damage". This trips well BEFORE
    /// the critical low-health reflex (<see cref="ShouldDisengage"/>) so the bot
    /// flees while it still has a safety margin, instead of dying mid-swing
    /// against a foe it cannot hurt OR cannot reach.
    ///
    /// A fight in which the bot has landed ANY hit or dealt ANY damage never
    /// trips this (it is not unwinnable), and a fight in which the bot is
    /// taking no net damage never trips it either (a harmless 0-damage
    /// stalemate is a tempo concern owned by the no-damage watchdog, not a
    /// death risk). Mechanical: keys ONLY on the bot's own swing outcomes
    /// (landed / evaded / server-refused) and its own health vital — no monster
    /// KIND, name, wcid, landblock, or server text, and it never chooses a target.
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
    /// <param name="swingsRefused">
    /// Swings the SERVER refused this fight (a semantic AttackDone error such as
    /// out-of-range / cannot-attack — NOT the benign auto-repeat-loop cancel),
    /// counting consecutive refusals since the last swing that actually reached
    /// the target. A target that keeps refusing every swing cannot be connected
    /// with at all.
    /// </param>
    /// <param name="minRefusedSwings">
    /// Minimum refused-swing count before "cannot connect" is conclusive. May be
    /// LOWER than <paramref name="minEvadedSwings"/> because a refusal is stronger
    /// evidence than an evade (the swing never reached the target).
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
        int swingsLanded, uint damageDealt,
        int swingsEvaded, int minEvadedSwings,
        int swingsRefused, int minRefusedSwings,
        uint healthCurrent, uint healthMax,
        double? peakHealthFraction, double healthLostFraction)
    {
        if (!inCombat) return false;
        if (healthMax == 0u) return false;       // health not yet synced
        if (healthCurrent == 0u) return false;   // death/respawn path owns this
        // Unwinnable: zero landed, zero damage, AND enough no-progress swings to be
        // conclusive — either the target EVADED enough (it dodges every swing) OR the
        // server REFUSED enough swings (the bot cannot connect at all, e.g. a target it
        // cannot reach). A refused swing is even stronger "cannot damage" evidence than an
        // evade (the swing never landed on the target to be dodged), so its threshold can
        // be lower.
        if (swingsLanded != 0 || damageDealt != 0u
            || (swingsEvaded < minEvadedSwings && swingsRefused < minRefusedSwings))
            return false;
        // Losing: own health has dropped meaningfully below the fight's
        // high-water mark (taking inbound damage we cannot answer).
        if (peakHealthFraction is not double peak) return false;
        var current = (double)healthCurrent / healthMax;
        return (peak - current) >= healthLostFraction;
    }

    /// <summary>
    /// True when the bot should break off NOW because it is LOSING THE DAMAGE
    /// EXCHANGE — even though it is landing SOME hits. The unwinnable-losing
    /// reflex above only catches a ZERO-offense fight; this one catches the
    /// case the bot lands the occasional hit yet bleeds out far faster than the
    /// target: its OWN health has fallen at least
    /// <paramref name="selfHealthLostFraction"/> of max below this fight's
    /// high-water mark WHILE the target has lost at most
    /// <paramref name="maxTargetHealthLostFraction"/> of its health since the
    /// fight began, over a sustained run of swings. This trips EARLIER than the
    /// critical low-health reflex (<see cref="ShouldDisengage"/>) so the bot
    /// flees while it still has the HP buffer to actually escape, instead of
    /// disengaging at a hair of health and dying during the retreat.
    ///
    /// The target-health comparison is what keeps it from aborting a CLOSE but
    /// winnable trade: if the target's health is also dropping (it lost MORE
    /// than the cap) the exchange is contested, not lost, so this stays quiet
    /// and the low-health reflex owns the endgame. Target health unknown (not
    /// yet observed) ⇒ does NOT fire (conservative — cannot tell losing from
    /// trading). Mechanical: keys ONLY on the bot's own health vital, its own
    /// swing counts, and the target's OBSERVED health fraction — no monster
    /// KIND, name, wcid, landblock, or server text, and it never chooses a
    /// target. It naturally covers a vitae-weakened (low effective max HP)
    /// respawn: a fragile bot loses its HP fraction fast, so it flees fast,
    /// with NO knowledge of vitae itself.
    /// </summary>
    /// <param name="swingsLanded">Swings that landed a hit this fight.</param>
    /// <param name="swingsEvaded">Swings the target evaded this fight.</param>
    /// <param name="minSwings">
    /// Minimum total swings (landed + evaded) before the exchange verdict is
    /// conclusive, so an unlucky first exchange cannot trip it.
    /// </param>
    /// <param name="peakSelfHealthFraction">
    /// Highest self health fraction observed this engagement (high-water mark),
    /// or null if not yet sampled.
    /// </param>
    /// <param name="selfHealthLostFraction">
    /// Fraction of max health the bot must have LOST since its high-water mark.
    /// </param>
    /// <param name="targetHealthAtStart">
    /// Target health fraction when the fight began, or null if not observed.
    /// </param>
    /// <param name="targetHealthNow">
    /// Target's latest observed health fraction, or null if not observed.
    /// </param>
    /// <param name="maxTargetHealthLostFraction">
    /// The most the target may have lost (start − now) and still count as
    /// "barely scratched"; above this the fight is a contested trade, not lost.
    /// </param>
    public static bool ShouldDisengageLosingExchange(
        bool inCombat,
        int swingsLanded, int swingsEvaded, int minSwings,
        uint healthCurrent, uint healthMax,
        double? peakSelfHealthFraction, double selfHealthLostFraction,
        double? targetHealthAtStart, double? targetHealthNow,
        double maxTargetHealthLostFraction)
    {
        if (!inCombat) return false;
        if (healthMax == 0u) return false;       // health not yet synced
        if (healthCurrent == 0u) return false;   // death/respawn path owns this
        // Sustained engagement only — not the first couple swings.
        if (swingsLanded + swingsEvaded < minSwings) return false;
        // Losing: own health dropped a large fraction below the fight's
        // high-water mark.
        if (peakSelfHealthFraction is not double peak) return false;
        var selfNow = (double)healthCurrent / healthMax;
        if ((peak - selfNow) < selfHealthLostFraction) return false;
        // ...while the target is barely scratched. Without target-health
        // knowledge we cannot distinguish a losing fight from an even trade,
        // so stay quiet (the low-health reflex still backstops).
        if (targetHealthAtStart is not double tStart) return false;
        if (targetHealthNow is not double tNow) return false;
        return (tStart - tNow) <= maxTargetHealthLostFraction;
    }

    /// <summary>
    /// Combined disengage decision: returns a short reason tag for WHY the bot
    /// should break off combat this tick, or null to keep fighting. The
    /// critical low-health reflex (<see cref="ShouldDisengage"/>) takes
    /// precedence ("low-health"); otherwise the unwinnable-and-losing early
    /// flee (<see cref="ShouldDisengageUnwinnableLosing"/>) may fire
    /// ("unwinnable-losing"); otherwise the losing-exchange early flee
    /// (<see cref="ShouldDisengageLosingExchange"/>) may fire
    /// ("losing-exchange"). Pure: same inputs as the underlying decisions; the
    /// tag exists only so the caller can log which reflex fired.
    /// </summary>
    public static string? DisengageReason(
        uint healthCurrent, uint healthMax, bool inCombat,
        double disengageFraction, uint criticalHpFloor,
        int swingsLanded, uint damageDealt, int swingsEvaded, int minEvadedSwings,
        int swingsRefused, int minRefusedSwings,
        double? peakHealthFraction, double healthLostFraction,
        int losingExchangeMinSwings, double losingExchangeSelfHealthLostFraction,
        double? targetHealthAtStart, double? targetHealthNow,
        double losingExchangeMaxTargetHealthLostFraction)
    {
        if (ShouldDisengage(healthCurrent, healthMax, inCombat, disengageFraction, criticalHpFloor))
            return "low-health";
        if (ShouldDisengageUnwinnableLosing(
                inCombat, swingsLanded, damageDealt, swingsEvaded, minEvadedSwings,
                swingsRefused, minRefusedSwings,
                healthCurrent, healthMax, peakHealthFraction, healthLostFraction))
            return "unwinnable-losing";
        if (ShouldDisengageLosingExchange(
                inCombat, swingsLanded, swingsEvaded, losingExchangeMinSwings,
                healthCurrent, healthMax, peakHealthFraction,
                losingExchangeSelfHealthLostFraction,
                targetHealthAtStart, targetHealthNow,
                losingExchangeMaxTargetHealthLostFraction))
            return "losing-exchange";
        return null;
    }

    /// <summary>
    /// Default re-engage health fraction: the bot must NOT (re)start melee until
    /// its health recovers to at least this fraction of max. Strictly higher than
    /// the disengage fraction (anti-oscillation hysteresis). Shared so the Motor's
    /// dispatch REFUSE and the Strategy layer's autonomous combat-chain gate use one
    /// source of truth (a divergence would let the chain mint Attacks the Motor then
    /// refuses, looping).
    /// </summary>
    public const double DefaultReengageHealthFraction = 0.70;

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
