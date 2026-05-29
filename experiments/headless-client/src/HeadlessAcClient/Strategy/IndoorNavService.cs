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
/// Result of an <see cref="IndoorNavService.TryFindPath"/> call.
/// Waypoints are XYZ positions to step through in order; the last
/// waypoint is the snapped destination (may differ from the caller-
/// requested toXYZ if the request wasn't on the walkable mesh).
/// </summary>
internal readonly record struct IndoorPathResult(
    IndoorPathStatus Status,
    IReadOnlyList<Vector3> Waypoints,
    string? Reason)
{
    public static IndoorPathResult Of(IndoorPathStatus status, string? reason = null)
        => new(status, Array.Empty<Vector3>(), reason);
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

    public long Success => Interlocked.Read(ref _success);
    public long NoPath => Interlocked.Read(ref _noPath);
    public long NotIndoor => Interlocked.Read(ref _notIndoor);
    public long CrossLandblock => Interlocked.Read(ref _crossLandblock);
    public long NoGraph => Interlocked.Read(ref _noGraph);
    public long Disabled => Interlocked.Read(ref _disabled);
    public long GraphsLoaded => Interlocked.Read(ref _graphsLoaded);
    public long GraphsFailed => Interlocked.Read(ref _graphsFailed);

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

    public string Summary()
        => $"indoor-nav: success={Success} no-path={NoPath} not-indoor={NotIndoor} "
         + $"cross-lb={CrossLandblock} no-graph={NoGraph} disabled={Disabled} "
         + $"graphs={GraphsLoaded} loaded ({GraphsFailed} failed)";
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
    }

    /// <summary>
    /// Construct an enabled instance backed by <paramref
    /// name="loader"/>. The caller is responsible for having
    /// initialised <see cref="DatManager"/> before constructing
    /// the loader; we don't do it here because DatManager is a
    /// process-wide singleton with init costs we want at startup,
    /// not on first nav query.
    /// </summary>
    public IndoorNavService(LandblockNavLoader loader, Action<string>? log = null)
    {
        _enabled = true;
        _loader = loader;
        _log = log ?? Console.WriteLine;
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

        // Pathfinder respects the walkableCells filter when snapping
        // BOTH endpoints; if the caller's seenCells is too small to
        // contain either endpoint, the snap fails and we get
        // Found=false with a parseable FailureReason. Pass null for
        // an empty set (means "no fog-of-war filter") to keep the
        // semantics predictable; callers that want strict fog must
        // pre-validate.
        IReadOnlySet<uint>? walkableCells =
            seenCells.Count == 0 ? null : seenCells;

        var result = _pathfinder.FindWalkablePath(
            graph, fromXYZ, toXYZ, walkableCells);

        if (!result.Found || result.Points.Count == 0)
            return Record(IndoorPathResult.Of(
                IndoorPathStatus.NoPath, result.FailureReason));

        return Record(new IndoorPathResult(
            IndoorPathStatus.Success, result.Points, null));
    }
}
