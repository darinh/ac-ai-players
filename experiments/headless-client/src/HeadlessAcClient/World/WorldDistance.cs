// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldDistance — cell-aware spatial math for AC's landblock+cell
// coordinate system. Centralizes the squared-distance formula so
// every spatial query (WithinRadius, NearestN, etc.) uses the same
// rule and any future refinement (e.g. proper indoor handling)
// happens in one place.
//
// AC coordinate model:
//   - LandblockId is a 32-bit cell-id. Top byte is LandblockX
//     (0-254), next byte is LandblockY (0-254), low 16 bits are
//     the cell index within the landblock (0x0001-0x0040 outdoor
//     surface; 0x0100+ indoor).
//   - A landblock is 192 game units per side.
//   - Position.X / Position.Y are LOCAL to the landblock cell
//     (range [0,192) for outdoor cells).
//   - Position.Z is global elevation (no per-landblock offset).
//
// Algorithm port: ACE Source/ACE.Entity/Position.cs:395
// (`SquaredDistanceTo`). Two notable deviations from a naive port:
//
//   1. Landblock byte components are cast to `int` BEFORE subtraction.
//      Naively writing `(LX_a - LX_b) * 192` with `byte` operands
//      promotes to `int` and is safe, but with `uint` operands it
//      wraps on underflow and produces a galactically wrong delta
//      when the target is west/south of the origin. The cast is
//      explicit here so future edits don't reintroduce the bug.
//   2. Z-axis uses pure local delta (no landblock-scaling), matching
//      ACE. Z is global in the AC coordinate system.

using System;
using System.Numerics;

namespace HeadlessAcClient.World;

internal static class WorldDistance
{
    /// <summary>
    /// Squared distance, in game units, between two AC world points.
    /// "Squared" because spatial queries typically compare against
    /// a squared radius (no sqrt needed).
    /// Same-cell fast path uses pure Vector3 delta; otherwise
    /// landblock-scaled cross-cell math.
    /// </summary>
    public static float SquaredDistanceBetween(
        uint cellA, Vector3 posA,
        uint cellB, Vector3 posB)
    {
        if (cellA == cellB)
        {
            var dxLocal = posA.X - posB.X;
            var dyLocal = posA.Y - posB.Y;
            var dzLocal = posA.Z - posB.Z;
            return dxLocal * dxLocal + dyLocal * dyLocal + dzLocal * dzLocal;
        }

        // CAST TO INT BEFORE SUBTRACTING — see file header rationale.
        var lxA = (int)((cellA >> 24) & 0xFF);
        var lyA = (int)((cellA >> 16) & 0xFF);
        var lxB = (int)((cellB >> 24) & 0xFF);
        var lyB = (int)((cellB >> 16) & 0xFF);

        var dx = (lxA - lxB) * 192f + posA.X - posB.X;
        var dy = (lyA - lyB) * 192f + posA.Y - posB.Y;
        var dz = posA.Z - posB.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    /// Try to compute squared distance between two snapshots. Returns
    /// false (and sets distance to NaN) if either snapshot lacks a
    /// CellId (no spatial state yet — pre-ObjectCreate observation).
    /// Self-distance is 0.
    /// </summary>
    public static bool TrySquaredDistance(
        WorldObjectSnapshot a,
        WorldObjectSnapshot b,
        out float squaredDistance)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));

        if (a.CellId is not uint cellA || b.CellId is not uint cellB)
        {
            squaredDistance = float.NaN;
            return false;
        }

        squaredDistance = SquaredDistanceBetween(cellA, a.Position, cellB, b.Position);
        return true;
    }
}
