// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for OutdoorSeamCell — pure geometry for the outdoor seam-cell
// AutonomousPosition override. Outdoor self positions are
// landblock-relative; an outdoor walk motion (frontier probe OR an
// Attack/Pickup interaction approach) packs a fixed source cell while
// the dead-reckoned step overshoots into a neighbor landblock. The
// override re-expresses the step in the coordinate frame of the cell
// that actually contains it so the (cell, pos) pair stays internally
// consistent across a landblock seam. These tests assert it fires for
// ANY outdoor self-cell step that crosses a landblock seam (no
// frontier-probe / path-cell gating), declines on same-landblock steps,
// and is gated out indoors / while an indoor path owns the AP cell.

using System.Numerics;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class OutdoorSeamCellTests
{
    // Source landblock 0xA9B4: lbx=0xA9=169, lby=0xB4=180. Origin Y = 180*192 = 34560.
    private const uint SourceCell = 0xA9B40001u;

    // A step to global (32500, 34559) lands one meter south of the 0xA9B4
    // origin -> landblock 0xA9B3 (lby=179). lx = 32500-169*192 = 52 -> cx=2;
    // ly = 34559-179*192 = 191 -> cy=7 (clamped); cell = 2*8+7+1 = 24 = 0x18.
    private const float StepGX = 32500f;
    private const float StepGY = 34559f;
    private const uint  ExpectedNeighborCell = 0xA9B30018u;
    private const float ExpectedLocalX = 52f;   // 32500 - 169*192
    private const float ExpectedLocalY = 191f;  // 34559 - 179*192
    private const float StepZ = 94.0f;

    [Fact]
    public void Override_OutdoorSeamCross_DerivesNeighborCell()
    {
        var ok = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ,
            apCellId:            out var apCellId,
            apLocalPos:          out var apLocalPos);

        Assert.True(ok);
        Assert.Equal(ExpectedNeighborCell, apCellId);
        Assert.Equal(ExpectedLocalX, apLocalPos.X, 3);
        Assert.Equal(ExpectedLocalY, apLocalPos.Y, 3);
        Assert.Equal(StepZ, apLocalPos.Z, 3);
    }

    [Fact]
    public void Override_ApproachMotionSeamCross_DerivesNeighborCell_NoPathCellGate()
    {
        // The generalization: an interaction-approach motion (no frontier
        // probe, no rasterized path-cell set) crossing a seam STILL derives
        // the neighbor cell. This is the case the frontier-only predecessor
        // declined, which froze Attack/Pickup approaches at the seam.
        var ok = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ,
            apCellId:            out var apCellId,
            apLocalPos:          out _);

        Assert.True(ok);
        Assert.Equal(ExpectedNeighborCell, apCellId);
    }

    [Fact]
    public void Override_IndoorWaypointPathActive_KeepsSourceCell()
    {
        var ok = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: true,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ,
            apCellId:            out var apCellId,
            apLocalPos:          out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_SelfCellIndoor_KeepsSourceCell()
    {
        var ok = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   false,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ,
            apCellId:            out var apCellId,
            apLocalPos:          out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_SameLandblockStep_KeepsSourceCell()
    {
        // Step to global (32500, 34600): lby = 34600/192 = 180 -> landblock
        // 0xA9B4, SAME as the source. The override must NOT fire — same-
        // landblock walks stay byte-identical (the server re-derives the
        // intra-landblock cell from the AP coords).
        var ok = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         32500f,
            stepGlobalY:         34600f,
            stepZ:               StepZ,
            apCellId:            out var apCellId,
            apLocalPos:          out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_NeighborLocalPos_IsExpressedInNeighborLandblockFrame()
    {
        // Crossing WEST into landblock 0xA8B4 (lbx=0xA8=168): a step to
        // global X just below the 0xA9 origin (169*192 = 32448) lands in
        // lbx=168 with local X near the high end of that landblock.
        const float gx = 32447f;          // 168*192 = 32256; lx = 191
        const float gy = 34600f;          // lby = 180 -> 0xB4; ly = 40
        var ok = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         gx,
            stepGlobalY:         gy,
            stepZ:               StepZ,
            apCellId:            out var apCellId,
            apLocalPos:          out var apLocalPos);

        Assert.True(ok);
        Assert.Equal(0xA8u, (apCellId >> 24) & 0xFFu); // lbx = 168
        Assert.Equal(0xB4u, (apCellId >> 16) & 0xFFu); // lby = 180
        Assert.Equal(191f, apLocalPos.X, 3);           // 32447 - 168*192
        Assert.Equal(40f, apLocalPos.Y, 3);            // 34600 - 180*192
    }
}
