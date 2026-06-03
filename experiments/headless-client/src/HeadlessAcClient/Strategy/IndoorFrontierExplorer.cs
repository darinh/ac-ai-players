// SPDX-License-Identifier: AGPL-3.0-or-later
//
// IndoorFrontierExplorer — autonomous indoor spatial search (the
// "frontier" of robotics coverage planning), road-to-endgame Phase A1.
//
// Problem it solves: a fresh bot indoors is told (by quest dialog) to
// reach a target in another room. The target's room is occluded — the
// server only streams a room's contents to the client after the avatar
// physically crosses into it — so the target never enters perception
// and the LLM has nothing to lock onto. The bot loops re-acting on the
// one NPC it can already see.
//
// The fix is NOT to make the LLM micro-author each doorway crossing
// (that smuggles the engine's visibility model into the prompt and
// fails open on novel phrasing). Instead, when the active goal's target
// can't be resolved, the Motor/Tactics layer runs a content-free
// frontier search: walk toward the nearest reachable cell the bot has
// not yet entered. Crossing into it loads its contents; the LLM's named
// target then becomes perceivable and normal goal execution resumes.
//
// Per the hardcoded-knowledge audit this carries NO game knowledge:
//   * It reads ONLY the static indoor navmesh (DAT geometry — "what a
//     human player's client renders and collides against") plus the
//     bot's own visited-cell set.
//   * It never inspects object names, wcids, item types, or quest
//     state, and never decides to INTERACT with anything — it only
//     chooses WHERE to move so the world can be perceived. The decision
//     of WHAT to find stays entirely with the LLM goal.
//
// The chosen cell is handed to the existing indoor motor as a plain
// destination; that motor's K-hop planner already routes into
// not-yet-entered cells and opens doors en route, so this class only
// has to answer "which unexplored cell, and a stand-able point in it".

using System.Numerics;

using AcAiPlayers.WorldNav;

namespace HeadlessAcClient.Strategy;

internal static class IndoorFrontierExplorer
{
    /// <summary>The nearest unexplored reachable cell and a stand-able
    /// interior floor point within it to walk to.</summary>
    internal readonly record struct FrontierTarget(uint CellId, Vector3 Position);

    /// <summary>
    /// Choose the nearest reachable cell that the bot has not yet entered.
    /// Breadth-first over the static cell-connection graph from <paramref
    /// name="currentCellId"/>, so the first qualifying cell found is the
    /// nearest by doorway hops. A cell qualifies when it is not in
    /// <paramref name="seenCells"/>, not in <paramref name="cooldownCells"/>,
    /// and has at least one stand-able floor node. Returns null when the
    /// current cell isn't in the graph or no qualifying frontier exists
    /// (e.g. the whole reachable interior has been explored).
    /// </summary>
    internal static FrontierTarget? ChooseFrontier(
        IndoorNavGraph graph,
        uint currentCellId,
        IReadOnlySet<uint> seenCells,
        IReadOnlySet<uint> cooldownCells)
    {
        if (graph is null) return null;
        if (!graph.Cells.ContainsKey(currentCellId)) return null;

        var bfsSeen = new HashSet<uint> { currentCellId };
        var queue = new Queue<uint>();
        queue.Enqueue(currentCellId);

        while (queue.Count > 0)
        {
            var cellId = queue.Dequeue();
            if (!graph.Cells.TryGetValue(cellId, out var cell)) continue;

            // Deterministic neighbour ordering (ascending OtherCellId):
            // the DAT connection list order is arbitrary, so sort for
            // reproducible frontier selection between equal-hop cells.
            foreach (var conn in cell.Connections.OrderBy(c => c.OtherCellId))
            {
                if (!conn.OtherCellLoaded) continue;
                var other = conn.OtherCellId;
                if (!bfsSeen.Add(other)) continue;
                if (!graph.Cells.TryGetValue(other, out var otherCell)) continue;

                bool unexplored = !seenCells.Contains(other)
                                  && !cooldownCells.Contains(other);
                if (unexplored)
                {
                    var pos = PickInteriorFloor(otherCell);
                    if (pos is Vector3 p)
                        return new FrontierTarget(other, p);
                    // No stand-able floor: can't target it, but keep
                    // searching past it for a deeper reachable frontier.
                }

                // Traverse through seen / cooled / floorless cells to
                // reach genuinely-unexplored space beyond them.
                queue.Enqueue(other);
            }
        }

        return null;
    }

    /// <summary>
    /// A robustly-interior stand point: the Floor node nearest the cell
    /// centroid. Once reached, this loads the cell's server-side
    /// visibility so its occupants become perceivable. Returns null when
    /// the cell has no Floor node (e.g. a pure transition cell).
    /// </summary>
    private static Vector3? PickInteriorFloor(IndoorCell cell)
    {
        Vector3? best = null;
        float bestSq = float.MaxValue;
        foreach (var node in cell.WalkableNodes)
        {
            if (node.Kind != WalkableNodeKind.Floor) continue;
            var dx = node.PositionWorld.X - cell.CentroidWorld.X;
            var dy = node.PositionWorld.Y - cell.CentroidWorld.Y;
            var sq = dx * dx + dy * dy;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = node.PositionWorld;
            }
        }
        return best;
    }
}
