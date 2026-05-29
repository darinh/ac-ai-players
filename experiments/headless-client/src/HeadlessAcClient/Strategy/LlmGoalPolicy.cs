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

using HeadlessAcClient.Strategy.Intent;

namespace HeadlessAcClient.Strategy;

internal sealed class LlmGoalPolicy : IGoalPolicy
{
    private readonly LlmGoalClient _client;
    private readonly IGoalPolicy _fallback;
    private readonly IWeenieRepository _weenies;
    private readonly ITrainingDataSink? _training;
    private readonly IntentStack? _stack;
    private readonly IntentIdAllocator? _idAllocator;

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

    public LlmGoalPolicy(
        LlmGoalClient client,
        IGoalPolicy fallback,
        IWeenieRepository weenies,
        ITrainingDataSink? training = null,
        IntentStack? stack = null,
        IntentIdAllocator? idAllocator = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _weenies = weenies ?? throw new ArgumentNullException(nameof(weenies));
        _training = training;
        _stack = stack;
        // If a stack is supplied an allocator must be too (we need to
        // assign ids when the LLM omits them). Easier to default than
        // to require the caller to thread one through.
        _idAllocator = idAllocator ?? (stack is null ? null : new IntentIdAllocator());
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

        var userPrompt = BuildUserPrompt(world, events, currentGoal, _stack);
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

        // Slice R wiring — strategic stack mutations are applied BEFORE
        // the goal is consumed so the rendered "## Intent stack" the
        // next prompt sees reflects everything the LLM just emitted.
        // Rejected batches are logged into training data. A goal-only
        // response is fine (empty stack_ops or no stack_ops field).
        if (_stack is not null && _idAllocator is not null)
        {
            if (TryParseStackOps(result.Content, out var stackRevision, out var stackOps, out var opsErr))
            {
                if (stackOps is not null && stackOps.Count > 0)
                {
                    var outcome = IntentStackOpsApplier.TryApply(
                        _stack, _idAllocator, stackOps, stackRevision, world, events, nowUtc.UtcDateTime);
                    Console.WriteLine(
                        $"[intent-stack] result={outcome.Result} ops={stackOps.Count} " +
                        $"applied={outcome.AppliedLog.Count} " +
                        $"revision_after={_stack.Revision} depth_after={_stack.Depth}");
                    if (outcome.Result != BatchApplyResult.Ok)
                    {
                        _training?.RecordParseError(decisionId,
                            $"stack-ops rejected: {outcome.Result} reason={outcome.RejectReason}");
                    }
                }
            }
            else
            {
                _training?.RecordParseError(decisionId,
                    $"stack-ops parse failed: {opsErr ?? "unknown"}");
            }
        }

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

        // Slice N — programmatic rejection enforcement.
        //
        // The "do NOT retry the same (kind, target, item) combo"
        // prompt rule (Slice F) is observably violated by the LLM
        // — see decisions 51,52,55,58 in spike8 where Give(Worcer,
        // List of Items) was emitted 4 times with a TradeAiDoesntWant
        // rejection between every attempt. Prompt-only enforcement
        // is insufficient; the policy must enforce the rule itself.
        //
        // If the LLM-returned goal matches a recent ActionRejected
        // (by item guid/wcid/name OR by target name), drop the goal
        // and fall through to the fallback policy. The fallback has
        // its own dedup (NoQuestKnowledgePolicy.recentlyRejectedGuids
        // + _recentProposedGuids) and will pick a fresh schema-only
        // action (Pickup, Wield, Explore) instead of re-trying the
        // same blocked target.
        if (IsGoalRecentlyRejected(goal, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM goal kind={goal.Kind} target={goal.Target}" +
                (goal.Item is null ? "" : $" item={goal.Item}") +
                " — matches a recent ActionRejected; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: LLM goal matched a recent ActionRejected");
            return _fallback.ProposeGoal(world, events, currentGoal);
        }

        _training?.RecordEmittedGoal(decisionId, goal);
        return goal;
    }

