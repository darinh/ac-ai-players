// SPDX-License-Identifier: AGPL-3.0-or-later
// SightedTargetResolver — consumer half of FOV discovery (slice 4).
//
// When the live worldState cannot satisfy a goal's Target (the entity is
// out of the bot's current view), the bot can still recall WHERE it last
// saw a matching entity from its per-bot SightedLocation memory and walk
// back toward those coordinates. This class performs that lookup.
//
// It is deliberately MECHANICAL resolution of an LLM-specified selector,
// not autonomous targeting: it filters remembered sightings by the
// selector fields it can verify against a sighting (Name, NameContains,
// Wcid), scoped to the bot's current landblock, and breaks ties by
// most-recently-seen. It assigns NO priority to object types and
// hardcodes NO names/wcids/landblocks — the "what" always comes from the
// LLM's Goal.Target.

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal static class SightedTargetResolver
{
    /// <summary>
    /// Returns the best same-landblock remembered match for
    /// <paramref name="selector"/>, or null when there is no match.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Selector.Name"/>, <see cref="Selector.NameContains"/>
    /// and <see cref="Selector.Wcid"/> are verifiable against a
    /// <see cref="SightedLocation"/>; a sighting carries no ItemType or
    /// ShortDesc, and its <see cref="SightedLocation.Id"/> is not a live
    /// object guid. If the selector populates none of the supported
    /// fields, this declines (returns null) rather than guess. All
    /// populated supported fields must match (AND). Cross-landblock
    /// sightings are excluded — there is no inter-landblock travel
    /// executor yet.
    /// </remarks>
    /// <param name="sighted">Snapshot of remembered sighted locations.</param>
    /// <param name="selector">The LLM-specified target selector.</param>
    /// <param name="currentCellId">The bot's current cell id; only
    /// sightings in the same landblock (upper 16 bits) are considered.</param>
    /// <param name="excluded">SightedLocation ids to skip (e.g. on
    /// revisit cooldown). May be null.</param>
    public static SightedLocation? Resolve(
        IReadOnlyList<SightedLocation> sighted,
        Selector selector,
        uint currentCellId,
        IReadOnlySet<Guid>? excluded = null)
    {
        if (sighted is null || sighted.Count == 0 || selector is null)
            return null;

        var hasName = !string.IsNullOrWhiteSpace(selector.Name);
        var hasNameContains = !string.IsNullOrWhiteSpace(selector.NameContains);
        var hasWcid = selector.Wcid is not null;
        if (!hasName && !hasNameContains && !hasWcid)
            return null;

        var landblock = currentCellId & 0xFFFF0000u;

        SightedLocation? best = null;
        foreach (var s in sighted)
        {
            if (excluded is not null && excluded.Contains(s.Id)) continue;
            if ((s.CellId & 0xFFFF0000u) != landblock) continue;
            if (hasName &&
                !string.Equals(s.Name, selector.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (hasNameContains &&
                (s.Name is null ||
                 !s.Name.Contains(selector.NameContains!, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (hasWcid && s.Wcid != selector.Wcid) continue;

            if (best is null || s.LastSeenUtc > best.LastSeenUtc)
                best = s;
        }
        return best;
    }
}
