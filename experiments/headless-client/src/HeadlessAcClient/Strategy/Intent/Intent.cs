// SPDX-License-Identifier: AGPL-3.0-or-later
// Intent / IntentStack — FILO stack of strategic commitments above
// the per-tick Goal. Solves the cross-deliberation amnesia problem
// the user identified:
//
//   "the bot had a FILO intent queue, you could see what it is doing
//    and planning to do, and it would not forget to return to jonathon
//    after exploring and fighting a bit. also, this way if there are
//    complex instructions from a quest, those could also be queued.
//    it also allows a bot to do a side quest it discovered while on a
//    separate quest."
//
// Layering:
//
//   Goal      — per-tick tactical unit (Use, Talk, Attack, Pickup...).
//               Existed before Slice R. Consumed by Tactics.
//   Intent    — persistent strategic frame (a multi-step commitment).
//               LLM pushes/pops these as it plans.
//   Stack     — small FILO container of Intents. Single owner: the
//               strategy/tactics tick loop. Bounded depth.
//
// Per the rubber-duck review (Slice R design) we enforce:
//
//   B1. ActionCompleted != IntentCompleted. The stack DOES NOT auto-
//       pop the top frame just because a Goal finished. Top is popped
//       only when its CompletionPredicate returns true OR its Deadline
//       elapses OR the LLM explicitly emits a pop_top op.
//
//   B2. Completion is a TYPED predicate (see IntentPredicate), not a
//       freeform LLM expression. Baselines are captured at push time
//       so delta-style predicates ("gained 2 levels", "killed 10
//       Golems since push") can compare apples-to-apples.
//
//   B3. Lifecycle is explicit. v1 supports:
//         Active     — current head of execution.
//         Blocked    — top can't make progress (target gone, rejected
//                      too many times); LLM should pop or replace.
//         Completed  — predicate satisfied; archived in History.
//         Expired    — deadline elapsed; archived in History.
//
//   Non-blocking choices we kept simple in v1:
//     - No middle-stack abandon by id. LLM must pop_top, then push
//       again. Keeps stack reasoning linear.
//     - Combat/loot/heal stay as TACTICAL interrupts (existing pre-
//       emptors in HandshakeDriver), NOT on the stack. Otherwise the
//       LLM has to constantly re-push trivial reflexes.
//     - MaxDepth=8 with REFUSE-on-overflow. Never pop the root (the
//       overarching mission). Avoids cliff effects where context loss
//       makes the bot forget why it logged in.
//     - Stack revision counter increments on every mutation. Surfaced
//       in the LLM prompt so a stale in-flight response can be
//       detected and discarded.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;

namespace HeadlessAcClient.Strategy.Intent;

internal enum IntentLifecycle
{
    Active    = 0,
    Blocked   = 1,
    Completed = 2,
    Expired   = 3,
}

/// <summary>
/// One strategic commitment. The LLM authors the Kind / Target /
/// Rationale strings; the typed CompletionPredicate is what we
/// actually evaluate per tick. Baseline is captured by IntentStack
/// at push time so per-tick predicate evaluation is deterministic.
/// </summary>
internal sealed record Intent
{
    /// <summary>Monotonic per-process id, e.g. "i-007". Stable across pop/push.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Free-form short label chosen by the LLM. Examples: "quest:apple-collect", "grind-to-level-10", "go-buy-spell-comps".</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("target_name")]
    public string? TargetName { get; init; }

    [JsonPropertyName("target_guid")]
    public uint? TargetGuid { get; init; }

    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = "";

    /// <summary>Wall-clock pop deadline. Null = no expiry.</summary>
    [JsonPropertyName("deadline_utc")]
    public DateTime? DeadlineUtc { get; init; }

    /// <summary>How completion is detected. See IntentPredicate.cs for the DSL.</summary>
    [JsonPropertyName("completion")]
    public required IntentPredicate Completion { get; init; }

    /// <summary>Captured at push time by IntentStack.TryPush.</summary>
    [JsonPropertyName("baseline")]
    public required IntentBaseline Baseline { get; init; }

    [JsonPropertyName("status")]
    public IntentLifecycle Status { get; init; } = IntentLifecycle.Active;

    [JsonPropertyName("last_failure")]
    public string? LastFailure { get; init; }

