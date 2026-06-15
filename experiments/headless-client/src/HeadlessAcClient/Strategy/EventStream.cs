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
    // self-progress wake (cp-2280, generalized to a value-edge): the bot's
    // unspent experience (the spendable self-progress resource, PropertyInt64
    // AvailableExperience) took a NEW value — either first became KNOWN or
    // differs from the last observed value. The Motor emits ONE deduped event
    // (consecutive value-edge) carrying RAW self facts (unspent XP, lifetime
    // total, current/peak HP, level). Structural salience wake ONLY — a
    // direct analogue of CombatFeedback: it makes the LLM re-read `## Self`
    // (e.g. after an instant XP-spend, which emits no external salient event)
    // instead of discovering an XP balance only by diffing successive
    // projections. Source assigns NO urgency, names NO attribute/skill, and
    // says NOTHING about spending — WHAT to do with the XP is owned entirely
    // by the prompt RULES (SPEND XP). Dedup is an exact value-edge, NOT a
    // magnitude band — no judgement about how much XP change is material.
    // Text = the raw self-fact summary.
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
    // inbound-damage-onset-wake: the bot just TOOK a landed inbound swing
    // (GameEventDefenderNotification 0x01B2). The Motor emits ONE deduped
    // event per inbound-damage EPISODE (the first hit, or the first hit after
    // a >= window-TTL lull in inbound hits) carrying RAW facts (the damage
    // amount and the attacker's display name). Structural salience wake ONLY,
    // the DEFENSIVE analogue of CombatFeedback above: the offensive
    // CombatFeedback one-shot fires on the bot's first SWING (early, still
    // healthy), so it never coincides with the moment the bot STARTS taking
    // damage. This wakes the LLM at that moment so it re-reads the
    // `## Combat readiness` inbound-damage line (cp-2314) and decides whether
    // to keep attacking, disengage (Explore), or Recall (cp-2310) — WHICH the
    // source never decides. Episode dedup is a hit-lull bookkeeping gate, NOT
    // an HP/damage magnitude threshold (cp-2280: salience must never encode a
    // materiality band). Name = the attacker's display name, Text = the raw
    // "inbound hit landed (N damage) from X" summary.
    InboundDamageTaken      = 21,
    // npc-local-speech-perception: the player HEARD local/area chat-text
    // (GameMessageHearSpeech 0x02BB) from a non-self speaker — the spoken-aloud
    // sibling of the directed NpcDialog (a server Tell). The receive loop routes
    // one per non-self HearSpeech into EventStream's DEDICATED heard-speech
    // window (AppendHeardSpeech) — NOT the main event ring — because ambient
    // local speech can be high-volume and must never evict the bot's critical
    // recent-event memory. Deliberately NOT salient (omitted from
    // LlmGoalPolicy.IsSalientKind): low-priority ambient context, surfaced in
    // `## Server hints` only when the LLM is already deliberating, NEVER a wake
    // on its own. Name = speaker display name, Text = the spoken line, ChatType
    // = the raw wire ChatMessageType, ItemGuid = speaker guid.
    HeardSpeech             = 22,
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
        EventKind.InboundDamageTaken   => $"#{Sequence} InboundDamage \"{Truncate(Text, 120)}\"",
        EventKind.HeardSpeech          => $"#{Sequence} HeardSpeech from=\"{Name}\" \"{Truncate(Text, 120)}\"",
        _                              => $"#{Sequence} {Kind}",
    };

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "...");
}

internal sealed class EventStream
{
    public const int DefaultCapacity = 256;

