// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Pathfinder - A* over an IndoorNavGraph (cell-level routing).
//
// Nodes = cells (keyed by their 32-bit DAT cell ID).
// Edges = connections. Traversing connection C from cell A to cell B
//         costs dist(A.centroid, C.centroid) + dist(C.centroid,
//         B.centroid), which matches how the bot will actually walk
//         (it aims at the connection centroid, passes through it,
//         then aims at the next connection centroid in the chain).
// Heuristic = Euclidean distance from current cell centroid to goal.
//
// IMPORTANT CAVEAT (see ac-ai-players/.../pathfinding-architecture.md):
// Cell-level routing alone does NOT guarantee a clear walk path.
// Cell centroids and connection centroids ignore the static
// decorations (signs, furniture, columns, lifestones) and dynamic
// obstacles (NPCs, players, mobs, closed doors) inside cells. A
// fine-grained walkable-surface layer + dynamic obstacle layer is
// required for collision-free movement. This Pathfinder is the
// coarse layer; treat its output as a sequence of waypoints to
// REFINE, not to follow blindly.
//
// The result includes:
//   - the chain of cell IDs in order
//   - a flat list of Waypoints (cell-centroid, connection-point,
//     cell-centroid, ..., cell-centroid)
//   - total path cost and visited-node count (for diagnostics)
//
// Per ADR-0010 the runtime fog-of-war gate is layered on top: the
// caller filters Cells to only the cells the bot has actually
// perceived before invoking Find.

using System.Numerics;

namespace AcAiPlayers.WorldNav;

public sealed class Pathfinder
{
    public enum WaypointKind
    {
        /// <summary>The waypoint is a cell centroid (interior point of a cell volume).</summary>
        CellCentroid,
        /// <summary>The waypoint is a connection point (the boundary between two adjacent cells).</summary>
        ConnectionPoint,
    }

    /// <summary>One step along a navigation path.</summary>
    public readonly record struct Waypoint(Vector3 Position, WaypointKind Kind, uint Id);

    public sealed class Result
    {
        public required bool Found { get; init; }
        /// <summary>Ordered cell IDs from start to goal (inclusive). Empty if not found.</summary>
        public required IReadOnlyList<uint> CellPath { get; init; }
        /// <summary>
        /// Alternating cell-centroid / connection-point waypoints the
        /// walk-tick should aim at, in order. Length is
        /// 2*CellPath.Count - 1 when found and all connection
        /// centroids resolved.
        /// </summary>
        public required IReadOnlyList<Waypoint> Waypoints { get; init; }
        /// <summary>Total Euclidean cost of the chosen path. 0 if not found.</summary>
        public required float TotalCost { get; init; }
        /// <summary>How many cells A* expanded before terminating. For diagnostics.</summary>
        public required int VisitedNodes { get; init; }
        /// <summary>Reason the search failed, if Found is false.</summary>
        public string? FailureReason { get; init; }
    }