    /// <summary>
    /// Free-form LLM note describing a completion condition that no
    /// existing predicate expresses cleanly. Surfaced in training
    /// data and in the rendered intent stack so the next deliberation
    /// reminds the LLM what it asked for. Never consumed at runtime.
    /// </summary>
    [JsonPropertyName("predicate_request")]
    public string? PredicateRequest { get; init; }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder(96);
        sb.Append('[').Append(Id).Append("] ").Append(Kind);
        if (!string.IsNullOrEmpty(TargetName)) sb.Append(" target=\"").Append(TargetName).Append('"');
        else if (TargetGuid is uint g)         sb.Append(" target=0x").Append(g.ToString("X8"));
        sb.Append(" until=").Append(Completion.Summary());
        sb.Append(" status=").Append(Status);
        if (DeadlineUtc is DateTime d) sb.Append(" deadline=").Append(d.ToString("u"));
        if (!string.IsNullOrEmpty(LastFailure)) sb.Append(" lastFail=\"").Append(LastFailure).Append('"');
        return sb.ToString();
    }
}

internal enum StackOpResult
{
    Ok                = 0,
    RefusedOverflow   = 1,
    RefusedRootPop    = 2,
    RefusedEmpty      = 3,
    RefusedRevision   = 4,
}

/// <summary>
/// FILO stack of Intents. Single-owner mutator (the tick loop). Per
/// the rubber-duck critique we cap depth, refuse-on-overflow, never
/// pop the root, and maintain a revision counter for stale-response
/// race detection.
/// </summary>
internal sealed class IntentStack
{
    public const int DefaultMaxDepth = 8;

    private readonly List<Intent> _frames = new();
    private readonly List<Intent> _history = new();
    private readonly int _maxDepth;
    private long _revision;

    public IntentStack(int maxDepth = DefaultMaxDepth)
    {
        if (maxDepth < 2) throw new ArgumentOutOfRangeException(nameof(maxDepth), "must be >= 2 (root + at least one nested)");
        _maxDepth = maxDepth;
    }

    /// <summary>Increments on every mutation. Surfaced to the LLM so stale responses can be detected.</summary>
    public long Revision => _revision;

    public int Depth => _frames.Count;

    public int MaxDepth => _maxDepth;

    public bool IsEmpty => _frames.Count == 0;

    public Intent? Top => _frames.Count == 0 ? null : _frames[^1];

    public Intent? Root => _frames.Count == 0 ? null : _frames[0];

    /// <summary>Bottom-up snapshot (root at index 0, top at last index).</summary>
    public IReadOnlyList<Intent> Frames => _frames.ToList();

    /// <summary>Newest-first history of popped frames (Completed / Expired / Blocked-then-popped).</summary>
    public IReadOnlyList<Intent> History => _history.AsReadOnly();

    /// <summary>
    /// Push a new intent on top of the stack. Captures baseline from
    /// (world, events, now) if intent's Baseline is the sentinel
    /// "uncaptured" instance from BuildBlank. Refuses on overflow
    /// (and if expectedRevision is supplied and doesn't match).
    /// </summary>
    public StackOpResult TryPush(Intent intent, long? expectedRevision = null)
    {
        if (expectedRevision is long er && er != _revision) return StackOpResult.RefusedRevision;
        if (_frames.Count >= _maxDepth) return StackOpResult.RefusedOverflow;

        _frames.Add(intent);
        _revision++;
        return StackOpResult.Ok;
    }

    /// <summary>
    /// Pop the top frame with the given final lifecycle (Completed,
    /// Expired, or Blocked-then-popped manually). Refuses if doing so
    /// would empty the stack (root is sacred — push a new root via
    /// caller bootstrap instead).
    /// </summary>
    public StackOpResult PopTop(IntentLifecycle finalStatus, string? reason = null, long? expectedRevision = null)
    {
        if (expectedRevision is long er && er != _revision) return StackOpResult.RefusedRevision;
        if (_frames.Count == 0) return StackOpResult.RefusedEmpty;
        if (_frames.Count == 1) return StackOpResult.RefusedRootPop;

        var top = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        var archived = top with
        {
            Status = finalStatus,
            LastFailure = reason ?? top.LastFailure,
        };
        _history.Insert(0, archived);
        if (_history.Count > 32) _history.RemoveAt(_history.Count - 1);
        _revision++;
        return StackOpResult.Ok;
    }

