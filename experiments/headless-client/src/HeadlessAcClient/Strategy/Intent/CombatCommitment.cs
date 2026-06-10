// SPDX-License-Identifier: AGPL-3.0-or-later
//
// CombatCommitment — recognises when the IntentStack top is an EXPLICIT,
// LLM-authored kill commitment expressed as a TYPED kill-count completion
// predicate, and extracts the LLM's optional named-kind filter.
//
// This is the authorization gate for the Motor's autonomous kill-intent
// decomposition (LlmGoalPolicy.ChooseCombatChainTarget): the Motor may mint
// the next Attack toward an already-counted "kill N [of X]" goal WITHOUT a
// per-monster LLM round-trip, but ONLY because the LLM itself authored that
// sustained commitment via a typed predicate. Reading a generic
// `visible_tag:monster` excursion or a Kind label is NOT enough (the rubber-
// duck review flagged that as over-reach) — it must be a kill-count
// predicate, which is an unambiguous numeric commitment to keep killing.
//
// INVARIANT (audit): this never ORIGINATES a combat commitment. It only reads
// typed predicate fields the LLM authored (the count threshold and the
// optional name_contains filter). It names no wcids, NPC names, landblocks, or
// object-type urgency. The decision of WHETHER to grind, and WHICH kind, is
// the LLM's — encoded in the predicate it pushed.

namespace HeadlessAcClient.Strategy.Intent;

internal static class CombatCommitment
{
    /// <summary>
    /// True when <paramref name="top"/> is an <see cref="IntentLifecycle.Active"/>
    /// intent whose typed completion is a kill-count predicate
    /// (<see cref="KillCountSincePushAtLeastPredicate"/> or
    /// <see cref="KillCountTotalAtLeastPredicate"/>) — i.e. the LLM explicitly
    /// committed to "kill N [of a named kind]". <paramref name="nameFilter"/>
    /// receives the LLM-authored <c>name_contains</c> filter when present
    /// (so decomposition only attacks the named kind), or null for a
    /// kind-agnostic kill-count commitment. Null/blocked/non-kill-count tops
    /// return false.
    /// </summary>
    internal static bool IsActiveKillCommitment(Intent? top, out string? nameFilter)
    {
        nameFilter = null;
        if (top is not { Status: IntentLifecycle.Active }) return false;

        switch (top.Completion)
        {
            case KillCountSincePushAtLeastPredicate k:
                nameFilter = string.IsNullOrWhiteSpace(k.NameContains) ? null : k.NameContains.Trim();
                return true;
            case KillCountTotalAtLeastPredicate:
                // Kind-agnostic "reach N total kills" — no name filter.
                return true;
            default:
                return false;
        }
    }
}
