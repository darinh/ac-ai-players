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
        IReadOnlySet<uint>? excludeGuids = null,
        bool attackableOnly = false)
    {
        if (sel is null) throw new ArgumentNullException(nameof(sel));
        if (sel.IsEmpty) return Array.Empty<WorldObjectSnapshot>();

        // Candidate set after every NON-name-exact filter. The exact name match
        // is applied separately so a miss can fall back to a bounded fuzzy name
        // match WITHOUT relaxing any other constraint (guid/wcid/itemtype/
        // shortdesc/namecontains/landblock/corpse/excluded-guids all still hold).
        var candidates = world.Objects.Values
            .Where(o => !excludeCorpses || !IsCorpse(o))
            .Where(o => excludeGuids is null || !excludeGuids.Contains(o.Guid))
            .Where(o => !attackableOnly || MatchesAttackable(o))
            .Where(o => MatchesGuid(o, sel))
            .Where(o => MatchesNameContains(o, sel))
            .Where(o => MatchesWcid(o, sel))
            .Where(o => MatchesItemTypeMask(o, sel))
            .Where(o => MatchesShortDescContains(o, sel, weenies))
            .Where(o => MatchesSameLandblockAsActor(o, actor))
            .ToList();

        var exact = candidates.Where(o => MatchesName(o, sel)).ToList();
        if (exact.Count > 0 || string.IsNullOrEmpty(sel.Name))
            return exact;

        // Exact name match (including the quoted-role strip in MatchesName) found
        // nothing. AC NPC names frequently bake an occupation/title into the wire
        // name (a personal name preceded or followed by descriptor words), and the
        // model often refers to the NPC by only the distinctive part. Fall back to
        // matching the selector name as a contiguous WHOLE-WORD subsequence of the
        // object name — but accept it ONLY when it identifies a SINGLE candidate, so
        // a partial that could mean several different objects stays unresolved (the
        // LLM re-decides) rather than silently snapping to the nearest. Resolves the
        // LLM's OWN named target more leniently; never invents a target; no game
        // knowledge (pure word-boundary string comparison on observed names).
        var fuzzy = candidates
            .Where(o => MatchesNameWordSubsequence(o.Name, sel.Name))
            .ToList();
        return fuzzy.Count == 1 ? fuzzy : exact; // exact is empty here
    }

    public static WorldObjectSnapshot? ResolveSingleNearest(
        Selector sel,
        WorldState world,
        WorldObjectSnapshot? referencePoint = null,
        IWeenieRepository? weenies = null,
        bool excludeCorpses = false,
        IReadOnlySet<uint>? excludeGuids = null,
        bool attackableOnly = false)
    {
        // Use referencePoint as the actor for the locality filter:
        // a single-nearest resolution is asking "what should this
        // actor act on?", so confining to the actor's landblock is
        // the right default.
        var all = Resolve(sel, world, weenies, actor: referencePoint,
            excludeCorpses: excludeCorpses, excludeGuids: excludeGuids,
            attackableOnly: attackableOnly);
        if (all.Count == 0) return null;
        if (referencePoint is null) return all[0];

        return all
            .Select(o => (o, ok: WorldDistance.TrySelectionSquaredDistance(referencePoint, o, out var d2), d2))
            .OrderBy(t => t.ok ? t.d2 : double.MaxValue)
            .First().o;
    }

    /// <summary>
    /// Resolve a selector to exactly one item currently wielded by the actor.
    /// Dequip is intentionally strict: zero or several matches return null so
    /// the motor never chooses which equipped item the LLM meant.
    /// </summary>
    public static WorldObjectSnapshot? ResolveUniqueWieldedByActor(
        Selector sel,
        WorldState world,
        uint actorGuid,
        IWeenieRepository? weenies,
        out int matchCount)
    {
        var matches = Resolve(sel, world, weenies)
            .Where(o => o.WielderGuid == actorGuid)
            .Take(2)
            .ToList();
        matchCount = matches.Count;
        return matches.Count == 1 ? matches[0] : null;
    }

    // An Attack goal must bind a target the server flags Attackable that is
    // NOT a fellow player. A selector can match a world object by NAME yet be a
    // non-attackable non-creature (an item whose name merely contains a
    // selector word) or a non-attackable NPC (vendor / healer); dispatching a
    // melee/missile attack at it lands nothing and strands the bot in the
    // no-damage abandon watchdog. Uses the RAW server Attackable bit, NOT the
    // IsMonster composite, on purpose: the autonomous combat fallback emits
    // Attack for ANY observed-hostile non-player creature — including radar-blip
    // creatures that IsMonster excludes — so gating on IsMonster here would drop
    // a valid self-defense attack (producer/consumer mismatch). Players carry
    // the Attackable bit too, so they are excluded by guid band (the
    // players-are-not-monsters invariant; the bot does not attack a fellow
    // player). Pure wire classification; the LLM still chose the selector.
    private static bool MatchesAttackable(WorldObjectSnapshot o)
        => ((o.ObjectDescriptionFlags ?? 0u) & (uint)ObjectDescriptionFlag.Attackable) != 0
           && !WorldStateProjection.IsPlayerGuid(o.Guid);

    /// <summary>
    /// Resolve a player-directed selector (e.g. a FellowshipRecruit target) to the
    /// SINGLE matching PLAYER other than the actor. A social invite must be
    /// unambiguous, so this returns the unique player snapshot ONLY when exactly one
    /// player matches; it returns null when zero or several players match (the caller
    /// Fails so Strategy re-decides with a sharper name) and never picks the nearest
    /// on its own. <paramref name="matchCount"/> reports how many players matched
    /// (0 / 1 / N) so the caller can distinguish "no match" from "ambiguous".
    /// Self is excluded (the actor cannot recruit itself, and self sits at distance 0
    /// so a nearest-pick would otherwise always return it).
    /// </summary>
    public static WorldObjectSnapshot? ResolveUniquePlayerOtherThanActor(
        Selector sel,
        WorldState world,
        WorldObjectSnapshot actor,
        out int matchCount)
    {
        var players = Resolve(sel, world, actor: actor)
            .Where(o => o.Guid != actor.Guid && WorldStateProjection.IsPlayerGuid(o.Guid))
            .ToList();
        matchCount = players.Count;
        return players.Count == 1 ? players[0] : null;
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

    /// <summary>
    /// True when <paramref name="selectorName"/>, split into whole words, appears
    /// as a CONTIGUOUS subsequence of <paramref name="objectName"/>'s words
    /// (case-insensitive). Words are runs of letters/digits, so punctuation
    /// (commas, apostrophes) neither fuses nor splits a word inconsistently. Used
    /// only as a uniqueness-gated fallback after an exact name match fails, so the
    /// model can name an NPC by the distinctive part of an occupation/title-laden
    /// wire name. Pure string comparison; no game knowledge.
    /// </summary>
    internal static bool MatchesNameWordSubsequence(string? objectName, string? selectorName)
    {
        if (string.IsNullOrWhiteSpace(objectName) || string.IsNullOrWhiteSpace(selectorName))
            return false;
        var objWords = TokenizeWords(objectName);
        var selWords = TokenizeWords(selectorName);
        if (selWords.Count == 0 || selWords.Count > objWords.Count) return false;
        for (int start = 0; start + selWords.Count <= objWords.Count; start++)
        {
            bool all = true;
            for (int k = 0; k < selWords.Count; k++)
            {
                if (!string.Equals(objWords[start + k], selWords[k], StringComparison.OrdinalIgnoreCase))
                {
                    all = false;
                    break;
                }
            }
            if (all) return true;
        }
        return false;
    }

    // Split a name into whole-word tokens (maximal runs of letters/digits),
    // dropping all punctuation/whitespace. No StringBuilder/regex dependency.
    private static List<string> TokenizeWords(string s)
    {
        var tokens = new List<string>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && !char.IsLetterOrDigit(s[i])) i++;
            int start = i;
            while (i < n && char.IsLetterOrDigit(s[i])) i++;
            if (i > start) tokens.Add(s.Substring(start, i - start));
        }
        return tokens;
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
