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
bool quiet = false;
bool diag = false;

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
        case "--quiet": quiet = true; break;
        case "--diag": diag = true; break;
        case "-h":
        case "--help":
            Console.WriteLine("WorldNavBuilder --dat <dir> --landblock <hex> --out <svg> [--show-connection-ids] [--diag] [--quiet]");
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

var svg = new SvgRenderer().Render(graph, new SvgRenderer.Options
{
    ShowConnectionIds = showConnectionIds,
});

var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
if (!string.IsNullOrEmpty(outDir))
    Directory.CreateDirectory(outDir);
File.WriteAllText(outPath, svg);

if (!quiet)
    Console.WriteLine($"wrote {svg.Length:N0} bytes -> {outPath}");

return 0;
