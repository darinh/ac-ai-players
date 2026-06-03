// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Slice 6 (cross-cell on-foot traversal): NavGraph.PlanWaypointToward —
/// route-guided execution toward a remembered location in another
/// landblock, over the bot's OWN explored connectivity. The planner now
/// returns the contiguous prefix of route nodes that stay in the bot's
/// CURRENT landblock and are reachable by on-foot (Walked / CrossedBoundary)
/// edges only, walking the bot cell-to-cell up to the landblock boundary.
/// It stops BEFORE the first hop that leaves the landblock or uses a
/// door/portal/item (TransitionPending), or reports no explored route
/// (NoRoute). The start anchor must be a node in the bot's EXACT current
/// cell (no landblock-wide fallback for an executor).
/// </summary>
public sealed class NavRoutePlanTests : IDisposable
{
    private readonly string _dir;
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow;
    private readonly List<NavGraph> _graphs = new();

    // Bot's landblock (0x8602), two cells within it; a second landblock
    // (0xA9B4) reached via a portal edge; a third (0x8603) reached on foot.
    private const uint TownCellA = 0x86020001u; // bot's cell
    private const uint TownCellB = 0x86020002u; // same landblock, other cell
    private const uint AcadCell  = 0xA9B40001u; // other landblock (portal)
    private const uint NextCell  = 0x8603001Au; // other landblock (on foot)

