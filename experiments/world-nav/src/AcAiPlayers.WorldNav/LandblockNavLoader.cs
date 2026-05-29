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
            var walkable = ComputeWalkableNodes(cellId, floors, obstacles);
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

        return new IndoorNavGraph
        {
            LandblockId = landblockId,
            Cells = cells,
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
}
