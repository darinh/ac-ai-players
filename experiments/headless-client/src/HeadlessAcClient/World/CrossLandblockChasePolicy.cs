namespace HeadlessAcClient.World;

using System;

/// <summary>
/// Pure decision for the Attack cross-landblock resolver's
/// "no explored navgraph route" fallback.
///
/// When the bot has an LLM-issued Attack goal whose target is
/// remembered in a DIFFERENT landblock, the Motor first tries to
/// route over its own explored connectivity. Indoors that route is
/// mandatory (doors/portals gate movement). OUTDOORS, a player just
/// heads straight toward a monster visible across a landblock seam,
/// so the absence of an explored route should NOT abandon the chase.
///
/// This helper decides ONLY the geometric eligibility of a straight
/// outdoor steer. It encodes no game knowledge: it inspects only the
/// wire cell-ids (outdoor bit + landblock adjacency). The LLM still
/// chose the Attack target; the Motor only mechanically executes it.
/// </summary>
public static class CrossLandblockChasePolicy
{
    /// <summary>
    /// True when, after the navgraph returned no on-foot route to a
    /// cross-landblock sighting, the Motor may steer STRAIGHT to the
    /// remembered absolute coords instead of cooling down.
    ///
    /// Requires both cells to be valid (non-zero), OUTDOOR, and in
    /// the same-or-adjacent landblock (Chebyshev &lt;= 1). Indoor
    /// cells, the zero/default cell, and far landblocks fall back to
    /// the cooldown path (they need real pathfinding, or the sighting
    /// is too stale/distant to chase blindly).
    /// </summary>
    public static bool ShouldStraightSteerOutdoor(uint selfCell, uint sightingCell)
    {
        if (selfCell == 0u || sightingCell == 0u)
            return false;
        if (!WorldDistance.IsOutdoor(selfCell) || !WorldDistance.IsOutdoor(sightingCell))
            return false;
        return WorldDistance.IsSameOrAdjacentLandblock(selfCell, sightingCell);
    }

    /// <summary>
    /// Diagnostic companion to <see cref="ShouldStraightSteerOutdoor"/>:
    /// returns a short reason string naming the FIRST failing sub-condition,
    /// so the cooldown branch can log WHY a straight outdoor steer was refused
    /// (zero cell, indoor self/sighting, or a non-adjacent landblock with the
    /// Chebyshev grid distance). Returns "eligible" when nothing fails (the
    /// steer would be allowed). Pure wire-coordinate geometry; no game
    /// knowledge.
    /// </summary>
    public static string ExplainStraightSteerRefusal(uint selfCell, uint sightingCell)
    {
        if (selfCell == 0u || sightingCell == 0u)
            return $"zero-cell (self=0x{selfCell:X8} sighting=0x{sightingCell:X8})";
        if (!WorldDistance.IsOutdoor(selfCell))
            return $"self-indoor (cell=0x{selfCell:X8})";
        if (!WorldDistance.IsOutdoor(sightingCell))
            return $"sighting-indoor (cell=0x{sightingCell:X8})";
        if (!WorldDistance.IsSameOrAdjacentLandblock(selfCell, sightingCell))
        {
            var dx = Math.Abs((int)((selfCell >> 24) & 0xFF) - (int)((sightingCell >> 24) & 0xFF));
            var dy = Math.Abs((int)((selfCell >> 16) & 0xFF) - (int)((sightingCell >> 16) & 0xFF));
            return $"non-adjacent landblock (self lb=0x{(selfCell >> 16) & 0xFFFF:X4} " +
                   $"sighting lb=0x{(sightingCell >> 16) & 0xFFFF:X4} chebyshev={Math.Max(dx, dy)})";
        }
        return "eligible";
    }
}
