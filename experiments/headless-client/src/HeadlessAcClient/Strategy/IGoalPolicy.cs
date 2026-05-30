// SPDX-License-Identifier: AGPL-3.0-or-later
// IGoalPolicy — the Strategy-layer contract. Two impls today:
//   - NoQuestKnowledgePolicy: zero game-content knowledge, drives
//     the bot via ItemType bitmask priorities only. Used as the
//     LLM fallback (offline, rate-limited, or parse failure).
//   - LlmGoalPolicy (Slice B): asks GitHub Models with a snapshot
//     of WorldStateProjection + recent EventStream, parses a
//     Goal JSON, falls back to NoQuestKnowledgePolicy on failure.
//
// Per ADR-0011-equivalent + EPIC #67: NO IMPLEMENTATION may key
// behavior on a hardcoded wcid/name. Selectors are ALLOWED to
// carry wcid because the runtime LLM may legitimately reference
// a wcid it observed this session. Source code must not contain
// wcid literals as decision triggers.

namespace HeadlessAcClient.Strategy;

internal interface IGoalPolicy
{
    string Source { get; }

    /// <summary>
    /// True iff the policy currently has an asynchronous decision
    /// pending (e.g. LlmGoalPolicy waiting on an HTTP response).
    /// Callers in the Motor layer use this to defer their own
    /// fallback decisions while Strategy is deliberating, so an
    /// in-flight LLM call isn't bypassed by the schema-only picker.
    /// Default-impl returns false for synchronous policies like
    /// NoQuestKnowledgePolicy.
    /// </summary>
    bool HasInflight => false;

    /// <summary>
    /// Propose the next goal given the current world state and
    /// event-stream observations. May return null to mean "no
    /// goal change; keep doing what you're doing". May return the
    /// existing `currentGoal` to mean the same.
    /// </summary>
    Goal? ProposeGoal(
        WorldStateProjection world,
        EventStream events,
        Goal? currentGoal);
}
