// SPDX-License-Identifier: AGPL-3.0-or-later
//
// IndoorNavService — Phase 3 integration glue between the headless
// client's walk-tick and the AcAiPlayers.WorldNav static indoor
// navmesh library.
//
// What this provides:
//
//   * Lazy per-landblock loading of IndoorNavGraph from the AC1
//     client DAT files (via LandblockNavLoader). Cached for the
//     process lifetime.
//   * TryFindPath(from, to, seenCells) — returns a list of XYZ
//     waypoints the bot can step through, OR a status code
//     explaining why no path was returned (cross-landblock,
//     outdoor, no graph, partitioned, etc.).
//   * Static helpers for the indoor-vs-outdoor cell-id test and
//     landblock extraction so callers don't repeat the bit-twiddle.
//
// What this does NOT do:
//
//   * Track fog-of-war. The caller (HandshakeDriver) owns the
//     HashSet of seen indoor cells and passes it in.
//   * Drive the walk-tick. The caller decides when to query a
//     path, when to advance waypoints, and when to fall back to
//     straight-line motion.
//   * Load anything from the server. Only client-side DAT
//     geometry is read. Per the project's hardcoded-knowledge
//     audit, DAT geometry is treated as "what a human player's
//     client renders + collides against" and is NOT game
//     knowledge.
//
// Concurrency model:
//
//   * `Lazy<IndoorNavGraph?>` per landblock guarantees single-
//     flight construction even if multiple threads hit the same
//     id at once. The headless client is single-threaded today
//     but a future API host or multi-bot harness might not be.
//   * The DatManager (in ACE.DatLoader) is initialised once at
//     service construction; subsequent reads are file-system-
//     level and thread-safe per the DatLoader contract.
//
// Failure modes:
//
//   * DAT directory missing or unreadable -> service is
//     constructed in a disabled state; every TryFindPath
//     returns Status=Disabled. Headless client continues with
//     pre-WorldNav straight-line behavior.
//   * Landblock id not present in the DAT (e.g. dungeon expansion
//     that never shipped) -> Status=NoGraph.
//   * Walkable graph partitioned (current v1 limitation) ->
//     Status=NoPath. Caller surfaces as ActionRejected so the
//     dedup machinery avoids retrying the same unreachable
//     destination from the same start.
//
// Phase 3 telemetry hooks (TelemetryCounters) make the v1
// partitioning rate measurable in production; a future v2 of
// LandblockNavLoader will be judged by whether it lifts the
// Success rate.

using System.Collections.Concurrent;
using System.Numerics;

using ACE.DatLoader;

using AcAiPlayers.WorldNav;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Status code returned by <see cref="IndoorNavService.TryFindPath"/>.
/// Callers use this to decide between "follow waypoints", "fall back
/// to straight-line motion" (outdoor / cross-landblock), and "abort
/// motion + emit ActionRejected" (truly no path).
/// </summary>
internal enum IndoorPathStatus
{
    /// <summary>Service disabled (no DAT files, AC_INDOOR_NAV=0).
    /// Caller falls back to existing straight-line behavior.</summary>
    Disabled,

    /// <summary>Source or destination cell is outdoor. The library
    /// only models indoor cells; caller falls back to straight-
    /// line motion (which is correct for outdoor terrain).</summary>
    NotIndoor,

    /// <summary>Source and destination are in different landblocks.
    /// Cross-landblock routing is the high-level NavGraph's job
    /// (UsedDoor / UsedPortal edges); caller should not try to
    /// straight-line indoors toward a different landblock.</summary>
    CrossLandblock,

    /// <summary>Landblock has no indoor geometry (e.g. an outdoor-
    /// only landblock id, or a missing DAT entry). Caller falls
    /// back to straight-line motion.</summary>
    NoGraph,

