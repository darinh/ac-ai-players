// SPDX-License-Identifier: AGPL-3.0-or-later
// SelectorResolver — turn an abstract Selector into 0..N concrete
// WorldObjectSnapshots. Used by Tactics to find the live target
// for a goal each tick.
//
// Resolution rules (AND across populated fields, OR across
// candidates):
//   - Guid           : exact match (used when LLM cites a guid)
//   - Name           : exact case-insensitive match
//   - NameContains   : substring case-insensitive match
//   - Wcid           : exact WeenieClassId match
//   - ItemTypeMask   : (ItemType & mask) != 0
//   - ShortDescContains : substring match on the cached
//     ShortDesc from WeenieRepository. Selector with this set
//     requires `weenies` non-null; if null the field is ignored.
//
// Locality filter:
//   When the optional `actor` snapshot is non-null and has a
//   populated CellId, world-space candidates (those without a
//   ContainerGuid, i.e. not in someone's inventory) are restricted
//   to actor's current OR an adjacent landblock (Chebyshev <= 1 on
//   the landblock X/Y bytes). The adjacency tolerance lets a target
//   physically next to the actor resolve when it sits one landblock
//   over after a freshly-crossed seam (e.g. the corpse of a mob
//   killed at the boundary). This prevents stale snapshots from
//   FAR landblocks (e.g. the academy Society Greeter after a Free
//   Ride teleport to Holtburg — many landblocks away, still
//   rejected) from resolving for a bot that has since moved away —
//   the server doesn't always emit ObjectDelete for objects that
//   fall out of broadcast range, so WorldState accumulates them
//   indefinitely.
//
// Per the architecture rule, this resolver does NOT bake game
// content. It only matches what was observed at runtime.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Tactics;

internal static class SelectorResolver
{
    public static IReadOnlyList<WorldObjectSnapshot> Resolve(
        Selector sel,
        WorldState world,
        IWeenieRepository? weenies = null,
        WorldObjectSnapshot? actor = null,
        bool excludeCorpses = false,
        IReadOnlySet<uint>? excludeGuids = null)
    {
        if (sel is null) throw new ArgumentNullException(nameof(sel));
        if (sel.IsEmpty) return Array.Empty<WorldObjectSnapshot>();

        return world.Objects.Values
            .Where(o => !excludeCorpses || !IsCorpse(o))
            .Where(o => excludeGuids is null || !excludeGuids.Contains(o.Guid))
            .Where(o => MatchesGuid(o, sel))
            .Where(o => MatchesName(o, sel))
            .Where(o => MatchesNameContains(o, sel))
            .Where(o => MatchesWcid(o, sel))
            .Where(o => MatchesItemTypeMask(o, sel))
            .Where(o => MatchesShortDescContains(o, sel, weenies))
            .Where(o => MatchesSameLandblockAsActor(o, actor))
            .ToList();
    }

    public static WorldObjectSnapshot? ResolveSingleNearest(
        Selector sel,
        WorldState world,
        WorldObjectSnapshot? referencePoint = null,
        IWeenieRepository? weenies = null,
        bool excludeCorpses = false,
        IReadOnlySet<uint>? excludeGuids = null)
    {
        // Use referencePoint as the actor for the locality filter:
        // a single-nearest resolution is asking "what should this
        // actor act on?", so confining to the actor's landblock is
        // the right default.
        var all = Resolve(sel, world, weenies, actor: referencePoint,
            excludeCorpses: excludeCorpses, excludeGuids: excludeGuids);
        if (all.Count == 0) return null;
        if (referencePoint is null) return all[0];

        return all
            .Select(o => (o, ok: WorldDistance.TrySelectionSquaredDistance(referencePoint, o, out var d2), d2))
            .OrderBy(t => t.ok ? t.d2 : double.MaxValue)
            .First().o;
    }

    // A corpse retains the slain creature's NAME but is not an attackable
    // target (the wire ObjectDescriptionFlag.Corpse bit). Callers resolving an
    // Attack target pass excludeCorpses:true so an Attack{Name} after a kill
    // resolves to a LIVE name-match, not the corpse the bot is standing on.
    // Pickup/Use callers leave it false — a corpse IS a valid loot target.
    private static bool IsCorpse(WorldObjectSnapshot o) =>
        o.ObjectDescriptionFlags is uint df && (df & (uint)ObjectDescriptionFlag.Corpse) != 0;

    private static bool MatchesGuid(WorldObjectSnapshot o, Selector s) =>
        s.Guid is null || o.Guid == s.Guid;

