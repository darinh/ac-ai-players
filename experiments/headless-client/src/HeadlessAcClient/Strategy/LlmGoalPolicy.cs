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

    /// <summary>
    /// Hard deadline on a single LLM HTTP call. Belt-and-suspenders
    /// for the HttpClient.Timeout inside LlmGoalClient: in the
    /// flexguid01 run-01 spike, kickoff #20 never returned even
    /// though the HttpClient timeout is 30s — the bot's `_inflight`
    /// stayed non-null forever and no further LLM calls fired.
    /// Cancellation here guarantees RunAsync resolves so the policy
    /// can clear `_inflight` and resume deliberation.
    /// </summary>
    public TimeSpan LlmCallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>System prompt sent on every call. Stable so the LLM caches it.</summary>
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    private long _lastEventConsideredSequence = -1;
    private DateTimeOffset _lastCalledAtUtc = DateTimeOffset.MinValue;

    // Durable landblock-dwell tracking. The prompt's `minutes in current
    // landblock` signal (gate for the town-stuck and world-object USE
    // loop-break rules) must NOT depend on a LandblockChanged event still
    // sitting in the retained EventStream window: a bot that entered its
    // landblock via login/enter-world never emits that event, and after a
    // long idle any old one is evicted — so the signal silently degraded
    // to "(no LandblockChanged event in retained window)" and the `> 5`
    // gate became unevaluable, leaving a bot milling in a safe town
    // forever (never exploring out to monsters). We instead stamp the
    // entry time whenever the OBSERVED self-landblock value changes.
    // Purely mechanical bookkeeping — a timestamp keyed on a wire-derived
    // landblock id, no game knowledge.
    private uint? _dwellLandblock;
    private DateTimeOffset _dwellEntryUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProposeTickUtc = DateTimeOffset.MinValue;
    // A gap larger than this between ProposeGoal ticks means the bot was
    // disconnected/reconnected (ProposeGoal is otherwise called every
    // brain tick, even during 429 backoff). On resume we reseed the entry
    // stamp so a reconnect into the SAME landblock does not inherit a
    // stale, falsely-large dwell.
    private static readonly TimeSpan DwellSessionGap = TimeSpan.FromSeconds(60);

    // Sticky LLM-objective (call-volume reduction). _lastLlmGoal holds
    // the most-recent LLM-authored goal so the policy can RE-DRIVE it
    // when the tactical goal clears with no external world change,
    // instead of burning an LLM round-trip on every goal completion.
    // _stickyReEmitCount bounds consecutive re-drives of the SAME
    // objective (reset to 0 when a fresh LLM goal is consumed). See the
    // sticky gate in ProposeGoal (which consults hasNonPickerExternal /
    // pickerArrived / pickerStartWake).
    private Goal? _lastLlmGoal;
    private int _stickyReEmitCount;

    /// <summary>
    /// Max consecutive sticky re-emits of the last LLM objective before
    /// forcing a fresh LLM call. Counts re-CLEARS of the goal (not
    /// ticks), so it bounds spin on an unreachable target while still
    /// letting a reachable one be driven to completion. Reset to 0
    /// whenever a new LLM goal is consumed.
    /// </summary>
    public int MaxStickyReEmits { get; init; } = 3;

    /// <summary>
    /// Call-volume reduction: separate, longer coalesce window for the
    /// picker-START wakeup path. The autonomous fallback picker keeps
    /// switching targets in an object-rich area, and each
    /// PickerActivityStarted previously punched through MinCallInterval
    /// and woke the LLM. That bursts calls (and burns daily quota) when
    /// there is a lot to look at. A picker-start for the SAME target
    /// inside this window is suppressed; a NEW target still wakes
    /// immediately. PickerArrivedNoAction is NOT subject to this — it is
    /// the safety valve that lets the LLM name a verb before the parked
    /// bot moves on, so it must still punch through (see the gate in
    /// ProposeGoal). Pure timing/identity bookkeeping — no game knowledge.
    /// </summary>
    public TimeSpan PickerStartCoalesce { get; init; } = TimeSpan.FromSeconds(8);

    // Call-volume reduction: separate state for the picker-START coalesce
    // + same-target dedupe. Keyed on the picker event's own target
    // (guid hex when non-zero, else name token) so it is robust to the
    // SetCurrentPickerActivity timing and works without the driver. This
    // is DELIBERATELY independent of _lastEventConsideredSequence: a
    // suppressed picker-start must NOT advance the sticky/external-event
    // floor, or it would hide real external salient events from the
    // sticky-objective gate. _lastPickerStartWakeKey holds the target of
    // the most-recent picker-start that DID wake the LLM; the timestamp
    // bounds same-target suppression to PickerStartCoalesce.
    private string? _lastPickerStartWakeKey;
    private DateTimeOffset _lastPickerStartWakeAtUtc = DateTimeOffset.MinValue;

    // Slice V (ac-ai-players#86): the picker's most-recent activity
    // surfaced to the LLM as a parallel "## Autonomous picker
    // activity" block in the prompt. Set by the driver each tick
    // BEFORE ProposeGoal via SetCurrentPickerActivity. Null = picker
    // is idle (no autonomous selection in flight).
    private PickerActivity? _currentPickerActivity;

    // Slice W.2 (ac-ai-players#87): top-N exploration candidates the
    // fallback picker is considering when the in-range queue is
    // empty. Surfaced as "## Exploration candidates" in the prompt
    // so the LLM can override the fallback's nearest-distance pick
    // by emitting `Explore{target=<guid|name>}`. Empty list (or
    // null) = no candidates / nothing to show.
    private IReadOnlyList<ExplorationCandidate>? _currentExplorationCandidates;

    /// <summary>
    /// Driver-driven setter. Called by HandshakeDriver each tick
    /// before <see cref="ProposeGoal"/> so the LLM prompt's
    /// "## Autonomous picker activity" block reflects what the
    /// picker is doing RIGHT NOW. Null = picker is idle.
    /// </summary>
    public void SetCurrentPickerActivity(PickerActivity? activity)
        => _currentPickerActivity = activity;

    /// <summary>
    /// Driver-driven setter for Slice W.2 candidate list. Called
    /// before ProposeGoal when the in-range queue is empty and the
    /// fallback picker has off-screen options. Null / empty = no
    /// fallback candidates this tick.
    /// </summary>
    public void SetCurrentExplorationCandidates(IReadOnlyList<ExplorationCandidate>? candidates)
        => _currentExplorationCandidates = candidates;

    // Slice T — 429 / rate-limit backoff. GitHub Models (the spike's
    // current LLM provider) returns HTTP 429 once a small per-minute
    // and per-day quota is exhausted. Without backoff the policy
    // burns retries every few seconds the entire spike (54-decision
    // run on 2026-05-29 saw 28 consecutive 429s — every LLM call
    // failed). On a 429 we set _backoffUntilUtc and double
    // _currentBackoff (cap 5 min). ProposeGoal gates on it before
    // kicking off another call. Any successful (Ok=true) result
    // resets the backoff to 30s. Other error kinds (transport, 5xx,
    // parse) are NOT counted as backoff-triggering — they retry
    // immediately as before.
    private DateTimeOffset _backoffUntilUtc = DateTimeOffset.MinValue;
    private TimeSpan       _currentBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff     = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);
    // Floor for Retry-After honoring. If the server says "retry in 0s"
    // (or in <1s) we still wait 1s so the LLM is not hammered in a
    // tight retry loop -- the 2s MinCallInterval already coalesces
    // back-to-back calls, but this is a separate floor specifically
    // for the rate-limit-window logic.
    private static readonly TimeSpan MinRetryAfter  = TimeSpan.FromSeconds(1);

    /// <summary>
    /// In-flight LLM call. ProposeGoal is called from the receive
    /// loop and must NEVER block the loop on a 1s HTTP RTT, so we
    /// kick off a Task on the first triggering tick and poll its
    /// completion on subsequent ticks.
    /// </summary>
    private Task<(LlmResult Result, Guid DecisionId, string UserPrompt, string ProjJson, long EventSeqAtCallStart, bool HadCurrentGoalAtCallStart)>? _inflight;

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

        // Stamp the durable landblock-entry time BEFORE any early return
        // so the `minutes in current landblock` prompt signal reflects the
        // latest observation regardless of which path this tick takes
        // (in-flight poll, backoff, coalesce, fallback). See field docs.
        UpdateDwellTracking(world.Self.Landblock, events, nowUtc);

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
        // action failed (WeenieErrorWithString surfaced as a SEMANTIC
        // ActionRejected) since our last LLM look, drop currentGoal
        // so the LLM is not anchored on the failed goal in the
        // prompt's '## Current goal' section. Parallels the
        // landblock-change guard above. Stops the loop observed in
        // stalefix-run-01 where the Society Greeter kept rejecting
        // the Calling Stone with TradeAiDoesntWant and the LLM kept
        // re-emitting Give(Society Greeter, Calling Stone) forever.
        // Transport-failure rejections (could-not-walk) are excluded
        // by HasRejectionSince — they don't invalidate the goal.
        if (currentGoal is not null && HasRejectionSince(events, _lastEventConsideredSequence))
        {
            Console.WriteLine(
                $"[strategy] LlmGoalPolicy: ActionRejected since last look → " +
                $"dropping rejected goal '{currentGoal.Kind} target={currentGoal.Target}' from prompt anchor");
            currentGoal = null;
        }

        // 3) Decide whether to kick off a new call.
        // Slice T — if we're inside a 429 backoff window, do NOT
        // kick off another call. Returning currentGoal keeps the
        // tactical layer driving on its existing plan; the fallback
        // (NoQuestKnowledgePolicy) handles cases where currentGoal
        // is null. This is intentionally separate from the coalesce
        // gate below: coalesce is a 2s per-call rate limit; backoff
        // is a multi-minute back-off after the LLM provider tells
        // us we're over quota.
        if (nowUtc < _backoffUntilUtc)
        {
            var remaining = _backoffUntilUtc - nowUtc;
            if (currentGoal is null)
            {
                // No goal to keep driving on — fall through to
                // fallback policy this tick so the bot keeps acting
                // while we wait for the rate-limit window.
                Console.WriteLine(
                    $"[strategy] LlmGoalPolicy: 429 backoff active ({remaining.TotalSeconds:F0}s remaining); " +
                    $"no current goal — deferring to fallback");
                return _fallback.ProposeGoal(world, events, currentGoal);
            }
            return currentGoal;
        }

        var hasNonPickerSalient = HasNewNonPickerSalientEvent(events);
        var hasNonPickerExternal = HasNewNonPickerExternalEvent(events);
        var stuck = nowUtc - _lastCalledAtUtc > StuckTimeout;
        var coalesce = nowUtc - _lastCalledAtUtc < MinCallInterval;
        // Picker wakeups are split (call-volume reduction).
        //
        // - PickerArrivedNoAction: the picker parked the bot next to a
        //   target and sent NO opcode; the LLM has a narrow window to
        //   name a verb before the picker moves on. This is the safety
        //   valve against the bot looking dumb near a discovered target,
        //   so it MUST still punch through coalesce on every occurrence.
        // - PickerActivityStarted: the picker merely switched its
        //   auto-driven target. In an object-rich area this fires
        //   constantly and used to wake the LLM every time, bursting
        //   calls. Now a start for the SAME target within
        //   PickerStartCoalesce is suppressed; a NEW target still wakes
        //   immediately (so the LLM never loses the chance to override a
        //   genuinely new autonomous pick before the bot commits).
        var pickerArrived = HasPickerArrivedSince(events, _lastEventConsideredSequence);
        var pickerStartKey = NewestPickerStartTargetKeySince(events, _lastEventConsideredSequence);
        var pickerStartWake = pickerStartKey is not null && ShouldWakeForPickerStart(pickerStartKey, nowUtc);

        // Suppressed picker-start: a same-target (or in-window) start was
        // the ONLY thing that would have woken the LLM. Skip the call and
        // keep driving the current goal.
        //
        // We ADVANCE _lastEventConsideredSequence past the consumed
        // picker-start(s) here. This is safe — and necessary — because the
        // guard proves the window since the floor contains NO genuinely
        // external event: !hasNonPickerSalient rules out every salient
        // non-picker kind, and !hasNonPickerExternal additionally rules out
        // InventoryItemRemoved (external but not salient — e.g. a completed
        // Give). So the only thing being consumed is picker-start noise we
        // are deliberately ignoring. Without advancing, the single
        // per-switch picker-start would linger in the stream and (a) re-log
        // this suppression every tick, and (b) later trip the
        // sticky-objective gate's external-event check once the goal
        // clears, defeating the free sticky re-emit. The picker-start
        // dedupe window itself is tracked by SEPARATE state
        // (_lastPickerStartWakeKey/_lastPickerStartWakeAtUtc), so advancing
        // the floor does not disturb it.
        if (pickerStartKey is not null && !pickerStartWake
            && currentGoal is not null
            && !hasNonPickerSalient && !hasNonPickerExternal
            && !pickerArrived && !stuck)
        {
            var sameTarget = string.Equals(pickerStartKey, _lastPickerStartWakeKey, StringComparison.Ordinal);
            Console.WriteLine(
                $"[llm-call] suppressed reason=picker-start-{(sameTarget ? "same-target" : "coalesce")} " +
                $"target={pickerStartKey} window={PickerStartCoalesce.TotalSeconds:F0}s");
            _lastEventConsideredSequence = events.NextSequence;
            return currentGoal;
        }

        var anyWake = hasNonPickerSalient || pickerArrived || pickerStartWake;
        if (currentGoal is not null && !anyWake && !stuck) return currentGoal;
        // Non-picker salient events still respect the 2s coalesce; the
        // picker arrival + new-target picker-start paths bypass it.
        if (coalesce && currentGoal is not null && !pickerArrived && !pickerStartWake) return currentGoal;

        // STICKY LLM-OBJECTIVE (call-volume reduction). The tactical
        // goal has cleared (currentGoal == null), so without this gate
        // every goal completion would burn a multi-second LLM round
        // trip. But if the last LLM-authored objective is still
        // unfinished and NOTHING happened in the world except our own
        // goal-lifecycle churn, re-drive that objective for free.
        //
        // Discriminator: a genuinely completed Talk/Pickup/Use emits an
        // EXTERNAL salient event (NpcDialog / InventoryItemAdded /
        // PopupString / ServerMessage / ActionRejected / LandblockChanged /
        // picker arrival) — that breaks this gate (via hasNonPickerExternal
        // or pickerArrived) and lets the LLM decide fresh. A genuinely NEW
        // picker target (pickerStartWake) also breaks it, so the bot is not
        // blinded to discoveries while aimless. Only same-target picker
        // FLUTTER (pickerStartWake == false — same target within the
        // PickerStartCoalesce window) is ignored, matching the current-goal
        // path's debounce. A
        // directed pursuit that cleared WITHOUT arriving (e.g. a
        // walk-to-unseen-target whose motion lock timed out) emits ONLY
        // Goal* lifecycle events, so we keep pursuing the same target.
        // Returning the goal re-installs it as currentGoal, so the
        // Motor drives it normally (via the line-264 early-return) until
        // it clears again; the retry budget counts re-CLEARS so an
        // unreachable target cannot spin forever — after MaxStickyReEmits
        // re-clears we fall through to a real LLM re-think. !stuck keeps
        // the 30s stuck-timeout able to force a fresh call. Establishment
        // is unaffected: _lastLlmGoal is null until the first successful
        // LLM goal. The budget resets when a new LLM goal is consumed
        // (see ConsumeResult). This carries NO game knowledge — it is
        // pure event-kind + goal-provenance bookkeeping.
        if (currentGoal is null
            && _lastLlmGoal is not null
            && !stuck
            && _stickyReEmitCount < MaxStickyReEmits
            && !hasNonPickerExternal
            && !pickerArrived
            && !pickerStartWake)
        {
            // Call-volume reduction (aimless path). Live evidence: a fresh
            // L1 bot is dominantly aimless (currentGoal == null) in an
            // object-rich area, and the autonomous picker constantly
            // re-fires PickerActivityStarted as it switches its auto-driven
            // target. A picker-START is in the external-change set, so it
            // used to trip the sticky gate and burn a multi-second LLM
            // establishment call.
            //
            // We re-drive the unfinished LLM objective for free ONLY when
            // the picker-start is mere FLUTTER — i.e. !pickerStartWake, the
            // SAME target that last woke the LLM, within PickerStartCoalesce
            // (the exact debounce the current-goal path uses, so aimless and
            // busy are consistent). A genuinely NEW picker target makes
            // pickerStartWake true and breaks this gate, so the LLM still
            // gets to weigh the discovery — the bot is not blinded to new
            // objects while aimless. A picker ARRIVAL (pickerArrived, the
            // verb-naming moment) and any non-picker external change
            // (NpcDialog / InventoryItemAdded / InventoryItemRemoved /
            // ActionRejected / LandblockChanged …) also break the gate.
            // Bounded by MaxStickyReEmits re-clears and the 30s stuck-timeout
            // exactly as before.
            //
            // ADVANCE the event floor past the consumed flutter picker-
            // start(s). The gate above proves the window holds NO genuinely
            // external event (no non-picker external, no arrival) and no
            // wake-worthy picker-start — only flutter we are deliberately
            // ignoring — so this hides nothing real. It is necessary: a
            // re-emitted sticky goal makes currentGoal non-null next tick,
            // and a lingering flutter picker-start would otherwise re-log /
            // re-evaluate every tick. The picker-start dedupe window is
            // tracked by SEPARATE state (_lastPickerStartWakeKey/At), so
            // advancing the floor does not disturb it.
            _lastEventConsideredSequence = events.NextSequence;
            _stickyReEmitCount++;
            var sticky = _lastLlmGoal with { Id = Guid.NewGuid(), CreatedAtUtc = nowUtc };
            Console.WriteLine(
                $"[strategy] sticky-objective re-emit #{_stickyReEmitCount}/{MaxStickyReEmits} " +
                $"kind={sticky.Kind} target={sticky.Target}" +
                (sticky.Item is null ? "" : $" item={sticky.Item}") +
                " (no external salient event since last LLM look; skipping LLM call)");
            return sticky;
        }

        _lastCalledAtUtc = nowUtc;
        var eventSeqAtCallStart = events.NextSequence;
        _lastEventConsideredSequence = eventSeqAtCallStart;

        // Record the picker-start wake (separate from the event floor) so
        // a repeat start for the SAME target is coalesced for the next
        // PickerStartCoalesce window. Only record when a picker-start was
        // the (or a) reason we are waking — never infer sanction from a
        // call triggered by something else.
        if (pickerStartWake && pickerStartKey is not null)
        {
            _lastPickerStartWakeKey = pickerStartKey;
            _lastPickerStartWakeAtUtc = nowUtc;
        }

        var dwellEntry = DwellEntryForPrompt(world.Self.Landblock);
        var userPrompt = BuildUserPrompt(world, events, currentGoal, _stack, _currentPickerActivity, _currentExplorationCandidates, dwellEntry);
        var projJson = JsonSerializer.Serialize(world);
        var decisionId = Guid.NewGuid();

        // Slice W.3 diagnostic — log every LLM kickoff so we can
        // tell at a glance whether the LLM is being called, why,
        // and how often. Without this the bot parks at "PICKER
        // ARRIVED no-action" silently and the operator cannot
        // distinguish "LLM never called" from "LLM called and
        // failed silently into the fallback path". Trigger categories
        // are mutually exclusive and measurable (the old classification
        // could never report picker-steering because the picker kinds
        // were folded into the wider salient check first).
        var trigger = currentGoal is null
            ? "no-current-goal"
            : (hasNonPickerSalient ? "non-picker-salient"
                : (pickerArrived ? "picker-arrived"
                    : (pickerStartWake ? "picker-start"
                        : (stuck ? "stuck-timeout" : "unknown"))));
        var dwellMinStr = dwellEntry is DateTimeOffset de
            ? Math.Max(0.0, (nowUtc - de).TotalMinutes).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : "n/a";
        Console.WriteLine(
            $"[llm-call] kickoff id={decisionId} trigger={trigger} " +
            $"prompt-bytes={userPrompt.Length} dwell-min={dwellMinStr} model={_client.Model}");

        _inflight = RunAsync(userPrompt, decisionId, projJson, eventSeqAtCallStart, currentGoal is not null);
        return currentGoal; // keep doing whatever we were doing while the LLM thinks
    }

    private async Task<(LlmResult, Guid, string, string, long, bool)> RunAsync(string userPrompt, Guid decisionId, string projJson, long eventSeqAtCallStart, bool hadCurrentGoalAtCallStart)
    {
        LlmResult result;
        using var cts = new CancellationTokenSource(LlmCallTimeout);
        try
        {
            result = await _client.CompleteAsync(SystemPrompt, userPrompt, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Hard-deadline guard. Without this, a hung HttpClient
            // call leaves `_inflight` non-null forever and the bot
            // never deliberates again (seen in flexguid01 run-01).
            result = new LlmResult(false, "", "",
                (int)LlmCallTimeout.TotalMilliseconds,
                $"timeout after {LlmCallTimeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            result = new LlmResult(false, "", "", 0, $"unhandled: {ex.Message}");
        }
        return (result, decisionId, userPrompt, projJson, eventSeqAtCallStart, hadCurrentGoalAtCallStart);
    }

    private Goal? ConsumeResult(
        Task<(LlmResult Result, Guid DecisionId, string UserPrompt, string ProjJson, long EventSeqAtCallStart, bool HadCurrentGoalAtCallStart)> finishedTask,
        WorldStateProjection world,
        EventStream events,
        Goal? currentGoal,
        DateTimeOffset nowUtc)
    {
        var (result, decisionId, userPrompt, projJson, eventSeqAtCallStart, hadCurrentGoalAtCallStart) = finishedTask.GetAwaiter().GetResult();

        // Sticky-objective invalidation: a deliberation has now resolved.
        // Clear the remembered LLM objective up-front so that EVERY exit
        // below other than a freshly-parsed success leaves it null. This
        // stops a stale objective from being re-driven after a failed /
        // discarded / dedup-dropped fresh deliberation (the triggering
        // external event is already behind _lastEventConsideredSequence,
        // so sticky could otherwise re-emit the OLD goal). The success
        // path re-sets it below.
        _lastLlmGoal = null;

        // Stale-result detection (narrow). If a plan-INVALIDATING
        // event arrived after we kicked off the LLM call, the world
        // has moved past the prompt we sent and the response would
        // lock the bot into a stale plan. Discard, reset throttling,
        // and force fresh deliberation next tick.
        //
        // CRITICAL: the trigger set (HasNewSalientEvent) is wide —
        // chatty events like ServerMessage and NpcDialog SHOULD
        // invite the LLM to think again. The discard set is narrow
        // — only events that genuinely obsolete the in-flight
        // response. Conflating the two (prior to this fix) caused
        // 2/8 LLM calls in spike `bot_llama01` to be discarded
        // mid-flight by the ServerMessage / NpcDialog firehose,
        // making active combat impossible (every Attack goal
        // would be cancelled by its own damage-number stream).
        //
        // DELIBERATION-RACE FIX: when there was NO LLM plan at
        // call-start (an *establishment* call), Goal* lifecycle
        // events that arrive during the call do NOT invalidate the
        // response — they are the autonomous fallback policy's own
        // churn (it sets then Clears a CurrentGoal every ~2s as it
        // visits nearby objects, each Clear emitting GoalCompleted).
        // Gating on the CALL-START plan state (not consume-time
        // currentGoal, which can legitimately go null mid-call) lets
        // a fresh L1 bot in an object-rich room actually land an LLM
        // goal instead of having every ~7s establishment call
        // discarded by the 2s picker cadence. LandblockChanged /
        // InventoryItemRemoved / SEMANTIC ActionRejected stay
        // invalidating regardless — those reflect real world movement
        // the prompt no longer matches. A TRANSPORT-failure
        // ActionRejected (synthetic motor codes 0xFFFC-0xFFFE: the
        // autonomous picker could not WALK to a candidate) is NOT
        // invalidating — it does not change the object snapshot the LLM
        // reasoned about. Same-target suppression for transport failures
        // is owned by IsGoalRecentlyRejected (which has target matching
        // and arrival-clearing); see IsPlanInvalidatingEvent.
        var staleSinceCall = HasPlanInvalidatingSince(events, eventSeqAtCallStart, hadCurrentGoalAtCallStart);

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
            Console.WriteLine(
                $"[llm-call] stale id={decisionId} latency={result.LatencyMs}ms " +
                $"(plan-invalidating event arrived during call; discarding response)");
            // Reset _lastCalledAtUtc to bypass MinCallInterval on the
            // next ProposeGoal — we want to re-call ASAP with fresh
            // observations.
            _lastCalledAtUtc = DateTimeOffset.MinValue;
            _lastEventConsideredSequence = -1;
            return currentGoal;
        }

        if (!result.Ok)
        {
            // Slice W.3 diagnostic — surface the failure reason so
            // the operator can tell auth-failed / parse-failed /
            // transient-5xx / 429 apart at a glance. Previously this
            // path silently returned the fallback goal with no log
            // line, hiding any non-429 failure (e.g. no api key,
            // model name typo, network blip) for the entire run.
            Console.WriteLine(
                $"[llm-call] FAILED id={decisionId} latency={result.LatencyMs}ms " +
                $"error={result.Error ?? "(null)"}");
            // Slice T (extended) — 429 / rate-limit backoff trigger.
            // Prefer the structured HttpStatusCode field over substring
            // matching on Error (the old check was brittle to message
            // format changes and could false-positive on unrelated
            // errors containing "429"). Honour the server's Retry-After
            // header when present; otherwise fall back to the
            // exponential window. Always ratchet _currentBackoff so
            // persistent failures still escalate pressure even if the
            // server keeps returning small Retry-After hints.
            var is429 =
                result.StatusCode == (System.Net.HttpStatusCode)429 ||
                (result.Error is not null && result.Error.Contains("429"));
            if (is429)
            {
                TimeSpan window;
                bool honored = false;
                if (result.RetryAfter is { } ra && ra > TimeSpan.Zero)
                {
                    if (ra < MinRetryAfter) window = MinRetryAfter;
                    else if (ra > MaxBackoff) window = MaxBackoff;
                    else window = ra;
                    honored = true;
                }
                else
                {
                    window = _currentBackoff;
                }
                _backoffUntilUtc = nowUtc + window;
                Console.WriteLine(
                    $"[strategy] LlmGoalPolicy: 429 detected — " +
                    (honored
                        ? $"retry-after={result.RetryAfter!.Value.TotalSeconds:F1}s honored (window={window.TotalSeconds:F0}s); "
                        : $"window={window.TotalSeconds:F0}s; ") +
                    $"backoff until {_backoffUntilUtc:HH:mm:ss}Z " +
                    $"(next exponential interval {(_currentBackoff.TotalSeconds * 2):F0}s)");
                var next = TimeSpan.FromSeconds(_currentBackoff.TotalSeconds * 2);
                _currentBackoff = next > MaxBackoff ? MaxBackoff : next;
            }
            return _fallback.ProposeGoal(world, events, currentGoal);
        }

        // Slice T — successful LLM call resets the backoff so a
        // future 429 starts at the initial 30s interval again
        // (we don't want a single recovered request to skip the
        // doubling discipline for a NEW rate-limit event).
        _currentBackoff = InitialBackoff;

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
            Console.WriteLine(
                $"[llm-call] parse-error id={decisionId} latency={result.LatencyMs}ms " +
                $"error={parseError ?? "(null)"} content-bytes={result.Content?.Length ?? 0}");
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

        // Inventory-USE dedup (2026-05-30): if the LLM emitted Use
        // against an inventory item we've already USE'd in the
        // recent event window, drop it. Motivating spike
        // (bot_stalenarrow01) showed Llama-3.3-70B emitting
        // Use{Letter From Home} 5 times in 3 min because the
        // tutorial letter is non-consumable; the short_desc
        // ("double-click to read") never goes away, so the LLM
        // keeps re-emitting the same Use. This crowded out Attack
        // emission (Sparring Golem at d=49u was visible + monster-
        // tagged, weapon wielded, but never attacked). Falling
        // through to the fallback gives the bot a chance to pick
        // a different action.
        if (IsInventoryUseRecentlyDispatched(goal, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Use kind={goal.Kind} target={goal.Target}" +
                (goal.Item is null ? "" : $" item={goal.Item}") +
                " — inventory item already USE'd recently; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: LLM Use targets recently-used inventory item");
            return _fallback.ProposeGoal(world, events, currentGoal);
        }

        _training?.RecordEmittedGoal(decisionId, goal);
        // Sticky-objective bookkeeping: remember this LLM-authored goal
        // so ProposeGoal can re-drive it without another LLM call while
        // it remains unfinished, and give the new objective a fresh
        // re-emit budget.
        _lastLlmGoal = goal;
        _stickyReEmitCount = 0;
        Console.WriteLine(
            $"[llm-call] success id={decisionId} latency={result.LatencyMs}ms " +
            $"goal=kind={goal.Kind} target={goal.Target}" +
            (goal.Item is null ? "" : $" item={goal.Item}"));
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

            bool matched = false;

            // Item-specific rejection (carries item wcid/name —
            // typically Slice J's InventoryServerSaveFailed).
            if (itemWcid is uint w && ev.Wcid == w) matched = true;
            else if (!string.IsNullOrWhiteSpace(itemName) &&
                !string.IsNullOrWhiteSpace(ev.Name) &&
                string.Equals(ev.Name, itemName, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
            }
            // Target-name match: NPC name carried in Name (Unreachable)
            // or Text (WeenieErrorWithString puts the NPC name there).
            else if (!string.IsNullOrWhiteSpace(targetName))
            {
                if (!string.IsNullOrWhiteSpace(ev.Name) &&
                    string.Equals(ev.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                }
                else if (!string.IsNullOrWhiteSpace(ev.Text))
                {
                    if (string.Equals(ev.Text, targetName, StringComparison.OrdinalIgnoreCase))
                        matched = true;
                    // Substring match (for "Unreachable: 'X' (walk timeout ...)")
                    // gated on a minimum target-name length to avoid
                    // false positives on short common substrings.
                    else if (targetName.Length >= 4 &&
                        ev.Text.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                    }
                }
            }

            if (!matched) continue;

            // Transport-failure staleness. Synthetic motor-side
            // rejections (Unreachable / Blocked / NoIndoorPath, codes
            // 0xFFFC-0xFFFE) mean the bot could not WALK to the target
            // — NOT that the server refused the interaction. They are
            // transient: once the bot has SINCE arrived in range of the
            // same target (a later PickerArrivedNoAction for the same
            // guid/name), the rejection is obsolete and must not block
            // the interact verb. Without this, a bot parked in range of
            // a pickup-eligible item it earlier walk-timed-out toward
            // deadlocks — the picker keeps re-selecting the nearest item
            // and the LLM's correct Pickup is dropped every cycle.
            // Server (semantic) rejections — TradeAiDoesntWant,
            // InventoryServerSaveFailed, WeenieErrorWithString — carry
            // real WeenieError codes and stay blocking regardless.
            if (IsTransportFailureRejection(ev) && HasArrivedAtTargetSince(events, ev))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// True iff the ActionRejected is a synthetic motor-side transport
    /// failure (the bot could not walk to the target), as opposed to a
    /// server-side semantic refusal. Transport failures are emitted by
    /// the motor with reserved codes 0xFFFC (NoIndoorPath), 0xFFFD
    /// (Blocked), and 0xFFFE (Unreachable) — see HandshakeDriver. Real
    /// WeenieError codes are far smaller, so the reserved high range is
    /// an unambiguous discriminator.
    /// </summary>
    internal static bool IsTransportFailureRejection(StreamEvent ev) =>
        ev.Kind == EventKind.ActionRejected &&
        ev.ErrorCode is 0xFFFCu or 0xFFFDu or 0xFFFEu;

    /// <summary>
    /// True iff a <see cref="EventKind.PickerArrivedNoAction"/> event for
    /// the SAME target as <paramref name="rejection"/> occurred AFTER the
    /// rejection (strictly higher Sequence) — i.e. the bot has since
    /// reached the target it earlier failed to walk to. Matches by target
    /// guid (ItemGuid) when both carry one; otherwise by Name.
    /// </summary>
    internal static bool HasArrivedAtTargetSince(EventStream events, StreamEvent rejection)
    {
        foreach (var ev in events.Recent())
        {
            if (ev.Kind != EventKind.PickerArrivedNoAction) continue;
            if (ev.Sequence <= rejection.Sequence) continue;

            if (rejection.ItemGuid is uint rg && rg != 0 &&
                ev.ItemGuid is uint ag && ag != 0)
            {
                if (ag == rg) return true;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(rejection.Name) &&
                !string.IsNullOrWhiteSpace(ev.Name) &&
                string.Equals(ev.Name, rejection.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True iff the goal is a <see cref="GoalKind.Use"/> whose
    /// target or item matches an <see cref="EventKind.InventoryItemUsed"/>
    /// event within the recent event window — meaning we already
    /// dispatched a GameActionUse against the same inventory item
    /// recently. Match key:
    /// <list type="bullet">
    /// <item>Item wcid (if the goal carries an Item).</item>
    /// <item>Item name (case-insensitive exact match).</item>
    /// <item>Target wcid (if the LLM put the item under target/
    /// rather than item/, which the prompt does for inventory-USE).</item>
    /// <item>Target name (case-insensitive exact).</item>
    /// </list>
    /// Only applies to <see cref="GoalKind.Use"/>; other verbs
    /// (Pickup, Wield, Talk, Attack, Give) are unaffected — a
    /// re-USE block on those would be wrong.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="EventStream.RecentOfKind"/> with N=16 — pulls
    /// only InventoryItemUsed events regardless of how many other
    /// (high-volume) events have arrived since the dispatch.
    ///
    /// Original implementation used <c>Recent(30)</c> (mixed-kind)
    /// to match <see cref="IsGoalRecentlyRejected"/>, but spike
    /// bot_invdedup01 (2026-05-30) showed two <c>Use{Letter From
    /// Home}</c> dispatches with seven LLM kickoffs between them;
    /// the <c>InventoryItemUsed</c> marker from the first dispatch
    /// had been evicted from the 30-event window by intervening
    /// LandblockChanged / InventoryItemAdded / NpcDialog /
    /// ServerMessage / GoalCompleted events. RecentOfKind is the
    /// semantically correct primitive — "have I used this item in
    /// the last 16 USE dispatches?" — and isolates the dedup from
    /// noise in the event stream. The IsGoalRecentlyRejected mixed-
    /// kind window stays at 30 because ActionRejected events are
    /// frequent enough that a per-kind window would over-dedup.
    ///
    /// For non-consumable inventory (notes, letters, tutorial
    /// items) this prevents the runaway loop. For consumables
    /// (potions, scrolls) the bot can re-USE after 16 distinct USE
    /// dispatches, which is fine for the current academy/M3 scope;
    /// M4+ may want a wall-clock window or an "item still in
    /// inventory unchanged" predicate.
    /// </remarks>
    internal static bool IsInventoryUseRecentlyDispatched(Goal goal, EventStream events)
    {
        if (goal.Kind != GoalKind.Use) return false;
        const int LookbackUseEvents = 16;

        var targetName = goal.Target?.Name;
        var targetWcid = goal.Target?.Wcid;
        var itemName   = goal.Item?.Name;
        var itemWcid   = goal.Item?.Wcid;

        if (string.IsNullOrWhiteSpace(targetName) &&
            string.IsNullOrWhiteSpace(itemName) &&
            targetWcid is null && itemWcid is null)
        {
            return false;
        }

        foreach (var ev in events.RecentOfKind(EventKind.InventoryItemUsed, LookbackUseEvents))
        {
            if (itemWcid is uint iw && ev.Wcid == iw) return true;
            if (targetWcid is uint tw && ev.Wcid == tw) return true;

            if (!string.IsNullOrWhiteSpace(ev.Name))
            {
                if (!string.IsNullOrWhiteSpace(itemName) &&
                    string.Equals(ev.Name, itemName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(targetName) &&
                    string.Equals(ev.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if any event newer than <paramref name="sequenceFloor"/>
    /// is plan-invalidating: it makes the in-flight LLM response
    /// (which was generated against an older world snapshot) unsafe
    /// to act on. INTENTIONALLY narrower than the "wake the LLM"
    /// trigger set — chatty events (ServerMessage, NpcDialog,
    /// PopupString, BookText) and option-adding events
    /// (InventoryItemAdded) should NOT cancel an in-flight call.
    /// </summary>
    /// <remarks>
    /// Why each kind is included:
    /// <list type="bullet">
    /// <item>LandblockChanged — teleport / zone crossing makes all
    /// positional context in the prompt wrong.</item>
    /// <item>InventoryItemRemoved — an item the LLM may have named
    /// as a goal target is gone.</item>
    /// <item>ActionRejected — server denied something; we MUST
    /// re-deliberate before issuing another action.</item>
    /// <item>GoalCompleted / GoalFailed / GoalExpired — the active
    /// goal context the LLM reasoned over no longer applies;
    /// accepting the response could resurrect the just-finished
    /// goal or retry a goal Tactics already proved impossible.</item>
    /// </list>
    /// Why each kind is excluded (deliberately):
    /// <list type="bullet">
    /// <item>ServerMessage / NpcDialog — chatty firehose during
    /// combat or conversation; do not invalidate a strategic plan.</item>
    /// <item>PopupString / BookText — informational; enrich
    /// knowledge but don't obsolete the in-flight goal.</item>
    /// <item>InventoryItemAdded — a new option appeared but the
    /// in-flight plan is still valid; the next deliberation will
    /// see the new item.</item>
    /// <item>PickerActivityStarted / PickerArrivedNoAction —
    /// picker-steering events; they wake the LLM but the in-flight
    /// LLM call already reflects the most recent prompt-time
    /// picker state.</item>
    /// </list>
    /// </remarks>
    internal static bool HasPlanInvalidatingSince(EventStream events, long sequenceFloor)
        => HasPlanInvalidatingSince(events, sequenceFloor, hasActivePlan: true);

    /// <summary>
    /// Plan-invalidation test with explicit knowledge of whether an
    /// LLM plan was active when the in-flight call was kicked off.
    /// When <paramref name="hasActivePlan"/> is false the call was an
    /// *establishment* call (no tactical goal to protect at call-start);
    /// Goal* lifecycle events that carry a tactical <c>GoalId</c> are
    /// then EXCLUDED from invalidation because they are the autonomous
    /// fallback policy's own set-then-Clear churn (TacticsExecutor.Clear
    /// /Fail always stamp the completing goal's id). Goal* events WITHOUT
    /// a GoalId represent strategic intent-stack completion — that does
    /// stale the prompt's intent context, so it stays invalidating even
    /// for an establishment call. World-movement kinds (LandblockChanged
    /// / InventoryItemRemoved / ActionRejected) stay invalidating in both
    /// modes.
    /// </summary>
    internal static bool HasPlanInvalidatingSince(EventStream events, long sequenceFloor, bool hasActivePlan)
    {
        return events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => IsPlanInvalidatingEvent(e, hasActivePlan));
    }

    /// <summary>
    /// Event-level plan-invalidation classifier. Unlike the kind-only
    /// <see cref="IsPlanInvalidatingKind(EventKind, bool)"/> it can
    /// distinguish tactical goal churn (non-null <c>GoalId</c>) from
    /// strategic intent-stack completion (null <c>GoalId</c>) when there
    /// was no tactical plan at call-start. See
    /// <see cref="HasPlanInvalidatingSince(EventStream, long, bool)"/>.
    /// </summary>
    private static bool IsPlanInvalidatingEvent(StreamEvent e, bool hasActivePlan)
    {
        if (e.Kind is EventKind.LandblockChanged
                   or EventKind.InventoryItemRemoved)
            return true;

        // ActionRejected splits two ways. A SEMANTIC rejection (real
        // server WeenieError, e.g. TradeAiDoesntWant) means the world
        // refused the interaction — the prompt snapshot is obsolete, so
        // it stays invalidating. A TRANSPORT-failure rejection (synthetic
        // motor codes 0xFFFC-0xFFFE: NoIndoorPath / Blocked / Unreachable)
        // only means the motor could not WALK to a target; the object
        // snapshot the LLM reasoned about is unchanged, so it must NOT
        // discard the in-flight response. Without this carve-out a fresh
        // bot's autonomous-picker walk-timeout (transport failure) during
        // an establishment call wrongly staled the response. Same-target
        // suppression for transport failures is handled downstream by
        // IsGoalRecentlyRejected.
        if (e.Kind is EventKind.ActionRejected)
            return !IsTransportFailureRejection(e);

        var isGoalLifecycle = e.Kind is EventKind.GoalCompleted
                                     or EventKind.GoalFailed
                                     or EventKind.GoalExpired;
        if (!isGoalLifecycle)
            return false;

        if (hasActivePlan)
            return true;

        // Establishment call: a tactical-goal lifecycle event (has a
        // GoalId) is fallback churn → ignore. A GoalId-less one is
        // intent-stack completion → still invalidates.
        return e.GoalId is null;
    }

    /// <summary>
    /// The plan-invalidating event-kind classifier (kind-only). See
    /// <see cref="HasPlanInvalidatingSince(EventStream, long)"/> for the
    /// include / exclude rationale.
    /// </summary>
    internal static bool IsPlanInvalidatingKind(EventKind kind) =>
        IsPlanInvalidatingKind(kind, hasActivePlan: true);

    /// <summary>
    /// Kind-only plan-invalidation classifier with explicit active-plan
    /// context. The Goal* lifecycle kinds only invalidate when a plan was
    /// active at call-start; otherwise they are treated as fallback-policy
    /// churn (the event-level <see cref="IsPlanInvalidatingEvent"/> refines
    /// this further using the GoalId to spare strategic intent completion).
    /// NOTE: kind-only cannot see ErrorCode, so it conservatively treats
    /// ALL <see cref="EventKind.ActionRejected"/> as invalidating. The
    /// event-level classifier refines this — a transport-failure rejection
    /// (synthetic motor codes 0xFFFC-0xFFFE) is NOT invalidating there.
    /// </summary>
    internal static bool IsPlanInvalidatingKind(EventKind kind, bool hasActivePlan)
    {
        if (kind is EventKind.LandblockChanged
                 or EventKind.InventoryItemRemoved
                 or EventKind.ActionRejected)
            return true;

        if (!hasActivePlan)
            return false;

        return kind is EventKind.GoalCompleted
                    or EventKind.GoalFailed
                    or EventKind.GoalExpired;
    }

    /// <summary>
    /// The "wake the LLM" event-kind classifier used by
    /// <see cref="HasNewSalientEvent"/>. Wider than
    /// <see cref="IsPlanInvalidatingKind"/> — it includes chatty
    /// events whose only effect is to give the LLM something new
    /// to react to (NpcDialog, ServerMessage, BookText) and
    /// option-adding events (InventoryItemAdded), as well as the
    /// picker-steering events that need a deliberation pass.
    /// </summary>
    /// <remarks>
    /// EXCLUDED on purpose:
    /// <list type="bullet">
    /// <item><see cref="EventKind.InventoryItemUsed"/> — self-
    /// emitted echo of our own dispatch; waking the LLM on it
    /// would defeat the dedup it exists to power.</item>
    /// <item><see cref="EventKind.PickerActivityCompleted"/> —
    /// only Started churns deliberation; Completed is bookkeeping.</item>
    /// </list>
    /// </remarks>
    internal static bool IsSalientKind(EventKind kind) =>
        kind is EventKind.PopupString
             or EventKind.InventoryItemAdded
             or EventKind.LandblockChanged
             or EventKind.GoalCompleted
             or EventKind.GoalFailed
             or EventKind.GoalExpired
             or EventKind.NpcDialog
             or EventKind.ServerMessage
             or EventKind.ActionRejected
             or EventKind.BookText
             or EventKind.PickerActivityStarted
             or EventKind.PickerArrivedNoAction;

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
        // Same shape as HasLandblockChangeSince — look for a SEMANTIC
        // ActionRejected (server told us the last action failed) newer
        // than our last LLM look. TRANSPORT-failure rejections (synthetic
        // motor codes 0xFFFC-0xFFFE: the bot could not WALK to the target)
        // are deliberately excluded: they do not mean the goal is invalid,
        // only that the route failed, so they must NOT drop the current
        // goal from the prompt anchor. Dropping on a transient walk-timeout
        // would force a needless re-establishment and undo a just-landed
        // LLM goal. Transport-failure same-target suppression is owned by
        // IsGoalRecentlyRejected (target matching + arrival-clearing).
        return events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => e.Kind == EventKind.ActionRejected
                      && !IsTransportFailureRejection(e));
    }

    internal static bool HasPickerActivityStartedSince(EventStream events, long sequenceFloor)
    {
        // Slice W.1 (#86) / Slice W.3 (#88) — picker did something
        // since our last look that the LLM needs to weigh in on
        // BEFORE the next dispatch. Two cases:
        //
        // - PickerActivityStarted: picker switched its auto-driven
        //   target. The bot will walk toward it; the LLM must get
        //   a chance to confirm/override before arrival ends in a
        //   verb dispatch.
        // - PickerArrivedNoAction: picker walked to its target
        //   without an LLM verb goal. The motor parked the bot
        //   and sent NO opcode. The LLM has a narrow ~2s window
        //   to name a verb before the picker moves on to the
        //   next candidate. This MUST punch through coalesce.
        //
        // Both bypass the normal MinCallInterval coalesce gate.
        // Function name kept for binary-compat with W.1; semantics
        // are now "any picker-steering event since the floor".
        return events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => e.Kind is EventKind.PickerActivityStarted
                              or EventKind.PickerArrivedNoAction);
    }

    internal static bool IsPickerKind(EventKind kind) =>
        kind is EventKind.PickerActivityStarted
             or EventKind.PickerArrivedNoAction;

    // Call-volume reduction: PickerArrivedNoAction since the floor. This
    // is the safety valve (the bot is parked next to a target with no
    // verb), so it always wakes the LLM — it is NOT subject to the
    // picker-start coalesce. Split out from the picker-start path so the
    // two wakeups can be gated independently.
    internal static bool HasPickerArrivedSince(EventStream events, long sequenceFloor)
    {
        return events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => e.Kind == EventKind.PickerArrivedNoAction);
    }

    // Call-volume reduction: target key of the NEWEST PickerActivityStarted
    // since the floor, or null if there is none. The key identifies the
    // picker's auto-driven target so a rapid sequence of starts toward the
    // SAME target can be coalesced. GUID-first (the stable identity); name
    // is a fallback; a start with neither falls back to its own sequence
    // number so it is always treated as a distinct (new) target and never
    // wrongly suppressed. No game knowledge — the key is an opaque identity
    // token, never interpreted as what the target IS in-game.
    internal static string? NewestPickerStartTargetKeySince(EventStream events, long sequenceFloor)
    {
        foreach (var e in events.Recent().TakeWhile(ev => ev.Sequence >= sequenceFloor))
        {
            if (e.Kind != EventKind.PickerActivityStarted) continue;
            if (e.ItemGuid is uint g && g != 0) return $"0x{g:X8}";
            if (!string.IsNullOrWhiteSpace(e.Name)) return $"name:{e.Name}";
            return $"seq:{e.Sequence}";
        }
        return null;
    }

    // Wake the LLM for a picker-start iff it is a NEW target (different
    // from the last picker-start that woke us) OR enough time has elapsed
    // since that wake (PickerStartCoalesce). Same target within the window
    // is suppressed. Keyed on separate state so it never disturbs the
    // sticky/external-event floor.
    private bool ShouldWakeForPickerStart(string key, DateTimeOffset nowUtc)
        => !string.Equals(key, _lastPickerStartWakeKey, StringComparison.Ordinal)
           || nowUtc - _lastPickerStartWakeAtUtc >= PickerStartCoalesce;

    private bool HasNewSalientEvent(EventStream events)
    {
        // Anything since our last look that's of a salient kind.
        return events.Recent()
            .TakeWhile(e => e.Sequence >= _lastEventConsideredSequence)
            .Any(e => IsSalientKind(e.Kind));
    }

    // Call-volume reduction: salient events EXCLUDING the two picker
    // kinds. The picker wakeups are gated separately (arrival always
    // wakes; start is coalesced/deduped), so the generic salient check
    // must not double-count them — otherwise a suppressed picker-start
    // would still wake the LLM via the wider salient path.
    private bool HasNewNonPickerSalientEvent(EventStream events)
    {
        return events.Recent()
            .TakeWhile(e => e.Sequence >= _lastEventConsideredSequence)
            .Any(e => IsSalientKind(e.Kind) && !IsPickerKind(e.Kind));
    }

    // Call-volume reduction: EXTERNAL change events EXCLUDING the two
    // picker kinds. Used by the picker-start suppression guard (current-goal
    // path) AND the sticky-objective gate (aimless path, which additionally
    // consults pickerArrived / pickerStartWake) to prove it is safe to
    // advance the event floor past consumed picker-start noise.
    // IsExternalChangeKind is a SUPERSET of (salient && !goal-lifecycle) —
    // critically it also includes InventoryItemRemoved, which is external
    // but NOT salient (a completed Give removes an item, often with no
    // accompanying NpcDialog). Guarding on this prevents advancing the
    // floor past — and thereby hiding from the sticky-objective gate — a
    // real InventoryItemRemoved that merely happens to share the window
    // with picker-start noise.
    private bool HasNewNonPickerExternalEvent(EventStream events)
    {
        return events.Recent()
            .TakeWhile(e => e.Sequence >= _lastEventConsideredSequence)
            .Any(e => IsExternalChangeKind(e.Kind) && !IsPickerKind(e.Kind));
    }

    internal static bool IsExternalChangeKind(EventKind kind) =>
        (IsSalientKind(kind) && !IsGoalLifecycleKind(kind))
        || kind is EventKind.InventoryItemRemoved;

    internal static bool IsGoalLifecycleKind(EventKind kind) =>
        kind is EventKind.GoalCompleted
             or EventKind.GoalFailed
             or EventKind.GoalExpired;

    // Durable landblock-dwell bookkeeping. Called at the top of every
    // ProposeGoal tick. Stamps _dwellEntryUtc whenever the observed
    // self-landblock changes, on first observation, or when a session
    // gap (disconnect/reconnect) is detected. Mechanical only — keyed on
    // a wire-derived landblock id, no game knowledge.
    private void UpdateDwellTracking(uint? currentLandblock, EventStream events, DateTimeOffset nowUtc)
    {
        // Unknown landblock this tick (pre-enter-world, or a disconnect/
        // reconnect gap): keep the prior stamp AND do NOT advance the tick
        // clock. That way a reconnect's idle gap accumulates against the
        // last KNOWN-landblock tick, so re-entering the same landblock
        // after a reconnect still trips the session-resume reseed below
        // even when null ticks keep firing during the reconnect. The
        // prompt falls back to event-window rendering when we pass null.
        if (currentLandblock is null)
            return;

        var sessionResumed = _lastProposeTickUtc != DateTimeOffset.MinValue
            && (nowUtc - _lastProposeTickUtc) > DwellSessionGap;
        _lastProposeTickUtc = nowUtc;

        if (sessionResumed || _dwellLandblock is null || _dwellLandblock != currentLandblock)
        {
            _dwellLandblock = currentLandblock;
            // Anchor the entry time. Prefer an in-window LandblockChanged
            // whose DESTINATION is this landblock (accurate when the
            // transition was actually observed); else fall back to now
            // (first observation / login-placement, which emits no
            // LandblockChanged — the exact case the old event-window-only
            // logic mis-rendered). On a session resume (disconnect gap) we
            // ignore any pre-disconnect event and use now, so a reconnect
            // into the SAME landblock does not inherit a stale dwell.
            var entry = nowUtc;
            if (!sessionResumed)
            {
                var match = events.Recent(EventStream.DefaultCapacity)
                    .FirstOrDefault(e => e.Kind == EventKind.LandblockChanged
                        && e.LandblockTo == currentLandblock);
                if (match is not null && match.Utc < entry)
                    entry = match.Utc;
            }
            _dwellEntryUtc = entry;
        }
    }

    // The durable entry time to hand the prompt builder, but ONLY when it
    // corresponds to the landblock the bot is observed in THIS tick.
    // Otherwise null → the builder uses its event-window fallback.
    private DateTimeOffset? DwellEntryForPrompt(uint? currentLandblock)
        => (currentLandblock is uint lb && _dwellLandblock == lb)
            ? _dwellEntryUtc
            : (DateTimeOffset?)null;

    internal static string BuildUserPrompt(WorldStateProjection world, EventStream events, Goal? currentGoal)
        => BuildUserPrompt(world, events, currentGoal, stack: null, pickerActivity: null, explorationCandidates: null);

    internal static string BuildUserPrompt(WorldStateProjection world, EventStream events, Goal? currentGoal, IntentStack? stack)
        => BuildUserPrompt(world, events, currentGoal, stack, pickerActivity: null, explorationCandidates: null);

    internal static string BuildUserPrompt(
        WorldStateProjection world,
        EventStream events,
        Goal? currentGoal,
        IntentStack? stack,
        PickerActivity? pickerActivity)
        => BuildUserPrompt(world, events, currentGoal, stack, pickerActivity, explorationCandidates: null);

    internal static string BuildUserPrompt(
        WorldStateProjection world,
        EventStream events,
        Goal? currentGoal,
        IntentStack? stack,
        PickerActivity? pickerActivity,
        IReadOnlyList<ExplorationCandidate>? explorationCandidates,
        DateTimeOffset? dwellEntryUtc = null)
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
        sb.AppendLine("- Reason ONLY from the observed world below. Do NOT invent NPCs, items, or wcids not listed. Prefer NAME selectors over wcid (wcids change between sessions).");
        sb.AppendLine("- If an inventory item's short_desc says what to do with it, follow it. 'Give' requires BOTH target (the NPC) and item (the thing given).");
        sb.AppendLine("- `ActionRejected` = the server refused that exact (kind, target, item). Do NOT immediately retry the same combo; read its `label`/`message`, then pick a different verb, item, or NPC. TWO+ rejections of the same target+item (any verb) = BLOCKED (unmet prerequisite). Items whose `short_desc` says 'double-click', 'read', or 'activate' must be Use'd on yourself FIRST (target = your own name from `## Self`) before related Give/Talk unlock — prefer `Use{target: name=\"<your-name>\", item: name=\"<that item>\"}` over retrying a blocked combo.");
        sb.AppendLine("- Read `## Server hints`: phrases like \"Double click X\" or \"Use X to ...\" tell you the exact verb+target. If that object is visible AND the server instructed it, emit `Use{target: name=\"X\"}`. The server is your tutorial; don't ignore it for pure exploration.");
        sb.AppendLine("- Combat targets: `monster`-tagged creatures are valid combat targets (grant XP + loot); `npc`-tagged are civilians — talk/trade, do NOT attack. Combat is the primary XP source outside NPC quests.");
        sb.AppendLine("- LEVELING is core progress — be PROACTIVE, not reactive. When combat-ready (a weapon shows `wielded@` in `## Inventory`) AND not mid an explicit server/quest directive: if a `monster` is in view, `Attack` it (per COMBAT SAFETY below); if NO `monster` is in view, do NOT loiter among town `npc`s once their dialog is exhausted — emit `Explore{target: {name: \"anywhere\"}}` toward open areas where monsters live. Do not wait to be attacked first.");
        sb.AppendLine("- COMBAT SAFETY & PACE: fight roughly one `monster` at a time — if several cluster or more than one is `HOSTILE`, back off and pull them singly. Danger signals you have: your `deaths` count and, when shown, `health` in `## Self` (monster levels are NOT given — judge from OUTCOMES, not numbers). If `deaths` rises or `health` is low, DISENGAGE (emit `Explore` to break away) and AVOID re-attacking the same KIND of monster that just defeated you (pick a different/more distant target). Explicit server/quest directives and looting fresh corpses outrank optional combat; don't grind one spot forever.");
        sb.AppendLine("- Looting: a dead monster becomes a `corpse` (a container that DECAYS). `Use{target: name=\"<corpse>\"}` to open, then `Pickup{target: name=\"<item>\"}` items that appear. NEVER skip a fresh corpse to chase the next NPC.");
        sb.AppendLine("- Loot containers: `chest`-tagged openables (Container + Openable, don't decay). `Use` to open, then `Pickup` contents. NEVER skip an unopened chest to chase the next NPC.");
        sb.AppendLine("- Writables: a `sign` (stuck) is read in place with `Use{target: name=\"<sign>\"}`; a `book` (not stuck) is `Pickup`-able — prefer Pickup.");
        sb.AppendLine("- LOOP-BREAK — do not repeat an action that produced no change (see the `Location & recency` section): (a) Talk: if you emitted `Talk{X}` 3+ times in the last 10 emissions with no new item and no new server hint, talk to a different NPC or Use/Give/Explore. (b) inventory-USE: if `Recently used inventory items` lists an item as `still in inventory (not consumed)`, the policy WILL drop a Use against it — do not re-emit unless a new event (ActionRejected recovery hint, new dialog/hint, inventory change) justifies it; when broken, pick a DIFFERENT action (a `monster` in view + weapon wielded → `Attack`; a not-yet-talked visible NPC → `Talk`; a visible pickup item → `Pickup`; else `Explore`). (c) world-object USE: the `Location & recency` section lists `recent Use emissions` per target; 3+ Uses of the SAME target with no change (same landblock per `minutes in current landblock`, no new hint/item) is a dead end → `Explore{target: {name: \"anywhere\"}}` or pick a different target. Re-Use ONLY if something concrete changed (you crossed into a new landblock, a new hint/item appeared, or an `ActionRejected` told you to retry).");
        sb.AppendLine("- PASSAGE-OPENED is not progress: opening a `door` (or any non-container `openable`) does NOT move you. Only MOVING to a new area counts (current cell/landblock changing, or previously-unseen objects in `Visible nearby`). After Using a door once, do NOT Use it again from the same spot — emit `Explore{target: {name: \"anywhere\"}}` (or a goal beyond it) to travel THROUGH it. (Does NOT apply to `chest`/`corpse` containers, which you Use to reveal loot then `Pickup`.)");
        sb.AppendLine("- LOOP-BREAK (town-stuck): if `minutes in current landblock` > 5 AND `nearest monster: (none in view)` AND every visible creature is `npc` (no `monster` tag anywhere), you are STUCK in a town — emit `Explore{target: {name: \"anywhere\"}}` immediately. The picker walks you through visible Doors/portals to new areas. This OVERRIDES Talk/Give even when a new NPC is visible — talk-to-every-NPC is not progress with no monsters in view.");
        sb.AppendLine("- HUNT EXCURSION (leave a tapped-out safe zone to find monsters): monsters do NOT spawn in safe zones — you must travel OUT to surrounding open country. When combat-ready, NO `monster` anywhere in `Visible nearby`, NO un-acted server/quest directive naming a specific next target (re-talking an NPC with no NEW dialog and browsing vendors do NOT count), AND `minutes in current landblock` is more than a few with local progress dried up (no new level, quest item, or unique hint), the zone is TAPPED OUT. Emit `Explore{target: {name: \"anywhere\"}}` — crossing out takes MANY ticks, so KEEP emitting it every cycle (your own recent `Explore` does NOT mean the excursion is done; do NOT revert to talking the same town NPCs mid-excursion) until your `landblock` actually changes OR a `monster` appears (then `Attack` it). A NEW server/quest directive, quest item, danger, or fresh dialog step interrupts the hunt — act on it. Quest progress outranks an optional hunt.");
        sb.AppendLine("- BLOCKED targets: `ActionRejected` label `Blocked`/`Unreachable` = server physics held the bot against geometry (wall, closed door, barrier). Do NOT re-emit the same target. Prefer a visible Door (walk to / Use it — it likely leads where you were going); else `Explore` to route around. The bot cannot clip through obstacles.");
        if (stack is not null)
        {
            sb.AppendLine("- STRATEGIC STACK: `## Intent stack` is the current plan; TOP is the active sub-goal, ancestors paused. Per-cycle goals advance TOP. PUSH on a discovered sub-task; POP_TOP when done and no predicate caught it (rare — predicates auto-pop); REPLACE_TOP when right-frame-wrong-target; MARK_TOP_BLOCKED when stuck. Always echo `stack_revision`.");
            sb.AppendLine("- COMPLETION PREDICATES: pick the typed predicate matching your termination criterion; prefer server-authoritative (num_deaths, coin_value). *_total_* for absolute thresholds, *_since_push_* for deltas. If none fits, {\"$type\":\"never\"} + `predicate_request`.");
        }
        sb.AppendLine("- AUTONOMOUS PICKER: when `## Autonomous picker activity` is present, the schema-only picker auto-drives WHERE TO WALK (nearest eligible candidate by distance) because you had no goal that tick; it OWNS NO VERBS. On arrival the motor sends NOTHING unless your Goal's Kind names a verb (`Use`/`Talk`/`Pickup`/`Attack`/`Give`). `picker has ARRIVED at target X` = parked next to X awaiting a verb — emit `Use`/`Talk`/`Pickup`/`Attack{target: name=\"X\"}`, or `Explore{target: name=\"<other>\"}` to redirect. Doing nothing parks ~2s then picks the next candidate.");
        sb.AppendLine("- TRANSITIONS — doors and portals: `door`/`portal`-tagged objects are activated with `Use{target: name=\"<name>\"}` (the picker never auto-opens them). When parked at a door/portal with no better verb, `Use` it — that's how the bot moves between rooms/buildings/landblocks. If a door rejects Use as Locked and you hold an item whose `short_desc`/name says key, retry `Use{target: name=\"<door>\", item: name=\"<key>\"}`.");
        sb.AppendLine("- EXPLORATION CANDIDATES: when `## Exploration candidates` is present, the in-range queue is empty and the fallback walks to the nearest off-screen object; the TOP entry is the default. To pick a DIFFERENT one (e.g. backtrack through a visited door, or skip a distant pickup for a closer visited NPC), emit `Explore{target: {guid: \"0x...\"}}` (guid is the most reliable selector) or `{name: \"...\"}`.");
        sb.AppendLine("- PURSUE UNSEEN OBJECTIVES: when dialog or a hint tells you to find/reach/talk-to someone NOT in `Visible nearby` (e.g. \"talk to the trainer in the next room\", \"find the captain\"), emit a goal NAMING it — `Talk`/`Give`/`Explore{target: {name: \"<role-or-name>\"}}` — even though it is not yet visible; the bot walks through rooms to discover it. A role phrase (\"the guard\", \"the trainer\") is a valid target name when no proper name is given. Do NOT keep re-talking an NPC whose dialog you already got — pursue the objective that dialog gave you. With no named objective and nothing useful visible, emit `Explore{target: {name: \"anywhere\"}}`.");
        sb.AppendLine("- SERVER-INSTRUCTION PRECEDENCE: `## Server hints` text that tells you how to LEAVE, EXIT, PROCEED PAST, or ADVANCE BEYOND the area — especially naming a person/place or warning the step is irreversible — OUTRANKS repeating a local interaction you already observed (re-picking an item you hold, re-talking an NPC who gave no new dialog, re-using an object that didn't change). When such an instruction is present and unacted, emit a `Talk`/`Use`/`Explore` toward the named target (even if not visible) INSTEAD of looping completed steps.");
        sb.AppendLine("- FINISH MULTI-STEP DIRECTIVES: if you hold an item the server gave you for an unfinished objective (\"take this and bring it back\", \"give X to Y\", \"use this to leave\"), completing it OUTRANKS incidental looting/exploration — return to the NAMED npc/object and `Give`/`Use` it. Treat an unused objective item as an open task, not as done.");
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

        if (pickerActivity is not null)
        {
            // Slice V (#86): parallel surface to the strategic
            // Intent stack — exposes what the schema-only picker is
            // auto-driving so the LLM can see and steer.
            // Slice W.3 (#88): when the picker has WALKED to its
            // target and no verb goal was in flight, Arrived=true
            // and the prompt switches to an "AWAITING VERB" form
            // making explicit that the bot is parked and the LLM
            // must name a verb (Use/Talk/Pickup/Attack/Explore) to
            // either act or release the parking.
            sb.AppendLine("## Autonomous picker activity");
            if (pickerActivity.Arrived)
            {
                sb.AppendLine(
                    $"- picker has ARRIVED at target 0x{pickerActivity.TargetGuid:X8} " +
                    $"(\"{pickerActivity.TargetName}\") and is awaiting a verb");
            }
            else
            {
                sb.AppendLine(
                    $"- picker is investigating target 0x{pickerActivity.TargetGuid:X8} " +
                    $"(\"{pickerActivity.TargetName}\")");
            }
            sb.AppendLine($"- source: {pickerActivity.Source}");
            sb.AppendLine($"- reason: {pickerActivity.Reason}");
            var ageS = Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - pickerActivity.StartedAtUtc).TotalSeconds));
            sb.AppendLine($"- started: {ageS}s ago");
            if (pickerActivity.Arrived)
            {
                sb.AppendLine(
                    "- NOTE: the motor has NOT sent any opcode on arrival. The picker " +
                    "owns WHERE TO STAND; the LLM owns WHAT TO DO. Emit `Use{target: ...}`, " +
                    "`Talk{target: ...}`, `Pickup{target: ...}`, or `Attack{target: ...}` " +
                    "against this target (or any visible alternative) to act. If you do " +
                    "nothing, the picker will park here for ~2 seconds and then move on " +
                    "to the next mechanically-nearest candidate.");
            }
            else
            {
                sb.AppendLine(
                    "- NOTE: this is the bot's autonomous fallback because you had no per-cycle goal " +
                    "at that moment. Emit a goal to take control; the picker will defer.");
            }
            sb.AppendLine();
        }

        if (explorationCandidates is not null && explorationCandidates.Count > 0)
        {
            // Slice W.2 (#87): the fallback picker's candidate set
            // surfaced to the LLM. Listed nearest-first; the picker
            // will walk to the top entry unless the LLM emits an
            // Explore goal naming a different one. Visited
            // candidates are flagged so the LLM can deliberately
            // backtrack (the picker no longer auto-backtracks).
            sb.AppendLine("## Exploration candidates (off-screen known objects in current landblock)");
            foreach (var c in explorationCandidates)
            {
                var vis = c.Visited ? " VISITED" : "";
                sb.AppendLine(
                    $"- 0x{c.Guid:X8} \"{c.Name}\" dist={c.Distance:F1}u cell=0x{c.CellId:X8}{vis}");
            }
            sb.AppendLine(
                "- NOTE: the in-range queue is empty. The fallback picker will walk to the TOP " +
                "entry above by mechanical distance. To pick a different one, emit " +
                "`Explore{target: {guid: \"0x...\"}}` (most reliable) or `Explore{target: {name: \"...\"}}`. " +
                "Visited candidates are legitimate Explore targets when you want to backtrack.");
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

        // 2026-05-30 — Inventory-USE recency surface. Renders
        // recent EventKind.InventoryItemUsed events so the LLM can
        // see "you already used this N times" and avoid re-emitting
        // the same Use against a non-consumable. Without this, the
        // policy-side dedup drops the goal but the LLM keeps
        // generating it (wasting calls + crowding out other action
        // emission). The "still in inventory" marker tells the LLM
        // the item wasn't consumed, so re-using it is unlikely to
        // produce a different outcome.
        var recentInvUses = events.Recent(64)
            .Where(e => e.Kind == EventKind.InventoryItemUsed)
            .GroupBy(e => e.Name ?? $"wcid={e.Wcid}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Name = g.Key,
                Wcid = g.First().Wcid,
                Count = g.Count(),
                LastSeq = g.Max(e => e.Sequence),
            })
            .OrderByDescending(x => x.LastSeq)
            .Take(5)
            .ToList();
        if (recentInvUses.Count > 0)
        {
            sb.AppendLine("## Recently used inventory items");
            foreach (var u in recentInvUses)
            {
                var stillHeld = world.Inventory.Any(i =>
                    (u.Wcid is uint uw && i.Wcid == uw) ||
                    string.Equals(i.Name, u.Name, StringComparison.OrdinalIgnoreCase));
                var heldStr = stillHeld
                    ? "still in inventory (not consumed)"
                    : "no longer in inventory";
                var wcidStr = u.Wcid is uint w2 ? $" wcid={w2}" : "";
                sb.AppendLine($"- {u.Name}{wcidStr}: used x{u.Count} recently — {heldStr}");
            }
            sb.AppendLine(
                "- NOTE: re-using an item that is still in your inventory unchanged " +
                "is unlikely to produce a different outcome. The policy will drop " +
                "repeat Use goals against any item listed above. Pick a different " +
                "action — e.g. Talk/Give/Pickup/Attack — unless a new event " +
                "(rejection, NPC dialog, server hint, inventory change) gives you a " +
                "concrete reason to retry.");
            sb.AppendLine();
        }

        sb.AppendLine("## Visible nearby");
        AppendVisibleNearby(sb, world.Visible);
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
        if (dwellEntryUtc is DateTimeOffset entryUtc)
        {
            // Durable signal: time since the observed self-landblock last
            // changed, independent of whether a LandblockChanged event is
            // still retained in the event window. Clamp against backward
            // clock adjustments so the LLM never sees a negative dwell.
            var dwellMin = Math.Max(0.0, (DateTimeOffset.UtcNow - entryUtc).TotalMinutes);
            sb.AppendLine($"- minutes in current landblock: {dwellMin:F1}");
        }
        else
        {
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
        // Per-target recent Use emissions (last 10 GoalEmitted events
        // of kind Use). Mirrors the Talk-count surface so the LLM can
        // see when it is re-Using the SAME world object (e.g. a door
        // that opens but never transports it). The key is the verbatim
        // target-selector substring the bot itself emitted (guid and/or
        // name), so two Uses of the same object collapse to one count
        // and two distinct objects stay separate. No server text is
        // parsed and no object-type knowledge is used — this is the
        // bot's own emission history, counted by structure only.
        var useCountByTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Use ", StringComparison.Ordinal)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(txt, "target=(.*?) item=.*? source=");
            if (!m.Success) continue;
            var sel = m.Groups[1].Value.Trim();
            if (sel.Length == 0 || sel == "<empty>") continue;
            // Canonicalize to a stable identity so the same object
            // collapses to one count even when emitted with different
            // selector detail (guid+name one tick, name-only the next).
            // Prefer the guid token, else the name token, else the whole
            // selector. Mechanical structural parse of the bot's own
            // emission — no object-type knowledge.
            var gm = System.Text.RegularExpressions.Regex.Match(sel, "guid=0x[0-9A-Fa-f]+");
            var nm = System.Text.RegularExpressions.Regex.Match(sel, "name=\"[^\"]*\"");
            var key = gm.Success ? gm.Value : (nm.Success ? nm.Value : sel);
            useCountByTarget[key] = useCountByTarget.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        if (useCountByTarget.Count > 0)
        {
            sb.AppendLine("- recent Use emissions (last 10 goals):");
            foreach (var kv in useCountByTarget.OrderByDescending(p => p.Value))
            {
                sb.AppendLine($"    - {kv.Key}: x{kv.Value}");
            }
        }
        else
        {
            sb.AppendLine("- recent Use emissions: (none)");
        }
        sb.AppendLine();

        // Pull ServerMessage + NpcDialog + PopupString from the full
        // retained window (capacity 256). These are high-signal cues
        // ("Double click the lifestone", "give the token back to leave")
        // that get pushed out of the generic Recent events tail fast.
        // Without this section ambient NPC chatter can fully evict a
        // one-time tutorial/exit directive within ~25 events.
        //
        // Each surface keeps both the earliest and newest distinct
        // entries (see RetainEnds): early one-time directives are durable
        // anchors that must outlive later chatter, while the newest give
        // current context. Per-surface budgets:
        //   - ServerMessage: 4 earliest + 8 newest
        //   - NpcDialog:      4 earliest + 6 newest
        //   - PopupString:    6 earliest + 6 newest
        // Dedup exact repeats so the same banner doesn't waste tokens.
        // For every durable hint surface we keep BOTH ends of the history
        // (earliest + newest distinct), content-blind, via RetainEnds:
        //   - the earliest-seen distinct entries are durable anchors —
        //     one-time directives ("go talk to X to leave; give the token
        //     back; you can never return") arrive early and must NOT be
        //     evicted by a later flood of similar events before the bot
        //     acts on them;
        //   - the newest distinct entries give current context.
        // Selection is purely by event age (Sequence), never by parsing
        // event TEXT (which would be hardcoded game knowledge).
        var hintPool = events.Recent(EventStream.DefaultCapacity);
        var serverHints = RetainEnds(
            hintPool
                .Where(e => e.Kind == EventKind.ServerMessage && !string.IsNullOrEmpty(e.Text))
                .GroupBy(e => (e.ChatType, e.Text))
                .Select(g => g.First())  // newest occurrence of each unique line
                .ToList(),
            earliest: 4, newest: 8);
        var npcHints = RetainEnds(
            hintPool
                .Where(e => e.Kind == EventKind.NpcDialog && !string.IsNullOrEmpty(e.Text))
                .GroupBy(e => (e.Name, e.Text))
                .Select(g => g.First())
                .ToList(),
            earliest: 4, newest: 6);
        var popupHints = RetainEnds(
            hintPool
                .Where(e => e.Kind == EventKind.PopupString && !string.IsNullOrEmpty(e.Text))
                .GroupBy(e => e.Text)
                .Select(g => g.First())
                .ToList(),
            earliest: 6, newest: 6);
        if (serverHints.Count > 0 || npcHints.Count > 0 || popupHints.Count > 0)
        {
            sb.AppendLine("## Server hints (recent — text the server sent you, dedupe'd)");
            foreach (var h in serverHints)
                sb.AppendLine($"- ServerMessage[chat=0x{h.ChatType ?? 0:X}]: \"{Truncate(h.Text, 320)}\"");
            foreach (var h in npcHints)
                sb.AppendLine($"- NpcDialog from=\"{h.Name}\": \"{Truncate(h.Text, 320)}\"");
            foreach (var h in popupHints)
                sb.AppendLine($"- PopupString: \"{Truncate(h.Text, 320)}\"");
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

    /// <summary>
    /// Keep both ends of an already-deduped hint list: the N earliest and
    /// the M newest distinct entries (by Sequence), unioned and returned
    /// oldest-first. This keeps an early one-time directive (e.g. an exit
    /// instruction) from being evicted by a later flood of similar events,
    /// while still surfacing the most recent context. Selection is purely
    /// by event age — it never inspects event text — so it introduces no
    /// game knowledge.
    /// </summary>
    private static List<StreamEvent> RetainEnds(List<StreamEvent> distinct, int earliest, int newest)
    {
        if (distinct.Count <= earliest + newest)
            return distinct.OrderBy(e => e.Sequence).ToList();
        var head = distinct.OrderBy(e => e.Sequence).Take(earliest);
        var tail = distinct.OrderByDescending(e => e.Sequence).Take(newest);
        return head.Concat(tail)
            .GroupBy(e => e.Sequence)
            .Select(g => g.First())
            .OrderBy(e => e.Sequence)
            .ToList();
    }

    // Renders the `## Visible nearby` body. In object-dense areas (towns
    // with 100+ visible objects) an uncapped listing pushes the whole
    // prompt past some models' request-size limit (HTTP 413). To stay
    // within budget we keep every higher-information row — any object whose
    // wire-derived projection carries an affordance/state tag (creature/
    // portal/door/corpse/chest/book/sign/lifestone/vendor/healer/openable/
    // hostile) — and truncate only "plain" rows that expose nothing but
    // name/wcid/distance, taking the NEAREST plain rows first (distance is
    // geometric, not game knowledge). This is an information-budget
    // decision, NOT a strategy decision: no per-type priority is assigned
    // beyond "has any tag" vs "plain". The authoritative monster-presence
    // signal the rules rely on lives in `## Combat readiness` (computed
    // directly from world.Visible), not in this rendered text.
    private const int VisiblePlainSoftCap = 50;
    private const int VisibleSectionCharBudget = 10000;
    // Headroom reserved (out of the budget) for the two omission-summary
    // lines so the whole section stays <= VisibleSectionCharBudget even after
    // those lines are appended. Generous: at most ~12 "kind=NNNNNNN" pairs.
    private const int VisibleSummaryReserve = 400;
    // Hard clamp on a single rendered row so one pathological object name can
    // never blow the budget (the always-emit-first-tagged-row guarantee).
    private const int VisibleRowMaxChars = 400;

    private static string ClampRow(string row) =>
        row.Length <= VisibleRowMaxChars
            ? row
            : row.Substring(0, VisibleRowMaxChars - 1) + "\u2026";

    private static bool IsTaggedVisible(VisibleObjectProjection v) =>
        v.IsCreature || v.IsPortal || v.IsDoor || v.IsCorpse || v.IsChest
        || v.IsBook || v.IsSign || v.IsLifestone || v.IsVendor || v.IsHealer
        || v.IsOpenable || v.ObservedHostile;

    private static string RenderVisibleRow(VisibleObjectProjection v)
    {
        var sb = new StringBuilder();
        sb.Append($"- {v.Name}");
        if (v.Wcid is uint vw) sb.Append($" (wcid={vw}");
        else sb.Append(" (");
        if (v.IsCreature)
        {
            // Slice H — discriminate combat targets from civilians.
            // `monster` = server-flagged Attackable AND no custom radar
            // blip color AND not Vendor/Healer. `npc` = any other
            // creature. Both signals come from the wire; we never
            // hardcode wcid lists or English-name matches.
            if (v.IsMonster) sb.Append(" monster");
            else             sb.Append(" npc");
        }
        if (v.IsPortal)   sb.Append(" portal");
        if (v.IsDoor)     sb.Append(" door");
        if (v.IsCorpse)   sb.Append(" corpse");
        if (v.IsChest)    sb.Append(" chest");
        if (v.IsBook)     sb.Append(" book");
        if (v.IsSign)     sb.Append(" sign");
        if (v.IsLifestone) sb.Append(" lifestone");
        if (v.IsVendor)   sb.Append(" vendor");
        if (v.IsHealer)   sb.Append(" healer");
        if (v.IsOpenable) sb.Append(" openable");
        if (v.ObservedHostile) sb.Append(" HOSTILE");
        if (v.Distance is float d) sb.Append($" d={d:F1}");
        sb.Append(')');
        return sb.ToString();
    }

    private static string SummarizeOmittedTags(IReadOnlyList<VisibleObjectProjection> omitted)
    {
        int monster = 0, npc = 0, portal = 0, door = 0, corpse = 0, chest = 0,
            book = 0, sign = 0, lifestone = 0, vendor = 0, healer = 0, openable = 0;
        foreach (var v in omitted)
        {
            if (v.IsCreature) { if (v.IsMonster) monster++; else npc++; }
            if (v.IsPortal) portal++;
            if (v.IsDoor) door++;
            if (v.IsCorpse) corpse++;
            if (v.IsChest) chest++;
            if (v.IsBook) book++;
            if (v.IsSign) sign++;
            if (v.IsLifestone) lifestone++;
            if (v.IsVendor) vendor++;
            if (v.IsHealer) healer++;
            if (v.IsOpenable) openable++;
        }
        var parts = new List<string>();
        void Add(string k, int n) { if (n > 0) parts.Add($"{k}={n}"); }
        Add("monster", monster); Add("npc", npc); Add("portal", portal);
        Add("door", door); Add("corpse", corpse); Add("chest", chest);
        Add("book", book); Add("sign", sign); Add("lifestone", lifestone);
        Add("vendor", vendor); Add("healer", healer); Add("openable", openable);
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    internal static void AppendVisibleNearby(StringBuilder sb, IReadOnlyList<VisibleObjectProjection> visible)
    {
        if (visible.Count == 0) { sb.AppendLine("- (nothing)"); return; }

        var tagged = visible.Where(IsTaggedVisible)
            .OrderBy(v => v.Distance ?? float.MaxValue).ToList();
        var plain = visible.Where(v => !IsTaggedVisible(v))
            .OrderBy(v => v.Distance ?? float.MaxValue).ToList();

        // Rows must fit within the budget minus the summary headroom so the
        // closing omission-summary lines never push the section over budget.
        int rowBudget = VisibleSectionCharBudget - VisibleSummaryReserve;
        int chars = 0;
        int taggedShown = 0;
        foreach (var v in tagged)
        {
            var row = ClampRow(RenderVisibleRow(v));
            int cost = row.Length + 1; // include the newline AppendLine adds
            // Always render at least one tagged row (clamped); otherwise stop
            // once the next row would exceed the row budget so the section
            // stays bounded even when tagged objects alone are numerous.
            if (taggedShown > 0 && chars + cost > rowBudget) break;
            sb.AppendLine(row);
            chars += cost;
            taggedShown++;
        }
        if (taggedShown < tagged.Count)
        {
            var omitted = tagged.Skip(taggedShown).ToList();
            sb.AppendLine($"- (+{omitted.Count} more tagged objects not shown due to prompt budget: {SummarizeOmittedTags(omitted)})");
        }

        int plainShown = 0;
        foreach (var v in plain)
        {
            if (plainShown >= VisiblePlainSoftCap) break;
            var row = ClampRow(RenderVisibleRow(v));
            int cost = row.Length + 1;
            if (chars + cost > rowBudget) break;
            sb.AppendLine(row);
            chars += cost;
            plainShown++;
        }
        if (plainShown < plain.Count)
        {
            sb.AppendLine($"- (+{plain.Count - plainShown} more distant plain objects not shown)");
        }
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
