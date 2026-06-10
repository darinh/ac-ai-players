using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// combat-feel ledger — a durable, per-mob-identity record of the
/// bot's OWN observed combat outcomes (kills, deaths, near-deaths).
/// Persisted across sessions by <see cref="CombatFeelStore"/> so the bot
/// retains "this kind keeps killing me" knowledge across the frequent
/// process restarts instead of re-learning it every run.
///
/// Architecture note: this is bookkeeping the LLM cannot do for itself
/// (it has no cross-tick memory). The ledger stores RAW recorded
/// outcomes only. It assigns NO priority, NO danger label, and makes NO
/// avoidance decision — those belong to the LLM (the "COMBAT SAFETY"
/// prompt rule already tells it to avoid the kind of monster that
/// defeated it). HandshakeDriver feeds the ledger the wire-observed
/// outcomes and surfaces <see cref="Snapshot"/> to the prompt.
///
/// Identity key: WeenieClassId when known (same wcid == same monster
/// kind across instances), else the normalized display name. The
/// per-instance guid is deliberately NOT used — the bot must learn
/// about the KIND of monster, not one spawn.
/// </summary>
internal sealed class CombatFeelLedger
{
    /// <summary>The kind of monster an outcome is recorded against.</summary>
    public readonly record struct MobIdentity(uint? Wcid, string? Name);

    private sealed class Entry
    {
        public string? DisplayName;
        public uint? Wcid;
        public int Kills;
        public int Deaths;
        public int NearDeaths;
        public int Ineffective;
        public int Fights;
        public string LastOutcome = "";
        public long LastOutcomeOrder;
        // Highest bot level at which a LOSS (death/near-death/ineffective) to
        // this kind was recorded. null until a loss is recorded with a known
        // level. Used by the fallback's adaptive beaten-kind re-test only.
        public int? MaxLossBotLevel;
    }

    private readonly Dictionary<string, Entry> _byKey = new();
    private long _order;

    /// <summary>
    /// Set on every recorded mutation, cleared by <see cref="MarkClean"/>.
    /// Lets a persistence store skip rewrites when nothing changed since the
    /// last save. Pure bookkeeping — no decision is made from it.
    /// </summary>
    public bool Dirty { get; private set; }

    /// <summary>Clears the <see cref="Dirty"/> flag after a successful save.</summary>
    public void MarkClean() => Dirty = false;

    /// <summary>
    /// Stable key for a monster kind. Prefers wcid; falls back to the
    /// normalized name. Returns null when neither is usable (the caller
    /// then skips recording — an unidentifiable foe must not be merged
    /// into a bogus bucket).
    /// </summary>
    public static string? KeyOf(MobIdentity id)
    {
        if (id.Wcid is uint w && w != 0u) return "w:" + w.ToString();
        var n = NormalizeName(id.Name);
        return n is null ? null : "n:" + n;
    }

    /// <summary>
    /// Whitespace-collapsed, lower-invariant form of a display name, or
    /// null when the name is unusable. Exposed so the LLM-facing lookup
    /// can match a visible monster against recorded rows by the SAME name
    /// normalization the ledger keys by. The <c>"(unknown)"</c> Snapshot
    /// display fallback (a row with no observed name) is deliberately NOT
    /// matchable so it can never produce a spurious join.
    /// </summary>
    internal static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var collapsed = string.Join(' ',
            name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return null;
        var lower = collapsed.ToLowerInvariant();
        return lower == "(unknown)" ? null : lower;
    }

    public void RecordKill(MobIdentity id) => Bump(id, e => { e.Kills++; }, "kill");
    public void RecordDeath(MobIdentity id, int? botLevel = null) => Bump(id, e => { e.Deaths++; }, "death", botLevel);
    public void RecordNearDeath(MobIdentity id, int? botLevel = null) => Bump(id, e => { e.NearDeaths++; }, "near-death", botLevel);

    /// <summary>
    /// Records a non-lethal INEFFECTIVE engagement: the bot disengaged a
    /// fight it could not make progress in (the Motor's no-progress abandon —
    /// 0 damage over the watchdog window, or all swings evaded) WITHOUT a
    /// kill or death. Lets the LLM learn the KIND out-defends it without the
    /// bot having to die first. RAW recorded fact; no avoidance decision.
    /// </summary>
    public void RecordIneffective(MobIdentity id, int? botLevel = null) => Bump(id, e => { e.Ineffective++; }, "ineffective", botLevel);

