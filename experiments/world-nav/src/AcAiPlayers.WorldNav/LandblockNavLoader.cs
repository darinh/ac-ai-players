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
//   3. Load the Environment from PortalDat. Look up the CellStruct.
//   4. For each CellPortal:
//        - Look up PhysicsPolygons[PolygonId].
//        - LoadVertices(CellStruct.VertexArray) to populate vertex
//          positions.
//        - Compute vertex-centroid in cell-local coords.
//        - Transform to world via Frame (Origin + rotate-by-Orientation).
//      This world point is the portal's canonical waypoint.
//   5. Compute the cell's geometric centroid from all PhysicsPolygons.
//   6. Resolve OtherCellId from cell-within-landblock-ushort to a full
//      32-bit cell ID. In the DAT, CellPortal.OtherCellId stores only
//      the low 16 bits when the target is in the same landblock; we
//      OR in the landblock prefix.
//      (Cross-landblock indoor portals are rare for early dungeons /
//      academy and out of scope for the first pass. They surface as
//      OtherCellLoaded = false.)
//
// Notes:
//   - We do NOT walk the BSP tree or do triangle sampling. The portal
//     centroid + cell centroid pair is a coarse but correct navmesh
//     for first-cut A*; refinement (e.g. selecting non-blocking
//     interior waypoints) is a later slice.
//   - We use PhysicsPolygons (collision mesh) rather than Polygons
//     (drawing mesh) so the waypoints sit on surfaces the bot can
//     actually traverse.

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
            var portals = new List<IndoorPortal>(raw.EnvCell.CellPortals.Count);
            foreach (var portal in raw.EnvCell.CellPortals)
            {
                var otherFull = ((uint)landblockId << 16) | portal.OtherCellId;
                Vector3? centroid = null;
                if (raw.Mesh != null)
                    centroid = TryComputePortalCentroidWorld(raw.Mesh, raw.EnvCell.Position, portal.PolygonId);

                portals.Add(new IndoorPortal
                {
                    OwnerCellId = cellId,
                    OtherCellId = otherFull,
                    OtherCellLoaded = loadedCellIds.Contains(otherFull),
                    PolygonId = portal.PolygonId,
                    CentroidWorld = centroid,
                });
            }

            var (centroidWorld, boundsWorld) = ComputeCellGeometry(raw);
            cells[cellId] = new IndoorCell
            {
                CellId = cellId,
                LandblockId = landblockId,
                CellWithinLandblock = (ushort)(cellId & 0xFFFF),
                OriginWorld = raw.EnvCell.Position.Origin,
                CentroidWorld = centroidWorld,
                BoundsWorld = boundsWorld,
                Portals = portals,
                HasGeometry = raw.Mesh != null,
            };
        }

        var aggregatePoints = new List<Vector3>();
        foreach (var cell in cells.Values)
        {
            aggregatePoints.Add(new Vector3(cell.BoundsWorld.MinX, cell.BoundsWorld.MinY, cell.BoundsWorld.MinZ));
            aggregatePoints.Add(new Vector3(cell.BoundsWorld.MaxX, cell.BoundsWorld.MaxY, cell.BoundsWorld.MaxZ));
            foreach (var portal in cell.Portals)
                if (portal.CentroidWorld is { } pc)
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

    private static Vector3? TryComputePortalCentroidWorld(CellStruct mesh, Frame frame, ushort polygonId)
    {
        // Try drawing polygons first (where portals usually live),
        // then fall back to physics polygons.
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

    private sealed class RawCell
    {
        public required EnvCell EnvCell { get; init; }
        public CellStruct? Mesh { get; init; }
    }
}
