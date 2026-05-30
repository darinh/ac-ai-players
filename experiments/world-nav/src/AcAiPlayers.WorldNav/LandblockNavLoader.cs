// SPDX-License-Identifier: AGPL-3.0-or-later
//
// LandblockNavLoader - given an open DAT pair (CellDat + PortalDat),
// build an IndoorNavGraph for one landblock.
//
// Algorithm:
//   1. Enumerate AllFiles in CellDat. The DAT file format keys every
//      file by a 32-bit ID. For indoor cells the ID layout is
//      `(landblock << 16) | (cell-within-landblock)`, where the low
//      16 bits are >= 0x0100 (sub-0x0100 IDs are outdoor terrain
//      slots in a landblock). Filter to files whose high 16 bits
//      match `landblockId` AND whose low 16 bits are >= 0x0100.
//   2. For each match, read the EnvCell record. Extract:
//        - Position (Frame: cell-local-to-world transform)
//        - EnvironmentId (PortalDat file ID for the cell mesh prefab)
//        - CellStructure (ushort: which CellStruct within Environment)
//        - CellPortals (list of {PolygonId, OtherCellId, OtherPortalId})
//          We model each of these as a CellConnection in our domain
//          model -- the DAT name "portal" collides with the in-game
//          Portal teleporter object.
//   3. Load the Environment from PortalDat. Look up the CellStruct.
//   4. For each CellPortal -> build one CellConnection:
//        - Look up PhysicsPolygons[PolygonId].
//        - LoadVertices(CellStruct.VertexArray) to populate vertex
//          positions.
//        - Compute vertex-centroid in cell-local coords.
//        - Transform to world via Frame (Origin + rotate-by-Orientation).
//      This world point is the connection's canonical waypoint.
//   5. Compute the cell's geometric centroid from all PhysicsPolygons.
//   5b. Read EnvCell.StaticObjects (list of Stab). Each Stab refers
//       to a SetupModel (Stab.Id high byte 0x02) placed at Stab.Frame
//       (local to the cell). Load the Setup, project its CylSpheres
//       and Spheres to world coords via stab.Frame composed with
//       cell.Position, and attach as StaticObstacles on the cell.
//       Stabs whose Id is NOT a SetupModel (e.g. raw GfxObj-only
//       refs at 0x01xxxxxx) are skipped — those would need BSP
//       traversal which is out of scope for this layer.
//   6. Resolve OtherCellId from cell-within-landblock-ushort to a full
//      32-bit cell ID. In the DAT, CellPortal.OtherCellId stores only
//      the low 16 bits when the target is in the same landblock; we
//      OR in the landblock prefix.
//      (Cross-landblock indoor connections are rare for early dungeons
//      / academy and out of scope for the first pass. They surface as
//      OtherCellLoaded = false.)
//
// Notes:
//   - We do NOT walk the BSP tree or do triangle sampling. The
//     connection centroid + cell centroid pair is a coarse but correct
//     navmesh for first-cut A*; refinement (e.g. selecting
//     non-blocking interior waypoints) is a later slice.
//   - We use PhysicsPolygons (collision mesh) for cell geometry so
//     waypoints sit on surfaces the bot can actually traverse, but
//     drawing-mesh Polygons for connection openings (most CellPortal
//     openings live there, not in PhysicsPolygons).

using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;

namespace AcAiPlayers.WorldNav;

public sealed class LandblockNavLoader
{
    private readonly CellDatDatabase _cellDat;
    private readonly DatDatabase _portalDat;

    public LandblockNavLoader(CellDatDatabase cellDat, DatDatabase portalDat)
    {
        _cellDat = cellDat;
        _portalDat = portalDat;
    }

