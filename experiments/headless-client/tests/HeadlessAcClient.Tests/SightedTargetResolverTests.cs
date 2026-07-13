// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Slice 4 (FOV consumption): resolving an LLM Selector against remembered
/// SightedLocation memory. Verifies mechanical selector matching, the
/// same-landblock restriction, the most-recent tie-break, cooldown
/// exclusion, and that unsupported-only selectors decline.
/// </summary>
public sealed class SightedTargetResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow;
    private readonly List<NavGraph> _graphs = new();

    // Two cells in landblock 0x8602.
    private const uint CellA = 0x860201ADu;
    private const uint CellB = 0x860201B5u;
    // A cell in a DIFFERENT landblock (0x8603).
    private const uint OtherLandblockCell = 0x8603001Au;

    public SightedTargetResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sighted-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var g in _graphs) { try { g.Dispose(); } catch { } }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private NavGraph NewGraph()
    {
        var g = new NavGraph(_dir);
        _graphs.Add(g);
        return g;
    }

    [Fact]
    public void Resolve_matches_exact_name_in_same_landblock()
    {
        var g = NewGraph();
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Name = "jonathan" }, CellB);

        Assert.NotNull(hit);
        Assert.Equal("Jonathan", hit!.Name);
    }

    [Fact]
    public void Resolve_excludes_other_landblock()
    {
        var g = NewGraph();
        g.RecordSightedLocation(OtherLandblockCell, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Name = "Jonathan" }, CellA);

        Assert.Null(hit);
    }

    [Fact]
    public void Resolve_matches_by_wcid()
    {
        var g = NewGraph();
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 29335u, "Quest Token",
            EntityKind.Unknown, null, _t0);

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Wcid = 29335u }, CellA);

        Assert.NotNull(hit);
        Assert.Equal(29335u, hit!.Wcid);
    }

    [Fact]
    public void Resolve_matches_name_contains_substring()
    {
        var g = NewGraph();
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Academy Guard Master",
            EntityKind.Unknown, null, _t0);

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { NameContains = "guard" }, CellA);

        Assert.NotNull(hit);
        Assert.Equal("Academy Guard Master", hit!.Name);
    }

    [Fact]
    public void Resolve_ands_name_and_wcid()
    {
        var g = NewGraph();
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        // Name matches but wcid does not -> no match (AND semantics).
        var miss = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Name = "Jonathan", Wcid = 999u }, CellA);
        Assert.Null(miss);

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Name = "Jonathan", Wcid = 100u }, CellA);
        Assert.NotNull(hit);
    }

    [Fact]
    public void Resolve_declines_unsupported_only_selectors()
    {
        var g = NewGraph();
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        // Guid / ItemTypeMask / ShortDescContains cannot be verified
        // against a sighting; a selector using only those must decline.
        Assert.Null(SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Guid = 0x5000_0001u }, CellA));
        Assert.Null(SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { ItemTypeMask = 0x8u }, CellA));
        Assert.Null(SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { ShortDescContains = "exit" }, CellA));
    }

    [Fact]
    public void Resolve_skips_excluded_ids()
    {
        var g = NewGraph();
        var id = g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        var excluded = new HashSet<Guid> { id };
        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Name = "Jonathan" }, CellA, excluded);

        Assert.Null(hit);
    }

    [Fact]
    public void Resolve_tie_breaks_most_recently_seen()
    {
        var g = NewGraph();
        // Two distinct entities both matching NameContains="Guard", in the
        // same landblock, seen at different times.
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Guard Alpha",
            EntityKind.Unknown, null, _t0);
        g.RecordSightedLocation(CellB, new Vector3(9, 0, 9), 101u, "Guard Beta",
            EntityKind.Unknown, null, _t0.AddSeconds(60));

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { NameContains = "Guard" }, CellA);

        Assert.NotNull(hit);
        Assert.Equal("Guard Beta", hit!.Name);
    }

    [Fact]
    public void Resolve_returns_null_when_no_match()
    {
        var g = NewGraph();
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Name = "Nobody" }, CellA);

        Assert.Null(hit);
    }

    [Fact]
    public void Resolve_handles_empty_memory()
    {
        var g = NewGraph();
        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { Name = "Jonathan" }, CellA);
        Assert.Null(hit);
    }

    // ─── ResolveCrossLandblock (slice 5) ─────────────────────────────

    [Fact]
    public void ResolveCrossLandblock_matches_entity_in_another_landblock()
    {
        var g = NewGraph();
        g.RecordSightedLocation(OtherLandblockCell, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        var hit = SightedTargetResolver.ResolveCrossLandblock(
            g.SnapshotSighted(), new Selector { Name = "jonathan" }, CellA);

        Assert.NotNull(hit);
        Assert.Equal("Jonathan", hit!.Name);
        Assert.Equal(OtherLandblockCell, hit.CellId);
    }

    [Fact]
    public void ResolveCrossLandblock_excludes_same_landblock()
    {
        var g = NewGraph();
        // Same landblock as the bot (0x8602) -> NOT a cross-landblock hit.
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        var hit = SightedTargetResolver.ResolveCrossLandblock(
            g.SnapshotSighted(), new Selector { Name = "Jonathan" }, CellB);

        Assert.Null(hit);
    }

    [Fact]
    public void ResolveCrossLandblock_tie_breaks_most_recently_seen_across_landblocks()
    {
        var g = NewGraph();
        // Two matches, both in landblocks OTHER than the bot's current
        // one, seen at different times. Most-recent wins.
        const uint farCellOlder = 0x8603001Au; // lb 0x8603
        const uint farCellNewer = 0x8604002Bu; // lb 0x8604
        g.RecordSightedLocation(farCellOlder, new Vector3(5, 0, 5), 100u, "Guard Alpha",
            EntityKind.Unknown, null, _t0);
        g.RecordSightedLocation(farCellNewer, new Vector3(9, 0, 9), 101u, "Guard Beta",
            EntityKind.Unknown, null, _t0.AddSeconds(60));

        var hit = SightedTargetResolver.ResolveCrossLandblock(
            g.SnapshotSighted(), new Selector { NameContains = "Guard" }, CellA);

        Assert.NotNull(hit);
        Assert.Equal("Guard Beta", hit!.Name);
    }

    [Fact]
    public void ResolveCrossLandblock_declines_unsupported_only_selectors()
    {
        var g = NewGraph();
        g.RecordSightedLocation(OtherLandblockCell, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        Assert.Null(SightedTargetResolver.ResolveCrossLandblock(
            g.SnapshotSighted(), new Selector { Guid = 0x5000_0001u }, CellA));
    }

    [Fact]
    public void ResolveCrossLandblock_skips_excluded_ids()
    {
        var g = NewGraph();
        var id = g.RecordSightedLocation(OtherLandblockCell, new Vector3(5, 0, 5), 100u, "Jonathan",
            EntityKind.Unknown, null, _t0);

        var excluded = new HashSet<Guid> { id };
        var hit = SightedTargetResolver.ResolveCrossLandblock(
            g.SnapshotSighted(), new Selector { Name = "Jonathan" }, CellA, excluded);

        Assert.Null(hit);
    }

    // ─── attackableOnly (an Attack must bind an attackable creature) ──────
    // The Attack path passes attackableOnly:true so a combat verb never
    // routes the bot to a sighting that merely matches the selector by NAME
    // but is a non-creature (an item) or a non-attackable creature. Live bug:
    // an Attack `name_contains="monster"` bound a non-creature "Yellow Monster
    // Seed" in another landblock and walked a 28-hop route to attack nothing.

    [Fact]
    public void IsAttackableSightingKind_only_true_for_mob()
    {
        Assert.True(SightedTargetResolver.IsAttackableSightingKind(EntityKind.Mob));
        foreach (var kind in new[]
        {
            EntityKind.Unknown, EntityKind.NPC, EntityKind.Player, EntityKind.Item,
            EntityKind.Portal, EntityKind.Door, EntityKind.Vendor, EntityKind.Healer,
            EntityKind.Lifestone, EntityKind.Corpse,
        })
            Assert.False(SightedTargetResolver.IsAttackableSightingKind(kind), $"kind={kind}");
    }

    [Fact]
    public void Resolve_attackableOnly_skips_non_creature_but_default_binds_it()
    {
        var g = NewGraph();
        // A non-creature item whose NAME contains the selector word "monster"
        // (ClassifySighting labels a non-creature EntityKind.Unknown).
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Yellow Monster Seed",
            EntityKind.Unknown, null, _t0);

        // Attack path: declines the item — you cannot attack it.
        Assert.Null(SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { NameContains = "monster" }, CellB,
            attackableOnly: true));

        // Explore path (default false): still binds it — byte-identical prior
        // behaviour (any remembered location is a valid walk destination).
        var explore = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { NameContains = "monster" }, CellB);
        Assert.NotNull(explore);
        Assert.Equal("Yellow Monster Seed", explore!.Name);
    }

    [Fact]
    public void Resolve_attackableOnly_returns_mob()
    {
        var g = NewGraph();
        g.RecordSightedLocation(CellA, new Vector3(5, 0, 5), 100u, "Monster Beast",
            EntityKind.Mob, null, _t0);

        var hit = SightedTargetResolver.Resolve(
            g.SnapshotSighted(), new Selector { NameContains = "monster" }, CellB,
            attackableOnly: true);

        Assert.NotNull(hit);
        Assert.Equal("Monster Beast", hit!.Name);
    }

    [Fact]
    public void ResolveCrossLandblock_attackableOnly_skips_non_creature()
    {
        var g = NewGraph();
        g.RecordSightedLocation(OtherLandblockCell, new Vector3(5, 0, 5), 100u, "Yellow Monster Seed",
            EntityKind.Unknown, null, _t0);

        Assert.Null(SightedTargetResolver.ResolveCrossLandblock(
            g.SnapshotSighted(), new Selector { NameContains = "monster" }, CellA,
            attackableOnly: true));
    }

    [Fact]
    public void ResolveCrossLandblock_attackableOnly_picks_mob_over_more_recent_non_creature()
    {
        var g = NewGraph();
        // Both match NameContains="monster"; the non-creature was seen MORE
        // recently (would win the plain tie-break), but attackableOnly must
        // still return the attackable Mob and never the item.
        const uint mobCell  = 0x8603001Au; // lb 0x8603
        const uint itemCell = 0x8604002Bu; // lb 0x8604
        g.RecordSightedLocation(mobCell, new Vector3(5, 0, 5), 100u, "Monster Beast",
            EntityKind.Mob, null, _t0);
        g.RecordSightedLocation(itemCell, new Vector3(9, 0, 9), 101u, "Yellow Monster Seed",
            EntityKind.Unknown, null, _t0.AddSeconds(60));

        var hit = SightedTargetResolver.ResolveCrossLandblock(
            g.SnapshotSighted(), new Selector { NameContains = "monster" }, CellA,
            attackableOnly: true);

        Assert.NotNull(hit);
        Assert.Equal("Monster Beast", hit!.Name);
    }
}
