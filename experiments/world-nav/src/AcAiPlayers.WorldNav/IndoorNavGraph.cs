// SPDX-License-Identifier: AGPL-3.0-or-later
//
// IndoorNavGraph - the static indoor navmesh produced by
// AcAiPlayers.WorldNav from a single landblock's EnvCell records.
//
// Vocabulary (consistent across this library):
//   cell        - a room-sized indoor volume from client_cell_1.dat
//                 (DAT type: EnvCell)
//   connection  - the boundary between two adjacent cells (a doorway,
//                 archway, or open opening). Connections are the edges
//                 of the cell graph. (DAT type: CellPortal -- renamed
//                 to "connection" in our domain model so we don't
//                 collide with the in-game Portal world objects, which
//                 are the swirly teleporters.)
//   path        - the ordered sequence of cells (and the world-space
//                 waypoints inside them) the bot walks to get from
//                 one point to another. Produced by Pathfinder (A*).
//
// Per ADR-0010 / ac-ai-players#47, indoor navigation is A* over the
// precomputed cell graph: each connection carries the polygon centroid
// (in world space) which is the canonical waypoint at the boundary.
// A path through a multi-room building is a sequence of
// (cell-interior point) -> (connection point) -> (cell-interior
// point) -> (next connection point) -> ... transitions.
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
    /// Other cells this cell is connected to. Each connection
    /// corresponds to a doorway, archway, or open boundary between
    /// two adjacent cells. NOTE: this is distinct from the in-game
    /// Portal world objects (swirly teleporters). The DAT type is
    /// called CellPortal; we use "Connection" in our domain model.
    /// </summary>
    public required IReadOnlyList<CellConnection> Connections { get; init; }

    /// <summary>
    /// Static obstacles physically placed inside this cell (signs,
    /// columns, furniture, lifestones, training dummies, etc.).
    /// These come from <c>EnvCell.StaticObjects</c> in the DAT and
    /// are part of the world's fixed geometry — they never move and
    /// every player on every server sees them in the same place. We
    /// extract the broad-phase collision primitives (CylSpheres,
    /// Spheres) from each object's SetupModel and project them to
    /// world space. The bot must route around them.
    ///
    /// NOT INCLUDED: dynamic obstacles (NPCs, mobs, players, items,
    /// closed-but-openable doors) — those are runtime-sensed via
    /// wire packets and never come from DAT.
    /// </summary>
    public required IReadOnlyList<StaticObstacle> StaticObstacles { get; init; }

    /// <summary>
    /// Walkable floor surfaces inside this cell. Derived from the
    /// cell's <c>CellStruct.PhysicsPolygons</c> by selecting polygons
    /// whose face normal points up in world space (i.e. their world-
    /// space normal Z component exceeds the loader's floor threshold).
    /// Vertices are projected to world space at load time.
    ///
    /// The bot only walks ON a floor polygon — walls, ceilings, and
    /// near-vertical surfaces are excluded. Walkable-node sampling
    /// (see <see cref="WalkableNodes"/>) scatters grid points across
    /// these polygons and carves out static-obstacle footprints.
    /// </summary>
    public required IReadOnlyList<FloorPolygon> FloorPolygons { get; init; }

    /// <summary>
    /// Grid-sampled walkable points inside this cell. Each node is a
    /// world-space (X, Y, Z) position the bot's feet can stand on:
    /// it falls inside one of the cell's <see cref="FloorPolygons"/>
    /// and is NOT covered by any of its <see cref="StaticObstacles"/>'s
    /// top-down footprints. These are the vertices the per-cell
    /// micro-pathfinder walks along; <see cref="WalkableEdges"/>
    /// stitches them into a graph.
    /// </summary>
    public required IReadOnlyList<WalkableNode> WalkableNodes { get; init; }

    /// <summary>
    /// 8-neighbour adjacency edges between this cell's
    /// <see cref="WalkableNodes"/>. Each edge connects two nodes that
    /// are one grid step apart (cardinal or diagonal) AND whose
    /// straight-line segment clears every static-obstacle circle AND
    /// whose Z difference is small enough that the bot can step
    /// (no jumping over half-walls). Indices reference
    /// <see cref="WalkableNodes"/>; the edge list is DEDUPLICATED so
    /// each undirected (A, B) pair appears exactly once with
    /// <c>NodeA &lt; NodeB</c>. Cross-cell edges (through doorways)
    /// are NOT in this list — the cell-graph
    /// <see cref="Connections"/> handle inter-cell hops.
    /// </summary>
    public required IReadOnlyList<WalkableEdge> WalkableEdges { get; init; }

    /// <summary>
    /// True if this cell's geometry was available in the DAT.
    /// False indicates the cell was referenced by a neighbouring
    /// portal but its own record could not be loaded.
    /// </summary>
    public required bool HasGeometry { get; init; }
}

