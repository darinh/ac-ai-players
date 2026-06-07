namespace HeadlessAcClient.World;

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
}
