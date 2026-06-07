using System;
using System.Linq;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Tests for <see cref="ApproachDistanceTracker"/>: the per-target rolling
/// history of measured self→target distances recorded at each interaction
/// goal lock. The most-recent target's history is projected into the
/// prompt's "## Approach distance history" capsule so the LLM can see
/// whether its repeated selections of the same target are reducing the
/// distance. Live repro: nine Talk locks on one NPC at a constant 27.47u.
/// </summary>
public class ApproachDistanceTrackerTests
{
    private const uint GuidA = 0x80003068u;
    private const uint GuidB = 0x80000AAAu;
    private static readonly DateTime T0 = new(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    [Fact]
    public void Empty_TryGetMostRecent_False()
    {
        var t = new ApproachDistanceTracker();
        Assert.False(t.TryGetMostRecent(T0, Fresh, 2, out _, out _, out _));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void SingleSample_BelowMinSamples_False()
    {
        var t = new ApproachDistanceTracker();
        t.Record(GuidA, "Worcer", 27.5, T0);
        Assert.False(t.TryGetMostRecent(T0, Fresh, 2, out _, out _, out _));
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void TwoSamplesSameTarget_ReturnsHistoryOldestToNewest()
    {
        var t = new ApproachDistanceTracker();
        t.Record(GuidA, "Worcer", 99.7, T0);
        t.Record(GuidA, "Worcer", 27.5, T0.AddSeconds(2));
        Assert.True(t.TryGetMostRecent(T0.AddSeconds(2), Fresh, 2, out var g, out var n, out var s));
        Assert.Equal(GuidA, g);
        Assert.Equal("Worcer", n);
        Assert.Equal(new[] { 99.7, 27.5 }, s.ToArray());
    }

    [Fact]
    public void MostRecentlyRecordedTarget_Wins()
    {
        var t = new ApproachDistanceTracker();
        t.Record(GuidA, "Worcer", 10.0, T0);
        t.Record(GuidA, "Worcer", 10.0, T0.AddSeconds(1));
        t.Record(GuidB, "Asenala", 2.6, T0.AddSeconds(2));
        t.Record(GuidB, "Asenala", 2.6, T0.AddSeconds(3));
        Assert.True(t.TryGetMostRecent(T0.AddSeconds(3), Fresh, 2, out var g, out var n, out _));
        Assert.Equal(GuidB, g);
        Assert.Equal("Asenala", n);
    }

    [Fact]
    public void StaleBeyondFreshness_False()
    {
        var t = new ApproachDistanceTracker();
        t.Record(GuidA, "Worcer", 27.5, T0);
        t.Record(GuidA, "Worcer", 27.5, T0.AddSeconds(1));
        // Query well after the freshness window from the last record.
        Assert.False(t.TryGetMostRecent(T0.AddSeconds(1).Add(Fresh).AddSeconds(1), Fresh, 2, out _, out _, out _));
    }

    [Fact]
    public void SampleHistory_CappedToMaxPerTarget_OldestDropped()
    {
        var t = new ApproachDistanceTracker();
        var n = ApproachDistanceTracker.MaxSamplesPerTarget + 3;
        for (var i = 0; i < n; i++)
            t.Record(GuidA, "Worcer", i, T0.AddSeconds(i));
        Assert.True(t.TryGetMostRecent(T0.AddSeconds(n), Fresh, 2, out _, out _, out var s));
        Assert.Equal(ApproachDistanceTracker.MaxSamplesPerTarget, s.Count);
        // Oldest dropped: the newest sample is the last recorded value.
        Assert.Equal((double)(n - 1), s[s.Count - 1]);
        Assert.Equal((double)(n - ApproachDistanceTracker.MaxSamplesPerTarget), s[0]);
    }

    [Fact]
    public void TrackedTargets_CappedToMax_LeastRecentEvicted()
    {
        var t = new ApproachDistanceTracker();
        // Record one sample each for MaxTrackedTargets+2 distinct guids.
        var total = ApproachDistanceTracker.MaxTrackedTargets + 2;
        for (var i = 0; i < total; i++)
            t.Record((uint)(0x1000 + i), $"npc{i}", i, T0.AddSeconds(i));
        Assert.True(t.Count <= ApproachDistanceTracker.MaxTrackedTargets);
        // The first-recorded guid should have been evicted.
        t.Record((uint)0x1000, "npc0-again", 1, T0.AddSeconds(total));
        t.Record((uint)0x1000, "npc0-again", 1, T0.AddSeconds(total + 1));
        Assert.True(t.TryGetMostRecent(T0.AddSeconds(total + 1), Fresh, 2, out var g, out _, out _));
        Assert.Equal((uint)0x1000, g);
    }

    [Fact]
    public void EmptyName_DoesNotClobber_ExistingName()
    {
        var t = new ApproachDistanceTracker();
        t.Record(GuidA, "Worcer", 27.5, T0);
        t.Record(GuidA, null, 27.5, T0.AddSeconds(1));
        t.Record(GuidA, "", 27.5, T0.AddSeconds(2));
        Assert.True(t.TryGetMostRecent(T0.AddSeconds(2), Fresh, 2, out _, out var n, out _));
        Assert.Equal("Worcer", n);
    }
}
