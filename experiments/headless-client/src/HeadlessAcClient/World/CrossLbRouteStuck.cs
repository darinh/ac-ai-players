namespace HeadlessAcClient.World;

using System;
using System.Collections.Generic;

/// <summary>
/// Mechanical nav bookkeeping: per cross-landblock Explore sighting, tracks whether the
/// bot is actually CONVERGING on the destination across successive route advances, or
/// stalling.
///
/// When the bot Explores a DISTANT sighting it cannot directly route to, the Motor advances
/// along an on-foot route PREFIX to the farthest boundary node it can reach through its own
/// explored cells, then "arrives" + re-deliberates for the next segment. If the bot never
/// gets CLOSER to the destination — it keeps returning to the same boundary it cannot cross,
/// OR it wanders between DIFFERENT boundaries with the route length fluctuating (live: the
/// same area name re-Explored 13+ times, the route prefix swinging 12 -> 68 hops, the bot
/// never arriving) — the destination is effectively UNREACHABLE from the bot's current area,
/// yet it re-Explores it indefinitely.
///
/// This records the straight-line distance from the bot to the sighting at each advance and
/// counts CONSECUTIVE advances that did NOT get meaningfully closer than the best distance
/// achieved so far. At the threshold it reports the destination
/// <see cref="RouteAdvanceState.Blocked"/> so the caller can surface it to the LLM (the
/// route-blocked Explore-loop cue) — the LLM then decides to pursue something else. An advance
/// that gets meaningfully closer is <see cref="RouteAdvanceState.Progress"/> (the route is
/// converging — reset the stall count); a non-closing advance below the threshold is
/// <see cref="RouteAdvanceState.Building"/>. Distance-based convergence SUBSUMES the
/// same-boundary case (re-hitting one boundary never gets closer) AND catches the
/// wander-between-boundaries case. Pure bookkeeping over the Motor's OWN observations — no
/// game knowledge, no priority, no autonomous target choice.
/// </summary>
internal sealed class CrossLbRouteStuck
{
    /// <summary>Outcome of recording one cross-landblock route advance.</summary>
    internal enum RouteAdvanceState
    {
        /// <summary>The bot got meaningfully closer than its best so far — converging.</summary>
        Progress,

        /// <summary>No closer than the best so far, but the stall is below the threshold.</summary>
        Building,

        /// <summary>Stalled (no net approach) at/above the threshold — route blocked.</summary>
        Blocked,
    }

    private const int MaxTrackedTargets = 256;

    // Meaningful convergence: an advance must close at least this many units on the LAST PROGRESS
    // BASELINE (the distance at the most recent Progress, NOT the running minimum) to count as
    // Progress. Comparing against a FIXED baseline — rather than lowering it on every sub-epsilon
    // advance — lets steady small gains (e.g. 3u per cycle) ACCUMULATE into a Progress step instead
    // of the threshold creeping down with the bot and false-Blocking a slowly-but-genuinely
    // converging route. Small wobble / re-hitting one boundary stays a stall.
    private const float ConvergeEpsilonU = 5f;

    private readonly Dictionary<Guid, (float ProgressBaselineU, int StallCount)> _byTarget = new();
    private readonly int _threshold;

    /// <param name="threshold">
    /// Consecutive non-converging advances that mark a route blocked. Clamped to a floor of 2
    /// (a single advance can never be "stuck"; a repeat is the minimum signal).
    /// </param>
    public CrossLbRouteStuck(int threshold) => _threshold = Math.Max(2, threshold);

    /// <summary>
    /// Record that the route toward <paramref name="sightingId"/> just advanced while the bot
    /// is <paramref name="distanceToSightingU"/> from the destination. Returns Progress when the
    /// bot has closed &gt;= ConvergeEpsilonU on the last progress baseline (converging — advance the
    /// baseline, reset the stall), else Building, then Blocked once it has failed to converge for
    /// &gt;= threshold consecutive advances.
    /// </summary>
    public RouteAdvanceState RecordAdvance(Guid sightingId, float distanceToSightingU)
    {
        if (_byTarget.TryGetValue(sightingId, out var prev))
        {
            if (distanceToSightingU <= prev.ProgressBaselineU - ConvergeEpsilonU)
            {
                // Converged meaningfully past the last baseline — advance the baseline, reset stall.
                _byTarget[sightingId] = (distanceToSightingU, 0);
                return RouteAdvanceState.Progress;
            }

            // No meaningful gain vs the LAST PROGRESS baseline. Keep the baseline FIXED (do NOT
            // lower it toward the current distance) so steady sub-epsilon gains still accumulate to
            // a future Progress step instead of the baseline creeping down with them.
            var stall = prev.StallCount + 1;
            _byTarget[sightingId] = (prev.ProgressBaselineU, stall);
            return stall >= _threshold ? RouteAdvanceState.Blocked : RouteAdvanceState.Building;
        }

        // First advance for this sighting = baseline (no prior to compare) = progress.
        if (_byTarget.Count > MaxTrackedTargets) _byTarget.Clear();
        _byTarget[sightingId] = (distanceToSightingU, 0);
        return RouteAdvanceState.Progress;
    }
}
