// SPDX-License-Identifier: AGPL-3.0-or-later
// LlmGoalPolicy — orchestrates the LLM goal-derivation loop.
//
//   1. Decide whether to call (event-driven, not per-tick).
//   2. Build prompt from WorldStateProjection + EventStream tail.
//   3. Call LlmGoalClient.
//   4. Parse Goal JSON. On parse failure, fall back to inner policy.
//   5. Hand back the new (or current) goal.
//
// The LLM is the COMPILER, not the controller. It produces a Goal
// then steps out. Tactics executes the goal tick-by-tick using
// only schema knowledge.
//
// What we DON'T do here (per architecture):
//   - We never hardcode a wcid/name as a content trigger. The
//     prompt presents what was OBSERVED (inventory + visible +
//     events) and asks the LLM to pick. If the LLM picks
//     "Jonathan" it's because the projection showed an NPC
//     named Jonathan AND an inventory item whose ShortDesc
//     says "Give this token to Jonathan...". The content lives
//     in the game data, not in the source code.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessAcClient.Strategy;

internal sealed class LlmGoalPolicy : IGoalPolicy
{
    private readonly LlmGoalClient _client;
    private readonly IGoalPolicy _fallback;
    private readonly IWeenieRepository _weenies;
    private readonly ITrainingDataSink? _training;

    /// <summary>
    /// Minimum interval between LLM calls. Even when an event would
    /// normally trigger a call, we coalesce within this window to
    /// avoid bursting the LLM with quick-fire popups.
    /// </summary>
    public TimeSpan MinCallInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wall-clock "stuck" timer: if no event arrives and no goal
    /// completes within this, re-deliberate.
    /// </summary>
    public TimeSpan StuckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>System prompt sent on every call. Stable so the LLM caches it.</summary>
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    private long _lastEventConsideredSequence = -1;
    private DateTimeOffset _lastCalledAtUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// In-flight LLM call. ProposeGoal is called from the receive
    /// loop and must NEVER block the loop on a 1s HTTP RTT, so we
    /// kick off a Task on the first triggering tick and poll its
    /// completion on subsequent ticks.
    /// </summary>
    private Task<(LlmResult Result, Guid DecisionId, string UserPrompt, string ProjJson, long EventSeqAtCallStart)>? _inflight;

    /// <summary>True iff an LLM call is currently in flight (no result consumed yet).</summary>
    public bool HasInflight => _inflight is not null && !_inflight.IsCompleted;

    /// <summary>
    /// Test/diagnostic helper. Blocks until any in-flight LLM call
    /// completes (or returns immediately if none). Production code
    /// should NEVER call this from a hot loop — use ProposeGoal's
    /// poll model instead.
    /// </summary>
    public async Task WaitForInFlightAsync()
    {
        var t = _inflight;
        if (t is not null)
        {
            try { await t.ConfigureAwait(false); } catch { /* swallowed; ConsumeResult handles errors */ }
        }
    }

    public LlmGoalPolicy(LlmGoalClient client, IGoalPolicy fallback, IWeenieRepository weenies, ITrainingDataSink? training = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _weenies = weenies ?? throw new ArgumentNullException(nameof(weenies));
        _training = training;
    }

    public string Source => $"llm:{_client.Model}";

    public Goal? ProposeGoal(WorldStateProjection world, EventStream events, Goal? currentGoal)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        // 1) Poll an in-flight call first — if it finished, consume it.
        if (_inflight is not null && _inflight.IsCompleted)
        {
            var finished = _inflight;
            _inflight = null;
            return ConsumeResult(finished, world, events, currentGoal, nowUtc);
        }

        // 2) Still in flight: don't kick off another, don't change goal.
        if (_inflight is not null)
            return currentGoal;

        // 2.5) Stale-goal-on-teleport guard. If the bot crossed a
        // landblock boundary since our last LLM look, the prior goal
        // was derived for a world we are no longer in. Drop it from
        // the prompt anchor so the LLM re-deliberates from the new
        // observations rather than re-emitting (e.g.) the academy
        // Give-to-Society-Greeter goal after a Free Ride teleport
        // to Holtburg. The SelectorResolver landblock filter is the
        // belt; this is the suspenders that stop the LLM from
        // burning tokens re-proposing the same dead goal.
        if (currentGoal is not null && HasLandblockChangeSince(events, _lastEventConsideredSequence))
        {
            Console.WriteLine(
                $"[strategy] LlmGoalPolicy: landblock change detected → " +
                $"dropping stale goal '{currentGoal.Kind} target={currentGoal.Target}' from prompt anchor");
            currentGoal = null;
        }