    /// <summary>
    /// True iff the goal targets an item or NPC that the server (or
    /// our local walk-timeout) has rejected within the recent event
    /// window. Matches by:
    ///   - Item wcid (precise, when the rejection carries it from
    ///     InventoryServerSaveFailed).
    ///   - Item name (case-insensitive exact match).
    ///   - Target name appearing in the rejection's Name field
    ///     (Unreachable carries motionTarget.Name) or in the Text
    ///     field (WeenieErrorWithString puts the NPC name there for
    ///     Give rejections, "Unreachable: 'NPC' (walk timeout ...)"
    ///     puts it for walk-timeout rejections).
    /// Short target names (&lt; 4 chars) skip substring matching to
    /// avoid false positives on tokens like "the" embedded in a
    /// longer rejection message.
    /// </summary>
    internal static bool IsGoalRecentlyRejected(Goal goal, EventStream events)
    {
        // Slice O — widened from 15 to 30 events. In spike9 the LLM
        // attempted Give(Society Greeter, Calling Stone) 3 times across
        // ~7000 log lines while accumulating Unreachable + walk-tick
        // events between attempts; the original 15-event window only
        // caught the first repeat. 30 events ~= 10 LLM decisions of
        // context which is enough to span an LLM
        // observe/walk/timeout/retry cycle.
        const int LookbackEvents = 30;
        var targetName = goal.Target?.Name;
        var itemName   = goal.Item?.Name;
        var itemWcid   = goal.Item?.Wcid;

        if (string.IsNullOrWhiteSpace(targetName) &&
            string.IsNullOrWhiteSpace(itemName) &&
            itemWcid is null)
        {
            return false;
        }

        foreach (var ev in events.Recent(LookbackEvents))
        {
            if (ev.Kind != EventKind.ActionRejected) continue;

            // Item-specific rejection (carries item wcid/name —
            // typically Slice J's InventoryServerSaveFailed).
            if (itemWcid is uint w && ev.Wcid == w) return true;
            if (!string.IsNullOrWhiteSpace(itemName) &&
                !string.IsNullOrWhiteSpace(ev.Name) &&
                string.Equals(ev.Name, itemName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Target-name match: NPC name carried in Name (Unreachable)
            // or Text (WeenieErrorWithString puts the NPC name there).
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                if (!string.IsNullOrWhiteSpace(ev.Name) &&
                    string.Equals(ev.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(ev.Text))
                {
                    if (string.Equals(ev.Text, targetName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    // Substring match (for "Unreachable: 'X' (walk timeout ...)")
                    // gated on a minimum target-name length to avoid
                    // false positives on short common substrings.
                    if (targetName.Length >= 4 &&
                        ev.Text.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
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
                              or EventKind.ActionRejected
                              or EventKind.BookText);
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
                              or EventKind.ActionRejected
                              or EventKind.BookText);
    }

    internal static string BuildUserPrompt(WorldStateProjection world, EventStream events, Goal? currentGoal)
        => BuildUserPrompt(world, events, currentGoal, stack: null);

    internal static string BuildUserPrompt(WorldStateProjection world, EventStream events, Goal? currentGoal, IntentStack? stack)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("# Asheron's Call bot — derive the next goal.");
        sb.AppendLine();
        sb.AppendLine("Output JSON exactly matching this schema (no extra fields):");
        if (stack is null)
        {
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
        }
        else
        {
            sb.AppendLine("""
{
  // -- per-cycle tactical goal (REQUIRED — the tactics layer
  //    executes this in the next few ticks) --
  "goal_id": "<new uuid>",
  "kind": "Give" | "Use" | "Attack" | "Pickup" | "Wield" | "GoTo" | "Talk" | "Wait" | "Explore",
  "target": { "name"?: string, "name_contains"?: string, "wcid"?: number, "item_type_mask"?: number, "short_desc_contains"?: string, "guid"?: number },
  "item":   { ...same as target... } | null,
  "rationale": string,
  "priority": 1..10,
  "expires_in_seconds": number | null,

  // -- strategic intent stack (OPTIONAL — include only when you want
  //    to push/pop/replace top/mark-blocked; omit entirely if the
  //    stack should stay as-is). See `## Intent stack` below. --
  "stack_revision": <number — echo the current revision shown in
                     `## Intent stack`; mismatch rejects the batch>,
  "stack_ops": [
    {
      "op": "push",
      "intent": {
        "id":          "<new id, e.g. i-005>",
        "kind":        "<freeform tag, e.g. quest:collect-apples>",
        "target_name": "<NPC or 'self' or null>",
        "target_guid":  <optional number>,
        "rationale":   "<why this intent now>",
        "deadline_seconds": <optional number; null = no deadline>,
        "completion": {
          // Pick exactly ONE typed completion predicate. Common ones:
          //   {"$type":"inventory_contains_at_least","wcid":<n>,"count":<n>}
          //   {"$type":"inventory_contains_at_least","name_contains":"<s>","count":<n>}
          //   {"$type":"landblock_equals","value": 0x<hex>}
          //   {"$type":"kill_count_since_push_at_least","count":<n>}
          //   {"$type":"kill_count_total_at_least","count":<n>}
          //   {"$type":"levels_gained_total_at_least","count":<n>}
          //   {"$type":"num_deaths_at_least","count":<n>}
          //   {"$type":"num_deaths_since_push_at_most","budget":<n>}
          //   {"$type":"coin_value_at_least","count":<n>}
          //   {"$type":"coin_gain_since_push_at_least","count":<n>}
          //   {"$type":"units_traveled_since_push_at_least","units":<n>}
          //   {"$type":"and","predicates":[ ...nested... ]}
          //   {"$type":"or","predicates":[ ...nested... ]}
          //   {"$type":"never"}  -- e.g. for safety-cap-only intents
        },
        // ESCAPE HATCH: if NO existing completion type fits, set
        // completion to {"$type":"never"} and populate this field with
        // a prose description of the predicate we need. We will add
        // the type in the next dev iteration; meanwhile the intent
        // can only be popped by deadline or by an explicit pop_top.
        "predicate_request": "<null or string>"
      },
      "reason": "<short note>"
    }
    // OR: {"op":"pop_top",          "reason":"..."}
    // OR: {"op":"replace_top","intent":{...},"reason":"..."}
    // OR: {"op":"mark_top_blocked", "reason":"..."}
  ]
}
""");
        }
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Reason ONLY from the observed world below. Do NOT invent NPCs, items, or wcids that aren't listed.");
        sb.AppendLine("- Prefer NAME selectors over wcid (wcids change between sessions).");
        sb.AppendLine("- If an inventory item's short_desc tells you what to do with it, follow it.");
        sb.AppendLine("- 'Give' requires both target (the NPC) and item (the thing being given).");
        sb.AppendLine("- If a recent event is `ActionRejected`, the server refused that exact attempt. Do NOT immediately retry the same (kind, target, item) combination. Pick a different verb (e.g. Use instead of Give), a different item, or a different NPC. Read the rejection's `label` and `message` for the reason.");
        sb.AppendLine("- If you see TWO or more rejections of the same target+item combo (any verb), that combo is BLOCKED — the NPC has an unmet prerequisite. Inventory items whose `short_desc` mentions 'double-click', 'read', or 'activate' must be Use'd on yourself first (target = your own name from `## Self`) before related Give/Talk steps unlock. If you have an un-used item like this, prefer `Use{target: name=\"<your-name>\", item: name=\"<note-or-letter-item>\"}` over retrying the blocked combo.");
        sb.AppendLine("- Read `## Server hints` closely. Phrases like \"Double click X\" or \"Use X to ...\" tell you EXACTLY what verb to use on what target. If an object is visible nearby AND the server has instructed you to use it, emit `Use{target: name=\"X\"}`. The server is your tutorial; do not ignore its instructions in favor of pure exploration.");
        sb.AppendLine("- Combat: creatures tagged `monster` are appropriate combat targets and grant XP and loot. Creatures tagged `npc` are civilians — talk to or trade with them, do NOT attack. If a `monster` is visible nearby AND a weapon is wielded (visible in inventory as `wielded@...`) AND no server hint or item short_desc gives a more specific quest action, emit `Attack{target: name=\"X\"}` on the nearest monster. Combat is the primary source of XP outside of NPC quests.");
        sb.AppendLine("- Looting: when a monster dies it becomes a `corpse` object that appears in the visible-nearby list tagged `corpse`. Corpses are time-sensitive containers — they decay. Emit `Use{target: name=\"<corpse name>\"}` to open one; after that, items inside the corpse appear as new visible objects and you should `Pickup{target: name=\"<item>\"}` them. Loot (pyreals, components, gear) is your main income outside of quest rewards. NEVER skip a fresh corpse to chase the next NPC.");
        sb.AppendLine("- LOOP-BREAK (Talk loop): If you have emitted `Talk{X}` 3 or more times in the last 10 goal emissions (see the Location & recency section below) AND no new inventory item has been added since AND no new unique server hint has appeared, STOP talking to X. Talk to a different visible NPC, or Use/Give an inventory item to a different target, or emit `Explore`.");
        sb.AppendLine("- LOOP-BREAK (town-stuck): If `minutes in current landblock` is greater than 5 AND `Combat readiness` shows `nearest monster: (none in view)` AND every visible creature is tagged `npc` (no `monster` tag anywhere in Visible nearby), you are STUCK INDOORS in a town. Stop cycling through NPCs — they have no more quests for you here. Emit `Explore{target: {name: \"anywhere\"}}` immediately. The schema picker will walk you through visible Doors and portals to discover new NPCs, monsters, and items. Combat XP, loot, and contracts happen OUTDOORS, not in town interiors. This rule OVERRIDES `Talk` and `Give` even when a new NPC is visible — talk-to-every-NPC is not progress when there are no monsters in view.");
        if (stack is not null)
        {
            sb.AppendLine("- STRATEGIC STACK: `## Intent stack` shows the bot's current strategic plan. The TOP intent is the active sub-goal; ancestors are paused waiting for it. Per-cycle goals should advance the TOP. PUSH a new intent when you discover a sub-task. POP_TOP when the sub-task is done and no completion predicate caught it (rare — predicates auto-pop). REPLACE_TOP when the intent was right-track-wrong-target. MARK_TOP_BLOCKED when you cannot make progress and want to record why (the next call you can pop or replace). Always echo `stack_revision` to detect races.");
            sb.AppendLine("- COMPLETION PREDICATES: pick the typed predicate that matches your termination criterion. Prefer server-authoritative ones (num_deaths, coin_value) when applicable — they survive crashes and are exact. Use *_total_* for absolute thresholds (\"reach level 5\"), *_since_push_* for deltas (\"kill 3 more\"). If none fits, use {\"$type\":\"never\"} + `predicate_request` (escape hatch).");
        }
        sb.AppendLine("- Priority: 9-10 health-critical; 7-8 quest progress; 5-6 fight/loot; 3-4 explore.");
        sb.AppendLine();

        sb.AppendLine("## Self");
        sb.AppendLine($"- name: {world.Self.Name}");
        sb.AppendLine($"- landblock: 0x{world.Self.Landblock ?? 0:X4}");
        sb.AppendLine($"- pos: ({world.Self.PositionX:F1}, {world.Self.PositionY:F1}, {world.Self.PositionZ:F1})");
        if (world.Self.Level is int lv) sb.AppendLine($"- level: {lv}");
        if (world.Self.HealthFraction is float hf) sb.AppendLine($"- health: {hf:P0}");
        if (world.Self.NumDeaths is int nd) sb.AppendLine($"- deaths (server-tracked): {nd}");
        if (world.Self.CoinValue is int cv) sb.AppendLine($"- coin (server-tracked): {cv} pyreals");
        sb.AppendLine();

        if (stack is not null)
        {
            sb.AppendLine(IntentStackOpsApplier.RenderStackForPrompt(stack));
            sb.AppendLine();
        }

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
            if (v.IsCreature)
            {
                // Slice H — discriminate combat targets from civilians.
                // `monster` = server-flagged Attackable AND no custom
                // radar blip color AND not Vendor/Healer. `npc` = any
                // other creature (civilians, shopkeepers, healers).
                // Both signals come from the wire; we never hardcode
                // wcid lists or English-name matches.
                if (v.IsMonster) sb.Append(" monster");
                else             sb.Append(" npc");
            }
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

        // Slice H — Combat readiness summary. Surfaces the three
        // pieces of state the LLM needs to decide whether to engage:
        // weapon, monster proximity, hostile incoming. Health is
        // intentionally omitted until WorldStateProjection actually
        // populates HealthFraction reliably (currently null in most
        // ticks — rubber-duck flagged this).
        var weaponWielded = world.Inventory.Any(i => i.WieldedAt is uint w && w != 0);
        var nearestMonster = world.Visible
            .Where(v => v.IsMonster)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        var observedHostile = world.Visible.FirstOrDefault(v => v.ObservedHostile);
        sb.AppendLine("## Combat readiness");
        sb.AppendLine($"- weapon: {(weaponWielded ? "wielded" : "NOT wielded")}");
        if (nearestMonster is not null)
        {
            var dStr = nearestMonster.Distance is float dm ? $" d={dm:F1}" : "";
            sb.AppendLine($"- nearest monster: {nearestMonster.Name}{dStr}");
        }
        else
        {
            sb.AppendLine("- nearest monster: (none in view)");
        }
        if (observedHostile is not null)
        {
            sb.AppendLine($"- observed hostile: {observedHostile.Name} (it has attacked you — fight back or flee)");
        }
        sb.AppendLine();

        // Slice I — Location & recency. Surfaces the two signals
        // the LLM needs to break out of town-NPC loops: how long
        // it has been in the current landblock, and how many
        // Talk{X} goals it has emitted recently per NPC. Both
        // come from the EventStream — no hardcoded knowledge of
        // landblocks or NPC names. The LOOP-BREAK rule above
        // references the recent-Talk counts directly.
        sb.AppendLine("## Location & recency");
        var hintPoolForRecency = events.Recent(EventStream.DefaultCapacity);
        var lastLandblockChange = hintPoolForRecency
            .FirstOrDefault(e => e.Kind == EventKind.LandblockChanged);
        if (lastLandblockChange is not null)
        {
            var dwellMin = (DateTimeOffset.UtcNow - lastLandblockChange.Utc).TotalMinutes;
            sb.AppendLine($"- minutes in current landblock: {dwellMin:F1}");
        }
        else
        {
            sb.AppendLine("- minutes in current landblock: (no LandblockChanged event in retained window)");
        }
        // Per-NPC recent Talk emissions (last 10 GoalEmitted events
        // of kind Talk). Tactics formats GoalEmitted Text as
        // `<Kind> target=<Selector> item=<Selector> source=<src>`
        // so we look for lines starting with "Talk target=name=\"X\"".
        var recentGoalEmits = hintPoolForRecency
            .Where(e => e.Kind == EventKind.GoalEmitted && !string.IsNullOrEmpty(e.Text))
            .Take(10)
            .ToList();
        var talkCountByNpc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Talk ", StringComparison.Ordinal)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(txt, "target=name=\"([^\"]+)\"");
            if (m.Success)
            {
                var n = m.Groups[1].Value;
                talkCountByNpc[n] = talkCountByNpc.TryGetValue(n, out var c) ? c + 1 : 1;
            }
        }
        if (talkCountByNpc.Count > 0)
        {
            sb.AppendLine("- recent Talk emissions (last 10 goals):");
            foreach (var kv in talkCountByNpc.OrderByDescending(p => p.Value))
            {
                sb.AppendLine($"    - {kv.Key}: x{kv.Value}");
            }
        }
        else
        {
            sb.AppendLine("- recent Talk emissions: (none)");
        }
        sb.AppendLine();

        // Pull ServerMessage + NpcDialog from the full retained
        // window (capacity 256). These are high-signal cues
        // ("Double click the lifestone", "I need a token") that get
        // pushed out of the generic Recent events tail fast. Without
        // this section ambient NPC chatter can fully evict a one-
        // time tutorial hint within ~25 events.
        //
        // Budgets are split per-kind so server tutorials survive
        // bursts of NPC small-talk in a busy town:
        //   - up to 8 ServerMessages (rare, high-value)
        //   - up to 6 NpcDialog lines (denser, more chatty)
        // Dedup exact repeats so the same banner doesn't waste tokens.
        var hintPool = events.Recent(EventStream.DefaultCapacity);
        var serverHints = hintPool
            .Where(e => e.Kind == EventKind.ServerMessage)
            .GroupBy(e => (e.ChatType, e.Text))
            .Select(g => g.First())  // newest-first ordering preserved
            .OrderByDescending(e => e.Sequence)
            .Take(8)
            .ToList();
        var npcHints = hintPool
            .Where(e => e.Kind == EventKind.NpcDialog)
            .GroupBy(e => (e.Name, e.Text))
            .Select(g => g.First())
            .OrderByDescending(e => e.Sequence)
            .Take(6)
            .ToList();
        if (serverHints.Count > 0 || npcHints.Count > 0)
        {
            sb.AppendLine("## Server hints (recent — text the server sent you, dedupe'd)");
            foreach (var h in serverHints)
                sb.AppendLine($"- ServerMessage[chat=0x{h.ChatType ?? 0:X}]: \"{Truncate(h.Text, 320)}\"");
            foreach (var h in npcHints)
                sb.AppendLine($"- NpcDialog from=\"{h.Name}\": \"{Truncate(h.Text, 320)}\"");
            sb.AppendLine();
        }

        // Slice M — quest book / scroll / parchment contents.
        // Surfaced as its own section so the LLM can read directions,
        // coordinates, and item requirement lists. Deduped by book
        // guid (you can re-open the same book many times); keep the
        // last 3 distinct books so a busy quest hub doesn't blow the
        // token budget. Newest-first ordering.
        var bookTexts = hintPool
            .Where(e => e.Kind == EventKind.BookText && !string.IsNullOrEmpty(e.Text))
            .GroupBy(e => e.ItemGuid ?? 0u)
            .Select(g => g.OrderByDescending(e => e.Sequence).First())
            .OrderByDescending(e => e.Sequence)
            .Take(3)
            .ToList();
        if (bookTexts.Count > 0)
        {
            sb.AppendLine("## Quest book texts (newest first — read these for quest directions, item lists, coordinates)");
            foreach (var b in bookTexts)
            {
                sb.AppendLine($"- BookText name=\"{b.Name}\" guid=0x{b.ItemGuid ?? 0:X8}:");
                // 800 chars is generous: enough for the typical
                // 1-page quest book that contains an item list +
                // coordinate hint. Pages beyond this are usually
                // flavor text.
                sb.AppendLine($"    \"{Truncate(b.Text, 800)}\"");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Recent events (newest first)");
        var recent = events.Recent(25);
        if (recent.Count == 0) sb.AppendLine("- (none)");
        else foreach (var e in recent) sb.AppendLine($"- {e}");
        sb.AppendLine();

        // Pull out ActionRejected events into a dedicated section so
        // the LLM cannot miss them in the 15-event tail. These are
        // strong "don't retry that" signals from the server.
        //
        // Slice O — diversify by (label, target). In spike9 the bot
        // accumulated 95 Unreachable rejections while a critical
        // TradeAiDoesntWant rejection (Greeter refused Calling Stone)
        // never made it into the 5-most-recent window the LLM was
        // shown — the bot kept retrying Give(Greeter, CallingStone)
        // for 30+ minutes. Bucket the recent rejections by their
        // (ErrorLabel, Text/Name) tuple and keep only the most-recent
        // of each bucket so every distinct rejection class surfaces.
        var rejections = events.Recent(100)
            .Where(e => e.Kind == EventKind.ActionRejected)
            .GroupBy(e =>
            {
                var label = e.ErrorLabel ?? "?";
                var key = e.Name ?? e.Text ?? string.Empty;
                return label + "|" + key;
            })
            .Select(g => g.First())
            .Take(8)
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

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "...");

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

    /// <summary>
    /// Extract optional `stack_revision` (long) and `stack_ops` (array)
    /// from the LLM JSON response. Returns true on syntactic success
    /// (including the case where neither field is present — both out
    /// params are null). Returns false only when JSON itself can't be
    /// parsed or the stack_ops array can't be deserialized to the
    /// strongly-typed shape.
    /// </summary>
    internal static bool TryParseStackOps(
        string json,
        out long? stackRevision,
        out IReadOnlyList<IntentStackOp>? stackOps,
        out string? error)
    {
        stackRevision = null;
        stackOps = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true; // no stack info — fine

            if (root.TryGetProperty("stack_revision", out var rev) &&
                rev.ValueKind == JsonValueKind.Number)
            {
                stackRevision = rev.GetInt64();
            }

            if (root.TryGetProperty("stack_ops", out var opsEl) &&
                opsEl.ValueKind == JsonValueKind.Array)
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                stackOps = JsonSerializer.Deserialize<List<IntentStackOp>>(opsEl.GetRawText(), opts);
            }
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
decide the bot's NEXT GOAL based on what it currently perceives, and
to manage a small FILO STACK of strategic INTENTS that persist across
your deliberations.

You are NOT a controller. You output one tactical Goal (executed by
the tactics layer in the next few ticks) plus, optionally, a batch of
mutations to the strategic intent stack. You will be called again
when a new event arrives, the goal completes, or an intent's
completion predicate pops the top.

Architectural constraints you MUST respect:
- Use ONLY information from the prompt (inventory, visible objects,
  server hints, recent events, intent stack). Do not assume world
  knowledge from outside.
- Refer to NPCs and items by NAME, not by wcid (wcids are session-
  scoped runtime ids; the name is the stable identifier).
- If an inventory item's short_desc tells you what to do with it
  (e.g., "Give this to X"), that is the canonical clue. Follow it.
- If you are uncertain, output a low-priority 'Talk' or 'Explore'
  goal so the bot keeps moving and surfaces more observations.

Intent stack — when `## Intent stack` is present in the prompt:
- The TOP intent is the active strategic sub-goal. Ancestors are
  PAUSED waiting for top. Your per-cycle Goal should advance TOP.
- The stack persists across deliberations — don't redundantly re-push
  the same intent you can see on the stack.
- PUSH a new intent when you discover a sub-task (e.g. you accepted
  a quest that requires collecting items: push a "collect" intent on
  top of the existing "do quest" root). Always include a typed
  COMPLETION predicate so the stack auto-pops when satisfied.
- POP_TOP only when the predicate didn't fire but the intent is
  truly done (rare).
- REPLACE_TOP when the same strategic frame applies but the specific
  target / parameters were wrong.
- MARK_TOP_BLOCKED when you cannot advance and want to record why,
  so a later deliberation can pop or replace it.
- Always echo `stack_revision` from the prompt so we detect races.
- COMPLETION PREDICATES: prefer server-authoritative types
  (num_deaths, coin_value) when applicable. Use *_total_* for
  absolute thresholds, *_since_push_* for deltas. If none fits,
  use `{"$type":"never"}` + populate `predicate_request` with the
  predicate type we should add.

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
