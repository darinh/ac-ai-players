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
}
