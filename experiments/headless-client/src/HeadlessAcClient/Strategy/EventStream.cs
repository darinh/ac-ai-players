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
    // The server returned a WeenieError (or WeenieErrorWithString)
    // in response to an action we dispatched. The bot's Strategy
    // layer reads this from EventStream + WorldStateProjection
    // (recent_rejections section) so the LLM knows not to re-propose
    // the same (kind, target, item) combination.
    GoalRejected          = 12,
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

    /// <summary>
    /// WeenieError code as reported by the server. Only populated
    /// for <see cref="EventKind.GoalRejected"/>.
    /// </summary>
    [JsonPropertyName("error_code")]
    public uint? ErrorCode { get; init; }

    /// <summary>
    /// Stringified <see cref="GoalKind"/> of the goal whose dispatch
    /// produced the rejection. Carried denormalized on the event so
    /// the projection / prompt rendering doesn't need to chase the
    /// goal by id after the original Goal record may be gone.
    /// </summary>
    [JsonPropertyName("rejected_goal_kind")]
    public string? RejectedGoalKind { get; init; }

    /// <summary>
    /// Name of the item involved in a Give-style rejection (sourced
    /// from the dispatch-time snapshot). Distinct from <see cref="Name"/>,
    /// which is reserved for the target NPC's name.
    /// </summary>
    [JsonPropertyName("item_name")]
    public string? ItemName { get; init; }

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
        EventKind.GoalRejected         =>
            $"#{Sequence} GoalRejected  kind={RejectedGoalKind ?? "?"} " +
            $"target=\"{Name}\"" +
            (string.IsNullOrEmpty(ItemName) ? "" : $" item=\"{ItemName}\"") +
            $" error=0x{ErrorCode ?? 0:X4}" +
            (string.IsNullOrEmpty(Text) ? "" : $" \"{Truncate(Text, 100)}\""),
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
