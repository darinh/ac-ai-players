// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public sealed class NavGraphTests : IDisposable
{
    private readonly string _dir;
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow;

    public NavGraphTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "navgraph-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private NavGraph NewGraph() => new(_dir) { FlushInterval = TimeSpan.Zero };

    [Fact]
    public void RecordVisit_dedupes_nearby_positions_within_merge_radius()
    {
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var a = g.RecordVisit(cell, new Vector3(10, 0, 10), _t0);
        var b = g.RecordVisit(cell, new Vector3(12, 0, 11), _t0.AddSeconds(1)); // ~2.24m
        Assert.Equal(a, b);
        Assert.Equal(1, g.NodeCount);
        var node = g.FindNode(a)!;
        Assert.Equal(2, node.VisitCount);
    }

    [Fact]
    public void RecordVisit_creates_distinct_node_beyond_merge_radius()
    {
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var a = g.RecordVisit(cell, new Vector3(0, 0, 0), _t0);
        var b = g.RecordVisit(cell, new Vector3(10, 0, 0), _t0.AddSeconds(1));
        Assert.NotEqual(a, b);
        Assert.Equal(2, g.NodeCount);
    }

    [Fact]
    public void RecordVisit_auto_walked_edge_within_same_landblock_under_max_distance()
    {
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var a = g.RecordVisit(cell, new Vector3(0, 0, 0), _t0);
        var b = g.RecordVisit(cell, new Vector3(10, 0, 0), _t0.AddSeconds(1));
        var edges = g.SnapshotEdges();
        Assert.Single(edges);
        Assert.Equal(NavEdgeKind.Walked, edges[0].Kind);
        Assert.Equal(a, edges[0].FromNodeId);
        Assert.Equal(b, edges[0].ToNodeId);
    }

    [Fact]
    public void RecordVisit_does_not_auto_edge_across_landblocks()
    {
        // Cross-landblock transitions MUST be explicit (door/portal/item).
        // Auto-Walked edges across landblock boundaries would let the
        // router believe two distant cells are walkable when in fact
        // the bot teleported.
        var g = NewGraph();
        var inAcademy = g.RecordVisit(0x860201ADu, new Vector3(60, 0, -35), _t0);
        var inHoltburg = g.RecordVisit(0xA9B40000u | 0x100u, new Vector3(84, 7, 94), _t0.AddSeconds(2));
        Assert.NotEqual(inAcademy, inHoltburg);
        Assert.Equal(2, g.NodeCount);
        Assert.Equal(0, g.EdgeCount); // no auto-edge
    }

    [Fact]
    public void RecordVisit_does_not_auto_edge_for_large_in_landblock_jumps()
    {
        // Same landblock, but jumped > MaxAutoEdgeMeters apart. This is
        // the "teleport / spawn within landblock" guard — a real walk
        // would have intermediate ticks.
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        g.RecordVisit(cell, new Vector3(0, 0, 0), _t0);
        g.RecordVisit(cell, new Vector3(100, 0, 0), _t0.AddSeconds(1)); // > 20m
        Assert.Equal(2, g.NodeCount);
        Assert.Equal(0, g.EdgeCount); // gate kicked in
    }

    [Fact]
    public void Wall_shortcut_is_blocked_when_no_edge_was_recorded()
    {
        // Two nodes close in space but in DIFFERENT indoor cells (low
        // 16 bits >= 0x100). Indoor cells = walled rooms — a wall may
        // sit between them. Pathfinder must NOT synthesize an edge
        // from chronological adjacency alone for indoor cell pairs;
        // the driver must explicitly RecordEdge with UsedDoor.
        var g = NewGraph();
        var nodeA = g.RecordVisit(0x86020101u, new Vector3(50, 0, 50), _t0);
        var nodeB = g.RecordVisit(0x86020102u, new Vector3(51, 0, 51), _t0.AddSeconds(1));
        Assert.NotEqual(nodeA, nodeB);
        var route = g.FindRoute(nodeA, nodeB);
        Assert.Null(route);
    }

    [Fact]
    public void RecordVisit_auto_edges_across_outdoor_cells_in_same_landblock()
    {
        // Outdoor cells (low 16 bits < 0x100) are continuous terrain.
        // Walking naturally crosses cell boundaries every 24m. These
        // SHOULD auto-edge — they're not walled off.
        var g = NewGraph();
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var b = g.RecordVisit(0x86020002u, new Vector3(5, 0, 0), _t0.AddSeconds(1));
        Assert.NotEqual(a, b);
        var edges = g.SnapshotEdges();
        Assert.Single(edges);
        Assert.Equal(NavEdgeKind.Walked, edges[0].Kind);
    }

    [Fact]
    public void RecordObservation_attaches_to_node_with_relative_position_and_distance()
    {
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var nid = g.RecordVisit(cell, new Vector3(10, 0, 10), _t0);
        g.RecordObservation(nid, wcid: 30991, name: "Society Greeter",
            entityPosition: new Vector3(13, 0, 14), kind: EntityKind.NPC, utc: _t0);
        var node = g.FindNode(nid)!;
        var obs = Assert.Single(node.Observations);
        Assert.Equal("Society Greeter", obs.Name);
        Assert.Equal(5f, obs.Distance, precision: 2); // sqrt(9+16)
        Assert.Equal(new Vector3(3, 0, 4), obs.RelativePosition);
        Assert.Equal(1, obs.SightingCount);
    }

    [Fact]
    public void RecordObservation_increments_sighting_count_for_same_entity()
    {
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var nid = g.RecordVisit(cell, new Vector3(10, 0, 10), _t0);
        g.RecordObservation(nid, 30991, "Society Greeter",
            new Vector3(13, 0, 14), EntityKind.NPC, _t0);
        g.RecordObservation(nid, 30991, "Society Greeter",
            new Vector3(13, 0, 14), EntityKind.NPC, _t0.AddMinutes(5));
        var obs = Assert.Single(g.FindNode(nid)!.Observations);
        Assert.Equal(2, obs.SightingCount);
        Assert.Equal(_t0.AddMinutes(5), obs.LastSeenUtc);
    }

    [Fact]
    public void FindEntity_returns_sightings_sorted_by_recency()
    {
        var g = NewGraph();
        var n1 = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var n2 = g.RecordVisit(0x86020002u, new Vector3(100, 0, 100), _t0.AddSeconds(30));
        g.RecordObservation(n1, 30991, "Society Greeter", new Vector3(5, 0, 0), EntityKind.NPC, _t0);
        g.RecordObservation(n2, 30991, "Society Greeter", new Vector3(105, 0, 100), EntityKind.NPC, _t0.AddMinutes(10));
        var hits = g.FindEntity("greeter");
        Assert.Equal(2, hits.Count);
        Assert.Equal(n2, hits[0].Node.Id); // most recent first
        Assert.Equal(n1, hits[1].Node.Id);
    }

    [Fact]
    public void FindRoute_returns_lower_cost_path_when_multiple_exist()
    {
        var g = NewGraph();
        // Build a diamond: A → B → D vs A → C → D, where A-B-D is shorter.
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var b = g.RecordVisit(0x86020001u, new Vector3(5, 0, 0), _t0.AddSeconds(1));
        // Reset chronological continuation so the next visit doesn't auto-edge.
        var c = NewGraph(); // dummy; we'll build C manually via RecordEdge instead
        // Actually use RecordEdge for the C side to control costs precisely.
        var cN = g.RecordVisit(0x86020001u, new Vector3(15, 0, 0), _t0.AddSeconds(2)); // distant — won't auto-edge
        var d = g.RecordVisit(0x86020001u, new Vector3(15, 0, 5), _t0.AddSeconds(3));
        // The auto-edge from cN to d (distance 5) will exist — that's fine.
        // Add the short B→D direct edge.
        g.RecordEdge(b, d, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        var route = g.FindRoute(a, d);
        Assert.NotNull(route);
        // Path through B should be cheaper than path through cN.
        Assert.Contains(route!.Steps, s => s.Node.Id == b);
        Assert.Equal(a, route.Steps.First().Node.Id);
        Assert.Equal(d, route.Steps.Last().Node.Id);
    }

    [Fact]
    public void FindRoute_returns_null_for_disconnected_nodes()
    {
        var g = NewGraph();
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var b = g.RecordVisit(0xA9B40001u, new Vector3(0, 0, 0), _t0.AddSeconds(10));
        Assert.Null(g.FindRoute(a, b));
    }

    [Fact]
    public void FindRoute_single_node_returns_zero_cost_route()
    {
        var g = NewGraph();
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var route = g.FindRoute(a, a);
        Assert.NotNull(route);
        Assert.Equal(0f, route!.TotalCostSeconds);
        Assert.Single(route.Steps);
    }

    [Fact]
    public void RecordEdge_uses_kind_specific_cost()
    {
        var g = NewGraph();
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var b = g.RecordVisit(0xA9B40001u, new Vector3(0, 0, 0), _t0.AddSeconds(5));
        var edgeId = g.RecordEdge(a, b, NavEdgeKind.UsedPortal,
            useItemName: "Free Ride to Holtburg", useObjectGuid: 0xDEADBEEF, utc: _t0.AddSeconds(6));
        var edge = g.SnapshotEdges().Single(e => e.Id == edgeId);
        Assert.Equal(NavEdgeKind.UsedPortal, edge.Kind);
        Assert.Equal(5.0f, edge.CostSeconds);
        Assert.Equal("Free Ride to Holtburg", edge.UseItemName);
    }

    [Fact]
    public void RecordEdge_dedupes_by_endpoints_and_kind()
    {
        var g = NewGraph();
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var b = g.RecordVisit(0xA9B40001u, new Vector3(0, 0, 0), _t0.AddSeconds(5));
        var e1 = g.RecordEdge(a, b, NavEdgeKind.UsedPortal, "Free Ride", 0x123u, _t0.AddSeconds(6));
        var e2 = g.RecordEdge(a, b, NavEdgeKind.UsedPortal, "Free Ride", 0x123u, _t0.AddSeconds(60));
        Assert.Equal(e1, e2);
        Assert.Equal(1, g.EdgeCount);
        var edge = g.SnapshotEdges().Single();
        Assert.Equal(_t0.AddSeconds(60), edge.LastVerifiedUtc);
    }

    [Fact]
    public void Metadata_tagging_then_query_finds_place()
    {
        var g = NewGraph();
        var nid = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var node = g.FindNode(nid)!;
        var area = g.FindArea(node.AreaId!.Value)!;
        var place = g.FindPlace(area.PlaceId)!;

        g.TagPlace(place.Id, name: "Holtburg Training Academy", kind: PlaceKind.Building,
            addTags: new[] { "tutorial", "starter" });
        g.TagArea(area.Id, kind: AreaKind.Room, floor: 1, roomName: "Greeting Hall");

        var byName = g.QueryPlaces(name: "Holtburg");
        Assert.Single(byName);
        var byTag = g.QueryPlaces(tag: "tutorial");
        Assert.Single(byTag);
        var byKind = g.QueryPlaces(kind: PlaceKind.Building);
        Assert.Single(byKind);

        var rooms = g.QueryAreas(placeId: place.Id, kind: AreaKind.Room, floor: 1, roomName: "greeting");
        Assert.Single(rooms);
    }

    [Fact]
    public void EnsureRegion_and_EnsureArea_are_idempotent()
    {
        var g = NewGraph();
        var r1 = g.EnsureRegion(0x8602);
        var r2 = g.EnsureRegion(0x8602);
        Assert.Equal(r1, r2);
        Assert.Equal(1, g.RegionCount);

        var a1 = g.EnsureArea(0x860201ADu);
        var a2 = g.EnsureArea(0x860201ADu);
        Assert.Equal(a1, a2);
        Assert.Equal(1, g.AreaCount);
    }

    [Fact]
    public void Journal_roundtrip_preserves_full_state_across_restart()
    {
        var nidA = Guid.Empty; var nidB = Guid.Empty;
        Guid placeId; Guid areaId;
        {
            var g = NewGraph();
            nidA = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
            nidB = g.RecordVisit(0x86020001u, new Vector3(5, 0, 0), _t0.AddSeconds(1));
            g.RecordObservation(nidB, 30991, "Society Greeter",
                new Vector3(10, 0, 0), EntityKind.NPC, _t0.AddSeconds(2));
            areaId = g.FindNode(nidA)!.AreaId!.Value;
            placeId = g.FindArea(areaId)!.PlaceId;
            g.TagPlace(placeId, name: "Academy", kind: PlaceKind.Building);
            g.TagArea(areaId, kind: AreaKind.Hall, roomName: "Lobby");
            g.Flush();
        }
        {
            var g2 = NewGraph();
            Assert.Equal(2, g2.NodeCount);
            Assert.Equal(1, g2.EdgeCount); // the auto-Walked edge
            Assert.Equal(1, g2.PlaceCount);
            Assert.Equal(1, g2.AreaCount);
            Assert.Equal(1, g2.RegionCount);

            var nodeA = g2.FindNode(nidA);
            Assert.NotNull(nodeA);
            Assert.Equal(new Vector3(0, 0, 0), nodeA!.Position);

            var nodeB = g2.FindNode(nidB);
            Assert.NotNull(nodeB);
            var obs = Assert.Single(nodeB!.Observations);
            Assert.Equal("Society Greeter", obs.Name);
            Assert.Equal(30991u, obs.Wcid);

            var place = g2.FindPlace(placeId);
            Assert.NotNull(place);
            Assert.Equal("Academy", place!.Name);
            Assert.Equal(PlaceKind.Building, place.Kind);

            var area = g2.FindArea(areaId);
            Assert.NotNull(area);
            Assert.Equal(AreaKind.Hall, area!.Kind);
            Assert.Equal("Lobby", area.RoomName);

            // Routing survives restart
            Assert.NotNull(g2.FindRoute(nidA, nidB));
        }
    }

    [Fact]
    public void FindRouteToEntity_routes_to_node_where_entity_was_last_seen()
    {
        var g = NewGraph();
        var start = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        var mid   = g.RecordVisit(0x86020001u, new Vector3(5, 0, 0), _t0.AddSeconds(1));
        var seen  = g.RecordVisit(0x86020001u, new Vector3(10, 0, 0), _t0.AddSeconds(2));
        g.RecordObservation(seen, 30991, "Society Greeter",
            new Vector3(11, 0, 0), EntityKind.NPC, _t0.AddSeconds(2));
        var route = g.FindRouteToEntity(start, "Greeter");
        Assert.NotNull(route);
        Assert.Equal(start, route!.Steps.First().Node.Id);
        Assert.Equal(seen, route.Steps.Last().Node.Id);
    }

    [Fact]
    public void FindNearestNode_searches_within_same_cell_only()
    {
        var g = NewGraph();
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        g.RecordVisit(0x86020002u, new Vector3(1, 0, 1), _t0.AddSeconds(1)); // different cell
        var near = g.FindNearestNode(0x86020001u, new Vector3(1, 0, 1), maxDistance: 50f);
        Assert.NotNull(near);
        Assert.Equal(a, near!.Id);
    }
}