    /// <summary>
    /// Load the indoor cell graph for one landblock.
    /// </summary>
    public IndoorNavGraph Load(ushort landblockId)
    {
        var indoorCellIds = EnumerateIndoorCellIds(landblockId).ToList();

        var rawCells = new Dictionary<uint, RawCell>(capacity: indoorCellIds.Count);
        foreach (var cellId in indoorCellIds)
        {
            var raw = TryLoadRawCell(cellId, landblockId);
            if (raw != null)
                rawCells[cellId] = raw;
        }

        var loadedCellIds = new HashSet<uint>(rawCells.Keys);
        var cells = new Dictionary<uint, IndoorCell>(capacity: rawCells.Count);
        foreach (var (cellId, raw) in rawCells)
        {
            var connections = new List<CellConnection>(raw.EnvCell.CellPortals.Count);
            foreach (var cellPortal in raw.EnvCell.CellPortals)
            {
                var otherFull = ((uint)landblockId << 16) | cellPortal.OtherCellId;
                Vector3? centroid = null;
                if (raw.Mesh != null)
                    centroid = TryComputeConnectionCentroidWorld(raw.Mesh, raw.EnvCell.Position, cellPortal.PolygonId);

                connections.Add(new CellConnection
                {
                    OwnerCellId = cellId,
                    OtherCellId = otherFull,
                    OtherCellLoaded = loadedCellIds.Contains(otherFull),
                    PolygonId = cellPortal.PolygonId,
                    CentroidWorld = centroid,
                });
            }

            var (centroidWorld, boundsWorld) = ComputeCellGeometry(raw);
            var obstacles = ComputeStaticObstacles(raw);
            var floors = ComputeFloorPolygons(raw);
            var floorNodes = ComputeWalkableNodes(cellId, floors, obstacles);
            var (walkable, walkEdges) = AppendDoorwayNodesAndEdges(
                cellId, floorNodes, floors, obstacles, connections);
            cells[cellId] = new IndoorCell
            {
                CellId = cellId,
                LandblockId = landblockId,
                CellWithinLandblock = (ushort)(cellId & 0xFFFF),
                OriginWorld = raw.EnvCell.Position.Origin,
                CentroidWorld = centroidWorld,
                BoundsWorld = boundsWorld,
                Connections = connections,
                StaticObstacles = obstacles,
                FloorPolygons = floors,
                WalkableNodes = walkable,
                WalkableEdges = walkEdges,
                HasGeometry = raw.Mesh != null,
            };
        }

        var aggregatePoints = new List<Vector3>();
        foreach (var cell in cells.Values)
        {
            aggregatePoints.Add(new Vector3(cell.BoundsWorld.MinX, cell.BoundsWorld.MinY, cell.BoundsWorld.MinZ));
            aggregatePoints.Add(new Vector3(cell.BoundsWorld.MaxX, cell.BoundsWorld.MaxY, cell.BoundsWorld.MaxZ));
            foreach (var connection in cell.Connections)
                if (connection.CentroidWorld is { } pc)
                    aggregatePoints.Add(pc);
        }
        var bounds = aggregatePoints.Count == 0
            ? new NavBounds(0, 0, 0, 0, 0, 0)
            : NavBounds.FromPoints(aggregatePoints);

        var bridges = ComputeWalkableBridges(cells);

        return new IndoorNavGraph
        {
            LandblockId = landblockId,
            Cells = cells,
            WalkableBridges = bridges,
            BoundsWorld = bounds,
        };
    }

    private IEnumerable<uint> EnumerateIndoorCellIds(ushort landblockId)
    {
        var prefix = (uint)landblockId << 16;
        foreach (var fileId in _cellDat.AllFiles.Keys)
        {
            if ((fileId >> 16) != landblockId)
                continue;
            var lo = fileId & 0xFFFF;
            // Indoor cells live at >= 0x0100; outdoor terrain
            // sub-cells use 0x0001-0x0040 and the 0xFFFE/0xFFFF
            // slots are LandblockInfo / CellLandblock metadata.
            if (lo < 0x0100 || lo >= 0xFFFE)
                continue;
            yield return fileId;
        }
    }

    private RawCell? TryLoadRawCell(uint cellId, ushort landblockId)
    {
        EnvCell env;
        try
        {
            env = _cellDat.ReadFromDat<EnvCell>(cellId);
        }
        catch
        {
            return null;
        }
        if (env == null || env.Id == 0)
            return null;

        ACE.DatLoader.FileTypes.Environment? environment = null;
        try
        {
            environment = _portalDat.ReadFromDat<ACE.DatLoader.FileTypes.Environment>(env.EnvironmentId);
        }
        catch
        {
            environment = null;
        }

        CellStruct? mesh = null;
        if (environment != null && environment.Cells.TryGetValue(env.CellStructure, out var cs))
            mesh = cs;

        if (mesh != null)
            EnsurePhysicsVerticesLoaded(mesh);

        return new RawCell { EnvCell = env, Mesh = mesh };
    }

    /// <summary>
    /// <see cref="Polygon.LoadVertices(CVertexArray)"/> is lazy and
    /// the DAT loader does not call it automatically; populate the
    /// per-polygon Vertices list before we read positions.
    /// We need vertices on BOTH polygon collections because a portal
    /// polygon may live in either:
    ///   - <see cref="CellStruct.PhysicsPolygons"/> when the portal
    ///     is also part of the cell's collision mesh (rare), or
    ///   - <see cref="CellStruct.Polygons"/> (drawing mesh) which
    ///     holds the vast majority of portal opening polygons.
    /// </summary>
    private static void EnsurePhysicsVerticesLoaded(CellStruct mesh)
    {
        foreach (var poly in mesh.PhysicsPolygons.Values)
        {
            if (poly.Vertices == null)
                poly.LoadVertices(mesh.VertexArray);
        }
        foreach (var poly in mesh.Polygons.Values)
        {
            if (poly.Vertices == null)
                poly.LoadVertices(mesh.VertexArray);
        }
    }

    private static Vector3? TryComputeConnectionCentroidWorld(CellStruct mesh, Frame frame, ushort polygonId)
    {
        // Try drawing polygons first (where connection openings usually
        // live), then fall back to physics polygons.
        if (!mesh.Polygons.TryGetValue(polygonId, out var poly)
            && !mesh.PhysicsPolygons.TryGetValue(polygonId, out poly))
        {
            return null;
        }
        if (poly.Vertices == null || poly.Vertices.Count == 0)
            return null;

        var local = Vector3.Zero;
        foreach (var v in poly.Vertices)
            local += v.Origin;
        local /= poly.Vertices.Count;

        return CellLocalToWorld(local, frame);
    }

