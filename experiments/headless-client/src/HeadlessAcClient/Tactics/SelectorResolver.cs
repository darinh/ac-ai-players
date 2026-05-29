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
// Per the architecture rule, this resolver does NOT bake game
// content. It only matches what was observed at runtime.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Tactics;

internal static class SelectorResolver
{
    public static IReadOnlyList<WorldObjectSnapshot> Resolve(
        Selector sel,
        WorldState world,
        IWeenieRepository? weenies = null)
    {
        if (sel is null) throw new ArgumentNullException(nameof(sel));
        if (sel.IsEmpty) return Array.Empty<WorldObjectSnapshot>();

        return world.Objects.Values
            .Where(o => MatchesGuid(o, sel))
            .Where(o => MatchesName(o, sel))
            .Where(o => MatchesNameContains(o, sel))
            .Where(o => MatchesWcid(o, sel))
            .Where(o => MatchesItemTypeMask(o, sel))
            .Where(o => MatchesShortDescContains(o, sel, weenies))
            .ToList();
    }

    public static WorldObjectSnapshot? ResolveSingleNearest(
        Selector sel,
        WorldState world,
        WorldObjectSnapshot? referencePoint = null,
        IWeenieRepository? weenies = null)
    {
        var all = Resolve(sel, world, weenies);
        if (all.Count == 0) return null;
        if (referencePoint is null) return all[0];

        return all
            .Select(o => (o, ok: WorldDistance.TrySquaredDistance(referencePoint, o, out var d2), d2))
            .OrderBy(t => t.ok ? t.d2 : double.MaxValue)
            .First().o;
    }

    private static bool MatchesGuid(WorldObjectSnapshot o, Selector s) =>
        s.Guid is null || o.Guid == s.Guid;

    private static bool MatchesName(WorldObjectSnapshot o, Selector s)
    {
        if (string.IsNullOrEmpty(s.Name)) return true;
        return o.Name is not null && string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase);
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
}
