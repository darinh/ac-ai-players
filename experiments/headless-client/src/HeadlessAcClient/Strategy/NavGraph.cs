// SPDX-License-Identifier: AGPL-3.0-or-later
//
// NavGraph — topological-semantic spatial memory for the bot.
//
// One global graph, persistent across sessions / characters / accounts.
// Replaces the per-landblock-JSON NavGraphRecorder. Designed for the
// whole game (academies, towns, dungeons, raids), not one location.
//
// Hierarchy (smallest to largest):
//
//   Region  = landblock (server-given, 16-bit id)
//   Place   = town / building / dungeon (clustered cells; name from
//             NPC dialog or LLM tag)
//   Area    = room / indoor cell / outdoor patch (cell-id-derived)
//   Node    = a specific position the bot has stood at
//             (deduped within MergeRadius)
//
// Edges connect nodes. Each edge has a kind {Walked, CrossedBoundary,
// UsedDoor, UsedPortal, UsedItem} and a cost. Pathfinding is A* over
// the edge graph using a Euclidean (Pythagorean) heuristic on node
// coordinates; admissibility comes from scaling the heuristic by the
// minimum cost-per-meter observed across all edges (so when a graph
// contains teleport-class edges with huge geometric span but tiny cost
// — a portal cast in 5 s across 5 km — the heuristic scale collapses
// toward 0 and A* degenerates to Dijkstra, which never pessimistically
// prunes the portal shortcut). Layer separation (L0/L1/L2 HPA*) is
// implicit in the edge-kind cost ordering, not a separate search
// structure.
//
// Edges are DIRECTED. (FromNodeId, ToNodeId) is an ordered pair; A→B
// does not imply B→A exists. Walked auto-edges are emitted only in
// the direction the bot actually traversed; the reverse edge appears
// later if and only if the bot walks back. This is the correct model
// for one-way game geometry: a cliff jump (drop down works, climb up
// doesn't), a one-way exit portal (academy → Holtburg only), a
// quest-fired teleport that fires once on first NPC interaction. The
// graph must NOT assume reverse traversability without evidence.
//
// Edge cost rule:
//   * Walked / CrossedBoundary — distance-scaled (baseCost × meters).
//     A continuous walk: longer = more expensive.
//   * UsedDoor / UsedPortal / UsedItem — fixed (baseCost only). The
//     geometric distance between endpoints is informational only;
//     the cost is the action time (open the door, cast the portal,
//     activate the item). Modeled as TWO SEPARATE NODES — the
//     entrance position and the exit position — connected by ONE
//     edge of the right kind. So a portal in Holtburg with exit in
//     the academy is: HoltburgPortalEntranceNode → UsedPortal(cost≈5s)
//     → AcademyPortalExitNode. The 5 km Euclidean gap between the
//     two nodes' positions reflects reality (the bot really did move
//     5 km of world space) but doesn't enter the edge cost. This
//     mirrors how a human plans: "walk to the portal, take it, walk
//     from where I end up to the destination" — three legs, two of
//     them walking, one teleport.
//
// Trips (executor-facing concept):
//
//   A "trip" is a contiguous walk-leg bounded by teleport-class edges
//   (UsedDoor, UsedPortal, UsedItem). A NavRoute returned by FindRoute
//   that contains K teleport edges is K+1 trips. Academy → Holtburg
//   typically resolves to 2 trips: (academy spawn → Calling Stone)
//   then UsedItem("Calling Stone") then (Holtburg arrival → goal).
//   The executor walks each trip head-down, then at the trip boundary
//   stops at the entrance node, fires the bound action (door click /
//   portal use / item use), and waits for the position-discontinuity
//   teleport event before starting the next trip from the exit node.
//   NavRouteStep.EdgeFromPrevious.Kind tells the executor which kind
//   of action to dispatch at each step boundary.
//
// Edge mortality (TODO, see ac-ai-players#76 — data model — and
// ac-ai-players#75 — path executor that populates the fields). The
// graph today treats every edge as permanent and unconditional. Three
// real-game cases violate that and deserve future modeling. They form
// TWO orthogonal axes: the MECHANISM (what action moves you — already
// captured by NavEdgeKind) and the LIFECYCLE (how long the edge stays
// valid — NOT yet captured).
//
//   1. Directional (HANDLED). Walked / UsedPortal / etc. are recorded
//      only in the direction the bot actually traversed. A one-way
//      academy → Holtburg exit portal will not produce a phantom
//      reverse edge from the planner's perspective. This is the only
//      one of the three already enforced by the data model.
//
//   2. Single-use / consumed (TODO). A Calling Stone is consumed on
//      first use. A one-shot quest trigger fires once. An NPC who
//      teleports you the first time you talk to them and never again.
//      Recorded as normal UsedItem / UsedPortal edges, the planner
//      will happily re-route through them. Future: UsesRemaining
//      counter on edge, decremented by the executor on each attempt,
//      with the executor calling PenalizeEdge(huge) when an attempt
//      fails to act.
//
//   3. Time-bounded / ephemeral (TODO). A mage-summoned portal exists
//      for ~30 seconds. A spell-summoned door, a temporary breach in
//      a wall, a monster that's blocking a corridor — all transient.
//      Recorded as a normal edge, the planner will route through an
//      edge that vanished hours ago. Future: ExpiresUtc / TtlSeconds
//      on edge, hard-filtered by the planner; observation logged for
//      training data ("we once saw a summoned portal here at time T")
//      but routing ignores expired edges.
//
// Portal TYPE matters because it pins down the lifecycle. The single
// NavEdgeKind.UsedPortal is too coarse. Real portal subtypes the bot
// will encounter:
//
//   * World portal — stationary world geometry (Holtburg lifestone
//     portal, academy exit portals). Permanent. Re-usable both ways
//     if both directions are observable (some are one-way).
//   * NPC-triggered teleport — an NPC moves you on dialog success
//     (Jonathan in the academy). Often single-use (gated by quest
//     state); may be re-usable if the NPC re-offers the service.
//   * Item-bound portal — using an inventory item moves you. Sub-cases:
//       - Consumed on use (Calling Stone, single-use teleport gems).
//       - Cooldown / charge-based (Recall stones, lifestone-tie).
//   * Summoned portal — a mage spell ("Portal Summon Self") creates
//     a portal object in the world for ~30 s. Ephemeral. Anyone in
//     range can use it. Lifecycle bound to a wall-clock TTL.
//   * Recall spell — built-in player spell (Lifestone Recall,
//     Allegiance Recall, Bindstone Recall). No world object; cast
//     from anywhere. Re-usable with cooldown.
//
// Future schema (not implemented yet): add NavEdgePortalSubtype enum
// and NavEdgeLifecycle { Permanent, SingleUse, Cooldown, Ephemeral,
// Conditional, Unknown } as orthogonal fields on the edge. Populate
// from the LLM goal-compiler ("this is a Calling Stone, mark the
// resulting edge SingleUse + Consumed") and from observed failures
// (executor calls PenalizeEdge on a use attempt that the server
// rejects). Until then, the planner is naively optimistic and the
// executor must defend itself at runtime via stall + re-route.
//
// Path executor contract (consumer side, lives in HandshakeDriver):
//
//   1. Strategy picks a goal entity / node.
//   2. Tactics calls FindRoute(here, goal) → NavRoute = ordered queue
//      of NavRouteStep. Dequeue head; that's the next waypoint.
//   3. Motor issues walk instructions toward the waypoint's Position,
//      tick-by-tick. Each tick check: did distance-to-waypoint shrink?
//      Player / creature blocking the lane is a real-game hazard.
//   4. Arrival: dist ≤ MergeRadius → dequeue next waypoint.
//   5. Stall (no progress for N consecutive ticks): use
//      FindNodesWithin(currentCell, currentPos, radius) to enumerate
//      reachable side-step candidates, call PenalizeEdge on the
//      blocked edge so A* avoids it, and re-call FindRoute(sidestep,
//      originalGoal). Splice the new route in front of the remaining
//      queue and continue.
//   6. On every tick, RecordVisit(currentCell, currentPos, utc) so
//      the per-tick walkability chain keeps the graph honest about
//      what was actually traversed.
//
// Each node also carries Observations — what entities (NPCs, items,
// monsters, portals) were visible from that position, with relative
// position + timestamps + sighting count. Entity lookup ("where did
// I last see X?") searches all observations across all nodes.
//
// Metadata (Town, Building, Floor, RoomName) is OPTIONAL on Place /
// Area / Node — null by default, filled in incrementally by:
//   * deterministic sources (landblock-id → region-name from ACE DB),
//   * heuristic clustering (indoor cells in same landblock → Building),
//   * LLM tagging (extract from NPC dialog / quest text),
//   * explicit TagPlace / TagArea / TagNode calls.
//
// Persistence: append-only JSONL under data/nav/. Six files, one per
// entity kind. Each line is a complete snapshot of the entity at that
// point in time; on load, last-write-wins by Id. The journal IS the
// training data the user asked for.
//
// Anti-hardcoding rule (UNCHANGED from NavGraphRecorder): we record
// what we OBSERVE. We never seed nodes / observations / metadata with
// hand-written facts.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy;

