// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck: the pure predicate
// gating the stuck-timer suppression. It returns true only for an Attack goal
// whose CurrentFight has landed swings or dealt damage and whose target identity
// (guid when set, else name) matches the goal's selector. Names are placeholders.

using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class StuckSuppressActiveMeleeTests
{
    private static Goal Attack(string? name, uint? guid = null) =>
        new() { Kind = GoalKind.Attack, Target = new Selector { Name = name, Guid = guid } };

    private static CombatFightStatus Fight(string? targetName, int landed, uint damage, uint guid = 0x1000u) =>
        new(TargetGuid: guid, TargetName: targetName, SwingsLanded: landed,
            SwingsEvaded: 0, DamageDealt: damage);

    [Fact]
    public void AttackLock_ActiveFight_LandedSwings_SameTarget_True()
    {
        Assert.True(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast"), Fight("Quarry Beast", landed: 2, damage: 0)));
    }

    [Fact]
    public void AttackLock_ActiveFight_DealtDamage_SameTarget_True()
    {
        Assert.True(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast"), Fight("Quarry Beast", landed: 0, damage: 7)));
    }

    [Fact]
    public void AttackLock_FightWithNoProgress_False()
    {
        // No swings landed and no damage dealt this fight -> not yet productive.
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast"), Fight("Quarry Beast", landed: 0, damage: 0)));
    }

    [Fact]
    public void AttackLock_ActiveFight_DifferentTarget_False()
    {
        // The active fight is on a different target than the goal names.
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast"), Fight("Roaming Aggressor", landed: 3, damage: 9)));
    }

    [Fact]
    public void NonAttackGoal_False()
    {
        var explore = new Goal { Kind = GoalKind.Explore, Target = new Selector { Name = "anywhere" } };
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            explore, Fight("anywhere", landed: 2, damage: 5)));
    }

    [Fact]
    public void NullFight_False()
    {
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(Attack("Quarry Beast"), null));
    }

    [Fact]
    public void NullGoal_False()
    {
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(null, Fight("Quarry Beast", 2, 5)));
    }

    [Fact]
    public void NullFightTargetName_False()
    {
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast"), Fight(null, landed: 2, damage: 5)));
    }

    [Fact]
    public void NullGoalTargetName_False()
    {
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack(null), Fight("Quarry Beast", landed: 2, damage: 5)));
    }

    [Fact]
    public void TargetNameMatch_IsCaseInsensitive_True()
    {
        Assert.True(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("quarry BEAST"), Fight("Quarry Beast", landed: 1, damage: 0)));
    }

    [Fact]
    public void GuidPinned_SameGuid_True()
    {
        Assert.True(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast", guid: 0x2000u),
            Fight("Quarry Beast", landed: 2, damage: 0, guid: 0x2000u)));
    }

    [Fact]
    public void GuidPinned_DifferentGuid_SameName_False()
    {
        // Guid-pinned goal, but the active fight is a same-NAMED but different
        // individual (different guid) -> do not suppress the re-deliberation.
        Assert.False(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast", guid: 0x2000u),
            Fight("Quarry Beast", landed: 2, damage: 4, guid: 0x9999u)));
    }

    [Fact]
    public void GuidPinned_SameGuid_NameMismatch_True()
    {
        // When guid-pinned, the guid match wins even if the names differ (e.g.
        // a late-resolved display name) -> still the LLM's locked individual.
        Assert.True(LlmGoalPolicy.ShouldContinueActiveMeleeOnStuck(
            Attack("Quarry Beast", guid: 0x2000u),
            Fight("Quarry Beast Hatchling", landed: 1, damage: 0, guid: 0x2000u)));
    }
}
