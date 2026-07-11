// SPDX-License-Identifier: AGPL-3.0-or-later
// TeammateSightingWake — pure edge-detector that appends one salience-wake event
// the moment an operator-configured teammate (AC_BOTS_TEAMMATE_NAMES) comes into
// view, so the bot re-consults the LLM immediately instead of only at the next
// scheduled decision. This is WHEN-to-consult-the-LLM bookkeeping only (the
// structural analogue of the SelfProgressChanged / InboundDamageTaken wakes): it
// assigns no urgency, selects no target, and decides no action. It never moves the
// bot and never interacts with anything — it only appends an event that lets the
// LLM decide. Scoped to configured teammates (an empty config never triggers it, so
// a single bot pays nothing). No game knowledge.

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal static class TeammateSightingWake
{
    /// <summary>
    /// Default minimum interval between wakes for the SAME teammate. A pure
    /// debounce/eviction window (bookkeeping), not a game constant: it bounds how
    /// often a teammate that repeatedly leaves and re-enters view can re-wake the
    /// LLM. It carries no game-specific meaning.
    /// </summary>
    internal static readonly TimeSpan DefaultReFireCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Decide whether a configured teammate has NEWLY come into view since the last
    /// check and, if so, build one <c>TeammateSighted</c> salience-wake event.
    /// </summary>
    /// <remarks>
    /// Two bookkeeping states, both mutated in place:
    /// <list type="bullet">
    /// <item><paramref name="lastVisibleTeammates"/> holds the configured teammates
    /// visible at the previous check and is reset to the current visible-configured
    /// set on EVERY call. This is the not-visible → visible EDGE: a teammate that
    /// stays continuously visible does not re-fire; one that leaves view and returns
    /// is a fresh edge.</item>
    /// <item><paramref name="cooldownUntilByTeammate"/> maps a teammate to the time
    /// before which it must not re-fire (a per-teammate debounce). Set when a wake
    /// fires; expired entries are pruned each call so the map stays bounded by the
    /// teammates on active cooldown.</item>
    /// </list>
    /// A fresh edge fires only when the teammate is not currently on cooldown, so a
    /// teammate that oscillates across the view boundary re-wakes at most once per
    /// <paramref name="reFireCooldown"/>. The bot's own name is never a teammate.
    /// The method reads no game-state significance of any kind — it is purely a raw
    /// visibility edge plus a time debounce.
    ///
    /// Returns <c>false</c> (emitting nothing) when no teammate is configured, none
    /// newly appeared, or every newly-appeared teammate is still on cooldown. The
    /// dedup set is still updated in those cases so a later genuine edge is detected
    /// correctly.
    /// </remarks>
    /// <param name="teammateNames">Configured teammate names (AC_BOTS_TEAMMATE_NAMES).</param>
    /// <param name="ownName">The bot's own display name (never a teammate of itself).</param>
    /// <param name="visiblePlayerNames">Names of currently visible players.</param>
    /// <param name="nowUtc">The current time (for cooldown arithmetic).</param>
    /// <param name="reFireCooldown">Minimum interval before the same teammate can re-fire.</param>
    /// <param name="cooldownUntilByTeammate">Debounce state (mutated).</param>
    /// <param name="lastVisibleTeammates">Edge state (mutated): teammates visible last check.</param>
    /// <param name="ev">The built wake event when the method returns <c>true</c>.</param>
    /// <param name="logLine">A raw diagnostic line when the method returns <c>true</c>.</param>
    internal static bool TryBuildTeammateSightingEvent(
        IReadOnlyCollection<string> teammateNames,
        string? ownName,
        IEnumerable<string?> visiblePlayerNames,
        DateTimeOffset nowUtc,
        TimeSpan reFireCooldown,
        Dictionary<string, DateTimeOffset> cooldownUntilByTeammate,
        HashSet<string> lastVisibleTeammates,
        out StreamEvent ev, out string logLine)
    {
        ev = null!;
        logLine = string.Empty;

        if (teammateNames.Count == 0)
        {
            // No operator team → nothing to track; keep both bookkeeping states empty
            // so a later (re)configuration starts from a clean edge.
            lastVisibleTeammates.Clear();
            cooldownUntilByTeammate.Clear();
            return false;
        }

        // Configured teammates currently in view (excluding self), case-insensitively
        // distinct. teammateNames is tiny, so a linear ordinal-ignore-case scan avoids
        // allocating a lookup set on this per-decision path.
        var currentVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string>? newlyAppeared = null;
        foreach (var n in visiblePlayerNames)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (string.Equals(n, ownName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ContainsIgnoreCase(teammateNames, n!)) continue;
            if (!currentVisible.Add(n!)) continue; // already counted this teammate
            // The not-visible -> visible edge: visible now but not at the last check.
            if (!lastVisibleTeammates.Contains(n!))
                (newlyAppeared ??= new List<string>()).Add(n!);
        }

        // Reset the edge state to the current visible-configured set (departures reset,
        // enabling a later re-fire; continuous presence never re-fires).
        lastVisibleTeammates.Clear();
        foreach (var n in currentVisible)
            lastVisibleTeammates.Add(n);

        // Prune expired debounce entries so the map stays bounded.
        if (cooldownUntilByTeammate.Count > 0)
        {
            List<string>? expired = null;
            foreach (var kv in cooldownUntilByTeammate)
                if (kv.Value <= nowUtc)
                    (expired ??= new List<string>()).Add(kv.Key);
            if (expired is not null)
                foreach (var k in expired)
                    cooldownUntilByTeammate.Remove(k);
        }

        if (newlyAppeared is null)
            return false;

        // A fresh edge fires only when the teammate is not on its re-fire cooldown.
        List<string>? toWake = null;
        foreach (var n in newlyAppeared)
            if (!cooldownUntilByTeammate.ContainsKey(n))
                (toWake ??= new List<string>()).Add(n);
        if (toWake is null)
            return false;

        toWake.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var n in toWake)
            cooldownUntilByTeammate[n] = nowUtc + reFireCooldown;

        var joined = string.Join(", ", toWake);
        ev = new StreamEvent
        {
            Sequence = 0,
            Utc = nowUtc,
            Kind = EventKind.TeammateSighted,
            Name = toWake[0],
            Text = $"Configured teammate now in view: {joined}.",
        };
        logLine =
            $"[teammate-sighting] TeammateSighted: {joined} — waking LLM (teammate came into view).";
        return true;
    }

    private static bool ContainsIgnoreCase(IReadOnlyCollection<string> names, string value)
    {
        foreach (var n in names)
            if (string.Equals(n, value, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
