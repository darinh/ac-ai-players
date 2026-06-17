// SPDX-License-Identifier: AGPL-3.0-or-later
// CombatDeathAttribution — pure helpers for deciding whether the bot's own
// death should be recorded against the monster KIND it was last fighting.
//
// This is mechanical bookkeeping the LLM cannot do for itself: it has no
// cross-tick memory, so source must bridge the gap between "we were
// fighting foe X" and a self-death that arrives a few ticks later (after the
// disengage reflex has already cleared the live combat lock). It carries NO
// game knowledge: it never names a monster, never assigns priority, never
// reads English text or landblock IDs, and never decides WHAT to fight. It
// only answers two value-free questions:
//
//   1. SignalMatchesFoe — does an observed combat signal refer to the SAME
//      monster KIND we are tracking for death attribution? (generic wcid /
//      normalized-name identity match only.)
//   2. IsFresh — is a self-death recent enough (relative to the last
//      confirmed combat moment with that foe) to be that foe's doing?
//
// Why this exists: the death-attribution freshness timestamp was previously
// stamped ONLY on the bot's sparse OUTBOUND swing commands. With the server
// driving the auto-repeat swing loop, a long evade-heavy fight sends few
// outbound swings, so the freshness window expired before a flee-then-die
// death and the strongest learned-avoidance signal (an actual DEATH) was
// silently dropped. Refreshing the window on observed (server-driven) combat
// signals naming the same foe keeps attribution honest.

using System;
using HeadlessAcClient.World; // (kept for parity with sibling helpers)

namespace HeadlessAcClient.Strategy;

internal static class CombatDeathAttribution
{
    /// <summary>
    /// How recent the last confirmed combat moment with a foe must be for a
    /// self-death to be attributed to it. A flee-then-die death almost always
    /// lands within seconds of the last swing; this bound keeps a stale or
    /// unrelated later death (e.g. a fall minutes afterward) from poisoning
    /// the ledger with the wrong identity.
    /// </summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromSeconds(12);

    /// <summary>
    /// True when an observed combat signal naming
    /// (<paramref name="observedWcid"/>, <paramref name="observedName"/>)
    /// refers to the SAME monster kind currently tracked for death
    /// attribution (<paramref name="foeWcid"/>, <paramref name="foeName"/>).
    ///
    /// Identity only — no monster-specific rules:
    ///   - if BOTH sides carry a usable wcid, they must be equal (wcid is the
    ///     definitive kind key; equal-name-but-different-wcid is a different
    ///     kind and must NOT match);
    ///   - otherwise fall back to normalized-name equality (same normalization
    ///     the ledger keys by, so the unmatchable "(unknown)" fallback can
    ///     never produce a spurious match).
    /// Returns false when neither identity channel can be compared.
    /// </summary>
    public static bool SignalMatchesFoe(
        uint? foeWcid, string? foeName, uint? observedWcid, string? observedName)
    {
        bool foeHasWcid = foeWcid is uint fw0 && fw0 != 0u;
        bool obsHasWcid = observedWcid is uint ow0 && ow0 != 0u;
        if (foeHasWcid && obsHasWcid)
            return foeWcid!.Value == observedWcid!.Value;

        var fn = CombatFeelLedger.NormalizeName(foeName);
        var on = CombatFeelLedger.NormalizeName(observedName);
        return fn is not null && on is not null && fn == on;
    }

    /// <summary>
    /// True when a self-death observed at <paramref name="now"/> is recent
    /// enough (within <paramref name="freshness"/>) of the last confirmed
    /// combat moment <paramref name="foeAt"/> to attribute to that foe.
    /// </summary>
    public static bool IsFresh(DateTime foeAt, DateTime now, TimeSpan freshness)
        => now - foeAt < freshness;

    /// <summary>
    /// Choose the foe identity to record a self-death against, or null to leave
    /// the ledger untouched. Prefers the foe the bot was actively fighting
    /// (<paramref name="lastCombatFoe"/>) when that engagement is fresh and the
    /// identity resolves; otherwise falls back to the foe that most recently
    /// LANDED damage on the bot (<paramref name="lastDamager"/>) when fresh and
    /// resolvable.
    ///
    /// Why the fallback: the bot is frequently killed by a DIFFERENT foe than the
    /// one it was swinging at — a swarm add, or a creature that aggroed mid-travel
    /// — so <paramref name="lastCombatFoe"/> is stale or names the wrong (weaker)
    /// kind at the moment of death and the lethal kind is never learned. The
    /// most-recent foe to LAND damage is the best-supported single candidate: it
    /// dealt actual damage within the freshness window before death. Both anchors
    /// are gated on IsFresh + a resolvable identity (KeyOf), so a stale or
    /// unidentifiable foe never poisons the ledger. Pure identity bookkeeping —
    /// no monster names, priorities, or game rules.
    /// </summary>
    public static CombatFeelLedger.MobIdentity? ChooseDeathFoe(
        (uint? Wcid, string? Name, DateTime At)? lastCombatFoe,
        (uint? Wcid, string? Name, DateTime At)? lastDamager,
        DateTime now, TimeSpan freshness)
    {
        if (Attributable(lastCombatFoe, now, freshness) is { } engaged) return engaged;
        if (Attributable(lastDamager, now, freshness) is { } damager) return damager;
        return null;
    }

    private static CombatFeelLedger.MobIdentity? Attributable(
        (uint? Wcid, string? Name, DateTime At)? anchor, DateTime now, TimeSpan freshness)
    {
        if (anchor is not { } a) return null;
        if (!IsFresh(a.At, now, freshness)) return null;
        var id = new CombatFeelLedger.MobIdentity(a.Wcid, a.Name);
        return CombatFeelLedger.KeyOf(id) is null ? null : id;
    }
}
