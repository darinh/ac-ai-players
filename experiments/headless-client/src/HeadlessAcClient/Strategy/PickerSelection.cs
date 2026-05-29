// SPDX-License-Identifier: AGPL-3.0-or-later
// PickerSelection — Slice W.1 (ac-ai-players#86).
//
// The schema-only picker that drives the bot between LLM
// deliberations. This used to encode a type-based PRIORITY LADDER
// (NPC > corpse > door > pickup > else) plus a corpse-loot bump,
// which the audit at .github/skills/audit-hardcoded-knowledge/
// SKILL.md flagged as game-knowledge: the ranking "NPCs are more
// important than corpses are more important than doors" is a
// STRATEGIC judgement, not a mechanical one, and strategic choices
// belong in the LLM.
//
// W.1 removes the in-range ladder. The picker now selects the
// nearest mechanically-eligible candidate, period. Strategy is
// the LLM's job (see ac-ai-players#86); the picker's job is to
// give the bot something near to look at when the LLM hasn't
// emitted a goal yet. The LLM sees what the picker is doing via
// Slice V's "## Autonomous picker activity" prompt block and can
// override at any time.
//
// MECHANICAL FILTERS PRESERVED (loop-prevention, not priority):
//   - Drop objects flagged as inside the bot's own bag
//     (ContainerGuid == selfGuid). They typically have no CellId
//     and so are excluded by WithinRadius anyway, but the
//     belt-and-braces filter protects against servers that leave
//     CellId populated on a contained item.
//   - Drop pickups whose Name has already been picked up at least
//     once. Visited-by-GUID exclusion (applied UPSTREAM by the
//     caller) does not catch respawns that get a fresh GUID with
//     the same Name. Without this filter the bot would farm the
//     same chair / apple / loot bag forever after the server
//     respawns it.
//
// FORBIDDEN BUMPS REMOVED (game knowledge — must NEVER come back):
//   - "NPC > corpse > door > pickup > else" priority ladder.
//   - "fresh corpse jumps to prio 0" loot bump.
//   - "wearable with unfilled slot > door" preference.
//   - "sign deserves prio 2 because reading > picking" preference.
//   All of those are strategic. The LLM has the RULES and the
//   Slice V activity surface to make them.
//
// The fallback picker (HandshakeDriver, exploration branch) still
// contains its own ladder + a visited-door backtrack. Removing it
// is Slice W.2 (#86) — needs an LLM-owned Explore replacement
// first so the bot doesn't get stranded when nothing is in radius.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

internal static class PickerSelection
{
    /// <summary>
    /// Picks the nearest mechanically-eligible candidate from
    /// <paramref name="inRange"/>. Returns null if nothing is
    /// eligible.
    /// </summary>
    /// <param name="inRange">
    /// Pre-filtered candidates. The caller is expected to have
    /// already excluded: self, empty-named objects, players,
    /// visited GUIDs, and satisfied weenie classes. This method
    /// applies only the additional MECHANICAL filters listed in
    /// the file header.
    /// </param>
    /// <param name="self">The bot's own snapshot — distance origin.</param>
    /// <param name="selfGuid">Bot character GUID for ContainerGuid checks.</param>
    /// <param name="pickupCountByName">
    /// Per-Name pickup counter. Any pickup-eligible candidate with
    /// count &gt; 0 is dropped (anti-respawn).
    /// </param>
    /// <param name="pickupItemTypeMask">
    /// ItemType bitmask of pickup-eligible types. Used to identify
    /// pickup-class candidates for the pickedBefore filter; passed
    /// in (rather than hardcoded) so production HandshakeDriver and
    /// tests share one source of truth.
    /// </param>
    public static WorldObjectSnapshot? PickNearest(
        IEnumerable<WorldObjectSnapshot> inRange,
        WorldObjectSnapshot self,
        uint selfGuid,
        IReadOnlyDictionary<string, int> pickupCountByName,
        uint pickupItemTypeMask)
    {
        if (inRange is null) throw new ArgumentNullException(nameof(inRange));
        if (self is null) throw new ArgumentNullException(nameof(self));
        if (pickupCountByName is null) throw new ArgumentNullException(nameof(pickupCountByName));

        return inRange
            .Where(s => !IsContainedInSelfBag(s, selfGuid))
            .Where(s => !IsRespawnOfPickedItem(s, pickupCountByName, pickupItemTypeMask))
            .Select(s =>
            {
                WorldDistance.TrySquaredDistance(self, s, out var d2);
                return (snap: s, d2);
            })
            .OrderBy(t => t.d2)
            .Select(t => t.snap)
            .FirstOrDefault();
    }

    private static bool IsContainedInSelfBag(WorldObjectSnapshot s, uint selfGuid)
        => s.ContainerGuid is uint cg && cg == selfGuid;

    private static bool IsRespawnOfPickedItem(
        WorldObjectSnapshot s,
        IReadOnlyDictionary<string, int> pickupCountByName,
        uint pickupItemTypeMask)
    {
        var descFlags = s.ObjectDescriptionFlags ?? 0u;
        var isStuck = (descFlags & (uint)ObjectDescriptionFlag.Stuck) != 0;
        var isPortal = (descFlags & (uint)ObjectDescriptionFlag.Portal) != 0;
        var isWritable = s.ItemType is uint wt && (wt & 0x00002000u) != 0;
        var isBookPickup = isWritable && !isStuck;
        var isSign = isWritable && isStuck;
        var isPickup =
            (s.ItemType is uint it && (it & pickupItemTypeMask) != 0 && !isPortal && !isSign)
            || isBookPickup;
        if (!isPickup) return false;
        return pickupCountByName.TryGetValue(s.Name ?? string.Empty, out var pc) && pc > 0;
    }
}