    /// <summary>
    /// Run A* from <paramref name="fromCellId"/> to <paramref name="toCellId"/>
    /// over the static cell graph. Returns the chain of cells +
    /// waypoints if a path exists, otherwise <see cref="Result.Found"/>
    /// is false and <see cref="Result.FailureReason"/> explains why.
    /// </summary>
    /// <param name="walkableCells">
    /// Optional filter for fog-of-war gating. When non-null, A* will
    /// only expand through cells whose IDs are in this set. The start
    /// and goal cells must both be in the set. When null, every cell
    /// in the graph is walkable.
    /// </param>
    public Result Find(
        IndoorNavGraph graph,
        uint fromCellId,
        uint toCellId,
        IReadOnlySet<uint>? walkableCells = null)
    {
        if (!graph.Cells.TryGetValue(fromCellId, out var fromCell))
            return Empty($"start cell 0x{fromCellId:X8} is not in the graph");
        if (!graph.Cells.TryGetValue(toCellId, out var toCell))
            return Empty($"goal cell 0x{toCellId:X8} is not in the graph");
        if (walkableCells is not null)
        {
            if (!walkableCells.Contains(fromCellId))
                return Empty($"start cell 0x{fromCellId:X8} is not in the walkable set (fog of war)");
            if (!walkableCells.Contains(toCellId))
                return Empty($"goal cell 0x{toCellId:X8} is not in the walkable set (fog of war)");
        }
        if (fromCellId == toCellId)
        {
            return new Result
            {
                Found = true,
                CellPath = new[] { fromCellId },
                Waypoints = new[] { new Waypoint(fromCell.CentroidWorld, WaypointKind.CellCentroid, fromCellId) },
                TotalCost = 0,
                VisitedNodes = 1,
            };
        }

        var goalCentroid = toCell.CentroidWorld;
        var gScore = new Dictionary<uint, float> { [fromCellId] = 0f };
        var cameFrom = new Dictionary<uint, (uint prevCell, CellConnection connection)>();
        var open = new PriorityQueue<uint, float>();
        open.Enqueue(fromCellId, Vector3.Distance(fromCell.CentroidWorld, goalCentroid));
        var visited = new HashSet<uint>();

        while (open.TryDequeue(out var currentId, out _))
        {
            // Skip stale entries: a cell may have been re-queued with a
            // better priority after we already settled it.
            if (!visited.Add(currentId))
                continue;

            if (currentId == toCellId)
                return Reconstruct(graph, fromCellId, toCellId, cameFrom, gScore[toCellId], visited.Count);

            var current = graph.Cells[currentId];
            float currentG = gScore[currentId];
            foreach (var connection in current.Connections)
            {
                if (!connection.OtherCellLoaded) continue;
                uint nextId = connection.OtherCellId;
                if (walkableCells is not null && !walkableCells.Contains(nextId)) continue;
                if (!graph.Cells.TryGetValue(nextId, out var nextCell)) continue;

                float stepCost = EdgeCost(current, connection, nextCell);
                float tentative = currentG + stepCost;
                if (gScore.TryGetValue(nextId, out var prevG) && tentative >= prevG)
                    continue;

                gScore[nextId] = tentative;
                cameFrom[nextId] = (currentId, connection);
                float f = tentative + Vector3.Distance(nextCell.CentroidWorld, goalCentroid);
                open.Enqueue(nextId, f);
            }
        }

        return new Result
        {
            Found = false,
            CellPath = Array.Empty<uint>(),
            Waypoints = Array.Empty<Waypoint>(),
            TotalCost = 0,
            VisitedNodes = visited.Count,
            FailureReason = $"no path from 0x{fromCellId:X8} to 0x{toCellId:X8} (graph is partitioned or the goal is unreachable through connections)",
        };
    }

    private static float EdgeCost(IndoorCell from, CellConnection connection, IndoorCell to)
    {
        if (connection.CentroidWorld is Vector3 pc)
            return Vector3.Distance(from.CentroidWorld, pc) + Vector3.Distance(pc, to.CentroidWorld);
        return Vector3.Distance(from.CentroidWorld, to.CentroidWorld);
    }

    private static Result Reconstruct(
        IndoorNavGraph graph,
        uint fromId,
        uint toId,
        Dictionary<uint, (uint prevCell, CellConnection connection)> cameFrom,
        float totalCost,
        int visitedNodes)
    {
        // Walk the predecessor chain backwards from goal to start, then reverse.
        var cellChain = new List<uint> { toId };
        var connectionChain = new List<CellConnection>(); // length = cellChain.Count - 1
        uint cursor = toId;
        while (cursor != fromId)
        {
            var (prev, connection) = cameFrom[cursor];
            cellChain.Add(prev);
            connectionChain.Add(connection);
            cursor = prev;
        }
        cellChain.Reverse();
        connectionChain.Reverse();

        // Build alternating waypoint list: cell0, conn01, cell1, conn12, cell2, ...
        var waypoints = new List<Waypoint>(cellChain.Count * 2);
        for (int i = 0; i < cellChain.Count; i++)
        {
            var cell = graph.Cells[cellChain[i]];
            waypoints.Add(new Waypoint(cell.CentroidWorld, WaypointKind.CellCentroid, cell.CellId));
            if (i < connectionChain.Count)
            {
                var connection = connectionChain[i];
                if (connection.CentroidWorld is Vector3 pc)
                    waypoints.Add(new Waypoint(pc, WaypointKind.ConnectionPoint, connection.PolygonId));
            }
        }

        return new Result
        {
            Found = true,
            CellPath = cellChain,
            Waypoints = waypoints,
            TotalCost = totalCost,
            VisitedNodes = visitedNodes,
        };
    }