    /// <summary>
    /// Records the START of an engagement against a monster kind (the
    /// first swing of a fresh target). Increments the Fights counter
    /// without changing the win/loss outcome columns.
    /// </summary>
    public void RecordFightStart(MobIdentity id) => Bump(id, e => { e.Fights++; }, null);

    private void Bump(MobIdentity id, Action<Entry> mutate, string? outcome, int? lossBotLevel = null)
    {
        var key = KeyOf(id);
        if (key is null) return;
        if (!_byKey.TryGetValue(key, out var e))
        {
            e = new Entry();
            _byKey[key] = e;
        }
        if (!string.IsNullOrWhiteSpace(id.Name)) e.DisplayName = id.Name!.Trim();
        if (id.Wcid is uint w && w != 0u) e.Wcid = w;
        mutate(e);
        // Track the HIGHEST bot level at which a LOSS to this kind was recorded
        // (callers pass it only for loss outcomes). The fallback's beaten-kind
        // re-test compares it to the bot's CURRENT level so a kind lost-to at a
        // lower level is re-tested once the bot has grown stronger. Monotonic
        // max so the verdict re-stands one level higher after a re-test loss.
        if (lossBotLevel is int lvl)
            e.MaxLossBotLevel = e.MaxLossBotLevel is int cur ? Math.Max(cur, lvl) : lvl;
        if (outcome is not null)
        {
            e.LastOutcome = outcome;
            e.LastOutcomeOrder = ++_order;
        }
        Dirty = true;
    }

    /// <summary>True when nothing significant has been recorded yet.</summary>
    public bool IsEmpty => _byKey.Values.All(e =>
        e.Kills == 0 && e.Deaths == 0 && e.NearDeaths == 0 && e.Ineffective == 0);

    /// <summary>
    /// The most-relevant recorded outcomes for the prompt, capped at
    /// <paramref name="max"/>. Entries with no kill/death/near-death
    /// signal are omitted. Ordering is by recency of the last recorded
    /// outcome only (content-blind, like an eviction window) — the LLM,
    /// not source, decides which kinds matter. Returns null when there is
    /// nothing significant to show.
    /// </summary>
    public IReadOnlyList<CombatHistoryEntry>? Snapshot(int max = 6)
    {
        var significant = _byKey.Values
            .Where(e => e.Kills > 0 || e.Deaths > 0 || e.NearDeaths > 0 || e.Ineffective > 0)
            .OrderByDescending(e => e.LastOutcomeOrder)
            .Take(Math.Max(0, max))
            .Select(e => new CombatHistoryEntry(
                Name: string.IsNullOrWhiteSpace(e.DisplayName) ? "(unknown)" : e.DisplayName!,
                Wcid: e.Wcid,
                Kills: e.Kills,
                Deaths: e.Deaths,
                NearDeaths: e.NearDeaths,
                Ineffective: e.Ineffective,
                Fights: e.Fights,
                LastOutcome: e.LastOutcome,
                MaxLossBotLevel: e.MaxLossBotLevel))
            .ToList();
        return significant.Count == 0 ? null : significant;
    }

    // ---- cross-session persistence ------------------------------------
    // The ledger is the bot's OWN observed combat outcomes per monster kind.
    // Without persistence it is rebuilt empty every process start, so the bot
    // re-learns (and re-dies to) the same kinds each session. ToJson/FromJson
    // make that learning durable across restarts. RAW recorded facts only —
    // no priority, no avoidance decision; the LLM still decides what to do
    // with the counts. wcids/names here are RUNTIME-OBSERVED and written by the
    // bot, never a hardcoded source list.

    private const int PersistVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record EntryDto(
        string Key, string? DisplayName, uint? Wcid,
        int Kills, int Deaths, int NearDeaths, int Ineffective, int Fights,
        string LastOutcome, long LastOutcomeOrder, int? MaxLossBotLevel = null);

    private sealed record LedgerDto(int Version, long Order, List<EntryDto> Entries);

    /// <summary>
    /// Serializes the full ledger (every entry + the recency counter) to JSON.
    /// </summary>
    public string ToJson()
    {
        var entries = _byKey
            .Select(kv => new EntryDto(
                kv.Key, kv.Value.DisplayName, kv.Value.Wcid,
                kv.Value.Kills, kv.Value.Deaths, kv.Value.NearDeaths,
                kv.Value.Ineffective, kv.Value.Fights,
                kv.Value.LastOutcome, kv.Value.LastOutcomeOrder, kv.Value.MaxLossBotLevel))
            .ToList();
        return JsonSerializer.Serialize(new LedgerDto(PersistVersion, _order, entries), JsonOpts);
    }

