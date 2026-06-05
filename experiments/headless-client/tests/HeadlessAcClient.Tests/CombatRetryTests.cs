// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CombatRetry.ShouldReattack — the pure timing decision for
// the Phase 7f.2 melee loop-keeper re-send.

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatRetryTests
{
    private const double Normal = 5.0;
    private const double FastMin = 0.35;

    [Fact]
    public void NoCancel_BeforeNormalInterval_DoesNotReattack()
        => Assert.False(CombatRetry.ShouldReattack(2.0, cancelRetryRequested: false, Normal, FastMin));

    [Fact]
    public void NoCancel_AtNormalInterval_Reattacks()
        => Assert.True(CombatRetry.ShouldReattack(5.0, cancelRetryRequested: false, Normal, FastMin));

    [Fact]
    public void NoCancel_PastNormalInterval_Reattacks()
        => Assert.True(CombatRetry.ShouldReattack(7.5, cancelRetryRequested: false, Normal, FastMin));

    [Fact]
    public void Cancel_PastFastMin_ReattacksEarly()
        // A cancel arrived and the anti-spam window has passed — fire
        // well before the 5s safety net.
        => Assert.True(CombatRetry.ShouldReattack(0.4, cancelRetryRequested: true, Normal, FastMin));

    [Fact]
    public void Cancel_AtFastMin_Reattacks()
        => Assert.True(CombatRetry.ShouldReattack(0.35, cancelRetryRequested: true, Normal, FastMin));

    [Fact]
    public void Cancel_WithinFastMin_DoesNotSpam()
        // A burst of cancels must not re-send faster than the anti-spam
        // floor.
        => Assert.False(CombatRetry.ShouldReattack(0.1, cancelRetryRequested: true, Normal, FastMin));

    [Fact]
    public void NegativeElapsed_NeverReattacks()
    {
        Assert.False(CombatRetry.ShouldReattack(-1.0, cancelRetryRequested: true, Normal, FastMin));
        Assert.False(CombatRetry.ShouldReattack(-1.0, cancelRetryRequested: false, Normal, FastMin));
    }

    [Fact]
    public void Cancel_StillHonorsNormalIntervalWhenNoFastWindow()
    {
        // Even with fastMin == normal (degenerate config), a cancel does
        // not fire before the interval.
        Assert.False(CombatRetry.ShouldReattack(1.0, cancelRetryRequested: true, Normal, Normal));
        Assert.True(CombatRetry.ShouldReattack(5.0, cancelRetryRequested: true, Normal, Normal));
    }
}
