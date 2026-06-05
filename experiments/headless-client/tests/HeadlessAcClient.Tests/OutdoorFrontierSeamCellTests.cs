// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for OutdoorFrontierSeamCell — pure geometry for the
// outdoor-frontier seam-cell AutonomousPosition override. A first-time
// outdoor frontier probe leaves the indoor waypoint path null, so the
// motor's indoor cell-advance never runs; when the straight probe segment
// crosses an outdoor landblock seam the packet keeps claiming the SOURCE
// cell while the position overshoots into the neighbor landblock, and the
// server freezes the bot. These tests assert the override fires ONLY for an
// outdoor frontier probe whose next step lands in a DIFFERENT outdoor
// landblock cell that is already in the probe's own rasterized path-cell
// set, and that the derived (cellId, local pos) pair is correct.

using System.Collections.Generic;
using System.Numerics;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class OutdoorFrontierSeamCellTests
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

    private static IReadOnlySet<uint> Cells(params uint[] c) => new HashSet<uint>(c);

    [Fact]
    public void Override_OutdoorFrontierSeamCrossInPathCells_DerivesNeighborCell()
    {
        var ok = OutdoorFrontierSeamCell.TryDeriveSeamCell(
            isOutdoorFrontierProbe: true,
            hasIndoorWaypointPath:  false,
            selfCellIsIndoor:       false,
            pathCells:              Cells(ExpectedNeighborCell),
            lockedCellId:           SourceCell,
            stepGlobalX:            StepGX,
            stepGlobalY:            StepGY,
            stepZ:                  StepZ,
            apCellId:               out var apCellId,
            apLocalPos:             out var apLocalPos);

        Assert.True(ok);
        Assert.Equal(ExpectedNeighborCell, apCellId);
        Assert.Equal(ExpectedLocalX, apLocalPos.X, 3);
        Assert.Equal(ExpectedLocalY, apLocalPos.Y, 3);
        Assert.Equal(StepZ, apLocalPos.Z, 3);
    }

    [Fact]
    public void Override_NotAFrontierProbe_KeepsSourceCell()
    {
        var ok = OutdoorFrontierSeamCell.TryDeriveSeamCell(
            isOutdoorFrontierProbe: false,
            hasIndoorWaypointPath:  false,
            selfCellIsIndoor:       false,
            pathCells:              Cells(ExpectedNeighborCell),
            lockedCellId:           SourceCell,
            stepGlobalX:            StepGX,
            stepGlobalY:            StepGY,
            stepZ:                  StepZ,
            apCellId:               out var apCellId,
            apLocalPos:             out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_IndoorWaypointPathActive_KeepsSourceCell()
    {
        var ok = OutdoorFrontierSeamCell.TryDeriveSeamCell(
            isOutdoorFrontierProbe: true,
            hasIndoorWaypointPath:  true,
            selfCellIsIndoor:       false,
            pathCells:              Cells(ExpectedNeighborCell),
            lockedCellId:           SourceCell,
            stepGlobalX:            StepGX,
            stepGlobalY:            StepGY,
            stepZ:                  StepZ,
            apCellId:               out var apCellId,
            apLocalPos:             out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_SelfCellIndoor_KeepsSourceCell()
    {
        var ok = OutdoorFrontierSeamCell.TryDeriveSeamCell(
            isOutdoorFrontierProbe: true,
            hasIndoorWaypointPath:  false,
            selfCellIsIndoor:       true,
            pathCells:              Cells(ExpectedNeighborCell),
            lockedCellId:           SourceCell,
            stepGlobalX:            StepGX,
            stepGlobalY:            StepGY,
            stepZ:                  StepZ,
            apCellId:               out var apCellId,
            apLocalPos:             out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_NullPathCells_KeepsSourceCell()
    {
        var ok = OutdoorFrontierSeamCell.TryDeriveSeamCell(
            isOutdoorFrontierProbe: true,
            hasIndoorWaypointPath:  false,
            selfCellIsIndoor:       false,
            pathCells:              null,
            lockedCellId:           SourceCell,
            stepGlobalX:            StepGX,
            stepGlobalY:            StepGY,
            stepZ:                  StepZ,
            apCellId:               out var apCellId,
            apLocalPos:             out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_NeighborCellNotInPathCells_KeepsSourceCell()
    {
        // A different neighbor cell is in the route, but not the one the step
        // actually lands in -> must NOT override (stays bounded by the route).
        var ok = OutdoorFrontierSeamCell.TryDeriveSeamCell(
            isOutdoorFrontierProbe: true,
            hasIndoorWaypointPath:  false,
            selfCellIsIndoor:       false,
            pathCells:              Cells(0xA9B30019u),
            lockedCellId:           SourceCell,
            stepGlobalX:            StepGX,
            stepGlobalY:            StepGY,
            stepZ:                  StepZ,
            apCellId:               out var apCellId,
            apLocalPos:             out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }

    [Fact]
    public void Override_SameLandblockStep_KeepsSourceCell()
    {
        // Step to global (32500, 34600): lby = 34600/192 = 180 -> landblock
        // 0xA9B4, SAME as the source. Even though the derived cell is in the
        // route, the override must NOT fire (same-landblock walks must be
        // byte-identical / handled by the existing reactive slide).
        var derivedSameLb = 0xA9B40012u; // lx=52->cx=2, ly=40->cy=1, 2*8+1+1=18
        var ok = OutdoorFrontierSeamCell.TryDeriveSeamCell(
            isOutdoorFrontierProbe: true,
            hasIndoorWaypointPath:  false,
            selfCellIsIndoor:       false,
            pathCells:              Cells(derivedSameLb),
            lockedCellId:           SourceCell,
            stepGlobalX:            32500f,
            stepGlobalY:            34600f,
            stepZ:                  StepZ,
            apCellId:               out var apCellId,
            apLocalPos:             out _);

        Assert.False(ok);
        Assert.Equal(SourceCell, apCellId);
    }
}
