// SPDX-License-Identifier: AGPL-3.0-or-later
// TeammateCoordination — pure helpers for operator-configured bot-team grouping.
// Parses the AC_BOTS_TEAMMATE_NAMES operator list and, when a configured teammate
// is visible, elects a DETERMINISTIC leader so two symmetric bots do not both try
// to lead their own one-member fellowship (which makes mutual recruitment
// impossible). The election is a total, symmetric ordering of names, so both bots
// independently agree on who leads. No game knowledge and no autonomous action:
// this only shapes which prompt directive renders; the LLM still emits the goal.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HeadlessAcClient.Strategy;

/// <summary>The bot's role in forming a fellowship with a configured teammate.</summary>
internal enum TeammateRole
{
    /// <summary>No configured teammate is currently visible (or config/own name blank).</summary>
    None,
    /// <summary>This bot leads: it creates the fellowship and recruits the counterpart.</summary>
    Leader,
    /// <summary>This bot follows: it waits for and accepts the leader's invite.</summary>
    Follower,
}

/// <summary>
/// The election outcome. <see cref="CounterpartName"/> is the teammate to recruit
/// (Leader) or the leader to wait for/stay near (Follower); null when
/// <see cref="Role"/> is None.
/// </summary>
internal readonly record struct TeammateRoleDecision(TeammateRole Role, string? CounterpartName);

internal static class TeammateCoordination
{
    /// <summary>
    /// Parse AC_BOTS_TEAMMATE_NAMES: comma/semicolon-separated names, trimmed and
    /// de-duplicated (ordinal-ignore-case). Unset/blank/whitespace → empty set.
    /// </summary>
    internal static IReadOnlyCollection<string> ParseNames(string? envValue)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(envValue))
            foreach (var part in envValue.Split(
                         new[] { ',', ';' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(part);
        return set;
    }

    /// <summary>
    /// Elect this bot's role among itself and any visible configured teammates.
    /// Returns None when no configured teammate is visible, or when the own name or
    /// teammate set is empty. Otherwise the alphabetically-first name
    /// (ordinal-ignore-case) among {own name} ∪ {visible teammate names} is the
    /// leader: if that is this bot → Leader (counterpart = the first matched teammate
    /// to recruit); else → Follower (counterpart = the elected leader to wait for).
    /// The ordering is total and symmetric, so both bots agree on exactly one leader.
    /// A visible player whose name equals the bot's own name is ignored (a bot never
    /// teams with a same-named entity).
    /// </summary>
    internal static TeammateRoleDecision Decide(
        string? ownName,
        IReadOnlyCollection<string> teammateNames,
        IEnumerable<string?> visiblePlayerNames)
    {
        if (string.IsNullOrWhiteSpace(ownName) || teammateNames.Count == 0)
            return new TeammateRoleDecision(TeammateRole.None, null);

        // Normalize to a case-insensitive lookup so matching does not depend on the
        // comparer of the passed collection (production passes a ParseNames set, but a
        // caller could pass a plain list). The set is only a handful of names.
        var teammateSet = new HashSet<string>(teammateNames, StringComparer.OrdinalIgnoreCase);

        var matched = visiblePlayerNames
            .Where(n => !string.IsNullOrWhiteSpace(n)
                        && teammateSet.Contains(n!)
                        && !string.Equals(n, ownName, StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, NameOrderComparer)
            .ToList();
        if (matched.Count == 0)
            return new TeammateRoleDecision(TeammateRole.None, null);

        // The elected leader is the first name (by the total NameOrder below) across
        // this bot and all visible matched teammates. `matched` is already
        // case-insensitively distinct from ownName (a case-variant of own is excluded
        // as self above) and internally deduped, so comparing ownName against only the
        // first matched teammate is equivalent to taking the global minimum.
        var firstTeammate = matched[0];
        var ownLeads = CompareNames(ownName, firstTeammate) < 0;
        return ownLeads
            ? new TeammateRoleDecision(TeammateRole.Leader, firstTeammate)
            : new TeammateRoleDecision(TeammateRole.Follower, firstTeammate);
    }

    // Total ordering over names: case-insensitive first (so casing does not decide the
    // team leader in normal use), then ordinal as a deterministic tie-breaker so two
    // names that differ ONLY by case still elect exactly one leader instead of tying
    // (both bots compute the same order, so they never both pick Leader or Follower).
    internal static int CompareNames(string a, string b)
    {
        var c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.Compare(a, b, StringComparison.Ordinal);
    }

    private static readonly IComparer<string> NameOrderComparer =
        Comparer<string>.Create(CompareNames);

    /// <summary>
    /// The first visible configured teammate (by the total <see cref="CompareNames"/>
    /// order) that is NOT already a member of <paramref name="currentMemberNames"/>, or
    /// null when none remain. Used by the fellowship leader to recruit teammates one at
    /// a time until every co-present configured teammate has joined — so a 3+ bot team
    /// does not stall after the first recruit. Matching is case-insensitive.
    /// </summary>
    internal static string? FirstUnrecruitedTeammate(
        IReadOnlyCollection<string> teammateNames,
        IEnumerable<string?> visiblePlayerNames,
        IEnumerable<string?> currentMemberNames)
    {
        if (teammateNames.Count == 0)
            return null;
        var teammateSet = new HashSet<string>(teammateNames, StringComparer.OrdinalIgnoreCase);
        var members = new HashSet<string>(
            currentMemberNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!),
            StringComparer.OrdinalIgnoreCase);
        return visiblePlayerNames
            .Where(n => !string.IsNullOrWhiteSpace(n)
                        && teammateSet.Contains(n!)
                        && !members.Contains(n!))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, NameOrderComparer)
            .FirstOrDefault();
    }
}
