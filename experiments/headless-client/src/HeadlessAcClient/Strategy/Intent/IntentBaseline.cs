// SPDX-License-Identifier: AGPL-3.0-or-later
// IntentBaseline — frozen world snapshot captured at the moment an
// intent is pushed onto the stack. Predicates that observe state
// CHANGE (events arriving since N, landblock crossing, levels gained,
// kills accumulated) consult this baseline rather than the live world.
//
// Without baselines: "I have the Calling Stone" trivially completes
// the moment you push the intent if you already have one in your
// pack. "Talked to Jonathan" completes on the first NpcDialog event
// that happened to be from him before you decided to go see him.
//
// Captured at push time:
//   - PushedAtUtc           — wall clock for ElapsedSeconds checks.
//   - LastEventSequence     — Events.Recent up to this seq are PRE.
//   - Landblock             — for "left this area" detection.
//   - Position              — for distance-from-spawn style checks.
//   - Level                 — for "gained N levels" detection.
//   - VisibleAtPush         — guid set, for "this target disappeared"
//                             detection.
//   - InventoryCountsAtPush — wcid -> count, deep-copied so later
//                             changes to live inventory don't mutate
//                             the snapshot.
//
// Single mutator: IntentStack.TryPush. Read by IntentPredicate
// implementations only. Records are immutable so concurrent readers
// are safe even if the stack mutator runs on a different thread.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy.Intent;

internal sealed record IntentBaseline
{
    [JsonPropertyName("pushed_at_utc")]
    public required DateTime PushedAtUtc { get; init; }

    [JsonPropertyName("last_event_sequence")]
    public required long LastEventSequence { get; init; }

    [JsonPropertyName("landblock")]
    public uint? Landblock { get; init; }

    [JsonPropertyName("position")]
    public Vector3? Position { get; init; }

    [JsonPropertyName("level")]
    public int? Level { get; init; }

    /// <summary>PropertyInt.NumDeaths at push (server-authoritative).</summary>
    [JsonPropertyName("num_deaths")]
    public int? NumDeaths { get; init; }

    /// <summary>PropertyInt.CoinValue at push (server-authoritative pyreal count).</summary>
    [JsonPropertyName("coin_value")]
    public int? CoinValue { get; init; }

    [JsonPropertyName("visible_at_push")]
    public required ImmutableHashSet<uint> VisibleAtPush { get; init; }

    [JsonPropertyName("inventory_counts_at_push")]
    public required ImmutableDictionary<uint, int> InventoryCountsAtPush { get; init; }

    /// <summary>
    /// Frozen lifetime-counter values at push time. Lets predicates
    /// compute "since-push" deltas authoritatively without scanning
    /// the bounded EventStream.
    /// </summary>
    [JsonPropertyName("stats_at_push")]
    public required StatsSnapshot StatsAtPush { get; init; }

    public static IntentBaseline Capture(WorldStateProjection world, EventStream events, DateTime utcNow)
        => Capture(world, events, utcNow, stats: null);

    public static IntentBaseline Capture(
        WorldStateProjection world,
        EventStream events,
        DateTime utcNow,
        BotStatistics? stats)
    {
        var visible = world.Visible.Select(v => v.Guid).ToImmutableHashSet();

        var inv = world.Inventory
            .Where(i => i.Wcid != 0)
            .GroupBy(i => i.Wcid)
            .ToImmutableDictionary(g => g.Key, g => g.Count());

        Vector3? pos = new Vector3(world.Self.PositionX, world.Self.PositionY, world.Self.PositionZ);

        // Events.NextSequence is the seq the NEXT Append will use, so
        // "last appended" is NextSequence-1. Predicates compare with
        // `> LastEventSequence`, so picking NextSequence-1 makes the
        // boundary exclusive of pre-push events and inclusive of any
        // event appended on or after the push.
        var lastSeq = events.NextSequence - 1;

        return new IntentBaseline
        {
            PushedAtUtc = utcNow,
            LastEventSequence = lastSeq,
            Landblock = world.Self.Landblock,
            Position = pos,
            Level = world.Self.Level,
            NumDeaths = world.Self.NumDeaths,
            CoinValue = world.Self.CoinValue,
            VisibleAtPush = visible,
            InventoryCountsAtPush = inv,
            StatsAtPush = stats?.Snapshot() ?? default,
        };
    }
}