internal sealed class NavGraph : IDisposable
{
    /// <summary>
    /// Two visits closer than this in the same cell merge into the
    /// same node. Should be ≥ the bot's tick movement step so we
    /// don't spawn a fresh node on every receive cycle.
    /// </summary>
    public float MergeRadiusMeters { get; init; } = 4.0f;

    /// <summary>
    /// Per-tick walkability gate. Consecutive RecordVisit calls within
    /// this distance prove the bot WALKED between those positions
    /// (not teleported / lag-corrected / sync'd). Walked edges only
    /// form across a continuous chain of per-tick movements all under
    /// this threshold. Default 2.0m fits typical AC tick rates
    /// (5-10 Hz) and run speeds (3-6 m/s) with jitter headroom.
    /// </summary>
    public float MaxTickWalkMeters { get; init; } = 2.0f;

    /// <summary>
    /// Hard cap on the distance between two distinct nodes for an
    /// auto-Walked edge. Even if the per-tick chain stayed continuous,
    /// nodes farther apart than this require explicit RecordEdge —
    /// belt-and-suspenders against pathological recorder drift.
    /// </summary>
    public float MaxAutoEdgeMeters { get; init; } = 8.0f;

    /// <summary>
    /// No-op since the switch to append-on-write JSONL. Kept for
    /// back-compat with constructors that still pass it.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Default edge cost (seconds, used as Dijkstra weight) by kind.
    /// Walked = 1.0 per meter / 4 m/s walk speed. Door, portal, item
    /// numbers chosen to bias router toward walking when geometry
    /// allows, and toward portal-shortcuts when cross-region.
    /// </summary>
    public IReadOnlyDictionary<NavEdgeKind, float> EdgeKindBaseCost { get; init; } =
        new Dictionary<NavEdgeKind, float>
        {
            [NavEdgeKind.Walked]           = 1.0f,
            [NavEdgeKind.CrossedBoundary]  = 1.0f,
            [NavEdgeKind.UsedDoor]         = 0.5f,
            [NavEdgeKind.UsedPortal]       = 5.0f,
            [NavEdgeKind.UsedItem]         = 10.0f,
        };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private readonly string _directory;

    // Primary state — all keyed by GUID, owned by this graph.
    private readonly Dictionary<Guid, NavRegion>  _regions  = new();
    private readonly Dictionary<Guid, NavPlace>   _places   = new();
    private readonly Dictionary<Guid, NavArea>    _areas    = new();
    private readonly Dictionary<Guid, NavNode>    _nodes    = new();
    private readonly Dictionary<Guid, NavEdge>    _edges    = new();

    // Spatial / structural indexes (rebuilt on load; kept in sync on writes).
    private readonly Dictionary<uint, NavRegion>  _regionByLandblock = new();
    private readonly Dictionary<uint, NavArea>    _areaByCell        = new();
    private readonly Dictionary<uint, List<NavNode>> _nodesByCell    = new();
    // Adjacency index — per-node outgoing edges. Maintained on every
    // AddOrRefreshEdgeLocked so the bot can ask "from here, where can
    // I go?" in O(degree) without scanning all edges. Same edge object
    // is shared with _edges; mutations to cost/lastVerified are visible
    // through both views.
    private readonly Dictionary<Guid, List<NavEdge>> _outgoingByNode = new();

    // Continuation state (per-graph-instance; resets across sessions
    // intentionally — chronological Walked edges should NOT span a
    // restart since intervening state is unknown).
    private Guid? _lastVisitNodeId;
    private Vector3? _lastTickPosition;
    private uint? _lastTickCellId;