    // Server PopupStrings are the server's directed tutorial/instruction text
    // (low-volume, directed-at-self — NOT the chat firehose). A one-time login
    // directive ("go talk to X to leave; give the token back") is the bot's
    // "how to proceed/exit here" guidance, but it ages out of the bounded event
    // ring long before the bot is ready to act on it (~256 events later). Keep a
    // SEPARATE, distinct-by-text, first-seen, capped list of the EARLIEST popups
    // that survives the ring so those durable early anchors can still surface in
    // the prompt. Server text only; captured by event KIND + age, never by
    // parsing the text (which would be hardcoded game knowledge). Once the cap is
    // reached the earliest anchors are locked in; newer popups still surface via
    // the recent ring.
    private const int MaxPersistentPopups = 24;
    private readonly List<StreamEvent> _persistentPopups = new();
    private readonly HashSet<string> _persistentPopupTexts = new(StringComparer.Ordinal);

    // NPC-spoken directives arrive as NpcDialog events (the server's Tell from a
    // non-self guid — see HandshakeDriver). They carry the same kind of "how to
    // proceed/where to go next" guidance as PopupStrings ("go talk to X in the
    // next room", a quest assignment), but they too age out of the bounded ring
    // before the bot acts AND the `## Server hints` section that carries them is
    // hard-cut in dense scenes. Persist the EARLIEST distinct NpcDialog lines the
    // same way, with a SMALLER cap because NPC speech is chattier than the
    // low-volume directed PopupStrings. Captured by event KIND + first-seen text
    // + cap only — never by parsing the content (that would be hardcoded game
    // knowledge); the LLM and the prompt's "greetings/flavor are not tasks" rule
    // filter non-directives.
    private const int MaxPersistentNpcDialogs = 8;
    private readonly List<StreamEvent> _persistentNpcDialogs = new();
    private readonly HashSet<string> _persistentNpcDialogTexts = new(StringComparer.Ordinal);

    // The EARLIEST stores above lock in once their cap is reached, so a LATE
    // directive (e.g. a "you have completed your training, take the portal"
    // NpcDialog that only fires after a long onboarding) is never persisted and
    // is then evicted from the bounded event ring by intervening combat — it
    // reaches the prompt ONLY via `## Server hints`, which is hard-cut in dense
    // scenes (cp-2382/2383/2385 lineage). Keep a SEPARATE sliding window of the
    // MOST-RECENT distinct directives (newest pushes out oldest) so the CURRENT
    // actionable instruction also survives ring eviction and the prompt hard-cut.
    // Captured by event KIND + text only — never parsed (no game knowledge).
    private const int MaxRecentPopups = 4;
    private const int MaxRecentNpcDialogs = 4;
    private readonly List<StreamEvent> _recentPopups = new();
    private readonly List<StreamEvent> _recentNpcDialogs = new();

    // npc-local-speech-perception — heard local/area speech (HeardSpeech). This
    // is AMBIENT and potentially HIGH-VOLUME (creature emotes, other players'/
    // bots' chatter), so it lives in a SEPARATE bounded window and is NEVER
    // appended to the main event ring: flooding the ring would evict the bot's
    // recent ActionRejected / GoalFailed / goal-lifecycle memory (the prompt
    // looks back ~100 events) and could strand it in a retry loop. Captured by
    // event KIND only, never by parsing the spoken text (no game knowledge).
    private const int MaxHeardSpeech = 12;
    private readonly List<StreamEvent> _heardSpeech = new();

    // Goal emissions are infrequent (one every several seconds) but the main
    // ring is perception/motion-dominated, so a goal can be evicted within
    // seconds under heavy traffic. Keep a DEDICATED window of recent
    // GoalEmitted events that outlives the ring, so goal-history reads (e.g.
    // counting repeated actions toward the same target) span the full realistic
    // window, not the ring's perception-bounded seconds. Same durability
    // pattern as the popup/NPC-dialog stores above. No text parsing here; pure
    // bookkeeping.
    //
    // Retain by TIME, not a small fixed count: a count cap could trim an early
    // hand-in attempt once enough UNRELATED goals follow in a chatty session,
    // silently dropping a recomputed attempt count back below threshold (the
    // same bug, just delayed from seconds to minutes). A generous hard count
    // cap bounds memory against a pathological burst.
    private static readonly TimeSpan GoalEmissionRetention = TimeSpan.FromMinutes(30);
    private const int MaxRecentGoalEmissions = 512;
    private readonly List<StreamEvent> _recentGoalEmissions = new();

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