    private static Result Empty(string reason) => new()
    {
        Found = false,
        CellPath = Array.Empty<uint>(),
        Waypoints = Array.Empty<Waypoint>(),
        TotalCost = 0,
        VisitedNodes = 0,
        FailureReason = reason,
    };

    // ------------------------------------------------------------------
    // Walkable-node A*
    // ------------------------------------------------------------------

    /// <summary>
    /// A single node in the unified walkable graph, identified by its
    /// owning cell and its index inside that cell's WalkableNodes list.
    /// </summary>
    public readonly record struct WalkableNodeRef(uint CellId, int NodeIndex);

    public sealed class WalkableResult
    {
        public required bool Found { get; init; }
        /// <summary>Ordered world-space positions from start to goal. Empty if not found.</summary>
        public required IReadOnlyList<Vector3> Points { get; init; }
        /// <summary>Ordered node references corresponding to <see cref="Points"/>.</summary>
        public required IReadOnlyList<WalkableNodeRef> NodePath { get; init; }
        /// <summary>Total 3D Euclidean cost of the chosen path. 0 if not found.</summary>
        public required float TotalCost { get; init; }
        /// <summary>How many nodes A* settled before terminating.</summary>
        public required int VisitedNodes { get; init; }
        public string? FailureReason { get; init; }
    }

