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
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void FirstAdvance_IsProgress()
    {
        var t = new CrossLbRouteStuck(4);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f));
    }

    [Fact]
    public void NotConverging_BuildsThenBlocksAtThreshold()
    {
        var t = new CrossLbRouteStuck(4);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f)); // baseline
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 100f)); // stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 100f)); // stall 2
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 100f)); // stall 3
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, 100f));  // stall 4 == threshold
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, 100f));  // stays blocked
    }

    [Fact]
    public void WanderingBetweenBoundaries_StillBlocks()
    {
        // The varying-route case: distance-to-sighting FLUCTUATES (different boundaries each cycle)
        // but never gets meaningfully closer than the best -> still blocks (the same-boundary
        // detector would have missed this because the boundary changes).
        var t = new CrossLbRouteStuck(4);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f)); // baseline best=100
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 130f)); // farther
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 110f)); // closer than 130, not < 95
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 140f)); // farther
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, 120f));  // stall 4 -> Blocked
    }

    [Fact]
    public void MeaningfulApproach_ResetsToProgress_SmallWobbleDoesNot()
    {
        var t = new CrossLbRouteStuck(3);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f)); // baseline best=100
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 100f)); // stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 98f));  // only 2u (< 5u epsilon) -> stall 2
        // A meaningful approach (>= 5u closer than baseline=100) resets the stall.
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 90f));  // 10u closer -> converging
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 90f));  // stall 1 again
    }

    [Fact]
    public void SteadySmallConvergence_NeverBlocks()
    {
        // Each advance closes < epsilon (5u) but the bot IS steadily converging. The FIXED progress
        // baseline (not lowered on sub-epsilon stalls) lets small gains ACCUMULATE into a Progress
        // step, so a slow-but-genuine approach is never false-Blocked.
        var t = new CrossLbRouteStuck(4);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f)); // baseline 100
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 97f));  // 3u vs 100 -> stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 94f));  // 6u vs baseline 100 -> Progress, baseline 94
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 91f));  // 3u vs 94 -> stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 88f));  // 6u vs 94 -> Progress, baseline 88
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 85f));  // 3u vs 88 -> stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 82f));  // 6u vs 88 -> Progress
        // 7 advances of steady 3u convergence -> never 4 consecutive stalls -> never Blocked.
    }

    [Fact]
    public void SetbackThenRecovery_MustBeatTheFixedBaseline()
    {
        // Deliberate semantic: "progress" means beating the last real progress BASELINE, not merely
        // recovering from a setback. After a setback (distance jumps UP), the bot must close to
        // <= baseline - epsilon to register Progress; partial recovery is still a stall.
        var t = new CrossLbRouteStuck(4);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f)); // baseline 100
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 130f)); // setback -> stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 110f)); // recovering, not past 95 -> stall 2
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 96f));  // 4u from baseline, < 5u -> stall 3
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 94f));  // 6u past baseline 100 -> Progress
    }

    [Fact]
    public void DistinctSightings_TrackedIndependently()
    {
        var t = new CrossLbRouteStuck(2);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f));
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Other, 100f));
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 100f)); // S stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Other, 100f));    // other stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, 100f));  // S stall 2 == threshold
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Other, 100f));     // other stall 2
    }

    [Fact]
    public void ThresholdBelowTwo_ClampedToTwo()
    {
        // A single advance can never be "stuck"; the floor is 2 (a non-converging repeat is the minimum signal).
        var t = new CrossLbRouteStuck(1);
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Progress, t.RecordAdvance(Sighting, 100f)); // baseline
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Building, t.RecordAdvance(Sighting, 100f)); // stall 1
        Assert.Equal(CrossLbRouteStuck.RouteAdvanceState.Blocked, t.RecordAdvance(Sighting, 100f));  // stall 2 == clamped threshold
    }
}

