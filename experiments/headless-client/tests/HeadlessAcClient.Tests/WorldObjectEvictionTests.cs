// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for WorldObjectEviction — the pure block-distance decision behind the
// world-snapshot distant-object eviction sweep. The stateful sweep + preservation
// rules are tested via WorldState.EvictDistantObjects in WorldStateTests.cs.

using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class WorldObjectEvictionTests
{
    // ---- ResolveBlockRadius ----

    [Theory]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData("   ", 3)]
    [InlineData("not-a-number", 3)]
    [InlineData("-1", 3)]      // negative -> default
    [InlineData("0", 0)]       // explicit disable
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("64", 64)]
    [InlineData("999", 64)]    // clamped to max
    public void ResolveBlockRadius(string? env, int expected)
    {
        Assert.Equal(expected, WorldObjectEviction.ResolveBlockRadius(env));
    }

    // ---- BlockX / BlockY (top two bytes of the CellId) ----

    [Fact]
    public void BlockXY_DecodesTopTwoBytes()
    {
        // 0xA8B50024 -> block X = 0xA8 (168), block Y = 0xB5 (181), cell 0x0024 ignored.
        Assert.Equal(0xA8, WorldObjectEviction.BlockX(0xA8B50024));
        Assert.Equal(0xB5, WorldObjectEviction.BlockY(0xA8B50024));
        Assert.Equal(0xAE, WorldObjectEviction.BlockX(0xAEAF000B));
        Assert.Equal(0xAF, WorldObjectEviction.BlockY(0xAEAF000B));
    }

    // ---- BlockDistance (Chebyshev over landblocks) ----

    [Fact]
    public void BlockDistance_SameLandblock_IsZero()
    {
        // Different cells within the SAME landblock -> distance 0 (cell bits ignored).
        Assert.Equal(0, WorldObjectEviction.BlockDistance(0xA8B50024, 0xA8B5FFFF));
    }

    [Fact]
    public void BlockDistance_IsChebyshev()
    {
        // block (0xA8,0xB5) vs (0xAE,0xAF): dX=6, dY=6 -> max = 6.
        Assert.Equal(6, WorldObjectEviction.BlockDistance(0xA8B50000, 0xAEAF0000));
        // block (0xA8,0xB5) vs (0xA9,0xB5): dX=1, dY=0 -> 1 (adjacent).
        Assert.Equal(1, WorldObjectEviction.BlockDistance(0xA8B50000, 0xA9B50000));
        // block (0xA8,0xB5) vs (0xAA,0xB8): dX=2, dY=3 -> max = 3.
        Assert.Equal(3, WorldObjectEviction.BlockDistance(0xA8B50000, 0xAAB80000));
    }

    // ---- ShouldEvictByBlockDistance ----

    [Fact]
    public void ShouldEvict_WithinRadius_False()
    {
        // 1 block away, radius 3 -> keep.
        Assert.False(WorldObjectEviction.ShouldEvictByBlockDistance(0xA8B50000, 0xA9B50000, 3));
    }

    [Fact]
    public void ShouldEvict_AtRadius_False()
    {
        // exactly 3 blocks away, radius 3 -> keep (only MORE than radius is evicted).
        Assert.False(WorldObjectEviction.ShouldEvictByBlockDistance(0xA8B50000, 0xABB50000, 3));
    }

    [Fact]
    public void ShouldEvict_BeyondRadius_True()
    {
        // 6 blocks away, radius 3 -> evict.
        Assert.True(WorldObjectEviction.ShouldEvictByBlockDistance(0xA8B50000, 0xAEB50000, 3));
    }

    [Fact]
    public void ShouldEvict_RadiusZero_NeverEvicts()
    {
        // radius 0 (disabled): even a very far object is kept.
        Assert.False(WorldObjectEviction.ShouldEvictByBlockDistance(0xA8B50000, 0xFFFF0000, 0));
    }

    [Fact]
    public void ShouldEvict_SameLandblock_False()
    {
        Assert.False(WorldObjectEviction.ShouldEvictByBlockDistance(0xA8B50024, 0xA8B50099, 3));
    }
}
