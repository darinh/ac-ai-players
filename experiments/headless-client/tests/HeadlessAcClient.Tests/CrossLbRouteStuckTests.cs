using System;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Tests for <see cref="CrossLbRouteStuck"/>: the per-sighting "route keeps
/// re-advancing the same boundary" counter that marks a cross-landblock Explore
/// destination route-blocked (unreachable from the bot's current area) so the policy
/// can cue the LLM to stop re-Exploring it.
/// </summary>
public class CrossLbRouteStuckTests
{
    private static readonly Guid Sighting = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Boundary = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Boundary2 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void FirstAdvance_IsProgress()
    {
        var t = new CrossLbRouteStuck(4);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, Boundary));
    }

    [Fact]
    public void SameBoundaryRepeats_BuildThenBlockAtThreshold()
    {
        var t = new CrossLbRouteStuck(4);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, Boundary)); // 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, Boundary));  // 2
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, Boundary));  // 3
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, Boundary));   // 4 == threshold
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, Boundary));   // stays blocked
    }

    [Fact]
    public void NewBoundary_ResetsToProgress()
    {
        var t = new CrossLbRouteStuck(3);
        t.RecordAdvance(Sighting, Boundary); // 1
        t.RecordAdvance(Sighting, Boundary); // 2
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, Boundary)); // 3
        // Route advanced PAST the boundary (a new boundary node) => progress, count resets.
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, Boundary2));
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, Boundary2));
    }

    [Fact]
    public void DistinctSightings_TrackedIndependently()
    {
        var t = new CrossLbRouteStuck(2);
        var other = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, Boundary));
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(other, Boundary));
        // Each sighting's own second same-boundary advance reaches the threshold (2) independently.
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, Boundary));
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(other, Boundary));
    }

    [Fact]
    public void ThresholdBelowTwo_ClampedToTwo()
    {
        // A single advance can never be "stuck"; the floor is 2 (a repeat is the minimum signal).
        var t = new CrossLbRouteStuck(1);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, Boundary));
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, Boundary));
    }
}
