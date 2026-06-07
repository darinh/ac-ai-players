// SPDX-License-Identifier: AGPL-3.0-or-later
// ApproachDistanceProjection — the recent measured self→target distance
// history for the interaction target the bot most recently moved toward,
// surfaced to the LLM in the "## Approach distance history" prompt section.
//
// Why: the Motor computes the self→target distance every time it locks an
// interaction goal, but that number is Motor-only. Across ticks the LLM
// cannot tell whether its repeated selections of the SAME target are
// actually reducing the distance, so when repeated locks fail to close the
// distance it may keep re-selecting the target. Surfacing the raw recent
// distance samples lets the LLM read the trend and decide.
//
// Audit note: this is PERCEPTION. The fields are a guid, the object's
// current display name (wire-decoded), and a list of raw distance
// measurements the Motor already took. The source assigns NO priority and
// gives NO instruction — it states observed measurements. The LLM decides.

namespace HeadlessAcClient.Strategy;

using System.Collections.Generic;

/// <summary>
/// Recent measured distance samples (oldest→newest, world units) to the
/// interaction target the bot most recently locked a goal on. Projected at
/// prompt-build time so the renderer stays deterministic (no wall-clock or
/// tracker read inside <see cref="LlmGoalPolicy.BuildUserPrompt"/>).
/// </summary>
internal sealed record ApproachDistanceProjection
{
    /// <summary>The interaction target's guid.</summary>
    public required uint Guid { get; init; }

    /// <summary>
    /// Current display name of the target at projection time, or null when
    /// it is no longer in the world projection (render guid-only).
    /// </summary>
    public required string? Name { get; init; }

    /// <summary>
    /// Measured straight-line distances (world units) to the target at each
    /// of the recent goal locks on it, ordered oldest→newest. Raw
    /// measurements; no derived "trend" or judgement.
    /// </summary>
    public required IReadOnlyList<double> DistanceSamplesUnits { get; init; }
}
