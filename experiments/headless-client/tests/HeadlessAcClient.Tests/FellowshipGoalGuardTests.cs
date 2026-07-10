// SPDX-License-Identifier: AGPL-3.0-or-later
// fellowship-create-guard: tests for FellowshipGoalGuard.IsRedundantFellowshipCreate,
// the mechanical guard that stops the Motor from re-sending FellowshipCreate while the
// bot is already in a fellowship (which the server handles by disbanding the current
// team — observed live collapsing a 4-member fellowship back to 1).

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class FellowshipGoalGuardTests
{
    [Fact]
    public void FellowshipCreate_WhileInFellowship_IsRejected()
        => Assert.True(FellowshipGoalGuard.IsRedundantFellowshipCreate(
            GoalKind.FellowshipCreate, inFellowship: true));

    [Fact]
    public void FellowshipCreate_WhileNotInFellowship_IsAllowed()
        => Assert.False(FellowshipGoalGuard.IsRedundantFellowshipCreate(
            GoalKind.FellowshipCreate, inFellowship: false));

    [Fact]
    public void FellowshipRecruit_WhileInFellowship_IsNotAffected()
        => Assert.False(FellowshipGoalGuard.IsRedundantFellowshipCreate(
            GoalKind.FellowshipRecruit, inFellowship: true));

    [Fact]
    public void FellowshipQuit_WhileInFellowship_IsNotAffected()
        => Assert.False(FellowshipGoalGuard.IsRedundantFellowshipCreate(
            GoalKind.FellowshipQuit, inFellowship: true));

    [Fact]
    public void NonFellowshipGoal_WhileInFellowship_IsNotAffected()
        => Assert.False(FellowshipGoalGuard.IsRedundantFellowshipCreate(
            GoalKind.Attack, inFellowship: true));
}
