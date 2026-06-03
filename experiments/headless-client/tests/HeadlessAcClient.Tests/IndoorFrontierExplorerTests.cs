// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for IndoorFrontierExplorer — autonomous indoor frontier
// search (road-to-endgame Phase A1). Each test builds a small static
// cell graph and asserts the explorer picks the nearest reachable
// cell the bot has not yet entered (or declines when there is none).

using System.Collections.Generic;
using System.Numerics;

using AcAiPlayers.WorldNav;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class IndoorFrontierExplorerTests
{
    private static readonly IReadOnlySet<uint> None = new HashSet<uint>();

    [Fact]
    public void NearestUnexplored_IsChosen()
    {
        // A(current,seen) - B(unseen) - C(unseen). B is one hop, C two.
        var graph = Graph(
            Cell(0x86020100u, new[] { Conn(0x86020100u, 0x86020101u) },
                centroid: V(10, 0), floor: new[] { V(10, 0) }),
            Cell(0x86020101u, new[] { Conn(0x86020101u, 0x86020100u), Conn(0x86020101u, 0x86020102u) },
                centroid: V(20, 0), floor: new[] { V(20, 0) }),
            Cell(0x86020102u, new[] { Conn(0x86020102u, 0x86020101u) },
                centroid: V(30, 0), floor: new[] { V(30, 0) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86020100u, Seen(0x86020100u), None);

        Assert.NotNull(result);
        Assert.Equal(0x86020101u, result!.Value.CellId);
        Assert.Equal(V(20, 0), result.Value.Position);
    }

    [Fact]
    public void TraversesThroughSeenCells_ToReachUnexplored()
    {
        // A(current,seen) - B(seen) - C(unseen). Must walk past B to C.
        var graph = Graph(
            Cell(0x86020100u, new[] { Conn(0x86020100u, 0x86020101u) },
                centroid: V(10, 0), floor: new[] { V(10, 0) }),
            Cell(0x86020101u, new[] { Conn(0x86020101u, 0x86020100u), Conn(0x86020101u, 0x86020102u) },
                centroid: V(20, 0), floor: new[] { V(20, 0) }),
            Cell(0x86020102u, new[] { Conn(0x86020102u, 0x86020101u) },
                centroid: V(30, 0), floor: new[] { V(30, 0) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86020100u, Seen(0x86020100u, 0x86020101u), None);

        Assert.NotNull(result);
        Assert.Equal(0x86020102u, result!.Value.CellId);
    }

    [Fact]
    public void CooldownCell_IsSkippedButTraversedThrough()
    {
        // A(current,seen) - B(unseen, ON COOLDOWN) - C(unseen).
        // B is nearer but cooled; the explorer steps past it to C.
        var graph = Graph(
            Cell(0x86020100u, new[] { Conn(0x86020100u, 0x86020101u) },
                centroid: V(10, 0), floor: new[] { V(10, 0) }),
            Cell(0x86020101u, new[] { Conn(0x86020101u, 0x86020100u), Conn(0x86020101u, 0x86020102u) },
                centroid: V(20, 0), floor: new[] { V(20, 0) }),
            Cell(0x86020102u, new[] { Conn(0x86020102u, 0x86020101u) },
                centroid: V(30, 0), floor: new[] { V(30, 0) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86020100u, Seen(0x86020100u), Seen(0x86020101u));

        Assert.NotNull(result);
        Assert.Equal(0x86020102u, result!.Value.CellId);
    }

    [Fact]
    public void AllExplored_ReturnsNull()
    {
        var graph = Graph(
            Cell(0x86020100u, new[] { Conn(0x86020100u, 0x86020101u) },
                centroid: V(10, 0), floor: new[] { V(10, 0) }),
            Cell(0x86020101u, new[] { Conn(0x86020101u, 0x86020100u) },
                centroid: V(20, 0), floor: new[] { V(20, 0) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86020100u, Seen(0x86020100u, 0x86020101u), None);

        Assert.Null(result);
    }

    [Fact]
    public void FrontierWithoutFloor_IsSkipped_DeeperFloorCellReturned()
    {
        // A(current,seen) - B(unseen, NO floor) - C(unseen, floor).
        var graph = Graph(
            Cell(0x86020100u, new[] { Conn(0x86020100u, 0x86020101u) },
                centroid: V(10, 0), floor: new[] { V(10, 0) }),
            Cell(0x86020101u, new[] { Conn(0x86020101u, 0x86020100u), Conn(0x86020101u, 0x86020102u) },
                centroid: V(20, 0), floor: System.Array.Empty<Vector3>()),
            Cell(0x86020102u, new[] { Conn(0x86020102u, 0x86020101u) },
                centroid: V(30, 0), floor: new[] { V(30, 0) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86020100u, Seen(0x86020100u), None);

        Assert.NotNull(result);
        Assert.Equal(0x86020102u, result!.Value.CellId);
    }

    [Fact]
    public void DanglingConnection_IsNotTraversed()
    {
        // A(current) - B via an UNLOADED (cross-landblock) connection.
        // Nothing reachable -> null.
        var graph = Graph(
            Cell(0x86020100u, new[] { Dangling(0x86020100u, 0x86030100u) },
                centroid: V(10, 0), floor: new[] { V(10, 0) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86020100u, Seen(0x86020100u), None);

        Assert.Null(result);
    }

    [Fact]
    public void CurrentCellNotInGraph_ReturnsNull()
    {
        var graph = Graph(
            Cell(0x86020100u, System.Array.Empty<CellConnection>(),
                centroid: V(0, 0), floor: new[] { V(0, 0) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86029999u, None, None);

        Assert.Null(result);
    }

    [Fact]
    public void Position_IsFloorNodeNearestCentroid()
    {
        // Frontier cell has two floor nodes; the one nearest the cell
        // centroid is chosen as the stand point.
        var graph = Graph(
            Cell(0x86020100u, new[] { Conn(0x86020100u, 0x86020101u) },
                centroid: V(0, 0), floor: new[] { V(0, 0) }),
            Cell(0x86020101u, new[] { Conn(0x86020101u, 0x86020100u) },
                centroid: V(50, 50),
                floor: new[] { V(48, 51), V(5, 5) }));

        var result = IndoorFrontierExplorer.ChooseFrontier(
            graph, 0x86020100u, Seen(0x86020100u), None);

        Assert.NotNull(result);
        Assert.Equal(0x86020101u, result!.Value.CellId);
        Assert.Equal(V(48, 51), result.Value.Position);
    }

    // ---- builders -------------------------------------------------

    private static Vector3 V(float x, float y) => new(x, y, 0f);

    private static IReadOnlySet<uint> Seen(params uint[] ids) => new HashSet<uint>(ids);

    private static IndoorNavGraph Graph(params IndoorCell[] cells)
    {
        var dict = new Dictionary<uint, IndoorCell>();
        foreach (var c in cells) dict[c.CellId] = c;
        return new IndoorNavGraph
        {
            LandblockId = 0x8602,
            Cells = dict,
            WalkableBridges = System.Array.Empty<WalkableBridge>(),
            BoundsWorld = default,
        };
    }

    private static IndoorCell Cell(
        uint cellId,
        IReadOnlyList<CellConnection> conns,
        Vector3 centroid,
        IReadOnlyList<Vector3> floor)
    {
        var nodes = new List<WalkableNode>();
        foreach (var p in floor)
            nodes.Add(new WalkableNode
            {
                CellId = cellId,
                FloorPolygonIndex = 0,
                PositionWorld = p,
                Kind = WalkableNodeKind.Floor,
                ConnectionPolygonId = null,
            });

        return new IndoorCell
        {
            CellId = cellId,
            LandblockId = (ushort)(cellId >> 16),
            CellWithinLandblock = (ushort)(cellId & 0xFFFF),
            OriginWorld = Vector3.Zero,
            CentroidWorld = centroid,
            BoundsWorld = default,
            HasGeometry = floor.Count > 0,
            Connections = conns,
            StaticObstacles = System.Array.Empty<StaticObstacle>(),
            FloorPolygons = System.Array.Empty<FloorPolygon>(),
            WalkableNodes = nodes,
            WalkableEdges = System.Array.Empty<WalkableEdge>(),
        };
    }

    private static CellConnection Conn(uint from, uint to)
        => new()
        {
            OwnerCellId = from,
            OtherCellId = to,
            OtherCellLoaded = true,
            PolygonId = 0,
            CentroidWorld = Vector3.Zero,
        };

    private static CellConnection Dangling(uint from, uint to)
        => new()
        {
            OwnerCellId = from,
            OtherCellId = to,
            OtherCellLoaded = false,
            PolygonId = 0,
            CentroidWorld = Vector3.Zero,
        };
}
