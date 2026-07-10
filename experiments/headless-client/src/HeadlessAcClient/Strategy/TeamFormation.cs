// SPDX-License-Identifier: AGPL-3.0-or-later
// TeamFormation — pure, side-effect-free DESCRIPTION of the operator-declared team's
// (AC_BOTS_TEAMMATE_NAMES) fellowship/allegiance formation state, for observability
// only. The Motor logs the snapshot when it changes so multi-bot formation progress
// (fellowship grouping, progressive recruiting, vassal-swear) is greppable from a
// single line per transition instead of being reconstructed from scattered recruit /
// fellowship-update / player-sighting lines. Reads only wire-decoded projections +
// operator config; makes NO decision, assigns NO priority, and selects NO target.

using System.Collections.Generic;
using System.Linq;

namespace HeadlessAcClient.Strategy;

internal static class TeamFormation
{
    /// <summary>
    /// A stable, change-detectable one-line snapshot of the operator-declared team's
    /// formation state, or <c>null</c> when no teammates are configured (nothing to
    /// observe). The string is intended purely for a log marker the Motor emits on
    /// change — it drives no behavior.
    /// </summary>
    /// <param name="teammateNames">Configured teammate names (AC_BOTS_TEAMMATE_NAMES).</param>
    /// <param name="selfName">The bot's own name, folded into the roster count so the
    /// intended team size is correct whether or not the operator listed self.</param>
    /// <param name="inFellowship">Whether the bot is currently in a fellowship.</param>
    /// <param name="amLeader">Whether the bot is the fellowship leader.</param>
    /// <param name="fellowshipLeaderName">Display name of the fellowship leader, if known.</param>
    /// <param name="currentMemberNames">Names currently in the fellowship roster.</param>
    /// <param name="visiblePlayerNames">Names of currently visible players.</param>
    /// <param name="isInAllegiance">Whether the bot is in an allegiance (self projection).</param>
    /// <param name="isOwnMonarch">Whether the bot is its own monarch (self projection).</param>
    internal static string? Describe(
        IReadOnlyCollection<string> teammateNames,
        string? selfName,
        bool inFellowship,
        bool amLeader,
        string? fellowshipLeaderName,
        IEnumerable<string?> currentMemberNames,
        IEnumerable<string?> visiblePlayerNames,
        bool isInAllegiance,
        bool isOwnMonarch)
    {
        if (teammateNames.Count == 0)
            return null;

        // Intended team size = distinct configured teammates plus self (the operator may
        // or may not have listed self in AC_BOTS_TEAMMATE_NAMES; folding it in makes the
        // "members=N/roster" progress read correct either way).
        var rosterSet = new HashSet<string>(teammateNames, System.StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(selfName))
            rosterSet.Add(selfName!);
        var roster = rosterSet.Count;

        if (!inFellowship)
        {
            var visibleTeammates = CountVisibleTeammates(teammateNames, visiblePlayerNames);
            return $"role=ungrouped fellowship=no members=0/{roster} visible-teammates={visibleTeammates}";
        }

        // Count only CONFIGURED-team members (incl. self) actually in the fellowship, so a
        // non-teammate grouping in cannot inflate the progress read (e.g. a stranger must
        // not turn "2/2" into a misleading "3/2"). This is the operator-team's progress,
        // not the raw whole-fellowship size.
        var present = currentMemberNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && rosterSet.Contains(n!))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .Count();

        if (amLeader)
        {
            var next = TeammateCoordination.FirstUnrecruitedTeammate(
                teammateNames, visiblePlayerNames, currentMemberNames);
            return $"role=leader fellowship=yes members={present}/{roster} " +
                   $"next-recruit={next ?? "none"}";
        }

        var vassal = isInAllegiance && !isOwnMonarch;
        return $"role=follower fellowship=yes members={present}/{roster} " +
               $"leader={fellowshipLeaderName ?? "?"} vassal={(vassal ? "yes" : "no")}";
    }

    private static int CountVisibleTeammates(
        IReadOnlyCollection<string> teammateNames, IEnumerable<string?> visiblePlayerNames)
    {
        var set = new HashSet<string>(teammateNames, System.StringComparer.OrdinalIgnoreCase);
        return visiblePlayerNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && set.Contains(n!))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .Count();
    }
}
