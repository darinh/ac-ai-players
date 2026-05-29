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
// UsedDoor, UsedPortal, UsedItem} and a cost. Pathfinding is Dijkstra
// over the edge graph; layer separation (L0/L1/L2 HPA*) is implicit
// in the edge-kind cost ordering, not a separate search structure.
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

internal sealed class NavGraph
{
    /// <summary>
    /// Two visits closer than this in the same cell merge into the
    /// same node. Should be ≥ the bot's tick movement step so we
    /// don't spawn a fresh node on every receive cycle.
    /// </summary>
    public float MergeRadiusMeters { get; init; } = 4.0f;

    /// <summary>
    /// Auto-Walked-edge creation is suppressed if successive ticks
    /// jump farther than this in the same landblock — protects against
    /// teleport / slide / re-spawn within a landblock recording fake
    /// walkable shortcuts. Cross-landblock jumps NEVER auto-edge.
    /// </summary>
    public float MaxAutoEdgeMeters { get; init; } = 20.0f;

    /// <summary>How often to flush dirty journals to disk.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(15);

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

    // Continuation state (per-graph-instance; resets across sessions
    // intentionally — chronological Walked edges should NOT span a
    // restart since intervening state is unknown).
    private Guid? _lastVisitNodeId;

    private DateTimeOffset _lastFlushUtc = DateTimeOffset.MinValue;
    private readonly HashSet<string> _dirtyJournals = new();
    private bool _writeFailureLogged;

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
            MarkDirty("regions");
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
                MarkDirty("places");
                placeId = p.Id;
            }
            else
            {
                placeId = place.Id;
            }

            var a = new NavArea { Id = Guid.NewGuid(), CellId = cellId, PlaceId = placeId, Kind = kind };
            _areas[a.Id] = a;
            _areaByCell[cellId] = a;
            MarkDirty("areas");
            return a.Id;
        }
    }

    // ─── Recording ───────────────────────────────────────────────────

    /// <summary>
    /// Records the bot's current position. Returns the node Id —
    /// either an existing nearby node (visit-bumped) or a newly-
    /// created one. Automatically adds a Walked edge from the previous
    /// recorded visit if it was in the same landblock and within
    /// MaxAutoEdgeMeters (suppressed across cells with too-large jumps
    /// to avoid recording fake teleport shortcuts).
    /// </summary>
    public Guid RecordVisit(uint cellId, Vector3 position, DateTimeOffset utc)
    {
        if (cellId == 0)
            throw new ArgumentException("cellId must be non-zero", nameof(cellId));

        lock (_lock)
        {
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
                MarkDirty("nodes");
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
                MarkDirty("nodes");
            }

            // Auto-edge from previous visit if eligible. Conservative
            // guards (rubber-duck flagged wall-shortcut risk):
            //   * same landblock (cross-landblock = explicit only)
            //   * within MaxAutoEdgeMeters (guards teleport-within-LB)
            //   * either same cellId OR both cells are outdoor terrain
            //     (low 16 bits < 0x100). Different indoor cells in the
            //     same landblock = walled rooms; never auto-edge across
            //     a potential wall. The driver must call RecordEdge
            //     explicitly with UsedDoor / UsedPortal / UsedItem.
            if (_lastVisitNodeId is Guid prev && prev != nodeId &&
                _nodes.TryGetValue(prev, out var prevNode))
            {
                var sameLandblock = prevNode.Landblock == (cellId >> 16);
                var sameCell = prevNode.CellId == cellId;
                var bothOutdoor = (prevNode.CellId & 0xFFFFu) < 0x100u &&
                                  (cellId & 0xFFFFu) < 0x100u;
                var dist = Vector3.Distance(prevNode.Position, position);
                if (sameLandblock && dist <= MaxAutoEdgeMeters && (sameCell || bothOutdoor))
                {
                    AddOrRefreshEdgeLocked(prev, nodeId, NavEdgeKind.Walked,
                        useItemName: null, useObjectGuid: null,
                        cost: EdgeKindBaseCost.GetValueOrDefault(NavEdgeKind.Walked, 1.0f) * Math.Max(dist, 0.1f),
                        utc);
                }
            }

            _lastVisitNodeId = nodeId;
            MaybeFlush(utc);
            return nodeId;
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
            }
            else
            {
                node.Observations.Add(new EntityObservation
                {
                    Wcid = wcid,
                    Name = name,
                    RelativePosition = rel,
                    Distance = dist,
                    Kind = kind,
                    FirstSeenUtc = utc,
                    LastSeenUtc = utc,
                    SightingCount = 1,
                });
            }
            MarkDirty("observations");
        }
    }

    /// <summary>
    /// Records an explicit edge (caller-provided kind). Used by the
    /// driver for non-Walked transitions: door usage, portal weenie,
    /// inventory item teleport (Calling Stone, recall, gem), and
    /// passive landblock-boundary crossings. Walked edges are
    /// auto-created by RecordVisit; this method is for everything else.
    /// </summary>
    public Guid RecordEdge(Guid fromNodeId, Guid toNodeId, NavEdgeKind kind,
                            string? useItemName, uint? useObjectGuid, DateTimeOffset utc)
    {
        lock (_lock)
        {
            if (!_nodes.ContainsKey(fromNodeId) || !_nodes.ContainsKey(toNodeId))
                throw new ArgumentException("Edge endpoints must be existing nodes");
            var baseCost = EdgeKindBaseCost.GetValueOrDefault(kind, 1.0f);
            return AddOrRefreshEdgeLocked(fromNodeId, toNodeId, kind, useItemName, useObjectGuid, baseCost, utc);
        }
    }

    // ─── Metadata tagging ────────────────────────────────────────────

    public void TagRegion(Guid regionId, string? name)
    {
        lock (_lock)
        {
            if (!_regions.TryGetValue(regionId, out var r)) return;
            if (name is not null) r.Name = name;
            MarkDirty("regions");
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
            MarkDirty("places");
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
            MarkDirty("areas");
        }
    }

    public void TagNode(Guid nodeId, IEnumerable<string>? addTags = null)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(nodeId, out var n)) return;
            if (addTags is not null) foreach (var t in addTags) n.Tags.Add(t);
            MarkDirty("nodes");
        }
    }

    // ─── Pathfinding (Dijkstra over all edges) ───────────────────────

    /// <summary>
    /// Dijkstra over the directed edge graph. Edge cost = the edge's
    /// CostSeconds (set per-kind on creation; kind-based defaults bias
    /// toward walking for short hops and portals for cross-region).
    /// Returns null if no route exists.
    /// </summary>
    public NavRoute? FindRoute(Guid startNodeId, Guid goalNodeId)
    {
        lock (_lock)
        {
            if (!_nodes.ContainsKey(startNodeId) || !_nodes.ContainsKey(goalNodeId)) return null;
            if (startNodeId == goalNodeId)
                return new NavRoute(new[] { new NavRouteStep(_nodes[startNodeId], null) }, 0f);

            // Build adjacency once per call (small graphs; spike scale)
            var adj = new Dictionary<Guid, List<NavEdge>>();
            foreach (var e in _edges.Values)
            {
                if (!adj.TryGetValue(e.FromNodeId, out var list))
                    adj[e.FromNodeId] = list = new();
                list.Add(e);
            }

            var dist = new Dictionary<Guid, float> { [startNodeId] = 0f };
            var prevEdge = new Dictionary<Guid, NavEdge>();
            var visited = new HashSet<Guid>();
            // Naive O(V²) priority — V is small enough (< 10k) for the
            // spike, and a real heap pulls in another dep we don't need.
            while (true)
            {
                Guid? bestId = null;
                var bestD = float.PositiveInfinity;
                foreach (var (id, d) in dist)
                {
                    if (!visited.Contains(id) && d < bestD)
                    {
                        bestId = id;
                        bestD = d;
                    }
                }
                if (bestId is null) return null;
                if (bestId.Value == goalNodeId) break;
                visited.Add(bestId.Value);

                if (!adj.TryGetValue(bestId.Value, out var outs)) continue;
                foreach (var e in outs)
                {
                    if (visited.Contains(e.ToNodeId)) continue;
                    var nd = bestD + e.CostSeconds;
                    if (!dist.TryGetValue(e.ToNodeId, out var cur) || nd < cur)
                    {
                        dist[e.ToNodeId] = nd;
                        prevEdge[e.ToNodeId] = e;
                    }
                }
            }

            // Reconstruct
            var stepsRev = new List<NavRouteStep>();
            var cursor = goalNodeId;
            while (cursor != startNodeId)
            {
                var e = prevEdge[cursor];
                stepsRev.Add(new NavRouteStep(_nodes[cursor], e));
                cursor = e.FromNodeId;
            }
            stepsRev.Add(new NavRouteStep(_nodes[startNodeId], null));
            stepsRev.Reverse();
            return new NavRoute(stepsRev, dist[goalNodeId]);
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
            foreach (var kind in _dirtyJournals.ToArray())
            {
                TryWriteJournal(kind);
            }
            _dirtyJournals.Clear();
            _lastFlushUtc = DateTimeOffset.UtcNow;
        }
    }

    // ─── Internals ───────────────────────────────────────────────────

    private Guid EnsureRegionLocked(uint landblock)
    {
        if (_regionByLandblock.TryGetValue(landblock, out var existing)) return existing.Id;
        var r = new NavRegion { Id = Guid.NewGuid(), LandblockId = landblock };
        _regions[r.Id] = r;
        _regionByLandblock[landblock] = r;
        MarkDirty("regions");
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
            MarkDirty("places");
            placeId = p.Id;
        }
        else placeId = place.Id;
        var a = new NavArea { Id = Guid.NewGuid(), CellId = cellId, PlaceId = placeId, Kind = kind };
        _areas[a.Id] = a;
        _areaByCell[cellId] = a;
        MarkDirty("areas");
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
                MarkDirty("edges");
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
        MarkDirty("edges");
        return edge.Id;
    }

    private void MarkDirty(string journal)
    {
        _dirtyJournals.Add(journal);
        if (DateTimeOffset.UtcNow - _lastFlushUtc >= FlushInterval)
        {
            foreach (var j in _dirtyJournals.ToArray()) TryWriteJournal(j);
            _dirtyJournals.Clear();
            _lastFlushUtc = DateTimeOffset.UtcNow;
        }
    }

    private void MaybeFlush(DateTimeOffset utc)
    {
        if (utc - _lastFlushUtc < FlushInterval) return;
        foreach (var j in _dirtyJournals.ToArray()) TryWriteJournal(j);
        _dirtyJournals.Clear();
        _lastFlushUtc = utc;
    }

    private void TryWriteJournal(string kind)
    {
        var path = Path.Combine(_directory, $"{kind}.jsonl");
        try
        {
            // Snapshot-rewrite: simpler than delta journal and keeps
            // the file small for the spike. Each session rewrites the
            // whole journal on every flush. Trade-off acknowledged:
            // O(N) writes; revisit when N gets large.
            using var fs = File.Create(path);
            using var w  = new StreamWriter(fs);
            switch (kind)
            {
                case "regions":      foreach (var r in _regions.Values) w.WriteLine(JsonSerializer.Serialize(RegionDto.From(r), JsonOpts)); break;
                case "places":       foreach (var p in _places.Values)  w.WriteLine(JsonSerializer.Serialize(PlaceDto.From(p),  JsonOpts)); break;
                case "areas":        foreach (var a in _areas.Values)   w.WriteLine(JsonSerializer.Serialize(AreaDto.From(a),   JsonOpts)); break;
                case "nodes":        foreach (var n in _nodes.Values)   w.WriteLine(JsonSerializer.Serialize(NodeDto.From(n),   JsonOpts)); break;
                case "edges":        foreach (var e in _edges.Values)   w.WriteLine(JsonSerializer.Serialize(EdgeDto.From(e),   JsonOpts)); break;
                case "observations":
                    foreach (var n in _nodes.Values)
                        foreach (var o in n.Observations)
                            w.WriteLine(JsonSerializer.Serialize(ObservationDto.From(n.Id, o), JsonOpts));
                    break;
            }
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
            LoadJournal("regions", line => {
                var d = JsonSerializer.Deserialize<RegionDto>(line, JsonOpts);
                if (d is not null) { var r = d.To(); _regions[r.Id] = r; _regionByLandblock[r.LandblockId] = r; }
            });
            LoadJournal("places", line => {
                var d = JsonSerializer.Deserialize<PlaceDto>(line, JsonOpts);
                if (d is not null) { var p = d.To(); _places[p.Id] = p; }
            });
            LoadJournal("areas", line => {
                var d = JsonSerializer.Deserialize<AreaDto>(line, JsonOpts);
                if (d is not null) { var a = d.To(); _areas[a.Id] = a; _areaByCell[a.CellId] = a; }
            });
            LoadJournal("nodes", line => {
                var d = JsonSerializer.Deserialize<NodeDto>(line, JsonOpts);
                if (d is not null) {
                    var n = d.To();
                    _nodes[n.Id] = n;
                    if (!_nodesByCell.TryGetValue(n.CellId, out var list)) _nodesByCell[n.CellId] = list = new();
                    list.Add(n);
                }
            });
            LoadJournal("edges", line => {
                var d = JsonSerializer.Deserialize<EdgeDto>(line, JsonOpts);
                if (d is not null) { var e = d.To(); _edges[e.Id] = e; }
            });
            LoadJournal("observations", line => {
                var d = JsonSerializer.Deserialize<ObservationDto>(line, JsonOpts);
                if (d is not null && _nodes.TryGetValue(d.NodeId, out var n)) n.Observations.Add(d.ToObs());
            });
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

internal enum EntityKind  { Unknown, NPC, Player, Item, Mob, Portal, Door, Vendor, Healer, Lifestone, Corpse }
internal enum AreaKind    { Unknown, Room, Hall, Plaza, Outdoor, Dungeon }
internal enum PlaceKind   { Unknown, Town, Building, Dungeon, PortalHub, Outdoor }
internal enum NavEdgeKind { Walked, CrossedBoundary, UsedDoor, UsedPortal, UsedItem }
