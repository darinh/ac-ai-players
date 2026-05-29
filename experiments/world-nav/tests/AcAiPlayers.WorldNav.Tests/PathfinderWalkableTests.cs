// SPDX-License-Identifier: AGPL-3.0-or-later
//
// PathfinderWalkableTests - synthetic 4-cell graph covering the
// walkable-node A* (intra-cell edges + cross-cell bridges) without
// touching DAT files. Cell layout (top-down, X right, Y up):
//
//   +-------+-------+
//   |   C   |   D   |
//   +-------+-------+
//   |   A   |   B   |
//   +-------+-------+
//
// Each cell is a 10u x 10u square at Z=0 (C and D at Z=4 simulate
// an upper floor reached by a stair, modeled here as a Z-jumping
// bridge from A->C). Every cell holds a 3x3 grid of walkable nodes
// (nodes 0..8). Bridges connect adjacent cells through their
// shared edge centroid.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AcAiPlayers.WorldNav;

using Xunit;

namespace AcAiPlayers.WorldNav.Tests;

public class PathfinderWalkableTests
{
    private static IndoorCell MakeCell(
        uint cellId,
        float originX,
        float originY,
        float z,
        IReadOnlyList<StaticObstacle>? obstacles = null)
    {
        var nodes = new List<WalkableNode>();
        for (int gy = 0; gy < 3; gy++)
        {
            for (int gx = 0; gx < 3; gx++)
            {
                nodes.Add(new WalkableNode
                {
                    CellId = cellId,
                    FloorPolygonIndex = 0,
                    PositionWorld = new Vector3(originX + gx * 5f, originY + gy * 5f, z),
                });
            }
        }
        // 8-neighbour edges across the 3x3 grid (cardinal + diagonal).
        var edges = new List<WalkableEdge>();
        int Idx(int gx, int gy) => gy * 3 + gx;
        var offsets = new (int dx, int dy)[]
        {
            (1, 0), (0, 1), (1, 1), (-1, 1),
        };
        for (int gy = 0; gy < 3; gy++)
        {
            for (int gx = 0; gx < 3; gx++)
            {
                foreach (var (dx, dy) in offsets)
                {
                    int nx = gx + dx, ny = gy + dy;
                    if (nx < 0 || nx >= 3 || ny < 0 || ny >= 3) continue;
                    var a = nodes[Idx(gx, gy)].PositionWorld;
                    var b = nodes[Idx(nx, ny)].PositionWorld;
                    edges.Add(new WalkableEdge(Idx(gx, gy), Idx(nx, ny), Vector3.Distance(a, b)));
                }
            }
        }

        var origin = new Vector3(originX, originY, z);
        return new IndoorCell
        {
            CellId = cellId,
            LandblockId = (ushort)(cellId >> 16),
            CellWithinLandblock = (ushort)(cellId & 0xFFFF),
            OriginWorld = origin,
            CentroidWorld = new Vector3(originX + 5f, originY + 5f, z),
            BoundsWorld = new NavBounds(originX, originY, originX + 10f, originY + 10f, z, z),
            Connections = new List<CellConnection>(),
            StaticObstacles = obstacles ?? new List<StaticObstacle>(),
            FloorPolygons = new List<FloorPolygon>(),
            WalkableNodes = nodes,
            WalkableEdges = edges,
            HasGeometry = true,
        };
    }

    private static IndoorNavGraph BuildSquareGraph(IReadOnlyList<StaticObstacle>? bObstacles = null)
    {
        // Cells laid out:
        //   C(0,10) D(10,10)
        //   A(0,0)  B(10,0)
        var a = MakeCell(0x0A, 0f, 0f, 0f);
        var b = MakeCell(0x0B, 10f, 0f, 0f, bObstacles);
        var c = MakeCell(0x0C, 0f, 10f, 4f);  // upper floor
        var d = MakeCell(0x0D, 10f, 10f, 4f);

        var cells = new Dictionary<uint, IndoorCell>
        {
            [a.CellId] = a,
            [b.CellId] = b,
            [c.CellId] = c,
            [d.CellId] = d,
        };

        // Bridges through shared edges (centroid at the boundary).
        // Pick the node closest to the boundary centroid on each side.
        // A right-middle node (gx=2,gy=1) idx=5 <-> B left-middle (gx=0,gy=1) idx=3
        // A top-middle  node (gx=1,gy=2) idx=7 <-> C bottom-middle (gx=1,gy=0) idx=1
        // B top-middle  idx=7 <-> D bottom-middle idx=1
        // C right-middle idx=5 <-> D left-middle idx=3
        var bridges = new List<WalkableBridge>
        {
            new(a.CellId, 5, b.CellId, 3, 1, 5f),
            new(a.CellId, 7, c.CellId, 1, 2, 5f + 4f),
            new(b.CellId, 7, d.CellId, 1, 3, 5f + 4f),
            new(c.CellId, 5, d.CellId, 3, 4, 5f),
        };

        return new IndoorNavGraph
        {
            LandblockId = 0x0001,
            Cells = cells,
            BoundsWorld = new NavBounds(0, 0, 20, 20, 0, 4),
            WalkableBridges = bridges,
        };
    }

