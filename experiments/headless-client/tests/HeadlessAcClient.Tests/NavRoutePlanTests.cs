// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Slice 5 (cross-landblock FOV consumption): NavGraph.PlanWaypointToward
/// — route-guided same-cell waypointing toward a remembered location in
/// another landblock, over the bot's OWN explored connectivity. The
/// planner never crosses a landblock and never leaves the bot's current
/// cell; it advances to the farthest contiguous same-cell route node or
/// reports that the next hop needs re-deliberation (TransitionPending) or
/// that no explored route exists (NoRoute).
/// </summary>
public sealed class NavRoutePlanTests : IDisposable
{
    private readonly string _dir;
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow;
    private readonly List<NavGraph> _graphs = new();

    // Bot's landblock (0x8602), two cells within it; and a second
    // landblock (0xA9B4) reached only via a portal edge.
    private const uint TownCellA = 0x86020001u; // bot's cell
    private const uint TownCellB = 0x86020002u; // same landblock, other cell
    private const uint AcadCell  = 0xA9B40001u; // other landblock

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

    /// <summary>
    /// Build A->B->C (same cell, walked) -> [portal] -> D -> goal in a
    /// second landblock. Returns the node ids.
    /// </summary>
    private (Guid a, Guid b, Guid c, Guid d, Guid goal) BuildCrossLandblockRoute(NavGraph g)
    {
        var t = _t0;
        var a = g.RecordVisit(TownCellA, new Vector3(0, 0, 0), t);
        g.BreakWalkedChain();
        var b = g.RecordVisit(TownCellA, new Vector3(10, 0, 0), t.AddSeconds(1));
        g.BreakWalkedChain();
        var c = g.RecordVisit(TownCellA, new Vector3(20, 0, 0), t.AddSeconds(2));
        g.BreakWalkedChain();
        var d = g.RecordVisit(AcadCell, new Vector3(5000, 0, 5000), t.AddSeconds(3));
        g.BreakWalkedChain();
        var goal = g.RecordVisit(AcadCell, new Vector3(5010, 0, 5000), t.AddSeconds(4));

        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, t.AddSeconds(5));
        g.RecordEdge(b, c, NavEdgeKind.Walked, null, null, t.AddSeconds(5));
        g.RecordEdge(c, d, NavEdgeKind.UsedPortal, null, null, t.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, t.AddSeconds(5));
        return (a, b, c, d, goal);
    }

    [Fact]
    public void Plan_advances_to_farthest_same_cell_node_before_transition()
    {
        var g = NewGraph();
        var (_, _, c, _, _) = BuildCrossLandblockRoute(g);

        // Bot at A; remembered target near the goal node in the other lb.
        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.AdvanceSameCell, plan.Kind);
        Assert.NotNull(plan.AdvanceNode);
        // Farthest contiguous same-cell node before the portal hop is C.
        Assert.Equal(c, plan.AdvanceNode!.Id);
        // The boundary-approach node (just before the cross-lb edge) is C.
        Assert.Equal(c, plan.BoundaryNode!.Id);
        // The route crosses into landblock 0xA9B4.
        Assert.Equal((ushort)0xA9B4, plan.NextLandblock);
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
        Assert.Null(plan.AdvanceNode);
        Assert.Equal(c, plan.BoundaryNode!.Id);
        Assert.Equal((ushort)0xA9B4, plan.NextLandblock);
    }

    [Fact]
    public void Plan_does_not_advance_into_a_different_cell_of_same_landblock()
    {
        var g = NewGraph();
        var t = _t0;
        // A->B same cell (walked), B->E different cell same landblock
        // (walked), E->[portal]->D other landblock.
        var a = g.RecordVisit(TownCellA, new Vector3(0, 0, 0), t);
        g.BreakWalkedChain();
        var b = g.RecordVisit(TownCellA, new Vector3(10, 0, 0), t.AddSeconds(1));
        g.BreakWalkedChain();
        var e = g.RecordVisit(TownCellB, new Vector3(30, 0, 0), t.AddSeconds(2));
        g.BreakWalkedChain();
        var d = g.RecordVisit(AcadCell, new Vector3(5000, 0, 5000), t.AddSeconds(3));
        g.BreakWalkedChain();
        var goal = g.RecordVisit(AcadCell, new Vector3(5010, 0, 5000), t.AddSeconds(4));
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, t.AddSeconds(5));
        g.RecordEdge(b, e, NavEdgeKind.Walked, null, null, t.AddSeconds(5));
        g.RecordEdge(e, d, NavEdgeKind.UsedPortal, null, null, t.AddSeconds(5));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, t.AddSeconds(5));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        // Same-cell prefix from A ends at B; E is a different cell, so the
        // planner must NOT advance into it (would trip the cell-cross gate).
        Assert.Equal(RouteWaypointKind.AdvanceSameCell, plan.Kind);
        Assert.Equal(b, plan.AdvanceNode!.Id);
    }

    [Fact]
    public void Plan_returns_no_route_when_landblocks_are_not_connected()
    {
        var g = NewGraph();
        var t = _t0;
        // Nodes in both landblocks but NO cross-landblock edge.
        var a = g.RecordVisit(TownCellA, new Vector3(0, 0, 0), t);
        g.BreakWalkedChain();
        var b = g.RecordVisit(TownCellA, new Vector3(10, 0, 0), t.AddSeconds(1));
        g.RecordEdge(a, b, NavEdgeKind.Walked, null, null, t.AddSeconds(2));
        g.BreakWalkedChain();
        g.RecordVisit(AcadCell, new Vector3(5000, 0, 5000), t.AddSeconds(3));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5000, 0, 5000));

        Assert.Equal(RouteWaypointKind.NoRoute, plan.Kind);
    }

    [Fact]
    public void Plan_returns_no_route_when_goal_landblock_has_no_nodes()
    {
        var g = NewGraph();
        BuildCrossLandblockRoute(g);

        // Ask for a landblock the bot has never seen a node in.
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

        // Bot is ~700m from the nearest node in its cell (> anchor radii).
        var plan = g.PlanWaypointToward(TownCellA, new Vector3(500, 0, 500),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.NoRoute, plan.Kind);
    }

    [Fact]
    public void Plan_transition_pending_reports_immediate_cell_boundary_not_distant_landblock()
    {
        var g = NewGraph();
        var t = _t0;
        // Bot stands at B (last node of its cell). The route's next hop is
        // an INTRA-landblock cell change (B->E, both in lb 0x8602), and the
        // landblock exit (E->[portal]->D) is a further hop away.
        var b = g.RecordVisit(TownCellA, new Vector3(0, 0, 0), t);
        g.BreakWalkedChain();
        var e = g.RecordVisit(TownCellB, new Vector3(20, 0, 0), t.AddSeconds(1));
        g.BreakWalkedChain();
        var d = g.RecordVisit(AcadCell, new Vector3(5000, 0, 5000), t.AddSeconds(2));
        g.BreakWalkedChain();
        var goal = g.RecordVisit(AcadCell, new Vector3(5010, 0, 5000), t.AddSeconds(3));
        g.RecordEdge(b, e, NavEdgeKind.Walked, null, null, t.AddSeconds(4));
        g.RecordEdge(e, d, NavEdgeKind.UsedPortal, null, null, t.AddSeconds(4));
        g.RecordEdge(d, goal, NavEdgeKind.Walked, null, null, t.AddSeconds(4));

        var plan = g.PlanWaypointToward(TownCellA, new Vector3(0, 0, 0),
            AcadCell, new Vector3(5010, 0, 5000));

        Assert.Equal(RouteWaypointKind.TransitionPending, plan.Kind);
        Assert.Equal(b, plan.BoundaryNode!.Id);
        // The bot halts at the B->E CELL boundary, which stays inside
        // landblock 0x8602 — the payload must describe that immediate hop,
        // NOT the eventual 0xA9B4 landblock exit two hops away.
        Assert.Equal((ushort)0x8602, plan.NextLandblock);
    }
}
