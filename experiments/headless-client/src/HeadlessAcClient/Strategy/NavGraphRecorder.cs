// SPDX-License-Identifier: AGPL-3.0-or-later
// NavGraphRecorder — captures the bot's spatial trajectory as a
// per-landblock graph of waypoints + observed named objects + edges
// to adjacent landblocks. The goal is to give the bot something to
// revisit so it doesn't re-explore the same map every session.
//
// File layout (one JSON per landblock):
//   experiments/headless-client/data/nav/{landblock:X8}.json
//
// Schema:
//   {
//     "landblock": "0x86020000",
//     "first_seen_utc": "...",
//     "last_seen_utc": "...",
//     "visit_count": N,
//     "waypoints": [ { "x": .., "y": .., "z": .., "first_seen_utc": .. } ],
//     "landmarks": [ { "wcid": .., "name": .., "x": .., "y": .., "z": .. } ],
//     "exits": [ { "to_landblock": "0xA9B40000", "via_item": "Calling Stone",
//                  "from_x":..,"from_y":..,"from_z":..,
//                  "first_seen_utc": .. } ]
//   }
//
// Recording is best-effort and never blocks the receive loop. Disk
// writes are coalesced (flushed on landblock change AND every
// FlushIntervalSeconds).
//
// Anti-hardcoding rule: we record what we OBSERVE; we never seed
// the graph with hand-written landmarks or exits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy;