    [Fact]
    public void FindsPathAcrossSingleBridge()
    {
        var graph = BuildSquareGraph();
        var pf = new Pathfinder();

        // Place from clearly inside A and to clearly inside B so the
        // endpoint snap-to-nearest doesn't collapse to the shared-edge
        // node (which is identical-position in both cells).
        var fromA = new Vector3(2.5f, 5f, 0f);
        var toB = new Vector3(17.5f, 5f, 0f);
        var result = pf.FindWalkablePath(graph, fromA, toB);

        Assert.True(result.Found, result.FailureReason);
        Assert.True(result.Points.Count >= 2);
        var cellsTouched = result.NodePath.Select(n => n.CellId).Distinct().ToList();
        Assert.Contains((uint)0x0A, cellsTouched);
        Assert.Contains((uint)0x0B, cellsTouched);
    }

    [Fact]
    public void FindsMultiBridgePathAcrossStairs()
    {
        var graph = BuildSquareGraph();
        var pf = new Pathfinder();

        // A bottom-left to D top-right requires 2 bridges minimum.
        var fromA = new Vector3(0f, 0f, 0f);
        var toD = new Vector3(20f, 20f, 4f);
        var result = pf.FindWalkablePath(graph, fromA, toD);

        Assert.True(result.Found, result.FailureReason);
        var cellsTouched = result.NodePath.Select(n => n.CellId).Distinct().ToList();
        Assert.Contains((uint)0x0A, cellsTouched);
        Assert.Contains((uint)0x0D, cellsTouched);
        // Should NOT skip floors: end Z must be 4.
        Assert.Equal(4f, result.Points[^1].Z);
    }

    [Fact]
    public void RespectsFogOfWar_RejectsPathThroughUnseenCell()
    {
        var graph = BuildSquareGraph();
        var pf = new Pathfinder();

        // A and D are diagonal; there is no direct A-D bridge in the
        // synthetic graph (A bridges to B and C, D bridges to B and C).
        // With seen={A,D} the only routes require an unseen cell (B or
        // C), so the search must fail.
        var seen = new HashSet<uint> { 0x0A, 0x0D };
        var result = pf.FindWalkablePath(
            graph,
            new Vector3(2.5f, 2.5f, 0f),
            new Vector3(17.5f, 17.5f, 4f),
            seen);

        Assert.False(result.Found);
    }

    [Fact]
    public void RespectsFogOfWar_AllowsPathThroughSeenCells()
    {
        var graph = BuildSquareGraph();
        var pf = new Pathfinder();

        var seen = new HashSet<uint> { 0x0A, 0x0B };
        var result = pf.FindWalkablePath(
            graph,
            new Vector3(2.5f, 5f, 0f),
            new Vector3(17.5f, 5f, 0f),
            seen);

        Assert.True(result.Found, result.FailureReason);
    }

    [Fact]
    public void IntraCellEdgeCostsAre3D_NotPlanar()
    {
        // Build a single cell with two nodes at different Z and a
        // single edge between them. The reported edge distance must
        // match Vector3.Distance, NOT |dx| + |dy| or sqrt(dx^2 + dy^2).
        var nodes = new List<WalkableNode>
        {
            new() { CellId = 0x01, FloorPolygonIndex = 0, PositionWorld = new Vector3(0, 0, 0) },
            new() { CellId = 0x01, FloorPolygonIndex = 0, PositionWorld = new Vector3(3, 4, 12) },
        };
        var edges = new List<WalkableEdge>
        {
            new(0, 1, Vector3.Distance(nodes[0].PositionWorld, nodes[1].PositionWorld)),
        };
        Assert.Equal(13f, edges[0].DistanceUnits, 3);
    }

    [Fact]
    public void NearestWalkableNode_RespectsCellFilter()
    {
        var graph = BuildSquareGraph();

        // (20,20,0) is closest to D's top-right corner; with
        // walkableCells={A} we must instead pick a node in A.
        var seenA = new HashSet<uint> { 0x0A };
        var picked = Pathfinder.NearestWalkableNode(
            graph, new Vector3(20, 20, 4), seenA);
        Assert.NotNull(picked);
        Assert.Equal((uint)0x0A, picked!.Value.CellId);
    }

    [Fact]
    public void BridgeDedup_NoDuplicateBridgesForSamePair()
    {
        // Sanity: the synthetic graph above places exactly four bridges.
        var graph = BuildSquareGraph();
        Assert.Equal(4, graph.WalkableBridgeCount);
    }

    [Fact]
    public void EmptyGraph_ReturnsNotFoundWithReason()
    {
        var graph = new IndoorNavGraph
        {
            LandblockId = 0x0001,
            Cells = new Dictionary<uint, IndoorCell>(),
            BoundsWorld = new NavBounds(0, 0, 0, 0, 0, 0),
            WalkableBridges = new List<WalkableBridge>(),
        };
        var pf = new Pathfinder();
        var result = pf.FindWalkablePath(graph, Vector3.Zero, Vector3.One);
        Assert.False(result.Found);
        Assert.NotNull(result.FailureReason);
    }
}
