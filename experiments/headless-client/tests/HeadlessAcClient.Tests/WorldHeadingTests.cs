// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for WorldHeading — cell-aware "face from A toward B" math.
//
// Coverage strategy:
//   - Pure DeltaXY math: same-cell, cross-cell (E/W/N/S), underflow
//     regression for west/south (rubber-duck on Phase 5f caught the
//     uint underflow class), Z is intentionally ignored.
//   - Sign-direction regression: DeltaXY(from, to) returns `to - from`
//     NOT `from - to`. WorldDistance is symmetric in this regard;
//     WorldHeading is not. Yaw inversion is the easiest bug to ship
//     here (rubber-duck on Phase 6 flagged this explicitly).
//   - YawFromDelta: all four cardinal directions; null on zero delta;
//     throw on NaN/Infinity.
//   - TryYawToTarget: null-CellId returns false; coincident XY returns
//     false; otherwise returns expected yaw.
//   - RotationFromYaw / ExtractYaw round-trip.

using System;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class WorldHeadingTests
{
    private const uint SelfGuid = 0x50000005;
    private const uint TargetA  = 0x50000010;

    private const uint CellLB1234_Cell1 = 0x12340001;
    private const uint CellLB1334_Cell1 = 0x13340001; // one east  of LB1234
    private const uint CellLB1134_Cell1 = 0x11340001; // one west  of LB1234
    private const uint CellLB1235_Cell1 = 0x12350001; // one north of LB1234
    private const uint CellLB1233_Cell1 = 0x12330001; // one south of LB1234

    private static void AssertVec2Eq(Vector2 expected, Vector2 actual, float tol = 1e-4f)
    {
        Assert.InRange(actual.X - expected.X, -tol, tol);
        Assert.InRange(actual.Y - expected.Y, -tol, tol);
    }

    private static void AssertYawEq(float expected, float actual, float tol = 1e-4f)
    {
        // Yaw angles are equivalent modulo 2*pi; +pi and -pi are the
        // same direction (south). atan2 may return either based on
        // the sign bit of negative-zero, so compute the absolute
        // angular distance the short way around the circle.
        var twoPi = 2.0 * Math.PI;
        var raw = Math.Abs(actual - expected) % twoPi;
        var dist = (float)Math.Min(raw, twoPi - raw);
        Assert.InRange(dist, 0f, tol);
    }

    // ---- DeltaXY: sign convention ----

    [Fact]
    public void DeltaXY_SameCell_PointsFromOriginToTarget()
    {
        var from = new Vector3(10, 20, 0);
        var to   = new Vector3(13, 24, 0);
        var d = WorldHeading.DeltaXY(CellLB1234_Cell1, from, CellLB1234_Cell1, to);
        // delta is target - origin -> (+3, +4)
        AssertVec2Eq(new Vector2(3, 4), d);
    }

    [Fact]
    public void DeltaXY_SameCell_NegatedWhenArgsSwapped()
    {
        var a = new Vector3(10, 20, 0);
        var b = new Vector3(13, 24, 0);
        var ab = WorldHeading.DeltaXY(CellLB1234_Cell1, a, CellLB1234_Cell1, b);
        var ba = WorldHeading.DeltaXY(CellLB1234_Cell1, b, CellLB1234_Cell1, a);
        AssertVec2Eq(new Vector2(-ab.X, -ab.Y), ba);
    }

    [Fact]
    public void DeltaXY_IgnoresZ()
    {
        var from = new Vector3(0, 0, 0);
        var to   = new Vector3(0, 0, 100);
        var d = WorldHeading.DeltaXY(CellLB1234_Cell1, from, CellLB1234_Cell1, to);
        AssertVec2Eq(Vector2.Zero, d);
    }

    // ---- DeltaXY: cross-cell ----

    [Fact]
    public void DeltaXY_CrossLandblock_East_PointsPositiveX()
    {
        // target one landblock east -> delta X = +192 (+ local diff).
        var from = new Vector3(10, 50, 0);
        var to   = new Vector3(10, 50, 0);
        var d = WorldHeading.DeltaXY(CellLB1234_Cell1, from, CellLB1334_Cell1, to);
        AssertVec2Eq(new Vector2(192, 0), d);
    }

    [Fact]
    public void DeltaXY_CrossLandblock_West_PointsNegativeX()
    {
        // target one landblock west -> delta X = -192.
        // This is the underflow regression: naive uint subtraction
        // would wrap (LX_to=0x11 minus LX_from=0x12 as uint = 0xFFFFFFFF)
        // and produce a +ve delta ~4 billion units.
        var from = new Vector3(10, 50, 0);
        var to   = new Vector3(10, 50, 0);
        var d = WorldHeading.DeltaXY(CellLB1234_Cell1, from, CellLB1134_Cell1, to);
        AssertVec2Eq(new Vector2(-192, 0), d);
    }

    [Fact]
    public void DeltaXY_CrossLandblock_North_PointsPositiveY()
    {
        var from = new Vector3(50, 10, 0);
        var to   = new Vector3(50, 10, 0);
        var d = WorldHeading.DeltaXY(CellLB1234_Cell1, from, CellLB1235_Cell1, to);
        AssertVec2Eq(new Vector2(0, 192), d);
    }

    [Fact]
    public void DeltaXY_CrossLandblock_South_PointsNegativeY()
    {
        // South underflow regression (mirror of west).
        var from = new Vector3(50, 10, 0);
        var to   = new Vector3(50, 10, 0);
        var d = WorldHeading.DeltaXY(CellLB1234_Cell1, from, CellLB1233_Cell1, to);
        AssertVec2Eq(new Vector2(0, -192), d);
    }

    [Fact]
    public void DeltaXY_CrossLandblock_CombinesLandblockAndLocalDelta()
    {
        var from = new Vector3(100, 50, 0);  // local
        var to   = new Vector3(120, 80, 0);  // local in NE landblock
        var d = WorldHeading.DeltaXY(CellLB1234_Cell1, from, CellLB1334_Cell1, to);
        // dx = +192 + (120 - 100) = +212
        // dy =   +0 + ( 80 -  50) = +30
        AssertVec2Eq(new Vector2(212, 30), d);
    }

    // ---- YawFromDelta: cardinal directions ----

    [Fact]
    public void YawFromDelta_North_IsZero()
    {
        var y = WorldHeading.YawFromDelta(new Vector2(0, 1));
        Assert.NotNull(y);
        AssertYawEq(0f, y!.Value);
    }

    [Fact]
    public void YawFromDelta_East_IsNegativePiOverTwo()
    {
        // east = +X. yaw = atan2(-(+1), 0) = atan2(-1, 0) = -pi/2.
        var y = WorldHeading.YawFromDelta(new Vector2(1, 0));
        Assert.NotNull(y);
        AssertYawEq(-(float)Math.PI / 2, y!.Value);
    }

    [Fact]
    public void YawFromDelta_West_IsPositivePiOverTwo()
    {
        // west = -X. yaw = atan2(-(-1), 0) = atan2(1, 0) = +pi/2.
        var y = WorldHeading.YawFromDelta(new Vector2(-1, 0));
        Assert.NotNull(y);
        AssertYawEq((float)Math.PI / 2, y!.Value);
    }

    [Fact]
    public void YawFromDelta_South_IsPi()
    {
        var y = WorldHeading.YawFromDelta(new Vector2(0, -1));
        Assert.NotNull(y);
        // atan2(0, -1) returns +pi.
        AssertYawEq((float)Math.PI, y!.Value);
    }

    [Fact]
    public void YawFromDelta_ZeroDelta_ReturnsNull()
    {
        var y = WorldHeading.YawFromDelta(Vector2.Zero);
        Assert.Null(y);
    }

    [Fact]
    public void YawFromDelta_NaN_Throws()
        => Assert.Throws<ArgumentException>(
            () => WorldHeading.YawFromDelta(new Vector2(float.NaN, 0f)));

    [Fact]
    public void YawFromDelta_Infinity_Throws()
        => Assert.Throws<ArgumentException>(
            () => WorldHeading.YawFromDelta(new Vector2(float.PositiveInfinity, 0f)));

    // ---- TryYawToTarget ----

    [Fact]
    public void TryYawToTarget_BothHaveCellId_ReturnsExpectedYaw()
    {
        var self   = BuildSnap(SelfGuid, CellLB1234_Cell1, new Vector3(0, 0, 0));
        var target = BuildSnap(TargetA,  CellLB1234_Cell1, new Vector3(0, 5, 0));  // due north
        Assert.True(WorldHeading.TryYawToTarget(self, target, out var yaw));
        AssertYawEq(0f, yaw);
    }

    [Fact]
    public void TryYawToTarget_TargetEastOfSelf_NegativeYaw()
    {
        var self   = BuildSnap(SelfGuid, CellLB1234_Cell1, new Vector3(0, 0, 0));
        var target = BuildSnap(TargetA,  CellLB1234_Cell1, new Vector3(5, 0, 0));
        Assert.True(WorldHeading.TryYawToTarget(self, target, out var yaw));
        AssertYawEq(-(float)Math.PI / 2, yaw);
    }

    [Fact]
    public void TryYawToTarget_SelfHasNoCellId_ReturnsFalse()
    {
        var self   = new WorldObjectSnapshot(SelfGuid);                          // no CellId
        var target = BuildSnap(TargetA, CellLB1234_Cell1, new Vector3(5, 0, 0));
        Assert.False(WorldHeading.TryYawToTarget(self, target, out var yaw));
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void TryYawToTarget_TargetHasNoCellId_ReturnsFalse()
    {
        var self   = BuildSnap(SelfGuid, CellLB1234_Cell1, new Vector3(0, 0, 0));
        var target = new WorldObjectSnapshot(TargetA);                            // no CellId
        Assert.False(WorldHeading.TryYawToTarget(self, target, out var yaw));
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void TryYawToTarget_CoincidentXY_ReturnsFalse()
    {
        var self   = BuildSnap(SelfGuid, CellLB1234_Cell1, new Vector3(10, 20, 0));
        var target = BuildSnap(TargetA,  CellLB1234_Cell1, new Vector3(10, 20, 5)); // same XY, different Z
        Assert.False(WorldHeading.TryYawToTarget(self, target, out var yaw));
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void TryYawToTarget_CrossLandblockEast_NegativeYaw()
    {
        // Cross-landblock east -> still "east", still yaw = -pi/2.
        var self   = BuildSnap(SelfGuid, CellLB1234_Cell1, new Vector3(50, 50, 0));
        var target = BuildSnap(TargetA,  CellLB1334_Cell1, new Vector3(50, 50, 0));
        Assert.True(WorldHeading.TryYawToTarget(self, target, out var yaw));
        AssertYawEq(-(float)Math.PI / 2, yaw);
    }

    [Fact]
    public void TryYawToTarget_NullSelf_Throws()
    {
        var target = BuildSnap(TargetA, CellLB1234_Cell1, Vector3.Zero);
        Assert.Throws<ArgumentNullException>(
            () => WorldHeading.TryYawToTarget(null!, target, out _));
    }

    [Fact]
    public void TryYawToTarget_NullTarget_Throws()
    {
        var self = BuildSnap(SelfGuid, CellLB1234_Cell1, Vector3.Zero);
        Assert.Throws<ArgumentNullException>(
            () => WorldHeading.TryYawToTarget(self, null!, out _));
    }

    // ---- RotationFromYaw / ExtractYaw round-trip ----

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    [InlineData(1.5707963f)]   // pi/2
    [InlineData(-1.5707963f)]
    public void RotationFromYaw_RoundTripsViaExtractYaw(float yaw)
    {
        var q = WorldHeading.RotationFromYaw(yaw);
        var extracted = WorldHeading.ExtractYaw(q);
        AssertYawEq(yaw, extracted);
    }

    [Fact]
    public void RotationFromYaw_Identity_AtZero()
    {
        var q = WorldHeading.RotationFromYaw(0f);
        Assert.Equal(1f, q.W, 5);
        Assert.Equal(0f, q.X, 5);
        Assert.Equal(0f, q.Y, 5);
        Assert.Equal(0f, q.Z, 5);
    }

    [Fact]
    public void RotationFromYaw_PiOverTwo_PureZ()
    {
        // For yaw = pi/2: q.W = cos(pi/4), q.Z = sin(pi/4), X=Y=0.
        var q = WorldHeading.RotationFromYaw((float)Math.PI / 2);
        Assert.InRange(q.W, 0.707f, 0.708f);
        Assert.Equal(0f, q.X, 5);
        Assert.Equal(0f, q.Y, 5);
        Assert.InRange(q.Z, 0.707f, 0.708f);
    }

    // ---- helpers ----
    // Minimal snapshot builder. We mutate internal-set properties
    // directly (test project has InternalsVisibleTo).

    private static WorldObjectSnapshot BuildSnap(uint guid, uint cellId, Vector3 pos)
    {
        var s = new WorldObjectSnapshot(guid)
        {
            CellId   = cellId,
            Position = pos,
            Rotation = Quaternion.Identity,
        };
        return s;
    }
}
