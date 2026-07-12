// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for LlmGoalPolicy.CanContinueExploreThroughStuck: the pure predicate that
// decides whether a stuck-timer-blocked, budget-exempt untargeted-Explore free
// re-drive may CONTINUE without a forced LLM call. It returns true only when the
// feature is enabled (maxContinue > 0), the consecutive-override budget is not
// spent, AND the bot advanced at least minAdvanceUnits (world XY) since the last
// re-drive (covering ground, not wall-stuck). A null prior position never qualifies.
// reduce-llm-call-volume.

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ExploreStuckContinueTests
{
    private const float Min = 3.0f;

    [Fact]
    public void Disabled_WhenMaxIsZero()
    {
        // Advancing well past the threshold, but the feature is OFF (cap 0).
        Assert.False(LlmGoalPolicy.CanContinueExploreThroughStuck((0f, 0f), 100f, 0f, 0, 0, Min));
    }

    [Fact]
    public void BudgetSpent_WhenCountAtCap()
    {
        Assert.False(LlmGoalPolicy.CanContinueExploreThroughStuck((0f, 0f), 100f, 0f, 6, 6, Min));
    }

    [Fact]
    public void BudgetSpent_WhenCountAboveCap()
    {
        Assert.False(LlmGoalPolicy.CanContinueExploreThroughStuck((0f, 0f), 100f, 0f, 9, 6, Min));
    }

    [Fact]
    public void NullPriorPosition_DoesNotQualify()
    {
        Assert.False(LlmGoalPolicy.CanContinueExploreThroughStuck(null, 100f, 0f, 0, 6, Min));
    }

    [Fact]
    public void Advancing_UnderBudget_Qualifies()
    {
        // Moved 3u east (== threshold) with budget remaining.
        Assert.True(LlmGoalPolicy.CanContinueExploreThroughStuck((0f, 0f), 3f, 0f, 0, 6, Min));
    }

    [Fact]
    public void AdvanceExactlyAtThreshold_Qualifies()
    {
        // Diagonal move of magnitude ~3.11u (> 3).
        Assert.True(LlmGoalPolicy.CanContinueExploreThroughStuck((10f, 10f), 12.2f, 12.2f, 2, 6, Min));
    }

    [Fact]
    public void Frozen_Position_DoesNotQualify()
    {
        // Wall-stuck: no movement -> re-consult (the safe default).
        Assert.False(LlmGoalPolicy.CanContinueExploreThroughStuck((50f, 50f), 50f, 50f, 0, 6, Min));
    }

    [Fact]
    public void TinyDrift_BelowThreshold_DoesNotQualify()
    {
        // Moved ~2u (< 3): not enough to count as covering ground.
        Assert.False(LlmGoalPolicy.CanContinueExploreThroughStuck((0f, 0f), 2f, 0f, 0, 6, Min));
    }

    [Fact]
    public void LastAllowedOverride_Qualifies()
    {
        // continueCount = cap - 1 -> still one override left.
        Assert.True(LlmGoalPolicy.CanContinueExploreThroughStuck((0f, 0f), 10f, 0f, 5, 6, Min));
    }

    [Fact]
    public void NegativeDeltaBeyondThreshold_Qualifies()
    {
        // Direction does not matter — only the magnitude of the advance.
        Assert.True(LlmGoalPolicy.CanContinueExploreThroughStuck((100f, 100f), 96f, 100f, 0, 6, Min));
    }

    // ---- ResolveMaxExploreStuckContinue (config parse/clamp; default OFF) ----

    [Theory]
    [InlineData(null, 0)]      // unset -> OFF
    [InlineData("", 0)]        // blank -> OFF
    [InlineData("   ", 0)]     // whitespace -> OFF
    [InlineData("0", 0)]       // explicit 0 -> OFF
    [InlineData("-4", 0)]      // negative -> OFF
    [InlineData("abc", 0)]     // unparseable -> OFF
    [InlineData("1", 1)]       // min enabled
    [InlineData("6", 6)]       // typical
    [InlineData("30", 30)]     // at cap
    [InlineData("31", 30)]     // above cap -> clamped
    [InlineData("999", 30)]    // far above cap -> clamped
    public void ResolveMaxExploreStuckContinue_ParsesAndClamps(string? env, int expected) =>
        Assert.Equal(expected, LlmGoalPolicy.ResolveMaxExploreStuckContinue(env));
}
