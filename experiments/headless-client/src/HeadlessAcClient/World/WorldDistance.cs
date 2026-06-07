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
    /// Squared HORIZONTAL (XY-plane) distance, in game units, between
    /// two AC world points — identical to <see cref="SquaredDistanceBetween"/>
    /// but with the Z (elevation) term dropped.
    ///
    /// Used for outdoor SELECTION/PERCEPTION distance. Outdoors the
    /// headless client never samples terrain height, so it floats the
    /// bot at its frozen spawn Z while the server tolerates the
    /// client-claimed outdoor Z (outdoor position is effectively
    /// client-authoritative). Monsters, by contrast, sit at their true
    /// surface Z — often tens of units below the bot's stale Z. A 3D
    /// distance therefore carries a SPURIOUS dz term that makes a
    /// monster standing right next to the bot (in XY) read as far away.
    /// For deciding WHICH object is near (LLM perception + autonomous
    /// picker ranking) the physically meaningful measure outdoors is
    /// horizontal distance; the motor APPROACH still uses full 3D so the
    /// in-approach Z-convergence (see MeleeApproachZ) can descend the bot
    /// onto the target's surface before a swing.
    /// </summary>
    public static float SquaredHorizontalDistanceBetween(
        uint cellA, Vector3 posA,
        uint cellB, Vector3 posB)
    {
        if (cellA == cellB)
        {
            var dxLocal = posA.X - posB.X;
            var dyLocal = posA.Y - posB.Y;
            return dxLocal * dxLocal + dyLocal * dyLocal;
        }

        // CAST TO INT BEFORE SUBTRACTING — see file header rationale.
        var lxA = (int)((cellA >> 24) & 0xFF);
        var lyA = (int)((cellA >> 16) & 0xFF);
        var lxB = (int)((cellB >> 24) & 0xFF);
        var lyB = (int)((cellB >> 16) & 0xFF);

        var dx = (lxA - lxB) * 192f + posA.X - posB.X;
        var dy = (lyA - lyB) * 192f + posA.Y - posB.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// A cell is OUTDOOR when its low-16-bit cell index is below the
    /// indoor (EnvCell) range that starts at 0x0100. Outdoor surface
    /// cells are 0x0001-0x0040.
    /// </summary>
    public static bool IsOutdoor(uint cellId) => (cellId & 0xFFFFu) < 0x100u;

    /// <summary>
    /// True when two cell-ids lie in the SAME landblock or in
    /// horizontally / vertically / diagonally ADJACENT landblocks
    /// (Chebyshev distance &lt;= 1 on the 8-bit LandblockX = (c &gt;&gt; 24) &amp; 0xFF
    /// and LandblockY = (c &gt;&gt; 16) &amp; 0xFF grid). Pure wire-coordinate
    /// geometry: it decodes only the landblock bytes and IGNORES the
    /// low-16-bit cell index, so two cells in the same landblock are
    /// always adjacent regardless of their cell indices.
    ///
    /// Used to decide whether a freshly-sighted entity is near enough to
    /// remember as a navigable target: a monster seen one landblock away
    /// is reachable on foot and feeds the cross-landblock sighting
    /// resolver, whereas a far/disconnected landblock (e.g. a stale
    /// ObjectCreate from a teleport destination) is not adjacent and is
    /// rejected.
    /// </summary>
    public static bool IsSameOrAdjacentLandblock(uint cellA, uint cellB)
    {
        var lxA = (int)((cellA >> 24) & 0xFF);
        var lyA = (int)((cellA >> 16) & 0xFF);
        var lxB = (int)((cellB >> 24) & 0xFF);
        var lyB = (int)((cellB >> 16) & 0xFF);
        return Math.Abs(lxA - lxB) <= 1 && Math.Abs(lyA - lyB) <= 1;
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

    /// <summary>
    /// Try to compute the squared SELECTION distance between two
    /// snapshots: HORIZONTAL (2D) when BOTH endpoints are outdoor,
    /// full 3D otherwise. This is the distance used to decide which
    /// object is "near" for LLM perception and autonomous picker
    /// ranking — see <see cref="SquaredHorizontalDistanceBetween"/> for
    /// why outdoor selection must ignore the (frozen, client-authoritative)
    /// self Z. Indoor cells have flat per-cell floors and a real Z that
    /// the server owns, so indoor (or mixed indoor/outdoor) selection
    /// stays 3D. Returns false (distance NaN) if either snapshot lacks a
    /// CellId, matching <see cref="TrySquaredDistance"/>.
    /// </summary>
    public static bool TrySelectionSquaredDistance(
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

        squaredDistance = IsOutdoor(cellA) && IsOutdoor(cellB)
            ? SquaredHorizontalDistanceBetween(cellA, a.Position, cellB, b.Position)
            : SquaredDistanceBetween(cellA, a.Position, cellB, b.Position);
        return true;
    }
}
