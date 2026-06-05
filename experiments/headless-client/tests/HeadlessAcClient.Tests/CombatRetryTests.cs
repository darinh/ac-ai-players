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

    private const double StickSettle = 2.0;

    [Fact]
    public void ServerStickActive_SuppressesCancelDrivenFastRetry()
        // The server is mid move-into-range (stick observed 0.5s ago); a
        // cancel-driven re-send would cancel that move-to, so suppress it.
        => Assert.False(CombatRetry.ShouldReattack(
            0.5, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: 0.5, StickSettle));

    [Fact]
    public void ServerStickActive_SuppressesPeriodicReattack()
        // Even past the 5s safety net, an active stick (still chasing)
        // must not trigger a re-send that would cancel the server move-to.
        => Assert.False(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: 1.0, StickSettle));

    [Fact]
    public void ServerStickStale_AllowsReattackAgain()
        // The stick observation has aged past the settle window — the
        // server has stopped sticking us, so the normal re-send resumes.
        => Assert.True(CombatRetry.ShouldReattack(
            6.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: 2.5, StickSettle));

    [Fact]
    public void ServerStickStale_AllowsCancelFastRetry()
        => Assert.True(CombatRetry.ShouldReattack(
            0.5, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: 3.0, StickSettle));

    [Fact]
    public void NoServerStickObserved_BehavesAsBefore()
    {
        // null stick == no suppression: identical to the legacy 4-arg form.
        Assert.True(CombatRetry.ShouldReattack(
            5.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle));
        Assert.True(CombatRetry.ShouldReattack(
            0.4, cancelRetryRequested: true, Normal, FastMin,
            secondsSinceServerStick: null, StickSettle));
    }

    [Fact]
    public void NegativeStickElapsed_TreatedAsNotActive()
        // Clock skew on the stick timestamp must not be read as "active"
        // (which would suppress forever); fall through to normal logic.
        => Assert.True(CombatRetry.ShouldReattack(
            5.0, cancelRetryRequested: false, Normal, FastMin,
            secondsSinceServerStick: -1.0, StickSettle));
}
