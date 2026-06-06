// SPDX-License-Identifier: AGPL-3.0-or-later
// EventStream — append-only ring buffer of typed observations,
// fed by the receive loop and read by Strategy (LLM policy).
//
// Trigger boundaries (NOT every tick — would bankrupt the LLM):
//   - new PopupString
//   - new ServerMessage (system / NPC dialog channel)
//   - new inventory item (previously-unseen wcid in container=self)
//   - landblock crossing (self CellId high-16 changed)
//   - goal completed / failed / expired
//   - "stuck" timer fired (no action progress for N seconds)
//
// EventStream stores up to MaxEvents recent events (default 256).
// When full, oldest events are evicted.
//
// Reads are by category + count (e.g. "last 5 PopupStrings",
// "last 20 events of any kind, newest first"). Writes are append-
// only and stamped with a monotonic bot-tick number plus a UTC
// timestamp.
//
// Concurrency: same single-threaded receive loop owns this. If
// the network layer is ever multi-threaded, wrap the ring buffer
// with a lock — Goal/Selector are value types, this isn't.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy;

internal enum EventKind
{
    Unknown               = 0,
    PopupString           = 1,
    ServerMessage         = 2,
    InventoryItemAdded    = 3,
    InventoryItemRemoved  = 4,
    LandblockChanged      = 5,
    GoalEmitted           = 6,
    GoalCompleted         = 7,
    GoalFailed            = 8,
    GoalExpired           = 9,
    NpcDialog             = 10,
    HealthChanged         = 11,
    // Mechanical: the server told us our last action failed.
    // Carries the raw WeenieError code + label + message string.
    // Surfaced so the LLM can pivot off a stuck retry loop and so
    // LlmGoalPolicy can drop the stale currentGoal anchor.
    ActionRejected        = 12,
    // Slice M: full text of a quest book / scroll / parchment the
    // bot has Used. The server returns BookDataResponse with up to
    // 1000 chars per page; we concatenate the pages so the LLM can
    // read directions, item lists, and coordinates the way a human
    // player would. ItemGuid = book guid (dedup key), Name = book
    // display name (e.g. "A List of Items"), Text = body content.
    BookText              = 13,
    // Slice V (ac-ai-players#86): the schema-only picker began or
    // changed an autonomous activity. ItemGuid = target guid, Name
    // = target name, Text = "<source>: <reason>" (e.g.
    // "in-range: nearest unvisited NPC"). Made salient so the LLM
    // wakes up on the picker's choice rather than discovering it
    // only when the bot has already finished investigating.
    PickerActivityStarted = 14,
    // Slice V: the picker either reached its target (and dispatched
    // the action) or moved on to a different target. ItemGuid =
    // the target guid that was being investigated. NOT salient on
    // its own; the next PickerActivityStarted (if any) will wake
    // the LLM if anything material happened.
    PickerActivityCompleted = 15,
    // Slice W.3 (ac-ai-players#88): the picker walked to its auto-
    // locked target but no LLM goal named a verb to dispatch on
    // arrival, so the motor sent nothing. ItemGuid = the target
    // guid the bot is now standing next to, Name = its display
    // name, Text = "<source>: <reason>" copied from the prior
    // PickerActivityStarted. Salient — wakes the LLM so it can
    // emit a verb goal (Use/Talk/Pickup/Attack) before the next
    // picker pick moves the bot on. Without this event the LLM
    // would only learn about the arrival by diffing successive
    // Visible-nearby projections, which is too slow.
    PickerArrivedNoAction   = 16,
    // 2026-05-30 (inventory-USE dedup): the bot just dispatched a
    // GameActionUse against an inventory item it carries (the
    // "inventory-Use direct" path in HandshakeDriver — items in
    // bag have no spatial position so they bypass the motor).
    // Carries ItemGuid (the item's guid), Wcid, and Name.
    //
    // Used by LlmGoalPolicy.IsInventoryUseRecentlyDispatched to
    // drop LLM goals that repeat Use{inventory item} against an
    // item the bot has already USE'd in the recent event window —
    // motivating case is non-consumable tutorial letters whose
    // short_desc keeps instructing "double-click to read", causing
    // the LLM to re-emit the same goal every deliberation and
    // crowd out other actions (e.g. Attack against a visible
    // monster).
    //
    // Deliberately NOT plan-invalidating (we just dispatched it
    // ourselves) and NOT salient (does not wake the LLM). It's a
    // self-emitted echo that exists solely for dedup + prompt
    // rendering.
    InventoryItemUsed       = 17,
    // combat-damage-output: a per-fight combat OUTCOME observation
    // surfaced to the LLM. Decoded from the server's attacker-side
    // notifications (AttackerNotification 0x01B1 = a swing landed;
    // EvasionAttackerNotification 0x01B3 = a swing was evaded). The
    // Motor counts landed vs evaded swings against the active combat
    // target and, once a fight first produces swing-outcome telemetry,
    // emits ONE deduped event (per target) carrying the RAW
    // landed/evaded/damage counts. Structural wake only — no in-source
    // tactical judgment. Salient (wakes the LLM) but NOT a rejection:
    // it must never poison the ActionRejected dedup or auto-drop the
    // Attack goal — disengage and target choice stay the LLM's call.
    // Name = the defender's
    // display name, Text = the raw "landed N / evaded M" summary.
    CombatFeedback          = 18,
    // self-progress wake (cp-2280): the bot's unspent experience (the
    // spendable self-progress resource, PropertyInt64 AvailableExperience)
    // first became KNOWN or crossed a coarse order-of-magnitude band. The
    // Motor emits ONE deduped event (per band) carrying RAW self facts
    // (unspent XP, lifetime total, current/peak HP, level). Structural
    // salience wake ONLY — a direct analogue of CombatFeedback: it makes
    // the LLM re-read `## Self` early instead of discovering an XP balance
    // only by diffing successive projections. Source assigns NO urgency,
    // names NO attribute/skill, and says NOTHING about spending — WHAT to
    // do with the XP is owned entirely by the prompt RULES (SPEND XP). The
    // band is a generic magnitude-visibility bucket (log10), NOT an
    // attribute/skill XP cost. Text = the raw self-fact summary.
    SelfProgressChanged     = 19,
    // visible-recent-interaction (2026-06-06): the Motor just completed
    // a SPATIAL interact (Use without an item, or Pickup) against a world
    // object — the spatial analogue of InventoryItemUsed above. ItemGuid =
    // the object's guid, Wcid + Name = its identity. Emitted once per
    // completed action cycle whose locked goal was Use/Pickup.
    //
    // Used by LlmGoalPolicy to render the `## Recently interacted objects`
    // surface so the LLM can see "you already interacted with this
    // chest/door N times" and stop re-picking the same object. Motivating
    // case (cp-2290 live-fire): a bot Used the same Holtburg chest 3x and
    // revisited the same door, burning a ~5s LLM round-trip each cycle
    // because nothing in the prompt flagged those objects as already worked.
    //
    // Deliberately NOT salient (does not wake the LLM) and NOT
    // plan-invalidating — a self-emitted echo that exists solely for
    // prompt rendering. UNLIKE the inventory dedup, it drives NO
    // source-side goal drop; whether to re-interact stays the LLM's call.
    WorldObjectInteracted   = 20,
}