        // 2.6) Action-rejected guard. If the server told us our last
        // action failed (WeenieErrorWithString surfaced as
        // ActionRejected) since our last LLM look, drop currentGoal
        // so the LLM is not anchored on the failed goal in the
        // prompt's '## Current goal' section. Parallels the
        // landblock-change guard above. Stops the loop observed in
        // stalefix-run-01 where the Society Greeter kept rejecting
        // the Calling Stone with TradeAiDoesntWant and the LLM kept
        // re-emitting Give(Society Greeter, Calling Stone) forever.
        if (currentGoal is not null && HasRejectionSince(events, _lastEventConsideredSequence))
        {
            Console.WriteLine(
                $"[strategy] LlmGoalPolicy: ActionRejected since last look → " +
                $"dropping rejected goal '{currentGoal.Kind} target={currentGoal.Target}' from prompt anchor");
            currentGoal = null;
        }

        // 3) Decide whether to kick off a new call.
        var hasNewSalient = HasNewSalientEvent(events);
        var stuck = nowUtc - _lastCalledAtUtc > StuckTimeout;
        var coalesce = nowUtc - _lastCalledAtUtc < MinCallInterval;

        if (currentGoal is not null && !hasNewSalient && !stuck) return currentGoal;
        if (coalesce && currentGoal is not null)                 return currentGoal;

        _lastCalledAtUtc = nowUtc;
        var eventSeqAtCallStart = events.NextSequence;
        _lastEventConsideredSequence = eventSeqAtCallStart;

        var userPrompt = BuildUserPrompt(world, events, currentGoal);
        var projJson = JsonSerializer.Serialize(world);
        var decisionId = Guid.NewGuid();

