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

    [Fact]
    public void DoorwayNode_KindAndConnectionPolygonId_RoundTrip()
    {
        // A doorway WalkableNode must carry Kind=Doorway and a
        // non-null ConnectionPolygonId so the headless-client's
        // walk-tick can correlate the waypoint with a live Door
        // entity and dispatch USE before traversing. This is the
        // minimum schema invariant that the LandblockNavLoader
        // promises to fulfill for every doorway node it inserts.
        var floor = new WalkableNode
        {
            CellId = 0x0A,
            FloorPolygonIndex = 0,
            PositionWorld = new Vector3(0, 0, 0),
        };
        Assert.Equal(WalkableNodeKind.Floor, floor.Kind);
        Assert.Null(floor.ConnectionPolygonId);

        var doorway = new WalkableNode
        {
            CellId = 0x0A,
            FloorPolygonIndex = -1,
            PositionWorld = new Vector3(1, 2, 3),
            Kind = WalkableNodeKind.Doorway,
            ConnectionPolygonId = 42,
        };
        Assert.Equal(WalkableNodeKind.Doorway, doorway.Kind);
        Assert.Equal((ushort)42, doorway.ConnectionPolygonId);
    }

    [Fact]
    public void DoorwayNodeCount_AggregatorReflectsPerCellKindMix()
    {
        // IndoorNavGraph.WalkableDoorwayNodeCount and
        // WalkableFloorNodeCount sum per-kind across all cells.
        // Build a tiny graph with two cells, each with 2 floor
        // nodes and 1 doorway, and assert the aggregates.
        IndoorCell Make(uint id)
        {
            var nodes = new List<WalkableNode>
            {
                new() { CellId = id, FloorPolygonIndex = 0, PositionWorld = Vector3.Zero },
                new() { CellId = id, FloorPolygonIndex = 0, PositionWorld = new Vector3(1, 0, 0) },
                new()
                {
                    CellId = id,
                    FloorPolygonIndex = -1,
                    PositionWorld = new Vector3(0.5f, 0.5f, 0),
                    Kind = WalkableNodeKind.Doorway,
                    ConnectionPolygonId = 7,
                },
            };
            return new IndoorCell
            {
                CellId = id,
                LandblockId = (ushort)(id >> 16),
                CellWithinLandblock = (ushort)(id & 0xFFFF),
                OriginWorld = Vector3.Zero,
                CentroidWorld = Vector3.Zero,
                BoundsWorld = new NavBounds(0, 0, 1, 1, 0, 0),
                Connections = new List<CellConnection>(),
                StaticObstacles = new List<StaticObstacle>(),
                FloorPolygons = new List<FloorPolygon>(),
                WalkableNodes = nodes,
                WalkableEdges = new List<WalkableEdge>(),
                HasGeometry = true,
            };
        }
        var graph = new IndoorNavGraph
        {
            LandblockId = 0x0001,
            Cells = new Dictionary<uint, IndoorCell>
            {
                [0x0A] = Make(0x0A),
                [0x0B] = Make(0x0B),
            },
            BoundsWorld = new NavBounds(0, 0, 1, 1, 0, 0),
            WalkableBridges = new List<WalkableBridge>(),
        };
        Assert.Equal(6, graph.WalkableNodeCount);
        Assert.Equal(4, graph.WalkableFloorNodeCount);
        Assert.Equal(2, graph.WalkableDoorwayNodeCount);
    }

    /// <summary>
    /// Threshold-cell regression: when a cell's floor polygons
    /// tessellate into two disconnected node clusters (common for
    /// stairs, narrow arches, doorway thresholds) and the cell holds
    /// two doorway nodes — one wired into each cluster — A* MUST be
    /// able to traverse from one doorway to the other within the
    /// cell. Without an explicit doorway↔doorway intra-cell edge the
    /// bot strands itself the moment it enters the cell from one side
    /// because the other side's doorway is in a different component.
    ///
    /// Locks in the "doorway pair edges" fix in
    /// LandblockNavLoader.AppendDoorwayNodesAndEdges. This test
    /// directly synthesises the cell to keep the assertion focused
    /// on the structural requirement; the loader-side fix is what
    /// produces this shape from real DAT data.
    /// </summary>
    [Fact]
    public void ThresholdCell_DoorwayPairEdge_EnablesCrossCellTraversal()
    {
        // Layout (top-down):
        //   +-----------+-------------------------+-----------+
        //   |    A      |  T (threshold cell)     |    B      |
        //   |  3x3 grid |  cluster_W . cluster_E  |  3x3 grid |
        //   +-----------+-------------------------+-----------+
        // T contains:
        //   - 3 floor nodes on west side (idx 0,1,2) — wired internally
        //   - 3 floor nodes on east side (idx 3,4,5) — wired internally
        //   - 1 doorway node on west (idx 6) wired only to cluster_W
        //   - 1 doorway node on east (idx 7) wired only to cluster_E
        // No floor edge bridges the two clusters (mimics the real
        // academy threshold cell 0x860201B4: two stair tread groups).
        var a = MakeCell(0x0A, 0f, 0f, 0f);
        var b = MakeCell(0x0B, 20f, 0f, 0f);
        var t = BuildThresholdCell(includeDoorwayPairEdge: false);

        var graph = new IndoorNavGraph
        {
            LandblockId = 0x0001,
            Cells = new Dictionary<uint, IndoorCell>
            {
                [a.CellId] = a,
                [b.CellId] = b,
                [t.CellId] = t,
            },
            BoundsWorld = new NavBounds(0, 0, 30, 10, 0, 0),
            // Bridge A↔T into doorway-W (idx 6), bridge T↔B from
            // doorway-E (idx 7). Without the doorway-pair edge,
            // A→B should fail because the two doorways are in
            // different components of T.
            WalkableBridges = new List<WalkableBridge>
            {
                new(a.CellId, 5, t.CellId, 6, 1, 1f),
                new(t.CellId, 7, b.CellId, 3, 2, 1f),
            },
        };

        var pf = new Pathfinder();
        var fromA = new Vector3(2.5f, 5f, 0f);
        // Place the goal deep inside B (centroid-east) so the K=5
        // multi-source snap doesn't pull T's east doorway into the
        // goal set — both T.7 (20,5,0) and B.3 (20,5,0) sit on the
        // shared boundary and tie for "nearest node".
        var toB = new Vector3(27.5f, 5f, 0f);
        var withoutFix = pf.FindWalkablePath(graph, fromA, toB);
        Assert.False(
            withoutFix.Found,
            "Without the doorway-pair edge the threshold cell partitions A from B.");

        // Now rebuild T WITH the doorway-pair edge and rebuild the
        // graph (Cells is exposed as IReadOnlyDictionary). The same
        // A→B query must succeed.
        var tFixed = BuildThresholdCell(includeDoorwayPairEdge: true);
        var graphFixed = new IndoorNavGraph
        {
            LandblockId = graph.LandblockId,
            Cells = new Dictionary<uint, IndoorCell>
            {
                [a.CellId] = a,
                [b.CellId] = b,
                [tFixed.CellId] = tFixed,
            },
            BoundsWorld = graph.BoundsWorld,
            WalkableBridges = graph.WalkableBridges,
        };

        var withFix = pf.FindWalkablePath(graphFixed, fromA, toB);
        Assert.True(
            withFix.Found,
            $"With the doorway-pair edge the threshold cell is traversable: {withFix.FailureReason}");
        var cellsTouched = withFix.NodePath.Select(n => n.CellId).Distinct().ToList();
        Assert.Contains((uint)0x0A, cellsTouched);
        Assert.Contains((uint)0x0B, cellsTouched);
        Assert.Contains(tFixed.CellId, cellsTouched);
    }

    /// <summary>
    /// Build a threshold-style cell at X=[10..20] Y=[0..10] with two
    /// disconnected floor clusters (west / east) plus two doorway
    /// nodes. If <paramref name="includeDoorwayPairEdge"/> is true,
    /// emit a direct edge between the two doorway nodes (the fix);
    /// otherwise leave them disconnected (the bug).
    /// </summary>
    private static IndoorCell BuildThresholdCell(bool includeDoorwayPairEdge)
    {
        // Use a constant cell id outside the {0x0A, 0x0B} test set.
        const uint id = 0x0C;
        var nodes = new List<WalkableNode>
        {
            // West cluster (idx 0..2): along x=11..13, y=5
            new() { CellId = id, FloorPolygonIndex = 0, PositionWorld = new Vector3(11f, 5f, 0f) },
            new() { CellId = id, FloorPolygonIndex = 0, PositionWorld = new Vector3(12f, 5f, 0f) },
            new() { CellId = id, FloorPolygonIndex = 0, PositionWorld = new Vector3(13f, 5f, 0f) },
            // East cluster (idx 3..5): along x=17..19, y=5 — note the
            // 3-unit gap between cluster_W (x=13) and cluster_E (x=17)
            // exceeds any single grid step so no 8-neighbour edge can
            // bridge them.
            new() { CellId = id, FloorPolygonIndex = 1, PositionWorld = new Vector3(17f, 5f, 0f) },
            new() { CellId = id, FloorPolygonIndex = 1, PositionWorld = new Vector3(18f, 5f, 0f) },
            new() { CellId = id, FloorPolygonIndex = 1, PositionWorld = new Vector3(19f, 5f, 0f) },
            // Doorway west (idx 6) at the boundary with A.
            new()
            {
                CellId = id,
                FloorPolygonIndex = 0,
                PositionWorld = new Vector3(10f, 5f, 0f),
                Kind = WalkableNodeKind.Doorway,
                ConnectionPolygonId = 1,
            },
            // Doorway east (idx 7) at the boundary with B.
            new()
            {
                CellId = id,
                FloorPolygonIndex = 1,
                PositionWorld = new Vector3(20f, 5f, 0f),
                Kind = WalkableNodeKind.Doorway,
                ConnectionPolygonId = 2,
            },
        };
        var edges = new List<WalkableEdge>
        {
            // West cluster internal edges.
            new(0, 1, 1f),
            new(1, 2, 1f),
            // East cluster internal edges.
            new(3, 4, 1f),
            new(4, 5, 1f),
            // Doorway-west wires only to the west cluster.
            new(0, 6, 1f),
            // Doorway-east wires only to the east cluster.
            new(5, 7, 1f),
        };
        if (includeDoorwayPairEdge)
        {
            // The fix: doorway↔doorway intra-cell edge bridges the
            // two clusters. Distance is the 3D Euclidean span.
            edges.Add(new WalkableEdge(6, 7, Vector3.Distance(
                nodes[6].PositionWorld, nodes[7].PositionWorld)));
        }
        return new IndoorCell
        {
            CellId = id,
            LandblockId = (ushort)(id >> 16),
            CellWithinLandblock = (ushort)(id & 0xFFFF),
            OriginWorld = new Vector3(10f, 0f, 0f),
            CentroidWorld = new Vector3(15f, 5f, 0f),
            BoundsWorld = new NavBounds(10f, 0f, 20f, 10f, 0f, 0f),
            Connections = new List<CellConnection>(),
            StaticObstacles = new List<StaticObstacle>(),
            FloorPolygons = new List<FloorPolygon>(),
            WalkableNodes = nodes,
            WalkableEdges = edges,
            HasGeometry = true,
        };
    }
}