    /// <summary>Indoor pathfinder ran and found no path between
    /// the snapped endpoints. Distinguishes between "graph is
    /// partitioned and the goal is in a different component"
    /// and a real unreachable destination. Caller should surface
    /// as ActionRejected; do NOT straight-line through walls.
    /// </summary>
    NoPath,

    /// <summary>Path returned successfully; <see
    /// cref="IndoorPathResult.Waypoints"/> is populated with at
    /// least one entry.</summary>
    Success,
}

/// <summary>
/// One step in an indoor path. Position is the world-XYZ to walk to;
/// <see cref="CellId"/> identifies which static-graph cell owns the
/// step; <see cref="Kind"/> distinguishes "I'm just crossing the
/// room" (Floor) from "I'm passing through a structural doorway"
/// (Doorway). Doorway steps with a non-null
/// <see cref="ConnectionPolygonId"/> let the walk-tick locate the
/// corresponding Door entity (if any) and dispatch USE before
/// stepping through.
/// </summary>
internal readonly record struct IndoorWaypoint(
    Vector3 Position,
    uint CellId,
    WalkableNodeKind Kind,
    ushort? ConnectionPolygonId);

/// <summary>
/// Result of an <see cref="IndoorNavService.TryFindPath"/> call.
/// Waypoints are XYZ positions to step through in order; the last
/// waypoint is the snapped destination (may differ from the caller-
/// requested toXYZ if the request wasn't on the walkable mesh).
/// <para>
/// <see cref="PathCells"/> is the de-duplicated set of cells the
/// path is expected to traverse. The caller uses this to suppress
/// the walk-tick's default "stop on cell crossing" behaviour while
/// we're following a known-good multi-cell path.
/// </para>
/// </summary>
internal readonly record struct IndoorPathResult(
    IndoorPathStatus Status,
    IReadOnlyList<IndoorWaypoint> Waypoints,
    IReadOnlySet<uint> PathCells,
    string? Reason)
{
    public static IndoorPathResult Of(IndoorPathStatus status, string? reason = null)
        => new(status, Array.Empty<IndoorWaypoint>(),
               (IReadOnlySet<uint>)new HashSet<uint>(), reason);
}

/// <summary>
/// Process-lifetime telemetry counters for indoor-nav queries.
/// Exposed for logging at run summary; safe to read from any thread.
/// </summary>
internal sealed class IndoorNavTelemetry
{
    private long _success;
    private long _noPath;
    private long _notIndoor;
    private long _crossLandblock;
    private long _noGraph;
    private long _disabled;
    private long _graphsLoaded;
    private long _graphsFailed;
    private long _seedCellsTotal;
    private long _expandedCellsTotal;
    private long _expansionCalls;

    public long Success => Interlocked.Read(ref _success);
    public long NoPath => Interlocked.Read(ref _noPath);
    public long NotIndoor => Interlocked.Read(ref _notIndoor);
    public long CrossLandblock => Interlocked.Read(ref _crossLandblock);
    public long NoGraph => Interlocked.Read(ref _noGraph);
    public long Disabled => Interlocked.Read(ref _disabled);
    public long GraphsLoaded => Interlocked.Read(ref _graphsLoaded);
    public long GraphsFailed => Interlocked.Read(ref _graphsFailed);
    public long SeedCellsTotal => Interlocked.Read(ref _seedCellsTotal);
    public long ExpandedCellsTotal => Interlocked.Read(ref _expandedCellsTotal);
    public long ExpansionCalls => Interlocked.Read(ref _expansionCalls);

    internal void Record(IndoorPathStatus s)
    {
        switch (s)
        {
            case IndoorPathStatus.Success: Interlocked.Increment(ref _success); break;
            case IndoorPathStatus.NoPath: Interlocked.Increment(ref _noPath); break;
            case IndoorPathStatus.NotIndoor: Interlocked.Increment(ref _notIndoor); break;
            case IndoorPathStatus.CrossLandblock: Interlocked.Increment(ref _crossLandblock); break;
            case IndoorPathStatus.NoGraph: Interlocked.Increment(ref _noGraph); break;
            case IndoorPathStatus.Disabled: Interlocked.Increment(ref _disabled); break;
        }
    }

