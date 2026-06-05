// SPDX-License-Identifier: AGPL-3.0-or-later
// PickerSelection — Slice W.1 + W.2 (ac-ai-players#86, #87).
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
// W.1 removed the in-range ladder. The in-range picker now selects
// the nearest mechanically-eligible candidate, period.
//
// W.2 removes the *fallback* picker's ladder and visited-door
// backtrack strategy. The fallback now picks the nearest known
// off-screen object addressable from the bot's current landblock,
// with the same mechanical filters as the in-range picker. No
// type-based bumps. No backtrack-via-visited-door preference.
// Strategic exploration is owned by the LLM: it sees a new
// "## Exploration candidates" prompt block listing the same set
// the fallback considers, and can emit `Explore{target=<guid|name>}`
// to override the fallback's nearest-distance pick (the Explore
// goal honors `target` selectors so the LLM can name a specific
// landmark or visited door to backtrack through).
//
// MECHANICAL FILTERS PRESERVED (loop-prevention, not priority):
//   - Drop objects physically attached to the bot — i.e. the bot's
//     own bag contents (ContainerGuid == selfGuid) OR items the bot
//     currently wields (WielderGuid == selfGuid). Both are
//     "owned/equipped by me" in the wire schema sense and are not
//     legitimate motion targets. ContainerGuid covers starter
//     inventory and looted items not yet equipped; WielderGuid
//     covers actively wielded weapons/shields/jewellery. They
//     typically have no CellId and so are excluded by WithinRadius
//     anyway, but the belt-and-braces filter protects against:
//       (a) servers that leave CellId populated on a contained item,
//       (b) re-login flows where the bot reconnects with already-
//           wielded gear AND that gear arrives via ObjectCreate
//           with the bot's own CellId. Without this filter the
//           pure-distance picker would lock onto the wielded item
//           at d=0u and brick (sliceW01 run-02 lesson).
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
//   - "visited door earns prio 2 because backtracking re-stimulates
//      cells" preference (W.2 — this WAS the rationale for the
//      fallback's visited-door bump; the LLM now decides when
//      backtracking is worthwhile via the candidate surface).
//   All of those are strategic. The LLM has the RULES and the
//   Slice V / W.2 surfaces to make them.

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
    /// <param name="selfGuid">
    /// Bot character GUID. Used to drop items physically attached
    /// to the bot — ContainerGuid (bagged) or WielderGuid (wielded).
    /// </param>
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
            .Where(s => !IsAttachedToSelf(s, selfGuid))
            .Where(s => !IsRespawnOfPickedItem(s, pickupCountByName, pickupItemTypeMask))
            .Select(s =>
            {
                WorldDistance.TrySelectionSquaredDistance(self, s, out var d2);
                return (snap: s, d2);
            })
            .OrderBy(t => t.d2)
            .Select(t => t.snap)
            .FirstOrDefault();
    }

    private static bool IsAttachedToSelf(WorldObjectSnapshot s, uint selfGuid)
        => (s.ContainerGuid is uint cg && cg == selfGuid)
        || (s.WielderGuid is uint wg && wg == selfGuid);

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

    /// <summary>
    /// W.2 — fallback picker for exploration when the in-range queue
    /// is empty. Returns the nearest mechanically-eligible
    /// off-screen known object addressable from the bot's current
    /// landblock, or null if nothing eligible remains.
    ///
    /// Mechanical filters (the same shape as <see cref="PickNearest"/>
    /// plus addressability):
    ///   - drop self / empty-name / player GUIDs (caller-applied
    ///     in <paramref name="known"/>).
    ///   - drop visited GUIDs (caller-applied via
    ///     <paramref name="visitedGuids"/>; backtrack-via-visited-
    ///     door is the LLM's job now, not the picker's).
    ///   - drop satisfied weenie classes (caller-applied).
    ///   - drop ContainerGuid==self / WielderGuid==self.
    ///   - drop pickup-eligible respawns whose Name we've picked
    ///     up at least once (anti-respawn).
    ///   - drop objects whose CellId is not in the bot's current
    ///     landblock (high-16-bits match). Reaching a different
    ///     landblock requires server-side cell hand-off; chasing
    ///     a remembered object 300u away in another building tends
    ///     to either no-op or wander into closed-door geometry.
    ///     Same-landblock keeps the pure-distance choice on a
    ///     reachable target.
    ///   - drop objects with no CellId (an item with no spatial
    ///     position cannot be walked toward).
    ///
    /// NO type-based bumps. NO door / corpse / pickup preference.
    /// Strategic exploration is the LLM's responsibility — it sees
    /// the same candidate set via the "## Exploration candidates"
    /// prompt block and can override by emitting Explore{target}.
    /// </summary>
    /// <param name="known">All known world objects (pre-filtered
    /// by caller for self / empty-name / player / visited /
    /// satisfied-wcid).</param>
    /// <param name="self">Bot's own snapshot.</param>
    /// <param name="selfGuid">Bot character GUID.</param>
    /// <param name="selfLandblock">High-16-bits of bot's current
    /// CellId (the "landblock"). Candidates with CellId in a
    /// different landblock are excluded.</param>
    /// <param name="pickupCountByName">Per-Name pickup counter.</param>
    /// <param name="pickupItemTypeMask">ItemType bitmask for pickup
    /// classification (anti-respawn filter).</param>
    public static WorldObjectSnapshot? PickNearestFallback(
        IEnumerable<WorldObjectSnapshot> known,
        WorldObjectSnapshot self,
        uint selfGuid,
        uint selfLandblock,
        IReadOnlyDictionary<string, int> pickupCountByName,
        uint pickupItemTypeMask)
    {
        if (known is null) throw new ArgumentNullException(nameof(known));
        if (self is null) throw new ArgumentNullException(nameof(self));
        if (pickupCountByName is null) throw new ArgumentNullException(nameof(pickupCountByName));

        return EnumerateFallbackCandidates(known, self, selfGuid, selfLandblock, pickupCountByName, pickupItemTypeMask)
            .Select(t => t.snap)
            .FirstOrDefault();
    }

    /// <summary>
    /// W.2 — enumerate the fallback picker's candidate set, sorted
    /// nearest-first. Used by HandshakeDriver to both pick the
    /// nearest target AND surface the top-N list to the LLM via
    /// the "## Exploration candidates" prompt block.
    /// </summary>
    public static IEnumerable<(WorldObjectSnapshot snap, float distance)> EnumerateFallbackCandidates(
        IEnumerable<WorldObjectSnapshot> known,
        WorldObjectSnapshot self,
        uint selfGuid,
        uint selfLandblock,
        IReadOnlyDictionary<string, int> pickupCountByName,
        uint pickupItemTypeMask)
    {
        if (known is null) throw new ArgumentNullException(nameof(known));
        if (self is null) throw new ArgumentNullException(nameof(self));
        if (pickupCountByName is null) throw new ArgumentNullException(nameof(pickupCountByName));

        return known
            .Where(s => s.Guid != selfGuid)
            .Where(s => !string.IsNullOrEmpty(s.Name))
            .Where(s => !IsAttachedToSelf(s, selfGuid))
            .Where(s => !IsRespawnOfPickedItem(s, pickupCountByName, pickupItemTypeMask))
            .Where(s => s.CellId is uint sc && sc != 0u && (sc & 0xFFFF0000u) == (selfLandblock & 0xFFFF0000u))
            .Select(s =>
            {
                WorldDistance.TrySelectionSquaredDistance(self, s, out var d2);
                return (snap: s, d2);
            })
            .OrderBy(t => t.d2)
            .Select(t => (t.snap, (float)Math.Sqrt(t.d2)));
    }
}