    private static bool MatchesName(WorldObjectSnapshot o, Selector s)
    {
        if (string.IsNullOrEmpty(s.Name)) return true;
        if (o.Name is null) return false;
        if (string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase)) return true;
        // The prompt renders a visible object as `<Name> "<role/title>"` (the
        // weenie Quality string in quotes after the name) so role-named directives
        // can be matched. The model frequently copies that WHOLE rendered label
        // into a target selector (e.g. `<Name> "<role>"`), which never equals the
        // bare wire `<Name>`. Tolerate a trailing quoted-role suffix on the SELECTOR
        // name and re-test against the bare object name. Pure string normalization
        // of the LLM's own selector input; no game knowledge, no hardcoded names.
        var bare = StripTrailingQuotedRoleTitle(s.Name);
        return bare is not null
            && string.Equals(o.Name, bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strip a trailing ` "&lt;role/title&gt;"` quoted segment from a selector name —
    /// the exact shape the prompt appends after an object's name. Returns the bare
    /// base name (trimmed) when such a trailing quoted segment is present and a
    /// non-empty base remains; otherwise null (so callers do not re-test the same
    /// string they already matched exactly). The base name is taken up to the LAST
    /// opening double-quote, so a rare name that itself contains quotes degrades
    /// gracefully. Sanitization only; no game knowledge.
    /// </summary>
    internal static string? StripTrailingQuotedRoleTitle(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var trimmed = name.TrimEnd();
        if (trimmed.Length < 2 || trimmed[^1] != '"') return null;
        var openIdx = trimmed.LastIndexOf('"', trimmed.Length - 2);
        // The prompt appends the role as ` "<role>"` — a SPACE then the quoted
        // segment. Require whitespace immediately before the opening quote so this
        // only strips that rendered role suffix, never a double-quote that is part
        // of a genuine wire name (e.g. `Foo"Bar"`, which must remain a MISS).
        if (openIdx <= 0 || !char.IsWhiteSpace(trimmed[openIdx - 1])) return null;
        var bare = trimmed[..openIdx].TrimEnd();
        return bare.Length > 0 ? bare : null;
    }

    private static bool MatchesNameContains(WorldObjectSnapshot o, Selector s)
    {
        if (string.IsNullOrEmpty(s.NameContains)) return true;
        return o.Name is not null &&
               o.Name.Contains(s.NameContains, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesWcid(WorldObjectSnapshot o, Selector s) =>
        s.Wcid is null || (o.WeenieClassId is uint w && w == s.Wcid);

    private static bool MatchesItemTypeMask(WorldObjectSnapshot o, Selector s) =>
        s.ItemTypeMask is null || (o.ItemType is uint it && (it & s.ItemTypeMask) != 0);

    private static bool MatchesShortDescContains(WorldObjectSnapshot o, Selector s, IWeenieRepository? weenies)
    {
        if (string.IsNullOrEmpty(s.ShortDescContains)) return true;
        if (weenies is null) return false; // can't match without the repo
        if (o.WeenieClassId is not uint wcid) return false;
        var rec = weenies.TryGet(wcid);
        return rec?.ShortDesc is not null &&
               rec.ShortDesc.Contains(s.ShortDescContains, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSameLandblockAsActor(
        WorldObjectSnapshot o, WorldObjectSnapshot? actor)
    {
        // No actor or actor has no cell yet -> can't filter, accept.
        if (actor is null) return true;
        if (actor.CellId is not uint actorCell || actorCell == 0u) return true;

        // Items being carried (inventory) have a ContainerGuid set and
        // typically no meaningful CellId. They travel with their owner,
        // so they are always "local" to whoever carries them.
        if (o.ContainerGuid is uint c && c != 0u) return true;

        // World-space object: require the SAME or an ADJACENT landblock
        // (Chebyshev distance <= 1 on the landblock X/Y bytes). A target
        // that is physically next to the actor can sit one landblock over
        // when the actor has just crossed a seam (the corpse of a mob
        // killed at the boundary, an NPC across the street). The strict
        // same-landblock rule rejected those even at ~1 unit, so an
        // explicit LLM Use/Talk/Attack goal resolved to MISS and the bot
        // could not loot its own kill across the seam. Same-or-adjacent
        // mirrors the cp-2295/2296 cross-landblock sighting precedent and
        // still rejects the stale post-TELEPORT case the filter exists for
        // (e.g. academy 0x8602 lingering after a Free Ride to Holtburg
        // 0xAAB5/0xA9B4 — not adjacent, still dropped). Pure wire-coordinate
        // geometry, no game knowledge.
        if (o.CellId is not uint oCell || oCell == 0u) return true;
        return WorldDistance.IsSameOrAdjacentLandblock(oCell, actorCell);
    }
}
