// SPDX-License-Identifier: AGPL-3.0-or-later
// PickerActivity — Slice V.
//
// A single record describing what the schema-only picker (the
// LLM-gap fallback in HandshakeDriver) is currently auto-driving.
// Rendered in the LLM prompt as a parallel "## Autonomous picker
// activity" block, distinct from the strategic Intent stack.
//
// Architectural intent (per ac-ai-players#86):
//
//   The picker MUST NOT silently dispatch actions the LLM never
//   asked for. Slice V exposes the picker's choice as a visible,
//   steerable surface — the LLM sees what the bot is doing, and
//   can either push an Intent to take strategic control, pop its
//   top to abandon a now-revealed-wrong plan, or leave the picker
//   alone to finish.
//
// Why NOT on the IntentStack:
//
//   The rubber-duck critique on the Slice V plan identified that
//   pushing picker-authored frames onto the stack would:
//     (1) violate the never-pop-root invariant when the picker
//         pushes onto an empty stack;
//     (2) invert strategic ownership — TOP is supposed to be the
//         active sub-goal owned by the LLM, not a tactical pivot
//         the picker chose;
//     (3) generate revision churn that conflicts with in-flight
//         LLM stack_ops carrying a stale revision number.
//
//   A parallel surface preserves stack invariants while still
//   making the picker's autonomous work VISIBLE and STEERABLE.

using System;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// What the schema-only picker is auto-driving this moment. Null =
/// the picker is idle (either the LLM has a pre-empted goal in
/// flight, or nothing visible is worth investigating).
/// </summary>
internal sealed record PickerActivity
{
    /// <summary>Guid of the world object the picker chose.</summary>
    public required uint TargetGuid { get; init; }

    /// <summary>Display name of the target at the time of selection.</summary>
    public required string TargetName { get; init; }

    /// <summary>
    /// Free-form picker-site identifier — "in-range" or "fallback"
    /// — so the LLM can tell which picker path was taken. NOT a
    /// game-knowledge label; it's just the source picker's name.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Short human-readable reason. Kept generic — must NOT encode
    /// game knowledge ("looting fresh corpse", "talking to greeter").
    /// Allowed values come from a small fixed set in HandshakeDriver
    /// describing the picker's MECHANICAL reason (e.g. "nearest
    /// mechanically-eligible candidate", "mechanical nearest known
    /// object in current landblock"). W.2 removed the visited-door
    /// backtrack preference — that's the LLM's call now.
    /// </summary>
    public required string Reason { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// Slice W.3 (ac-ai-players#88): true once the picker has walked
    /// to the target and the post-walk-done block fired WITHOUT a
    /// matching LLM verb goal. In this state the motor sent no
    /// opcode; the bot is parked next to the target waiting for
    /// the LLM to name a verb (Use/Talk/Pickup/Attack). The flag
    /// is rendered in the "## Autonomous picker activity" prompt
    /// block so the LLM sees the awaiting-verb status directly,
    /// not just by inference from the PickerArrivedNoAction event.
    /// Default false (set by the post-walk-done block via `with`).
    /// </summary>
    public bool Arrived { get; init; }
}
