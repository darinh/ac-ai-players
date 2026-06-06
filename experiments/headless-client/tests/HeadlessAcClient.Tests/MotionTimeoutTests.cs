// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for MotionTimeout — the pure no-lock-vs-productive motion
// wall-clock timeout selection (cp-2272).

using System;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class MotionTimeoutTests
{
    private const int Locked = 30;
    private const int NoLock = 6;

    [Fact]
    public void EffectiveSeconds_ProductiveLock_UsesLongTimeout()
        => Assert.Equal(Locked, MotionTimeout.EffectiveSeconds(true, Locked, NoLock));

    [Fact]
    public void EffectiveSeconds_NoLock_UsesShortTimeout()
        => Assert.Equal(NoLock, MotionTimeout.EffectiveSeconds(false, Locked, NoLock));

    [Fact]
    public void NoLock_BeforeShortTimeout_NotExpired()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = start.AddSeconds(5);
        Assert.False(MotionTimeout.IsExpired(start, now, hasProductiveLock: false, Locked, NoLock));
    }

    [Fact]
    public void NoLock_PastShortTimeout_Expired()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = start.AddSeconds(7);
        Assert.True(MotionTimeout.IsExpired(start, now, hasProductiveLock: false, Locked, NoLock));
    }

    [Fact]
    public void ProductiveLock_AtShortTimeout_NotExpired()
    {
        // A productive motion must NOT be cut short at the no-lock window;
        // it keeps the full safety timeout.
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = start.AddSeconds(10);
        Assert.False(MotionTimeout.IsExpired(start, now, hasProductiveLock: true, Locked, NoLock));
    }

    [Fact]
    public void ProductiveLock_PastLongTimeout_Expired()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = start.AddSeconds(31);
        Assert.True(MotionTimeout.IsExpired(start, now, hasProductiveLock: true, Locked, NoLock));
    }

    [Fact]
    public void NoLock_AtExactShortBoundary_NotYetExpired()
    {
        // Strictly greater-than: at exactly the boundary the motion is not
        // yet expired (matches the prior `> timeout` semantics).
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = start.AddSeconds(NoLock);
        Assert.False(MotionTimeout.IsExpired(start, now, hasProductiveLock: false, Locked, NoLock));
    }
}
