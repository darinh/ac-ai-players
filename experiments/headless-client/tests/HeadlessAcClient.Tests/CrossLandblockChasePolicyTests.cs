using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Tests for <see cref="CrossLandblockChasePolicy.ShouldStraightSteerOutdoor"/>:
/// the geometric gate that lets the Attack cross-landblock resolver
/// steer straight to a remembered sighting when the navgraph found no
/// on-foot route AND both cells are outdoor + adjacent.
/// </summary>
public class CrossLandblockChasePolicyTests
{
    // Cell-id layout: LandblockX = (c >> 24) & 0xFF, LandblockY = (c >> 16) & 0xFF.
    // Outdoor when (c & 0xFFFF) < 0x100. Build a clean outdoor cell:
    private static uint Outdoor(byte lbx, byte lby, ushort cell = 0x0000)
        => ((uint)lbx << 24) | ((uint)lby << 16) | cell;

    // Indoor cell (low-16 >= 0x100).
    private static uint Indoor(byte lbx, byte lby, ushort cell = 0x0100)
        => ((uint)lbx << 24) | ((uint)lby << 16) | cell;

    [Fact]
    public void SameLandblock_Outdoor_AllowsStraightSteer()
    {
        var self = Outdoor(0xA8, 0xB4);
        var sighting = Outdoor(0xA8, 0xB4, 0x00FF);
        Assert.True(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }

    [Theory]
    // The 8 landblock neighbours of 0xA8B4 (the live Drudge case is the
    // north neighbour 0xA8B5).
    [InlineData(0xA8, 0xB5)] // N
    [InlineData(0xA8, 0xB3)] // S
    [InlineData(0xA9, 0xB4)] // E
    [InlineData(0xA7, 0xB4)] // W
    [InlineData(0xA9, 0xB5)] // NE
    [InlineData(0xA7, 0xB5)] // NW
    [InlineData(0xA9, 0xB3)] // SE
    [InlineData(0xA7, 0xB3)] // SW
    public void AdjacentLandblock_Outdoor_AllowsStraightSteer(byte lbx, byte lby)
    {
        var self = Outdoor(0xA8, 0xB4);
        var sighting = Outdoor(lbx, lby);
        Assert.True(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }

    [Fact]
    public void LiveDrudgeCase_0xA8B4_to_0xA8B5_AllowsStraightSteer()
    {
        // From the cp-2295 live-fire: bot in 0xA8B4, Drudge in 0xA8B5.
        var self = Outdoor(0xA8, 0xB4, 0x002A);
        var sighting = Outdoor(0xA8, 0xB5, 0x0017);
        Assert.True(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }

    [Theory]
    [InlineData(0xAA, 0xB4)] // two landblocks east
    [InlineData(0xA8, 0xB6)] // two north
    [InlineData(0xAA, 0xB6)] // two diagonal
    [InlineData(0x12, 0x34)] // far away (e.g. post-teleport stale sighting)
    public void FarLandblock_Outdoor_BlocksStraightSteer(byte lbx, byte lby)
    {
        var self = Outdoor(0xA8, 0xB4);
        var sighting = Outdoor(lbx, lby);
        Assert.False(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }

    [Fact]
    public void SelfIndoor_BlocksStraightSteer()
    {
        // Indoor needs real door/portal pathfinding, not a blind walk.
        var self = Indoor(0xA8, 0xB4);
        var sighting = Outdoor(0xA8, 0xB5);
        Assert.False(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }

    [Fact]
    public void SightingIndoor_BlocksStraightSteer()
    {
        var self = Outdoor(0xA8, 0xB4);
        var sighting = Indoor(0xA8, 0xB5);
        Assert.False(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }

    [Fact]
    public void BothIndoor_BlocksStraightSteer()
    {
        var self = Indoor(0xA8, 0xB4);
        var sighting = Indoor(0xA8, 0xB5);
        Assert.False(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }

    [Fact]
    public void ZeroSelfCell_BlocksStraightSteer()
    {
        // IsOutdoor(0) is true (0 & 0xFFFF < 0x100) so the zero/default
        // cell must be rejected explicitly to avoid steering on invalid
        // geometry.
        var sighting = Outdoor(0xA8, 0xB5);
        Assert.False(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(0u, sighting));
    }

    [Fact]
    public void ZeroSightingCell_BlocksStraightSteer()
    {
        var self = Outdoor(0xA8, 0xB4);
        Assert.False(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, 0u));
    }

    [Fact]
    public void BothZeroCells_BlockStraightSteer()
    {
        Assert.False(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(0u, 0u));
    }

    [Fact]
    public void LowCellIndexDoesNotAffectAdjacency()
    {
        // Two outdoor cells in adjacent landblocks but with very
        // different low-16 indices are still adjacent (the low-16 must
        // be ignored for the landblock-grid distance).
        var self = Outdoor(0xA8, 0xB4, 0x00FE);
        var sighting = Outdoor(0xA9, 0xB4, 0x0001);
        Assert.True(CrossLandblockChasePolicy.ShouldStraightSteerOutdoor(self, sighting));
    }
}
