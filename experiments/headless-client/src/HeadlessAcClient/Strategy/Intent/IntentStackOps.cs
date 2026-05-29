// SPDX-License-Identifier: AGPL-3.0-or-later
// IntentStackOps — the LLM-facing mutation language for IntentStack.
//
// Per the rubber-duck Slice R review (non-blocking 3): the LLM's
// stack-mutation surface is intentionally small. Four ops, each
// carrying a reason. Anything more ambitious (mid-stack abandon,
// reorder, splice) would mean the LLM has to track linear order AND
// reason about cross-frame interactions — too much rope.
//
//   push              — push a new intent onto the top.
//   pop_top           — pop the top (it's done or abandoned).
//   replace_top       — swap the top in place (intent was wrong-but-
//                       related; e.g. "buy comps" -> "buy armor").
//   mark_top_blocked  — declare the top stuck without popping it.
//
// LLM JSON schema (carried at the top level of the response next to
// the per-cycle goal fields):
//
//   {
//     "stack_revision": 5,             // echo of the prompt's revision
//     "stack_ops": [
//       {
//         "op": "push",
//         "intent": {
//           "id":          "<new id, e.g. i-005>",
//           "kind":        "quest:collect-apples",
//           "target_name": "Jonathan",
//           "target_guid": 0x90000010,   // optional
//           "rationale":   "Jonathan asked for 5 apples for the kitchen.",
//           "deadline_seconds": 600,     // optional, null = no deadline
//           "completion":  { ...IntentPredicate JSON... }
//         },
//         "reason": "Just took apple-quest from Jonathan."
//       },
//       { "op": "pop_top",     "reason": "Apple count >= 5; quest done." },
//       { "op": "replace_top", "intent": {...}, "reason": "..." },
//       { "op": "mark_top_blocked", "reason": "Jonathan went /afk." }
//     ],
//     "goal_id": "...",  // per-cycle tactical goal — same as today
//     ...
//   }
//
// All four ops are applied in order. If ANY op fails (revision
// mismatch, depth overflow, root-pop, empty stack), the entire batch
// is REJECTED — partial application would leave the stack in a state
// the LLM did not anticipate. The training data records what was
// rejected so we can debug LLM patterns.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy.Intent;

internal enum IntentStackOpKind
{
    Push           = 0,
    PopTop         = 1,
    ReplaceTop     = 2,
    MarkTopBlocked = 3,
}

internal sealed record IntentSpec
{
    /// <summary>Stable id chosen by the LLM (e.g. "i-005").</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("target_name")]
    public string? TargetName { get; init; }

    [JsonPropertyName("target_guid")]
    public uint? TargetGuid { get; init; }

    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = "";

    [JsonPropertyName("deadline_seconds")]
    public int? DeadlineSeconds { get; init; }

    [JsonPropertyName("completion")]
    public required IntentPredicate Completion { get; init; }

    /// <summary>
    /// Optional LLM-facing escape hatch. When the LLM wants a
    /// completion condition that NO existing predicate expresses
    /// cleanly, it MUST still pick a real predicate (typically
    /// AlwaysFalse + a deadline so the intent doesn't jam) AND
    /// populate this field with a free-form description of the
    /// missing predicate (e.g. "I need allegiance_xp_at_least").
    /// We log these to training data so we can add new predicate
    /// types in the next slice. NEVER consumed at runtime; purely
    /// developer-facing diagnostics.
    /// </summary>
    [JsonPropertyName("predicate_request")]
    public string? PredicateRequest { get; init; }
}

internal sealed record IntentStackOp
{
    [JsonPropertyName("op")]
    public required IntentStackOpKind Op { get; init; }

    [JsonPropertyName("intent")]
    public IntentSpec? Intent { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
}
