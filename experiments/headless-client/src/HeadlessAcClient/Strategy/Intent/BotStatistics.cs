// SPDX-License-Identifier: AGPL-3.0-or-later
// BotStatistics — lifetime (per-session) counters the bot accumulates
// from observed events. Distinct from EventStream (which is a bounded
// 256-event ring): these counters are unbounded and survive event
// eviction.
//
// Why this exists (per user feedback on Slice R):
//   The IntentStack predicate DSL initially only supported
//   "since-push" deltas — "kill 3 monsters from now". That's enough
//   for "go grind 3 levels" but not for absolute total tests like
//   "I'll grind until my session kill count reaches 142", and the
//   delta version was scanning the bounded EventStream for
//   GoalCompleted-with-text-startswith-Attack, which silently
//   under-counts whenever > 256 events happen between push and
//   completion check. Using an authoritative monotonic counter
//   fixes both problems.
//
// Update contract:
//   - Single owner: the headless client's receive loop. No locks.
//   - Counters are monotonic-non-decreasing (incrementing only). The
//     only mutator is Pump(EventStream), called once per tick. Pump
//     processes events newer than _lastSequenceProcessed and updates
//     counters; old events are never re-counted.
//   - Snapshot() returns a frozen StatsSnapshot record captured at a
//     point in time, used as a baseline for "since-push" predicates.
//
// What we count today:
//   - Kills              — GoalCompleted with text starting "Attack"
//   - LevelsGained       — net increases in Self.Level we've observed
//                          (separate from absolute level; tracks
//                          deltas the bot achieved during this run)
//   - CorpsesOpened      — GoalCompleted with text starting "Use" and
//                          name matching a recently-died target
//                          (placeholder — populated by HandshakeDriver
//                          via direct Increment calls when Slice Q's
//                          corpse-USE hook fires).
//
// What we DON'T count yet (add when an intent needs them):
//   - Deaths (no Death event in EventStream today)
//   - Gold earned/spent (need to scan InventoryItemAdded for Pyreal wcid)
//   - Quests accepted/completed (need BookText categorization)

using System;

namespace HeadlessAcClient.Strategy.Intent;

internal sealed class BotStatistics
{
    /// <summary>Kills successfully scored this session.</summary>
    public long Kills { get; private set; }

    /// <summary>Net Self.Level increase observed this session (Level - first-observed-Level).</summary>
    public int LevelsGained { get; private set; }

    /// <summary>Corpses we explicitly opened via USE (Slice Q hook).</summary>
    public long CorpsesOpened { get; private set; }

    /// <summary>Books / scrolls / parchments we read at least once.</summary>
    public long BookTextsRead { get; private set; }

    /// <summary>
    /// Cumulative XY distance traveled this session, in AC world units
    /// (~one "step" per unit). Z-axis ignored — we don't want elevator
    /// rides counted as exploration. Updated from per-tick projection
    /// deltas; pure client-side, lost on bot restart.
    /// </summary>
    public double UnitsTraveled { get; private set; }

    private long _lastSequenceProcessed = -1;
    private int? _firstObservedLevel;
    private float? _lastX;
    private float? _lastY;
    private uint? _lastLandblock;

    /// <summary>Manual increment hook (e.g. HandshakeDriver invokes on corpse USE dispatch).</summary>
    public void IncrementCorpsesOpened() => CorpsesOpened++;

    /// <summary>
    /// Walk events that arrived since the last call and update
    /// counters. Idempotent: re-running with no new events is a no-op.
    /// Cheap: O(new events).
    /// </summary>
    public void Pump(EventStream events, WorldStateProjection world)
    {
        // Materialize the newest-first list once so we can index it.
        var batch = events.Recent(EventStream.DefaultCapacity);
        // batch is newest-first; walk it backwards (oldest-first)
        // applying counter updates for any event we haven't already
        // processed. Single forward sweep is sufficient because
        // _lastSequenceProcessed is monotonic.
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            var e = batch[i];
            if (e.Sequence <= _lastSequenceProcessed) continue;
            Apply(e);
            if (e.Sequence > _lastSequenceProcessed) _lastSequenceProcessed = e.Sequence;
        }

        // Level deltas: track the first level we ever observe and
        // compute LevelsGained as (current - first). Simpler than
        // accumulating deltas on every change and avoids miscounting
        // if the projection misses a tick.
        if (world.Self.Level is int lvl)
        {
            _firstObservedLevel ??= lvl;
            var delta = lvl - _firstObservedLevel.Value;
            if (delta > LevelsGained) LevelsGained = delta;
        }

        // Distance: integrate per-tick XY position deltas. We deliberately
        // RESET _lastX/_lastY on landblock transition (teleports, portals,
        // Free Ride routes) so the bot doesn't credit "moved 2km" for a
        // single instantaneous warp. A real player physically walking
        // crosses landblocks too, but a step-rate of one cell per tick
        // is impossible — so we just zero the integrator on any cross.
        var x = world.Self.PositionX;
        var y = world.Self.PositionY;
        var lb = world.Self.Landblock;
        if (_lastX is float lx && _lastY is float ly && _lastLandblock is uint llb && llb == lb)
        {
            var dx = x - lx;
            var dy = y - ly;
            var d = Math.Sqrt(dx * dx + dy * dy);
            // Cap per-tick to 50u to defend against teleports within the
            // same landblock (e.g. lifestone tie-recall short hop). At
            // ~5-10u/tick walking speed this never clips honest motion.
            if (d > 0 && d < 50.0)
                UnitsTraveled += d;
        }
        _lastX = x;
        _lastY = y;
        _lastLandblock = lb;
    }

    private void Apply(StreamEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.GoalCompleted:
                if (!string.IsNullOrEmpty(e.Text) &&
                    e.Text.StartsWith("Attack", StringComparison.Ordinal))
                {
                    Kills++;
                }
                break;
            case EventKind.BookText:
                BookTextsRead++;
                break;
        }
    }

    /// <summary>Capture an immutable snapshot of all counters at a point in time.</summary>
    public StatsSnapshot Snapshot() => new(Kills, LevelsGained, CorpsesOpened, BookTextsRead, UnitsTraveled);
}

/// <summary>
/// Immutable copy of all stat counters at a specific moment. Stored
/// on IntentBaseline so "since-push" predicates can compute deltas
/// against the precise counter values that were in effect at push.
/// </summary>
internal readonly record struct StatsSnapshot(
    long Kills,
    int LevelsGained,
    long CorpsesOpened,
    long BookTextsRead,
    double UnitsTraveled);
