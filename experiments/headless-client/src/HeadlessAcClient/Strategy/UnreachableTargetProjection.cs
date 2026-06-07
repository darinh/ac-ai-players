// SPDX-License-Identifier: AGPL-3.0-or-later
// UnreachableTargetProjection — one interaction-target guid the SERVER
// refused as out-of-reach, surfaced to the LLM in the
// "## Server-refused interaction targets" prompt section.
//
// Why: the cp-2338 InteractUnreachableTracker is a Motor-only guard. When
// the server refuses an interaction as out-of-reach, the Motor marks that
// guid and silently treats any goal that resolves to it as unresolved for
// a TTL cooldown. But the LLM is BLIND to that suppression — its prompt
// still lists the object as a normal visible target, so it keeps emitting
// the same interaction goal, which the resolver re-resolves to the
// suppressed guid and the Motor drops every cycle (live repro: a Use{Door}
// goal that the sticky-objective re-emitted 3x, all unresolved, before a
// real LLM call escaped). Surfacing the suppression as perception lets the
// LLM stop targeting the refused guid and pick a reachable one instead.
//
// Audit note: this is PERCEPTION. The fields are a guid the SERVER itself
// refused, the object's current display name (wire-decoded), and the TTL
// state of the Motor's own cooldown. The source assigns NO priority and
// gives NO instruction — it states the observed server response and the
// Motor's current resolver behavior. The LLM decides what to do.

namespace HeadlessAcClient.Strategy;

/// <summary>
/// One interaction-target guid currently suppressed by the
/// <c>InteractUnreachableTracker</c> because the server refused it as
/// out-of-reach. Projected at prompt-build time with the pre-computed
/// remaining cooldown so the prompt renderer stays deterministic (no
/// wall-clock read inside <see cref="LlmGoalPolicy.BuildUserPrompt"/>).
/// </summary>
internal sealed record UnreachableTargetProjection
{
    /// <summary>The guid the server refused as out-of-reach.</summary>
    public required uint Guid { get; init; }

    /// <summary>
    /// Current display name of the object at projection time, or null when
    /// the object is no longer in the world projection (render guid-only).
    /// </summary>
    public required string? Name { get; init; }

    /// <summary>
    /// Approximate seconds remaining on the Motor's suppression cooldown,
    /// clamped non-negative. TTL state, not an instruction.
    /// </summary>
    public required double RemainingCooldownSeconds { get; init; }
}
