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
        // Decide whether to deliberate. Cheap path:
        //   - no current goal -> always deliberate
        //   - new salient event since we last looked -> deliberate
        //   - stuck-timer expired -> deliberate
        //   - else keep current goal
        var nowUtc = DateTimeOffset.UtcNow;
        var hasNewSalient = HasNewSalientEvent(events);
        var stuck = nowUtc - _lastCalledAtUtc > StuckTimeout;

        if (currentGoal is not null && !hasNewSalient && !stuck)
            return currentGoal;

        // Coalesce within MinCallInterval window.
        if (nowUtc - _lastCalledAtUtc < MinCallInterval && currentGoal is not null)
            return currentGoal;

        _lastCalledAtUtc = nowUtc;
        _lastEventConsideredSequence = events.NextSequence;

        // Build prompt synchronously from the projection (already in hand).
        var userPrompt = BuildUserPrompt(world, events, currentGoal);

        // Call synchronously via blocking wait. Tactics has its own
        // tick budget; this is acceptable while we keep ProposeGoal
        // strictly out of the receive hot loop (HandshakeDriver
        // calls it from a dedicated deliberation cadence).
        LlmResult result;
        try
        {
            result = _client.CompleteAsync(SystemPrompt, userPrompt, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new LlmResult(false, "", "", 0, $"unhandled: {ex.Message}");
        }

        // Always record the attempt to training-data, success or not.
        var decisionId = Guid.NewGuid();
        _training?.RecordDecision(new TrainingDecision
        {
            Id = decisionId,
            CreatedAtUtc = nowUtc,
            Trigger = stuck ? "stuck-timer" : (currentGoal is null ? "no-current-goal" : "new-event"),
            Model = _client.Model,
            Endpoint = _client.Endpoint,
            SystemPrompt = SystemPrompt,
            UserPrompt = userPrompt,
            WorldProjectionJson = JsonSerializer.Serialize(world),
            LlmOk = result.Ok,
            LlmLatencyMs = result.LatencyMs,
            LlmRawResponse = result.RawResponse,
            LlmError = result.Error,
        });

        if (!result.Ok)
            return _fallback.ProposeGoal(world, events, currentGoal);

        if (!TryParseGoal(result.Content, out var parsed, out var parseError))
        {
            _training?.RecordParseError(decisionId, parseError ?? "unknown");
            return _fallback.ProposeGoal(world, events, currentGoal);
        }

        // Tag with source + creation time.
        var goal = parsed! with
        {
            Source = Source,
            CreatedAtUtc = nowUtc,
            Id = parsed.Id == Guid.Empty ? Guid.NewGuid() : parsed.Id,
        };

        _training?.RecordEmittedGoal(decisionId, goal);
        return goal;
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
                              or EventKind.ServerMessage);
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
