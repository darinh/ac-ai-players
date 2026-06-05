// SPDX-License-Identifier: AGPL-3.0-or-later
// SightedRecallProjection — one remembered out-of-view creature
// sighting surfaced to the LLM in the "## Recently sighted (out of
// view)" prompt section.
//
// Why: the LLM prompt otherwise shows ONLY currently-visible objects,
// so when a monster leaves the bot's field of view the LLM forgets it
// existed and cannot direct the bot back. The bot's own per-bot
// SightedLocation memory (NavGraph) already records where every named
// entity was last seen, and the resolver + nav can already route back
// to a remembered target — the missing link was surfacing the memory
// to the LLM so it can choose to return.
//
// Audit note: this is PERCEPTION. The fields are the bot's own
// remembered observations (wire-derived kind, name, last-seen coords,
// age). The source assigns NO priority — the LLM decides whether and
// what to pursue, exactly as it does for the live "## Visible nearby"
// and "## Combat readiness" sections. Position fields let the prompt
// render an approximate bearing/distance; the bot resolves the actual
// target via the standard selector at execute time.

namespace HeadlessAcClient.Strategy;

/// <summary>
/// One remembered creature sighting, projected from a
/// <c>SightedLocation</c> at prompt-build time. Carries the
/// pre-computed age so the prompt renderer stays deterministic
/// (no wall-clock read inside <see cref="LlmGoalPolicy.BuildUserPrompt"/>).
/// </summary>
internal sealed record SightedRecallProjection
{
    /// <summary>Display name; addressable via <see cref="Selector.Name"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Weenie class id when known; addressable via <see cref="Selector.Wcid"/>.</summary>
    public required uint? Wcid { get; init; }

    /// <summary>Wire-derived coarse kind (Mob / NPC / …).</summary>
    public required EntityKind Kind { get; init; }

    /// <summary>Landblock the entity was last seen in (high 16 bits of its cell).</summary>
    public required uint Landblock { get; init; }

    /// <summary>Absolute world-X of the last sighting (for bearing/distance).</summary>
    public required float WorldX { get; init; }

    /// <summary>Absolute world-Y of the last sighting (for bearing/distance).</summary>
    public required float WorldY { get; init; }

    /// <summary>Seconds since the entity was last seen, computed at push time.</summary>
    public required double AgeSeconds { get; init; }
}
