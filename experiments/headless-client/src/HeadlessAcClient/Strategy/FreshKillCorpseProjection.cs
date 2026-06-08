// SPDX-License-Identifier: AGPL-3.0-or-later
// FreshKillCorpseProjection — surface the bot's OWN fresh, unlooted kill
// corpses to the LLM so a kill is followed by looting (the picker abandons a
// corpse after a short wait that the multi-second LLM decision latency outruns,
// so fresh kills go unlooted; the hunt-excursion intent then re-drives the bot
// away). Pure mechanical perception over the bot's OWN kill record + the wire
// Corpse flag + the bot's OWN opened-corpse set — no hardcoded object names, no
// priority. Strategy still decides WHETHER to loot.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

internal sealed record FreshKillCorpse(string Name, uint Guid, float Distance);

internal static class FreshKillCorpseProjection
{
    // A visible object is a "fresh own-kill corpse to loot" when ALL hold:
    //   - it carries the wire Corpse flag (ObjectDescriptionFlag bit 0x2000);
    //   - the bot has NOT opened it (its guid is not flagged by isCorpseOpened,
    //     set when the bot dispatches a Use against a corpse);
    //   - it sits within killMatchRadiusUnits of a position where the bot KILLED
    //     a creature within the recency window — a creature corpse replaces the
    //     creature in place, so a corpse at the bot's OWN kill site is the bot's
    //     own kill (proximity correlation, robust to creature-name overlaps);
    //   - it is within maxDistanceUnits of the bot (actionable — the bot can
    //     walk back to loot it).
    // Returns the nearest maxResults, nearest-first. No name parsing, no
    // hardcoded prefix, no game-knowledge value judgment — Strategy decides
    // whether to loot via the existing prompt rules.
    public static IReadOnlyList<FreshKillCorpse> Compute(
        IEnumerable<WorldObjectSnapshot> visible,
        WorldObjectSnapshot self,
        IReadOnlyCollection<RecentKill> recentKills,
        Func<uint, bool> isCorpseOpened,
        DateTimeOffset now,
        TimeSpan recencyWindow,
        float killMatchRadiusUnits,
        float maxDistanceUnits,
        int maxResults)
    {
        if (visible is null || self is null || recentKills is null || isCorpseOpened is null)
            return Array.Empty<FreshKillCorpse>();

        var freshKills = recentKills
            .Where(k => now - k.At <= recencyWindow)
            .ToList();
        if (freshKills.Count == 0) return Array.Empty<FreshKillCorpse>();

        var matchRadiusSq = killMatchRadiusUnits * killMatchRadiusUnits;
        const uint CorpseFlag = (uint)ObjectDescriptionFlag.Corpse;
        var matched = new List<FreshKillCorpse>();
        foreach (var v in visible)
        {
            if (v is null) continue;
            if (((v.ObjectDescriptionFlags ?? 0u) & CorpseFlag) == 0) continue;
            if (isCorpseOpened(v.Guid)) continue;
            if (string.IsNullOrEmpty(v.Name)) continue;
            if (v.CellId is not uint vcell) continue;

            var (cgx, cgy) = AcCoords.ToGlobalXY(vcell, v.Position);
            var nearAKill = freshKills.Any(k =>
            {
                var dx = cgx - k.GlobalX;
                var dy = cgy - k.GlobalY;
                return dx * dx + dy * dy <= matchRadiusSq;
            });
            if (!nearAKill) continue;

            if (!WorldDistance.TrySelectionSquaredDistance(self, v, out var d2)) continue;
            var dist = (float)Math.Sqrt(d2);
            if (dist > maxDistanceUnits) continue;
            matched.Add(new FreshKillCorpse(v.Name!, v.Guid, dist));
        }

        return matched
            .OrderBy(c => c.Distance)
            .Take(maxResults)
            .ToList();
    }
}

// A kill the bot made, kept briefly (global-XY of the kill site + time) so a
// freshly-spawned corpse at that site can be correlated to the bot's OWN kill
// by proximity + recency. Pure bookkeeping over the bot's OWN outcome.
internal readonly record struct RecentKill(float GlobalX, float GlobalY, DateTimeOffset At);
