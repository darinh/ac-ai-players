// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Field-of-view discovery memory: entities the bot has SEEN but not
/// necessarily stood at. Recorded as <see cref="SightedLocation"/>s that
/// are deliberately separate from the routing graph, so they can never
/// imply a walkable shortcut (the wall-shortcut invariant) nor pollute
/// routing-anchor queries.
/// </summary>
public sealed class SightedLocationTests : IDisposable
{
    private readonly string _dir;
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow;
    private readonly List<NavGraph> _graphs = new();

    // Indoor cell (low 16 bits >= 0x100) → CoordNS/EW null.
    private const uint IndoorCell = 0x860201ADu;
    // Outdoor cell (low 16 bits < 0x100) → CoordNS/EW populated.
    private const uint OutdoorCell = 0x8602001Au;

    public SightedLocationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sighted-tests-" + Guid.NewGuid().ToString("N"));
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
    public void RecordSightedLocation_creates_record_without_node_or_edge()
    {
        var g = NewGraph();
        var id = g.RecordSightedLocation(IndoorCell, new Vector3(5, 0, 5), 42u, "Door",
            EntityKind.Door, observerNodeId: null, _t0);

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(1, g.SightedCount);
        // The honesty invariant: discovering by sight must NOT add a
        // walkable node or edge.
        Assert.Equal(0, g.NodeCount);
        Assert.Equal(0, g.EdgeCount);
        Assert.Empty(g.SnapshotEdges());
    }

    [Fact]
    public void RecordSightedLocation_does_not_break_walked_chain()
    {
        var g = NewGraph();
        var t = _t0;
        // Walk a continuous per-tick chain (steps <= 2m), but observe a
        // sighted entity mid-walk. The sighting must NOT reset the walked
        // chain, so an auto-Walked edge still forms between the first node
        // and the final distinct node (>4m away, beyond MergeRadius).
        var a = g.RecordVisit(IndoorCell, new Vector3(0, 0, 0), t);
        t = t.AddSeconds(0.5);
        g.RecordVisit(IndoorCell, new Vector3(1.5f, 0, 0), t); // dedups into a
        g.RecordSightedLocation(IndoorCell, new Vector3(20, 0, 20), 99u, "Chest",
            EntityKind.Unknown, a, t.AddSeconds(0.1));
        t = t.AddSeconds(0.5);
        g.RecordVisit(IndoorCell, new Vector3(3.0f, 0, 0), t); // dedups into a
        t = t.AddSeconds(0.5);
        var b = g.RecordVisit(IndoorCell, new Vector3(4.5f, 0, 0), t); // >4m → new node

        Assert.NotEqual(a, b);
        Assert.NotNull(g.FindRoute(a, b));
        Assert.All(g.SnapshotEdges(), e => Assert.Equal(NavEdgeKind.Walked, e.Kind));
    }

    [Fact]
    public void Sighted_location_is_not_a_routing_anchor()
    {
        var g = NewGraph();
        // A sighted entity far from any visited node must never be
        // returned by node-anchor queries — those operate on visited
        // routing nodes only.
        g.RecordSightedLocation(IndoorCell, new Vector3(50, 0, 50), 7u, "Statue",
            EntityKind.Unknown, observerNodeId: null, _t0);

        Assert.Null(g.FindNearestNode(IndoorCell, new Vector3(50, 0, 50)));
        Assert.Empty(g.FindNodesWithin(IndoorCell, new Vector3(50, 0, 50), 100f));
    }

    [Fact]
    public void RecordSightedLocation_dedupes_same_entity_within_merge_radius()
    {
        var g = NewGraph();
        var first = g.RecordSightedLocation(IndoorCell, new Vector3(10, 0, 10), 5u, "Lever",
            EntityKind.Unknown, null, _t0);
        var again = g.RecordSightedLocation(IndoorCell, new Vector3(11, 0, 11), 5u, "Lever",
            EntityKind.Unknown, null, _t0.AddSeconds(1)); // ~1.4m, same wcid

        Assert.Equal(first, again);
        Assert.Equal(1, g.SightedCount);
        var loc = g.SnapshotSighted().Single();
        Assert.Equal(2, loc.SightingCount);
        // Latest position wins.
        Assert.Equal(11f, loc.Position.X, 3);
    }

    [Fact]
    public void RecordSightedLocation_distinct_wcids_colocated_stay_separate()
    {
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(10, 0, 10), 5u, "Lever",
            EntityKind.Unknown, null, _t0);
        g.RecordSightedLocation(IndoorCell, new Vector3(10, 0, 10), 6u, "Lamp",
            EntityKind.Unknown, null, _t0); // same spot, different entity