    // Append-only JSONL writers, held open for the graph lifetime.
    // Each Record* / Tag* / Ensure* call writes the affected entity
    // immediately so a crash loses nothing past the last write.
    private readonly Dictionary<string, StreamWriter> _writers = new();
    private bool _writeFailureLogged;
    private bool _disposed;
    // A* heuristic scale: the minimum observed (cost / geometric distance)
    // across all edges seen so far. Initialized to +infinity (no edges
    // yet → heuristic returns 0 → A* degenerates to Dijkstra, which is
    // optimal). Updated downward on every AddOrRefreshEdgeLocked. This
    // formulation keeps the heuristic admissible even when the graph
    // contains fixed-cost teleport edges (UsedPortal=5.0 across a 5000m
    // hop has cost/distance=0.001), which would otherwise blow past the
    // edge cost and prune the optimal portal-shortcut route.
    private float _minCostPerMeter = float.PositiveInfinity;

    public string Directory => _directory;

    public NavGraph(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.CurrentDirectory,
            "experiments", "headless-client", "data", "nav");
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nav] WARN create dir failed: {ex.GetType().Name}: {ex.Message}");
        }
        _minCostPerMeter = float.PositiveInfinity;
        TryLoadJournals();
    }

    // ─── Snapshots / queries ─────────────────────────────────────────

    public int RegionCount  { get { lock (_lock) return _regions.Count; } }
    public int PlaceCount   { get { lock (_lock) return _places.Count; } }
    public int AreaCount    { get { lock (_lock) return _areas.Count; } }
    public int NodeCount    { get { lock (_lock) return _nodes.Count; } }
    public int EdgeCount    { get { lock (_lock) return _edges.Count; } }

    public IReadOnlyList<NavNode>   SnapshotNodes()   { lock (_lock) return _nodes.Values.ToArray(); }
    public IReadOnlyList<NavEdge>   SnapshotEdges()   { lock (_lock) return _edges.Values.ToArray(); }
    public IReadOnlyList<NavArea>   SnapshotAreas()   { lock (_lock) return _areas.Values.ToArray(); }
    public IReadOnlyList<NavPlace>  SnapshotPlaces()  { lock (_lock) return _places.Values.ToArray(); }
    public IReadOnlyList<NavRegion> SnapshotRegions() { lock (_lock) return _regions.Values.ToArray(); }

    public NavNode?   FindNode(Guid id)   { lock (_lock) return _nodes.TryGetValue(id, out var n) ? n : null; }
    public NavArea?   FindArea(Guid id)   { lock (_lock) return _areas.TryGetValue(id, out var a) ? a : null; }
    public NavPlace?  FindPlace(Guid id)  { lock (_lock) return _places.TryGetValue(id, out var p) ? p : null; }
    public NavRegion? FindRegion(Guid id) { lock (_lock) return _regions.TryGetValue(id, out var r) ? r : null; }

    /// <summary>
    /// Returns the closest existing node to (cellId, position) within
    /// <paramref name="maxDistance"/>. Same-cell only; cross-cell snap
    /// is not meaningful for an indoor world (cells are walled off).
    /// </summary>
    public NavNode? FindNearestNode(uint cellId, Vector3 position, float maxDistance = 50f)
    {
        lock (_lock)
        {
            if (!_nodesByCell.TryGetValue(cellId, out var list) || list.Count == 0) return null;
            NavNode? best = null;
            var bestD = maxDistance;
            foreach (var n in list)
            {
                var d = Vector3.Distance(n.Position, position);
                if (d <= bestD)
                {
                    best = n;
                    bestD = d;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Returns every node within <paramref name="radiusMeters"/> of
    /// <paramref name="position"/>, sorted nearest-first. Searches the
    /// given cell first, then every other cell in the same landblock
    /// (so the path executor can find a fall-back waypoint when stuck
    /// mid-walk, e.g. a player blocks the doorway and a sidestep node
    /// in the adjacent outdoor cell is the way around).
    /// </summary>
    public IReadOnlyList<(NavNode Node, float Distance)> FindNodesWithin(
        uint cellId, Vector3 position, float radiusMeters)
    {
        lock (_lock)
        {
            var landblock = cellId >> 16;
            var hits = new List<(NavNode, float)>();
            foreach (var (cid, list) in _nodesByCell)
            {
                if ((cid >> 16) != landblock) continue;
                foreach (var n in list)
                {
                    var d = Vector3.Distance(n.Position, position);
                    if (d <= radiusMeters) hits.Add((n, d));
                }
            }
            hits.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return hits;
        }
    }

    /// <summary>
    /// Soft-blocks an edge by multiplying its cost. Use when the path
    /// executor detects the bot stopped making progress along this edge
    /// (a player or creature blocking the path, locked door, NPC dialog
    /// in progress). A* will route around it on the next FindRoute,
    /// without permanently corrupting the graph — call
    /// <see cref="RestoreEdgeCost"/> once the obstacle clears, or let
    /// the next successful traversal refresh the minimum-observed cost.
    /// </summary>
    public void PenalizeEdge(Guid edgeId, float costMultiplier)
    {
        if (costMultiplier <= 1f) return;
        lock (_lock)
        {
            if (_edges.TryGetValue(edgeId, out var e))
            {
                e.CostSeconds *= costMultiplier;
                Append("edges", EdgeDto.From(e));
            }
        }
    }

    /// <summary>
    /// Search all observations across all nodes for entries matching
    /// <paramref name="namePattern"/> (case-insensitive substring).
    /// Returns most-recent sighting first.
    /// </summary>
    public IReadOnlyList<EntitySighting> FindEntity(string namePattern)
    {
        if (string.IsNullOrEmpty(namePattern)) return Array.Empty<EntitySighting>();
        lock (_lock)
        {
            var hits = new List<EntitySighting>();
            foreach (var node in _nodes.Values)
            {
                foreach (var obs in node.Observations)
                {
                    if (obs.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new EntitySighting(node, obs));
                }
            }
            hits.Sort((a, b) => b.Observation.LastSeenUtc.CompareTo(a.Observation.LastSeenUtc));
            return hits;
        }
    }

    public IReadOnlyList<NavPlace> QueryPlaces(string? name = null, PlaceKind? kind = null, string? tag = null)
    {
        lock (_lock)
        {
            return _places.Values.Where(p =>
                (name is null || (p.Name is not null && p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))) &&
                (kind is null || p.Kind == kind.Value) &&
                (tag is null  || p.Tags.Contains(tag))).ToArray();
        }
    }

    public IReadOnlyList<NavArea> QueryAreas(Guid? placeId = null, AreaKind? kind = null,
                                              int? floor = null, string? roomName = null, string? tag = null)
    {
        lock (_lock)
        {
            return _areas.Values.Where(a =>
                (placeId  is null || a.PlaceId == placeId.Value) &&
                (kind     is null || a.Kind == kind.Value) &&
                (floor    is null || a.Floor == floor.Value) &&
                (roomName is null || (a.RoomName is not null && a.RoomName.Contains(roomName, StringComparison.OrdinalIgnoreCase))) &&
                (tag      is null || a.Tags.Contains(tag))).ToArray();
        }
    }

    // ─── Hierarchy upsert ────────────────────────────────────────────

    /// <summary>
    /// Idempotent: returns the existing region for this landblock, or
    /// creates one. Region.Name remains null until TagRegion fills it.
    /// </summary>
    public Guid EnsureRegion(uint landblockId)
    {
        lock (_lock)
        {
            if (_regionByLandblock.TryGetValue(landblockId, out var existing)) return existing.Id;
            var r = new NavRegion { Id = Guid.NewGuid(), LandblockId = landblockId };
            _regions[r.Id] = r;
            _regionByLandblock[landblockId] = r;
            Append("regions", RegionDto.From(r));
            return r.Id;
        }
    }

    /// <summary>
    /// Returns the existing area for this cell or creates one. Parent
    /// Place is auto-created (Kind=Unknown) so the caller doesn't
    /// have to know whether the cell is indoor or outdoor.
    /// </summary>
    public Guid EnsureArea(uint cellId, AreaKind kind = AreaKind.Unknown)
    {
        lock (_lock)
        {
            if (_areaByCell.TryGetValue(cellId, out var existing)) return existing.Id;

            var landblock = cellId >> 16;
            var regionId  = EnsureRegionLocked(landblock);

            // Default: one Place per Region. Heuristic clustering of
            // cells into multi-Place groupings is future work — for now,
            // every cell in landblock X belongs to the (sole) Place
            // anchored to that region. TagPlace can promote it later.
            var place = _places.Values.FirstOrDefault(p => p.RegionId == regionId);
            Guid placeId;
            if (place is null)
            {
                var p = new NavPlace { Id = Guid.NewGuid(), RegionId = regionId, Kind = PlaceKind.Unknown };
                _places[p.Id] = p;
                Append("places", PlaceDto.From(p));
                placeId = p.Id;
            }
            else
            {
                placeId = place.Id;
            }

            var a = new NavArea { Id = Guid.NewGuid(), CellId = cellId, PlaceId = placeId, Kind = kind };
            _areas[a.Id] = a;
            _areaByCell[cellId] = a;
            Append("areas", AreaDto.From(a));
            return a.Id;
        }
    }

    // ─── Recording ───────────────────────────────────────────────────

    /// <summary>
    /// Records the bot's current position. Returns the node Id —
    /// either an existing nearby node (visit-bumped) or a newly-
    /// created one.
    ///
    /// Auto-Walked-edge formation requires a verified continuous
    /// per-tick chain: the previous RecordVisit must have been within
    /// MaxTickWalkMeters of this one (proving the bot WALKED, not
    /// teleported or lag-corrected). Any tick that jumps farther
    /// breaks the chain, and the next distinct node will NOT
    /// auto-edge. The driver should call this every tick so the chain
    /// stays continuous.
    /// </summary>
    public Guid RecordVisit(uint cellId, Vector3 position, DateTimeOffset utc)
    {
        if (cellId == 0)
            throw new ArgumentException("cellId must be non-zero", nameof(cellId));

        lock (_lock)
        {
            // Per-tick walkability gate. If we have a previous tick
            // position and it's within MaxTickWalkMeters AND the cell
            // is unchanged or both cells are outdoor terrain in the
            // SAME landblock, the bot walked since the last tick —
            // chain continues. Otherwise break the chain so no auto-
            // edge forms across the gap. Same-landblock is required
            // because cell-local positions aren't comparable across
            // landblock boundaries (every landblock's origin is its
            // own SW corner).
            bool chainContinuous = false;
            if (_lastTickPosition is Vector3 lastPos && _lastTickCellId is uint lastCell)
            {
                var sameLandblock = (lastCell >> 16) == (cellId >> 16);
                var bothOutdoor = (lastCell & 0xFFFFu) < 0x100u &&
                                  (cellId   & 0xFFFFu) < 0x100u;
                var sameCellOrOutdoor = lastCell == cellId || (sameLandblock && bothOutdoor);
                var tickDelta = Vector3.Distance(lastPos, position);
                chainContinuous = sameCellOrOutdoor && tickDelta <= MaxTickWalkMeters;
            }
            if (!chainContinuous) _lastVisitNodeId = null;

            var areaId = EnsureAreaLocked(cellId, AreaKind.Unknown);
            Guid nodeId;

            // Dedup: nearest existing node in this cell within MergeRadius
            NavNode? near = null;
            if (_nodesByCell.TryGetValue(cellId, out var inCell))
            {
                var bestD = MergeRadiusMeters;
                foreach (var n in inCell)
                {
                    var d = Vector3.Distance(n.Position, position);
                    if (d <= bestD) { near = n; bestD = d; }
                }
            }
            if (near is not null)
            {
                near.LastSeenUtc = utc;
                near.VisitCount++;
                nodeId = near.Id;
                Append("nodes", NodeDto.From(near));
            }
            else
            {
                var landblock = cellId >> 16;
                var n = new NavNode
                {
                    Id = Guid.NewGuid(),
                    CellId = cellId,
                    Landblock = landblock,
                    Position = position,
                    FirstSeenUtc = utc,
                    AreaId = areaId,
                };
                n.LastSeenUtc = utc;
                n.VisitCount = 1;
                _nodes[n.Id] = n;
                if (!_nodesByCell.TryGetValue(cellId, out var list))
                    _nodesByCell[cellId] = list = new();
                list.Add(n);
                nodeId = n.Id;
                Append("nodes", NodeDto.From(n));
            }

            // Auto-edge: only when the per-tick chain has been
            // continuous since the previous distinct node AND the
            // straight-line distance is bounded (belt-and-suspenders).
            if (_lastVisitNodeId is Guid prev && prev != nodeId &&
                _nodes.TryGetValue(prev, out var prevNode))
            {
                var dist = Vector3.Distance(prevNode.Position, position);
                if (dist <= MaxAutoEdgeMeters)
                {
                    AddOrRefreshEdgeLocked(prev, nodeId, NavEdgeKind.Walked,
                        useItemName: null, useObjectGuid: null,
                        cost: EdgeKindBaseCost.GetValueOrDefault(NavEdgeKind.Walked, 1.0f) * Math.Max(dist, 0.1f),
                        utc);
                }
            }

            _lastVisitNodeId = nodeId;
            _lastTickPosition = position;
            _lastTickCellId = cellId;
            return nodeId;
        }
    }

    /// <summary>
    /// Explicitly invalidate the per-tick chain so the next RecordVisit
    /// will not auto-edge from the previous one. Call when the driver
    /// KNOWS a non-walked transition just happened (portal use,
    /// inventory item teleport, cross-landblock, login warp).
    /// </summary>
    public void BreakWalkedChain()
    {
        lock (_lock)
        {
            _lastVisitNodeId = null;
            _lastTickPosition = null;
            _lastTickCellId = null;
        }
    }

    /// <summary>
    /// Records that an entity was observed at the given world position
    /// while the bot was at <paramref name="fromNodeId"/>. Updates the
    /// existing observation (matched by wcid + name + rounded position)
    /// or appends a new one. Distance and relative position are derived
    /// from the node's position.
    /// </summary>
    public void RecordObservation(Guid fromNodeId, uint? wcid, string name,
                                   Vector3 entityPosition, EntityKind kind,
                                   DateTimeOffset utc)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_lock)
        {
            if (!_nodes.TryGetValue(fromNodeId, out var node)) return;
            var rel = entityPosition - node.Position;
            var dist = rel.Length();

            // Dedup key: (wcid, name, position rounded to integer
            // meters). Re-sightings update LastSeenUtc and bump count.
            EntityObservation? existing = null;
            foreach (var o in node.Observations)
            {
                if (o.Wcid == wcid &&
                    string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    (int)Math.Round(o.RelativePosition.X) == (int)Math.Round(rel.X) &&
                    (int)Math.Round(o.RelativePosition.Y) == (int)Math.Round(rel.Y) &&
                    (int)Math.Round(o.RelativePosition.Z) == (int)Math.Round(rel.Z))
                {
                    existing = o;
                    break;
                }
            }
            if (existing is not null)
            {
                existing.LastSeenUtc = utc;
                existing.SightingCount++;
                Append("observations", ObservationDto.From(fromNodeId, existing));
            }
            else
            {
                var obs = new EntityObservation
                {
                    Wcid = wcid,
                    Name = name,
                    RelativePosition = rel,
                    Distance = dist,
                    Kind = kind,
                    FirstSeenUtc = utc,
                    LastSeenUtc = utc,
                    SightingCount = 1,
                };
                node.Observations.Add(obs);
                Append("observations", ObservationDto.From(fromNodeId, obs));
            }
        }
    }

    /// <summary>
    /// Records an explicit edge (caller-provided kind). Used by the
    /// driver for non-Walked transitions: door usage, portal weenie,
    /// inventory item teleport (Calling Stone, recall, gem), and
    /// passive landblock-boundary crossings. Walked edges are
    /// auto-created by RecordVisit; this method is for everything else.
    ///
    /// Cost rule by kind:
    ///   * Walked / CrossedBoundary — baseCost × geometric distance
    ///     (continuous traversal scales with how far you walked).
    ///   * UsedDoor / UsedPortal / UsedItem — baseCost only
    ///     (a fixed action time independent of endpoint separation;
    ///     a portal can shortcut across the entire world for the same
    ///     5-second cast time).
    /// </summary>
    public Guid RecordEdge(Guid fromNodeId, Guid toNodeId, NavEdgeKind kind,
                            string? useItemName, uint? useObjectGuid, DateTimeOffset utc)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(fromNodeId, out var fromNode) ||
                !_nodes.TryGetValue(toNodeId,   out var toNode))
                throw new ArgumentException("Edge endpoints must be existing nodes");
            var baseCost = EdgeKindBaseCost.GetValueOrDefault(kind, 1.0f);
            var cost = kind switch
            {
                NavEdgeKind.Walked           => baseCost * Math.Max(Vector3.Distance(fromNode.Position, toNode.Position), 0.1f),
                NavEdgeKind.CrossedBoundary  => baseCost * Math.Max(Vector3.Distance(fromNode.Position, toNode.Position), 0.1f),
                _                            => baseCost,
            };
            return AddOrRefreshEdgeLocked(fromNodeId, toNodeId, kind, useItemName, useObjectGuid, cost, utc);
        }
    }

    // ─── Metadata tagging ────────────────────────────────────────────

    public void TagRegion(Guid regionId, string? name)
    {
        lock (_lock)
        {
            if (!_regions.TryGetValue(regionId, out var r)) return;
            if (name is not null) r.Name = name;
            Append("regions", RegionDto.From(r));
        }
    }

    public void TagPlace(Guid placeId, string? name = null, PlaceKind? kind = null, IEnumerable<string>? addTags = null)
    {
        lock (_lock)
        {
            if (!_places.TryGetValue(placeId, out var p)) return;
            if (name is not null) p.Name = name;
            if (kind is not null) p.Kind = kind.Value;
            if (addTags is not null) foreach (var t in addTags) p.Tags.Add(t);
            Append("places", PlaceDto.From(p));
        }
    }

    public void TagArea(Guid areaId, AreaKind? kind = null, int? floor = null,
                         string? roomName = null, IEnumerable<string>? addTags = null)
    {
        lock (_lock)
        {
            if (!_areas.TryGetValue(areaId, out var a)) return;
            if (kind is not null) a.Kind = kind.Value;
            if (floor is not null) a.Floor = floor.Value;
            if (roomName is not null) a.RoomName = roomName;
            if (addTags is not null) foreach (var t in addTags) a.Tags.Add(t);
            Append("areas", AreaDto.From(a));
        }
    }

    public void TagNode(Guid nodeId, IEnumerable<string>? addTags = null)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(nodeId, out var n)) return;
            if (addTags is not null) foreach (var t in addTags) n.Tags.Add(t);
            Append("nodes", NodeDto.From(n));
        }
    }

    // ─── Pathfinding (Dijkstra over all edges) ───────────────────────

    /// <summary>
    /// A* over the directed edge graph, with a Euclidean heuristic on
    /// node coordinates. Edge cost = the edge's CostSeconds (set
    /// per-kind on creation; kind-based defaults bias toward walking
    /// for short hops and portals for cross-region).
    ///
    /// Heuristic admissibility: every edge's cost ≥ MinEdgeBaseCost ×
    /// geometric distance, so MinEdgeBaseCost × straight-line distance
    /// is a lower bound on the remaining cost. A* with an admissible
    /// heuristic returns the same optimal path as Dijkstra but expands
    /// far fewer nodes — especially on large open-world graphs.
    /// Returns null if no route exists.
    /// </summary>
    public NavRoute? FindRoute(Guid startNodeId, Guid goalNodeId)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(startNodeId, out var startNode) ||
                !_nodes.TryGetValue(goalNodeId,  out var goalNode)) return null;
            if (startNodeId == goalNodeId)
                return new NavRoute(new[] { new NavRouteStep(startNode, null) }, 0f);

            // Heuristic scale: 0 if no edges seen yet (Dijkstra), else
            // the smallest cost-per-meter ratio observed across all
            // edges (kept admissible by construction in
            // AddOrRefreshEdgeLocked).
            var hScale = float.IsPositiveInfinity(_minCostPerMeter) ? 0f : _minCostPerMeter;
            var gScore   = new Dictionary<Guid, float> { [startNodeId] = 0f };
            var fScore   = new Dictionary<Guid, float> { [startNodeId] = hScale * Vector3.Distance(startNode.Position, goalNode.Position) };
            var prevEdge = new Dictionary<Guid, NavEdge>();
            var open     = new HashSet<Guid> { startNodeId };
            var closed   = new HashSet<Guid>();

            // Naive O(V) min-pick over the open set — fine for the
            // current node count (sub-10k); switch to a binary heap if
            // the graph grows beyond that.
            while (open.Count > 0)
            {
                Guid? bestId = null;
                var bestF = float.PositiveInfinity;
                foreach (var id in open)
                {
                    if (fScore.TryGetValue(id, out var f) && f < bestF)
                    {
                        bestId = id;
                        bestF = f;
                    }
                }
                if (bestId is null) return null;
                var current = bestId.Value;
                if (current == goalNodeId) break;
                open.Remove(current);
                closed.Add(current);

                if (!_outgoingByNode.TryGetValue(current, out var outs)) continue;
                var curG = gScore[current];
                foreach (var e in outs)
                {
                    if (closed.Contains(e.ToNodeId)) continue;
                    var tentativeG = curG + e.CostSeconds;
                    if (!gScore.TryGetValue(e.ToNodeId, out var existingG) || tentativeG < existingG)
                    {
                        gScore[e.ToNodeId] = tentativeG;
                        prevEdge[e.ToNodeId] = e;
                        var toNode = _nodes[e.ToNodeId];
                        fScore[e.ToNodeId] = tentativeG +
                            hScale * Vector3.Distance(toNode.Position, goalNode.Position);
                        open.Add(e.ToNodeId);
                    }
                }
            }

            if (!gScore.ContainsKey(goalNodeId)) return null;

            // Reconstruct
            var stepsRev = new List<NavRouteStep>();
            var cursor = goalNodeId;
            while (cursor != startNodeId)
            {
                var e = prevEdge[cursor];
                stepsRev.Add(new NavRouteStep(_nodes[cursor], e));
                cursor = e.FromNodeId;
            }
            stepsRev.Add(new NavRouteStep(startNode, null));
            stepsRev.Reverse();
            return new NavRoute(stepsRev, gScore[goalNodeId]);
        }
    }

    /// <summary>
    /// Returns the outgoing edges (and their destination nodes) from
    /// <paramref name="nodeId"/>. This is the "from here, where can I
    /// go?" query the bot uses to enumerate immediate options without
    /// scanning the whole edge table. Returns an empty list if the
    /// node is unknown or has no outgoing edges.
    /// </summary>
    public IReadOnlyList<NavConnection> GetOutgoingConnections(Guid nodeId)
    {
        lock (_lock)
        {
            if (!_outgoingByNode.TryGetValue(nodeId, out var outs) || outs.Count == 0)
                return Array.Empty<NavConnection>();
            var result = new List<NavConnection>(outs.Count);
            foreach (var e in outs)
            {
                if (_nodes.TryGetValue(e.ToNodeId, out var to))
                    result.Add(new NavConnection(e, to));
            }
            return result;
        }
    }

    /// <summary>
    /// Find the most-recent sighting of <paramref name="namePattern"/>
    /// and route to the node where it was observed. Returns null if
    /// the entity was never observed or no route exists. The caller
    /// should still walk the last leg (node → entity.RelativePosition)
    /// directly since entities move; the route gets you to the
    /// neighborhood, not the entity's last byte-position.
    /// </summary>
    public NavRoute? FindRouteToEntity(Guid startNodeId, string namePattern)
    {
        var sightings = FindEntity(namePattern);
        if (sightings.Count == 0) return null;
        // Try most-recent-first; fall through to older sightings if
        // earlier ones are unreachable.
        foreach (var s in sightings)
        {
            var route = FindRoute(startNodeId, s.Node.Id);
            if (route is not null) return route;
        }
        return null;
    }

    // ─── Persistence ─────────────────────────────────────────────────

    public void Flush()
    {
        lock (_lock)
        {
            foreach (var w in _writers.Values)
            {
                try { w.Flush(); } catch { /* best effort */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var w in _writers.Values)
            {
                try { w.Flush(); w.Dispose(); } catch { /* best effort */ }
            }
            _writers.Clear();
        }
    }

    // ─── Internals ───────────────────────────────────────────────────

    private Guid EnsureRegionLocked(uint landblock)
    {
        if (_regionByLandblock.TryGetValue(landblock, out var existing)) return existing.Id;
        var r = new NavRegion { Id = Guid.NewGuid(), LandblockId = landblock };
        _regions[r.Id] = r;
        _regionByLandblock[landblock] = r;
        Append("regions", RegionDto.From(r));
        return r.Id;
    }

    private Guid EnsureAreaLocked(uint cellId, AreaKind kind)
    {
        if (_areaByCell.TryGetValue(cellId, out var existing)) return existing.Id;
        var landblock = cellId >> 16;
        var regionId  = EnsureRegionLocked(landblock);
        var place = _places.Values.FirstOrDefault(p => p.RegionId == regionId);
        Guid placeId;
        if (place is null)
        {
            var p = new NavPlace { Id = Guid.NewGuid(), RegionId = regionId, Kind = PlaceKind.Unknown };
            _places[p.Id] = p;
            Append("places", PlaceDto.From(p));
            placeId = p.Id;
        }
        else placeId = place.Id;
        var a = new NavArea { Id = Guid.NewGuid(), CellId = cellId, PlaceId = placeId, Kind = kind };
        _areas[a.Id] = a;
        _areaByCell[cellId] = a;
        Append("areas", AreaDto.From(a));
        return a.Id;
    }

    private Guid AddOrRefreshEdgeLocked(Guid fromId, Guid toId, NavEdgeKind kind,
                                         string? useItemName, uint? useObjectGuid,
                                         float cost, DateTimeOffset utc)
    {
        // Edges are de-duped by (from, to, kind, useItemName, useObjectGuid).
        foreach (var e in _edges.Values)
        {
            if (e.FromNodeId == fromId && e.ToNodeId == toId && e.Kind == kind &&
                e.UseItemName == useItemName && e.UseObjectGuid == useObjectGuid)
            {
                e.LastVerifiedUtc = utc;
                // Use the minimum observed cost (best-known traversal).
                if (cost < e.CostSeconds) e.CostSeconds = cost;
                UpdateMinCostPerMeterLocked(fromId, toId, e.CostSeconds);
                Append("edges", EdgeDto.From(e));
                return e.Id;
            }
        }
        var edge = new NavEdge
        {
            Id = Guid.NewGuid(),
            FromNodeId = fromId,
            ToNodeId = toId,
            Kind = kind,
            UseItemName = useItemName,
            UseObjectGuid = useObjectGuid,
            CostSeconds = cost,
            FirstVerifiedUtc = utc,
            LastVerifiedUtc = utc,
        };
        _edges[edge.Id] = edge;
        if (!_outgoingByNode.TryGetValue(fromId, out var outs))
            _outgoingByNode[fromId] = outs = new();
        outs.Add(edge);
        UpdateMinCostPerMeterLocked(fromId, toId, cost);
        Append("edges", EdgeDto.From(edge));
        return edge.Id;
    }

    // Keeps the A* heuristic admissible by tracking the smallest
    // observed cost-per-meter ratio across all edges. See the
    // _minCostPerMeter field comment for why this matters.
    private void UpdateMinCostPerMeterLocked(Guid fromId, Guid toId, float cost)
    {
        if (!_nodes.TryGetValue(fromId, out var fromNode) ||
            !_nodes.TryGetValue(toId,   out var toNode)) return;
        var dist = Math.Max(0.1f, Vector3.Distance(fromNode.Position, toNode.Position));
        var ratio = cost / dist;
        if (ratio < _minCostPerMeter) _minCostPerMeter = ratio;
    }

    private void Append(string kind, object dto)
    {
        if (_disposed) return;
        try
        {
            if (!_writers.TryGetValue(kind, out var w))
            {
                var path = Path.Combine(_directory, $"{kind}.jsonl");
                // FileShare.Read so external tools (jq, tail -f) can
                // read the journal while the bot is running.
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                w = new StreamWriter(fs) { AutoFlush = true };
                _writers[kind] = w;
            }
            w.WriteLine(JsonSerializer.Serialize(dto, dto.GetType(), JsonOpts));
        }
        catch (Exception ex)
        {
            if (!_writeFailureLogged)
            {
                _writeFailureLogged = true;
                Console.Error.WriteLine($"[nav] WARN first journal write failure ({kind}): {ex.GetType().Name}: {ex.Message}; future failures suppressed");
            }
        }
    }

    private void TryLoadJournals()
    {
        try
        {
            // Stage 1: read every line of every journal. Same Id may
            // appear multiple times across the file (append-on-write);
            // last-wins dedup happens automatically via dict overwrite.
            LoadJournal("regions", line => {
                var d = JsonSerializer.Deserialize<RegionDto>(line, JsonOpts);
                if (d is not null) { var r = d.To(); _regions[r.Id] = r; }
            });
            LoadJournal("places", line => {
                var d = JsonSerializer.Deserialize<PlaceDto>(line, JsonOpts);
                if (d is not null) { var p = d.To(); _places[p.Id] = p; }
            });
            LoadJournal("areas", line => {
                var d = JsonSerializer.Deserialize<AreaDto>(line, JsonOpts);
                if (d is not null) { var a = d.To(); _areas[a.Id] = a; }
            });
            LoadJournal("nodes", line => {
                var d = JsonSerializer.Deserialize<NodeDto>(line, JsonOpts);
                if (d is not null) { var n = d.To(); _nodes[n.Id] = n; }
            });
            LoadJournal("edges", line => {
                var d = JsonSerializer.Deserialize<EdgeDto>(line, JsonOpts);
                if (d is not null) { var e = d.To(); _edges[e.Id] = e; }
            });

            // Stage 2: rebuild spatial / structural indexes from the
            // deduplicated entity dictionaries.
            foreach (var r in _regions.Values) _regionByLandblock[r.LandblockId] = r;
            foreach (var a in _areas.Values)   _areaByCell[a.CellId] = a;
            foreach (var n in _nodes.Values)
            {
                if (!_nodesByCell.TryGetValue(n.CellId, out var list))
                    _nodesByCell[n.CellId] = list = new();
                list.Add(n);
            }
            // Outgoing-edges adjacency: bucketize once after dedup so
            // node-centric "where can I go from here?" queries are O(degree).
            // Also re-seed the A* heuristic scale from the loaded edges.
            foreach (var e in _edges.Values)
            {
                if (!_outgoingByNode.TryGetValue(e.FromNodeId, out var outs))
                    _outgoingByNode[e.FromNodeId] = outs = new();
                outs.Add(e);
                UpdateMinCostPerMeterLocked(e.FromNodeId, e.ToNodeId, e.CostSeconds);
            }

            // Stage 3: observations — same-key dedup since the journal
            // is append-only. Key: (NodeId, Wcid, Name, RoundedPos).
            // Last-write-wins via dictionary overwrite, then attach.
            var obsByKey = new Dictionary<(Guid, uint?, string, int, int, int), ObservationDto>();
            LoadJournal("observations", line => {
                var d = JsonSerializer.Deserialize<ObservationDto>(line, JsonOpts);
                if (d is null) return;
                var key = (d.NodeId, d.Wcid, d.Name.ToLowerInvariant(),
                    (int)Math.Round(d.RelativePosition.X),
                    (int)Math.Round(d.RelativePosition.Y),
                    (int)Math.Round(d.RelativePosition.Z));
                obsByKey[key] = d;
            });
            foreach (var d in obsByKey.Values)
            {
                if (_nodes.TryGetValue(d.NodeId, out var n))
                    n.Observations.Add(d.ToObs());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nav] WARN journal load failed: {ex.GetType().Name}: {ex.Message}; starting fresh");
        }
    }

    private void LoadJournal(string kind, Action<string> consume)
    {
        var path = Path.Combine(_directory, $"{kind}.jsonl");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { consume(line); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[nav] WARN skipping bad line in {kind}.jsonl: {ex.Message}");
            }
        }
    }

    // ─── DTOs (Vector3 serialization workaround + stable schema) ─────

    internal sealed record Vec3Dto(float X, float Y, float Z)
    {
        public static Vec3Dto From(Vector3 v) => new(v.X, v.Y, v.Z);
        public Vector3 To() => new(X, Y, Z);
    }

    internal sealed record RegionDto(Guid Id, uint LandblockId, string? Name)
    {
        public static RegionDto From(NavRegion r) => new(r.Id, r.LandblockId, r.Name);
        public NavRegion To() => new() { Id = Id, LandblockId = LandblockId, Name = Name };
    }

    internal sealed record PlaceDto(Guid Id, Guid RegionId, string? Name, PlaceKind Kind, string[] Tags)
    {
        public static PlaceDto From(NavPlace p) => new(p.Id, p.RegionId, p.Name, p.Kind, p.Tags.ToArray());
        public NavPlace To() {
            var p = new NavPlace { Id = Id, RegionId = RegionId, Kind = Kind };
            p.Name = Name;
            foreach (var t in Tags) p.Tags.Add(t);
            return p;
        }
    }

    internal sealed record AreaDto(Guid Id, uint CellId, Guid PlaceId, AreaKind Kind, int? Floor, string? RoomName, string[] Tags)
    {
        public static AreaDto From(NavArea a) => new(a.Id, a.CellId, a.PlaceId, a.Kind, a.Floor, a.RoomName, a.Tags.ToArray());
        public NavArea To() {
            var a = new NavArea { Id = Id, CellId = CellId, PlaceId = PlaceId, Kind = Kind };
            a.Floor = Floor;
            a.RoomName = RoomName;
            foreach (var t in Tags) a.Tags.Add(t);
            return a;
        }
    }

    internal sealed record NodeDto(Guid Id, uint CellId, uint Landblock, Vec3Dto Position,
                                    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc,
                                    int VisitCount, Guid? AreaId, string[] Tags)
    {
        public static NodeDto From(NavNode n) => new(n.Id, n.CellId, n.Landblock, Vec3Dto.From(n.Position),
            n.FirstSeenUtc, n.LastSeenUtc, n.VisitCount, n.AreaId, n.Tags.ToArray());
        public NavNode To() {
            var n = new NavNode {
                Id = Id, CellId = CellId, Landblock = Landblock, Position = Position.To(),
                FirstSeenUtc = FirstSeenUtc, AreaId = AreaId,
            };
            n.LastSeenUtc = LastSeenUtc;
            n.VisitCount = VisitCount;
            foreach (var t in Tags) n.Tags.Add(t);
            return n;
        }
    }

    internal sealed record EdgeDto(Guid Id, Guid FromNodeId, Guid ToNodeId, NavEdgeKind Kind,
                                    string? UseItemName, uint? UseObjectGuid, float CostSeconds,
                                    DateTimeOffset FirstVerifiedUtc, DateTimeOffset LastVerifiedUtc)
    {
        public static EdgeDto From(NavEdge e) => new(e.Id, e.FromNodeId, e.ToNodeId, e.Kind,
            e.UseItemName, e.UseObjectGuid, e.CostSeconds, e.FirstVerifiedUtc, e.LastVerifiedUtc);
        public NavEdge To() => new() {
            Id = Id, FromNodeId = FromNodeId, ToNodeId = ToNodeId, Kind = Kind,
            UseItemName = UseItemName, UseObjectGuid = UseObjectGuid,
            CostSeconds = CostSeconds,
            FirstVerifiedUtc = FirstVerifiedUtc, LastVerifiedUtc = LastVerifiedUtc,
        };
    }

    internal sealed record ObservationDto(Guid NodeId, uint? Wcid, string Name, Vec3Dto RelativePosition,
                                           float Distance, EntityKind Kind,
                                           DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, int SightingCount)
    {
        public static ObservationDto From(Guid nodeId, EntityObservation o) =>
            new(nodeId, o.Wcid, o.Name, Vec3Dto.From(o.RelativePosition), o.Distance, o.Kind,
                o.FirstSeenUtc, o.LastSeenUtc, o.SightingCount);
        public EntityObservation ToObs() {
            var o = new EntityObservation {
                Wcid = Wcid, Name = Name, RelativePosition = RelativePosition.To(),
                Distance = Distance, Kind = Kind, FirstSeenUtc = FirstSeenUtc,
            };
            o.LastSeenUtc = LastSeenUtc;
            o.SightingCount = SightingCount;
            return o;
        }
    }
}

