// SPDX-License-Identifier: AGPL-3.0-or-later
//
// WorldNavBuilder CLI - load one landblock from the AC1 DAT files
// and emit a static indoor nav graph plus a diagnostic SVG.
//
// Usage:
//   WorldNavBuilder --dat <dat-dir> --landblock <hex> --out <svg-path>
//
// Defaults:
//   --dat        C:\ACE\Dats
//   --landblock  8602  (Holtburg Training Academy)
//   --out        academy.svg
//
// This is throwaway diagnostic tooling. Treat the SVG output as a
// debugging aid, not a deliverable. The library this CLI exercises
// (AcAiPlayers.WorldNav) is the long-lived artifact.

using System.Globalization;
using System.Numerics;
using System.Text;

using ACE.DatLoader;

using AcAiPlayers.WorldNav;

// ACE.DatLoader reads Windows-1252 strings via Encoding.GetEncoding(1252);
// .NET Core only ships UTF + a couple of ASCII variants in the base
// runtime. System.Text.Encoding.CodePages adds the rest; we have to
// register its provider before any DAT file with embedded text is read.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

string datDir = @"C:\ACE\Dats";
ushort landblock = 0x8602;
string outPath = "academy.svg";
bool showConnectionIds = false;
bool showWalkableEdges = false;
bool showWalkableBridges = false;
bool quiet = false;
bool diag = false;
Vector3? traceFrom = null;
Vector3? traceTo = null;

static Vector3 ParseV3(string s, string flag)
{
    var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 3)
        throw new ArgumentException($"{flag} expects three comma-separated floats (x,y,z), got '{s}'");
    return new Vector3(
        float.Parse(parts[0], CultureInfo.InvariantCulture),
        float.Parse(parts[1], CultureInfo.InvariantCulture),
        float.Parse(parts[2], CultureInfo.InvariantCulture));
}

for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    string Next(string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"missing value for {name}");
        return args[++i];
    }
    switch (a)
    {
        case "--dat": datDir = Next(a); break;
        case "--landblock":
            var hex = Next(a).Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            landblock = ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            break;
        case "--out": outPath = Next(a); break;
        case "--show-connection-ids": showConnectionIds = true; break;
        case "--show-walkable-edges": showWalkableEdges = true; break;
        case "--show-walkable-bridges": showWalkableBridges = true; break;
        case "--trace":
            traceFrom = ParseV3(Next(a), "--trace from");
            traceTo = ParseV3(Next(a), "--trace to");
            break;
        case "--quiet": quiet = true; break;
        case "--diag": diag = true; break;
        case "-h":
        case "--help":
            Console.WriteLine("WorldNavBuilder --dat <dir> --landblock <hex> --out <svg> [--show-connection-ids] [--show-walkable-edges] [--show-walkable-bridges] [--trace x,y,z x,y,z] [--diag] [--quiet]");
            return 0;
        default:
            Console.Error.WriteLine($"unknown arg: {a}");
            return 2;
    }
}

if (!Directory.Exists(datDir))
{
    Console.Error.WriteLine($"DAT directory not found: {datDir}");
    return 1;
}

if (!quiet)
    Console.WriteLine($"opening DAT files at {datDir} ...");

// keepOpen=true so reads after Initialize can hit the stream;
// loadCell=true (default). Initialize is idempotent across processes
// because each process gets its own static state.
DatManager.Initialize(datDir, keepOpen: true, loadCell: true);

if (DatManager.CellDat == null)
{
    Console.Error.WriteLine($"failed to open client_cell_1.dat in {datDir}");
    return 1;
}
if (DatManager.PortalDat == null)
{
    Console.Error.WriteLine($"failed to open client_portal.dat in {datDir}");
    return 1;
}

if (!quiet)
{
    Console.WriteLine($"  CellDat: {DatManager.CellDat.AllFiles.Count:N0} files");
    Console.WriteLine($"  PortalDat: {DatManager.PortalDat.AllFiles.Count:N0} files");
    Console.WriteLine($"loading landblock 0x{landblock:X4} ...");
}

var loader = new LandblockNavLoader(DatManager.CellDat, DatManager.PortalDat);
var graph = loader.Load(landblock);

if (!quiet)
{
    Console.WriteLine($"  cells:        {graph.CellCount}");
    Console.WriteLine($"  connections:  {graph.ConnectionCount}");
    Console.WriteLine($"  obstacles:    {graph.StaticObstacleCount} static primitives");
    Console.WriteLine($"  floor polys:  {graph.FloorPolygonCount} walkable surfaces");
    Console.WriteLine($"  walk nodes:   {graph.WalkableNodeCount} grid-sampled stand points");
    Console.WriteLine($"  walk edges:   {graph.WalkableEdgeCount} 8-neighbour intra-cell links");
    Console.WriteLine($"  walk bridges: {graph.WalkableBridgeCount} cross-cell doorway hops");
    Console.WriteLine($"  bounds:  X[{graph.BoundsWorld.MinX:0.##}..{graph.BoundsWorld.MaxX:0.##}] Y[{graph.BoundsWorld.MinY:0.##}..{graph.BoundsWorld.MaxY:0.##}] Z[{graph.BoundsWorld.MinZ:0.##}..{graph.BoundsWorld.MaxZ:0.##}]");

    int withGeom = 0, withoutGeom = 0;
    foreach (var cell in graph.Cells.Values)
        if (cell.HasGeometry) withGeom++; else withoutGeom++;
    Console.WriteLine($"  geometry: {withGeom} loaded, {withoutGeom} missing");

    int resolvedConns = 0, danglingConns = 0, unresolvedCentroid = 0;
    foreach (var cell in graph.Cells.Values)
        foreach (var c in cell.Connections)
        {
            if (c.CentroidWorld == null) unresolvedCentroid++;
            if (c.OtherCellLoaded) resolvedConns++; else danglingConns++;
        }
    Console.WriteLine($"  connection status: {resolvedConns} resolved, {danglingConns} dangling, {unresolvedCentroid} centroid-missing");

    int cylN = 0, sphN = 0, boundN = 0;
    foreach (var cell in graph.Cells.Values)
        foreach (var o in cell.StaticObstacles)
        {
            if (o.Shape == ObstacleShape.Cylinder) cylN++;
            else if (o.Shape == ObstacleShape.Sphere) sphN++;
            else boundN++;
        }
    Console.WriteLine($"  obstacle breakdown: {cylN} cylinders, {sphN} spheres, {boundN} setup-bound fallbacks");

    int cellsWithNoWalk = 0;
    foreach (var cell in graph.Cells.Values)
        if (cell.WalkableNodes.Count == 0) cellsWithNoWalk++;
    Console.WriteLine($"  cells with no walkable nodes: {cellsWithNoWalk} of {graph.CellCount}");
}