    private static (Vector3 centroid, NavBounds bounds) ComputeCellGeometry(RawCell raw)
    {
        var origin = raw.EnvCell.Position.Origin;
        if (raw.Mesh == null)
        {
            // No geometry available; collapse to origin with zero extent.
            return (origin, new NavBounds(origin.X, origin.Y, origin.X, origin.Y, origin.Z, origin.Z));
        }

        var worldPoints = new List<Vector3>();
        foreach (var poly in raw.Mesh.PhysicsPolygons.Values)
        {
            if (poly.Vertices == null)
                continue;
            foreach (var v in poly.Vertices)
                worldPoints.Add(CellLocalToWorld(v.Origin, raw.EnvCell.Position));
        }

        if (worldPoints.Count == 0)
            return (origin, new NavBounds(origin.X, origin.Y, origin.X, origin.Y, origin.Z, origin.Z));

        var sum = Vector3.Zero;
        foreach (var p in worldPoints)
            sum += p;
        var centroid = sum / worldPoints.Count;
        var bounds = NavBounds.FromPoints(worldPoints);
        return (centroid, bounds);
    }

    private static Vector3 CellLocalToWorld(Vector3 local, Frame frame)
    {
        // Frame is rigid-body: world = origin + rotate(local, orientation).
        // System.Numerics rotates a vector by a unit quaternion via
        // Vector3.Transform(v, q).
        return frame.Origin + Vector3.Transform(local, frame.Orientation);
    }

    /// <summary>
    /// Compose two rigid frames: an obstacle point given in stab-local
    /// space goes through stabFrame (object -> cell-local) then through
    /// cellFrame (cell-local -> world).
    /// </summary>
    private static Vector3 StabLocalToWorld(Vector3 local, Frame stabFrame, Frame cellFrame)
    {
        var cellLocal = stabFrame.Origin + Vector3.Transform(local, stabFrame.Orientation);
        return cellFrame.Origin + Vector3.Transform(cellLocal, cellFrame.Orientation);
    }

