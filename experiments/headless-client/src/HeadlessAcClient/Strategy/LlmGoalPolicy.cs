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
        sb.AppendLine("- Read `## Server hints` closely. Phrases like \"Double click X\" or \"Use X to ...\" tell you EXACTLY what verb to use on what target. If an object is visible nearby AND the server has instructed you to use it, emit `Use{target: name=\"X\"}`. The server is your tutorial; do not ignore its instructions in favor of pure exploration.");
        sb.AppendLine("- Combat: creatures tagged `monster` are appropriate combat targets and grant XP and loot. Creatures tagged `npc` are civilians — talk to or trade with them, do NOT attack. If a `monster` is visible nearby AND a weapon is wielded (visible in inventory as `wielded@...`) AND no server hint or item short_desc gives a more specific quest action, emit `Attack{target: name=\"X\"}` on the nearest monster. Combat is the primary source of XP outside of NPC quests.");
        sb.AppendLine("- LOOP-BREAK (Talk loop): If you have emitted `Talk{X}` 3 or more times in the last 10 goal emissions (see the Location & recency section below) AND no new inventory item has been added since AND no new unique server hint has appeared, STOP talking to X. Talk to a different visible NPC, or Use/Give an inventory item to a different target, or emit `Explore`.");
        sb.AppendLine("- LOOP-BREAK (town-stuck): If `minutes in current landblock` is greater than 5 AND `Combat readiness` shows `nearest monster: (none in view)` AND every visible creature is tagged `npc` (no `monster` tag anywhere in Visible nearby), you are STUCK INDOORS in a town. Stop cycling through NPCs — they have no more quests for you here. Emit `Explore{target: {name: \"anywhere\"}}` immediately. The schema picker will walk you through visible Doors and portals to discover new NPCs, monsters, and items. Combat XP, loot, and contracts happen OUTDOORS, not in town interiors. This rule OVERRIDES `Talk` and `Give` even when a new NPC is visible — talk-to-every-NPC is not progress when there are no monsters in view.");
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

        sb.AppendLine("## Recent events (newest first)");
        var recent = events.Recent(25);
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

    public const string DefaultSystemPrompt = """
You are the strategy layer for an Asheron's Call bot. Your job is to
decide the bot's NEXT GOAL based on what it currently perceives.

You are NOT a controller. You output one Goal as a JSON object. The
bot's tactics layer executes the goal step by step. You will be
called again when a new event arrives or the goal completes.

Architectural constraints you MUST respect:
- Use ONLY information from the prompt (inventory, visible objects,
  server hints, recent events). Do not assume world knowledge from
  outside.
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