        // Capture the earliest distinct server PopupStrings into a store that
        // outlives the ring. Gate on event KIND + first-seen text + a hard cap —
        // no parsing of the text content (that would be hardcoded game knowledge).
        if (stamped.Kind == EventKind.PopupString
            && !string.IsNullOrEmpty(stamped.Text)
            && _persistentPopups.Count < MaxPersistentPopups
            && _persistentPopupTexts.Add(stamped.Text))
        {
            _persistentPopups.Add(stamped);
        }

        // Same durability for the earliest distinct NPC-spoken directives.
        if (stamped.Kind == EventKind.NpcDialog
            && !string.IsNullOrEmpty(stamped.Text)
            && _persistentNpcDialogs.Count < MaxPersistentNpcDialogs
            && _persistentNpcDialogTexts.Add(stamped.Text))
        {
            _persistentNpcDialogs.Add(stamped);
        }

        // Sliding window of the MOST-RECENT distinct directives (both kinds), so
        // a late actionable instruction survives ring eviction even after the
        // earliest store is full. Move-to-newest on a repeat, then trim oldest.
        if (stamped.Kind == EventKind.PopupString && !string.IsNullOrEmpty(stamped.Text))
        {
            _recentPopups.RemoveAll(e => string.Equals(e.Text, stamped.Text, StringComparison.Ordinal));
            _recentPopups.Add(stamped);
            while (_recentPopups.Count > MaxRecentPopups)
                _recentPopups.RemoveAt(0);
        }
        if (stamped.Kind == EventKind.NpcDialog && !string.IsNullOrEmpty(stamped.Text))
        {
            _recentNpcDialogs.RemoveAll(e => string.Equals(e.Text, stamped.Text, StringComparison.Ordinal));
            _recentNpcDialogs.Add(stamped);
            while (_recentNpcDialogs.Count > MaxRecentNpcDialogs)
                _recentNpcDialogs.RemoveAt(0);
        }

        // Durable goal-emission window (see _recentGoalEmissions): retain recent
        // GoalEmitted events independent of the ring so a goal is not lost to
        // perception eviction within seconds. Evict by TIME (older than the
        // retention window), then a generous hard count cap as a memory bound.
        if (stamped.Kind == EventKind.GoalEmitted)
        {
            _recentGoalEmissions.Add(stamped);
            var cutoff = stamped.Utc - GoalEmissionRetention;
            while (_recentGoalEmissions.Count > 0
                && (_recentGoalEmissions[0].Utc < cutoff
                    || _recentGoalEmissions.Count > MaxRecentGoalEmissions))
                _recentGoalEmissions.RemoveAt(0);
        }

