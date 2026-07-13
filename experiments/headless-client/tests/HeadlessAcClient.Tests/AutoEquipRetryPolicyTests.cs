// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for AutoEquipRetryPolicy — the login auto-equip retry-after-cooldown
// timing (re-send a still-unwielded raced equip once the item-creation ack has
// settled, capped by an attempt count). Pure timer/attempt predicate; no game
// knowledge.

using System;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class AutoEquipRetryPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 4;

    [Fact]
    public void ShouldRetry_CooldownElapsed_UnderCap_True()
    {
        // Sent 10s ago (== cooldown), 1 attempt < 4: release for a retry.
        Assert.True(AutoEquipRetryPolicy.ShouldRetry(
            sentAt: T0, attempts: 1, now: T0 + TimeSpan.FromSeconds(10),
            cooldown: Cooldown, maxAttempts: MaxAttempts));
    }

    [Fact]
    public void ShouldRetry_WithinCooldown_False()
    {
        // Sent 9s ago (< 10s cooldown): still awaiting the ack — do not re-send.
        Assert.False(AutoEquipRetryPolicy.ShouldRetry(
            sentAt: T0, attempts: 1, now: T0 + TimeSpan.FromSeconds(9),
            cooldown: Cooldown, maxAttempts: MaxAttempts));
    }

    [Fact]
    public void ShouldRetry_AttemptCapReached_False()
    {
        // At the cap, never retry again even long after the cooldown.
        Assert.False(AutoEquipRetryPolicy.ShouldRetry(
            sentAt: T0, attempts: MaxAttempts, now: T0 + TimeSpan.FromSeconds(600),
            cooldown: Cooldown, maxAttempts: MaxAttempts));
    }

    [Fact]
    public void ShouldRetry_LastAttemptUnderCap_CooldownElapsed_True()
    {
        // attempts == max-1 is still under the cap: one final retry is allowed.
        Assert.True(AutoEquipRetryPolicy.ShouldRetry(
            sentAt: T0, attempts: MaxAttempts - 1, now: T0 + TimeSpan.FromSeconds(30),
            cooldown: Cooldown, maxAttempts: MaxAttempts));
    }

    [Fact]
    public void ShouldRetry_MaxAttemptsOne_NeverRetries()
    {
        // MaxAttempts=1 disables the retry (one-shot): the first (and only) send
        // already counts as attempt 1, so a further release never fires.
        Assert.False(AutoEquipRetryPolicy.ShouldRetry(
            sentAt: T0, attempts: 1, now: T0 + TimeSpan.FromSeconds(600),
            cooldown: Cooldown, maxAttempts: 1));
    }

    [Theory]
    [InlineData(null, 10.0)]     // unset -> default
    [InlineData("", 10.0)]       // blank -> default
    [InlineData("bad", 10.0)]    // unparseable -> default
    [InlineData("0", 10.0)]      // below min (1) -> default (rejected, < Min)
    [InlineData("0.5", 10.0)]    // below min -> default
    [InlineData("1", 1.0)]       // min
    [InlineData("15", 15.0)]
    [InlineData("500", 120.0)]   // above max -> clamped
    public void ResolveCooldownSeconds_ClampsAndDefaults(string? env, double expected)
        => Assert.Equal(expected, AutoEquipRetryPolicy.ResolveCooldownSeconds(env));

    [Theory]
    [InlineData(null, 4)]    // unset -> default
    [InlineData("", 4)]      // blank -> default
    [InlineData("bad", 4)]   // unparseable -> default
    [InlineData("0", 4)]     // below min (1) -> default
    [InlineData("1", 1)]     // min (one-shot)
    [InlineData("4", 4)]
    [InlineData("7", 7)]
    [InlineData("999", 20)]  // above max -> clamped
    public void ResolveMaxAttempts_ClampsAndDefaults(string? env, int expected)
        => Assert.Equal(expected, AutoEquipRetryPolicy.ResolveMaxAttempts(env));
}
