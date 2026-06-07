// SPDX-License-Identifier: AGPL-3.0-or-later
//
// HuntAuthorization — a single typed predicate answering "does the
// IntentStack currently carry an LLM/operator HUNT commitment?".
//
// A hunt commitment is recognised from TYPED intent signals only — never
// from English rationale parsing:
//   * the operator-root "Hunt" intent (Kind == "Hunt"), or
//   * an LLM-authored hunt excursion (Kind == "hunt-excursion"), or
//   * any intent whose typed completion is `visible_tag:monster` — the
//     LLM chose "until a monster is in view" as the goalpost.
//
// This is shared by two Motor sites so they agree on one definition:
//   1. the outdoor frontier Mob-bias (TryChooseOutdoorFrontierDest call
//      site) — bias the geometric walk toward remembered sightings; and
//   2. the autonomous picker suppression — while a hunt is active, the
//      picker must not autonomously walk the bot to the nearest inert
//      town object, capturing it away from the hunt.
//
// It carries NO game knowledge: it inspects Intent.Kind labels the LLM
// itself authored and the typed completion predicate. It names no wcids,
// NPC names, or landblocks, and decides nothing about WHAT to interact
// with — only WHETHER the strategist has committed to hunting.

namespace HeadlessAcClient.Strategy.Intent;

internal static class HuntAuthorization
{
    /// <summary>
    /// True when <paramref name="top"/> represents a hunt commitment by
    /// its LLM-authored Kind label or its typed `visible_tag:monster`
    /// completion. Null (empty stack) returns false. Does NOT inspect
    /// lifecycle status — callers that need an *active* hunt should also
    /// check <c>top.Status == IntentLifecycle.Active</c>.
    /// </summary>
    internal static bool IsHuntCommitment(Intent? top)
    {
        if (top is null) return false;
        if (string.Equals(top.Kind, "Hunt", System.StringComparison.Ordinal)) return true;
        if (string.Equals(top.Kind, "hunt-excursion", System.StringComparison.Ordinal)) return true;
        return top.Completion is VisibleTagPredicate vtp &&
               string.Equals(vtp.Tag, "monster", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the stack's top intent is a hunt commitment AND it is
    /// still <see cref="IntentLifecycle.Active"/> (not Blocked / Completed
    /// / Expired). This is the signal the autonomous picker uses to
    /// suppress its town-scenery walk: a stale or blocked hunt must not
    /// keep the picker muted, so it requires a live commitment.
    /// </summary>
    internal static bool IsActiveHunt(Intent? top)
        => top is { Status: IntentLifecycle.Active } && IsHuntCommitment(top);
}
