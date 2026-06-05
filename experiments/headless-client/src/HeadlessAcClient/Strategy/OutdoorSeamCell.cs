using System.Numerics;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Pure geometry for the outdoor seam-cell AutonomousPosition override.
///
/// Outdoor self positions are LANDBLOCK-relative (X,Y in 0..192). The
/// motor dead-reckons a step as newPos = self + step in the locked
/// cell's landblock frame, but the AutonomousPosition packet claims a
/// fixed source cell (the motion lock) for an outdoor approach. As the
/// bot walks toward a target in or near a NEIGHBOR landblock, newPos.X
/// or newPos.Y leaves the 0..192 range while the claimed cell stays in
/// the source landblock. The (cell, pos) pair is then internally
/// inconsistent: the server's PhysicsObj tries to transition from the
/// claimed (wrong) landblock cell to the cell the coordinates actually
/// fall in — frequently two landblocks away — the transition FAILS, and
/// the server broadcasts the bot's position as the cell origin (0,0,0),
/// which the client then adopts and re-sends in a self-reinforcing
/// collapse. The bot freezes at the seam and can never close distance on
/// an outdoor target.
///
/// This helper derives the AutonomousPosition cell from the step's GLOBAL
/// coordinates and, when that cell sits in a DIFFERENT outdoor landblock
/// than the claimed source cell, returns the neighbor (cellId,
/// neighbor-landblock-local pos) so the packet is internally consistent
/// and each per-tick transition is at most one adjacent cell. It is pure
/// locomotion geometry: it never chooses a target, never inspects object
/// types, and only re-expresses a position the motor has already decided
/// to step to in the coordinate frame of the cell that contains it.
///
/// Intra-landblock cell crossings are intentionally left unchanged
/// (byte-identical): the server re-derives the structural cell from the
/// AP coordinates WITHIN a landblock (update_object_server), so only a
/// wrong LANDBLOCK — which the server cannot re-derive from a stale
/// claim — needs correcting here.
/// </summary>
internal static class OutdoorSeamCell
{
    /// <summary>The neighbor-landblock cell + local position to claim at a seam crossing.</summary>
    internal readonly record struct SeamCell(uint CellId, Vector3 LocalPos);

    /// <summary>
    /// Try to derive a neighbor-landblock AP cell + local position for an
    /// outdoor walk step that crosses a landblock seam.
    ///
    /// Returns a <see cref="SeamCell"/> only when the step crosses into a
    /// DIFFERENT outdoor landblock; returns null otherwise. A null result
    /// means the caller must keep its own (locked cell, dead-reckoned local
    /// position) unchanged — there is deliberately no out-parameter to
    /// clobber, so a non-seam tick can never collapse the AP position to
    /// the cell origin.
    /// </summary>
    /// <param name="followingIndoorPath">True when the indoor multi-cell cell-advance owns the AP cell (do nothing here).</param>
    /// <param name="selfCellIsOutdoor">True when the bot's current cell is outdoor.</param>
    /// <param name="lockedCellId">The currently claimed (source landblock) cell.</param>
    /// <param name="stepGlobalX">Global X of the step's destination position.</param>
    /// <param name="stepGlobalY">Global Y of the step's destination position.</param>
    /// <param name="stepZ">Z to carry into the neighbor-local position (unchanged).</param>
    /// <returns>The neighbor (cell, local pos) on a seam crossing; otherwise null.</returns>
    public static SeamCell? TryDeriveSeamCell(
        bool followingIndoorPath,
        bool selfCellIsOutdoor,
        uint lockedCellId,
        float stepGlobalX,
        float stepGlobalY,
        float stepZ)
    {
        // The indoor cell-advance owns the AP cell while following a
        // planned multi-cell indoor path; and an indoor self cell is
        // client-authoritative (no coordinate re-derivation), so the
        // outdoor seam logic never applies there.
        if (followingIndoorPath || !selfCellIsOutdoor)
            return null;

        var derivedCell = AcCoords.OutdoorCellIdFromGlobal(stepGlobalX, stepGlobalY);
        if (derivedCell == 0u)
            return null;

        // Only override on an actual landblock seam crossing; same-landblock
        // outdoor steps keep the source cell so they are byte-identical (the
        // server re-derives the intra-landblock cell from the AP coords).
        if ((derivedCell & 0xFFFF0000u) == (lockedCellId & 0xFFFF0000u))
            return null;

        var lbx = (int)((derivedCell >> 24) & 0xFFu);
        var lby = (int)((derivedCell >> 16) & 0xFFu);
        return new SeamCell(
            derivedCell,
            new Vector3(
                stepGlobalX - lbx * AcCoords.BlockLength,
                stepGlobalY - lby * AcCoords.BlockLength,
                stepZ));
    }
}