    /// <summary>
    /// Run A* across the unified walkable-node graph (intra-cell
    /// <see cref="WalkableEdge"/> + cross-cell <see cref="WalkableBridge"/>).
    /// The returned path starts at the walkable node nearest to
    /// <paramref name="fromWorld"/> and ends at the walkable node
    /// nearest to <paramref name="toWorld"/>; callers should treat the
    /// first and last Points as snap-to-graph approximations of the
    /// requested endpoints.
    ///
    /// Uses multi-source / multi-goal A*: snaps the world endpoints to
    /// the K nearest walkable nodes (K =
    /// <see cref="SnapCandidatesPerEndpoint"/>) and treats every start
    /// candidate as a virtual source and every goal candidate as a
    /// virtual sink. This is robust to a single nearest-node landing
    /// in an intra-cell micro-component that is partitioned from the
    /// bridges or from the rest of the room by static obstacles —
    /// alternative snap candidates often lie in the cell's main
    /// connected component and produce a successful path.
    /// </summary>
    /// <param name="walkableCells">
    /// Optional fog-of-war filter. When non-null, both endpoints must
    /// resolve to walkable nodes whose cells are in this set, and A*
    /// will not expand into any cell outside the set.
    /// </param>
    public WalkableResult FindWalkablePath(
        IndoorNavGraph graph,
        Vector3 fromWorld,
        Vector3 toWorld,
        IReadOnlySet<uint>? walkableCells = null)
    {
        var startCandidates = NearestWalkableNodes(
            graph, fromWorld, SnapCandidatesPerEndpoint, walkableCells);
        if (startCandidates.Count == 0)
            return EmptyWalkable("no walkable node near start position (try widening walkableCells or check the cell is loaded)");
        var goalCandidates = NearestWalkableNodes(
            graph, toWorld, SnapCandidatesPerEndpoint, walkableCells);
        if (goalCandidates.Count == 0)
            return EmptyWalkable("no walkable node near goal position");

        // Use the FIRST (closest) goal candidate as the heuristic
        // anchor. Multi-goal A* with the min-of-K heuristic would be
        // ideal but more code; "anchor on closest goal" still respects
        // A* admissibility because the heuristic underestimates cost
        // to ANY goal in the goal-set as long as it underestimates
        // cost to the anchor.
        var goalAnchorPos = graph.Cells[goalCandidates[0].CellId]
            .WalkableNodes[goalCandidates[0].NodeIndex].PositionWorld;
        var goalSet = new HashSet<WalkableNodeRef>(goalCandidates);

        // Single-node shortcut: if any start candidate is also a goal
        // candidate, return immediately.
        foreach (var s in startCandidates)
        {
            if (goalSet.Contains(s))
            {
                var pos = graph.Cells[s.CellId].WalkableNodes[s.NodeIndex].PositionWorld;
                return new WalkableResult
                {
                    Found = true,
                    Points = new[] { pos },
                    NodePath = new[] { s },
                    TotalCost = 0,
                    VisitedNodes = 1,
                };
            }
        }

        // Build a per-endpoint index of bridges so adjacency expansion
        // is O(degree) instead of O(total bridges). For ~12k bridges
        // this is sub-millisecond; cache externally if you call this
        // in a tight loop.
        var bridgesByEndpoint = new Dictionary<WalkableNodeRef, List<WalkableBridge>>();
        foreach (var b in graph.WalkableBridges)
        {
            var a = new WalkableNodeRef(b.FromCellId, b.FromNodeIndex);
            var c = new WalkableNodeRef(b.ToCellId, b.ToNodeIndex);
            if (!bridgesByEndpoint.TryGetValue(a, out var listA))
                bridgesByEndpoint[a] = listA = new List<WalkableBridge>();
            listA.Add(b);
            if (!bridgesByEndpoint.TryGetValue(c, out var listC))
                bridgesByEndpoint[c] = listC = new List<WalkableBridge>();
            listC.Add(b);
        }

        var gScore = new Dictionary<WalkableNodeRef, float>();
        var cameFrom = new Dictionary<WalkableNodeRef, WalkableNodeRef>();
        var open = new PriorityQueue<WalkableNodeRef, float>();
        // Sentinel value used by ReconstructWalkable to detect "this
        // node is a virtual start" (no predecessor to follow).
        var multiStart = new HashSet<WalkableNodeRef>();
        foreach (var sc in startCandidates)
        {
            // Use distance from the requested world position to the
            // snapped node as the initial g-cost. Two start candidates
            // farther from the world position pay a higher initial
            // cost, so A* naturally prefers the closer snap when both
            // can reach the goal.
            var scPos = graph.Cells[sc.CellId].WalkableNodes[sc.NodeIndex].PositionWorld;
            float initG = Vector3.Distance(fromWorld, scPos);
            if (gScore.TryGetValue(sc, out var prev) && prev <= initG) continue;
            gScore[sc] = initG;
            multiStart.Add(sc);
            open.Enqueue(sc, initG + Vector3.Distance(scPos, goalAnchorPos));
        }

        var visited = new HashSet<WalkableNodeRef>();

        while (open.TryDequeue(out var current, out _))
        {
            if (!visited.Add(current)) continue;
            if (goalSet.Contains(current))
                return ReconstructWalkableMulti(
                    graph, multiStart, current, cameFrom,
                    gScore[current], visited.Count);

            var currentCell = graph.Cells[current.CellId];
            float currentG = gScore[current];

            // 1) Intra-cell neighbours via this cell's WalkableEdges.
            foreach (var edge in currentCell.WalkableEdges)
            {
                int otherIdx = edge.NodeA == current.NodeIndex
                    ? edge.NodeB
                    : edge.NodeB == current.NodeIndex ? edge.NodeA : -1;
                if (otherIdx < 0) continue;
                var nbr = new WalkableNodeRef(current.CellId, otherIdx);
                RelaxWalkable(graph, nbr, currentG + edge.DistanceUnits, current,
                    gScore, cameFrom, open, goalAnchorPos);
            }

            // 2) Cross-cell neighbours via WalkableBridges touching this node.
            if (bridgesByEndpoint.TryGetValue(current, out var attached))
            {
                foreach (var b in attached)
                {
                    var nbr = b.FromCellId == current.CellId && b.FromNodeIndex == current.NodeIndex
                        ? new WalkableNodeRef(b.ToCellId, b.ToNodeIndex)
                        : new WalkableNodeRef(b.FromCellId, b.FromNodeIndex);
                    if (walkableCells is not null && !walkableCells.Contains(nbr.CellId)) continue;
                    RelaxWalkable(graph, nbr, currentG + b.DistanceUnits, current,
                        gScore, cameFrom, open, goalAnchorPos);
                }
            }
        }

        return new WalkableResult
        {
            Found = false,
            Points = Array.Empty<Vector3>(),
            NodePath = Array.Empty<WalkableNodeRef>(),
            TotalCost = 0,
            VisitedNodes = visited.Count,
            FailureReason = "no walkable path between snapped nodes (graph partitioned at this resolution; check WalkableBridges / fog of war)",
        };
    }

