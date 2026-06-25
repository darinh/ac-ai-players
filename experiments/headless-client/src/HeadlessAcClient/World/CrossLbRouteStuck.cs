namespace HeadlessAcClient.World;

using System;
using System.Collections.Generic;

/// <summary>
/// Mechanical nav bookkeeping: per cross-landblock Explore sighting, tracks the
/// boundary node the route advance last steered to and how many times in a row it
/// re-advanced that SAME boundary.
///
/// When the bot Explores a DISTANT sighting it cannot directly route to, the Motor
/// advances along an on-foot route PREFIX to the farthest boundary node it can reach
/// through its own explored cells, then "arrives" + re-deliberates for the next
/// segment. If the bot keeps returning to the SAME boundary (it cannot get PAST it —
/// the next landblock is unexplored/unwalkable, or the navmesh has no crossing), the
/// destination is effectively UNREACHABLE from the bot's current area, yet the bot
/// re-Explores it indefinitely (live: an area name re-Explored 13+ times, the route
/// re-advancing the same boundary node each cycle, the bot never reaching it).
///
/// This counts the consecutive same-boundary re-advances per sighting and, at the
/// threshold, reports the destination as <see cref="RouteAdvanceState.Blocked"/> so the
/// caller can surface it to the LLM (the route-blocked Explore-loop cue) — the LLM then
/// decides to pursue something else. A NEW boundary node is
/// <see cref="RouteAdvanceState.Progress"/> (the route advanced past the old one); a
/// same-boundary repeat below the threshold is <see cref="RouteAdvanceState.Building"/>.
/// Pure bookkeeping over the Motor's OWN route-advance observations — no game knowledge,
/// no priority, no autonomous target choice.
/// </summary>
internal sealed class CrossLbRouteStuck
{
    /// <summary>Outcome of recording one cross-landblock route advance.</summary>
    internal enum RouteAdvanceState
    {
        /// <summary>A new boundary node — the route advanced past the previous one.</summary>
        Progress,

        /// <summary>The same boundary repeated, but below the blocked threshold.</summary>
        Building,

        /// <summary>The same boundary repeated at/above the threshold — route blocked.</summary>
        Blocked,
    }

    private const int MaxTrackedTargets = 256;

    private readonly Dictionary<Guid, (Guid BoundaryId, int Count)> _byTarget = new();
    private readonly int _threshold;

    /// <param name="threshold">
    /// Consecutive same-boundary re-advances that mark a route blocked. Clamped to a
    /// floor of 2 (a single advance can never be "stuck"; a repeat is the minimum signal).
    /// </param>
    public CrossLbRouteStuck(int threshold) => _threshold = Math.Max(2, threshold);

    /// <summary>
    /// Record that the route toward <paramref name="sightingId"/> just steered to
    /// <paramref name="boundaryId"/>. Returns whether this re-advanced the SAME boundary
    /// (Building / Blocked) or moved to a new one (Progress).
    /// </summary>
    public RouteAdvanceState RecordAdvance(Guid sightingId, Guid boundaryId)
    {
        if (_byTarget.TryGetValue(sightingId, out var prev) && prev.BoundaryId == boundaryId)
        {
            var n = prev.Count + 1;
            _byTarget[sightingId] = (boundaryId, n);
            return n >= _threshold ? RouteAdvanceState.Blocked : RouteAdvanceState.Building;
        }

        // New (or first) boundary for this sighting = progress past any earlier one.
        if (_byTarget.Count > MaxTrackedTargets && !_byTarget.ContainsKey(sightingId))
            _byTarget.Clear();
        _byTarget[sightingId] = (boundaryId, 1);
        return RouteAdvanceState.Progress;
    }
}
