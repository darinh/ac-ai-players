// SPDX-License-Identifier: AGPL-3.0-or-later
// WeenieRepository — read-only lookup of weenie-class strings
// (Name, ShortDesc, LongDesc) from the ACE `ace_world` database.
//
// The wire protocol does NOT deliver static weenie strings. The
// Strategy/LLM layer needs them to derive content-aware decisions
// ("the item whose ShortDesc says 'Give this to Jonathan'"). We
// preload on first sighting of a wcid, cache with an LRU, and
// return nullable records.
//
// Connection string defaults match a fresh ACE dev install on
// this host: Server=localhost; Database=ace_world; User=root;
// no password. Overridable via env var AC_BOTS_WORLD_DB_CONN.
//
// Concurrency model: TryGet is non-blocking; EnsureLoadedAsync
// does the DB hit. Multiple in-flight requests for the same
// wcid coalesce via a per-wcid TaskCompletionSource.
//
// ace_world.weenie_properties_string.type:
//   1  = Name
//   14 = LongDesc
//   16 = ShortDesc

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace HeadlessAcClient.Strategy;

internal sealed class WeenieRepository : IWeenieRepository
{
    private const int PropNameId      = 1;
    private const int PropLongDescId  = 14;
    private const int PropShortDescId = 16;

    private readonly string _connString;
    private readonly ConcurrentDictionary<uint, WeenieStringRecord?> _cache = new();
    private readonly ConcurrentDictionary<uint, Task<(WeenieStringRecord? Record, bool LoadFailed)>> _inflight = new();
    private int _firstFailureLogged;

    public WeenieRepository(string? connectionString = null)
    {
        _connString = connectionString
            ?? Environment.GetEnvironmentVariable("AC_BOTS_WORLD_DB_CONN")
            ?? "Server=localhost;Database=ace_world;User=root;Password=;AllowZeroDateTime=true;ConvertZeroDateTime=true";
    }

    public WeenieStringRecord? TryGet(uint wcid)
    {
        return _cache.TryGetValue(wcid, out var r) ? r : null;
    }

    public async Task EnsureLoadedAsync(uint wcid, CancellationToken ct = default)
    {
        if (_cache.ContainsKey(wcid)) return;

        var task = _inflight.GetOrAdd(wcid, w => LoadAsync(w, ct));
        WeenieStringRecord? rec;
        bool loadFailed;
        try
        {
            (rec, loadFailed) = await task.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(wcid, out _);
        }

        // Only cache on a clean result. Caching null after a transient
        // DB error would permanently blind the LLM to this wcid's
        // ShortDesc for the rest of the session — bad outcome for
        // quest reasoning. Confirmed "not found" (rec=null, loadFailed
        // =false) is still cached as a tombstone so we don't re-query.
        if (!loadFailed)
        {
            _cache[wcid] = rec;
        }
    }

    private async Task<(WeenieStringRecord? Record, bool LoadFailed)> LoadAsync(uint wcid, CancellationToken ct)
    {
        try
        {
            await using var conn = new MySqlConnection(_connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT type, value FROM weenie_properties_string WHERE object_Id = @w AND type IN (@n, @s, @l)";
            cmd.Parameters.AddWithValue("@w", wcid);
            cmd.Parameters.AddWithValue("@n", PropNameId);
            cmd.Parameters.AddWithValue("@s", PropShortDescId);
            cmd.Parameters.AddWithValue("@l", PropLongDescId);

            string? name = null, sd = null, ld = null;
            await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            {
                var t = rdr.GetInt32(0);
                var v = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                switch (t)
                {
                    case PropNameId:      name = v; break;
                    case PropShortDescId: sd   = v; break;
                    case PropLongDescId:  ld   = v; break;
                }
            }
            if (name is null && sd is null && ld is null) return (null, false);
            return (new WeenieStringRecord(wcid, name, sd, ld), false);
        }
        catch (Exception ex)
        {
            // Log first-time failures so MySQL outages are visible.
            // Caller bypasses caching when LoadFailed=true so the next
            // EnsureLoadedAsync call will re-attempt.
            if (System.Threading.Interlocked.CompareExchange(ref _firstFailureLogged, 1, 0) == 0)
            {
                Console.Error.WriteLine(
                    $"[weenies] WARN first weenie-load failure (wcid={wcid}): " +
                    $"{ex.GetType().Name}: {ex.Message}; future failures suppressed");
            }
            return (null, true);
        }
    }

    /// <summary>
    /// Direct cache poke for tests. Production code should use EnsureLoadedAsync.
    /// </summary>
    internal void SeedForTest(uint wcid, string? name, string? shortDesc, string? longDesc)
        => _cache[wcid] = new WeenieStringRecord(wcid, name, shortDesc, longDesc);
}