    /// <summary>
    /// How many snap candidates per endpoint to feed into multi-source
    /// / multi-goal A*. The nearest single walkable node is often in a
    /// partitioned intra-cell micro-component; offering alternative
    /// snap points lets A* escape via a sibling node in the cell's
    /// main connected component. K=5 is generous; the A* runtime cost
    /// is dominated by graph traversal, not start-set size.
    /// </summary>
    private const int SnapCandidatesPerEndpoint = 5;

    private static void RelaxWalkable(
        IndoorNavGraph graph,
        WalkableNodeRef neighbour,
        float tentativeG,
        WalkableNodeRef predecessor,
        Dictionary<WalkableNodeRef, float> gScore,
        Dictionary<WalkableNodeRef, WalkableNodeRef> cameFrom,
        PriorityQueue<WalkableNodeRef, float> open,
        Vector3 goalPos)
    {
        if (gScore.TryGetValue(neighbour, out var prevG) && tentativeG >= prevG)
            return;
        gScore[neighbour] = tentativeG;
        cameFrom[neighbour] = predecessor;
        var pos = graph.Cells[neighbour.CellId].WalkableNodes[neighbour.NodeIndex].PositionWorld;
        open.Enqueue(neighbour, tentativeG + Vector3.Distance(pos, goalPos));
    }

    private static WalkableResult ReconstructWalkable(
        IndoorNavGraph graph,
        WalkableNodeRef start,
        WalkableNodeRef goal,
        Dictionary<WalkableNodeRef, WalkableNodeRef> cameFrom,
        float totalCost,
        int visitedNodes)
    {
        var chain = new List<WalkableNodeRef> { goal };
        var cursor = goal;
        while (cursor != start)
        {
            cursor = cameFrom[cursor];
            chain.Add(cursor);
        }
        chain.Reverse();

        var points = new List<Vector3>(chain.Count);
        foreach (var n in chain)
            points.Add(graph.Cells[n.CellId].WalkableNodes[n.NodeIndex].PositionWorld);

        return new WalkableResult
        {
            Found = true,
            Points = points,
            NodePath = chain,
            TotalCost = totalCost,
            VisitedNodes = visitedNodes,
        };
    }

    /// <summary>
    /// Multi-source variant of <see cref="ReconstructWalkable"/>:
    /// follows <paramref name="cameFrom"/> from <paramref name="goal"/>
    /// backward until we hit a node in <paramref name="multiStart"/>
    /// (the set of virtual start candidates). The chain returned is
    /// ordered start -> goal.
    /// </summary>
    private static WalkableResult ReconstructWalkableMulti(
        IndoorNavGraph graph,
        HashSet<WalkableNodeRef> multiStart,
        WalkableNodeRef goal,
        Dictionary<WalkableNodeRef, WalkableNodeRef> cameFrom,
        float totalCost,
        int visitedNodes)
    {
        var chain = new List<WalkableNodeRef> { goal };
        var cursor = goal;
        // Walk back until we hit one of the virtual sources. Virtual
        // sources have no entry in cameFrom (we never wrote one for
        // them), so a TryGetValue miss is the terminator.
        while (!multiStart.Contains(cursor))
        {
            if (!cameFrom.TryGetValue(cursor, out var prev)) break;
            cursor = prev;
            chain.Add(cursor);
        }
        chain.Reverse();

        var points = new List<Vector3>(chain.Count);
        foreach (var n in chain)
            points.Add(graph.Cells[n.CellId].WalkableNodes[n.NodeIndex].PositionWorld);

        return new WalkableResult
        {
            Found = true,
            Points = points,
            NodePath = chain,
            TotalCost = totalCost,
            VisitedNodes = visitedNodes,
        };
    }

