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
}
