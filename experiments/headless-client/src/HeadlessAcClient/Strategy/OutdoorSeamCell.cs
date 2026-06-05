using System.Numerics;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Pure geometry for the outdoor AutonomousPosition cell-consistency
/// override (cell must match the position's coordinates).
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
/// coordinates and, when that cell differs from the claimed source cell,
/// returns the coordinates' real (cellId, cell-landblock-local pos) so the
/// packet is internally consistent and each per-tick transition matches
/// where the bot actually stands. It is pure locomotion geometry: it never
/// chooses a target, never inspects object types, and only re-expresses a
/// position the motor has already decided to step to in the coordinate
/// frame of the cell that contains it.
///
/// INTRA-landblock cell crossings are corrected too (NOT left to the
/// server). An earlier version assumed the server re-derives the
/// structural cell from the AP coordinates WITHIN a landblock
/// (update_object_server), so it only corrected wrong LANDBLOCKS. Live
/// combat evidence disproved that: a bot that walked across several
/// intra-landblock 24 m cells during a melee approach kept claiming its
/// frozen motion-start cell (e.g. cell 0x08) while its coordinates fell in
/// a different cell (0x2D) of the SAME landblock; the server ADOPTED the
/// stale (cell, pos) pair (it did NOT re-derive), and the server-side
/// StickToObject move during combat then completed with an error
/// (OnMoveComplete(error) -> HandleActionCancelAttack), cancelling every
/// melee swing and dropping the bot out of combat stance — 0 damage, then
/// death. So the invariant is now unconditional: an outbound outdoor
/// position must claim the cell its own coordinates derive to, whether
/// that cell is in the same landblock or a neighbor.
/// </summary>
internal static class OutdoorSeamCell
{
    /// <summary>The corrected cell + cell-landblock-local position to claim.</summary>
    internal readonly record struct SeamCell(uint CellId, Vector3 LocalPos);

    /// <summary>
    /// Try to derive the consistent outdoor AP cell + local position for a
    /// walk step, given the step's GLOBAL coordinates.
    ///
    /// Returns a <see cref="SeamCell"/> only when the coordinates' derived
    /// cell DIFFERS from the claimed source cell (intra-landblock OR a
    /// landblock seam); returns null otherwise. A null result means the
    /// caller must keep its own (locked cell, dead-reckoned local position)
    /// unchanged — there is deliberately no out-parameter to clobber, so a
    /// no-change tick can never collapse the AP position to the cell origin.
    /// </summary>
    /// <param name="followingIndoorPath">True when the indoor multi-cell cell-advance owns the AP cell (do nothing here).</param>
    /// <param name="selfCellIsOutdoor">True when the bot's current cell is outdoor.</param>
    /// <param name="lockedCellId">The currently claimed (source) cell.</param>
    /// <param name="stepGlobalX">Global X of the step's destination position.</param>
    /// <param name="stepGlobalY">Global Y of the step's destination position.</param>
    /// <param name="stepZ">Z to carry into the cell-local position (unchanged).</param>
    /// <returns>The corrected (cell, local pos) when it differs from the source; otherwise null.</returns>
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
        // outdoor cell-consistency logic never applies there.
        if (followingIndoorPath || !selfCellIsOutdoor)
            return null;

        var derivedCell = AcCoords.OutdoorCellIdFromGlobal(stepGlobalX, stepGlobalY);
        if (derivedCell == 0u)
            return null;

        // No correction needed when the coordinates already fall in the
        // claimed cell — return null so the caller keeps its own
        // dead-reckoned (cell, pos) byte-identical (and emits no log spam).
        if (derivedCell == lockedCellId)
            return null;

        return Canonicalize(stepGlobalX, stepGlobalY, stepZ);
    }

    /// <summary>
    /// UNCONDITIONALLY derive the consistent outdoor (cell, cell-landblock-
    /// local position) pair for a set of GLOBAL coordinates — i.e. the cell
    /// the coordinates actually fall in plus that cell's landblock-local
    /// position. Unlike <see cref="TryDeriveSeamCell"/> this never returns
    /// null for outdoor coordinates: it always re-expresses the position in
    /// its own canonical cell frame.
    ///
    /// Use this when the CALLER cannot guarantee its currently-held local
    /// position is expressed in the claimed cell's landblock frame — e.g. the
    /// STOP packet, whose cached waypoint local position may be in a frame the
    /// server has since slid away from. Deriving the local position from the
    /// frame-free global coordinates is then the only way to guarantee the
    /// emitted (cell, pos) pair is internally consistent (a stale local pos
    /// paired with a slid cell mis-projects by a full landblock, ~192 m).
    ///
    /// Returns null only for out-of-range global coordinates
    /// (<see cref="AcCoords.OutdoorCellIdFromGlobal"/> == 0).
    /// </summary>
    public static SeamCell? Canonicalize(float globalX, float globalY, float z)
    {
        var derivedCell = AcCoords.OutdoorCellIdFromGlobal(globalX, globalY);
        if (derivedCell == 0u)
            return null;

        var lbx = (int)((derivedCell >> 24) & 0xFFu);
        var lby = (int)((derivedCell >> 16) & 0xFFu);
        return new SeamCell(
            derivedCell,
            new Vector3(
                globalX - lbx * AcCoords.BlockLength,
                globalY - lby * AcCoords.BlockLength,
                z));
    }
}
