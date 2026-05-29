// SPDX-License-Identifier: AGPL-3.0-or-later
//
// IndoorNavServiceTests — Phase 3.0 unit tests for the API surface
// of the headless-client side of the WorldNav integration.
//
// These tests do NOT load real DAT files; they exercise the
// disabled-mode path, the static helpers, and the early-exit
// branches that gate on cell-id shape (indoor-vs-outdoor,
// cross-landblock). The "happy path" — loading a real graph and
// returning waypoints — is covered end-to-end by AcAiPlayers.WorldNav's
// own test fixtures (PathfinderWalkableTests) and the live spike
// runs against academy 0x8602.

using System.Collections.Generic;
using System.Numerics;

using AcAiPlayers.WorldNav;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public sealed class IndoorNavServiceTests
{
    [Fact]
    public void IsIndoorCell_Indoor()
    {
        // Academy 0x8602 cell 0x01AD — lower 16 bits 0x01AD >= 0x100.
        Assert.True(IndoorNavService.IsIndoorCell(0x860201ADu));
        // Boundary: first indoor index is 0x100.
        Assert.True(IndoorNavService.IsIndoorCell(0x86020100u));
    }

    [Fact]
    public void IsIndoorCell_Outdoor()
    {
        // Outdoor LandCell with lower bits 0x0001..0x00FF.
        Assert.False(IndoorNavService.IsIndoorCell(0xA9B40001u));
        Assert.False(IndoorNavService.IsIndoorCell(0xA9B400FFu));
        Assert.False(IndoorNavService.IsIndoorCell(0xA9B40000u));
    }

    [Fact]
    public void GetLandblockId_ReturnsUpper16Bits()
    {
        Assert.Equal((ushort)0x8602, IndoorNavService.GetLandblockId(0x860201ADu));
        Assert.Equal((ushort)0xA9B4, IndoorNavService.GetLandblockId(0xA9B40001u));
    }

    [Fact]
    public void DisabledService_AlwaysReturnsDisabled()
    {
        var svc = new IndoorNavService();
        Assert.False(svc.IsEnabled);
        var r = svc.TryFindPath(
            0x860201ADu, new Vector3(100, -50, 0),
            0x860201B0u, new Vector3(120, -50, 0),
            new HashSet<uint>());
        Assert.Equal(IndoorPathStatus.Disabled, r.Status);
        Assert.Empty(r.Waypoints);
        Assert.Equal(1, svc.Telemetry.Disabled);
    }

    [Fact]
    public void DisabledService_DoesNotShortCircuitBeforeDisabledCheck()
    {
        // Even with outdoor cell ids, the disabled service should
        // still return Disabled (and increment that counter, not
        // NotIndoor). Catches regressions where someone reorders
        // the guard chain in TryFindPath.
        var svc = new IndoorNavService();
        var r = svc.TryFindPath(
            0xA9B40001u, Vector3.Zero,
            0xA9B40002u, Vector3.Zero,
            new HashSet<uint>());
        Assert.Equal(IndoorPathStatus.Disabled, r.Status);
        Assert.Equal(0, svc.Telemetry.NotIndoor);
    }

    [Fact]
    public void TelemetrySummary_IncludesAllCategories()
    {
        var telem = new IndoorNavTelemetry();
        // Reflection-free smoke: just make sure the Summary
        // string mentions every visible category so a grep-based
        // log scraper can find them.
        var s = telem.Summary();
        Assert.Contains("success=", s);
        Assert.Contains("no-path=", s);
        Assert.Contains("not-indoor=", s);
        Assert.Contains("cross-lb=", s);
        Assert.Contains("no-graph=", s);
        Assert.Contains("disabled=", s);
        Assert.Contains("graphs=", s);
        Assert.Contains("avg-seed=", s);
        Assert.Contains("avg-expanded=", s);
    }

    // ---- Phase 3.1 K-hop seen-cells expansion ----

    [Fact]
    public void ExpandViaConnections_ZeroHops_ReturnsCopyOfSeed()
    {
        var graph = BuildLinearGraph(5);
        var seed = (IReadOnlySet<uint>)new HashSet<uint> { 0x86020100u };
        var expanded = IndoorNavService.ExpandViaConnections(graph, seed, hops: 0);
        Assert.Single(expanded);
        Assert.Contains(0x86020100u, expanded);
    }

    [Fact]
    public void ExpandViaConnections_EmptySeed_ReturnsEmpty()
    {
        var graph = BuildLinearGraph(5);
        var seed = (IReadOnlySet<uint>)new HashSet<uint>();
        var expanded = IndoorNavService.ExpandViaConnections(graph, seed, hops: 4);
        Assert.Empty(expanded);
    }

    [Fact]
    public void ExpandViaConnections_OneHop_ReachesAdjacent()
    {
        // Linear A-B-C-D-E. Seed={A=0x100}, K=1 → {A, B}.
        var graph = BuildLinearGraph(5);
        var seed = (IReadOnlySet<uint>)new HashSet<uint> { 0x86020100u };
        var expanded = IndoorNavService.ExpandViaConnections(graph, seed, hops: 1);
        Assert.Equal(2, expanded.Count);
        Assert.Contains(0x86020100u, expanded);
        Assert.Contains(0x86020101u, expanded);
    }

    [Fact]
    public void ExpandViaConnections_FourHops_ReachesFarEnd()
    {
        // Linear A-B-C-D-E. Seed={A}, K=4 → all 5 cells.
        var graph = BuildLinearGraph(5);
        var seed = (IReadOnlySet<uint>)new HashSet<uint> { 0x86020100u };
        var expanded = IndoorNavService.ExpandViaConnections(graph, seed, hops: 4);
        Assert.Equal(5, expanded.Count);
        for (uint i = 0; i < 5; i++)
            Assert.Contains(0x86020100u + i, expanded);
    }

    [Fact]
    public void ExpandViaConnections_BoundedByConnectivity()
    {
        // Two disconnected components: A-B and C-D. Seed={A}, K=10
        // → {A, B} only. C and D unreachable, must NOT leak in.
        var graph = BuildTwoComponentGraph();
        var seed = (IReadOnlySet<uint>)new HashSet<uint> { 0x86020100u };
        var expanded = IndoorNavService.ExpandViaConnections(graph, seed, hops: 10);
        Assert.Equal(2, expanded.Count);
        Assert.Contains(0x86020100u, expanded);
        Assert.Contains(0x86020101u, expanded);
        Assert.DoesNotContain(0x86020200u, expanded);
        Assert.DoesNotContain(0x86020201u, expanded);
    }

    [Fact]
    public void ExpandViaConnections_SkipsDanglingEdges()
    {
        // A-B, plus A has a dangling edge to a cell not in the graph.
        // Expansion must not visit the dangling cell.
        var graph = BuildLinearWithDanglingEdge();
        var seed = (IReadOnlySet<uint>)new HashSet<uint> { 0x86020100u };
        var expanded = IndoorNavService.ExpandViaConnections(graph, seed, hops: 4);
        Assert.Equal(2, expanded.Count);
        Assert.Contains(0x86020100u, expanded);
        Assert.Contains(0x86020101u, expanded);
        Assert.DoesNotContain(0x86029999u, expanded);
    }

    // ---- Synthetic graph builders for K-hop tests ----

    private static IndoorNavGraph BuildLinearGraph(int n)
    {
        // Build a chain A-B-C-... with bidirectional connections.
        var cells = new Dictionary<uint, AcAiPlayers.WorldNav.IndoorCell>();
        for (int i = 0; i < n; i++)
        {
            uint cid = 0x86020100u + (uint)i;
            var conns = new List<AcAiPlayers.WorldNav.CellConnection>();
            if (i > 0)
                conns.Add(MakeConn(cid, 0x86020100u + (uint)(i - 1)));
            if (i < n - 1)
                conns.Add(MakeConn(cid, 0x86020100u + (uint)(i + 1)));
            cells[cid] = MakeCell(cid, conns);
        }
        return new AcAiPlayers.WorldNav.IndoorNavGraph
        {
            LandblockId = 0x8602,
            Cells = cells,
            WalkableBridges = System.Array.Empty<AcAiPlayers.WorldNav.WalkableBridge>(),
            BoundsWorld = default,
        };
    }

    private static IndoorNavGraph BuildTwoComponentGraph()
    {
        var cells = new Dictionary<uint, AcAiPlayers.WorldNav.IndoorCell>
        {
            [0x86020100u] = MakeCell(0x86020100u,
                new List<AcAiPlayers.WorldNav.CellConnection>
                { MakeConn(0x86020100u, 0x86020101u) }),
            [0x86020101u] = MakeCell(0x86020101u,
                new List<AcAiPlayers.WorldNav.CellConnection>
                { MakeConn(0x86020101u, 0x86020100u) }),
            [0x86020200u] = MakeCell(0x86020200u,
                new List<AcAiPlayers.WorldNav.CellConnection>
                { MakeConn(0x86020200u, 0x86020201u) }),
            [0x86020201u] = MakeCell(0x86020201u,
                new List<AcAiPlayers.WorldNav.CellConnection>
                { MakeConn(0x86020201u, 0x86020200u) }),
        };
        return new AcAiPlayers.WorldNav.IndoorNavGraph
        {
            LandblockId = 0x8602,
            Cells = cells,
            WalkableBridges = System.Array.Empty<AcAiPlayers.WorldNav.WalkableBridge>(),
            BoundsWorld = default,
        };
    }

    private static IndoorNavGraph BuildLinearWithDanglingEdge()
    {
        var cells = new Dictionary<uint, AcAiPlayers.WorldNav.IndoorCell>
        {
            [0x86020100u] = MakeCell(0x86020100u,
                new List<AcAiPlayers.WorldNav.CellConnection>
                {
                    MakeConn(0x86020100u, 0x86020101u),
                    MakeDanglingConn(0x86020100u, 0x86029999u),
                }),
            [0x86020101u] = MakeCell(0x86020101u,
                new List<AcAiPlayers.WorldNav.CellConnection>
                { MakeConn(0x86020101u, 0x86020100u) }),
        };
        return new AcAiPlayers.WorldNav.IndoorNavGraph
        {
            LandblockId = 0x8602,
            Cells = cells,
            WalkableBridges = System.Array.Empty<AcAiPlayers.WorldNav.WalkableBridge>(),
            BoundsWorld = default,
        };
    }

    private static AcAiPlayers.WorldNav.IndoorCell MakeCell(
        uint cellId,
        IReadOnlyList<AcAiPlayers.WorldNav.CellConnection> conns)
        => new AcAiPlayers.WorldNav.IndoorCell
        {
            CellId = cellId,
            LandblockId = (ushort)(cellId >> 16),
            CellWithinLandblock = (ushort)(cellId & 0xFFFF),
            OriginWorld = Vector3.Zero,
            CentroidWorld = Vector3.Zero,
            BoundsWorld = default,
            HasGeometry = false,
            Connections = conns,
            StaticObstacles = System.Array.Empty<AcAiPlayers.WorldNav.StaticObstacle>(),
            FloorPolygons = System.Array.Empty<AcAiPlayers.WorldNav.FloorPolygon>(),
            WalkableNodes = System.Array.Empty<AcAiPlayers.WorldNav.WalkableNode>(),
            WalkableEdges = System.Array.Empty<AcAiPlayers.WorldNav.WalkableEdge>(),
        };

    private static AcAiPlayers.WorldNav.CellConnection MakeConn(uint from, uint to)
        => new AcAiPlayers.WorldNav.CellConnection
        {
            OwnerCellId = from,
            OtherCellId = to,
            OtherCellLoaded = true,
            PolygonId = 0,
            CentroidWorld = Vector3.Zero,
        };

    private static AcAiPlayers.WorldNav.CellConnection MakeDanglingConn(uint from, uint to)
        => new AcAiPlayers.WorldNav.CellConnection
        {
            OwnerCellId = from,
            OtherCellId = to,
            OtherCellLoaded = false,
            PolygonId = 0,
            CentroidWorld = Vector3.Zero,
        };
}