    public NavRoutePlanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "navplan-tests-" + Guid.NewGuid().ToString("N"));
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

    private Guid Visit(NavGraph g, uint cell, Vector3 pos, double sec)
    {
        var id = g.RecordVisit(cell, pos, _t0.AddSeconds(sec));
        g.BreakWalkedChain();
        return id;
    }

    /// <summary>
    /// Build A->B->C (same cell, walked) -> [portal] -> D -> goal in a
    /// second landblock. Returns the node ids.
    /// </summary>
    private (Guid a, Guid b, Guid c, Guid d, Guid goal) BuildCrossLandblockRoute(NavGraph g)
    {
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, TownCellA, new Vector3(10, 0, 0), 1);
        var c = Visit(g, TownCellA, new Vector3(20, 0, 0), 2);
        var d = Visit(g, AcadCell, new Vector3(5000, 0, 5000), 3);
        var goal = Visit(g, AcadCell, new Vector3(5010, 0, 5000), 4);

        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(b, c, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(c, d, NavEdgeKind.UsedPortal, null, null, _t0.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        return (a, b, c, d, goal);
    }

    [Fact]
    public void Plan_advances_the_full_walked_prefix_and_stops_before_a_portal()
    {
        var g = NewGraph();
        var (_, b, c, _, _) = BuildCrossLandblockRoute(g);

        // Bot at A; remembered target near the goal node in the other lb.
        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        // Waypoints are the forward hops after the start anchor (A): B then C.
        Assert.Equal(new[] { b, c }, plan.Waypoints.Select(n => n.Id).ToArray());
        // Waypoints[0] is the immediate next hop (B); BoundaryNode the stop (C).
        Assert.Equal(b, plan.Waypoints[0].Id);
        Assert.Equal(c, plan.BoundaryNode!.Id);
        // The blocking hop after C is the portal into landblock 0xA9B4.
        Assert.Equal((ushort)0xA9B4, plan.NextLandblock);
        Assert.Equal(NavEdgeKind.UsedPortal, plan.NextEdgeKind);
    }

    [Fact]
    public void Plan_path_cells_include_the_bots_starting_cell()
    {
        var g = NewGraph();
        BuildCrossLandblockRoute(g);

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        // The motor's cell-crossing gate compares against this set; it must
        // contain the bot's starting cell so the very first walk-tick (still
        // in TownCellA) does not trip the gate.
        Assert.Contains(TownCellA, plan.PathCells);
    }

    [Fact]
    public void Plan_advances_across_cells_within_the_same_landblock()
    {
        var g = NewGraph();
        // A->B same cell (walked), B->E different cell SAME landblock
        // (walked), E->[portal]->D other landblock. Slice 6 walks THROUGH
        // the intra-landblock cell boundary (B->E), unlike Slice 5.
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, TownCellA, new Vector3(10, 0, 0), 1);
        var e = Visit(g, TownCellB, new Vector3(30, 0, 0), 2);
        var d = Visit(g, AcadCell, new Vector3(5000, 0, 5000), 3);
        var goal = Visit(g, AcadCell, new Vector3(5010, 0, 5000), 4);
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(b, e, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(e, d, NavEdgeKind.UsedPortal, null, null, _t0.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        // Walks through both cells of landblock 0x8602 to the boundary node E.
        Assert.Equal(new[] { b, e }, plan.Waypoints.Select(n => n.Id).ToArray());
        Assert.Equal(e, plan.BoundaryNode!.Id);
        // Both traversed cells (plus the start) are in the slide set.
        Assert.Contains(TownCellA, plan.PathCells);
        Assert.Contains(TownCellB, plan.PathCells);
    }

    [Fact]
    public void Plan_path_cells_rasterize_intermediate_cells_with_no_node()
    {
        var g = NewGraph();
        // A in cell ...01 (x=0), B 60 m east in cell ...11 (x=60 -> cellX=2).
        // The straight A->B segment also crosses cell ...09 (cellX=1,
        // x in 24..47), which has NO node of its own. The motor's
        // cell-crossing gate would stop there unless PathCells covers it.
        const uint CellX2 = 0x86020011u; // outdoor cellX=2,cellY=0 -> idx 17
        const uint CellX1 = 0x86020009u; // outdoor cellX=1,cellY=0 -> idx 9
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, CellX2, new Vector3(60, 0, 0), 1);
        var d = Visit(g, AcadCell, new Vector3(5000, 0, 5000), 2);
        var goal = Visit(g, AcadCell, new Vector3(5010, 0, 5000), 3);
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(b, d, NavEdgeKind.UsedPortal, null, null, _t0.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        Assert.Equal(new[] { b }, plan.Waypoints.Select(n => n.Id).ToArray());
        // Start cell, both node cells, AND the node-less intermediate cell.
        Assert.Contains(TownCellA, plan.PathCells);
        Assert.Contains(CellX2, plan.PathCells);
        Assert.Contains(CellX1, plan.PathCells);
    }

    [Fact]
    public void Plan_allows_crossed_boundary_edges_within_the_landblock()
    {
        var g = NewGraph();
        // A->B same cell (walked), B->E other cell same landblock via a
        // CrossedBoundary edge (an on-foot intra-landblock cell crossing).
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, TownCellA, new Vector3(10, 0, 0), 1);
        var e = Visit(g, TownCellB, new Vector3(30, 0, 0), 2);
        var d = Visit(g, AcadCell, new Vector3(5000, 0, 5000), 3);
        var goal = Visit(g, AcadCell, new Vector3(5010, 0, 5000), 4);
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(b, e, NavEdgeKind.CrossedBoundary, null, null, _t0.AddSeconds(5));
        g.RecordEdge(e, d, NavEdgeKind.UsedPortal, null, null, _t0.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        Assert.Equal(e, plan.BoundaryNode!.Id);
    }

    [Fact]
    public void Plan_stops_before_a_door_hop()
    {
        var g = NewGraph();
        // A->B walked, B->E via a DOOR (an action edge the LLM must own),
        // even though E is in the same landblock.
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, TownCellA, new Vector3(10, 0, 0), 1);
        var e = Visit(g, TownCellB, new Vector3(30, 0, 0), 2);
        var d = Visit(g, AcadCell, new Vector3(5000, 0, 5000), 3);
        var goal = Visit(g, AcadCell, new Vector3(5010, 0, 5000), 4);
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(b, e, NavEdgeKind.UsedDoor, null, null, _t0.AddSeconds(5));
        g.RecordEdge(e, d, NavEdgeKind.UsedPortal, null, null, _t0.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        // Stops at B (before the door); does not advance through E.
        Assert.Equal(b, plan.BoundaryNode!.Id);
        Assert.Equal(new[] { b }, plan.Waypoints.Select(n => n.Id).ToArray());
        Assert.Equal(NavEdgeKind.UsedDoor, plan.NextEdgeKind);
    }

    [Fact]
    public void Plan_stops_before_an_item_hop()
    {
        var g = NewGraph();
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, TownCellA, new Vector3(10, 0, 0), 1);
        var d = Visit(g, AcadCell, new Vector3(5000, 0, 5000), 2);
        var goal = Visit(g, AcadCell, new Vector3(5010, 0, 5000), 3);
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(b, d, NavEdgeKind.UsedItem, null, null, _t0.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        Assert.Equal(b, plan.BoundaryNode!.Id);
        Assert.Equal(NavEdgeKind.UsedItem, plan.NextEdgeKind);
    }

    [Fact]
    public void Plan_stops_before_leaving_the_landblock_on_foot()
    {
        var g = NewGraph();
        // A->B walked same landblock, B->F walked into a DIFFERENT landblock.
        // The bot must not auto-cross the landblock even on foot.
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, TownCellA, new Vector3(10, 0, 0), 1);
        var f = Visit(g, NextCell, new Vector3(20, 0, 0), 2);
        var goal = Visit(g, NextCell, new Vector3(30, 0, 0), 3);
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(b, f, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));
        g.RecordEdge(f, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(5));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            NextCell, new Vector3(30, 0, 0));

        Assert.Equal(RouteWaypointKind.Advance, plan.Kind);
        // Advances to B (the last node in landblock 0x8602) and stops there.
        Assert.Equal(b, plan.BoundaryNode!.Id);
        Assert.Equal((ushort)0x8603, plan.NextLandblock);
        Assert.Equal(NavEdgeKind.Walked, plan.NextEdgeKind);
    }

    [Fact]
    public void Plan_reports_transition_pending_when_bot_is_at_the_boundary()
    {
        var g = NewGraph();
        var (_, _, c, _, _) = BuildCrossLandblockRoute(g);

        // Bot is already standing at C; the very next hop is the portal.
        var plan = g.PlanWaypointToward(TownCellA, new Vector3(20, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.TransitionPending, plan.Kind);
        Assert.Empty(plan.Waypoints);
        Assert.Equal(c, plan.BoundaryNode!.Id);
        Assert.Equal((ushort)0xA9B4, plan.NextLandblock);
        Assert.Equal(NavEdgeKind.UsedPortal, plan.NextEdgeKind);
    }

    [Fact]
    public void Plan_returns_no_route_when_landblocks_are_not_connected()
    {
        var g = NewGraph();
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var b = Visit(g, TownCellA, new Vector3(10, 0, 0), 1);
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, _t0.AddSeconds(2));
        Visit(g, AcadCell, new Vector3(5000, 0, 5000), 3);

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5000, 0, 5000));

        Assert.Equal(RouteWaypointKind.NoRoute, plan.Kind);
    }

    [Fact]
    public void Plan_returns_no_route_when_goal_landblock_has_no_nodes()
    {
        var g = NewGraph();
        BuildCrossLandblockRoute(g);

        const uint unknownCell = 0xBBBB0001u;
        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            unknownCell, new Vector3(1, 0, 1));

        Assert.Equal(RouteWaypointKind.NoRoute, plan.Kind);
    }

    [Fact]
    public void Plan_returns_no_route_when_bot_is_not_near_any_node()
    {
        var g = NewGraph();
        BuildCrossLandblockRoute(g);

        // Bot is ~700m from the nearest node in its cell (> anchor radius).
        var plan = g.PlanWaypointToward(TownCellA, new Vector3(500, 0, 500),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.NoRoute, plan.Kind);
    }

    [Fact]
    public void Plan_returns_no_route_when_bots_exact_cell_has_no_node()
    {
        var g = NewGraph();
        // The bot's CURRENT cell (TownCellB) has no explored node, but the
        // landblock does (TownCellA). An executor must NOT fall back to a far
        // node in another cell — there's no proven path from where the bot
        // actually stands — so the plan is NoRoute.
        var a = Visit(g, TownCellA, new Vector3(0, 0, 0), 0);
        var c = Visit(g, TownCellA, new Vector3(20, 0, 0), 1);
        var d = Visit(g, AcadCell, new Vector3(5000, 0, 5000), 2);
        var goal = Visit(g, AcadCell, new Vector3(5010, 0, 5000), 3);
        g.RecordEdge(a, c, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));
        g.RecordEdge(c, d, NavEdgeKind.UsedPortal, null, null, _t0.AddSeconds(4));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, _t0.AddSeconds(4));

        // Bot stands in TownCellB (no node there) near where node A is.
        var plan = g.PlanWaypointToward(TownCellB, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.NoRoute, plan.Kind);
    }
}
