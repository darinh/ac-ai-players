// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for OutdoorLocalAvoidance — pure geometry for reactive outdoor
// obstacle avoidance. When a straight-line outdoor walk is blocked, the
// motor asks this helper for a bounded sequence of short detour waypoints
// at alternate local headings. These tests assert the offset sequence is
// bounded + deterministic, every detour is forward-progressing, the
// distance is honored, and degenerate inputs decline cleanly.

using System;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class OutdoorLocalAvoidanceTests
{
    [Fact]
    public void MaxAttempts_IsBounded_AndMatchesOffsetCount()
    {
        Assert.Equal(4, OutdoorLocalAvoidance.MaxAttempts);
        Assert.Equal(OutdoorLocalAvoidance.DetourOffsetsRad.Length,
                     OutdoorLocalAvoidance.MaxAttempts);
    }

    [Fact]
    public void TryChooseDetour_AttemptOutOfRange_ReturnsFalse()
    {
        Assert.False(OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 10f, 0f, attemptIndex: -1, detourDistance: 8f, out _, out _));
        Assert.False(OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 10f, 0f,
            attemptIndex: OutdoorLocalAvoidance.MaxAttempts,
            detourDistance: 8f, out _, out _));
    }

    [Fact]
    public void TryChooseDetour_SelfEqualsTarget_ReturnsFalse()
    {
        // No meaningful bearing to offset from -> decline.
        Assert.False(OutdoorLocalAvoidance.TryChooseDetour(
            5f, 5f, 5f, 5f, attemptIndex: 0, detourDistance: 8f, out _, out _));
    }

    [Fact]
    public void TryChooseDetour_HonorsDistance()
    {
        // Target due +X; attempt 0 is +45°. Waypoint must be exactly
        // detourDistance from self.
        const float dist = 8f;
        Assert.True(OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 100f, 0f, attemptIndex: 0, detourDistance: dist,
            out var gx, out var gy));
        var len = MathF.Sqrt(gx * gx + gy * gy);
        Assert.Equal(dist, len, 3);
    }

    [Fact]
    public void TryChooseDetour_NonPositiveDistance_FallsBackToDefault()
    {
        Assert.True(OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 100f, 0f, attemptIndex: 0, detourDistance: 0f,
            out var gx, out var gy));
        var len = MathF.Sqrt(gx * gx + gy * gy);
        Assert.Equal(OutdoorLocalAvoidance.DefaultDetourDistance, len, 3);
    }

    [Fact]
    public void TryChooseDetour_AllAttempts_AreForwardProgressing()
    {
        // Arbitrary self + target; every built-in detour must keep
        // dot(detourDir, targetDir) >= 0 (no backward sidesteps).
        const float sx = 12f, sy = -7f, tx = 60f, ty = 33f;
        for (int i = 0; i < OutdoorLocalAvoidance.MaxAttempts; i++)
        {
            Assert.True(OutdoorLocalAvoidance.TryChooseDetour(
                sx, sy, tx, ty, i, 8f, out var gx, out var gy),
                $"attempt {i} should produce a waypoint");
            Assert.True(OutdoorLocalAvoidance.IsForwardProgress(
                sx, sy, tx, ty, gx, gy),
                $"attempt {i} must keep non-negative forward progress");
        }
    }

    [Fact]
    public void TryChooseDetour_Plus45_RotatesHeadingCorrectly()
    {
        // Target due +X (bearing 0). +45° detour bearing -> equal X and Y
        // components, both positive.
        Assert.True(OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 100f, 0f, attemptIndex: 0, detourDistance: 10f,
            out var gx, out var gy));
        Assert.True(gx > 0f);
        Assert.True(gy > 0f);
        Assert.Equal(gx, gy, 3); // 45° -> cos == sin
    }

    [Fact]
    public void TryChooseDetour_Minus45_MirrorsPlus45()
    {
        OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 100f, 0f, 0, 10f, out var gxP, out var gyP);
        OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 100f, 0f, 1, 10f, out var gxM, out var gyM);
        // +45° and -45° share the same forward (X) component and opposite
        // lateral (Y) component.
        Assert.Equal(gxP, gxM, 3);
        Assert.Equal(gyP, -gyM, 3);
    }

    [Fact]
    public void TryChooseDetour_Plus90_IsPurelyLateral()
    {
        // Target due +X; +90° detour -> straight +Y, zero forward X.
        Assert.True(OutdoorLocalAvoidance.TryChooseDetour(
            0f, 0f, 100f, 0f, attemptIndex: 2, detourDistance: 10f,
            out var gx, out var gy));
        Assert.Equal(0f, gx, 3);
        Assert.True(gy > 0f);
        // dot == 0 is still allowed (non-negative).
        Assert.True(OutdoorLocalAvoidance.IsForwardProgress(
            0f, 0f, 100f, 0f, gx, gy));
    }

    [Fact]
    public void IsForwardProgress_BackwardDirection_ReturnsFalse()
    {
        // A detour pointing away from the target (behind self) is rejected.
        Assert.False(OutdoorLocalAvoidance.IsForwardProgress(
            0f, 0f, 100f, 0f, detourGx: -10f, detourGy: 0f));
    }
}