        _inflight = RunAsync(userPrompt, decisionId, projJson, eventSeqAtCallStart);
        return currentGoal; // keep doing whatever we were doing while the LLM thinks
    }

    private async Task<(LlmResult, Guid, string, string, long)> RunAsync(string userPrompt, Guid decisionId, string projJson, long eventSeqAtCallStart)
    {
        LlmResult result;
        try
        {
            result = await _client.CompleteAsync(SystemPrompt, userPrompt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new LlmResult(false, "", "", 0, $"unhandled: {ex.Message}");
        }
        return (result, decisionId, userPrompt, projJson, eventSeqAtCallStart);
    }

    private Goal? ConsumeResult(
        Task<(LlmResult Result, Guid DecisionId, string UserPrompt, string ProjJson, long EventSeqAtCallStart)> finishedTask,
        WorldStateProjection world,
        EventStream events,
        Goal? currentGoal,
        DateTimeOffset nowUtc)
    {
        var (result, decisionId, userPrompt, projJson, eventSeqAtCallStart) = finishedTask.GetAwaiter().GetResult();

        // M1.6 — stale-result detection. If a salient event arrived
        // after we kicked off the LLM call, the world has moved past
        // the prompt we sent. Acting on the result would lock the bot
        // into stale plans (e.g. "give exit token to Jonathan" after
        // a teleport already left the academy). Discard the result
        // and force a fresh deliberation on the next tick. The
        // training record still captures what the LLM produced so we
        // can analyze stale-rate offline.
        var staleSinceCall = HasSalientSinceSequence(events, eventSeqAtCallStart);

        _training?.RecordDecision(new TrainingDecision
        {
            Id = decisionId,
            CreatedAtUtc = nowUtc,
            Trigger = currentGoal is null ? "no-current-goal" : "new-event-or-stuck",
            Model = _client.Model,
            Endpoint = _client.Endpoint,
            SystemPrompt = SystemPrompt,
            UserPrompt = userPrompt,
            WorldProjectionJson = projJson,
            LlmOk = result.Ok,
            LlmLatencyMs = result.LatencyMs,
            LlmRawResponse = result.RawResponse,
            LlmError = result.Error,
        });

        if (staleSinceCall)
        {
            // Reset _lastCalledAtUtc to bypass MinCallInterval on the
            // next ProposeGoal — we want to re-call ASAP with fresh
            // observations.
            _lastCalledAtUtc = DateTimeOffset.MinValue;
            _lastEventConsideredSequence = -1;
            return currentGoal;
        }

        if (!result.Ok)
            return _fallback.ProposeGoal(world, events, currentGoal);

        if (!TryParseGoal(result.Content, out var parsed, out var parseError))
        {
            _training?.RecordParseError(decisionId, parseError ?? "unknown");
            return _fallback.ProposeGoal(world, events, currentGoal);
        }

        var goal = parsed! with
        {
            Source = Source,
            CreatedAtUtc = nowUtc,
            Id = parsed.Id == Guid.Empty ? Guid.NewGuid() : parsed.Id,
        };

        _training?.RecordEmittedGoal(decisionId, goal);
        return goal;
    }

    private static bool HasSalientSinceSequence(EventStream events, long sequenceFloor)
    {
        return events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => e.Kind is EventKind.PopupString
                              or EventKind.InventoryItemAdded
                              or EventKind.InventoryItemRemoved
                              or EventKind.LandblockChanged
                              or EventKind.NpcDialog
                              or EventKind.ServerMessage
                              or EventKind.ActionRejected);
    }

    internal static bool HasLandblockChangeSince(EventStream events, long sequenceFloor)
    {
        // Recent() returns newest-first. Filter the suffix that's newer
        // than our last look for any LandblockChanged event. sequenceFloor
        // of -1 (the initial state) accepts any event.
        return events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => e.Kind == EventKind.LandblockChanged);
    }

    internal static bool HasRejectionSince(EventStream events, long sequenceFloor)
    {
        // Same shape as HasLandblockChangeSince — look for an
        // ActionRejected (server told us the last action failed)
        // newer than our last LLM look.
        return events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => e.Kind == EventKind.ActionRejected);
    }

    private bool HasNewSalientEvent(EventStream events)
    {
        // Anything since our last look that's of a salient kind.
        return events.Recent()
            .TakeWhile(e => e.Sequence >= _lastEventConsideredSequence)
            .Any(e => e.Kind is EventKind.PopupString
                              or EventKind.InventoryItemAdded
                              or EventKind.LandblockChanged
                              or EventKind.GoalCompleted
                              or EventKind.GoalFailed
                              or EventKind.GoalExpired
                              or EventKind.NpcDialog
                              or EventKind.ServerMessage
                              or EventKind.ActionRejected);
    }

    private static string BuildUserPrompt(WorldStateProjection world, EventStream events, Goal? currentGoal)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("# Asheron's Call bot — derive the next goal.");
        sb.AppendLine();
        sb.AppendLine("Output JSON exactly matching this schema (no extra fields):");
        sb.AppendLine("""
{
  "goal_id": "<new uuid>",
  "kind": "Give" | "Use" | "Attack" | "Pickup" | "Wield" | "GoTo" | "Talk" | "Wait" | "Explore",
  "target": { "name"?: string, "name_contains"?: string, "wcid"?: number, "item_type_mask"?: number, "short_desc_contains"?: string, "guid"?: number },
  "item":   { ...same as target... } | null,
  "rationale": string,
  "priority": 1..10,
  "expires_in_seconds": number | null
}
""");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Reason ONLY from the observed world below. Do NOT invent NPCs, items, or wcids that aren't listed.");
        sb.AppendLine("- Prefer NAME selectors over wcid (wcids change between sessions).");
        sb.AppendLine("- If an inventory item's short_desc tells you what to do with it, follow it.");
        sb.AppendLine("- 'Give' requires both target (the NPC) and item (the thing being given).");
        sb.AppendLine("- If a recent event is `ActionRejected`, the server refused that exact attempt. Do NOT immediately retry the same (kind, target, item) combination. Pick a different verb (e.g. Use instead of Give), a different item, or a different NPC. Read the rejection's `label` and `message` for the reason.");
        sb.AppendLine("- Priority: 9-10 health-critical; 7-8 quest progress; 5-6 fight/loot; 3-4 explore.");
        sb.AppendLine();

        sb.AppendLine("## Self");
        sb.AppendLine($"- name: {world.Self.Name}");
        sb.AppendLine($"- landblock: 0x{world.Self.Landblock ?? 0:X4}");
        sb.AppendLine($"- pos: ({world.Self.PositionX:F1}, {world.Self.PositionY:F1}, {world.Self.PositionZ:F1})");
        if (world.Self.Level is int lv) sb.AppendLine($"- level: {lv}");
        if (world.Self.HealthFraction is float hf) sb.AppendLine($"- health: {hf:P0}");
        sb.AppendLine();

        sb.AppendLine("## Inventory");
        if (world.Inventory.Count == 0) sb.AppendLine("- (empty)");
        else foreach (var i in world.Inventory)
        {
            sb.Append($"- {i.Name} (wcid={i.Wcid}");
            if (i.WieldedAt is uint w && w != 0) sb.Append($", wielded@0x{w:X}");
            sb.AppendLine(")");
            if (!string.IsNullOrWhiteSpace(i.ShortDesc))
                sb.AppendLine($"    short_desc: {i.ShortDesc}");
        }
        sb.AppendLine();

        sb.AppendLine("## Visible nearby");
        if (world.Visible.Count == 0) sb.AppendLine("- (nothing)");
        else foreach (var v in world.Visible)
        {
            sb.Append($"- {v.Name}");
            if (v.Wcid is uint vw) sb.Append($" (wcid={vw}");
            else sb.Append(" (");
            if (v.IsCreature) sb.Append(" creature");
            if (v.IsPortal)   sb.Append(" portal");
            if (v.IsDoor)     sb.Append(" door");
            if (v.IsCorpse)   sb.Append(" corpse");
            if (v.IsLifestone) sb.Append(" lifestone");
            if (v.IsVendor)   sb.Append(" vendor");
            if (v.IsHealer)   sb.Append(" healer");
            if (v.IsOpenable) sb.Append(" openable");
            if (v.ObservedHostile) sb.Append(" HOSTILE");
            if (v.Distance is float d) sb.Append($" d={d:F1}");
            sb.AppendLine(")");
        }
        sb.AppendLine();

        sb.AppendLine("## Recent events (newest first)");
        var recent = events.Recent(15);
        if (recent.Count == 0) sb.AppendLine("- (none)");
        else foreach (var e in recent) sb.AppendLine($"- {e}");
        sb.AppendLine();

        // Pull out ActionRejected events into a dedicated section so
        // the LLM cannot miss them in the 15-event tail. These are
        // strong "don't retry that" signals from the server.
        var rejections = events.Recent(50)
            .Where(e => e.Kind == EventKind.ActionRejected)
            .Take(5)
            .ToList();
        if (rejections.Count > 0)
        {
            sb.AppendLine("## Recent rejections (server refused these — do NOT retry the same combo)");
            foreach (var r in rejections) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (currentGoal is not null)
        {
            sb.AppendLine("## Current goal");
            sb.AppendLine($"- {currentGoal}");
            sb.AppendLine();
            sb.AppendLine("Keep it if it still looks right; replace if observation says otherwise.");
        }

        return sb.ToString();
    }

    internal static bool TryParseGoal(string json, out Goal? goal, out string? error)
    {
        goal = null; error = null;
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var parsed = JsonSerializer.Deserialize<Goal>(json, opts);
            if (parsed is null) { error = "deserialized to null"; return false; }
            if (parsed.Target is null || parsed.Target.IsEmpty)
            {
                error = "target selector missing or empty";
                return false;
            }
            if (parsed.Kind == GoalKind.Give && (parsed.Item is null || parsed.Item.IsEmpty))
            {
                error = "Give goal requires non-empty item selector";
                return false;
            }
            goal = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public const string DefaultSystemPrompt = """
You are the strategy layer for an Asheron's Call bot. Your job is to
decide the bot's NEXT GOAL based on what it currently perceives.

You are NOT a controller. You output one Goal as a JSON object. The
bot's tactics layer executes the goal step by step. You will be
called again when a new event arrives or the goal completes.

Architectural constraints you MUST respect:
- Use ONLY information from the prompt (inventory, visible objects,
  recent events). Do not assume world knowledge from outside.
- Refer to NPCs and items by NAME, not by wcid (wcids are session-
  scoped runtime ids; the name is the stable identifier).
- If an inventory item's short_desc tells you what to do with it
  (e.g., "Give this to X"), that is the canonical clue. Follow it.
- If you are uncertain, output a low-priority 'Talk' or 'Explore'
  goal so the bot keeps moving and surfaces more observations.

Output JSON only. No prose outside the JSON object.
""";
}

/// <summary>
/// Optional sink for training-data recording. Stub for Slice D
/// (TrainingDataRecorder). LlmGoalPolicy passes a null sink in
/// Slice B and the calls are no-ops.
/// </summary>
internal interface ITrainingDataSink
{
    void RecordDecision(TrainingDecision decision);
    void RecordParseError(Guid decisionId, string error);
    void RecordEmittedGoal(Guid decisionId, Goal goal);
    void RecordOutcome(Guid goalId, string outcome, string? evidence = null);
}

internal sealed record TrainingDecision
{
    public required Guid Id { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string Trigger { get; init; }
    public required string Model { get; init; }
    public required string Endpoint { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public required string WorldProjectionJson { get; init; }
    public required bool LlmOk { get; init; }
    public required int LlmLatencyMs { get; init; }
    public required string LlmRawResponse { get; init; }
    public required string? LlmError { get; init; }
}
