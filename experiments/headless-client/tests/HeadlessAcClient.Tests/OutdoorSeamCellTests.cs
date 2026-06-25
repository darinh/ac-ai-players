// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for OutdoorSeamCell — pure geometry for the outdoor seam-cell
// AutonomousPosition override. Outdoor self positions are
// landblock-relative; an outdoor walk motion (frontier probe OR an
// Attack/Pickup interaction approach) packs a fixed source cell while
// the dead-reckoned step overshoots into a neighbor landblock. The
// override re-expresses the step in the coordinate frame of the cell
// that actually contains it so the (cell, pos) pair stays internally
// consistent across any cell change. These tests assert it fires for ANY
// outdoor self-cell step whose coordinates fall in a DIFFERENT cell than
// the claimed source (intra-landblock OR a landblock seam; no
// frontier-probe / path-cell gating), declines only when the coordinates
// already fall in the claimed cell, and is gated out indoors / while an
// indoor path owns the AP cell.
//
// The helper returns a nullable SeamCell (NOT out-parameters): a null
// result means "no seam crossing — keep your own cell + position". This
// makes the original out-parameter footgun structurally impossible (an
// always-assigned `out apLocalPos = default` clobbered the caller's
// dead-reckoned position to (0,0,0) on every non-seam tick, freezing the
// bot at the cell origin — caught in live-verify).

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
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ);

        Assert.NotNull(seam);
        Assert.Equal(ExpectedNeighborCell, seam!.Value.CellId);
        Assert.Equal(ExpectedLocalX, seam.Value.LocalPos.X, 3);
        Assert.Equal(ExpectedLocalY, seam.Value.LocalPos.Y, 3);
        Assert.Equal(StepZ, seam.Value.LocalPos.Z, 3);
    }

    [Fact]
    public void Override_ApproachMotionSeamCross_DerivesNeighborCell_NoPathCellGate()
    {
        // The generalization: an interaction-approach motion (no frontier
        // probe, no rasterized path-cell set) crossing a seam STILL derives
        // the neighbor cell. This is the case the frontier-only predecessor
        // declined, which froze Attack/Pickup approaches at the seam.
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ);

        Assert.NotNull(seam);
        Assert.Equal(ExpectedNeighborCell, seam!.Value.CellId);
    }

    [Fact]
    public void Override_IndoorWaypointPathActive_ReturnsNull()
    {
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: true,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ);

        Assert.Null(seam);
    }

    [Fact]
    public void Override_SelfCellIndoor_ReturnsNull()
    {
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   false,
            lockedCellId:        SourceCell,
            stepGlobalX:         StepGX,
            stepGlobalY:         StepGY,
            stepZ:               StepZ);

        Assert.Null(seam);
    }

    [Fact]
    public void Override_SameLandblock_DifferentCell_DerivesIntraLandblockCell()
    {
        // Step to global (32500, 34600): SAME landblock 0xA9B4 as the source
        // (lbx=169, lby=180) but a DIFFERENT 24 m cell.
        //   lx = 32500 - 169*192 = 52 -> cx = 2
        //   ly = 34600 - 180*192 = 40 -> cy = 1
        //   cell = 2*8 + 1 + 1 = 18 = 0x12
        // The source cell is 0x01, so the coordinates fall in a different
        // cell of the SAME landblock. The override MUST correct the claimed
        // cell (the server does NOT re-derive intra-landblock cells — it
        // adopts the stale claim, which breaks the in-combat StickToObject
        // move). Local pos is unchanged (same landblock frame).
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         32500f,
            stepGlobalY:         34600f,
            stepZ:               StepZ);

        Assert.NotNull(seam);
        Assert.Equal(0xA9B40012u, seam!.Value.CellId);
        Assert.Equal(52f, seam.Value.LocalPos.X, 3);   // 32500 - 169*192
        Assert.Equal(40f, seam.Value.LocalPos.Y, 3);   // 34600 - 180*192
        Assert.Equal(StepZ, seam.Value.LocalPos.Z, 3);
    }

    [Fact]
    public void Override_SameCell_ReturnsNull()
    {
        // A step whose coordinates fall in the SAME cell already claimed:
        // no correction needed -> null so the caller keeps its own
        // dead-reckoned (cell, pos) byte-identical (NOT collapsed to the
        // cell origin). This is the regression guard for the live-caught
        // out-parameter clobber bug. SourceCell 0xA9B40001 = cell index 1 =
        // cx=0, cy=0 -> local X,Y in [0,24). global (32450, 34565):
        //   lx = 32450 - 169*192 = 2 -> cx = 0; ly = 34565 - 180*192 = 5 -> cy = 0
        //   cell = 0*8 + 0 + 1 = 1 -> same as source.
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         32450f,
            stepGlobalY:         34565f,
            stepZ:               StepZ);

        Assert.Null(seam);
    }

    [Fact]
    public void Override_LiveCombatRepro_IntraLandblockApproach_CorrectsStaleCell()
    {
        // Exact live-verify repro (trkverify3, landblock 0xACB3 =
        // lbx=0xAC=172, lby=0xB3=179): during a melee approach the bot's
        // claimed cell froze at 0xACB30008 while its coordinates walked into
        // a different cell of the SAME landblock. stopPos=(143.90,97.18):
        //   global = (172*192 + 143.90, 179*192 + 97.18) = (33167.90, 34465.18)
        //   lx = 143.90 -> cx = 5; ly = 97.18 -> cy = 4
        //   cell = 5*8 + 4 + 1 = 45 = 0x2D
        // The frozen claim 0x08 != the coords' real cell 0x2D, so the server
        // adopted an inconsistent (cell, pos) and the in-combat StickToObject
        // move errored -> every swing cancelled, 0 damage, death. The
        // override must correct 0x08 -> 0x2D with the local pos unchanged.
        const uint frozenCell = 0xACB30008u;
        const float gx = 172 * 192 + 143.90f;
        const float gy = 179 * 192 + 97.18f;
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        frozenCell,
            stepGlobalX:         gx,
            stepGlobalY:         gy,
            stepZ:               27.79f);

        Assert.NotNull(seam);
        Assert.Equal(0xACB3002Du, seam!.Value.CellId);
        Assert.Equal(143.90f, seam.Value.LocalPos.X, 2);
        Assert.Equal(97.18f, seam.Value.LocalPos.Y, 2);
    }

    [Fact]
    public void Override_NeighborLocalPos_IsExpressedInNeighborLandblockFrame()
    {
        // Crossing WEST into landblock 0xA8B4 (lbx=0xA8=168): a step to
        // global X just below the 0xA9 origin (169*192 = 32448) lands in
        // lbx=168 with local X near the high end of that landblock.
        const float gx = 32447f;          // 168*192 = 32256; lx = 191
        const float gy = 34600f;          // lby = 180 -> 0xB4; ly = 40
        var seam = OutdoorSeamCell.TryDeriveSeamCell(
            followingIndoorPath: false,
            selfCellIsOutdoor:   true,
            lockedCellId:        SourceCell,
            stepGlobalX:         gx,
            stepGlobalY:         gy,
            stepZ:               StepZ);

        Assert.NotNull(seam);
        Assert.Equal(0xA8u, (seam!.Value.CellId >> 24) & 0xFFu); // lbx = 168
        Assert.Equal(0xB4u, (seam.Value.CellId >> 16) & 0xFFu);  // lby = 180
        Assert.Equal(191f, seam.Value.LocalPos.X, 3);            // 32447 - 168*192
        Assert.Equal(40f, seam.Value.LocalPos.Y, 3);             // 34600 - 180*192
    }

    // --- Canonicalize: UNCONDITIONAL frame-free (cell, local pos) derivation.
    //
    // STOP cannot rely on TryDeriveSeamCell's null="keep your own pos"
    // contract because its cached waypoint local pos may be in a frame the
    // server has since slid away from (after a seam crossing). Both review
    // models independently flagged this: when the server already advanced
    // stopCell to the seam's neighbor cell, TryDeriveSeamCell sees
    // derivedCell == lockedCellId and returns null, leaving the OLD-frame
    // local pos paired with the NEW cell -> a full-landblock (~192 m)
    // mis-projection. Canonicalize re-derives the local pos from the global
    // coordinates UNCONDITIONALLY, so the emitted (cell, pos) pair is always
    // internally consistent regardless of what cell the caller thought it was
    // in.

    [Fact]
    public void Canonicalize_SeamGlobal_DerivesNeighborCellAndLocalPos()
    {
        var canon = OutdoorSeamCell.Canonicalize(StepGX, StepGY, StepZ);

        Assert.NotNull(canon);
        Assert.Equal(ExpectedNeighborCell, canon!.Value.CellId);
        Assert.Equal(ExpectedLocalX, canon.Value.LocalPos.X, 3);
        Assert.Equal(ExpectedLocalY, canon.Value.LocalPos.Y, 3);
        Assert.Equal(StepZ, canon.Value.LocalPos.Z, 3);
    }

    [Fact]
    public void Canonicalize_CoordsInClaimedCell_StillReturnsConsistentPair()
    {
        // The review-flagged regression case. The bot's coordinates fall in
        // cell 0xA9B30018; if the caller naively kept a stale local pos
        // generated in the SOURCE landblock 0xA9B4's frame (X=52 there would
        // be X=52 in 0xB3 too, but consider a post-seam pos like X=193 in the
        // old frame) it would mis-project. Canonicalize ignores any prior
        // frame and returns the cell + the local pos derived straight from the
        // global coords — it NEVER returns null for in-range coords, so the
        // caller always gets a self-consistent pair to emit.
        var canon = OutdoorSeamCell.Canonicalize(StepGX, StepGY, StepZ);

        Assert.NotNull(canon);
        // local pos is exactly global minus the derived cell's landblock origin
        var lbx = (int)((canon!.Value.CellId >> 24) & 0xFFu);
        var lby = (int)((canon.Value.CellId >> 16) & 0xFFu);
        Assert.Equal(StepGX - lbx * 192f, canon.Value.LocalPos.X, 3);
        Assert.Equal(StepGY - lby * 192f, canon.Value.LocalPos.Y, 3);
    }

    [Fact]
    public void Canonicalize_SameLandblock_ReturnsSourceCellWithLocalPos()
    {
        // Coords that fall in the SOURCE cell 0xA9B40001 (cx=0, cy=0). Unlike
        // TryDeriveSeamCell (which returns null here to stay byte-identical),
        // Canonicalize returns the cell + local pos so STOP always has a
        // concrete consistent pair. global (32450, 34565):
        //   lx = 32450 - 169*192 = 2; ly = 34565 - 180*192 = 5; cell = 0x01.
        var canon = OutdoorSeamCell.Canonicalize(32450f, 34565f, StepZ);

        Assert.NotNull(canon);
        Assert.Equal(0xA9B40001u, canon!.Value.CellId);
        Assert.Equal(2f, canon.Value.LocalPos.X, 3);
        Assert.Equal(5f, canon.Value.LocalPos.Y, 3);
        Assert.Equal(StepZ, canon.Value.LocalPos.Z, 3);
    }

    // ---- AcCoords.IsOnFootSelfMove (on-foot travel vs teleport, for the
    //      death-location capture that must not record a respawn teleport) ----

    [Fact]
    public void IsOnFootSelfMove_SameLandblock_AnyDistance_IsOnFoot()
    {
        // A walk WITHIN one landblock is on-foot even corner-to-corner (the block
        // is only 192m); same high-16 bits => on-foot regardless of in-block distance.
        Assert.True(AcCoords.IsOnFootSelfMove(
            0xAAB50001u, new Vector3(1f, 1f, 0f),
            0xAAB50001u, new Vector3(180f, 180f, 0f), 48f));
    }

    [Fact]
    public void IsOnFootSelfMove_SameLandblock_IndoorCellChange_IsOnFoot()
    {
        // An indoor cell change WITHIN the same landblock (a door) is on-foot.
        Assert.True(AcCoords.IsOnFootSelfMove(
            0xAAB50100u, new Vector3(5f, 5f, 0f),
            0xAAB50105u, new Vector3(7f, 7f, 0f), 48f));
    }

    [Fact]
    public void IsOnFootSelfMove_AdjacentBlock_ShortStep_IsOnFoot()
    {
        // Outdoor step across a landblock seam, physically ~4m apart -> on-foot.
        // 0xAAB5 north edge (y=190) -> 0xAAB6 south edge (y=2): global Y differs by ~4m.
        Assert.True(AcCoords.IsOnFootSelfMove(
            0xAAB50005u, new Vector3(10f, 190f, 0f),
            0xAAB60005u, new Vector3(10f, 2f, 0f), 48f));
    }

    [Fact]
    public void IsOnFootSelfMove_DifferentBlock_FarJump_IsTeleport()
    {
        // Same axis but ~282m apart across the seam -> a teleport, NOT on-foot.
        Assert.False(AcCoords.IsOnFootSelfMove(
            0xAAB50005u, new Vector3(10f, 10f, 0f),
            0xAAB60005u, new Vector3(10f, 100f, 0f), 48f));
    }

    [Fact]
    public void IsOnFootSelfMove_CrossLandblockIndoorTransition_IsTeleport()
    {
        // An indoor cell in one landblock to an indoor cell in ANOTHER is a
        // door/portal, never an on-foot surface seam.
        Assert.False(AcCoords.IsOnFootSelfMove(
            0xAAB50100u, new Vector3(5f, 5f, 0f),
            0xAAB60100u, new Vector3(5f, 5f, 0f), 48f));
    }
}
