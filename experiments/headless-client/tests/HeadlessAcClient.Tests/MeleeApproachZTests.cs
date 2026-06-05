// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for MeleeApproachZ — pure vertical-convergence geometry for the
// outdoor melee approach. Outdoor self Z is client-authoritative and the
// walk-tick preserves it, so a bot approaching a target on elevated
// terrain stops at the right XY but the wrong Z and its 3D-gated melee
// never connects. These tests assert the convergence predicate is scoped
// (outdoor + Attack + above tolerance) and the Z step is clamped,
// non-overshooting, and direction-correct.

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class MeleeApproachZTests
{
    // AcCoords.IsIndoor(cellId) == (cellId & 0xFFFF) >= 0x100.
    private const uint OutdoorCellA = 0xA9B30001u; // low16 = 0x0001 -> outdoor
    private const uint OutdoorCellB = 0xA9B3001Cu; // low16 = 0x001C -> outdoor
    private const uint IndoorCell   = 0x86020100u; // low16 = 0x0100 -> indoor

    [Fact]
    public void ShouldConverge_OutdoorAttackAboveTolerance_True()
    {
        Assert.True(MeleeApproachZ.ShouldConverge(
            aimingAtWaypoint: false,
            isAttackGoal: true,
            selfCell: OutdoorCellA,
            targetCell: OutdoorCellB,
            selfZ: 94.0f,
            targetZ: 113.8f,
            toleranceUnits: 1.0f));
    }

    [Fact]
    public void ShouldConverge_WithinTolerance_False()
    {
        Assert.False(MeleeApproachZ.ShouldConverge(
            aimingAtWaypoint: false,
            isAttackGoal: true,
            selfCell: OutdoorCellA,
            targetCell: OutdoorCellB,
            selfZ: 94.0f,
            targetZ: 94.5f,
            toleranceUnits: 1.0f));
    }

    [Fact]
    public void ShouldConverge_NonAttackGoal_False()
    {
        Assert.False(MeleeApproachZ.ShouldConverge(
            aimingAtWaypoint: false,
            isAttackGoal: false,
            selfCell: OutdoorCellA,
            targetCell: OutdoorCellB,
            selfZ: 94.0f,
            targetZ: 113.8f,
            toleranceUnits: 1.0f));
    }

    [Fact]
    public void ShouldConverge_AimingAtWaypoint_False()
    {
        Assert.False(MeleeApproachZ.ShouldConverge(
            aimingAtWaypoint: true,
            isAttackGoal: true,
            selfCell: OutdoorCellA,
            targetCell: OutdoorCellB,
            selfZ: 94.0f,
            targetZ: 113.8f,
            toleranceUnits: 1.0f));
    }

    [Fact]
    public void ShouldConverge_IndoorSelf_False()
    {
        Assert.False(MeleeApproachZ.ShouldConverge(
            aimingAtWaypoint: false,
            isAttackGoal: true,
            selfCell: IndoorCell,
            targetCell: OutdoorCellB,
            selfZ: 0.0f,
            targetZ: 20.0f,
            toleranceUnits: 1.0f));
    }

    [Fact]
    public void ShouldConverge_IndoorTarget_False()
    {
        Assert.False(MeleeApproachZ.ShouldConverge(
            aimingAtWaypoint: false,
            isAttackGoal: true,
            selfCell: OutdoorCellA,
            targetCell: IndoorCell,
            selfZ: 0.0f,
            targetZ: 20.0f,
            toleranceUnits: 1.0f));
    }

    [Fact]
    public void StepToward_GapLargerThanMax_StepsExactlyMaxUpward()
    {
        // 19.8u gap, 1.25u/tick cap -> exactly +1.25u this tick.
        var newZ = MeleeApproachZ.StepToward(94.0f, 113.8f, 1.25f);
        Assert.Equal(95.25f, newZ, 3);
    }

    [Fact]
    public void StepToward_GapLargerThanMax_StepsExactlyMaxDownward()
    {
        // Target below self: step down by the cap, never overshoot.
        var newZ = MeleeApproachZ.StepToward(113.8f, 94.0f, 1.25f);
        Assert.Equal(112.55f, newZ, 3);
    }

    [Fact]
    public void StepToward_GapSmallerThanMax_LandsExactlyOnTarget()
    {
        var newZ = MeleeApproachZ.StepToward(94.0f, 94.4f, 1.25f);
        Assert.Equal(94.4f, newZ, 3);
    }

    [Fact]
    public void StepToward_NonPositiveMax_PreservesZ()
    {
        Assert.Equal(94.0f, MeleeApproachZ.StepToward(94.0f, 113.8f, 0.0f), 3);
        Assert.Equal(94.0f, MeleeApproachZ.StepToward(94.0f, 113.8f, -1.0f), 3);
    }

    [Fact]
    public void StepToward_RepeatedSteps_ConvergeWithoutOvershoot()
    {
        float z = 94.0f;
        const float target = 113.8f;
        for (int i = 0; i < 100 && System.MathF.Abs(target - z) > 1e-3f; i++)
        {
            float next = MeleeApproachZ.StepToward(z, target, 1.25f);
            // Monotonic toward target, never past it.
            Assert.True(next >= z);
            Assert.True(next <= target + 1e-3f);
            z = next;
        }
        Assert.Equal(target, z, 2);
    }
}