/// <summary>
/// One observation. Discriminated by Kind. Strongly-typed
/// payloads (PopupString -> Text, InventoryItemAdded -> ItemGuid +
/// Wcid + Name) keep the LLM prompt deterministic.
/// </summary>
internal sealed record StreamEvent
{
    [JsonPropertyName("seq")]
    public required long Sequence { get; init; }

    [JsonPropertyName("ts")]
    public required DateTimeOffset Utc { get; init; }

    [JsonPropertyName("kind")]
    public required EventKind Kind { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("chat_type")]
    public int? ChatType { get; init; }

    [JsonPropertyName("item_guid")]
    public uint? ItemGuid { get; init; }

    [JsonPropertyName("wcid")]
    public uint? Wcid { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("item_type")]
    public uint? ItemType { get; init; }

    [JsonPropertyName("landblock_from")]
    public uint? LandblockFrom { get; init; }

    [JsonPropertyName("landblock_to")]
    public uint? LandblockTo { get; init; }

    [JsonPropertyName("goal_id")]
    public Guid? GoalId { get; init; }

    [JsonPropertyName("health_fraction")]
    public float? HealthFraction { get; init; }

    // For ActionRejected: raw WeenieError code (e.g. 0x046A) plus
    // a human-readable label resolved from WeenieErrorLabels. The
    // server's accompanying string is in Text. Optional so other
    // event kinds aren't forced to carry it.
    [JsonPropertyName("error_code")]
    public uint? ErrorCode { get; init; }

    [JsonPropertyName("error_label")]
    public string? ErrorLabel { get; init; }