    /// <summary>
    /// Rebuilds a ledger from <see cref="ToJson"/> output. Unparseable or
    /// version-mismatched input yields an empty ledger (the caller starts
    /// fresh rather than crashing). The loaded ledger is NOT dirty.
    /// </summary>
    public static CombatFeelLedger FromJson(string? json)
    {
        var ledger = new CombatFeelLedger();
        if (string.IsNullOrWhiteSpace(json)) return ledger;

        LedgerDto? dto;
        try { dto = JsonSerializer.Deserialize<LedgerDto>(json, JsonOpts); }
        catch (JsonException) { return ledger; }
        if (dto is null || dto.Version != PersistVersion || dto.Entries is null)
            return ledger;

        foreach (var d in dto.Entries)
        {
            if (string.IsNullOrEmpty(d.Key)) continue;
            ledger._byKey[d.Key] = new Entry
            {
                DisplayName = d.DisplayName,
                Wcid = d.Wcid,
                Kills = d.Kills,
                Deaths = d.Deaths,
                NearDeaths = d.NearDeaths,
                Ineffective = d.Ineffective,
                Fights = d.Fights,
                LastOutcome = d.LastOutcome ?? "",
                LastOutcomeOrder = d.LastOutcomeOrder,
                // Absent in ledgers written before this field existed (the DTO
                // field is optional, so old JSON deserializes it to null). A
                // null MaxLossBotLevel disables the level-aware re-test, so old
                // data behaves EXACTLY as before — a permanent beaten verdict.
                MaxLossBotLevel = d.MaxLossBotLevel,
            };
        }
        // Resume the recency counter past the highest persisted order so newly
        // recorded outcomes still rank as most-recent.
        ledger._order = Math.Max(
            dto.Order,
            ledger._byKey.Values.Count == 0 ? 0L : ledger._byKey.Values.Max(e => e.LastOutcomeOrder));
        return ledger;
    }

    /// <summary>
    /// Folds another ledger's observations into this one by taking the HIGHER
    /// count per counter per kind (and adopting kinds only the other has).
    /// Used before a save to merge any on-disk changes a richer prior file —
    /// or, in theory, a concurrent writer — recorded, so a blind full-file
    /// rewrite can never drop them. Max-merge is monotonic, never
    /// double-counts, and converges (repeated merges reach the same fixpoint).
    /// </summary>
    public void MergeFrom(CombatFeelLedger other)
    {
        foreach (var (key, o) in other._byKey)
        {
            if (!_byKey.TryGetValue(key, out var e))
            {
                _byKey[key] = new Entry
                {
                    DisplayName = o.DisplayName,
                    Wcid = o.Wcid,
                    Kills = o.Kills,
                    Deaths = o.Deaths,
                    NearDeaths = o.NearDeaths,
                    Ineffective = o.Ineffective,
                    Fights = o.Fights,
                    LastOutcome = o.LastOutcome,
                    LastOutcomeOrder = o.LastOutcomeOrder,
                    MaxLossBotLevel = o.MaxLossBotLevel,
                };
                Dirty = true;
                continue;
            }
            if (o.Kills > e.Kills) { e.Kills = o.Kills; Dirty = true; }
            if (o.Deaths > e.Deaths) { e.Deaths = o.Deaths; Dirty = true; }
            if (o.NearDeaths > e.NearDeaths) { e.NearDeaths = o.NearDeaths; Dirty = true; }
            if (o.Ineffective > e.Ineffective) { e.Ineffective = o.Ineffective; Dirty = true; }
            if (o.Fights > e.Fights) { e.Fights = o.Fights; Dirty = true; }
            // Max-merge the loss level (monotonic, like the counts) so a richer
            // on-disk record's higher loss level is never lost on rewrite.
            if (o.MaxLossBotLevel is int ol && (e.MaxLossBotLevel is not int el || ol > el))
            {
                e.MaxLossBotLevel = ol;
                Dirty = true;
            }
            if (string.IsNullOrEmpty(e.DisplayName) && !string.IsNullOrEmpty(o.DisplayName))
                e.DisplayName = o.DisplayName;
            if (e.Wcid is null && o.Wcid is not null) e.Wcid = o.Wcid;
            if (o.LastOutcomeOrder > e.LastOutcomeOrder)
            {
                e.LastOutcome = o.LastOutcome;
                e.LastOutcomeOrder = o.LastOutcomeOrder;
            }
        }
        var maxOrder = _byKey.Values.Count == 0 ? 0L : _byKey.Values.Max(e => e.LastOutcomeOrder);
        if (maxOrder > _order) _order = maxOrder;
    }
}
