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
}
