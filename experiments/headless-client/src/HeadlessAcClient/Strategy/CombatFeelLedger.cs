using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// combat-feel ledger — a per-session, per-mob-identity record of the
/// bot's OWN observed combat outcomes (kills, deaths, near-deaths).
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
    }

    private readonly Dictionary<string, Entry> _byKey = new();
    private long _order;

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
    public void RecordDeath(MobIdentity id) => Bump(id, e => { e.Deaths++; }, "death");
    public void RecordNearDeath(MobIdentity id) => Bump(id, e => { e.NearDeaths++; }, "near-death");

    /// <summary>
    /// Records a non-lethal INEFFECTIVE engagement: the bot disengaged a
    /// fight it could not make progress in (the Motor's no-progress abandon —
    /// 0 damage over the watchdog window, or all swings evaded) WITHOUT a
    /// kill or death. Lets the LLM learn the KIND out-defends it without the
    /// bot having to die first. RAW recorded fact; no avoidance decision.
    /// </summary>
    public void RecordIneffective(MobIdentity id) => Bump(id, e => { e.Ineffective++; }, "ineffective");

    /// <summary>
    /// Records the START of an engagement against a monster kind (the
    /// first swing of a fresh target). Increments the Fights counter
    /// without changing the win/loss outcome columns.
    /// </summary>
    public void RecordFightStart(MobIdentity id) => Bump(id, e => { e.Fights++; }, null);

    private void Bump(MobIdentity id, Action<Entry> mutate, string? outcome)
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
        if (outcome is not null)
        {
            e.LastOutcome = outcome;
            e.LastOutcomeOrder = ++_order;
        }
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
                LastOutcome: e.LastOutcome))
            .ToList();
        return significant.Count == 0 ? null : significant;
    }
}
