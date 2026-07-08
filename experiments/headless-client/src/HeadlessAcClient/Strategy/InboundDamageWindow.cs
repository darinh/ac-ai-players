using System;
using System.Collections.Generic;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// active-combat-telemetry: one inbound hit the bot TOOK — the UTC time it
/// landed and the damage amount, decoded from a GameEventDefenderNotification
/// (0x01B2). Raw bookkeeping the Motor (HandshakeDriver) keeps in a short
/// rolling window so the "recent inbound damage" prompt line survives a flee,
/// when the per-fight <see cref="CombatFightStatus"/> is cleared at the
/// disengage reflex.
/// </summary>
internal readonly record struct InboundHit(DateTime At, uint Damage);

/// <summary>
/// active-combat-telemetry: pure helper that evicts stale inbound hits from a
/// rolling window and summarizes what remains. The summary is surfaced to the
/// LLM verbatim in the "## Combat readiness" prompt section as RAW perception
/// (how many hits the bot took and the total damage in the last few seconds)
/// so it can judge whether it is losing a fight and decide to disengage or
/// Recall. Source counts and sums only — it assigns no danger label and makes
/// no fight-vs-flee decision (that is the LLM's call, per the COMBAT SAFETY
/// rule). The window length is an eviction TTL (bookkeeping), not a game
/// constant: it carries no game-specific meaning.
/// </summary>
internal static class InboundDamageWindow
{
    /// <summary>
    /// Remove hits older than <paramref name="windowSeconds"/> from
    /// <paramref name="hits"/> (in place, oldest-first eviction) relative to
    /// <paramref name="now"/>, then summarize the survivors. Returns
    /// <c>null</c> when no hit remains in the window (so the prompt section is
    /// omitted entirely — zero static-floor cost when the bot is not under
    /// attack).
    /// </summary>
    internal static RecentInboundDamage? PruneAndSummarize(
        List<InboundHit> hits, DateTime now, double windowSeconds)
    {
        if (hits is null)
            return null;
        if (hits.Count > 0)
            hits.RemoveAll(h => (now - h.At).TotalSeconds > windowSeconds);
        if (hits.Count == 0)
            return null;
        uint total = 0;
        foreach (var h in hits)
            total += h.Damage;
        return new RecentInboundDamage(hits.Count, total, windowSeconds);
    }

    /// <summary>
    /// inbound-damage-onset-wake: decide whether a newly-landed inbound hit at
    /// <paramref name="hitUtc"/> BEGINS a new inbound-damage episode that
    /// warrants one structural LLM wake. An episode begins on the first hit
    /// ever (<paramref name="previousHitUtc"/> is <c>null</c>) or on the first
    /// hit after a lull of at least <paramref name="windowSeconds"/> since the
    /// previous inbound hit. Within a continuous fight (hits closer together
    /// than the window) only the first hit begins an episode, so the Motor
    /// wakes the LLM exactly once per episode. This is a hit-lull bookkeeping
    /// gate, NOT an HP/damage magnitude threshold — source assigns no
    /// materiality band (cp-2280); WHAT to do about the damage stays the LLM's
    /// call. <paramref name="previousHitUtc"/> is the time of the most recent
    /// prior inbound hit still tracked in the rolling window (the window is
    /// cleared on landblock change, so a fresh area re-arms naturally).
    /// </summary>
    internal static bool BeginsNewInboundEpisode(
        DateTime? previousHitUtc, DateTime hitUtc, double windowSeconds)
        => previousHitUtc is not DateTime prev
           || (hitUtc - prev).TotalSeconds >= windowSeconds;

    /// <summary>
    /// Decide whether a newly-landed inbound hit warrants emitting a fresh
    /// <c>InboundDamageTaken</c> event. Emit on a new hit-lull EPISODE (<see
    /// cref="BeginsNewInboundEpisode"/>) OR when the (normalized) attacker
    /// changes from the one the last event was emitted for. The latter is what
    /// surfaces a FOREIGN / additional attacker that joins DURING an active
    /// episode (no lull) — without it the swarm-add would never wake the LLM,
    /// since episode dedup alone coalesces all mid-fight hits into one event.
    /// Same-attacker continuous hits still coalesce (episode false + attacker
    /// unchanged). Callers pass attacker keys already normalized (e.g. via
    /// CombatFeelLedger.NormalizeName) so this stays a pure, dependency-free
    /// decision. An unknown current attacker (<c>null</c> key) never forces an
    /// attacker-change emit; only the episode gate can emit for it.
    /// </summary>
    internal static bool ShouldEmitInboundDamageEvent(
        DateTime? previousHitUtc, DateTime hitUtc, double windowSeconds,
        string? currentAttackerNorm, string? lastEmittedAttackerNorm)
        => BeginsNewInboundEpisode(previousHitUtc, hitUtc, windowSeconds)
           || (currentAttackerNorm is not null
               && !string.Equals(currentAttackerNorm, lastEmittedAttackerNorm, StringComparison.Ordinal));
}