/// <summary>
/// One walkable floor surface inside a cell. A floor polygon is a
/// PhysicsPolygon from the owning cell's <c>CellStruct</c> whose
/// face normal points up in world space (positive Z above some
/// threshold). Vertices are already projected to world space.
///
/// The polygon is the substrate for the walkable mesh: Phase 2 will
/// sample grid points within each floor polygon's footprint, drop
/// any that fall inside a <see cref="StaticObstacle"/>, and connect
/// the survivors into a per-cell walkable graph.
/// </summary>
public sealed class FloorPolygon
{
    /// <summary>
    /// The PhysicsPolygon's polygon ID inside the source CellStruct.
    /// Diagnostic only; not used for routing.
    /// </summary>
    public required ushort PolygonId { get; init; }

    /// <summary>
    /// World-space vertices of the polygon, preserving the original
    /// winding from the DAT. At least 3 entries.
    /// </summary>
    public required IReadOnlyList<Vector3> VerticesWorld { get; init; }

    /// <summary>
    /// World-space face normal, normalised, oriented to point UP
    /// (Z component is positive — i.e. away from the floor, into the
    /// room).
    /// </summary>
    public required Vector3 NormalWorld { get; init; }
}

/// <summary>
/// One grid-sampled walkable point inside an indoor cell. Produced by
/// scanning each <see cref="FloorPolygon"/>'s XY footprint at a fixed
/// spacing and keeping samples that are (a) inside the polygon and
/// (b) outside every <see cref="StaticObstacle"/> in the same cell.
/// The Z coord is the polygon's plane elevation at that XY (so on
/// stair ramps successive samples form a sloped chain).
///
/// Walkable nodes are the substrate of the cell-interior pathfinder.
/// A future Phase 2 step will connect them via 8-neighbour edges and
/// the cell-graph A* will route through them instead of cell centroids.
/// </summary>
public sealed class WalkableNode
{
    /// <summary>Owning cell's full 32-bit ID.</summary>
    public required uint CellId { get; init; }

    /// <summary>
    /// Index into the owning cell's <see cref="IndoorCell.FloorPolygons"/>
    /// — the polygon this sample sits on. Used so a per-cell micro-mesh
    /// can avoid bridging samples that belong to disconnected floor
    /// regions (e.g. a stair landing vs the main floor of the same cell).
    /// </summary>
    public required int FloorPolygonIndex { get; init; }

    /// <summary>World-space sample position (Z taken from the polygon plane).</summary>
    public required Vector3 PositionWorld { get; init; }
}

/// <summary>
/// One undirected edge between two same-cell <see cref="WalkableNode"/>s.
/// The line segment between the two nodes is guaranteed to clear every
/// <see cref="StaticObstacle"/> in the cell and to have a small enough
/// vertical step that the bot can walk it (no jumping).
/// </summary>
public readonly record struct WalkableEdge(int NodeA, int NodeB, float DistanceUnits);

/// <summary>
/// The shape of a static obstacle's footprint, as it appears in the
/// SetupModel's broad-phase collision data.
/// </summary>
public enum ObstacleShape
{
    /// <summary>Vertical cylinder. Top-down footprint = circle of <c>Radius</c>.</summary>
    Cylinder,

    /// <summary>Sphere. Top-down footprint = circle of <c>Radius</c>.</summary>
    Sphere,

