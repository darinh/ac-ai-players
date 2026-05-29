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
    private readonly List<NavGraph> _graphs = new();

    public NavGraphTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "navgraph-tests-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// Walks from <paramref name="from"/> to <paramref name="to"/> in
    /// per-tick steps of at most ~1.5 m (under the 2 m chain gate) in
    /// the given cell. Records every tick, returning the final node
    /// id. Use whenever a test needs auto-Walked edges to form.
    /// </summary>
    private Guid WalkVisit(NavGraph g, uint cell, Vector3 from, Vector3 to,
                            ref DateTimeOffset t, float stepMeters = 1.5f)
    {
        g.RecordVisit(cell, from, t);
        var delta = to - from;
        var distance = delta.Length();
        if (distance == 0)
        {
            t = t.AddSeconds(0.5);
            return g.RecordVisit(cell, to, t);
        }
        var steps = Math.Max(1, (int)Math.Ceiling(distance / stepMeters));
        Guid last = Guid.Empty;
        for (var i = 1; i <= steps; i++)
        {
            var f = (float)i / steps;
            t = t.AddSeconds(0.5);
            last = g.RecordVisit(cell, from + delta * f, t);
        }
        return last;
    }

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
        var t = _t0;
        var first  = g.RecordVisit(cell, new Vector3(0, 0, 0), t);
        var second = WalkVisit(g, cell, new Vector3(0, 0, 0), new Vector3(5, 0, 0), ref t);
        Assert.NotEqual(first, second);
        var edges = g.SnapshotEdges();
        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.Equal(NavEdgeKind.Walked, e.Kind));
        // The chain ends at `second` and starts at the first node, so
        // there must be a path between them.
        Assert.NotNull(g.FindRoute(first, second));
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
        // Walking naturally crosses cell boundaries every 24m. With
        // per-tick chain verification, the bot must walk in steps ≤
        // MaxTickWalkMeters across the boundary for the chain to hold.
        var g = NewGraph();
        var t = _t0;
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), t);
        // Tick along the boundary, staying in cell A then crossing.
        t = t.AddSeconds(0.5); g.RecordVisit(0x86020001u, new Vector3(1.5f, 0, 0), t);
        t = t.AddSeconds(0.5); g.RecordVisit(0x86020001u, new Vector3(3.0f, 0, 0), t);
        t = t.AddSeconds(0.5); var b = g.RecordVisit(0x86020002u, new Vector3(4.5f, 0, 0), t);
        t = t.AddSeconds(0.5); g.RecordVisit(0x86020002u, new Vector3(6.0f, 0, 0), t);
        Assert.NotEqual(a, b);
        var edges = g.SnapshotEdges();
        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.Equal(NavEdgeKind.Walked, e.Kind));
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
        // Build a diamond explicitly with RecordEdge so we control the
        // costs. The per-tick chain would also auto-edge nearby visits;
        // using BreakWalkedChain between visits keeps the topology clean.
        const uint cell = 0x86020001u;
        var a = g.RecordVisit(cell, new Vector3(0,  0, 0), _t0);
        g.BreakWalkedChain();
        var b = g.RecordVisit(cell, new Vector3(5,  0, 0), _t0.AddSeconds(1));
        g.BreakWalkedChain();
        var c = g.RecordVisit(cell, new Vector3(0,  0, 5), _t0.AddSeconds(2));
        g.BreakWalkedChain();
        var d = g.RecordVisit(cell, new Vector3(5,  0, 5), _t0.AddSeconds(3));
        // A→B (short), B→D (short), A→C (long), C→D (long).
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        g.RecordEdge(b, d, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        g.RecordEdge(a, c, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        g.RecordEdge(c, d, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        // Make C→D expensive so the router prefers A→B→D.
        var slowEdgeId = g.SnapshotEdges().Single(e => e.FromNodeId == c && e.ToNodeId == d).Id;
        g.PenalizeEdge(slowEdgeId, 100f);
        var route = g.FindRoute(a, d);
        Assert.NotNull(route);
        Assert.Contains(route!.Steps, s => s.Node.Id == b);
        Assert.DoesNotContain(route.Steps, s => s.Node.Id == c);
        Assert.Equal(a, route.Steps.First().Node.Id);
        Assert.Equal(d, route.Steps.Last().Node.Id);
    }

    [Fact]
    public void FindRoute_returns_null_for_disconnected_nodes()
    {
        var g = NewGraph();
        var a = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        // Different landblock → chain breaks regardless of position
        // (per-tick gate REQUIRES same landblock; cell-local positions
        // aren't comparable across landblock boundaries).
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
        // Same-landblock chain gate ensures no auto-edge across landblocks.
        Assert.Equal(0, g.EdgeCount);
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
            var t = _t0;
            nidA = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), t);
            nidB = WalkVisit(g, 0x86020001u, new Vector3(0, 0, 0), new Vector3(5, 0, 0), ref t);
            g.RecordObservation(nidB, 30991, "Society Greeter",
                new Vector3(10, 0, 0), EntityKind.NPC, t.AddSeconds(1));
            areaId = g.FindNode(nidA)!.AreaId!.Value;
            placeId = g.FindArea(areaId)!.PlaceId;
            g.TagPlace(placeId, name: "Academy", kind: PlaceKind.Building);
            g.TagArea(areaId, kind: AreaKind.Hall, roomName: "Lobby");
            g.Flush();
            g.Dispose(); // release writer locks before reopening
        }
        {
            var g2 = NewGraph();
            Assert.Equal(2, g2.NodeCount);
            Assert.True(g2.EdgeCount >= 1, "auto-Walked edge from the stepped walk should persist");
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
        var t = _t0;
        var start = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), t);
        var seen  = WalkVisit(g, 0x86020001u, new Vector3(0, 0, 0), new Vector3(10, 0, 0), ref t);
        g.RecordObservation(seen, 30991, "Society Greeter",
            new Vector3(11, 0, 0), EntityKind.NPC, t.AddSeconds(1));
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

    [Fact]
    public void Per_tick_chain_breaks_on_single_oversized_jump()
    {
        // Confirms the wall-shortcut fix: even within a single cell,
        // two non-adjacent ticks (delta > MaxTickWalkMeters) must NOT
        // form a Walked edge. A wall could sit between them.
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var a = g.RecordVisit(cell, new Vector3(0,  0, 0), _t0);
        // Jump 10 m in a single tick — vastly more than 2 m/tick.
        var b = g.RecordVisit(cell, new Vector3(10, 0, 0), _t0.AddSeconds(0.5));
        Assert.NotEqual(a, b);
        Assert.Equal(0, g.EdgeCount);
    }

    [Fact]
    public void Per_tick_chain_resumes_after_break()
    {
        // Once the chain breaks, a stepwise walk from the new position
        // should start forming auto-edges again.
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var t = _t0;
        var a = g.RecordVisit(cell, new Vector3(0,  0, 0), t);
        t = t.AddSeconds(0.5);
        var b = g.RecordVisit(cell, new Vector3(20, 0, 0), t); // jump — breaks chain
        Assert.Equal(0, g.EdgeCount);
        // Now walk from b stepwise to c.
        var c = WalkVisit(g, cell, new Vector3(20, 0, 0), new Vector3(30, 0, 0), ref t);
        Assert.NotEqual(b, c);
        // Edges should now exist along the b→c walk.
        Assert.NotEmpty(g.SnapshotEdges());
        Assert.NotNull(g.FindRoute(b, c));
        // But there should still be NO route from a to c (chain broke).
        Assert.Null(g.FindRoute(a, c));
    }

    [Fact]
    public void BreakWalkedChain_prevents_next_auto_edge()
    {
        var g = NewGraph();
        const uint cell = 0x860201ADu;
        var t = _t0;
        // Stepwise walk forms an edge.
        var a = g.RecordVisit(cell, new Vector3(0, 0, 0), t);
        t = t.AddSeconds(0.5);
        var b = g.RecordVisit(cell, new Vector3(1.5f, 0, 0), t);
        // a and b dedupe (within MergeRadius=4), so still 1 node + 0 edges.
        Assert.Equal(a, b);
        Assert.Equal(0, g.EdgeCount);
        g.BreakWalkedChain();
        // Even though the next visit is within MaxTickWalkMeters, the
        // explicit break suppresses the auto-edge.
        t = t.AddSeconds(0.5);
        var c = g.RecordVisit(cell, new Vector3(3.0f, 0, 0), t);
        Assert.Equal(0, g.EdgeCount);
    }

    [Fact]
    public void GetOutgoingConnections_returns_all_edges_from_a_node()
    {
        var g = NewGraph();
        const uint cell = 0x86020001u;
        var a = g.RecordVisit(cell, new Vector3(0, 0, 0), _t0);
        g.BreakWalkedChain();
        var b = g.RecordVisit(cell, new Vector3(5, 0, 0), _t0.AddSeconds(1));
        g.BreakWalkedChain();
        var c = g.RecordVisit(cell, new Vector3(0, 0, 5), _t0.AddSeconds(2));
        g.RecordEdge(a, b, NavEdgeKind.Walked,    null, null, _t0.AddSeconds(3));
        g.RecordEdge(a, c, NavEdgeKind.UsedDoor,  null, null, _t0.AddSeconds(4));
        var outs = g.GetOutgoingConnections(a);
        Assert.Equal(2, outs.Count);
        Assert.Contains(outs, o => o.To.Id == b && o.Edge.Kind == NavEdgeKind.Walked);
        Assert.Contains(outs, o => o.To.Id == c && o.Edge.Kind == NavEdgeKind.UsedDoor);
    }

    [Fact]
    public void GetOutgoingConnections_returns_empty_for_unknown_or_isolated_node()
    {
        var g = NewGraph();
        var isolated = g.RecordVisit(0x86020001u, new Vector3(0, 0, 0), _t0);
        Assert.Empty(g.GetOutgoingConnections(isolated));
        Assert.Empty(g.GetOutgoingConnections(Guid.NewGuid()));
    }

    [Fact]
    public void FindNodesWithin_returns_nodes_sorted_by_distance()
    {
        var g = NewGraph();
        // All in same landblock 0x8602, different cells.
        var n1 = g.RecordVisit(0x86020001u, new Vector3(0,  0, 0),  _t0);
        var n2 = g.RecordVisit(0x86020001u, new Vector3(10, 0, 0),  _t0.AddSeconds(10));
        var n3 = g.RecordVisit(0x86020002u, new Vector3(0,  0, 20), _t0.AddSeconds(20));
        // Different landblock — must be excluded.
        var nFar = g.RecordVisit(0xA9B40001u, new Vector3(1, 0, 1), _t0.AddSeconds(30));

        var hits = g.FindNodesWithin(0x86020001u, new Vector3(0, 0, 0), radiusMeters: 25f);
        Assert.Equal(3, hits.Count);
        Assert.Equal(n1, hits[0].Node.Id); // distance 0
        Assert.Equal(n2, hits[1].Node.Id); // distance 10
        Assert.Equal(n3, hits[2].Node.Id); // distance 20
        Assert.DoesNotContain(hits, h => h.Node.Id == nFar);
    }

    [Fact]
    public void FindNodesWithin_respects_radius()
    {
        var g = NewGraph();
        var n1 = g.RecordVisit(0x86020001u, new Vector3(0,  0, 0),  _t0);
        g.RecordVisit(0x86020002u, new Vector3(50, 0, 0), _t0.AddSeconds(1));
        var hits = g.FindNodesWithin(0x86020001u, new Vector3(0, 0, 0), radiusMeters: 10f);
        Assert.Single(hits);
        Assert.Equal(n1, hits[0].Node.Id);
    }

    [Fact]
    public void PenalizeEdge_makes_router_prefer_alternate_path()
    {
        // Diamond: A→B→D and A→C→D, both initially equal cost.
        // Penalize B→D and verify the router switches to A→C→D.
        var g = NewGraph();
        const uint cell = 0x86020001u;
        var a = g.RecordVisit(cell, new Vector3(0,  0, 0), _t0);
        g.BreakWalkedChain();
        var b = g.RecordVisit(cell, new Vector3(10, 0, 0), _t0.AddSeconds(1));
        g.BreakWalkedChain();
        var c = g.RecordVisit(cell, new Vector3(0,  0, 10), _t0.AddSeconds(2));
        g.BreakWalkedChain();
        var d = g.RecordVisit(cell, new Vector3(10, 0, 10), _t0.AddSeconds(3));
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        g.RecordEdge(b, d, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        g.RecordEdge(a, c, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        g.RecordEdge(c, d, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        var before = g.FindRoute(a, d);
        Assert.NotNull(before);
        // Both routes equal cost; either is acceptable. Penalize whichever
        // path the planner currently prefers and verify it switches.
        var preferredMid = before!.Steps[1].Node.Id;
        var preferredEdgeFromA = g.SnapshotEdges().Single(e => e.FromNodeId == a && e.ToNodeId == preferredMid);
        g.PenalizeEdge(preferredEdgeFromA.Id, 100f);
        var after = g.FindRoute(a, d);
        Assert.NotNull(after);
        Assert.NotEqual(preferredMid, after!.Steps[1].Node.Id);
    }

    [Fact]
    public void FindRoute_uses_admissible_euclidean_heuristic()
    {
        // Heuristic admissibility check: even with a cheap fixed-cost
        // edge kind (UsedDoor base cost 0.5 regardless of geometric
        // distance), A* must still return the optimal (cheapest) path.
        // Without the running _minCostPerMeter scale this case would
        // prune the direct door edge.
        var g = NewGraph();
        const uint cell = 0x86020001u;
        var a = g.RecordVisit(cell, new Vector3(0,    0, 0), _t0);
        g.BreakWalkedChain();
        var b = g.RecordVisit(cell, new Vector3(50,   0, 0), _t0.AddSeconds(1));
        g.BreakWalkedChain();
        var c = g.RecordVisit(cell, new Vector3(100,  0, 0), _t0.AddSeconds(2));
        // Direct A→C via door — fixed cost 0.5 regardless of 100 m gap.
        g.RecordEdge(a, c, NavEdgeKind.UsedDoor, null, null, _t0.AddSeconds(3));
        // Indirect A→B→C via walked — 1.0 × 50 + 1.0 × 50 = 100.
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(3));
        g.RecordEdge(b, c, NavEdgeKind.Walked, null, null, _t0.AddSeconds(3));
        var route = g.FindRoute(a, c);
        Assert.NotNull(route);
        // Optimal path is the direct door edge.
        Assert.Equal(2, route!.Steps.Count);
        Assert.Equal(a, route.Steps[0].Node.Id);
        Assert.Equal(c, route.Steps[1].Node.Id);
        Assert.Equal(NavEdgeKind.UsedDoor, route.Steps[1].EdgeFromPrevious!.Kind);
        Assert.Equal(0.5f, route.TotalCostSeconds, precision: 2);
    }

    [Fact]
    public void FindRoute_prefers_portal_shortcut_over_long_walk()
    {
        // Two-node portal model: entrance node (Holtburg side) and
        // exit node (academy side) are SEPARATE nodes with their own
        // world-coord positions, connected by a single UsedPortal edge
        // whose cost is the cast time (fixed), independent of the
        // 5 km geometric gap. Goal: reach a destination geometrically
        // far from the bot but close to the portal exit. A* must pick
        // the portal route (a→portalIn→portalOut→goal, cost ≈ 5 + 1)
        // instead of trying to walk the 5 km straight line.
        var g = NewGraph();
        const uint townCell = 0x86020001u;
        const uint acadCell = 0xA9B40001u;
        var t = _t0;
        var here       = g.RecordVisit(townCell, new Vector3(  0, 0,   0), t);
        g.BreakWalkedChain();
        var portalIn   = g.RecordVisit(townCell, new Vector3(  5, 0,   0), t.AddSeconds(1));
        g.BreakWalkedChain();
        // Portal exit is in a different landblock, geometrically distant.
        var portalOut  = g.RecordVisit(acadCell, new Vector3(5000, 0, 5000), t.AddSeconds(2));
        g.BreakWalkedChain();
        var goalNode   = g.RecordVisit(acadCell, new Vector3(5010, 0, 5000), t.AddSeconds(3));

        // Walk legs on either side of the portal (~1 m).
        g.RecordEdge(here,      portalIn,  NavEdgeKind.Walked,     null, null, t.AddSeconds(4));
        g.RecordEdge(portalOut, goalNode,  NavEdgeKind.Walked,     null, null, t.AddSeconds(4));
        // The portal itself: fixed 5 s teleport, regardless of 5 km span.
        g.RecordEdge(portalIn,  portalOut, NavEdgeKind.UsedPortal, "Town Portal", null, t.AddSeconds(4));

        var route = g.FindRoute(here, goalNode);
        Assert.NotNull(route);
        Assert.Equal(4, route!.Steps.Count);
        Assert.Equal(here,      route.Steps[0].Node.Id);
        Assert.Equal(portalIn,  route.Steps[1].Node.Id);
        Assert.Equal(portalOut, route.Steps[2].Node.Id);
        Assert.Equal(goalNode,  route.Steps[3].Node.Id);
        Assert.Equal(NavEdgeKind.UsedPortal, route.Steps[2].EdgeFromPrevious!.Kind);
        // Cost ≈ 5 (walk to portal: ~5 m) + 5 (portal cast) + 1 (walk
        // from exit to goal). Definitely far less than walking 5 km.
        Assert.True(route.TotalCostSeconds < 100f,
            $"expected portal route < 100 s, got {route.TotalCostSeconds}");
    }
}