    public override string ToString() => Kind switch
    {
        EventKind.PopupString          => $"#{Sequence} PopupString \"{Truncate(Text, 120)}\"",
        EventKind.ServerMessage        => $"#{Sequence} ServerMessage[{ChatType}] \"{Truncate(Text, 120)}\"",
        EventKind.InventoryItemAdded   => $"#{Sequence} InventoryAdd  wcid={Wcid} name=\"{Name}\" guid=0x{ItemGuid ?? 0:X8}",
        EventKind.InventoryItemRemoved => $"#{Sequence} InventoryRm   wcid={Wcid} name=\"{Name}\" guid=0x{ItemGuid ?? 0:X8}",
        EventKind.LandblockChanged     => $"#{Sequence} Landblock 0x{LandblockFrom ?? 0:X4}->0x{LandblockTo ?? 0:X4}",
        EventKind.GoalEmitted          => $"#{Sequence} GoalEmitted   id={GoalId:N} \"{Truncate(Text, 60)}\"",
        EventKind.GoalCompleted        => $"#{Sequence} GoalCompleted id={GoalId:N}",
        EventKind.GoalFailed           => $"#{Sequence} GoalFailed    id={GoalId:N} reason=\"{Truncate(Text, 60)}\"",
        EventKind.GoalExpired          => $"#{Sequence} GoalExpired   id={GoalId:N}",
        EventKind.NpcDialog            => $"#{Sequence} NpcDialog from=\"{Name}\" \"{Truncate(Text, 120)}\"",
        EventKind.HealthChanged        => $"#{Sequence} Health frac={HealthFraction:F2}",
        EventKind.ActionRejected       => $"#{Sequence} ActionRejected code=0x{ErrorCode ?? 0:X4} label=\"{ErrorLabel ?? "?"}\" message=\"{Truncate(Text, 120)}\"",
        EventKind.BookText             => $"#{Sequence} BookText name=\"{Name}\" guid=0x{ItemGuid ?? 0:X8} \"{Truncate(Text, 120)}\"",
        EventKind.PickerArrivedNoAction => $"#{Sequence} PickerArrivedNoAction guid=0x{ItemGuid ?? 0:X8} name=\"{Name}\" \"{Truncate(Text, 80)}\"",
        EventKind.InventoryItemUsed    => $"#{Sequence} InventoryUsed wcid={Wcid} name=\"{Name}\" guid=0x{ItemGuid ?? 0:X8}",
        EventKind.CombatFeedback       => $"#{Sequence} CombatFeedback target=\"{Name}\" \"{Truncate(Text, 120)}\"",
        EventKind.SelfProgressChanged  => $"#{Sequence} SelfProgress  \"{Truncate(Text, 120)}\"",
        _                              => $"#{Sequence} {Kind}",
    };

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "...");
}

internal sealed class EventStream
{
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly LinkedList<StreamEvent> _events = new();
    private long _nextSeq;

    public EventStream(int capacity = DefaultCapacity)
    {
        if (capacity < 8) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be >= 8");
        _capacity = capacity;
    }

    public int Count => _events.Count;

    /// <summary>Monotonically-increasing sequence assigned by Append.</summary>
    public long NextSequence => _nextSeq;

    /// <summary>
    /// Append a new event. Returns the appended event (with the
    /// assigned Sequence). Evicts the oldest event if capacity
    /// would be exceeded.
    /// </summary>
    public StreamEvent Append(StreamEvent ev)
    {
        var stamped = ev with { Sequence = _nextSeq++ };
        _events.AddLast(stamped);
        while (_events.Count > _capacity)
            _events.RemoveFirst();
        return stamped;
    }

    /// <summary>Newest-first enumeration of all retained events.</summary>
    public IEnumerable<StreamEvent> Recent()
    {
        var node = _events.Last;
        while (node is not null)
        {
            yield return node.Value;
            node = node.Previous;
        }
    }

    /// <summary>Newest-first, limited to N events of any kind.</summary>
    public IReadOnlyList<StreamEvent> Recent(int count) =>
        Recent().Take(count).ToList();

    /// <summary>Newest-first, of a specific kind, limited to N.</summary>
    public IReadOnlyList<StreamEvent> RecentOfKind(EventKind kind, int count) =>
        Recent().Where(e => e.Kind == kind).Take(count).ToList();

    /// <summary>True if any event of the given kind exists at or after the given sequence.</summary>
    public bool HasNewSince(EventKind kind, long sequence) =>
        _events.Any(e => e.Kind == kind && e.Sequence >= sequence);
}