        return stamped;
    }

    /// <summary>
    /// Append a heard local/area speech (HeardSpeech) observation to its
    /// DEDICATED bounded window — NOT the main event ring. Ambient local speech
    /// can be high-volume; segregating it keeps the ring's critical
    /// action/rejection/goal-lifecycle memory intact. Stamps a monotonic
    /// Sequence (the shared counter) so consumers can order earliest/newest.
    /// Evicts the oldest beyond <see cref="MaxHeardSpeech"/>. No in-store dedup,
    /// so the prompt can still derive a repeat count over the window.
    /// </summary>
    public StreamEvent AppendHeardSpeech(StreamEvent ev)
    {
        var stamped = ev with { Sequence = _nextSeq++ };
        _heardSpeech.Add(stamped);
        while (_heardSpeech.Count > MaxHeardSpeech)
            _heardSpeech.RemoveAt(0);
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

    /// <summary>
    /// Newest-first GoalEmitted events from the DEDICATED durable window, which
    /// outlives the perception-dominated ring — so goal history spans minutes,
    /// not the ring's seconds. Limited to the retained
    /// <see cref="MaxRecentGoalEmissions"/>. Use this (not the ring) when
    /// counting recent goals so high-volume perception traffic cannot evict a
    /// goal before it is counted.
    /// </summary>
    public IReadOnlyList<StreamEvent> RecentGoalEmissions()
    {
        var result = new List<StreamEvent>(_recentGoalEmissions.Count);
        for (int i = _recentGoalEmissions.Count - 1; i >= 0; i--)
            result.Add(_recentGoalEmissions[i]);
        return result;
    }

    /// <summary>Newest-first, of a specific kind, limited to N.</summary>
    public IReadOnlyList<StreamEvent> RecentOfKind(EventKind kind, int count) =>
        Recent().Where(e => e.Kind == kind).Take(count).ToList();

    /// <summary>True if any event of the given kind exists at or after the given sequence.</summary>
    public bool HasNewSince(EventKind kind, long sequence) =>
        _events.Any(e => e.Kind == kind && e.Sequence >= sequence);

    /// <summary>
    /// The earliest distinct server PopupString events seen this session, in
    /// first-seen order, capped at <see cref="MaxPersistentPopups"/>. These
    /// survive eviction from the bounded event ring so a one-time login/exit
    /// directive remains available to the prompt long after it ages out of
    /// <see cref="Recent()"/>. Each retains its original (low) Sequence so a
    /// consumer can order it as "earliest".
    /// </summary>
    public IReadOnlyList<StreamEvent> PersistentPopupStrings() => _persistentPopups;

    /// <summary>
    /// The earliest distinct NPC-spoken directive lines (NpcDialog) seen this
    /// session, in first-seen order, capped at <see cref="MaxPersistentNpcDialogs"/>.
    /// Like <see cref="PersistentPopupStrings"/> these survive ring eviction so an
    /// early "go to X / do Y" NPC instruction stays available to the prompt. Each
    /// retains its original (low) Sequence and its <c>Name</c> (the speaking NPC).
    /// </summary>
    public IReadOnlyList<StreamEvent> PersistentNpcDialogs() => _persistentNpcDialogs;

    /// <summary>
    /// The MOST-RECENT distinct server PopupString directives seen this session,
    /// oldest-first, capped at <see cref="MaxRecentPopups"/>. Complements
    /// <see cref="PersistentPopupStrings"/> (the earliest anchors): once the
    /// earliest store is full this sliding window keeps the CURRENT directive
    /// available past ring eviction and the prompt hard-cut.
    /// </summary>
    public IReadOnlyList<StreamEvent> RecentPersistentPopupStrings() => _recentPopups;

    /// <summary>
    /// The MOST-RECENT distinct NPC-spoken directives (NpcDialog), oldest-first,
    /// capped at <see cref="MaxRecentNpcDialogs"/>. Complements
    /// <see cref="PersistentNpcDialogs"/> so a late "you are done, now do X"
    /// instruction survives even after the earliest store is full.
    /// </summary>
    public IReadOnlyList<StreamEvent> RecentPersistentNpcDialogs() => _recentNpcDialogs;

    /// <summary>
    /// The MOST-RECENT heard local/area speech (HeardSpeech), capped at
    /// <see cref="MaxHeardSpeech"/>. Kept OUT of the main event ring (see
    /// <see cref="AppendHeardSpeech"/>) so ambient chatter never evicts the
    /// bot's critical recent-event memory. Read by the prompt's `## Server
    /// hints` HeardSpeech category. Raw recent lines (no in-store dedup) so the
    /// prompt can derive a repeat count; the consumer orders by Sequence.
    /// </summary>
    public IReadOnlyList<StreamEvent> RecentHeardSpeech() => _heardSpeech;
}