internal sealed class NavGraphRecorder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Minimum distance between successive waypoints before we record a new one.</summary>
    public float WaypointSpacingMeters { get; init; } = 4.0f;

    /// <summary>Flush dirty landblock files at most this often.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly string _directory;
    private readonly Dictionary<uint, LandblockRecord> _byLandblock = new();
    private readonly HashSet<uint> _dirty = new();
    private DateTimeOffset _lastFlushUtc = DateTimeOffset.MinValue;
    private uint? _lastLandblock;
    private Vector3? _lastWaypoint;
    private bool _firstFailureLogged;

    public string Directory => _directory;
    public int LandblocksTracked { get { lock (_lock) return _byLandblock.Count; } }

    public NavGraphRecorder(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.CurrentDirectory,
            "experiments", "headless-client", "data", "nav");
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            Console.WriteLine($"[nav] NavGraphRecorder rooted at {_directory}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nav] WARN create dir failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Record the bot's current position (call once per tick). The
    /// landblock id is derived from the top 16 bits of the CellId.
    /// </summary>
    public void RecordSelfPosition(uint cellId, Vector3 position, DateTimeOffset utc)
    {
        if (cellId == 0) return;
        var landblock = cellId & 0xFFFF0000u;
        var lb = EnsureLandblock(landblock, utc);

        if (_lastWaypoint is Vector3 prev)
        {
            // Skip if too close to previous waypoint AND same landblock
            // — keeps the graph dense without exploding file size.
            if (_lastLandblock == landblock &&
                Vector3.Distance(prev, position) < WaypointSpacingMeters)
            {
                MaybeFlush(utc);
                return;
            }
        }

        lock (_lock)
        {
            lb.LastSeenUtc = utc;
            lb.Waypoints.Add(new Waypoint
            {
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                FirstSeenUtc = utc,
            });
            _dirty.Add(landblock);
        }
        _lastWaypoint = position;
        _lastLandblock = landblock;

        MaybeFlush(utc);
    }

    /// <summary>
    /// Record an observed landmark (named object) anchored in a landblock.
    /// Called from ObjectCreate decode site.
    /// </summary>
    public void RecordLandmark(uint cellId, Vector3 position, uint? wcid, string? name, DateTimeOffset utc)
    {
        if (cellId == 0 || string.IsNullOrEmpty(name)) return;
        var landblock = cellId & 0xFFFF0000u;
        var lb = EnsureLandblock(landblock, utc);
        lock (_lock)
        {
            // De-dup by (wcid, name, rounded position).
            var key = $"{wcid}/{name}/{(int)position.X}/{(int)position.Y}/{(int)position.Z}";
            if (!lb.LandmarkKeys.Add(key)) return;
            lb.Landmarks.Add(new Landmark
            {
                Wcid = wcid,
                Name = name,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                FirstSeenUtc = utc,
            });
            _dirty.Add(landblock);
        }
    }

    /// <summary>
    /// Record a transition between two landblocks. Called when a
    /// LandblockChanged event fires. <paramref name="viaItem"/> is the
    /// item the bot used (Calling Stone, portal name, etc.) — null if
    /// unknown (e.g. walked across a cell boundary).
    /// </summary>
    public void RecordExit(uint fromLandblock, uint toLandblock, Vector3? exitPosition, string? viaItem, DateTimeOffset utc)
    {
        if (fromLandblock == 0 || toLandblock == 0 || fromLandblock == toLandblock) return;
        var lb = EnsureLandblock(fromLandblock, utc);
        lock (_lock)
        {
            var key = $"{toLandblock:X8}|{viaItem}";
            if (!lb.ExitKeys.Add(key)) return;
            lb.Exits.Add(new Exit
            {
                ToLandblock = $"0x{toLandblock:X8}",
                ViaItem = viaItem,
                FromX = exitPosition?.X,
                FromY = exitPosition?.Y,
                FromZ = exitPosition?.Z,
                FirstSeenUtc = utc,
            });
            _dirty.Add(fromLandblock);
        }
        MaybeFlush(utc);
    }

    /// <summary>
    /// Force a flush of all dirty landblock files. Call at session
    /// shutdown.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            foreach (var lb in _dirty.ToList())
            {
                TryWrite(lb);
            }
            _dirty.Clear();
            _lastFlushUtc = DateTimeOffset.UtcNow;
        }
    }

    private LandblockRecord EnsureLandblock(uint landblock, DateTimeOffset utc)
    {
        lock (_lock)
        {
            if (!_byLandblock.TryGetValue(landblock, out var lb))
            {
                lb = LoadOrCreate(landblock, utc);
                _byLandblock[landblock] = lb;
            }
            lb.VisitCount++;
            return lb;
        }
    }

    private LandblockRecord LoadOrCreate(uint landblock, DateTimeOffset utc)
    {
        var path = Path.Combine(_directory, $"{landblock:X8}.json");
        if (File.Exists(path))
        {
            try
            {
                using var fs = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize<LandblockRecord>(fs, JsonOpts);
                if (loaded is not null)
                {
                    loaded.LastSeenUtc = utc;
                    // Rebuild dedup keys after load.
                    foreach (var lm in loaded.Landmarks)
                        loaded.LandmarkKeys.Add($"{lm.Wcid}/{lm.Name}/{(int)lm.X}/{(int)lm.Y}/{(int)lm.Z}");
                    foreach (var ex in loaded.Exits)
                        loaded.ExitKeys.Add($"{ex.ToLandblock.Replace("0x", "")}|{ex.ViaItem}");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[nav] WARN reload {landblock:X8} failed: {ex.Message}; starting fresh");
            }
        }
        return new LandblockRecord
        {
            Landblock = $"0x{landblock:X8}",
            FirstSeenUtc = utc,
            LastSeenUtc = utc,
        };
    }

    private void MaybeFlush(DateTimeOffset utc)
    {
        if (utc - _lastFlushUtc < FlushInterval) return;
        Flush();
    }

    private void TryWrite(uint landblock)
    {
        if (!_byLandblock.TryGetValue(landblock, out var lb)) return;
        var path = Path.Combine(_directory, $"{landblock:X8}.json");
        try
        {
            using var fs = File.Create(path);
            JsonSerializer.Serialize(fs, lb, JsonOpts);
        }
        catch (Exception ex)
        {
            if (!_firstFailureLogged)
            {
                _firstFailureLogged = true;
                Console.Error.WriteLine(
                    $"[nav] WARN first write failure ({landblock:X8}): {ex.GetType().Name}: {ex.Message}; future failures suppressed");
            }
        }
    }

    internal sealed class LandblockRecord
    {
        public required string Landblock { get; init; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public int VisitCount { get; set; }
        public List<Waypoint> Waypoints { get; set; } = new();
        public List<Landmark> Landmarks { get; set; } = new();
        public List<Exit> Exits { get; set; } = new();

        [JsonIgnore] public HashSet<string> LandmarkKeys { get; } = new();
        [JsonIgnore] public HashSet<string> ExitKeys { get; } = new();
    }

    internal sealed class Waypoint
    {
        public required float X { get; init; }
        public required float Y { get; init; }
        public required float Z { get; init; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
    }

    internal sealed class Landmark
    {
        public uint? Wcid { get; init; }
        public string? Name { get; init; }
        public required float X { get; init; }
        public required float Y { get; init; }
        public required float Z { get; init; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
    }

    internal sealed class Exit
    {
        public required string ToLandblock { get; init; }
        public string? ViaItem { get; init; }
        public float? FromX { get; init; }
        public float? FromY { get; init; }
        public float? FromZ { get; init; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
    }
}
