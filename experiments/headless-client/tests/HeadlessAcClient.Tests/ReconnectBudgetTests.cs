// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for HandshakeDriver.ReconnectBudget — the pure consecutive-reconnect
// budget state machine extracted from the login-resilience loop.
//
// Why this exists: MaxLoginReconnects bounds CONSECUTIVE connect/login failures,
// but the budget was previously the loop iteration counter, so transient
// disconnects spread across a long run (AC_BOTS_OBSERVE_SECONDS) accumulated
// toward the cap and could force the process to exit. The budget now tracks a
// consecutive-failure streak that a healthy IN-WORLD observe window resets: if the
// bot genuinely played (inWorldSeconds, the time committed to the world) for >=
// the threshold before a disconnect, the earlier reconnects are ancient history.
// A short window (flapping) or a pre-world stall reports too few in-world seconds
// and does NOT reset, so a real failure streak still gives up at the cap and a
// dead server cannot livelock. The socket loop itself cannot be unit-tested, so
// the budget transitions are exercised here directly.

using HeadlessAcClient.Protocol;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ReconnectBudgetTests
{
    // Mirror the production wiring: cap 5, 300s healthy window, 5000ms backoff base.
    private static HandshakeDriver.ReconnectBudget New() =>
        new HandshakeDriver.ReconnectBudget(maxConsecutive: 5, healthyWindowSeconds: 300, backoffBaseMs: 5000);

    [Fact]
    public void FreshBudget_CanRetry_ZeroFailures()
    {
        var b = New();
        Assert.True(b.CanRetry);
        Assert.Equal(0, b.ConsecutiveFailures);
    }

    [Fact]
    public void FiveConsecutiveFailures_ThenGivesUp_WithEscalatingBackoff()
    {
        var b = New();
        // Exactly 5 retries allowed, backoff base*1..base*5, then CanRetry is false.
        var expectedBackoffs = new[] { 5000, 10000, 15000, 20000, 25000 };
        for (int i = 0; i < 5; i++)
        {
            Assert.True(b.CanRetry);
            Assert.Equal(expectedBackoffs[i], b.RegisterFailure());
            Assert.Equal(i + 1, b.ConsecutiveFailures);
        }
        // 6th failure: budget exhausted -> give up (the loop propagates / returns).
        Assert.False(b.CanRetry);
    }

    [Fact]
    public void HealthyInWorldWindow_ResetsStreak()
    {
        var b = New();
        b.RegisterFailure();
        b.RegisterFailure();
        b.RegisterFailure();
        b.RegisterFailure();
        Assert.Equal(4, b.ConsecutiveFailures);

        // A long IN-WORLD window resets the streak.
        b.NoteObserveWindow(inWorldSeconds: 400);
        Assert.Equal(0, b.ConsecutiveFailures);
        Assert.True(b.CanRetry);

        // The next disconnect starts a fresh streak at backoff base*1.
        Assert.Equal(5000, b.RegisterFailure());
        Assert.Equal(1, b.ConsecutiveFailures);
    }

    [Fact]
    public void ShortWindow_DoesNotReset_StreakStillGivesUp()
    {
        var b = New();
        // Four failures, then a SHORT (flapping) in-world window: no reset.
        for (int i = 0; i < 4; i++) b.RegisterFailure();
        b.NoteObserveWindow(inWorldSeconds: 100);
        Assert.Equal(4, b.ConsecutiveFailures);
        Assert.True(b.CanRetry);
        b.RegisterFailure();              // 5th
        Assert.False(b.CanRetry);         // give up on the next
    }

    [Fact]
    public void NeverEnteredWorld_DoesNotReset()
    {
        var b = New();
        for (int i = 0; i < 4; i++) b.RegisterFailure();
        // A window that NEVER entered the world reports inWorldSeconds = 0; it must
        // NOT reset — otherwise a permanently failing pre-world connection would
        // livelock. (A long pre-world stall + brief play likewise reports only the
        // small in-world duration, so it does not reset either.)
        b.NoteObserveWindow(inWorldSeconds: 0);
        Assert.Equal(4, b.ConsecutiveFailures);
        b.NoteObserveWindow(inWorldSeconds: 2);   // 2s of play after a long stall
        Assert.Equal(4, b.ConsecutiveFailures);
        b.RegisterFailure();
        Assert.False(b.CanRetry);
    }

    [Fact]
    public void ThresholdBoundary_300Resets_299DoesNot()
    {
        var atThreshold = New();
        atThreshold.RegisterFailure();
        atThreshold.NoteObserveWindow(inWorldSeconds: 300);
        Assert.Equal(0, atThreshold.ConsecutiveFailures);

        var underThreshold = New();
        underThreshold.RegisterFailure();
        underThreshold.NoteObserveWindow(inWorldSeconds: 299.999);
        Assert.Equal(1, underThreshold.ConsecutiveFailures);
    }

    [Fact]
    public void HealthyWindowReconnect_ThenConnectStageFailures_StreakContinuesFromOne()
    {
        var b = New();
        // Played healthily, disconnected once (streak -> 1 after the reset+register),
        b.RegisterFailure();
        b.RegisterFailure();
        b.NoteObserveWindow(inWorldSeconds: 600); // reset to 0
        Assert.Equal(5000, b.RegisterFailure());                     // post-reset reconnect -> 1
        Assert.Equal(1, b.ConsecutiveFailures);
        // Subsequent connect-stage failures continue the fresh streak.
        Assert.Equal(10000, b.RegisterFailure());
        Assert.Equal(2, b.ConsecutiveFailures);
    }
}
