// SPDX-License-Identifier: AGPL-3.0-or-later
// IWeenieRepository — abstraction over ace_world MariaDB lookup
// for static weenie-class string data (Name, ShortDesc, LongDesc,
// UseDesc, Title).
//
// The wire protocol does NOT deliver these description strings for
// objects. The bot needs them in two places:
//   1) Strategy/LLM prompt — an item's own description text (a
//      short_desc OR a use-instruction telling you what to do with
//      it, e.g. give/return it to a named NPC) is the entire reason
//      the LLM can derive the GIVE goal without hardcoding.
//   2) Selector resolution — Tactics matches `short_desc_contains`
//      against the cached desc.
//
// Concrete impl (WeenieRepository, Slice B) wraps a MySqlConnector
// connection to `ace_world.weenie_properties_string` and an LRU
// cache keyed by wcid. The interface is here so Slice A can
// reference it (e.g., from WorldStateProjection.FromWorldState)
// without taking the MySql dependency yet.

namespace HeadlessAcClient.Strategy;

internal sealed record WeenieStringRecord(uint Wcid, string? Name, string? ShortDesc, string? LongDesc, string? UseDesc = null, string? Title = null, int? WeaponSkillId = null);

internal interface IWeenieRepository
{
    /// <summary>
    /// Return cached record for wcid or null if not yet looked up
    /// / not present in DB. Must be non-blocking — call
    /// <see cref="EnsureLoadedAsync"/> first if a fresh result is
    /// required.
    /// </summary>
    WeenieStringRecord? TryGet(uint wcid);

    /// <summary>
    /// Block-load a weenie record into the cache. No-op if
    /// already cached. Idempotent.
    /// </summary>
    System.Threading.Tasks.Task EnsureLoadedAsync(uint wcid, System.Threading.CancellationToken ct = default);
}