    internal void RecordGraphLoaded() => Interlocked.Increment(ref _graphsLoaded);
    internal void RecordGraphFailed() => Interlocked.Increment(ref _graphsFailed);

    internal void RecordExpansion(int seedCount, int expandedCount)
    {
        Interlocked.Add(ref _seedCellsTotal, seedCount);
        Interlocked.Add(ref _expandedCellsTotal, expandedCount);
        Interlocked.Increment(ref _expansionCalls);
    }

    public string Summary()
    {
        var calls = ExpansionCalls;
        var avgSeed = calls == 0 ? 0.0 : (double)SeedCellsTotal / calls;
        var avgExpanded = calls == 0 ? 0.0 : (double)ExpandedCellsTotal / calls;
        return $"indoor-nav: success={Success} no-path={NoPath} not-indoor={NotIndoor} "
             + $"cross-lb={CrossLandblock} no-graph={NoGraph} disabled={Disabled} "
             + $"graphs={GraphsLoaded} loaded ({GraphsFailed} failed) "
             + $"avg-seed={avgSeed:F1} avg-expanded={avgExpanded:F1} "
             + $"expansion-calls={calls}";
    }
}

internal sealed class IndoorNavService
{
    /// <summary>
    /// In AC's cell-id scheme, the lower 16 bits of a cell id
    /// distinguish indoor cells (>= 0x100, each is one room
    /// inside an EnvCell mesh) from outdoor cells (0x0001..0x00FF,
    /// each is one terrain tile inside a LandCell). The library
    /// only models indoor cells.
    /// </summary>
    private const ushort IndoorCellLowerBitsMin = 0x0100;

    private readonly bool _enabled;
    private readonly ConcurrentDictionary<ushort, Lazy<IndoorNavGraph?>> _cache = new();
    private readonly LandblockNavLoader? _loader;
    private readonly Pathfinder _pathfinder = new();
    private readonly Action<string> _log;
    /// <summary>
    /// Phase 3.1 — number of hops to expand the caller's seen-cells
    /// set through the static cell-connection graph BEFORE running
    /// A*. Models "look through doorway" awareness: a human player in
    /// a room can see/plan into adjacent rooms without physically
    /// entering them. Per-cell discovery of CONTENT (NPCs, mobs,
    /// items) is unchanged — only PATH planning gets the look-ahead.
    ///
    /// K=0 disables expansion (planner restricted to exactly the
    /// cells the caller marked seen).
    /// K=4 (default) gives a comfortable "I can see down this
    /// corridor + into the next two rooms" horizon while still
    /// forcing the bot to explore to discover distant areas.
    /// </summary>
    private readonly int _expansionHops;

    public IndoorNavTelemetry Telemetry { get; } = new();

    /// <summary>
    /// Construct a disabled instance (every TryFindPath returns
    /// Disabled). Used when AC_INDOOR_NAV is off or DAT files
    /// aren't available.
    /// </summary>
    public IndoorNavService(Action<string>? log = null)
    {
        _enabled = false;
        _loader = null;
        _log = log ?? Console.WriteLine;
        _expansionHops = 0;
    }

    /// <summary>
    /// Construct an enabled instance backed by <paramref
    /// name="loader"/>. The caller is responsible for having
    /// initialised <see cref="DatManager"/> before constructing
    /// the loader; we don't do it here because DatManager is a
    /// process-wide singleton with init costs we want at startup,
    /// not on first nav query.
    /// </summary>
    public IndoorNavService(
        LandblockNavLoader loader,
        Action<string>? log = null,
        int expansionHops = 4)
    {
        _enabled = true;
        _loader = loader;
        _log = log ?? Console.WriteLine;
        _expansionHops = Math.Max(0, expansionHops);
    }