    /// <summary>
    /// Find the walkable node in the graph closest to
    /// <paramref name="world"/> in 3D Euclidean distance, optionally
    /// restricted to cells in <paramref name="walkableCells"/>. Returns
    /// null when no cell with walkable nodes is in scope.
    /// </summary>
    public static WalkableNodeRef? NearestWalkableNode(
        IndoorNavGraph graph,
        Vector3 world,
        IReadOnlySet<uint>? walkableCells = null)
    {
        WalkableNodeRef? best = null;
        float bestSq = float.PositiveInfinity;
        foreach (var (cellId, cell) in graph.Cells)
        {
            if (walkableCells is not null && !walkableCells.Contains(cellId)) continue;
            for (int i = 0; i < cell.WalkableNodes.Count; i++)
            {
                var p = cell.WalkableNodes[i].PositionWorld;
                float dx = p.X - world.X;
                float dy = p.Y - world.Y;
                float dz = p.Z - world.Z;
                float d = dx * dx + dy * dy + dz * dz;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = new WalkableNodeRef(cellId, i);
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Find up to <paramref name="k"/> walkable nodes in the graph
    /// closest to <paramref name="world"/> in 3D Euclidean distance,
    /// optionally restricted to cells in <paramref name="walkableCells"/>.
    /// Returned list is ordered ascending by distance (closest first)
    /// and may be empty if no eligible cell has walkable nodes.
    ///
    /// Used by <see cref="FindWalkablePath"/> to seed multi-source /
    /// multi-goal A*. K alternative snap candidates are robust against
    /// the single nearest landing in an intra-cell micro-component
    /// that is partitioned from the rest of the room (a real failure
    /// mode in the academy reference where signs and door columns
    /// partition floor samples).
    /// </summary>
    public static IReadOnlyList<WalkableNodeRef> NearestWalkableNodes(
        IndoorNavGraph graph,
        Vector3 world,
        int k,
        IReadOnlySet<uint>? walkableCells = null)
    {
        if (k <= 0) return Array.Empty<WalkableNodeRef>();
        var picks = new List<(WalkableNodeRef Ref, float DistSq)>(k * 4);
        foreach (var (cellId, cell) in graph.Cells)
        {
            if (walkableCells is not null && !walkableCells.Contains(cellId)) continue;
            for (int i = 0; i < cell.WalkableNodes.Count; i++)
            {
                var p = cell.WalkableNodes[i].PositionWorld;
                float dx = p.X - world.X;
                float dy = p.Y - world.Y;
                float dz = p.Z - world.Z;
                float d = dx * dx + dy * dy + dz * dz;
                picks.Add((new WalkableNodeRef(cellId, i), d));
            }
        }
        if (picks.Count == 0) return Array.Empty<WalkableNodeRef>();
        picks.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        int take = System.Math.Min(k, picks.Count);
        var result = new List<WalkableNodeRef>(take);
        for (int i = 0; i < take; i++)
            result.Add(picks[i].Ref);
        return result;
    }

    private static WalkableResult EmptyWalkable(string reason) => new()
    {
        Found = false,
        Points = Array.Empty<Vector3>(),
        NodePath = Array.Empty<WalkableNodeRef>(),
        TotalCost = 0,
        VisitedNodes = 0,
        FailureReason = reason,
    };
}