        Assert.Equal(2, g.SightedCount);
    }

    [Fact]
    public void RecordSightedLocation_same_entity_far_apart_creates_distinct_records()
    {
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(0, 0, 0), 8u, "Wanderer",
            EntityKind.Unknown, null, _t0);
        g.RecordSightedLocation(IndoorCell, new Vector3(30, 0, 0), 8u, "Wanderer",
            EntityKind.Unknown, null, _t0.AddSeconds(5)); // same wcid, far away

        Assert.Equal(2, g.SightedCount);
    }

    [Fact]
    public void FindSightedLocations_matches_by_name_recency_first()
    {
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(0, 0, 0), 1u, "Town Crier",
            EntityKind.Unknown, null, _t0);
        g.RecordSightedLocation(IndoorCell, new Vector3(20, 0, 0), 2u, "Town Guard",
            EntityKind.Unknown, null, _t0.AddSeconds(10));

        var hits = g.FindSightedLocations("Town");
        Assert.Equal(2, hits.Count);
        Assert.Equal("Town Guard", hits[0].Name); // most recent first
    }

    [Fact]
    public void FindSightedLocationsWithin_returns_nearest_first_in_landblock()
    {
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(0, 0, 0), 1u, "Near",
            EntityKind.Unknown, null, _t0);
        g.RecordSightedLocation(IndoorCell, new Vector3(8, 0, 0), 2u, "Far",
            EntityKind.Unknown, null, _t0);

        var hits = g.FindSightedLocationsWithin(IndoorCell, new Vector3(1, 0, 0), 50f);
        Assert.Equal(2, hits.Count);
        Assert.Equal("Near", hits[0].Location.Name);
        Assert.True(hits[0].Distance <= hits[1].Distance);
    }

    [Fact]
    public void FindSightedLocationsWithin_measures_distance_across_cells_in_landblock()
    {
        // Two sightings in DIFFERENT cells of the SAME landblock. AC
        // Position.X/Y are landblock-local, so distance must be measured
        // with cell-aware math (WorldDistance), not a per-cell same-frame
        // shortcut. A and B are 30 units apart along X.
        const uint cellA = 0x860201ADu; // indoor, landblock 0x8602
        const uint cellB = 0x860201B5u; // different indoor cell, same landblock
        var g = NewGraph();
        g.RecordSightedLocation(cellA, new Vector3(10, 10, 0), 1u, "RoomA NPC",
            EntityKind.Unknown, null, _t0);
        g.RecordSightedLocation(cellB, new Vector3(40, 10, 0), 2u, "RoomB NPC",
            EntityKind.Unknown, null, _t0);

        // Query from cell A's position. Radius 20 reaches A (d=0) but not
        // B (d=30) — confirms B is neither a false 0-distance collision
        // nor an inflated cross-cell value.
        var near = g.FindSightedLocationsWithin(cellA, new Vector3(10, 10, 0), 20f);
        Assert.Single(near);
        Assert.Equal("RoomA NPC", near[0].Location.Name);

        // Radius 35 reaches both; B is ~30 units away.
        var both = g.FindSightedLocationsWithin(cellA, new Vector3(10, 10, 0), 35f);
        Assert.Equal(2, both.Count);
        var bHit = both.Single(h => h.Location.Name == "RoomB NPC");
        Assert.Equal(30f, bHit.Distance, 1);
    }

    [Fact]
    public void RecordSightedLocation_outdoor_populates_map_coords_indoor_does_not()
    {
        var g = NewGraph();
        var outId = g.RecordSightedLocation(OutdoorCell, new Vector3(12, 0, 34), 1u, "Outdoor NPC",
            EntityKind.Unknown, null, _t0);
        var inId = g.RecordSightedLocation(IndoorCell, new Vector3(12, 0, 34), 2u, "Indoor NPC",
            EntityKind.Unknown, null, _t0);

        var outdoor = g.SnapshotSighted().Single(s => s.Id == outId);
        var indoor = g.SnapshotSighted().Single(s => s.Id == inId);

        // WorldX = landblockX*192 + pos.X. Landblock 0x86 => 134.
        Assert.Equal(134 * 192 + 12f, outdoor.WorldX, 2);
        Assert.NotNull(outdoor.CoordNS);
        Assert.Null(indoor.CoordNS);
    }

    [Fact]
    public void RecordSightedLocation_rejects_empty_name_and_zero_cell()
    {
        var g = NewGraph();
        Assert.Equal(Guid.Empty,
            g.RecordSightedLocation(IndoorCell, Vector3.Zero, 1u, "", EntityKind.Unknown, null, _t0));
        Assert.Equal(0, g.SightedCount);
        Assert.Throws<ArgumentException>(() =>
            g.RecordSightedLocation(0u, Vector3.Zero, 1u, "X", EntityKind.Unknown, null, _t0));
    }

    [Fact]
    public void Journal_roundtrip_preserves_sighted_locations()
    {
        var g1 = NewGraph();
        g1.RecordSightedLocation(OutdoorCell, new Vector3(7, 0, 9), 5u, "Lifestone",
            EntityKind.Lifestone, null, _t0);
        g1.RecordSightedLocation(OutdoorCell, new Vector3(7.5f, 0, 9), 5u, "Lifestone",
            EntityKind.Lifestone, null, _t0.AddSeconds(31)); // past throttle → re-persists count 2
        g1.Flush();
        g1.Dispose(); // release writer locks before reopening (simulates restart)

        var g2 = NewGraph(); // re-loads the same directory's journals
        Assert.Equal(1, g2.SightedCount);
        var loc = g2.SnapshotSighted().Single();
        Assert.Equal("Lifestone", loc.Name);
        Assert.Equal(EntityKind.Lifestone, loc.Kind);
        Assert.Equal(2, loc.SightingCount);
        Assert.NotNull(loc.CoordNS);
    }

    [Fact]
    public void Sighted_dedup_persists_first_write_immediately_for_reload()
    {
        // A fresh sighting must be persisted immediately (not throttled),
        // so a crash right after the first sighting loses nothing.
        var g1 = NewGraph();
        g1.RecordSightedLocation(IndoorCell, new Vector3(3, 0, 3), 1u, "Anvil",
            EntityKind.Unknown, null, _t0);
        g1.Dispose(); // release writer locks before reopening (simulates restart)
        var g2 = NewGraph();
        Assert.Equal(1, g2.SightedCount);
    }

    [Fact]
    public void RecordSightedLocation_marks_vendor_when_flagged()
    {
        // The vendor wire bit observed at sighting time is stored on the
        // remembered location so the recall projection can surface it.
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(5, 0, 5), 10u, "Shopkeeper",
            EntityKind.NPC, null, _t0, isVendor: true);
        Assert.True(g.SnapshotSighted().Single().IsVendor);
    }

    [Fact]
    public void RecordSightedLocation_defaults_non_vendor()
    {
        // The default is non-vendor — a plain sighting must not be marked.
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(5, 0, 5), 11u, "Wanderer",
            EntityKind.NPC, null, _t0);
        Assert.False(g.SnapshotSighted().Single().IsVendor);
    }

    [Fact]
    public void RecordSightedLocation_vendor_flag_is_sticky_across_resight()
    {
        // Once seen as a vendor, a later re-sighting whose wire flags
        // momentarily lack the bit must NOT un-mark the memory.
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(5, 0, 5), 12u, "Shopkeeper",
            EntityKind.NPC, null, _t0, isVendor: true);
        g.RecordSightedLocation(IndoorCell, new Vector3(5.5f, 0, 5), 12u, "Shopkeeper",
            EntityKind.NPC, null, _t0.AddSeconds(1), isVendor: false); // same entity
        var loc = g.SnapshotSighted().Single();
        Assert.Equal(2, loc.SightingCount);
        Assert.True(loc.IsVendor);
    }

    [Fact]
    public void RecordSightedLocation_vendor_promotion_requires_name_match()
    {
        // Defensive guard: sightings merge by wcid (a class/template id, not a
        // unique instance id). A later sighting of a DIFFERENT-named object that
        // shares the wcid within the merge radius must NOT stamp its vendor-ness
        // onto the stored (differently-named) remembered identity.
        var g = NewGraph();
        g.RecordSightedLocation(IndoorCell, new Vector3(5, 0, 5), 20u, "Plain NPC",
            EntityKind.NPC, null, _t0, isVendor: false);
        g.RecordSightedLocation(IndoorCell, new Vector3(5.5f, 0, 5), 20u, "Vendor NPC",
            EntityKind.NPC, null, _t0.AddSeconds(1), isVendor: true); // merges by wcid
        var loc = g.SnapshotSighted().Single();
        Assert.Equal(2, loc.SightingCount);   // confirms they merged
        Assert.False(loc.IsVendor);           // vendor-ness did NOT bleed across names
    }

    [Fact]
    public void Sighted_vendor_flag_round_trips_through_persistence()
    {
        // The vendor mark must survive the journal write/reload round-trip
        // (SightedLocationDto), else a reconnect loses it.
        var g1 = NewGraph();
        g1.RecordSightedLocation(IndoorCell, new Vector3(3, 0, 3), 13u, "Shopkeeper",
            EntityKind.NPC, null, _t0, isVendor: true);
        g1.Dispose();
        var g2 = NewGraph();
        Assert.True(g2.SnapshotSighted().Single().IsVendor);
    }
}