    /// <summary>Replace the top frame in place. Used when the LLM realizes the current intent is wrong-but-related.</summary>
    public StackOpResult ReplaceTop(Intent newTop, string? reason = null, long? expectedRevision = null)
    {
        if (expectedRevision is long er && er != _revision) return StackOpResult.RefusedRevision;
        if (_frames.Count == 0) return StackOpResult.RefusedEmpty;

        var old = _frames[^1];
        _frames[^1] = newTop;
        var archived = old with
        {
            Status = IntentLifecycle.Blocked,
            LastFailure = reason ?? "replaced",
        };
        _history.Insert(0, archived);
        if (_history.Count > 32) _history.RemoveAt(_history.Count - 1);
        _revision++;
        return StackOpResult.Ok;
    }

    /// <summary>Mark the top frame Blocked in place (no pop). The LLM can later replace or pop it.</summary>
    public StackOpResult MarkTopBlocked(string reason, long? expectedRevision = null)
    {
        if (expectedRevision is long er && er != _revision) return StackOpResult.RefusedRevision;
        if (_frames.Count == 0) return StackOpResult.RefusedEmpty;

        _frames[^1] = _frames[^1] with
        {
            Status = IntentLifecycle.Blocked,
            LastFailure = reason,
        };
        _revision++;
        return StackOpResult.Ok;
    }

    /// <summary>
    /// Evaluate Top against (world, events, now). If its predicate is
    /// satisfied OR its deadline elapsed, pop it (with the matching
    /// final status) and return the popped frame. Otherwise return
    /// null. This is the per-tick "is the bot done yet?" hook.
    /// </summary>
    public Intent? CheckTopForCompletion(WorldStateProjection world, EventStream events, DateTime utcNow)
        => CheckTopForCompletion(world, events, utcNow, stats: null);

    /// <summary>
    /// Stats-aware overload. The HandshakeDriver tick passes its
    /// BotStatistics instance so kill-count, levels-gained, units-
    /// traveled, etc. predicates can resolve. Legacy callers (tests,
    /// pure-predicate evaluations) pass null and stats-based predicates
    /// evaluate false.
    /// </summary>
    public Intent? CheckTopForCompletion(WorldStateProjection world, EventStream events, DateTime utcNow, BotStatistics? stats)
    {
        if (_frames.Count == 0) return null;

        var top = _frames[^1];
        if (top.Status == IntentLifecycle.Completed || top.Status == IntentLifecycle.Expired)
        {
            // shouldn't happen — terminal statuses live in History — but be defensive.
            return null;
        }

        if (top.DeadlineUtc is DateTime d && utcNow >= d)
        {
            if (_frames.Count == 1)
            {
                // Root expired: mark Blocked but don't pop. Caller
                // (likely the LLM next deliberation) will see the
                // Expired-but-rooted state and replace.
                _frames[0] = top with
                {
                    Status = IntentLifecycle.Blocked,
                    LastFailure = "deadline elapsed (root)",
                };
                _revision++;
                return null;
            }
            PopTop(IntentLifecycle.Expired, "deadline elapsed");
            return _history[0];
        }

        var ctx = new IntentEvalContext(world, events, top.Baseline, utcNow) { Stats = stats };
        if (top.Completion.IsSatisfied(ctx))
        {
            if (_frames.Count == 1)
            {
                // Root completed: mark Completed in place; caller decides whether to bootstrap a new root.
                _frames[0] = top with { Status = IntentLifecycle.Completed };
                _revision++;
                return _frames[0];
            }
            PopTop(IntentLifecycle.Completed, "predicate satisfied");
            return _history[0];
        }

        return null;
    }

    public IntentStackSnapshot Snapshot() =>
        new()
        {
            Revision = _revision,
            MaxDepth = _maxDepth,
            Frames = _frames.ToImmutableList(),
            History = _history.ToImmutableList(),
        };
}

internal sealed record IntentStackSnapshot
{
    [JsonPropertyName("revision")]    public required long Revision { get; init; }
    [JsonPropertyName("max_depth")]   public required int MaxDepth { get; init; }
    [JsonPropertyName("frames")]      public required IReadOnlyList<Intent> Frames { get; init; }
    [JsonPropertyName("history")]     public required IReadOnlyList<Intent> History { get; init; }
}

/// <summary>
/// Monotonic id generator for Intent.Id. Thread-affine (single-owner).
/// </summary>
internal sealed class IntentIdAllocator
{
    private int _next = 1;
    public string Allocate()
    {
        var id = _next++;
        return $"i-{id:D3}";
    }
}
