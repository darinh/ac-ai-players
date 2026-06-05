using System.Collections.Generic;
using System.Numerics;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Pure geometry for the outdoor-frontier seam-cell AP override.
///
/// A first-time outdoor frontier probe rasterizes its straight walk
/// segment into a path-cell set but leaves the indoor waypoint path
/// null, so the motor's indoor cell-advance never runs. When the
/// straight line crosses an outdoor LANDBLOCK seam the AutonomousPosition
/// packet keeps claiming the SOURCE landblock cell while the bot's
/// position overshoots into the neighbor landblock's coordinate range;
/// the server rejects the inconsistent (cell, pos) pair and the bot
/// freezes at the seam.
///
/// This helper derives the AutonomousPosition cell from the step's
/// GLOBAL coordinates and, when that cell sits in a DIFFERENT outdoor
/// landblock that already belongs to the probe's own rasterized
/// path-cell set, returns the neighbor (cellId, neighbor-local pos) so
/// the packet is internally consistent. It is pure locomotion geometry:
/// it never chooses a target, never inspects object types, and is bounded
/// by the caller-supplied path-cell set (the probe's own chosen route).
/// </summary>
internal static class OutdoorFrontierSeamCell
{
    /// <summary>
    /// Try to derive a neighbor-landblock AP cell + local position for an
    /// outdoor frontier probe whose next step crosses a landblock seam.
    /// </summary>
    /// <param name="isOutdoorFrontierProbe">The active motion is an anonymous outdoor frontier probe.</param>
    /// <param name="hasIndoorWaypointPath">True when an indoor waypoint path is active (the indoor cell-advance owns the AP cell — do nothing here).</param>
    /// <param name="selfCellIsIndoor">True when the bot's current cell is indoor.</param>
    /// <param name="pathCells">The probe's own rasterized path-cell set; the derived cell must be a member.</param>
    /// <param name="lockedCellId">The currently claimed (source landblock) cell.</param>
    /// <param name="stepGlobalX">Global X of the step's destination position.</param>
    /// <param name="stepGlobalY">Global Y of the step's destination position.</param>
    /// <param name="stepZ">Z to carry into the neighbor-local position (unchanged).</param>
    /// <param name="apCellId">On success, the neighbor landblock cell to claim; otherwise <paramref name="lockedCellId"/>.</param>
    /// <param name="apLocalPos">On success, the neighbor-landblock-local position; otherwise default.</param>
    /// <returns>True when an override was derived; false to keep the source cell + local position.</returns>
    public static bool TryDeriveSeamCell(
        bool isOutdoorFrontierProbe,
        bool hasIndoorWaypointPath,
        bool selfCellIsIndoor,
        IReadOnlySet<uint>? pathCells,
        uint lockedCellId,
        float stepGlobalX,
        float stepGlobalY,
        float stepZ,
        out uint apCellId,
        out Vector3 apLocalPos)
    {
        apCellId = lockedCellId;
        apLocalPos = default;

        if (!isOutdoorFrontierProbe || hasIndoorWaypointPath || selfCellIsIndoor || pathCells is null)
            return false;

        var derivedCell = AcCoords.OutdoorCellIdFromGlobal(stepGlobalX, stepGlobalY);
        if (derivedCell == 0u)
            return false;

        // Only override on an actual landblock seam crossing; same-landblock
        // outdoor steps keep the source cell so they are byte-identical.
        if ((derivedCell & 0xFFFF0000u) == (lockedCellId & 0xFFFF0000u))
            return false;

        // The neighbor cell must already be part of the probe's own route.
        if (!pathCells.Contains(derivedCell))
            return false;

        var lbx = (int)((derivedCell >> 24) & 0xFFu);
        var lby = (int)((derivedCell >> 16) & 0xFFu);
        apCellId = derivedCell;
        apLocalPos = new Vector3(
            stepGlobalX - lbx * AcCoords.BlockLength,
            stepGlobalY - lby * AcCoords.BlockLength,
            stepZ);
        return true;
    }
}