if (graph.CellCount == 0)
{
    Console.Error.WriteLine($"no indoor cells found for landblock 0x{landblock:X4}. is the landblock ID right?");
    return 1;
}

if (diag)
{
    Console.WriteLine();
    Console.WriteLine("=== diagnostic ===");

    // Z histogram of cell centroids (1u bins).
    var zhist = new SortedDictionary<int, int>();
    foreach (var cell in graph.Cells.Values)
    {
        int bin = (int)Math.Floor(cell.CentroidWorld.Z);
        zhist[bin] = zhist.TryGetValue(bin, out var v) ? v + 1 : 1;
    }
    Console.WriteLine($"cell-centroid Z histogram (Min={graph.BoundsWorld.MinZ:0.##} Max={graph.BoundsWorld.MaxZ:0.##}):");
    foreach (var kv in zhist)
        Console.WriteLine($"  Z={kv.Key,4}u : {kv.Value,4} cells  {new string('#', Math.Min(kv.Value, 60))}");

    // Cells with the largest vertical extent (candidate stair / ramp cells).
    Console.WriteLine();
    Console.WriteLine("top-15 cells by vertical extent (Z span, candidate stair/ramp cells):");
    var byExtent = graph.Cells.Values
        .Where(c => c.HasGeometry)
        .Select(c => (c, span: c.BoundsWorld.MaxZ - c.BoundsWorld.MinZ))
        .OrderByDescending(t => t.span)
        .Take(15);
    foreach (var (c, span) in byExtent)
        Console.WriteLine($"  0x{c.CellId:X8}  z-span={span,6:0.00}u  centroid=({c.CentroidWorld.X:0.0},{c.CentroidWorld.Y:0.0},{c.CentroidWorld.Z:0.0})  connections={c.Connections.Count}");

    // Connections whose two endpoint cells are at noticeably different Z.
    // These are inter-floor traversals (stairs, ramps, ladders, drops).
    Console.WriteLine();
    int totalConns = 0, interFloorConns = 0, sameFloorConns = 0;
    var interFloorDetails = new List<(uint from, uint to, float dz, float pz)>();
    foreach (var cell in graph.Cells.Values)
    {
        foreach (var c in cell.Connections)
        {
            totalConns++;
            if (!c.OtherCellLoaded) continue;
            var other = graph.Cells[c.OtherCellId];
            float dz = other.CentroidWorld.Z - cell.CentroidWorld.Z;
            if (Math.Abs(dz) >= 1.5f)
            {
                interFloorConns++;
                interFloorDetails.Add((cell.CellId, c.OtherCellId, dz, c.CentroidWorld?.Z ?? cell.CentroidWorld.Z));
            }
            else
            {
                sameFloorConns++;
            }
        }
    }
    Console.WriteLine($"connection Z-delta breakdown: {sameFloorConns} same-floor (|dz|<1.5u), {interFloorConns} inter-floor (|dz|>=1.5u), total {totalConns}");
    Console.WriteLine($"top-15 inter-floor connections (sorted by |dz|):");
    foreach (var d in interFloorDetails.OrderByDescending(x => Math.Abs(x.dz)).Take(15))
        Console.WriteLine($"  0x{d.from:X8} -> 0x{d.to:X8}  dz={d.dz,+7:0.00}u  conn-z={d.pz:0.0}");
}

var pathTrace = Array.Empty<Vector3>();
if (traceFrom is { } pf && traceTo is { } pt)
{
    var result = new Pathfinder().FindWalkablePath(graph, pf, pt);
    if (result.Found)
    {
        pathTrace = result.Points.ToArray();
        if (!quiet)
        {
            Console.WriteLine($"trace: {pathTrace.Length} waypoints, cost={result.TotalCost:0.0}u, visited={result.VisitedNodes}");
            Console.WriteLine($"       start={pathTrace[0]:0.0} goal={pathTrace[^1]:0.0}");
        }
    }
    else if (!quiet)
    {
        Console.Error.WriteLine($"trace: FAILED ({result.FailureReason}); visited={result.VisitedNodes}");
    }
}

var svg = new SvgRenderer().Render(graph, new SvgRenderer.Options
{
    ShowConnectionIds = showConnectionIds,
    ShowWalkableEdges = showWalkableEdges,
    ShowWalkableBridges = showWalkableBridges,
    PathTrace = pathTrace,
});

var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
if (!string.IsNullOrEmpty(outDir))
    Directory.CreateDirectory(outDir);
File.WriteAllText(outPath, svg);

if (!quiet)
    Console.WriteLine($"wrote {svg.Length:N0} bytes -> {outPath}");

return 0;
