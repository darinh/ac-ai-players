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
bool showPortalIds = false;
bool quiet = false;

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
        case "--show-portal-ids": showPortalIds = true; break;
        case "--quiet": quiet = true; break;
        case "-h":
        case "--help":
            Console.WriteLine("WorldNavBuilder --dat <dir> --landblock <hex> --out <svg> [--show-portal-ids] [--quiet]");
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
    Console.WriteLine($"  cells:   {graph.CellCount}");
    Console.WriteLine($"  portals: {graph.PortalCount}");
    Console.WriteLine($"  bounds:  X[{graph.BoundsWorld.MinX:0.##}..{graph.BoundsWorld.MaxX:0.##}] Y[{graph.BoundsWorld.MinY:0.##}..{graph.BoundsWorld.MaxY:0.##}] Z[{graph.BoundsWorld.MinZ:0.##}..{graph.BoundsWorld.MaxZ:0.##}]");

    int withGeom = 0, withoutGeom = 0;
    foreach (var cell in graph.Cells.Values)
        if (cell.HasGeometry) withGeom++; else withoutGeom++;
    Console.WriteLine($"  geometry: {withGeom} loaded, {withoutGeom} missing");

    int resolvedPortals = 0, danglingPortals = 0, unresolvedCentroid = 0;
    foreach (var cell in graph.Cells.Values)
        foreach (var p in cell.Portals)
        {
            if (p.CentroidWorld == null) unresolvedCentroid++;
            if (p.OtherCellLoaded) resolvedPortals++; else danglingPortals++;
        }
    Console.WriteLine($"  portal status: {resolvedPortals} resolved, {danglingPortals} dangling, {unresolvedCentroid} centroid-missing");
}

if (graph.CellCount == 0)
{
    Console.Error.WriteLine($"no indoor cells found for landblock 0x{landblock:X4}. is the landblock ID right?");
    return 1;
}

var svg = new SvgRenderer().Render(graph, new SvgRenderer.Options
{
    ShowPortalIds = showPortalIds,
});

var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
if (!string.IsNullOrEmpty(outDir))
    Directory.CreateDirectory(outDir);
File.WriteAllText(outPath, svg);

if (!quiet)
    Console.WriteLine($"wrote {svg.Length:N0} bytes -> {outPath}");

return 0;
