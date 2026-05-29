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
bool partition = false;
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
        case "--partition": partition = true; break;
        case "-h":
        case "--help":
            Console.WriteLine("WorldNavBuilder --dat <dir> --landblock <hex> --out <svg> [--show-connection-ids] [--show-walkable-edges] [--show-walkable-bridges] [--trace x,y,z x,y,z] [--partition] [--diag] [--quiet]");
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
    Console.WriteLine($"  walk nodes:   {graph.WalkableNodeCount} total ({graph.WalkableFloorNodeCount} floor + {graph.WalkableDoorwayNodeCount} doorway)");
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

if (partition)
{
    // Run A* between every ordered pair of distinct cell centroids
    // (excluding self-pairs). Reports the success ratio + a handful
    // of failing pairs so we can audit gaps in doorway-node coverage.
    // This is O(N^2) where N is cell count; for academy (568) that's
    // ~322k traces. We sample-cap to keep runtime sane.
    var cellIds = graph.Cells.Keys.OrderBy(x => x).ToArray();
    var rng = new Random(12345);
    const int sampleCap = 2000;
    var pairs = new List<(uint a, uint b)>();
    for (int trials = 0; trials < sampleCap; trials++)
    {
        var a = cellIds[rng.Next(cellIds.Length)];
        var b = cellIds[rng.Next(cellIds.Length)];
        if (a == b) continue;
        pairs.Add((a, b));
    }
    int hit = 0, miss = 0;
    var pf2 = new Pathfinder();
    var firstFails = new List<(uint a, uint b, string reason, int visited)>();
    foreach (var (a, b) in pairs)
    {
        var cA = graph.Cells[a].CentroidWorld;
        var cB = graph.Cells[b].CentroidWorld;
        var r = pf2.FindWalkablePath(graph, cA, cB);
        if (r.Found) hit++;
        else
        {
            miss++;
            if (firstFails.Count < 12)
                firstFails.Add((a, b, r.FailureReason ?? "(no reason)", r.VisitedNodes));
        }
    }
    Console.WriteLine($"partition: {hit}/{hit + miss} random pairs reachable ({(hit * 100.0 / Math.Max(1, hit + miss)):0.0}%)");
    foreach (var f in firstFails)
        Console.WriteLine($"  RAND FAIL  0x{f.a:X8} -> 0x{f.b:X8}  visited={f.visited}  reason={f.reason}");

    // Focused check: only pairs that share a direct cross-cell
    // bridge. If even these fail, the doorway-node wiring or
    // floor-graph snap is broken at the cell scale, not the
    // landblock scale.
    var bridgePairs = graph.WalkableBridges
        .Select(b => (a: System.Math.Min(b.FromCellId, b.ToCellId),
                      b: System.Math.Max(b.FromCellId, b.ToCellId)))
        .Distinct()
        .ToArray();
    int bhit = 0, bmiss = 0;
    var bFails = new List<(uint a, uint b, string reason, int visited)>();
    foreach (var (a, b) in bridgePairs)
    {
        var cA = graph.Cells[a].CentroidWorld;
        var cB = graph.Cells[b].CentroidWorld;
        var r = pf2.FindWalkablePath(graph, cA, cB);
        if (r.Found) bhit++;
        else
        {
            bmiss++;
            if (bFails.Count < 12) bFails.Add((a, b, r.FailureReason ?? "(no reason)", r.VisitedNodes));
        }
    }
    Console.WriteLine($"partition: {bhit}/{bhit + bmiss} BRIDGED pairs reachable ({(bhit * 100.0 / Math.Max(1, bhit + bmiss)):0.0}%)");
    foreach (var f in bFails)
        Console.WriteLine($"  BRIDGE FAIL  0x{f.a:X8} -> 0x{f.b:X8}  visited={f.visited}  reason={f.reason}");

    // Per-cell connected-component analysis. Count CCs in the
    // intra-cell walkable subgraph + record the largest-CC fraction
    // so we can see how badly each cell is internally partitioned.
    int badlyPartitioned = 0;
    int hugeFragmentation = 0;
    foreach (var kvp in graph.Cells)
    {
        var cell = kvp.Value;
        int n = cell.WalkableNodes.Count;
        if (n == 0) continue;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (var e in cell.WalkableEdges)
        {
            adj[e.NodeA].Add(e.NodeB);
            adj[e.NodeB].Add(e.NodeA);
        }
        var comp = new int[n];
        for (int i = 0; i < n; i++) comp[i] = -1;
        int cc = 0;
        int bestSize = 0;
        for (int i = 0; i < n; i++)
        {
            if (comp[i] != -1) continue;
            int size = 0;
            var q = new Queue<int>();
            q.Enqueue(i);
            comp[i] = cc;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                size++;
                foreach (var v in adj[u])
                {
                    if (comp[v] == -1) { comp[v] = cc; q.Enqueue(v); }
                }
            }
            if (size > bestSize) bestSize = size;
            cc++;
        }
        if (cc > 1) badlyPartitioned++;
        if (bestSize < n / 2) hugeFragmentation++;
    }
    Console.WriteLine($"per-cell CC: {badlyPartitioned}/{graph.Cells.Count} cells have >1 component; {hugeFragmentation} cells have largest-CC < 50% of nodes");
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
