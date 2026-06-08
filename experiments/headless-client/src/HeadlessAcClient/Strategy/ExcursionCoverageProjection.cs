// SPDX-License-Identifier: AGPL-3.0-or-later
// ExcursionCoverageProjection — a rolling-window summary of the bot's OWN
// recent outdoor coverage, surfaced to the LLM in the "## Recent outdoor
// coverage" prompt capsule.
//
// Why: the directional Explore{direction} verb (cp-2351) lets the LLM steer a
// hunt excursion by compass bearing, but the LLM picks the bearing BLIND — it
// has no raw facts about how much ground it has recently covered or which way
// it has been heading. Without that it cannot recognize a fruitless bearing and
// try a different one. This projection states the raw facts; the LLM decides.
//
// Audit note: this is PERCEPTION over the bot's OWN memory only — a count of
// distinct outdoor landblocks it has visited in a rolling time window, the net
// travel vector from the oldest visited node in the window to its current
// position, and a count of its OWN recorded Mob sightings in the same window.
// No map, no zone table, no hardcoded monster locations, no recommendation.
// The source states observed measurements; the LLM decides whether/where to go.

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Rolling-window summary of the bot's own recent outdoor coverage. Projected
/// at prompt-build time (the Motor reads its visited-node + sighting memory),
/// so the renderer stays deterministic (no wall-clock or tracker read inside
/// <see cref="LlmGoalPolicy.BuildUserPrompt"/>).
/// </summary>
internal sealed record ExcursionCoverageProjection
{
    /// <summary>The length of the rolling window, in minutes.</summary>
    public required double WindowMinutes { get; init; }

    /// <summary>
    /// Count of DISTINCT outdoor landblocks the bot has visited within the
    /// window (its own visited-node memory; landblock = CellId >> 16). Count
    /// only — no landblock ids, no map.
    /// </summary>
    public required int DistinctOutdoorLandblocks { get; init; }

    /// <summary>
    /// Net travel vector (global meters, +Y north / +X east) from the OLDEST
    /// visited outdoor node in the window to the bot's current position. The
    /// renderer turns this into an 8-way compass bearing; the raw components
    /// are never shown. Window-level displacement (slow-changing), not the
    /// instantaneous step, so it does not invite per-tick direction thrash.
    /// </summary>
    public required float NetTravelDx { get; init; }

    /// <summary>See <see cref="NetTravelDx"/>.</summary>
    public required float NetTravelDy { get; init; }

    /// <summary>
    /// Count of the bot's OWN recorded Mob sightings within the window. Zero is
    /// meaningful (the excursion has found nothing); a positive count is too
    /// (it was not barren). Count only — no names, no positions.
    /// </summary>
    public required int MobSightingsInWindow { get; init; }
}
