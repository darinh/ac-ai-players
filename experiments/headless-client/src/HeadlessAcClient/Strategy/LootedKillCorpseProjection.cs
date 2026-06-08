// SPDX-License-Identifier: AGPL-3.0-or-later
// LootedKillCorpseProjection — the bot's OWN kill corpses it opened and the
// loot system reported empty (no un-visited contents remained inside). Kept
// briefly (guid -> name + time) and projected most-recent-first.
//
// Why: when an opened corpse holds nothing the loot pre-empt reports "no
// un-visited contents remain" and stops tracking it, but the corpse can keep
// surfacing by name in recency sections of the prompt with no looted outcome
// attached. Projecting the observed empty-loot result lets that fact be
// rendered ("## Already looted") in the most decision-proximate slot.
//
// Audit note: PERCEPTION over the bot's OWN loot outcome. Each record is a
// guid + the corpse's wire-decoded display name + the time the loot system
// found it empty. No priority, no urgency, no game knowledge — Strategy still
// decides what to do next.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HeadlessAcClient.Strategy;

internal sealed record LootedCorpse(string Name, uint Guid);

internal static class LootedKillCorpseProjection
{
    // Project the bot's recently-emptied own-kill corpses (guid -> name + time)
    // into the most-recent-first list the prompt surfaces. Stale entries (older
    // than recencyWindow) are dropped so the signal reflects only corpses the
    // bot is plausibly still standing near / still referencing in its recent
    // emissions. Keyed by guid, so two distinct emptied corpses that share a
    // name are kept separately (the suppression set needs every guid); the
    // render dedupes by name and disambiguates against fresh unlooted kills.
    public static IReadOnlyList<LootedCorpse> Compute(
        IReadOnlyDictionary<uint, (string Name, DateTimeOffset At)> emptiedCorpses,
        DateTimeOffset now,
        TimeSpan recencyWindow,
        int maxResults)
    {
        if (emptiedCorpses is null || emptiedCorpses.Count == 0)
            return Array.Empty<LootedCorpse>();

        return emptiedCorpses
            .Where(kv => now - kv.Value.At <= recencyWindow && !string.IsNullOrEmpty(kv.Value.Name))
            .OrderByDescending(kv => kv.Value.At)
            .Take(maxResults)
            .Select(kv => new LootedCorpse(kv.Value.Name, kv.Key))
            .ToList();
    }
}
