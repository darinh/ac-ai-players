// SPDX-License-Identifier: AGPL-3.0-or-later

namespace HeadlessAcClient.Tests;

using System;
using HeadlessAcClient.Strategy;
using Xunit;

public class InventoryActionRetryPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 4;

    [Fact]
    public void ShouldRetry_CooldownElapsed_UnderCap()
        => Assert.True(InventoryActionRetryPolicy.ShouldRetry(
            T0, 1, 0, T0 + Cooldown, Cooldown, MaxAttempts));

    [Fact]
    public void ShouldRetry_WithinCooldown_IsFalse()
        => Assert.False(InventoryActionRetryPolicy.ShouldRetry(
            T0, 1, 0, T0 + TimeSpan.FromSeconds(9), Cooldown, MaxAttempts));

    [Fact]
    public void ShouldRetry_SecondExplicitRejection_IsFalse()
        => Assert.False(InventoryActionRetryPolicy.ShouldRetry(
            T0, 2, InventoryActionRetryPolicy.ConclusiveExplicitRejectionCount,
            T0 + TimeSpan.FromMinutes(1), Cooldown, MaxAttempts));

    [Fact]
    public void RecordExplicitRejection_FirstNoneAllowsOneRetry()
    {
        var count = InventoryActionRetryPolicy.RecordExplicitRejection(0, 0);

        Assert.Equal(1, count);
        Assert.True(InventoryActionRetryPolicy.ShouldRetry(
            T0, 1, count, T0 + Cooldown, Cooldown, MaxAttempts));
    }

    [Fact]
    public void RecordExplicitRejection_SecondNoneIsConclusive()
    {
        var first = InventoryActionRetryPolicy.RecordExplicitRejection(0, 0);
        var second = InventoryActionRetryPolicy.RecordExplicitRejection(first, 0);

        Assert.Equal(InventoryActionRetryPolicy.ConclusiveExplicitRejectionCount, second);
    }

    [Fact]
    public void RecordExplicitRejection_SpecificErrorIsConclusive()
        => Assert.Equal(
            InventoryActionRetryPolicy.ConclusiveExplicitRejectionCount,
            InventoryActionRetryPolicy.RecordExplicitRejection(0, 0x420));

    [Fact]
    public void TimedOut_AtCapAfterCooldown()
        => Assert.True(InventoryActionRetryPolicy.TimedOut(
            T0, MaxAttempts, 0, T0 + Cooldown, Cooldown, MaxAttempts));

    [Fact]
    public void TimedOut_BeforeCooldown_IsFalse()
        => Assert.False(InventoryActionRetryPolicy.TimedOut(
            T0, MaxAttempts, 0, T0 + TimeSpan.FromSeconds(9), Cooldown, MaxAttempts));

    [Theory]
    [InlineData(null, 10.0)]
    [InlineData("", 10.0)]
    [InlineData("bad", 10.0)]
    [InlineData("0", 10.0)]
    [InlineData("1", 1.0)]
    [InlineData("15", 15.0)]
    [InlineData("500", 120.0)]
    public void ResolveCooldownSeconds_ClampsAndDefaults(string? value, double expected)
        => Assert.Equal(expected, InventoryActionRetryPolicy.ResolveCooldownSeconds(value));

    [Theory]
    [InlineData(null, 4)]
    [InlineData("", 4)]
    [InlineData("bad", 4)]
    [InlineData("0", 4)]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("999", 20)]
    public void ResolveMaxAttempts_ClampsAndDefaults(string? value, int expected)
        => Assert.Equal(expected, InventoryActionRetryPolicy.ResolveMaxAttempts(value));
}
