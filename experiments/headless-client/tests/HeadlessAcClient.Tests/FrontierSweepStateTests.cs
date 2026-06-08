// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for FrontierSweepState — the cp-2363 anti-tunnel sweep bookkeeping
// for the AIMLESS outdoor frontier. The state cycles a compass heading through
// the eight sectors as the bot crosses distinct landblocks, so an undirected
// Explore fans across directions instead of tunnelling one bearing.

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class FrontierSweepStateTests
{
    [Fact]
    public void InitialHeading_IsEast()
    {
        var sweep = new FrontierSweepState(landblockSpan: 4);
        Assert.Equal("east", sweep.CurrentHeading);
        Assert.Equal(0, sweep.Sector);
    }

    [Fact]
    public void StayingInOneLandblock_NeverRotates()
    {
        var sweep = new FrontierSweepState(landblockSpan: 4);
        for (int i = 0; i < 50; i++)
            Assert.Equal("east", sweep.Advance(0xA9B4u));
        Assert.Equal(0, sweep.Sector);
    }

    [Fact]
    public void RevisitingSameLandblock_DoesNotDoubleCount()
    {
        var sweep = new FrontierSweepState(landblockSpan: 4);
        // Five visits but only four DISTINCT landblocks => no rotation yet.
        sweep.Advance(0xA9B4u);
        sweep.Advance(0xA9B4u);
        sweep.Advance(0xAAB4u);
        sweep.Advance(0xABB4u);
        Assert.Equal("east", sweep.Advance(0xACB4u)); // 4 distinct, still east
        Assert.Equal(0, sweep.Sector);
    }

    [Fact]
    public void CrossingMoreThanSpanDistinctLandblocks_RotatesToNextSector()
    {
        var sweep = new FrontierSweepState(landblockSpan: 4);
        sweep.Advance(0xA9B4u);
        sweep.Advance(0xAAB4u);
        sweep.Advance(0xABB4u);
        sweep.Advance(0xACB4u);              // 4 distinct => still east
        Assert.Equal("east", sweep.CurrentHeading);
        var heading = sweep.Advance(0xADB4u); // 5th distinct => rotate
        Assert.Equal("north", heading);
        Assert.Equal(1, sweep.Sector);
    }

    [Fact]
    public void AfterRotation_LandblockSetReseedsWithCurrent()
    {
        var sweep = new FrontierSweepState(landblockSpan: 4);
        // Rotate once (5 distinct).
        foreach (var lb in new uint[] { 1, 2, 3, 4, 5 }) sweep.Advance(lb);
        Assert.Equal("north", sweep.CurrentHeading);
        // The rotating landblock (5) seeded the new heading's set, so it takes
        // four MORE distinct landblocks (6,7,8,9) to rotate again, not five.
        sweep.Advance(6u);
        sweep.Advance(7u);
        sweep.Advance(8u);
        // set is {5,6,7,8,9} = 5 distinct (> span 4) => Advance(9) rotates and
        // returns the new heading.
        Assert.Equal("west", sweep.Advance(9u));
        Assert.Equal("west", sweep.CurrentHeading);
    }

    [Fact]
    public void ZeroLandblock_IsIgnored()
    {
        var sweep = new FrontierSweepState(landblockSpan: 4);
        // Indoor/unknown cells (landblock 0) neither count nor reset progress.
        sweep.Advance(0xA9B4u);
        sweep.Advance(0u);
        sweep.Advance(0xAAB4u);
        sweep.Advance(0u);
        sweep.Advance(0xABB4u);
        sweep.Advance(0u);
        Assert.Equal("east", sweep.Advance(0xACB4u)); // 4 distinct real => still east
        var heading = sweep.Advance(0xADB4u);          // 5th real => rotate
        Assert.Equal("north", heading);
    }

    [Fact]
    public void FullCycle_VisitsAllEightHeadingsAndWraps()
    {
        var sweep = new FrontierSweepState(landblockSpan: 4);
        // Collect the initial heading plus each heading the sweep rotates to,
        // by feeding fresh distinct landblocks until it changes. Robust to the
        // reseed carry-over (a rotation reseeds the set with the current block,
        // so the adds-per-rotation is not a fixed constant).
        var seen = new System.Collections.Generic.List<string> { sweep.CurrentHeading };
        uint lb = 100u;
        while (seen.Count < 9)
        {
            var before = sweep.CurrentHeading;
            var after = sweep.Advance(lb++);
            if (after != before) seen.Add(after);
        }

        Assert.Equal(
            new[] { "east", "north", "west", "south", "northeast",
                    "northwest", "southwest", "southeast", "east" },
            seen);
        Assert.Equal(0, sweep.Sector); // wrapped back to east
    }

    [Fact]
    public void SpanClampedToMinimumOne()
    {
        var sweep = new FrontierSweepState(landblockSpan: 0); // clamps to 1
        sweep.Advance(1u);                       // 1 distinct => still east
        Assert.Equal("east", sweep.CurrentHeading);
        Assert.Equal("north", sweep.Advance(2u)); // 2nd distinct (> 1) => rotate
    }
}
