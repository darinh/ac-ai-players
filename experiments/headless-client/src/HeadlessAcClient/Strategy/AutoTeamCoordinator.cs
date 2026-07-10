// SPDX-License-Identifier: AGPL-3.0-or-later
// AutoTeamCoordinator — pure decisions for the config-flagged (AC_BOTS_AUTO_TEAM)
// auto-team behaviors that mechanically complete the OPERATOR-DECLARED team
// (AC_BOTS_TEAMMATE_NAMES) regardless of model quality. Fellowship auto-join uses a
// native ACE client option (set at login); allegiance has no native auto-accept, so
// the monarch's approval of a configured teammate's swear is driven here by having the
// Motor emit the AllegianceApprove goal the LLM would otherwise have to choose in time.
// Pure predicates only — no I/O, no target selection; the swear is INITIATED by the
// teammate, and this only decides whether to answer an already-pending request.

using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

internal static class AutoTeamCoordinator
{
    /// <summary>
    /// True when auto-team is enabled AND a swear-allegiance request is pending from a
    /// configured teammate — in which case the Motor should emit AllegianceApprove so
    /// the monarch answers within the server's confirmation window without depending on
    /// the LLM to pick the approve goal in time. False when disabled, no request is
    /// pending, or the requester is not a configured teammate (then the LLM decides).
    /// </summary>
    internal static bool ShouldAutoApproveAllegiance(
        bool autoTeamEnabled,
        System.Collections.Generic.IReadOnlyCollection<string> teammateNames,
        PendingAllegianceRequest? pending)
        => autoTeamEnabled
           && pending is { } request
           && TeammateCoordination.IsConfiguredTeammate(request.Text, teammateNames);
}