    public bool IsEnabled => _enabled;

    /// <summary>True if <paramref name="cellId"/> looks like an
    /// indoor cell (lower 16 bits &gt;= 0x100).</summary>
    public static bool IsIndoorCell(uint cellId)
        => (cellId & 0xFFFFu) >= IndoorCellLowerBitsMin;

    /// <summary>Upper 16 bits of a cell id == the landblock id.</summary>
    public static ushort GetLandblockId(uint cellId)
        => (ushort)(cellId >> 16);

    /// <summary>
    /// Lazy-load the indoor nav graph for <paramref
    /// name="landblockId"/>. Returns null if the landblock has no
    /// indoor cells, or if loading failed (logged once).
    /// </summary>
    public IndoorNavGraph? GetOrLoad(ushort landblockId)
    {
        if (!_enabled || _loader is null)
            return null;

        var lazy = _cache.GetOrAdd(landblockId, id => new Lazy<IndoorNavGraph?>(() =>
        {
            try
            {
                var g = _loader.Load(id);
                if (g.CellCount == 0)
                {
                    _log($"[indoor-nav] landblock 0x{id:X4} has no indoor cells; caching as null");
                    return null;
                }
                Telemetry.RecordGraphLoaded();
                _log($"[indoor-nav] loaded landblock 0x{id:X4}: "
                    + $"cells={g.CellCount} bridges={g.WalkableBridgeCount} "
                    + $"walk-nodes={g.WalkableNodeCount}");
                return g;
            }
            catch (Exception ex)
            {
                Telemetry.RecordGraphFailed();
                _log($"[indoor-nav] FAILED to load landblock 0x{id:X4}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }, isThreadSafe: true));

        return lazy.Value;
    }

    /// <summary>
    /// Try to compute a walkable indoor path from <paramref
    /// name="fromCellId"/> @ <paramref name="fromXYZ"/> to <paramref
    /// name="toCellId"/> @ <paramref name="toXYZ"/>, restricted to
    /// cells in <paramref name="seenCells"/> (the bot's per-session
    /// fog-of-war). Returns a status code + (on Success) a list of
    /// XYZ waypoints.
    /// </summary>
    public IndoorPathResult TryFindPath(
        uint fromCellId,
        Vector3 fromXYZ,
        uint toCellId,
        Vector3 toXYZ,
        IReadOnlySet<uint> seenCells)
    {
        IndoorPathResult Record(IndoorPathResult r)
        {
            Telemetry.Record(r.Status);
            return r;
        }

        if (!_enabled)
            return Record(IndoorPathResult.Of(IndoorPathStatus.Disabled));

        if (!IsIndoorCell(fromCellId) || !IsIndoorCell(toCellId))
            return Record(IndoorPathResult.Of(
                IndoorPathStatus.NotIndoor,
                $"from=0x{fromCellId:X8} to=0x{toCellId:X8}"));

        var fromLb = GetLandblockId(fromCellId);
        var toLb = GetLandblockId(toCellId);
        if (fromLb != toLb)
            return Record(IndoorPathResult.Of(
                IndoorPathStatus.CrossLandblock,
                $"from=0x{fromLb:X4} to=0x{toLb:X4}"));

        var graph = GetOrLoad(fromLb);
        if (graph is null)
            return Record(IndoorPathResult.Of(
                IndoorPathStatus.NoGraph,
                $"landblock 0x{fromLb:X4} has no indoor graph"));

        // Phase 3.1 K-hop expansion: the caller's seenCells is the
        // bot's directly-observed set ("I saw an object here, or
        // I'm standing in this cell"). For planning purposes we
        // expand it by K hops through the static cell-connection
        // graph — modelling "I can see/plan into adjacent rooms
        // through doorways" the same way a human player can. The
        // bot's TRUE knowledge of WHERE CONTENT IS (NPCs, mobs,
        // items) is unchanged; only the PATH planner gets the
        // wider working set. Discovery still requires the bot to
        // observe content via worldState.Objects from server-side
        // visibility broadcasts.
        var seedSet = (IReadOnlySet<uint>)new HashSet<uint>(seenCells);
        // Always include the bot's current cell — it must be in
        // the working set or A* can't even snap the start node.
        if (IsIndoorCell(fromCellId) && GetLandblockId(fromCellId) == fromLb)
        {
            if (seedSet is HashSet<uint> hs1)
                hs1.Add(fromCellId);
        }
        var working = _expansionHops > 0
            ? ExpandViaConnections(graph, seedSet, _expansionHops)
            : seedSet;
        Telemetry.RecordExpansion(seedSet.Count, working.Count);

        // Pathfinder respects the walkableCells filter when snapping
        // BOTH endpoints; if the caller's seenCells is too small to
        // contain either endpoint, the snap fails and we get
        // Found=false with a parseable FailureReason. Pass null for
        // an empty set (means "no fog-of-war filter") to keep the
        // semantics predictable; callers that want strict fog must
        // pre-validate.
        IReadOnlySet<uint>? walkableCells =
            working.Count == 0 ? null : working;

        var result = _pathfinder.FindWalkablePath(
            graph, fromXYZ, toXYZ, walkableCells);

        if (!result.Found || result.Points.Count == 0)
            return Record(IndoorPathResult.Of(
                IndoorPathStatus.NoPath, result.FailureReason));

        var pathCells = new HashSet<uint>();
        var waypoints = new IndoorWaypoint[result.NodePath.Count];
        for (int i = 0; i < result.NodePath.Count; i++)
        {
            var nref = result.NodePath[i];
            pathCells.Add(nref.CellId);
            var node = graph.Cells[nref.CellId].WalkableNodes[nref.NodeIndex];
            waypoints[i] = new IndoorWaypoint(
                Position: result.Points[i],
                CellId: nref.CellId,
                Kind: node.Kind,
                ConnectionPolygonId: node.Kind == WalkableNodeKind.Doorway
                    ? node.ConnectionPolygonId
                    : null);
        }

        return Record(new IndoorPathResult(
            IndoorPathStatus.Success, waypoints, pathCells, null));
    }

    /// <summary>
    /// BFS expansion of a seen-cells seed through the static
    /// cell-connection graph. Returns a NEW set containing every
    /// cell reachable from any seed within <paramref name="hops"/>
    /// connection traversals.
    ///
    /// This is the planning-side "look through doorways" model:
    /// cells that the bot hasn't physically observed but that lie
    /// within K connection-hops of an observed cell can still be
    /// USED for routing. The caller's true knowledge of WHAT is in
    /// those cells remains discovery-only.
    ///
    /// Cells whose <see cref="CellConnection.OtherCellLoaded"/> is
    /// false are dangling edges (cross-landblock / missing DAT
    /// record) and are NOT followed.
    /// </summary>
    internal static IReadOnlySet<uint> ExpandViaConnections(
        IndoorNavGraph graph,
        IReadOnlySet<uint> seed,
        int hops)
    {
        var visited = new HashSet<uint>(seed);
        if (hops <= 0 || seed.Count == 0)
            return visited;

        var frontier = new List<uint>(seed);
        var next = new List<uint>();
        for (int h = 0; h < hops && frontier.Count > 0; h++)
        {
            next.Clear();
            foreach (var cellId in frontier)
            {
                if (!graph.Cells.TryGetValue(cellId, out var cell))
                    continue;
                foreach (var conn in cell.Connections)
                {
                    if (!conn.OtherCellLoaded)
                        continue;
                    var other = conn.OtherCellId;
                    if (visited.Add(other))
                        next.Add(other);
                }
            }
            (frontier, next) = (next, frontier);
        }
        return visited;
    }
}
