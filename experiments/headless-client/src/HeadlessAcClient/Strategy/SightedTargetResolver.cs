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
    /// <param name="attackableOnly">When true, only a sighting whose wire
    /// classification is an attackable creature (<see cref="EntityKind.Mob"/>)
    /// may match — used by the Attack path so a combat verb never binds a
    /// non-creature (an item whose name merely contains a selector word) or a
    /// non-attackable creature. The Explore path leaves this false (any
    /// remembered location is a valid walk destination).</param>
    public static SightedLocation? Resolve(
        IReadOnlyList<SightedLocation> sighted,
        Selector selector,
        uint currentCellId,
        IReadOnlySet<Guid>? excluded = null,
        bool attackableOnly = false)
        => ResolveCore(sighted, selector, currentCellId, sameLandblock: true, excluded, attackableOnly);

    /// <summary>
    /// Returns the best remembered match for <paramref name="selector"/>
    /// in a landblock OTHER than the bot's current one, or null. Same
    /// mechanical selector matching as <see cref="Resolve"/>; this is the
    /// entry point a route-guided navigation consumer uses to head toward
    /// a target last seen elsewhere in the world over the bot's own
    /// explored connectivity. Most-recently-seen wins on ties.
    /// </summary>
    public static SightedLocation? ResolveCrossLandblock(
        IReadOnlyList<SightedLocation> sighted,
        Selector selector,
        uint currentCellId,
        IReadOnlySet<Guid>? excluded = null,
        bool attackableOnly = false)
        => ResolveCore(sighted, selector, currentCellId, sameLandblock: false, excluded, attackableOnly);

    /// <summary>
    /// True when a sighting of <paramref name="kind"/> is a valid out-of-view
    /// ATTACK-steer destination — a wire-classified attackable monster
    /// (<see cref="EntityKind.Mob"/>). A sighting carries only the coarse
    /// EntityKind (no raw Attackable bit), and this path STEERS the bot toward
    /// a remembered target to HUNT it; a non-creature sighting (an item / place
    /// → <see cref="EntityKind.Unknown"/>) or a non-monster creature (NPC /
    /// vendor / healer → <see cref="EntityKind.NPC"/>) is not a hunt
    /// destination, so routing an out-of-view Attack toward it only walks the
    /// bot to coordinates where no attack can land. (Self-defense against a
    /// hostile creature is an IN-VIEW event resolved by the live
    /// SelectorResolver, which uses the finer raw Attackable bit and so is
    /// unaffected by this out-of-view hunt-steer.) A player sighting is
    /// <see cref="EntityKind.Unknown"/> (guid-aware) and thus excluded too.
    /// Pure wire classification; assigns no priority and hardcodes no names.
    /// </summary>
    internal static bool IsAttackableSightingKind(EntityKind kind)
        => kind == EntityKind.Mob;

    /// <summary>
    /// Shared matcher. <paramref name="sameLandblock"/> selects whether a
    /// candidate must be in the bot's current landblock (true) or in any
    /// OTHER landblock (false). All other semantics are identical.
    /// </summary>
    private static SightedLocation? ResolveCore(
        IReadOnlyList<SightedLocation> sighted,
        Selector selector,
        uint currentCellId,
        bool sameLandblock,
        IReadOnlySet<Guid>? excluded,
        bool attackableOnly = false)
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
            var inSameLandblock = (s.CellId & 0xFFFF0000u) == landblock;
            if (inSameLandblock != sameLandblock) continue;
            if (hasName &&
                !string.Equals(s.Name, selector.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (hasNameContains &&
                (s.Name is null ||
                 !s.Name.Contains(selector.NameContains!, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (hasWcid && s.Wcid != selector.Wcid) continue;
            // An Attack destination must be an attackable creature. A sighting
            // can match the selector by NAME yet be a non-creature (an item
            // whose name merely contains a selector word) or a non-attackable
            // creature; routing an Attack toward it only walks the bot to
            // coordinates where nothing can be attacked. Pure wire
            // classification; the LLM still chose the selector.
            if (attackableOnly && !IsAttackableSightingKind(s.Kind)) continue;

            if (best is null || s.LastSeenUtc > best.LastSeenUtc)
                best = s;
        }
        return best;
    }
}
