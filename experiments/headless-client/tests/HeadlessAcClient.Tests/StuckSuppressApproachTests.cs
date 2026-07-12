// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for LlmGoalPolicy.ShouldContinueApproachOnStuck: the pure predicate that
// suppresses the stuck-timer LLM re-deliberation while an object-pursuit walk is
// demonstrably APPROACHING its target (bot-to-target distance strictly inbound). It
// returns true only for an object-pursuit goal (Attack/Use/Talk/Pickup/Give/Wield/GoTo)
// whose progress trend has >= 3 samples where the latest is the closest observed AND
// strictly closer than the first. Flat/drifting/bounced trends re-deliberate. Names are
// placeholders. reduce-llm-call-volume.

using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class StuckSuppressApproachTests
{
    private static Goal Pursue(GoalKind kind, string name = "Quarry Beast") =>
        new() { Kind = kind, Target = new Selector { Name = name } };

    private static GoalProgressSnapshot Prog(params float[] distances) =>
        new("Quarry Beast", distances, SpanSeconds: (distances.Length - 1) * 1.5);

    [Fact]
    public void NullGoal_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(null, Prog(10, 7, 4)));

    [Fact]
    public void NullProgress_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Attack), null));

    [Fact]
    public void NonObjectPursuit_Explore_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(
            new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = "anywhere" } },
            Prog(10, 7, 4)));

    [Fact]
    public void NonObjectPursuit_Wait_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(
            new Goal { Kind = GoalKind.Wait, Target = new Selector() }, Prog(10, 7, 4)));

    [Fact]
    public void FewerThanThreeSamples_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Attack), Prog(10, 4)));

    [Fact]
    public void StrictlyInbound_True() =>
        Assert.True(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Attack), Prog(10, 7, 4)));

    [Fact]
    public void InboundToArrival_True() =>
        Assert.True(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Use), Prog(10, 5, 2)));

    [Fact]
    public void FlatTrend_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Talk), Prog(5, 5, 5)));

    [Fact]
    public void DriftingAway_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Pickup), Prog(4, 7, 10)));

    [Fact]
    public void BouncedBack_LatestNotClosest_False() =>
        // Net inbound vs first (5 < 10) but the latest (5) is NOT the closest observed (3):
        // the bot got close then drifted back, so it should re-deliberate.
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.GoTo), Prog(10, 3, 5)));

    [Fact]
    public void AllObjectPursuitKinds_Inbound_True()
    {
        foreach (var k in new[] { GoalKind.Attack, GoalKind.Use, GoalKind.Talk,
                                  GoalKind.Pickup, GoalKind.Give, GoalKind.Wield, GoalKind.GoTo })
            Assert.True(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(k), Prog(9, 6, 3)),
                $"{k} approaching should suppress");
    }

    [Fact]
    public void LongerInboundTrend_True() =>
        Assert.True(LlmGoalPolicy.ShouldContinueApproachOnStuck(
            Pursue(GoalKind.Attack), Prog(20, 17, 14, 11, 8, 5, 2)));

    [Fact]
    public void EqualFirstAndLast_NotStrictlyInbound_False() =>
        // latest == first (no net progress) must not suppress.
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Attack), Prog(6, 4, 6)));

    [Fact]
    public void PlateauAtBestDistance_False() =>
        // Approached (10 -> 4) then STALLED at the best distance (4 -> 4): progress has
        // stopped in the most recent step, so it must re-deliberate rather than suppress.
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(Pursue(GoalKind.Attack), Prog(10, 4, 4)));

    [Fact]
    public void LongApproachThenPlateau_False() =>
        Assert.False(LlmGoalPolicy.ShouldContinueApproachOnStuck(
            Pursue(GoalKind.Use), Prog(13, 10, 8, 6, 4, 4, 4, 4)));
}
