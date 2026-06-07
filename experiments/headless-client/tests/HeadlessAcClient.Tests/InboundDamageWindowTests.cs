using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// active-combat-telemetry: unit tests for the pure rolling-window helper
/// that evicts stale inbound hits and summarizes the survivors. No live LLM
/// or network — deterministic over an injected "now".
/// </summary>
public class InboundDamageWindowTests
{
    private static readonly DateTime Now = new(2026, 6, 7, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptyList_ReturnsNull()
    {
        var hits = new List<InboundHit>();
        Assert.Null(InboundDamageWindow.PruneAndSummarize(hits, Now, 12.0));
        Assert.Empty(hits);
    }

    [Fact]
    public void NullList_ReturnsNull()
    {
        Assert.Null(InboundDamageWindow.PruneAndSummarize(null!, Now, 12.0));
    }

    [Fact]
    public void AllWithinWindow_CountsAndSums()
    {
        var hits = new List<InboundHit>
        {
            new(Now.AddSeconds(-1), 5u),
            new(Now.AddSeconds(-4), 3u),
            new(Now.AddSeconds(-11), 2u),
        };
        var summary = InboundDamageWindow.PruneAndSummarize(hits, Now, 12.0);
        Assert.NotNull(summary);
        Assert.Equal(3, summary!.Hits);
        Assert.Equal(10u, summary.TotalDamage);
        Assert.Equal(12.0, summary.WindowSeconds);
        // Nothing was expired, so the backing list is untouched.
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void ExpiredHits_ArePrunedAndExcluded()
    {
        var hits = new List<InboundHit>
        {
            new(Now.AddSeconds(-2), 6u),   // in window
            new(Now.AddSeconds(-13), 4u),  // expired (>12s)
            new(Now.AddSeconds(-30), 9u),  // expired
            new(Now.AddSeconds(-0.5), 1u), // in window
        };
        var summary = InboundDamageWindow.PruneAndSummarize(hits, Now, 12.0);
        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Hits);
        Assert.Equal(7u, summary.TotalDamage);
        // The two expired entries were evicted in place.
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void AllExpired_ReturnsNullAndEmptiesList()
    {
        var hits = new List<InboundHit>
        {
            new(Now.AddSeconds(-13), 4u),
            new(Now.AddSeconds(-20), 9u),
        };
        Assert.Null(InboundDamageWindow.PruneAndSummarize(hits, Now, 12.0));
        Assert.Empty(hits);
    }

    [Fact]
    public void BoundaryHit_AtExactlyWindowEdge_IsRetained()
    {
        // (now - At) == windowSeconds is NOT older-than-window, so it stays.
        var hits = new List<InboundHit> { new(Now.AddSeconds(-12), 8u) };
        var summary = InboundDamageWindow.PruneAndSummarize(hits, Now, 12.0);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Hits);
        Assert.Equal(8u, summary.TotalDamage);
    }

    // inbound-damage-onset-wake: episode-boundary detection for the one
    // structural LLM wake per inbound-damage episode.

    [Fact]
    public void Episode_FirstHitEver_BeginsEpisode()
    {
        // previousHitUtc == null => the first inbound hit always begins one.
        Assert.True(InboundDamageWindow.BeginsNewInboundEpisode(null, Now, 12.0));
    }

    [Fact]
    public void Episode_HitWithinWindowOfPrevious_DoesNotBegin()
    {
        // A continuous fight: hits closer together than the window are the
        // SAME episode, so only the first wakes the LLM.
        var prev = Now.AddSeconds(-2);
        Assert.False(InboundDamageWindow.BeginsNewInboundEpisode(prev, Now, 12.0));
    }

    [Fact]
    public void Episode_HitAfterLull_BeginsNewEpisode()
    {
        // A lull of at least the window since the previous hit re-arms.
        var prev = Now.AddSeconds(-13);
        Assert.True(InboundDamageWindow.BeginsNewInboundEpisode(prev, Now, 12.0));
    }

    [Fact]
    public void Episode_HitAtExactlyWindowGap_BeginsNewEpisode()
    {
        // gap == window is treated as a lull (>= boundary), re-arming the wake
        // — symmetric with the LULL side, distinct from the retain-on-edge
        // prune rule which keeps an exactly-window-old hit.
        var prev = Now.AddSeconds(-12);
        Assert.True(InboundDamageWindow.BeginsNewInboundEpisode(prev, Now, 12.0));
    }
}