    /// <summary>
    /// Setup-level bounding cylinder used as a fallback when the
    /// SetupModel declares no per-part CylSpheres or Spheres (rare
    /// for placeable decorations but possible for "simple setup"
    /// generated objects). Anchored at the stab origin with
    /// <see cref="StaticObstacle.Radius"/> and
    /// <see cref="StaticObstacle.Height"/> taken from
    /// <c>SetupModel.Radius</c> / <c>SetupModel.Height</c>.
    /// </summary>
    BoundingCylinder,
}

/// <summary>
/// One static-obstacle primitive in world space. Several
/// StaticObstacle entries can come from a single Stab (a SetupModel
/// is allowed to have multiple CylSpheres + Spheres). We flatten
/// them so the consumer doesn't have to know about Setups.
/// </summary>
public sealed class StaticObstacle
{
    /// <summary>The Stab's SetupModel ID (0x02xxxxxx), for diagnostics.</summary>
    public required uint SetupId { get; init; }

    /// <summary>Which broad-phase primitive this entry came from.</summary>
    public required ObstacleShape Shape { get; init; }

    /// <summary>
    /// World-space center of the primitive. For a CylSphere this is
    /// the BASE of the cylinder (Z = floor); for a Sphere it is the
    /// geometric center.
    /// </summary>
    public required Vector3 CenterWorld { get; init; }

    /// <summary>XY-plane radius of the obstacle footprint, in world units.</summary>
    public required float Radius { get; init; }

    /// <summary>
    /// Vertical extent. For Cylinder/BoundingCylinder this is the
    /// height upward from <see cref="CenterWorld"/>. For Sphere it
    /// is 0 (caller should treat the sphere as <c>Radius</c> tall).
    /// </summary>
    public required float Height { get; init; }
}

/// <summary>
/// A connection between two adjacent indoor cells (a doorway,
/// archway, or open opening). Connections are the edges of the cell
/// graph; the bot walks across one connection to move from one cell
/// to the next. Distinct from the in-game Portal world objects
/// (teleporters); the DAT format calls these "CellPortals" but in
/// our domain model we call them Connections to avoid that overload.
/// </summary>
public sealed class CellConnection
{
    /// <summary>The cell this connection belongs to (the "from" cell).</summary>
    public required uint OwnerCellId { get; init; }

    /// <summary>
    /// The cell on the other side of this connection. May resolve to
    /// a cell whose geometry we haven't loaded (e.g. across a
    /// landblock boundary); use <see cref="OtherCellLoaded"/> to check.
    /// </summary>
    public required uint OtherCellId { get; init; }

    /// <summary>
    /// True if <see cref="OtherCellId"/> resolved to a loaded cell in
    /// the same <see cref="IndoorNavGraph"/>. False = dangling edge
    /// (other landblock, missing DAT record, etc.).
    /// </summary>
    public required bool OtherCellLoaded { get; init; }

    /// <summary>
    /// Polygon ID of the connection opening in the owning cell's
    /// CellStruct. Used for diagnostics; not currently used for
    /// routing decisions.
    /// </summary>
    public required ushort PolygonId { get; init; }

    /// <summary>
    /// World-space centroid of the connection polygon (the geometric
    /// midpoint of the doorway / opening). This is the canonical
    /// waypoint the bot walks TO when crossing this connection.
    /// Null if the polygon could not be resolved (e.g. missing
    /// Environment record in client_portal.dat).
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

    /// <summary>Total connection count across all cells (counts both directions when both sides loaded).</summary>
    public int ConnectionCount => Cells.Values.Sum(c => c.Connections.Count);

    /// <summary>Total static-obstacle primitive count across all cells.</summary>
    public int StaticObstacleCount => Cells.Values.Sum(c => c.StaticObstacles.Count);

    /// <summary>Total floor-polygon count across all cells (walkable surfaces).</summary>
    public int FloorPolygonCount => Cells.Values.Sum(c => c.FloorPolygons.Count);

    /// <summary>Total walkable-node count across all cells (grid samples).</summary>
    public int WalkableNodeCount => Cells.Values.Sum(c => c.WalkableNodes.Count);

    /// <summary>Total walkable-edge count across all cells (intra-cell only).</summary>
    public int WalkableEdgeCount => Cells.Values.Sum(c => c.WalkableEdges.Count);
}