    /// <summary>
    /// Extract static-obstacle footprints for every Stab inside this
    /// cell. Each Stab points at a SetupModel; we surface its
    /// broad-phase collision primitives (CylSpheres + Spheres). If a
    /// Setup has neither but advertises a bounding cylinder via its
    /// own Radius/Height, fall back to that anchored at the stab origin.
    /// </summary>
    private List<StaticObstacle> ComputeStaticObstacles(RawCell raw)
    {
        var result = new List<StaticObstacle>();
        foreach (var stab in raw.EnvCell.StaticObjects)
        {
            // DAT id-space: 0x01xxxxxx = GfxObj (mesh only), 0x02xxxxxx = SetupModel.
            // Only SetupModels expose the CylSphere/Sphere primitives
            // we need for a cheap top-down footprint; raw GfxObj stabs
            // would require PhysicsBSP traversal and are skipped for now.
            if ((stab.Id >> 24) != 0x02)
                continue;

            SetupModel? setup;
            try
            {
                setup = _portalDat.ReadFromDat<SetupModel>(stab.Id);
            }
            catch
            {
                continue;
            }
            if (setup == null)
                continue;

            int before = result.Count;

            foreach (var cs in setup.CylSpheres)
            {
                if (cs.Radius <= 0) continue;
                var world = StabLocalToWorld(cs.Origin, stab.Frame, raw.EnvCell.Position);
                result.Add(new StaticObstacle
                {
                    SetupId = stab.Id,
                    Shape = ObstacleShape.Cylinder,
                    CenterWorld = world,
                    Radius = cs.Radius,
                    Height = cs.Height,
                });
            }

            foreach (var sp in setup.Spheres)
            {
                if (sp.Radius <= 0) continue;
                var world = StabLocalToWorld(sp.Origin, stab.Frame, raw.EnvCell.Position);
                result.Add(new StaticObstacle
                {
                    SetupId = stab.Id,
                    Shape = ObstacleShape.Sphere,
                    CenterWorld = world,
                    Radius = sp.Radius,
                    Height = 0f,
                });
            }

            // Fallback: setup declared no per-part primitive but does
            // advertise a bounding cylinder (Setup.Radius/Height).
            // Anchor it at the stab origin so we still render *something*
            // for objects whose collision lives only in PhysicsBSP.
            if (result.Count == before && setup.Radius > 0)
            {
                var world = StabLocalToWorld(Vector3.Zero, stab.Frame, raw.EnvCell.Position);
                result.Add(new StaticObstacle
                {
                    SetupId = stab.Id,
                    Shape = ObstacleShape.BoundingCylinder,
                    CenterWorld = world,
                    Radius = setup.Radius,
                    Height = setup.Height,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// World-space Z component (cosine of the normal-to-vertical angle)
    /// required for a PhysicsPolygon to count as a walkable floor.
    /// 0.7 ≈ 45° — accepts level floors and stair ramps; rejects
    /// walls (~0) and ceilings (negative Z, which we flip then accept
    /// only if abs is below threshold).
    /// </summary>
    private const float FloorNormalZThreshold = 0.7f;

    /// <summary>
    /// Identify walkable floor surfaces in a cell. A floor is a
    /// PhysicsPolygon whose face normal, after rotation by the cell's
    /// world orientation, has a positive Z component above the
    /// <see cref="FloorNormalZThreshold"/>. Ceilings (normals pointing
    /// down), walls (normals nearly horizontal), and degenerate
    /// polygons (zero-area, collinear vertices) are excluded.
    ///
    /// Returns vertices in world space; the original DAT vertex order
    /// is preserved (callers should treat the polygon as "the points in
    /// the order the DAT stored them" — it may be CW or CCW depending
    /// on the polygon's <c>SidesType</c>).
    /// </summary>
    private static List<FloorPolygon> ComputeFloorPolygons(RawCell raw)
    {
        var result = new List<FloorPolygon>();
        if (raw.Mesh == null) return result;

        var cellFrame = raw.EnvCell.Position;
        foreach (var (polyId, poly) in raw.Mesh.PhysicsPolygons)
        {
            if (poly.Vertices == null || poly.Vertices.Count < 3)
                continue;

            // Face normal from first three vertices (treats the
            // polygon as planar — true for AC's grid-aligned floors,
            // close enough for the few sloped ramps).
            var v0 = poly.Vertices[0].Origin;
            var v1 = poly.Vertices[1].Origin;
            var v2 = poly.Vertices[2].Origin;
            var localNormal = Vector3.Cross(v1 - v0, v2 - v0);
            if (localNormal.LengthSquared() < 1e-6f)
                continue; // degenerate

            localNormal = Vector3.Normalize(localNormal);
            var worldNormal = Vector3.Transform(localNormal, cellFrame.Orientation);

            // Floor test: world-space normal must point up. The cross
            // product's sign depends on vertex winding (CW vs CCW), so
            // we accept either orientation but only when the surface
            // is genuinely near-horizontal — abs(Z) > threshold.
            if (System.Math.Abs(worldNormal.Z) < FloorNormalZThreshold)
                continue;

            // For ceilings the normal points down; for floors it points
            // up. We want floors specifically. After absorbing winding,
            // a "floor" is a horizontal surface that the bot stands on,
            // i.e. one whose centroid is BELOW any other horizontal
            // surface above it. Simpler heuristic: if normal Z >= 0 we
            // treat it as a floor; if < 0 we flip it (treating it as
            // CW-wound floor). This will incorrectly classify ceilings
            // as floors — Phase 2 will discriminate by picking the
            // lowest horizontal surface per (X,Y) column.
            var upwardNormal = worldNormal.Z >= 0 ? worldNormal : -worldNormal;

            var worldVerts = new Vector3[poly.Vertices.Count];
            for (int i = 0; i < poly.Vertices.Count; i++)
                worldVerts[i] = CellLocalToWorld(poly.Vertices[i].Origin, cellFrame);

            result.Add(new FloorPolygon
            {
                PolygonId = polyId,
                VerticesWorld = worldVerts,
                NormalWorld = upwardNormal,
            });
        }
        return result;
    }

    private sealed class RawCell
    {
        public required EnvCell EnvCell { get; init; }
        public CellStruct? Mesh { get; init; }
    }

    /// <summary>
    /// Spacing of the walkable-node grid in world units. AC's bot
    /// footprint is roughly 0.5u radius; 1.0u spacing leaves a small
    /// cushion between samples without bloating the SVG. Tunable
    /// later — too coarse and the bot misses narrow corridors,
    /// too fine and the per-cell node count explodes.
    /// </summary>
    private const float WalkableSampleSpacing = 1.0f;

    /// <summary>
    /// How far above a cell's lowest floor vertex a horizontal
    /// PhysicsPolygon is allowed to be before we classify it as the
    /// CEILING instead of a floor and stop sampling it. Bot standing
    /// height is ~1.8u; allowing 4u keeps stair ramps + low mezzanines
    /// in scope while skipping the cell ceiling 8-12u up.
    /// </summary>
    private const float CeilingSkipHeight = 4.0f;

    /// <summary>
    /// Grid-sample walkable points inside this cell. For every floor
    /// polygon (skipping any that look like ceilings — see
    /// <see cref="CeilingSkipHeight"/>) we scan a grid at
    /// <see cref="WalkableSampleSpacing"/> resolution, drop samples
    /// outside the polygon's XY footprint (ray-cast point-in-polygon),
    /// drop samples covered by any static obstacle's circle footprint,
    /// and project the remaining samples onto the polygon's plane to
    /// get their Z.
    /// </summary>
    private static List<WalkableNode> ComputeWalkableNodes(
        uint cellId,
        IReadOnlyList<FloorPolygon> floors,
        IReadOnlyList<StaticObstacle> obstacles)
    {
        var nodes = new List<WalkableNode>();
        if (floors.Count == 0)
            return nodes;

        // Per-cell minimum floor Z is the reference for the
        // ceiling-skip filter. Any polygon whose average vertex Z is
        // more than CeilingSkipHeight above this is assumed to be the
        // ceiling of the same room and gets skipped.
        float cellMinFloorZ = float.PositiveInfinity;
        foreach (var f in floors)
            foreach (var v in f.VerticesWorld)
                if (v.Z < cellMinFloorZ) cellMinFloorZ = v.Z;
        float ceilingCutoff = cellMinFloorZ + CeilingSkipHeight;

        for (int polyIdx = 0; polyIdx < floors.Count; polyIdx++)
        {
            var poly = floors[polyIdx];
            if (poly.VerticesWorld.Count < 3) continue;

            // Polygon avg Z vs ceiling cutoff.
            float avgZ = 0f;
            foreach (var v in poly.VerticesWorld) avgZ += v.Z;
            avgZ /= poly.VerticesWorld.Count;
            if (avgZ > ceilingCutoff) continue;

            // XY bbox for the sample sweep.
            float pMinX = float.PositiveInfinity, pMaxX = float.NegativeInfinity;
            float pMinY = float.PositiveInfinity, pMaxY = float.NegativeInfinity;
            foreach (var v in poly.VerticesWorld)
            {
                if (v.X < pMinX) pMinX = v.X;
                if (v.X > pMaxX) pMaxX = v.X;
                if (v.Y < pMinY) pMinY = v.Y;
                if (v.Y > pMaxY) pMaxY = v.Y;
            }

            // Snap to the global integer-multiple grid so adjacent
            // polygons in the same cell sample identical X/Y rows.
            float startX = (float)System.Math.Ceiling(pMinX / WalkableSampleSpacing) * WalkableSampleSpacing;
            float startY = (float)System.Math.Ceiling(pMinY / WalkableSampleSpacing) * WalkableSampleSpacing;

            for (float x = startX; x <= pMaxX; x += WalkableSampleSpacing)
            {
                for (float y = startY; y <= pMaxY; y += WalkableSampleSpacing)
                {
                    if (!PointInPolygonXY(x, y, poly.VerticesWorld)) continue;
                    if (PointInsideAnyObstacleXY(x, y, obstacles)) continue;
                    float z = ProjectZOntoPlane(x, y, poly);
                    nodes.Add(new WalkableNode
                    {
                        CellId = cellId,
                        FloorPolygonIndex = polyIdx,
                        PositionWorld = new Vector3(x, y, z),
                    });
                }
            }
        }
        return nodes;
    }

    /// <summary>
    /// Classic ray-cast point-in-polygon test (Jordan curve theorem).
    /// Works for both convex and concave 2D polygons; doesn't care
    /// about winding order.
    /// </summary>
    private static bool PointInPolygonXY(float x, float y, IReadOnlyList<Vector3> poly)
    {
        int n = poly.Count;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = poly[i].X, yi = poly[i].Y;
            float xj = poly[j].X, yj = poly[j].Y;
            bool intersect = ((yi > y) != (yj > y))
                          && (x < (xj - xi) * (y - yi) / (yj - yi) + xi);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// True if the XY point sits inside any obstacle's top-down
    /// circular footprint (cylinder, sphere, or bounding-cylinder
    /// fallback — all rendered as circles).
    /// </summary>
    private static bool PointInsideAnyObstacleXY(float x, float y, IReadOnlyList<StaticObstacle> obstacles)
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            var o = obstacles[i];
            float dx = x - o.CenterWorld.X;
            float dy = y - o.CenterWorld.Y;
            if (dx * dx + dy * dy <= o.Radius * o.Radius) return true;
        }
        return false;
    }

    /// <summary>
    /// Solve the polygon's plane equation for Z given (x, y).
    /// Plane: N . (P - V0) = 0
    ///   => z = V0.Z - (N.X*(x-V0.X) + N.Y*(y-V0.Y)) / N.Z
    /// Safe because <see cref="FloorNormalZThreshold"/> guarantees
    /// |N.Z| >= 0.7 for every polygon that reaches this code path.
    /// </summary>
    private static float ProjectZOntoPlane(float x, float y, FloorPolygon poly)
    {
        var v0 = poly.VerticesWorld[0];
        var n = poly.NormalWorld;
        return v0.Z - (n.X * (x - v0.X) + n.Y * (y - v0.Y)) / n.Z;
    }

    /// <summary>
    /// Maximum vertical step a bot can take between two adjacent
    /// walkable nodes (in world Z units). Stairs and ramps in AC are
    /// often steep — a 45-degree ramp at 1.0u XY spacing yields 1.0u
    /// dz per step. Set to 1.0u to allow up to a 45-degree slope.
    /// Same-cell only: the spatial hash de-dupes XY duplicates so
    /// this can never produce vertical "elevator" edges between two
    /// stacked nodes at the same XY. Different cells get their edges
    /// via the cell-graph layer (CellConnection).
    /// </summary>
    private const float WalkableStepMaxDz = 1.0f;

    /// <summary>
    /// Connect each walkable node to its 8 grid neighbours when:
    ///   (a) the neighbour exists (sample landed on the floor)
    ///   (b) the vertical step |dz| &lt;= WalkableStepMaxDz
    ///   (c) the XY line segment clears every static obstacle's
    ///       circle (segment-vs-circle distance &gt; radius).
    /// Returns deduplicated undirected edges (NodeA &lt; NodeB).
    /// </summary>
    private static List<WalkableEdge> ComputeWalkableEdges(
        IReadOnlyList<WalkableNode> nodes,
        IReadOnlyList<StaticObstacle> obstacles)
    {
        var edges = new List<WalkableEdge>();
        if (nodes.Count < 2) return edges;

        // Spatial hash keyed by integer grid coords so we can look up
        // neighbours in O(1). We snap to the same WalkableSampleSpacing
        // grid the sampler used, so positions land on exact integers
        // after dividing.
        var index = new Dictionary<(int gx, int gy), int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            var p = nodes[i].PositionWorld;
            int gx = (int)System.Math.Round(p.X / WalkableSampleSpacing);
            int gy = (int)System.Math.Round(p.Y / WalkableSampleSpacing);
            // If two samples collide (e.g. floor + ceiling at same XY,
            // though ceiling filter usually drops the latter), keep
            // the first — edges still form a connected component for
            // the surviving node, and the orphan stays isolated.
            if (!index.ContainsKey((gx, gy)))
                index[(gx, gy)] = i;
        }

        // 8 neighbour offsets: 4 cardinal (length 1) + 4 diagonal
        // (length sqrt(2)). We only emit each undirected edge once
        // by requiring the chosen offset's grid index to be strictly
        // greater (so the OTHER node has a larger key).
        var offsets = new (int dx, int dy, float dist)[]
        {
            (+1,  0, 1.0f),
            (+1, +1, 1.41421356f),
            ( 0, +1, 1.0f),
            (-1, +1, 1.41421356f),
        };

        for (int i = 0; i < nodes.Count; i++)
        {
            var a = nodes[i].PositionWorld;
            int gx = (int)System.Math.Round(a.X / WalkableSampleSpacing);
            int gy = (int)System.Math.Round(a.Y / WalkableSampleSpacing);

            foreach (var (dx, dy, dist) in offsets)
            {
                if (!index.TryGetValue((gx + dx, gy + dy), out var j)) continue;
                var b = nodes[j].PositionWorld;

                // Vertical step gate (no jumping over half-walls).
                if (System.Math.Abs(b.Z - a.Z) > WalkableStepMaxDz) continue;

                // XY segment must clear every obstacle circle.
                if (SegmentIntersectsAnyObstacleXY(a.X, a.Y, b.X, b.Y, obstacles)) continue;

                // Store the TRUE 3D Euclidean distance — not the 2D
                // grid distance. On stairs/ramps the Z difference
                // matters: a 1.0u XY step with 1.0u Z step is sqrt(2)u
                // long, not 1.0u. Using the 2D value would understate
                // the cost and could violate A*'s admissible-heuristic
                // invariant if the heuristic is 3D Euclidean.
                edges.Add(new WalkableEdge(i, j, Vector3.Distance(a, b)));
            }
        }

        return edges;
    }

    /// <summary>
    /// True if the XY segment (ax,ay)-(bx,by) passes within
    /// <c>obstacle.Radius</c> of any obstacle's center. Standard
    /// closest-point-on-segment formulation.
    /// </summary>
    private static bool SegmentIntersectsAnyObstacleXY(
        float ax, float ay, float bx, float by,
        IReadOnlyList<StaticObstacle> obstacles)
    {
        float dx = bx - ax;
        float dy = by - ay;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-10f) return false; // degenerate segment

        for (int i = 0; i < obstacles.Count; i++)
        {
            var o = obstacles[i];
            float cx = o.CenterWorld.X;
            float cy = o.CenterWorld.Y;
            // Project (cx,cy) onto segment, clamped to [0,1].
            float t = ((cx - ax) * dx + (cy - ay) * dy) / lenSq;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            float px = ax + t * dx;
            float py = ay + t * dy;
            float ex = cx - px;
            float ey = cy - py;
            if (ex * ex + ey * ey <= o.Radius * o.Radius) return true;
        }
        return false;
    }

    /// <summary>
    /// Build cross-cell walkable bridges by matching up Doorway
    /// <see cref="WalkableNode"/>s on either side of each
    /// <see cref="CellConnection"/>. Each loaded-both-sides connection
    /// with a resolved centroid emits exactly ONE bridge.
    ///
    /// The owning side's Doorway node is found by exact
    /// <see cref="WalkableNode.ConnectionPolygonId"/> match (the cell
    /// built that node from THIS connection record). The other side's
    /// Doorway node is found by world-position proximity to the
    /// shared connection centroid — necessary because each cell has
    /// its OWN PolygonId namespace for the shared doorway face and
    /// the two sides almost never share a numeric ID.
    ///
    /// De-duplicated by unordered cell-pair plus node-index pair so
    /// the same physical doorway isn't bridged twice when both sides
    /// list it.
    /// </summary>
    private static List<WalkableBridge> ComputeWalkableBridges(
        IReadOnlyDictionary<uint, IndoorCell> cells)
    {
        var bridges = new List<WalkableBridge>();
        var seen = new HashSet<(uint, int, uint, int)>();

        foreach (var cell in cells.Values)
        {
            foreach (var connection in cell.Connections)
            {
                if (!connection.OtherCellLoaded) continue;
                if (connection.CentroidWorld is not Vector3 cc) continue;
                if (!cells.TryGetValue(connection.OtherCellId, out var other)) continue;

                int fromIdx = FindDoorwayNodeIndex(cell, connection.PolygonId);
                if (fromIdx < 0) continue;
                // Each cell has its own PolygonId namespace for the
                // shared doorway face — match the other side's
                // Doorway node by world-position proximity to the
                // shared centroid (XY only; Z differs because each
                // side projects to its own floor plane).
                int toIdx = FindDoorwayNodeByPositionXY(other, cc, DoorwayMatchToleranceUnits);
                if (toIdx < 0) continue;

                var (ca, na, cb, nb) = cell.CellId < other.CellId
                    ? (cell.CellId, fromIdx, other.CellId, toIdx)
                    : (other.CellId, toIdx, cell.CellId, fromIdx);
                if (!seen.Add((ca, na, cb, nb))) continue;

                var fromPos = cell.WalkableNodes[fromIdx].PositionWorld;
                var toPos = other.WalkableNodes[toIdx].PositionWorld;
                float cost = Vector3.Distance(fromPos, toPos);

                bridges.Add(new WalkableBridge(
                    FromCellId: cell.CellId,
                    FromNodeIndex: fromIdx,
                    ToCellId: other.CellId,
                    ToNodeIndex: toIdx,
                    ConnectionPolygonId: connection.PolygonId,
                    DistanceUnits: cost));
            }
        }

        return bridges;
    }

    /// <summary>
    /// Tolerance (XY world units) for matching the other-cell Doorway
    /// node to this cell's connection centroid. Both DAT records for
    /// a shared doorway face independently compute the centroid from
    /// their own mesh + frame; numerical drift between the two is
    /// usually well under 0.1u but can be larger for irregular
    /// arches. 2.0u is generous enough to absorb that drift while
    /// still preventing accidental matches across truly separate
    /// adjacent doorways.
    /// </summary>
    private const float DoorwayMatchToleranceUnits = 2.0f;

    /// <summary>
    /// Find the index of the Doorway <see cref="WalkableNode"/> in
    /// <paramref name="cell"/> whose
    /// <see cref="WalkableNode.ConnectionPolygonId"/> matches
    /// <paramref name="polygonId"/>. Returns -1 if no such node exists
    /// (e.g. the centroid failed to resolve for that connection so
    /// <see cref="AppendDoorwayNodesAndEdges"/> didn't emit a doorway
    /// node for it).
    /// </summary>
    private static int FindDoorwayNodeIndex(IndoorCell cell, ushort polygonId)
    {
        for (int i = 0; i < cell.WalkableNodes.Count; i++)
        {
            var n = cell.WalkableNodes[i];
            if (n.Kind == WalkableNodeKind.Doorway && n.ConnectionPolygonId == polygonId)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the index of the Doorway <see cref="WalkableNode"/> in
    /// <paramref name="cell"/> closest in world XY to
    /// <paramref name="worldPos"/>, returning -1 if no Doorway node
    /// is within <paramref name="toleranceUnits"/> in the XY plane.
    /// Z is intentionally ignored because the two sides of the same
    /// connection project to different floor planes (e.g. stair top
    /// vs stair bottom).
    /// </summary>
    private static int FindDoorwayNodeByPositionXY(
        IndoorCell cell, Vector3 worldPos, float toleranceUnits)
    {
        float tolSq = toleranceUnits * toleranceUnits;
        int best = -1;
        float bestSq = tolSq;
        for (int i = 0; i < cell.WalkableNodes.Count; i++)
        {
            var n = cell.WalkableNodes[i];
            if (n.Kind != WalkableNodeKind.Doorway) continue;
            float dx = n.PositionWorld.X - worldPos.X;
            float dy = n.PositionWorld.Y - worldPos.Y;
            float dSq = dx * dx + dy * dy;
            if (dSq <= bestSq)
            {
                bestSq = dSq;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Maximum XY distance between a doorway node (placed at the
    /// connection centroid) and the floor nodes it may connect to via
    /// intra-cell <see cref="WalkableEdge"/>s. Doorways are usually
    /// reachable from any floor node within ~4–6u; 8u is generous to
    /// account for rooms where the door is set back from the main
    /// floor or where sampling left gaps right next to the threshold.
    /// </summary>
    private const float DoorwayConnectionRadius = 8.0f;

    /// <summary>
    /// Vertical step allowance from a doorway node to a candidate
    /// floor node, in world Z units. More generous than
    /// <see cref="WalkableStepMaxDz"/> because doorway centroids sit
    /// at the polygon midpoint, which is often elevated relative to
    /// the floor sill on either side (especially for arches).
    /// </summary>
    private const float DoorwayConnectionMaxDz = 2.0f;

    /// <summary>
    /// Append one Doorway <see cref="WalkableNode"/> per loaded-side
    /// of each <see cref="CellConnection"/> with a resolved centroid,
    /// and wire it into the intra-cell <see cref="WalkableEdge"/>
    /// graph via LOS-checked edges to nearby Floor nodes.
    ///
    /// Why doorway nodes are structural (not just "the nearest floor
    /// sample to the centroid"): A* over a sampled floor + cross-cell
    /// bridges partitions badly when the nearest-to-centroid floor
    /// sample lands in a disconnected micro-component cut off from
    /// the rest of the room by a static obstacle (sign, column,
    /// pillar) or by a sub-cell elevation step the
    /// <see cref="WalkableStepMaxDz"/> gate rejects. Making the
    /// doorway its own node — anchored at the canonical doorway XY
    /// regardless of sampling — and connecting it to ALL floor nodes
    /// within <see cref="DoorwayConnectionRadius"/> (LOS-checked)
    /// guarantees the doorway is reachable from at least one floor
    /// node per cell, AND lets A* enter/exit via whichever floor
    /// node sits in the bot's connected component.
    ///
    /// The Doorway node ALSO carries the connection's PolygonId in
    /// <see cref="WalkableNode.ConnectionPolygonId"/> so the runtime
    /// consumer can correlate it with an observed Door entity and
    /// dispatch USE-to-open BEFORE walking through.
    /// </summary>
    private static (List<WalkableNode> nodes, List<WalkableEdge> edges) AppendDoorwayNodesAndEdges(
        uint cellId,
        List<WalkableNode> floorNodes,
        IReadOnlyList<FloorPolygon> floors,
        IReadOnlyList<StaticObstacle> obstacles,
        IReadOnlyList<CellConnection> connections)
    {
        var nodes = new List<WalkableNode>(floorNodes);
        var edges = ComputeWalkableEdges(floorNodes, obstacles);

        float cellMinFloorZ = float.PositiveInfinity;
        foreach (var f in floors)
            foreach (var v in f.VerticesWorld)
                if (v.Z < cellMinFloorZ) cellMinFloorZ = v.Z;

        foreach (var connection in connections)
        {
            // Skip doorways whose centroid we couldn't resolve (other
            // landblock, missing DAT record, etc.). These don't get
            // structural nodes — fog of war + cell-level routing
            // handle the abstract crossing if needed.
            if (connection.CentroidWorld is not Vector3 cc) continue;

            // Project the doorway centroid down to floor level on
            // THIS cell's side. Strategy:
            //   1. If there's a floor polygon whose XY footprint
            //      contains the centroid, use its plane Z (preferring
            //      the highest one at-or-below the centroid Z so we
            //      pick the floor under the doorway, not a balcony).
            //   2. Else use the nearest floor node's Z (within
            //      DoorwayConnectionRadius).
            //   3. Else fall back to cellMinFloorZ.
            int floorPolyIdx = -1;
            float floorZ = cc.Z;
            for (int pi = 0; pi < floors.Count; pi++)
            {
                if (floors[pi].VerticesWorld.Count < 3) continue;
                if (!PointInPolygonXY(cc.X, cc.Y, floors[pi].VerticesWorld)) continue;
                float z = ProjectZOntoPlane(cc.X, cc.Y, floors[pi]);
                if (z <= cc.Z + 0.5f && (floorPolyIdx < 0 || z > floorZ))
                {
                    floorPolyIdx = pi;
                    floorZ = z;
                }
            }
            if (floorPolyIdx < 0)
            {
                float bestDistSq = DoorwayConnectionRadius * DoorwayConnectionRadius;
                bool found = false;
                for (int i = 0; i < floorNodes.Count; i++)
                {
                    var p = floorNodes[i].PositionWorld;
                    float dxx = p.X - cc.X;
                    float dyy = p.Y - cc.Y;
                    float distSq = dxx * dxx + dyy * dyy;
                    if (distSq <= bestDistSq)
                    {
                        bestDistSq = distSq;
                        floorZ = p.Z;
                        floorPolyIdx = floorNodes[i].FloorPolygonIndex;
                        found = true;
                    }
                }
                if (!found && !float.IsPositiveInfinity(cellMinFloorZ))
                    floorZ = cellMinFloorZ;
            }

            var doorwayNode = new WalkableNode
            {
                CellId = cellId,
                FloorPolygonIndex = floorPolyIdx,
                PositionWorld = new Vector3(cc.X, cc.Y, floorZ),
                Kind = WalkableNodeKind.Doorway,
                ConnectionPolygonId = connection.PolygonId,
            };
            int doorwayIdx = nodes.Count;
            nodes.Add(doorwayNode);

            // Wire the doorway into the intra-cell graph: connect to
            // every floor node within DoorwayConnectionRadius whose
            // segment clears the obstacle field. Without these edges
            // the doorway node would be isolated and A* couldn't
            // enter/exit the cell through it.
            for (int i = 0; i < floorNodes.Count; i++)
            {
                var fp = floorNodes[i].PositionWorld;
                float dxx = fp.X - cc.X;
                float dyy = fp.Y - cc.Y;
                float xyDistSq = dxx * dxx + dyy * dyy;
                if (xyDistSq > DoorwayConnectionRadius * DoorwayConnectionRadius) continue;
                if (System.Math.Abs(fp.Z - floorZ) > DoorwayConnectionMaxDz) continue;
                if (SegmentIntersectsAnyObstacleXY(cc.X, cc.Y, fp.X, fp.Y, obstacles)) continue;
                float dist = Vector3.Distance(new Vector3(cc.X, cc.Y, floorZ), fp);
                // Floor node i first (NodeA < NodeB convention since
                // the doorway node is appended at the end).
                edges.Add(new WalkableEdge(i, doorwayIdx, dist));
            }
        }

        // Stair / threshold fix: ensure every pair of Doorway nodes in
        // this cell is directly connected (LOS-checked). Threshold
        // cells — stairs, arches, narrow corridors — often have
        // fragmented floor polygons whose 8-neighbour grid edges leave
        // each side of the threshold in a separate component. Without
        // a direct doorway↔doorway edge the bot can enter via one
        // doorway and get stranded, unable to reach the doorway on
        // the other side even though the cell IS architecturally one
        // traversable space.
        //
        // We deliberately skip the WalkableStepMaxDz gate here: a
        // stair cell IS the vertical traversal between two floors,
        // so |dz| between its two doorways can legitimately be 3-4u
        // (the stair's rise). Obstacles still block (so a barricaded
        // archway with a CylSphere obstacle in the middle won't get
        // wired through).
        //
        // The bridges layer remains the only cross-cell edge mechanism.
        // Both endpoints here are in the SAME cell (cellId), so this
        // can never connect through to another landblock.
        int doorwayStart = floorNodes.Count;
        for (int a = doorwayStart; a < nodes.Count; a++)
        {
            for (int b = a + 1; b < nodes.Count; b++)
            {
                var pa = nodes[a].PositionWorld;
                var pb = nodes[b].PositionWorld;
                if (SegmentIntersectsAnyObstacleXY(pa.X, pa.Y, pb.X, pb.Y, obstacles)) continue;
                edges.Add(new WalkableEdge(a, b, Vector3.Distance(pa, pb)));
            }
        }

        return (nodes, edges);
    }
}
