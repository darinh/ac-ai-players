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
}
