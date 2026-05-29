// SPDX-License-Identifier: AGPL-3.0-or-later
//
// IndoorNavGraph - the static indoor navmesh produced by
// AcAiPlayers.WorldNav from a single landblock's EnvCell records.
//
// Per ADR-0010 / ac-ai-players#47, indoor navigation is A* over the
// precomputed EnvCell.CellPortal graph: each portal carries
// PolygonId / OtherCellId / OtherPortalId and the polygon centroid
// (in world space) is the canonical waypoint. Cells are room-sized
// volumes; the path through a multi-room building is a sequence of
// (cell-interior point) -> (portal centroid) -> (cell-interior point)
// -> (next portal centroid) -> ... transitions.
//
// "Static" means: this navmesh comes purely from the DAT files. It
// is the same for every player and every server iteration. It is
// the ground-truth geometry of the world, not the bot's perception.
//
// The bot's "fog of war" overlay (per-bot visited-cell memory +
// dynamic object metadata observed at runtime) lives elsewhere and
// is layered on top of this static graph at query time. The two
// concerns are deliberately separate so the static layer can be
// precomputed once and shared by:
//   - the headless client's per-bot NavGraph (hydrated lazily for
//     cells the bot has perceived)
//   - the future ACE.Mod.Pathfinding Harmony mod (server-side
//     pathfinding for BotPlayer and human-player /path helpers)

using System.Numerics;

namespace AcAiPlayers.WorldNav;

/// <summary>
/// One indoor cell (room volume) from <c>client_cell_1.dat</c>.
/// </summary>
public sealed class IndoorCell
{
    /// <summary>Full 32-bit DAT cell ID, e.g. 0x860201AD.</summary>
    public required uint CellId { get; init; }

    /// <summary>16-bit landblock prefix, e.g. 0x8602.</summary>
    public required ushort LandblockId { get; init; }

    /// <summary>16-bit cell-within-landblock, e.g. 0x01AD.</summary>
    public required ushort CellWithinLandblock { get; init; }

    /// <summary>
    /// World position of the cell origin. Per ACE convention this is
    /// the bottom-left corner / pivot used by the cell's local frame.
    /// </summary>
    public required Vector3 OriginWorld { get; init; }

    /// <summary>
    /// Geometric centre of the cell volume in world space. Computed
    /// from the cell's PhysicsPolygons; falls back to
    /// <see cref="OriginWorld"/> if no geometry is available.
    /// </summary>
    public required Vector3 CentroidWorld { get; init; }

    /// <summary>
    /// Axis-aligned bounding box of the cell in world space (XY only;
    /// Z is min/max). Used for SVG bounds computation and as a coarse
    /// "is this point inside this cell" test before deferring to BSP.
    /// </summary>
    public required NavBounds BoundsWorld { get; init; }

    /// <summary>
    /// Portals (gateways) leading OUT of this cell to neighbouring
    /// cells. Each portal corresponds to a doorway, archway, or
    /// open boundary between two EnvCells.
    /// </summary>
    public required IReadOnlyList<IndoorPortal> Portals { get; init; }

    /// <summary>
    /// True if this cell's geometry was available in the DAT.
    /// False indicates the cell was referenced by a neighbouring
    /// portal but its own record could not be loaded.
    /// </summary>
    public required bool HasGeometry { get; init; }
}

/// <summary>
/// One gateway between two indoor cells. Edges in the nav graph.
/// </summary>
public sealed class IndoorPortal
{
    /// <summary>The cell this portal sits inside (the "from" cell).</summary>
    public required uint OwnerCellId { get; init; }

    /// <summary>
    /// The cell on the other side of this portal. May resolve to a
    /// cell whose geometry we haven't loaded (e.g. across a landblock
    /// boundary); use <see cref="OtherCellLoaded"/> to check.
    /// </summary>
    public required uint OtherCellId { get; init; }

    /// <summary>
    /// True if <see cref="OtherCellId"/> resolved to a loaded cell in
    /// the same <see cref="IndoorNavGraph"/>. False = dangling edge
    /// (other landblock, missing DAT record, etc.).
    /// </summary>
    public required bool OtherCellLoaded { get; init; }

    /// <summary>
    /// Polygon ID of the portal in the owning cell's CellStruct.
    /// Used for diagnostics; not currently used for routing.
    /// </summary>
    public required ushort PolygonId { get; init; }

    /// <summary>
    /// World-space centroid of the portal polygon. This is the
    /// canonical waypoint the bot walks TO when traversing this
    /// portal. Null if the portal polygon could not be resolved
    /// (e.g. missing Environment record in client_portal.dat).
    /// </summary>
    public required Vector3? CentroidWorld { get; init; }
}

/// <summary>
/// Coarse XY-aligned bounds in world space. We track Z min/max
/// separately so we can sanity-check elevation transitions later.
/// </summary>
public readonly record struct NavBounds(
    float MinX, float MinY, float MaxX, float MaxY, float MinZ, float MaxZ)
{
    public static NavBounds FromPoints(IEnumerable<Vector3> points)
    {
        bool any = false;
        float minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        foreach (var p in points)
        {
            if (!any)
            {
                minX = maxX = p.X;
                minY = maxY = p.Y;
                minZ = maxZ = p.Z;
                any = true;
                continue;
            }

            if (p.X < minX) minX = p.X; else if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; else if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; else if (p.Z > maxZ) maxZ = p.Z;
        }
        return new NavBounds(minX, minY, maxX, maxY, minZ, maxZ);
    }

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;

    public NavBounds Union(NavBounds other) => new(
        System.Math.Min(MinX, other.MinX),
        System.Math.Min(MinY, other.MinY),
        System.Math.Max(MaxX, other.MaxX),
        System.Math.Max(MaxY, other.MaxY),
        System.Math.Min(MinZ, other.MinZ),
        System.Math.Max(MaxZ, other.MaxZ));
}

/// <summary>
/// The full static indoor navmesh for one landblock.
/// </summary>
public sealed class IndoorNavGraph
{
    /// <summary>16-bit landblock ID, e.g. 0x8602.</summary>
    public required ushort LandblockId { get; init; }

    /// <summary>All loaded indoor cells in this landblock, keyed by full 32-bit cell ID.</summary>
    public required IReadOnlyDictionary<uint, IndoorCell> Cells { get; init; }

    /// <summary>World-space bounds covering every loaded cell + portal centroid.</summary>
    public required NavBounds BoundsWorld { get; init; }

    /// <summary>How many EnvCell records the loader successfully decoded.</summary>
    public int CellCount => Cells.Count;

    /// <summary>How many portals across all cells (counts both directions if both sides loaded).</summary>
    public int PortalCount => Cells.Values.Sum(c => c.Portals.Count);
}
