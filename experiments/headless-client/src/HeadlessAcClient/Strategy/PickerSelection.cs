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
//
// REMOVED (picker-name-respawn-audit) — the per-Name "anti-respawn"
// pickup filter. It dropped any pickup-eligible candidate whose Name
// had been picked up >=1 time before (keyed on a Dictionary<Name,int>).
// That was a STRATEGIC valuation ("one copy of this named pickup is
// enough; duplicates aren't worth re-collecting") smuggled into the
// Motor — a real player may legitimately want several of the same
// consumable, and which loot is worth picking is the LLM's call. It
// also silently HID those items from the "## Exploration candidates"
// surface, so the LLM could not override. The pickup count is now
// surfaced to the LLM as a factual `picked_name_count=N` annotation on
// each candidate (ExplorationCandidate.PickedNameCount); the LLM
// decides whether to re-pick. The autonomous picker no longer dispatches
// Pickup at all (Slice W.3 goal-gating: a picker auto-lock without an
// LLM verb goal is an arrival-no-op), so removing this filter cannot
// reintroduce an autonomous same-name pickup loop.
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
    public static WorldObjectSnapshot? PickNearest(
        IEnumerable<WorldObjectSnapshot> inRange,
        WorldObjectSnapshot self,
        uint selfGuid)
    {
        if (inRange is null) throw new ArgumentNullException(nameof(inRange));
        if (self is null) throw new ArgumentNullException(nameof(self));

        return inRange
            .Where(s => !IsAttachedToSelf(s, selfGuid))
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

    // Mechanical self-identity filter — the SAME class as the IsAttachedToSelf
    // filter above (drop objects that ARE the bot's own). Drops a Corpse-flagged
    // object (wire ObjectDescriptionFlag bit 0x2000) whose name is the bot's OWN
    // runtime name on a word boundary, i.e. the bot's own corpse. Keys only on the
    // wire flag + self.Name; no hardcoded object/zone/name, and no type-priority.
    private static bool IsBotsOwnCorpse(WorldObjectSnapshot s, WorldObjectSnapshot self)
    {
        const uint CorpseFlag = (uint)ObjectDescriptionFlag.Corpse;
        if (((s.ObjectDescriptionFlags ?? 0u) & CorpseFlag) == 0) return false;
        var selfName = self.Name;
        if (string.IsNullOrEmpty(selfName) || string.IsNullOrEmpty(s.Name)) return false;
        // Word-boundary match (Equals OR ends with " " + the self name), NOT a raw
        // substring — so an object whose name merely superstrings the self name is
        // not dropped. Uses only the bot's OWN runtime name; no hardcoded prefix.
        return s.Name!.Equals(selfName, StringComparison.Ordinal)
            || s.Name!.EndsWith(" " + selfName, StringComparison.Ordinal);
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
    public static WorldObjectSnapshot? PickNearestFallback(
        IEnumerable<WorldObjectSnapshot> known,
        WorldObjectSnapshot self,
        uint selfGuid,
        uint selfLandblock)
    {
        if (known is null) throw new ArgumentNullException(nameof(known));
        if (self is null) throw new ArgumentNullException(nameof(self));

        return EnumerateFallbackCandidates(known, self, selfGuid, selfLandblock)
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
        uint selfLandblock)
    {
        if (known is null) throw new ArgumentNullException(nameof(known));
        if (self is null) throw new ArgumentNullException(nameof(self));

        return known
            .Where(s => s.Guid != selfGuid)
            .Where(s => !string.IsNullOrEmpty(s.Name))
            .Where(s => !IsAttachedToSelf(s, selfGuid))
            .Where(s => !IsBotsOwnCorpse(s, self))
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