// ─── Domain types ────────────────────────────────────────────────────

internal sealed class NavRegion
{
    public required Guid Id { get; init; }
    public required uint LandblockId { get; init; }
    public string? Name { get; set; }
}

internal sealed class NavPlace
{
    public required Guid Id { get; init; }
    public required Guid RegionId { get; init; }
    public string? Name { get; set; }
    public PlaceKind Kind { get; set; } = PlaceKind.Unknown;
    public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class NavArea
{
    public required Guid Id { get; init; }
    public required uint CellId { get; init; }
    public required Guid PlaceId { get; init; }
    public AreaKind Kind { get; set; } = AreaKind.Unknown;
    public int? Floor { get; set; }
    public string? RoomName { get; set; }
    public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class NavNode
{
    public required Guid Id { get; init; }
    public required uint CellId { get; init; }
    public required uint Landblock { get; init; }
    public required Vector3 Position { get; init; }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int VisitCount { get; set; }
    public Guid? AreaId { get; set; }
    public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EntityObservation> Observations { get; } = new();
}

internal sealed class NavEdge
{
    public required Guid Id { get; init; }
    public required Guid FromNodeId { get; init; }
    public required Guid ToNodeId { get; init; }
    public required NavEdgeKind Kind { get; init; }
    public string? UseItemName { get; init; }
    public uint? UseObjectGuid { get; init; }
    public float CostSeconds { get; set; } = 1.0f;
    public required DateTimeOffset FirstVerifiedUtc { get; init; }
    public DateTimeOffset LastVerifiedUtc { get; set; }
}

internal sealed class EntityObservation
{
    public required uint? Wcid { get; init; }
    public required string Name { get; init; }
    public required Vector3 RelativePosition { get; init; }
    public required float Distance { get; init; }
    public required EntityKind Kind { get; init; }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int SightingCount { get; set; }
}

internal sealed record EntitySighting(NavNode Node, EntityObservation Observation);

internal sealed record NavRoute(IReadOnlyList<NavRouteStep> Steps, float TotalCostSeconds);
internal sealed record NavRouteStep(NavNode Node, NavEdge? EdgeFromPrevious);
internal sealed record NavConnection(NavEdge Edge, NavNode To);

internal enum EntityKind  { Unknown, NPC, Player, Item, Mob, Portal, Door, Vendor, Healer, Lifestone, Corpse }
internal enum AreaKind    { Unknown, Room, Hall, Plaza, Outdoor, Dungeon }
internal enum PlaceKind   { Unknown, Town, Building, Dungeon, PortalHub, Outdoor }
internal enum NavEdgeKind { Walked, CrossedBoundary, UsedDoor, UsedPortal, UsedItem }
