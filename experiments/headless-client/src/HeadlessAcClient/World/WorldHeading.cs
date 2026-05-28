// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldHeading — cell-aware "face from A toward B" math for AC's
// landblock+cell coordinate system. Companion to WorldDistance.
//
// AC heading convention (ported from Source/ACE.Entity/Position.cs):
//
//   Line 63 of Position.cs:
//       Rotation = Quaternion.CreateFromYawPitchRoll(0, 0,
//                      (float)Math.Atan2(-dir.X, dir.Y));
//
//   Lines 101-105 of Position.cs (inverse — heading -> unit vector):
//       heading = Math.Atan2(x, y);
//       dx = -Math.Sin(heading) * dist;
//       dy =  Math.Cos(heading) * dist;
//
// What that means concretely (AC's world is Z-up, +Y = north, +X = east):
//   - heading = 0    => facing +Y (north)
//   - heading = +PI/2 => facing -X (west)   [right-hand rotation around +Z]
//   - heading = -PI/2 => facing +X (east)
//   - heading = +PI  => facing -Y (south)
//   - "face from A toward B" yaw = atan2(-(B.X-A.X), (B.Y-A.Y))
//                                = atan2(-dx, dy)
//
// Critical sign distinction vs WorldDistance:
//   - WorldDistance.SquaredDistanceBetween(a, b) is symmetric — it
//     squares the deltas, so sign doesn't matter.
//   - WorldHeading needs the delta to POINT FROM origin TO target.
//     DeltaXY is therefore `to - from`, NOT `from - to`. This is the
//     easiest place to accidentally invert yaw (per rubber-duck on
//     Phase 6); the test suite asserts the sign convention.
//
// Cross-cell delta uses the same int-cast underflow protection as
// WorldDistance: cast landblock byte components to `int` BEFORE
// subtracting, otherwise west/south targets produce ~4-billion-unit
// deltas because `uint` underflow wraps. See WorldDistance.cs for
// the full rationale.

using System;
using System.Numerics;

namespace HeadlessAcClient.World;

internal static class WorldHeading
{
    /// <summary>
    /// Cell-aware 2D delta vector pointing FROM the "from" point
    /// TO the "to" point, in game units (no Z component — yaw math
    /// is XY-only). Same-cell fast path skips landblock math.
    /// </summary>
    public static Vector2 DeltaXY(
        uint fromCell, Vector3 fromPos,
        uint toCell,   Vector3 toPos)
    {
        if (fromCell == toCell)
        {
            return new Vector2(toPos.X - fromPos.X, toPos.Y - fromPos.Y);
        }

        // CAST TO INT BEFORE SUBTRACTING — see file header rationale.
        var lxFrom = (int)((fromCell >> 24) & 0xFF);
        var lyFrom = (int)((fromCell >> 16) & 0xFF);
        var lxTo   = (int)((toCell   >> 24) & 0xFF);
        var lyTo   = (int)((toCell   >> 16) & 0xFF);

        var dx = (lxTo - lxFrom) * 192f + toPos.X - fromPos.X;
        var dy = (lyTo - lyFrom) * 192f + toPos.Y - fromPos.Y;
        return new Vector2(dx, dy);
    }

    /// <summary>
    /// Yaw angle (radians) for the supplied XY delta, following AC's
    /// convention: <c>atan2(-dx, dy)</c>. Returns <see langword="null"/>
    /// if the delta has zero magnitude (no meaningful direction).
    /// Throws <see cref="ArgumentException"/> for NaN/Infinity input —
    /// callers should not be reaching here with garbage.
    /// </summary>
    public static float? YawFromDelta(Vector2 delta)
    {
        if (float.IsNaN(delta.X) || float.IsNaN(delta.Y) ||
            float.IsInfinity(delta.X) || float.IsInfinity(delta.Y))
        {
            throw new ArgumentException(
                $"delta must be finite (got {delta})", nameof(delta));
        }

        // Zero-length delta has no direction. Caller should keep
        // existing rotation rather than snap to an arbitrary yaw.
        if (delta.X == 0f && delta.Y == 0f) return null;

        return (float)Math.Atan2(-delta.X, delta.Y);
    }

    /// <summary>
    /// Compute the yaw that points <paramref name="self"/> at
    /// <paramref name="target"/>. Returns <see langword="false"/>
    /// if either snapshot lacks a CellId, or if the two are at the
    /// same XY point (no meaningful direction).
    /// </summary>
    public static bool TryYawToTarget(
        WorldObjectSnapshot self,
        WorldObjectSnapshot target,
        out float yaw)
    {
        if (self is null)   throw new ArgumentNullException(nameof(self));
        if (target is null) throw new ArgumentNullException(nameof(target));

        if (self.CellId is not uint selfCell ||
            target.CellId is not uint targetCell)
        {
            yaw = 0f;
            return false;
        }

        var delta = DeltaXY(selfCell, self.Position, targetCell, target.Position);
        var maybeYaw = YawFromDelta(delta);
        if (maybeYaw is not float y)
        {
            yaw = 0f;
            return false;
        }

        yaw = y;
        return true;
    }

    /// <summary>
    /// Build a quaternion representing a pure yaw rotation around
    /// the Z axis. Matches ACE's
    /// <c>Quaternion.CreateFromYawPitchRoll(0, 0, yaw)</c> usage —
    /// AC characters only rotate around vertical.
    /// </summary>
    public static Quaternion RotationFromYaw(float yaw)
        => Quaternion.CreateFromYawPitchRoll(0f, 0f, yaw);

    /// <summary>
    /// Extract the yaw (radians) from a pure-Z rotation quaternion.
    /// Convenience for tests / round-trip checks. Inverse of
    /// <see cref="RotationFromYaw"/> for quaternions that lie in the
    /// XZ-Z plane only (i.e. only the Z and W components are
    /// significant — pitch/roll are zero, which is always true for
    /// AC characters).
    /// </summary>
    public static float ExtractYaw(Quaternion q)
    {
        // For a pure Z-axis rotation by angle theta:
        //   q.W = cos(theta/2), q.Z = sin(theta/2)
        // -> theta = 2 * atan2(q.Z, q.W)
        return 2f * (float)Math.Atan2(q.Z, q.W);
    }
}
