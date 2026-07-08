// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for LivenessWatchdog — the process-level stall detector that force-exits
// the bot when its main loop stops making progress (so the supervisor relaunches).
// The pure decision + env parsing are tested directly; one integration test
// exercises the real background-thread fire path (Start is idempotent, so only a
// single test may call it in the shared test process).

using System;
using System.Threading;
using HeadlessAcClient;
using Xunit;

namespace HeadlessAcClient.Tests;

public class LivenessWatchdogTests
{
    [Fact]
    public void IsStalled_TrueWhenGapExceedsTimeout()
    {
        const long now = 5_000_000L;
        var last = now - 301_000L; // 301s ago (monotonic ms)
        Assert.True(LivenessWatchdog.IsStalled(last, now, TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void IsStalled_FalseWhenWithinTimeout()
    {
        const long now = 5_000_000L;
        var last = now - 10_000L; // 10s ago
        Assert.False(LivenessWatchdog.IsStalled(last, now, TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void IsStalled_FalseAtExactlyTimeout()
    {
        // Boundary: a gap EQUAL to the timeout is not yet a stall (strict >).
        const long now = 5_000_000L;
        var last = now - 300_000L;
        Assert.False(LivenessWatchdog.IsStalled(last, now, TimeSpan.FromSeconds(300)));
    }

    [Theory]
    [InlineData("300", 300)]
    [InlineData("600", 600)]
    [InlineData("  1200  ", 1200)]
    public void ResolveStallTimeout_ParsesValidValue(string raw, int expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected), LivenessWatchdog.ResolveStallTimeout(raw));

    [Theory]
    [InlineData("5", LivenessWatchdog.MinStallTimeoutSeconds)]   // below floor -> clamped up
    [InlineData("30", LivenessWatchdog.MinStallTimeoutSeconds)]  // still below the reconnect-gap floor
    [InlineData("999999", 3600)]                                 // above ceiling -> clamped down
    public void ResolveStallTimeout_ClampsToBounds(string raw, int expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected), LivenessWatchdog.ResolveStallTimeout(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-10")]
    public void ResolveStallTimeout_FallsBackOnNullOrInvalid(string? raw)
        => Assert.Equal(
            TimeSpan.FromSeconds(LivenessWatchdog.DefaultStallTimeoutSeconds),
            LivenessWatchdog.ResolveStallTimeout(raw));

    [Fact]
    public void RecordProgress_BumpsHeartbeatToNow()
    {
        LivenessWatchdog.RecordProgress();
        var last = LivenessWatchdog.LastProgressMs;
        var now = Environment.TickCount64;
        // Freshly bumped -> not stalled under any sane timeout, and within a few
        // seconds of now (proves RecordProgress wrote ~Environment.TickCount64).
        Assert.False(LivenessWatchdog.IsStalled(last, now, TimeSpan.FromSeconds(30)));
        Assert.True(now - last < 5_000L);
    }

    [Fact]
    public void Start_FiresOnStall_WhenHeartbeatGoesStale()
    {
        // Integration: with a tiny stall timeout + poll, the heartbeat Start records
        // goes stale within ~poll and the monitor thread invokes onStall exactly once.
        // Start is idempotent (production guard), so this is the ONLY test that calls it.
        var fired = new ManualResetEventSlim(false);
        string? message = null;
        LivenessWatchdog.Start(
            stallTimeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(20),
            log: _ => { },
            onStall: msg => { message = msg; fired.Set(); });
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "watchdog did not fire onStall for a stale heartbeat");
        Assert.Contains("NO PROGRESS", message);
    }
}
