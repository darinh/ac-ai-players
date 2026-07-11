// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the TeammateSighted co-location wake: a per-connection salience nudge
// the Motor fires ONCE on the not-visible -> visible edge of an operator-configured
// teammate (AC_BOTS_TEAMMATE_NAMES), debounced per teammate by a re-fire cooldown,
// so the LLM re-consults the moment a teammate is perceivable instead of only at the
// next scheduled decision. A structural analogue of the SelfProgressChanged /
// InboundDamageTaken wakes: it surfaces RAW facts (which teammate came into view),
// assigns no urgency, selects no target, and decides no action.

using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class TeammateSightingWakeTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private static HashSet<string> Edge(params string[] seed) =>
        new HashSet<string>(seed, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, DateTimeOffset> Cool() =>
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyCollection<string> Team(params string[] names) => names;

    private static bool Fire(
        IReadOnlyCollection<string> team, string? own, string?[] visible,
        DateTimeOffset now, Dictionary<string, DateTimeOffset> cool, HashSet<string> edge,
        out StreamEvent ev, out string log) =>
        TeammateSightingWake.TryBuildTeammateSightingEvent(
            team, own, visible, now, Cooldown, cool, edge, out ev, out log);

    private static readonly DateTimeOffset T0 = new(2026, 7, 10, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoTeammatesConfigured_ReturnsFalse_AndClearsState()
    {
        var edge = Edge("Stale");
        var cool = Cool();
        cool["Stale"] = T0.AddSeconds(30);
        var built = Fire(Team(), "Mba", new string?[] { "SomePlayer" }, T0, cool, edge, out _, out _);

        Assert.False(built);
        Assert.Empty(edge);
        Assert.Empty(cool);
    }

    [Fact]
    public void TeammateAppearsFirstTime_EmitsWakeStampsEdgeAndCooldown()
    {
        var edge = Edge();
        var cool = Cool();
        var built = Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0, cool, edge, out var ev, out var log);

        Assert.True(built);
        Assert.Equal(EventKind.TeammateSighted, ev.Kind);
        Assert.Equal("Mbb", ev.Name);
        Assert.Contains("Mbb", ev.Text);
        Assert.Contains("Mbb", log);
        Assert.Contains("Mbb", edge);
        Assert.True(cool.ContainsKey("Mbb"));
        Assert.Equal(T0 + Cooldown, cool["Mbb"]);
    }

    [Fact]
    public void EmittedEventIsSalientAndNotPicker()
    {
        // The wake only works if it flows through the non-picker salient path.
        Assert.True(LlmGoalPolicy.IsSalientKind(EventKind.TeammateSighted));
        Assert.False(LlmGoalPolicy.IsPickerKind(EventKind.TeammateSighted));
    }

    [Fact]
    public void ContinuousPresence_DoesNotReFire_EvenPastCooldown()
    {
        var edge = Edge();
        var cool = Cool();
        Assert.True(Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0, cool, edge, out _, out _));
        // Still visible the next tick — no re-fire (edge, not level).
        Assert.False(Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0.AddSeconds(1), cool, edge, out _, out _));
        // Still visible well past the cooldown — the edge gate still suppresses, so a
        // continuously co-located teammate never produces periodic wakes.
        Assert.False(Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0.AddSeconds(120), cool, edge, out _, out _));
    }

    [Fact]
    public void LeaveThenReturnWithinCooldown_IsDebounced()
    {
        var edge = Edge();
        var cool = Cool();
        Assert.True(Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0, cool, edge, out _, out _));
        // Left view — no event; edge cleared to current (empty) visibility.
        Assert.False(Fire(Team("Mbb"), "Mba", Array.Empty<string?>(), T0.AddSeconds(10), cool, edge, out _, out _));
        Assert.Empty(edge);
        // Returned within the cooldown window — a fresh edge, but debounced.
        Assert.False(Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0.AddSeconds(20), cool, edge, out _, out _));
    }

    [Fact]
    public void LeaveThenReturnAfterCooldown_ReFires()
    {
        var edge = Edge();
        var cool = Cool();
        Assert.True(Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0, cool, edge, out _, out _));
        Assert.False(Fire(Team("Mbb"), "Mba", Array.Empty<string?>(), T0.AddSeconds(10), cool, edge, out _, out _));
        // Returned after the cooldown expired — a genuine new co-location window.
        Assert.True(Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0.AddSeconds(90), cool, edge, out _, out _));
        Assert.Equal(T0.AddSeconds(90) + Cooldown, cool["Mbb"]);
    }

    [Fact]
    public void ExpiredCooldownEntries_ArePruned()
    {
        var edge = Edge();
        var cool = Cool();
        cool["Ghost"] = T0.AddSeconds(-1); // already expired
        // No teammate visible, but a configured team exists so the prune runs.
        Assert.False(Fire(Team("Mbb"), "Mba", Array.Empty<string?>(), T0, cool, edge, out _, out _));
        Assert.False(cool.ContainsKey("Ghost"));
    }

    [Fact]
    public void OwnNameAmongVisiblePlayers_IsNeverATeammate()
    {
        var edge = Edge();
        var cool = Cool();
        var built = Fire(Team("Mba"), "Mba", new string?[] { "Mba" }, T0, cool, edge, out _, out _);

        Assert.False(built);
        Assert.Empty(edge);
    }

    [Fact]
    public void NonTeammatePlayerInView_IsIgnored()
    {
        var edge = Edge();
        var cool = Cool();
        var built = Fire(Team("Mbb"), "Mba", new string?[] { "RandomStranger" }, T0, cool, edge, out _, out _);

        Assert.False(built);
        Assert.Empty(edge);
    }

    [Fact]
    public void MultipleTeammatesAppearTogether_SingleEvent_ListsAllOrdered()
    {
        var edge = Edge();
        var cool = Cool();
        var built = Fire(Team("Mbb", "Mbc"), "Mba", new string?[] { "Mbc", "Mbb" }, T0, cool, edge, out var ev, out _);

        Assert.True(built);
        Assert.Contains("Mbb, Mbc", ev.Text); // deterministic, case-insensitive order
        Assert.Contains("Mbb", edge);
        Assert.Contains("Mbc", edge);
    }

    [Fact]
    public void OnlyOneOfTwoIsNew_WakesForTheNewOneOnly()
    {
        var edge = Edge();
        var cool = Cool();
        // Mbb already visible from a prior tick.
        Assert.True(Fire(Team("Mbb", "Mbc"), "Mba", new string?[] { "Mbb" }, T0, cool, edge, out _, out _));
        // Now Mbc joins; Mbb stays. Only Mbc is a new edge.
        var built = Fire(Team("Mbb", "Mbc"), "Mba", new string?[] { "Mbb", "Mbc" }, T0.AddSeconds(5), cool, edge, out var ev, out _);

        Assert.True(built);
        Assert.Equal("Mbc", ev.Name);
        Assert.Contains("Mbc", ev.Text);
        Assert.DoesNotContain("Mbb", ev.Text);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var edge = Edge();
        var cool = Cool();
        var built = Fire(Team("Mbb"), "Mba", new string?[] { "mbb" }, T0, cool, edge, out var ev, out _);

        Assert.True(built);
        Assert.Contains("mbb", ev.Text);
    }

    [Fact]
    public void EventTextCarriesNoUrgencyOrDirective()
    {
        // HK shape guard: the wake text is raw perception only — no imperative verb,
        // no urgency word, no game rule, no fellowship/allegiance framing.
        var edge = Edge();
        var cool = Cool();
        Fire(Team("Mbb"), "Mba", new string?[] { "Mbb" }, T0, cool, edge, out var ev, out _);

        var text = ev.Text!.ToLowerInvariant();
        Assert.DoesNotContain("fellowship", text);
        Assert.DoesNotContain("recruit", text);
        Assert.DoesNotContain("swear", text);
        Assert.DoesNotContain("allegiance", text);
        Assert.DoesNotContain("group", text);
        Assert.DoesNotContain("must", text);
        Assert.DoesNotContain("should", text);
    }

    [Fact]
    public void BlankVisibleNames_AreIgnored()
    {
        var edge = Edge();
        var cool = Cool();
        var built = Fire(Team("Mbb"), "Mba", new string?[] { null, "", "   " }, T0, cool, edge, out _, out _);

        Assert.False(built);
        Assert.Empty(edge);
    }
}
