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
    private readonly ConcurrentDictionary<uint, Task<WeenieStringRecord?>> _inflight = new();

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
        try
        {
            var rec = await task.ConfigureAwait(false);
            _cache[wcid] = rec; // may be null (not in DB) — cached as a tombstone
        }
        finally
        {
            _inflight.TryRemove(wcid, out _);
        }
    }

    private async Task<WeenieStringRecord?> LoadAsync(uint wcid, CancellationToken ct)
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
            if (name is null && sd is null && ld is null) return null;
            return new WeenieStringRecord(wcid, name, sd, ld);
        }
        catch (Exception)
        {
            // Cache miss on error; caller may retry via a fresh EnsureLoadedAsync.
            return null;
        }
    }

    /// <summary>
    /// Direct cache poke for tests. Production code should use EnsureLoadedAsync.
    /// </summary>
    internal void SeedForTest(uint wcid, string? name, string? shortDesc, string? longDesc)
        => _cache[wcid] = new WeenieStringRecord(wcid, name, shortDesc, longDesc);
}
