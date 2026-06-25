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
using HeadlessAcClient.World;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Render-ready snapshot of the bot-to-target distance trend for the active
/// goal's pursued object. Pure own-geometry bookkeeping the prompt surfaces so
/// the LLM can judge whether an unproductive pursuit is worth continuing.
/// </summary>
internal sealed record GoalProgressSnapshot(
    string TargetLabel,
    IReadOnlyList<float> Distances,
    double SpanSeconds);

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
    /// avoid bursting the LLM with quick-fire popups. Default 2s; a long
    /// unattended run can RAISE it via AC_BOTS_MIN_CALL_INTERVAL_SECONDS to
    /// stretch a daily per-model LLM quota across more wall-clock hours — the
    /// bot keeps executing its current goal during the coalesce defer, so this
    /// throttles call RATE without slowing action execution. The standing
    /// reduce-llm-call-volume goal. ~10s is a reasonable unattended value; the
    /// value is capped at StuckTimeout (a longer window would suppress the
    /// stuck-timer re-deliberation backstop, and salient/picker/stuck wakes still
    /// bound the effective delay). 0 disables the throttle (tests set Zero).
    /// </summary>
    public TimeSpan MinCallInterval { get; init; } =
        ResolveMinCallInterval(Environment.GetEnvironmentVariable("AC_BOTS_MIN_CALL_INTERVAL_SECONDS"));

    // Parse the AC_BOTS_MIN_CALL_INTERVAL_SECONDS override for MinCallInterval. A
    // non-negative integer is used (clamped to the StuckTimeout ceiling so the
    // coalesce window can never outlast the stuck-timer re-deliberation backstop);
    // anything else (unset, blank, unparseable, negative) falls back to the 2s
    // default. Request-RATE management keyed to the per-model daily LLM quota, NOT
    // strategy or game knowledge.
    internal static TimeSpan ResolveMinCallInterval(string? envValue)
    {
        const int DefaultSeconds = 2;
        if (int.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0)
            return TimeSpan.FromSeconds(Math.Min(v, StuckTimeoutSeconds));
        return TimeSpan.FromSeconds(DefaultSeconds);
    }

    // Floor (in unspent XP) for rendering the `## Unspent XP` prompt capsule, read
    // once at type-load from AC_BOTS_MIN_RAISE_XP. Default 0 renders the capsule for
    // any positive unspent (byte-identical prior behavior). Pure runtime config.
    internal static readonly long MinMeaningfulUnspentXp =
        ResolveMinMeaningfulUnspentXp(Environment.GetEnvironmentVariable("AC_BOTS_MIN_RAISE_XP"));

    // Parse AC_BOTS_MIN_RAISE_XP. A non-negative integer is used (clamped to a sane
    // ceiling); anything else (unset/blank/unparseable/negative) falls back to 0.
    internal static long ResolveMinMeaningfulUnspentXp(string? envValue)
    {
        const long Default = 0;
        const long Max = 1_000_000;
        if (long.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0)
            return Math.Min(v, Max);
        return Default;
    }

    // Recency window (seconds) for the survivability-spend salience cue: it fires only
    // when the bot's most-recent in-session death was within this many seconds (it is
    // ACTIVELY dying, matching the SPEND XP rule's "dying fast" current bottleneck) — NOT
    // for a single stale death from earlier this session, since secondsSinceLastDeath
    // stays non-null all session once any death is observed. Mechanical timer; no game
    // knowledge.
    private const int RecentDeathSalienceWindowSeconds = 300;

    // Returns true when unspent XP is positive and at or above the configured floor.
    // At the default floor (0) this is exactly the prior `unspent > 0` check.
    internal static bool ShouldSurfaceUnspentXp(long unspent, long minMeaningful)
        => unspent > 0 && unspent >= minMeaningful;

    /// <summary>
    /// Wall-clock "stuck" timer: if no event arrives and no goal
    /// completes within this, re-deliberate.
    /// </summary>
    public TimeSpan StuckTimeout { get; init; } = TimeSpan.FromSeconds(StuckTimeoutSeconds);

    // The stuck-timer backstop in seconds. Shared so the MinCallInterval ceiling
    // (ResolveMinCallInterval) can cap at it: a coalesce window longer than the
    // stuck timer would suppress the re-deliberation the stuck timer guarantees.
    internal const int StuckTimeoutSeconds = 30;

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

    // Town-stuck hunt-egress enforcement (mechanical LLM-COMPLIANCE
    // backstop). The LOOP-BREAK(town-stuck) + HUNT EXCURSION + PERSIST
    // prompt rules (BuildUserPrompt RULES block) tell the LLM to leave a
    // tapped-out, monster-free safe zone once dwell exceeds a few minutes,
    // but the model observably IGNORES them and keeps Talking town NPCs
    // forever (live Diseng62715: stayed in 0xA9B4 the whole ~19min run,
    // 291 positions all in one landblock, 0 Explore, dwell well past 5min).
    // When the bot is demonstrably stuck we MECHANICALLY substitute a
    // targetless Explore so the existing OutdoorFrontierExplorer drives
    // egress. This is enforcement of an existing prompt directive, NOT new
    // strategy: a targetless Explore interacts with no LLM-unrequested
    // object, and every gate is a typed wire affordance or a timer (no NPC
    // names, wcids, landblock ids, or English-dialog parsing). Mirrors the
    // accepted server-side recovery-tick pattern (ac-ai-players#110).
    private const double EgressDwellMinutes = 5.0;
    private static readonly TimeSpan EgressNoProgressGrace = TimeSpan.FromMinutes(2);
    // Seam-independent barren-stall first-trigger. The dwellMinutes trigger is
    // per-landblock and resets at every seam, so a bot oscillating between two
    // adjacent safe landblocks never accumulates the threshold in either and
    // egress never engages. sinceMaterialProgress does NOT reset at seams (only
    // on real own-progress: an inventory delta or a level gain), so this trigger
    // catches a bot stalled across a small landblock cluster regardless of how
    // it split its time. 2x the dwell threshold: even a perfectly even split
    // across two landblocks accumulates here before either hits the per-landblock
    // threshold, while a productive hunt resets it on every loot/level.
    private static readonly TimeSpan BarrenStallTimeout =
        TimeSpan.FromMinutes(2 * EgressDwellMinutes);
    // Fresh-directive egress veto window. A low-level bot still being actively
    // guided by the server (a NEW, distinct tutorial/instruction PopupString it
    // has not yet acted on) is making progress even with no inventory/level
    // delta — finishing available guided training grants outsized early rewards
    // (XP, skill credits, gear) and outranks an optional hunt. While a fresh
    // distinct directive is within this window the egress latch is vetoed
    // (same tier as a visible monster). BOUNDED so it can NEVER deadlock: only
    // server-pushed PopupStrings count (low-volume, directed-at-self, NOT the
    // per-NPC dialog the town-stuck loop is built from, and never self-driven
    // by the bot's own Talk loop), only DISTINCT text resets it (a repeated
    // popup does not), and the veto ages out FreshDirectiveGrace after the last
    // distinct directive — so once training stops instructing, egress proceeds.
    // Enforcement-only: mechanically mirrors the LLM-facing HUNT EXCURSION
    // prompt rule ("Quest progress outranks an optional hunt … a NEW
    // server/quest directive … interrupts the hunt") in BuildUserPrompt; if
    // that bullet is removed, revisit this veto so it stays prompt-anchored.
    private static readonly TimeSpan FreshDirectiveGrace = TimeSpan.FromMinutes(2);

    // Debounce for the `## Unseen objective target` salience capsule: only flag
    // an active objective whose named target has never been observed AFTER it has
    // been pursued at least this long, so a just-pushed objective whose target the
    // bot is still travelling toward (its room not yet loaded) is not flagged on
    // the very first tick. Temporal scoping (a settle window), not a behavioural
    // threshold; the bot re-deliberates every few seconds, so this is a few
    // decisions of grace before the never-observed fact surfaces.
    private static readonly TimeSpan UnseenObjectiveTargetGrace = TimeSpan.FromSeconds(20);
    // Liveness backstop for the monster-in-view egress veto. Once the bot is
    // tapped out, a NON-HOSTILE monster KIND that stays visible-but-unengaged
    // (the bot keeps choosing overridable social/stationary goals instead of
    // attacking it or leaving) for this long no longer counts as a reason to
    // stay. This is the ONLY signal — source assigns the kind no value/danger
    // label; an ObservedHostile attacker is NEVER ignored, and the override it
    // unblocks can only redirect a social/stationary verb (Attack/Pickup/
    // Explore/transit-Use/corpse-loot stay preserved), so a false positive
    // costs at most one redirected Talk. Mirrors the per-dwell killed-kind
    // exclusion (IsFarmedHere). Without it, a single passive attackable
    // creature (which legitimately passes IsMonster) pins a tapped-out bot in
    // a starter zone forever via the monsterInView veto.
    private static readonly TimeSpan IgnoredKindExposureTimeout =
        TimeSpan.FromMinutes(EgressDwellMinutes);
    private static readonly IReadOnlySet<string> EmptyKindSet =
        new HashSet<string>();
    // Wielded weapon bits that count as "combat-ready" for egress: Melee
    // (0x1) | Missile (0x100) | Caster (0x8000). A bot with no weapon is
    // not yet ready to hunt, so it keeps its full town grace.
    private const uint EgressWieldedWeaponMask =
        ItemTypeMasks.MeleeWeapon | 0x100u | 0x8000u;
    private int _egressLastInventoryCount = -1;
    // Wall-clock of the bot's last MATERIAL own-progress: an inventory-count
    // delta OR a level gain. Deliberately seam-independent (a landblock change
    // is NOT progress). Feeds sinceMaterialProgress for both the no-progress
    // grace and the seam-independent barren-stall trigger.
    private DateTimeOffset _egressLastProgressUtc = DateTimeOffset.MinValue;
    // Last observed self-level, for detecting a level gain as own-progress
    // (resets the no-progress clock so an XP-only productive hunt is not
    // flagged barren-stalled). Own data only — audit-safe.
    private int? _egressLastObservedLevel;
    // Own-death recency for the prompt. The server-tracked NumDeaths is a
    // CUMULATIVE count (persists across sessions), so it cannot tell the LLM
    // whether a death just happened or was long ago. These track the wall-clock
    // of the most recent IN-SESSION death increment so the prompt can render
    // recency telemetry the LLM uses to decide whether to head back out and
    // resume hunting. First observation only anchors the count (a pre-existing
    // total is NOT a fresh death); only an increment stamps the clock. Own
    // outcome + a timer — no game content. Mirrors _egressLastObservedLevel.
    private int? _lastObservedDeaths;
    private DateTimeOffset? _lastOwnDeathUtc;
    // Rolling window of the bot's OWN death timestamps (one per observed death
    // increment), used to compute the recent death-RATE for death-spiral
    // detection. Pruned to DeathSpiralWindow in RecentOwnDeathCount. Own outcome
    // + timers — no game content.
    private readonly List<DateTimeOffset> _ownDeathTimesUtc = new();
    private readonly HashSet<uint> _talkedNpcGuids = new();
    private readonly HashSet<string> _talkedNpcNames = new(StringComparer.OrdinalIgnoreCase);

    // Current-goal progress telemetry (cp-2300). Samples the bot-to-target
    // distance of the ACTIVE goal's resolved target across recent ProposeGoal
    // ticks so the prompt can surface a raw distance trend ("41u -> 45u over
    // 12s"). Pure own-bookkeeping: it tracks the geometry of a pursuit the LLM
    // already chose, never decides anything. The LLM reads the trend and
    // decides whether to continue the pursuit — source renders only numbers,
    // no judgment, no object-type knowledge.
    // _goalProgressKey is the tracked Selector identity (resets the buffer when
    // the goal target changes); _goalProgressGuid locks onto one matched object
    // so multiple same-name objects cannot cross-contaminate the trend.
    private string? _goalProgressKey;
    private uint? _goalProgressGuid;
    private string? _goalProgressLabel;
    private DateTimeOffset _goalProgressLastSampleUtc;
    private readonly System.Collections.Generic.List<(DateTimeOffset Utc, float Distance)> _goalProgressSamples = new();
    private static readonly TimeSpan GoalProgressMinSampleInterval = TimeSpan.FromSeconds(1.5);
    private const int GoalProgressMaxSamples = 8;

    // Fresh-directive tracking (paired with FreshDirectiveGrace above).
    // _egressLastDirectiveSeqSeen is the high-water event sequence already
    // examined (so each tick scans only NEW events — idempotent + cheap).
    // _egressLastCreditedDirectiveText is the last popup text that reset the
    // grace, used to reject a REPEATED identical popup (the anti-idle loop).
    // _egressLastFreshDirectiveUtc is the wall-clock of the last DISTINCT
    // directive; the veto holds while now - it < FreshDirectiveGrace. Own
    // observation only (typed event kind + freshness) — audit-safe.
    private long _egressLastDirectiveSeqSeen = -1;
    private string? _egressLastCreditedDirectiveText;
    private DateTimeOffset _egressLastFreshDirectiveUtc = DateTimeOffset.MinValue;
    // Sticky egress latch. Once egress triggers we keep it engaged across
    // landblock seams until a monster appears, the bot disarms, or it makes
    // material progress. Without this latch the override would drop the tick
    // after the bot crosses a seam (dwell resets to 0), reverting to Talk and
    // pathing back — an infinite ping-pong between two adjacent safe zones.
    private bool _isEgressing;
    // Per monster-KIND wall-clock of when it first became CONTINUOUSLY visible
    // while the bot was tapped out and choosing an overridable (non-engaging,
    // non-leaving) goal. A kind whose continuous eligible exposure passes
    // IgnoredKindExposureTimeout joins the per-dwell "ignored" set so it stops
    // vetoing egress (see ComputeEffectiveMonsterInView). Reset per kind on PVS
    // absence, on the LLM engaging the kind (Attack), and whenever the bot is
    // not tapped-out-and-ignoring; cleared wholesale on a landblock change
    // (the notion is per-dwell, like the killed set). Own observed behavior
    // only — audit-safe.
    private readonly Dictionary<string, DateTimeOffset> _ignoredKindFirstEligibleUtc = new();
    private uint? _ignoredExposureLandblock;
    // Kinds already logged as "ignored" this dwell, so the observability line
    // fires once per kind per dwell (cleared with the tracker on a landblock
    // change). Pure logging bookkeeping.
    private readonly HashSet<string> _loggedIgnoredKinds = new();
    // The bot's OWN self-level captured when it entered the current dwell
    // landblock (snapshotted in UpdateDwellTracking; lazily filled if level
    // was not yet observed at entry). Compared against the current level to
    // surface a "hunt tapped out" perception fact: combat-ready + dwelled
    // past the threshold + no level gained here = this area no longer levels
    // the bot, so it should travel for tougher monsters. Own-progress signal
    // only — no monster type/level judgement, audit-safe.
    private int? _levelAtCurrentLandblockEntry;

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

    // Diagnostic-only tempo meter (reduce-llm-call-volume). Counts the LLM
    // round-trips spent per kill so the per-monster call cost is measurable.
    // Pure observability — never consulted by any decision. See LlmTempoMeter.
    private readonly LlmTempoMeter _tempo = new();

    // SOURCE RE-DRIVE of an LLM-authored exploration commitment
    // (execution persistence — NOT game knowledge). When a single LLM
    // response BOTH pushes a new strategic intent that becomes TOP AND
    // returns an inert `Explore` tactical goal, we record the pushed
    // intent's id + that exact Explore goal. While that EXACT LLM-authored
    // intent remains TOP and uncompleted, the policy re-drives that EXACT
    // LLM-authored Explore each tick WITHOUT re-consulting the LLM — so an
    // LLM that commits to (e.g.) a hunt excursion is not pulled off it by
    // every tick's re-deliberation. The intent's OWN typed completion
    // predicate (LLM-authored: e.g. a monster comes into view) and/or its
    // deadline end the commitment; source adds only bookkeeping. Source
    // NEVER inspects the intent's kind/name, nor any NPC/monster/town/wcid
    // knowledge. Cleared on ANY other LLM response (a fresh decision
    // supersedes), on a mechanical plan-invalidating break, or on the
    // liveness budget. Gated to Explore because an "anywhere" Explore is
    // motor-owned and non-interactive — re-driving an interactive verb
    // (Talk/Pickup/...) through changing events could repeat-interact a
    // stale/unasked target.
    private string? _redriveIntentId;
    private Goal? _redriveGoal;
    private int _redriveReinstalls;

    /// <summary>
    /// Liveness backstop for source re-drive: max times the stored Explore
    /// is reinstalled (each reinstall == the prior Explore cleared) before
    /// forcing a real LLM re-think, even if the intent has not completed.
    /// Game-agnostic bookkeeping. The LLM-authored deadline is the primary
    /// terminator; this bounds a wedged/very-long-deadline excursion.
    /// </summary>
    public int MaxRedriveReinstalls { get; init; } = 12;

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

    // World-object USE loop-break state (2026-06-04). A weak model can
    // loop Use{same world object} when the Use SUCCEEDS (e.g. a door
    // opens) but yields no progress the bot can act on — classically an
    // indoor door the motor opens but cannot path the bot THROUGH to the
    // adjacent cell. Such a Use is neither ActionRejected (it succeeded)
    // nor an inventory item, so IsGoalRecentlyRejected and
    // IsInventoryUseRecentlyDispatched both miss it. This holds the
    // identity + self position of the last accepted world-object Use so a
    // STATIONARY repeat (same target, bot has not moved, nothing entered
    // or left inventory) can be detected and dropped. Pure mechanical
    // bookkeeping over the bot's OWN emission key + its OWN self cell/
    // position + inventory-change events — no object-type knowledge.
    private WorldUseRepeat? _lastWorldUseRepeat;

    private sealed record WorldUseRepeat(
        string Key, uint? Landblock, uint? Cell, float X, float Y, long SequenceFloor, int Count);

    // Tunables for the stationary world-object USE loop-break.
    private const float StationaryUseEpsilon = 0.75f;
    private const float StationaryUseEpsilonSq = StationaryUseEpsilon * StationaryUseEpsilon;
    private const int StationaryUseRepeatThreshold = 3;

    // ── Landblock-scoped world-object USE churn loop-break (cp-2354) ──────
    // The stationary guard above resets on ANY movement, so it MISSES a TOUR
    // of several doors/openables within ONE landblock: the bot walks between
    // the doors (cell/position changes) and re-Uses them, never egressing,
    // while each per-target stationary streak resets on the move. This episode
    // keys on the LANDBLOCK (not cell/position) and counts per-target bare-Use
    // emissions ACROSS intervening moves; it resets ONLY when the landblock
    // changes (genuine egress) or inventory changes (a productive key-Use or
    // loot). A never-before-Used target gets first-use forgiveness (count 1),
    // so a legitimate sequence of DISTINCT doors toward an exit is never
    // broken; only a RE-Used target reaching the threshold LATCHES as
    // suppressed for the rest of the episode (so a picker re-arrival + LLM
    // re-emit cannot leak one Use per cycle). Pure mechanical loop bookkeeping
    // over the bot's OWN emission identity + its OWN self landblock +
    // inventory events; landblock is decoded coordinate state, not a named
    // zone — no object-type or game knowledge.
    private WorldUseChurnEpisode? _worldUseChurnEpisode;

    private sealed class WorldUseChurnEpisode
    {
        public required uint? Landblock { get; init; }
        public long FloorSequence;
        public readonly Dictionary<string, int> UseCounts = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Suppressed = new(StringComparer.OrdinalIgnoreCase);
        // cp-2359: latched once the episode has Used too MANY DISTINCT world
        // objects in this landblock without egress or a productive inventory
        // change — a barren TOUR of different objects (which the per-target
        // Suppressed set above cannot catch). While latched, any bare
        // world-object Use is deferred to Explore until the episode resets.
        public bool DistinctChurnLatched;

        // cp-2371: a per-key Use count that SURVIVES the inventory-change reset
        // (it is carried over when the episode is re-created within the SAME
        // landblock) and resets ONLY on a landblock change. The episode-level
        // UseCounts above reset on any inventory change so a productive loot
        // forgives the DISTINCT tour (cp-2359) — but that let a bot evade the
        // per-target loop-break by interleaving an UNRELATED productive Pickup
        // between repeats of the SAME barren Use (e.g. Use door, Use door,
        // Pickup item, repeat): the door count reset before reaching the
        // threshold. Re-Using the SAME object many times is barren regardless of
        // unrelated inventory gains, so this cumulative count latches it.
        public Dictionary<string, int> PersistentUseCounts = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PersistentSuppressed = new(StringComparer.OrdinalIgnoreCase);
    }

    // Fire on the Nth bare-Use emission against the SAME world-object identity
    // within one landblock episode (across intervening moves). 3 mirrors the
    // StationaryUseRepeatThreshold / MultiNpcTalkChurnStaleThreshold: the LLM
    // has chosen the same object three times without the landblock changing or
    // inventory advancing — strong loop evidence.
    private const int LandblockWorldUseChurnThreshold = 3;

    // cp-2371: latch a SINGLE world-object identity re-Used this many times in
    // one landblock CUMULATIVELY — i.e. counting across the inventory-change
    // resets that clear the per-episode UseCounts. Higher than the per-episode
    // threshold (3) so a couple of legitimate retries interleaved with real
    // loot never trip it, but a genuine barren same-object loop (which can hide
    // behind unrelated Pickups) still latches. Resets only on a landblock change.
    private const int PersistentWorldUseChurnThreshold = 5;

    // cp-2359: fire once the episode has Used this many DISTINCT world objects
    // in one landblock with NO egress and NO productive inventory change — a
    // barren tour of different objects (e.g. working every container/door in a
    // safe zone) that the per-target threshold above never catches because each
    // object is Used only once or twice. Higher than the per-target threshold:
    // touring a few distinct objects is plausibly legitimate, but the episode
    // resets the instant an inventory change (a productive loot/key-Use) or a
    // landblock change occurs, so only an UNPRODUCTIVE tour accumulates this far.
    private const int LandblockDistinctUseChurnThreshold = 5;

    // Stationary NPC Talk loop-break (2026-06-05). A weak model loops
    // Talk{same NPC} on a dead-end NPC whose quest it cannot satisfy
    // (e.g. "bring me a frost infusion"); the server re-emits the same
    // canned dialog on every Talk, so neither the inventory-USE dedup nor
    // the world-object USE dedup catch it, and the advisory recent-Talk
    // COUNT surface is ignored. This holds the identity + self position of
    // the last accepted Talk so a STATIONARY repeat (same NPC, bot has not
    // moved, nothing entered or left inventory) can be detected and
    // dropped. Pure mechanical bookkeeping over the bot's OWN Talk emission
    // key + its OWN self cell/position + inventory-change events — it parses
    // NO server dialog text and encodes no NPC knowledge (mirrors
    // IsStationaryWorldUseRepeat). Movement or an inventory change (a real
    // multi-step turn-in) resets the streak, so genuine progress is never
    // suppressed.
    private NpcTalkRepeat? _lastNpcTalkRepeat;

    private sealed record NpcTalkRepeat(
        string Key, uint? Landblock, uint? Cell, float X, float Y,
        long SequenceFloor, int Count);

    // Talk gets slightly more loop-break tolerance than Use (4 vs 3): an
    // initial Talk commonly yields a multi-message reply before a stationary
    // re-emission can be judged a no-progress loop.
    private const int NpcTalkRepeatThreshold = 4;

    // Movement- AND novelty-independent single-NPC Talk-fixation backstop. The
    // stationary guard (IsExhaustedNpcTalkRepeat) resets on movement, the roving
    // guard (IsRovingNpcTalkLoop) resets its raw streak on ANY inventory /
    // landblock / self-progress event — so a bot that interleaves re-Talking ONE
    // onboarding NPC with incidental combat/loot (each kill = self-progress, each
    // pickup = inventory-added) keeps resetting that streak and loops the NPC
    // indefinitely while the dialog cycles new-looking canned lines. This backstop
    // counts the bot's OWN Talk emissions to one NPC over its last-N emitted GOALS
    // (immune to interleaving, since unrelated goals are simply other entries in
    // the window) and fires when they dominate the window. Threshold sits above
    // the stationary (4) and below the roving raw (8) thresholds; the window
    // mirrors the `## Recent Talk` recency render the LLM already sees ("Talk to X
    // xN in last 10 goals"). Mechanical repeat-count over the bot's own history —
    // no NPC/quest content, no game knowledge.
    private const int SingleNpcTalkHistoryWindowGoals = 10;
    private const int SingleNpcTalkHistoryThreshold = 6;

    // Cross-kind interaction fixation loop-break (2026-06-05). After a kill,
    // a weak model fixates on the resulting EMPTY corpse, ALTERNATING
    // Use{Corpse} and Pickup{Corpse} forever. The per-kind guards above each
    // count only their OWN GoalKind, so the alternation never trips either
    // (Use never reaches 3 consecutive, and Pickup is unguarded). This holds
    // the identity + self position of the last accepted INTERACT goal (Use
    // with no Item, or Pickup) so a STATIONARY repeat ACROSS those kinds
    // (same target, bot has not moved, nothing entered or left inventory) is
    // detected and dropped. Pure mechanical bookkeeping over the bot's OWN
    // emission key + its OWN self cell/position + inventory-change events —
    // it parses NO server text and encodes no object knowledge. A real loot
    // (Slice Q auto-loot / a successful Pickup) changes inventory and resets
    // the streak, so a non-empty corpse is never suppressed; only a
    // zero-progress fixation fires.
    private InteractFixation? _lastInteractFixation;

    private sealed record InteractFixation(
        string Key, uint? Landblock, uint? Cell, float X, float Y,
        long SequenceFloor, int Count);

    // The cross-kind guard catches a mixed-kind alternation the per-kind
    // guards miss; threshold 4 drops the 4th same-target interact in a
    // stationary no-progress streak.
    private const int InteractFixationThreshold = 4;

    // cp-2344 — multi-NPC Talk-churn episode. IsExhaustedNpcTalkRepeat resets
    // its streak whenever the Talk target changes, so it NEVER fires when the
    // bot ALTERNATES Talk between a small set of NPCs (a referral ping-pong),
    // because that guard resets its streak on every target change. This tracks
    // a STATIONARY no-progress Talk EPISODE over a small cyclic target set. It
    // accrues a "stale" streak while the bot stays put, observes NO server
    // progress signal (inventory / landblock / self-progression), AND sees NO
    // dialog NOVELTY (a server text fingerprint it has not seen this episode)
    // between Talk emissions; a productive turn-in, zone change, XP gain, or a
    // genuinely new line of dialog resets it. Pure mechanical loop-detection of
    // the bot's OWN Talk emissions + server-observable progress + runtime text
    // NOVELTY by normalized hash (never the text's meaning) — no NPC names, no
    // quest knowledge. The "cycle period cap" of 2 distinct targets scopes this
    // to the tight alternation the single-NPC guard misses; a larger Talk
    // frontier reads as traversal, not a loop, and abandons the episode.
    private TalkChurnEpisode? _talkChurnEpisode;

    private sealed class TalkChurnEpisode
    {
        public long FloorSequence;
        public uint? Landblock;
        public uint? Cell;
        public float X;
        public float Y;
        public int StaleTalks;
        public readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> SeenDialogFingerprints = new(StringComparer.Ordinal);
    }

    // Fire on the Nth consecutive STALE (no-novelty, no-progress, stationary)
    // Talk in a <=2-target cycle. A legitimate referral chain (A->B->A, or
    // A->B->A->B) produces new dialog/an item/a move that resets the streak
    // well before this, so the 3rd stale repeat over a 2-NPC cycle is a
    // defensible mechanical loop signal — the same shape as the single-NPC
    // NpcTalkRepeatThreshold and the cross-kind InteractFixationThreshold.
    private const int MultiNpcTalkChurnStaleThreshold = 3;
    private const int MultiNpcTalkChurnMaxTargets = 2;

    // cp roving-multi-npc-talk-loop: IsMultiNpcTalkChurn resets on intra-landblock
    // MOVEMENT (cell/position), so a referral PING-PONG between a small set of
    // NPCs at DIFFERENT cells of the SAME landblock resets its episode on every
    // walk and never accumulates (and the roving SINGLE-NPC guard needs one
    // target). This sibling episode counts STALE Talks over a <=MaxTargets cycle
    // WITHOUT the movement reset — resetting only on LEAVING the landblock or
    // server-observable PROGRESS or DIALOG NOVELTY — so an advancing multi-NPC
    // conversation is never suppressed, but a position-independent cycle of <=N
    // exhausted NPCs in one landblock fires. Reuses TalkChurnEpisode (its
    // Cell/X/Y are unused for the reset here).
    private TalkChurnEpisode? _rovingTalkChurnEpisode;
    // Higher than the stationary MultiNpcTalkChurnStaleThreshold (3) and equal to
    // the roving single-NPC threshold: roving is a weaker loop signal than a
    // stationary cycle, so require one more stale repeat before breaking.
    private const int RovingMultiNpcTalkChurnStaleThreshold = 4;

    // cp-2365: roving single-NPC Talk-loop guard. IsExhaustedNpcTalkRepeat
    // resets on MOVEMENT, and IsMultiNpcTalkChurn requires >=2 targets, so a bot
    // that keeps walking up to (or around) the SAME NPC loops past BOTH and
    // never breaks. This sibling counts consecutive STALE Talks to the SAME NPC
    // regardless of position, resetting only on a DIFFERENT NPC or server-
    // observable PROGRESS (new dialog text by hash, inventory, landblock, or
    // self-progression) — so a genuinely advancing single-NPC conversation is
    // never suppressed. Pure mechanical bookkeeping over the bot's OWN Talk
    // emissions + progress-event PRESENCE + dialog-text novelty; no NPC names,
    // wcids, or quest content.
    private RovingNpcTalkLoop? _rovingNpcTalkLoop;

    private sealed class RovingNpcTalkLoop
    {
        public string Key = "";
        public long FloorSequence;
        public int StaleTalks;
        // Raw count of consecutive same-NPC Talks in this streak, regardless of
        // dialog novelty (StaleTalks resets on novelty; this does not). Backstops
        // the stale counter against a varied-but-unproductive cycling-dialog loop.
        public int TotalTalks;
        public readonly HashSet<string> SeenDialogFingerprints = new(StringComparer.Ordinal);
    }

    private const int RovingNpcTalkLoopStaleThreshold = 4;
    // cp-2390-era: raw backstop. The stale counter resets on every NOVEL dialog
    // fingerprint, so an NPC that cycles through many varied canned lines (live:
    // 15x Worcer Talks) keeps StaleTalks below the stale threshold and slips the
    // guard until the slow dwell-egress (~5min). A run of this many CONSECUTIVE
    // same-NPC Talks with NO inventory/landblock/self-progress (those reset the
    // whole streak) is a loop regardless of dialog variety. Higher than the
    // stale threshold so a genuinely long advancing conversation is not cut off.
    private const int RovingNpcTalkLoopRawThreshold = 8;

    // cp-2415: after the roving guard (above) confirms a stale single-NPC Talk
    // loop and egresses, the LLM's persistent intent often re-targets the SAME
    // exhausted NPC on the very next tick, so the bot oscillates straight back
    // (live: one Scribe re-Talked 31x, an earlier NPC 10x — each egress followed
    // by an immediate re-Talk). Record the fired target's resolved-guid key here
    // with a short TTL and DROP further Talks to it until the TTL expires, so the
    // egress STICKS and the bot moves on to new content instead of re-greeting a
    // dead conversation. The TTL re-probes later in case the NPC gains new
    // dialog. Keyed by the SAME resolved guid the roving guard uses. Pure
    // mechanical loop-break bookkeeping from the bot's OWN confirmed no-progress
    // signal — no NPC names, wcids, or game content.
    private readonly Dictionary<string, DateTimeOffset> _talkLoopSuppressedUntil = new(StringComparer.Ordinal);
    private static readonly TimeSpan TalkLoopSuppressionTtl = TimeSpan.FromSeconds(90);

    // Server text kinds that count as "dialog" for novelty fingerprinting.
    // Wire-decoded categories only — no per-message content knowledge.
    private static readonly EventKind[] TalkChurnDialogKinds =
    {
        EventKind.NpcDialog,
        EventKind.ServerMessage,
        EventKind.PopupString,
        EventKind.BookText,
    };

    // Early Talk-loop egress (2026-06-07). A PROVEN stationary NPC Talk
    // fixation (IsExhaustedNpcTalkRepeat — 4 same-NPC Talks with no movement
    // and no inventory change) is time-INDEPENDENT dead-end evidence: re-Talking
    // it can never make progress. The general hunt-egress only breaks such a
    // loop once dwell passes EgressDwellMinutes (5 min), so a bot in a monster-
    // free safe zone wastes ~5 min Talk-looping a silent NPC the picker keeps
    // re-parking it at (live-observed: a Pathwarden Talk-looped ~8x for ~5 min).
    // This breaks the loop the moment the fixation is proven, WITHOUT the dwell/
    // tapped-out gate, as long as no hostile is in view (defend/flee that) and
    // the server is not actively guiding the bot with a fresh directive. A short
    // latch stops the picker re-parking on the same dead NPC the next tick (the
    // first Explore step moves the bot, which resets the per-emission fixation
    // counter, so without the latch the loop would just re-accrue). The "loop
    // kind" tag is the bot's OWN goal verb (Talk), NOT any NPC identity; the
    // substitute is a generic Explore{anywhere}. No NPC names/wcids/priorities —
    // own-signal mechanical loop recovery, audit-safe.
    private const string NpcTalkLoopKind = "NPC Talk";
    // cp-2372: the loopKind tag passed to EscapeOrFallback for a confirmed bare
    // world-object Use churn (stationary repeat OR landblock tour). The bot's
    // OWN goal verb (Use), not any object identity.
    private const string WorldUseLoopKind = "world-object Use";
    private DateTimeOffset _talkLoopEgressUntilUtc = DateTimeOffset.MinValue;
    private uint? _talkLoopEgressLandblock;
    private static readonly TimeSpan TalkLoopEgressDuration = TimeSpan.FromSeconds(90);

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

    // Remembered out-of-view creature sightings surfaced as the
    // "## Recently sighted (out of view)" prompt block. Projected from
    // the bot's own SightedLocation memory and pushed by the driver
    // each tick before ProposeGoal. Null / empty = nothing remembered
    // to surface (e.g. before the first sighting, or all in view).
    private IReadOnlyList<SightedRecallProjection>? _currentRecentSightings;

    /// <summary>
    /// Driver-driven setter for the remembered-sightings recall block.
    /// Called before ProposeGoal with the bot's own out-of-view
    /// creature memory so the LLM can choose to navigate back to a
    /// monster that left its field of view. Null / empty = nothing to
    /// surface.
    /// </summary>
    public void SetRecentSightings(IReadOnlyList<SightedRecallProjection>? sightings)
        => _currentRecentSightings = sightings;

    // cp-2340 — interaction-target guids the server refused as out-of-reach
    // and the Motor's InteractUnreachableTracker is currently suppressing.
    // Surfaced as the "## Server-refused interaction targets" prompt block
    // so the LLM is not blind to which guids the resolver will drop, and
    // stops re-emitting an interaction goal that resolves only to a
    // suppressed target. Pushed by the driver each tick before ProposeGoal.
    // Null / empty = nothing currently suppressed.
    private IReadOnlyList<UnreachableTargetProjection>? _currentUnreachableTargets;

    /// <summary>
    /// Driver-driven setter for the server-refused (out-of-reach)
    /// suppression set. Called before ProposeGoal with the live entries of
    /// the Motor's <c>InteractUnreachableTracker</c> projected to display
    /// names + remaining cooldown. Null / empty = nothing to surface.
    /// </summary>
    public void SetUnreachableTargets(IReadOnlyList<UnreachableTargetProjection>? targets)
        => _currentUnreachableTargets = targets;

    // cp-2342 — recent measured self→target distance history for the
    // interaction target the bot most recently locked a goal on. Surfaced as
    // the "## Approach distance history" prompt block so the LLM can see,
    // across ticks, whether its repeated selections of the SAME target are
    // actually reducing the distance. When repeated locks on one target fail
    // to close the distance, the LLM has no prompt-visible signal of that.
    // Pushed by the driver each tick
    // before ProposeGoal. Null = nothing to surface (no recent fixation, or
    // the target is already within the Motor's arrival radius).
    private ApproachDistanceProjection? _currentApproachDistance;

    /// <summary>
    /// Driver-driven setter for the approach-distance-history block. Called
    /// before ProposeGoal with the most-recently-locked interaction target's
    /// recent distance samples (already gated by the driver on freshness,
    /// sample count, and still-outside-arrival-radius). Null = nothing to
    /// surface.
    /// </summary>
    public void SetApproachDistanceHistory(ApproachDistanceProjection? approach)
        => _currentApproachDistance = approach;

    private ExcursionCoverageProjection? _currentExcursionCoverage;

    /// <summary>
    /// Driver-driven setter for the "## Recent outdoor coverage" capsule.
    /// Called before ProposeGoal with a rolling-window summary of the bot's own
    /// recent outdoor coverage (distinct landblocks visited, net travel vector,
    /// own Mob sightings) when the bot is outdoors with visited-node memory.
    /// Null = nothing to surface. The capsule additionally render-gates on a
    /// recent Explore emission so it does not clutter town/quest/combat prompts.
    /// </summary>
    public void SetExcursionCoverage(ExcursionCoverageProjection? coverage)
        => _currentExcursionCoverage = coverage;

    // loot-fresh-kills (cp-2357): the bot's OWN fresh, unlooted kill corpses
    // (matched by name+recency to a recent kill, not yet opened, within range),
    // surfaced as the "## Fresh kill to loot" capsule so the LLM loots a kill
    // before the hunt-excursion re-drives it away. Null/empty = nothing to surface.
    private IReadOnlyList<FreshKillCorpse>? _currentFreshKillCorpses;

    public void SetFreshKillCorpses(IReadOnlyList<FreshKillCorpse>? corpses)
        => _currentFreshKillCorpses = corpses;

    // loot-fresh-kills follow-up (cp-2358): the bot's OWN kill corpses it opened
    // and the loot system reported empty. Surfaced as the "## Already looted"
    // capsule so the observed empty-loot outcome is available in the prompt.
    // Null/empty = nothing to surface.
    private IReadOnlyList<LootedCorpse>? _currentLootedEmptyCorpses;

    public void SetLootedEmptyCorpses(IReadOnlyList<LootedCorpse>? corpses)
        => _currentLootedEmptyCorpses = corpses;

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

    /// <summary>UtcNow when the current <see cref="_inflight"/> call was kicked
    /// off, or null when none is in flight. Drives the stuck-call watchdog: the
    /// <see cref="LlmCallTimeout"/> CTS inside RunAsync is the PRIMARY guard, but
    /// a hang its cancellation does not unwind (observed live: a kickoff that
    /// never returned left <c>_inflight</c> non-null and the bot stopped
    /// deliberating for many minutes) needs a poll-side backstop that does NOT
    /// depend on the call honoring cancellation. Cleared whenever
    /// <c>_inflight</c> is cleared.</summary>
    private DateTimeOffset? _inflightStartedAt;

    /// <summary>Hard wall on a single in-flight LLM call as seen by the
    /// deliberation poll. Set well ABOVE <see cref="LlmCallTimeout"/> (60s) so it
    /// only fires when that primary guard failed to unwind the call; the poll
    /// then abandons the orphaned task and re-deliberates so the bot can never
    /// wedge on a hung call. Pure infrastructure recovery — no game knowledge.</summary>
    private static readonly TimeSpan InflightHardWall = TimeSpan.FromSeconds(120);

    /// <summary>How many in-flight LLM calls the watchdog has abandoned this
    /// session. Telemetry only: a non-zero (and especially a climbing) count flags
    /// a misbehaving endpoint that ignores the call-timeout, since each abandoned
    /// call leaves an orphaned request running until it finally returns. A late
    /// orphan outcome can still nudge <see cref="LlmGoalClient"/> model-selection
    /// state (cooldown / infra-breaker) — bounded, transient, and self-corrected
    /// by the periodic primary re-probe — so the count is the early-warning signal
    /// if abandonment ever stops being rare.</summary>
    private int _inflightAbandonedCount;

    /// <summary>Poll-side watchdog decision: the current in-flight LLM call has
    /// outlived the hard wall, so the primary call-timeout CTS did not unwind it.
    /// Pure time comparison; the caller abandons the orphaned task and
    /// re-deliberates. Exposed for unit testing the recovery boundary.</summary>
    internal static bool IsInflightStuck(DateTimeOffset? startedAt, DateTimeOffset now, TimeSpan hardWall) =>
        startedAt is { } s && now - s >= hardWall;

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
        // Enforce durable goal-history (emission/failure) retention against the CURRENT
        // time at the single decision entry, BEFORE any durable-history read this tick
        // (the run-summary diagnostic below, ProposeGoalCore's loop-break/fixation reads,
        // and the hunt-egress override). Append-time pruning alone would leave stale
        // entries alive on a wall-clock stuck-timeout re-deliberation that re-reads history
        // with no intervening append.
        events.PruneGoalHistory(DateTimeOffset.UtcNow);
        // One run-summary emit per tick at most: the time-based fallback (below) and
        // the every-15-kickoffs trigger (in ProposeGoalCore) can both come due on the
        // same tick; this flag, reset each tick, lets the kickoff path skip a
        // duplicate when the time-based path already emitted.
        _summaryEmittedThisTick = false;
        // Diagnostic tempo: detect new kills from the bot's OWN combat-feel
        // total and, when it rises, log the LLM round-trips spent since the
        // prior kill. Read-only; never affects the goal returned below. Anchor
        // only on a NON-NULL snapshot: CombatHistoryFull is published at init
        // (after the persisted ledger loads) so it is normally present from the
        // first tick, but skipping the null case keeps the baseline from
        // anchoring at 0 and then reporting a persisted total as a spurious
        // burst if the publish ever lands after the first ProposeGoal.
        if (world.CombatHistoryFull is { } tempoHist
            && _tempo.ObserveTotalKills(tempoHist.Sum(h => (long)h.Kills), DateTimeOffset.UtcNow) is string tempoLine)
            Console.WriteLine($"[tempo] {tempoLine}");
        // Time-based [run-summary] fallback: the primary trigger fires every
        // SummaryIntervalDecisions LLM kickoffs, but under sustained 429 the bot
        // rarely kicks off, so without this a struggling run emits almost no
        // summaries. Emitting at least every SummaryMaxIntervalSeconds keeps the run
        // self-reporting (explored landblocks, level, active model) even while the
        // LLM is walled. Pure observability; never affects the goal returned.
        if (ShouldEmitTimeBasedSummary(_lastSummaryEmitAtUtc, DateTimeOffset.UtcNow))
            EmitRunSummary(world, events);
        return ApplyHuntEgressOverride(ProposeGoalCore(world, events, currentGoal), world, events);
    }

    // Mechanical hunt-egress enforcement. Applied to EVERY goal this policy
    // would return (LLM-accepted, fallback, held, or re-driven) so it also
    // covers the 429/backoff/parse-fail paths that defer to the fallback —
    // which itself Talks town NPCs before its Explore default
    // (NoQuestKnowledgePolicy step 6 vs step 7). See the field-doc above for
    // the architecture rationale. Counter-free + time-based so it is
    // idempotent per tick: a held social goal that stays stuck is overridden
    // every tick (driving continuous egress) with no double-counting.
    private Goal? ApplyHuntEgressOverride(Goal? goal, WorldStateProjection world, EventStream events)
    {
        if (goal is null) return goal;

        var nowUtc = DateTimeOffset.UtcNow;
        var lb = world.Self.Landblock;

        // Track MATERIAL progress: an inventory delta (received/spent a
        // quest item, looted) OR a level gain stamps a fresh "last progress"
        // time. Repeated identical NPC dialog is NOT progress (that is the loop
        // we break) — so we deliberately do NOT look at dialog/hint events here.
        // A landblock change is deliberately NOT treated as progress: crossing
        // an invisible spatial seam into another safe zone must NOT reset the
        // clock, or the bot ping-pongs between adjacent town landblocks
        // (each crossing granting a fresh grace). The sticky _isEgressing
        // latch (below) carries egress through seams, and the seam-independent
        // barren-stall trigger ENGAGES it even when the per-landblock dwell
        // keeps resetting.
        var invCount = world.Inventory.Count;
        if (_egressLastInventoryCount < 0)
            // First observation this session: anchor the no-progress clock to
            // now. The field default (MinValue) would otherwise read as years
            // and fire the barren-stall trigger on the very first tick.
            _egressLastProgressUtc = nowUtc;
        else if (invCount != _egressLastInventoryCount)
            _egressLastProgressUtc = nowUtc;
        _egressLastInventoryCount = invCount;
        // A level gain is the purest own-progress signal: a productive hunt
        // that yields XP-only progression (no inventory delta) must also reset
        // the no-progress clock so it is not flagged barren-stalled mid-hunt.
        if (world.Self.Level is int egLvlNow)
        {
            if (_egressLastObservedLevel is int egPrevLvl && egLvlNow > egPrevLvl)
                _egressLastProgressUtc = nowUtc;
            _egressLastObservedLevel = egLvlNow;
        }

        // Early Talk-loop egress sustain: while the talk-loop latch is active
        // (still in the same landblock, no monster in view, no fresh server
        // directive), keep substituting the social dwell-extending verbs the LLM
        // loops on (Talk/Give) with Explore so the bot actually walks OUT instead
        // of the picker re-parking it on the dead NPC every tick. The latch
        // self-clears on a landblock change (loop broken — bot left), an
        // attackable monster appearing (engage it), a fresh directive, or timeout.
        // Non-social verbs (the bot's own Explore/Pickup/Attack) pass through and
        // themselves break the loop.
        if (IsTalkLoopEgressActive(
                nowUtc, _talkLoopEgressUntilUtc, _talkLoopEgressLandblock, lb,
                AnyAttackableMonsterInView(world), RecentFreshDirective(events, nowUtc)))
        {
            if (IsEgressOverridableVerb(goal.Kind))
            {
                Console.WriteLine(
                    $"[llm-override] talk-loop egress (sustained): substituting {goal.Kind} " +
                    $"target={goal.Target} with Explore{{anywhere}} until the bot leaves the loop.");
                return MakeEgressExploreGoal(
                    nowUtc, "override:talk-loop-egress",
                    "mechanical talk-loop egress (sustained): still in the dead-end " +
                    "conversation landblock; leaving to break the loop");
            }
        }
        else if (_talkLoopEgressLandblock is not null)
        {
            // Latch lapsed (timed out, left the landblock, hostile / directive
            // appeared) — clear the recorded landblock so it cannot match later.
            _talkLoopEgressLandblock = null;
        }

        var dwellEntry = DwellEntryForPrompt(lb);
        var dwellMin = dwellEntry is DateTimeOffset de
            ? Math.Max(0.0, (nowUtc - de).TotalMinutes)
            : 0.0;

        var combatReady = world.Inventory.Any(i =>
            i.WieldedAt is uint w && w != 0 &&
            i.ItemType is uint it && (it & EgressWieldedWeaponMask) != 0);

        // "Tapped out" = combat-ready, dwelled past the threshold here, and
        // gained 0 levels since arriving (the bot's OWN raw self-progress
        // signal — see HuntTappedOutFact). Only when tapped out does an
        // already-farmed-here kind stop counting as a reason to stay.
        var tappedOut = HuntTappedOutFact(
            combatReady, world.Self.Level, _levelAtCurrentLandblockEntry,
            dwellMin, EgressDwellMinutes) is not null;

        // A visible monster keeps the bot here (cancels egress) UNLESS it is
        // a kind the bot has already farmed in THIS landblock while tapped
        // out (and is not currently attacking the bot). A first-time/unknown
        // kind or any ObservedHostile attacker still counts — the bot stays
        // and the LLM engages it. This is the only change to the egress
        // trigger; ComputeEgressActive itself stays pure.
        //
        // Liveness backstop: also drop a non-hostile kind the bot has kept
        // visible-but-unengaged for a sustained tapped-out window (it keeps
        // choosing overridable goals rather than attacking it or leaving) so a
        // single passive attackable creature cannot pin a tapped-out bot here
        // forever. Reset the tracker on a landblock change (per-dwell, like the
        // killed set); accrue only while tapped out AND the goal this tick is
        // overridable (Talk/Give/Use) — engaging (Attack/Pickup/Wield) or
        // leaving (Explore) is not "ignoring".
        if (_ignoredExposureLandblock != lb)
        {
            _ignoredKindFirstEligibleUtc.Clear();
            _loggedIgnoredKinds.Clear();
            _ignoredExposureLandblock = lb;
        }
        var ignoreEligible = tappedOut && IsEgressIgnoreEligibleGoal(goal.Kind);
        var engagedKinds = goal.Kind == GoalKind.Attack
            ? VisibleKindKeysMatching(goal.Target, world)
            : EmptyKindSet;
        var ignoredKinds = UpdateIgnoredKindExposure(
            _ignoredKindFirstEligibleUtc,
            VisibleMonsterKindKeys(world),
            engagedKinds, ignoreEligible, nowUtc, IgnoredKindExposureTimeout);
        foreach (var ik in ignoredKinds)
            if (_loggedIgnoredKinds.Add(ik))
                // Observability: a non-hostile kind has been visible-but-
                // unengaged past the timeout this dwell, so it no longer vetoes
                // egress. Logged once per kind per dwell (opaque kind-key only).
                Console.WriteLine(
                    $"[llm-override] hunt-egress ignored-kind: '{ik}' visible-but-unengaged " +
                    $">= {IgnoredKindExposureTimeout.TotalMinutes:F0}min while tapped out — " +
                    $"no longer vetoes egress.");

        var effectiveMonsterInView = ComputeEffectiveMonsterInView(
            world.Visible, world.KilledKindsThisDwell, tappedOut, ignoredKinds);

        var sinceProgress = nowUtc - _egressLastProgressUtc;
        // A fresh, distinct server tutorial/instruction directive vetoes egress
        // for a bounded grace (see FreshDirectiveGrace) so a low-level bot
        // finishes available guided training before being forced out to hunt.
        var recentFreshDirective = RecentFreshDirective(events, nowUtc);
        // Update the sticky latch every tick (idempotent). It engages once
        // dwell passes the threshold and DIS-engages the moment a cancel
        // condition holds, regardless of the current landblock.
        var wasEgressing = _isEgressing;
        _isEgressing = ComputeEgressActive(
            _isEgressing, combatReady, effectiveMonsterInView, dwellMin, sinceProgress,
            tappedOut, recentFreshDirective);
        if (_isEgressing && !wasEgressing)
            // Latch just engaged — log once per engagement so the trigger is
            // observable even when the current goal is not overridable (e.g.
            // the bot is looping on a preserved transit Use). dwell vs the
            // seam-independent stall timer tells which first-trigger fired.
            Console.WriteLine(
                $"[llm-override] hunt-egress ENGAGED: dwell={dwellMin:F1}min, " +
                $"no-progress={sinceProgress.TotalMinutes:F1}min, tappedOut={tappedOut}, " +
                $"trigger={(dwellMin >= EgressDwellMinutes ? "dwell" : "barren-stall")}.");
        if (!_isEgressing)
            return goal;

        // Egress is engaged. Substitute the goals that would keep the bot
        // stuck in this tapped-out zone: the social dwell-extending verbs
        // (Talk/Give) the LLM loops on, AND — only when tapped out — an
        // Attack that merely re-kills a kind already farmed here (yields no
        // levels). Everything else passes through untouched: an Explore/Use
        // (door/portal) the LLM itself produced, a Pickup (self-arm), a
        // first-time/unknown-kind Attack, a self-defense Attack on a HOSTILE,
        // and corpse looting (Use/Pickup on a corpse).
        string? overrideReason = null;
        if (IsEgressOverridableVerb(goal.Kind))
            overrideReason = "social-verb";
        else if (goal.Kind == GoalKind.Attack &&
                 IsTappedOutRepeatKillAttack(goal, world, tappedOut))
            overrideReason = "repeat-farm-attack";
        else if (IsEgressOverridableStationaryUse(goal, world))
            overrideReason = "stationary-use";

        if (overrideReason is null)
            return goal;

        Console.WriteLine(
            $"[llm-override] town-stuck hunt-egress: dwell={dwellMin:F1}min, combat-ready, " +
            $"reason={overrideReason}, no productive/hostile monster in view, no inventory " +
            $"progress for {sinceProgress.TotalMinutes:F1}min — substituting {goal.Kind} " +
            $"target={goal.Target} with Explore{{anywhere}} to leave the tapped-out zone.");

        return MakeEgressExploreGoal(
            nowUtc, "override:hunt-egress",
            "mechanical hunt-egress: tapped-out monster-free safe zone, dwell past threshold " +
            "with no material progress; leaving to find monsters (enforces the HUNT EXCURSION rule)");
    }

    // Construct the generic targetless Explore{anywhere} goal that the egress
    // mechanisms substitute to leave a tapped-out zone. Centralized so every
    // egress path mints an identical goal (the picker/frontier explorer drives
    // the actual direction). Pure factory — no game knowledge.
    private static Goal MakeEgressExploreGoal(DateTimeOffset nowUtc, string source, string rationale)
        => new Goal
        {
            Kind = GoalKind.Explore,
            Target = new Selector { Name = "anywhere" },
            Source = source,
            CreatedAtUtc = nowUtc,
            Id = Guid.NewGuid(),
            Priority = 1,
            Rationale = rationale,
        };

    // Pure gate for the stuck-loop egress substitution (below). A combat-ready
    // bot that is ALSO tapped out (dwelled past the threshold here with 0 levels
    // gained) and has NO attackable monster in view has nothing left to gain in
    // this zone, so a proven no-progress interaction loop should send it away
    // rather than to the fallback (which re-picks the same dead-end class of
    // object). When a monster IS in view the egress must defer — the bot should
    // engage the visible XP target, not wander off to "find monsters" it already
    // sees. Extracted for deterministic unit testing. Own signals only — no game
    // content.
    internal static bool ShouldEscapeStuckLoop(
        bool combatReady, bool tappedOut, bool monsterInView)
        => combatReady && tappedOut && !monsterInView;

    // When a fixation guard has detected a proven no-progress interaction loop
    // (a door/forge/chest/NPC re-tried with no movement and no inventory
    // change), substitute a generic Explore so a tapped-out combat-ready bot
    // LEAVES instead of deferring to the fallback — which re-selects the same
    // dead-end class of stationary object, keeping the bot wedged in a town.
    // This enforces the LOOP-BREAK / HUNT EXCURSION prompt rules mechanically
    // when a weak model ignores them, and works INDEPENDENTLY of the egress
    // latch (which a persistent non-hostile creature in PVS can keep suppressed
    // via the monster-in-view cancel). Gated tightly:
    //   - combat-ready (typed wielded weapon) — an UNARMED bot may legitimately
    //     need to Use objects to progress; do not send it wandering.
    //   - tapped out (HuntTappedOutFact: dwelled past EgressDwellMinutes with 0
    //     levels gained since arriving) — early in a zone a Use loop may be a
    //     genuine progress attempt.
    //   - no attackable monster in view — engage a visible monster (defend/flee a
    //     hostile, or fight a non-hostile XP target) instead of wandering off to
    //     find one that is already in view.
    // Own signals only (typed wield, own dwell/level, own monster-in-view
    // perception); the trigger is the already-audited fixation guard. No
    // door/chest/monster-kind knowledge; substitutes a generic Explore.
    private bool ShouldEscapeStuckLoopWithExplore(WorldStateProjection world, DateTimeOffset nowUtc)
    {
        var combatReady = world.Inventory.Any(i =>
            i.WieldedAt is uint w && w != 0 &&
            i.ItemType is uint it && (it & EgressWieldedWeaponMask) != 0);
        var dwellEntry = DwellEntryForPrompt(world.Self.Landblock);
        var dwellMin = dwellEntry is DateTimeOffset de
            ? Math.Max(0.0, (nowUtc - de).TotalMinutes) : 0.0;
        var tappedOut = HuntTappedOutFact(
            combatReady, world.Self.Level, _levelAtCurrentLandblockEntry,
            dwellMin, EgressDwellMinutes) is not null;
        return ShouldEscapeStuckLoop(combatReady, tappedOut, AnyAttackableMonsterInView(world));
    }

    // A fixation guard fired (proven no-progress interaction loop). Either send
    // a tapped-out combat-ready bot away with Explore (stuck-loop egress) or, if
    // not in that state, defer to the fallback as before.
    private Goal? EscapeOrFallback(
        WorldStateProjection world, EventStream events, Goal? currentGoal,
        DateTimeOffset nowUtc, string loopKind, string? loopTargetName = null)
    {
        // Shared egress signals, read ONCE. RecentFreshDirective has per-tick side
        // effects (advances the directive high-water sequence, may log a grace
        // credit), so it is read here exactly once AND only for a Talk loop — the
        // sole egress below that consults it (ShouldEarlyEscapeTalkLoop) is
        // Talk-only, so a Use / interaction / attack drop must not advance the Talk
        // directive grace.
        var monsterInView = AnyAttackableMonsterInView(world);
        var freshDirective = loopKind == NpcTalkLoopKind && RecentFreshDirective(events, nowUtc);

        // Exhausted-NPC break-contact (cp070): when the bot's OWN goal history PROVES
        // a single-NPC Talk fixation — it Talked this one NPC >= the fixation
        // threshold of its last N emitted goals (the cp069 signal, immune to the
        // interleaved combat/loot that resets the stationary and roving guards) — it
        // is provably NOT doing anything productive: not advancing the directive it
        // keeps re-greeting, and not engaging any monster in view (it has chosen Talk
        // over Attack at least threshold times). Break the loop with ONE generic
        // Explore FIRST and UNCONDITIONALLY — NOT gated on a fresh directive (a
        // proven-stale fixation is not finishing guided training) NOR on
        // monster-in-view: the latched Talk egress and the tapped-out stuck-loop
        // egress below are BOTH monster-in-view-gated, so in a zone that always shows
        // a monster (e.g. a training yard full of practice constructs) a proven
        // fixation would otherwise wedge with NO egress able to fire — re-prompting
        // the LLM every tick forever. This egress is UNLATCHED: it relocates the bot
        // one step and RE-DELIBERATES next tick, so a same-landblock next room is not
        // overshot and, if a winnable monster is in view, the LLM can Attack it on
        // the very next decision. Audit-clean: generic Explore{anywhere} — the Motor
        // selects NO target; the LLM picks the next interactable next decision.
        var provenSingleNpcTalkFixation =
            loopTargetName is { Length: > 0 } breakContactNpc
            && CountTalkGoalsToNameInLastN(events, breakContactNpc, SingleNpcTalkHistoryWindowGoals)
                >= SingleNpcTalkHistoryThreshold;
        if (ShouldBreakContactExhaustedNpc(loopKind, provenSingleNpcTalkFixation))
        {
            Console.WriteLine(
                "[llm-override] exhausted-npc break-contact: proven single-NPC Talk fixation " +
                "in recent goal history — one UNLATCHED Explore{anywhere} to break contact and " +
                "re-deliberate with fresh perception (Motor picks no target).");
            return MakeEgressExploreGoal(
                nowUtc, "override:exhausted-npc-breakcontact",
                "mechanical break-contact: proven single-NPC Talk fixation in recent goal " +
                "history; one unlatched Explore to leave the spent conversation and re-deliberate");
        }

        if (ShouldEscapeStuckLoopWithExplore(world, nowUtc))
        {
            Console.WriteLine(
                "[llm-override] stuck-loop egress: tapped-out combat-ready bot looping " +
                $"{loopKind} with no progress and no monster in view — " +
                "substituting Explore{anywhere} to leave the zone.");
            return MakeEgressExploreGoal(
                nowUtc, "override:stuck-loop-egress",
                $"mechanical stuck-loop egress: tapped-out, looping {loopKind} with no " +
                "progress; leaving to find monsters (enforces the LOOP-BREAK rules)");
        }
        // Early Talk-loop egress: a PROVEN stationary NPC Talk fixation is a
        // dead end regardless of dwell time, so break it now (before the 5-min
        // tapped-out gate the general egress needs) unless an attackable monster
        // is in view (engage that XP target) or the server is actively guiding the
        // bot. Latch it briefly so the picker cannot re-park on the same dead NPC
        // next tick.
        if (ShouldEarlyEscapeTalkLoop(
                loopKind, monsterInView, freshDirective))
        {
            _talkLoopEgressUntilUtc = nowUtc + TalkLoopEgressDuration;
            _talkLoopEgressLandblock = world.Self.Landblock;
            Console.WriteLine(
                "[llm-override] talk-loop egress: proven stationary NPC Talk fixation, " +
                "no monster in view — substituting Explore{anywhere} " +
                $"(latched {TalkLoopEgressDuration.TotalSeconds:F0}s) to break the loop.");
            return MakeEgressExploreGoal(
                nowUtc, "override:talk-loop-egress",
                "mechanical talk-loop egress: proven stationary NPC Talk fixation with no " +
                "monster in view; leaving to break the dead-end conversation loop");
        }
        // cp-2372: a confirmed bare world-object Use churn (this method is only
        // reached AFTER the cp-2354 churn guard fired — the bot has re-Used the
        // SAME object, or toured interior objects, with NO egress and NO inventory
        // gain) is PROVEN not-progress. Realise the churn guard's documented
        // intent ("Explore OUT instead of re-touring interior doors") with a
        // generic Explore so the bot travels THROUGH/past the looped object,
        // instead of deferring to the fallback (which just re-picks the same
        // interior objects). Unlike a Talk loop — which may be following a fresh
        // NPC instruction — re-Using one object cannot be "finishing guided
        // training", so this fires even within FreshDirectiveGrace; an attackable
        // monster in view still takes priority (engage the visible XP target —
        // defend/flee a hostile or fight a non-hostile — never wander off to find
        // a monster already in view).
        if (ShouldEscapeWorldUseLoop(loopKind, monsterInView))
        {
            Console.WriteLine(
                "[llm-override] use-loop egress: confirmed world-object Use churn with no " +
                "monster in view — substituting Explore{anywhere} to travel through/past the " +
                "looped object (enforces the LOOP-BREAK / PASSAGE-OPENED rules).");
            return MakeEgressExploreGoal(
                nowUtc, "override:use-loop-egress",
                "mechanical use-loop egress: confirmed bare world-object Use churn with no " +
                "egress, no inventory gain, no monster in view; Exploring to travel through " +
                "instead of re-Using the same object");
        }
        return _fallback.ProposeGoal(world, events, currentGoal);
    }

    // True iff the bot has any non-corpse monster in view that it can attack —
    // hostile (already attacking it) OR a non-hostile creature that still grants
    // XP. The Explore-egress substitutions above exist to LEAVE and FIND monsters,
    // so they must DEFER when a monster is ALREADY in view: the bot should engage
    // it (defend/flee a hostile, or fight a non-hostile XP target), not wander off
    // to look for one it can already see. Mirrors the `## Monsters in view`
    // capsule predicate (cp-2335/2366). Own-perception wire flags only — no game
    // content.
    internal static bool AnyAttackableMonsterInView(WorldStateProjection world)
        => world.Visible.Any(v => !v.IsCorpse && (v.IsMonster || v.ObservedHostile));

    // True when a bare world-object Use (the kind the world-Use churn guards own)
    // targets the vendor whose trade panel is currently OPEN. Re-Using an
    // already-open vendor is a transactional no-op (the panel stays open), NOT a
    // dead interior-door tour — so the world-Use churn egress must not FLEE it
    // (a committed Explore-away abandons the transactable panel before the bot
    // can Buy/Sell at the open offerings). Matches the open vendor by the
    // selector's guid, or by the LLM's name selector against the visible object
    // carrying the open vendor's guid. Open-panel wire fact + selector match
    // only; the LLM still decides whether/what to Buy or Sell.
    internal static bool LoopedUseTargetsOpenVendor(Goal goal, WorldStateProjection world)
    {
        if (world.Vendor is not { } ven) return false;
        if (goal.Kind != GoalKind.Use || goal.Item is not null) return false;
        if (goal.Target.Guid is uint g) return g == ven.VendorGuid;
        return world.Visible.Any(v => v.Guid == ven.VendorGuid
            && VisibleMatchesSelector(goal.Target, v));
    }

    // True iff there IS at least one attackable monster in view AND every such
    // monster is a kind the bot's own ledger marks LETHAL-beaten — the SAME
    // definition the veto and the ## Beaten kinds capsule use (a recorded DEATH
    // plus IsBeatenKind, lethal-retestable only once out-levelled) — i.e. there
    // is nothing in view the bot can currently win against and nothing it should
    // re-attempt. Used to break the beaten-kind STALEMATE: a beaten kind still
    // counts as a monster-in-view, so the general stuck-loop egress (which
    // requires NO monster in view) never fires and the bot stays parked; the LLM
    // is then re-asked every decision and re-picks the SAME vetoed Attack, burning
    // scarce LLM budget. Matching the veto's LETHAL-only definition is deliberate:
    // a merely SURVIVED (non-lethal) beaten kind in view is one the bot MAY still
    // re-attempt (the veto honors that), so its presence DEFERS this egress. An
    // actively-hostile monster in view is a live threat the Motor's flee/defend
    // reflexes must own, so its presence also DEFERS (return false). Likewise a
    // winnable (not-beaten) monster in view DEFERS — engage that XP target, do not
    // wander off. Own-perception wire flags + own combat ledger + own level only;
    // no game content, no priority on object types.
    internal static bool OnlyBeatenMonstersInView(WorldStateProjection world)
    {
        var monsters = world.Visible
            .Where(v => !v.IsCorpse && (v.IsMonster || v.ObservedHostile))
            .ToList();
        if (monsters.Count == 0) return false;
        if (monsters.Any(v => v.ObservedHostile)) return false;
        return monsters.All(v => IsLethalBeatenKind(
            world.CombatHistoryFull, v.Wcid, v.Name, world.Self.Level));
    }

    internal static int CountUntalkedNpcsInView(
        WorldStateProjection world,
        IReadOnlySet<uint>? talkedNpcGuids,
        IReadOnlySet<string>? talkedNpcNames = null,
        bool excludeVendors = false)
        => world.Visible.Count(v =>
            v.IsCreature
            && !v.IsMonster
            && !v.IsCorpse
            && !v.ObservedHostile
            // IsCreature and IsVendor are independent wire bits: a live vendor is
            // BOTH. Callers that already have a separate vendor path (the
            // kill-task-source gate) pass excludeVendors:true so a vendor-creature
            // is not double-counted as a dialog npc.
            && (!excludeVendors || !v.IsVendor)
            && !IsNpcAlreadyTalked(v, talkedNpcGuids, talkedNpcNames));

    // A visible NPC counts as already-talked when EITHER its resolved guid or
    // its display name appears in the session talked-set. The bot's own Talk
    // emissions are usually name-only (the LLM names a target; the wire guid is
    // resolved later by the Motor and is never written back into the emitted
    // Goal), so name is the identity that actually matches across cycles; a
    // fallback-sourced Talk additionally carries a guid. Matching either token
    // keeps an NPC marked talked regardless of which token its emission carried.
    private static bool IsNpcAlreadyTalked(
        VisibleObjectProjection v,
        IReadOnlySet<uint>? talkedNpcGuids,
        IReadOnlySet<string>? talkedNpcNames)
        => (talkedNpcGuids?.Contains(v.Guid) ?? false)
           || (v.Name is { Length: > 0 } name && (talkedNpcNames?.Contains(name) ?? false));

    private void RecordTalkedNpcs(EventStream events)
    {
        // Read the last goal emissions from the DEDICATED durable window, not the
        // perception-dominated ring: under heavy traffic the ring's "recent 10
        // goals" is starved (goals evicted within seconds), so a just-Talked NPC
        // could be missed and re-talked. The durable window holds the true last
        // emissions. (Same eviction fix as CountRecentTalkGoalsToName.)
        foreach (var ge in events.RecentGoalEmissions()
                     .Where(e => !string.IsNullOrEmpty(e.Text))
                     .Take(10))
        {
            if (!TryExtractTalkGoalTargetIdentity(ge.Text!, out var guid, out var name))
                continue;
            if (guid is { } g) _talkedNpcGuids.Add(g);
            if (!string.IsNullOrEmpty(name)) _talkedNpcNames.Add(name!);
        }
    }

    // Parse a Talk GoalEmitted Text into whichever target identity tokens it
    // carries. LLM Talk goals are name-only; fallback Talk goals also carry a
    // resolved guid. Returns true when at least one token was found. Purely a
    // structural parse of the bot's own emission text — no game knowledge.
    internal static bool TryExtractTalkGoalTargetIdentity(
        string text, out uint? guid, out string? name)
    {
        guid = null;
        name = null;
        if (!text.StartsWith("Talk ", StringComparison.Ordinal)) return false;
        if (!TryExtractGoalTargetSelector(text, out var selector)) return false;
        var gm = System.Text.RegularExpressions.Regex.Match(selector, "guid=0x([0-9A-Fa-f]+)");
        if (gm.Success && uint.TryParse(
                gm.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var g))
            guid = g;
        var nm = System.Text.RegularExpressions.Regex.Match(selector, "name=\"([^\"]+)\"");
        if (nm.Success) name = nm.Groups[1].Value;
        return guid is not null || name is not null;
    }

    private static bool TryExtractGoalTargetSelector(string text, out string selector)
    {
        selector = string.Empty;
        var sm = System.Text.RegularExpressions.Regex.Match(text, "target=(.*?) item=.*? source=");
        if (!sm.Success) return false;
        selector = sm.Groups[1].Value.Trim();
        return selector.Length > 0 && selector != "<empty>";
    }

    // Pure decision: a freshly PROVEN stationary NPC Talk fixation should break
    // the loop immediately (early egress) when no attackable monster is in view
    // and the server is not actively guiding the bot with a fresh directive.
    // Scoped to the Talk loop kind ONLY — a world-object Use loop may be a genuine
    // early-zone progress attempt, so it keeps the dwell-gated path. The egress
    // exists to LEAVE and find activity, so it defers when a monster is already in
    // view — the bot should engage that XP target (defend/flee a hostile, or fight
    // a non-hostile) rather than wander off (cp-2378 principle, applied to the
    // third egress path). Extracted for deterministic unit testing; own-signal
    // only, no game content.
    internal static bool ShouldEarlyEscapeTalkLoop(
        string loopKind, bool monsterInView, bool freshDirective)
        => loopKind == NpcTalkLoopKind && !monsterInView && !freshDirective;

    // Pure decision: a single-NPC Talk fixation PROVEN by the bot's OWN recent
    // goal-emission history (it Talked one NPC past the cp069 fixation threshold of
    // its last N emitted goals — immune to the interleaved combat/loot that resets
    // the stationary and roving guards) should break contact with ONE unlatched
    // Explore. Fires UNCONDITIONALLY for the Talk loop kind once the fixation is
    // proven — NOT gated on a fresh directive (a proven-stale fixation is not
    // finishing guided training, it is stuck re-greeting a spent step) and NOT on
    // monster-in-view (the latched Talk egress and the tapped-out stuck-loop egress
    // are BOTH monster-in-view-gated, so in a zone that always shows a monster a
    // proven fixation would wedge with no egress able to fire; and a bot that has
    // chosen Talk over Attack >= threshold times is provably not going to engage the
    // monster, so deferring to it just loops). The caller substitutes a target-less
    // generic Explore and re-deliberates next tick, so a winnable monster can still
    // be Attacked on the following decision. Extracted for deterministic unit
    // testing; own-signal only (own emission history), no game content; the Motor
    // chooses no target.
    internal static bool ShouldBreakContactExhaustedNpc(
        string loopKind, bool provenSingleNpcTalkFixation)
        => loopKind == NpcTalkLoopKind && provenSingleNpcTalkFixation;

    // Pure decision: a confirmed bare world-object Use churn should break the
    // loop with a generic Explore (travel through/past the looped object) rather
    // than defer to the fallback. NOT gated on freshDirective — re-Using the
    // SAME object cannot be "finishing guided training", so a confirmed churn
    // overrides the directive grace (unlike a Talk loop). Only an attackable
    // monster in view suppresses it: the egress exists to LEAVE and find monsters,
    // so when one is already in view the bot should engage it (defend/flee a
    // hostile or fight a non-hostile XP target) instead of wandering. Extracted
    // for deterministic unit testing; own-signal only, no game content.
    internal static bool ShouldEscapeWorldUseLoop(string loopKind, bool monsterInView)
        => loopKind == WorldUseLoopKind && !monsterInView;

    // Pure decision: the early Talk-loop egress latch is still ACTIVE this tick.
    // Active while within the latch window AND still in the same landblock the
    // loop was detected in (leaving the landblock means the loop is broken) AND
    // no attackable monster has appeared (engage that XP target instead of
    // continuing to wander) AND the server is not freshly guiding the bot.
    // Extracted for deterministic unit testing; own-signal only, no game content.
    internal static bool IsTalkLoopEgressActive(
        DateTimeOffset nowUtc, DateTimeOffset until, uint? latchLandblock,
        uint? currentLandblock, bool monsterInView, bool freshDirective)
        => nowUtc < until
           && latchLandblock is uint lb && currentLandblock is uint cur && lb == cur
           && !monsterInView
           && !freshDirective;

    // Returns true while a FRESH, distinct server tutorial/instruction popup is
    // within FreshDirectiveGrace — the signal that the bot is actively being
    // guided and should finish training before egress. Idempotent per tick:
    // scans only events newer than the high-water sequence, credits the grace
    // ONLY on a PopupString whose text differs from the last credited one (a
    // repeated popup is the anti-idle loop and must NOT reset it), and lets the
    // veto age out so it can never deadlock. PopupString only (NOT NpcDialog):
    // popups are low-volume, server-pushed, directed-at-self, and never emitted
    // by the bot's own Talk loop — so a town full of NPCs cannot pin the bot.
    // Typed event-kind + freshness; no names/wcids/landblocks — audit-safe.
    internal bool RecentFreshDirective(EventStream events, DateTimeOffset nowUtc)
    {
        var newestSeq = _egressLastDirectiveSeqSeen;
        string? freshText = null;
        var freshUtc = DateTimeOffset.MinValue;
        foreach (var e in events.Recent()) // newest-first
        {
            if (e.Sequence <= _egressLastDirectiveSeqSeen) break;
            if (e.Sequence > newestSeq) newestSeq = e.Sequence;
            if (e.Kind != EventKind.PopupString) continue;
            var t = e.Text?.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            // First (newest) distinct popup in this batch wins.
            if (freshText is null && t != _egressLastCreditedDirectiveText)
            {
                freshText = t;
                freshUtc = e.Utc;
            }
        }
        _egressLastDirectiveSeqSeen = newestSeq;
        // Credit the grace from the DIRECTIVE'S OWN timestamp, not call time,
        // and only if the directive itself is recent. A stale popup (e.g. an
        // old login popup picked up on the first whole-buffer scan, or one
        // processed after a delay) must NOT grant a full fresh veto when no
        // current guidance exists. Clamp the stamp to <= now so a future-dated
        // event can't over-extend.
        if (freshText is not null && (nowUtc - freshUtc) < FreshDirectiveGrace)
        {
            _egressLastCreditedDirectiveText = freshText;
            _egressLastFreshDirectiveUtc = freshUtc < nowUtc ? freshUtc : nowUtc;
            Console.WriteLine(
                "[llm-override] hunt-egress directive-grace: fresh server directive " +
                $"credited — deferring egress up to {FreshDirectiveGrace.TotalMinutes:F0}min " +
                "to finish guided training.");
        }
        return _egressLastFreshDirectiveUtc != DateTimeOffset.MinValue
            && (nowUtc - _egressLastFreshDirectiveUtc) < FreshDirectiveGrace;
    }

    // Pure sticky-latch transition for hunt-egress. Engages once the bot has
    // dwelled past the threshold in a tapped-out monster-free safe zone, and
    // STAYS engaged across landblock seams (so the bot actually leaves the
    // town cluster instead of nudging one seam and reverting) until a cancel
    // condition holds. All inputs are typed affordances / timers — no game
    // content. Extracted for deterministic unit testing.
    internal static bool ComputeEgressActive(
        bool currentlyEgressing, bool combatReady, bool monsterInView,
        double dwellMinutes, TimeSpan sinceMaterialProgress, bool tappedOut = false,
        bool recentFreshDirective = false)
    {
        // Highest-priority cancel: the bot is actively being guided by the
        // server (a fresh, distinct, not-yet-acted tutorial/instruction popup
        // within FreshDirectiveGrace). Finishing available guided training
        // outranks an optional hunt, so veto egress entirely — including an
        // egress already in progress. This can NEVER deadlock: only DISTINCT
        // server popups credit it (a repeat does not) and it ages out once the
        // server stops instructing, after which the dwell/stall triggers below
        // fire normally. See FreshDirectiveGrace + RecentFreshDirective.
        if (recentFreshDirective)
            return false;
        // Cancel conditions take priority — they end an egress in progress:
        //  - a monster is now engageable here (we reached the hunt);
        //  - the bot is no longer combat-ready (disarmed);
        //  - material (inventory) progress happened recently (real quest) —
        //    UNLESS the bot is tapped out. When tapped out (combat-ready,
        //    dwelled past the threshold, 0 levels gained here) the inventory
        //    churn is just trivial-corpse loot from re-farming the same kinds;
        //    the bot's own 0-levels self-progress signal is the authority, so
        //    the loot grace must not keep deferring egress forever.
        if (monsterInView || !combatReady)
            return false;
        if (!tappedOut && sinceMaterialProgress < EgressNoProgressGrace)
            return false;
        // Sticky: stay engaged once started, regardless of dwell reset at seams.
        if (currentlyEgressing) return true;
        // First-trigger A: dwelled past the threshold in THIS landblock.
        // First-trigger B (seam-independent): combat-ready, monster-free, and no
        // material progress for well past the dwell threshold — catches a bot
        // oscillating between adjacent safe landblocks where per-landblock dwell
        // keeps resetting and trigger A can never fire.
        return dwellMinutes >= EgressDwellMinutes
            || sinceMaterialProgress >= BarrenStallTimeout;
    }

    // Only social, dwell-extending verbs are substituted unconditionally while
    // egressing. Pickup/Wield/Attack/Explore are never overridden here (Pickup
    // may be self-arming; the rest are already progress). A Use is handled
    // separately by IsEgressOverridableStationaryUse so transit Uses survive.
    internal static bool IsEgressOverridableVerb(GoalKind kind)
        => kind == GoalKind.Talk || kind == GoalKind.Give;

    // While egressing, a Use targeting a STATIONARY non-transit world object
    // (e.g. a crafting station the LLM fixates on and re-walks to) extends the
    // dwell exactly like Talk/Give, so substitute it with Explore. Transit and
    // interactive affordances are PRESERVED so the bot can still leave and loot:
    // a door/portal Use is the way OUT, a corpse Use is looting, an openable
    // (container) Use is real interaction. Decision uses ONLY typed wire-bit
    // projection flags (IsPortal/IsDoor/IsOpenable/IsCorpse) — no object names,
    // wcids, landblocks, or priorities. Conservative: overrides only when the
    // target resolves to visible object(s) ALL confirmed non-transit; an
    // unresolved or mixed target passes through untouched.
    internal static bool IsEgressOverridableStationaryUse(
        Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Use) return false;
        var sel = goal.Target;
        if (sel.IsEmpty) return false;
        var matches = world.Visible
            .Where(v => VisibleMatchesSelector(sel, v))
            .ToList();
        if (matches.Count == 0) return false;
        return matches.All(v =>
            !v.IsPortal && !v.IsDoor && !v.IsOpenable && !v.IsCorpse);
    }

    // cold-start egress: true when a visible monster is one the bot has
    // already FARMED in the current landblock — i.e. the bot is tapped out
    // (0 levels gained here), the monster is NOT currently attacking the bot,
    // and its kind-key is in the per-dwell killed set. Such a monster no
    // longer counts as a reason to stay. Uses ONLY the bot's own outcomes
    // (its own kills here + its own level progress) and wire-derived
    // hostility — no hardcoded names/wcids/landblocks, no value/danger label.
    internal static bool IsFarmedHere(
        VisibleObjectProjection v, IReadOnlySet<string>? killedThisDwell, bool tappedOut)
    {
        if (!tappedOut) return false;
        if (v.ObservedHostile) return false;
        if (killedThisDwell is null || killedThisDwell.Count == 0) return false;
        return CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(v.Wcid, v.Name))
                   is string k
               && killedThisDwell.Contains(k);
    }

    // cold-start egress: like the plain "any monster in view" check, but a
    // kind the bot has already farmed here (see IsFarmedHere) OR has kept
    // visible-but-unengaged past the liveness timeout (see IsIgnoredHere) does
    // not count while tapped out. A first-time/unknown kind or any
    // ObservedHostile attacker still counts (cancels egress so the bot engages
    // it). ignoredThisDwell is optional so existing callers are unaffected.
    internal static bool ComputeEffectiveMonsterInView(
        IReadOnlyList<VisibleObjectProjection> visible,
        IReadOnlySet<string>? killedThisDwell, bool tappedOut,
        IReadOnlySet<string>? ignoredThisDwell = null)
        => visible.Any(v => v.IsMonster && !v.IsCorpse
                            && !IsFarmedHere(v, killedThisDwell, tappedOut)
                            && !IsIgnoredHere(v, ignoredThisDwell, tappedOut));

    // Liveness backstop counterpart to IsFarmedHere: true when a visible
    // non-hostile monster's kind-key is in the per-dwell "ignored" set (the
    // bot kept it visible-but-unengaged past IgnoredKindExposureTimeout while
    // tapped out). ObservedHostile attackers are never ignored. Same own-data
    // basis as IsFarmedHere — no value/danger label per type.
    internal static bool IsIgnoredHere(
        VisibleObjectProjection v, IReadOnlySet<string>? ignoredThisDwell, bool tappedOut)
    {
        if (!tappedOut) return false;
        if (v.ObservedHostile) return false;
        if (ignoredThisDwell is null || ignoredThisDwell.Count == 0) return false;
        return CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(v.Wcid, v.Name))
                   is string k
               && ignoredThisDwell.Contains(k);
    }

    // A goal whose presence (while tapped out) counts as the bot "ignoring" a
    // visible monster: the social/stationary verbs egress may override. An
    // Attack/Pickup/Wield (engaging/arming) or Explore (already leaving) is NOT
    // ignoring, so it does not accrue ignored-kind exposure.
    private static bool IsEgressIgnoreEligibleGoal(GoalKind kind)
        => kind == GoalKind.Talk || kind == GoalKind.Give || kind == GoalKind.Use;

    // Pure update of the per-kind eligible-exposure tracker. Mutates
    // firstEligibleUtc in place and returns the set of kind-keys whose
    // CONTINUOUS eligible exposure has reached `timeout` (the "ignored" set).
    // When eligibleContext is false the tracker is cleared (no accrual). A kind
    // that is hostile, is one the bot just chose to engage (engagedKinds), or
    // is no longer visible is dropped — so a qualifying kind must be present,
    // non-hostile, and unengaged the WHOLE window. Own-behavior bookkeeping
    // only; assigns no value to any kind.
    internal static IReadOnlySet<string> UpdateIgnoredKindExposure(
        IDictionary<string, DateTimeOffset> firstEligibleUtc,
        IReadOnlyCollection<(string Key, bool Hostile)> visibleMonsterKinds,
        IReadOnlySet<string> engagedKinds,
        bool eligibleContext,
        DateTimeOffset now,
        TimeSpan timeout)
    {
        if (!eligibleContext)
        {
            firstEligibleUtc.Clear();
            return EmptyKindSet;
        }
        var eligibleNow = new HashSet<string>();
        foreach (var (key, hostile) in visibleMonsterKinds)
        {
            if (hostile) continue;
            if (engagedKinds.Contains(key)) continue;
            eligibleNow.Add(key);
        }
        foreach (var stale in firstEligibleUtc.Keys.Where(k => !eligibleNow.Contains(k)).ToList())
            firstEligibleUtc.Remove(stale);
        var ignored = new HashSet<string>();
        foreach (var key in eligibleNow)
        {
            if (!firstEligibleUtc.TryGetValue(key, out var first))
            {
                first = now;
                firstEligibleUtc[key] = first;
            }
            if (now - first >= timeout)
                ignored.Add(key);
        }
        return ignored.Count == 0 ? EmptyKindSet : ignored;
    }

    // Kind-keys of all currently-visible monsters paired with whether each is
    // an active ObservedHostile attacker. Corpses excluded. Null kind-keys
    // (no wcid and no name) dropped.
    private static IReadOnlyCollection<(string Key, bool Hostile)> VisibleMonsterKindKeys(
        WorldStateProjection world)
    {
        var list = new List<(string, bool)>();
        foreach (var v in world.Visible)
        {
            if (!v.IsMonster || v.IsCorpse) continue;
            if (CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(v.Wcid, v.Name)) is string k)
                list.Add((k, v.ObservedHostile));
        }
        return list;
    }

    // Kind-keys of the visible monsters a selector resolves to (the kinds the
    // bot is choosing to engage this tick). Empty when nothing matches.
    private static IReadOnlySet<string> VisibleKindKeysMatching(
        Selector sel, WorldStateProjection world)
    {
        if (sel.IsEmpty) return EmptyKindSet;
        var set = new HashSet<string>();
        foreach (var v in world.Visible)
        {
            if (!v.IsMonster || v.IsCorpse) continue;
            if (!VisibleMatchesSelector(sel, v)) continue;
            if (CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(v.Wcid, v.Name)) is string k)
                set.Add(k);
        }
        return set.Count == 0 ? EmptyKindSet : set;
    }

    // cold-start egress: true only for a narrow, safe-to-substitute Attack —
    // an Attack the LLM emitted, while tapped out, whose every matching
    // visible monster is a non-hostile kind already farmed in this landblock.
    // Guards (all must hold): tapped out; the goal is an Attack with a target
    // selector; the bot has killed something here; NO visible monster is
    // currently attacking the bot (self-defense outranks egress); the bot is
    // not mid-fight (CurrentFight null — let the active swing resolve); the
    // selector resolves to at least one visible monster and EVERY match is an
    // already-farmed-here kind (so a name-collision with an unfarmed/tougher
    // kind is never suppressed). Decision uses the bot's own learned futility
    // only; the LLM still owns engaging anything new.
    internal static bool IsTappedOutRepeatKillAttack(
        Goal goal, WorldStateProjection world, bool tappedOut)
    {
        if (!tappedOut) return false;
        if (goal.Kind != GoalKind.Attack) return false;
        var sel = goal.Target;
        if (sel.IsEmpty) return false;
        var killedHere = world.KilledKindsThisDwell;
        if (killedHere is null || killedHere.Count == 0) return false;
        if (world.Visible.Any(v => v.IsMonster && !v.IsCorpse && v.ObservedHostile))
            return false;
        if (world.CurrentFight is not null) return false;

        var matches = world.Visible
            .Where(v => v.IsMonster && !v.IsCorpse && VisibleMatchesSelector(sel, v))
            .ToList();
        if (matches.Count == 0) return false;
        return matches.All(m => IsFarmedHere(m, killedHere, tappedOut));
    }

    // Conservative projection-level selector match for the egress Attack
    // override. Mirrors the positive identity predicates of
    // Tactics.SelectorResolver (guid / exact name / name-substring / wcid)
    // against a VisibleObjectProjection. Requires at least one identity field
    // to be set so a loose/empty selector never matches-all.
    private static bool VisibleMatchesSelector(Selector sel, VisibleObjectProjection v)
    {
        var hasIdentity = sel.Guid is not null
            || !string.IsNullOrEmpty(sel.Name)
            || !string.IsNullOrEmpty(sel.NameContains)
            || sel.Wcid is not null;
        if (!hasIdentity) return false;
        if (sel.Guid is uint g && v.Guid != g) return false;
        if (!string.IsNullOrEmpty(sel.Name)
            && !string.Equals(v.Name, sel.Name, StringComparison.OrdinalIgnoreCase)
            // Mirror SelectorResolver.MatchesName: the prompt renders objects as
            // `<Name> "<role>"`, and the model often copies that whole label into a
            // selector. Tolerate a trailing quoted-role suffix and re-test the bare
            // name, so the policy-side "is this target visible" checks stay
            // consistent with the Motor's resolver.
            && !(HeadlessAcClient.Tactics.SelectorResolver.StripTrailingQuotedRoleTitle(sel.Name) is string bareName
                 && string.Equals(v.Name, bareName, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrEmpty(sel.NameContains)
            && (v.Name is null
                || !v.Name.Contains(sel.NameContains, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (sel.Wcid is uint w && !(v.Wcid is uint vw && vw == w)) return false;
        return true;
    }

    // Selector match for inventory items — mirrors VisibleMatchesSelector above
    // but operates on an InventoryItemProjection. Used by the useless-launcher
    // Wield drop guard to confirm a Wield selector resolves to a specific bag
    // item before testing whether that item is loadable. Requires at least one
    // identity field (Guid/Name/NameContains/Wcid) so an empty selector never
    // matches-all. Pure wire-value comparison; no names/wcids in source.
    private static bool InventoryMatchesSelector(Selector sel, InventoryItemProjection i)
    {
        var hasIdentity = sel.Guid is not null
            || !string.IsNullOrEmpty(sel.Name)
            || !string.IsNullOrEmpty(sel.NameContains)
            || sel.Wcid is not null
            || sel.ItemTypeMask is not null
            || !string.IsNullOrEmpty(sel.ShortDescContains);
        if (!hasIdentity) return false;
        if (sel.Guid is uint g && i.Guid != g) return false;
        if (!string.IsNullOrEmpty(sel.Name)
            && !string.Equals(i.Name, sel.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(sel.NameContains)
            && !i.Name.Contains(sel.NameContains, StringComparison.OrdinalIgnoreCase))
            return false;
        if (sel.Wcid is uint w && i.Wcid != w) return false;
        // Mirror SelectorResolver.MatchesItemTypeMask / MatchesShortDescContains so the
        // useless-launcher guard resolves a Wield selector EXACTLY like the Motor's wield
        // executor — otherwise an item_type_mask or short_desc_contains selector matches
        // a different item here than the executor would actually wield (the guard could
        // drop a legitimate Wield or miss the useless launcher).
        if (sel.ItemTypeMask is uint m && !(i.ItemType is uint it && (it & m) != 0)) return false;
        if (!string.IsNullOrEmpty(sel.ShortDescContains)
            && !(i.ShortDesc is not null
                 && i.ShortDesc.Contains(sel.ShortDescContains, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    // Returns the resolved ground-weapon guid iff this Wield goal names a takeable
    // weapon lying on the GROUND — a visible world object, not a bag item. The
    // Motor's Wield dispatch equips ONLY items already in inventory, so a weapon on
    // the ground must be PICKED UP first; the Combat-readiness capsule already
    // advises "Pickup it to arm" for exactly this object, but a model sometimes
    // emits the end-goal verb Wield for the named ground weapon instead of the
    // Pickup step, which the Motor then fails (wasting the call and leaving the bot
    // unarmed). Detecting it lets ProposeGoalCore perform the mechanical
    // prerequisite (rewrite to a Pickup of the SAME named weapon). Mirrors the
    // groundWeapon capsule predicate (visible, non-monster, non-corpse, melee
    // weapon, equippable, not server-refused) and matches it by the goal's OWN
    // selector — the LLM chose WHICH weapon, so there is no autonomous target pick
    // and no game knowledge.
    internal static uint? TryResolveWieldGroundWeapon(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Wield) return null;
        // The wielded object may be named in `target` (the observed mis-shape) or
        // `item`; use whichever the LLM populated.
        var sel = !goal.Target.IsEmpty ? goal.Target
                : (goal.Item is { IsEmpty: false } ? goal.Item : null);
        if (sel is null) return null;
        // A matching BAG item that is itself EQUIPPABLE and not already wielded is
        // wieldable directly by the normal Wield path (and its swap/dequip handling)
        // — no pickup prerequisite, so defer to it. A non-equippable same-named bag
        // item (e.g. a quest item) does NOT suppress arming from a ground weapon.
        // (A same-named ground copy resolving ahead of an in-bag copy inside the
        // Motor is a pre-existing resolver edge, not introduced here.)
        if (world.Inventory.Any(i =>
                InventoryMatchesSelector(sel, i) &&
                i.ValidLocations is uint ivl && ivl != 0 &&
                (i.WieldedAt is null || i.WieldedAt == 0)))
            return null;
        // Semantic refusals only (transport/walk-timeout failures clear on arrival),
        // mirroring the groundWeapon capsule so a server-refused weapon stops being
        // re-picked instead of looping.
        var refused = events
            .RecentOfKind(EventKind.ActionRejected, 32)
            .Where(e => e.ItemGuid is uint && !IsTransportFailureRejection(e))
            .Select(e => e.ItemGuid!.Value)
            .ToHashSet();
        // Require a UNIQUE match: the LLM must have named a single weapon. An
        // ambiguous selector (e.g. name_contains, or two same-named ground weapons)
        // stays unresolved so the LLM re-decides — the Motor never autonomously
        // picks one of several candidates (mirrors SelectorResolver's unique-match
        // rule).
        var matches = world.Visible.Where(v =>
            !v.IsMonster && !v.IsCorpse &&
            v.ItemType is uint vit && (vit & ItemTypeMasks.MeleeWeapon) != 0 &&
            !refused.Contains(v.Guid) &&
            VisibleMatchesSelector(sel, v)).ToList();
        return matches.Count == 1 ? matches[0].Guid : (uint?)null;
    }

    // True (returns the object guid) iff this Pickup goal uniquely names a visible
    // Use-container: a CORPSE, or an OPENABLE container the server will NOT let the
    // bot take. Such an object is actuated with `Use` (the Motor's Use handler opens
    // it and transfers its contents), not taken: a Pickup of it resolves to MISS and
    // the bot loops the wrong verb. The takeability test mirrors the server's pickup
    // gate exactly (PutItemInContainer refuses a non-dynamic, stuck, or creature
    // object): an IsChest object that is not a creature is a Use-container when its
    // guid is below the dynamic range (world-static, IsDynamicGuid == false) OR it
    // carries the Stuck wire bit. A genuinely takeable container item (dynamic guid,
    // not stuck) reads as IsChest too (the Openable bit is set on WeenieType.Container)
    // but is left as a Pickup. Unique match only; server-refused guids excluded. The
    // LLM chose WHICH object — the Motor only substitutes the mechanically-correct verb
    // (no autonomous target pick, no game knowledge; keyed on the IsCorpse/IsChest/
    // IsStuck/IsMonster wire bits + the wire guid range). Mirrors TryResolveWieldGroundWeapon.
    internal static uint? TryResolvePickupUseContainer(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Pickup) return null;
        if (goal.Target.IsEmpty) return null;
        var refused = events
            .RecentOfKind(EventKind.ActionRejected, 32)
            .Where(e => e.ItemGuid is uint && !IsTransportFailureRejection(e))
            .Select(e => e.ItemGuid!.Value)
            .ToHashSet();
        var matches = world.Visible.Where(v =>
            (v.IsCorpse || (v.IsChest && !v.IsMonster && (v.IsStuck || !IsDynamicGuid(v.Guid)))) &&
            !refused.Contains(v.Guid) &&
            VisibleMatchesSelector(goal.Target, v)).ToList();
        return matches.Count == 1 ? matches[0].Guid : (uint?)null;
    }

    // The recent-window + repeat threshold for the looped-Explore-toward-a-vendor rewrite.
    // The number of recent Explore emissions naming the SAME visible vendor that marks a
    // loop (vs a single legitimate approach), at/above which ProposeGoalCore converts the
    // looped Explore into a `Use` of that vendor. Env-configurable via
    // AC_BOTS_EXPLORE_VENDOR_LOOP_THRESHOLD (default 3, clamp [2, 10]); read once at
    // type-load. Lowering it converts a vendor-Explore loop one LLM kickoff sooner
    // (reduce-llm-call-volume) at the cost of a slightly higher transit false-positive (a
    // target named twice while passing it -> a one-step `Use` that self-corrects). The floor
    // is 2 so a single approach emission is never preempted (a re-emission is still required).
    // The match count is over emissions in the recent ExploreLoopedVendorWindow (not strictly
    // consecutive), so a still-visible vendor named >= threshold times within that window —
    // while the CURRENT goal re-Explores it — converts; at floor 2 that is a quick revisit.
    internal static readonly int ExploreLoopedVendorThreshold =
        ResolveExploreLoopedVendorThreshold(
            Environment.GetEnvironmentVariable("AC_BOTS_EXPLORE_VENDOR_LOOP_THRESHOLD"));

    // Parse AC_BOTS_EXPLORE_VENDOR_LOOP_THRESHOLD. A positive integer >= 2 is used (clamped
    // to [2, 10]); anything else (unset/blank/unparseable/<2) falls back to 3.
    internal static int ResolveExploreLoopedVendorThreshold(string? envValue)
    {
        const int Default = 3;
        const int Min = 2;
        const int Max = 10;
        if (int.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= Min)
            return Math.Min(v, Max);
        return Default;
    }
    private static readonly TimeSpan ExploreLoopedVendorWindow = TimeSpan.FromMinutes(5);

    // Death-spiral detection threshold: how many of the bot's OWN deaths within
    // DeathSpiralWindow mark an active death-spiral. AC applies a stacking respawn
    // penalty for repeated deaths that lowers effective max HP (and other vitals)
    // until the deaths STOP — so a bot that keeps dying respawns ever weaker and
    // cannot recover by raising max HP, only by surviving long enough for the
    // penalty to fade. The LLM cannot derive this rate from the cumulative deaths
    // count or the single last-death timestamp, so the prompt surfaces it. Env
    // AC_BOTS_DEATH_SPIRAL_MIN_DEATHS overrides; default 3, clamp [2, 10].
    internal static readonly int DeathSpiralMinDeaths =
        ResolveDeathSpiralMinDeaths(
            Environment.GetEnvironmentVariable("AC_BOTS_DEATH_SPIRAL_MIN_DEATHS"));

    // area-death-memory: the bot's OWN death count IN its current landblock at or
    // above which the "## Area danger" cue surfaces. 2 (not 1) so a single unlucky
    // death does not flag an area — only an area that has killed the bot MORE THAN
    // ONCE this session (a re-entered or stood-in deadly spot). Spatial complement
    // of DeathSpiralMinDeaths (a death RATE anywhere); this is deaths in ONE place.
    private const int AreaDeathSalienceThreshold = 2;

    // Parse AC_BOTS_DEATH_SPIRAL_MIN_DEATHS. A positive integer >= 2 is used
    // (clamped to [2, 10]); anything else (unset/blank/unparseable/<2) falls back to 3.
    internal static int ResolveDeathSpiralMinDeaths(string? envValue)
    {
        const int Default = 3;
        const int Min = 2;
        const int Max = 10;
        if (int.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= Min)
            return Math.Min(v, Max);
        return Default;
    }
    private static readonly TimeSpan DeathSpiralWindow = TimeSpan.FromMinutes(10);

    // Returns the guid of a visible VENDOR the bot has repeatedly Explored TOWARD (>=
    // ExploreLoopedVendorThreshold recent Explore emissions naming it). Explore only WALKS
    // to a target and never interacts, so an Explore that NAMES a vendor "arrives" at it
    // without engaging and a model then re-Explores the SAME in-view vendor every cycle (a
    // loop). The bot named the vendor (intent to reach it); the mechanically-correct way to
    // engage a reached vendor is `Use` (open its trade panel), so ProposeGoalCore rewrites
    // the looped Explore into a Use of that SAME vendor. UNIQUE match only; never a monster
    // or corpse. The >= threshold gate means a first/legitimate approach Explore is never
    // preempted (the LLM may switch to Use itself); only a stuck loop is rewritten. Own
    // emission history + perception; no game knowledge, no source-side target choice.
    internal static uint? TryResolveExploreLoopedVendor(
        Goal goal, WorldStateProjection world, EventStream events, DateTimeOffset since)
    {
        if (goal.Kind != GoalKind.Explore) return null;
        if (goal.Target.Name is not string targetName || string.IsNullOrWhiteSpace(targetName)) return null;
        // Resolve the target to a UNIQUE visible vendor using the SAME name semantics the
        // Motor's SelectorResolver uses (exact / quoted-role-strip / unique whole-word
        // subsequence), so a partial name resolves the same way the Motor actually walked
        // there — otherwise an exact-only check would MISS a fuzzy-resolved loop.
        if (ResolveUniqueVisibleVendorByName(world.Visible, targetName) is not uint vendorGuid) return null;
        // Count recent Explore emissions that bind the SAME vendor IDENTITY (by guid, via
        // the same resolver), not the raw string — so aliases for one vendor accumulate,
        // and the count is attributed by what each name binds in the CURRENT view.
        var counts = CountRecentEmittedTargetNames(events, since, "Explore", excludeItemGoals: false);
        var loopCount = 0;
        foreach (var kv in counts)
            if (ResolveUniqueVisibleVendorByName(world.Visible, kv.Key) == vendorGuid)
                loopCount += kv.Value;
        return loopCount >= ExploreLoopedVendorThreshold ? vendorGuid : (uint?)null;
    }

    // Buy{vendor-name} with NO vendor trade panel open -> the vendor guid to Use
    // (approach + open the panel). The Buy dispatch FAILS a Buy when no panel is open
    // within reach (by design, expecting the LLM to Use/approach the vendor first), but
    // a model often re-emits the SAME Buy (sticky) instead, looping on "no panel open"
    // without ever approaching. When the Buy NAMES a uniquely-visible vendor and no panel
    // is open, this returns that vendor's guid so the verb can be rewritten to Use. Once
    // a panel IS open (world.Vendor != null) it returns null, so it only fires during the
    // approach phase (no loop; the eventual Buy resolves normally). Keyed on the open-panel
    // wire state + the IsVendor bit + the bot's OWN named target; no autonomous pick.
    internal static uint? TryResolveBuyVendorNoPanel(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Buy) return null;
        if (world.Vendor is not null) return null;          // a panel is open -> let Buy run
        if (goal.Target?.Name is not string name || string.IsNullOrWhiteSpace(name)) return null;
        return ResolveUniqueVisibleVendorByName(world.Visible, name);
    }

    // Returns the guid of the UNIQUE visible VENDOR (IsVendor, not monster/corpse) that
    // binds `name` under the SAME name semantics as the Motor's SelectorResolver: exact
    // (case-insensitive), exact after stripping a trailing quoted-role suffix, OR a UNIQUE
    // whole-word subsequence fuzzy match. Returns null when none OR more than one binds
    // (ambiguous stays unrewritten). Mirrors VisibleResolvesName but over vendors only and
    // returns the identity. Pure string comparison on observed names + the IsVendor wire
    // bit; no game knowledge.
    private static uint? ResolveUniqueVisibleVendorByName(
        IReadOnlyList<VisibleObjectProjection> visible, string name)
    {
        var vendors = visible.Where(v => v.IsVendor && !v.IsMonster && !v.IsCorpse && v.Name is not null).ToList();
        if (vendors.Count == 0) return null;
        var bare = HeadlessAcClient.Tactics.SelectorResolver.StripTrailingQuotedRoleTitle(name) ?? name;
        var exact = vendors.Where(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(v.Name, bare, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].Guid;
        if (exact.Count > 1) return null;
        var fuzzy = vendors.Where(v =>
            HeadlessAcClient.Tactics.SelectorResolver.MatchesNameWordSubsequence(v.Name, name)).ToList();
        return fuzzy.Count == 1 ? fuzzy[0].Guid : (uint?)null;
    }

    // Does NAME plausibly resolve to an item the bot OWNS (in its own inventory)? LENIENT
    // (ANY exact / role-stripped / whole-word-subsequence match — not a unique match), used
    // only to SUPPRESS the Use-item-world-object rewrite below: when the name could be an
    // owned item, the Use is a legitimate self-Use (read/activate) the Motor dispatches, so
    // the rewrite must not fire. Erring toward "matches" keeps a genuine self-Use from being
    // hijacked. Mirrors SelectorResolver name semantics; own inventory only; no game knowledge.
    private static bool InventoryResolvesName(IReadOnlyList<InventoryItemProjection> inventory, string name)
    {
        var bare = HeadlessAcClient.Tactics.SelectorResolver.StripTrailingQuotedRoleTitle(name) ?? name;
        return inventory.Any(i => i.Name is not null
            && (string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Name, bare, StringComparison.OrdinalIgnoreCase)
                || HeadlessAcClient.Tactics.SelectorResolver.MatchesNameWordSubsequence(i.Name, name)));
    }

    // The GUID of the unique visible vendor/NPC whose name resolves to NAME (exact /
    // role-stripped / unique whole-word subsequence — the SAME SelectorResolver semantics the
    // Motor's Use target resolves with). Monsters and corpses are excluded (they have their own
    // Attack / corpse-loot paths). Returns null when 0 or >1 candidates match (an ambiguous
    // partial stays unresolved so the LLM re-decides). Used to move a world object a model
    // mis-filed into a Use goal's ITEM field back into the TARGET field. Pure name resolution
    // over perception; no game knowledge, no autonomous target choice.
    private static uint? ResolveUniqueVisibleUseTargetByName(
        IReadOnlyList<VisibleObjectProjection> visible, string name)
    {
        var pool = visible.Where(v =>
            (v.IsVendor || v.IsCreature) && !v.IsMonster && !v.IsCorpse && v.Name is not null).ToList();
        if (pool.Count == 0) return null;
        var bare = HeadlessAcClient.Tactics.SelectorResolver.StripTrailingQuotedRoleTitle(name) ?? name;
        var exact = pool.Where(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(v.Name, bare, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].Guid;
        if (exact.Count > 1) return null;
        var fuzzy = pool.Where(v =>
            HeadlessAcClient.Tactics.SelectorResolver.MatchesNameWordSubsequence(v.Name, name)).ToList();
        return fuzzy.Count == 1 ? fuzzy[0].Guid : (uint?)null;
    }

    // Use{item=<world object>, no world target} -> the GUID of that visible vendor/NPC, so the
    // misfiled name can be moved into the TARGET field. The self-Use shape Use{item=X, no
    // target} is for activating an OWNED inventory item on yourself; a model sometimes mis-files
    // a VISIBLE vendor/NPC into that item field with no target (expecting it to open/engage the
    // object), but the Motor's self-Use only dispatches an in-bag item, so it resolves to MISS
    // and the bot loops the wrong shape. Fire ONLY when the item-field name is NOT a plausible
    // owned item (so a true self-Use is never hijacked) AND uniquely resolves to a visible
    // vendor/NPC. The LLM chose WHICH object; the Motor only corrects the field. A two-object
    // Use{target=container, item=key} carries a non-empty target and is excluded. No game knowledge.
    internal static uint? TryResolveUseWorldObjectInItemField(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Use) return null;
        if (goal.Target is { IsEmpty: false }) return null;
        var itemName = goal.Item?.Name;
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        if (world.Inventory is not null && InventoryResolvesName(world.Inventory, itemName)) return null;
        return world.Visible is null ? null : ResolveUniqueVisibleUseTargetByName(world.Visible, itemName);
    }

    // Returns the EXACT open-vendor offering name to Buy when a Use goal mis-files an
    // open-vendor FOR-SALE item into its `item` field with no world target, or null. The
    // self-Use shape Use{item=X, no target} is for activating an OWNED inventory item; a
    // model sometimes names a vendor-PANEL offering it wants to acquire there instead, but
    // the Motor's self-Use only resolves an in-bag item, so a panel offering resolves to
    // MISS and the bot loops the wrong shape. The mechanically-correct verb to acquire a
    // panel item is Buy. Fires only when (a) a vendor trade panel is open (world.Vendor),
    // (b) the item-field name is NOT a plausible owned item (so a genuine self-Use is never
    // hijacked) and NOT a visible vendor/NPC (Use-item-world-object handles those), and
    // (c) the name matches one of the open vendor's offerings EXACTLY (case-insensitive,
    // trimmed — the SAME semantics the Motor's Buy resolves with). Pure string comparison
    // over the wire-decoded offer list; no game knowledge, no source-side target choice
    // (the LLM named the item).
    internal static string? TryResolveUseItemVendorOffering(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Use) return null;
        if (goal.Target is { IsEmpty: false }) return null;
        var itemName = goal.Item?.Name;
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        if (world.Vendor?.Offers is not { Count: > 0 } offers) return null;
        if (world.Inventory is not null && InventoryResolvesName(world.Inventory, itemName)) return null;
        if (world.Visible is not null && ResolveUniqueVisibleUseTargetByName(world.Visible, itemName) is not null)
            return null;
        var trimmed = itemName.Trim();
        foreach (var o in offers)
            if (!string.IsNullOrWhiteSpace(o.Name)
                && string.Equals(o.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return o.Name;
        return null;
    }

    // Returns the EXACT open-vendor offering name to Buy when a Pickup goal names a
    // vendor-panel item rather than a takeable ground object, or null. A Pickup takes
    // only ground items, so a Pickup whose target is one of the open vendor's for-sale
    // offerings resolves to MISS; the mechanically-correct verb to acquire a panel item
    // is Buy. Fires only when (a) a vendor trade panel is open (world.Vendor), (b) the
    // goal name matches one of its offerings EXACTLY (case-insensitive, trimmed — the
    // SAME exact semantics the Motor's Buy uses via ResolveVendorItemExact, so the
    // rewritten Buy resolves), and (c) NO visible world object binds the name (so the
    // Pickup is not a legitimate ground pickup the rewrite would hijack). Pure string
    // comparison over the wire-decoded offer list + observed visible names; no game
    // knowledge, no source-side target choice (the LLM named the item).
    internal static string? TryResolvePickupVendorItemName(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Pickup) return null;
        if (goal.Target?.Name is not string name || string.IsNullOrWhiteSpace(name)) return null;
        if (world.Vendor?.Offers is not { Count: > 0 } offers) return null;
        // If a visible NON-CORPSE object resolves the name, leave the Pickup alone (do
        // not hijack a real ground pickup of a same-named object). Corpses are excluded
        // to mirror the real Pickup resolver, which never binds a corpse — a corpse Pickup
        // is handled earlier by the Pickup->Use rewrite, so a same-named corpse must not
        // suppress this vendor rewrite.
        if (world.Visible is not null && VisibleResolvesName(world.Visible, name, excludeCorpses: true))
            return null;
        var trimmed = name.Trim();
        foreach (var o in offers)
            if (!string.IsNullOrWhiteSpace(o.Name)
                && string.Equals(o.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return o.Name;
        return null;
    }

    // True iff the guid is in the server's DYNAMIC range (0x80000000-0xFFFFFFFE):
    // world-generated, takeable objects. Guids below this range are world-static
    // (StaticObjectMin 0x70000000+, landblock-organized) and a Pickup of them is
    // refused server-side. Mirrors ACE.Entity ObjectGuid.IsDynamic (wire-protocol
    // guid-range constant).
    internal static bool IsDynamicGuid(uint guid) => guid >= 0x80000000u && guid <= 0xFFFFFFFEu;

    // True iff this RaiseSkill goal's target skill is NOT present in the bot's
    // loaded raisable-skills list (`world.Self.TrainedSkills`, already filtered to
    // raisable). Matching mirrors the Motor's resolver (SkillRaise.TryResolveSkillId,
    // separator/case tolerant) PLUS a raw case-insensitive name fallback, so a skill
    // that IS present is never flagged. Only judges when the list is loaded (Count>0).
    // Own skill list + the goal's own target; no game knowledge, no skill list.
    internal static bool IsRaiseOfUntrainedSkill(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.RaiseSkill) return false;
        if (goal.Target.Name is not string raiseSkillName) return false;
        if (world.Self.TrainedSkills is not { Count: > 0 } trained) return false;
        if (!SkillRaise.TryResolveSkillId(raiseSkillName, out var raiseSkillId)) return false;
        return !trained.Any(ts =>
            string.Equals(ts.Name, raiseSkillName, StringComparison.OrdinalIgnoreCase)
            || (SkillRaise.TryResolveSkillId(ts.Name, out var trainedId) && trainedId == raiseSkillId));
    }

    // True iff this is a Raise* goal (attribute/skill/vital) with NO SPENDABLE unspent
    // XP. Unknown balance is treated as NOT droppable (don't assume futile before the
    // projection has loaded it). "Spendable" uses the SAME meaningful-floor predicate
    // (ShouldSurfaceUnspentXp) that gates the SPEND XP prompt cues, so the drop is
    // consistent with the prompt: below the floor the cues are suppressed AND a raise is
    // dropped (at the default floor of 0 this fires only at unspent<=0). Own self-XP wire
    // state + own goal kind; no game knowledge.
    internal static bool IsRaiseGoalWithNoSpendableXp(Goal goal, WorldStateProjection world, long minMeaningful)
        => goal.Kind is GoalKind.RaiseAttribute or GoalKind.RaiseSkill or GoalKind.RaiseVital
           && world.Self.AvailableExperience is long unspent
           && !ShouldSurfaceUnspentXp(unspent, minMeaningful);

    // Pure "hunt tapped out" perception signal. Returns a raw self-progress
    // fact string to surface to the LLM when the bot, combat-ready, has
    // dwelled in its current landblock past the threshold WITHOUT gaining a
    // level since it arrived. Returns null when not tapped out or when level
    // is unknown. The string reports ONLY the bot's own data (its own level,
    // its own dwell time, +0 levels gained); it encodes no monster type/level,
    // name, wcid, landblock, conclusion, or action directive — audit-safe.
    // The LLM owns the decision; the matching RULES bullet tells it how to act.
    internal static string? HuntTappedOutFact(
        bool combatReady, int? currentLevel, int? levelAtLandblockEntry,
        double? dwellMinutes, double dwellThresholdMinutes)
    {
        if (!combatReady) return null;
        if (currentLevel is not int lvl) return null;
        if (levelAtLandblockEntry is not int entryLvl) return null;
        if (dwellMinutes is not double dm || dm < dwellThresholdMinutes) return null;
        // Gained at least one level here → the area is still productive.
        if (lvl > entryLvl) return null;
        return $"tapped out: level {lvl}, {dm:F0} min in this area with +0 levels gained since arriving";
    }

    // Pure predicate for the stuck-timer suppression below. Returns true only when
    // currentGoal.Kind == Attack, currentFight is non-null with SwingsLanded > 0 or
    // DamageDealt > 0, and the fight's target matches the goal's selector: an exact
    // guid match when Target.Guid is set (non-zero), else a case-insensitive
    // Target.Name == TargetName match. Reads only the goal selector and combat
    // telemetry; selects no target.
    internal static bool ShouldContinueActiveMeleeOnStuck(Goal? currentGoal, CombatFightStatus? currentFight)
    {
        if (currentGoal is not { Kind: GoalKind.Attack }) return false;
        if (currentFight is not { } f) return false;
        if (f.SwingsLanded <= 0 && f.DamageDealt == 0) return false;
        // guid match when the goal is guid-pinned (non-zero), else name match.
        if (currentGoal.Target.Guid is uint goalGuid && goalGuid != 0)
            return f.TargetGuid == goalGuid;
        return f.TargetName is { Length: > 0 } fightName
            && string.Equals(currentGoal.Target.Name, fightName, StringComparison.OrdinalIgnoreCase);
    }

    private Goal? ProposeGoalCore(WorldStateProjection world, EventStream events, Goal? currentGoal)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        // Stamp the durable landblock-entry time BEFORE any early return
        // so the `minutes in current landblock` prompt signal reflects the
        // latest observation regardless of which path this tick takes
        // (in-flight poll, backoff, coalesce, fallback). See field docs.
        UpdateDwellTracking(world.Self.Landblock, world.Self.Level, events, nowUtc);
        UpdateDeathRecencyTracking(world.Self.NumDeaths, nowUtc);
        UpdateGoalProgressTracking(world, currentGoal, nowUtc);

        // Behavior-preserving diagnostic (cp024 pattern): surface why the stage-3
        // contract "DONE" note does or does not fire, so the roving-Explore
        // criterion-2 stall is precisely characterizable. Logs only on change.
        EmitContractStage3Diagnostic(world, events);

        // Every-tick reset of the contract-source diagnostic throttle (its EMISSION
        // runs later at the prompt-build point with the refreshed talked-set). This
        // must run before any early return so a finished-batch state that toggles
        // during an in-flight/backoff span still re-logs when it reappears.
        ResetContractBatchSourceDiagThrottle(world);

        // 1) Poll an in-flight call first — if it finished, consume it.
        if (_inflight is not null && _inflight.IsCompleted)
        {
            var finished = _inflight;
            _inflight = null;
            _inflightStartedAt = null;
            return ConsumeResult(finished, world, events, currentGoal, nowUtc);
        }

        // 2) Still in flight. The LlmCallTimeout CTS in RunAsync is the primary
        // guard, but a hang it does not unwind would otherwise pin _inflight
        // non-null forever and stop deliberation (observed live). If the call has
        // outlived the hard wall, abandon the orphaned task — its result, if it
        // ever lands, is ignored because nothing polls it once _inflight is null —
        // and fall through to re-deliberate. Otherwise keep doing whatever we were
        // doing while the LLM thinks.
        if (_inflight is not null)
        {
            if (!IsInflightStuck(_inflightStartedAt, nowUtc, InflightHardWall))
                return currentGoal;
            _inflightAbandonedCount++;
            Console.WriteLine(
                $"[llm-watchdog] in-flight LLM call exceeded the {InflightHardWall.TotalSeconds:F0}s hard wall " +
                "(the call-timeout did not unwind it) — abandoning it and re-deliberating so the bot does not wedge " +
                $"(abandoned={_inflightAbandonedCount} this session).");
            _inflight = null;
            _inflightStartedAt = null;
        }

        // 2.5) Stale-goal-on-teleport guard. If the bot crossed a
        // landblock boundary since our last LLM look, the prior goal
        // was derived for a world we are no longer in. Drop it from
        // the prompt anchor so the LLM re-deliberates from the new
        // observations rather than re-emitting a goal that was only
        // valid in the area we just left after a teleport into a new
        // one. The SelectorResolver landblock filter is the
        // belt; this is the suspenders that stop the LLM from
        // burning tokens re-proposing the same dead goal.
        // Mechanical plan-invalidation signals since our last LLM look,
        // computed once (also used by the source re-drive gate below).
        // Transport-failure ("could not walk") rejections are excluded by
        // HasRejectionSince — only SEMANTIC rejections invalidate.
        var landblockChangedSinceLook = HasLandblockChangeSince(events, _lastEventConsideredSequence);
        var semanticRejectSinceLook = HasRejectionSince(events, _lastEventConsideredSequence);
        // A durable inventory change (item picked up, used, given, sold) is
        // a mechanical plan-invalidating state change — unlike ambient
        // dialog/chatter — so it must END a source re-drive (and must never
        // be silently consumed by the re-drive floor-advance below).
        var inventoryChangedSinceLook = events.Recent()
            .TakeWhile(e => e.Sequence >= _lastEventConsideredSequence)
            .Any(e => e.Kind is EventKind.InventoryItemAdded or EventKind.InventoryItemRemoved);

        if (currentGoal is not null && landblockChangedSinceLook
            && currentGoal.Source != "override:hunt-egress")
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
        // a stale-goal run where an NPC kept rejecting an offered
        // quest item with TradeAiDoesntWant and the LLM kept
        // re-emitting Give(that NPC, that item) forever.
        // Transport-failure rejections (could-not-walk) are excluded
        // by HasRejectionSince — they don't invalidate the goal.
        if (currentGoal is not null && semanticRejectSinceLook)
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

        // SOURCE RE-DRIVE of an LLM-authored exploration commitment.
        // While the EXACT intent the LLM pushed alongside an Explore goal
        // remains TOP and uncompleted, re-drive that EXACT Explore WITHOUT
        // re-consulting the LLM, so the model is not pulled off its own
        // committed excursion by every tick's re-deliberation. This takes
        // precedence over the picker-start / sticky paths below.
        //
        // The commitment ends — provenance cleared, the sticky-objective free
        // re-emit BELOW is suppressed so a REAL LLM re-deliberation fires — on
        // MECHANICAL conditions only: the intent left TOP (auto-popped on its
        // LLM-authored completion e.g. monster-visible, on its deadline, or the
        // LLM popped/replaced it), is still TOP but no longer Active (e.g.
        // MarkTopBlocked in place) or past its deadline, a landblock change
        // (egress progress / a new area to re-deliberate in), a SEMANTIC
        // ActionRejected, a durable inventory change, the wall-clock stuck
        // timeout, or the liveness reinstall budget. Ambient salient events
        // (town NPC dialog, server chatter, picker churn) deliberately do NOT
        // end it — ignoring those re-deliberation triggers is the entire point.
        // Source never inspects the intent kind or any object/quest knowledge.
        //
        // Whenever a commitment ends, redriveEndedMustCallLlm forces a fresh
        // LLM decision: several break reasons (top-left/blocked/deadline,
        // budget) are NOT otherwise sticky-gate guards, so without this flag
        // the sticky path could re-emit the same inert Explore for free and
        // silently ignore the LLM-authored completion.
        var redriveEndedMustCallLlm = false;
        if (_redriveIntentId is not null && _redriveGoal is not null)
        {
            var redriveTop = _stack?.Top;
            var stillTop = redriveTop is not null
                && string.Equals(redriveTop.Id, _redriveIntentId, StringComparison.Ordinal);
            var leftTop = !stillTop;
            var topInactive = stillTop && redriveTop!.Status != IntentLifecycle.Active;
            var topDeadlinePassed = stillTop
                && redriveTop!.DeadlineUtc is DateTime dl && nowUtc.UtcDateTime >= dl;
            var budgetExhausted = _redriveReinstalls >= MaxRedriveReinstalls;
            // Same reached-target break as the sticky path below: a committed
            // Explore excursion that has ARRIVED at its named target must yield a
            // fresh LLM decision (Explore never interacts), not keep re-driving in
            // place. Without this the higher-priority re-drive path re-installs a
            // reached Explore for free, never showing the `## Reached Explore
            // target` capsule. Keys on the re-driven goal's OWN target.
            var redriveExploreReached = IsExploreToReachedTarget(_redriveGoal, world);
            if (stuck || landblockChangedSinceLook || semanticRejectSinceLook
                || inventoryChangedSinceLook || leftTop || topInactive || topDeadlinePassed
                || budgetExhausted || redriveExploreReached)
            {
                Console.WriteLine(
                    $"[strategy] re-drive ended: intent={_redriveIntentId} reason=" +
                    (stuck ? "stuck-timeout"
                     : landblockChangedSinceLook ? "landblock-changed"
                     : semanticRejectSinceLook ? "semantic-reject"
                     : inventoryChangedSinceLook ? "inventory-changed"
                     : leftTop ? "intent-left-top"
                     : topInactive ? $"top-{redriveTop!.Status}"
                     : topDeadlinePassed ? "top-deadline"
                     : redriveExploreReached ? "explore-target-reached"
                     : $"budget({_redriveReinstalls}/{MaxRedriveReinstalls})"));
                _redriveIntentId = null;
                _redriveGoal = null;
                _redriveReinstalls = 0;
                redriveEndedMustCallLlm = true;
                // fall through to the normal call/coalesce logic; the sticky
                // path below is gated off this flag so a real LLM call fires.
            }
            else
            {
                // Suppress the LLM call. Consume ambient events so they do
                // not accumulate (we have already checked every mechanical
                // break condition — incl. durable inventory changes — against
                // the current floor above, so nothing plan-invalidating is
                // being hidden).
                _lastEventConsideredSequence = events.NextSequence;
                // Preserve an in-flight active goal (do not clobber a walk
                // already executing); only reinstall when the goal cleared.
                if (currentGoal is not null)
                    return currentGoal;
                _redriveReinstalls++;
                var redriven = _redriveGoal with { Id = Guid.NewGuid(), CreatedAtUtc = nowUtc };
                Console.WriteLine(
                    $"[strategy] re-drive #{_redriveReinstalls}/{MaxRedriveReinstalls} " +
                    $"intent={_redriveIntentId} goal=Explore target={redriven.Target} (LLM call suppressed)");
                return redriven;
            }
        }

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
        // reduce-llm-call: when ShouldContinueActiveMeleeOnStuck holds and there is
        // no salient/picker wake, return the current goal on a stuck-timer instead
        // of falling through to re-deliberation. Affects only the stuck-timer LLM
        // re-invocation; the per-tick Motor handling and the salient-event (anyWake)
        // wake path are unchanged.
        if (!anyWake && stuck && ShouldContinueActiveMeleeOnStuck(currentGoal, world.CurrentFight))
            return currentGoal;
        // Non-picker salient events still respect the coalesce window; the
        // picker arrival + new-target picker-start paths bypass it. The `&& !stuck`
        // guard ensures the stuck-timer re-deliberation backstop always punches
        // through, so a (capped, but defensively also a hypothetical caller-set)
        // MinCallInterval can never suppress re-deliberation past StuckTimeout.
        if (coalesce && currentGoal is not null && !pickerArrived && !pickerStartWake && !stuck) return currentGoal;

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
        // break-sticky-on-self-interact: a spatial Use/Pickup whose target the
        // Motor ALREADY interacted with since the last LLM look must NOT be
        // free-re-driven. Live loop (cp-2304): the LLM picked Use{Door}; using
        // the door returns server UseDone(ok) but moves the bot nowhere and
        // emits NO external salient event, so this gate re-drove the same
        // Use{Door} 3x for free (~18s) while the picker kept re-arriving at it.
        // The Motor's own once-per-cycle WorldObjectInteracted echo (the same
        // signal that feeds the `## Recently interacted objects` prompt section,
        // cp-2291) proves the objective was already ATTEMPTED; force a real LLM
        // call so the model re-reads that telemetry and picks differently. This
        // does NOT claim the objective semantically SUCCEEDED — only that it was
        // attempted without an external change, so a free retry is pointless.
        // Pure identity bookkeeping (guid/name/wcid match, selector AND
        // semantics); no game knowledge.
        var stickyAlreadyAttempted = _lastLlmGoal is not null
            && SelfAlreadyInteractedWithGoalTarget(events, _lastLlmGoal);
        if (currentGoal is null && stickyAlreadyAttempted
            && !hasNonPickerExternal && !pickerArrived && !pickerStartWake)
        {
            Console.WriteLine(
                $"[strategy] sticky re-emit broken: self already interacted with " +
                $"{_lastLlmGoal!.Kind} target={_lastLlmGoal.Target} since last LLM look " +
                "— forcing fresh LLM decision (recently-interacted telemetry)");
        }
        // break-sticky-on-reached-explore (cp021 complement): a NAMED Explore
        // goal whose target the bot has now REACHED (it is within reach in
        // `Visible nearby`) must NOT be free-re-driven. Explore is navigate-only —
        // arrival sends no opcode — so re-driving the same Explore leaves the bot
        // standing beside the target doing nothing, AND the decision-proximate
        // `## Reached Explore target` capsule (which tells the LLM to switch to an
        // interaction verb) is never shown, because a sticky re-emit skips the LLM
        // call entirely. Force a real LLM decision AT the arrival moment instead of
        // after up to MaxStickyReEmits wasted in-place re-drives. Pure mechanical
        // bookkeeping (the bot's OWN Explore goal + a name-matched visible object
        // within reach); no game knowledge, no source-side interaction decision —
        // the LLM still chooses whether/how to interact or to move on.
        var stickyExploreReached =
            IsExploreToReachedTarget(_lastLlmGoal, world);
        if (currentGoal is null && stickyExploreReached
            && !hasNonPickerExternal && !pickerArrived && !pickerStartWake)
        {
            Console.WriteLine(
                "[strategy] sticky re-emit broken: reached named Explore target " +
                $"{_lastLlmGoal!.Target} — forcing fresh LLM decision (switch to interaction)");
        }
        // Budget exemption for a NON-TARGETED Explore (schema "anywhere"
        // sentinel). The MaxStickyReEmits cap exists to stop spin on an
        // UNREACHABLE named target; a targetless Explore has no object target
        // and is never "unreachable", so the cap does not apply to it. While
        // crossing toward open country a bare Explore should re-drive for free
        // until a GENUINE wake — a new picker target/arrival (a discovered
        // object), any non-picker external change, a landblock change, or the
        // stuck-timeout backstop — every one of which still breaks the gate
        // below. Only the retry-count cap is lifted; a TARGETED Explore
        // (named/guid/wcid/...) keeps the cap. Pure goal-shape bookkeeping;
        // the schema sentinel is not game knowledge.
        var stickyUntargetedExplore = IsUntargetedExploreGoal(_lastLlmGoal);
        if (currentGoal is null
            && _lastLlmGoal is not null
            && !stuck
            && !redriveEndedMustCallLlm
            && (_stickyReEmitCount < MaxStickyReEmits || stickyUntargetedExplore)
            && !hasNonPickerExternal
            && !pickerArrived
            && !pickerStartWake
            && !stickyAlreadyAttempted
            && !stickyExploreReached)
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
                $"[strategy] sticky-objective re-emit #{_stickyReEmitCount}" +
                (stickyUntargetedExplore ? " (untargeted-Explore: budget-exempt)" : $"/{MaxStickyReEmits}") +
                $" kind={sticky.Kind} target={sticky.Target}" +
                (sticky.Item is null ? "" : $" item={sticky.Item}") +
                " (no external salient event since last LLM look; skipping LLM call)");
            return sticky;
        }

        // Autonomous kill-intent decomposition (reduce-llm-call-volume).
        // [INVARIANT — audit] The Motor only DECOMPOSES an LLM-authored TYPED
        // kill-count commitment ("kill N [of X]") into the next Attack; it never
        // ORIGINATES a combat commitment. We fire ONLY on the quiescent
        // post-kill path: currentGoal == null AND no decision-worthy change has
        // arrived since the last LLM look (FirstChainInterruptingKindSince — an
        // inventory change, NPC dialog, readable text, or an action
        // rejection incl. a fresh disengage all route to the LLM instead; a
        // kill's own combat ServerMessage/feedback/damage do NOT (nor does a
        // landblock crossing — see IsChainInterruptingKind), so the chain
        // is not made inert by its own kills). So a genuinely decision-worthy
        // event is never masked by one more autonomous Attack. If the stack top
        // is a kill-count commitment and a matching,
        // in-perception, not-beaten hostile is visible, mint that Attack WITHOUT
        // the LLM round-trip (returning it as currentGoal so the Motor drives
        // it). Flee precedence is enforced downstream by the Motor's dispatch
        // self-preservation gate (it refuses the Attack while health is low /
        // the threat is on the avoid cooldown) and the losing-fight disengage
        // reflexes. Bounded by MaxCombatChainAttacks (periodic forced LLM
        // re-check), the intent's completion predicate + deadline, and the
        // chain-interrupting-event routing above.
        //
        // NOT gated by the wall-clock stuck-timeout. `stuck` means "no LLM call
        // in the last StuckTimeout" — a re-engagement backstop for IDLE/aimless
        // ticks. But minting the next Attack toward an ACTIVE kill-count
        // commitment with a matching target IN VIEW is the productive action, not
        // idleness. A single kill cycle (travel + swing + post-action cooldown)
        // routinely exceeds StuckTimeout, so gating the chain on !stuck starved it
        // to zero mints — every kill fell back to a per-kill LLM round-trip, the
        // exact reduce-llm-call-volume regression this decomposition exists to
        // remove. The backstop's purpose survives WITHOUT the gate: when no
        // committed target is in view ChooseCombatChainTarget returns null and
        // control falls through to the normal stuck-timeout LLM call below.
        //
        // ALSO not gated by the autonomous picker's arrival/start (pickerArrived
        // / pickerStartWake). Those flags wake the LLM to name a verb when the
        // picker parks at / switches to a target it chose on its own — a safety
        // valve against standing idle next to a discovery. That valve is
        // REDUNDANT here: the chain already supplies the verb (Attack) for a
        // matching committed target, so the chain minted 0/N live (no-mint reason
        // gate:picker-arrived after each kill) while the LLM was re-consulted for
        // a target it had already committed to kill. The valve survives WITHOUT
        // the gate: the chain acts ONLY on a matching committed monster — a
        // non-matching pick (an NPC, a portal, a different kind) yields no chain
        // target, so ChooseCombatChainTarget returns null and control falls
        // through to the LLM, which still weighs the discovery.
        var chainCommitmentActive = CombatCommitment.IsActiveKillCommitment(_stack?.Top, out _);
        var chainInterruptingKind = FirstChainInterruptingKindSince(events, _lastEventConsideredSequence);
        var chainInterrupting = chainInterruptingKind is not null;
        string? chainNoMintReason = null;
        if (currentGoal is null && !chainInterrupting)
        {
            var chainTarget = ChooseCombatChainTarget(
                _stack?.Top,
                world.Visible,
                world.CombatHistoryFull,
                world.Self.Level,
                CombatChainEnabled,
                _combatChainCount,
                MaxCombatChainAttacks,
                out var chainSkipReason,
                combatCapable: IsCombatCapable(world.Inventory),
                canUnarmedMelee: CanUnarmedMelee(world.Inventory));
            if (chainTarget is not null)
            {
                _combatChainCount++;
                _lastChainNoMintReason = null;
                // Consume the events pending at this mint so a deliberately
                // ignored picker arrival/start (and own-loot / combat-progress)
                // does NOT linger and kick a redundant LLM call on a later tick
                // — that call would reset _combatChainCount and collapse the
                // MaxCombatChainAttacks batch to a single mint, defeating the
                // tempo win. The gate above proved none are chain-interrupting,
                // so this hides nothing decision-worthy: the LLM re-reads the
                // full world at the next bounded re-engagement. Mirrors the
                // sticky-objective re-emit floor-advance + picker-start record.
                _lastEventConsideredSequence = events.NextSequence;
                if (pickerStartWake && pickerStartKey is not null)
                {
                    _lastPickerStartWakeKey = pickerStartKey;
                    _lastPickerStartWakeAtUtc = nowUtc;
                }
                var commitmentSummary = _stack?.Top?.Completion.Summary() ?? "kill-count";
                Console.WriteLine(
                    $"[combat-chain] mint #{_combatChainCount}/{MaxCombatChainAttacks} " +
                    $"Attack target=0x{chainTarget.Guid:X8} '{chainTarget.Name}' " +
                    $"d={(chainTarget.Distance is float cd ? cd.ToString("F1") : "?")} " +
                    $"until={commitmentSummary} " +
                    "(no LLM call — decomposing LLM-authored kill-count commitment)");
                return new Goal
                {
                    Id = Guid.NewGuid(),
                    Kind = GoalKind.Attack,
                    Target = new Selector { Guid = chainTarget.Guid, Name = chainTarget.Name },
                    Priority = 7,
                    Rationale =
                        $"autonomous kill-intent decomposition (#{_combatChainCount}/{MaxCombatChainAttacks}): {commitmentSummary}",
                };
            }
            // Chain gate was OPEN with an active commitment yet nothing minted —
            // surface the internal reason (e.g. no-matching-monster: the committed
            // kind is not visible) so the chain-never-fires tempo gap is diagnosable.
            if (chainCommitmentActive) chainNoMintReason = chainSkipReason ?? "no-target";
        }
        else if (chainCommitmentActive)
        {
            // A commitment is active but the chain GATE is closed — the LLM is
            // consulted instead. Record WHICH gate starved the chain: either a
            // sticky goal redrive is active (currentGoal != null) or a
            // genuinely decision-worthy interrupting event (item-removal, dialog,
            // zone, readable, rejection) arrived. The wall-clock stuck-timeout and
            // the autonomous picker's arrival/start are deliberately NOT chain
            // gates (see above), so they are not reasons here.
            chainNoMintReason =
                currentGoal is not null ? "gate:sticky-goal-redrive"
                : $"gate:chain-interrupting-event:{chainInterruptingKind}";
        }
        if (chainNoMintReason is not null && chainNoMintReason != _lastChainNoMintReason)
        {
            _lastChainNoMintReason = chainNoMintReason;
            Console.WriteLine(
                $"[combat-chain] no-mint reason={chainNoMintReason} " +
                $"commitment={_stack?.Top?.Completion.Summary() ?? "?"} " +
                $"budget={_combatChainCount}/{MaxCombatChainAttacks}");
        }

        // Reduce-llm-call-volume (default ON; opt OUT via AC_BOTS_SKIP_FIXATED_TALK_CALL=0/false/off).
        // A PROVEN stale single-NPC Talk fixation with NO new decision-worthy change
        // since the last LLM look: re-deliberating most often just reproduces a Talk the
        // fixation guards immediately drop to the break-contact egress, so skip the
        // redundant call and reach that SAME EscapeOrFallback directly. This is a bounded
        // request-tempo heuristic, NOT strict behavioral equivalence — the freshness gates
        // ensure the bot never skips over genuinely new input it could act on:
        //   - `!hasNonPickerExternal`: IsExternalChangeKind = (salient AND not
        //     goal-lifecycle) OR InventoryItemRemoved, so a fresh NpcDialog / PopupString /
        //     BookText / ServerMessage / inventory add-or-remove / zone change /
        //     ActionRejected BLOCKS the skip (it EXCLUDES the bot's OWN goal-lifecycle, so
        //     the gate still fires on the dominant no-current-goal post-Talk tick);
        //   - `!pickerArrived && !pickerStartWake`: a NEW autonomous picker discovery
        //     (corpse/door/portal/ground-item) BLOCKS the skip so the LLM can choose
        //     Pickup/Use on it rather than being forced into a break-contact Explore
        //     (mirrors the empty-explore skip's picker guard);
        //   - `!HasNewStrategicIntentCompletionSince`: an intent-stack pop BLOCKS the skip;
        //   - `!StackHasNoActiveObjective`: a non-Active top (incl. a root deadline-Blocked
        //     in place, which emits no lifecycle event) BLOCKS the skip so the bot wakes to
        //     re-plan rather than escape-Explore while objective-less.
        // Self-limiting: the egress's Explore goals age the Talk fixation out of the
        // history window within a few decisions, re-engaging the LLM. NOTE: unlike the
        // post-LLM Talk-fixation drop site this does NOT call RecordTalkLoopSuppression
        // (no LLM goal/guid here) — the gate re-detects the fixation itself. Enabled by
        // default because the saved call is redundant in the common case and the egress is
        // identical to the post-LLM fixation-drop path.
        if (SkipFixatedTalkCallEnabled
            && SkipGateFreshnessAllows(hasNonPickerExternal, pickerArrived, pickerStartWake, events)
            && ProvenTalkFixationNameFromHistory(events) is string fixatedTalkName)
        {
            Console.WriteLine(
                $"[llm-skip] proven stale Talk fixation on \"{fixatedTalkName}\" with no new external " +
                "event — skipping the redundant LLM call; deferring to break-contact egress/fallback.");
            _summarySkips++;
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, fixatedTalkName);
        }

        // Reduce-llm-call-volume (default ON; opt OUT via AC_BOTS_SKIP_EMPTY_EXPLORE_CALL=0/false/off).
        // When the bot is on a sustained UNTARGETED Explore (its last emitted goal was
        // `Explore{anywhere}` — pure Motor-owned travel with nothing to interact with),
        // with NOTHING WINNABLE in view to engage (no attackable monster, OR only
        // non-hostile beaten-kind monsters the Attack veto already rejects — re-asking
        // re-picks the SAME vetoed Attack; no vendor; no un-talked NPC) and NO
        // decision-worthy change since the last LLM look, re-deliberating just
        // reproduces the SAME untargeted Explore — so skip the redundant call and continue
        // traveling. The beaten-only arm reuses OnlyBeatenMonstersInView (the same
        // lethal-beaten ledger predicate the stalemate egress uses): a winnable
        // (not-beaten) monster OR an actively-hostile one in view makes it false, so the
        // skip never hides a fresh XP target or a live threat (those re-wake the LLM). The freshness gates are a SUPERSET of the Talk-fixation skip's:
        //   - !hasNonPickerExternal: a fresh dialog / inventory change / zone change /
        //     rejection / damage BLOCKS the skip (the LLM sees it);
        //   - !pickerArrived && !pickerStartWake: the autonomous picker discovering or
        //     arriving at an interactable (door/portal/corpse/ground item/weapon) BLOCKS
        //     the skip, so a discovery is never hidden behind the travel skip;
        //   - !HasNewStrategicIntentCompletionSince: an intent-stack pop / top-objective
        //     change BLOCKS the skip so the bot re-deliberates the new objective;
        //   - !StackHasNoActiveObjective: a non-Active top (incl. a root deadline-Blocked
        //     IN PLACE, which emits no lifecycle event) BLOCKS the skip so the bot wakes to
        //     re-plan rather than auto-Explore while objective-less.
        // Bounded by MaxEmptyExploreSkips so the LLM re-chooses the heading periodically.
        // Default ON (opt out with the env var); request-tempo only — the bot continues its
        // OWN prior untargeted-travel decision, no new target is picked (no source-side
        // interaction choice).
        if (SkipEmptyExploreCallEnabled
            && SkipGateFreshnessAllows(hasNonPickerExternal, pickerArrived, pickerStartWake, events)
            && _emptyExploreSkips < MaxEmptyExploreSkips
            && (!AnyAttackableMonsterInView(world) || OnlyBeatenMonstersInView(world))
            && !world.Visible.Any(v => v.IsVendor)
            && CountUntalkedNpcsInView(world, _talkedNpcGuids, _talkedNpcNames, excludeVendors: true) == 0
            && LastEmitWasUntargetedExplore(events))
        {
            _emptyExploreSkips++;
            _summarySkips++;
            Console.WriteLine(
                "[llm-skip] sustained Explore travel: nothing winnable in view to engage (no monster, or " +
                "only non-hostile beaten kinds) and no new event since the last look — skipping the redundant " +
                $"LLM call and continuing to Explore ({_emptyExploreSkips}/{MaxEmptyExploreSkips}).");
            return MakeEgressExploreGoal(
                nowUtc, "skip:empty-explore",
                "continuing an untargeted Explore through empty space without a redundant LLM call");
        }
        _emptyExploreSkips = 0;

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
        // cp-2359: project the current landblock's world-Use churn episode (the
        // bot's OWN distinct-object Use count + whether the distinct-tour egress
        // guard has latched) so the prompt can state the no-progress activity as
        // a fact. Only when the episode belongs to the current landblock.
        (int Distinct, bool Latched)? localUseChurn =
            _worldUseChurnEpisode is { } ue && ue.Landblock == world.Self.Landblock && ue.UseCounts.Count > 0
                ? (ue.UseCounts.Count, ue.DistinctChurnLatched)
                : null;
        RecordTalkedNpcs(events);
        // Behavior-preserving diagnostic (cp030 pattern): when the bot holds a
        // FINISHED contract batch, surface whether a contract SOURCE is actionable
        // in view (so a non-engage is the LLM ignoring it) or not (the source is
        // out of view with no navigate-back signal). Emitted AFTER RecordTalkedNpcs
        // and right before BuildUserPrompt so it reads the SAME refreshed talked-set
        // the FIND-A-KILL-TASK-SOURCE rule uses. Console-only; logs on change;
        // resets when the finished-batch state is absent.
        EmitContractBatchSourceDiagnostic(world);
        var userPrompt = BuildUserPrompt(world, events, currentGoal, _stack, _currentPickerActivity, _currentExplorationCandidates, dwellEntry, _currentRecentSightings, _levelAtCurrentLandblockEntry, SecondsSinceLastOwnDeath(nowUtc), BuildGoalProgressSnapshot(), _currentUnreachableTargets, _currentApproachDistance, _currentExcursionCoverage, _currentFreshKillCorpses, _currentLootedEmptyCorpses, localUseChurn, _talkedNpcGuids, _talkedNpcNames, promptCeiling: _adaptivePromptCeiling, recentOwnDeathCount: RecentOwnDeathCount(nowUtc));
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
        var sentSections = PromptSectionHeaders(userPrompt);
        Console.WriteLine(
            $"[llm-call] kickoff id={decisionId} trigger={trigger} " +
            $"prompt-bytes={userPrompt.Length} dwell-min={dwellMinStr} model={_client.Model} " +
            $"sections={sentSections.Count}[{string.Join("|", sentSections)}]");

        // Run-summary diagnostic: aggregate decision tempo + progression and emit
        // one [run-summary] line every SummaryIntervalDecisions kickoffs so a run is
        // self-reporting (stuck-in-landblock, flat level, trigger/loop mix) without
        // manual log archaeology. Pure observability; no behavior change.
        _summaryDecisions++;
        _summaryTriggers[trigger] = _summaryTriggers.GetValueOrDefault(trigger) + 1;
        if (world.Self.Landblock is uint summaryLb) _summaryLandblocks.Add(summaryLb);
        if (world.Self.NumDeaths is int nd0) _summaryBaselineDeaths ??= nd0;
        if (_summaryDecisions % SummaryIntervalDecisions == 0 && !_summaryEmittedThisTick)
            EmitRunSummary(world, events);
        _tempo.RecordLlmCall();

        _inflight = RunAsync(userPrompt, decisionId, projJson, eventSeqAtCallStart, currentGoal is not null);
        _inflightStartedAt = DateTimeOffset.UtcNow;
        return currentGoal; // keep doing whatever we were doing while the LLM thinks
    }

    // Run-summary diagnostic aggregates (pure observability; no behavior change).
    internal const int SummaryIntervalDecisions = 15;
    private int _summaryDecisions;
    // Wall-clock of the last [run-summary] emit. Under sustained LLM 429 (or any
    // low-kickoff state) the every-15-kickoffs trigger rarely fires, so a struggling
    // run would go dark; a time-based fallback (SummaryMaxIntervalSeconds) keeps the
    // run self-reporting liveness + progress regardless of kickoff rate. Initialized
    // to construction time so the first time-based emit is one interval in, not at
    // tick 0 with empty aggregates.
    private DateTimeOffset _lastSummaryEmitAtUtc = DateTimeOffset.UtcNow;
    internal const int SummaryMaxIntervalSeconds = 300;
    // Reset at the top of each ProposeGoal tick; set when EmitRunSummary fires, so
    // the time-based and every-15-kickoffs triggers never double-emit on one tick.
    private bool _summaryEmittedThisTick;
    // Count of LLM calls SKIPPED by the reduce-llm-call-volume gates (fixated-talk +
    // empty-explore) since the run started — surfaced in [run-summary] so a run
    // self-reports how many redundant calls the tempo gates saved (the standing
    // reduce-llm-call-volume goal's per-run effect). Pure observability.
    private int _summarySkips;
    // Count of LLM-emitted Attacks the beaten-kind veto DROPPED this run (the bot
    // ordered combat on a KIND its own ledger marks beaten/un-out-leveled). The
    // dominant open-world override when the bot is in territory too tough for it;
    // read against kills= it shows how many combat decisions land on un-winnable
    // targets vs actual kills. Surfaced as beaten-vetoes=N in [run-summary]. Pure
    // observability.
    private int _summaryBeatenVetoes;
    // NumDeaths observed at the first decision of this run. [run-summary] reports
    // current NumDeaths minus this baseline = deaths THIS run. Null until first
    // observed.
    private int? _summaryBaselineDeaths;
    // DISTINCT vendor guids seen this run while the bot held a finished contract
    // batch (a contract-refresh BUY opportunity). A set (not a per-tick counter) so
    // lingering at or re-approaching the same vendor counts ONCE; surfaced as
    // refresh-opps=N in [run-summary] so a run self-reports whether the bot acts on
    // the criterion-2 contract refresh or stalls. Pure observability.
    private readonly HashSet<uint> _summaryRefreshVendorGuids = new();
    private readonly Dictionary<string, int> _summaryTriggers = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _summaryLandblocks = new();

    // True when at least SummaryMaxIntervalSeconds have elapsed since the last
    // [run-summary] emit — the time-based fallback trigger that keeps a low-kickoff
    // (e.g. 429-walled) run self-reporting. Pure predicate, unit-testable.
    internal static bool ShouldEmitTimeBasedSummary(DateTimeOffset lastEmitUtc, DateTimeOffset nowUtc)
        => (nowUtc - lastEmitUtc).TotalSeconds >= SummaryMaxIntervalSeconds;

    // Build + print the [run-summary] line from the current aggregates and reset the
    // time-based emit clock. Called by BOTH triggers (every-N-kickoffs and the
    // time-based fallback) so they share one format and one clock. Pure observability.
    private void EmitRunSummary(WorldStateProjection world, EventStream events)
    {
        var deathsThisRun = ComputeRunDeaths(world.Self.NumDeaths, _summaryBaselineDeaths);
        Console.WriteLine(BuildRunSummaryLine(
            _summaryDecisions, _summaryTriggers, _summaryLandblocks.Count,
            world.Self.Landblock, world.Self.Level, world.Self.TotalExperience, _client.Model,
            TopRepeatedGoalEmitLabel(events, SummaryIntervalDecisions), _summarySkips,
            FormatContractCounts(world.Contracts), _stack?.Depth, _summaryRefreshVendorGuids.Count,
            world.CumulativeSwingsLanded, world.CumulativeSwingsEvaded, deathsThisRun,
            IsCombatCapable(world.Inventory), world.Self.HealthObservedPeak, world.Self.CoinValue,
            world.Self.AvailableExperience, RecentGoalFailureCount(events),
            FormatCombatAttributes(world.Self.Attributes), world.CumulativeKills, _summaryBeatenVetoes));
        _lastSummaryEmitAtUtc = DateTimeOffset.UtcNow;
        _summaryEmittedThisTick = true;
    }

    // Build the periodic [run-summary] line: decision count, trigger histogram,
    // distinct landblocks visited + the last one, level + lifetime XP, and the
    // active LLM model. Lets a run self-report stuck-in-landblock / flat-level /
    // loop-trigger patterns at a glance. Pure formatting over already-observed run
    // state; no game knowledge, no behavior.
    // Compact contract-state summary for [run-summary]: total tracked contracts
    // with the in-progress / done breakdown, e.g. "5(p3/d2)". Surfaced so a run
    // self-reports criterion-2/3 contract THROUGHPUT at a glance — whether batches
    // advance to done and then refresh (counts cycle) or stall at done. Returns
    // null when no contracts are tracked. Pure read of the wire ContractStage
    // codes (source ACE ContractTracker.cs: 1 Available, 2 InProgress, 3
    // DoneOrPendingRepeat, 4+ ProgressCounter); no game knowledge, no behavior.
    internal static string? FormatContractCounts(IReadOnlyList<ContractProjection>? contracts)
    {
        if (contracts is null || contracts.Count == 0) return null;
        var inProgress = 0;
        var done = 0;
        for (var i = 0; i < contracts.Count; i++)
        {
            var stage = contracts[i].Stage;
            if (stage == 3u) done++;
            else if (stage == 2u || stage >= 4u) inProgress++;
        }
        return $"{contracts.Count}(p{inProgress}/d{done})";
    }

    // Per-run deaths = current lifetime NumDeaths minus the run-start baseline, or
    // null when either is unknown. Pure derivation (extracted for testing).
    internal static int? ComputeRunDeaths(int? currentNumDeaths, int? baseline)
        => currentNumDeaths is int cur && baseline is int b ? cur - b : (int?)null;

    // Count of ALL recent terminal goal failures in the durable GoalFailed window
    // (EventStream.RecentGoalFailures — ~30 min, capped, survives perception eviction). A
    // failure is a goal the Motor could not complete: a selector that resolved to no live
    // object (a dispatch-MISS), plus deferred/validation Fails. Surfaced as fails= in
    // [run-summary] so a run self-reports its recent failure load (not a pure churn count).
    // Pure read of the bot's OWN failure history; no behavior change, no game knowledge.
    internal static int RecentGoalFailureCount(EventStream? events)
        => events?.RecentGoalFailures().Count ?? 0;

    // The bot's key COMBAT attribute base values (endurance -> max HP / survival, coordination
    // -> accuracy, strength -> damage) for [run-summary], e.g. "end:13 coord:10 str:47". Surfaces
    // the allocation STATE behind hppeak= / swings= (the EFFECTS): e.g. a rising endurance whose
    // max-HP gain is masked by death-vitae, or a low coordination capping accuracy, are visible
    // here when the effect fields alone are ambiguous. Shown only when at least one of the three
    // is known. Pure read of the bot's OWN live attribute projection; no behavior, no game knowledge.
    internal static string? FormatCombatAttributes(IReadOnlyList<SelfAttributeProjection>? attrs)
    {
        if (attrs is null || attrs.Count == 0) return null;
        uint? Get(string n) =>
            attrs.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))?.Base;
        var parts = new List<string>(3);
        if (Get("endurance") is uint e) parts.Add($"end:{e}");
        if (Get("coordination") is uint c) parts.Add($"coord:{c}");
        if (Get("strength") is uint s) parts.Add($"str:{s}");
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    internal static string BuildRunSummaryLine(
        int decisions, IReadOnlyDictionary<string, int> triggerCounts,
        int distinctLandblocks, uint? lastLandblock, int? level, long? totalXp, string model,
        string? topEmit = null, int skips = 0, string? contracts = null, int? intentDepth = null,
        int refreshOpps = 0, int swingsLanded = 0, int swingsEvaded = 0, int? deathsThisRun = null,
        bool armed = true, int? maxHpProxy = null, int? coin = null, long? unspent = null,
        int recentFails = 0, string? combatAttrs = null, int kills = 0, int beatenVetoes = 0)
    {
        var triggers = triggerCounts.Count == 0
            ? "-"
            : string.Join(",", triggerCounts
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
        var lb = lastLandblock is uint l ? $"0x{l:X4}" : "?";
        var line = $"[run-summary] decisions={decisions} triggers={{{triggers}}} " +
               $"distinct-landblocks={distinctLandblocks} last-landblock={lb} " +
               $"level={(level?.ToString() ?? "?")} total-xp={(totalXp?.ToString() ?? "?")} active-model={model}";
        // Reduce-llm-call-volume effect: how many redundant LLM calls the tempo skip
        // gates have saved this run (shown only when >0). Pure observability.
        if (skips > 0)
            line += $" skips={skips}";
        // Loop-detector field: the single most-repeated recent goal emission, shown
        // only when it recurs (>=2). A goal the bot re-emits many times is the
        // signature of a fixation OR an unresolved-target (target=MISS) loop — the
        // dominant gap class — so surfacing it makes a run self-report the loop
        // without manual log archaeology. Purely a structural read of the bot's OWN
        // emission history; no behavior change, no game knowledge.
        if (!string.IsNullOrEmpty(topEmit))
            line += $" top-emit={topEmit}";
        // Recent failure-load signal: count of ALL recent terminal goal failures in the
        // durable GoalFailed window (~30 min) — a goal the Motor could not complete (a
        // selector that resolved to no live object, plus deferred/validation Fails). A compact
        // gauge of how many goals are failing that no other field shows: top-emit= surfaces
        // only the SINGLE most-repeated emission, and the trigger histogram shows WHY
        // deliberation happened, not how many goals failed — so a multi-target churn (failures
        // spread across several targets) shows up here even when top-emit's #1 is small. NOTE
        // the windows differ (top-emit= reads the last N emissions, this is ~30 min), so read
        // it as a load gauge, not a per-interval or pure-churn count. Shown only when >0. Pure
        // observability; no behavior change.
        if (recentFails > 0)
            line += $" fails={recentFails}";
        // Criterion-2/3 contract throughput: tracked contracts with the
        // in-progress/done breakdown (shown only when any are tracked). Lets a run
        // self-report whether contract batches advance to done and then refresh, or
        // stall. Pure observability; no behavior change, no game knowledge.
        if (!string.IsNullOrEmpty(contracts))
            line += $" contracts={contracts}";
        // Criterion-3 plan-compilation signal: the strategic IntentStack depth. The
        // LLM compiles NPC/quest dialog into pushed Intents (a Plan). intents=0 means
        // no active strategic stack, intents=1 means a single active strategic
        // intent, intents>1 means a nested/multi-step compiled plan. Watching the
        // depth across a run shows whether plans get compiled and persist. Shown when
        // known (>=0). Pure structural read of the bot's OWN strategic state; no
        // behavior change, no game knowledge.
        if (intentDepth is int depth && depth >= 0)
            line += $" intents={depth}";
        // Criterion-2 contract-refresh opportunity count: distinct situations this
        // run where the bot held a finished contract batch AND a vendor was in view
        // (a chance to Buy a fresh contract). Shown only when >0. Read together with
        // `contracts=` (state): refresh-opps>0 while the done-batch never refreshes
        // self-reports the buy-gap. Pure observability; no behavior change.
        if (refreshOpps > 0)
            line += $" refresh-opps={refreshOpps}";
        // Economy resource: the bot's server-tracked coin balance (Self.CoinValue). The
        // arming + contract-refresh economy is gated on coin (a contract/weapon costs
        // coin the bot may not have). Shown when known (>=0). Read with refresh-opps= +
        // armed=: coin near 0 while refresh-opps>0 / armed=no self-reports a
        // coin-starvation wall vs a model-priority gap. Pure observability; no behavior
        // change, no game knowledge.
        if (coin is int cn && cn >= 0)
            line += $" coin={cn}";
        // Unspent (raisable) experience — shown when known + >0 (the hoarding signal;
        // 0 = nothing to spend, omitted). Pairs with swings= / armed= to flag XP the
        // bot is not spending. Pure observability; no behavior change, no game knowledge.
        if (unspent is long ux && ux > 0)
            line += $" unspent={ux}";
        // Combat OUTCOME: foes the bot was fighting that died this run
        // (CumulativeKills). Pairs with swings= (accuracy attempts) — landing
        // swings with kills=0 means the target out-defends/out-heals you; kills
        // rising is the core open-world productivity signal that total-xp=
        // reflects only indirectly. Shown only when >0. Pure observability; no
        // behavior change, no game knowledge.
        if (kills > 0)
            line += $" kills={kills}";
        // Beaten-kind veto count: LLM Attacks dropped this run because the target KIND
        // is on the bot's own beaten ledger (too tough). Read against kills= it shows
        // the combat-decision efficiency — a high beaten-vetoes with low kills means
        // the bot is in territory too tough for it (choosing un-winnable targets) and
        // should get stronger or relocate. Shown only when >0. Pure observability.
        if (beatenVetoes > 0)
            line += $" beaten-vetoes={beatenVetoes}";
        // Combat-effectiveness signal: surface the session swing-outcome counters
        // (CumulativeSwingsLanded / CumulativeSwingsEvaded) in [run-summary], shown
        // only when at least one has incremented. Pure observability; no behavior
        // change, no game knowledge.
        if (swingsLanded + swingsEvaded > 0)
            line += $" swings={swingsLanded}L/{swingsEvaded}E";
        // Deaths recorded THIS run (current lifetime NumDeaths minus the run-start
        // baseline), shown only when >0. Pure observability; no behavior change.
        if (deathsThisRun is int dr && dr > 0)
            line += $" deaths={dr}";
        // Peak current HP observed this run (HealthObservedPeak, a max-HP proxy).
        // Pairs with swings= and deaths= as a combat-effectiveness diagnostic. Shown
        // only when known + positive. Pure observability; no behavior change, no game
        // knowledge.
        if (maxHpProxy is int mhp && mhp > 0)
            line += $" hppeak={mhp}";
        // Combat-attribute STATE behind the survival/accuracy EFFECT fields (hppeak=/swings=):
        // endurance/coordination/strength base values, shown when known. Lets a run self-report
        // whether the bot's XP-allocation is actually moving the right stat (e.g. endurance
        // rising while hppeak stays low = death-vitae masking the gain). Pure observability.
        if (!string.IsNullOrEmpty(combatAttrs))
            line += $" attrs=[{combatAttrs}]";
        // Append armed=no when the bot has NO combat-capable wielded weapon
        // (IsCombatCapable over the bot's OWN wielded inventory returns false).
        // Shown only in that state, mirroring the deaths= field. Pure
        // observability over wielded-inventory wire state; no behavior change.
        if (!armed)
            line += " armed=no";
        return line;
    }

    /// <summary>
    /// The most-repeated goal emission (keyed by verb + target identity — guid token
    /// if present, else name) among the last <paramref name="window"/> GoalEmitted
    /// events, returned as a <c>[verb target]xN</c> label ONLY when it recurs
    /// (count &gt;= 2). Mirrors the GoalEmitted Text parse used by the repeat-loop
    /// guards (Tactics formats it as <c>&lt;Kind&gt; target=&lt;Selector&gt; item=...
    /// source=...</c>). A run that re-emits the same goal — a fixation, or an
    /// unresolved-target re-emit loop — self-reports it in <c>[run-summary]</c>.
    /// Pure structural read of the bot's OWN emission history; no game knowledge.
    /// </summary>
    internal static string? TopRepeatedGoalEmitLabel(EventStream events, int window)
    {
        var recent = events.RecentGoalEmissions()
            .Where(e => !string.IsNullOrEmpty(e.Text))
            .Take(window)
            .ToList();
        if (recent.Count == 0) return null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? topKey = null;
        var topCount = 0;
        foreach (var ge in recent)
        {
            var txt = ge.Text!;
            var sp = txt.IndexOf(' ');
            var kind = sp > 0 ? txt[..sp] : txt;
            var key = kind;
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti >= 0)
            {
                // Identity of the target selector. Robust against a name that itself
                // contains " item="/" source=" — a bounded `target=(.*?) item=` regex
                // truncates such names (the same hazard CountRecentTalkGoalsToName
                // calls out). The selector prints `guid=...` before `name="..."`, so a
                // guid-LED target keys by its guid (the leading token); otherwise the
                // first `name="..."` after `target=` is the target's (a name-only item
                // appears later), and the quote run [^"]+ safely spans embedded tokens.
                var after = txt[ti..];
                var body = after.Length > 7 ? after[7..] : string.Empty;
                if (body.StartsWith("guid=0x", StringComparison.Ordinal))
                {
                    var gm = System.Text.RegularExpressions.Regex.Match(body, "^guid=0x[0-9A-Fa-f]+");
                    if (gm.Success) key = kind + " " + gm.Value;
                }
                else
                {
                    var nm = System.Text.RegularExpressions.Regex.Match(after, "name=\"([^\"]+)\"");
                    if (nm.Success) key = kind + " " + nm.Groups[1].Value;
                }
            }
            var c = counts.GetValueOrDefault(key) + 1;
            counts[key] = c;
            if (c > topCount) { topCount = c; topKey = key; }
        }
        return topCount >= 2 && topKey is not null ? $"[{topKey}]x{topCount}" : null;
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
            // HTTP 413 Payload Too Large — the model endpoint rejected the
            // request body as too large. Auto-lower the session prompt ceiling —
            // stepping it down toward the floor — so subsequent calls shrink to a
            // fitting size, letting a payload-limited but
            // capable model (e.g. deepseek-v3 at the default 26000 ceiling)
            // self-adapt without a manual AC_BOTS_PROMPT_CEILING. One-way (never
            // raised) so a persistent endpoint limit is honoured for the run.
            // Request-size management keyed to the endpoint's limit, not strategy.
            var loweredCeiling = LowerCeilingOnPayloadTooLarge(
                _adaptivePromptCeiling, result.StatusCode, result.Error,
                MinConfigurablePromptCeilingChars);
            if (loweredCeiling != _adaptivePromptCeiling)
            {
                Console.WriteLine(
                    $"[llm-call] 413 Payload Too Large at prompt-ceiling={_adaptivePromptCeiling} -> " +
                    $"backing off to {loweredCeiling} " +
                    $"(model payload limit; subsequent prompts auto-fit; repeats step down further).");
                _adaptivePromptCeiling = loweredCeiling;
            }
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
        var pushedNewTop = false;
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
                        $"applied={outcome.AppliedLog.Count - outcome.SuppressedCount} " +
                        $"suppressed={outcome.SuppressedCount} " +
                        $"revision_after={_stack.Revision} depth_after={_stack.Depth}" +
                        (outcome.Result != BatchApplyResult.Ok && !string.IsNullOrEmpty(outcome.RejectReason)
                            ? $" reason=\"{outcome.RejectReason}\"" : "") +
                        (outcome.StaleRevisionTolerated ? " stale_revision_tolerated=push-only" : ""));
                    if (outcome.Result != BatchApplyResult.Ok)
                    {
                        _training?.RecordParseError(decisionId,
                            $"stack-ops rejected: {outcome.Result} reason={outcome.RejectReason}");
                    }
                    // Re-drive provenance precondition: the batch applied
                    // cleanly AND its LAST op is a Push, so the effective
                    // TOP was created by a push in THIS response (a trailing
                    // pop/replace/mark would have changed or removed it).
                    pushedNewTop = outcome.Result == BatchApplyResult.Ok
                        && stackOps[^1].Op == IntentStackOpKind.Push;
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

        // Reached-Explore no-op loop-break (cp022): the LLM emitted an Explore
        // toward a target the bot has ALREADY reached (within reach in
        // `Visible nearby`). Explore is navigate-only — walking to where you are
        // already standing makes no progress, and re-installing it would arrive
        // instantly, clear, and re-force a fresh LLM call every cycle (a call
        // storm). The `## Reached Explore target` capsule has already told the LLM
        // to switch to an interaction verb; since it re-emitted a no-op Explore
        // instead, drop it and defer to the fallback so the bot moves on rather
        // than burning an LLM round-trip per cycle in place. Keys on the goal's
        // OWN target, so a NEW Explore toward a not-yet-reached target is kept.
        if (IsExploreToReachedTarget(goal, world))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Explore target={goal.Target}" +
                " — target already reached; Explore cannot interact; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: Explore toward an already-reached target (no-op)");
            return _fallback.ProposeGoal(world, events, currentGoal);
        }


        // Settled stage-3 turn-in Explore no-op (cp051): once the cp050 Talk guard
        // suppresses re-Talking a settled turn-in NPC, a model fixated on that NPC
        // re-routes to Explore (navigate-to) the SAME settled NPC. Exploring toward a
        // settled stage-3 turn-in NPC that is ALREADY IN VIEW is a no-op: the contract
        // is done (nothing to hand in there) and the NPC is right here in the
        // populated area, so walking to it changes nothing — a fresh contract source
        // is reachable from here instead. Drop it and defer to the fallback. The
        // IN-VIEW gate is the safety: when the settled NPC is NOT in view the Explore
        // is left alone, so a legitimate TRAVEL-BACK toward a source area
        // (RETURN-TO-A-CONTRACT-SOURCE) is never suppressed. The reached-Explore guard
        // above only catches the WITHIN-REACH subset; this catches a settled NPC that
        // is visible but farther (the bot having wandered off and now navigating back
        // to it). The settled-NPC recognition is keyed on the RESOLVED visible
        // object's name (NOT the raw selector text), so a `name_contains`- or
        // `wcid`-only Explore selector — which carries no `name` — is still matched
        // via its in-view object. Same settled-turn-in recognition as the cp050 Talk
        // guard (which already counts Explore pursuits toward its threshold) + own
        // perception; no game knowledge.
        if (goal.Kind == GoalKind.Explore
            && goal.Target is { } settledExploreTarget
            && world.Visible.Any(v => VisibleMatchesSelector(settledExploreTarget, v)
                && IsSettledStage3TurnInNpc(world, events, v.Name)))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Explore target={goal.Target}" +
                " — navigating to a settled stage-3 turn-in NPC already in view is a no-op;" +
                " deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: Explore toward a settled stage-3 turn-in NPC in view");
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

        // World-object USE loop-break (2026-06-04): a Use that SUCCEEDS
        // but makes no progress (the bot cannot path through the opened
        // door, no loot, no movement) is re-emitted forever by a weak
        // model even though "## Location & recency" already surfaces the
        // repeat. Drop the STATIONARY repeat (same target, bot has not
        // moved, no inventory change) and defer to the fallback so the bot
        // does something else. Complements the inventory-USE dedup above
        // (which owns goal.Item Uses); this owns bare world-object Uses.
        if (IsStationaryWorldUseRepeat(goal, world, events))
        {
            // Don't FLEE the currently-open vendor: re-Using an already-open
            // vendor panel is a transactional no-op (the panel stays open), not a
            // dead-end loop to escape; locking an Explore-away abandons the
            // transactable panel before the bot can Buy/Sell. Drop the redundant
            // Use and defer (no committed egress) so the next deliberation can
            // transact at the open offerings.
            if (LoopedUseTargetsOpenVendor(goal, world))
            {
                Console.WriteLine(
                    $"[llm-dedup] dropping LLM Use target={goal.Target}" +
                    " — stationary re-Use of the OPEN vendor; deferring (no flee) so the bot can Buy/Sell.");
                _training?.RecordParseError(decisionId,
                    "dropped-by-dedup: stationary re-Use of open vendor (no flee)");
                return _fallback.ProposeGoal(world, events, currentGoal);
            }
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Use target={goal.Target}" +
                " — stationary world-object Use repeated with no progress; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: stationary no-op world-object Use loop");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, WorldUseLoopKind);
        }

        // Landblock world-object USE churn loop-break (cp-2354): the stationary
        // guard above resets on movement, so it misses a TOUR of several doors
        // within ONE landblock (the bot walks between them and re-Uses them,
        // never egressing). Drop a bare world-object Use re-emitted against the
        // same target the Nth time within the same landblock (no egress, no
        // inventory change) and defer to the escape/fallback so the bot Explores
        // OUT instead of re-touring interior doors. First-use forgiveness keeps
        // a legitimate multi-door exit (each DISTINCT door Used once) from firing.
        if (IsLandblockWorldUseChurn(goal, world, events))
        {
            // Same open-vendor exception as above: a vendor is a transactable
            // station, not an interior door to tour past — don't flee it.
            if (LoopedUseTargetsOpenVendor(goal, world))
            {
                Console.WriteLine(
                    $"[llm-dedup] dropping LLM Use target={goal.Target}" +
                    " — re-Use churn on the OPEN vendor; deferring (no flee) so the bot can Buy/Sell.");
                _training?.RecordParseError(decisionId,
                    "dropped-by-dedup: re-Use churn on open vendor (no flee)");
                return _fallback.ProposeGoal(world, events, currentGoal);
            }
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Use target={goal.Target}" +
                " — world-object Use churn within one landblock (same target re-Used, or too many distinct" +
                " objects toured, with no egress); deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: landblock world-object Use churn (no egress)");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, WorldUseLoopKind);
        }

        // Settled stage-3 turn-in loop-break (cp050): a Talk re-emitted at the
        // turn-in NPC of a contract already DONE (wire ContractStage 3,
        // done/pending-repeat) past the post-completion attempt threshold is the
        // fixation the `## Contracts` "DONE (stage 3)" note describes — such a
        // contract has no separate hand-in (its reward is the issuer's to grant on
        // its own terms), so re-Talking its settled turn-in NPC can never change the
        // stage. The per-NPC / multi-NPC / roving Talk guards MISS this when the bot
        // ROVES between two such turn-in NPCs (movement resets the stationary guards)
        // and each NPC re-greets with fresh flavor text (dialog-novelty resets the
        // churn guards). This guard is position- AND novelty-independent: it fires
        // purely on the contract's wire stage + the bot's OWN post-stage-3 pursuit
        // count (the SAME recognition that renders the prompt note, shared via
        // IsSettledStage3TurnIn), so a model that ignores the note cannot keep
        // marching back. It allows the legitimate attempts (one hand-in + a batch
        // refresh) BEFORE the threshold, and stands down entirely if that NPC also
        // has LIVE business (it starts/turns-in any non-done contract — e.g. a fresh
        // batch just obtained from this same source). Own contract stage + own goal
        // history; no NPC/quest names, no game knowledge.
        if (goal.Kind == GoalKind.Talk
            && IsSettledStage3TurnInNpc(world, events, goal.Target?.Name))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — settled stage-3 contract turn-in NPC re-Talked past the done threshold;" +
                " no separate hand-in, deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: settled stage-3 contract turn-in re-Talk");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, goal.Target?.Name);
        }

        // NPC Talk loop-break (2026-06-05): a Talk re-emitted at the SAME
        // stationary NPC with no movement and no inventory change is an
        // exhausted conversation (the server just re-greets) — drop it and
        // defer to the fallback so the bot does something else instead of
        // re-locking the dead NPC. A real turn-in changes inventory and
        // walking to a different NPC changes the cell, so both reset the
        // streak and are never suppressed.
        if (IsExhaustedNpcTalkRepeat(goal, world, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — stationary NPC Talk repeated with no progress; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: stationary no-progress NPC Talk loop");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, goal.Target?.Name);
        }

        // Multi-NPC Talk-churn loop-break (cp-2344): the single-NPC guard above
        // resets on every target change, so it misses a referral PING-PONG
        // between a small set of NPCs. This fires on a stationary no-progress,
        // no-dialog-novelty cycle over <=2 distinct targets — the alternation
        // the per-NPC guard cannot see — and breaks it via the SAME egress.
        if (IsMultiNpcTalkChurn(goal, world, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — multi-NPC Talk cycle with no progress or dialog novelty; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: stationary no-progress multi-NPC Talk cycle");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, goal.Target?.Name);
        }

        // Roving multi-NPC Talk-churn (cp roving-multi-npc-talk-loop): the
        // stationary multi-NPC guard above resets on intra-landblock movement, so
        // a ping-pong between a small set of NPCs at DIFFERENT cells of one
        // landblock slips past it (and the roving SINGLE-NPC guard needs one
        // target). This fires on a position-independent no-progress, no-dialog-
        // novelty cycle over <=2 NPCs in the same landblock and breaks it via the
        // SAME egress.
        if (IsRovingMultiNpcTalkChurn(goal, world, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — roving multi-NPC Talk cycle with no progress or dialog novelty; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: roving no-progress multi-NPC Talk cycle");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, goal.Target?.Name);
        }

        // Roving single-NPC Talk-loop break (cp-2365): the stationary single-NPC
        // guard resets on MOVEMENT and the multi-NPC guard needs >=2 targets, so a
        // bot that keeps walking up to the SAME NPC loops past both. This fires on
        // N consecutive STALE (no dialog-novelty, no progress) Talks to ONE NPC
        // regardless of position, and breaks it via the SAME egress so the bot
        // does something else (train / explore / equip) instead of re-greeting a
        // dead conversation.
        // cp-2415: a Talk to a guid the roving guard recently confirmed as an
        // exhausted loop is TTL-suppressed — drop it immediately so the LLM's
        // persistent intent cannot re-drive the bot back to a dead conversation
        // before the egress has moved it on. Re-probed once the TTL expires.
        if (IsTalkLoopTtlSuppressed(goal, world, nowUtc))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — exhausted-NPC TTL suppression active; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: TTL-suppressed exhausted-NPC Talk loop");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, goal.Target?.Name);
        }
        if (IsRovingNpcTalkLoop(goal, world, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — roving single-NPC Talk loop with no progress or dialog novelty; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: roving no-progress single-NPC Talk loop");
            RecordTalkLoopSuppression(goal, world, nowUtc);
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, goal.Target?.Name);
        }

        // Single-NPC Talk-fixation backstop (cp069): the stationary guard resets
        // on movement and the roving guard resets its raw streak on ANY inventory/
        // landblock/self-progress event, so a bot that re-Talks ONE onboarding NPC
        // while interleaving incidental combat/loot (each kill = self-progress,
        // each pickup = inventory-added) keeps resetting both and loops the NPC
        // forever as its dialog cycles new-looking canned lines. This reads the
        // durable goal-emission history (Talks to ONE NPC dominating the last N
        // emitted goals) so the interleaved loop is caught, and breaks it via the
        // SAME egress + TTL suppression so the bot does something else instead.
        if (IsSingleNpcTalkFixationByHistory(goal, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — single-NPC Talk fixation across recent goal history; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: single-NPC Talk fixation in recent goal history");
            RecordTalkLoopSuppression(goal, world, nowUtc);
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind, goal.Target?.Name);
        }

        // Cross-kind interaction fixation loop-break (2026-06-05): the per-kind
        // guards above each count only their own GoalKind, so a mixed
        // Use{target} ↔ Pickup{target} alternation on one stationary target
        // (classically an emptied corpse after a kill) trips neither. Drop the
        // mixed-kind stationary repeat and defer to the fallback so the bot
        // moves on. A real loot changes inventory and resets the streak.
        if (IsStationaryInteractFixation(goal, world, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM interact kind={goal.Kind} target={goal.Target}" +
                " — stationary same-target interaction repeated with no progress; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: stationary no-progress cross-kind interaction loop");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "interaction");
        }

        // Unreachable-target repeat loop-break (2026-06-05): cp-2272 cut the
        // motor's no-lock fast-fail from 30s to 6s, which ~5x's the rate at
        // which a weak model can re-emit the SAME world-target goal on an
        // object/creature that has left the world (an Attack on a monster that
        // wandered OUT of PVS, or a Pickup on a corpse/item that was looted or
        // despawned) — no live snapshot, no explored sighting route, no frontier
        // → motor emits a terminal GoalFailed "selector resolved to no live
        // object". Each re-emit just re-fails instantly and wakes another
        // no-current-goal LLM call — burning quota. Drop the repeat and defer to
        // the fallback (a real Explore that MOVES the bot) once the motor has
        // failed to reach this exact target twice. Skipped the moment the target
        // re-enters PVS (let the real engagement proceed) and self-expiring as
        // the failures age out of the event window.
        if (IsUnreachableTargetRepeat(goal, world, events, nowUtc))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM {goal.Kind} target={goal.Target}" +
                " — target repeatedly unreachable (out of PVS, no route); deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: repeatedly-unreachable out-of-PVS target");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, $"unreachable {goal.Kind}");
        }

        // Low-health Attack-defer loop-break (reduce-llm-call-volume): when the bot's
        // health is below the re-engage threshold the Motor REFUSES to walk an Attack
        // into melee, failing the goal "combat deferred: self-health too low to
        // re-engage — recover before attacking". A weak model re-emits Attack every
        // cycle anyway (live cp039-validate: 30 such deferrals in one low-health
        // stretch), each waking a no-current-goal LLM call the Motor defers again —
        // burning quota while the bot should be RECOVERING. After the Motor has
        // deferred an Attack for low health repeatedly, substitute an Explore egress
        // so the bot LEAVES the fight and lets health regen instead of re-emitting an
        // Attack it is too weak to land. Fires EVEN with a monster in view (unlike the
        // stuck/talk/use egresses) — the whole point is the bot is too weak to engage
        // the visible monster; the Motor's own low-health disengage/flee reflexes own
        // safety, and the untargeted-Explore visible fallback now excludes attackable
        // monsters while combat-suppressed (HandshakeDriver) so the recover-egress
        // cannot lock a monster as its Explore landmark and walk back into danger.
        // Self-limiting: as the bot moves away its health recovers and normal
        // combat resumes. Own self-health failure history only; the LLM still chose
        // WHAT to fight — this only defers it while recovering. No game knowledge.
        if (goal.Kind == GoalKind.Attack && IsLowHealthDeferredAttackRepeat(events, nowUtc))
        {
            Console.WriteLine(
                $"[llm-override] deferred-attack egress: dropping LLM Attack target={goal.Target}" +
                " — Motor repeatedly deferred it (low self-health or a just-disengaged target on its" +
                " avoid cooldown); substituting Explore{anywhere} to break the loop.");
            _training?.RecordParseError(decisionId,
                "dropped-by-override: LLM Attack repeatedly deferred (low health / avoid cooldown)");
            return MakeEgressExploreGoal(
                nowUtc, "override:low-health-attack-egress",
                "mechanical deferred-attack egress: the Motor repeatedly refused this Attack (self-health " +
                "below the re-engage threshold, or the target is on its brief post-disengage avoid cooldown); " +
                "leaving this target instead of looping a refused Attack");
        }

        // Unarmed-Attack drop (reduce-llm-call-volume): an LLM Attack while the bot
        // is NOT combat-capable (no wielded melee weapon, and no wielded missile
        // weapon WITH ammo) is a doomed swing — the server cancels every attack and
        // 0 damage lands. A model may still pick an OPTIONAL Attack on a passive
        // winnable-looking kind despite the UNARMED combat-readiness line. Drop it
        // and defer to the fallback (self-arm / explore / non-combat progress) so the
        // bot stops burning cycles on attacks it cannot land. SELF-DEFENSE exempt: if
        // the Attack's NAMED target is itself a live HOSTILE (the bot is fighting back
        // the thing engaging it), KEEP the Attack so it can defend or flee (the
        // SELF-ARM rule's "a HOSTILE attacker still takes priority — defend or flee
        // even while unarmed"). A DIFFERENT hostile being in view does NOT exempt a
        // swing at a passive named target — the fallback will re-aim at the real
        // hostile. Own wire-state only — the wielded-weapon slot bits (IsCombatCapable)
        // + the ObservedHostile threat bit; the LLM still chose WHAT to fight, this
        // only declines a doomed OPTIONAL engagement. No game knowledge.
        if (IsOptionalAttackWhileNotCombatCapable(goal, world))
        {
            // Distinguish the two drop states for an accurate log + training label:
            // (a) no live hostile in view at all; (b) a hostile IS in view but it is
            // not the Attack's named target (so the swing is still misdirected).
            var hostileElsewhere = world.Visible.Any(v => !v.IsCorpse && v.ObservedHostile);
            var threatNote = hostileElsewhere
                ? "named target is not an active hostile (a different hostile is in view)"
                : "no hostile is engaging";
            Console.WriteLine(
                $"[llm-override] unarmed-attack drop: dropping LLM Attack target={goal.Target}" +
                $" — bot is not combat-capable (no usable weapon) and {threatNote}; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                $"dropped-by-override: LLM Attack while not combat-capable (no usable weapon; {threatNote})");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "unarmed-attack");
        }

        // Unactuatable-RaiseSkill drop (reduce-llm-call-volume). When the bot's
        // raisable-skills list is loaded and a RaiseSkill names a skill that is NOT
        // in it, the goal cannot be actuated and a model may otherwise re-emit it.
        // Drop it and defer to the fallback so the bot does something useful and
        // re-deliberates. The LLM still owns WHICH target to raise — this only drops
        // one it cannot actuate, like the Wield-no-weapon / useless-launcher drops.
        if (IsRaiseOfUntrainedSkill(goal, world))
        {
            Console.WriteLine(
                $"[llm-override] untrained-raiseskill drop: target={goal.Target}" +
                " — skill not in the bot's raisable-skills list; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-override: RaiseSkill of a skill not in trained skills");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "untrained-raiseskill");
        }

        // Unactuatable/below-floor Raise drop (reduce-llm-call-volume). A Raise* goal
        // (attribute, skill, or vital) with unspent XP below the meaningful spend floor
        // is dropped and deferred to the fallback: at the default floor (0) this is the
        // Motor's own refusal at unspent<=0 — a model otherwise re-emits the raise every
        // cycle, looping — while above 0 it also drops sub-floor balances, staying
        // consistent with the SPEND XP cue-suppression gated on the SAME floor (those
        // tiny balances are not worth a raise turn). Defer so the bot does productive
        // work and re-deliberates once it has meaningful XP again. The LLM still owns
        // WHICH target to raise — like the untrained-raiseskill / Wield-no-weapon drops.
        if (IsRaiseGoalWithNoSpendableXp(goal, world, MinMeaningfulUnspentXp))
        {
            Console.WriteLine(
                $"[llm-override] raise-no-xp drop: {goal.Kind} target={goal.Target}" +
                " — no spendable unspent XP; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-override: Raise with no spendable unspent XP");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "raise-no-xp");
        }

        // Wield-of-a-ground-weapon -> Pickup (mechanical prerequisite,
        // reduce-llm-call-volume). A model sometimes emits Wield for a weapon lying
        // on the GROUND (which the Combat-readiness capsule shows as "Pickup it to
        // arm"); the Motor's Wield equips only IN-BAG items, so it fails the call and
        // the bot stays unarmed. Perform the prerequisite the LLM's own choice
        // requires: rewrite the Wield of a resolved ground weapon into a Pickup of
        // that SAME named weapon. Next tick it is in the bag and the normal Wield
        // path equips it. The LLM chose WHICH weapon — the Motor only fetches it
        // (mechanical execution, like the pre-existing dequip-before-wield swap; no
        // autonomous target pick, no game knowledge). Placed before the Wield-specific
        // guards below so the rewritten Pickup flows through the normal pickup path.
        if (TryResolveWieldGroundWeapon(goal, world, events) is uint groundWeaponGuid)
        {
            Console.WriteLine(
                $"[llm-override] wield-ground-weapon -> pickup: target={goal.Target}" +
                $" guid=0x{groundWeaponGuid:X8} — named weapon is on the ground, not in the" +
                " bag; Wield equips only in-bag items, so picking it up first.");
            return goal with
            {
                Kind = GoalKind.Pickup,
                Target = new Selector { Guid = groundWeaponGuid },
                Item = null,
            };
        }

        // Pickup-of-a-Use-container -> Use (mechanical prerequisite, reduce-llm-call-volume).
        // A model sometimes emits Pickup for a CORPSE or an openable world-static container,
        // but such an object is a Use-container (the server refuses to take it), so the Pickup
        // selector resolves to MISS and the bot loops the wrong verb. Perform the prerequisite
        // the LLM's choice requires: rewrite the Pickup of a resolved Use-container into a Use
        // of that SAME object (the Use handler opens it). The LLM chose WHICH object — the Motor
        // only substitutes the correct verb (mechanical execution, like the Wield->Pickup rewrite
        // above; no autonomous target pick, no game knowledge — keyed on the IsCorpse/IsChest/
        // IsStuck wire bits + the wire guid range).
        if (TryResolvePickupUseContainer(goal, world, events) is uint pickupContainerGuid)
        {
            Console.WriteLine(
                $"[llm-override] pickup-container -> use: target={goal.Target}" +
                $" guid=0x{pickupContainerGuid:X8} — a corpse or non-takeable container is" +
                " actuated with Use, not Pickup.");
            return goal with
            {
                Kind = GoalKind.Use,
                Target = new Selector { Guid = pickupContainerGuid },
                Item = null,
            };
        }

        // Pickup-of-a-vendor-panel-item -> Buy (mechanical verb correction,
        // reduce-llm-call-volume). At an open vendor a model sometimes emits Pickup for an
        // item that is in the vendor's PANEL (for sale), not on the ground; Pickup takes
        // only ground items, so it resolves to MISS and the bot loops the wrong verb. The
        // mechanically-correct way to acquire a panel item is Buy. Rewrite the Pickup of a
        // name that exactly matches an open-vendor offering (and binds no visible world
        // object, so it is not a ground pickup) into a Buy of that SAME offering. The LLM
        // chose WHICH item; the Motor only substitutes the verb (like the Pickup->Use /
        // Wield->Pickup rewrites above; no autonomous target pick, keyed on the open-vendor
        // offer list + the bot's named target).
        if (TryResolvePickupVendorItemName(goal, world) is { } vendorOfferName)
        {
            Console.WriteLine(
                $"[llm-override] pickup-vendor-item -> buy: target={goal.Target}" +
                " — a vendor-panel item is acquired with Buy, not Pickup.");
            return goal with
            {
                Kind = GoalKind.Buy,
                Target = new Selector { Name = vendorOfferName },
                Item = null,
            };
        }

        // Explore-toward-a-visible-vendor loop -> Use (reduce-llm-call-volume + engagement
        // progress). Explore only WALKS to a target and never interacts, so an Explore that
        // NAMES a vendor "arrives" at it without engaging and a model then re-Explores the
        // SAME in-view vendor every cycle (a loop). The bot named the vendor — intent to
        // reach it — and the mechanically-correct way to engage a reached vendor is `Use`
        // (open its trade panel), so rewrite the looped Explore into a Use of that SAME
        // vendor. Like the Pickup->Use / Wield->Pickup rewrites; no autonomous target pick
        // (the LLM chose WHICH vendor), no game knowledge — keyed on the IsVendor wire bit
        // + the bot's OWN Explore-emission history. The >= threshold gate never preempts a
        // first/legitimate approach Explore.
        if (TryResolveExploreLoopedVendor(goal, world, events, nowUtc - ExploreLoopedVendorWindow) is uint exploreVendorGuid)
        {
            Console.WriteLine(
                $"[llm-override] explore-vendor -> use: target={goal.Target}" +
                $" guid=0x{exploreVendorGuid:X8} — repeatedly Exploring a visible vendor" +
                " without engaging; Use it (Explore only walks, never interacts).");
            return goal with
            {
                Kind = GoalKind.Use,
                Target = new Selector { Guid = exploreVendorGuid },
                Item = null,
            };
        }

        // Buy{vendor} with no trade panel open -> Use{vendor} (approach + open the panel).
        // The Buy dispatch FAILS a Buy when no vendor panel is open within reach (by design,
        // expecting the LLM to approach the vendor first via Use), but a model often re-emits
        // the SAME Buy (sticky) instead of Use, looping on "no panel open" without ever
        // approaching. When the Buy names a VISIBLE vendor and no panel is open, rewrite into
        // Use{that vendor} -- the approach the dispatch expects. Once the panel is open it
        // never fires, so the eventual Buy resolves normally + the affordability marker/
        // recent-buy guard handle an unaffordable item; no loop. Mirrors the explore-vendor->
        // use / Pickup->Use rewrites; the LLM chose WHICH vendor, the Motor only corrects the
        // verb/sequencing.
        if (TryResolveBuyVendorNoPanel(goal, world) is uint buyVendorGuid)
        {
            Console.WriteLine(
                $"[llm-override] buy-no-panel -> use: target={goal.Target}" +
                $" guid=0x{buyVendorGuid:X8} — Buy needs an OPEN vendor panel; none open," +
                " so Use the visible vendor first to approach + open its trade panel.");
            return goal with
            {
                Kind = GoalKind.Use,
                Target = new Selector { Guid = buyVendorGuid },
                Item = null,
            };
        }

        // Use{item=<world object>, no target} -> Use{target=<that object>} (mechanical field
        // correction, reduce-llm-call-volume). The self-Use shape Use{item=X, no target} is for
        // activating an OWNED inventory item on yourself; a model sometimes mis-files a VISIBLE
        // vendor/NPC into that item field with no target (expecting it to open/engage the object),
        // but the Motor's self-Use only resolves an in-bag item, so the goal MISSes and the bot
        // loops the wrong shape. When the item-field name is NOT a plausible owned item AND uniquely
        // resolves to a visible vendor/NPC, move it into the TARGET field (the standard
        // Use-a-world-object shape the Motor engages). The LLM chose WHICH object; the Motor only
        // corrects the field (like the Pickup->Use / Wield->Pickup rewrites above; no autonomous
        // target pick, no game knowledge — keyed on the inventory-vs-visible resolution of the
        // bot's OWN named item).
        if (TryResolveUseWorldObjectInItemField(goal, world) is uint useWorldObjGuid)
        {
            Console.WriteLine(
                $"[llm-override] use-item-world-object -> target: item={goal.Item}" +
                $" guid=0x{useWorldObjGuid:X8} — a visible world object was mis-filed into the Use" +
                " item field; engaging it as the target (self-Use only acts on an in-bag item).");
            return goal with
            {
                Kind = GoalKind.Use,
                Target = new Selector { Guid = useWorldObjGuid },
                Item = null,
            };
        }

        // Use{item=<open-vendor offering>, no target} -> Buy{that offering} (mechanical verb
        // correction, reduce-llm-call-volume). The self-Use shape Use{item=X, no target} is for
        // activating an OWNED inventory item; a model at an open vendor sometimes names a PANEL
        // offering it wants to acquire in that item field, but the Motor's self-Use only resolves
        // an in-bag item, so the panel offering MISSes and the bot loops the wrong shape. The
        // mechanically-correct verb to acquire a panel item is Buy. When the item-field name is
        // NOT a plausible owned item AND NOT a visible vendor/NPC (handled above) AND matches an
        // open-vendor offering exactly, rewrite to a Buy of that SAME offering. The LLM chose WHICH
        // item; the Motor only substitutes the verb (like the Pickup-vendor-item->Buy rewrite; no
        // autonomous target pick, no game knowledge — keyed on the open-vendor offer list).
        if (TryResolveUseItemVendorOffering(goal, world) is { } useVendorOfferName)
        {
            Console.WriteLine(
                $"[llm-override] use-item-vendor-offering -> buy: item={goal.Item}" +
                " — a for-sale vendor-panel item was mis-filed into the Use item field; acquiring" +
                " it with Buy (self-Use only acts on an in-bag item).");
            return goal with
            {
                Kind = GoalKind.Buy,
                Target = new Selector { Name = useVendorOfferName },
                Item = null,
            };
        }

        // Wield no-weapon loop-break (reduce-llm-call-volume): the Motor's direct
        // Wield dispatch FAILS with "no equippable inventory weapon" when an LLM
        // Wield selector resolves to nothing the wield path can equip into a weapon
        // slot. A model can re-emit such a Wield every cycle, burning a
        // no-current-goal LLM call each time with no equip and no progress. After
        // repeated such rejections, AND only while the bag holds NO un-wielded item
        // that could actually be wielded into a weapon slot, drop the Wield and defer
        // to the fallback (which MOVES the bot) instead of re-emitting an
        // un-equippable Wield. The inventory gate keeps a legitimate swap to a
        // just-acquired weapon flowing: the LLM Wield path can dequip the blocker,
        // but the mechanical fallback deliberately will not, so dropping the Wield
        // while a real weapon is in the bag would strand it. The wielded-weapon gate
        // additionally withholds the drop whenever a main-hand weapon is ALREADY
        // wielded — the bot is armed (the loop-break exists to move an UN-armed bot
        // toward acquiring a weapon), and a valid ammo reload / shield equip (which
        // the main-weapon-slot inventory test does not see) must still flow while a
        // launcher/weapon is wielded. Self-limiting: once an equippable weapon is held
        // (or the failures age out of the window) Wield flows again. Own Wield-failure
        // history + own equip-slot bits only; no item preference, no game knowledge.
        if (goal.Kind == GoalKind.Wield
            && IsWieldNoWeaponRepeat(events)
            && !HasEquippableInventoryWeapon(world)
            && !HasWieldedMainWeapon(world))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Wield item={goal.Item}" +
                " — Motor repeatedly found no equippable inventory weapon and the bag holds none; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: repeated Wield with no equippable inventory weapon");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "wield no-weapon");
        }

        // Useless-launcher Wield drop (cp061): a bag missile LAUNCHER with no
        // loadable ammo cannot fire and cp060 will dequip it immediately after
        // wield — producing an infinite LLM-wield / Motor-dequip ping-pong that
        // prevents the bot from ever fighting unarmed. Drop the Wield and defer
        // to the fallback so cp047 autonomous combat runs unarmed instead.
        // Self-limiting: once the bot acquires loadable ammo the guard returns
        // false and the normal Wield path resumes. Thrown weapons (AmmoType null)
        // are their own projectile and are NEVER dropped. Pure loadout arithmetic
        // (ItemType + AmmoType + ValidLocations); no game knowledge.
        if (IsWieldOfUnusableLauncher(goal, world))
        {
            Console.WriteLine(
                $"[llm-override] useless-launcher wield drop: item={goal.Item ?? goal.Target}" +
                " — bag launcher has no loadable ammo and would be immediately re-dequipped by cp060; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-override: Wield of an ammoless launcher with no loadable ammo");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "wield useless launcher");
        }

        // Beaten-kind Attack veto: an LLM Attack can name a KIND the bot's OWN
        // ledger shows it keeps LOSING to (the IsBeatenKind verdict the
        // autonomous picker and the COMBAT SAFETY prompt rule already apply). A
        // weak model may misread its own record and re-pick such a kind;
        // re-attacking just loses again (often near-0 hit-rate), and left
        // unchecked the bot can die to the same kind repeatedly. Drop the
        // OPTIONAL (non-self-defense) engagement and defer to the fallback — it
        // Explores away or autonomously picks a NOT-beaten target. The veto is
        // gated on the bot NOT having out-leveled the loss, so an explicit order
        // can re-attempt the kind once the bot is demonstrably stronger. A
        // self-defense Attack on an actively-hostile beaten kind is also exempt
        // (predicate returns false). Bot-owned outcomes + own level only; no
        // game knowledge.
        if (IsOptionalAttackOnBeatenKind(goal, world))
        {
            _summaryBeatenVetoes++;
            Console.WriteLine(
                $"[llm-override] beaten-kind veto: dropping LLM Attack target={goal.Target}" +
                " — own combat ledger marks this kind beaten (losses, no kills); deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-override: LLM Attack on a beaten kind (own ledger: losses, no kills)");
            // Beaten-kind STALEMATE egress: when EVERY attackable monster in view
            // is a beaten kind (nothing winnable, no live threat), the plain
            // fallback would keep the bot parked here (a beaten kind still counts
            // as a monster-in-view, so the stuck-loop egress cannot fire), so the
            // next no-current-goal decision re-presents the same scene and a weak
            // model re-picks the SAME vetoed Attack — burning quota on a veto that
            // can never pass. Explore OUT to find a winnable target instead of
            // re-deferring. Self-limiting: as the bot moves, the beaten kinds leave
            // view and a fresh scene unblocks normal play. (reduce-llm-call-volume)
            if (OnlyBeatenMonstersInView(world))
            {
                Console.WriteLine(
                    "[llm-override] beaten-kind egress: every attackable monster in view is a " +
                    "beaten kind — substituting Explore{anywhere} to leave and find a winnable target.");
                return MakeEgressExploreGoal(
                    nowUtc, "override:beaten-kind-egress",
                    "mechanical beaten-kind egress: every attackable monster in view is a kind the " +
                    "bot's own ledger marks beaten; leaving to find a winnable target instead of " +
                    "re-emitting a vetoed Attack");
            }
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "beaten-kind Attack");
        }

        _training?.RecordEmittedGoal(decisionId, goal);
        // Sticky-objective bookkeeping: remember this LLM-authored goal
        // so ProposeGoal can re-drive it without another LLM call while
        // it remains unfinished, and give the new objective a fresh
        // re-emit budget.
        _lastLlmGoal = goal;
        _stickyReEmitCount = 0;
        // Reset the autonomous combat-chain budget ONLY here — when a USABLE LLM
        // decision has actually been consumed. Doing it at call kickoff would
        // refresh the cap even when the call later FAILS (429/timeout) or is
        // discarded as stale, letting the bot chain another MaxCombatChainAttacks
        // with no real oversight under intermittent LLM failure. Gating the reset
        // on a successful consume keeps the cap meaningful: with no usable LLM
        // result the count stays at the cap and the chain stays parked (the
        // fallback policy still drives combat).
        _combatChainCount = 0;

        // Source re-drive capture (execution persistence). Every accepted
        // LLM response either re-captures or CLEARS provenance, so a fresh
        // LLM decision always supersedes a prior commitment. Capture only
        // the exact, narrow case: this response pushed a new TOP (trailing
        // push, applied cleanly) AND emitted an inert Explore AND the new
        // TOP carries an LLM-authored deadline (liveness guarantee). No
        // game knowledge: the intent kind is never inspected.
        if (pushedNewTop
            && goal.Kind == GoalKind.Explore
            && _stack is { } st
            && st.Depth > 1
            && st.Top is { } newTop
            && newTop.DeadlineUtc is not null)
        {
            _redriveIntentId = newTop.Id;
            _redriveGoal = goal;
            _redriveReinstalls = 0;
            Console.WriteLine(
                $"[strategy] re-drive armed: intent={newTop.Id} goal=Explore target={goal.Target} " +
                $"deadline={newTop.DeadlineUtc:HH:mm:ss}Z");
        }
        else
        {
            _redriveIntentId = null;
            _redriveGoal = null;
            _redriveReinstalls = 0;
        }
        Console.WriteLine(
            $"[llm-call] success id={decisionId} latency={result.LatencyMs}ms " +
            $"goal=kind={goal.Kind} target={goal.Target}" +
            (goal.Item is null ? "" : $" item={goal.Item}") +
            (RationaleLogPreview(goal.Rationale) is { Length: > 0 } why ? $" why=\"{why}\"" : ""));
        return goal;
    }

    // Truncate + single-line the LLM's OWN rationale for the [llm-call] success log
    // so each strategic decision's REASONING is greppable (why the LLM chose this
    // goal over an alternative) without flooding the line or letting a long
    // rationale evict it. The rationale is free-form LLM text, so we also collapse
    // whitespace to one line, strip stray control chars, and neutralize embedded
    // double-quotes so they cannot prematurely close the why="..." field. Pure
    // formatter over the LLM's own output (logging only; never read by
    // decision-making), so it carries no game knowledge.
    internal static string RationaleLogPreview(string? rationale)
    {
        if (string.IsNullOrWhiteSpace(rationale)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            rationale, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");
        var oneLine = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ")
            .Replace('"', '\'')
            .Trim();
        const int Max = 160;
        if (oneLine.Length <= Max) return oneLine;
        // Avoid truncating in the middle of a UTF-16 surrogate pair.
        var cut = char.IsHighSurrogate(oneLine[Max - 1]) ? Max - 1 : Max;
        return oneLine.Substring(0, cut) + "…";
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
        // Slice O — widened from 15 to 30 events. In one spike the LLM
        // attempted Give(an NPC, a quest item) 3 times across
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
            else if (!string.IsNullOrWhiteSpace(targetName) &&
                RejectionEventMatchesTargetName(ev, targetName))
            {
                matched = true;
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

    // Shared name-match for an ActionRejected event against a goal target NAME:
    // the NPC/place name is carried in Name (synthetic Unreachable rejections) or
    // in Text (WeenieErrorWithString puts the name there, sometimes as part of a
    // longer "Unreachable: 'X' (walk timeout ...)" message). Substring matching on
    // Text is gated on a minimum name length to avoid false positives on short
    // common substrings. Extracted so IsGoalRecentlyRejected and the transport-only
    // IsExploreNameTransportRefused below stay in lock-step. No game knowledge.
    private static bool RejectionEventMatchesTargetName(StreamEvent ev, string targetName)
    {
        if (!string.IsNullOrWhiteSpace(ev.Name) &&
            string.Equals(ev.Name, targetName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(ev.Text))
        {
            if (string.Equals(ev.Text, targetName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (targetName.Length >= 4 &&
                ev.Text.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // True iff a recent ActionRejected naming `name` is a TRANSPORT failure
    // (Unreachable / Blocked / NoIndoorPath — the bot could not WALK there) that is
    // still live (the bot has NOT since arrived at the target). This is the precise
    // condition under which a re-emitted Explore toward `name` keeps being dropped
    // for NO WALKABLE ROUTE, as distinct from a SEMANTIC server refusal (which
    // IsGoalRecentlyRejected also matches but which is NOT a path problem). Used to
    // scope the Explore-loop "being refused as unreachable" wording to genuine path
    // failures so it never mislabels a semantic refusal. Mirrors the transport
    // staleness-clearing in IsGoalRecentlyRejected; own rejection record only.
    internal static bool IsExploreNameTransportRefused(string? name, EventStream events)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        const int LookbackEvents = 30;
        foreach (var ev in events.Recent(LookbackEvents))
        {
            if (!IsTransportFailureRejection(ev)) continue;
            if (!RejectionEventMatchesTargetName(ev, name)) continue;
            if (HasArrivedAtTargetSince(events, ev)) continue;
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
    /// True iff the ActionRejected is the SURFACED combat swing-loop cancel —
    /// it carries the Motor-reserved code
    /// <see cref="CombatRetry.SurfacedSwingLoopCancelCode"/> (0xFFFA) stamped by
    /// the AttackDone surfacing path (HandshakeDriver) in place of the raw wire
    /// code 0x0036. The Motor handles this signal itself — it immediately
    /// re-sends the bare attack to restart the loop (the fast-retry arm in
    /// HandshakeDriver) — so it names no problem the LLM can act on; it is the
    /// routine teardown of one swing iteration, not a semantic refusal. The
    /// reserved code is REQUIRED for correctness: the raw 0x0036 also rides on
    /// inventory ActionCancelled rejections, which ARE decision-worthy and must
    /// still interrupt — keying on the raw code would conflate them. A genuine
    /// action refusal (out-of-range, cannot-attack, skill-too-low, …) carries
    /// its own code, and the Motor's self-preservation disengage carries its
    /// own reserved code, so neither is matched here. Pure wire-code
    /// classification reusing the named combat constant; no game knowledge.
    /// </summary>
    internal static bool IsAttackLoopCancelRejection(StreamEvent ev) =>
        ev.Kind == EventKind.ActionRejected &&
        ev.ErrorCode == CombatRetry.SurfacedSwingLoopCancelCode;

    /// <summary>
    /// The exact suffix of the GoalFailed reason text the motor emits when a
    /// goal's selector resolved to no live world object AND no sighting route
    /// AND no frontier (the terminal "give up, re-deliberate" branch in
    /// HandshakeDriver). The full Text is "{GoalKind}: selector resolved to no
    /// live object" — matching the SUFFIX excludes the other Fail reason at
    /// the same site ("...: combat deferred: self-health too low..."), which
    /// must NOT be treated as an unreachable target.
    /// </summary>
    private const string NoLiveObjectFailSuffix =
        ": selector resolved to no live object";

    // A SECOND terminal-failure signal IsUnreachableTargetRepeat keys on, distinct
    // from the no-live-object suffix: the server refused an interaction as OUT OF
    // REACH — after the Use/Pickup it walked the bot TOWARD the very target instead
    // of completing it (the object sat at a different elevation/footing than the
    // XY-only arrival check assumed). Unlike a despawn, the object is still
    // PHYSICALLY IN VIEW, so the in-view short-circuit must NOT exempt it: re-emitting
    // the same goal just re-walks to the unreachable object and re-fails every cycle.
    // Matched as a substring of the GoalFailed Text ("{Kind}: interaction target out
    // of reach: ..."), keyed on the failed goal's own selector name; no server
    // dialogue, no hardcoded names/wcids/landblocks, no game knowledge.
    private const string InteractOutOfReachFailMarker =
        "interaction target out of reach";

    // Recency window for the unreachable-target loop signals (out-of-reach + no-live-
    // object). The durable GoalFailed window retains ~30 min; this bounds the name-keyed
    // suppression to a CURRENT loop so a once-unreachable name is retried after the bot
    // has had time to reposition, instead of being blacklisted for the whole retention.
    private static readonly TimeSpan UnreachableRepeatRecency = TimeSpan.FromSeconds(90);

    // Marker substring of the Motor's Attack-defer GoalFailed reason
    // (HandshakeDriver: "{Kind}: combat deferred: self-health too low to re-engage —
    // recover before attacking"). The Motor emits this SHARED marker for BOTH defer
    // causes: self-health below the re-engage threshold AND a recently-disengaged
    // target on its brief avoid cooldown (the bot fled it, then re-attacked it at
    // full health). Distinct from the two unreachable-target signals above: the
    // target is reachable, but the engage is being declined.
    private const string CombatDeferredLowHealthMarker =
        "self-health too low to re-engage";

    // Recency bound for the deferred-Attack loop signal. The durable GoalFailed window
    // retains failures for ~30 min (eviction-resistant, unlike events.Recent(N) which the
    // perception firehose evicts), so WITHOUT a recency bound two stale defers would keep
    // the egress armed for the whole retention and convert EVERY later Attack to Explore
    // (a ~30-min combat lockout after one brief defer episode). A real loop emits a defer
    // each decision cycle (~tens of seconds apart), so a short window distinguishes a
    // CURRENT loop from a resolved one; once the bot stops being refused, the signal
    // clears within this window.
    private static readonly TimeSpan DeferredAttackRepeatRecency = TimeSpan.FromSeconds(90);

    /// <summary>
    /// True iff the Motor has REPEATEDLY (>= threshold) DEFERRED an Attack with the
    /// combat-defer reason in the RECENT past — the engage is being declined (self below
    /// the re-engage health threshold, OR the target is on the brief post-disengage avoid
    /// cooldown) and a weak model keeps re-emitting the same Attack, burning a
    /// no-current-goal LLM call each cycle and looping on a refused Attack instead of
    /// disengaging. The caller substitutes an Explore egress (leave this target — recover
    /// and/or fight something else).
    ///
    /// Reads the DURABLE GoalFailed window (RecentGoalFailures), NOT events.Recent(N):
    /// consecutive deferred-Attack refusals land on SEPARATE decisions ~tens of seconds
    /// apart, with hundreds of perception/motion events between, so the prior refusal was
    /// evicted from the ring before the count reached the threshold — the egress then fired
    /// ONLY when two refusals were adjacent in the ring, i.e. ≈never in practice, so a
    /// refused Attack looped unbroken. SAME eviction fix as IsWieldNoWeaponRepeat. Scoped
    /// to <see cref="DeferredAttackRepeatRecency"/> of <paramref name="nowUtc"/> (the
    /// caller's decision clock) so the long durable retention does not lock the bot out of
    /// combat long after a loop ends. RecentGoalFailures() is newest-first, so the scan
    /// stops at the first too-old entry. Pure read of the bot's OWN GoalFailed history — no
    /// target choice, no self-state threshold logic here (that lives in the Motor), no game
    /// knowledge.
    /// </summary>
    internal static bool IsLowHealthDeferredAttackRepeat(EventStream events, DateTimeOffset? nowUtc = null)
    {
        const int LowHealthDeferRepeatThreshold = 2;
        var cutoff = (nowUtc ?? DateTimeOffset.UtcNow) - DeferredAttackRepeatRecency;
        var deferrals = 0;
        foreach (var ev in events.RecentGoalFailures())   // newest-first
        {
            if (ev.Utc < cutoff) break;                   // older than the window; rest are older too
            if (ev.Text is null ||
                ev.Text.IndexOf(CombatDeferredLowHealthMarker, StringComparison.Ordinal) < 0)
                continue;
            if (++deferrals >= LowHealthDeferRepeatThreshold) return true;
        }
        return false;
    }

    // Marker substring of the Motor's Wield-dispatch failure reason
    // (HandshakeDriver emits "{Kind}: wield: no equippable inventory weapon")
    // raised when an LLM Wield selector resolves to nothing the wield path can
    // equip into a weapon slot.
    private const string WieldNoWeaponFailMarker =
        "wield: no equippable inventory weapon";

    /// <summary>
    /// True iff the Motor has REPEATEDLY (>= threshold) rejected a Wield with the
    /// "no equippable inventory weapon" reason — a model keeps re-emitting a Wield the
    /// wield path cannot satisfy (a selector that resolves to no equippable in-bag
    /// weapon, INCLUDING a generic <c>name_contains="weapon"</c> type-descriptor that
    /// matches no item NAME and so fails at selector resolution), burning a
    /// no-current-goal LLM call each cycle with no equip and no progress. The caller
    /// pairs this with <see cref="HasEquippableInventoryWeapon"/> and, only when nothing
    /// equippable is held, drops the Wield and defers to the fallback (which MOVES the
    /// bot toward acquiring a weapon). Reads the DURABLE GoalFailed window, not the
    /// perception-dominated ring: the futile Wields recur ACROSS decisions with heavy
    /// perception/motion traffic between them, which evicted the prior failure from the
    /// ring before the count reached the threshold (so the loop-break previously fired
    /// ONLY when two failures were adjacent). Keyed on the SPECIFIC no-weapon failure
    /// reason — a SUCCESSFUL Wild (e.g. a valid ammo reload or shield equip) produces no
    /// such failure and is never counted. Pure read of the bot's OWN GoalFailed history;
    /// no item preference, no game knowledge.
    /// </summary>
    internal static bool IsWieldNoWeaponRepeat(EventStream events)
    {
        const int WieldNoWeaponRepeatThreshold = 2;
        var rejects = 0;
        foreach (var ev in events.RecentGoalFailures())
        {
            if (ev.Text is null ||
                ev.Text.IndexOf(WieldNoWeaponFailMarker, StringComparison.Ordinal) < 0)
                continue;
            if (++rejects >= WieldNoWeaponRepeatThreshold) return true;
        }
        return false;
    }

    /// <summary>
    /// True if the bag holds an un-wielded WEAPON the wield path could equip into a
    /// primary-weapon slot — its ValidLocations intersects
    /// <see cref="WeaponSwap.MainWeaponSlotMask"/> (the melee/missile/held/two-handed
    /// slots) AND its ItemType is a weapon (<see cref="WeaponSwap.WeaponItemTypeMask"/>:
    /// melee/missile/caster). Requiring the weapon ItemType means a non-weapon main-slot
    /// item (e.g. a torch sitting in the bag with a Held ValidLocations) does NOT read as
    /// "a weapon is available". Pure equip-slot/ItemType-bit projection: the dedicated
    /// ammo slot and the armor/jewelry slots are NOT weapon slots, so a bag holding only
    /// those (or only an already-wielded weapon) reads as "no weapon"; no item
    /// preference, no game knowledge. The Wield loop-break is withheld whenever this is
    /// true, so a legitimate swap to a just-acquired weapon (which the LLM Wield path can
    /// actuate but the mechanical fallback will not) is never dropped.
    /// </summary>
    internal static bool HasEquippableInventoryWeapon(WorldStateProjection world)
    {
        var inventory = world.Inventory;
        if (inventory is null) return false;
        foreach (var it in inventory)
        {
            if (it.WieldedAt is uint w && w != 0) continue;
            if (it.ValidLocations is uint vl && (vl & WeaponSwap.MainWeaponSlotMask) != 0
                && it.ItemType is uint t && (t & WeaponSwap.WeaponItemTypeMask) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True if the bot already has a WEAPON wielded in a main-hand weapon slot — i.e.
    /// the canonical <see cref="WeaponSwap.IsWieldedWeapon"/> (WieldedAt intersects
    /// <see cref="WeaponSwap.MainWeaponSlotMask"/> AND the ItemType is a weapon, so a
    /// wielded non-weapon held item such as a torch does NOT qualify). The bot is armed,
    /// so the no-weapon Wield loop-break (which exists to move an UN-armed bot toward
    /// acquiring a weapon) must be withheld: a model wielding a missile launcher then
    /// loading ammo, or adding a shield, emits a VALID Wield that the main-weapon-slot
    /// inventory test (<see cref="HasEquippableInventoryWeapon"/>, which ignores the
    /// ammo/shield slots) does not register, and dropping it would strand a legitimate
    /// reload/equip. Pure equip-slot/ItemType-bit projection; no item preference, no
    /// game knowledge.
    /// </summary>
    internal static bool HasWieldedMainWeapon(WorldStateProjection world)
    {
        var inventory = world.Inventory;
        if (inventory is null) return false;
        foreach (var it in inventory)
            if (WeaponSwap.IsWieldedWeapon(
                    new WeaponSwap.ItemFacts(it.Guid, it.ItemType, it.ValidLocations, it.WieldedAt)))
                return true;
        return false;
    }

    /// <summary>
    /// True iff an LLM goal is re-proposing a target the motor has REPEATEDLY
    /// (≥ <c>UnreachableRepeatThreshold</c>) failed to reach. Two distinct terminal
    /// failures count, each ≥2 within its window:
    /// <list type="bullet">
    /// <item>a "selector resolved to no live object" GoalFailed — the named target
    /// is out of PVS (despawned/wandered off), no explored sighting route, frontier
    /// found nothing. Skipped the instant the target re-enters PVS so a real
    /// engagement is never suppressed.</item>
    /// <item>an "interaction target out of reach" GoalFailed — the object is
    /// PHYSICALLY IN VIEW but the server refused the Use/Pickup, walking the bot
    /// toward it (a different elevation/footing the XY-only arrival check misread as
    /// "arrived"). This case is NOT skipped while in view — that is the whole point:
    /// re-emitting the same goal just re-walks to the visible-but-unreachable object
    /// and re-fails (a weak model can re-emit a Pickup of an out-of-reach world item
    /// across many cycles, never dropped because it stays in view).</item>
    /// </list>
    /// Either way, re-emitting just re-fails and wakes another no-current-goal LLM
    /// call, burning per-day quota; the caller drops the repeat and defers to
    /// <see cref="EscapeOrFallback"/> (a real Explore that MOVES the bot).
    ///
    /// Scoped to the world-target verbs (<see cref="GoalKind.Attack"/>/<see
    /// cref="GoalKind.Pickup"/>/bare <see cref="GoalKind.Use"/>). NPC-directed verbs
    /// (Talk/Give) have their own dedicated loop guards and are deliberately not
    /// covered here; a two-object item-Use is exempt (item-miss vs target-miss
    /// ambiguity). Complements the world-object Use-churn guards
    /// (<see cref="IsStationaryWorldUseRepeat"/>/<see cref="IsLandblockWorldUseChurn"/>).
    ///
    /// Pure, no policy state: the implicit cooldown is the RECENCY-scoped durable
    /// GoalFailed window — once the failures age past <see cref="UnreachableRepeatRecency"/>
    /// the goal flows again (so the bot retries a once-unreachable target after it has had
    /// time to reposition). Correlates by the failed goal's OWN selector name (carried on
    /// the GoalFailed event) — no server dialogue text, no hardcoded names/wcids/landblocks.
    /// </summary>
    internal static bool IsUnreachableTargetRepeat(
        Goal goal, WorldStateProjection world, EventStream events, DateTimeOffset? nowUtc = null)
    {
        if (goal.Kind is not (GoalKind.Attack or GoalKind.Pickup or GoalKind.Use)) return false;
        // A two-object Use (Use an inventory ITEM on/with a target — a key on a
        // door, a reagent, etc.) fails with the SAME "no live object" text when the
        // ITEM does not resolve, not because the world TARGET vanished — and the
        // GoalFailed event carries only the target name, so we cannot tell them
        // apart after the fact. The in-view short-circuit also cannot clear it when
        // the target is self. So scope the Use coverage to a BARE world-object Use
        // (no item) — exactly the vanished corpse/chest/door case this catches.
        // Consequence (acknowledged, not a regression — Use was uncovered before
        // this): a two-object Use whose WORLD TARGET genuinely vanished is also
        // exempt here, and the world-object Use-churn guards likewise skip
        // item-Uses, so that rarer case stays uncaught by name alone. Catching it
        // safely would need the failure event to distinguish an item-miss from a
        // target-miss — a separate change, deliberately out of scope.
        if (goal.Kind is GoalKind.Use && goal.Item is { IsEmpty: false }) return false;
        var target = goal.Target;
        var targetName = target?.Name;
        if (target is null || string.IsNullOrWhiteSpace(targetName)) return false;

        // Recency-scoped read of the DURABLE GoalFailed window. The old events.Recent(N)
        // read the perception-dominated ring: consecutive same-name failures land on
        // SEPARATE decisions ~tens of seconds apart with hundreds of perception events
        // between, so the prior failure was evicted before the count reached the threshold
        // and the suppression fired only when two failures were adjacent in the ring
        // (≈never), so an unreachable target looped unbroken. RecentGoalFailures() is
        // durable (not ring-evicted); scope it to UnreachableRepeatRecency of nowUtc so a
        // once-unreachable name is retried after the bot has had time to reposition. SAME
        // eviction fix as IsLowHealthDeferredAttackRepeat / IsWieldNoWeaponRepeat.
        const int OutOfReachRepeatThreshold = 2;
        var cutoff = (nowUtc ?? DateTimeOffset.UtcNow) - UnreachableRepeatRecency;
        // Visible-but-UNREACHABLE case: a target the server repeatedly refused as OUT
        // OF REACH (it walked the bot toward the object after the interaction instead
        // of completing it — a different elevation/footing) is dropped EVEN WHILE IN
        // VIEW. The in-view short-circuit below is for the despawn case (let the motor
        // re-resolve a live snapshot); it must NOT rescue a goal that keeps re-walking
        // to a visible object the server will not let it interact with — that just
        // re-fails and wakes another no-current-goal LLM call every cycle.
        //
        // Drop the whole NAME (not just the failing guid). A nearer-but-suppressed
        // same-named object does NOT let the motor fall through to a farther reachable
        // sibling: Pickup/Use resolution (SelectorResolver.ResolveSingleNearest) picks
        // the NEAREST match and HandshakeDriver only NULLS it when it is suppressed
        // (treats it as unresolved) — it does not re-resolve to the next sibling. So
        // while a nearer instance stays out-of-reach, re-emitting the NAME just keeps
        // resolving and nulling that nearer guid. Dropping the name and deferring to
        // EscapeOrFallback (a real Explore that MOVES the bot) is the correct escape:
        // repositioning is what makes a different instance the nearest/reachable one.
        // Same name-keyed, recency-bounded, self-expiring shape as the no-live-object
        // count; correlates by the failed goal's OWN selector name only.
        {
            var outOfReach = 0;
            foreach (var ev in events.RecentGoalFailures())   // newest-first
            {
                if (ev.Utc < cutoff) break;                   // older than the window; rest are older too
                if (ev.Text is null ||
                    ev.Text.IndexOf(InteractOutOfReachFailMarker, StringComparison.Ordinal) < 0)
                    continue;
                if (string.IsNullOrWhiteSpace(ev.Name) ||
                    !string.Equals(ev.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (++outOfReach >= OutOfReachRepeatThreshold) return true;
            }
        }

        // Target currently in view → never suppress; the motor will resolve a
        // live snapshot and the real Attack can fire.
        if (world.Visible.Any(v => VisibleMatchesSelector(target, v)))
            return false;

        const int UnreachableRepeatThreshold = 2;
        int count = 0;
        foreach (var ev in events.RecentGoalFailures())   // newest-first
        {
            if (ev.Utc < cutoff) break;                   // older than the window; rest are older too
            if (ev.Text is null ||
                !ev.Text.EndsWith(NoLiveObjectFailSuffix, StringComparison.Ordinal))
                continue;
            if (string.IsNullOrWhiteSpace(ev.Name) ||
                !string.Equals(ev.Name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (++count >= UnreachableRepeatThreshold) return true;
        }
        return false;
    }

    /// <summary>
    /// True iff an LLM <see cref="GoalKind.Attack"/> OPTIONALLY targets a KIND
    /// the bot's OWN combat ledger marks <see cref="IsBeatenKind"/> — it has
    /// died / near-died / been ineffective against that kind with no kills
    /// (level-aware: a non-lethal loss is re-testable once the bot out-levels
    /// it; a lethal loss stays beaten). This is the SAME verdict the autonomous
    /// kill-commitment picker and the COMBAT SAFETY prompt rule already apply;
    /// a weak model can still misread its own record and re-pick such a kind,
    /// where re-attacking just loses again (often near-0 hit-rate).
    ///
    /// "OPTIONAL" excludes self-defense: when the named kind is currently an
    /// ACTIVE HOSTILE in view (attacking the bot now) the Attack is left alone
    /// — the Motor's losing-fight disengage and self-preservation reflexes own
    /// that case (mirroring the town-egress rule that never overrides a
    /// self-defense Attack). So only a CHOSEN, non-threatened engagement of a
    /// kind that keeps beating the bot is vetoed.
    ///
    /// Pure; bot-owned outcomes + own level only — no wcid/NPC/landblock, no
    /// game knowledge. Extracted for deterministic unit testing. The caller
    /// drops the goal and defers to <see cref="EscapeOrFallback"/>, which
    /// Explores away or lets the fallback autonomously pick a NOT-beaten target
    /// — it never re-attacks this kind.
    /// </summary>
    internal static bool IsOptionalAttackOnBeatenKind(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Attack) return false;
        var target = goal.Target;
        if (target is null) return false;
        // Attack selectors are name-first; fall back to the substring hook, and
        // keep any wcid so the ledger lookup can match by either identity (a
        // name-only check would silently miss a wcid- or substring-only target).
        var targetName = target.Name ?? target.NameContains;
        if (string.IsNullOrWhiteSpace(targetName) && target.Wcid is null) return false;

        // Self-defense exemption: a kind actively hostile in view is attacking
        // the bot now; fighting back (and the Motor's flee reflexes) own that —
        // do not veto. Only a non-threatened, chosen engagement is overridable.
        // Uses the Motor's exact-then-unique-fuzzy name semantics so a partial-name
        // Attack the Motor WILL resolve to a hostile in view is not wrongly vetoed.
        if (TargetResolvesToHostileInViewLikeMotor(target, world))
            return false;

        // Override an explicit Attack order ONLY to prevent an UNRECOVERABLE
        // outcome: a kind whose own ledger records an actual DEATH. A merely
        // SURVIVED loss (Deaths==0: only near-deaths or ineffective swings) is
        // NOT vetoed here. Survival during any re-attempt is enforced
        // independently by the Motor's low-health flee / self-preservation
        // gate, so this veto need not also block survivable kinds; and the
        // out-level re-test gate (below) keys on the bot's level, which a
        // survived loss does not raise, so vetoing here would bar re-engagement
        // indefinitely. A FATAL-loss kind stays beaten, re-testable only once
        // the bot out-levels the death. Bot-owned outcome counts + own level
        // only; no game knowledge. The lethal-beaten verdict itself is the shared
        // IsLethalBeatenKind helper, so this veto and the stalemate egress gate
        // (OnlyBeatenMonstersInView) can never disagree about which kinds count.
        return IsLethalBeatenKind(world.CombatHistoryFull, target.Wcid, targetName,
            world.Self.Level);
    }

    /// <summary>
    /// True iff an LLM <see cref="GoalKind.Attack"/> should be dropped because the
    /// bot cannot land a hit — it is NOT combat-capable (no wielded melee weapon,
    /// and no wielded missile weapon WITH ammo; see <see cref="IsCombatCapable"/>)
    /// AND cannot melee-unarmed (see <see cref="CanUnarmedMelee"/>) AND there is no
    /// live HOSTILE in view to defend against. An unarmed-with-blocked-melee attack
    /// is a doomed swing the server cancels (e.g. launcher forces Missile mode with
    /// no ammo); a truly unarmed bot (no weapon at all) CAN land fist hits and is
    /// NOT dropped here. SELF-DEFENSE is exempt: a HOSTILE actively engaging the bot
    /// keeps its Attack. Pure wire-state — wielded weapon/ammo slot bits + the
    /// ObservedHostile threat bit; the LLM still chose WHAT to fight, this only
    /// declines a genuinely doomed engagement. No game knowledge.
    /// </summary>
    internal static bool IsOptionalAttackWhileNotCombatCapable(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Attack) return false;
        // Usable weapon → not blocked.
        if (IsCombatCapable(world.Inventory)) return false;
        // No weapon in main-weapon slot → unarmed melee is viable right now.
        if (CanUnarmedMelee(world.Inventory)) return false;
        // Self-defense exempt: keep the Attack ONLY when its NAMED target is itself a
        // live HOSTILE (the bot is fighting back the thing engaging it). A check for
        // ANY hostile in view would be too broad — the Motor attacks the named
        // selector, NOT the hostile, so an Attack on a PASSIVE target while a
        // different hostile is elsewhere is still a doomed optional swing and must be
        // dropped. Mirrors IsOptionalAttackOnBeatenKind's target-specific exemption,
        // including the Motor's exact-then-unique-fuzzy name resolution.
        if (goal.Target is { } t && TargetResolvesToHostileInViewLikeMotor(t, world))
            return false;
        return true;
    }

    /// <summary>
    /// True iff <paramref name="target"/> resolves to a live HOSTILE in view using the
    /// SAME exact-then-unique-fuzzy name semantics as the Motor's
    /// <see cref="SelectorResolver"/> (Attack path: corpses excluded). Exact name
    /// (incl. the quoted-role strip), <c>NameContains</c>, and <c>Wcid</c> matches
    /// are the primary path; if NONE match, a UNIQUE whole-word-subsequence name
    /// match is accepted. Keeps the Attack self-defense exemptions in agreement with
    /// the target the Motor will actually attack, so a partial-name Attack on a
    /// hostile-in-view is not wrongly vetoed/dropped by the policy. Bot-owned
    /// perception only; no game knowledge.
    /// </summary>
    private static bool TargetResolvesToHostileInViewLikeMotor(Selector target, WorldStateProjection world)
    {
        // Primary: non-corpse visible objects matching by exact name / NameContains
        // / Wcid (the Motor returns ALL such and picks nearest). Self-defense fires
        // when any such match is hostile.
        var exact = world.Visible
            .Where(v => !v.IsCorpse && VisibleMatchesSelector(target, v))
            .ToList();
        if (exact.Count > 0)
            return exact.Any(v => v.ObservedHostile);
        // No exact match anywhere in view: mirror the Motor's UNIQUE whole-word-
        // subsequence fallback (only meaningful for a name selector).
        if (string.IsNullOrEmpty(target.Name)) return false;
        var fuzzy = world.Visible
            .Where(v => !v.IsCorpse
                        && HeadlessAcClient.Tactics.SelectorResolver.MatchesNameWordSubsequence(v.Name, target.Name))
            .ToList();
        return fuzzy.Count == 1 && fuzzy[0].ObservedHostile;
    }

    // The DECISION-path definition of a LETHAL-beaten kind: the bot's own
    // AGGREGATE ledger for this kind records an actual DEATH (Deaths>0) AND
    // IsBeatenKind still holds (0 kills, not out-levelled). Shared by the
    // explicit-Attack veto (IsOptionalAttackOnBeatenKind) and the stalemate
    // egress gate (OnlyBeatenMonstersInView) so the two NEVER drift — a kind the
    // veto blocks is exactly a kind that counts toward the egress, and vice
    // versa. A merely SURVIVED loss (Deaths==0) is NOT lethal-beaten, so both
    // sites keep the deadlock-fix option to re-attempt it. Aggregate own ledger +
    // own level only; no game knowledge.
    internal static bool IsLethalBeatenKind(
        IReadOnlyList<CombatHistoryEntry>? history, uint? wcid, string? name, int? currentLevel)
    {
        var record = FindCombatRecord(history, wcid, name);
        return record is { Deaths: > 0 }
            && IsBeatenKind(history, wcid, name, currentLevel,
                lethalRetestableWhenOutleveled: true);
    }

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
    /// True iff this Use goal is a STATIONARY repeat of the last accepted
    /// world-object Use: same canonical target, the bot has not changed
    /// landblock/cell and has not moved more than
    /// <see cref="StationaryUseEpsilon"/> units, and nothing entered or
    /// left inventory since that Use. Such a loop is a no-op (e.g. a door
    /// the motor opens but cannot path the bot THROUGH to the next cell) —
    /// the caller drops it and defers to the fallback so the bot tries a
    /// different action instead of re-locking the dead target every tick.
    /// Returns true once the same stationary Use is seen
    /// <see cref="StationaryUseRepeatThreshold"/> times in a row.
    ///
    /// <para>Stateful: each call updates the tracked Use identity/position.
    /// Only world-object Uses (<c>goal.Item is null</c>) are considered —
    /// inventory Uses are owned by
    /// <see cref="IsInventoryUseRecentlyDispatched"/>. The state stays
    /// active across drops so the bot remains unstuck until it actually
    /// moves (cell/position change) or inventory changes.</para>
    ///
    /// <para>Audit note: mechanical bookkeeping over the bot's OWN emission
    /// key + its OWN self cell/position + inventory-change events. Encodes
    /// no object-type knowledge (no door/chest/portal special-casing) and
    /// parses no server text.</para>
    /// </summary>
    internal bool IsStationaryWorldUseRepeat(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Use) return false;
        if (goal.Item is not null) return false;

        var key = CanonicalUseTargetKey(goal.Target);
        if (key is null) return false;

        var self = world.Self;
        var prev = _lastWorldUseRepeat;

        bool sameTarget = prev is not null &&
            string.Equals(prev.Key, key, StringComparison.OrdinalIgnoreCase);

        bool moved = prev is null
            || prev.Landblock != self.Landblock
            || prev.Cell != self.CellId
            || SquaredXyDistance(prev.X, prev.Y, self.PositionX, self.PositionY) > StationaryUseEpsilonSq;

        bool inventoryChanged = prev is not null &&
            (events.HasNewSince(EventKind.InventoryItemAdded, prev.SequenceFloor)
             || events.HasNewSince(EventKind.InventoryItemRemoved, prev.SequenceFloor));

        if (!sameTarget || moved || inventoryChanged)
        {
            _lastWorldUseRepeat = new WorldUseRepeat(
                key, self.Landblock, self.CellId, self.PositionX, self.PositionY,
                events.NextSequence, 1);
            return false;
        }

        int count = prev!.Count + 1;
        _lastWorldUseRepeat = prev with
        {
            X = self.PositionX,
            Y = self.PositionY,
            SequenceFloor = events.NextSequence,
            Count = count,
        };
        return count >= StationaryUseRepeatThreshold;
    }

    /// <summary>
    /// True when an LLM-authored bare <see cref="GoalKind.Use"/> targets a
    /// world-object identity the bot has already Used
    /// <see cref="LandblockWorldUseChurnThreshold"/> times within the CURRENT
    /// landblock episode — a multi-door TOUR that
    /// <see cref="IsStationaryWorldUseRepeat"/> (which resets on any movement)
    /// structurally cannot catch, because the bot walks between the doors. The
    /// episode is keyed on the LANDBLOCK and counts per-target bare-Use
    /// emissions ACROSS intervening cell/position moves; it resets ONLY on a
    /// landblock change (genuine egress) or an inventory change (a productive
    /// key-Use / loot). A never-before-Used target-identity in the episode gets
    /// first-use forgiveness (count 1), so a legitimate sequence of DISTINCT
    /// doors toward an exit never fires; only a RE-Used identity reaching the
    /// threshold LATCHES as suppressed for the rest of the episode (so a picker
    /// re-arrival + LLM re-emit cannot leak one Use per cycle). The per-target
    /// identity is the GUID when the selector carries one (stable, cell-
    /// independent), else the NAME disambiguated by the bot's self CELL so three
    /// DISTINCT same-named ("Door") corridor doors in distinct cells never
    /// collapse onto one count. Pure mechanical loop bookkeeping over the bot's
    /// OWN emission identity (<see cref="CanonicalUseTargetKey"/> + self cell),
    /// its OWN self landblock, and the PRESENCE of inventory events — no
    /// object-type, door, or zone knowledge. Returns false for under-specified
    /// selectors (no stable identity).
    /// </summary>
    internal bool IsLandblockWorldUseChurn(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Use) return false;
        if (goal.Item is not null) return false;   // item-Use is owned by the inventory dedup
        var targetKey = CanonicalUseTargetKey(goal.Target);
        if (targetKey is null) return false;

        var self = world.Self;

        // A GUID is a stable per-object identity, so key on it alone — re-Using
        // the SAME guid is unambiguously one object regardless of approach cell.
        // A NAME-only selector (the common case: the LLM emits {"name":"Door"})
        // cannot tell three DISTINCT same-named doors in a corridor apart, so
        // disambiguate it by the bot's self CELL at emission: when the picker
        // has parked the bot at a door to Use it, the self cell IS that door's
        // interaction spot — distinct corridor doors sit in DISTINCT cells and
        // never collapse onto one count, while a door re-Used from the same spot
        // keys identically across the tour. Cell is decoded coordinate state,
        // not game knowledge.
        var key = goal.Target!.Guid is not null
            ? targetKey
            : $"{targetKey}@0x{self.CellId:X8}";

        var ep = _worldUseChurnEpisode;

        // Reset on a landblock change (egress succeeded) or inventory change (a
        // productive key-Use/loot). NOT on cell/position change — the tour moves
        // between doors WITHIN the landblock, which must not clear the counts.
        bool landblockChanged = ep is null || ep.Landblock != self.Landblock;
        bool inventoryChanged = ep is not null &&
            (events.HasNewSince(EventKind.InventoryItemAdded, ep.FloorSequence)
             || events.HasNewSince(EventKind.InventoryItemRemoved, ep.FloorSequence));

        if (ep is null || landblockChanged || inventoryChanged)
        {
            // cp-2371: carry the cumulative per-key counts across an inventory-
            // change reset within the SAME landblock (a barren same-object loop
            // can hide behind unrelated productive Pickups); a landblock change
            // wipes them (genuine egress).
            var carriedCounts = (!landblockChanged && ep is not null)
                ? ep.PersistentUseCounts : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var carriedSuppressed = (!landblockChanged && ep is not null)
                ? ep.PersistentSuppressed : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ep = new WorldUseChurnEpisode { Landblock = self.Landblock, FloorSequence = events.NextSequence };
            ep.PersistentUseCounts = carriedCounts;
            ep.PersistentSuppressed = carriedSuppressed;
            ep.UseCounts[key] = 1;
            _worldUseChurnEpisode = ep;

            int persistentReset = (carriedCounts.TryGetValue(key, out var pcr) ? pcr : 0) + 1;
            carriedCounts[key] = persistentReset;
            if (carriedSuppressed.Contains(key) || persistentReset >= PersistentWorldUseChurnThreshold)
            {
                carriedSuppressed.Add(key);
                return true;
            }
            return false;
        }

        // cp-2371: a single object re-Used past the CUMULATIVE threshold (across
        // intra-landblock inventory resets) stays suppressed until egress.
        if (ep.PersistentSuppressed.Contains(key))
        {
            ep.FloorSequence = events.NextSequence;
            return true;
        }

        // Already latched as a no-egress loop this episode → keep suppressing.
        // The reset conditions above are the ONLY way out, so a picker
        // re-arrival + LLM re-emit cannot leak one Use per cycle.
        if (ep.Suppressed.Contains(key))
        {
            ep.FloorSequence = events.NextSequence;
            return true;
        }

        // cp-2359: a barren TOUR — many DISTINCT world objects Used in this
        // landblock with no egress and no productive inventory change. Once
        // latched, ANY bare world-object Use is deferred to Explore until the
        // episode resets (egress or productive loot), so the bot stops working
        // every container/door in a tapped-out zone and heads out to hunt. The
        // latch is episode-wide (not per-key) because the tour is over DIFFERENT
        // objects; the per-key Suppressed set above cannot catch it.
        if (ep.DistinctChurnLatched)
        {
            ep.FloorSequence = events.NextSequence;
            return true;
        }

        int count = (ep.UseCounts.TryGetValue(key, out var c) ? c : 0) + 1;
        ep.UseCounts[key] = count;
        ep.FloorSequence = events.NextSequence;

        // cp-2371: also advance the cumulative per-key count (survives inventory
        // resets) and latch the SAME object re-Used past the cumulative
        // threshold, so a barren same-object loop cannot hide behind interleaved
        // unrelated Pickups.
        int persistent = (ep.PersistentUseCounts.TryGetValue(key, out var pc) ? pc : 0) + 1;
        ep.PersistentUseCounts[key] = persistent;
        if (persistent >= PersistentWorldUseChurnThreshold)
        {
            ep.PersistentSuppressed.Add(key);
            return true;
        }

        if (ep.UseCounts.Count >= LandblockDistinctUseChurnThreshold)
        {
            ep.DistinctChurnLatched = true;
            return true;
        }

        if (count >= LandblockWorldUseChurnThreshold)
        {
            ep.Suppressed.Add(key);
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when an LLM-authored <see cref="GoalKind.Talk"/> at the SAME
    /// stationary NPC has been re-emitted <see cref="NpcTalkRepeatThreshold"/>
    /// times in a row with no movement and no inventory change — an
    /// exhausted conversation the LLM keeps re-proposing because the server
    /// re-emits the same canned reply on every Talk. Pure mechanical
    /// bookkeeping over the bot's OWN emission identity, its OWN self
    /// cell/position, and inventory-change events — parses NO server dialog
    /// text and encodes no NPC knowledge (mirrors
    /// <see cref="IsStationaryWorldUseRepeat"/>). Returns false for
    /// under-specified selectors (no stable per-NPC identity to loop-break).
    /// A real multi-step turn-in changes inventory (reset) and walking
    /// between NPCs changes the cell (reset), so genuine progress is never
    /// suppressed.
    /// </summary>
    internal bool IsExhaustedNpcTalkRepeat(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Talk) return false;

        var key = CanonicalUseTargetKey(goal.Target);
        if (key is null) return false;

        var self = world.Self;
        var prev = _lastNpcTalkRepeat;

        bool sameTarget = prev is not null &&
            string.Equals(prev.Key, key, StringComparison.OrdinalIgnoreCase);

        bool moved = prev is null
            || prev.Landblock != self.Landblock
            || prev.Cell != self.CellId
            || SquaredXyDistance(prev.X, prev.Y, self.PositionX, self.PositionY) > StationaryUseEpsilonSq;

        bool inventoryChanged = prev is not null &&
            (events.HasNewSince(EventKind.InventoryItemAdded, prev.SequenceFloor)
             || events.HasNewSince(EventKind.InventoryItemRemoved, prev.SequenceFloor));

        if (!sameTarget || moved || inventoryChanged)
        {
            _lastNpcTalkRepeat = new NpcTalkRepeat(
                key, self.Landblock, self.CellId, self.PositionX, self.PositionY,
                events.NextSequence, 1);
            return false;
        }

        int count = prev!.Count + 1;
        _lastNpcTalkRepeat = prev with
        {
            X = self.PositionX,
            Y = self.PositionY,
            SequenceFloor = events.NextSequence,
            Count = count,
        };
        return count >= NpcTalkRepeatThreshold;
    }

    /// <summary>
    /// True when the bot is fixating on ONE stationary target across the
    /// INTERACT goal kinds — a world-object <see cref="GoalKind.Use"/>
    /// (<c>goal.Item is null</c>) or a <see cref="GoalKind.Pickup"/> — by
    /// re-emitting interactions at the SAME target
    /// <see cref="InteractFixationThreshold"/> times in a row with no
    /// movement and no inventory change. The per-kind guards
    /// (<see cref="IsStationaryWorldUseRepeat"/>,
    /// <see cref="IsExhaustedNpcTalkRepeat"/>) each count only their own
    /// GoalKind, so a mixed alternation (classically Use{Corpse} ↔
    /// Pickup{Corpse} on an emptied corpse after a kill) slips past both —
    /// Use never reaches its threshold consecutively and Pickup is
    /// unguarded. This guard closes that gap by counting across the interact
    /// kinds on the SAME canonical target.
    ///
    /// <para>Pure mechanical bookkeeping, identical in shape to the per-kind
    /// guards: keys on the bot's OWN emission identity
    /// (<see cref="CanonicalUseTargetKey"/>), its OWN self cell/position, and
    /// inventory-change EVENT PRESENCE. Parses NO server text and encodes no
    /// object-type knowledge. A real loot (Slice Q auto-loot or a successful
    /// Pickup) raises InventoryItemAdded/Removed and resets the streak, so a
    /// productive interaction is never suppressed; movement or a different
    /// target also resets. Under-specified selectors (no guid/exact-name) are
    /// not guarded.</para>
    /// </summary>
    internal bool IsStationaryInteractFixation(Goal goal, WorldStateProjection world, EventStream events)
    {
        bool isInteract = goal.Kind == GoalKind.Pickup
            || (goal.Kind == GoalKind.Use && goal.Item is null);
        if (!isInteract) return false;

        var key = CanonicalUseTargetKey(goal.Target);
        if (key is null) return false;

        var self = world.Self;
        var prev = _lastInteractFixation;

        bool sameTarget = prev is not null &&
            string.Equals(prev.Key, key, StringComparison.OrdinalIgnoreCase);

        bool moved = prev is null
            || prev.Landblock != self.Landblock
            || prev.Cell != self.CellId
            || SquaredXyDistance(prev.X, prev.Y, self.PositionX, self.PositionY) > StationaryUseEpsilonSq;

        bool inventoryChanged = prev is not null &&
            (events.HasNewSince(EventKind.InventoryItemAdded, prev.SequenceFloor)
             || events.HasNewSince(EventKind.InventoryItemRemoved, prev.SequenceFloor));

        if (!sameTarget || moved || inventoryChanged)
        {
            _lastInteractFixation = new InteractFixation(
                key, self.Landblock, self.CellId, self.PositionX, self.PositionY,
                events.NextSequence, 1);
            return false;
        }

        int interactCount = prev!.Count + 1;
        _lastInteractFixation = prev with
        {
            X = self.PositionX,
            Y = self.PositionY,
            SequenceFloor = events.NextSequence,
            Count = interactCount,
        };
        return interactCount >= InteractFixationThreshold;
    }

    /// <summary>
    /// True when the bot is churning Talk emissions over a SMALL cyclic set of
    /// NPCs — the 2+-NPC alternation that <see cref="IsExhaustedNpcTalkRepeat"/>
    /// (which resets on every target change) structurally cannot catch — while
    /// STATIONARY, with NO server-observable progress, and NO dialog NOVELTY
    /// between emissions. Complements the single-NPC guard; requires at least 2
    /// distinct targets so it never double-counts a same-NPC fixation.
    ///
    /// <para>Pure mechanical loop-detection, identical in spirit to the per-kind
    /// guards: it keys on the bot's OWN Talk emission identities
    /// (<see cref="CanonicalUseTargetKey"/>), its OWN self cell/position, the
    /// PRESENCE of inventory / landblock / self-progression EVENTS, and the
    /// NOVELTY of server text by normalized hash (NEW vs already-seen — it never
    /// interprets the text). A real turn-in (inventory), a zone change
    /// (landblock), an XP/level gain (self-progression), a move, or a
    /// genuinely new line of dialog all reset the streak, so a productive
    /// referral chain is never suppressed; only a stationary no-progress,
    /// no-novelty cycle fires. No NPC names, wcids, or quest content.</para>
    /// </summary>
    internal bool IsMultiNpcTalkChurn(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Talk) return false;

        var key = CanonicalUseTargetKey(goal.Target);
        if (key is null) return false;

        var self = world.Self;
        var ep = _talkChurnEpisode;

        // Hard reset: no episode yet, the bot moved, or any server-observable
        // progress occurred since the last counted Talk. Progress means the
        // conversation is advancing — never a dead loop.
        bool moved = ep is null
            || ep.Landblock != self.Landblock
            || ep.Cell != self.CellId
            || SquaredXyDistance(ep.X, ep.Y, self.PositionX, self.PositionY) > StationaryUseEpsilonSq;
        bool progressed = ep is not null &&
            (events.HasNewSince(EventKind.InventoryItemAdded, ep.FloorSequence)
             || events.HasNewSince(EventKind.InventoryItemRemoved, ep.FloorSequence)
             || events.HasNewSince(EventKind.LandblockChanged, ep.FloorSequence)
             || events.HasNewSince(EventKind.SelfProgressChanged, ep.FloorSequence));

        if (ep is null || moved || progressed)
        {
            ep = new TalkChurnEpisode
            {
                FloorSequence = events.NextSequence,
                Landblock = self.Landblock,
                Cell = self.CellId,
                X = self.PositionX,
                Y = self.PositionY,
                StaleTalks = 0,
            };
            ep.Targets.Add(key);
            // Seed with dialog already in the buffer so a later re-send of the
            // SAME greeting reads as repetition, not novelty.
            foreach (var fp in DialogFingerprintsSince(events, 0))
                ep.SeenDialogFingerprints.Add(fp);
            _talkChurnEpisode = ep;
            return false;
        }

        // Dialog NOVELTY since the last counted Talk: a fingerprint the episode
        // has not seen means the conversation advanced (productive).
        bool novelDialog = false;
        foreach (var fp in DialogFingerprintsSince(events, ep.FloorSequence))
            if (ep.SeenDialogFingerprints.Add(fp))
                novelDialog = true;

        ep.Targets.Add(key);
        ep.X = self.PositionX;
        ep.Y = self.PositionY;
        ep.FloorSequence = events.NextSequence;

        // The Talk frontier grew past a tight cycle → traversal, not a loop.
        if (ep.Targets.Count > MultiNpcTalkChurnMaxTargets)
        {
            _talkChurnEpisode = null;
            return false;
        }

        if (novelDialog)
        {
            ep.StaleTalks = 0;
            return false;
        }

        ep.StaleTalks++;
        return ep.StaleTalks >= MultiNpcTalkChurnStaleThreshold && ep.Targets.Count >= 2;
    }

    /// <summary>
    /// True when the bot is cycling Talk over a SMALL set (&lt;= <see
    /// cref="MultiNpcTalkChurnMaxTargets"/>) of NPCs while ROVING — walking
    /// between them across the cells of one landblock. <see
    /// cref="IsMultiNpcTalkChurn"/> resets on intra-landblock movement and <see
    /// cref="IsRovingNpcTalkLoop"/> needs a single target, so a referral
    /// ping-pong between two NPCs at different cells slips past BOTH. Counts STALE
    /// (no dialog-novelty) Talks over the &lt;=N-target cycle, resetting on LEAVING
    /// the landblock or any server-observable PROGRESS (inventory add/remove,
    /// landblock change, self-progression) or NEW dialog text by hash — so an
    /// advancing multi-NPC conversation is never suppressed. Unlike the stationary
    /// multi-NPC guard it does NOT reset on cell/position movement, so an
    /// intra-landblock roving cycle is caught. Fires at <see
    /// cref="RovingMultiNpcTalkChurnStaleThreshold"/> and resets on fire so
    /// suppression is never permanent. Pure mechanical bookkeeping: the bot's OWN
    /// Talk emissions + progress-event PRESENCE + dialog-text novelty by hash. No
    /// NPC names, wcids, or quest content.
    /// </summary>
    internal bool IsRovingMultiNpcTalkChurn(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Talk) return false;

        // GUID-backed identity (mirrors IsRovingNpcTalkLoop): an LLM Talk goal is
        // NAME-only, so a name key would CONFLATE two DIFFERENT NPCs that share a
        // name (e.g. two generic townsfolk) into one target — the <=2-NPC cycle
        // would never reach 2 and never fire on a same-named ping-pong.
        // RovingTalkTargetGuidKey re-keys a name to the NEAREST visible instance's
        // guid, distinguishing same-named NPCs by position; fall back to the
        // canonical name key when the target resolves to no visible object.
        var key = RovingTalkTargetGuidKey(goal.Target, world) ?? CanonicalUseTargetKey(goal.Target);
        if (key is null) return false;

        var self = world.Self;
        var ep = _rovingTalkChurnEpisode;

        // Reset on LEAVING the landblock (genuine traversal) or server-observable
        // progress — but NOT on intra-landblock cell/position movement (the sole
        // distinction from IsMultiNpcTalkChurn). An A<->B ping-pong across the
        // cells of ONE landblock is a roving loop, not traversal.
        bool leftLandblock = ep is null || ep.Landblock != self.Landblock;
        bool progressed = ep is not null &&
            (events.HasNewSince(EventKind.InventoryItemAdded, ep.FloorSequence)
             || events.HasNewSince(EventKind.InventoryItemRemoved, ep.FloorSequence)
             || events.HasNewSince(EventKind.LandblockChanged, ep.FloorSequence)
             || events.HasNewSince(EventKind.SelfProgressChanged, ep.FloorSequence));

        if (ep is null || leftLandblock || progressed)
        {
            ep = new TalkChurnEpisode
            {
                FloorSequence = events.NextSequence,
                Landblock = self.Landblock,
                Cell = self.CellId,
                X = self.PositionX,
                Y = self.PositionY,
                StaleTalks = 0,
            };
            ep.Targets.Add(key);
            // Seed with dialog already in the buffer so a later re-send of the
            // SAME greeting reads as repetition, not novelty.
            foreach (var fp in DialogFingerprintsSince(events, 0))
                ep.SeenDialogFingerprints.Add(fp);
            _rovingTalkChurnEpisode = ep;
            return false;
        }

        bool novelDialog = false;
        foreach (var fp in DialogFingerprintsSince(events, ep.FloorSequence))
            if (ep.SeenDialogFingerprints.Add(fp))
                novelDialog = true;

        ep.Targets.Add(key);
        ep.FloorSequence = events.NextSequence;

        // The Talk frontier grew past a tight cycle → traversal (the bot is
        // talking many DISTINCT NPCs as it moves), not a loop.
        if (ep.Targets.Count > MultiNpcTalkChurnMaxTargets)
        {
            _rovingTalkChurnEpisode = null;
            return false;
        }

        if (novelDialog)
        {
            ep.StaleTalks = 0;
            return false;
        }

        ep.StaleTalks++;
        if (ep.StaleTalks >= RovingMultiNpcTalkChurnStaleThreshold && ep.Targets.Count >= 2)
        {
            // Reset on fire so suppression is never permanent (mirrors the roving
            // single-NPC guard): the drop defers to the fallback (the bot does
            // something else), and a later re-attempt at these NPCs starts a FRESH
            // streak that only re-fires if it loops again.
            _rovingTalkChurnEpisode = null;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when the bot is looping Talk on a SINGLE NPC while ROVING — walking
    /// up to (or around) the same NPC each emission. <see cref="IsExhaustedNpcTalkRepeat"/>
    /// resets on movement and <see cref="IsMultiNpcTalkChurn"/> requires >=2
    /// targets, so a moving same-NPC loop slips past BOTH. Counts consecutive
    /// STALE Talks to the SAME canonical target and
    /// fires at <see cref="RovingNpcTalkLoopStaleThreshold"/>. Resets on a
    /// DIFFERENT target or any server-observable PROGRESS — inventory add/remove,
    /// landblock change, self-progression, or a NEW line of dialog (normalized
    /// hash novelty). Unlike the stationary guards it does NOT reset on movement,
    /// so a roving same-NPC loop is caught; like the multi-NPC guard it still
    /// never suppresses an advancing conversation. Pure mechanical bookkeeping:
    /// the bot's OWN emission identity + progress-event PRESENCE + dialog novelty
    /// by hash. No NPC names, wcids, or quest content.
    /// </summary>
    internal bool IsRovingNpcTalkLoop(Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Talk) return false;

        // GUID-backed identity. An LLM Talk goal carries a NAME only (the guid
        // is resolved downstream by the Motor), so a guid-only key — as this
        // guard previously required — skipped EVERY LLM Talk goal and never
        // fired on the roving single-NPC loops it exists to catch (live: the
        // bot Talked one silent NPC 10x because this guard sat out the whole
        // loop). RovingTalkTargetGuidKey re-keys a name-only target to the
        // NEAREST visible object of that name: a STABLE guid while the bot
        // loops one stationary NPC (correct break), and one that CHANGES —
        // resetting the streak — if the bot genuinely moves to a different
        // instance, so distinct same-named NPCs are never conflated. Skip when
        // the target resolves to no visible object (the bot is not at it).
        var key = RovingTalkTargetGuidKey(goal.Target, world);
        if (key is null) return false;

        var ep = _rovingNpcTalkLoop;
        bool sameTarget = ep is not null &&
            string.Equals(ep.Key, key, StringComparison.OrdinalIgnoreCase);
        bool progressed = ep is not null &&
            (events.HasNewSince(EventKind.InventoryItemAdded, ep.FloorSequence)
             || events.HasNewSince(EventKind.InventoryItemRemoved, ep.FloorSequence)
             || events.HasNewSince(EventKind.LandblockChanged, ep.FloorSequence)
             || events.HasNewSince(EventKind.SelfProgressChanged, ep.FloorSequence));

        // Reset on a DIFFERENT NPC or any server-observable progress — but NOT
        // on movement (the sole distinction from IsExhaustedNpcTalkRepeat). Seed
        // the seen-dialog set so a later re-send of the SAME greeting reads as
        // repetition, not novelty.
        if (ep is null || !sameTarget || progressed)
        {
            ep = new RovingNpcTalkLoop { Key = key, FloorSequence = events.NextSequence, StaleTalks = 0 };
            foreach (var fp in DialogFingerprintsSince(events, 0))
                ep.SeenDialogFingerprints.Add(fp);
            _rovingNpcTalkLoop = ep;
            return false;
        }

        bool novelDialog = false;
        foreach (var fp in DialogFingerprintsSince(events, ep.FloorSequence))
            if (ep.SeenDialogFingerprints.Add(fp)) novelDialog = true;
        ep.FloorSequence = events.NextSequence;

        // Raw backstop FIRST: count this continuing same-NPC Talk regardless of
        // dialog novelty. The streak already reset above on any inventory /
        // landblock / self-progress, so a long run of consecutive Talks to ONE
        // NPC with none of those is an unproductive loop even when the NPC keeps
        // emitting NOVEL canned lines (which would otherwise reset StaleTalks and
        // hide the loop — live: 15x Worcer). Mechanical repeat-count over the
        // bot's OWN Talk emissions; no NPC/quest content.
        ep.TotalTalks++;
        if (ep.TotalTalks >= RovingNpcTalkLoopRawThreshold)
        {
            _rovingNpcTalkLoop = null;
            return true;
        }

        if (novelDialog) { ep.StaleTalks = 0; return false; }

        ep.StaleTalks++;
        if (ep.StaleTalks >= RovingNpcTalkLoopStaleThreshold)
        {
            // Reset on fire so suppression is NEVER permanent. The drop defers to
            // the fallback (the bot does something else); a later re-attempt at
            // the same NPC starts a FRESH streak and only re-fires if it loops
            // again. A genuinely-advancing NPC produces dialog/progress on the
            // re-attempt and is never blocked indefinitely; a true dead loop is
            // still broken on every Nth stale Talk.
            _rovingNpcTalkLoop = null;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Movement- and novelty-independent single-NPC Talk-fixation backstop. True
    /// when the bot's CURRENT Talk goal targets an NPC it has already Talked
    /// <see cref="SingleNpcTalkHistoryThreshold"/>+ times within its last
    /// <see cref="SingleNpcTalkHistoryWindowGoals"/> emitted goals. Unlike
    /// <see cref="IsExhaustedNpcTalkRepeat"/> (resets on movement) and
    /// <see cref="IsRovingNpcTalkLoop"/> (resets its raw streak on any inventory /
    /// landblock / self-progress event), this reads the durable goal-emission
    /// history, so a Talk loop INTERLEAVED with incidental combat/loot — which
    /// resets the episode guards every cycle — is still caught.
    ///
    /// <para>Keyed on the LLM goal's NAME (an LLM Talk target carries a name
    /// only), matched against the bot's own past Talk emissions exactly as the
    /// <c>## Recent Talk</c> recency render counts them. Counts the bot's OWN
    /// emissions; no NPC/quest content, no game knowledge.</para>
    /// </summary>
    internal static bool IsSingleNpcTalkFixationByHistory(Goal goal, EventStream events)
    {
        if (goal.Kind != GoalKind.Talk) return false;
        var name = goal.Target?.Name ?? goal.Target?.NameContains;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return CountTalkGoalsToNameInLastN(events, name, SingleNpcTalkHistoryWindowGoals)
            >= SingleNpcTalkHistoryThreshold;
    }

    /// <summary>
    /// cp-2415: true when this Talk goal's resolved NPC is currently within the
    /// TTL suppression window opened by a recently-confirmed roving Talk loop —
    /// the LLM's persistent intent should not re-drive the bot back to it yet.
    /// Side-effect: prunes an expired entry so the next Talk re-probes the NPC.
    /// Non-Talk goals and name-only-unresolvable targets are never suppressed.
    /// </summary>
    internal bool IsTalkLoopTtlSuppressed(Goal goal, WorldStateProjection world, DateTimeOffset nowUtc)
    {
        if (goal.Kind != GoalKind.Talk) return false;
        var key = RovingTalkTargetGuidKey(goal.Target, world);
        if (key is null) return false;
        if (!_talkLoopSuppressedUntil.TryGetValue(key, out var until)) return false;
        if (nowUtc < until) return true;
        _talkLoopSuppressedUntil.Remove(key);   // TTL expired — allow a re-probe
        return false;
    }

    /// <summary>
    /// cp-2415: open a TTL suppression window for this Talk goal's resolved NPC
    /// after the roving guard confirms an exhausted loop on it. Keyed by the same
    /// resolved guid the roving guard uses; no-op for unresolvable targets.
    /// </summary>
    internal void RecordTalkLoopSuppression(Goal goal, WorldStateProjection world, DateTimeOffset nowUtc)
    {
        var key = RovingTalkTargetGuidKey(goal.Target, world);
        if (key is null) return;
        // Opportunistic prune so the map cannot grow unbounded over a long
        // session — an expired key is otherwise only removed lazily when that
        // SAME key is re-queried, so keys recorded once and never re-Talked would
        // linger. Records happen only on a roving-guard fire (uncommon) and the
        // map only ever holds the few NPCs suppressed within the last TTL window,
        // so this stays O(small).
        if (_talkLoopSuppressedUntil.Count > 0)
        {
            foreach (var stale in _talkLoopSuppressedUntil
                         .Where(kv => nowUtc >= kv.Value).Select(kv => kv.Key).ToList())
                _talkLoopSuppressedUntil.Remove(stale);
        }
        _talkLoopSuppressedUntil[key] = nowUtc + TalkLoopSuppressionTtl;
    }

    /// <summary>Test-only: current number of live talk-loop suppression entries.</summary>
    internal int TalkLoopSuppressionEntryCount => _talkLoopSuppressedUntil.Count;

    // Normalized dialog fingerprints at sequence >= floor (newest-first source,
    // order irrelevant). A fingerprint is the dialog kind + whitespace-collapsed
    // lower-cased text — a structural identity that tells NEW server text from a
    // re-sent repeat WITHOUT interpreting meaning. Empty/blank text is skipped.
    private static IEnumerable<string> DialogFingerprintsSince(EventStream events, long floor)
    {
        foreach (var e in events.Recent())
        {
            if (e.Sequence < floor) continue;
            if (System.Array.IndexOf(TalkChurnDialogKinds, e.Kind) < 0) continue;
            if (string.IsNullOrWhiteSpace(e.Text)) continue;
            var norm = System.Text.RegularExpressions.Regex.Replace(
                e.Text.Trim().ToLowerInvariant(), "\\s+", " ");
            yield return $"{(int)e.Kind}:{norm}";
        }
    }

    private static float SquaredXyDistance(float ax, float ay, float bx, float by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Canonical identity for a world-object Use target: the guid token if
    /// present, else the exact-name token, else null (an under-specified
    /// selector — name_contains / wcid / mask only — has no stable
    /// per-object identity to loop-break on, so it is not guarded). Built
    /// from the same fields <see cref="Selector.ToString"/> renders so it
    /// agrees with the "Location &amp; recency" Use-emission key.
    /// </summary>
    private static string? CanonicalUseTargetKey(Selector? sel)
    {
        if (sel is null) return null;
        if (sel.Guid is { } g) return $"guid=0x{g:X8}";
        if (!string.IsNullOrEmpty(sel.Name)) return $"name=\"{sel.Name}\"";
        return null;
    }

    /// <summary>
    /// Stable guid key for the roving Talk-loop guard. A guid-bearing selector
    /// keys directly. A NAME-only selector — what an LLM Talk goal always is,
    /// since the Motor resolves the guid downstream — is re-keyed to the
    /// NEAREST visible object of that name (deterministic: nearest distance,
    /// ties broken by the lowest guid). That yields a STABLE key while the bot
    /// loops one stationary NPC (so the guard can fire) yet a DIFFERENT key if
    /// the bot moves to a genuinely distinct instance (so the streak resets and
    /// distinct same-named NPCs are never conflated). Returns null when the
    /// selector is unusable or matches no visible object. Pure mechanical
    /// identity resolution from the bot's OWN visible-object projection — no NPC
    /// names, wcids, or quest content are hardcoded.
    /// </summary>
    private static string? RovingTalkTargetGuidKey(Selector? sel, WorldStateProjection world)
    {
        if (sel is null) return null;
        if (sel.Guid is { } g) return $"guid=0x{g:X8}";

        var name = sel.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        VisibleObjectProjection? best = null;
        foreach (var v in world.Visible)
        {
            if (!string.Equals(v.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null) { best = v; continue; }
            var vd = v.Distance ?? float.MaxValue;
            var bd = best.Distance ?? float.MaxValue;
            if (vd < bd || (vd == bd && v.Guid < best.Guid)) best = v;
        }
        return best is null ? null : $"guid=0x{best.Guid:X8}";
    }
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
             or EventKind.PickerArrivedNoAction
             or EventKind.CombatFeedback
             or EventKind.SelfProgressChanged
             or EventKind.InboundDamageTaken;

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

    // True iff a STRATEGIC intent-completion goal-lifecycle event arrived since the
    // floor. An IntentStack auto-pop emits a GoalId-LESS GoalCompleted/Failed/Expired
    // (tactical goal churn carries a GoalId); a strategic completion changes the
    // bot's TOP objective, so it must force a fresh LLM look. The fixated-Talk
    // kickoff-skip consults this so it never suppresses the LLM across a top-intent
    // change (gpt-5.4 review). Mirrors the GoalId refinement in the event-level
    // plan-invalidation classifier; reads the bot's OWN goal-lifecycle, no game
    // knowledge.
    internal static bool HasNewStrategicIntentCompletionSince(EventStream events, long sequenceFloor) =>
        events.Recent()
            .TakeWhile(e => e.Sequence >= sequenceFloor)
            .Any(e => IsGoalLifecycleKind(e.Kind) && e.GoalId is null);

    // True when the strategic stack has NO ACTIVE top objective: a non-null stack
    // whose top is gone (popped to empty) OR whose top status is non-Active — the
    // SAME condition the `## No active objective` prompt capsule renders on. The
    // critical case the event-based HasNewStrategicIntentCompletionSince MISSES is a
    // ROOT DEADLINE elapsing: Intent.CheckTopForCompletion marks the root Blocked IN
    // PLACE and returns null WITHOUT emitting any Goal* lifecycle event, so a
    // reduce-llm-call skip gate keyed only on lifecycle events would keep
    // short-circuiting the LLM while the bot is actually objective-less and must
    // re-deliberate (REPLACE_TOP the blocked root). The skip gates AND the prompt
    // capsule therefore agree: in this state, wake the LLM. A NULL stack (no intent
    // system engaged — the bot is free-wandering) is NOT this state, so the gates
    // still skip the common no-stack travel case. Reads the bot's OWN stack status;
    // no game knowledge.
    internal static bool StackHasNoActiveObjective(IntentStack? stack) =>
        stack is not null && (stack.Top is null || stack.Top.Status != IntentLifecycle.Active);

    // How many of the bot's most-recent goal emissions the wander detector inspects, and how
    // many of them must be UNTARGETED Explores to count as a confirmed XP-hoarding wander. ">= 2"
    // (not 1) requires a SUSTAINED aimless drift, so a single Explore between productive actions
    // (e.g. one step toward a known place, or a one-off egress) does not trip it.
    private const int RecentWanderEmissionWindow = 10;
    private const int WanderStreakSpendThreshold = 2;

    // Count the bot's OWN recent UNTARGETED Explore emissions — the aimless "wander" pattern:
    // an `Explore` goal with NO concrete target (target `<empty>`, no name, or the generic
    // name "anywhere"; and no object guid). A DIRECTED Explore toward a NAMED place or a specific
    // guid is purposeful travel and is NOT counted. Purely structural read of the bot's own
    // GoalEmitted history (the "VERB target=... item=... source=..." text the executor records,
    // matching HasRecentRepeatedGoalOfKinds' parse) — no server text, no game knowledge. Used by
    // the SPEND-BEFORE-WANDER gate so an XP-hoarding wander is recognised even while an intent is
    // technically Active: the sustained aimless Explore IS the evidence that the active objective
    // is not actionable here, which the bare stack-status check misses.
    internal static int CountRecentUntargetedExploreEmissions(EventStream events)
    {
        var n = 0;
        foreach (var ge in events.RecentGoalEmissions()
                     .Where(e => !string.IsNullOrEmpty(e.Text))
                     .Take(RecentWanderEmissionWindow))
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Explore ", StringComparison.Ordinal)) continue;
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti < 0) continue;
            // Read the target selector's tokens the robust way the other emission
            // counters use (CountRecentTalkGoalsToName): scan from "target=" and take
            // the FIRST guid / name token. An Explore goal carries no item, so the
            // first guid/name after target= is the TARGET's — robust to a name that
            // itself contains " item=", which a bounded target=(.*?) item= regex would
            // truncate.
            var afterTarget = txt.Substring(ti);
            // A concrete (nonzero) object guid => a directed Explore; not a wander.
            var gm = System.Text.RegularExpressions.Regex.Match(afterTarget, "guid=0x([0-9A-Fa-f]+)");
            if (gm.Success && gm.Groups[1].Value.TrimStart('0').Length > 0) continue;
            var nm = System.Text.RegularExpressions.Regex.Match(afterTarget, "name=\"([^\"]*)\"");
            var name = nm.Success ? nm.Groups[1].Value.Trim() : string.Empty;
            // Untargeted = no concrete object: no name (empty / "<empty>" selector) OR
            // the generic "anywhere". A NAMED place is purposeful travel, not a wander.
            if (name.Length == 0 || name.Equals("anywhere", StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    // Gate for the SPEND-BEFORE-WANDER salience cue: meaningful unspent XP, NO recent death (the
    // recent-death case is owned by SURVIVABILITY-FIRST CHECK, so the two never double-fire), NO
    // monster in view it can currently defeat (none attackable, or only lethal-beaten kinds), and
    // the bot is WANDERING — either it has NO active objective, OR (even with an Active intent) it
    // is in a sustained UNTARGETED-Explore drift. That second path is the key fix: live, the bot
    // held an Active intent whose target was not actionable here, drifted on Explore "anywhere"
    // for many decisions while HOARDING XP, and the bare no-active-objective check suppressed this
    // cue for the whole drift. The wander-streak read (the bot's own emission history) recognises
    // that drift. SUPPRESSED in an active death-spiral (recentOwnDeathCount >= DeathSpiralMinDeaths):
    // there `## Survival caution` owns the steer (retreat + earn XP safely, NOT "spend XP to make
    // these monsters winnable"), and its window is wider than the SURVIVABILITY-FIRST recency gate
    // above, so this guard covers the spiral tail where that gate has lapsed. Pure predicate over
    // own self/stack/perception/emission facts; the cue it gates only POINTS at the SPEND XP
    // option, it never spends or picks a target.
    internal static bool ShouldSurfaceSpendBeforeWander(
        IntentStack? stack, WorldStateProjection world, int? secondsSinceLastDeath,
        int recentOwnDeathCount = 0, EventStream? events = null)
        => (StackHasNoActiveObjective(stack)
            || (events is not null
                && CountRecentUntargetedExploreEmissions(events) >= WanderStreakSpendThreshold))
           && (secondsSinceLastDeath is not int rd || rd > RecentDeathSalienceWindowSeconds)
           && recentOwnDeathCount < DeathSpiralMinDeaths
           && world.Self.AvailableExperience is long wxp
           && ShouldSurfaceUnspentXp(wxp, MinMeaningfulUnspentXp)
           && (!AnyAttackableMonsterInView(world) || OnlyBeatenMonstersInView(world));

    // Shared FRESHNESS guard for the reduce-llm-call-volume SKIP gates
    // (skip-fixated-talk and skip-empty-explore). Skipping the LLM call is safe
    // ONLY when nothing decision-worthy has changed since the last LLM look:
    //   - !hasNonPickerExternal: no fresh salient external event (dialog / readable
    //     text / inventory add-or-remove / zone change / server message / rejection);
    //   - !pickerArrived && !pickerStartWake: no NEW autonomous picker discovery
    //     (corpse/door/portal/ground-item) the LLM should be free to act on;
    //   - !HasNewStrategicIntentCompletionSince: no intent-stack pop / top-objective
    //     completion since the last look;
    //   - !StackHasNoActiveObjective: the top objective is still ACTIVE (catches a
    //     root deadline-Blocked IN PLACE, which emits no Goal lifecycle event).
    // CENTRALIZED so the two gates can never DIVERGE on these guards — both
    // gpt-5.4-blocking review findings this session were exactly such a divergence
    // (a gate missing the picker guard; both missing the stack-status guard). Each
    // gate ANDs its OWN remaining conditions on top of this. Reads the bot's OWN
    // event / picker / intent-stack state; no game knowledge.
    private bool SkipGateFreshnessAllows(
        bool hasNonPickerExternal, bool pickerArrived, bool pickerStartWake, EventStream events) =>
        !hasNonPickerExternal
        && !pickerArrived
        && !pickerStartWake
        && !HasNewStrategicIntentCompletionSince(events, _lastEventConsideredSequence)
        && !StackHasNoActiveObjective(_stack);

    // Events that must ROUTE TO THE LLM rather than let the autonomous combat
    // chain (ChooseCombatChainTarget) mint another Attack toward an active
    // kill-count commitment. A NARROW allowlist of genuinely decision-worthy
    // changes: an item LEAVING inventory (give/use/sell — a deliberate act),
    // NPC dialog, readable popup/book text, and ANY action rejection (which
    // INCLUDES the Motor's DisengageLowHealth refusal, so a fresh disengage
    // stops the chain).
    // It deliberately EXCLUDES the events a kill emits every time as ordinary
    // combat progress — ServerMessage ("you have slain ..."), CombatFeedback,
    // InboundDamageTaken — and SelfProgressChanged, so the chain is not made
    // inert by its own kills. It ALSO excludes InventoryItemAdded: picking up a
    // kill's own drops is an EXPECTED byproduct of the committed grind, not a
    // decision-worthy external change, and forcing a per-kill LLM round-trip on
    // every loot starved the chain (observed live: gate:chain-interrupting-event
    // after each corpse-loot, budget 0/N). An objective that DEPENDS on a looted
    // item is expressed as an inventory-bound completion predicate, NOT a
    // kill-count one, so it does NOT activate this chain (IsActiveKillCommitment
    // accepts only kill-count predicates) and is evaluated every tick — this
    // chain fires ONLY for pure kill-count grinds, where the loot is an
    // incidental trophy.
    // It ALSO excludes LandblockChanged: crossing a cell boundary mid-grind is
    // not itself decision-worthy here, because the visible-target filter in
    // ChooseCombatChainTarget already yields to the LLM (no-matching-monster)
    // when the committed kind is NOT present in the new area, while if the SAME
    // committed kind IS visible there the grind simply continued across the
    // boundary. Treating every crossing as an interrupt burned an LLM call every
    // few kills with the committed kind still in view (observed live:
    // gate:chain-interrupting-event:LandblockChanged). A genuine area change
    // still re-engages the LLM at the next MaxCombatChainAttacks bound, and
    // danger is owned by the disengage reflex (a non-excluded ActionRejected)
    // independently of zone changes.
    // The LLM re-engages at the next bound (every MaxCombatChainAttacks mints,
    // and on any genuine event above) and re-reads inventory then (as far as
    // prompt fitting surfaces it). Combat SAFETY is owned by the Motor's dispatch
    // self-preservation gate and the losing-fight disengage reflexes (not by this
    // routing gate); the kill-count completion predicate + the
    // MaxCombatChainAttacks cap also bound the chain. Pure wire-event-kind
    // classification; no game-content knowledge.
    internal static bool IsChainInterruptingKind(EventKind kind) =>
        kind is EventKind.InventoryItemRemoved
             or EventKind.NpcDialog
             or EventKind.PopupString
             or EventKind.BookText
             or EventKind.ActionRejected;

    // Event-level chain-interrupt test: the kind is chain-interrupting AND,
    // for an ActionRejected, it is NOT a benign motor-side outcome the
    // autonomous decomposition can ignore. Two such outcomes are excluded:
    //   * a TRANSPORT failure (cp025) — the Motor's own "could not reach /
    //     resolve the target" result (reserved codes 0xFFFC-0xFFFE via
    //     IsTransportFailureRejection); the target stopped resolving, so the
    //     decomposition advances to its next matching target; and
    //   * an auto-repeat swing-loop-dropped signal (the SURFACED combat cancel,
    //     Motor-reserved code via IsAttackLoopCancelRejection) — the Motor
    //     immediately re-sends the attack to restart the loop, so re-deliberating
    //     names nothing the LLM can act on (live cp025-validate.log: this signal,
    //     surfaced as an ActionRejected, closed the gate and burned a per-cancel
    //     LLM round-trip every few swings of a kill-count grind). The reserved
    //     code keeps this distinct from a same-named inventory ActionCancelled
    //     (raw 0x0036), which is decision-worthy and still interrupts.
    // Neither is a decision-worthy external change, so neither forces an LLM
    // round-trip. The higher-severity rejections this gate exists to catch —
    // the Motor's self-preservation disengage (a NON-transport reserved code)
    // and semantic action refusals (their own codes) — match neither benign
    // predicate, so they still interrupt. The rejection event is still
    // surfaced to the LLM regardless; this only governs whether it preempts
    // the autonomous chain. Pure wire-code classification; no game knowledge.
    internal static bool IsChainInterruptingEvent(StreamEvent e) =>
        IsChainInterruptingKind(e.Kind)
        && !(e.Kind == EventKind.ActionRejected
             && (IsTransportFailureRejection(e) || IsAttackLoopCancelRejection(e)));

    // True iff this dialog-class event repeats verbatim an EARLIER dialog event
    // of the SAME kind AND SAME source already in the retained window — i.e. the
    // bot has already seen this exact line from this exact speaker. Server
    // tutorial/training flavor re-broadcasts the same NpcDialog / PopupString /
    // BookText every few seconds (live: an Academy training construct re-emits the
    // same combat-slider tip and the same "double-click the body to loot" popup
    // repeatedly during a kill grind). The FIRST occurrence carries whatever
    // novelty it has and is NOT a repeat; only verbatim re-emissions from the same
    // source are. Source identity (SameDialogSource) keeps a different NPC's first
    // identical line from being swallowed. Restricted to dialog/popup/booktext: an
    // ActionRejected is a fresh action OUTCOME even when its text repeats, so it is
    // never deduped here. Exact-text + source bookkeeping over the bot's OWN
    // perception stream; no game-content knowledge (no hardcoded text/name/quest).
    internal static bool IsRepeatedDialogText(EventStream events, StreamEvent e)
    {
        if (e.Kind is not (EventKind.NpcDialog or EventKind.PopupString or EventKind.BookText))
            return false;
        if (string.IsNullOrWhiteSpace(e.Text))
            return false;
        foreach (var p in events.Recent())
        {
            if (p.Sequence >= e.Sequence) continue; // only strictly-earlier events
            if (p.Kind != e.Kind) continue;
            if (!string.Equals(p.Text, e.Text, StringComparison.Ordinal)) continue;
            if (SameDialogSource(p, e)) return true;
        }
        return false;
    }

    // Whether two same-kind dialog events share a source/speaker. A PopupString
    // carries no source, so two identical popups always match. NpcDialog / BookText
    // carry the speaker / book identity (ItemGuid, with Name as a fallback when a
    // guid is absent): a verbatim line is a REPEAT only from the SAME source, so a
    // different NPC's first identical line still interrupts. Own-perception
    // identity comparison; no game knowledge.
    private static bool SameDialogSource(StreamEvent earlier, StreamEvent e)
    {
        if (e.Kind == EventKind.PopupString) return true;
        if (earlier.ItemGuid is uint pg && pg != 0 && e.ItemGuid is uint eg && eg != 0)
            return pg == eg;
        return string.Equals(earlier.Name, e.Name, StringComparison.OrdinalIgnoreCase);
    }

    // The FIRST chain-interrupting event newer than `floorSeq` (newest-first
    // scan), or null when none — and its EventKind names the interrupter so the
    // no-mint diagnostic (the cp2925 pattern) stays characterizable. A dialog
    // event that merely REPEATS an earlier line the bot already saw
    // (IsRepeatedDialogText) is skipped: re-broadcast tutorial flavor must not
    // preempt an autonomous kill-chain and burn an LLM round-trip, while the first
    // occurrence still interrupts so genuinely new dialog reaches the LLM. Pure
    // event-kind + own-text classification; no game knowledge.
    internal static EventKind? FirstChainInterruptingKindSince(EventStream events, long floorSeq)
    {
        foreach (var e in events.Recent())
        {
            if (e.Sequence < floorSeq) break;
            if (IsChainInterruptingEvent(e) && !IsRepeatedDialogText(events, e))
                return e.Kind;
        }
        return null;
    }

    // Untargeted-Explore discriminator (call-volume reduction): true iff the
    // goal is an Explore whose target is the schema "anywhere" sentinel — it
    // carries NO resolved object selector (no guid/wcid/name_contains/
    // item_type/short_desc, and any Name is the literal schema token
    // "anywhere"). Such a goal is a Motor-owned traversal with nothing to
    // interact with, so the sticky-objective gate exempts it from the
    // unreachable-target retry budget. A goal naming ANY concrete target is
    // NOT untargeted. Schema-level goal-shape only; no game knowledge.
    internal static bool IsUntargetedExploreGoal(Goal? goal)
    {
        if (goal is not { Kind: GoalKind.Explore }) return false;
        var t = goal.Target;
        if (t.IsEmpty) return true;
        return t.Guid is null
            && t.Wcid is null
            && t.ItemTypeMask is null
            && string.IsNullOrWhiteSpace(t.NameContains)
            && string.IsNullOrWhiteSpace(t.ShortDescContains)
            && string.Equals(t.Name?.Trim(), "anywhere", StringComparison.OrdinalIgnoreCase);
    }

    // "Reached" distance for an Explore target: within this many XY units the
    // Motor's walk-tick has already declared arrival (MotorStopRadius is 1u
    // default / 4u portal), so the target is adjacent and interactable. The
    // margin above those radii absorbs the small drift between the projection
    // sample and the post-arrival decision; it stays far below the perception
    // radius so the signal means "reached", not merely "visible across the
    // area". Pure motor-geometry bookkeeping; no game knowledge.
    internal const float ReachedExploreTargetDistanceUnits = 5.0f;

    // Detect that the bot has been Exploring toward a NAMED target that is NOW
    // within reach in `Visible nearby` — i.e. it ARRIVED, but because `Explore`
    // never interacts it is standing beside the target having done nothing. A
    // weak model then re-`Explore`s the reached target (or walks to another)
    // forever. Returns the matched visible target's name + distance so the
    // caller can re-state, decision-proximate, the (hard-cut-buried) rule that
    // an arrived Explore must switch to an interaction verb. Pure structural
    // parse of the bot's OWN recent Explore emissions matched by name to a
    // visible object — no game knowledge, no priority, no source-side decision
    // (the LLM still chooses whether/how to interact or to move on).
    internal static bool TryDetectReachedExploreTarget(
        WorldStateProjection world, EventStream events,
        out string? targetName, out float distance)
    {
        targetName = null;
        distance = 0f;
        if (world?.Visible is null || events is null) return false;

        // The bot's CURRENT pursuit is its most-recent goal emission
        // (RecentGoalEmissions is newest-first). Fire only when that newest goal
        // is an Explore toward a NAMED target: the moment the bot switches to an
        // interaction verb (Talk/Give/Use/Attack) its newest goal is no longer an
        // Explore, so the cue stops (it must NOT keep firing while the bot is
        // already interacting); and an OLDER Explore toward some other target is
        // stale history, not the current pursuit. Supports both `name="X"`
        // (exact match) and `name_contains="X"` (substring match) selectors;
        // skips the untargeted "anywhere" sentinel.
        var newest = events.RecentGoalEmissions()
            .FirstOrDefault(e => !string.IsNullOrEmpty(e.Text));
        if (newest is null) return false;
        var txt = newest.Text!;
        if (!txt.StartsWith("Explore ", StringComparison.Ordinal)) return false;
        if (!TryExtractGoalTargetSelector(txt, out var sel)) return false;
        var nm = System.Text.RegularExpressions.Regex.Match(sel, "name(_contains)?=\"([^\"]+)\"");
        if (!nm.Success) return false;
        var token = nm.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (string.Equals(token.Trim(), "anywhere", StringComparison.OrdinalIgnoreCase)) return false;
        var isContains = nm.Groups[1].Success;

        // The closest visible object whose name matches the pursued selector and
        // is within reach. name_contains= matches by substring; name= resolves under
        // the SHARED selector semantics (exact / quoted-role-strip / unique
        // subsequence — ResolveVisibleObjectForName) so this reached detector and the
        // unresolved/visible-object Explore cues all agree on which object a name
        // binds (a fuzzy/role bind within reach is owned HERE, not left uncovered).
        VisibleObjectProjection? best = null;
        if (isContains)
        {
            foreach (var v in world.Visible)
            {
                if (v.Distance is not float d || d > ReachedExploreTargetDistanceUnits) continue;
                if (v.Name is not null
                    && v.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                    && (best is null || d < (best.Distance ?? float.MaxValue))) best = v;
            }
        }
        else
        {
            var resolved = ResolveVisibleObjectForName(world.Visible, token, excludeCorpses: false);
            if (resolved is { Distance: float rd } && rd <= ReachedExploreTargetDistanceUnits)
                best = resolved;
        }
        if (best is null) return false;
        targetName = best.Name;
        distance = best.Distance ?? 0f;
        return true;
    }

    // Goal-based reached check: true iff `goal` is a TARGETED Explore whose OWN
    // target (name= exact / name_contains= substring; not the untargeted
    // "anywhere" sentinel) matches a `Visible nearby` object within reach.
    // Unlike TryDetectReachedExploreTarget (which keys on the bot's recent
    // emission history to render the prompt capsule), this keys on the SPECIFIC
    // goal being evaluated — the Motor uses it to recognise that walking this
    // Explore makes no progress because the bot is already at its target, so a
    // NEW Explore toward a DIFFERENT (not-yet-reached) target is NOT matched.
    // Pure motor-geometry over the bot's own goal + visible objects; no game
    // knowledge, no source-side interaction decision.
    internal static bool IsExploreToReachedTarget(Goal? goal, WorldStateProjection world)
    {
        if (goal is not { Kind: GoalKind.Explore } || IsUntargetedExploreGoal(goal)) return false;
        if (world?.Visible is null) return false;
        var name = goal.Target?.Name;
        var nameContains = goal.Target?.NameContains;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(nameContains)) return false;
        // name= resolves under the SHARED selector semantics (exact / quoted-role-strip
        // / unique subsequence) so a fuzzy/role bind that is already within reach is
        // recognised as a no-op Explore (consistent with the reached cue + the
        // visible-object Explore cue); name_contains= stays a substring match.
        if (!string.IsNullOrWhiteSpace(name)
            && ResolveVisibleObjectForName(world.Visible, name, excludeCorpses: false)
                is { Distance: float rd } && rd <= ReachedExploreTargetDistanceUnits)
            return true;
        if (!string.IsNullOrWhiteSpace(nameContains))
            foreach (var v in world.Visible)
            {
                if (v.Distance is not float d || d > ReachedExploreTargetDistanceUnits) continue;
                if (v.Name is not null
                    && v.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        return false;
    }

    // cp067 repeated-Explore-to-unresolved-name window + threshold.
    private static readonly TimeSpan RepeatedUnresolvedExploreWindow = TimeSpan.FromMinutes(3);
    private const int RepeatedUnresolvedExploreThreshold = 3;
    // Sibling: repeated-Attack-toward-a-departed-target window + threshold. `## Visible`
    // is distance-bounded (visible radius), so a target absent from it is exactly one the
    // Attack goal would resolve to MISS (it left view/range, died, or the bot moved past it).
    private static readonly TimeSpan RepeatedUnresolvedAttackWindow = TimeSpan.FromMinutes(3);
    private const int RepeatedUnresolvedAttackThreshold = 3;
    // Sibling: repeated-Use-toward-a-departed-target window + threshold. A `Use` target
    // (vendor/NPC/door/container/corpse/item) absent from the distance-bounded `## Visible`
    // is one the Use goal would resolve to MISS (the bot walked out of range of it).
    private static readonly TimeSpan RepeatedUnresolvedUseWindow = TimeSpan.FromMinutes(3);
    private const int RepeatedUnresolvedUseThreshold = 3;
    // Sibling: repeated-Wield-of-an-UNOWNED-item window + threshold. A Wield only equips an
    // item in the bot's OWN inventory; re-Wielding a weapon it does not own (e.g. a vendor's
    // or a ground item) fails every time, leaving the bot unarmed.
    private static readonly TimeSpan RepeatedUnownedWieldWindow = TimeSpan.FromMinutes(3);
    private const int RepeatedUnownedWieldThreshold = 3;

    // cp067: the NAME the bot has Explored toward >= threshold times within the recent
    // window that resolves to NO visible object (a reached area / unresolved name), or
    // null when no such loop is forming. Explore is navigate-only; an Explore toward a
    // name with NO matching visible object walks to wherever the name last resolved (a
    // remembered/area location) and ARRIVES, clearing the goal and waking a fresh
    // no-current-goal LLM call that re-emits the SAME Explore — a call storm in place
    // (live: an area/location name re-Explored many times back-to-back, the Motor
    // reporting arrival although the name is not a visible object). cp022
    // IsExploreToReachedTarget only covers a target that IS a visible reached object;
    // this surfaces the no-visible-match (area/location-name) case to the LLM as an
    // informational CUE (the `## Explore loop` capsule) — NOT a hard drop, so a
    // legitimate TRAVEL-BACK toward a not-yet-visible distant target (the cp035/cp037/
    // cp051 return-to-source paths) is never overridden; the LLM still decides. Keyed
    // purely on the bot's OWN goal-emission history + perception. Untargeted ("anywhere")
    // and `name_contains`/`wcid`/coord Explores (no `name` token) are ignored. No game
    // knowledge, no priority, no source-side target choice.
    internal static string? RepeatedUnresolvedExploreName(
        WorldStateProjection world, EventStream events, DateTimeOffset since)
        => MostRepeatedUnresolvedTargetName(world, events, since, "Explore", RepeatedUnresolvedExploreThreshold, excludeCorpses: false);

    // COMPLEMENT of RepeatedUnresolvedExploreName: the NAME the bot has Explored
    // toward >= threshold times within the window that DOES resolve to a visible
    // object which is NOT yet within reach (so the cp022 `## Reached Explore
    // target` hard-drop, which only fires within ReachedExploreTargetDistanceUnits,
    // does not own it). Explore walks toward a target and stops at its arrival
    // radius WITHOUT interacting, so the goal clears and a fresh no-current-goal
    // call re-emits the SAME Explore; the unresolved-Explore cue cannot fire (the
    // name DOES resolve to a visible object) and the reached cue cannot fire (it is
    // beyond reach), so nothing tells the LLM to use the interaction verb (which
    // navigates INTO range AND acts). Resolve the SINGLE object the name binds
    // (ResolveVisibleObjectForName — the same semantics VisibleResolvesName uses)
    // and fire only when THAT object is beyond the reached radius, so this detector
    // and the reached cue never disagree on which object the name resolves to. Keyed
    // purely on the bot's OWN emission history + perception; no game knowledge, no
    // priority, no source-side target choice.
    internal static string? RepeatedResolvedFarVisibleExploreName(
        WorldStateProjection world, EventStream events, DateTimeOffset since)
    {
        if (world?.Visible is null) return null;
        foreach (var kv in CountRecentEmittedTargetNames(events, since, "Explore", excludeItemGoals: false)
                     .OrderByDescending(k => k.Value))
        {
            if (kv.Value < RepeatedUnresolvedExploreThreshold) break;
            // Resolve the SAME object VisibleResolvesName binds, then read ITS distance:
            // fire only when that bound object is BEYOND the reached radius (a within-reach
            // bind is owned by the cp022 `## Reached Explore target` cue + its hard-drop).
            // Using the single resolved object keeps this detector and the reached cue in
            // agreement on which object the name resolves to (a fuzzy/quoted-role bind that
            // a different matcher would miss cannot slip through as "not reached").
            if (ResolveVisibleObjectForName(world.Visible, kv.Key, excludeCorpses: false)
                    is { Distance: float d } && d > ReachedExploreTargetDistanceUnits)
                return kv.Key;
        }
        return null;
    }

    // Sibling for Attack: the NAME the bot has tried to `Attack` >= threshold times within
    // the window that matches NO currently-visible object — the monster has left view/range,
    // died, or the bot travelled past it, so the Attack would resolve to MISS and a fresh
    // no-current-goal call re-emits the SAME Attack (live: a named mob re-Attacked many times
    // back-to-back while no longer in view). Surfaced to the LLM as the `## Attack loop`
    // informational cue — NOT a hard drop, so a legitimate close-in toward a target just out
    // of the visible radius is never overridden. Same mechanics + audit posture as the
    // Explore loop: bot's OWN emission history + perception only.
    internal static string? RepeatedUnresolvedAttackTarget(
        WorldStateProjection world, EventStream events, DateTimeOffset since)
        => MostRepeatedUnresolvedTargetName(world, events, since, "Attack", RepeatedUnresolvedAttackThreshold, excludeCorpses: true);

    // Sibling for Use: the NAME the bot has tried to `Use` >= threshold times within the
    // window that NO currently-visible object would bind — the vendor/NPC/door/container/
    // corpse/item has left view/range, so the Use would resolve to MISS and a fresh
    // no-current-goal call re-emits the SAME Use (live: a vendor/NPC re-Used many times
    // back-to-back while no longer in view). Surfaced as the `## Use loop` informational cue
    // — NOT a hard drop, so a legitimate close-in toward a target just out of the visible
    // radius is never overridden. Corpses are NOT excluded (a corpse IS a valid Use/loot
    // target, so a visible corpse named X binds the Use). The MISFILE shape (empty target +
    // a name in the `item` field — a model naming the object to Use in the wrong field) is
    // ALSO counted via itemNameWhenTargetEmpty, so a departed-NPC item-field Use loop surfaces
    // too; a genuine two-object Use (both target AND item populated) stays ambiguous and is
    // skipped. Same audit posture as the others.
    internal static string? RepeatedUnresolvedUseTarget(
        WorldStateProjection world, EventStream events, DateTimeOffset since)
        => MostRepeatedUnresolvedTargetName(world, events, since, "Use", RepeatedUnresolvedUseThreshold, excludeCorpses: false, itemNameWhenTargetEmpty: true);

    private static readonly TimeSpan EngagementChurnWindow = TimeSpan.FromMinutes(3);
    private const int EngagementChurnDistinctThreshold = 3;

    // The number of DISTINCT interaction-target NAMES (Talk or Use) the bot recently emitted
    // toward AND that actually FAILED (a GoalFailed in the window) AND that NO currently-visible
    // object would bind — i.e. it is cycling through several DIFFERENT out-of-view/unreachable
    // targets, each failing to resolve. The single-target loop cues (## Use loop, the
    // Talk-fixation guards) each key on ONE repeated name and so never fire on a churn SPREAD
    // across several targets (each below its own threshold), the multi-target canvass case.
    // Requiring a corroborating FAILURE (not just "not visible now") is what excludes a
    // PRODUCTIVE visit to several targets in sequence — each reached + interacted successfully,
    // then walked past (no failure) is never counted. Names that bind a visible object (the bot
    // could still walk up), resolve to an OWNED inventory item (a legit self-Use, mirroring the
    // ## Use loop), or are the bot's OWN corpse (dedicated recovery cue) are all excluded. Pure:
    // own emission + failure history + perception; no game knowledge, no priority, no
    // source-side target choice.
    internal static int CountDistinctUnresolvedInteractionTargets(
        WorldStateProjection world, EventStream events, DateTimeOffset since)
    {
        if (world?.Visible is null || events is null) return 0;
        // Names that actually FAILED recently (a terminal GoalFailed carrying that target name,
        // role-normalized to match the emitted-name keys).
        var failedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in events.RecentGoalFailures())
        {
            if (f.Utc < since || string.IsNullOrWhiteSpace(f.Name)) continue;
            failedNames.Add(NormalizeEmittedTargetName(f.Name));
        }
        if (failedNames.Count == 0) return 0;
        var selfName = world.Self?.Name;
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (verb, itemFieldMode) in new[] { ("Talk", false), ("Use", true) })
            foreach (var kv in CountRecentEmittedTargetNames(
                         events, since, verb, excludeItemGoals: false, itemNameWhenTargetEmpty: itemFieldMode))
            {
                if (!failedNames.Contains(kv.Key)) continue; // only names that ACTUALLY failed
                if (IsOwnCorpseName(kv.Key, selfName)) continue;
                if (VisibleResolvesName(world.Visible, kv.Key, excludeCorpses: false)) continue;
                if (world.Inventory is not null && InventoryResolvesName(world.Inventory, kv.Key)) continue;
                distinct.Add(kv.Key);
            }
        return distinct.Count;
    }

    // Shared detector for a repeated goal-VERB emission toward a target NAME that NO
    // currently-visible object would bind. Counts the bot's own recent goal emissions
    // of `verb` within the window (parsing the echoed `target=...name="..."`) and
    // returns the most-repeated such name at/above `threshold` for which `## Visible`
    // holds no resolver-bindable object (see VisibleResolvesName — exact / quoted-role /
    // unique fuzzy), else null. The untargeted "anywhere" token is ignored. When
    // <paramref name="excludeItemGoals"/>, an emission carrying a populated `item=` field
    // is skipped: a two-object item-Use (an inventory item ON a target) fails with the
    // same "no live object" outcome whether the ITEM or the world TARGET did not resolve,
    // so it is ambiguous (mirrors IsUnreachableTargetRepeat). Pure: own emission history +
    // perception; no game knowledge, no priority, no source-side target choice.
    private static string? MostRepeatedUnresolvedTargetName(
        WorldStateProjection world, EventStream events, DateTimeOffset since, string verb, int threshold,
        bool excludeCorpses, bool excludeItemGoals = false, bool itemNameWhenTargetEmpty = false)
    {
        foreach (var kv in CountRecentEmittedTargetNames(events, since, verb, excludeItemGoals, itemNameWhenTargetEmpty: itemNameWhenTargetEmpty)
                     .OrderByDescending(k => k.Value))
        {
            if (kv.Value < threshold) break;
            if (world?.Visible is not null && VisibleResolvesName(world.Visible, kv.Key, excludeCorpses))
                continue; // a visible object the resolver could still bind — normal nav / picker handles it
            if (itemNameWhenTargetEmpty && world?.Inventory is not null
                && InventoryResolvesName(world.Inventory, kv.Key))
                continue; // a name the bot OWNS — the empty-target+item shape is a legit self-Use
                          // of an inventory item (mirrors TryResolveUseWorldObjectInItemField), not a
                          // departed-world-target loop, so do not surface the "not in view" cue.
            return kv.Key;
        }
        return null;
    }

    // Sibling for Wield: the NAME the bot has tried to `Wield` >= threshold times within
    // the window that is NOT an equippable weapon in its OWN `## Inventory` — it does not
    // own that item (it may be in a vendor's shop or on the ground), so the Wield fails
    // (the dispatch can only equip an in-bag item) and a fresh no-current-goal call
    // re-emits the SAME Wield (live: a vendor's weapon re-Wielded many times, the bot left
    // unarmed). The weapon NAME is taken from the ITEM field when present (the canonical
    // Wield shape is target=name="self" item=name="<weapon>"), else from the target field
    // (some models emit the weapon as the target). Surfaced as the `## Wield loop` cue.
    // Own emission history + own inventory only; no game knowledge.
    internal static string? RepeatedUnownedWieldName(
        WorldStateProjection world, EventStream events, DateTimeOffset since)
    {
        foreach (var kv in CountRecentEmittedTargetNames(events, since, "Wield", excludeItemGoals: false, preferItemName: true)
                     .OrderByDescending(k => k.Value))
        {
            if (kv.Value < RepeatedUnownedWieldThreshold) break;
            if (world?.Inventory is not null && InventoryResolvesWieldable(world.Inventory, kv.Key))
                continue; // an equippable item the bot OWNS — the Wield dispatch handles it
            return kv.Key;
        }
        return null;
    }

    // Count the bot's own recent goal emissions of `verb` (within the window) by the
    // target NAME parsed from the echoed `target=...name="..."`. With
    // <paramref name="preferItemName"/>, the NAME in the `item=` field is used when present,
    // falling back to the target name — for Wield, whose weapon is carried in the item field
    // (target is the self-placeholder); the "self" / "&lt;your-name&gt;" placeholders are then
    // ignored. With <paramref name="itemNameWhenTargetEmpty"/> (Use), the target name is used
    // when present, the ITEM name when the target is empty (the misfile shape), and a
    // BOTH-populated two-object emission is skipped as ambiguous. The untargeted "anywhere"
    // token is always ignored. When <paramref name="excludeItemGoals"/>, an emission carrying
    // a populated `item=` field is skipped (two-object ambiguity; mirrors
    // IsUnreachableTargetRepeat). The default (all flags false) path is the original
    // single-target parse, unchanged. Pure: own emission history only; no game knowledge.
    private static Dictionary<string, int> CountRecentEmittedTargetNames(
        EventStream events, DateTimeOffset since, string verb, bool excludeItemGoals, bool preferItemName = false,
        bool itemNameWhenTargetEmpty = false)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (events is null) return counts;
        foreach (var ge in events.RecentGoalEmissions().Where(e => !string.IsNullOrEmpty(e.Text)))
        {
            if (ge.Utc < since) continue;
            var txt = ge.Text!;
            if (!txt.StartsWith(verb, StringComparison.Ordinal)) continue;
            if (excludeItemGoals && EmissionHasPopulatedItem(txt)) continue;
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti < 0) continue;

            string? name;
            if (itemNameWhenTargetEmpty)
            {
                // Use: the target name normally, but for the MISFILE shape (empty target +
                // populated item) the ITEM name (the de-facto target — a model names the
                // object it wants to Use in the item field with no target). A genuine
                // two-object Use (BOTH a target AND an item selector populated) stays ambiguous
                // (either the world target or the held item could have failed to resolve) and
                // is skipped — the same posture excludeItemGoals took for all item-Uses.
                if (EmissionHasPopulatedItem(txt) && EmissionHasPopulatedTarget(txt)) continue;
                var (tName, iName) = ParseTargetAndItemNames(txt, ti);
                name = tName ?? iName;
                if (name is null) continue;
            }
            else if (!preferItemName)
            {
                // Single-target verbs: the first name= from `target=` onward is the target's
                // (item is empty), the original behavior.
                var nm = System.Text.RegularExpressions.Regex.Match(
                    txt.Substring(ti), "name=\"([^\"]+)\"");
                if (!nm.Success) continue;
                name = nm.Groups[1].Value;
            }
            else
            {
                // Wield: parse the target and item segments separately and PREFER the item
                // name (the weapon), since the canonical shape is target="self" item=weapon.
                var (tName, iName) = ParseTargetAndItemNames(txt, ti);
                name = iName ?? tName;
                if (name is null) continue;
            }

            // Normalize a rendered role-title suffix off the emitted name (the same
            // SelectorResolver.StripTrailingQuotedRoleTitle the Motor resolves with), so a
            // model that alternates `Foo` and `Foo "role"` for the SAME target accumulates
            // under one bare key instead of splitting the count. The placeholder tokens
            // below ("anywhere"/"self"/"<your-name>") carry no role suffix and normalize to
            // themselves, so their guards are unaffected.
            name = NormalizeEmittedTargetName(name);
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, "anywhere", StringComparison.OrdinalIgnoreCase)
                || (preferItemName
                    && (string.Equals(name, "self", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "<your-name>", StringComparison.OrdinalIgnoreCase)))) continue;
            counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
        }
        return counts;
    }

    // The first `name="..."` value within a single emission segment, or null.
    private static string? NameInSegment(string segment)
    {
        var m = System.Text.RegularExpressions.Regex.Match(segment, "name=\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Parse the target-segment name and the item-segment name from a goal-emission text,
    // starting at the `target=` index `ti`. The target segment runs from `target=` to the
    // next ` item=` (or ` source=` / end); the item segment from ` item=` to ` source=` /
    // end. Either name may be null (the segment is absent or carries no name="..."). Shared
    // by the Wield loop (prefers the item name) and the Use loop (uses the item name only
    // when the target is empty). Pure string parsing.
    private static (string? TargetName, string? ItemName) ParseTargetAndItemNames(string txt, int ti)
    {
        var ii = txt.IndexOf(" item=", ti, StringComparison.Ordinal);
        var srcIdx = txt.IndexOf(" source=", ti, StringComparison.Ordinal);
        int tEnd = ii >= 0 ? ii : (srcIdx >= 0 ? srcIdx : txt.Length);
        var tName = NameInSegment(txt.Substring(ti, tEnd - ti));
        string? iName = null;
        if (ii >= 0)
        {
            int iEnd = srcIdx > ii ? srcIdx : txt.Length;
            iName = NameInSegment(txt.Substring(ii, iEnd - ii));
        }
        return (tName, iName);
    }

    // True when the bot OWNS an item that could be wielded under that name: equippable
    // (ValidLocations != 0) OR already wielded (WieldedAt != 0), whose name binds `name`
    // under the resolver's name semantics (exact / quoted-role-strip / unique whole-word
    // subsequence). Used by the Wield-loop detector so a weapon the bot already owns (and
    // could wield, or is already wielding) is NOT counted as an unowned-Wield loop. Pure
    // string/flag comparison on owned inventory; no game knowledge.
    private static bool InventoryResolvesWieldable(IReadOnlyList<InventoryItemProjection> inventory, string name)
    {
        var wieldable = inventory
            .Where(i => (i.ValidLocations is uint vl && vl != 0) || (i.WieldedAt is uint wa && wa != 0))
            .ToList();
        var bare = HeadlessAcClient.Tactics.SelectorResolver.StripTrailingQuotedRoleTitle(name) ?? name;
        if (wieldable.Any(i => i.Name is not null
                && (string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(i.Name, bare, StringComparison.OrdinalIgnoreCase))))
            return true;
        return wieldable.Count(i =>
            HeadlessAcClient.Tactics.SelectorResolver.MatchesNameWordSubsequence(i.Name, name)) == 1;
    }

    // True when a goal-emission text carries a populated `item=` field — i.e. a
    // two-object goal (an inventory item ON a target). Emission shape is
    // `<Kind> target=<sel> item=<sel> source=<src>`; an EMPTY item selector renders as
    // the sentinel `<empty>` (Selector.ToString) — treated here as NOT populated, the
    // same as a blank field. Pure string parse of the bot's own emitted text.
    private static bool EmissionHasPopulatedItem(string txt)
    {
        var i = txt.IndexOf(" item=", StringComparison.Ordinal);
        if (i < 0) return false;
        int s = i + " item=".Length;
        int src = txt.IndexOf(" source=", s, StringComparison.Ordinal);
        int end = src >= 0 ? src : txt.Length;
        var itemVal = (s < end ? txt.Substring(s, end - s) : string.Empty).Trim();
        return itemVal.Length > 0 && !string.Equals(itemVal, "<empty>", StringComparison.Ordinal);
    }

    // True when a goal-emission text carries a populated `target=` field (NOT the `<empty>`
    // sentinel). The target segment runs from `target=` to the next ` item=` (or ` source=` /
    // end). Mirrors EmissionHasPopulatedItem for the target selector, so a NAMED or GUID
    // target both count as populated (used to tell a single-object/misfile Use apart from a
    // genuine two-object Use). Pure string parse of the bot's own emitted text.
    private static bool EmissionHasPopulatedTarget(string txt)
    {
        var t = txt.IndexOf("target=", StringComparison.Ordinal);
        if (t < 0) return false;
        int s = t + "target=".Length;
        int itemIdx = txt.IndexOf(" item=", s, StringComparison.Ordinal);
        int srcIdx = txt.IndexOf(" source=", s, StringComparison.Ordinal);
        int end = itemIdx >= 0 ? itemIdx : (srcIdx >= 0 ? srcIdx : txt.Length);
        var tgtVal = (s < end ? txt.Substring(s, end - s) : string.Empty).Trim();
        return tgtVal.Length > 0 && !string.Equals(tgtVal, "<empty>", StringComparison.Ordinal);
    }

    // True when `name` is the bot's OWN corpse name ("Corpse of <selfName>", ordinal
    // case-insensitive). The bot's own corpse has a dedicated `## Corpse` retrieval cue,
    // so the generic loop cues exempt it to avoid contradictory guidance. Pure
    // self-identity string match; no game knowledge.
    internal static bool IsOwnCorpseName(string? name, string? selfName)
        => !string.IsNullOrWhiteSpace(name)
           && !string.IsNullOrWhiteSpace(selfName)
           && string.Equals(name, "Corpse of " + selfName, StringComparison.OrdinalIgnoreCase);

    // True when a visible object would bind the selector NAME under the SAME name
    // semantics SelectorResolver uses: exact (case-insensitive), exact after stripping
    // a trailing quoted-role suffix from the emitted name, OR a UNIQUE whole-word
    // subsequence fuzzy match. When <paramref name="excludeCorpses"/>, corpse entries are
    // ignored (an Attack resolver excludes corpses, so a corpse named X does NOT make an
    // Attack loop resolvable). Used by the unresolved-loop detectors so a name the
    // resolver could still resolve is NOT counted as a dead loop. Pure string comparison
    // on observed names; no game knowledge.
    private static bool VisibleResolvesName(
        IReadOnlyList<VisibleObjectProjection> visible, string name, bool excludeCorpses)
        => ResolveVisibleObjectForName(visible, name, excludeCorpses) is not null;

    // The SINGLE visible object VisibleResolvesName binds the NAME to (the NEAREST
    // exact/quoted-role match, else the UNIQUE whole-word subsequence match), or null
    // when none binds. Exposes the bound object so callers can read its distance under
    // the EXACT resolver semantics (the loop-vs-reached detectors must agree on which
    // object the name resolves to, not approximate it with a different matcher). Pure
    // name comparison on observed objects; no game knowledge.
    private static VisibleObjectProjection? ResolveVisibleObjectForName(
        IReadOnlyList<VisibleObjectProjection> visible, string name, bool excludeCorpses)
    {
        var pool = excludeCorpses ? visible.Where(v => !v.IsCorpse) : visible;
        var bare = HeadlessAcClient.Tactics.SelectorResolver.StripTrailingQuotedRoleTitle(name) ?? name;
        var exact = pool
            .Where(v => v.Name is not null
                && (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.Name, bare, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (exact is not null) return exact;
        var subseq = pool
            .Where(v => HeadlessAcClient.Tactics.SelectorResolver.MatchesNameWordSubsequence(v.Name, name))
            .ToList();
        return subseq.Count == 1 ? subseq[0] : null;
    }
    // WorldObjectInteracted echo (a real Use/Pickup opcode dispatch) since the
    // last LLM look whose identity is selector-compatible with the spatial
    // goal's Target. Used to stop the sticky-objective gate from free-re-driving
    // an interaction the bot already attempted but that produced no external
    // change (e.g. a door that opened in place). Only Use/Pickup goals qualify;
    // WorldObjectInteracted is never emitted for other verbs. Match uses
    // Selector AND semantics over the identity fields the event carries
    // (Guid/Name/Wcid): every populated comparable field must match and at least
    // one comparable field must be present. NameContains/ItemTypeMask/
    // ShortDescContains are ignored (the event carries no data to evaluate them).
    private bool SelfAlreadyInteractedWithGoalTarget(EventStream events, Goal goal)
    {
        if (goal.Kind is not (GoalKind.Use or GoalKind.Pickup)) return false;
        var t = goal.Target;
        var hasComparable = t.Guid is not null
            || !string.IsNullOrWhiteSpace(t.Name)
            || t.Wcid is not null;
        if (!hasComparable) return false;

        return events.Recent()
            .TakeWhile(e => e.Sequence >= _lastEventConsideredSequence)
            .Where(e => e.Kind == EventKind.WorldObjectInteracted)
            .Any(e =>
                (t.Guid is null || (e.ItemGuid is uint g && g == t.Guid))
                && (string.IsNullOrWhiteSpace(t.Name)
                    || string.Equals(e.Name, t.Name, StringComparison.OrdinalIgnoreCase))
                && (t.Wcid is null || (e.Wcid is uint w && w == t.Wcid)));
    }

    // Durable landblock-dwell bookkeeping. Called at the top of every
    // ProposeGoal tick. Stamps _dwellEntryUtc whenever the observed
    // self-landblock changes, on first observation, or when a session
    // gap (disconnect/reconnect) is detected. Mechanical only — keyed on
    // a wire-derived landblock id, no game knowledge.
    private void UpdateDwellTracking(uint? currentLandblock, int? currentLevel, EventStream events, DateTimeOffset nowUtc)
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
            // Snapshot the bot's own level at landblock entry (may be null
            // if not yet observed; lazily filled below). Reset per landblock
            // so the "tapped out" signal is scoped to THIS area's farming and
            // never carries stale stall state across a seam.
            _levelAtCurrentLandblockEntry = currentLevel;
        }

        // Lazy fill: if level was unknown at entry but is known now (still in
        // the same dwell landblock), anchor the entry level to the first known
        // value so the tapped-out comparison has a baseline.
        if (_levelAtCurrentLandblockEntry is null && currentLevel is not null)
            _levelAtCurrentLandblockEntry = currentLevel;
    }

    // The durable entry time to hand the prompt builder, but ONLY when it
    // corresponds to the landblock the bot is observed in THIS tick.
    // Otherwise null → the builder uses its event-window fallback.
    private DateTimeOffset? DwellEntryForPrompt(uint? currentLandblock)
        => (currentLandblock is uint lb && _dwellLandblock == lb)
            ? _dwellEntryUtc
            : (DateTimeOffset?)null;

    // Mechanical own-death recency bookkeeping for the prompt. Stamps the
    // wall-clock when the bot's own server-tracked death count increments — the
    // LLM cannot derive recency from a cumulative count alone. First observation
    // only anchors (a pre-existing count from prior sessions is not a fresh
    // death); an equal/decreased/null value never stamps. Own outcome only — no
    // game content. Mirrors the level-gain anchoring in UpdateDwellTracking.
    private void UpdateDeathRecencyTracking(int? numDeaths, DateTimeOffset nowUtc)
    {
        if (numDeaths is not int nd) return;
        if (_lastObservedDeaths is int prev && nd > prev)
        {
            _lastOwnDeathUtc = nowUtc;
            // One timestamp per fresh death since the last observation (normally
            // a single increment; loop covers a multi-death gap between ticks).
            for (var i = prev; i < nd; i++)
                _ownDeathTimesUtc.Add(nowUtc);
        }
        _lastObservedDeaths = nd;
    }

    // Whole seconds since the last observed in-session own-death, or null if the
    // bot has not died this session. Clamped to >= 0. Handed to the prompt
    // builder as recency telemetry.
    private int? SecondsSinceLastOwnDeath(DateTimeOffset nowUtc)
        => _lastOwnDeathUtc is DateTimeOffset d
            ? Math.Max(0, (int)(nowUtc - d).TotalSeconds)
            : (int?)null;

    // Count of the bot's OWN deaths within DeathSpiralWindow of now, pruning
    // entries older than the window. A recent death-RATE the LLM cannot derive
    // from the cumulative deaths count or the single last-death timestamp; used
    // to surface the death-spiral caution. Own outcome + a timer — no game content.
    private int RecentOwnDeathCount(DateTimeOffset nowUtc)
        => PruneAndCountWithinWindow(_ownDeathTimesUtc, nowUtc, DeathSpiralWindow);

    // Prune timestamps strictly older than (now - window) from `times` in place,
    // then return how many remain (those within the window, inclusive of the
    // exact boundary). Pure sliding-window bookkeeping; no game content.
    internal static int PruneAndCountWithinWindow(
        List<DateTimeOffset> times, DateTimeOffset nowUtc, TimeSpan window)
    {
        var cutoff = nowUtc - window;
        times.RemoveAll(t => t < cutoff);
        return times.Count;
    }

    // Sample the bot-to-target distance of the active goal's target each tick.
    // Tracks ONE locked guid for the current goal-target selector so a chase
    // produces a coherent distance trend; resets when the LLM switches target.
    // Own-geometry bookkeeping only — no game knowledge, no behavior change.
    private void UpdateGoalProgressTracking(WorldStateProjection world, Goal? currentGoal, DateTimeOffset nowUtc)
    {
        // Only goals that pursue a concrete world object have a meaningful
        // distance trend. Wait/Explore/Raise* carry a NON-empty target selector
        // by schema (e.g. "anywhere", "health", "war magic") but never chase a
        // world object, so gate on the verb kind — not just Target.IsEmpty — so
        // a visible object that happens to match such a selector name cannot
        // produce a bogus trend.
        if (currentGoal is null || currentGoal.Target.IsEmpty || !IsObjectPursuitKind(currentGoal.Kind))
        {
            ResetGoalProgress();
            return;
        }

        var key = currentGoal.Target.ToString();
        if (!string.Equals(key, _goalProgressKey, StringComparison.Ordinal))
        {
            ResetGoalProgress();
            _goalProgressKey = key;
        }

        // Lock onto a specific matched guid the first time the selector resolves
        // to a visible object, then keep sampling THAT guid so two same-named
        // mobs cannot blend into one bogus trend (rubber-duck guidance). If the
        // locked object is no longer visible, do not silently re-lock onto a
        // different same-name object — wait for it to come back or for the goal
        // to change.
        VisibleObjectProjection? tracked = null;
        if (_goalProgressGuid is uint g)
            tracked = world.Visible.FirstOrDefault(v => v.Guid == g);
        if (tracked is null && _goalProgressGuid is null)
        {
            tracked = world.Visible
                .Where(v => v.Distance is not null && VisibleMatchesSelector(currentGoal.Target, v))
                .OrderBy(v => v.Distance!.Value)
                .FirstOrDefault();
            if (tracked is not null)
            {
                _goalProgressGuid = tracked.Guid;
                _goalProgressLabel = FormatGoalProgressLabel(tracked);
            }
        }

        // Target not in view this tick: keep any prior samples (a chase that
        // just lost line-of-sight still shows its last trend) but add nothing.
        if (tracked?.Distance is not float dist) return;

        // Throttle so a high tick rate doesn't fill the buffer with near-
        // duplicate samples spanning a fraction of a second.
        if (_goalProgressSamples.Count > 0 &&
            nowUtc - _goalProgressLastSampleUtc < GoalProgressMinSampleInterval)
            return;

        _goalProgressSamples.Add((nowUtc, dist));
        _goalProgressLastSampleUtc = nowUtc;
        if (_goalProgressSamples.Count > GoalProgressMaxSamples)
            _goalProgressSamples.RemoveAt(0);
    }

    private void ResetGoalProgress()
    {
        _goalProgressKey = null;
        _goalProgressGuid = null;
        _goalProgressLabel = null;
        _goalProgressSamples.Clear();
    }

    // Goal verbs whose execution is "walk to a concrete world object and act on
    // it" — the only kinds for which a bot-to-target distance trend is
    // meaningful. Wait/Explore/Raise*/Unknown do not pursue a world object
    // (their target selector, when present, names a place or an attribute, not
    // an object to approach). Verb taxonomy is the bot's own, not game content.
    internal static bool IsObjectPursuitKind(GoalKind kind) => kind switch
    {
        GoalKind.Give or GoalKind.Use or GoalKind.Attack or GoalKind.Pickup
            or GoalKind.Wield or GoalKind.GoTo or GoalKind.Talk => true,
        _ => false,
    };

    private static string FormatGoalProgressLabel(VisibleObjectProjection v)
    {
        var name = string.IsNullOrEmpty(v.Name) ? "(unnamed)" : v.Name;
        return $"{name} (guid=0x{v.Guid:X8})";
    }

    // Build a render-ready snapshot of the current distance trend, or null when
    // there are too few samples to show a trend (need at least two points).
    private GoalProgressSnapshot? BuildGoalProgressSnapshot()
    {
        if (_goalProgressSamples.Count < 2 || _goalProgressLabel is null) return null;
        var dists = _goalProgressSamples.Select(s => s.Distance).ToList();
        var span = (_goalProgressSamples[^1].Utc - _goalProgressSamples[0].Utc).TotalSeconds;
        return new GoalProgressSnapshot(_goalProgressLabel, dists, span);
    }

    internal static string BuildUserPrompt(WorldStateProjection world, EventStream events, Goal? currentGoal)
        => BuildUserPrompt(world, events, currentGoal, stack: null, pickerActivity: null, explorationCandidates: null);

    internal static string BuildUserPrompt(WorldStateProjection world, EventStream events, Goal? currentGoal, IntentStack? stack)
        => BuildUserPrompt(world, events, currentGoal, stack, pickerActivity: null, explorationCandidates: null);

    // cp-2400: cheap pre-pass for the NPC REPEAT EXHAUSTION rule's relevance
    // gate. Returns true when the recent emission history (last 10 GoalEmitted)
    // shows the SAME Talk target emitted 2+ times — i.e. a re-Talk loop is
    // forming and the "stop re-Talking an exhausted NPC" guidance is actionable.
    // Mirrors the talkByKey parse used by `## Location & recency` (Tactics formats
    // GoalEmitted Text as `<Kind> target=<Selector> item=<Selector> source=<src>`,
    // and Selector.ToString() prints `guid=...` before `name="..."`), keying by
    // the guid token when present else the name token, so re-Talks of the SAME
    // NPC collapse to one count even when the selector carries a guid. A purely
    // structural read of the bot's OWN emission history — no server text, no game
    // knowledge.
    // cp-2400/cp-2401: cheap pre-pass for the repeat-driven RULES gates. Returns
    // true when the recent emission history (last 10 GoalEmitted) shows the SAME
    // (verb,target) emitted 2+ times for ANY of the given verb prefixes — i.e. a
    // re-action loop is forming and the corresponding "stop repeating" guidance
    // is actionable. Mirrors the talkByKey/useByKey parse used by
    // `## Location & recency` (Tactics formats GoalEmitted Text as
    // `<Kind> target=<Selector> item=<Selector> source=<src>`, and
    // Selector.ToString() prints `guid=...` before `name="..."`), keying by
    // VERB + (guid token else name token) so re-emissions of the SAME target
    // collapse to one count while a Talk and a Use of the same object stay
    // distinct. A purely structural read of the bot's OWN emission history — no
    // server text, no game knowledge.
    internal static bool HasRecentRepeatedGoalOfKinds(EventStream events, params string[] verbPrefixes)
    {
        // Last goal emissions from the DEDICATED durable window, not the
        // perception-dominated ring: ring-starved "recent 10 goals" under-counts
        // repeats made across a perception-heavy gap, so a real loop slips the
        // guard. (Same eviction fix as CountRecentTalkGoalsToName.)
        var recent = events.RecentGoalEmissions()
            .Where(e => !string.IsNullOrEmpty(e.Text))
            .Take(10);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recent)
        {
            var txt = ge.Text!;
            var verb = System.Array.Find(verbPrefixes, p => txt.StartsWith(p, StringComparison.Ordinal));
            if (verb is null) continue;
            var sm = System.Text.RegularExpressions.Regex.Match(txt, "target=(.*?) item=.*? source=");
            if (!sm.Success) continue;
            var sel = sm.Groups[1].Value.Trim();
            if (sel.Length == 0 || sel == "<empty>") continue;
            var gm = System.Text.RegularExpressions.Regex.Match(sel, "guid=0x[0-9A-Fa-f]+");
            var nm = System.Text.RegularExpressions.Regex.Match(sel, "name=\"([^\"]+)\"");
            var key = verb + (gm.Success ? gm.Value : (nm.Success ? nm.Groups[1].Value : sel));
            // Distinguish by the ITEM too, not just verb+target: a Give (or an
            // item-bearing Use) of DIFFERENT items to the same target is the bot
            // trying different things, NOT a no-progress repeat. Only fold the
            // item in when present — Talk and bare object-Use carry an empty item,
            // so their keys are byte-identical to before (no behavior change for
            // the existing Talk/Use callers).
            var im = System.Text.RegularExpressions.Regex.Match(txt, "item=(.*?) source=");
            if (im.Success)
            {
                var itm = im.Groups[1].Value.Trim();
                if (itm.Length > 0 && itm != "<empty>") key += " item=" + itm;
            }
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (counts[key] >= 2) return true;
        }
        return false;
    }

    // Normalize a target NAME for goal-emission counting so the bot's OWN recorded
    // emission matches the bare object/NPC name the fixation/refresh counters key
    // on. The prompt renders an object as `<Name> "<role>"` and a model frequently
    // copies that whole label into the target selector; the recorded
    // `name="<Name> "<role>""` truncates at the inner quote to `<Name> ` here (and a
    // model may also emit the full `<Name> "<role>"`). Strip a trailing rendered
    // role-title and trim — the SAME normalization the Motor's
    // SelectorResolver.StripTrailingQuotedRoleTitle uses to RESOLVE such a target —
    // so a role-suffixed emission still counts against the bare name (otherwise the
    // counters silently read 0 for a model that suffixes its targets, and the
    // fixation/settled-turn-in/refresh guards keyed on them never fire). Pure string
    // normalization; no game knowledge.
    internal static string NormalizeEmittedTargetName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return (HeadlessAcClient.Tactics.SelectorResolver.StripTrailingQuotedRoleTitle(raw) ?? raw).Trim();
    }

    // Count the bot's OWN recent Talk goals aimed at a given NPC NAME that were
    // emitted at or after `since` (the time the contract became stage-3). A
    // purely structural read of GoalEmitted history (the "VERB target=...
    // item=... source=..." text the executor records) — no server text, no game
    // knowledge. Scoping to `since` keeps a pre-completion or other-contract
    // Talk to the same NPC from mis-counting toward a done contract's hand-in
    // attempts. Surfaced in ## Contracts so the LLM can SEE that a stage-3
    // contract has already had repeated hand-in attempts with no stage change.
    internal static int CountRecentTalkGoalsToName(EventStream events, string npcName, DateTimeOffset since)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return 0;
        var target = NormalizeEmittedTargetName(npcName);
        var n = 0;
        // Read from the DEDICATED durable goal-emission window, NOT the
        // perception-dominated ring: re-talks to a turn-in NPC spread across
        // minutes (with heavy perception/motion traffic between) were evicted
        // from the 256-event ring before this count could reach the hand-in
        // threshold, so the "DONE (stage 3)" note silently never rendered for a
        // genuinely re-talked NPC. The window is already bounded and is
        // time-scoped here by `since`.
        foreach (var ge in events.RecentGoalEmissions()
                     .Where(e => !string.IsNullOrEmpty(e.Text)))
        {
            if (ge.Utc < since) continue;
            var txt = ge.Text!;
            if (!txt.StartsWith("Talk", StringComparison.Ordinal)) continue;
            // Format is "Talk target=<selector> item=<item> source=<src>"; the
            // target's name is the FIRST name="..." AFTER target=. Reading it
            // this way is robust to a name that itself contains " item=" or
            // " source=" (a bounded target=(.*?) item= regex would truncate it).
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti < 0) continue;
            var nm = System.Text.RegularExpressions.Regex.Match(
                txt.Substring(ti), "name=\"([^\"]+)\"");
            if (!nm.Success) continue;
            if (string.Equals(NormalizeEmittedTargetName(nm.Groups[1].Value), target, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    /// <summary>
    /// Counts how many of the bot's LAST <paramref name="lastN"/> emitted goals
    /// were a <c>Talk</c> to <paramref name="npcName"/>. Window is by GOAL COUNT
    /// (newest <paramref name="lastN"/> emissions), NOT time — so it is immune to
    /// interleaved combat/loot/Explore goals diluting a Talk fixation, mirroring
    /// the <c>## Recent Talk</c> "Talk to X xN in last 10 goals" recency render.
    /// Structural parse of the bot's own emission Text; no game knowledge.
    /// </summary>
    internal static int CountTalkGoalsToNameInLastN(EventStream events, string npcName, int lastN)
    {
        if (string.IsNullOrWhiteSpace(npcName) || lastN <= 0) return 0;
        var target = NormalizeEmittedTargetName(npcName);
        var n = 0;
        foreach (var ge in events.RecentGoalEmissions()
                     .Where(e => !string.IsNullOrEmpty(e.Text))
                     .Take(lastN))
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Talk", StringComparison.Ordinal)) continue;
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti < 0) continue;
            var nm = System.Text.RegularExpressions.Regex.Match(
                txt.Substring(ti), "name=\"([^\"]+)\"");
            if (!nm.Success) continue;
            if (string.Equals(NormalizeEmittedTargetName(nm.Groups[1].Value), target, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    // History-only proven single-NPC Talk fixation: the dominant Talk-target NAME
    // across the bot's last SingleNpcTalkHistoryWindowGoals emitted goals, IF that
    // NPC was Talked at least SingleNpcTalkHistoryThreshold times. Unlike
    // IsSingleNpcTalkFixationByHistory (which keys on a GIVEN goal's target), this
    // derives the fixated name from history ALONE, so the kickoff gate can detect a
    // fixation BEFORE the LLM is called. Same Talk-emission parse as
    // CountTalkGoalsToNameInLastN. Counts the bot's OWN emissions; no game
    // knowledge. Returns null when no NPC dominates past the threshold.
    internal static string? ProvenTalkFixationNameFromHistory(EventStream events)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in events.RecentGoalEmissions()
                     .Where(e => !string.IsNullOrEmpty(e.Text))
                     .Take(SingleNpcTalkHistoryWindowGoals))
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Talk", StringComparison.Ordinal)) continue;
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti < 0) continue;
            var nm = System.Text.RegularExpressions.Regex.Match(
                txt.Substring(ti), "name=\"([^\"]+)\"");
            if (!nm.Success) continue;
            var name = NormalizeEmittedTargetName(nm.Groups[1].Value);
            if (name.Length == 0) continue;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        foreach (var kv in counts)
            if (kv.Value >= SingleNpcTalkHistoryThreshold)
                return kv.Key;
        return null;
    }

    // Mirror of CountRecentTalkGoalsToName for Explore goals — counts the bot's
    // OWN recent Explore goals aimed at a given NAME emitted at/after `since`,
    // reading the same DEDICATED durable goal-emission window (so re-Explores
    // spread across minutes are not evicted by perception traffic). A stage-3
    // contract whose objective is a "locate/reach" task is pursued via Explore
    // (navigate-only), NOT Talk, so the turn-in hand-in count misses it; this is
    // the Explore-pursuit equivalent. Purely structural read of the bot's own
    // goal-emission history; no server text, no game knowledge.
    internal static int CountRecentExploreGoalsToName(EventStream events, string name, DateTimeOffset since)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var target = NormalizeEmittedTargetName(name);
        var n = 0;
        foreach (var ge in events.RecentGoalEmissions()
                     .Where(e => !string.IsNullOrEmpty(e.Text)))
        {
            if (ge.Utc < since) continue;
            var txt = ge.Text!;
            if (!txt.StartsWith("Explore", StringComparison.Ordinal)) continue;
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti < 0) continue;
            var nm = System.Text.RegularExpressions.Regex.Match(
                txt.Substring(ti), "name=\"([^\"]+)\"");
            if (!nm.Success) continue;
            if (string.Equals(NormalizeEmittedTargetName(nm.Groups[1].Value), target, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    // Count the bot's OWN recent Talk OR Use goals aimed at a given NAME emitted
    // at/after `since` (same dedicated durable goal-emission window as the Talk /
    // Explore counters). Used to BOUND the refresh-a-finished-batch nudge: a source
    // is re-engaged via Talk (a dialog task-giver) or Use (a vendor), so both verbs
    // count as one re-engage attempt. Two-object Uses (Use a door WITH a key) carry
    // a different target name and so do not match the source. Purely a structural
    // read of the bot's own goal history; no server text, no game knowledge.
    internal static int CountRecentEngageGoalsToName(EventStream events, string name, DateTimeOffset since)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var target = NormalizeEmittedTargetName(name);
        var n = 0;
        foreach (var ge in events.RecentGoalEmissions()
                     .Where(e => !string.IsNullOrEmpty(e.Text)))
        {
            if (ge.Utc < since) continue;
            var txt = ge.Text!;
            if (!(txt.StartsWith("Talk", StringComparison.Ordinal)
                || txt.StartsWith("Use", StringComparison.Ordinal))) continue;
            var ti = txt.IndexOf("target=", StringComparison.Ordinal);
            if (ti < 0) continue;
            var nm = System.Text.RegularExpressions.Regex.Match(
                txt.Substring(ti), "name=\"([^\"]+)\"");
            if (!nm.Success) continue;
            if (string.Equals(NormalizeEmittedTargetName(nm.Groups[1].Value), target, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    // Throttle key for the stage-3 contract DONE-note diagnostic (log only when
    // the per-contract decision inputs change).
    private string? _lastContractStage3Diag;

    // Post-stage-3 pursuit thresholds for the "DONE (stage 3)" recognition (the
    // ## Contracts note + its diagnostic, kept in sync). A Talk is a discrete
    // hand-in ATTEMPT, so 2 of them while STILL stage-3 means done. An Explore is
    // NAVIGATION: the FIRST is legitimate travel-to-reach (not an attempt), so the
    // equivalent "stuck/roving a done contract" signal needs 1 travel + 2 redundant
    // re-navigations = 3 — high enough that ordinary travel to a far turn-in NPC
    // (1-2 Explores) before the first hand-in Talk does NOT trip it.
    private const int StageDoneTalkThreshold = 2;
    private const int StageDoneExploreThreshold = 3;

    // Max times the refresh-a-finished-batch nudge re-engages the SAME source
    // before treating it as tapped. The source is re-engaged for a NEW batch, NOT a
    // hand-in; if the batch is still all-done after this many re-engages, re-engaging
    // again is futile (a tapped source, or a refresh mechanic the bot cannot drive),
    // so the nudge self-limits and the bot moves on. Two attempts mirrors the
    // stage-3 hand-in threshold — enough to actuate, low enough to avoid a loop.
    private const int BatchRefreshAttemptThreshold = 2;

    // Per-contract recognition of a SETTLED stage-3 turn-in (cp050): the contract
    // is DONE (wire ContractStage 3, done/pending-repeat), recorded WHEN it became
    // done, its turn-in NPC is UNIQUE among tracked contracts (a shared turn-in NPC
    // makes per-contract attribution ambiguous), and the bot has already pursued
    // that turn-in NPC past the post-stage-3 attempt threshold (Talk hand-in OR
    // Explore locate) with no stage change. Such a contract has no separate hand-in.
    // Yields the per-NPC attempt counts for the caller's message. Pure mechanical
    // read: wire ContractStage + the bot's OWN goal-emission history; no game
    // knowledge. SHARED by the `## Contracts` "DONE (stage 3)" note and the Motor's
    // settled-turn-in Talk backstop so the recognition never drifts between them.
    //
    // `sinceOverride` lets a caller widen the attempt-count window's start past the
    // contract's own Stage3SinceUtc. The render leaves it null (its "you have gone N
    // times since THIS contract completed" message is per-contract). The Motor
    // backstop passes the NPC's FULLY-SETTLED time (the max Stage3SinceUtc across
    // every contract that NPC starts/turns-in) so Talks the bot made for a SIBLING
    // contract of the SAME NPC while THAT one was still live business do not leak in
    // as this contract's fixation once the sibling also settles.
    internal static bool IsSettledStage3TurnIn(
        WorldStateProjection world, EventStream events, ContractProjection c,
        out int talkTries, out int exploreTries, DateTimeOffset? sinceOverride = null)
    {
        talkTries = 0;
        exploreTries = 0;
        if (c.Stage != 3u) return false;
        var npcEnd = OneLine(c.NpcEnd);
        if (npcEnd is null) return false;
        if (c.Stage3SinceUtc is not { } since3) return false;
        if (world.Contracts.Count(o =>
                string.Equals(OneLine(o.NpcEnd), npcEnd, StringComparison.OrdinalIgnoreCase)) != 1)
            return false;
        var since = sinceOverride is { } ov && ov > since3 ? ov : since3;
        talkTries = CountRecentTalkGoalsToName(events, npcEnd, since);
        exploreTries = CountRecentExploreGoalsToName(events, npcEnd, since);
        return talkTries >= StageDoneTalkThreshold || exploreTries >= StageDoneExploreThreshold;
    }

    // Name-keyed wrapper for the Motor's settled-turn-in Talk backstop: is
    // `npcName` the settled stage-3 turn-in NPC (per IsSettledStage3TurnIn) of some
    // tracked contract, with NO remaining live business? An NPC that also starts or
    // turns in any NON-terminal (stage != 3) tracked contract is EXCLUDED — the bot
    // may legitimately Talk it to accept or progress that contract (e.g. a fresh
    // batch just obtained from this same source), so only a purely settled NPC
    // qualifies for suppression. The attempt-count window starts at the NPC's
    // FULLY-SETTLED time — the latest Stage3SinceUtc among ALL contracts it
    // starts/turns-in — so a re-Talk only counts as fixation once the NPC has no
    // live business left (Talks made while a sibling contract of the same NPC was
    // still live do not leak in). Own contract stage + own goal history; no game
    // knowledge.
    internal static bool IsSettledStage3TurnInNpc(
        WorldStateProjection world, EventStream events, string? npcName)
    {
        var name = OneLine(npcName);
        if (string.IsNullOrWhiteSpace(name)) return false;
        bool Involves(ContractProjection o) =>
            string.Equals(OneLine(o.NpcStart), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(OneLine(o.NpcEnd), name, StringComparison.OrdinalIgnoreCase);
        if (world.Contracts.Any(o => o.Stage != 3u && Involves(o)))
            return false;
        DateTimeOffset? fullySettledSince = null;
        foreach (var o in world.Contracts)
            if (Involves(o) && o.Stage3SinceUtc is { } s
                && (fullySettledSince is not { } cur || s > cur))
                fullySettledSince = s;
        foreach (var c in world.Contracts)
            if (string.Equals(OneLine(c.NpcEnd), name, StringComparison.OrdinalIgnoreCase)
                && IsSettledStage3TurnIn(world, events, c, out _, out _, fullySettledSince))
                return true;
        return false;
    }

    // The NAMES of any done-batch issuing/turn-in NPCs (a done contract's
    // NpcStart/NpcEnd) currently in view as a creature/vendor. Shared by the
    // unbounded "a contract source IS in view" check (DoneBatchIssuerInView) and the
    // bounded refresh nudge (DoneBatchSourceInViewToRefresh). Yields only when every
    // tracked contract is DONE (a finished batch). Own contract projection + own
    // perception; no hardcoded source name, no object-type priority.
    private static IEnumerable<string> VisibleDoneBatchSourceNames(WorldStateProjection world)
    {
        if (world.Contracts.Count == 0 || !world.Contracts.All(c => c.Stage == 3u))
            yield break;
        foreach (var c in world.Contracts)
            foreach (var srcName in new[] { OneLine(c.NpcStart), OneLine(c.NpcEnd) })
                if (!string.IsNullOrWhiteSpace(srcName)
                    && world.Visible.Any(v =>
                        (v.IsCreature || v.IsVendor)
                        && string.Equals(OneLine(v.Name), srcName, StringComparison.OrdinalIgnoreCase)))
                    yield return srcName!;
    }

    // A done batch's issuing/turn-in source is currently in view (UNBOUNDED — true
    // even after the refresh bound is spent, because a tapped source is still a
    // source physically in view). Used so the RETURN-TO-A-CONTRACT-SOURCE
    // travel-back nudge does NOT also fire ("no source in view") while such a source
    // stands right here — that source IS in view, so engage/skip it, do not travel
    // away to find one.
    internal static bool DoneBatchIssuerInView(WorldStateProjection world) =>
        VisibleDoneBatchSourceNames(world).Any();

    // The already-engaged-source case FIND-A-KILL-TASK-SOURCE's un-talked/unbrowsed
    // gate MISSES: the bot holds a FINISHED batch (every tracked contract stage-3),
    // and the npc/vendor that ISSUED it (a done contract's start/turn-in NPC NAME) is
    // in view, but it has already been talked/browsed — so FIND will not re-nudge it
    // even though re-engaging it is how a fresh batch is obtained. Recognize that
    // source by name-matching a done contract's NpcStart/NpcEnd against a visible
    // creature/vendor, BOUNDED by the bot's own recent re-engage count since the
    // batch completed (so a tapped source is not re-engaged forever). Own contract
    // projection + own perception + own goal history; no hardcoded source name, no
    // object-type priority, no source-side decision to engage.
    internal static bool DoneBatchSourceInViewToRefresh(WorldStateProjection world, EventStream events)
    {
        // When the batch BECAME all-done = the latest per-contract stage-3 time;
        // count re-engages only since then (earlier hand-ins do not count).
        DateTimeOffset? since = null;
        foreach (var c in world.Contracts)
            if (c.Stage3SinceUtc is { } s && (since is null || s > since))
                since = s;
        foreach (var srcName in VisibleDoneBatchSourceNames(world))
        {
            var attempts = since is { } sinceUtc
                ? CountRecentEngageGoalsToName(events, srcName, sinceUtc)
                : 0;
            if (attempts < BatchRefreshAttemptThreshold)
                return true;
        }
        return false;
    }

    // Diagnostic (cp024 pattern, behavior-preserving): the "DONE (stage 3)" note
    // in ## Contracts fires ONLY after >=2 Talk hand-ins to a UNIQUE turn-in NPC
    // since stage-3 — so it never fires for a stage-3 contract pursued via Explore
    // (a "locate/reach" objective) or one sharing a turn-in NPC, letting the bot
    // rove between such already-done contracts (live cp029-validate.log: an
    // Explore ping-pong between two stage-3 contract NPCs, broker present, 0 Buy).
    // Surface, throttled, the exact inputs to that decision (per stage-3 contract:
    // its end/start NPC, turn-in uniqueness, stage-3 timestamp presence, and the
    // bot's OWN Talk/Explore pursuit counts since stage-3) so the follow-up
    // behavior broadening is precise. Reads only the bot's own contract projection
    // + goal-emission history; no server text, no game knowledge, no decision.
    private void EmitContractStage3Diagnostic(WorldStateProjection world, EventStream events)
    {
        if (world.Contracts is not { Count: > 0 } contracts) return;
        var line = new StringBuilder();
        foreach (var c in contracts)
        {
            if (c.Stage != 3u) continue;
            var npcEnd = OneLine(c.NpcEnd);
            var npcStart = OneLine(c.NpcStart);
            var objective = OneLine(c.Description);
            var uniqEnd = npcEnd is not null && contracts.Count(o =>
                string.Equals(OneLine(o.NpcEnd), npcEnd, StringComparison.OrdinalIgnoreCase)) == 1;
            int talkEnd = -1, exploreEnd = -1, exploreStart = -1;
            if (c.Stage3SinceUtc is { } s3)
            {
                if (npcEnd is not null)
                {
                    talkEnd = CountRecentTalkGoalsToName(events, npcEnd, s3);
                    exploreEnd = CountRecentExploreGoalsToName(events, npcEnd, s3);
                }
                if (npcStart is not null)
                    exploreStart = CountRecentExploreGoalsToName(events, npcStart, s3);
            }
            var doneNoteFires = npcEnd is not null && c.Stage3SinceUtc is not null && uniqEnd
                && (talkEnd >= StageDoneTalkThreshold || exploreEnd >= StageDoneExploreThreshold);
            line.Append(
                $"id={c.ContractId} end=\"{npcEnd ?? "-"}\" start=\"{npcStart ?? "-"}\" " +
                $"obj=\"{Truncate(objective ?? "-", 50)}\" " +
                $"uniqEnd={uniqEnd} since3={(c.Stage3SinceUtc is not null)} " +
                $"talkEnd={talkEnd} expEnd={exploreEnd} expStart={exploreStart} doneNote={doneNoteFires} | ");
        }
        if (line.Length == 0)
        {
            // No stage-3 contracts this tick — clear the throttle so a later
            // reappearance in the SAME state is not suppressed as a duplicate.
            _lastContractStage3Diag = null;
            return;
        }
        var key = line.ToString();
        if (key == _lastContractStage3Diag) return;
        _lastContractStage3Diag = key;
        Console.WriteLine("[contract-stage3] " + key.TrimEnd(' ', '|'));
    }

    private string? _lastContractBatchSourceDiag;

    // Diagnostic (cp030 pattern, behavior-preserving): when the bot holds a
    // FINISHED contract batch (every tracked contract stage-3), the FIND-A-
    // KILL-TASK-SOURCE rule can only nudge re-engaging a source that is CURRENTLY
    // in `Visible nearby` (its untalked-npc / open-panel-vendor gate). Live
    // cp033-validate: the bot held a done batch while the issuing source sat out
    // of view (1 PVS create, 0 engage) and kept grinding open-world monsters.
    // Log, throttled, whether a source IS actionable in view (the rule fires, so
    // any non-engage is the LLM ignoring it) or NOT (a known source is out of
    // view and the bot has no navigate-back signal), plus the nearest visible
    // npc, so the follow-up fix targets the real cause. Instance-scoped throttle
    // (like the cp030 stage-3 diagnostic). The EMISSION runs at the prompt-build
    // decision point right after RecordTalkedNpcs, so it reads the SAME refreshed
    // talked-set the FIND-A-KILL-TASK-SOURCE rule uses; the THROTTLE RESET runs
    // separately EVERY tick (ResetContractBatchSourceDiagThrottle, before any
    // early return), so a later reappearance of the same state logs again even if
    // the batch toggled during an in-flight/backoff span. Reads own contract
    // projection + own perception + own talked-set only; no decision, no game
    // knowledge.
    private static bool HeldBatchAllDone(WorldStateProjection world)
        => world.Contracts.Count > 0 && world.Contracts.All(c => c.Stage == 3u);

    // Every-tick throttle reset (runs before ProposeGoalCore's early returns):
    // clear the dedupe key whenever the finished-batch state is absent, so a
    // later return to the SAME state is not suppressed as a duplicate. Decoupled
    // from the emission (which needs the refreshed talked-set, only available at
    // the prompt-build point). Field-only; no prompt/decision effect.
    private void ResetContractBatchSourceDiagThrottle(WorldStateProjection world)
    {
        if (!HeldBatchAllDone(world))
            _lastContractBatchSourceDiag = null;
    }

    private void EmitContractBatchSourceDiagnostic(WorldStateProjection world)
    {
        // Not applicable when no finished batch is held; the every-tick reset
        // above owns clearing the throttle, so just skip emission here.
        if (!HeldBatchAllDone(world)) return;
        var vendorInView = world.Visible.Any(v => v.IsVendor);
        // Criterion-2 self-report: record each DISTINCT vendor seen while the bot
        // holds a finished contract batch (a refresh BUY opportunity). The set
        // dedups by vendor guid, so lingering at or re-approaching the same vendor
        // counts ONCE regardless of how the diagnostic key below changes (distance,
        // nearby-NPC flips). Pure observability over the bot's OWN contract +
        // perception state; runs before the log dedup so it is not gated by it.
        CollectRefreshVendorGuids(world, _summaryRefreshVendorGuids);
        var untalkedNpcInView =
            CountUntalkedNpcsInView(world, _talkedNpcGuids, _talkedNpcNames, excludeVendors: true) > 0;
        var key = BuildContractSourceDiagKey(world, untalkedNpcInView, vendorInView);
        if (key == _lastContractBatchSourceDiag) return;
        _lastContractBatchSourceDiag = key;
        Console.WriteLine("[contract-source] " + key);
    }

    // Accumulate the guids of vendors in view WHILE the bot holds a finished
    // contract batch (a contract-refresh BUY opportunity) into <paramref
    // name="into"/>. A set keyed by vendor guid, so the SAME vendor across many
    // ticks (or with a changing diagnostic key — distance, nearby-NPC flips) counts
    // ONCE. Static + pure for unit testing. Own contract + perception state; no
    // game knowledge, no decision.
    internal static void CollectRefreshVendorGuids(WorldStateProjection world, HashSet<uint> into)
    {
        if (!HeldBatchAllDone(world)) return;
        foreach (var v in world.Visible)
            if (v.IsVendor) into.Add(v.Guid);
    }

    // Pure key builder for the contract-batch-source diagnostic (extracted for
    // deterministic unit testing). The nearest non-monster, non-corpse creature
    // is the "is a task-giver near?" proxy. `ruleFires` mirrors the FIND-A-
    // KILL-TASK-SOURCE gate (an open-panel-less vendor OR an un-talked npc in
    // view). Own perception + own contract projection + own vendor-panel state
    // only; no decision, no game knowledge.
    internal static string BuildContractSourceDiagKey(
        WorldStateProjection world, bool untalkedNpcInView, bool vendorInView)
    {
        var ruleFires = (vendorInView && world.Vendor is null) || untalkedNpcInView;
        var nearestNpc = world.Visible
            .Where(v => v.IsCreature && !v.IsMonster && !v.IsCorpse)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        var npcDesc = nearestNpc is null
            ? "none"
            : $"\"{OneLine(nearestNpc.Name) ?? "-"}\"@{(nearestNpc.Distance is { } d ? d.ToString("F1") : "?")}";
        return
            $"doneBatch={world.Contracts.Count} untalkedNpcInView={untalkedNpcInView} " +
            $"vendorInView={vendorInView} vendorPanelOpen={world.Vendor is not null} " +
            $"ruleFires={ruleFires} nearestNpc={npcDesc}";
    }

    // cp-2402: cheap pre-pass for the BLOCKED-targets rule's relevance gate.
    // Returns true when the recent event history carries an ActionRejected
    // whose ErrorLabel is "Blocked" or "Unreachable" (the Motor surfaces these
    // when server physics held the bot against geometry). The rule only tells
    // the LLM how to react to such a rejection, so it is inapplicable noise
    // without one. A purely structural read of the bot's OWN rejection events —
    // the SAME ErrorLabels the Motor emits (HandshakeDriver) — no game knowledge.
    private static bool HasRecentBlockedRejection(EventStream events)
        => events.RecentOfKind(EventKind.ActionRejected, 16)
            .Any(e => e.ErrorLabel is "Blocked" or "Unreachable");

    // True when the bot has had an ActionRejected within the recovery WINDOW —
    // recent enough that the reactive "how to recover from a refusal" guidance
    // is still actionable. TIME-based (not a raw event-count window) so it is
    // independent of the per-tick event rate: it reliably covers the next few
    // ~7s LLM decisions after a refusal, then DECAYS so the rule costs zero
    // prompt bytes once recovery is moot (a raw `.Any()` over retained
    // rejections would stay latched until they age out of the 256-event ring).
    // Structural read of the bot's OWN rejection events; no game knowledge.
    private static readonly TimeSpan ActionRejectedRecoveryWindow = TimeSpan.FromSeconds(30);
    private static bool HasRecentActionRejected(EventStream events)
        => events.RecentOfKind(EventKind.ActionRejected, 16)
            .Any(e => DateTimeOffset.UtcNow - e.Utc <= ActionRejectedRecoveryWindow);

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
        DateTimeOffset? dwellEntryUtc = null,
        IReadOnlyList<SightedRecallProjection>? recentSightings = null,
        int? levelAtLandblockEntry = null,
        int? secondsSinceLastDeath = null,
        GoalProgressSnapshot? goalProgress = null,
        IReadOnlyList<UnreachableTargetProjection>? unreachableTargets = null,
        ApproachDistanceProjection? approachDistance = null,
        ExcursionCoverageProjection? excursionCoverage = null,
        IReadOnlyList<FreshKillCorpse>? freshKillCorpses = null,
        IReadOnlyList<LootedCorpse>? lootedEmptyCorpses = null,
        (int Distinct, bool Latched)? localUseChurn = null,
        IReadOnlySet<uint>? talkedNpcGuids = null,
        IReadOnlySet<string>? talkedNpcNames = null,
        int? promptCeiling = null,
        int recentOwnDeathCount = 0)
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
  "kind": "Give" | "Use" | "Attack" | "Pickup" | "Wield" | "GoTo" | "Talk" | "Wait" | "Explore" | "RaiseAttribute" | "RaiseVital" | "RaiseSkill" | "Recall" | "Buy" | "Sell",
  "target": { "name"?: string, "name_contains"?: string, "wcid"?: number, "item_type_mask"?: number, "short_desc_contains"?: string, "guid"?: number },
  "item":   { ...same as target... } | null,
  "amount": number | null,   // Raise* only: whole positive XP; target.name = the attribute/vital/skill
  "direction": "north"|"northeast"|"east"|"southeast"|"south"|"southwest"|"west"|"northwest" | null,   // Explore only: OPTIONAL compass bearing the bot COMMITS to and travels (short forms n/ne/e/se/s/sw/w/nw also accepted); omit to wander undirected
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
  "kind": "Give" | "Use" | "Attack" | "Pickup" | "Wield" | "GoTo" | "Talk" | "Wait" | "Explore" | "RaiseAttribute" | "RaiseVital" | "RaiseSkill" | "Recall" | "Buy" | "Sell",
  "target": { "name"?: string, "name_contains"?: string, "wcid"?: number, "item_type_mask"?: number, "short_desc_contains"?: string, "guid"?: number },
  "item":   { ...same as target... } | null,
  "amount": number | null,   // Raise* only: whole positive XP; target.name = the attribute/vital/skill
  "direction": "north"|"northeast"|"east"|"southeast"|"south"|"southwest"|"west"|"northwest" | null,   // Explore only: OPTIONAL compass bearing the bot COMMITS to and travels (short forms n/ne/e/se/s/sw/w/nw also accepted); omit to wander undirected
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
          // Pick exactly ONE typed completion predicate. The "type"
          // field is the discriminator (NOT "$type"). Types + fields:
          //   {"type":"landblock_changed_from_push"}   -- you left the landblock you were in when this intent was pushed
          //   {"type":"landblock_equals","landblock":<number>}
          //   {"type":"visible_tag","tag":"<tag>"}     -- a `Visible nearby` object carries this tag (e.g. "monster","corpse")
          //   {"type":"no_monsters_visible"}
          //   {"type":"inventory_has_name","name_contains":"<s>"}
          //   {"type":"inventory_has_wcid","wcid":<n>}
          //   {"type":"inventory_added_since_push_at_least","count":<n>,"name_contains":"<s>"?}
          //   {"type":"kill_count_since_push_at_least","count":<n>,"name_contains":"<s>"?}
          //   {"type":"kill_count_total_at_least","count":<n>}
          //   {"type":"levels_gained_total_at_least","count":<n>}
          //   {"type":"level_at_least","level":<n>}
          //   {"type":"level_gain_since_push_at_least","count":<n>}
          //   {"type":"num_deaths_at_least","count":<n>}
          //   {"type":"num_deaths_since_push_at_most","count":<n>}
          //   {"type":"coin_value_at_least","count":<n>}
          //   {"type":"coin_gain_since_push_at_least","count":<n>}
          //   {"type":"units_traveled_since_push_at_least","count":<n>}
          //   {"type":"elapsed_seconds_at_least","seconds":<n>}
          //   {"type":"health_fraction_at_most","fraction":<0..1>}
          //   {"type":"any_of","children":[ ...nested... ]}
          //   {"type":"all_of","children":[ ...nested... ]}
          //   {"type":"not","child":{ ...nested... }}
          //   {"type":"always_false"}  -- never auto-completes (deadline/pop_top only)
        },
        // ESCAPE HATCH: if NO existing type fits, set completion to
        // {"type":"always_false"} and populate this field with a prose
        // description of the predicate we need. Meanwhile the intent can
        // only be popped by its deadline or by an explicit pop_top.
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
        sb.AppendLine("- ACT ON A GIVE-REQUEST FOR A HELD ITEM: if an inventory item's short_desc OR its `use` text — OR a visible NPC's recent dialogue/tell — asks you to GIVE / return / hand / drag a SPECIFIC item you ALREADY HOLD to a (named) NPC (a named item — NOT a generic vendor 'drag items to me to sell/buy' bark), satisfy it with `Give{target: that NPC, item: that item}` rather than only re-`Talk`ing for it (re-Talking just re-issues the request). `Give` requires BOTH target (the NPC) and item (the thing given). `Explore` toward the NPC first only if it is not in `Visible nearby`; if the `Give` is REFUSED, follow the REFUSED GIVE rule (Talk to advance the dialog, then re-Give).");
        // cp-2407: split the old combined ActionRejected rule. The pre-emptive
        // double-click-Use-on-self half stays ALWAYS-on (it tells the bot to
        // self-Use an activatable item BEFORE a related Give/Talk so the
        // rejection never happens — gating it on a prior rejection would defeat
        // its purpose). The REACTIVE recovery half (how to respond to a refusal)
        // is only actionable once a rejection has occurred, so it is gated on
        // HasRecentActionRejected (cp-2402 per-rule relevance-gating). A render
        // gate on the bot's OWN rejection events; the LLM still decides; no game
        // knowledge.
        sb.AppendLine("- Items whose `short_desc` says 'double-click', 'read', or 'activate' must be Use'd on yourself FIRST (before related Give/Talk unlock) — emit `Use{item: name=\"<that item>\"}` (the item acts on you; no target needed) over retrying a refused combo.");
        if (HasRecentActionRejected(events))
        sb.AppendLine("- `ActionRejected` = the server refused that exact (kind, target, item). Do NOT immediately retry the same combo; read its `label`/`message`, then pick a different verb, item, or NPC. TWO+ rejections of the same target+item (any verb) = BLOCKED (unmet prerequisite).");
        sb.AppendLine("- Read `## Server hints`, `## Early server directives`, and `## System messages`: phrases like \"Double click X\" or \"Use X to ...\" tell you the exact verb+target. If that object is visible AND the server instructed it, emit `Use{target: name=\"X\"}`. The server is your tutorial; don't ignore it for pure exploration.");
        // Whether a monster is in view — the SAME wire fact (`!IsCorpse &&
        // (IsMonster || ObservedHostile)`) that produces the `monsters in view`/
        // `nearest monster` lines. Computed HERE (moved up from below) so the
        // combat-targeting rule directly under it can also be gated on it.
        // Several rules are entirely about ONE side of this fact, so they are
        // gated on it (cp-2331/cp-2335/cp-2369 per-rule relevance-gating):
        // render a rule ONLY when its telemetry is present, so the applicable
        // rules are not buried by inapplicable ones. A render gate on an
        // observed fact; the LLM still decides; no game knowledge.
        var monsterInView =
            world.Visible.Any(v => !v.IsCorpse && (v.IsMonster || v.ObservedHostile));
        // cp-2406: the Combat targets rule (monster = valid XP target; npc =
        // do-not-attack; combat = primary XP source) is only ACTIONABLE when a
        // monster is actually in view — with none visible there is nothing to
        // Attack, the npc-do-not-attack guardrail is moot (Attack is not a
        // candidate selector), and the LEVELING rule below already steers
        // exploration toward monsters when none are in view. Gate it on
        // monsterInView to free prompt budget in the common no-monster scene.
        if (monsterInView)
        sb.AppendLine("- Combat targets: `monster`-tagged creatures are valid combat targets (grant XP + loot); `npc`-tagged are civilians — talk/trade, do NOT attack. Combat is the primary XP source outside NPC quests.");
        // SELF-ARM is entirely about getting armed: it applies ONLY when the bot
        // is NOT yet combat-effective (no melee weapon wielded, and no wielded
        // missile weapon with ammo loaded). Once combat-effective the rule is
        // moot, so gate it on the SAME wire fact (`WieldedAt != 0` + the typed
        // MeleeWeapon / MissileWeapon / MissileAmmo masks) that the `## Combat
        // readiness` `weapon:` line is derived from (cp-2335 per-rule gating
        // pattern). Behaviour-preserving — it renders identically whenever the
        // bot is unarmed or has a missile weapon with EMPTY ammo; the HOSTILE-
        // attacker-takes-priority clause is also carried by the COMBAT SAFETY
        // rule when a monster/hostile is in view. A render gate on an observed
        // fact; the LLM still decides; no game knowledge.
        var selfArmMeleeWielded = world.Inventory.Any(i =>
            i.WieldedAt is uint sw && sw != 0 &&
            i.ItemType is uint sit && (sit & ItemTypeMasks.MeleeWeapon) != 0);
        var selfArmMissileWielded = world.Inventory.Any(i =>
            // Require a MAIN-WEAPON slot, not just WieldedAt != 0: loaded ammo sits in
            // the ammo slot and can carry the MissileWeapon ItemType bit, so the slot
            // mask is what tells a wielded launcher from loaded ammo (mirrors
            // IsCombatCapable / WeaponSwap.IsWieldedWeapon).
            i.WieldedAt is uint smw && (smw & WeaponSwap.MainWeaponSlotMask) != 0 &&
            i.ItemType is uint smit && (smit & ItemTypeMasks.MissileWeapon) != 0);
        var selfArmAmmoLoaded = world.Inventory.Any(i =>
            i.WieldedAt is uint saw && saw == ItemTypeMasks.MissileAmmoSlot);
        // A wielded THROWN weapon (missile weapon with no AmmoType — it is its own
        // projectile, server Player_Missile.cs throws the weapon itself) is armed with
        // no separate ammo, so it makes the bot combat-effective on its own. Mirrors
        // IsCombatCapable / the body `wieldedThrownWeapon`.
        var selfArmThrownWielded = world.Inventory.Any(i =>
            i.WieldedAt is uint stw && (stw & WeaponSwap.MainWeaponSlotMask) != 0 &&
            i.ItemType is uint stit && (stit & ItemTypeMasks.MissileWeapon) != 0 &&
            i.AmmoType is null);
        var selfArmCombatEffective =
            selfArmMeleeWielded || selfArmThrownWielded || (selfArmMissileWielded && selfArmAmmoLoaded);
        if (!selfArmCombatEffective)
        sb.AppendLine("- SELF-ARM before fighting: if `Combat readiness` says `UNARMED` your fists fight at a big DISADVANTAGE (much lower accuracy and damage than a trained weapon) — you can usually beat only the WEAKEST monsters and anything tougher is risky until you arm up. Prefer to arm before OPTIONAL combat, but you CAN still fight unarmed, so don't wander empty-handed: arm if you can, else hunt the weak monsters you CAN beat. If it lists a `melee weapon in your inventory`, emit `Wield` for that item; else if it lists a `melee weapon nearby`, emit `Pickup` for it. If it lists a `throwable weapon in your inventory`, emit `Wield` for it — a thrown weapon is its own projectile, so once wielded you can `Attack` with NO ammo. If a `missile weapon` is wielded but `missile ammo: EMPTY`, you cannot fire — if it lists `missile ammo in your inventory`, emit `Wield` for that ammo before attacking. Do NOT re-emit a `Wield`/`Pickup` the policy rejected or that is unreachable — try the other source or move on. If you have NO weapon to `Wield` or `Pickup` but a `vendor` is in view, `Use` it to reveal its `Vendor offerings`, and if those list a `[weapon]` or a `[missile weapon/ammo]` you can afford, `Buy` it by its exact name — buying a weapon to arm yourself is DIRECTED progress that outranks optional grinding. After buying it lands in your inventory: a thrown weapon then shows as a `throwable weapon` to Wield (arming you directly, no ammo needed); a launcher and its ammo arm you only as a PAIR — BOTH must be in your bag before the `missile launcher + compatible ammo` hint appears. So if you bought a launcher (or ammo) and see no arming hint, you are missing the matching piece: buy THAT (or a thrown weapon) instead — do NOT re-buy the same item expecting a hint. If you have no weapon to `Wield`/`Pickup` and the `Vendor offerings` you have browsed here have nothing you can afford, then getting coin is your TOP priority — above more NPC-talk, vendor-browsing, or contract-buying: `Explore` to the WEAKEST monsters and `Attack` them to LOOT a weapon and coin from their corpses (a looted weapon arms you; looted coin then buys one). That loot-to-arm hunt is the ONE exception to arming before combat. A `HOSTILE` attacker still takes priority — defend or flee even while unarmed.");
        // Shared render gate: true only when unspent XP meets the floor predicate
        // (the default floor passes any positive value). Reused by every unspent-XP
        // prompt surface below so they gate identically. Computed once.
        var unspentSpendSurfaced = world.Self.AvailableExperience is long unspentForSpend
            && ShouldSurfaceUnspentXp(unspentForSpend, MinMeaningfulUnspentXp);
        // Stuck-unarmed state (no main weapon wielded AND none usable anywhere): the
        // SPEND XP / COMBAT SAFETY accuracy guidance below is written for a WIELDED
        // weapon (accuracy = the weapon skill + coordination, strength = damage only).
        // For UNARMED fists the UnarmedCombat to-hit is half STRENGTH + half coordination,
        // so strength raises accuracy too — surface the same exception the `unarmed
        // accuracy` note carries, gated on this state, so the broad rules do not
        // contradict the note/capsule for a weaponless bot.
        var stuckUnarmedForSpend = WieldedMainWeapon(world) is null && HasNoUsableWeaponAnywhere(world);
        sb.AppendLine("- WIELD A WEAPON YOU ARE SKILLED WITH: every weapon is governed by a weapon SKILL, and a TRAINED weapon skill is the main driver of whether your swings LAND — an UNTRAINED weapon skill misses far more, so you cannot kill with it no matter how strong the weapon. If `Combat readiness` shows a `weapon skill MISMATCH` line (you are wielding a weapon whose skill you have NOT trained while a TRAINED-skill weapon sits in your bag), emit `Wield` for the listed bag weapon — prefer a weaker-looking weapon you ARE skilled with over a stronger one you are not." + (unspentSpendSurfaced ? " Then raise that trained weapon skill with spare XP (see SPEND XP)." : ""));
        sb.AppendLine("- LEVELING is core progress — be PROACTIVE, not reactive. When combat-ready (`Combat readiness` does NOT say `UNARMED`) AND not mid an explicit server/quest directive: if a `monster` is in view, `Attack` it (per COMBAT SAFETY below); if NO `monster` is in view, do NOT loiter among town `npc`s once their dialog is exhausted — emit `Explore{target: {name: \"anywhere\"}}` toward open areas where monsters live. Do not wait to be attacked first. TAPPED-OUT EXCEPTION (overrides the directive hold): when `Combat readiness` says `tapped out` (combat-ready, but no new level or quest item for several minutes in this `landblock`) AND a `monster` is in view, `Attack` it EVEN while a server/quest directive is active (unless you have an UNACTED quest step available — see OBJECTIVE-OVER-GRIND below). A directive you keep re-`Talk`/`Use`/`Pickup`-ing with no NEW dialog, item, or level is NOT progressing — re-emitting that social verb just loops. `Attack` the visible `monster` for XP (per COMBAT SAFETY — skip a KIND that has already beaten you), then resume the directive. If EVERY visible `monster` is a KIND that has already beaten you (none safe to fight), emit `Explore{target: {name: \"anywhere\"}}` to leave this area rather than re-loop the stalled directive. OBJECTIVE-OVER-GRIND: but if you have an UNACTED step toward a server/quest objective — a place you have not yet reached, an NPC you have not yet `Give`/`Talk`-ed for it, or a held quest item whose own text says to deliver or `Use` it — DO that untried step instead of `Attack`ing the same weak `monster` kind for marginal XP; an untried quest step advances you faster than farming a kind that is barely leveling you. (With NO unacted objective step, keep hunting per this exception.)");
        // monsterInView is computed ABOVE (moved up so the Combat targets rule
        // can be gated on it too). The rules below reuse it.
        // The NON-HOSTILE rule references `nearest monster`/`monsters in view`
        // > 0, so render it ONLY when a monster is actually in view.
        if (monsterInView)
        sb.AppendLine("- NON-HOSTILE IS NOT NON-TARGET: a visible `monster` is a valid XP target whether or not it has attacked you — `0 attacking you now` means none are on you YET, NOT \"nothing to fight\". If `Combat readiness` lists a `nearest monster`, you ALREADY have a target, so do NOT emit `Explore` \"to find monsters\" while `monsters in view` is above 0 — `Attack` a killable/nearest `monster` instead. Having NO active objective is itself NOT a reason to wander: when a `monster` is in view and you have no other task, HUNTING it (killing it for XP and levels) IS a valid objective — so do NOT `Explore` \"to find a new objective\" or \"to find a new area\" PAST a visible killable `monster`; engage it where it is" + (unspentSpendSurfaced ? " (and `Raise...` any `unspent` XP between fights)" : "") + ". (per COMBAT SAFETY: prefer a KIND you can defeat and skip one that has already beaten you; low `health`, a fresh `corpse` to loot, or an explicit server/quest directive still take priority).");
        // cp-2400: the NPC REPEAT EXHAUSTION rule (~1.1KB) advises against
        // re-Talking an EXHAUSTED NPC. It is ONLY actionable once the bot has
        // actually re-Talked the SAME NPC — a Talk-goal repeat in the recent
        // emissions (which a `## Server hints` dialog `repeated xN` likewise
        // implies, since a dialog repeat follows a re-Talk). With no such repeat
        // it is inapplicable noise that buries the applicable rules, so gate it
        // on the observed repeat (cp-2368/69/92 per-rule relevance-gating). The
        // Motor's mechanical talk-loop guards (multi-NPC + per-NPC egress)
        // backstop loop-breaking regardless. A render gate on the bot's OWN
        // emission history; the LLM still decides; no game knowledge.
        if (HasRecentRepeatedGoalOfKinds(events, "Talk "))
        sb.AppendLine("- NPC REPEAT EXHAUSTION — a repeating conversation is not progress: when `## Server hints` tags an NPC's dialog `repeated xN` (the count it shows) with N>=3, OR the recency note shows `recent Talk emissions: <that NPC> xN` with N>=3 (this ALSO covers a SILENT NPC that returns no dialog), OR the SAME NPC keeps producing recent lines that add no new item, hint, inventory, location, or change, that conversation is EXHAUSTED — re-`Talk`ing it will NOT advance anything even if it alternates 2-3 canned lines, so do NOT keep Talking just because the latest line looks new. PIVOT to a DIFFERENT verb/target: if the dialog named a concrete next action, follow it with a NON-Talk verb (`Use` the object it pointed at, `Give` a held item, `Pickup` visible loot, or `Talk` a DIFFERENT not-yet-talked NPC); else `Explore` away. Re-Talking the same NPC is NEVER how you follow an exhausted directive. If killable `monster`s are in view and no concrete non-Talk action is actionable, `Attack` one for XP instead — but only AFTER the conversation is exhausted; an NPC with genuinely NEW dialog still takes priority.");
        // Gate the SPEND XP rule on the shared unspent-XP render gate (the default
        // floor passes any positive value). A render gate on an observed fact; the
        // LLM still decides.
        if (unspentSpendSurfaced)
        sb.AppendLine("- SPEND XP is a FIRST-CLASS action, not an afterthought: investing unspent XP permanently improves your character, so whenever `## Self` shows unspent XP and it is safe to deliberate (you are not mid a losing fight and no `HOSTILE` is on you), weigh investing some BEFORE choosing an OPTIONAL combat/explore action — do not let XP sit unspent run after run. `## Self` shows `experience: N total, M unspent`. Unspent XP is wasted until invested. Verbs: `RaiseAttribute{target: {name: \"<attribute>\"}, amount: <positive whole XP>}` (names: strength, endurance, quickness, coordination, focus, self), `RaiseVital{target: {name: \"<vital>\"}, amount: <XP>}` (names: health, stamina, mana), or `RaiseSkill{target: {name: \"<skill>\"}, amount: <XP>}` (the target MUST be a skill NAME from the `trained skills` list in `## Self` — NEVER a weapon ITEM's name such as the weapon you wield; if `## Self` shows NO `trained skills` list, do NOT use `RaiseSkill` at all, raise an attribute instead — the server rejects anything else). A positive `amount` is REQUIRED: invest in FEWER, LARGER raises. When you choose to invest in a target, commit a SUBSTANTIAL chunk — up to your full unspent balance — in that ONE raise, NOT the same small amount (e.g. 10) repeated across many turns, because EACH raise costs a full decision turn you need for combat and quests. This governs raise SIZE, not WHICH target: keep choosing the target by the bottleneck evidence below, and you may raise a DIFFERENT target next time. Attribute effects are MECHANICS; the allocation is YOUR call and there is NO fixed build: strength PRIMARILY drives DAMAGE — how HARD your swings hit, on a swing that already LANDS — and is NOT your main accuracy lever, while COORDINATION and the TRAINED WEAPON SKILL you fight with are your PRIMARY ACCURACY levers (how OFTEN your swings LAND), the trained weapon skill being the biggest (often bigger than the coordination attribute), raised via `RaiseSkill` using a name from `trained skills` in `## Self`; quickness aids defense and missile play; focus and self power magic; endurance and health raise MAX HEALTH." + (stuckUnarmedForSpend ? " UNARMED EXCEPTION: the coordination-vs-strength accuracy guidance in this rule is for a WIELDED weapon; when you fight UNARMED (no weapon — `Combat readiness` says `UNARMED`) your fist `UnarmedCombat` to-hit is half STRENGTH + half coordination, so STRENGTH raises accuracy AND damage — favor STRENGTH for unarmed misses (see the `unarmed accuracy` note)." : "") + " Do NOT pour every point into ONE attribute — spread XP across the attributes your actual skills depend on, and read the bottleneck from evidence: if you die too fast, survivability (endurance/health) is the limit; if `current fight` shows hits `evaded` (your swings keep MISSING), the limit is ACCURACY — PRIORITIZE coordination and your trained weapon skill (your accuracy levers; the weapon skill only when it governs your WIELDED weapon — see the `wielded-weapon accuracy` note) over strength, whose main effect is damage; if your swings LAND but deal 0/low `damage`, the limit is strength; if you fight with spells, raise magic attributes/skills. E.g. raise coordination (and your trained weapon skill) when melee swings MISS/evade; raise strength when swings LAND but barely hurt; raise endurance/health when low max HP is killing you. These are NOT co-equal defaults you pick by current HP: if you SURVIVE your fights but still cannot kill — your swings keep missing or barely hurt, racking up `ineffective`/`near-death` outcomes (NOT `deaths`) with no `kills` — the binding limit is OFFENSE, not max HP, and adding more endurance/health only lets you LOSE the same unwinnable fights more slowly. Raise coordination (and your trained weapon skill via `RaiseSkill` ONLY when it governs your WIELDED weapon — see the `wielded-weapon accuracy` note) when your swings MISS, and strength when they LAND but barely hurt — so do not pour XP only into attributes and leave the weapon skill you fight with neglected — until you can kill the weak monsters you meet. (But if instead you are DYING fast — taking `deaths`, dropping in a hit or two — then max HP survivability IS the limit, or the monsters here are simply too strong and you should Explore to a weaker area.)" + (recentOwnDeathCount >= DeathSpiralMinDeaths ? " DEATH-SPIRAL EXCEPTION: if you have died REPEATEDLY in quick succession (see `## Survival caution`), max HP is NOT the fix — repeated deaths stack a penalty that lowers effective max HP and RESET your recovery each time, so raising it will not dig you out while the deaths continue; retreat to safer content and earn XP WITHOUT dying until the penalty burns off." : ""));
        sb.AppendLine("- TAPPED OUT means MOVE ON: a `tapped out` line in `Combat readiness` means you have NOT gained a level here for a while. Emit `Explore{target: {name: \"anywhere\"}}` to travel to a new area with monsters you can DEFEAT. Prefer a monster you can actually kill over a tougher one — XP comes from KILLS, and a monster that defeats you sets you back, so do NOT chase `tougher` monsters for more XP. (Looting a fresh corpse or an explicit server/quest directive still comes first.)");
        // cp-2369: the COMBAT SAFETY & PACE rule (~2KB, the single largest
        // bullet) is ENTIRELY about an in-progress or imminent fight — every
        // clause references the `monsters in view`/`nearest monster` count, the
        // `current fight` line, `recent inbound damage`, or the per-kind combat
        // history of a VISIBLE monster. With NO monster in view AND no active
        // fight it is inapplicable noise; gate it on combat-relevance to free
        // ~2KB of prompt budget in a non-combat scene (cp-2335 per-rule gating
        // pattern). Behaviour-preserving: it renders whenever combat is possible
        // (a monster is visible OR a fight is underway — `CurrentFight` persists
        // through a flee where the foe scrolled out of view). A render gate on
        // an observed fact; the LLM still decides; no game knowledge.
        if (monsterInView || world.CurrentFight is not null)
        sb.AppendLine("- COMBAT SAFETY & PACE: fight roughly one `monster` at a time — if several cluster or more than one is `HOSTILE`, back off and pull them singly (the `monsters in view` line counts them: `H actively HOSTILE` of 2+ means you are SWARMED — break away with `Explore`). Danger signals you have: your `deaths` count and, when shown, `health` in `## Self` (monster levels are NOT given — judge from OUTCOMES, not numbers). The `health` line shows BOTH a percentage AND absolute HP (e.g. `100% (1/1 HP, rising)`) — trust the ABSOLUTE HP: a handful of HP is lethal even at a high %, and `rising` means you are still regenerating BELOW full strength, so finish recovering before STARTING an OPTIONAL fight (a `HOSTILE` attacker still takes priority). The `recent inbound damage` line shows hits you have TAKEN and total damage in the last few seconds — if it climbs while your absolute `health` is low you are losing: disengage (`Explore`) or `Recall` rather than fight to 0 HP. The `current fight` line in `Combat readiness` shows swings `landed` vs `evaded`: many `evaded` with 0 `landed` (0 damage dealt) means that target out-defends you and you CANNOT win — DISENGAGE now (emit `Explore` to break away) and try a different, weaker, or more distant `monster`. The `current fight` line also shows the `target health now N%` and, once the fight has run, `(was M% when this fight began)`: if AFTER MANY swings the target's health is still barely below its fight-start value AND you are taking `recent inbound damage` or your own `health` is low or falling, the exchange is going AGAINST you even though some swings land — DISENGAGE (`Explore`) and pick a weaker `monster`. Early in a fight a small health drop is NORMAL, so judge from a SUSTAINED run of swings, not the first few; and if the target's health is steadily DROPPING toward 0 while your health holds, a SLOW fight is still winnable — KEEP attacking until it dies (slow is fine, losing is not). The `combat history` lines in `Combat readiness` are your own past outcomes per monster KIND this session — and each `monster` row in `Visible nearby` now carries its own `[your record: ...]` inline — before engaging a visible monster, read its inline record (or match its name in `combat history`): prefer a KIND you have `kills` against; AVOID a KIND with `deaths`/`near-deaths`/`ineffective` (you fought it but could not kill it — it out-defends you) and no `kills` (it has beaten you — pick a different, weaker monster or Explore on; but if you SURVIVE these fights (racking up `ineffective`/`near-death`, NOT `deaths`) yet have NO `kills` against ANY recent kind, a weaker target will not help — the limit is your OWN offense (out-DEFENDED — your swings keep missing)" + (unspentSpendSurfaced ? (stuckUnarmedForSpend ? ", so SPEND XP on STRENGTH or coordination — when UNARMED your fist to-hit is half STRENGTH so favor STRENGTH (it raises accuracy AND damage; see the `unarmed accuracy` note)" : ", so SPEND XP on coordination (and your trained weapon skill ONLY if it governs your wielded weapon — see the `wielded-weapon accuracy` note) instead") : "") + "). Likewise if `deaths` rises or `health` is low, disengage and AVOID re-attacking the same KIND of monster that just defeated you. Explicit server/quest directives and looting fresh corpses outrank optional combat; don't grind one spot forever.");
        // The corpse-looting rule only applies when a corpse is actually in
        // view (the `corpse` tag is rendered from the same IsCorpse projection
        // flag). With no corpse visible the rule references a target that isn't
        // there; omit it to save prompt bytes and unbury the applicable rules.
        if (world.Visible.Any(v => v.IsCorpse))
        sb.AppendLine("- Looting: a dead monster becomes a `corpse` (a container that DECAYS). `Use{target: name=\"<corpse>\"}` to open, then `Pickup{target: name=\"<item>\"}` items that appear. NEVER skip a fresh corpse to chase the next NPC — UNLESS an unacted leave/advance/turn-in directive is pending (see SERVER-INSTRUCTION PRECEDENCE / FINISH MULTI-STEP DIRECTIVES), which outranks optional looting.");
        // The chest-looting rule only applies when a chest is in view (same
        // IsChest projection flag that renders the `chest` tag). Omit otherwise.
        if (world.Visible.Any(v => v.IsChest))
        sb.AppendLine("- Loot containers: `chest`-tagged openables (Container + Openable, don't decay). `Use` to open, then `Pickup` contents. NEVER skip an unopened chest to chase the next NPC — UNLESS an unacted leave/advance/turn-in directive is pending (see SERVER-INSTRUCTION PRECEDENCE), which outranks optional looting.");
        // cp-2370: the Writables rule only applies when a sign or book is
        // visible (same IsSign/IsBook projection flags that render the `sign`/
        // `book` tags). Omit it otherwise (cp-2331 corpse/chest gating pattern)
        // to save prompt bytes and unbury the applicable rules.
        if (world.Visible.Any(v => v.IsSign || v.IsBook))
        sb.AppendLine("- Writables: a `sign` (stuck) is read in place with `Use{target: name=\"<sign>\"}`; a `book` (not stuck) is `Pickup`-able — prefer Pickup.");
        // cp-2401: the main LOOP-BREAK rule (~1.1KB) is the action-repeat dead-end
        // guard — its three sub-cases all describe REPEATING an action with no
        // change: (a) Talk{X} 3+ times, (b) re-Use an unconsumed inventory item,
        // (c) Use the SAME world object 3+ times. It is only actionable once such
        // a repeat is forming, so gate it on an observed Talk-OR-Use goal repeat
        // (cp-2400 helper, extended to both verbs). All three sub-cases are
        // Motor-backstopped regardless of the prompt: cp-2344 talk-loop egress
        // (a), the policy mechanically drops an unconsumed-inventory re-Use (b,
        // stated in the rule itself), and the cp-2354/2359 world-object Use churn
        // guard (c) — so gating the ADVISORY rule is behaviour-preserving and
        // frees ~1.1KB in the common no-repeat case. cp-2368/69/92/2400 per-rule
        // relevance-gating; a render gate on the bot's OWN emission history, no
        // game knowledge. (The separate LOOP-BREAK town-stuck rule below stays
        // ungated — it keys on dwell time + no-monsters, not an action repeat.)
        if (HasRecentRepeatedGoalOfKinds(events, "Talk ", "Use "))
        sb.AppendLine("- LOOP-BREAK — do not repeat an action that produced no change (see the `Location & recency` section): (a) Talk: if you emitted `Talk{X}` 3+ times in the last 10 emissions with no new item and no new server hint, talk to a different NPC or Use/Give/Explore. (b) inventory-USE: if `Recently used inventory items` lists an item as `still in inventory (not consumed)`, the policy WILL drop a Use against it — do not re-emit unless a new event (ActionRejected recovery hint, new dialog/hint, inventory change) justifies it; when broken, pick a DIFFERENT action (a `monster` in view + weapon wielded → `Attack`; a not-yet-talked visible NPC → `Talk`; a visible pickup item → `Pickup`; else `Explore`). (c) world-object USE: the `Location & recency` section lists `recent Use emissions` per target; 3+ Uses of the SAME target with no change (same landblock per `minutes in current landblock`, no new hint/item) is a dead end → `Explore{target: {name: \"anywhere\"}}` or pick a different target. Re-Use ONLY if something concrete changed (you crossed into a new landblock, a new hint/item appeared, or an `ActionRejected` told you to retry).");
        // PASSAGE-OPENED stays UNGATED: it is relevant after the bot has USED a
        // door (a temporal/behavioural signal in `recent Use emissions`), not
        // only when a door is currently in `Visible nearby` — a door can scroll
        // out of view between the Use and the next decision. Visibility-gating
        // it would wrongly drop the rule mid door-Use loop, so it is left
        // always-on.
        sb.AppendLine("- PASSAGE-OPENED is not progress: opening a `door` (or any non-container `openable`) does NOT move you. Only MOVING to a new area counts (current cell/landblock changing, or previously-unseen objects in `Visible nearby`). After Using a door once, do NOT Use it again from the same spot — emit `Explore{target: {name: \"anywhere\"}}` (or a goal beyond it) to travel THROUGH it. (Does NOT apply to `chest`/`corpse` containers, which you Use to reveal loot then `Pickup`.)");
        // cp-2368: the next three rules are ENTIRELY about the NO-monster-in-
        // view case — LOOP-BREAK (town-stuck) requires `nearest monster: (none
        // in view)`, and HUNT EXCURSION / STEER A BARREN EXCURSION are about
        // travelling OUT to FIND monsters when NONE are visible. When a monster
        // is already in view they are inapplicable noise that buries the rules
        // that DO apply and waste ~2.5KB of prompt budget in a combat scene
        // (the cp-2335 per-rule gating pattern). Behaviour-preserving: each
        // still renders identically whenever no monster is in view.
        if (!monsterInView)
        sb.AppendLine("- LOOP-BREAK (town-stuck): if `minutes in current landblock` > 5 AND `nearest monster: (none in view)` AND every visible creature is `npc` (no `monster` tag anywhere) AND `untalked npcs in view: 0`, you are STUCK in a town — emit `Explore{target: {name: \"anywhere\"}}` immediately. The picker walks you through visible Doors/portals to new areas. UNLESS `untalked npcs in view` is above 0 — first `Talk` each untalked `npc` ONCE (a task-giver looks like any other npc; you only know by talking) to check for a task; only Explore away once `untalked npcs in view: 0`. This OVERRIDES RE-talking ALREADY-talked NPCs — talking the SAME npc again is still not progress with no monsters in view. BUT canvassing is BOUNDED: once `minutes in current landblock` is more than ~10, Explore away to hunt EVEN IF some untalked `npc`s remain — a large town has far more npcs than are worth talking, and leaving to find `monster`s you can fight to LEVEL outranks talking every last townsperson when no task is locally actionable. You can return later; hunting/leveling is the productive fallback, and endlessly Talking fresh townsfolk while `minutes in current landblock` keeps climbing is the exact stuck-in-town loop this rule exists to break.");
        if (!monsterInView)
        sb.AppendLine("- HUNT EXCURSION (leave a tapped-out safe zone to find monsters): monsters do NOT spawn in safe zones — you must travel OUT to surrounding open country. When combat-ready, NO `monster` anywhere in `Visible nearby`, NO un-acted server/quest directive naming a specific next target (re-talking an NPC with no NEW dialog and browsing vendors do NOT count), AND `minutes in current landblock` is more than a few with local progress dried up (no new level, quest item, or unique hint), the zone is TAPPED OUT. Emit `Explore{target: {name: \"anywhere\"}}` — crossing out takes MANY ticks, so KEEP emitting it every cycle (your own recent `Explore` does NOT mean the excursion is done; do NOT revert to talking the same town NPCs mid-excursion) until your `landblock` actually changes OR a `monster` appears (then `Attack` it). A NEW server/quest directive, quest item, danger, or fresh dialog step interrupts the hunt — act on it. Quest progress outranks an optional hunt.");
        if (!monsterInView)
        sb.AppendLine("- STEER A BARREN EXCURSION: `Explore` accepts an OPTIONAL `direction` — one of `north`, `northeast`, `east`, `southeast`, `south`, `southwest`, `west`, `northwest` — that biases WHICH way the excursion heads (the motor walks roughly that bearing toward unexplored ground). Plain `Explore{target: {name: \"anywhere\"}}` wanders UNDIRECTED, which can keep drifting the SAME way and re-cover empty country. So if a hunt excursion has already crossed SEVERAL `landblock`s (watch `minutes in current landblock` resetting and your own repeated recent `Explore`s) and STILL no `monster` has appeared, that bearing is barren — emit `Explore{target: {name: \"anywhere\"}, direction: \"<a DIFFERENT or opposite compass heading>\"}` to search NEW country instead of drifting the same way. Vary the heading across excursions until a `monster` appears (then `Attack` it). You have NO map — you are choosing a SEARCH direction to try, not a known monster location; `direction` is optional (when set, the bot COMMITS to that heading and travels it), so an undirected `Explore` still works when you have no reason to prefer a bearing.");
        if (!monsterInView)
        sb.AppendLine("- SEEK A KILL-TASK, don't just grind: hunting levels you, but a kill-task from a quest-giver is DIRECTED progression — and quest-givers stand in TOWNS/settlements, not open monster country. So when you have NO active quest/directive and have wandered empty country a while with none in hand, WEIGH heading back toward a town or NPC cluster you have ALREADY passed (`Explore` with a `direction` bearing toward it, then `Talk` its NPCs to look for a task) instead of drifting further out into empty country. `Talk` any NPC you pass to check whether it offers a task. (A winnable `monster` in view still comes first — this is for when you would otherwise just keep wandering.)");
        // cp-2402: the BLOCKED-targets rule (~370 chars) only tells the LLM how
        // to react to an ActionRejected `Blocked`/`Unreachable` (server physics
        // held the bot against geometry). It is inapplicable noise without such
        // a rejection, so gate it on one (cp-2368/69/92/2400/2401 per-rule
        // relevance-gating). The Motor's nav already routes around blocked
        // geometry mechanically, so the advisory rule is behaviour-preserving.
        // A render gate on the bot's OWN rejection events; no game knowledge.
        // cp-2408: the STUCK ESCAPE (Recall last-resort) rule is paired in here
        // too — it is ALSO only actionable when the bot is physically held
        // against geometry (the same Blocked/Unreachable signal: "the server
        // held you at the same position across repeated attempts"). Gating both
        // frees ~600 bytes when the bot moves freely; Recall stays an LLM
        // decision (the Motor never auto-Recalls).
        if (HasRecentBlockedRejection(events))
        {
            sb.AppendLine("- BLOCKED targets: `ActionRejected` label `Blocked`/`Unreachable` = server physics held the bot against geometry (wall, closed door, barrier). Do NOT re-emit the same target. Prefer a visible Door (walk to / Use it — it likely leads where you were going); else `Explore` to route around. The bot cannot clip through obstacles.");
            sb.AppendLine("- STUCK ESCAPE (last resort): `Recall{}` teleports you to your attuned lifestone. Use it ONLY when you are physically unable to move at all — e.g. the movement report (when shown) says the server held you at the same position across repeated attempts AND no visible Door or `Explore` route frees you (a ledge/cliff with your target far BELOW is a classic trap: every step is mid-air and rejected). It requires an attuned lifestone (Use a `Life Stone` to attune); the server refuses it inside the training academy and right after PvP, and it costs half your mana — so it is an escape hatch, NOT routine travel. Try a Door or `Explore` first; reach for `Recall` only when those cannot move you.");
        }
        // cp-2346 — does the recent event window carry server/NPC text the LLM
        // might need to compile (a task directive)? Pure presence check on the
        // same dialog kinds the `## Server hints` section renders; gates the
        // QUEST-DIALOG COMPILER rule + the `## Recent directive check` capsule
        // so they cost zero prompt bytes when no dialog is present.
        // cp2907 follow-on: also true when a server line survives ONLY in the
        // durable `## System messages` store after the 256-event ring evicted it
        // — but ONLY while that line is still RECENT, so a long-lived non-actionable
        // banner (e.g. the login greeting, which has no TTL in the durable store)
        // does not pin these rules on for the whole session and bloat the
        // at-ceiling prompt. Recency is measured against the newest event the
        // stream has seen (clock-independent, deterministic), not wall time. After
        // the window closes the heavy COMPILER nudge stops, but the line is STILL
        // SHOWN in `## System messages` and the always-on SERVER-INSTRUCTION
        // PRECEDENCE (leave/advance/proceed) and Read-hints (Use/double-click)
        // rules — which also cite `## System messages` — still cover those
        // directive classes; a compiled Intent also persists on the stack. So the
        // bound trims the large rule's per-tick cost without hiding the line (a
        // stale line outside those covered classes simply relies on the LLM acting
        // on the still-shown text). Pure presence + age check; no text parsing.
        const double ServerTextActionableSeconds = 300.0;
        var streamNowUtc = events.Recent(1).FirstOrDefault()?.Utc;
        var hasRecentServerDialog = events.Recent(EventStream.DefaultCapacity).Any(e =>
            (e.Kind == EventKind.NpcDialog || e.Kind == EventKind.ServerMessage || e.Kind == EventKind.PopupString)
            && !string.IsNullOrEmpty(e.Text))
            || (streamNowUtc is { } nowUtc && events.RecentServerMessages().Any(e =>
                (nowUtc - e.Utc).TotalSeconds <= ServerTextActionableSeconds));
        if (stack is not null)
        {
            sb.AppendLine("- STRATEGIC STACK: `## Intent stack` is the current plan; TOP is the active sub-goal, ancestors paused. Per-cycle goals advance TOP. PUSH on a discovered sub-task; POP_TOP when done and no predicate caught it (rare — predicates auto-pop); REPLACE_TOP when right-frame-wrong-target; MARK_TOP_BLOCKED when stuck. Always echo `stack_revision`.");
            sb.AppendLine("- COMPLETION PREDICATES: pick the typed predicate matching your termination criterion (the discriminator field is `type`, e.g. `{\"type\":\"kill_count_total_at_least\",\"count\":3}`); prefer server-authoritative (num_deaths, coin_value). *_total_* for absolute thresholds, *_since_push_* for deltas. A hunt excursion completes when a monster is finally in view: `{\"type\":\"visible_tag\",\"tag\":\"monster\"}` (set `deadline_seconds` too, as a liveness backstop). If none fits, `{\"type\":\"always_false\"}` + `predicate_request`.");
            // PERSIST A HUNT EXCURSION is about STARTING/maintaining an excursion
            // to FIND monsters; it is moot once a monster is already in view (the
            // cp-2368 monster-in-view gating set, which already gates the HUNT
            // EXCURSION / LOOP-BREAK-town-stuck / STEER-A-BARREN-EXCURSION rules
            // on !monsterInView). Gating it here completes that set and frees
            // ~1KB of the static preamble in combat scenes — exactly where the
            // dynamic perception sections (## Combat readiness/## Inventory/##
            // Visible nearby) are hard-cut under the 26000 ceiling. The MECHANICAL
            // auto-pop of a hunt-excursion intent the instant a monster appears is
            // unchanged (predicate-driven, not prompt-driven), and the completion
            // semantics remain stated in COMPLETION PREDICATES above, so this is
            // behavior-preserving when a monster is in view.
            if (!monsterInView)
                sb.AppendLine("- PERSIST A HUNT EXCURSION ON THE STACK: when you begin a hunt excursion (per the HUNT EXCURSION rule) and TOP is not already one, in the SAME response PUSH an intent — `kind`:\"hunt-excursion\", completion `{\"type\":\"visible_tag\",\"tag\":\"monster\"}`, and set `deadline_seconds` to a few minutes — alongside your `Explore`. The completion auto-pops the intent the instant a `monster` appears (then `Attack` it); the deadline is just a backstop so a wedged excursion eventually re-deliberates. While it stays TOP you are EN ROUTE: keep emitting `Explore{target: {name: \"anywhere\"}}`. Merely crossing into a new `landblock` is NOT done — if the new area is ALSO a monster-free town, the excursion stays TOP, so keep exploring OUT (do not start working town objects again). The ONLY things that interrupt are a genuinely NEW server/quest directive, a newly-acquired quest item, or danger — stale NPCs, vendors, and already-seen doors are NOT interrupts. This makes the excursion survive across ticks instead of being re-decided (and abandoned) every cycle.");
            if (hasRecentServerDialog || world.Contracts.Count > 0)
                sb.AppendLine("- QUEST-DIALOG COMPILER: if observed NPC/server text (see `## Server hints`, `## Early server directives`, and `## System messages`) — OR a contract you ALREADY hold (`## Contracts`, via its `objective`, including one accepted earlier with no new dialog this run) — ASSIGNS a task — names target creature(s) to kill, an item to fetch, or a place to reach — compile it onto the stack BEFORE optional grinding: PUSH (or REPLACE_TOP if TOP is a generic hunt-excursion) an intent whose `kind`/`target_name`/`rationale` COPY the named target(s) verbatim FROM that observed text or the contract's `objective`, and prefer `Attack` only against visible monsters whose names match those target words (unrelated monsters are optional fallback, NOT quest progress). Use a `kill_count_*` completion ONLY when the required count is explicitly stated in the observed text or the contract's `objective` — NEVER invent a count; if no predicate fits, use `{\"type\":\"always_false\"}` + `predicate_request`, and set `deadline_seconds` as a liveness backstop so an unreachable/unfound target eventually re-deliberates. When observed progress/dialog indicates the task is done — OR the held contract's `stage` shows 3 (done) in `## Contracts` — RETURN to the task-giver / the contract's `turn-in NPC` and `Talk`/`Use` it to turn in (`Explore` toward the contract's `turn-in location` bearing if that NPC is not in `Visible nearby`). So a `kill_count_*` auto-pop does not lose the turn-in: PUSH a `return-to-giver` intent FIRST with the kill/fetch intent ON TOP (the turn-in resurfaces as TOP when the top pops), OR record the task-giver and turn-in step in the kill/fetch intent's `rationale` (with the copied target; recent-history `rationale` is preserved after a pop). Turn in when the task shows done — observed dialog says so, the task intent shows Completed in recent history, OR the contract's `stage` is 3 — by returning to the task-giver / `turn-in NPC` and `Talk`/`Use`-ing it. Attempt that hand-in ONCE — counting ONLY a turn-in `Talk`/`Use` made AFTER the contract reached `stage` 3, NOT an earlier acceptance, greeting, or objective interaction with that same NPC (so a contract that really does clear on a final hand-in still gets its one real attempt). If that one post-`stage`-3 attempt leaves the contract STILL at `stage` 3 (unchanged), it needs no further action — some tasks have no separate hand-in (completion itself is the done state, and any batch reward is the issuer's to grant on its own terms). Do NOT just `POP_TOP` and walk away: the stateless rule above would re-compile the same turn-in once that `Talk` ages out of recent history, marching you back forever. Instead `MARK_TOP_BLOCKED` the turn-in intent with a `rationale` like \"stage-3 done; awaiting batch reward\" so it PERSISTS on the stack as a durable marker; if `## Intent stack` ALREADY shows such a blocked marker for this contract, you have handled it — do NOT re-compile it — leave it blocked and spend the turn on other directed progress (accept or complete ANOTHER task). Re-attempting an UNCHANGED `stage`-3 turn-in is fixation, not progress. Greetings, lore, vendor flavor, and descriptions with no requested action are NOT tasks — do NOT invent a task, target, count, NPC, or location.");
                            // GRIND-AS-KILL-COUNT (reduce-llm-call-volume): only useful once a
            // monster is in view (a combat scene), so gate on monsterInView (the
            // complement of the hunt-excursion gate above; cp-2368 per-rule
            // relevance gating). This teaches the intent VOCABULARY for an
            // already-made decision — the LLM still decides WHETHER to grind and
            // WHICH kind (from its own combat-feel record); the rule only says
            // "express a winning grind as a typed kill-count intent so the Motor
            // executes the repeats". That lets the cp-2426 autonomous
            // decomposition fire (the Motor mints the next Attack toward the
            // count without a per-monster LLM round-trip), which is the whole
            // point of reduce-llm-call-volume. No NPC/wcid/landblock; the kind
            // name is copied from observed perception exactly like the
            // QUEST-DIALOG COMPILER copies a named target.
            if (monsterInView)
                sb.AppendLine("- COMMIT A WINNING GRIND AS A KILL-COUNT INTENT: when you decide to keep `Attack`ing a monster KIND you are ALREADY winning against (its inline `[your record: ...]` or `combat history` shows `kills` and no fresh `deaths`/`ineffective`) and there is NO un-acted quest/server directive, PUSH an intent in the SAME response as your `Attack` — `kind`:\"hunt\", `target_name` the kind's name — and pick the completion that matches your aim: `{\"type\":\"kill_count_since_push_at_least\",\"count\":<a few>,\"name_contains\":\"<the kind's name>\"}` to commit to THAT kind (the bot then attacks only monsters whose name matches), OR `{\"type\":\"kill_count_total_at_least\",\"count\":<n>}` to count kills of ANY winnable kind toward a running session total (the bot attacks the nearest winnable monster of any kind). Set `deadline_seconds`. While that intent stays TOP the bot keeps `Attack`ing the nearest matching, in-view, not-`beaten` monster toward the count ON ITS OWN — you are NOT re-asked each kill, so you only re-decide when the count is met, the matching kind stops being winnable or leaves your view, a different decision-worthy event occurs (loot, dialog, danger), the bot re-checks on its own after a few autonomous attacks, or the deadline fires. Push this ONLY while you are winning — never for a kind whose record shows `deaths`/`ineffective` and no `kills`. A quest/server directive ALWAYS outranks a grind (compile it per the QUEST-DIALOG COMPILER rule first), and don't grind one spot forever — when a count completes, weigh moving on or seeking a kill-task.");
        }
        // The AUTONOMOUS PICKER rule only makes sense when the
        // `## Autonomous picker activity` section is present (same gate the
        // section itself uses below). Rendering it otherwise wastes prompt
        // bytes and buries the rules that DO apply. No game knowledge: a render
        // gate on whether the picker is currently active.
        if (pickerActivity is not null)
        sb.AppendLine("- AUTONOMOUS PICKER: when `## Autonomous picker activity` is present, the schema-only picker auto-drives WHERE TO WALK (nearest eligible candidate by distance) because you had no goal that tick; it OWNS NO VERBS. On arrival the motor sends NOTHING unless your Goal's Kind names a verb (`Use`/`Talk`/`Pickup`/`Attack`/`Give`). `picker has ARRIVED at target X` = parked next to X awaiting a verb — emit `Use`/`Talk`/`Pickup`/`Attack{target: name=\"X\"}`, or `Explore{target: name=\"<other>\"}` to redirect. Doing nothing parks ~2s then picks the next candidate.");
        // cp-2409: the TRANSITIONS (door/portal Use) and CLOSED DOORS rules are
        // only actionable when a door/portal is actually in view — the IsDoor/
        // IsPortal projection bits that render the `door`/`portal` row tags in
        // `Visible nearby`. With none present they reference objects that are
        // not there, so gate them on visibility (cp-2331 corpse/chest gating
        // pattern) to free prompt budget. A render gate on observed wire facts;
        // the LLM still decides; no game knowledge.
        var portalInView = world.Visible.Any(v => v.IsPortal);
        var doorInView = world.Visible.Any(v => v.IsDoor);
        // cp kill-task-source-nudge: a `vendor`-tagged object OR an un-talked
        // dialog `npc` is in view. The combat-only contract-refresh nudge below
        // recognizes BOTH because a kill-task source is not required to carry the
        // wire ObjectDescriptionFlag.Vendor bit — an un-talked dialog npc is a
        // source the LLM reaches by `Talk`. The no-monster LOOP-BREAK rule already
        // drives Talking un-talked npcs to find a task; this only extends the SAME
        // un-talked-npc fact into the monster-in-view case LOOP-BREAK is gated out
        // of. Gate on observed wire facts only (the IsVendor bit / the existing
        // untalked-npc projection), so it costs zero budget when no such object is
        // around. Talked-set membership keeps it to one Talk per npc.
        var vendorInView = world.Visible.Any(v => v.IsVendor);
        // Exclude vendors from the npc arm (excludeVendors:true): a vendor that is
        // ALSO a creature is handled by the vendor arm above, which an open panel
        // suppresses — counting it here too would let an open-panel vendor-creature
        // re-trigger the nudge through the npc arm, defeating that suppression.
        var untalkedNpcInView =
            CountUntalkedNpcsInView(world, talkedNpcGuids, talkedNpcNames, excludeVendors: true) > 0;
        if (doorInView || portalInView)
        sb.AppendLine("- TRANSITIONS — doors and portals: `door`/`portal`-tagged objects are activated with `Use{target: name=\"<name>\"}` (the picker never auto-opens them). When parked at a door/portal with no better verb, `Use` it — that's how the bot moves between rooms/buildings/landblocks. If a door rejects Use as Locked and you hold an item whose `short_desc`/name says key, retry `Use{target: name=\"<door>\", item: name=\"<key>\"}`.");
        // The EXPLORATION CANDIDATES rule only applies when that section is
        // present (same gate the section uses below). Omit it otherwise.
        if (explorationCandidates is not null && explorationCandidates.Count > 0)
        sb.AppendLine("- EXPLORATION CANDIDATES: when `## Exploration candidates` is present, the in-range queue is empty and the fallback walks to the nearest off-screen object; the TOP entry is the default. Each line shows `kind=mob|npc|object` (raw perception; `object`=non-creature). To pick a DIFFERENT one (e.g. an off-screen `mob` to hunt, backtrack through a visited door, or skip a distant pickup for a closer visited NPC), emit `Explore{target: {guid: \"0x...\"}}` (guid is the most reliable selector) or `{name: \"...\"}`.");
        sb.AppendLine("- PURSUE UNSEEN OBJECTIVES: when dialog or a hint tells you to find/reach/talk-to someone NOT in `Visible nearby` (e.g. \"talk to the trainer in the next room\", \"find the captain\"), emit a goal NAMING it — `Talk`/`Give`/`Explore{target: {name: \"<role-or-name>\"}}` — even though it is not yet visible; the bot walks through rooms to discover it. If that dialog/hint ALSO states a COMPASS DIRECTION toward the unseen target (one of north/northeast/east/southeast/south/southwest/west/northwest, e.g. \"directly south\", \"to the north\", \"head west\"), copy that stated compass bearing into an `Explore` goal's `direction` field — `Explore{target: {name: \"<role-or-name>\"}, direction: \"south\"}` — so the bot COMMITS toward that bearing and travels there instead of wandering off; use ONLY a compass bearing the text actually gives (a non-compass relative phrase like \"to the right\" or \"past the gate\" is NOT a valid `direction` — omit `direction` then and just NAME the target), never a guessed one. A role phrase (\"the guard\", \"the trainer\") is a valid target name when no proper name is given. Do NOT keep re-talking an NPC whose dialog you already got — pursue the objective that dialog gave you. THE TARGET MAY ALREADY BE IN VIEW: the MOMENT an `npc` or object whose NAME (or role) matches one a directive/dialog told you to reach — to leave/skip/advance, turn in or use an item, or get a reward — appears in `Visible nearby`, that IS the target it meant, EVEN IF the text placed it elsewhere (\"in the next room\", \"find the X\"): `Talk`/`Give`/`Use` it NOW (`Explore` only WALKS you toward a target and NEVER interacts — no talk/give/use happens when an `Explore` arrives — so once the target is in view you MUST switch to `Talk`/`Give`/`Use` on it; do not keep `Explore`-ing a target you have already reached). Do NOT grind monsters, loot, or work other objects past it — a named directive target coming into view is the whole point of the search, and reaching it OUTRANKS optional local activity. With no named objective and nothing useful visible, emit `Explore{target: {name: \"anywhere\"}}`.");
        if (doorInView)
        sb.AppendLine("- CLOSED DOORS ARE BARRIERS: a `door closed` row in `Visible nearby` is shut and blocks the rooms beyond it. If you are pursuing a target you cannot see or reach (e.g. the search-progress note says a named target is still not visible after several moves) and a `door closed` is nearby, `Use{target: name=\"<door>\"}` to open it, THEN `Explore` to travel through — a closed door is the usual reason the next room's occupant never appears. A `door open` row is already passable (just `Explore` through it); do not Use it again.");
        // The task-seeking rules above (SEEK A KILL-TASK, talk-NPCs-for-tasks,
        // HUNT EXCURSION) are gated on !monsterInView, so a monster in a town
        // SUPPRESSES them and the bot grinds past a contract vendor / task-giver.
        // This nudge covers that gap AND the no-monster case where the bot holds
        // a FINISHED batch (every tracked contract DONE, stage 3) and a fresh
        // source is the only way to keep earning — there the !monsterInView
        // LOOP-BREAK rule would otherwise gate re-engaging a source already in
        // view behind its >5min town-dwell timer. It fires when an unbrowsed
        // vendor (no vendor panel open) OR an un-talked npc is visible, no
        // ACTIONABLE contract is tracked (none held, OR every tracked contract is
        // DONE), AND EITHER a monster is in view (the suppressed-by-combat case)
        // OR a held batch is entirely done (the no-monster refresh case). The
        // no-monster arm requires a HELD finished batch — NOT merely zero
        // contracts — so a fresh character with no batch does not canvass every
        // townsperson (that stays owned by LOOP-BREAK / HUNT EXCURSION). The
        // vendor arm needs `world.Vendor is null` (an OPEN panel already shows the
        // wares in `## Vendor offerings`); the npc arm does not — an un-talked
        // task-giver is worth a Talk whether or not some other vendor panel is
        // open. Zero budget otherwise; the LLM still decides (a winnable monster
        // may come first). Stage is wire ContractStage data; no source-side
        // decision to buy or pursue.
        var noActionableContract =
            world.Contracts.Count == 0 || world.Contracts.All(c => c.Stage == 3u);
        var heldBatchAllDone =
            world.Contracts.Count > 0 && world.Contracts.All(c => c.Stage == 3u);
        // Recognize the already-engaged source that issued a now-finished batch (a
        // known broker in view that can hand out a FRESH batch) — the case FIND's
        // un-talked/unbrowsed disjuncts miss. Bounded; see DoneBatchSourceInViewToRefresh.
        var doneBatchSourceToRefresh = DoneBatchSourceInViewToRefresh(world, events);
        // Combat-readiness gate (cp gate-contract-cues-unarmed): an UNARMED bot
        // cannot complete a kill-task, so pursuing the contract cycle here competes
        // with the SELF-ARM loot-to-arm hunt — the prompt's stated TOP priority when
        // unarmed. Suppress this contract-pursuit nudge until the bot is combat-
        // effective; once armed it re-enables. Render gate on the combat-readiness
        // wire fact (mirrors the SELF-ARM rule's own `!selfArmCombatEffective` gate);
        // no game knowledge.
        if (((vendorInView && world.Vendor is null) || untalkedNpcInView || doneBatchSourceToRefresh)
            && noActionableContract
            && (monsterInView || heldBatchAllDone)
            && selfArmCombatEffective)
        {
            // The finished-batch refresh action depends on panel state: when a
            // vendor's trade panel is ALREADY open (world.Vendor set), the
            // productive way to refresh at a vendor source is to Buy a contract
            // from its already-shown offerings — a fresh Use only re-opens the open
            // panel. Route the refresh wording by panel state so the rule never
            // tells the bot to re-Use an already-open vendor (which would
            // contradict the open-panel `## Vendor offerings` cue).
            var refreshSourceAction = world.Vendor is null
                ? "re-engaging THAT specific source — `Use` it if it is a `vendor`, `Talk` it if it is a dialog `npc`"
                : "re-engaging THAT specific source — if it is the `vendor` whose panel you ALREADY have open, `Buy` a contract from its `## Vendor offerings` (a fresh `Use` only re-opens the open panel); `Use` a different in-view `vendor` whose panel is not open, or `Talk` it if it is a dialog `npc`";
            sb.AppendLine("- FIND A KILL-TASK SOURCE (vendor or task-giver npc): a `vendor`-tagged object OR a dialog `npc` in `Visible nearby` may offer task contracts — a kill-task you accept, complete, and turn in for a reward. You CANNOT see what a `vendor` offers until you `Use` it to reveal its wares in `## Vendor offerings`; a task-giver looks like any other `npc` — you only learn it offers a task by `Talk`ing it. When you hold NO actionable tracked contract — `## Contracts` is empty/absent, OR every tracked contract is DONE (stage 3, so the current batch is finished and you need a fresh one to keep earning) — and an unbrowsed `vendor` OR an un-talked `npc` is in view, it is worth ONE `Use{target: name=\"<vendor>\"}` on a vendor (or ONE `Talk{target: name=\"<npc>\"}` on an un-talked npc) to check for a task — DIRECTED progression that OUTRANKS open monster-grinding for XP. So with NO actionable tracked contract, `Talk` each un-talked `npc` in view ONCE (an npc whose quoted `role`/title is shown — e.g. a faction role — is especially likely to give a quest, directions, or the way forward) BEFORE grinding monsters for XP: open grinding is the FALLBACK once every nearby un-talked `npc` has been Talked and no directive remains. Checking is not itself quest progress, and you still decide whether anything offered is worth pursuing (a `HOSTILE` attacker on you, low `health`, or an explicit server/quest directive still takes priority over an optional npc check). Talk each un-talked npc only ONCE — re-talking an already-talked npc is not progress. ONE EXCEPTION — REFRESH A FINISHED BATCH: when every tracked contract is DONE (stage 3, a finished batch) and the `npc`/`vendor` your batch CAME FROM is in view (its `start NPC`/`turn-in NPC` shown in `## Contracts` matches a `Visible nearby` name), " + refreshSourceAction + " — to request a FRESH batch IS progress, EVEN though you have engaged it before: a finished batch has earned its reward and lets you take new work. This is NOT re-handing-in the settled contracts (those need no further hand-in) — it is asking for a NEW batch to keep earning, which OUTRANKS grinding monsters. If re-engaging that source brings no new contract after a try or two, it is tapped — move on and hunt/explore.");
        }
        // RETURN-TO-A-CONTRACT-SOURCE nudge (cp035): the FIND-A-KILL-TASK-SOURCE
        // rule above only fires when a source is IN VIEW. Live cp034-diag: holding
        // a FINISHED batch, the bot drifted into open country with NO npc/source in
        // view (nearestNpc=none, the issuing source never even loaded) and ground
        // monsters with no navigational pull back to a source, so the contract
        // cycle never refreshed. When the batch is all done, NO source is in view,
        // AND a contract carries a dat location (already rendered as a bearing in
        // ## Contracts), surface the option to TRAVEL back toward that area to find
        // a fresh source. Navigation only (Explore toward the bearing's compass),
        // never a re-Talk of a settled turn-in NPC; the LLM decides, and safety /
        // an active directive still outrank. Raw contract dat facts + own
        // perception; no object-type priority, no source-side decision to pursue.
        // A done-batch issuer standing in view (even already-talked, and even after
        // its refresh bound is spent) is a contract source PHYSICALLY in view — so it
        // must count as "source in view" here, or this travel-back nudge would fire
        // "NO contract source is in Visible nearby" and "do NOT re-Talk the turn-in
        // NPC" at the SAME time FIND-A-KILL-TASK-SOURCE's REFRESH exception nudges
        // re-engaging that very NPC (a direct contradiction for a broker whose issuer
        // == turn-in NPC). Use the UNBOUNDED issuer-in-view check (not the bounded
        // refresh predicate) so RETURN stays off whenever the source is present.
        var noContractSourceInView =
            !vendorInView && !untalkedNpcInView && !DoneBatchIssuerInView(world);
        // A bearing is only RENDERED in ## Contracts (and thus copyable into an
        // Explore `direction` via the contracts travel instruction) when the bot's
        // OWN position is known AND a contract carries a dat location. Gate on the
        // RENDERED-bearing condition, not raw coords in memory, so this nudge never
        // points the LLM at a bearing it cannot see. Check the FIRST tracked
        // contract specifically: the capsule emits rows in wire order and its
        // ContractsProtectedCharBudget can DROP a later row, but the first row is
        // ALWAYS emitted — so a first-contract bearing is GUARANTEED to render
        // (no budget-drop false-positive). Conservative: if only a later (possibly
        // budget-dropped) contract carries coords the nudge stays off, which is
        // safe — a done batch's contracts share a source area, so the first row's
        // bearing is the relevant one.
        var firstContract = world.Contracts.Count > 0 ? world.Contracts[0] : null;
        var selfPositionKnown = world.Self.CellId is not null;
        var aContractBearingRenders = selfPositionKnown
            && firstContract is { } fc
            && ((fc.TurnInWorldX is not null && fc.TurnInWorldY is not null)
                || (fc.QuestAreaWorldX is not null && fc.QuestAreaWorldY is not null));
        // cp037: a contract can carry NO dat location — its objective names a
        // turn-in / start NPC but has no coords — so aContractBearingRenders is
        // false and the bearing branch below stays dormant for it. The contract's
        // turn-in / start NPC NAME is still a navigate-back ANCHOR: that NPC stands
        // in the populated source area, and the PURSUE UNSEEN OBJECTIVES machinery
        // already lets the bot Explore toward a NAMED not-yet-visible target. Gate
        // on the FIRST contract's RENDERED name (the capsule emits "turn-in NPC:" /
        // "start NPC:" for the first row regardless of coords; mirrors the
        // conservative first-row bearing gate). Require self-position known for the
        // SAME reason the bearing branch does — the motor cannot travel toward a
        // target when the bot does not know where it is. Also require that NO contract
        // row the ## Contracts capsule actually RENDERS shows a bearing
        // (AnyRenderedContractBearing mirrors the capsule's budget pass exactly): a
        // precise compass bearing is a better anchor than a name, and the name-branch
        // text asserts "no contract shows a travel bearing" — so the name branch must
        // only speak when that is true of what the LLM actually sees. Gating on
        // RENDERED bearings (not raw coords) also fires correctly when a later
        // contract HAS coords but its row is budget-dropped (no bearing visible).
        // Mutually exclusive with the bearing branch. Own contract projection + own
        // perception; no object-type priority, no source-side decision, no game
        // content in source.
        var anyRenderedContractBearing = AnyRenderedContractBearing(world, events);
        var aContractTurnInNameRenders = selfPositionKnown
            && !anyRenderedContractBearing
            && firstContract is { } fcn
            && (OneLine(fcn.NpcEnd) is not null || OneLine(fcn.NpcStart) is not null);
        // Same combat-readiness gate as FIND-A-KILL-TASK-SOURCE: when UNARMED,
        // traveling back to a contract source competes with the SELF-ARM loot-to-arm
        // hunt — arm first, then resume the contract cycle. Mechanical render gate on
        // the combat-readiness wire fact; no game knowledge.
        if (heldBatchAllDone && noContractSourceInView
            && (aContractBearingRenders || aContractTurnInNameRenders)
            && selfArmCombatEffective)
        {
            if (aContractBearingRenders)
                sb.AppendLine("- RETURN TO A CONTRACT SOURCE: every tracked contract is DONE (stage 3) — your batch is finished and you need a FRESH source to keep earning — but NO contract source (a `vendor` or un-talked `npc`) is in `Visible nearby`. A fresh source sits back in the populated area your batch came from, in the direction of a contract's `objective area` / `turn-in location` bearing listed with your contracts below. TRAVEL back there — follow the travel instruction shown with your contracts to `Explore` toward that bearing — instead of grinding monsters that carry you FURTHER from any source. This is TRAVEL to reach a source area, NOT a hand-in: do NOT re-`Talk` a done contract's settled turn-in NPC. The moment a `vendor` or un-talked `npc` comes into view, switch to checking it for a new task (the FIND A KILL-TASK SOURCE rule). Health-critical safety and any active server/quest directive still come first.");
            else
                sb.AppendLine("- RETURN TO A CONTRACT SOURCE: every tracked contract is DONE (stage 3) — your batch is finished and you need a FRESH source to keep earning — but NO contract source (a `vendor` or un-talked `npc`) is in `Visible nearby`, and no contract shows a travel `bearing` to copy. A fresh source sits back in the populated area your batch came from, where a contract's `turn-in NPC` / `start NPC` (named with each contract below) stands. TRAVEL back there — emit `Explore{target: {name: \"<that NPC name>\"}}` to head toward that NPC's area — instead of grinding monsters that carry you FURTHER from any source. This is TRAVEL to reach the source AREA, not a hand-in: do NOT re-`Talk` a done contract's settled turn-in NPC — `Explore` only WALKS you toward the name and NEVER Talks, so simply reach the area and let the FIND A KILL-TASK SOURCE rule engage a `vendor` or `npc` the moment one is in `Visible nearby`. Health-critical safety and any active server/quest directive still come first.");
        }
        sb.AppendLine("- SERVER-INSTRUCTION PRECEDENCE: `## Server hints`, `## Early server directives`, or `## System messages` text that tells you how to LEAVE, EXIT, PROCEED PAST, ADVANCE BEYOND, SKIP, or otherwise COMPLETE or move on from the area or tutorial — especially naming a person/place or warning the step is irreversible — OUTRANKS repeating a local interaction you already observed (re-picking an item you hold, re-talking an NPC who gave no new dialog, re-using an object that didn't change) AND starting a FRESH incidental local interaction that does not itself advance the directive — looting a NEW corpse, picking up NEW loot, talking an as-yet-untalked NPC the directive did not name, or attacking another OPTIONAL monster (a first-time local action is no more 'progress' than a repeated one while an unacted leave/advance directive is shown); it likewise OUTRANKS optional grinding/exploration (the same way FINISH MULTI-STEP DIRECTIVES outranks incidental looting/exploration). When such an instruction is present and unacted, emit a `Talk`/`Use`/`Explore` toward the named target (even if not visible) INSTEAD of looping completed steps or grinding/looting for optional gains. This NEVER overrides health-critical safety, nor an action the directive ITSELF names or requires (reading a `sign` it told you to read, fetching/`Use`-ing/`Give`-ing an item it told you to get or turn in) — those ARE the directive, so do them. An OPTIONAL framing (\"if you wish\", \"when you are ready\", \"you may\") or a promise of EQUIVALENT rewards does NOT make such a directive absent — it is still an ACTIVE, pursuable progression option, so do NOT reason that the scene has 'no directive pending' while one is shown above.");
        sb.AppendLine("- AREA COMPLETE means MOVE ON: when server/NPC text states the current area's training or objective is DONE / COMPLETE / FINISHED (e.g. \"you have completed ...\", \"well done, you may now ...\", \"your training is finished\") — usually naming an exit, `portal`, or next place to go — the required purpose of THIS area is ACHIEVED. Continuing to grind the SAME (often respawning) monsters here for more XP is OPTIONAL and does NOT advance your progression; the named exit / next-step IS the progression. Pursue it — `Use` a named `portal`/exit if one is in view, else `Explore`/`Talk` toward the named next place — rather than re-grinding the area after it reports complete. (Health-critical safety and any step the exit directive ITSELF requires still come first; but absent those, do NOT justify 'one more fight' or 'a little more loot/XP' while the exit/next-step directive sits unacted — pursue the exit.)");
        sb.AppendLine("- FINISH MULTI-STEP DIRECTIVES: if you hold an item the server gave you for an unfinished objective (\"take this and bring it back\", \"give X to Y\", \"use this to leave\"), completing it OUTRANKS incidental looting/exploration — return to the NAMED npc/object and `Give`/`Use` it. Treat an unused objective item as an open task, not as done.");
        // cp give-rule-defer-shortdesc: when the bot has re-emitted a `Give` of
        // the same item to the same npc, the LLM is looping a Give the server
        // refuses (a semantic "doesn't want it" rejection). The Motor dedups the
        // repeat, but the LLM re-picks `Give` with no better alternative in mind.
        // The rule DEFERS to the item's own short_desc: if a held item's
        // short_desc directs giving it to THIS npc, a refusal means a prerequisite
        // is unmet (Talk it / do its steps first, then re-Give) — NOT the wrong
        // recipient; only when no item names the npc is the "wrong verb/target,
        // try Use or another npc" guidance applied. Gate on the bot's OWN
        // repeated-Give emission history (cp-2368/2400 per-rule relevance gating)
        // so it costs zero budget when no Give is looping. No item/npc names, no
        // priority; the LLM still decides.
        if (HasRecentRepeatedGoalOfKinds(events, "Give "))
        sb.AppendLine("- A REFUSED GIVE — DEFER TO THE ITEM'S INSTRUCTION: if you `Give{npc, item}` and the server REFUSES it because the npc does not want the item (a `doesn't want that` reply), FIRST check whether an inventory item's `short_desc` OR its `use` text — OR THIS npc's own recent dialogue/tell — explicitly tells you to `Give`/return it to THIS npc. If it does, that npc IS the intended recipient and the refusal means a PREREQUISITE is unmet — do NOT abandon it and do NOT switch to `Use`: `Talk` that npc and follow the dialog/steps it gives FIRST, THEN re-`Give` once. OTHERWISE (neither an item's `short_desc`/`use` text NOR this npc's recent dialogue names this npc as the recipient), that npc is the wrong target or verb — do NOT keep `Give`-ing the same item to the same npc: the item may ACT ON ITS OWN when you `Use` it (the way a `door` or `portal` activates on `Use`), so try `Use{item}`, or `Give` it to a DIFFERENT npc that a directive or dialog named. (This is about a server REFUSAL, not a movement failure: if your `Give` merely FAILED because you could not walk to/reach the npc, keep trying to reach that same npc — the target is still right.)");
        sb.AppendLine(world.Self.AvailableExperience is long bandXp && ShouldSurfaceUnspentXp(bandXp, MinMeaningfulUnspentXp)
            ? "- Priority: 9-10 health-critical; 7-8 quest progress; 5-6 fight/loot/invest unspent XP; 3-4 explore."
            : "- Priority: 9-10 health-critical; 7-8 quest progress; 5-6 fight/loot; 3-4 explore.");
        sb.AppendLine();

        sb.AppendLine("## Self");
        sb.AppendLine($"- name: {world.Self.Name}");
        sb.AppendLine($"- landblock: 0x{world.Self.Landblock ?? 0:X4}");
        sb.AppendLine($"- pos: ({world.Self.PositionX:F1}, {world.Self.PositionY:F1}, {world.Self.PositionZ:F1})");
        if (world.Self.Level is int lv) sb.AppendLine($"- level: {lv}");
        if (world.Self.TotalExperience is long txp)
        {
            // RaiseSkill is only a valid spend when a `trained skills` list is
            // present (its target must be one of those names); when none is
            // surfaced, advertise only the attribute/vital raise verbs so this
            // cue cannot contradict the SPEND XP rule's RaiseSkill guard.
            var raiseVerbs = world.Self.TrainedSkills is { Count: > 0 }
                ? "RaiseAttribute/RaiseVital/RaiseSkill"
                : "RaiseAttribute/RaiseVital";
            var avail = world.Self.AvailableExperience is long axp
                ? (ShouldSurfaceUnspentXp(axp, MinMeaningfulUnspentXp)
                    ? $", {axp} unspent (available to invest NOW via {raiseVerbs} — see SPEND XP)"
                    : $", {axp} unspent")
                : string.Empty;
            sb.AppendLine($"- experience: {txp} total{avail}");
            // Survivability-spend salience: a capable model that DOES spend XP can still
            // pour it ALL into offense (accuracy/damage) while dropping in a hit or two to
            // even WEAK monsters because its max HP is tiny — the SPEND XP rule's "when
            // DYING, max HP is the binding limit" tiebreaker is buried deep in that rule.
            // Elevate it to a salient CONDITIONAL pointer ONLY when the bot died RECENTLY
            // (within RecentDeathSalienceWindowSeconds — i.e. actively DYING, matching the
            // rule's "dying fast" current bottleneck, NOT one stale death from earlier this
            // session, since secondsSinceLastDeath stays non-null all session) AND has
            // unspent XP to invest. Reasons from the deaths telemetry + the unspent-XP gate
            // (same gate as the SPEND XP cues); names no attribute build / monster / number.
            // Balance-preserving: it restates the rule's both-options so it cannot
            // over-steer to endurance; the LLM still decides. SUPPRESSED in an active
            // death-spiral (recentOwnDeathCount >= DeathSpiralMinDeaths): there the
            // `## Survival caution` tail capsule takes over and correctly says raising max
            // HP will NOT help while the respawn penalty keeps re-stacking — so this
            // "invest in max HP FIRST" pointer must yield to avoid contradicting it.
            if (secondsSinceLastDeath is int dsd
                && dsd <= RecentDeathSalienceWindowSeconds
                && recentOwnDeathCount < DeathSpiralMinDeaths
                && world.Self.AvailableExperience is long sxp
                && ShouldSurfaceUnspentXp(sxp, MinMeaningfulUnspentXp))
                sb.AppendLine(
                    $"- SURVIVABILITY-FIRST CHECK: you DIED ~{dsd}s ago and have unspent XP. " +
                    "Per the SPEND XP rule, when DEATHS are the problem — you drop in a hit or " +
                    "two, even to weak monsters — the binding limit is your MAX HP, not accuracy or damage (a " +
                    "dead character lands no swings): invest in ENDURANCE/health to raise max HP FIRST, before " +
                    "pouring more XP into offense. (If instead you SURVIVE your fights but cannot KILL — swings " +
                    "miss or barely hurt, with NO recent deaths — then offense is the limit per SPEND XP.)");
            // SUSTAIN-COMBAT CHECK: the FLEE analog of the death case above. The Motor
            // repeatedly DEFERS the bot's Attack because its health is below the
            // re-engage threshold (IsLowHealthDeferredAttackRepeat) and substitutes a
            // recover-egress — so the bot SURVIVES by fleeing rather than dying, which
            // means neither the SURVIVABILITY-FIRST (recent-death) cue nor the death-
            // spiral caution fires, and a model has no signal that it keeps abandoning
            // fights. Surface the FLEE pattern, scoped to the gap: NOT a recent death
            // (owned by SURVIVABILITY-FIRST above), NOT a death-spiral (owned by
            // `## Survival caution`, which says max HP will NOT help while the penalty
            // re-stacks), and unspent XP to invest. It does NOT categorically claim max
            // HP is the limit — a low-health flee can be EITHER a winning-but-too-slow
            // fight (max HP lets you finish) OR an offense-limited fight (the SPEND XP
            // rule's "survive but cannot kill -> offense" case); it points the LLM at its
            // OWN landed-vs-evaded + target-health evidence to pick the lever, so it never
            // contradicts the SPEND XP offense guidance. The LLM still allocates. Reasons
            // from the bot's OWN deferral history + the unspent-XP gate; no game knowledge.
            else if (IsLowHealthDeferredAttackRepeat(events)
                && recentOwnDeathCount < DeathSpiralMinDeaths
                && world.Self.AvailableExperience is long fxp
                && ShouldSurfaceUnspentXp(fxp, MinMeaningfulUnspentXp))
                sb.AppendLine(
                    "- SUSTAIN-COMBAT CHECK: you keep BREAKING OFF fights to recover because your health drops too " +
                    "low to stay in melee (repeated low-health attack deferrals), with NO recent death and unspent " +
                    "XP. Surviving by FLEEING is not progress — you cannot finish a kill you keep running from. " +
                    "Decide the lever from YOUR evidence (per the SPEND XP rule): if your swings are LANDING and the " +
                    "target's health is FALLING (you are winning, just too slowly to outlast the damage you take), " +
                    "more MAX HP — raise ENDURANCE/health — lets you STAY in long enough to finish it; but if your " +
                    "swings keep MISSING or barely hurt (you are not making progress), OFFENSE is the limit instead " +
                    "and more max HP only lets you lose slower — raise accuracy/damage. Either way, invest the unspent " +
                    "XP your landed-vs-evaded split and the target's health trend point to, rather than fleeing every " +
                    "fight.");
        }
        // Attributes, raisable skills, health, and deaths — the compact,
        // decision-critical self facts, rendered via the shared helper so the
        // protected-tail `## Self` capsule (below) stays identical.
        AppendSelfCoreFacts(sb, world, secondsSinceLastDeath);
        if (world.Self.CoinValue is int cv) sb.AppendLine($"- coin (server-tracked): {cv} pyreals");
        sb.AppendLine();

        // ── ## Fellowship (membership perception) ────────────────────────
        // Raw membership facts from the server's FellowshipFullUpdate snapshot:
        // whether the bot is in a fellowship, who is in it, who leads, and the
        // share/open/lock flags. Conditional (omitted when not in a fellowship)
        // → zero static-floor cost in the common solo case. Pure perception; no
        // advice about whether or how to use the fellowship — the LLM owns any
        // fellowship decision.
        if (world.Fellowship is { } fellow)
        {
            sb.AppendLine("## Fellowship");
            var leaderClause = fellow.AmLeader
                ? "you are the leader"
                : (fellow.LeaderName is { Length: > 0 } ln ? $"led by {ln}" : "leader unknown");
            sb.AppendLine(
                $"- you are in a fellowship \"{fellow.Name}\" " +
                $"({fellow.MemberCount} member(s), {leaderClause})");
            if (fellow.Members.Count > 0)
            {
                sb.AppendLine(
                    "- members: " +
                    string.Join(", ", fellow.Members.Select(m =>
                        $"{m.Name} (L{m.Level}{(m.IsSelf ? ", you" : "")}{(m.IsLeader ? ", leader" : "")})")));
            }
            sb.AppendLine(
                $"- flags: shares XP {(fellow.ShareXp ? "yes" : "no")}, " +
                $"even share {(fellow.EvenShare ? "yes" : "no")}, " +
                $"open {(fellow.Open ? "yes" : "no")}, " +
                $"locked {(fellow.Locked ? "yes" : "no")}");
            sb.AppendLine();
        }

        // ── ## Contracts — relocated to the PROTECTED salience tail (search
        // "## Contracts" below). The body's trailing sections are hard-cut first
        // when the prompt overflows the request ceiling, which live guillotined
        // tracked-objective perception before the LLM saw it; rendering it among
        // the protected end-capsules keeps it intact.

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
                // Raw wire-derived kind so the LLM can tell a creature
                // candidate from inert scenery; ClassifySighting only
                // yields Mob/NPC/Unknown. No priority — perception only.
                var kind = c.Kind switch
                {
                    EntityKind.Mob => "mob",
                    EntityKind.NPC => "npc",
                    _ => "object",
                };
                // picker-name-respawn-audit: factual per-Name pickup tally,
                // shown only when the bot has already picked one. The LLM
                // decides whether a duplicate is worth re-collecting; no
                // recommendation or "skip" wording (that valuation is its call).
                var pickedCount = c.PickedNameCount > 0 ? $" picked_name_count={c.PickedNameCount}" : "";
                sb.AppendLine(
                    $"- 0x{c.Guid:X8} \"{c.Name}\" dist={c.Distance:F1}u cell=0x{c.CellId:X8} kind={kind}{pickedCount}{vis}");
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
        else
        {
            // Collapse identical inventory entries (same name, wcid, rendered
            // short_desc, and wielded state) into ONE row carrying an `xN`
            // count, preserving first-seen order. A bloated bag — e.g. dozens
            // of duplicate quest notes or portal gems — otherwise floods this
            // early section and shoves the later FIXED decision sections
            // (`## Combat readiness`, `## Visible nearby`) past the hard prompt
            // ceiling, where they are truncated away entirely. This is purely
            // mechanical equality-counting of identical RENDERED facts (mirrors
            // the deduped `(repeated xN)` server-hint surface): no name/wcid/
            // type heuristic, no game knowledge. Goal resolution is unaffected
            // — the picker matches against world.Inventory, not this text, and
            // this section prints no per-item guid.
            var invTrainedSkillNames = world.Self.TrainedSkills?
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var invCounts = new Dictionary<(string Name, uint Wcid, string ShortDesc, string UseDesc, uint Wielded, string Wield), int>();
            var invOrder = new List<(string Name, uint Wcid, string ShortDesc, string UseDesc, uint Wielded, string Wield)>();
            foreach (var i in world.Inventory)
            {
                var sd = string.IsNullOrWhiteSpace(i.ShortDesc) ? "" : i.ShortDesc!.Trim();
                var ud = string.IsNullOrWhiteSpace(i.UseDesc) ? "" : i.UseDesc!.Trim();
                // Key on the EFFECTIVE (rendered) use text: it is suppressed
                // below when identical to short_desc, so normalize it to "" here
                // too. Otherwise two items that RENDER identically (one with no
                // use, one whose use duplicates its short_desc) would get
                // different keys and fail to collapse.
                if (ud.Length > 0 && ud == sd) ud = "";
                var wielded = i.WieldedAt is uint iw ? iw : 0u;
                var key = (i.Name, i.Wcid, sd, ud, wielded, WieldAnnotation(i, invTrainedSkillNames));
                if (invCounts.TryGetValue(key, out var c)) invCounts[key] = c + 1;
                else { invCounts[key] = 1; invOrder.Add(key); }
            }
            foreach (var key in invOrder)
            {
                var n = invCounts[key];
                sb.Append($"- {key.Name} (wcid={key.Wcid}");
                if (key.Wielded != 0) sb.Append($", wielded@0x{key.Wielded:X}");
                sb.Append(")");
                sb.Append(key.Wield);
                if (n > 1) sb.Append($" x{n}");
                sb.AppendLine();
                if (key.ShortDesc.Length > 0)
                    sb.AppendLine($"    short_desc: {key.ShortDesc}");
                if (key.UseDesc.Length > 0)
                    sb.AppendLine($"    use: {key.UseDesc}");
            }
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

        // visible-recent-interaction (cp-2290): WORLD-object analogue of
        // the inventory-USE recency surface above. Renders recent
        // EventKind.WorldObjectInteracted echoes (self-emitted by the Motor
        // when a spatial Use/Pickup action cycle completes) for objects that
        // are STILL VISIBLE, so the LLM can see "you already interacted with
        // this chest/door N times" and stop re-picking it. Live-observed
        // loop: the bot Used the same chest 3x + revisited the same
        // door, burning a ~5s LLM round-trip each cycle, because nothing in
        // the prompt told it those objects were already worked. Telemetry
        // ONLY — unlike the inventory dedup, the policy does NOT drop the
        // repeat goal; the LLM decides. Filtered to currently-visible guids
        // so it is actionable context, not history noise. Conditional, so it
        // adds nothing to the static-floor prompt budget.
        var visibleGuids = world.Visible.Select(v => v.Guid).ToHashSet();
        var recentObjInteractions = events.Recent(64)
            .Where(e => e.Kind == EventKind.WorldObjectInteracted
                        && e.ItemGuid is uint g && visibleGuids.Contains(g))
            .GroupBy(e => e.ItemGuid!.Value)
            .Select(grp => new
            {
                Guid = grp.Key,
                Name = grp.Select(e => e.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                Wcid = grp.Select(e => e.Wcid).FirstOrDefault(w => w is not null),
                Count = grp.Count(),
                LastSeq = grp.Max(e => e.Sequence),
            })
            .OrderByDescending(x => x.LastSeq)
            .Take(8)
            .ToList();
        if (recentObjInteractions.Count > 0)
        {
            sb.AppendLine("## Recently interacted objects");
            foreach (var o in recentObjInteractions)
            {
                var nm = string.IsNullOrEmpty(o.Name) ? "(unknown)" : o.Name;
                var wcidStr = o.Wcid is uint ow ? $" wcid={ow}" : "";
                // cp-2375: a pickup-eligible item the bot interacted with that is
                // STILL VISIBLE means the pickup did NOT stick — had it been
                // acquired it would be in the bag, not on the ground. Surface that
                // as a factual not-acquired signal (telemetry only; the LLM still
                // decides) so the bot stops re-trying an un-acquirable ground item
                // instead of looping on it. Pickup-eligibility is the wire-decoded
                // ItemType & ItemTypeMasks.Pickup affordance, not a type priority.
                var vobj = world.Visible.FirstOrDefault(v => v.Guid == o.Guid);
                var failedPickup = vobj is not null && vobj.ItemType is uint vit
                    && (vit & ItemTypeMasks.Pickup) != 0;
                var pickupNote = failedPickup
                    ? " — a pickup-eligible item you tried to take is STILL on the ground" +
                      " (it did NOT enter your bag); re-trying the same way will not acquire it"
                    : "";
                sb.AppendLine($"- {nm}{wcidStr} guid=0x{o.Guid:X8}: interacted x{o.Count} recently (still visible){pickupNote}");
            }
            sb.AppendLine(
                "- NOTE: you have already interacted with the object(s) above. " +
                "Re-using the same world object without a new event (action " +
                "rejection, NPC dialog, inventory change, server hint) is " +
                "unlikely to produce a different outcome. Prefer a different " +
                "target or action unless you have a concrete new reason to retry.");
            sb.AppendLine();
        }

        sb.AppendLine("## Visible nearby");
        AppendVisibleNearby(sb, world.Visible, world.CombatHistory, world.OpenedCorpseGuids);
        sb.AppendLine();

        // Slice H — Combat readiness summary. Surfaces the state the
        // LLM needs to decide whether to engage: weapon, monster
        // proximity, hostile incoming, and (since self-health
        // perception) the bot's own health fraction when known.
        // Self-health now arrives via PrivateUpdateVital (0x02E7) and
        // is populated on damage/regen ticks; it is still null before
        // the first vital update of a session, so the line is gated.
        //
        // "weapon" means a MELEE WEAPON is wielded — NOT just any
        // equipped item. Counting armor/clothing as "weapon: wielded"
        // (the old `Any(WieldedAt != 0)` bug) let the bot think it was
        // armed after equipping a hat. The melee-weapon predicate
        // (ItemType has the MeleeWeapon bit AND WieldedAt != 0) mirrors
        // the wire-schema precondition for GameActionTargetedMeleeAttack
        // and the same check in NoQuestKnowledgePolicy. This is the only
        // attack path the motor currently executes, so missile/caster
        // wields are NOT counted as combat-ready here.
        var meleeWeaponWielded = world.Inventory.Any(i =>
            i.WieldedAt is uint w && w != 0 &&
            i.ItemType is uint it && (it & ItemTypeMasks.MeleeWeapon) != 0);
        // combat-missile-attack: the motor now ALSO executes a missile
        // attack path (bow/crossbow/atlatl), so a wielded missile weapon
        // counts as armed. Surface missile-weapon + ammo state as RAW
        // FACTS — the server silently no-ops a missile attack from an
        // ammo launcher with no ammo loaded, so the LLM needs to see
        // whether ammo is loaded to decide whether to wield ammo first.
        // Pure typed-affordance projection (ItemType MissileWeapon bit /
        // MissileAmmo SLOT bit), no names/wcids/landblocks.
        var wieldedMissileLauncher = world.Inventory.FirstOrDefault(i =>
            // Main-weapon slot only (see selfArmMissileWielded): loaded ammo carries
            // the MissileWeapon ItemType bit but lives in the ammo slot, so WieldedAt
            // != 0 alone would mis-count ammo as a wielded launcher.
            i.WieldedAt is uint mw && (mw & WeaponSwap.MainWeaponSlotMask) != 0 &&
            i.ItemType is uint mit && (mit & ItemTypeMasks.MissileWeapon) != 0);
        var missileWeaponWielded = wieldedMissileLauncher is not null;
        // A wielded THROWN weapon is its own projectile: the server (Player_Missile.cs)
        // uses `ammo = weapon.IsAmmoLauncher ? GetEquippedAmmo() : weapon`, so a missile
        // weapon that is NOT a launcher throws ITSELF and needs no separate ammo. The
        // wire discriminator (cp044/cp052): a real launcher declares an AmmoType; a thrown
        // weapon omits it (null). So a wielded missile weapon with a null AmmoType is a
        // thrown weapon — combat-capable on its own, NOT an empty launcher.
        var wieldedThrownWeapon = wieldedMissileLauncher is { AmmoType: null };
        var ammoLoaded = world.Inventory.Any(i =>
            i.WieldedAt is uint aw && aw == ItemTypeMasks.MissileAmmoSlot);
        // Items the server SEMANTICALLY refused for the bot recently — used
        // below to drop a self-arm suggestion the server would only refuse
        // again. An ActionRejected (e.g. an InventoryServerSaveFailed for a
        // wield/load the server would not actuate) carries the item guid.
        // EXCLUDE synthetic motor-side TRANSPORT failures (Unreachable /
        // Blocked / NoIndoorPath — reserved codes 0xFFFC-0xFFFE via
        // IsTransportFailureRejection): those mean "could not WALK to a world
        // target", carry that TARGET's guid, and clear once the bot arrives.
        // Inventory items and visible world objects share one guid space (a
        // ground item keeps its guid after pickup), so counting a transport
        // failure here could wrongly suppress a bag item the bot earlier
        // walk-timed-out toward and has since picked up. A wield/load is never
        // a transport failure, so excluding them loses nothing for inventory
        // suggestions and keeps this set to genuine server refusals. Same
        // 32-event ActionRejected window NoQuestKnowledgePolicy's
        // recentlyRejectedGuids uses; guids age out naturally, so a later
        // retry once the blocker is gone is fair game. Mechanical: reads the
        // bot's OWN rejection bookkeeping; the guid comes from the wire, not a
        // name/wcid list.
        var recentlyServerRefusedGuids = events
            .RecentOfKind(EventKind.ActionRejected, 32)
            .Where(e => e.ItemGuid is uint && !IsTransportFailureRejection(e))
            .Select(e => e.ItemGuid!.Value)
            .ToHashSet();
        var bagAmmo = (!missileWeaponWielded || wieldedThrownWeapon || ammoLoaded) ? null : world.Inventory.FirstOrDefault(i =>
            (i.WieldedAt is not uint baw || baw == 0) &&
            i.ValidLocations is uint vl && (vl & ItemTypeMasks.MissileAmmoSlot) != 0 &&
            AmmoTypeCompatible(wieldedMissileLauncher?.AmmoType, i.AmmoType) &&
            !recentlyServerRefusedGuids.Contains(i.Guid));
        // A wielded MISSILE weapon with no ammo loaded is NOT combat-effective
        // (it cannot fire), so for the purpose of surfacing how-to-arm
        // affordances — an un-wielded melee weapon already in the bag (→ Wield
        // it) or a melee weapon on the ground (→ Pickup it) — treat it the same
        // as UNARMED. This mirrors `selfArmCombatEffective` and the SELF-ARM
        // rule's combat-effective test (a missile weapon counts only with ammo
        // loaded), so a bot holding an empty missile weapon WITH a usable melee
        // weapon in its bag is shown that weapon instead of having the
        // acquisition hints suppressed — the suppression left it looping on an
        // ammo wield it cannot complete. Pure wire-state (WieldedAt + typed
        // masks); the LLM still decides; no advice, no game knowledge.
        var armed = meleeWeaponWielded || wieldedThrownWeapon || (missileWeaponWielded && ammoLoaded);
        // Acquisition affordances surfaced ONLY when unarmed, so the LLM
        // can act on "arm yourself" instead of merely noting it is
        // unarmed (the live failure mode): an unwielded melee weapon
        // already in the bag (→ Wield it) and the nearest pickup-able
        // melee weapon lying in the world (→ Pickup it). Both are pure
        // typed-affordance projections (ItemType MeleeWeapon bit), no
        // names/wcids/landblocks.
        //
        // ALL THREE self-arm suggestions — bagWeapon, bagAmmo (above) and
        // groundWeapon (below) — skip an item the server recently REFUSED
        // (recentlyServerRefusedGuids): re-surfacing an item the server will
        // only refuse again makes the LLM loop on it every decision (re-emit
        // the Wield/Pickup → server refuses → IsGoalRecentlyRejected drops it,
        // burning an LLM round-trip) while a DIFFERENT, usable item in the bag
        // / on the ground is never suggested, and each refusal is itself an
        // ActionRejected that also stalls the autonomous combat chain. The set
        // is SEMANTIC refusals only (transport failures were excluded above),
        // which is exactly the set IsGoalRecentlyRejected does NOT clear on
        // arrival — so withholding these suggestions matches what the dedup
        // would drop anyway. A transport-failed (could-not-walk-there) pickup
        // is deliberately NOT suppressed here: it clears once the bot arrives,
        // and that arrival-clearing is owned by IsGoalRecentlyRejected and the
        // picker's own rejection filter, not this arrival-unaware set.
        var bagWeapon = armed ? null : world.Inventory.FirstOrDefault(i =>
            (i.WieldedAt is not uint bw || bw == 0) &&
            i.ItemType is uint bit && (bit & ItemTypeMasks.MeleeWeapon) != 0 &&
            !recentlyServerRefusedGuids.Contains(i.Guid));
        var groundWeapon = armed ? null : world.Visible
            .Where(v => !v.IsMonster &&
                        v.ItemType is uint vit && (vit & ItemTypeMasks.MeleeWeapon) != 0 &&
                        !recentlyServerRefusedGuids.Contains(v.Guid))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        // A THROWN weapon in the bag (un-wielded, equippable into a main-weapon slot,
        // with NO AmmoType — it is its own projectile) is an arming path that needs no
        // separate ammo: the server (Player_Missile.cs) throws the weapon ITSELF for
        // damage when the wielded weapon is not a launcher (`ammo = weapon.IsAmmoLauncher
        // ? GetEquippedAmmo() : weapon`). bagWeapon/groundWeapon are melee-only and
        // bagLauncherAmmo requires a launcher WITH compatible ammo, so without this a bot
        // whose only usable weapons are throwables (e.g. its launchers have no ammo) is
        // shown no arming path at all and stays UNARMED. The main-weapon ValidLocations
        // gate (not just the MissileWeapon bit) keeps loaded ammo — which carries the
        // same bit but lives in the ammo slot — from being mis-surfaced as a throwable.
        // Surfaced ONLY when unarmed; skips server-refused items like the other self-arm
        // suggestions. Pure typed/wire affordance; the LLM decides; no game knowledge.
        var bagThrownWeapon = armed ? null : world.Inventory.FirstOrDefault(i =>
            (i.WieldedAt is not uint tw || tw == 0) &&
            i.ValidLocations is uint tvl && (tvl & WeaponSwap.MainWeaponSlotMask) != 0 &&
            i.ItemType is uint tit && (tit & ItemTypeMasks.MissileWeapon) != 0 &&
            i.AmmoType is null &&
            !recentlyServerRefusedGuids.Contains(i.Guid));
        // A missile LAUNCHER in the bag (un-wielded, equippable into a main-weapon
        // slot) paired with COMPATIBLE ammo ALSO in the bag — the ranged equivalent
        // of bagWeapon. Surfaced ONLY when unarmed AND the WIELDED missile weapon (if
        // any) has no loadable bag ammo (bagAmmo is null): the live failure mode is a
        // bot wielding an empty/incompatible missile weapon (no ammo it can load for
        // THAT launcher) while a DIFFERENT launcher it owns — whose ammo it ALSO owns
        // — sits un-surfaced in the bag, leaving the bot with no arming path at all
        // (bagWeapon/groundWeapon are melee-only). Reuses AmmoTypeCompatible (cp044).
        // Skips server-refused items like the other self-arm suggestions, so a launcher
        // or ammo the server will not actuate (e.g. an unmet skill requirement) is not
        // re-suggested every cycle. Pure typed/wire affordance (ItemType MissileWeapon
        // bit + ValidLocations main-weapon / ammo slots + AmmoType compatibility); no
        // names/wcids, no game knowledge.
        (InventoryItemProjection Launcher, InventoryItemProjection Ammo)? bagLauncherAmmo = null;
        if (!armed && bagAmmo is null)
        {
            foreach (var launcher in world.Inventory.Where(i =>
                (i.WieldedAt is not uint lw || lw == 0) &&
                i.ValidLocations is uint lvl && (lvl & WeaponSwap.MainWeaponSlotMask) != 0 &&
                i.ItemType is uint lit && (lit & ItemTypeMasks.MissileWeapon) != 0 &&
                // A real LAUNCHER (bow/crossbow/atlatl) declares its AmmoType on the
                // wire; a THROWN weapon (self-contained, the projectile itself) omits
                // it (null). Without this, a null-AmmoType thrown weapon would match
                // ANY ammo (AmmoTypeCompatible treats a null launcher AmmoType as
                // compatible with everything), producing a doomed "wield thrown weapon
                // + wield unrelated ammo" loadout whose load step the server refuses.
                i.AmmoType is not null &&
                !recentlyServerRefusedGuids.Contains(i.Guid)))
            {
                var compatAmmo = world.Inventory.FirstOrDefault(a =>
                    (a.WieldedAt is not uint aw2 || aw2 == 0) &&
                    a.ValidLocations is uint avl && (avl & ItemTypeMasks.MissileAmmoSlot) != 0 &&
                    AmmoTypeCompatible(launcher.AmmoType, a.AmmoType) &&
                    !recentlyServerRefusedGuids.Contains(a.Guid));
                if (compatAmmo is not null) { bagLauncherAmmo = (launcher, compatAmmo); break; }
            }
        }
        var nearestMonster = world.Visible
            .Where(v => v.IsMonster)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        // cp058: the nearest in-view VENDOR whose panel is NOT yet open, offered
        // below as the LAST-RESORT arming path — surfaced ONLY when the bot is
        // UNARMED and has NO in-bag / on-ground weapon, ammo, or launcher loadout to
        // arm with. A vendor may sell a weapon or ammo to BUY (its wares are unknown
        // until its panel is opened). Skips a server-refused vendor. Pure wire-flag
        // (IsVendor) projection; the LLM still decides whether to Use/Buy.
        var armVendor = armed ? null : world.Visible
            .Where(v => v.IsVendor && !recentlyServerRefusedGuids.Contains(v.Guid))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        var observedHostile = world.Visible.FirstOrDefault(v => !v.IsCorpse && v.ObservedHostile);
        // Threat counts for the cluster signal. A live combat threat is any
        // non-corpse monster OR anything actively attacking you (ObservedHostile
        // — e.g. a hostile flagged-as-npc creature), so the count never
        // contradicts the "observed hostile" line above and hostilesInView is
        // always a subset of monstersInView. Raw wire-flag counts only.
        var monstersInView = world.Visible.Count(v => !v.IsCorpse && (v.IsMonster || v.ObservedHostile));
        var hostilesInView = world.Visible.Count(v => !v.IsCorpse && v.ObservedHostile);
        sb.AppendLine("## Combat readiness");
        sb.AppendLine($"- weapon: {WeaponReadinessLine(meleeWeaponWielded, missileWeaponWielded, ammoLoaded, bagAmmo is not null, wieldedThrownWeapon)}");
        if (WeaponSkillSwapAdvisory(world, recentlyServerRefusedGuids) is string crSkillAdvisory)
            sb.AppendLine($"- {crSkillAdvisory}");
        if (WieldedWeaponUntrainedAccuracyNote(world) is string crUntrainedNote)
            sb.AppendLine($"- {crUntrainedNote}");
        if (FormatSelfHealth(world.Self.HealthCurrent, world.Self.HealthObservedPeak, world.Self.HealthFraction, world.Self.HealthRising) is string crHealthLine)
            sb.AppendLine(crHealthLine);
        if (FormatSelfStaminaWhenLow(world.Self.StaminaCurrent, world.Self.StaminaObservedPeak) is string crStaminaLine)
            sb.AppendLine(crStaminaLine);
        // coldstart hunt discovery — surface a "tapped out" fact when the bot
        // is combat-ready and has farmed this landblock past the dwell
        // threshold without leveling, so the LLM knows to travel for tougher
        // monsters even while trivial mobs remain in view. Own-progress signal
        // only (level + dwell), no monster-type judgement — see HuntTappedOutFact.
        var armedForHunt = meleeWeaponWielded || missileWeaponWielded;
        double? dwellMinForHunt = dwellEntryUtc is DateTimeOffset hutDwellEntry
            ? Math.Max(0.0, (DateTimeOffset.UtcNow - hutDwellEntry).TotalMinutes)
            : (double?)null;
        if (HuntTappedOutFact(armedForHunt, world.Self.Level, levelAtLandblockEntry,
                dwellMinForHunt, EgressDwellMinutes) is string tappedOutFact)
            sb.AppendLine($"- {tappedOutFact}");
        if (bagWeapon is not null)
            sb.AppendLine($"- melee weapon in your inventory (Wield it to arm): {bagWeapon.Name}");
        if (bagThrownWeapon is not null)
            sb.AppendLine($"- throwable weapon in your inventory (Wield it to arm — a thrown weapon is its own projectile, NO ammo needed): {bagThrownWeapon.Name}");
        if (groundWeapon is not null)
        {
            var gwd = groundWeapon.Distance is float gd ? $" d={gd:F1}" : "";
            sb.AppendLine($"- melee weapon nearby (Pickup it to arm): {groundWeapon.Name}{gwd}");
        }
        if (bagAmmo is not null)
            sb.AppendLine($"- missile ammo in your inventory (Wield it to load): {bagAmmo.Name}");
        if (bagLauncherAmmo is { } bla1)
            sb.AppendLine(
                "- missile launcher + compatible ammo in your inventory (Wield the launcher," +
                $" then Wield the ammo to load): {bla1.Launcher.Name} + {bla1.Ammo.Name}");
        // Ammoless-bag-launcher note (cp061): when unarmed and a bag launcher
        // has no compatible ammo, the LLM tends to re-wield it believing that
        // will arm the bot — it won't (launcher fires nothing, cp060 dequips it
        // immediately). Surface a plain fact so the LLM understands: fight
        // unarmed (unarmed melee is available) or obtain ammo first. No names
        // in source — the item name comes from the projection at runtime. Pure
        // typed-affordance projection; the LLM still decides.
        if (!armed)
        {
            var bagAmmoForCheck = world.Inventory
                .Where(a => a.WieldedAt is not uint abw || abw == 0)
                .Select(a => (a.ValidLocations, a.AmmoType))
                .ToList();
            var uselessBagLauncher = world.Inventory.FirstOrDefault(i =>
                (i.WieldedAt is not uint ulw || ulw == 0) &&
                i.ItemType is uint ulit && (ulit & ItemTypeMasks.MissileWeapon) != 0 &&
                i.AmmoType is not null &&
                !HasLoadableBagAmmoForLauncher(bagAmmoForCheck, i.AmmoType));
            if (uselessBagLauncher is not null)
                sb.AppendLine(
                    $"- NOTE: you have a missile launcher in your bag ({uselessBagLauncher.Name})" +
                    " but NO compatible ammo — a launcher without ammo CANNOT fire; wielding it" +
                    " does NOT arm you and will be immediately un-wielded." +
                    " Fight unarmed (unarmed melee is always available) or find ammo first.");
        }
        // cp062 — commit-to-unarmed-combat. When the bot has NO weapon to wield or buy
        // here AND a monster is in view, the weak model tends to keep emitting `Wield`
        // for the empty launcher (dropped every time by the loop-break) instead of
        // fighting. Unarmed melee (fists) is always available, so direct the LLM to
        // ATTACK the visible monster NOW rather than re-attempting a useless wield.
        // Surfaces the action affordance; the LLM still chooses the target and still
        // weighs the COMBAT SAFETY rule (a doomed/beaten engagement is vetoed
        // downstream). No specific monster, no priority — no game knowledge.
        if (!armed && monstersInView > 0 && armVendor is null &&
            bagWeapon is null && bagThrownWeapon is null && groundWeapon is null &&
            bagAmmo is null && bagLauncherAmmo is null)
            sb.AppendLine(
                "- FIGHT NOW: you have NO weapon to wield or buy here, but a monster is in" +
                " view and unarmed melee (fists) is ALWAYS available — emit `Attack` on a" +
                " visible monster to fight it. Do NOT emit `Wield` (no usable weapon exists;" +
                " an empty launcher cannot fire and is immediately un-wielded) — wielding wastes the turn.");
        if (bagWeapon is null && bagThrownWeapon is null && groundWeapon is null &&
            bagAmmo is null && bagLauncherAmmo is null &&
            world.Vendor is null && armVendor is not null)
        {
            var avd = armVendor.Distance is float avDist ? $" d={avDist:F1}" : "";
            sb.AppendLine(
                "- vendor nearby (you have NO weapon to Wield/Pickup — `Use` it ONCE to browse its " +
                "`Vendor offerings`, then `Buy` a `[weapon]`/`[missile weapon/ammo]` to arm; if nothing there " +
                $"is affordable, hunt the WEAKEST monsters to loot a weapon/coin rather than re-Using it or touring more vendors): {armVendor.Name}{avd}");
        }
        if (nearestMonster is not null)
        {
            var dStr = nearestMonster.Distance is float dm ? $" d={dm:F1}" : "";
            var recStr = FormatCombatRecordFor(
                world.CombatHistory, nearestMonster.Wcid, nearestMonster.Name);
            sb.AppendLine($"- nearest monster: {nearestMonster.Name}{dStr}{recStr}");
        }
        else
        {
            sb.AppendLine("- nearest monster: (none in view)");
        }
        if (observedHostile is not null)
        {
            var recStr = FormatCombatRecordFor(
                world.CombatHistory, observedHostile.Wcid, observedHostile.Name);
            sb.AppendLine($"- observed hostile: {observedHostile.Name}{recStr} (it has attacked you — fight back or flee)");
        }
        // threat-count: how many monsters are clustered in view and how many
        // are already attacking. Feeds the COMBAT SAFETY "pull singly" rule a
        // crisp count so it doesn't have to infer one from the interleaved
        // "## Visible nearby" list. RAW counts only — no priority/urgency.
        if (FormatThreatSummary(monstersInView, hostilesInView) is string threatLine)
            sb.AppendLine(threatLine);
        // combat-damage-output: surface the live outcome of the current
        // melee fight so the LLM can judge whether its swings are
        // actually connecting. RAW counts only — the LLM decides whether
        // to keep fighting or disengage (see COMBAT SAFETY rule).
        if (world.CurrentFight is { } cf && (cf.SwingsLanded + cf.SwingsEvaded) > 0)
        {
            var cfName = string.IsNullOrEmpty(cf.TargetName) ? "current target" : cf.TargetName;
            var healthNote = "";
            if (cf.CurrentTargetHealthFraction is float curHf)
            {
                var curPct = (int)Math.Round(Math.Clamp(curHf, 0f, 1f) * 100);
                healthNote = cf.FirstTargetHealthFraction is float firstHf
                    ? $"; target health now {curPct}% (was {(int)Math.Round(Math.Clamp(firstHf, 0f, 1f) * 100)}% when this fight began)"
                    : $"; target health now {curPct}%";
            }
            sb.AppendLine(
                $"- current fight vs \"{cfName}\": swings landed {cf.SwingsLanded}, " +
                $"evaded {cf.SwingsEvaded}, damage dealt {cf.DamageDealt}{healthNote}");
        }
        // active-combat-telemetry: surface how much damage the bot has TAKEN
        // recently. Lock-independent (a rolling TTL window in the Motor), so
        // unlike the `current fight` line above it persists through a flee —
        // the decisive moment when the LLM must weigh disengage/Recall vs
        // fighting to 0 HP. RAW counts only; the LLM owns the decision.
        if (FormatRecentInboundDamage(world.RecentInboundDamage) is string inboundDmgLine)
            sb.AppendLine(inboundDmgLine);
        // combat-feel: surface the bot's own recorded outcomes per monster
        // KIND, durable ACROSS sessions (CombatFeelStore persists the ledger).
        // RAW counts only — no danger label, no advice. The COMBAT SAFETY rule
        // tells the LLM to avoid a kind that keeps defeating it; this gives it
        // the cross-session memory to act on. Match a "Visible nearby" name
        // against these rows to judge a fight before starting it.
        if (world.CombatHistory is { Count: > 0 } hist)
        {
            sb.AppendLine("- combat history (your own outcomes vs each monster kind, across sessions):");
            foreach (var h in hist)
            {
                sb.AppendLine(
                    $"  - {h.Name}: fights {h.Fights}, kills {h.Kills}, " +
                    $"deaths {h.Deaths}, near-deaths {h.NearDeaths}, ineffective {h.Ineffective} (last: {h.LastOutcome})");
            }
        }
        sb.AppendLine();

        // Remembered out-of-view creature sightings (the recall analog
        // of "nearest monster" above). Lets the LLM direct the bot back
        // to a monster that left its field of view. Renders nothing when
        // there is nothing remembered to surface.
        AppendRecentSightings(sb, recentSightings, world);

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
        // Per-NPC recent Talk emissions (last 10 GoalEmitted events of
        // kind Talk). Tactics formats GoalEmitted Text as
        // `<Kind> target=<Selector> item=<Selector> source=<src>` and
        // Selector.ToString() prints `guid=...` BEFORE `name="..."`, so a
        // picker-resolved Talk goal reads `target=guid=0x.. name="X" item=`.
        // We therefore extract the whole target selector (mirroring the
        // recent-Use counter below) and key identity by the guid token when
        // present (else the name token, else the verbatim selector) so
        // re-Talks of the SAME NPC collapse to one count even when the
        // selector carries a guid. A name-only regex silently missed every
        // guid-bearing Talk goal, leaving the count empty during real loops.
        // The DISPLAY prefers the human name; identical display labels across
        // DISTINCT guids get a short guid disambiguator. Mechanical structural
        // parse of the bot's own emission history — no server text, no game
        // knowledge.
        var recentGoalEmits = events.RecentGoalEmissions()
            .Where(e => !string.IsNullOrEmpty(e.Text))
            .Take(10)
            .ToList();
        var talkByKey = new Dictionary<string, (int Count, string Display, string? Guid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Talk ", StringComparison.Ordinal)) continue;
            if (!TryExtractGoalTargetSelector(txt, out var sel)) continue;
            var gm = System.Text.RegularExpressions.Regex.Match(sel, "guid=0x[0-9A-Fa-f]+");
            var nm = System.Text.RegularExpressions.Regex.Match(sel, "name=\"([^\"]+)\"");
            var key = gm.Success ? gm.Value : (nm.Success ? nm.Groups[1].Value : sel);
            var display = nm.Success ? nm.Groups[1].Value : (gm.Success ? gm.Value : sel);
            if (talkByKey.TryGetValue(key, out var cur))
            {
                // Prefer a name-bearing display if we only had a guid before.
                var betterDisplay = cur.Display.StartsWith("guid=", StringComparison.Ordinal) && nm.Success
                    ? display : cur.Display;
                talkByKey[key] = (cur.Count + 1, betterDisplay, cur.Guid ?? (gm.Success ? gm.Value : null));
            }
            else
            {
                talkByKey[key] = (1, display, gm.Success ? gm.Value : null);
            }
        }
        if (talkByKey.Count > 0)
        {
            var dupDisplays = talkByKey.Values
                .GroupBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            sb.AppendLine("- recent Talk emissions (last 10 goals):");
            foreach (var kv in talkByKey.OrderByDescending(p => p.Value.Count))
            {
                var label = dupDisplays.Contains(kv.Value.Display) && kv.Value.Guid is not null
                    ? $"{kv.Value.Display} ({kv.Value.Guid})"
                    : kv.Value.Display;
                sb.AppendLine($"    - {label}: x{kv.Value.Count}");
            }
        }
        else
        {
            sb.AppendLine("- recent Talk emissions: (none)");
        }
        var untalkedNpcsInView = CountUntalkedNpcsInView(world, talkedNpcGuids, talkedNpcNames);
        sb.AppendLine(untalkedNpcsInView > 0
            ? $"- untalked npcs in view: {untalkedNpcsInView} (you have NOT yet Talked these — Talk each once to check whether it offers a task before leaving town)"
            : "- untalked npcs in view: 0 (every visible npc has been talked this session)");
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
        // npc-local-speech-perception — heard local/area speech (HeardSpeech),
        // the spoken-aloud sibling of NpcDialog. Sourced from EventStream's
        // DEDICATED heard-speech window (RecentHeardSpeech) — NOT the main event
        // ring — so high-volume ambient speech never evicts critical events.
        // Lower caps than NpcDialog: it is ambient context, so keep the budget
        // footprint small while still anchoring the earliest distinct lines.
        // Selection by event KIND + age only, never by parsing the text (which
        // would be hardcoded knowledge).
        var heardHints = RetainEnds(
            events.RecentHeardSpeech()
                .Where(e => !string.IsNullOrEmpty(e.Text))
                .GroupBy(e => (e.Name, e.Text))
                // Keep the NEWEST occurrence of each distinct line so a just-
                // respoken line carries its latest Sequence into RetainEnds.
                // RecentHeardSpeech() is oldest-first (unlike the newest-first
                // events.Recent() the other hint categories dedupe over), so a
                // plain First() would retain the stale first-seen Sequence and
                // RetainEnds could then drop a line that was actually spoken most
                // recently.
                .Select(g => g.OrderByDescending(e => e.Sequence).First())
                .ToList(),
            earliest: 2, newest: 4);
        // PopupString earliest anchors are sourced from BOTH the recent ring AND
        // the EventStream's persistent distinct-popup store (PersistentPopupStrings),
        // so a one-time login/exit directive that has already aged out of the 256-
        // event ring still surfaces here. The persisted events retain their original
        // (low) Sequence, so RetainEnds files them under "earliest"; the newest come
        // from the live ring. Dedup by text collapses a popup present in both.
        var popupHints = RetainEnds(
            hintPool
                .Where(e => e.Kind == EventKind.PopupString && !string.IsNullOrEmpty(e.Text))
                .Concat(events.PersistentPopupStrings())
                .GroupBy(e => e.Text)
                .Select(g => g.First())
                .ToList(),
            earliest: 6, newest: 6);
        // cp-2323 — repeated-hint count. The dedup above collapses every
        // identical line to ONE row, which is correct for token budget but
        // ERASES the fact that the SAME line arrived many times. Live-observed
        // loop: the bot Talk'd one NPC 12 times in a row; the NPC's reply was
        // byte-identical each time, but the deduped hint showed it once, so the
        // LLM had no signal that re-doing the action kept producing the same
        // response and looped. Re-derive the occurrence count for each retained
        // line (exact string-equality over the same retained window — pure
        // bookkeeping, NO semantic parsing of the text) and surface it as a
        // neutral `(repeated xN)` suffix so the LLM can tell a progressing
        // interaction (new text) from a stuck one (same text again). Telemetry
        // only; the LLM decides whether to retry. No object-type/NPC/quest
        // knowledge is encoded.
        var serverRepeats = hintPool
            .Where(e => e.Kind == EventKind.ServerMessage && !string.IsNullOrEmpty(e.Text))
            .GroupBy(e => (e.ChatType, e.Text))
            .ToDictionary(g => g.Key, g => g.Count());
        var npcRepeats = hintPool
            .Where(e => e.Kind == EventKind.NpcDialog && !string.IsNullOrEmpty(e.Text))
            .GroupBy(e => (e.Name, e.Text))
            .ToDictionary(g => g.Key, g => g.Count());
        var popupRepeats = hintPool
            .Where(e => e.Kind == EventKind.PopupString && !string.IsNullOrEmpty(e.Text))
            .GroupBy(e => e.Text!)
            .ToDictionary(g => g.Key, g => g.Count());
        var heardRepeats = events.RecentHeardSpeech()
            .Where(e => !string.IsNullOrEmpty(e.Text))
            .GroupBy(e => (e.Name, e.Text))
            .ToDictionary(g => g.Key, g => g.Count());
        static string RepeatSuffix(int count) => count > 1 ? $" (repeated x{count})" : "";
        if (serverHints.Count > 0 || npcHints.Count > 0 || popupHints.Count > 0 || heardHints.Count > 0)
        {
            sb.AppendLine("## Server hints (recent — text the server sent you, dedupe'd)");
            bool anyRepeated = false;
            foreach (var h in serverHints)
            {
                var c = serverRepeats.TryGetValue((h.ChatType, h.Text), out var sc) ? sc : 1;
                if (c > 1) anyRepeated = true;
                sb.AppendLine($"- ServerMessage[chat=0x{h.ChatType ?? 0:X}]: \"{Truncate(h.Text, 320)}\"{RepeatSuffix(c)}");
            }
            foreach (var h in npcHints)
            {
                var c = npcRepeats.TryGetValue((h.Name, h.Text), out var nc) ? nc : 1;
                if (c > 1) anyRepeated = true;
                sb.AppendLine($"- NpcDialog from=\"{h.Name}\": \"{Truncate(h.Text, 320)}\"{RepeatSuffix(c)}");
            }
            foreach (var h in popupHints)
            {
                var c = h.Text is { } pt && popupRepeats.TryGetValue(pt, out var pc) ? pc : 1;
                if (c > 1) anyRepeated = true;
                sb.AppendLine($"- PopupString: \"{Truncate(h.Text, 320)}\"{RepeatSuffix(c)}");
            }
            foreach (var h in heardHints)
            {
                var c = heardRepeats.TryGetValue((h.Name, h.Text), out var hc) ? hc : 1;
                if (c > 1) anyRepeated = true;
                sb.AppendLine($"- HeardSpeech from=\"{h.Name}\": \"{Truncate(h.Text, 320)}\"{RepeatSuffix(c)}");
            }
            if (anyRepeated)
                sb.AppendLine(
                    "- NOTE: \"(repeated xN)\" is how many times the server sent " +
                    "you that exact line in the recent window. A high repeat count " +
                    "means re-doing the same action keeps producing the identical " +
                    "response — prefer a different action or target unless you have " +
                    "a concrete new reason to retry.");
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
        // Slice O — diversify by (label, target). In one spike the bot
        // accumulated 95 Unreachable rejections while a critical
        // TradeAiDoesntWant rejection (an NPC refused an offered item)
        // never made it into the 5-most-recent window the LLM was
        // shown — the bot kept retrying Give(that NPC, that item)
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

        // Distill the bot's OWN goal-lifecycle outcomes (GoalCompleted /
        // GoalFailed) into a dedicated section. These already appear in the
        // 25-event "## Recent events" tail, but in a busy area high-volume
        // observe noise (Motion/UpdatePosition/ObjectCreate) evicts them long
        // before the next decision — the same eviction problem that justified
        // the "## Recent rejections" pull-out. Read the FAILED outcomes from the
        // DURABLE GoalFailed window (recency-scoped), not the 120-event ring: a
        // GoalFailed evicts within a few seconds of busy traffic, so the
        // "don't repeat failing goals" hint silently went BLANK exactly when the
        // bot was looping a failing goal. Completions stay ring-based — an evicted
        // "[done]" is only lost context, not a missed warning. Dedup by
        // (kind, target) keeping the most recent of each (newest-first after the
        // sort) so a repeatedly-failing engagement — e.g. an Attack on a
        // fleeing/far mob that keeps timing out — surfaces once and clearly
        // instead of either flooding the list or being lost. Pure echo of own
        // bookkeeping the LLM generated; it decides whether to retry or pick a
        // different target.
        var outcomeCutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        var goalOutcomes = events.RecentGoalFailures()
            .Where(e => e.Utc >= outcomeCutoff)
            .Concat(events.Recent(120).Where(e => e.Kind == EventKind.GoalCompleted))
            .OrderByDescending(e => e.Utc)
            .GroupBy(e => (e.Kind, key: e.Name ?? e.Text ?? string.Empty))
            .Select(g => g.First())
            .Take(8)
            .ToList();
        if (goalOutcomes.Count > 0)
        {
            sb.AppendLine("## Recent goal outcomes (your own recent goals — don't keep repeating ones that keep failing)");
            foreach (var o in goalOutcomes)
            {
                var verb = o.Kind == EventKind.GoalCompleted ? "[done]" : "[FAILED]";
                var target = string.IsNullOrEmpty(o.Name) ? "" : $" target=\"{o.Name}\"";
                var detail = string.IsNullOrEmpty(o.Text) ? "" : $": {Truncate(o.Text, 80)}";
                sb.AppendLine($"- {verb}{target}{detail}");
            }
            sb.AppendLine();
        }

        if (currentGoal is not null)
        {
            sb.AppendLine("## Current goal");
            sb.AppendLine($"- {currentGoal}");
            sb.AppendLine();
            sb.AppendLine("Keep it if it still looks right; replace if observation says otherwise.");
        }

        if (goalProgress is not null && goalProgress.Distances.Count >= 2)
        {
            var d = goalProgress.Distances;
            var trend = string.Join(" -> ", d.Select(x => $"{x:F1}u"));
            var net = d[^1] - d[0];
            var sign = net >= 0 ? "+" : "";
            sb.AppendLine();
            sb.AppendLine("## Current goal progress (raw bot-to-target distance over recent ticks)");
            sb.AppendLine($"- target {goalProgress.TargetLabel}: {trend} over {goalProgress.SpanSeconds:F0}s (net {sign}{net:F1}u, {d.Count} samples)");
        }

        // ── ## Movement (immobile-stuck telemetry) ───────────────────────
        // Raw own-movement bookkeeping: how many times in a row the bot
        // tried to move and the server held it in place WITHOUT its position
        // changing. Conditional (omitted at 0) → zero static-floor cost.
        // Pure mechanical fact; no game knowledge, no advice — the LLM
        // decides whether it is wedged and what to do instead.
        if (world.MovementBlockStopsSinceSelfMoved >= 1)
        {
            var n = world.MovementBlockStopsSinceSelfMoved;
            sb.AppendLine("## Movement");
            sb.AppendLine(
                $"- {n} consecutive move attempt(s) made no progress: the server held the bot " +
                $"at the same position each time, so the current targets are not reachable by " +
                $"walking from here (possible physical obstruction — e.g. boxed in or on a ledge).");
        }

        // ── ## Search progress (named-target frontier search) ────────────
        // Raw own-bookkeeping: when the bot pursues a NAMED target that is
        // not visible, the Motor walks toward unexplored cells to discover
        // it. Surface how many discovery probes the current search has spent
        // and whether they are repeating cells already tried (probes >
        // distinct cells ⇒ walking is not reaching new ground = a stalled
        // search). Gated to >= 3 probes so a normal short walk-to-discover
        // does not render. Pure facts; no advice, no game knowledge — the LLM
        // decides whether the target is unreachable this way and what to do
        // instead (e.g. open a Door, choose a different objective).
        if (world.NamedSearchProbeCount >= 3 &&
            !string.IsNullOrEmpty(world.NamedSearchTargetName))
        {
            var rawName = world.NamedSearchTargetName!;
            var name = rawName.Length > 60 ? rawName.Substring(0, 60) : rawName;
            sb.AppendLine("## Search progress");
            var line =
                $"- the named target '{name}' is still not visible after " +
                $"{world.NamedSearchProbeCount} discovery move(s) toward unexplored cells " +
                $"({world.NamedSearchDistinctCells} distinct cell(s) tried)";
            if (world.NamedSearchProbeCount > world.NamedSearchDistinctCells)
                line += "; the discovery moves are repeating cells already tried, so " +
                        "walking is not reaching new ground";
            line += ".";
            sb.AppendLine(line);
        }

        // ── ## Unspent XP (end-of-prompt salience capsule) ───────────────
        // The unspent-XP fact + the SPEND XP rule already render up in
        // `## Self` and the RULES preamble, yet live runs show the LLM
        // hoarding tens of thousands of XP for many decisions in a row
        // because the parked local affordance (Talk/Use the nearby object)
        // out-competes a fact buried ~22KB earlier. Re-surface the SAME
        // observed fact in the most decision-proximate slot — the very END
        // of the prompt, right before the model answers — so the Raise*
        // verbs sit on equal footing with Talk/Use/Pickup/Attack/Explore at
        // the decision point. Gated on the mechanical executability of those
        // verbs (unspent > 0), mirroring the SPEND XP rule gate. RAW fact +
        // an explicit not-a-recommendation disclaimer — no urgency wording,
        // no attribute priority, no "you should": the LLM decides whether,
        // how much, and which to raise (it reads attributes/skills in
        // `## Self` and the mechanics in the SPEND XP rule). No game
        // knowledge; perception re-positioned for salience.
        // cp-2343 — mark the start of the protected salience-capsule tail.
        // Everything appended from here to the return is the decision-proximate
        // end-capsule block: ## Unspent XP, ## Recent Talk, ## Recent Use,
        // ## Server-refused interaction targets, ## Approach distance history.
        // These short, individually-bounded perception capsules exist precisely
        // because they sit in the most decision-proximate slot. The request-
        // size fitter's defensive hard-cut trims the TAIL of the string, which
        // would delete these capsules when a large prompt overflows the
        // ceiling. Split the tail off as a PROTECTED suffix so the fitter trims
        // and hard-cuts only the body (whose trailing, lowest-value sections
        // render just above this point — the fixed ## Combat readiness et al.
        // render far earlier and are unaffected) and always re-appends the
        // capsules intact.
        var salienceTailStart = sb.Length;

        // ── ## Survival caution (death-spiral escape, protected tail) ─
        // Highest-urgency tail capsule: when the bot has died DeathSpiralMinDeaths+
        // times within DeathSpiralWindow it is in a death-spiral — AC's death
        // penalty is a stacking multiplier that lowers effective max HP (and other
        // vitals/skills) one step per death and RESETS its recovery counter on each
        // death, and it burns off ONLY by earning XP between deaths; so a bot that
        // keeps dying respawns ever weaker and cannot recover by raising max HP
        // alone. Surface the bot's OWN death-RATE + the generic mechanic + the
        // escape (disengage, travel to a safer/weaker area, earn XP from safe kills
        // until it burns down) in the protected tail so it survives the dense-scene
        // body cut. It explicitly OVERRIDES the optional-combat guidance and the
        // SPEND XP "dying -> raise max HP" tiebreaker for this state so those do not
        // contradict it. Gated purely on the bot's OWN observed deaths; it names no
        // monster/place/stat build/number (other than the bot's own death count)
        // and preserves the HOSTILE-on-you / explicit-directive priority. The LLM
        // still decides where to go and when to resume.
        if (recentOwnDeathCount >= DeathSpiralMinDeaths)
        {
            sb.AppendLine();
            sb.AppendLine("## Survival caution");
            sb.AppendLine(
                $"- you have DIED {recentOwnDeathCount} times in the last several minutes — you are in a " +
                "DEATH-SPIRAL. Repeated deaths in quick succession STACK a penalty that lowers your EFFECTIVE " +
                "max HP (and other vitals — you come back weaker each time), and EACH new death RESETS your " +
                "recovery progress, so the penalty burns off ONLY as you EARN XP WITHOUT dying — raising max HP " +
                "helps less and will NOT fix it alone while the deaths keep resetting your recovery. BREAK the " +
                "spiral: do NOT start optional fights you might lose; `Explore` AWAY from whatever keeps killing " +
                "you toward a SAFER, weaker area (or `Recall` if you have an attuned lifestone), then earn XP " +
                "from only SAFE, winnable kills (or other low-risk progress) until the penalty burns down. While " +
                "this caution applies it OVERRIDES the optional-combat/hunt guidance and the 'dying -> raise max " +
                "HP' XP tiebreaker above. (A `HOSTILE` already attacking you, or an explicit server/quest " +
                "directive, still takes priority — defend or flee as needed.)");
        }

        // ── ## Area danger (spatial death memory, protected tail) ────────────
        // Spatial complement of the RATE-based ## Survival caution: that one fires
        // on the bot's recent death-RATE anywhere; this one fires when the bot is
        // standing in a SPECIFIC area (its current landblock) it has died in MORE
        // THAN ONCE this session. The per-mob-kind combat-feel ledger only steers
        // the bot off KINDS that beat it; an area can stay lethal across SEVERAL
        // kinds or its terrain, and the bot can keep returning to the same deadly
        // hunting ground near its lifestone after each respawn. Surface the bot's
        // OWN per-area death tally so it can leave THIS area specifically. Gated on
        // the bot's own observed deaths-here; names no monster/place/stat/number
        // (other than its own death count) and assigns no danger label by source —
        // the LLM still decides where to go. Independent of the death-spiral gate,
        // so a repeatedly-deadly area is flagged even below the spiral rate.
        if (world.Self.CurrentLandblockDeaths >= AreaDeathSalienceThreshold)
        {
            sb.AppendLine();
            sb.AppendLine("## Area danger");
            sb.AppendLine(
                $"- you have DIED {world.Self.CurrentLandblockDeaths} times in THIS exact area (your current " +
                "location) this session — your OWN outcomes here, not a guess. This specific area has repeatedly " +
                "proven too dangerous for your current strength (it can be lethal across SEVERAL kinds or its " +
                "terrain, beyond any one beaten kind). LEAVE this area for a DIFFERENT one where you can survive " +
                "and make progress, rather than dying here again. (A `HOSTILE` already on you, or an explicit " +
                "server/quest directive, still takes priority — defend or flee as needed.)");
        }

        // ── ## Recent directive check (end-of-prompt salience capsule, cp-2346)
        // The QUEST-DIALOG COMPILER rule renders mid-prompt and can be lost
        // behind the long context; re-surface a short decision-proximate
        // reminder to CHECK whether the recent server/NPC text assigns a task
        // that needs a compiled stack objective. Gated on raw presence of
        // recent server/NPC dialog + a stack being enabled. RAW pointer +
        // explicit not-a-recommendation — task detection and WHAT to push stay
        // entirely with the LLM (it reads the words above); source names no
        // task, target, count, NPC, or location. No game knowledge.
        if (hasRecentServerDialog && stack is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Recent directive check");
            sb.AppendLine(
                "- `## Server hints`, `## Early server directives`, and `## System messages` contain recent and earliest NPC/server text. If that text ASSIGNS a task " +
                "(names creature(s) to kill, an item to fetch, a place to reach), you likely need a persistent " +
                "`stack_ops` objective compiled from it (see the QUEST-DIALOG COMPILER rule) before this tick's " +
                "optional Goal.");
            sb.AppendLine(
                "- raw fact, not a recommendation: decide from the observed words only; do not invent a task, " +
                "target, count, NPC, or location.");
        }

        // ── ## No active objective (end-of-prompt salience capsule, cp-2345) ─
        // When the IntentStack has no ACTIVE top intent (the top reached a
        // terminal state — Completed/Expired/Blocked — or the stack is empty)
        // the bot has no persistent strategic objective and acts tick-by-tick.
        // The `## Intent stack` section that shows the terminal/empty top
        // renders earlier in the prompt and can be out-competed by closer
        // affordances — the same burial pattern the `## Unspent XP` capsule
        // addresses. Re-surface the RAW stack state + the mechanical stack_ops
        // capability in the decision-proximate slot. Gated on raw presence (no
        // Active top), no threshold. RAW fact + capability + explicit
        // not-a-recommendation — it states the stack state and that stack_ops
        // CAN set an objective; it never says WHAT objective to set (that is the
        // LLM's strategic call, not source knowledge). No game knowledge;
        // perception re-positioned for salience.
        if (stack is not null && (stack.Top is null || stack.Top.Status != IntentLifecycle.Active))
        {
            var topState = stack.Top is null ? "empty" : stack.Top.Status.ToString();
            sb.AppendLine();
            sb.AppendLine("## No active objective");
            sb.AppendLine(
                $"- raw stack state: the Intent stack has no Active top intent (current top: {topState}).");
            sb.AppendLine(
                "- mechanical capability: a `stack_ops` push (or replace_top) sets a persistent objective the " +
                "bot pursues across ticks until its completion predicate fires; the full schema and the current " +
                "`stack_revision` to echo are in `## Intent stack` above.");
            sb.AppendLine(
                "- raw fact, not a recommendation: whether to set a persistent objective, what kind/target/" +
                "completion to use, and what per-tick goal to emit are your strategic choices from the facts above.");
        }

        // ── ## Spend before wandering (XP-hoarding wander salience, protected tail) ─
        // Sibling of the body SURVIVABILITY-FIRST CHECK but for a DIFFERENT trigger and placed
        // in the PROTECTED TAIL so it survives the dense-scene body hard-cut: unspent XP, NO
        // recent death (the death case is owned by SURVIVABILITY-FIRST — mutually exclusive), NO
        // monster in view it can defeat (none here, or only kinds that have already beaten it),
        // and the bot is WANDERING — either no active objective, OR a sustained untargeted
        // `Explore` "anywhere" drift even under an Active-but-unactionable intent. A weak model
        // defaults to that drift for ever-weaker foes while HOARDING the XP that would make nearby
        // monsters winnable. Elevate the SPEND option as a salient pointer for that exact
        // situation. Balance-preserving (spend OR travel-to-easier, but spend either way); names
        // no stat build / monster / number; the LLM still decides.
        if (ShouldSurfaceSpendBeforeWander(stack, world, secondsSinceLastDeath, recentOwnDeathCount, events))
        {
            sb.AppendLine();
            sb.AppendLine("## Spend before wandering");
            sb.AppendLine(
                "- you have unspent XP and no monster in view you can currently defeat (none here, or only kinds " +
                "that have already beaten you). Before falling back to wandering (`Explore` \"anywhere\") for " +
                "ever-weaker foes, INVEST that unspent XP now (raise a stat or skill per the SPEND XP rule above, " +
                "which explains what each does and how to choose by your bottleneck) so the monsters around you " +
                "become winnable: getting stronger IS progress. (If this area is genuinely far above your level, " +
                "traveling toward a known easier area is also valid — but spend your hoarded XP either way.)");
        }

        // ── ## Persistent objectives (intent stack re-surfaced, protected tail) ─
        // The full `## Intent stack` renders in the BODY (RenderStackForPrompt) and
        // is dropped by the dense-scene body hard-cut, so in a busy combat scene the
        // LLM loses sight of its OWN persistent intents — including a multi-step
        // task it compiled earlier that is now a PAUSED ancestor under newer hunt
        // frames — and reverts to tick-by-tick grinding as if the scene were
        // objective-free (live: it pushed a compiled quest intent, then later
        // rationalised "no active quest" while grinding unrelated mobs). Re-surface
        // a COMPACT frame list (bounded by MaxDepth) so the persistent plan always
        // survives the cut. Bot-own stack state; the LLM still decides; no game
        // knowledge.
        if (stack is not null && stack.Depth > 0)
        {
            var frames = stack.Frames;
            sb.AppendLine();
            sb.AppendLine(
                "## Persistent objectives (re-surfaced because the full `## Intent stack` above can be trimmed " +
                $"to fit; revision={stack.Revision}, depth={stack.Depth}/{stack.MaxDepth})");
            var hasActionableObjective = false;
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                var role = i == frames.Count - 1 ? "TOP" : $"ancestor[{i}]";
                var tgt = string.IsNullOrEmpty(f.TargetName) ? "" : $" target=\"{f.TargetName}\"";
                var rat = string.IsNullOrEmpty(f.Rationale) ? "" : $" — {Truncate(f.Rationale, 110)}";
                sb.AppendLine($"- {role}: kind={f.Kind} status={f.Status}{tgt}{rat}");
                if (f.Status == IntentLifecycle.Active)
                    hasActionableObjective = true;
            }
            if (hasActionableObjective)
                sb.AppendLine(
                    "- raw fact, not a recommendation: these are your OWN persistent objectives. An `Active` " +
                    "ancestor resurfaces as TOP when the frames above it pop — an earlier compiled task is NOT " +
                    "gone just because a newer frame sits on top, so do not treat the scene as having no directive " +
                    "while an `Active` one is listed above. Whether to pursue an unfinished one now is your call.");
            else
                sb.AppendLine(
                    "- raw fact, not a recommendation: every objective above has reached a terminal state " +
                    "(Blocked/Completed/Expired), so none is currently actionable; a `stack_ops` push or replace " +
                    "sets a new persistent objective if you want one. Your strategic call.");
        }

        // ── ## Settled turn-in (decision-proximate salience) ─────────────────
        // Live (cp053-observe): a turn-in intent whose target NPC is a SETTLED
        // stage-3 turn-in (the contract is DONE/pending-repeat with no hand-in — the
        // SAME recognition the `## Contracts` "DONE (stage 3)" note and the cp050
        // Motor Talk-drop share, via IsSettledStage3TurnInNpc) stays `Active` as the
        // TOP objective, so a weak model re-emits a Talk to that NPC every cycle. The
        // Motor drops each (cp050), but the LLM keeps burning a decision on the doomed
        // turn-in. The QUEST-DIALOG COMPILER rule already says to MARK_TOP_BLOCKED such
        // an intent, but that fact is buried far up the prompt; re-surface it in the
        // decision-proximate tail (the same salience pattern as `## Unspent XP`) so
        // the model resolves the dead objective and spends the turn on other progress.
        // Restricted to the TOP frame (like `## Unseen objective target` below):
        // MARK_TOP_BLOCKED acts on the TOP by identity, so nudging it while a settled
        // turn-in sits as an ANCESTOR would block the wrong (top) frame. Own intent
        // state + own contract stage + own goal history; no game knowledge, no
        // recommendation beyond the mechanical "this objective is settled".
        if (stack?.Top is { Status: IntentLifecycle.Active } settledTop
            && !string.IsNullOrEmpty(settledTop.TargetName)
            && IsSettledStage3TurnInNpc(world, events, settledTop.TargetName)
            && OneLine(settledTop.TargetName) is string settledObjectiveNpc)
        {
            sb.AppendLine();
            sb.AppendLine("## Settled turn-in");
            sb.AppendLine(
                $"- your TOP `Active` objective targets \"{settledObjectiveNpc}\", whose contract is " +
                "already DONE (stage 3) with no separate hand-in (see `## Contracts`). Re-Talking it " +
                "changes nothing. MARK_TOP_BLOCKED that turn-in intent (a durable marker) and spend the " +
                "turn on OTHER directed progress — re-attempting a settled turn-in is fixation, not " +
                "progress. Raw fact from your own stack + contract stage; your strategic call.");
        }

        // ── ## Unseen objective target (end-of-prompt salience capsule) ──────
        // Complements `## No active objective`: here there IS an Active top
        // intent, but its named target has NEVER entered the world model since
        // login (WasNameEverObserved == false) and the intent carries no resolved
        // TargetGuid — the bot is pursuing a name that has only ever appeared in
        // text, never as a real world object. Live runs show the LLM compiling a
        // dialog referral ("go talk to <name> in the next room") into an intent
        // and then Exploring indefinitely for a target that does not exist as an
        // object, burning decisions. Re-surface the raw never-observed fact in the
        // decision-proximate slot so it competes with the QUEST-DIALOG / SERVER-
        // INSTRUCTION rules that (correctly, in general) push toward a not-yet-
        // visible named target. Gated on raw presence (Active top + named target +
        // no resolved guid + never observed) plus a short settle window
        // (UnseenObjectiveTargetGrace) so a freshly-pushed objective whose room the
        // bot is still walking to is not flagged on the first tick. RAW fact + a
        // mechanical truth + explicit not-a-recommendation; no game knowledge, no
        // name list, no urgency — the LLM decides whether the name is real, was
        // flavour, or should be replaced.
        if (stack?.Top is { Status: IntentLifecycle.Active } unseenTop
            && unseenTop.TargetGuid is null
            && !string.IsNullOrEmpty(unseenTop.TargetName)
            && !world.WasNameEverObserved(unseenTop.TargetName))
        {
            var pursued = DateTime.UtcNow - unseenTop.Baseline.PushedAtUtc;
            if (pursued >= UnseenObjectiveTargetGrace)
            {
                var rawName = unseenTop.TargetName!;
                var name = rawName.Length > 60 ? rawName.Substring(0, 60) : rawName;
                var pursuedSec = (int)pursued.TotalSeconds;
                sb.AppendLine();
                sb.AppendLine("## Unseen objective target");
                sb.AppendLine(
                    $"- raw fact: your active objective (intent [{unseenTop.Id}] \"{unseenTop.Kind}\") names the " +
                    $"target '{name}', but no object named '{name}' has been observed anywhere in the world since " +
                    $"you logged in, across {pursuedSec}s of pursuing it.");
                sb.AppendLine(
                    "- mechanical fact: a Talk/Use/Attack/Pickup/Give goal can only act on an object that exists " +
                    "in the world; a name that appears only in text and never as a world object cannot be reached " +
                    "or acted on.");
                sb.AppendLine(
                    "- raw fact, not a recommendation: whether this name refers to a real object you have not " +
                    "reached yet, was flavour/lore in some text, or should be replaced (`stack_ops` replace_top/" +
                    "pop_top) is your strategic call from the facts above.");
            }
        }
        if (world.Self.AvailableExperience is long endcapUnspent
            && ShouldSurfaceUnspentXp(endcapUnspent, MinMeaningfulUnspentXp))
        {
            sb.AppendLine();
            sb.AppendLine("## Unspent XP");
            // RaiseSkill is only executable when a `trained skills` list is
            // present (its target must be one of those names); omit it here when
            // none is surfaced so this capsule cannot contradict the SPEND XP
            // rule's RaiseSkill guard.
            var endcapRaiseVerbs = world.Self.TrainedSkills is { Count: > 0 }
                ? "RaiseAttribute, RaiseVital, and RaiseSkill are"
                : "RaiseAttribute and RaiseVital are";
            sb.AppendLine(
                $"- you have {endcapUnspent} unspent experience available this tick; the goal " +
                $"verbs {endcapRaiseVerbs} executable right now, the " +
                "same as Talk/Use/Pickup/Attack/Explore.");
            // Co-locate the decision-proximate SURVIVABILITY facts with the
            // spend decision. The SAME max-HP and death facts already render in
            // `## Self`, but cp-2336 showed the LLM acts on end-capsule facts,
            // not the same facts buried earlier; surfacing them HERE, beside the
            // spendable XP, is exactly when the SPEND XP rule weighs
            // survivability against offense. RAW facts only, on RAW PRESENCE
            // (whenever the value is KNOWN — no magnitude/significance gate, per
            // the cp-2337 audit), with NO recommendation about which to raise
            // and NO restated mechanics (those live in the SPEND XP rule).
            var spendFacts = new List<string>();
            if ((world.Self.HealthObservedPeak ?? world.Self.HealthCurrent) is int endcapMaxHp)
                spendFacts.Add($"your health has peaked at {endcapMaxHp} HP this session (your observed maximum)");
            if (world.Self.NumDeaths is int endcapDeaths)
                spendFacts.Add($"you have died {endcapDeaths} times");
            // cp-2419: inline the bot's RAW base attribute values — the SAME
            // `{Name} {Base}` list the `## Self` section renders — beside the
            // spend decision. cp-2399 showed the LLM acts on a VALUE inlined in
            // THIS capsule, not the same value pointed-to in `## Self` ~22KB
            // earlier: surfacing max HP HERE flipped a mono-Strength bot to raise
            // Endurance. The OFFENSE side had the outcome (kills below) but its
            // attribute VALUES were only pointed-to, so the LLM could not weigh
            // (e.g.) a low coordination against its evaded swings when choosing
            // WHICH attribute to raise. ALL attributes, with NO offense/survival
            // selection in source (the SPEND XP rule supplies that mapping); RAW
            // values, no recommendation, no magnitude gate; no game knowledge.
            if (world.Self.Attributes is { Count: > 0 } endcapAttrs)
                spendFacts.Add(
                    $"your attributes are {string.Join(", ", endcapAttrs.Select(a => $"{a.Name} {a.Base}"))}");
            // cp-2410/cp-2411: the OFFENSE side of the SAME survivability-vs-
            // offense weighing. cp-2399 surfaced only the survival facts (max
            // HP/deaths), so live the L9 bot poured XP into endurance yet stayed
            // unable to KILL — it lands some hits but is out-damaged (0 kills).
            // cp-2410 counted only kinds recorded `ineffective` (the no-progress
            // stalemate), but live the kinds that WALL the bot record a `death`
            // or `near-death`, NOT `ineffective`, so that narrow gate never fired
            // and the offense signal stayed absent while the bot hoarded XP.
            // cp-2411 broadens it to every kind in the bot's OWN combat history
            // it has engaged but never killed (Kills == 0). Snapshot only keeps
            // rows with kills/deaths/near-deaths/ineffective > 0, so Kills == 0
            // here already means "fought it significantly, never beat it" — the
            // full can't-win-fights signal the LLM weighs (with the survival
            // facts above) when splitting XP between offense and endurance.
            // Co-locate it beside the spend decision so the SPEND XP rule's
            // accuracy-vs-damage mapping (coordination + weapon skill for
            // swings that evade/miss, strength for swings that land but barely
            // hurt) competes with the survival facts. RAW counts from the
            // combat-feel ledger, on RAW PRESENCE (an unkilled kind exists), NO
            // recommendation and NO magnitude gate; no game knowledge.
            if (world.CombatHistory is { Count: > 0 } endcapHist)
            {
                // endcapHist is the recency-capped Snapshot (most-recently-active
                // kinds), NOT the full ledger, so both counts are scoped to RECENT
                // combat and the wording must not overclaim a session "total".
                var endcapKills = endcapHist.Sum(h => h.Kills);
                var endcapUnkilledKinds = endcapHist.Count(h => h.Kills == 0);
                if (endcapUnkilledKinds > 0)
                    spendFacts.Add(
                        $"in recent combat you have {endcapKills} kill(s) and have fought " +
                        $"{endcapUnkilledKinds} monster kind(s) you have not killed");
            }
            // cp-2427: the offense-mechanism EVIDENCE that disambiguates the
            // failure mode the facts above only hint at. Deaths + max HP + the
            // "kinds not killed" count tell the LLM it is LOSING, but not WHY:
            // is its offense whiffing (swings evading) or is it connecting but
            // outlasted? Across models the bot poured XP into endurance while
            // its swings kept evading, because this capsule surfaced the
            // SURVIVAL side (deaths/max HP) yet never the RAW melee hit/evade
            // split — so the LLM had no evidence the problem was ACCURACY. The
            // bot's own session-cumulative landed-vs-evaded swing counts are
            // exactly that evidence; the SPEND XP rule already maps "how often
            // your swings land" to the attributes that drive it. RAW observed
            // outcomes, on RAW PRESENCE (any resolved swing), no magnitude gate,
            // NO recommendation about which attribute to raise; no game knowledge.
            var endcapSwingsLanded = world.CumulativeSwingsLanded;
            var endcapSwingsEvaded = world.CumulativeSwingsEvaded;
            var endcapHasSwings = endcapSwingsLanded + endcapSwingsEvaded > 0;
            if (endcapHasSwings)
                spendFacts.Add(
                    $"your melee swings this session have landed {endcapSwingsLanded} time(s) and " +
                    $"been evaded {endcapSwingsEvaded} time(s)");
            if (spendFacts.Count > 0)
                sb.AppendLine($"- raw fact: {string.Join("; ", spendFacts)}.");
            // cp2924: pointing at the SPEND XP rule (a far-away preamble bullet) is
            // weaker than re-stating its symptom->lever mapping AT the spend
            // decision (the cp-2336/2387 salience finding; cp2920 precedent for
            // re-stating an existing rule imperatively in a capsule). Live the bot
            // read its OWN evade-heavy split yet poured XP into endurance/strength
            // (HP/damage) and kept dying to the kind it could not hit, because the
            // mapping lived only in the distant rule. Re-state that EXISTING mapping
            // here, tied to the bot's own facts above; it names only the attribute/
            // skill vocabulary the SPEND XP rule already uses (no NPC/quest/item/
            // landblock name), and the allocation stays the LLM's call. Gated on the
            // bot having actually swung (so the text never references a landed-vs-
            // evaded split that did NOT render), and the weapon-skill clause mirrors
            // the rule's RaiseSkill guard (RaiseSkill is server-rejected when
            // `## Self` lists no trained skills).
            if (endcapHasSwings)
            {
                var hasTrainedSkill = world.Self.TrainedSkills is { Count: > 0 };
                // "Raise your trained WEAPON SKILL" only improves accuracy when the
                // WIELDED weapon is governed by a skill you have trained. The bot can
                // wield a weapon whose skill it has NOT trained (e.g. a thrown weapon
                // when its only trained weapon skill has no matching weapon); raising a
                // DIFFERENT trained skill does nothing for that weapon, so the lever
                // there is coordination. When the bot is fighting unarmed with NO weapon
                // to wield or buy anywhere, its swings are fists: the UnarmedCombat
                // to-hit is half STRENGTH + half coordination and unarmed DAMAGE is
                // STRENGTH-based, so the lever there is STRENGTH (favoured — it raises
                // both accuracy and damage), and a WEAPON SKILL governs a wielded weapon
                // it does not have, so RaiseSkill cannot help fists (see the `unarmed
                // accuracy` note). That unarmed branch fires regardless of trained skills
                // (so a weaponless no-trained-skills bot still gets the STRENGTH steer,
                // not the generic coordination-only text below). Gated on
                // HasNoUsableWeaponAnywhere so that if a usable weapon IS in the bag the
                // advice stays "raise your trained weapon skill" (the bot should wield
                // that weapon, and its skill then applies). The `wielded-weapon accuracy`
                // note (above) carries the wielded detail; here we just name the right
                // lever. "" = a weapon is wielded but its skill is unknown (still not a
                // confirmed trained skill); null = the wielded skill IS trained or no
                // weapon is wielded.
                var endcapUntrainedWieldedSkill = WieldedWeaponUntrainedSkillName(world);
                var endcapStuckUnarmed = WieldedMainWeapon(world) is null && HasNoUsableWeaponAnywhere(world);
                var accuracyLevers = endcapUntrainedWieldedSkill is string
                    ? "coordination (your WIELDED weapon's skill is NOT one of your `trained skills`, so `RaiseSkill` on a trained skill will NOT improve THIS weapon's hit rate — see the `wielded-weapon accuracy` note)"
                    : endcapStuckUnarmed
                    ? "STRENGTH or coordination (you have NO weapon wielded — your unarmed `UnarmedCombat` to-hit is half STRENGTH + half coordination, and a WEAPON SKILL does NOT govern fists, so `RaiseSkill` will NOT improve fist accuracy; favor STRENGTH since it ALSO raises fist DAMAGE — see the `unarmed accuracy` note; or arm a usable weapon and train its skill for a real upgrade)"
                    : hasTrainedSkill
                    ? "your trained WEAPON SKILL (the main accuracy lever, raised via `RaiseSkill` using a name from your `trained skills` in `## Self`) and coordination"
                    : "coordination (`## Self` lists no `trained skills`, so `RaiseSkill` is unavailable here — raise the attribute)";
                var hurtLever = "strength";
                // The "dying fast -> raise max HP" mapping is correct ONLY outside a
                // death-spiral. While spiraling (recentOwnDeathCount >= threshold) raising
                // max HP does NOT fix it (the penalty re-stacks + resets recovery), so point
                // at the `## Survival caution` escape instead — keeping this tail mapping
                // consistent with the caution + the SPEND XP DEATH-SPIRAL EXCEPTION.
                var dyingLever = recentOwnDeathCount >= DeathSpiralMinDeaths
                    ? "dying REPEATEDLY in quick succession is a DEATH-SPIRAL — raising max HP will NOT dig you out (see `## Survival caution`); retreat to safer content and earn XP WITHOUT dying"
                    : "dying FAST points to endurance/health";
                // For a WIELDED weapon, strength does not drive accuracy (the weapon skill +
                // coordination do), so it is the wrong accuracy lever. UNARMED is the exception:
                // fist to-hit is half STRENGTH, so strength IS an accuracy lever there (named in
                // accuracyLevers above) — omit the "strength is the wrong accuracy lever" caveat
                // when unarmed to avoid contradicting the unarmed mapping + the `unarmed accuracy` note.
                var strengthAccuracyCaveat = endcapStuckUnarmed
                    ? "."
                    : "; strength is the wrong lever for it too (strength's main effect is DAMAGE, not accuracy).";
                sb.AppendLine(
                    "- apply the SPEND XP rule to YOUR facts above (which to raise stays your call): swings being " +
                    $"EVADED (the landed-vs-evaded split) is an ACCURACY/miss problem — driven by {accuracyLevers}, " +
                    "and NOT fixed by endurance/health (which only raise max HP)" + strengthAccuracyCaveat +
                    $" Being OUT-DAMAGED after your hits LAND points to {hurtLever}; {dyingLever}. Pouring XP into max HP while your swings keep evading does NOT " +
                    "fix accuracy — read your own landed-vs-evaded and kills above and raise the lever your evidence " +
                    "points to.");
            }
            else
            {
                sb.AppendLine(
                    "- raw fact, not a recommendation: see `## Self` above for your trained skills, and " +
                    "the SPEND XP rule for what each verb does and how attributes affect survivability " +
                    "versus offense. Whether, how much, and which to raise is your call.");
            }
        }

        // ── ## Contracts (tracked-objective perception, end-of-prompt capsule) ─
        // Tracked contracts render HERE in the protected salience tail, not the
        // body, because the body's trailing sections are hard-cut first when the
        // prompt overflows the request ceiling — live, an object-dense town
        // pushed the body past 26000 and this section (previously rendered
        // mid-body) was guillotined before the LLM ever saw it. A tracked
        // objective (what it requires / where / who turns it in) is small and
        // decision-proximate, so it belongs with the other protected capsules.
        //
        // Each line is the server's contract tracker (numeric id + wire
        // ContractStage) enriched with the dat's own objective text
        // (ContractCatalog), looked up by id and surfaced verbatim. No
        // object-type priority/urgency in source and no source-side decision to
        // pursue or turn one in — the LLM decides (it reads each contract's
        // stage code and chooses). Rows are emitted in WIRE ORDER (NOT ranked —
        // source must not judge which objective matters) and bounded by a TOTAL
        // char budget so the protected tail stays well under the ceiling and can
        // never force the body hard-cut to eat the fixed rules preamble; the
        // first row is always emitted so at least one contract stays visible. A
        // `(+N more)` count note tells the LLM its view is partial.
        if (world.Contracts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Contracts");
            sb.AppendLine(
                "- tracked objectives (stage code: 1 available, 2 in progress, " +
                "3 done or pending repeat, 4+ in progress with a step counter):");
            var contractsShown = 0;
            var contractsChars = 0;
            // selfXY + per-row entry building are shared with AnyRenderedContractBearing
            // (see BuildContractEntry) so the rendered-bearing gate cannot drift from
            // what this capsule shows.
            var selfXY = ContractSelfXY(world);
            var anyContractBearing = false;
            foreach (var c in world.Contracts)
            {
                var entry = BuildContractEntry(c, world, events, selfXY, out var hasBearingThisContract);
                // Always render the first contract; stop once the rows would
                // exceed the capsule's char budget (the tail is non-trimmable,
                // so it must self-limit).
                if (contractsShown > 0 && contractsChars + entry.Length > ContractsProtectedCharBudget)
                    break;
                // Only now that this row is actually kept may its bearing license
                // the direction instruction below — otherwise a budget-dropped row
                // could leave the instruction referencing a bearing not shown.
                if (hasBearingThisContract)
                    anyContractBearing = true;
                sb.Append(entry);
                contractsChars += entry.Length;
                contractsShown++;
            }
            if (contractsShown < world.Contracts.Count)
                sb.AppendLine($"  - (+{world.Contracts.Count - contractsShown} more tracked, not shown)");
            // Decision-proximate REFRESH cue: when EVERY tracked contract is DONE
            // (stage 3 — the batch is finished and earns no more), a `vendor` is
            // in `## Visible nearby`, AND no vendor panel is open yet (world.Vendor
            // is null), surface the OPEN-the-vendor refresh action. When a panel IS
            // already open, the open-panel cue below routes the refresh to `Buy`
            // (re-`Use` would be a no-op), so this closed-panel variant is gated
            // off to avoid telling the bot to `Use` an already-open vendor. Wire
            // facts only (all-stage-3 + vendor-in-view + panel-closed) + the
            // generic buy mechanic; the LLM decides. No NPC/contract name.
            // Combat-readiness gate (cp gate-contract-cues-unarmed): a fresh-contract
            // refresh BUY competes with the SELF-ARM loot-to-arm hunt when UNARMED, so
            // suppress this nudge until the bot is combat-effective. Mechanical gate.
            if (HeldBatchAllDone(world) && world.Vendor is null
                && world.Visible.Any(v => v.IsVendor)
                && selfArmCombatEffective)
                sb.AppendLine(
                    "- a fresh contract to keep earning is BOUGHT at a `vendor`, not received by " +
                    "`Talk`ing: a `vendor` is in `## Visible nearby`, so `Use` it to reveal its " +
                    "`## Vendor offerings`, and if those list a contract, `Buy` one to take new work " +
                    "(if it offers none, not every vendor sells contracts — move on).");
            if (anyContractBearing)
                sb.AppendLine(
                    "- to TRAVEL to an `objective area` or `turn-in location` above that is NOT yet in " +
                    "`## Visible nearby`, do NOT emit an undirected `Explore` (it drifts and can stall against " +
                    "terrain): emit `Explore{target: {name: \"<the place or the turn-in NPC>\"}, direction: " +
                    "\"<the compass word from that bearing>\"}` — copy the bearing's compass word verbatim (one of " +
                    "n/ne/e/se/s/sw/w/nw; a bearing ending `SW` means `direction: \"sw\"`) so the bot COMMITS that " +
                    "heading and travels toward it; keep heading that same bearing each tick until it enters " +
                    "`## Visible nearby`, then `Talk`/`Use`/`Give` it.");
            sb.AppendLine(
                "- raw fact, not a recommendation: whether to pursue an objective " +
                "or turn one in is your call.");
        }

        // render the `## Corpse` prompt section (see AppendCorpseRecovery).
        AppendCorpseRecovery(sb, world);

        // ── ## Vendor offerings (open-vendor perception, end-of-prompt capsule) ─
        // When the bot has a vendor trade panel open (it Used/Talked a vendor),
        // render WHAT that vendor sells — each item's name + the cost to buy it
        // + stack size — so the LLM can decide whether to buy. The cost mirrors
        // the server's Vendor.GetSellCost EXACTLY (including its fixed-rate
        // override for one item type and its float multiply) and is stated in
        // the vendor's actual currency unit (coin, or an alternate currency).
        // Protected salience tail (survives the body hard-cut), beside the other
        // end-capsules. Rows are WIRE ORDER (NOT ranked — source must not judge
        // which item matters) and bounded by a TOTAL char budget so a big vendor
        // list can't evict the rules preamble; the first row is always emitted.
        // No object-type priority/urgency in source and no source-side decision
        // to buy — the LLM decides. The closing note that SOME for-sale items
        // are task contracts is a GENERIC game-mechanic fact (a vendor can sell
        // a contract item that, once Used, registers a directed task); it names
        // no specific contract, NPC, or area and the LLM identifies any such
        // item itself from the rendered names — so it carries no hardcoded game
        // knowledge and no decision to buy.
        if (world.Vendor is { } vendor && vendor.Offers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Vendor offerings");
            sb.AppendLine("- the vendor you have open is selling these:");
            // The currency unit: coin for a normal vendor, else the vendor's
            // alternate currency (raw wire fact; the server charges the SAME
            // GetSellCost amount, only the unit differs).
            var vendorUnit = vendor.AlternateCurrency == 0u
                ? "coin"
                : (OneLine(vendor.AlternateCurrencyName) ?? "alternate currency");
            var vendorShown = 0;
            var vendorChars = 0;
            foreach (var offer in vendor.Offers)
            {
                var entry = new StringBuilder();
                var name = OneLine(offer.Name) ?? "(unnamed)";
                // Tag a buyable arm so an UNARMED bot can identify one to buy: a MELEE
                // weapon (directly wieldable) or a MISSILE-bit offer. A missile-bit
                // offer cannot be told apart at the offer level (it has no slot/ammo
                // data) — it may be a thrown weapon, a launcher, or ammo — but ALL are
                // arming inputs: once BOUGHT into the bag, the existing self-arm
                // affordances classify it (a thrown weapon surfaces as "throwable
                // weapon"; a launcher+compatible-ammo as a ranged loadout). So tag it
                // generically so the bot knows it is worth buying to arm. Pure wire-bit
                // (ItemType) projection; no priority, the LLM still decides whether to buy.
                var weaponTag =
                    (offer.ItemType & ItemTypeMasks.MeleeWeapon) != 0 ? " [weapon]"
                    : (offer.ItemType & ItemTypeMasks.MissileWeapon) != 0 ? " [missile weapon/ammo]"
                    : "";
                if (offer.Value is uint val)
                {
                    // Mirror Vendor.GetSellCost: max(1, ceil((float)rate*value -
                    // 0.1)), where rate is the vendor's SellPrice EXCEPT the
                    // server forces a fixed rate for one item type. Keep the
                    // multiply in float — the server computes (float)rate*value,
                    // so widening to double would DIVERGE from what it charges.
                    var rate = offer.ItemType == ItemTypePromissoryNote
                        ? PromissoryNoteSellRate
                        : (float)vendor.BuyCostMultiplier;
                    var cost = Math.Max(1L,
                        (long)Math.Ceiling((double)(rate * val) - 0.1));
                    entry.Append($"  - {name}{weaponTag}: {cost} {vendorUnit} to buy (value {val})");
                    // Affordability marker for a COIN vendor (AlternateCurrency
                    // == 0u, so CoinValue is the currency): append it when the
                    // item's cost exceeds the bot's server-tracked CoinValue, and
                    // only when CoinValue is known. Pure wire-fact comparison (the
                    // computed cost vs CoinValue); the LLM still decides.
                    if (vendor.AlternateCurrency == 0u
                        && world.Self.CoinValue is int coinHave && cost > coinHave)
                        entry.Append($" — you have {coinHave} coin, CANNOT AFFORD");
                }
                else
                {
                    entry.Append($"  - {name}{weaponTag}");
                }
                if (offer.StackSize > 1)
                    entry.Append($", sold in stacks of {offer.StackSize}");
                entry.AppendLine();

                if (vendorShown > 0 && vendorChars + entry.Length > VendorOfferingsProtectedCharBudget)
                    break;
                sb.Append(entry);
                vendorChars += entry.Length;
                vendorShown++;
            }
            if (vendorShown < vendor.Offers.Count)
                sb.AppendLine($"  - (+{vendor.Offers.Count - vendorShown} more for sale, not shown)");
            sb.AppendLine(
                "- raw fact, not a recommendation: whether to buy anything, and " +
                "what, is your call. To buy one, emit a Buy goal with " +
                "target.name set to an item's exact name above (it works only " +
                "while you have this vendor open; quantity defaults to 1).");
            // This capsule renders ONLY while the vendor's trade panel is OPEN
            // (world.Vendor is set), so a fresh Use of the SAME vendor is a no-op
            // — the panel is already open and its list does not change. State that
            // so the LLM acts via Buy/Sell instead of re-emitting Use on the open
            // vendor. Buy targets a name from the list above; Sell targets a name
            // from ## Inventory. A recovery clause covers the case where the panel
            // is no longer live. Prompt text only; the LLM still decides.
            sb.AppendLine(
                "- the items above are the live offering of the vendor whose panel is OPEN. Act here with " +
                "`Buy` (an item by its exact name from the list ABOVE — a task contract counts, Buying it " +
                "takes new work) or `Sell` (an item by its exact name from your `## Inventory`). A fresh " +
                "`Use` on this same vendor only re-opens the already-open panel and shows nothing new, so " +
                "reach for `Buy`/`Sell` to make progress. (If a `Buy`/`Sell` reports no live panel, a single " +
                "`Use` re-opens it.)");
            sb.AppendLine(
                "- some for-sale items are TASK CONTRACTS: a directed task " +
                "(often a hunting/kill task — clearing a den or area of " +
                "monsters) that you accept by Buying the item and then Using " +
                "it from your pack (a Use goal on its name once it is in your " +
                "## Inventory). Accepting one gives a concrete directed " +
                "objective plus a reward on turn-in. Raw fact, not a " +
                "recommendation: whether to is your call.");
            // Renders a Buy-the-contract bridge prompt line. Gated on the bot's
            // OWN contract state (no tracked contract, or all tracked at the
            // terminal stage) AND an open vendor in perception; names no specific
            // contract (the LLM picks which, if any). Prompt text only; no
            // source-side decision to buy.
            // Combat-readiness gate (cp gate-contract-cues-unarmed): the open-panel
            // BUY-a-contract bridge competes with the SELF-ARM loot-to-arm hunt when
            // UNARMED, so suppress it until the bot is combat-effective — matching the
            // closed-panel refresh + FIND/RETURN gates. Mechanical render gate.
            if ((world.Contracts.Count == 0 || heldBatchAllDone) && selfArmCombatEffective)
                sb.AppendLine(
                    "- you have NO unfinished task contract right now and this vendor is OPEN: if any offering " +
                    "above is a TASK CONTRACT, the way to take new work is to BUY one here — emit " +
                    "`Buy{target: {name: \"<that contract's exact name>\"}}`, then `Use` it from your `## Inventory` " +
                    "to accept the task. Re-`Talk`ing town NPCs does NOT give you a new contract; BUYING one from " +
                    "this broker does. If you have ALREADY bought a task contract and it is sitting in your " +
                    "`## Inventory` unaccepted, `Use` it to accept the task INSTEAD of buying another. (Whether and " +
                    "which to buy is your call; health-critical safety and any active server/quest directive come first.)");

            // Renders the Sell-goal capability + its exact goal shape as an
            // LLM-facing prompt line, in the open-vendor slot beside the Buy
            // guidance. Emitted whenever a vendor panel is open; names no item —
            // the LLM selects any item from its own ## Inventory. No source-side
            // decision to sell.
            sb.AppendLine(
                "- to RAISE COIN, you can SELL bagged items to this OPEN vendor: emit " +
                "`Sell{target: {name: \"<an exact item name from ## Inventory>\"}}` (works only while this " +
                "vendor is open; quantity defaults to 1). Selling ALWAYS pays you in coin (even at a vendor " +
                "that charges an alternate currency to buy), and removes that item from your pack. Use this " +
                "when you cannot AFFORD a coin-priced offering you want — e.g. a Buy keeps failing because you " +
                "lack the coin: sell spare/unneeded items to build up coin, then Buy. A vendor only buys " +
                "certain item types, so if a Sell is refused, sell that item at a different vendor. Raw " +
                "mechanic, not a recommendation — WHAT (if anything) to sell is your call; do not sell gear " +
                "you are using or an item a directive/quest needs.");
        }

        // ── ## Monsters in view (end-of-prompt salience capsule) ─────────
        // Visible monsters already render mid-prompt (`## Visible nearby`,
        // `## Combat readiness`), but a fact placed earlier in this large
        // prompt competes for attention with everything after it; re-stating
        // the SAME computed perception in the most decision-proximate slot is
        // the established salience fix shared by the `## Unspent XP` and
        // `## Recent Talk` capsules. Rendered on RAW PRESENCE (any non-corpse
        // monster visible — no source-side threshold, mirroring the
        // `## Unspent XP` any-positive gate). RAW facts + an explicit
        // not-a-recommendation disclaimer — no urgency, no instruction to
        // engage, no object-type priority in source: the existing "a monster
        // is a valid XP target" RULE and the `## Combat readiness` history
        // supply the judgment, the LLM decides. Perception re-positioned for
        // salience; no game knowledge.
        var endcapMonsters = world.Visible
            .Where(v => !v.IsCorpse && (v.IsMonster || v.ObservedHostile))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .ToList();
        if (endcapMonsters.Count > 0)
        {
            var endcapGroups = endcapMonsters
                .GroupBy(v => string.IsNullOrWhiteSpace(v.Name) ? "(unknown)" : v.Name!,
                         StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            var endcapMonsterList = string.Join(", ", endcapGroups
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
            var endcapNearest = endcapMonsters[0];
            var endcapNearestName =
                string.IsNullOrWhiteSpace(endcapNearest.Name) ? "(unknown)" : endcapNearest.Name!;
            var endcapNearestDist =
                endcapNearest.Distance is float d ? $"at d={d:F1}u" : "at an unknown distance";
            sb.AppendLine();
            sb.AppendLine("## Monsters in view");
            sb.AppendLine(
                $"- {endcapMonsters.Count} attackable monster(s) in view ({endcapMonsterList}); " +
                $"nearest '{endcapNearestName}' {endcapNearestDist}. The Attack verb " +
                "is executable right now, the same as Talk/Use/Pickup/Explore.");
            // Per-kind LEARNED record, inline in this PROTECTED capsule so the
            // bot's own combat-feel outcomes survive even when the body
            // `## Combat readiness` section is hard-cut under the request
            // ceiling. Live academy failure: `## Combat readiness` (which holds
            // the per-kind record) was cut in 93% of combat prompts, so the bot
            // kept attacking a kind its OWN record showed it could not damage
            // (0 kills, all swings evaded). Raw counts only, from the bot's own
            // observed outcomes (CombatFeelLedger via FormatCombatRecordFor); no
            // game knowledge, no priority, no danger label — the existing COMBAT
            // SAFETY rule supplies the judgment. Only kinds that HAVE a record
            // render a row (FormatCombatRecordFor returns "" otherwise).
            foreach (var g in endcapGroups)
            {
                var rec = FormatCombatRecordFor(world.CombatHistory, g.First().Wcid, g.Key);
                if (rec.Length == 0)
                    continue;
                var gNearest = g.Min(v => v.Distance ?? float.MaxValue);
                var gDist = gNearest < float.MaxValue ? $" nearest d={gNearest:F1}u" : "";
                sb.AppendLine($"- {g.Key} x{g.Count()}{gDist}{rec}");
            }
            sb.AppendLine(
                "- raw fact, not a recommendation: any `[your record]` above is your OWN past " +
                "outcome vs that kind this session and across sessions (see also `## Combat " +
                "readiness`, when shown, for arms/health). Whether to engage, and which target, " +
                "is your call.");
        }

        // ── ## Nearest objects (protected nearest-object guarantee, cp-2367)
        // The mid-prompt `## Visible nearby` section is in PromptTrimOrder, so
        // the global request-size fitter (FitPromptToCeiling) can strip ALL of
        // its rows when an object-dense scene overflows the ceiling, leaving the
        // LLM with NO per-object perception (only the `## Combat readiness`
        // summary). Re-surface the nearest few objects in the protected salience
        // tail (which the fitter never trims) so the closest objects always
        // survive. Nearest-first by DISTANCE only (object-type-NEUTRAL; no type
        // priority — the LLM decides what matters); reuses the same audit-blessed
        // row renderer as `## Visible nearby`. Self-bounded to a small TOTAL char
        // budget so the protected tail stays well under the ceiling and can never
        // force the body hard-cut to eat the fixed rules preamble. Pure request-
        // size management by structural position (cp-2343 class); no game
        // knowledge.
        var nearestShownGuids = new HashSet<uint>();
        if (world.Visible.Count > 0)
        {
            var nearestRows = new List<string>();
            int nearestChars = 0;
            foreach (var v in world.Visible.OrderBy(v => v.Distance ?? float.MaxValue))
            {
                var row = ClampRow(RenderVisibleRow(v, world.CombatHistory, world.OpenedCorpseGuids));
                if (nearestRows.Count > 0 &&
                    nearestChars + row.Length + 1 > NearestObjectsProtectedCharBudget)
                    break;
                nearestRows.Add(row);
                nearestChars += row.Length + 1;
                nearestShownGuids.Add(v.Guid);
            }
            sb.AppendLine();
            sb.AppendLine("## Nearest objects");
            sb.AppendLine(
                $"- the {nearestRows.Count} nearest visible object(s) by distance, kept here so they " +
                "survive the prompt budget even when `## Visible nearby` above is trimmed away; the full " +
                "list is there when it fits:");
            foreach (var row in nearestRows)
                sb.AppendLine(row);
        }

        // ── ## Untalked NPCs nearby (protected salience capsule) ──────────
        // The same established salience fix as the `## Monsters in view`,
        // `## Recent Talk`, and `## Nearest objects` protected capsules: re-
        // surface an ALREADY-COMPUTED perception in the protected tail so it
        // survives the request-size fitter. The "untalked npcs in view: N"
        // recency line (CountUntalkedNpcsInView) already exposes this exact
        // perception class — visible creatures the bot has not Talked this
        // session — as a COUNT; this capsule lists their NAMES so that count is
        // actionable, because the model names its Talk target and a name it
        // never sees is a target it cannot reach. `## Nearest objects` above is
        // distance-capped and object-type-neutral, so in a creature-dense scene
        // a not-yet-Talked NPC beyond that cutoff is dropped once the body
        // `## Visible nearby` section is trimmed. Identity is mechanical only:
        // wire creature flags plus the session talked-set bookkeeping (the
        // bot's own emissions) — the same signals behind the count line. Rows
        // already shown by `## Nearest objects` are skipped to avoid pure
        // duplication. No object-type priority and no urgency in source: the
        // existing RULES (the same ones that already act on the untalked-npc
        // count) supply the judgment of whether and when to Talk, and the LLM
        // decides. Self-bounded char budget so the protected tail stays well
        // under the ceiling. Perception re-positioned for salience; no game
        // knowledge.
        if (world.Visible.Count > 0)
        {
            var untalkedRows = new List<string>();
            int untalkedChars = 0;
            foreach (var v in world.Visible
                         .Where(v =>
                             v.IsCreature && !v.IsMonster && !v.IsCorpse && !v.ObservedHostile
                             && !nearestShownGuids.Contains(v.Guid)
                             && !IsNpcAlreadyTalked(v, talkedNpcGuids, talkedNpcNames))
                         .OrderBy(v => v.Distance ?? float.MaxValue))
            {
                var row = ClampRow(RenderVisibleRow(v, world.CombatHistory, world.OpenedCorpseGuids));
                if (untalkedRows.Count > 0 &&
                    untalkedChars + row.Length + 1 > UntalkedNpcsProtectedCharBudget)
                    break;
                untalkedRows.Add(row);
                untalkedChars += row.Length + 1;
            }
            if (untalkedRows.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Untalked NPCs nearby");
                sb.AppendLine(
                    $"- {untalkedRows.Count} visible npc(s) you have NOT Talked this session that are " +
                    "not already listed above, kept here so they stay legible by name even when " +
                    "`## Visible nearby` is trimmed; nearest-first by distance:");
                foreach (var row in untalkedRows)
                    sb.AppendLine(row);
                sb.AppendLine(
                    "- raw fact, not a recommendation: these are the same not-yet-Talked visible npcs " +
                    "as the `untalked npcs in view` count above. The goal verbs Talk, Use, Pickup, " +
                    "Attack, and Explore all remain executable right now. Your call.");
            }
        }

        // ── ## Recent Talk (end-of-prompt salience capsule) ──────────────
        // The per-NPC recent-Talk counts already render mid-prompt in
        // `## Location & recency`, yet live runs show the LLM re-Talking the
        // same NPC 6-7 times in a row with rationales that show ZERO
        // awareness of the repeat count (the model treats each Talk as if it
        // will yield a new result) — the same burial pattern the `## Unspent
        // XP` capsule fixed: a fact present ~20KB earlier is out-competed by
        // the parked local affordance. Re-surface the SAME computed counts in
        // the most decision-proximate slot so the repeat is visible right
        // before the model answers. Rendered whenever any recent Talk exists (no
        // source-side significance threshold — the raw counts are exposed and
        // the LLM judges significance, mirroring the `## Unspent XP` capsule's
        // any-positive gate). RAW counts + an explicit not-a-recommendation
        // disclaimer — no urgency wording, no "stuck", no instruction to
        // pivot: the LLM decides. No game knowledge; perception
        // re-positioned for salience. Same-display/different-guid NPCs get a
        // guid disambiguator, mirroring the `## Location & recency` render.
        if (talkByKey.Count > 0)
        {
            var endcapDupDisplays = talkByKey.Values
                .GroupBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var endcapTalkList = string.Join(", ", talkByKey
                .OrderByDescending(p => p.Value.Count)
                .Select(p =>
                {
                    var label = endcapDupDisplays.Contains(p.Value.Display) && p.Value.Guid is not null
                        ? $"{p.Value.Display} ({p.Value.Guid})"
                        : p.Value.Display;
                    return $"{label} x{p.Value.Count}";
                }));
            sb.AppendLine();
            sb.AppendLine("## Recent Talk");
            sb.AppendLine(
                $"- in your last 10 emitted goals you emitted Talk to: {endcapTalkList}.");
            sb.AppendLine(
                "- raw fact, not a recommendation. The goal verbs Talk, Use, Pickup, Attack, and " +
                "Explore all remain executable right now. See `## Location & recency` above. Your call.");
        }

        // ── ## Recent Use (end-of-prompt salience capsule) ───────────────
        // The per-target recent-Use counts already render mid-prompt in
        // `## Location & recency`, yet live runs show the LLM re-Using the
        // SAME world object many times with rationales that show ZERO
        // awareness of the repeat (it reasons as if each Use opens a new
        // outcome even when the prior Uses produced no lasting change). This
        // is the SAME burial pattern the `## Unspent XP` and `## Recent Talk`
        // capsules fixed: a fact present ~20KB earlier is out-competed by the
        // parked local affordance. Re-surface the SAME emission history in the
        // most decision-proximate slot so the repeat is visible right before
        // the model answers. Mirrors the `talkByKey` construction exactly but
        // for the Use verb: key identity by the guid token when present (else
        // name, else verbatim selector), DISPLAY prefers the human name,
        // identical displays across DISTINCT guids get a guid disambiguator.
        // Rendered whenever any recent Use exists (no source-side significance
        // threshold — the raw counts are exposed and the LLM judges, mirroring
        // the other end-capsules' any-positive gate). RAW counts + an explicit
        // not-a-recommendation disclaimer — no urgency wording, no "loop", no
        // "dead end", no instruction to pivot: the LLM decides. No game
        // knowledge; perception re-positioned for salience.
        var useByKey = new Dictionary<string, (int Count, string Display, string? Guid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Use ", StringComparison.Ordinal)) continue;
            var sm = System.Text.RegularExpressions.Regex.Match(txt, "target=(.*?) item=.*? source=");
            if (!sm.Success) continue;
            var sel = sm.Groups[1].Value.Trim();
            if (sel.Length == 0 || sel == "<empty>") continue;
            var gm = System.Text.RegularExpressions.Regex.Match(sel, "guid=0x[0-9A-Fa-f]+");
            var nm = System.Text.RegularExpressions.Regex.Match(sel, "name=\"([^\"]+)\"");
            var key = gm.Success ? gm.Value : (nm.Success ? nm.Groups[1].Value : sel);
            var display = nm.Success ? nm.Groups[1].Value : (gm.Success ? gm.Value : sel);
            if (useByKey.TryGetValue(key, out var cur))
            {
                var betterDisplay = cur.Display.StartsWith("guid=", StringComparison.Ordinal) && nm.Success
                    ? display : cur.Display;
                useByKey[key] = (cur.Count + 1, betterDisplay, cur.Guid ?? (gm.Success ? gm.Value : null));
            }
            else
            {
                useByKey[key] = (1, display, gm.Success ? gm.Value : null);
            }
        }
        if (useByKey.Count > 0)
        {
            var endcapUseDupDisplays = useByKey.Values
                .GroupBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var endcapUseList = string.Join(", ", useByKey
                .OrderByDescending(p => p.Value.Count)
                .Select(p =>
                {
                    var label = endcapUseDupDisplays.Contains(p.Value.Display) && p.Value.Guid is not null
                        ? $"{p.Value.Display} ({p.Value.Guid})"
                        : p.Value.Display;
                    return $"{label} x{p.Value.Count}";
                }));
            sb.AppendLine();
            sb.AppendLine("## Recent Use");
            sb.AppendLine(
                $"- in your last 10 emitted goals you emitted Use on: {endcapUseList}.");
            sb.AppendLine(
                "- raw fact, not a recommendation. The goal verbs Talk, Use, Pickup, Attack, and " +
                "Explore all remain executable right now. See `## Location & recency` above. Your call.");
        }
        // ── ## Recent Give (end-of-prompt salience capsule) ──────────────
        // Mirrors ## Recent Use/## Recent Talk for the Give verb. Live academy
        // runs show the LLM re-emitting Give of the SAME item to the SAME
        // recipient many times even AFTER the give succeeded and the item left
        // inventory (it reasons as if the give is still pending). Re-surface the
        // recent Give emission history — keyed by the item identity, displayed as
        // "<item> to <recipient>" — in the decision-proximate slot so the repeat
        // is visible right before the model answers. Same construction as
        // useByKey but parsing the item selector (the give subject). RAW counts +
        // a not-a-recommendation disclaimer that points to ## Inventory (where the
        // LLM can see whether it still holds the item); no urgency, no "loop", no
        // game knowledge.
        var giveByKey = new Dictionary<string, (int Count, string Display, string? Guid, string? ItemName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Give ", StringComparison.Ordinal)) continue;
            var im = System.Text.RegularExpressions.Regex.Match(txt, "item=(.*?) source=");
            if (!im.Success) continue;
            var itemSel = im.Groups[1].Value.Trim();
            if (itemSel.Length == 0 || itemSel == "<empty>") continue;
            // Recipient name (for display only) from the target selector.
            var tsel = System.Text.RegularExpressions.Regex.Match(txt, "target=(.*?) item=");
            var tnm = tsel.Success
                ? System.Text.RegularExpressions.Regex.Match(tsel.Groups[1].Value, "name=\"([^\"]+)\"")
                : System.Text.RegularExpressions.Match.Empty;
            var gm = System.Text.RegularExpressions.Regex.Match(itemSel, "guid=0x[0-9A-Fa-f]+");
            var nm = System.Text.RegularExpressions.Regex.Match(itemSel, "name=\"([^\"]+)\"");
            var key = gm.Success ? gm.Value : (nm.Success ? nm.Groups[1].Value : itemSel);
            var itemDisplay = nm.Success ? nm.Groups[1].Value : (gm.Success ? gm.Value : itemSel);
            var itemName = nm.Success ? nm.Groups[1].Value : null;
            var display = tnm.Success ? $"{itemDisplay} to {tnm.Groups[1].Value}" : itemDisplay;
            if (giveByKey.TryGetValue(key, out var cur))
            {
                var betterDisplay = cur.Display.StartsWith("guid=", StringComparison.Ordinal) && nm.Success
                    ? display : cur.Display;
                giveByKey[key] = (cur.Count + 1, betterDisplay, cur.Guid ?? (gm.Success ? gm.Value : null), cur.ItemName ?? itemName);
            }
            else
            {
                giveByKey[key] = (1, display, gm.Success ? gm.Value : null, itemName);
            }
        }
        if (giveByKey.Count > 0)
        {
            var endcapGiveDupDisplays = giveByKey.Values
                .GroupBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var endcapGiveList = string.Join(", ", giveByKey
                .OrderByDescending(p => p.Value.Count)
                .Select(p =>
                {
                    var label = endcapGiveDupDisplays.Contains(p.Value.Display) && p.Value.Guid is not null
                        ? $"{p.Value.Display} ({p.Value.Guid})"
                        : p.Value.Display;
                    return $"{label} x{p.Value.Count}";
                }));
            sb.AppendLine();
            sb.AppendLine("## Recent Give");
            sb.AppendLine(
                $"- in your last 10 emitted goals you emitted Give on: {endcapGiveList}.");
            sb.AppendLine(
                "- raw fact, not a recommendation. A Give moves an item from your inventory to a recipient, " +
                "so it requires that item to still be in `## Inventory` above; the goal verbs Talk, Use, " +
                "Pickup, Attack, and Explore also remain executable right now. Your call.");
            // Spent-Give backstop. Live (a fresh-character run that finished the
            // starting area; the concrete trace is in this slice's commit body):
            // after a turn-in Give SUCCEEDED and the bot changed zones, the LLM
            // re-emitted that same Give many times in the next zone where neither
            // the item nor the recipient existed (each resolving to no target) — a
            // wasted deliberation per tick. The passive disclaimer above did not
            // stop it. When the bot has emitted a Give for a NAMED item >=2 times
            // AND that item is NO LONGER in inventory, the Give cannot succeed now
            // (the item left the pack — often because the Give already went
            // through) — surface a decision-proximate, actionable fact so the loop
            // ends. Pure compare of the bot's OWN emitted-Give history against its
            // OWN inventory (same OneLine normalization on both sides); no item/NPC
            // literals, no priority, the LLM still chooses what to do instead.
            var heldNamesLc = new HashSet<string>(
                world.Inventory
                    .Select(i => OneLine(i.Name))
                    .Where(n => n is not null)
                    .Select(n => n!.ToLowerInvariant()),
                StringComparer.Ordinal);
            var spentGiveDisplays = giveByKey.Values
                .Where(v => v.Count >= 2
                    && OneLine(v.ItemName) is string inm
                    && !heldNamesLc.Contains(inm.ToLowerInvariant()))
                .OrderByDescending(v => v.Count)
                .Select(v => v.Display)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (spentGiveDisplays.Count > 0)
                sb.AppendLine(
                    $"- you have repeatedly emitted Give for {string.Join(", ", spentGiveDisplays)}, but that " +
                    "item is NOT in `## Inventory` — you do NOT currently hold it, so you CANNOT Give it right " +
                    "now (a Give needs the item in your pack; if you already handed it over, that step is " +
                    "finished). Stop re-emitting this Give — re-acquire the item FIRST if you still intend to " +
                    "give it, otherwise pursue DIFFERENT progress (hunt a `monster`, `Talk` an un-talked NPC, " +
                    "`Explore` onward, or act on a `## Held-item objectives` entry).");
        }
        // ── ## Recent Pickup (end-of-prompt salience capsule) ────────────
        // Mirrors ## Recent Use/## Recent Give/## Recent Talk for the Pickup
        // verb. Live academy runs show the LLM re-emitting Pickup of the SAME
        // ground item many times when the pickup never sticks (the item stays on
        // the ground, 0 inventory add). The cp-2290 `## Recently interacted
        // objects` capsule (which cp-2375 annotates) does NOT cover this: a Pickup
        // does not emit a WorldObjectInteracted echo (only Use/Talk do), so that
        // capsule never renders the looped item. Re-surface the recent Pickup
        // emission history — keyed by the item identity (the goal's target) — in
        // the decision-proximate slot so the repeat is visible right before the
        // model answers. Same construction as useByKey. RAW counts + a
        // not-a-recommendation disclaimer pointing to ## Inventory / ## Visible
        // nearby (where the LLM can see whether the item actually arrived); no
        // urgency, no "loop", no game knowledge.
        var pickupByKey = new Dictionary<string, (int Count, string Display, string? Guid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Pickup ", StringComparison.Ordinal)) continue;
            var sm = System.Text.RegularExpressions.Regex.Match(txt, "target=(.*?) item=.*? source=");
            if (!sm.Success) continue;
            var sel = sm.Groups[1].Value.Trim();
            if (sel.Length == 0 || sel == "<empty>") continue;
            var gm = System.Text.RegularExpressions.Regex.Match(sel, "guid=0x[0-9A-Fa-f]+");
            var nm = System.Text.RegularExpressions.Regex.Match(sel, "name=\"([^\"]+)\"");
            var key = gm.Success ? gm.Value : (nm.Success ? nm.Groups[1].Value : sel);
            var display = nm.Success ? nm.Groups[1].Value : (gm.Success ? gm.Value : sel);
            if (pickupByKey.TryGetValue(key, out var cur))
            {
                var betterDisplay = cur.Display.StartsWith("guid=", StringComparison.Ordinal) && nm.Success
                    ? display : cur.Display;
                pickupByKey[key] = (cur.Count + 1, betterDisplay, cur.Guid ?? (gm.Success ? gm.Value : null));
            }
            else
            {
                pickupByKey[key] = (1, display, gm.Success ? gm.Value : null);
            }
        }
        if (pickupByKey.Count > 0)
        {
            var endcapPickupDupDisplays = pickupByKey.Values
                .GroupBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var endcapPickupList = string.Join(", ", pickupByKey
                .OrderByDescending(p => p.Value.Count)
                .Select(p =>
                {
                    var label = endcapPickupDupDisplays.Contains(p.Value.Display) && p.Value.Guid is not null
                        ? $"{p.Value.Display} ({p.Value.Guid})"
                        : p.Value.Display;
                    return $"{label} x{p.Value.Count}";
                }));
            sb.AppendLine();
            sb.AppendLine("## Recent Pickup");
            sb.AppendLine(
                $"- in your last 10 emitted goals you emitted Pickup on: {endcapPickupList}.");
            sb.AppendLine(
                "- raw fact, not a recommendation. A successful Pickup moves the item OFF the ground and INTO " +
                "`## Inventory` above; an item you keep picking that still appears in `## Visible nearby` and never " +
                "in `## Inventory` is not entering your bag. The goal verbs Talk, Use, Attack, and Explore also " +
                "remain executable right now. Your call.");
        }
        // ── ## Recent Buy (end-of-prompt salience capsule) ────────────────
        // Mirrors ## Recent Pickup/Give/Use/Talk for the Buy verb. Live (open-world
        // gpt-4o run): the LLM re-emitted Buy of the SAME vendor item many times
        // when the purchase never completed — the bot could not afford it (or the
        // vendor wanted a currency it lacked), so the bought item never arrived in
        // ## Inventory and the LLM kept re-buying. The Motor's pendingBuy dedup
        // throttles the actual purchases, but nothing tells the LLM the buy is not
        // sticking, so it burns a decision per cycle. Re-surface the recent Buy
        // emission history keyed by the item identity (the goal's target); and when
        // an item has been Bought >=2 times yet is STILL not in ## Inventory, add an
        // actionable "the buy is not completing — stop re-buying it" backstop (the
        // Buy analogue of the spent-Give backstop). RAW counts + own inventory; no
        // urgency, no game knowledge.
        var buyByKey = new Dictionary<string, (int Count, string Display, string? Guid, string? ItemName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Buy ", StringComparison.Ordinal)) continue;
            var sm = System.Text.RegularExpressions.Regex.Match(txt, "target=(.*?) item=.*? source=");
            if (!sm.Success) continue;
            var sel = sm.Groups[1].Value.Trim();
            if (sel.Length == 0 || sel == "<empty>") continue;
            var gm = System.Text.RegularExpressions.Regex.Match(sel, "guid=0x[0-9A-Fa-f]+");
            var nm = System.Text.RegularExpressions.Regex.Match(sel, "name=\"([^\"]+)\"");
            var key = gm.Success ? gm.Value : (nm.Success ? nm.Groups[1].Value : sel);
            var display = nm.Success ? nm.Groups[1].Value : (gm.Success ? gm.Value : sel);
            var itemName = nm.Success ? nm.Groups[1].Value : null;
            if (buyByKey.TryGetValue(key, out var cur))
            {
                var betterDisplay = cur.Display.StartsWith("guid=", StringComparison.Ordinal) && nm.Success
                    ? display : cur.Display;
                buyByKey[key] = (cur.Count + 1, betterDisplay, cur.Guid ?? (gm.Success ? gm.Value : null), cur.ItemName ?? itemName);
            }
            else
            {
                buyByKey[key] = (1, display, gm.Success ? gm.Value : null, itemName);
            }
        }
        if (buyByKey.Count > 0)
        {
            var endcapBuyDupDisplays = buyByKey.Values
                .GroupBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var endcapBuyList = string.Join(", ", buyByKey
                .OrderByDescending(p => p.Value.Count)
                .Select(p =>
                {
                    var label = endcapBuyDupDisplays.Contains(p.Value.Display) && p.Value.Guid is not null
                        ? $"{p.Value.Display} ({p.Value.Guid})"
                        : p.Value.Display;
                    return $"{label} x{p.Value.Count}";
                }));
            sb.AppendLine();
            sb.AppendLine("## Recent Buy");
            sb.AppendLine($"- in your last 10 emitted goals you emitted Buy on: {endcapBuyList}.");
            sb.AppendLine(
                "- raw fact, not a recommendation. A successful Buy moves the item from the open vendor INTO " +
                "`## Inventory` above (spending your currency). The goal verbs Talk, Use, Pickup, Attack, and " +
                "Explore also remain executable right now. Your call.");
            // Buy-not-completing backstop (the Buy analogue of the spent-Give
            // backstop): an item Bought >=2 times that is STILL not in ## Inventory
            // is not being acquired — most often it is unaffordable, or the vendor
            // wants a currency the bot lacks — so re-buying it only burns decisions.
            // Own emit history vs own inventory (OneLine-normalized both sides); no
            // game knowledge, no source-side decision.
            var buyHeldNamesLc = new HashSet<string>(
                world.Inventory
                    .Select(i => OneLine(i.Name))
                    .Where(n => n is not null)
                    .Select(n => n!.ToLowerInvariant()),
                StringComparer.Ordinal);
            var stalledBuyDisplays = buyByKey.Values
                .Where(v => v.Count >= 2
                    && OneLine(v.ItemName) is string inm
                    && !buyHeldNamesLc.Contains(inm.ToLowerInvariant()))
                .OrderByDescending(v => v.Count)
                .Select(v => v.Display)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (stalledBuyDisplays.Count > 0)
                sb.AppendLine(
                    $"- you have repeatedly emitted Buy for {string.Join(", ", stalledBuyDisplays)}, but it is NOT in " +
                    "`## Inventory` — if the purchase keeps FAILING to go through (you likely cannot afford it, or " +
                    "this vendor wants a currency you do not have), re-buying it will not help: try a CHEAPER " +
                    "offering, a different vendor, or go earn currency / pursue another objective. (If you are " +
                    "deliberately re-stocking a consumable you `Use` right after buying, ignore this.)");
        }
        // The cp-2338 InteractUnreachableTracker is a Motor-only guard: when
        // the server refuses an interaction as out-of-reach, the Motor marks
        // that guid and treats any goal that resolves to it as unresolved for
        // a TTL cooldown — but nothing tells the LLM. Live runs show the LLM,
        // blind to the suppression, re-emitting the SAME interaction goal
        // (e.g. Use{Door}), which the resolver re-resolves to the suppressed
        // guid and the Motor drops every cycle (the sticky-objective re-emit
        // burned ~3 cycles on one suppressed door before a real LLM call
        // escaped). Surface the SAME suppression set the Motor is enforcing,
        // in the decision-proximate slot, so the LLM stops targeting a refused
        // guid and picks a reachable one. Rendered whenever any guid is
        // currently suppressed (raw-presence gate, no source-side threshold).
        // RAW facts only — the server's own refusal + the Motor's current
        // resolver behavior + the cooldown's remaining time; no valuation, no
        // "avoid"/"skip"/"prefer", no instruction. Showing a currently-
        // suppressed guid hides nothing the Motor would act on: it already
        // drops these until the cooldown elapses. No game knowledge;
        // perception of the Motor's own state. Guid-only when the name is no
        // longer in the world projection.
        if (unreachableTargets is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Server-refused interaction targets");
            foreach (var u in unreachableTargets)
            {
                var label = string.IsNullOrEmpty(u.Name)
                    ? $"guid=0x{u.Guid:X8}"
                    : $"{u.Name} (guid=0x{u.Guid:X8})";
                var secs = (int)System.Math.Max(0, System.Math.Round(u.RemainingCooldownSeconds));
                sb.AppendLine(
                    $"- {label}: the last interaction attempt with this guid was refused by the " +
                    "server as out-of-reach; the resolver currently returns unresolved for any goal " +
                    $"that resolves to it. Suppression expires in about {secs}s.");
            }
            sb.AppendLine(
                "- raw fact, not a recommendation. The goal verbs Talk, Use, Pickup, Attack, and " +
                "Explore all remain executable right now. Your call.");
        }

        // ── ## Approach distance history (end-of-prompt salience capsule) ─
        // The Motor measures the self→target distance every time it locks an
        // interaction goal, but that number is Motor-only and the LLM cannot
        // tell, across ticks, whether its repeated selections of the SAME
        // target are reducing the distance. When repeated locks on one target
        // fail to close the distance there is otherwise no prompt-visible
        // fact showing the approach is flat. Surface the raw recent distance
        // samples (oldest→newest) in the decision-proximate slot so the LLM
        // can read the trend itself. The driver already gated this on
        // freshness, a >=2-sample data-availability floor, and the latest
        // sample still exceeding the Motor's mechanical arrival radius — so an
        // already-arrived target is never surfaced here. RAW measurements + a
        // not-a-recommendation disclaimer; NO "stuck"/"blocked"/"unreachable"/
        // "not closing"/"plateau" wording, no instruction to pivot. No game
        // knowledge; perception of the Motor's own measurements. Guid-only
        // when the name has left the projection.
        if (approachDistance is { DistanceSamplesUnits.Count: >= 2 } ad)
        {
            var label = string.IsNullOrEmpty(ad.Name)
                ? $"guid=0x{ad.Guid:X8}"
                : $"{ad.Name} (guid=0x{ad.Guid:X8})";
            var samples = string.Join(", ", ad.DistanceSamplesUnits.Select(d => $"{d:F1}u"));
            sb.AppendLine();
            sb.AppendLine("## Approach distance history");
            sb.AppendLine(
                $"- measured straight-line distance to {label} at each of your last " +
                $"{ad.DistanceSamplesUnits.Count} interaction locks on it (oldest to newest): {samples}.");
            sb.AppendLine(
                "- raw fact, not a recommendation. The goal verbs Talk, Use, Pickup, Attack, and " +
                "Explore all remain executable right now. Your call.");
        }

        // ── ## Recent outdoor coverage (end-of-prompt salience capsule) ──
        // Rolling-window summary of the bot's OWN recent outdoor coverage so an
        // LLM steering a hunt excursion (via the Explore `direction` verb) has
        // raw facts behind its bearing choice: how many distinct outdoor
        // landblocks it has recently crossed, which way it has net-travelled,
        // and how many of its OWN Mob sightings landed in the window. The
        // driver sets the projection only when outdoors with visited-node
        // memory; we additionally gate on a recent Explore emission so the
        // capsule never clutters town/quest/combat prompts. Raw counts + a
        // compass bearing only — no map, no zone, no recommendation.
        if (excursionCoverage is { } ec
            && recentGoalEmits.Any(ge => ge.Text!.StartsWith("Explore ", StringComparison.Ordinal)))
        {
            var bearing = Compass8(ec.NetTravelDx, ec.NetTravelDy);
            sb.AppendLine();
            sb.AppendLine("## Recent outdoor coverage");
            sb.AppendLine(
                $"- in the last ~{ec.WindowMinutes:F0} min of your own outdoor visited-node memory you have " +
                $"visited {ec.DistinctOutdoorLandblocks} distinct outdoor landblock(s); net travel from the " +
                $"oldest visited node in that window to your current position points {bearing}; you recorded " +
                $"{ec.MobSightingsInWindow} monster sighting(s) in the same window.");
            sb.AppendLine(
                "- raw fact, not a recommendation: this is not a map or a known monster location. The " +
                "`Explore` `direction` is optional; when set the bot COMMITS to that heading and travels it. Your call.");
        }

        // loot-fresh-kills (cp-2357): after a kill the picker abandons the corpse
        // when its short wait is outrun by the multi-second LLM latency, and the
        // hunt-excursion re-drives the bot away, so a fresh kill goes unlooted.
        // Surface the bot's OWN fresh, unlooted kill corpse(s) (matched to a recent
        // kill by name+recency, not yet opened) as a decision-proximate loot
        // opportunity. Perception + the existing loot-fresh-corpse rule above — no
        // game knowledge, no priority assigned here (the LLM decides).
        if (freshKillCorpses is { Count: > 0 } fkc)
        {
            var nearest = fkc[0];
            sb.AppendLine();
            sb.AppendLine("## Fresh kill to loot");
            sb.AppendLine(
                $"- a corpse from your own recent kill (\"{nearest.Name}\") is {nearest.Distance:F1}u away and not " +
                $"yet looted" + (fkc.Count > 1 ? $"; {fkc.Count - 1} more fresh corpse(s) await" : "") +
                ". Use it to reveal its loot, then Pickup.");
        }

        // cp-2358: the complement of "## Fresh kill to loot" — the bot's OWN
        // kill corpses it already opened and the loot system found EMPTY. Once a
        // corpse is opened the fresh-kill capsule above stops surfacing it, but
        // the bot's recent Use emissions still name it in "## Recent Use" (which
        // states every verb stays executable). State the observed loot OUTCOME
        // as a fact so the model has the empty result in the most decision-
        // proximate slot. A name is omitted when a fresh unlooted corpse shares
        // it (the fresh-kill capsule wins — avoids contradicting it). Perception
        // of the bot's OWN outcome; no instruction, no priority.
        if (lootedEmptyCorpses is { Count: > 0 } lec)
        {
            var freshNames = freshKillCorpses is { Count: > 0 } fk
                ? fk.Select(c => c.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase)
                : null;
            var emptyNames = lec
                .Select(c => c.Name)
                .Where(n => freshNames is null || !freshNames.Contains(n))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (emptyNames.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Already looted");
                sb.AppendLine(
                    "- you opened your own kill corpse(s) and the loot was empty — no contents " +
                    $"remained to take: {string.Join(", ", emptyNames)}.");
                sb.AppendLine(
                    "- raw fact, not a recommendation. Your call.");
            }
        }

        // cp-2359: when the distinct-object world-Use churn guard has latched —
        // the bot has Used several DISTINCT world objects in this landblock with
        // no egress and no productive inventory change — surface that no-progress
        // activity (the bot's OWN distinct-Use count + outcomes) plus the guard's
        // mechanical state, so the LLM understands why a world-object Use was
        // deferred and can apply its existing HUNT EXCURSION / tapped-out egress
        // reasoning. Guard-state perception (mirrors the cp-2340 server-refused
        // capsule); no priority, no game knowledge.
        if (localUseChurn is { Latched: true } luc)
        {
            var lvlGain = world.Self.Level is int lvNow && levelAtLandblockEntry is int lvEntry
                ? lvNow - lvEntry
                : (int?)null;
            sb.AppendLine();
            sb.AppendLine("## Local activity");
            sb.AppendLine(
                $"- in this landblock you have Used {luc.Distinct} distinct world objects with no new " +
                "inventory item" + (lvlGain is 0 ? " and no level gained" : "") + " since arriving.");
            sb.AppendLine(
                "- the motor is now deferring further bare world-object Use here to Explore until you " +
                "leave this landblock or your inventory changes.");
        }

        // ── ## Early server directives (protected-tail salience capsule) ──
        // A one-time directed instruction (how to proceed past or leave the
        // starting area) arrives as a server PopupString at login OR is spoken by
        // an NPC (NpcDialog — a server Tell), but the `## Server hints` section
        // that carries both renders mid-prompt and, in an object-dense scene, is
        // itself hard-cut by the request-size fitter before those lines render
        // (live-observed: the section truncated after its first ServerMessage,
        // dropping every PopupString and NpcDialog). Re-surface the EARLIEST
        // persisted distinct PopupStrings AND NPC directives (EventStream
        // Persistent{PopupStrings,NpcDialogs} — kept past the bounded event ring)
        // in the PROTECTED salience tail so the directed text always survives the
        // cut. Server/NPC text only, selected by event KIND + AGE, rendered
        // verbatim and truncated — NEVER parsed or branched on by content (that
        // would be hardcoded game knowledge). RAW facts + an explicit
        // not-a-recommendation disclaimer; the LLM reads the words and decides
        // whether any still applies. Mirrors the cp-2366 `## Monsters in view`
        // re-surface pattern; no game knowledge.
        var earlyServerDirectives = events.PersistentPopupStrings();
        var earlyNpcDirectives = events.PersistentNpcDialogs();
        if (earlyServerDirectives.Count > 0 || earlyNpcDirectives.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Early server directives");
            sb.AppendLine(
                "- directed text the server/NPCs sent you earlier this session (re-surfaced here " +
                "because one-time instructions arrive early and can scroll out of " +
                "`## Server hints` above before you act on them):");
            foreach (var d in earlyServerDirectives.Take(EarlyServerDirectiveCount))
                sb.AppendLine($"  - \"{Truncate(d.Text, 240)}\"");
            foreach (var d in earlyNpcDirectives.Take(EarlyNpcDirectiveCount))
                sb.AppendLine($"  - from \"{d.Name}\": \"{Truncate(d.Text, 240)}\"");
            // cp-2393 — ALSO surface the MOST-RECENT distinct directives that the
            // earliest-capped stores never captured (a late "you have completed
            // X, now take the portal" instruction), deduped against the earliest
            // lines above, so the CURRENT actionable instruction reaches the LLM
            // even after it is evicted from the ring and `## Server hints` is
            // hard-cut. Server/NPC text only, KIND+age selected, never parsed.
            var shownDirectiveTexts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in earlyServerDirectives.Take(EarlyServerDirectiveCount))
                if (d.Text is not null) shownDirectiveTexts.Add(d.Text);
            foreach (var d in earlyNpcDirectives.Take(EarlyNpcDirectiveCount))
                if (d.Text is not null) shownDirectiveTexts.Add(d.Text);
            var recentPopupDirectives = events.RecentPersistentPopupStrings()
                .Where(d => !string.IsNullOrEmpty(d.Text) && !shownDirectiveTexts.Contains(d.Text!))
                .ToList();
            var recentNpcDirectives = events.RecentPersistentNpcDialogs()
                .Where(d => !string.IsNullOrEmpty(d.Text) && !shownDirectiveTexts.Contains(d.Text!))
                .ToList();
            if (recentPopupDirectives.Count > 0 || recentNpcDirectives.Count > 0)
            {
                sb.AppendLine(
                    "- most recent directed text this session (the CURRENT instruction — what to do " +
                    "NOW — is most likely here, and SUPERSEDES an earlier directive you have already " +
                    "satisfied):");
                foreach (var d in recentPopupDirectives)
                    sb.AppendLine($"  - \"{Truncate(d.Text, 240)}\"");
                foreach (var d in recentNpcDirectives)
                    sb.AppendLine($"  - from \"{d.Name}\": \"{Truncate(d.Text, 240)}\"");
            }
            sb.AppendLine(
                "- raw fact, not a recommendation: these are the server's/NPC's own words, not an " +
                "instruction from me; greetings and flavor are not tasks. Whether any still applies, " +
                "and what to do about it, is your call.");
            // cp-2387 — re-surface the buried PURSUE UNSEEN OBJECTIVES /
            // SERVER-INSTRUCTION PRECEDENCE guidance in this decision-proximate
            // slot (the cp-2336/2337 salience pattern: a rule stated once in the
            // long preamble is reliably ignored; re-stating it next to the
            // decision changes behavior). Live: with a "go talk to <person>"
            // directive present the bot correctly stopped grinding but Explored
            // GENERICALLY instead of NAMING the target, so it never reached the
            // turn-in. This is a pointer back to existing rules, not a new rule
            // or any specific NPC/place — the LLM reads the directive's own words
            // and decides the target. No game knowledge.
            sb.AppendLine(
                "- reminder (see the PURSUE UNSEEN OBJECTIVES and SERVER-INSTRUCTION " +
                "PRECEDENCE rules above): if a directive above names a PERSON or PLACE to " +
                "reach, talk to, or proceed to (e.g. \"talk to <name>\", \"go to <place>\") and " +
                "you have NOT yet done so, pursue it by NAMING that exact target in your goal — " +
                "`Talk`/`Give`/`Explore{target: {name: \"<the named target>\"}}` — even if it is " +
                "not in `## Visible nearby` yet " +
                "(`Explore` only WALKS toward / discovers a target and NEVER interacts; so the MOMENT " +
                "that named target appears in `## Visible nearby` you have REACHED it and must switch to " +
                "`Talk`/`Give`/`Use` on it to actually act — re-`Explore`-ing it, or walking off to a " +
                "different target, then leaves you standing beside it having done nothing) " +
                "(and if that directive states a compass " +
                "bearing toward it, set the `Explore` `direction` to that stated compass bearing so the bot " +
                "commits that way), INSTEAD of Exploring \"anywhere\" generically or " +
                "grinding. A generic Explore does not satisfy a directive that names where to go. " +
                "This holds even when the directive is phrased optionally (\"if you wish to " +
                "skip/advance\", \"when you are ready\") or promises the same rewards: an unacted " +
                "skip/advance/leave directive is a PENDING option, not 'no directive' — weigh " +
                "pursuing it against optional grinding instead of dismissing its existence.");
        }

        // ── ## Held-item objectives (protected-tail: directive pinned to a held item) ──
        // A one-time server/NPC instruction for what to do with a SPECIFIC item
        // ("Return this item to <npc>", "give this token back to me", "Use this to
        // leave") arrives once, then scrolls past the earliest-N / most-recent-M
        // render caps of the directive capsules above (the "mushy middle" of the
        // persisted-directive stores) EVEN WHILE the bot still HOLDS the item it
        // names — so the bot loses the plan for an item it is carrying and grinds
        // instead of finishing the step (live: a fresh char held two server-given
        // exit tokens whose turn-in directive had scrolled out, and wedged in the
        // tutorial). Re-surface, for each item CURRENTLY in inventory, the
        // most-recent persisted directive whose OWN text NAMES that item, until the
        // item leaves the pack. Selection is the bot's OWN dynamic inventory
        // item-name matched (case-insensitive) against the server's OWN dynamic
        // directive text — NO parsed verbs, NO item/NPC/quest/wcid literals, NO
        // priority; rendered VERBATIM with the same not-a-recommendation disclaimer
        // as the directive capsules (greetings/flavor are filtered by the LLM, as
        // there). The existing FINISH MULTI-STEP DIRECTIVES / ACT ON A GIVE-REQUEST
        // / SERVER-INSTRUCTION PRECEDENCE / AREA COMPLETE rules do the acting; the
        // LLM decides. Survives ring eviction via the persistent directive stores.
        {
            var heldNames = world.Inventory
                .Select(i => OneLine(i.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (heldNames.Count > 0)
            {
                // All persisted directive lines (these survive the bounded event
                // ring), each tagged with its optional speaker (popups have none,
                // NPC dialogs carry the speaking NPC) and original sequence.
                var heldDirectiveSources = new List<(string Text, string? Speaker, long Seq)>();
                foreach (var d in events.PersistentPopupStrings())
                    if (!string.IsNullOrEmpty(d.Text)) heldDirectiveSources.Add((d.Text!, null, d.Sequence));
                foreach (var d in events.RecentPersistentPopupStrings())
                    if (!string.IsNullOrEmpty(d.Text)) heldDirectiveSources.Add((d.Text!, null, d.Sequence));
                foreach (var d in events.PersistentNpcDialogs())
                    if (!string.IsNullOrEmpty(d.Text)) heldDirectiveSources.Add((d.Text!, d.Name, d.Sequence));
                foreach (var d in events.RecentPersistentNpcDialogs())
                    if (!string.IsNullOrEmpty(d.Text)) heldDirectiveSources.Add((d.Text!, d.Name, d.Sequence));

                // Directive texts the `## Early server directives` capsule already
                // shows (its earliest-N slice + the full recent windows), so this
                // capsule adds ONLY the dropped "mushy middle" lines and never
                // repeats one already visible above.
                var heldShownAbove = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in events.PersistentPopupStrings().Take(EarlyServerDirectiveCount))
                    if (d.Text is not null) heldShownAbove.Add(d.Text);
                foreach (var d in events.PersistentNpcDialogs().Take(EarlyNpcDirectiveCount))
                    if (d.Text is not null) heldShownAbove.Add(d.Text);
                foreach (var d in events.RecentPersistentPopupStrings())
                    if (d.Text is not null) heldShownAbove.Add(d.Text);
                foreach (var d in events.RecentPersistentNpcDialogs())
                    if (d.Text is not null) heldShownAbove.Add(d.Text);

                var heldObjectiveRows = new List<string>();
                var heldRenderedTexts = new HashSet<string>(StringComparer.Ordinal);
                var heldObjectiveBudget = 700;
                foreach (var name in heldNames)
                {
                    // The most-recent (highest sequence) persisted directive whose
                    // own text NAMES this held item (as a whole word, so a short
                    // name like "Key" does not match "monkey") and is not already
                    // shown above.
                    (string Text, string? Speaker, long Seq)? best = null;
                    foreach (var d in heldDirectiveSources)
                    {
                        if (heldShownAbove.Contains(d.Text)) continue;
                        if (!DirectiveNamesItem(d.Text, name)) continue;
                        if (best is null || d.Seq > best.Value.Seq) best = d;
                    }
                    if (best is null) continue;
                    // One directive can name several held items; render its line
                    // once (keyed by the directive text) so the protected tail is
                    // not spent repeating the same instruction per item.
                    if (!heldRenderedTexts.Add(best.Value.Text)) continue;
                    var heldSpeaker = best.Value.Speaker is string s && !string.IsNullOrWhiteSpace(s)
                        ? $"from \"{OneLine(s)}\": " : "";
                    var heldRow = $"- `{name}`: {heldSpeaker}\"{Truncate(best.Value.Text, 200)}\"";
                    if (heldObjectiveBudget - heldRow.Length < 0) break;
                    heldObjectiveBudget -= heldRow.Length;
                    heldObjectiveRows.Add(heldRow);
                }
                if (heldObjectiveRows.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(
                        "## Held-item objectives (server/NPC text that NAMES an item you are STILL " +
                        "carrying — re-surfaced because such a one-time instruction can scroll out of " +
                        "the directive lists above while you still hold the item)");
                    foreach (var r in heldObjectiveRows) sb.AppendLine(r);
                    sb.AppendLine(
                        "- raw fact, not a recommendation: these are the server's/NPC's own earlier " +
                        "words about an item you are STILL carrying, re-shown because you have not yet " +
                        "acted on it; greetings and flavor are not tasks. Whether it still applies, and " +
                        "what to do (often `Give`/`Use` the item to the named target), is your call.");
                }
            }
        }

        // ── ## Reached Explore target (protected-tail decision-proximate cue) ──
        // cp reached-explore-target. Live (criterion-2 open-world run, cp020-validate.log):
        // the bot Explored toward a named NPC and ARRIVED beside it 28x, but never
        // switched to an interaction verb — it re-`Explore`d the reached target (or
        // ping-ponged to a second one) indefinitely, never acting. `Explore` is
        // navigate-only (HandshakeDriver's arrival short-circuit sends NO opcode), so
        // a reached Explore makes no progress. The PURSUE UNSEEN OBJECTIVES rule
        // already says switch to Talk/Give/Use on arrival, but it sits in the long
        // preamble that is hard-cut at the prompt-byte ceiling on every decision, and
        // the only re-statement is gated on a FRESH server/NPC directive (absent when
        // the pursuit comes from a held contract's objective). Re-state it here,
        // decision-proximate and UNgated, NAMING the specific reached target (the
        // bot's OWN Explore goal target echoed back + matched to a visible object).
        // No game knowledge, no priority, no source-side target decision — a
        // mechanical "you have arrived; Explore does not interact" fact; the LLM
        // still decides whether to interact, and how, or to move on.
        if (TryDetectReachedExploreTarget(world, events, out var reachedName, out var reachedDist)
            && OneLine(reachedName) is string reachedDisplay)
        {
            sb.AppendLine();
            sb.AppendLine("## Reached Explore target");
            sb.AppendLine(
                $"- you have been `Explore`-ing toward `{reachedDisplay}` and it is visible NOW " +
                $"(~{reachedDist:F0}u away) — you have REACHED it. `Explore` " +
                "only WALKS toward a target and NEVER interacts, so re-`Explore`-ing it (or " +
                "walking off to a different target) leaves you standing beside it having done " +
                "nothing. To ACT on it, switch to `Talk`/`Give`/`Use` on it now. If you have " +
                "nothing left to do with it (you already `Talk`ed it and it only repeats the same " +
                "flavor, or its task is already done), pursue DIFFERENT progress instead (another " +
                "un-talked npc, a vendor/contract source, or a fresh objective) — do NOT keep " +
                "`Explore`-ing a target you have already reached.");
        }

        // ── ## Explore loop (unresolved target) — cp067 protected-tail cue ──
        // The cp021 capsule above covers an Explore toward a target that IS a visible
        // reached object. A DISTINCT loop (live: an area/location name re-Explored many
        // times back-to-back with repeated arrivals) is the bot re-emitting Explore
        // toward a NAME that matches NO visible object — `Explore` navigates to where
        // the name last resolved, ARRIVES, clears the goal, and a fresh no-current-goal
        // LLM call re-emits the SAME Explore: a call storm in place that cp022/cp021
        // never catch (no visible match). Surface it as an informational CUE keyed on
        // the bot's OWN repeated emissions + perception — NOT a hard drop, so a
        // legitimate travel-back toward a not-yet-visible target is never overridden;
        // the LLM still decides. No game knowledge (the looped name is the bot's own
        // emitted goal text, echoed back), no priority, no source-side target choice.
        if (RepeatedUnresolvedExploreName(
                world, events, DateTimeOffset.UtcNow - RepeatedUnresolvedExploreWindow) is string loopedExploreName
            && OneLine(loopedExploreName) is string loopedExploreDisplay)
        {
            sb.AppendLine();
            sb.AppendLine("## Explore loop (unresolved target)");
            // Rejection-aware variant: if this SAME Explore target is being DROPPED
            // for a TRANSPORT failure — the bot could not WALK to the named place
            // (Unreachable/Blocked/NoIndoorPath) and has not since arrived — then it
            // is NOT actually travelling toward it: the goal is silently dropped to
            // the fallback every tick, so the "keep going if it's distant" guidance
            // below is WRONG (the position is NOT closing on the target). Scope this
            // to genuine PATH failures (not semantic server refusals, which are not a
            // "no route" problem) so the "unreachable" wording is always accurate.
            // Own emission history + the Motor's own transport-rejection record; no
            // game knowledge, no priority, no source-side target.
            var loopedExploreRejected = IsExploreNameTransportRefused(loopedExploreName, events);
            if (loopedExploreRejected)
                sb.AppendLine(
                    $"- you have `Explore`d toward `{loopedExploreDisplay}` several times recently and EACH attempt is " +
                    "being REFUSED as unreachable (no walkable route to it from here) and DROPPED — so you are NOT " +
                    $"travelling toward `{loopedExploreDisplay}` at all; re-emitting it just wastes this turn. " +
                    $"`{loopedExploreDisplay}` is not a reachable destination from here. To make progress, emit " +
                    "`Explore{target: {name: \"anywhere\"}}` to travel toward open ground, or interact with a VISIBLE " +
                    "object in `## Nearest objects` (`Talk`/`Use`/`Give`/`Pickup`/`Attack`) — do NOT re-`Explore` " +
                    $"`{loopedExploreDisplay}`.");
            else
                sb.AppendLine(
                    $"- you have `Explore`d toward `{loopedExploreDisplay}` several times recently but NO " +
                    $"object named `{loopedExploreDisplay}` is visible here. If `{loopedExploreDisplay}` is a " +
                    "DISTANT place you have NOT reached yet, keep `Explore`-ing toward it — that is normal " +
                    "travel (your `landblock`/position should be changing as you go). But if it is an area you " +
                    "have ALREADY reached (you keep ARRIVING with nothing here to act on, and your position is " +
                    "NOT changing), re-`Explore`-ing makes no progress: `Explore` only WALKS/discovers and " +
                    "CANNOT interact with a PLACE. In that case, to ADVANCE, interact with a VISIBLE object — a " +
                    "portal/NPC/door/sign in `## Nearest objects` (`Talk`/`Use`/`Give`/`Pickup`) — or pursue a " +
                    $"DIFFERENT objective, instead of re-`Explore`-ing `{loopedExploreDisplay}`.");
        }

        // ── ## Explore toward visible object — sibling of the Explore loop ──
        // The cue above covers an Explore toward a name with NO visible match. The
        // COMPLEMENT: the bot re-Explores a name that DOES resolve to a VISIBLE
        // object which is beyond reach — Explore walks toward it and stops at its
        // arrival radius WITHOUT interacting, so it never engages; the cp022 reached
        // cue cannot fire (it is beyond reach) and the unresolved cue cannot fire (it
        // IS visible), leaving the loop unaddressed. Surface it as an informational
        // CUE telling the LLM to use the INTERACTION verb (which navigates INTO range
        // AND acts) instead of re-Exploring a visible object. Keyed on the bot's OWN
        // repeated emissions + perception — NOT a hard drop, so a single
        // approach-Explore is never flagged; the LLM still decides. No game
        // knowledge, no priority, no source-side target/verb choice (it names the
        // generic verb menu, not which one to use).
        if (RepeatedResolvedFarVisibleExploreName(
                world, events, DateTimeOffset.UtcNow - RepeatedUnresolvedExploreWindow) is string visibleExploreName
            && OneLine(visibleExploreName) is string visibleExploreDisplay)
        {
            sb.AppendLine();
            sb.AppendLine("## Explore toward visible object");
            sb.AppendLine(
                $"- you have `Explore`d toward `{visibleExploreDisplay}` several times recently, but " +
                $"`{visibleExploreDisplay}` IS already VISIBLE in the world state here (just not yet within " +
                "reach). `Explore` only WALKS toward a target and STOPS at its arrival radius WITHOUT interacting, " +
                $"so re-`Explore`-ing `{visibleExploreDisplay}` never engages it. To ACT on it, emit the INTERACTION " +
                "verb directly — `Talk` an NPC, `Use` a vendor/door/object, `Attack` a monster, or `Pickup` an item — " +
                $"that verb walks you INTO range AND performs the action in one goal. Use the interaction verb on " +
                $"`{visibleExploreDisplay}` instead of re-`Explore`-ing it.");
        }

        // ── ## Attack loop (target not in view) — sibling of the Explore loop ──
        // The bot re-emits `Attack` toward a named monster that is no longer within the
        // visible radius (it moved away / died / the bot travelled past it), so the goal
        // resolves to MISS, clears, and a fresh no-current-goal call re-emits the SAME
        // Attack — a call storm in place (live: a named mob re-Attacked many times while
        // not in view). Surface it as an informational CUE keyed on the bot's OWN repeated
        // emissions + perception — NOT a hard drop, so a close-in toward a target just out
        // of view is never overridden; the LLM still decides. No game knowledge (the looped
        // name is the bot's own emitted goal text, echoed back), no priority, no source-side
        // target choice.
        if (RepeatedUnresolvedAttackTarget(
                world, events, DateTimeOffset.UtcNow - RepeatedUnresolvedAttackWindow) is string loopedAttackName
            && OneLine(loopedAttackName) is string loopedAttackDisplay)
        {
            sb.AppendLine();
            sb.AppendLine("## Attack loop (target not in view)");
            sb.AppendLine(
                $"- you have tried to `Attack` `{loopedAttackDisplay}` several times recently but NO monster " +
                $"named `{loopedAttackDisplay}` is in view in `## Nearest objects`. If you are still TRAVELLING " +
                $"toward `{loopedAttackDisplay}` and closing in (your `landblock`/position is changing as you go), " +
                "keep going — `Attack` walks you to a target, and it will come into view. But if your position is " +
                $"NOT changing, or `{loopedAttackDisplay}` has died or you have travelled PAST it, re-`Attack`-ing " +
                "makes no progress: `Attack` a DIFFERENT monster that IS visible in `## Nearest objects`, or pursue " +
                $"a DIFFERENT objective, instead of re-`Attack`-ing `{loopedAttackDisplay}`.");
        }

        // ── ## Use loop (target not in view) — sibling of the Attack/Explore loops ──
        // The bot re-emits `Use` toward a named object (vendor/NPC/door/container/corpse/
        // item) that is no longer within the visible radius (it walked out of range), so the
        // goal resolves to MISS, clears, and a fresh no-current-goal call re-emits the SAME
        // Use. Surface it as an informational CUE keyed on the bot's OWN repeated emissions +
        // perception — NOT a hard drop, so a close-in toward a target just out of view is
        // never overridden; the LLM still decides. The bot's OWN corpse is exempt: its
        // dedicated `## Corpse` cue already guides retrieval (Explore back + Use), so the
        // generic "Use a different object" wording would contradict it. No game knowledge
        // (the looped name is the bot's own emitted goal text, echoed back), no priority,
        // no source-side target choice.
        if (RepeatedUnresolvedUseTarget(
                world, events, DateTimeOffset.UtcNow - RepeatedUnresolvedUseWindow) is string loopedUseName
            && !IsOwnCorpseName(loopedUseName, world.Self.Name)
            && OneLine(loopedUseName) is string loopedUseDisplay)
        {
            sb.AppendLine();
            sb.AppendLine("## Use loop (target not in view)");
            sb.AppendLine(
                $"- you have tried to `Use` `{loopedUseDisplay}` several times recently but NO object named " +
                $"`{loopedUseDisplay}` is in view in `## Nearest objects`. If you are still TRAVELLING toward " +
                $"`{loopedUseDisplay}` and closing in (your `landblock`/position is changing as you go), keep going " +
                "— `Use` walks you to a target, and it will come into view. But if your position is NOT changing, or " +
                $"you have travelled PAST `{loopedUseDisplay}`, re-`Use`-ing makes no progress: `Use` a DIFFERENT " +
                "object that IS visible in `## Nearest objects`, or pursue a DIFFERENT objective, instead of " +
                $"re-`Use`-ing `{loopedUseDisplay}`.");
        }

        // ── ## Engagement churn — multi-target unresolved-interaction loop-break ──
        // Each single-target loop cue above fires on ONE repeated name; a model that cycles
        // through SEVERAL different nearby targets (Talk/Use), each going out of view/range
        // so each emission fails to resolve, trips NONE of them (each stays below its own
        // repeat threshold). Surface that multi-target canvass as an informational CUE keyed
        // on the bot's OWN recent emissions + perception — NOT a hard drop, so a legitimate
        // visit to several DISTANT not-yet-reached targets is never overridden (those resolve
        // once reached; this counts only names NO visible object binds). The LLM still
        // decides. No game knowledge, no priority, no source-side target choice.
        if (CountDistinctUnresolvedInteractionTargets(
                world, events, DateTimeOffset.UtcNow - EngagementChurnWindow) >= EngagementChurnDistinctThreshold)
        {
            sb.AppendLine();
            sb.AppendLine("## Engagement churn (several targets not in view)");
            sb.AppendLine(
                "- you have recently tried to `Talk`/`Use` SEVERAL different targets that are NOT visible " +
                "here — each interaction is failing to resolve. If you are CANVASSING nearby NPCs hoping one " +
                "helps, that is not progress: pick ONE target, walk right up to it (it must be visible + close " +
                "in `## Nearest objects` to `Talk`/`Use`), or stop interacting and pursue DIFFERENT progress " +
                "(hunt a monster, `Explore` to a new area, or act on a held objective). Do NOT keep cycling " +
                "`Talk`/`Use` across targets that never resolve.");
        }

        // ── ## Wield loop (you do not own that weapon) — sibling of the other loop cues ──
        // The bot re-emits `Wield` for a weapon NAME it does NOT own (it is in a vendor's
        // shop or on the ground, not in `## Inventory`). The Wield dispatch can only equip
        // an item already in the bot's bag, so it fails every time and the bot stays
        // unarmed. Surface it as an informational CUE keyed on the bot's OWN repeated
        // emissions + owned inventory — NOT a hard drop; the LLM still decides. No game
        // knowledge (the looped name is the bot's own emitted goal text, echoed back), no
        // priority, no source-side target choice.
        if (RepeatedUnownedWieldName(
                world, events, DateTimeOffset.UtcNow - RepeatedUnownedWieldWindow) is string loopedWieldName
            && OneLine(loopedWieldName) is string loopedWieldDisplay)
        {
            sb.AppendLine();
            sb.AppendLine("## Wield loop (you do not own that weapon)");
            sb.AppendLine(
                $"- you have tried to `Wield` `{loopedWieldDisplay}` several times but it is NOT an equippable " +
                "weapon in your `## Inventory` — you can only `Wield` a weapon you already OWN. If " +
                $"`{loopedWieldDisplay}` is for sale at a vendor, `Buy` it FIRST; if it is on the ground, " +
                "`Pickup` it FIRST. Otherwise `Wield` a weapon that IS listed in your `## Inventory`, or acquire " +
                $"one, instead of re-`Wield`-ing `{loopedWieldDisplay}`.");
        }

        // ── ## Un-equipped gear (protected-tail equip cue) ──
        // The bot may LOOT or be GIVEN wearable equipment (armor/clothing/
        // jewelry) and never put it on, leaving it carrying protection that
        // does nothing in the pack. Surface the carried-but-unworn pieces and
        // cue a Wield; the LLM decides whether/when to equip. No game knowledge
        // — pure typed slot-bit wire state (see HeldUnequippedWearables).
        // Drop a piece the server recently REFUSED to equip (e.g. a wear
        // requirement not met): re-surfacing it would contradict the later
        // `## Recently refused items` capsule and loop the LLM on a Wield the
        // server will only refuse again. Mirrors every self-arm affordance.
        var heldWearables = HeldUnequippedWearables(world.Inventory)
            .Where(i => !recentlyServerRefusedGuids.Contains(i.Guid))
            .ToList();
        if (heldWearables.Count > 0)
        {
            var distinctWearableNames = heldWearables
                .Select(it => OneLine(it.Name))
                .Where(n => n is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var wearableNames = distinctWearableNames.Take(4).ToList();
            if (wearableNames.Count > 0)
            {
                var sample = string.Join(", ", wearableNames.Select(n => $"`{n}`"));
                // "and more" only when DISTINCT names exceed the four shown — not
                // when duplicate-named copies collapsed under Distinct (else the
                // LLM hunts for named pieces that do not exist).
                var more = distinctWearableNames.Count > wearableNames.Count ? ", and more" : "";
                sb.AppendLine();
                sb.AppendLine("## Un-equipped gear");
                sb.AppendLine(
                    $"- you are CARRYING wearable equipment you have NOT put on: {sample}{more}. " +
                    "Worn gear protects you (or grants its benefit); the SAME item sitting in your " +
                    "pack does NOTHING. To put a piece on, `Wield` it (target that item by name) and " +
                    "the server seats it in its wear slot. Equip your carried gear so you are " +
                    "protected — especially before or during a fight.");
            }
        }

        // ── ## System messages (protected-tail durable status capsule) ──
        // Low-volume, high-value SYSTEM status lines the server sends are easily
        // evicted from the perception-dominated event ring within seconds, so the
        // body `## Server hints` may no longer carry them. RecentServerMessages()
        // is a dedicated durable store that keeps the most-recent distinct ones.
        // Surface them VERBATIM (like `## Server hints`) with no interpretation —
        // no game knowledge, no priority, the decision is the LLM's.
        var recentSystemMessages = events.RecentServerMessages();
        if (recentSystemMessages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## System messages (recent — status text the server sent you, newest last)");
            foreach (var m in recentSystemMessages)
                sb.AppendLine($"  - \"{Truncate(m.Text, 240)}\"");
            sb.AppendLine(
                "- raw fact, not a recommendation: these are the server's own status lines, not an " +
                "instruction from me. Whether any still applies, and what to do about it, is your call.");
        }

        // ── ## Held items (protected-tail cut-proof inventory, cp-2389) ──
        // The full `## Inventory` section renders in the BODY and is among the
        // first things the request-size fitter omits when the prompt overflows
        // (live: in a dense scene `## Inventory` was "omitted to fit
        // prompt budget", so the bot could not see a server-given quest item it
        // had to hand back to advance — and grinded instead of finishing the
        // step). Re-surface a COMPACT held-items list
        // (name + short_desc, deduped, char-bounded) in the PROTECTED salience
        // tail so the bot always knows what it is carrying even when the body
        // `## Inventory` is trimmed. Purely the bot's OWN inventory wire data,
        // deduped by rendered text; no item-type/wcid/NPC heuristic, no game
        // knowledge. The existing FINISH MULTI-STEP DIRECTIVES / pursue-target
        // rules supply what to DO with a held item; this only guarantees the
        // bot can SEE it.
        if (world.Inventory.Count > 0)
        {
            var heldSeen = new HashSet<string>(StringComparer.Ordinal);
            var heldRows = new List<string>();
            var heldBudget = HeldItemsProtectedCharBudget;
            foreach (var i in world.Inventory)
            {
                var sd = string.IsNullOrWhiteSpace(i.ShortDesc) ? "" : i.ShortDesc!.Trim();
                var ud = string.IsNullOrWhiteSpace(i.UseDesc) ? "" : i.UseDesc!.Trim();
                // Normalize to the EFFECTIVE (rendered) use text: a use string
                // identical to short_desc is not shown, so collapse it to "" so
                // it does not split the dedup key (two items that render the same
                // must share one row and one budget slot).
                if (ud.Length > 0 && ud == sd) ud = "";
                var key = i.Name + "\u0001" + sd + "\u0001" + ud;
                if (!heldSeen.Add(key)) continue;
                // Render the item's OWN description text — prefer ShortDesc, and
                // also include its `use` instruction when present (some items
                // carry their actionable "give/return this to ..." text only in
                // the Use string, not ShortDesc). Pure projection of the item's
                // own wire/weenie strings; the LLM reads and decides.
                string desc;
                if (sd.Length > 0 && ud.Length > 0) desc = $"{Truncate(sd, 110)} / use: {Truncate(ud, 110)}";
                else if (sd.Length > 0) desc = Truncate(sd, 120);
                else if (ud.Length > 0) desc = $"use: {Truncate(ud, 120)}";
                else desc = "";
                var row = desc.Length > 0 ? $"- {i.Name} — {desc}" : $"- {i.Name}";
                if (heldBudget - row.Length < 0) break;
                heldBudget -= row.Length;
                heldRows.Add(row);
            }
            if (heldRows.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Held items (you are carrying these — re-surfaced because `## Inventory` above can be trimmed to fit the prompt)");
                foreach (var r in heldRows) sb.AppendLine(r);
            }
        }

        // ── ## Recently refused items (protected-tail) ──
        // The action-side dedup (IsGoalRecentlyRejected) DROPS a re-emit of a goal
        // the server semantically refused, but a weak model keeps re-SELECTING the
        // same item every decision because nothing in the prompt tells it the
        // action was refused. Live (cp021-validate.log): ~50 Wield emissions of the
        // SAME both-hands weapon in one run (a swap whose dequip the server would
        // not actuate), every one dropped, while real progress stalled. Surface the
        // refused items by name so the LLM stops re-emitting the same goal and does
        // something else. Reuses the SAME server-refusal set the self-arm
        // suggestions + the weapon-skill advisory filter on (recentlyServerRefused,
        // computed once above): transport (could-not-walk) failures are excluded
        // there, so a ground item the bot walk-timed-out toward is not mislabeled.
        // Reads the bot's OWN rejection bookkeeping; the guid is from the wire, the
        // name from the item projection — no game knowledge, no priority. Usually
        // empty, so it costs zero prompt budget unless the bot is looping a refusal.
        if (recentlyServerRefusedGuids.Count > 0)
        {
            var refusedRows = world.Inventory
                .Where(i => recentlyServerRefusedGuids.Contains(i.Guid) && !string.IsNullOrWhiteSpace(i.Name))
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (refusedRows.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Recently refused items (the server would not actuate a recent action on these)");
                foreach (var it in refusedRows)
                    sb.AppendLine(
                        $"- {OneLine(it.Name)} (wcid={it.Wcid}): the server REFUSED a recent action on " +
                        "this item (e.g. it could not be equipped/swapped right now). Re-selecting it with " +
                        "the SAME goal (`Wield`/`Use`/`Give`) is unlikely to work right now — prefer keeping " +
                        "your current gear or a DIFFERENT item/action; you can revisit it later if the " +
                        "situation changes.");
            }
        }

        // ── ## Self core capsule (protected-tail cut-proof) ──
        // The full `## Self` section renders in the BODY just after the ~19KB
        // RULES preamble. When a dense scene inflates the prompt past the
        // ceiling, the request-size fitter's body hard-cut can land right after
        // the preamble and drop the ENTIRE mid-body — including `## Self`. Live:
        // the bot then could not see its own attributes / trained skills /
        // health, and (lacking a `trained skills` list) reached for a weapon
        // ITEM name when trying to RaiseSkill. Re-surface the same compact self
        // facts in the PROTECTED salience tail so the bot always knows its core
        // state, mirroring the `## Held items` cut-proofing (cp-2389). Same raw
        // projection facts via AppendSelfCoreFacts; no advice. The header is
        // rolled back when no core facts are known (e.g. pre-login), so it costs
        // nothing until the data exists.
        {
            int selfCapsuleStart = sb.Length;
            sb.AppendLine();
            sb.AppendLine("## Self (core state — re-surfaced because `## Self` above can be trimmed to fit the prompt)");
            int afterSelfCapsuleHeader = sb.Length;
            AppendSelfCoreFacts(sb, world, secondsSinceLastDeath);
            if (sb.Length == afterSelfCapsuleHeader)
                sb.Length = selfCapsuleStart;
        }

        // ── ## Combat readiness capsule (protected-tail cut-proof) ──
        // The body `## Combat readiness` renders just after the RULES preamble
        // and is dropped by the same dense-scene body hard-cut that drops
        // `## Self`. Live: lacking it, the LLM could not confirm whether it was
        // armed ("no combat readiness info to confirm safe fight. Unarmed or no
        // viable weapon indicated") and AVOIDED combat, choosing Explore over a
        // winnable fight. Re-surface the single most decision-critical compact
        // signal — the weapon/armed (UNARMED) line, plus how to arm when UNARMED
        // — in the PROTECTED salience tail via the shared WeaponReadinessLine.
        // Threat counts already survive via the tail's monster sections and
        // max-HP/deaths via the `## Self` capsule, so only the arm-state is
        // re-surfaced here. Raw wire-state; no advice.
        {
            sb.AppendLine();
            sb.AppendLine("## Combat readiness (re-surfaced because `## Combat readiness` above can be trimmed to fit the prompt)");
            sb.AppendLine($"- weapon: {WeaponReadinessLine(meleeWeaponWielded, missileWeaponWielded, ammoLoaded, bagAmmo is not null, wieldedThrownWeapon)}");
            if (WeaponSkillSwapAdvisory(world, recentlyServerRefusedGuids) is string capSkillAdvisory)
                sb.AppendLine($"- {capSkillAdvisory}");
            if (WieldedWeaponUntrainedAccuracyNote(world) is string capUntrainedNote)
                sb.AppendLine($"- {capUntrainedNote}");
            // The `tapped out` hunt-discovery fact (combat-ready + farmed this
            // area past the dwell threshold with +0 levels) renders in the body
            // `## Combat readiness` and is dropped by the same hard-cut — leaving
            // the always-rendered TAPPED OUT rule with no fact to act on (inert).
            // Re-surface it here (gated: HuntTappedOutFact returns null unless
            // actually tapped out) so the rule can fire and the bot moves to a
            // better hunting area. Same own-progress projection; no game knowledge.
            if (HuntTappedOutFact(armedForHunt, world.Self.Level, levelAtLandblockEntry,
                    dwellMinForHunt, EgressDwellMinutes) is string tappedOutCapsuleFact)
                sb.AppendLine($"- {tappedOutCapsuleFact}");
            // How-to-arm affordances. Each variable is already null-computed for
            // its applicable case (bagWeapon/groundWeapon are null when armed;
            // bagAmmo is null unless a missile weapon is wielded with empty
            // ammo), so they are surfaced directly — exactly like the body
            // section — WITHOUT an outer armed-state gate. An earlier outer
            // `if (!melee && !missile)` gate made the bagAmmo row dead code (it
            // requires a wielded missile weapon, which the gate excluded), so a
            // missile-empty bot was told ammo is EMPTY but never which to wield.
            if (bagWeapon is not null)
                sb.AppendLine($"- melee weapon in your inventory (Wield it to arm): {bagWeapon.Name}");
            if (bagThrownWeapon is not null)
                sb.AppendLine($"- throwable weapon in your inventory (Wield it to arm — a thrown weapon is its own projectile, NO ammo needed): {bagThrownWeapon.Name}");
            if (groundWeapon is not null)
            {
                var gwd = groundWeapon.Distance is float gd ? $" d={gd:F1}" : "";
                sb.AppendLine($"- melee weapon nearby (Pickup it to arm): {groundWeapon.Name}{gwd}");
            }
            if (bagAmmo is not null)
                sb.AppendLine($"- missile ammo in your inventory (Wield it to load): {bagAmmo.Name}");
            if (bagLauncherAmmo is { } bla2)
                sb.AppendLine(
                    "- missile launcher + compatible ammo in your inventory (Wield the launcher," +
                    $" then Wield the ammo to load): {bla2.Launcher.Name} + {bla2.Ammo.Name}");
            // cp062 — re-surface the FIGHT NOW directive in the protected tail (same gate
            // as the body): the body ## Combat readiness is dropped by the dense-scene
            // hard-cut, and `monstersInView > 0` is exactly the condition that triggers
            // that cut — so without this the weaponless-with-monster steer is lost in the
            // very scene it targets.
            if (!armed && monstersInView > 0 && armVendor is null &&
                bagWeapon is null && bagThrownWeapon is null && groundWeapon is null &&
                bagAmmo is null && bagLauncherAmmo is null)
                sb.AppendLine(
                    "- FIGHT NOW: you have NO weapon to wield or buy here, but a monster is in" +
                    " view and unarmed melee (fists) is ALWAYS available — emit `Attack` on a" +
                    " visible monster to fight it. Do NOT emit `Wield` (no usable weapon exists;" +
                    " an empty launcher cannot fire and is immediately un-wielded) — wielding wastes the turn.");
            if (bagWeapon is null && bagThrownWeapon is null && groundWeapon is null &&
                bagAmmo is null && bagLauncherAmmo is null &&
                world.Vendor is null && armVendor is not null)
            {
                var avd2 = armVendor.Distance is float ad2 ? $" d={ad2:F1}" : "";
                sb.AppendLine(
                    "- vendor nearby (you have NO weapon to Wield/Pickup — `Use` it ONCE to browse its " +
                    "`Vendor offerings`, then `Buy` a `[weapon]`/`[missile weapon/ammo]` to arm; if nothing there " +
                    $"is affordable, hunt the WEAKEST monsters to loot a weapon/coin rather than re-Using it or touring more vendors): {armVendor.Name}{avd2}");
            }
        }

        // ── ## Beaten kinds capsule (protected-tail cut-proof) ──
        // The body combat-history lines (the bot's own per-kind outcomes) render
        // in the body and are dropped by the dense-scene body hard-cut. Live:
        // with them gone, the LLM repeatedly ordered Attack on a kind its own
        // ledger marks beaten; the Motor's beaten-kind veto dropped each Attack
        // ("deferring to fallback"), wasting the decision. Re-surface the kinds
        // that veto would drop — the SAME source (CombatHistoryFull) and the SAME
        // predicate it uses (a recorded death + IsBeatenKind, lethal-retestable
        // only once out-levelled) — in the PROTECTED salience tail. Raw own-ledger
        // counts + the mechanical veto consequence; no advice, the LLM owns the
        // next action. Gated, so it costs nothing when there are no beaten kinds.
        if (world.CombatHistoryFull is { Count: > 0 } fullLedger)
        {
            var beatenKinds = fullLedger
                .Where(h => h.Deaths > 0 && IsBeatenKind(
                    world.CombatHistoryFull, h.Wcid, h.Name, world.Self.Level,
                    lethalRetestableWhenOutleveled: true))
                .ToList();
            if (beatenKinds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    "## Beaten kinds (your own combat ledger; the Motor DECLINES an offensive Attack you order on a " +
                    "kind below — it has killed you with 0 kills for you — and allows fighting back only when that " +
                    "kind is attacking you now)");
                foreach (var h in beatenKinds)
                    sb.AppendLine(
                        $"- {h.Name}: fights {h.Fights}, kills {h.Kills}, deaths {h.Deaths}, " +
                        $"near-deaths {h.NearDeaths}, ineffective {h.Ineffective} (last: {h.LastOutcome})");
            }
        }

        // ── ## Winnable kinds capsule (protected-tail cut-proof) ──
        // The body combat-history lines and the inline `[your record: ...]`
        // annotations on visible monsters are dropped by the body hard-cut, so
        // the LLM cannot see which kinds it is ALREADY winning against — the
        // exact evidence the COMMIT A WINNING GRIND AS A KILL-COUNT INTENT rule
        // needs to push a kill-count commitment that lets the Motor chain kills
        // WITHOUT a per-monster LLM call (reduce-llm-call-volume). Live: that
        // autonomous chain fired 0 times because this evidence never reached the
        // LLM. Re-surface the clearly-winnable kinds (own ledger: kills recorded,
        // no death) in the PROTECTED salience tail — the complement of the cp2916
        // beaten-kinds capsule. Raw own-ledger counts; the existing rule owns the
        // grind decision (and a quest/server directive outranks it); no new
        // advice. Gated, so it costs nothing when no winnable kinds are recorded.
        if (world.CombatHistoryFull is { Count: > 0 } winLedger)
        {
            var winnableKinds = winLedger
                .Where(h => h.Kills > 0 && h.Deaths == 0)
                .ToList();
            if (winnableKinds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(
                    "## Winnable kinds (your own combat ledger — kinds you have killed with no recorded death)");
                // The commit nudge references `stack_ops`, which only exists in the
                // decision schema when the IntentStack is enabled — gate it on the
                // same `stack is not null` condition as the COMMIT A WINNING GRIND
                // rule it points back to, so we never instruct the LLM to emit a
                // field the schema omits.
                if (stack is not null)
                    sb.AppendLine(
                        "- ACT ON THIS NOW, do not just read it: if you are about to Attack one of the kinds below and " +
                        "have NO active kill-count intent on the stack for it, COMMIT A WINNING GRIND in the SAME " +
                        "response — add a `hunt` kill-count push to `stack_ops` (per the COMMIT A WINNING GRIND AS A " +
                        "KILL-COUNT INTENT rule above, which gives the exact shape) so the Motor repeats the kills " +
                        "with NO per-kill LLM call. This is the reduce-llm-call-volume lever: while no such commitment " +
                        "is on the stack EVERY kill costs a full decision and leveling stalls. A quest/server directive " +
                        "still outranks this grind; push only for a kind you are genuinely winning against (listed below).");
                foreach (var h in winnableKinds)
                    sb.AppendLine(
                        $"- {h.Name}: fights {h.Fights}, kills {h.Kills}, deaths {h.Deaths}, " +
                        $"near-deaths {h.NearDeaths}, ineffective {h.Ineffective} (last: {h.LastOutcome})");
            }
        }

        // ── ## Location capsule (protected-tail cut-proof) ──
        // The body `## Location & recency` renders `minutes in current
        // landblock` — the dwell value the LOOP-BREAK (town-stuck) and HUNT
        // EXCURSION rules gate on — but that body section is dropped by the
        // dense-scene body hard-cut, so the LLM has those rules yet cannot see
        // the value they reference (live: the bot canvassed a town's NPCs
        // indefinitely, `minutes in current landblock` invisible, never leaving
        // to hunt/level). Re-surface the raw dwell value here so the rules can
        // evaluate "dwelled too long". Own-progress projection; no game knowledge.
        if (dwellMinForHunt is double dwellMinTail)
        {
            sb.AppendLine();
            sb.AppendLine("## Location (re-surfaced because `## Location & recency` above can be trimmed to fit the prompt)");
            sb.AppendLine($"- minutes in current landblock: {dwellMinTail:F1}");
        }

        var assembled = sb.ToString();
        var salienceTail = assembled.Substring(salienceTailStart);
        var body = assembled.Substring(0, salienceTailStart);
        return FitPromptToCeiling(body, salienceTail, promptCeiling ?? EffectivePromptCeilingChars);
    }

    // Some GitHub Models endpoints reject an over-large request body with
    // HTTP 413 Payload Too Large. Measured cliff (gpt-4.1-mini, this
    // project): a user prompt up to ~27600 chars succeeds, ~28400 fails;
    // a fixed ~2.5KB system prompt + JSON overhead rides on top. A 413
    // drops the bot into the knowledge-free fallback for the rest of the
    // run, so we keep the assembled user prompt under a ceiling below the
    // cliff. This is request-size management, NOT strategy: rows are shed
    // by section and by the nearest-first / newest-first / out-of-view
    // order the sections already render in — no row is kept or dropped by
    // its in-game type.
    private const int HardUserPromptCeilingChars = 26000;

    // Deploy-time override of the prompt ceiling via the AC_BOTS_PROMPT_CEILING
    // env var. Some GitHub Models endpoints (e.g. openai/gpt-4o, deepseek-v3)
    // reject a request body near 26000 chars with HTTP 413 even though their
    // token context is large; a 413 drops the bot into the knowledge-free
    // fallback for the rest of the run. Setting a smaller, model-appropriate cap
    // lets such a (often more capable) model accept the prompt. Read once at
    // load; clamp + fallback live in ResolvePromptCeiling. This is request-size
    // management keyed to a model endpoint's limits, NOT strategy or game
    // knowledge — no row is kept or dropped by its in-game type.
    private static readonly int EffectivePromptCeilingChars =
        ResolvePromptCeiling(Environment.GetEnvironmentVariable("AC_BOTS_PROMPT_CEILING"));

    // Adaptive session-level prompt ceiling. Starts at the configured
    // EffectivePromptCeilingChars and AUTO-LOWERS to MinConfigurablePromptCeilingChars
    // on the first HTTP 413 (Payload Too Large) from the model endpoint, then
    // stays lowered for the rest of the run (one-way; never raised). This lets a
    // payload-limited but capable model (e.g. deepseek-v3, which 413s a ~26000-
    // char body but FITS at ~10000) self-adapt WITHOUT a manual
    // AC_BOTS_PROMPT_CEILING — preventing the "413s every call -> knowledge-free
    // fallback -> looks non-viable" failure. Request-size management, not strategy.
    private int _adaptivePromptCeiling = EffectivePromptCeilingChars;

    // Parse the AC_BOTS_PROMPT_CEILING override. A value that parses as an int in
    // [MinConfigurablePromptCeilingChars, HardUserPromptCeilingChars] is used;
    // anything else (null, unset, unparseable, or out of range) falls back to the
    // HardUserPromptCeilingChars default. The lower bound guards against a typo
    // shrinking the prompt below the point where the fixed decision sections fit.
    internal const int MinConfigurablePromptCeilingChars = 10000;

    internal static int ResolvePromptCeiling(string? envValue) =>
        int.TryParse(envValue, out var ceiling)
            && ceiling >= MinConfigurablePromptCeilingChars
            && ceiling <= HardUserPromptCeilingChars
            ? ceiling
            : HardUserPromptCeilingChars;

    // Kill-switch for the autonomous kill-intent decomposition
    // (ChooseCombatChainTarget). When the LLM has pushed a TYPED kill-count
    // commitment ("kill N [of X]"), the Motor may mint the next Attack toward
    // that count WITHOUT a per-monster LLM round-trip — the standing
    // reduce-llm-call-volume tempo/quota goal. Set AC_BOTS_COMBAT_CHAIN to
    // 0 / false / off to disable instantly if a regression appears; any other
    // value (including unset) leaves it ON. Request-tempo management, not
    // strategy: the LLM authored the commitment; source only decomposes it.
    private static readonly bool CombatChainEnabled =
        ResolveCombatChainEnabled(Environment.GetEnvironmentVariable("AC_BOTS_COMBAT_CHAIN"));

    internal static bool ResolveCombatChainEnabled(string? envValue) =>
        !(string.Equals(envValue, "0", StringComparison.Ordinal)
          || string.Equals(envValue, "false", StringComparison.OrdinalIgnoreCase)
          || string.Equals(envValue, "off", StringComparison.OrdinalIgnoreCase));

    // Reduce-llm-call-volume gate (default ON; opt OUT with
    // AC_BOTS_SKIP_FIXATED_TALK_CALL=0/false/off). When the bot's recent goal
    // history is a PROVEN stale single-NPC Talk fixation and nothing
    // plan-invalidating has changed since the last LLM look, an LLM call would
    // just reproduce a Talk the fixation guards immediately drop — so skip it and
    // go straight to the same break-contact egress/fallback. Enabled by default
    // because the egress/fallback the bot reaches is IDENTICAL to the post-LLM
    // fixation-drop path (call -> Talk -> drop -> EscapeOrFallback), so the only
    // behavioral difference is the saved redundant call; the `!hasNonPickerExternal`
    // guard means a productive NPC (new dialog/item/etc.) blocks the skip.
    // Request-tempo management, not strategy.
    private static readonly bool SkipFixatedTalkCallEnabled =
        ResolveSkipFixatedTalkCall(Environment.GetEnvironmentVariable("AC_BOTS_SKIP_FIXATED_TALK_CALL"));

    internal static bool ResolveSkipFixatedTalkCall(string? envValue) =>
        !(string.Equals(envValue, "0", StringComparison.Ordinal)
          || string.Equals(envValue, "false", StringComparison.OrdinalIgnoreCase)
          || string.Equals(envValue, "off", StringComparison.OrdinalIgnoreCase));

    // Reduce-llm-call-volume gate (default ON; opt OUT with
    // AC_BOTS_SKIP_EMPTY_EXPLORE_CALL=0/false/off). When the bot is travelling
    // through empty space on a sustained UNTARGETED Explore with nothing in view to
    // engage and no new decision-worthy change, an LLM call would just reproduce the
    // same Explore — so skip it and continue (bounded by MaxEmptyExploreSkips). The
    // gate's freshness guards (!hasNonPickerExternal, !pickerArrived/!pickerStartWake,
    // !HasNewStrategicIntentCompletionSince, plus nothing attackable/vendor/un-talked
    // in view) ensure the bot never skips over input it could act on. Request-tempo
    // management only; the Explore continued is the bot's OWN prior untargeted-travel
    // decision (no new target picked).
    private static readonly bool SkipEmptyExploreCallEnabled =
        ResolveSkipEmptyExploreCall(Environment.GetEnvironmentVariable("AC_BOTS_SKIP_EMPTY_EXPLORE_CALL"));

    internal static bool ResolveSkipEmptyExploreCall(string? envValue) =>
        !(string.Equals(envValue, "0", StringComparison.Ordinal)
          || string.Equals(envValue, "false", StringComparison.OrdinalIgnoreCase)
          || string.Equals(envValue, "off", StringComparison.OrdinalIgnoreCase));

    // Consecutive empty-space Explore LLM-call skips since the last real call. Bounds
    // how far an untargeted travel excursion runs before the LLM re-evaluates the
    // heading; reset to 0 on every real LLM call (and whenever the skip gate declines).
    private int _emptyExploreSkips;

    // How many consecutive empty-space Explore calls the gate may skip before forcing
    // a real LLM deliberation. Keeps a barren excursion from drifting one direction
    // indefinitely without the LLM re-choosing where to look.
    private const int MaxEmptyExploreSkips = 6;

    // True iff the bot's MOST-RECENT emitted goal was an UNTARGETED Explore (the schema
    // "anywhere" sentinel or an empty target) — i.e. the bot is mid-travel with nothing
    // to interact with. Scans the bot's own emission history newest-first and stops at
    // the first emitted goal: a targeted Explore or any other verb returns false (the
    // bot was doing something concrete, not aimless travel). Schema-level goal-shape
    // over the bot's OWN history; no game knowledge.
    internal static bool LastEmitWasUntargetedExplore(EventStream events)
    {
        foreach (var e in events.Recent())
        {
            if (e.Kind != EventKind.GoalEmitted) continue;
            var t = e.Text;
            if (string.IsNullOrEmpty(t) || !t.StartsWith("Explore ", StringComparison.Ordinal)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(t, "target=(.*?) item=");
            if (!m.Success) return false;
            var sel = m.Groups[1].Value.Trim();
            return sel.Length == 0 || sel == "<empty>"
                || System.Text.RegularExpressions.Regex.IsMatch(
                    sel, "name=\"anywhere\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return false;
    }

    // Max consecutive autonomous Attacks the Motor may mint before it MUST
    // route a real LLM decision. A periodic oversight + hard liveness cap so a
    // never-completing commitment cannot chain forever. Reset to 0 whenever a
    // real LLM call is made (so each grind run gets a fresh budget). Tunable via
    // AC_BOTS_MAX_COMBAT_CHAIN (default 6, clamp [1, 12]); read once at type-load.
    // The ceiling is kept near the well-tested default so the window before a
    // forced LLM re-check (during which a non-chain-interrupting signal goes
    // unseen) stays small.
    internal static readonly int MaxCombatChainAttacks =
        ResolveMaxCombatChainAttacks(Environment.GetEnvironmentVariable("AC_BOTS_MAX_COMBAT_CHAIN"));

    // Parse AC_BOTS_MAX_COMBAT_CHAIN. A positive integer is used (clamped to
    // [1, 12]); anything else (unset/blank/unparseable/<1) falls back to 6.
    internal static int ResolveMaxCombatChainAttacks(string? envValue)
    {
        const int Default = 6;
        const int Min = 1;
        const int Max = 12;
        if (int.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= Min)
            return Math.Min(v, Max);
        return Default;
    }
    private int _combatChainCount;
    // Throttle for the [combat-chain] no-mint diagnostic: only log when the
    // reason CHANGES (the chain gate is evaluated ~4x/sec, so logging every tick
    // would spam). Diagnostic-only state; no behavior.
    private string? _lastChainNoMintReason;

    // On an HTTP 413 the adaptive prompt ceiling steps DOWN by this factor
    // rather than collapsing straight to the floor. A single 413 — e.g. from a
    // fallback-rotation model whose request-size limit is below the active
    // model's — should not crater the whole session's prompt budget to the
    // minimum and starve perception for every subsequent call. Repeated 413s
    // walk it down; the floor clamp keeps it at or above the minimum. ~0.8 backs
    // a ~26000 ceiling off to ~20800 in one step, clearing the measured payload
    // cliff of the common endpoints while preserving far more context than the
    // floor would.
    private const double PromptCeilingPayloadStepDownFactor = 0.8;

    // Decide the new adaptive prompt ceiling after an LLM call failure. On an
    // HTTP 413 (Payload Too Large) — by structured status OR an error string
    // containing "413" — step the ceiling DOWN toward (never below)
    // <paramref name="floor"/> so the next request is smaller and more likely to
    // fit the endpoint's payload limit, WITHOUT discarding all context at once.
    // One-way: never raises; a no-413 failure (or already-at-floor) returns the
    // current ceiling unchanged. Request-size management keyed to the endpoint,
    // not game logic.
    internal static int LowerCeilingOnPayloadTooLarge(
        int currentCeiling, System.Net.HttpStatusCode? status, string? error, int floor)
    {
        var is413 =
            status == System.Net.HttpStatusCode.RequestEntityTooLarge ||
            (error is not null && error.Contains("413"));
        if (!is413 || currentCeiling <= floor)
            return currentCeiling;
        var stepped = (int)(currentCeiling * PromptCeilingPayloadStepDownFactor);
        return Math.Max(stepped, floor);
    }

    // Lowest-value variable row-sections first. Out-of-view sightings are
    // the least actionable, then historical events, then the FAR end of
    // the nearest-first visible list. This order keeps the most actionable
    // content (nearest visible objects, and every fixed section such as
    // `## Combat readiness`) intact longest. `## Inventory` is last-resort
    // trimmable: its rows are deduped (xN-collapsed) at render time so it is
    // normally small, but a pathological bag of many DISTINCT items could
    // still bloat it; trimming its trailing rows here is preferable to the
    // defensive hard-cut guillotining the FIXED sections that render after
    // it. Trimming is by trailing-row count only — no in-game-type priority.
    private static readonly string[] PromptTrimOrder =
    {
        "## Recently sighted (out of view)",
        "## Recently sighted NPCs (out of view)",
        "## Recent events (newest first)",
        "## Visible nearby",
        "## Inventory",
    };

    // Diagnostic helper: the top-level `## ` section header names present in a
    // prompt, in order. The built prompt is chronically larger than the request-byte
    // ceiling, so the fitter trims the body's trailing sections; logging the SURVIVING
    // sections of the SENT prompt lets the operator see at a glance which sections
    // actually reached the model on a given call (vs were truncated out). A
    // protected-tail capsule re-surfaces a section under a header carrying a long
    // "... re-surfaced because ...)" note, sometimes with descriptive pre-text (e.g.
    // "Held items (you are carrying these — re-surfaced because ...)"); for ANY header
    // whose parenthetical contains "re-surfaced", that whole parenthetical is compacted
    // to a short "(re-surfaced)" tag while the base name is kept, so the capsule shows
    // as a DISTINCT, identifiable entry from its body section (the operator can see a
    // capsule survived even when its body section was trimmed). Ordinary headers that
    // legitimately contain parentheses (e.g. "Recently sighted (out of view)") do NOT
    // contain "re-surfaced" and are left intact. Pure string scan; no game knowledge,
    // no behavior change, no parsing of section CONTENT.
    internal static IReadOnlyList<string> PromptSectionHeaders(string prompt)
    {
        var headers = new List<string>();
        if (string.IsNullOrEmpty(prompt)) return headers;
        foreach (var rawLine in prompt.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("## ", StringComparison.Ordinal)) continue;
            var name = line.Substring(3).Trim();
            var rs = name.IndexOf(" (", StringComparison.Ordinal);
            if (rs > 0 && name.Contains("re-surfaced", StringComparison.Ordinal))
                name = name.Substring(0, rs) + " (re-surfaced)";
            headers.Add(name);
        }
        return headers;
    }

    internal static string FitPromptToCeiling(string prompt, int ceiling = HardUserPromptCeilingChars)
    {
        if (prompt.Length <= ceiling)
            return prompt;
        var nl = prompt.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new List<string>(prompt.Split(new[] { nl }, StringSplitOptions.None));
        foreach (var header in PromptTrimOrder)
        {
            if (JoinLen(lines, nl) <= ceiling)
                break;
            TrimSectionTrailingRows(lines, header, nl, ceiling);
        }
        var result = string.Join(nl, lines);
        if (result.Length > ceiling)
        {
            // Defensive backstop so the ceiling is an UNCONDITIONAL
            // invariant. With the current ~19KB fixed preamble + bounded
            // sections the cascade above always fits, so this only fires if
            // the trimmable sections are absent AND the fixed content alone
            // exceeds the ceiling (e.g. a future preamble growth) — better a
            // truncated prompt than an HTTP 413 + brainless fallback.
            const string suffix = "\n\u2026 (prompt hard-truncated to fit request budget)";
            var cut = Math.Max(0, ceiling - suffix.Length);
            result = result[..cut] + suffix;
            // Final clamp: when the ceiling is smaller than the marker itself
            // (reachable when a caller fits the BODY into a tiny remaining
            // budget after reserving a large protected suffix), the line above
            // still exceeds the ceiling. Clamp unconditionally so the ceiling
            // is an absolute invariant for every caller.
            if (result.Length > ceiling)
                result = result[..ceiling];
        }
        return result;
    }

    // cp-2343 — fit a prompt that ends in a PROTECTED salience-capsule suffix.
    // The single-argument overload above trims the four PromptTrimOrder
    // sections and then, as a last resort, hard-cuts the TAIL of the string —
    // which removes the decision-proximate end-capsules (## Unspent XP,
    // ## Recent Talk/Use, ## Server-refused, ## Approach distance history)
    // whenever a large context pushes the prompt over the ceiling. This
    // overload keeps the capsules: it fits the BODY into (ceiling − suffix
    // length) using the exact same tested cascade + backstop, then re-appends
    // the protected suffix intact. Because the body's own trailing sections
    // (the lowest-value ## blocks that render just above the capsules) absorb
    // the body hard-cut, the fixed decision sections that render far earlier
    // (## Combat readiness et al.) are unaffected — the cp-2334 invariant is
    // preserved. No game knowledge; pure request-size management by structural
    // position.
    internal static string FitPromptToCeiling(
        string body, string protectedSuffix, int ceiling = HardUserPromptCeilingChars)
    {
        if (string.IsNullOrEmpty(protectedSuffix))
            return FitPromptToCeiling(body, ceiling);
        if (body.Length + protectedSuffix.Length <= ceiling)
            return body + protectedSuffix;
        // The protected suffix alone meets/exceeds the ceiling (should not
        // happen — each capsule caps its own rows). Preserve the ceiling as an
        // UNCONDITIONAL invariant over capsule survival: fit the whole string.
        if (protectedSuffix.Length >= ceiling)
            return FitPromptToCeiling(body + protectedSuffix, ceiling);
        // Normal path: reserve the suffix's length, fit the body into the
        // remainder with the tested single-argument logic (whose final clamp
        // guarantees the returned body is ≤ the inner ceiling even when that
        // remainder is below the truncation-marker length), then re-append the
        // capsules. Total length ≤ (ceiling − suffix) + suffix = ceiling.
        return FitPromptToCeiling(body, ceiling - protectedSuffix.Length) + protectedSuffix;
    }

    private static int JoinLen(List<string> lines, string nl) =>
        lines.Count == 0 ? 0 : lines.Sum(l => l.Length) + nl.Length * (lines.Count - 1);

    // Drop trailing rows of the named section (keeping the header line),
    // keeping as MANY rows as still fit under the ceiling; if even an
    // empty section doesn't fit, drop them all and let the caller cascade
    // to the next section. A single compact marker replaces the dropped
    // rows. Section bounds are line-anchored (header line .. next `## `
    // line); absent sections are a no-op.
    private static void TrimSectionTrailingRows(List<string> lines, string header, string nl, int ceiling)
    {
        int start = lines.FindIndex(l => l == header);
        if (start < 0)
            return;
        int end = lines.FindIndex(start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0)
            end = lines.Count;
        int bodyCount = end - (start + 1);
        if (bodyCount <= 0)
            return;

        for (int keep = bodyCount; keep >= 0; keep--)
        {
            var trial = new List<string>(lines);
            int removeFrom = start + 1 + keep;
            int removeCount = end - removeFrom;
            if (removeCount > 0)
            {
                trial.RemoveRange(removeFrom, removeCount);
                trial.Insert(removeFrom, $"- (\u2026 {removeCount} farther row(s) omitted to fit prompt budget)");
            }
            if (keep == 0 || JoinLen(trial, nl) <= ceiling)
            {
                lines.Clear();
                lines.AddRange(trial);
                return;
            }
        }
    }

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "...");

    // Collapse a possibly multi-line dat string to a single trimmed line
    // (each prompt row is one line), or null when blank. Caps the length so a
    // long objective string can't crowd out the rest of the prompt. Mechanical
    // text shaping only — the dat's own words, no game knowledge applied.
    private static string? OneLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var collapsed = string.Join(' ',
            s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return null;
        return collapsed.Length <= 200 ? collapsed : collapsed[..200] + "...";
    }

    // True when <paramref name="itemName"/> appears in <paramref name="text"/> as
    // a WHOLE token (bounded by string edges or non-alphanumeric chars), so a
    // short/common held-item name does not match inside an unrelated word (e.g.
    // "Key" must not match "monkey", "Stone" must not match "milestone"). Pure
    // case-insensitive text matching of the bot's OWN inventory name against the
    // server's OWN directive text — no game knowledge, no item/NPC literals.
    internal static bool DirectiveNamesItem(string? text, string? itemName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(itemName)) return false;
        var from = 0;
        int at;
        while ((at = text.IndexOf(itemName, from, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var end = at + itemName.Length;
            var afterOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeOk && afterOk) return true;
            from = at + 1;
        }
        return false;
    }

    // Append the `## Corpse` prompt section. Two mutually-exclusive branches,
    // prompt text only: (1) when HasOwnCorpseInView matches a visible entry,
    // emit that branch's text and return; (2) otherwise, when the projection
    // carries CorpseWorldX/Y, emit a bearing+distance from those coords vs the
    // bot's position. Renders only; makes no decision.
    private static void AppendCorpseRecovery(StringBuilder sb, WorldStateProjection world)
    {
        // (1) A visible entry matched HasOwnCorpseInView.
        if (HasOwnCorpseInView(world.Visible, world.Self.Name))
        {
            sb.AppendLine();
            sb.AppendLine("## Corpse");
            sb.AppendLine(
                $"- one of the corpses in `## Visible nearby` is YOUR OWN (`Corpse of {world.Self.Name}`): " +
                "it holds the items you dropped when you died. `Use` it to recover them before it decays. " +
                "OPTIONAL: skip if you have already looted it or have more pressing progress.");
            return;
        }
        // (2) Projection carries death coords -> emit the bearing.
        if (world.CorpseWorldX is not float cx || world.CorpseWorldY is not float cy) return;
        if (ContractSelfXY(world) is not { } s) return;
        var dx = cx - s.Gx;
        var dy = cy - s.Gy;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        var dir = Compass8(dx, dy).ToLowerInvariant();
        var age = world.CorpseAgeSeconds is int a ? $" (you died ~{a}s ago)" : string.Empty;
        sb.AppendLine();
        sb.AppendLine("## Corpse");
        sb.AppendLine(
            $"- your own corpse is ~{dist:F0}u to the {dir}{age} and holds whatever you dropped when you " +
            $"died. To recover it, travel back — `Explore{{target: {{name: \"anywhere\"}}, direction: \"{dir}\"}}` " +
            "(keep heading that bearing each tick) — and once your `corpse` is in `## Visible nearby`, `Use` it " +
            "to loot. This is OPTIONAL: if it is far or you have more pressing progress, skip it.");
    }

    // True when some visible entry has IsCorpse set and Name equal (ordinal,
    // case-insensitive) to "Corpse of " + selfName. Pure match on the
    // caller-supplied name; returns on the first match; no decision.
    internal static bool HasOwnCorpseInView(
        IReadOnlyList<VisibleObjectProjection> visible, string? selfName)
    {
        if (visible is null || string.IsNullOrWhiteSpace(selfName)) return false;
        var ownCorpseName = "Corpse of " + selfName;
        foreach (var v in visible)
        {
            if (!v.IsCorpse) continue;
            if (string.Equals(v.Name, ownCorpseName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // The bot's own global (worldX, worldY) — used to turn a contract's dat-defined
    // location into a bearing+distance. Null when the bot's position is unknown.
    private static (float Gx, float Gy)? ContractSelfXY(WorldStateProjection world) =>
        world.Self.CellId is uint selfCell
            ? AcCoords.ToGlobalXY(selfCell,
                new System.Numerics.Vector3(world.Self.PositionX, world.Self.PositionY, world.Self.PositionZ))
            : null;

    // Build ONE `## Contracts` capsule row's text and report whether it carries a
    // travel bearing. Single source of truth shared by the capsule emit and the
    // early AnyRenderedContractBearing pre-pass, so the rendered-bearing gate the
    // RETURN-TO-A-CONTRACT-SOURCE nudge consults can never drift from what the
    // capsule actually shows. Pure projection of contract dat facts + the bot's own
    // goal history; no object-type priority, no source-side decision.
    private static StringBuilder BuildContractEntry(
        ContractProjection c, WorldStateProjection world, EventStream events,
        (float Gx, float Gy)? selfXY, out bool hasBearing)
    {
        var entry = new StringBuilder();
        hasBearing = false;
        var name = OneLine(c.Name);
        entry.AppendLine(name is null
            ? $"  - contract {c.ContractId}: stage {c.Stage}"
            : $"  - contract {c.ContractId} \"{name}\": stage {c.Stage}");
        var objective = OneLine(c.Description);
        if (objective is not null)
            // A stage-3 (DoneOrPendingRepeat) contract's objective is ALREADY
            // satisfied — mark the objective line complete immediately so the LLM
            // does not pursue it as an active task. Mechanical: keys on the
            // c.Stage==3u wire value; the objective text itself is server data
            // rendered as-is. Kept short to limit its char cost against the
            // capsule budget. (The separate DONE note below still fires once the
            // bot has over-pursued the turn-in/locate NPC; this qualifier is the
            // earlier, no-pursuit-needed signal so a satisfied objective is never
            // chased even once.)
            entry.AppendLine(c.Stage == 3u
                ? $"      objective: {objective}  (stage 3 done; objective already satisfied, do not pursue it)"
                : $"      objective: {objective}");
        var progress = OneLine(c.DescriptionProgress);
        if (progress is not null)
            entry.AppendLine($"      in progress: {progress}");
        var npcStart = OneLine(c.NpcStart);
        if (npcStart is not null)
            entry.AppendLine($"      start NPC: {npcStart}");
        var npcEnd = OneLine(c.NpcEnd);
        if (npcEnd is not null)
            entry.AppendLine($"      turn-in NPC: {npcEnd}");
        // Dat-defined locations as a bearing+distance from the bot, so it can head
        // there (Explore accepts a compass `direction`). Only when the dat carried
        // the location AND the bot's position is known. Raw facts; the LLM decides.
        string? BearingTo(float? tx, float? ty)
        {
            if (selfXY is not { } s || tx is not float x || ty is not float y) return null;
            var dx = x - s.Gx;
            var dy = y - s.Gy;
            var dist = MathF.Sqrt(dx * dx + dy * dy);
            return $"~{dist:F0}u {Compass8(dx, dy)}";
        }
        if (BearingTo(c.QuestAreaWorldX, c.QuestAreaWorldY) is string areaAt)
        {
            entry.AppendLine($"      objective area: {areaAt} from you");
            hasBearing = true;
        }
        if (BearingTo(c.TurnInWorldX, c.TurnInWorldY) is string turnInAt)
        {
            entry.AppendLine($"      turn-in location: {turnInAt} from you");
            hasBearing = true;
        }
        // A stage-3 contract is already complete. If the bot has ALREADY PURSUED its
        // turn-in NPC past the post-completion attempt threshold (Talk hand-in OR
        // navigate-only Explore locate) and it is STILL stage 3, that contract has no
        // separate hand-in (its reward is the issuer's to grant on its own terms) —
        // surface that mechanical fact + the bot's OWN attempt count so the LLM stops
        // re-attempting a turn-in/locate that has had no effect. The recognition lives
        // in IsSettledStage3TurnIn, SHARED with the Motor's settled-turn-in Talk
        // backstop so the prompt note and the drop can never drift.
        if (IsSettledStage3TurnIn(world, events, c, out var talkTries, out var exploreTries))
            entry.AppendLine(
                $"      DONE (stage 3, complete): you have already gone to " +
                $"{npcEnd} {talkTries + exploreTries}x (Talk/Explore) since this " +
                $"contract completed and it is STILL stage 3 — it has no separate " +
                $"hand-in, so further turn-in/locate attempts on {npcEnd} for it " +
                $"will not change anything. Treat it as finished and spend your " +
                $"turn on other progress (accept or complete another task).");
        return entry;
    }

    // Does ANY contract row that the `## Contracts` capsule actually RENDERS carry a
    // travel bearing? Mirrors the capsule's budget pass EXACTLY (same shared
    // BuildContractEntry, same ContractsProtectedCharBudget break, first row always
    // kept) so the RETURN-TO-A-CONTRACT-SOURCE name branch fires precisely when no
    // bearing is shown to copy — including when a later contract HAS coords but its
    // row is budget-dropped (no bearing visible). Read-only; no game knowledge.
    private static bool AnyRenderedContractBearing(WorldStateProjection world, EventStream events)
    {
        if (world.Contracts.Count == 0) return false;
        var selfXY = ContractSelfXY(world);
        var contractsShown = 0;
        var contractsChars = 0;
        foreach (var c in world.Contracts)
        {
            var entry = BuildContractEntry(c, world, events, selfXY, out var hasBearing);
            if (contractsShown > 0 && contractsChars + entry.Length > ContractsProtectedCharBudget)
                break;
            if (hasBearing)
                return true;
            contractsChars += entry.Length;
            contractsShown++;
        }
        return false;
    }

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
    // within budget we include rows NEAREST-FIRST (pure geometry) up to a
    // char budget + row soft cap, then summarize whatever was dropped. The
    // source assigns NO per-type priority to which rows survive — distance
    // alone decides, matching the cp-2316 prompt-ceiling trim that also
    // sheds the far tail of nearest-first `## Visible nearby`. The dropped
    // rows are summarized by decoded projection flag (a factual count, not a
    // priority) so the LLM still knows a far interactable exists and can move
    // toward it. The authoritative monster-presence signal the rules rely on
    // lives in `## Combat readiness` (computed directly from world.Visible),
    // not in this rendered text.
    private const int VisibleRowSoftCap = 50;
    private const int VisibleSectionCharBudget = 10000;
    // Headroom reserved (out of the budget) for the two omission-summary
    // lines so the whole section stays <= VisibleSectionCharBudget even after
    // those lines are appended. Generous: at most ~12 "kind=NNNNNNN" pairs.
    private const int VisibleSummaryReserve = 400;
    // Hard clamp on a single rendered row so one pathological object name can
    // never blow the budget (the always-emit-the-nearest-row guarantee).
    private const int VisibleRowMaxChars = 400;

    // Total char budget for the protected `## Nearest objects` capsule ROWS
    // (cp-2367) — bounds the rendered object rows only; the fixed header + the
    // one-line count caption (~230 chars) are added on top, so the whole
    // capsule is at most ~700 chars. Kept small so the protected salience tail
    // stays well under the request ceiling — the body fitter reserves the
    // tail's length and trims the BODY's trailing dynamic sections (never the
    // head preamble, which the hard-cut preserves) to fit. ~450 chars fits
    // several short rows (nearest-first) while leaving ample body headroom.
    private const int NearestObjectsProtectedCharBudget = 450;

    // Total char budget for the protected `## Untalked NPCs nearby` capsule
    // ROWS — bounds the not-yet-talked NPC rows re-surfaced because they fell
    // outside the distance-capped `## Nearest objects` capsule. Because it
    // renders only the not-yet-talked-NPC subset (filtered by own talked-set
    // bookkeeping + wire creature flags), ~450 chars reaches several rows
    // deeper into the by-distance object rank than the same budget spent on all
    // object types would, while keeping the combined protected salience tail
    // well under the request ceiling so the body hard-cut never eats the head
    // preamble. Same sizing rationale as `## Nearest objects`.
    private const int UntalkedNpcsProtectedCharBudget = 450;

    // Total char budget for the protected `## Held items` capsule ROWS (cp-2389)
    // — bounds the rendered inventory rows so a huge bag cannot bloat the
    // protected salience tail. ~1200 chars fits roughly a dozen item rows
    // (name + truncated short_desc), enough to surface the academy's quest
    // items; deduped first so duplicate stacks don't consume the budget.
    private const int HeldItemsProtectedCharBudget = 1200;

    // Total char budget for the protected `## Contracts` capsule ROWS — bounds
    // the rendered contract rows so a bloated tracker (many contracts, or long
    // dat objective text) cannot grow the protected salience tail enough to
    // force the body hard-cut to eat the fixed rules preamble. ~1200 fits
    // several contracts (id + stage + objective + turn-in NPC, each OneLine-
    // capped); the header + caption + disclaimer (~300 chars) are added on top.
    // Same sizing rationale as `## Held items`.
    private const int ContractsProtectedCharBudget = 1200;

    // Total char budget for the protected `## Vendor offerings` capsule ROWS —
    // bounds the rendered for-sale rows so a large vendor list (e.g. a general
    // store) cannot grow the protected tail enough to evict the rules preamble.
    // ~1200 fits roughly a dozen item rows (name + buy cost), each OneLine-capped;
    // the header + disclaimer (~250 chars) are added on top. Same rationale as
    // `## Held items` / `## Contracts`.
    private const int VendorOfferingsProtectedCharBudget = 1200;

    // ACE.Entity.Enum.ItemType.PromissoryNote (0x00040000): the one item type
    // for which the server's Vendor.GetSellCost overrides the per-vendor
    // SellPrice with a fixed rate (below), ignoring the vendor's own multiplier.
    // Mirrored so the rendered buy cost equals what the server will charge. A
    // wire/datatype constant + the server's own price FACT (source of truth:
    // ACE-bots Source/ACE.Entity/Enum/ItemType.cs and
    // Source/ACE.Server/WorldObjects/Vendor.cs GetSellCost) — NOT an object-type
    // priority or a bot preference.
    private const uint ItemTypePromissoryNote = 0x00040000u;
    private const float PromissoryNoteSellRate = 1.15f;

    // How many of the EARLIEST persisted distinct server PopupStrings the
    // `## Early server directives` protected-tail capsule re-surfaces. The
    // ring-evicted earliest directives that `## Server hints` tries to keep
    // (its earliest:6) are the durable onboarding/exit anchors; mirror that
    // count here so they survive even when `## Server hints` is itself hard-cut
    // by the request-size fitter. Each line is truncated, so the capsule stays
    // bounded (~6 * ~250 chars + framing) and the protected tail stays well
    // under the request ceiling.
    private const int EarlyServerDirectiveCount = 6;

    // How many of the EARLIEST persisted distinct NPC-spoken directives the
    // `## Early server directives` capsule re-surfaces. Smaller than the popup
    // count because NPC speech is chattier; the earliest NPC lines in any area
    // are the onboarding/directional ones, and each is truncated so the capsule
    // stays bounded.
    private const int EarlyNpcDirectiveCount = 4;

    private static string ClampRow(string row) =>
        row.Length <= VisibleRowMaxChars
            ? row
            : row.Substring(0, VisibleRowMaxChars - 1) + "\u2026";

    private static string RenderVisibleRow(
        VisibleObjectProjection v,
        IReadOnlyList<CombatHistoryEntry>? combatHistory = null,
        IReadOnlySet<uint>? openedCorpseGuids = null)
    {
        var sb = new StringBuilder();
        sb.Append($"- {v.Name}");
        // Role/title (weenie Quality string) in quotes after the name, so the
        // LLM can match a directive that names a target by ROLE ("talk to the
        // captain") to the visible NPC whose title carries that role. Only
        // objects that have one (typically NPCs) render it; pure projection.
        // SANITIZED to a single bounded line with no embedded double-quote, so
        // a multi-line or quote-bearing weenie string can never split this row
        // into extra prompt lines or close the role quote early.
        if (OneLine(v.Title) is string roleTitle)
            sb.Append($" \"{Truncate(roleTitle.Replace('"', '\''), 60)}\"");
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
        if (v.IsDoor)
        {
            sb.Append(" door");
            // Door open/closed affordance (wire-decoded Ethereal bit). A
            // CLOSED door is a barrier the LLM can Use{door} to open (e.g.
            // to reach a target in the next room); an OPEN door is already
            // passable. Omitted when the door's physics state is unknown so
            // we never assert a state without evidence. Pure observation —
            // no priority/urgency; the LLM decides whether to open it.
            if (v.IsDoorOpen is bool doorOpen)
                sb.Append(doorOpen ? " open" : " closed");
        }
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
        // loot bookkeeping: annotate a CORPSE row with whether the bot has
        // itself already opened it this session (cp-2314 follow-up). The wire
        // IsCorpse flag cannot say "I already looted this one"; the
        // openedCorpseGuids set is pure own-action bookkeeping (a corpse the
        // bot opened, TTL-bounded, NOT cleared when emptied so the claim stays
        // truthful). Lets the LLM avoid re-picking a corpse it already looted
        // and weigh an un-opened own kill it might otherwise walk past. No
        // priority/urgency — the LLM decides whether to Use{corpse}.
        if (v.IsCorpse)
            sb.Append(openedCorpseGuids is not null && openedCorpseGuids.Contains(v.Guid)
                ? " opened_by_bot_recently=yes"
                : " opened_by_bot_recently=no");
        // combat-feel: annotate a MONSTER row with the bot's own recorded
        // outcomes against that monster KIND this session (cp-2311/2312
        // feed the ledger; cp-2289 classifies the IsMonster wire flag).
        // This is the SAME inline annotation already shown on the
        // `nearest monster` / `observed hostile` lines, extended to EVERY
        // visible monster so the LLM sees "you have died to this kind" at
        // the exact decision point instead of cross-referencing the
        // aggregate `combat history` block. Monster rows only — never
        // annotate an npc/object even if a same-name history row exists.
        // Raw counts via the existing helper (empty string when no record);
        // no danger label, no priority — the COMBAT SAFETY rule decides.
        if (v.IsMonster)
            sb.Append(FormatCombatRecordFor(combatHistory, v.Wcid, v.Name));
        return sb.ToString();
    }

    private static string SummarizeOmittedTags(IReadOnlyList<VisibleObjectProjection> omitted)
    {
        int monster = 0, npc = 0, portal = 0, door = 0, corpse = 0, chest = 0,
            book = 0, sign = 0, lifestone = 0, vendor = 0, healer = 0, openable = 0,
            other = 0;
        foreach (var v in omitted)
        {
            bool tagged = false;
            if (v.IsCreature) { if (v.IsMonster) monster++; else npc++; tagged = true; }
            if (v.IsPortal) { portal++; tagged = true; }
            if (v.IsDoor) { door++; tagged = true; }
            if (v.IsCorpse) { corpse++; tagged = true; }
            if (v.IsChest) { chest++; tagged = true; }
            if (v.IsBook) { book++; tagged = true; }
            if (v.IsSign) { sign++; tagged = true; }
            if (v.IsLifestone) { lifestone++; tagged = true; }
            if (v.IsVendor) { vendor++; tagged = true; }
            if (v.IsHealer) { healer++; tagged = true; }
            if (v.IsOpenable) { openable++; tagged = true; }
            if (!tagged) other++;
        }
        var parts = new List<string>();
        void Add(string k, int n) { if (n > 0) parts.Add($"{k}={n}"); }
        Add("monster", monster); Add("npc", npc); Add("portal", portal);
        Add("door", door); Add("corpse", corpse); Add("chest", chest);
        Add("book", book); Add("sign", sign); Add("lifestone", lifestone);
        Add("vendor", vendor); Add("healer", healer); Add("openable", openable);
        Add("other", other);
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    // ── Recently sighted (out of view) recall block ──────────────────
    // Tunables for the remembered-monster recall section. Small, bounded:
    // a secondary perception surface, not a strategy.
    private const double RecentSightingTtlSeconds = 180.0;   // drop sightings older than 3 min
    private const int    RecentSightingMaxRows    = 5;        // cap rows surfaced
    private const int    RecentSightingCharBudget = 900;      // hard char ceiling for the block body

    /// <summary>
    /// Renders the "## Recently sighted (out of view)" block: the bot's
    /// own remembered MONSTER sightings that are NOT currently visible,
    /// so the LLM can choose to navigate back to one that left view. This
    /// is the recall analog of the live "nearest monster" line — pure
    /// perception, no priority assigned. Renders nothing (no header) when
    /// there is nothing to surface, to keep the static prompt floor
    /// unchanged for the common in-town / fresh-bot case.
    ///
    /// Filtering (all deterministic given the projected input):
    ///   * Mob-kind only (the remembered analog of the visible IsMonster
    ///     surface; NPC/Unknown remembered creatures are not listed).
    ///   * Exclude any sighting whose creature is CURRENTLY visible
    ///     (same wcid when both known, else same name) — it is already
    ///     in "## Visible nearby" / "## Combat readiness".
    ///   * TTL: drop sightings older than <see cref="RecentSightingTtlSeconds"/>.
    ///   * Dedup by (name, wcid, landblock); keep the most-recent.
    ///   * Most-recent first; capped by row count and a char budget.
    /// </summary>
    /// <summary>
    /// Looks up the bot's OWN recorded combat outcomes for a visible
    /// monster and returns an AGGREGATE record (summed counts) over every
    /// history row that shares the monster's identity — i.e. the exact
    /// wcid-preferred key OR the exact normalized display name. The name
    /// join is load-bearing: the wire assigns DIFFERENT wcids to variants
    /// that share one display name (e.g. an aggro and a no-aggro "Drudge
    /// Skulker"), so a death recorded against one variant must still warn
    /// the LLM about the other (the LLM reasons by name). Aggregating —
    /// rather than returning one arbitrary row — keeps the surfaced counts
    /// complete and order-independent. Matching is EXACT only: no
    /// substring/fuzzy match, and the "(unknown)" display fallback never
    /// joins. Returns null when there is no history or nothing matches.
    /// Pure projection join: surfaces the SAME raw counts already in the
    /// combat-history block, colocated at the decision point. No priority,
    /// no danger label, no ordering.
    /// </summary>
    internal static CombatHistoryEntry? FindCombatRecord(
        IReadOnlyList<CombatHistoryEntry>? history, uint? wcid, string? name)
    {
        if (history is null || history.Count == 0) return null;
        var key = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(wcid, name));
        if (key is null) return null;
        var normName = CombatFeelLedger.NormalizeName(name);

        int fights = 0, kills = 0, deaths = 0, nearDeaths = 0, ineffective = 0;
        int? maxLossBotLevel = null; // highest loss level across matched rows
        string? lastOutcome = null;   // history is recency-ordered: first match is newest
        string? displayName = null;   // representative name: first (newest) matched row
        var matched = false;
        foreach (var h in history)
        {
            var hKey = CombatFeelLedger.KeyOf(new CombatFeelLedger.MobIdentity(h.Wcid, h.Name));
            var isMatch = (hKey is not null && hKey == key)
                || (normName is not null
                    && CombatFeelLedger.NormalizeName(h.Name) == normName);
            if (!isMatch) continue;
            matched = true;
            fights += h.Fights;
            kills += h.Kills;
            deaths += h.Deaths;
            nearDeaths += h.NearDeaths;
            ineffective += h.Ineffective;
            if (h.MaxLossBotLevel is int hl)
                maxLossBotLevel = maxLossBotLevel is int cur ? (hl > cur ? hl : cur) : hl;
            lastOutcome ??= h.LastOutcome;
            displayName ??= h.Name;
        }
        if (!matched) return null;
        return new CombatHistoryEntry(
            Name: displayName ?? name ?? "(unknown)",
            Wcid: wcid,
            Kills: kills,
            Deaths: deaths,
            NearDeaths: nearDeaths,
            Ineffective: ineffective,
            Fights: fights,
            LastOutcome: lastOutcome ?? "",
            MaxLossBotLevel: maxLossBotLevel);
    }

    /// <summary>
    /// True iff the bot's OWN combat-feel <paramref name="history"/> marks the
    /// kind (<paramref name="wcid"/>/<paramref name="name"/>) as a repeated loss
    /// the bot has NOT out-leveled. "Beaten" = Kills==0 with a recorded loss
    /// (death/near-death/ineffective) — UNLESS that loss was NON-LETHAL
    /// (Deaths==0) and the bot's <paramref name="currentLevel"/> now exceeds the
    /// highest level it lost at, in which case it is re-testable (the bot is
    /// stronger now). Kinds that have EVER killed the bot (Deaths&gt;0) stay
    /// beaten regardless of level for autonomous picks (protects the no-death
    /// record); a caller acting on an explicit, deliberate order may set
    /// <paramref name="lethalRetestableWhenOutleveled"/> to let a lethal kind be
    /// re-tested too — once the bot out-levels the loss, OR (to break the
    /// single-death death-spiral) after just ONE recorded lethal loss, since a
    /// first-encounter death is not proof the kind is unbeatable and a permanent
    /// lock wedges the bot when this is the only kind in view to fight. A SECOND
    /// lethal loss (Deaths&gt;=2) restores the out-level requirement. Aggregates by
    /// wcid OR normalized name via <see cref="FindCombatRecord"/>. Shared by the
    /// fallback hunt-target skip AND the outdoor frontier mob-bias so both avoid
    /// the SAME kinds. Bot-owned outcomes + own level only; no game knowledge.
    /// Null/empty history or no matching record =&gt; not beaten.
    /// </summary>
    internal static bool IsBeatenKind(
        IReadOnlyList<CombatHistoryEntry>? history, uint? wcid, string? name, int? currentLevel,
        bool lethalRetestableWhenOutleveled = false)
    {
        var record = FindCombatRecord(history, wcid, name);
        if (record is null) return false;
        var lost = record.Kills == 0
            && (record.Deaths > 0 || record.NearDeaths > 0 || record.Ineffective > 0);
        if (!lost) return false;
        // Re-testable once the bot has out-grown the level it last lost at.
        // Non-lethal losses (Deaths==0) re-test this way by default; a lethal
        // loss only re-tests when the caller opts in (explicit, deliberate
        // order), otherwise it stays beaten to protect the no-death record.
        var retestable = record.Deaths == 0 || lethalRetestableWhenOutleveled;
        if (retestable
            && currentLevel is int cur
            && record.MaxLossBotLevel is int maxLossLevel
            && cur > maxLossLevel)
        {
            return false;
        }
        // First-death re-test (explicit-order path only). A SINGLE recorded
        // lethal loss is the bot's FIRST-encounter death to this kind: not
        // conclusive that the kind is unbeatable, and a permanent lock here
        // death-spirals the bot when this kind is the ONLY thing in view to fight
        // (die once -> avoid the only XP source -> never level past
        // MaxLossBotLevel -> never re-test -> wedged). A caller acting on a
        // deliberate order (lethalRetestableWhenOutleveled) has CHOSEN to engage,
        // so let it try again after one death. A SECOND lethal loss (Deaths>=2)
        // restores the out-level gate above. Autonomous picks (flag=false) are
        // unaffected: they still avoid every lethal kind to protect the no-death
        // record. Own death count only; no game knowledge.
        if (lethalRetestableWhenOutleveled && record.Deaths == 1)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Pure target selection for the Motor's autonomous kill-intent
    /// decomposition. Returns the visible monster the Motor may Attack NEXT
    /// toward an LLM-authored kill-count commitment WITHOUT a per-monster LLM
    /// round-trip — or null when no autonomous Attack should be minted (caller
    /// then falls through to a real LLM decision).
    ///
    /// INVARIANT (audit): this NEVER originates combat. It fires ONLY while the
    /// stack top is an LLM-authored TYPED kill-count commitment
    /// (<see cref="CombatCommitment.IsActiveKillCommitment"/>) — the LLM itself
    /// committed to "kill N [of X]" with a numeric predicate. It then picks the
    /// nearest in-PERCEPTION (<paramref name="perceptionRadius"/>) non-corpse
    /// target that is either a monster (`IsMonster`, hostile OR passive — the
    /// SAME set the LLM-decided Hunt decomposition attacks, so a passive-monster
    /// grind chains too) or an actively-`ObservedHostile` creature (self-
    /// defense), that matches the LLM's authored name filter (when the
    /// commitment carried one) and is not an <see cref="IsBeatenKind"/> the bot
    /// keeps losing to. It assigns NO object-type urgency and names no
    /// wcid/NPC/landblock. Flee precedence is NOT enforced here — the Motor's
    /// dispatch self-preservation gate refuses the Attack while health is low /
    /// the threat is on the avoid cooldown, and the CALLER only invokes this on
    /// the quiescent "nothing external happened" path (a fresh disengage is an
    /// ActionRejected external event, which keeps the caller out of here). The
    /// chain is bounded: it yields once
    /// <paramref name="chainCount"/> reaches <paramref name="maxChain"/> so the
    /// LLM re-checks at least every maxChain autonomous minted attacks (the
    /// intent's own completion predicate + deadline and a chain-interrupting
    /// event also end it). The chain is deliberately NOT bounded by the
    /// wall-clock stuck-timeout — a slow kill cycle (travel + swing + cooldown)
    /// routinely exceeds it and would otherwise starve the chain; see the mint
    /// call site.
    /// Disabled via <paramref name="enabled"/>.
    /// </summary>
    internal static VisibleObjectProjection? ChooseCombatChainTarget(
        Intent.Intent? top,
        IReadOnlyList<VisibleObjectProjection>? visible,
        IReadOnlyList<CombatHistoryEntry>? history,
        int? selfLevel,
        bool enabled,
        int chainCount,
        int maxChain,
        float perceptionRadius = WorldStateProjection.DefaultVisibleRadiusUnits,
        bool combatCapable = true,
        bool canUnarmedMelee = false)
        => ChooseCombatChainTarget(
            top, visible, history, selfLevel, enabled, chainCount, maxChain,
            out _, perceptionRadius, combatCapable, canUnarmedMelee);

    /// <summary>
    /// Overload that also reports WHY no target was minted (diagnostic only — the
    /// returned target is identical). <paramref name="skipReason"/> is null when a
    /// target is returned, else a stable tag (chain-disabled / budget-exhausted /
    /// no-visible / no-active-commitment / not-combat-capable / no-matching-monster)
    /// so the chain-never-fires tempo gap is observable in the log. Pure
    /// classification; no behavior change, no game knowledge.
    /// </summary>
    internal static VisibleObjectProjection? ChooseCombatChainTarget(
        Intent.Intent? top,
        IReadOnlyList<VisibleObjectProjection>? visible,
        IReadOnlyList<CombatHistoryEntry>? history,
        int? selfLevel,
        bool enabled,
        int chainCount,
        int maxChain,
        out string? skipReason,
        float perceptionRadius = WorldStateProjection.DefaultVisibleRadiusUnits,
        bool combatCapable = true,
        bool canUnarmedMelee = false)
    {
        skipReason = null;
        if (!enabled) { skipReason = "chain-disabled"; return null; }
        if (chainCount >= maxChain) { skipReason = "budget-exhausted"; return null; } // periodic forced LLM re-check
        // Do not mint an autonomous Attack while the bot cannot land a hit. The bot
        // is attack-viable when it has a usable weapon (combatCapable) OR when no
        // weapon is wielded at all (canUnarmedMelee — the server handles an unarmed
        // melee strike via the normal melee action). Without this gate, the chain
        // keeps decomposing a stale kill-count commitment into doomed swings the
        // server cancels — until the per-target NO-PROGRESS watchdog and the maxChain
        // re-check drain it. Yielding routes control to the LLM, which sees the
        // UNARMED combat-readiness line and arms or does non-combat progress.
        // Pure wire-state gate; the LLM still chose WHAT to do.
        if (!combatCapable && !canUnarmedMelee) { skipReason = "not-combat-capable"; return null; }
        if (visible is null || visible.Count == 0) { skipReason = "no-visible"; return null; }
        if (!CombatCommitment.IsActiveKillCommitment(top, out var nameFilter)) { skipReason = "no-active-commitment"; return null; }

        var target = visible
            // Match the LLM-decided Hunt decomposition's target set
            // (NoQuestKnowledgePolicy attacks any visible, non-corpse, non-beaten
            // `IsMonster`) AND the self-defense set (anything actively
            // `ObservedHostile`) — NOT just `ObservedHostile`. The LLM committed
            // to "kill N [of X]", so executing that commitment means attacking the
            // next matching monster whether or not it is currently attacking the
            // bot; AC's weak grind kinds are PASSIVE, so requiring ObservedHostile
            // made the common passive-monster grind fall back to a per-kill LLM
            // call — defeating the reduce-llm-call-volume purpose. Bounds are
            // unchanged (perception radius, name filter, beaten-skip, maxChain
            // re-check, deadline, and the Motor's low-health dispatch gate).
            .Where(v => (v.IsMonster || v.ObservedHostile) && !v.IsCorpse)
            .Where(v => (v.Distance ?? float.MaxValue) <= perceptionRadius)
            .Where(v => nameFilter is null
                        || (v.Name is { Length: > 0 } n
                            && n.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0))
            .Where(v => !IsBeatenKind(history, v.Wcid, v.Name, selfLevel))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (target is null) skipReason = "no-matching-monster";
        return target;
    }

    /// <summary>
    /// True if the bot can deal damage through the melee/missile attack chain: a
    /// wielded melee weapon, OR a wielded missile weapon WITH ammo loaded in the
    /// ammo slot. Mirrors the combat-effective test the SELF-ARM prompt rule and
    /// cp042's affordance gate use — an empty missile weapon (no loaded ammo)
    /// cannot fire and is NOT combat-capable. Pure wire-state (WieldedAt + typed
    /// ItemType / ammo-slot masks); no game knowledge.
    /// </summary>
    internal static bool IsCombatCapable(IReadOnlyList<InventoryItemProjection>? inventory)
    {
        if (inventory is null) return false;
        var meleeWielded = false;
        var thrownWielded = false;
        var launcherWielded = false;
        var ammoLoaded = false;
        foreach (var i in inventory)
        {
            // A weapon counts only when wielded in a MAIN-WEAPON slot. Loaded ammo
            // sits in the ammo slot (outside WeaponSwap.MainWeaponSlotMask) and can
            // carry a weapon ItemType bit, so the slot mask — not WieldedAt != 0 —
            // is what distinguishes a wielded launcher from loaded ammo (mirrors
            // WeaponSwap.IsWieldedWeapon).
            if (i.WieldedAt is uint w && (w & WeaponSwap.MainWeaponSlotMask) != 0 && i.ItemType is uint it)
            {
                if ((it & ItemTypeMasks.MeleeWeapon) != 0) meleeWielded = true;
                if ((it & ItemTypeMasks.MissileWeapon) != 0)
                {
                    // A THROWN weapon (no AmmoType — it is its own projectile, server
                    // Player_Missile.cs: `ammo = weapon.IsAmmoLauncher ? GetEquippedAmmo()
                    // : weapon`) fires with NO separate ammo, so a wielded thrown weapon
                    // is combat-capable on its own. A LAUNCHER (bow/crossbow/atlatl, with
                    // an AmmoType) needs loaded ammo (the server cancels a launcher attack
                    // when `IsAmmoLauncher && ammo == null`).
                    if (i.AmmoType is null) thrownWielded = true;
                    else launcherWielded = true;
                }
            }
            if (i.WieldedAt is uint aw && aw == ItemTypeMasks.MissileAmmoSlot) ammoLoaded = true;
        }
        return meleeWielded || thrownWielded || (launcherWielded && ammoLoaded);
    }

    /// <summary>
    /// True when NO weapon is wielded in a main-weapon slot — no melee weapon,
    /// no missile weapon (launcher or thrown) in the main-weapon slot. The server
    /// allows a melee attack with no equipped weapon (uses the unarmed skill, fists),
    /// so the bot can fight right now via the normal Melee attack path. This is the
    /// <see cref="WeaponReadiness.UnarmedMeleeOnly"/> state.
    ///
    /// <para>This is intentionally DISTINCT from <see cref="IsCombatCapable"/>:
    /// <c>IsCombatCapable</c> is true only when the bot has a <em>usable</em> weapon
    /// (melee, thrown, or launcher+ammo). <c>CanUnarmedMelee</c> is true only when
    /// NO weapon is wielded at all — no launcher is blocking Melee combat mode. The
    /// separation keeps the arming-affordance system intact: <c>IsCombatCapable ==
    /// false</c> still fires the SELF-ARM affordances (the LLM is told to seek a
    /// weapon) while combat is allowed to proceed unarmed.</para>
    ///
    /// Pure wire-state (WieldedAt + typed ItemType / slot masks); no game knowledge.
    /// </summary>
    internal static bool CanUnarmedMelee(IReadOnlyList<InventoryItemProjection>? inventory)
    {
        if (inventory is null) return true; // null = no info = assume no weapon blocking
        foreach (var i in inventory)
        {
            // Any weapon wielded in a main-weapon slot blocks unarmed melee:
            // a melee weapon means we already have IsCombatCapable=true (not this path);
            // a missile weapon in the main-weapon slot (launcher or thrown) forces
            // Missile combat mode — the server sends melee attacks as Missile opcodes
            // until the mode is changed, so the motor would need to dequip first.
            if (i.WieldedAt is uint w &&
                (w & WeaponSwap.MainWeaponSlotMask) != 0 &&
                i.ItemType is uint it &&
                ((it & ItemTypeMasks.MeleeWeapon) != 0 ||
                 (it & ItemTypeMasks.MissileWeapon) != 0))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Returns the bot's carried-but-UN-EQUIPPED wearable equipment: inventory
    /// items that declare a valid wear slot (<c>ValidLocations != 0</c>) yet are
    /// not currently worn (<c>WieldedAt</c> null/0), EXCLUDING primary weapons
    /// (the weapon affordances own those), the off-hand slot (the weapon/
    /// two-hand interaction is handled elsewhere), and ammunition. What remains
    /// is wearable gear that confers its benefit only while worn and does
    /// nothing sitting in the pack — the LLM is cued to <c>Wield</c> it.
    ///
    /// Pure wire-state (typed <c>ValidLocations</c>/<c>WieldedAt</c> slot bits);
    /// no game knowledge. The result preserves inventory order and is bounded
    /// only by the caller.
    /// </summary>
    internal static IReadOnlyList<InventoryItemProjection> HeldUnequippedWearables(
        IReadOnlyList<InventoryItemProjection>? inventory)
    {
        if (inventory is null || inventory.Count == 0)
            return Array.Empty<InventoryItemProjection>();
        var result = new List<InventoryItemProjection>();
        foreach (var i in inventory)
        {
            if (i.WieldedAt is uint w && w != 0) continue;            // already worn/equipped
            if (i.ValidLocations is not uint vl || vl == 0) continue; // not equippable anywhere
            if ((vl & WeaponSwap.MainWeaponSlotMask) != 0) continue;  // primary weapon → weapon affordances
            if ((vl & WeaponSwap.ShieldSlotMask) != 0) continue;      // off-hand → weapon/two-hand path
            if ((vl & ItemTypeMasks.MissileAmmoSlot) != 0) continue;  // ammunition
            result.Add(i);
        }
        return result;
    }


    /// <summary>
    /// Renders the inline raw-record annotation for a visible monster (or
    /// the empty string when there is no matching history). Raw counts
    /// only — the LLM judges danger from them via the COMBAT SAFETY rule.
    /// </summary>
    internal static string FormatCombatRecordFor(
        IReadOnlyList<CombatHistoryEntry>? history, uint? wcid, string? name)
    {
        var rec = FindCombatRecord(history, wcid, name);
        if (rec is null) return "";
        return $" [your record: fights {rec.Fights}, kills {rec.Kills}, " +
               $"deaths {rec.Deaths}, near-deaths {rec.NearDeaths}, ineffective {rec.Ineffective}, last {rec.LastOutcome}]";
    }

    /// <summary>
    /// Render a self-stamina line for the prompt ONLY when stamina is
    /// MEANINGFULLY LOW (current at or below half the observed-peak max).
    /// Stamina is the melee/run sustain pool: at low stamina swings weaken
    /// and the bot cannot run, so surfacing it lets the LLM rest/recover
    /// before an OPTIONAL fight (the way it already does for health). A
    /// near-full reading (e.g. 99/100) is NOT surfaced — it is a SIGNAL, not
    /// noise — which also keeps the static prompt floor unchanged AND makes
    /// the observed-peak under-estimate conservative (an under-estimated max
    /// reads as a HIGHER fraction, so it can only SUPPRESS, never false-fire).
    /// Returns null when full/near-full/unknown.
    /// </summary>
    internal const float StaminaLowFraction = 0.5f;
    internal static string? FormatSelfStaminaWhenLow(int? current, int? observedPeak)
    {
        if (current is not int cur || observedPeak is not int max || max <= 0) return null;
        var pct = Math.Clamp((float)cur / max, 0f, 1f);
        if (pct > StaminaLowFraction) return null;   // not meaningfully low -> no signal
        return $"- stamina: {pct.ToString("P0")} ({cur}/{max} observed) — stamina is LOW, which weakens your swings and stops you running; let it recover before an OPTIONAL fight (a `HOSTILE` attacker still takes priority).";
    }

    /// <summary>
    /// Render the raw self-health line for the prompt. Surfaces the
    /// wire-authoritative ABSOLUTE current HP (and observed peak) next to
    /// the fraction so the LLM is never misled by a "100%" computed from an
    /// under-estimated observed peak (e.g. logged in damaged at 1 HP). A
    /// `rising` note flags that current is still climbing (regen) and is
    /// therefore BELOW the true max. Returns null when no health is known.
    /// </summary>
    internal static string? FormatSelfHealth(
        int? current, int? observedPeak, float? fraction, bool? rising)
    {
        if (current is null && fraction is null) return null;
        var sb = new StringBuilder("- health: ");
        sb.Append(fraction is float f ? f.ToString("P0") : "unknown");
        if (current is int c)
        {
            sb.Append(" (");
            sb.Append(c);
            if (observedPeak is int p) { sb.Append('/'); sb.Append(p); }
            sb.Append(" HP");
            if (rising == true) sb.Append(", rising"); // still regenerating => below true max
            sb.Append(')');
        }
        return sb.ToString();
    }

    // The armed/UNARMED weapon-readiness line for `## Combat readiness`. Shared
    // by the body section and the protected-tail capsule so the two never
    // diverge. Mechanical wire-state rendering (what is wielded + whether the
    // missile weapon has ammo loaded); no advice, priority, or game knowledge.
    // `hasLoadableAmmo` says whether the bag holds ammo the wield path could load;
    // when a missile LAUNCHER is empty it decides whether to point at that ammo or
    // state plainly that none is loadable. `thrownWeapon` says the wielded missile
    // weapon is its own projectile (no AmmoType — server Player_Missile.cs throws the
    // weapon itself), so it is ARMED with no ammo needed. A launcher (has AmmoType)
    // with no loadable ammo cannot fire, so it carries the same UNARMED marker as no
    // weapon at all — the combat rules gate on that marker to stop pushing a doomed
    // Attack and steer the bot to arm or do non-combat progress instead.
    private static string WeaponReadinessLine(
        bool meleeWeaponWielded, bool missileWeaponWielded, bool ammoLoaded,
        bool hasLoadableAmmo, bool thrownWeapon)
    {
        if (meleeWeaponWielded)
            return "melee weapon wielded";
        if (missileWeaponWielded)
        {
            if (thrownWeapon)
                return "thrown weapon wielded (throws itself; no ammo needed)";
            var ammoState = ammoLoaded
                ? "loaded"
                : hasLoadableAmmo
                    ? "EMPTY (wield ammo to fire)"
                    : "EMPTY, no loadable ammo - UNARMED (cannot fire); unarmed melee with fists is available - obtain a weapon or ammo to improve effectiveness";
            return $"missile weapon wielded; missile ammo: {ammoState}";
        }
        // No weapon at all — the server allows a melee attack without any weapon
        // (unarmed skill). The bot can fight right now but should still seek a weapon
        // to improve effectiveness; the SELF-ARM affordances below name what to wield.
        return "NONE wielded - UNARMED (unarmed melee available; obtain a weapon to improve effectiveness)";
    }

    // Mirror the server's launcher/ammo precondition: a missile launcher and its
    // ammo may coexist only when their AmmoType (W_AMMO_TYPE) matches, and the
    // server does NOT reject when either side's AmmoType is unknown (null). So a
    // bag-ammo affordance is compatible unless BOTH types are known and differ.
    // Pure wire-value comparison; no names/wcids, no game knowledge.
    internal static bool AmmoTypeCompatible(ushort? launcherAmmoType, ushort? ammoType)
        => launcherAmmoType is not ushort lt || ammoType is not ushort at || lt == at;

    /// <summary>
    /// True when at least one of the bot's OWN bag items is loadable ammo for a
    /// wielded launcher: an item whose ValidLocations carries the missile-ammo
    /// SLOT bit (<see cref="ItemTypeMasks.MissileAmmoSlot"/>) and whose AmmoType is
    /// <see cref="AmmoTypeCompatible"/> with the launcher's. Mirrors the body's
    /// `bagAmmo` detection so the Motor's autonomous launcher-dequip never fires
    /// while the bot could instead LOAD ammo and keep the (more effective)
    /// launcher. Caller passes already-ownership-filtered bag items. Pure
    /// wire-value projection; no names/wcids, no game knowledge.
    /// </summary>
    internal static bool HasLoadableBagAmmoForLauncher(
        IEnumerable<(uint? ValidLocations, ushort? AmmoType)> ownedBagItems,
        ushort? launcherAmmoType)
        => ownedBagItems.Any(a =>
            a.ValidLocations is uint vl && (vl & ItemTypeMasks.MissileAmmoSlot) != 0 &&
            AmmoTypeCompatible(launcherAmmoType, a.AmmoType));

    // True when the bot has NO usable weapon anywhere: nothing combat-capable wielded
    // (IsCombatCapable) and nothing in the bag it could wield to attack — no melee
    // weapon, no thrown weapon (missile bit + null AmmoType), and no launcher with
    // compatible loadable bag ammo. In this state dropping a Wield can never lose a
    // usable weapon. Pure wire-state/loadout arithmetic; no game knowledge.
    private static bool HasNoUsableWeaponAnywhere(WorldStateProjection world)
    {
        if (IsCombatCapable(world.Inventory)) return false; // a usable weapon is wielded
        foreach (var i in world.Inventory)
        {
            if (i.WieldedAt is uint w && w != 0) continue; // wielded handled by IsCombatCapable
            if (i.ItemType is not uint it) continue;
            if ((it & ItemTypeMasks.MeleeWeapon) != 0) return false; // bag melee weapon
            if ((it & ItemTypeMasks.MissileWeapon) != 0)
            {
                if (i.AmmoType is null) return false; // bag thrown weapon (its own projectile)
                var bagAmmo = world.Inventory
                    .Where(a => (a.WieldedAt is not uint aw || aw == 0)
                                && a.ValidLocations is uint vl && (vl & ItemTypeMasks.MissileAmmoSlot) != 0
                                && AmmoTypeCompatible(i.AmmoType, a.AmmoType))
                    .Select(a => (a.ValidLocations, a.AmmoType));
                if (HasLoadableBagAmmoForLauncher(bagAmmo, i.AmmoType)) return false; // launcher+ammo
            }
        }
        return true;
    }

    /// <summary>
    /// True when a Wield goal targets a bag missile LAUNCHER that has no
    /// loadable ammo in the bag — so wielding it would be immediately
    /// reversed by the Motor's cp060 dequip, producing an infinite
    /// dequip/re-wield loop. A THROWN weapon (AmmoType null) is its own
    /// projectile and returns false. A launcher WITH compatible bag ammo
    /// returns false (the bot should wield it). Non-Wield goals always
    /// return false. Pure wire-state projection; no names/wcids, no
    /// game knowledge — the check is fully mechanical loadout arithmetic.
    /// </summary>
    internal static bool IsWieldOfUnusableLauncher(Goal goal, WorldStateProjection world)
    {
        if (goal.Kind != GoalKind.Wield) return false;
        // Provable-harmlessness gate: only intervene when the bot has NO usable weapon
        // anywhere (nothing combat-capable wielded, and no bag melee/thrown weapon or
        // launcher+compatible-ammo it could wield). In that genuinely-weaponless state —
        // the cp060 unarmed loop — dropping a Wield can never lose a usable weapon, so
        // the loop-break is harmless REGARDLESS of how the Motor resolves the selector
        // (over stale/off-screen objects the Strategy projection cannot see). When a
        // usable weapon IS available, the guard defers entirely and the prompt note +
        // self-arm affordances steer the LLM to that weapon instead.
        if (!HasNoUsableWeaponAnywhere(world)) return false;
        // The Motor's wield executor resolves goal.Item first and falls back to goal.Target
        // ONLY when Item resolves to no owned item (HandshakeDriver: itemSnap ?? targetSnap,
        // each filtered to ContainerGuid==self). The Strategy projection cannot reproduce
        // the executor's full-object-set resolution order, so this guard acts only on an
        // UNAMBIGUOUS single owned bag match and DEFERS on any ambiguity (>1 bag match, or a
        // visible world object the selector could also resolve) — it never drops a Wield the
        // Motor might resolve to a different object. An ambiguous Item does NOT fall through
        // to Target: the executor would still wield one of the Item matches, so retargeting
        // here would be wrong.
        var (item, itemAmbiguous) = ResolveBagWieldCandidate(goal.Item, world);
        if (itemAmbiguous) return false;
        var wielded = item;
        if (wielded is null)
        {
            var (target, targetAmbiguous) = ResolveBagWieldCandidate(goal.Target, world);
            if (targetAmbiguous) return false;
            wielded = target;
        }
        if (wielded is null) return false;
        // Useless iff a missile LAUNCHER (MissileWeapon bit + a non-null AmmoType — a
        // THROWN weapon has null AmmoType and is usable) with NO loadable bag ammo.
        if (!(wielded.ItemType is uint it && (it & ItemTypeMasks.MissileWeapon) != 0)
            || wielded.AmmoType is null)
            return false;
        var bagAmmoItems = world.Inventory
            .Where(i => i.WieldedAt is not uint bw || bw == 0)
            .Select(i => (i.ValidLocations, i.AmmoType));
        return !HasLoadableBagAmmoForLauncher(bagAmmoItems, wielded.AmmoType);
    }

    // Resolves a wield selector against the bot's own un-wielded bag, returning the single
    // match (or null) plus an Ambiguous flag. Ambiguous = the selector matches >1 bag item
    // OR a visible world object the executor's full-object resolver could also pick — in
    // both cases the projection cannot decide which item the Motor wields, so the guard
    // must defer. The visible check (VisibleCouldMatchWieldSelector) mirrors the resolver's
    // evaluable fields (guid/name/name_contains/wcid/item_type_mask). short_desc_contains is
    // not projected on visible objects; an all-short_desc selector is matched on the bag via
    // InventoryMatchesSelector, and a same-name visible collision is caught by the name
    // check (AC item names correlate with item type, so a same-name/different-type visible
    // object — the only residual short_desc ambiguity — is not realizable).
    private static (InventoryItemProjection? Item, bool Ambiguous) ResolveBagWieldCandidate(
        Selector? sel, WorldStateProjection world)
    {
        if (sel is null || sel.IsEmpty) return (null, false);
        if (world.Visible.Any(v => VisibleCouldMatchWieldSelector(sel, v))) return (null, true);
        var bagMatches = world.Inventory
            .Where(i => (i.WieldedAt is not uint w || w == 0) && InventoryMatchesSelector(sel, i))
            .Take(2)
            .ToList();
        if (bagMatches.Count > 1) return (null, true);
        return (bagMatches.Count == 1 ? bagMatches[0] : null, false);
    }

    // Visible-object counterpart of InventoryMatchesSelector for the wield ambiguity check:
    // honors the SelectorResolver fields evaluable on a VisibleObjectProjection
    // (guid/name/name_contains/wcid/item_type_mask). Requires at least one such field so a
    // bare/short_desc-only selector never matches-all here.
    private static bool VisibleCouldMatchWieldSelector(Selector sel, VisibleObjectProjection v)
    {
        var hasIdentity = sel.Guid is not null
            || !string.IsNullOrEmpty(sel.Name)
            || !string.IsNullOrEmpty(sel.NameContains)
            || sel.Wcid is not null
            || sel.ItemTypeMask is not null;
        if (!hasIdentity) return false;
        if (sel.Guid is uint g && v.Guid != g) return false;
        if (!string.IsNullOrEmpty(sel.Name)
            && !string.Equals(v.Name, sel.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(sel.NameContains)
            && (v.Name is null || !v.Name.Contains(sel.NameContains, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (sel.Wcid is uint w && !(v.Wcid is uint vw && vw == w)) return false;
        if (sel.ItemTypeMask is uint m && !(v.ItemType is uint it && (it & m) != 0)) return false;
        return true;
    }

    /// <summary>
    /// Advisory FACT for `## Combat readiness` when the bot is wielding a melee
    /// weapon whose governing skill it has NOT trained while a melee weapon
    /// matching a skill it HAS trained sits in the bag — so the LLM can swap to
    /// a weapon it actually hits with (the trained weapon skill is the main
    /// melee-accuracy driver). Returns null when the wielded weapon already uses
    /// a trained skill, no governing skill is known, or no trained-skill melee
    /// weapon is available. Pure projection: each weapon's GoverningSkill is
    /// decoded weenie data compared against the bot's OWN trained skills; source
    /// names no specific weapon/skill and makes no choice — the LLM decides
    /// whether to swap.
    /// </summary>
    internal static string? WeaponSkillSwapAdvisory(
        WorldStateProjection world, HashSet<uint>? refusedGuids = null)
    {
        var trained = world.Self.TrainedSkills;
        if (trained is null || trained.Count == 0) return null;

        bool IsTrained(string? skill) => !string.IsNullOrEmpty(skill) &&
            trained.Any(t => string.Equals(t.Name, skill, StringComparison.OrdinalIgnoreCase));

        var wielded = world.Inventory.FirstOrDefault(i =>
            i.WieldedAt is uint w && w != 0 &&
            i.ItemType is uint it && (it & ItemTypeMasks.MeleeWeapon) != 0);
        if (wielded?.GoverningSkill is not string wieldedSkill) return null;
        if (IsTrained(wieldedSkill)) return null; // already on a trained-skill weapon

        // Skip a trained bag weapon the server recently REFUSED to equip: telling
        // the bot to "Wield it" while `## Recently refused items` says the equip
        // was refused would be a same-prompt contradiction (and it would just be
        // dropped). Mirrors the self-arm suggestion filter (recentlyServerRefused).
        var trainedBagWeapon = world.Inventory.FirstOrDefault(i =>
            (i.WieldedAt is not uint bw || bw == 0) &&
            i.ItemType is uint it && (it & ItemTypeMasks.MeleeWeapon) != 0 &&
            IsTrained(i.GoverningSkill) &&
            (refusedGuids is null || !refusedGuids.Contains(i.Guid)));
        if (trainedBagWeapon?.GoverningSkill is not string bagSkill) return null;

        return $"weapon skill MISMATCH: your wielded {wielded.Name} uses the {wieldedSkill} skill, " +
               $"which is NOT one of your trained skills — an UNTRAINED weapon skill lands far fewer " +
               $"hits; a weapon governed by a skill you HAVE trained is in your bag: " +
               $"{trainedBagWeapon.Name} ({bagSkill}). Wield it to hit far more often.";
    }

    /// <summary>
    /// The bot's currently-wielded main-weapon (a melee or missile weapon in a
    /// main-weapon slot), or null when NO main-weapon is wielded (the bot is
    /// fighting unarmed). Pure projection over the inventory wire facts; shared so
    /// the wielded-vs-unarmed accuracy branch is judged from one predicate.
    /// </summary>
    internal static InventoryItemProjection? WieldedMainWeapon(WorldStateProjection world) =>
        world.Inventory.FirstOrDefault(i =>
            i.WieldedAt is uint w && (w & WeaponSwap.MainWeaponSlotMask) != 0 &&
            i.ItemType is uint it &&
            ((it & ItemTypeMasks.MeleeWeapon) != 0 || (it & ItemTypeMasks.MissileWeapon) != 0));

    /// <summary>
    /// When the bot's WIELDED main-weapon is governed by a skill it has NOT
    /// trained, returns a signal for the SPEND-XP accuracy advice: the skill's
    /// NAME when the governing skill is KNOWN-and-untrained, or "" (empty) when a
    /// weapon IS wielded but its governing skill is UNKNOWN (null) — in BOTH cases
    /// raising a trained weapon skill cannot be confirmed to help THIS weapon, so
    /// the accuracy lever is coordination. Returns null when the wielded weapon's
    /// skill IS trained, no main-weapon is wielded, or the trained-skill list is
    /// unknown. Considers BOTH melee and missile main-weapons (a thrown weapon is a
    /// missile weapon). Pure projection: decoded weenie GoverningSkill compared
    /// against the bot's OWN trained skills; names no specific weapon/skill and
    /// makes no choice — the LLM decides what to raise.
    /// </summary>
    internal static string? WieldedWeaponUntrainedSkillName(WorldStateProjection world)
    {
        var trained = world.Self.TrainedSkills;
        if (trained is null || trained.Count == 0) return null;
        var wielded = WieldedMainWeapon(world);
        if (wielded is null) return null;             // no main-weapon wielded
        var gs = wielded.GoverningSkill;
        if (string.IsNullOrEmpty(gs)) return "";      // wielded, but skill UNKNOWN
        return trained.Any(t => string.Equals(t.Name, gs, StringComparison.OrdinalIgnoreCase))
            ? null                                    // wielded skill IS trained
            : gs;                                     // wielded skill KNOWN-untrained
    }

    /// <summary>
    /// Combat-readiness accuracy/offense advisory FACT. TWO cases:
    /// (1) the bot is WIELDING a weapon governed by a skill it has NOT trained (or
    /// whose skill is unknown) — raising a TRAINED weapon skill does nothing for THIS
    /// weapon, so COORDINATION is the accuracy lever; (2) the bot has NO main-weapon
    /// wielded AND no usable weapon available anywhere (the stuck-unarmed state) — its
    /// swings are fists, whose UnarmedCombat to-hit is half Strength + half Coordination
    /// and whose DAMAGE is Strength-based, so STRENGTH is the better lever (it raises both
    /// accuracy AND damage; coordination raises accuracy only). Not swing-gated, so it
    /// covers the pre-combat case and the dense-scene body cut (re-surfaced in the protected
    /// tail). Returns null when neither case applies: a main-weapon IS wielded but
    /// its skill is TRAINED (or, for Case 1 only, the trained-skill list is unknown,
    /// which suppresses the wielded note), OR no weapon is wielded but a usable
    /// weapon IS available to wield/buy (so it never contradicts a "wield/buy the
    /// available weapon" affordance). Pure projection; the LLM still decides what to
    /// raise.
    /// </summary>
    internal static string? WieldedWeaponUntrainedAccuracyNote(WorldStateProjection world)
    {
        if (WieldedWeaponUntrainedSkillName(world) is string s)
        {
            var which = s.Length > 0 ? $"its skill ({s})" : "its skill";
            return $"wielded-weapon accuracy: {which} is NOT one of your `trained skills`, so raising a " +
                   "TRAINED weapon skill will NOT improve THIS weapon's hit rate — raise COORDINATION for " +
                   "accuracy (or arm a weapon governed by a skill you HAVE trained for a real upgrade).";
        }
        // Case 2: genuinely fighting unarmed (no main-weapon wielded AND no usable
        // weapon anywhere — the cp060/cp061/cp062 stuck state). Unarmed swing TO-HIT is the
        // UnarmedCombat skill (half Strength + half Coordination) and unarmed DAMAGE is
        // Strength-based, so STRENGTH is the better unarmed lever — it raises both accuracy
        // and damage, while Coordination raises accuracy only. Surface it so a weaponless bot
        // with high coordination but low strength raises STRENGTH (the binding limit on its
        // fist damage) rather than pouring more into accuracy it already has. Gated tight on
        // HasNoUsableWeaponAnywhere so it never fires when a weapon is available to wield/buy.
        if (WieldedMainWeapon(world) is null && HasNoUsableWeaponAnywhere(world))
            return "unarmed accuracy: with no weapon wielded your swings are fists — `UnarmedCombat` to-hit is " +
                   "half STRENGTH + half COORDINATION and unarmed DAMAGE is STRENGTH-based, so favor STRENGTH (it " +
                   "raises BOTH accuracy and damage; COORDINATION raises accuracy only), or arm a usable weapon " +
                   "and train its skill for a real upgrade.";
        return null;
    }

    // Compact, decision-relevant WIELD annotation for an inventory row, derived
    // PURELY from the item's own wire facts: for a WEAPON, its governing-skill name
    // (GoverningSkill, decoded from the weenie's PropertyInt.WeaponSkill) checked
    // against the bot's OWN trained-skill names. Live (cp027-validate.log): a melee
    // char burned >50% of one run's LLM budget re-Wielding missile weapons it has no
    // skill for, because the inventory listed no weapon skill so the model guessed.
    // Surfacing the wire fact lets the LLM stop choosing a Wield the server will not
    // actuate / a weapon it cannot use well. Scoped to weapons only (GoverningSkill
    // != null) to stay sparse: the vast majority of bag items are never wield
    // candidates, so blanket-tagging them would only bloat this trim-first section.
    // No game knowledge (no item/skill list, no priority, no target choice): returns
    // an empty string for an already-wielded item or any non-weapon.
    internal static string WieldAnnotation(
        InventoryItemProjection item, HashSet<string>? trainedSkillNames)
    {
        // Already wielded — the `wielded@` marker already conveys the state.
        if (item.WieldedAt is uint w && w != 0) return "";
        // A weapon carries a governing skill; surface it + whether it is trained (an
        // untrained weapon skill lands far fewer hits — the same mechanical fact the
        // weapon-skill swap advisory and the SPEND XP rule already state).
        if (item.GoverningSkill is string gs && gs.Length > 0)
        {
            // Judge trained-vs-untrained ONLY when the trained-skill list is KNOWN
            // and non-empty. A null/empty set means skills have not loaded yet (per
            // WorldStateProjection) — surface the skill name but make NO trained
            // claim, so a weapon is never falsely branded UNTRAINED in that window
            // (mirrors WeaponSkillSwapAdvisory, which makes no claim when unknown).
            if (trainedSkillNames is not { Count: > 0 }) return $" [weapon skill {gs}]";
            return trainedSkillNames.Contains(gs)
                ? $" [weapon skill {gs}: trained]"
                : $" [weapon skill {gs}: UNTRAINED — far fewer hits]";
        }
        return "";
    }

    // Append the decision-critical, COMPACT self facts — attributes, raisable
    // (trained/specialized) skills, health, and death count — in their stable
    // line format. Shared by the body `## Self` section and the protected-tail
    // `## Self` capsule so the two never diverge. Raw projection facts only; no
    // advice, priority, or game knowledge. Appends nothing when none are known.
    private static void AppendSelfCoreFacts(
        StringBuilder sb, WorldStateProjection world, int? secondsSinceLastDeath)
    {
        if (world.Self.Attributes is { Count: > 0 } selfAttrs)
            // RAW base attribute values (unbuffed), seeded at login and kept
            // live by discrete PrivateUpdateAttribute (0x02E3) after a raise.
            sb.AppendLine($"- attributes: {string.Join(", ", selfAttrs.Select(a => $"{a.Name} {a.Base}"))}");
        if (world.Self.TrainedSkills is { Count: > 0 } selfSkills)
            // RAW list of the skills the character actually has (wire
            // AdvancementClass Trained/Specialized) — the only valid RaiseSkill
            // targets. Seeded at login, kept live by discrete PrivateUpdateSkill
            // (0x02DD) after a raise.
            sb.AppendLine(
                "- trained skills (valid RaiseSkill targets): " +
                string.Join(", ", selfSkills.Select(s =>
                    $"{s.Name} ({s.Advancement}, raised {s.RaisedRanks})")));
        if (FormatSelfHealth(world.Self.HealthCurrent, world.Self.HealthObservedPeak, world.Self.HealthFraction, world.Self.HealthRising) is string selfHealthLine)
            sb.AppendLine(selfHealthLine);
        if (world.Self.NumDeaths is int nd)
        {
            // Cumulative total + (when known) how long ago the most recent
            // in-session death was, so the LLM can tell a fresh respawn from an
            // old count. Raw telemetry — no urgency/recommendation baked in.
            var recency = secondsSinceLastDeath is int ds
                ? $" (most recent observed ~{ds}s ago)"
                : "";
            sb.AppendLine($"- deaths (server-tracked): {nd}{recency}");
        }
    }

    /// <summary>
    /// Render the raw threat-count line for `## Combat readiness`: how many
    /// monsters are in view and how many of those are actively hostile
    /// (already attacking). RAW counts only — no danger label, no advice.
    /// The COMBAT SAFETY rule tells the LLM to pull clustered or
    /// multiple-hostile mobs singly; this is the perception signal that rule
    /// acts on, so it doesn't have to count rows in `## Visible nearby`
    /// (which interleaves monsters with NPCs/objects). Corpses are excluded
    /// by the caller. Returns null when no monsters are in view (the caller
    /// already emits a "nearest monster: (none in view)" line).
    /// </summary>
    internal static string? FormatThreatSummary(int monstersInView, int hostilesInView)
    {
        if (monstersInView <= 0) return null;
        var hostilePart = hostilesInView > 0
            ? $"{hostilesInView} actively HOSTILE (attacking you now)"
            : "0 attacking you now";
        return $"- monsters in view: {monstersInView} ({hostilePart})";
    }

    /// <summary>
    /// Render the raw "recent inbound damage" line for `## Combat readiness`:
    /// how many hits the bot has TAKEN and the total damage within the rolling
    /// window. RAW facts only — no danger label, no advice; the COMBAT SAFETY
    /// rule and the LLM own the fight-vs-flee/Recall interpretation. Returns
    /// null when there is no recent inbound damage to report (so the line is
    /// omitted and costs nothing when the bot is not under attack).
    /// </summary>
    internal static string? FormatRecentInboundDamage(RecentInboundDamage? damage)
    {
        if (damage is not { } d || d.Hits <= 0) return null;
        var window = d.WindowSeconds.ToString("0.#");
        var hitWord = d.Hits == 1 ? "hit" : "hits";
        return $"- recent inbound damage: {d.Hits} {hitWord} taking {d.TotalDamage} " +
               $"damage in the last ~{window}s";
    }

    internal static void AppendRecentSightings(
        StringBuilder sb,
        IReadOnlyList<SightedRecallProjection>? sightings,
        WorldStateProjection world)
    {
        if (sightings is null || sightings.Count == 0) return;

        // Identity of currently-visible creatures, so we never re-advertise
        // something the LLM can already see live.
        var visibleWcids = new HashSet<uint>();
        var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in world.Visible)
        {
            if (v.Wcid is uint vw) visibleWcids.Add(vw);
            if (!string.IsNullOrEmpty(v.Name)) visibleNames.Add(v.Name);
        }

        bool CurrentlyVisible(SightedRecallProjection s)
            => (s.Wcid is uint sw && visibleWcids.Contains(sw))
               || visibleNames.Contains(s.Name);

        // Self position must be lifted into the SAME absolute world frame as
        // the stored sightings (which NavGraph keeps in absolute coords).
        // world.Self.Position* is landblock-LOCAL (0..192), so convert via
        // the cell's landblock origin. Without this the distance/bearing are
        // computed against the world origin, not the bot (live bug: a monster
        // ~20m away rendered as "~47525m").
        var selfLb = world.Self.Landblock;
        (float X, float Y)? selfGlobal = world.Self.CellId is uint selfCell
            ? AcCoords.ToGlobalXY(selfCell, world.Self.PositionX, world.Self.PositionY)
            : null;

        // Render one bounded block PER KIND so an NPC-dense town can never
        // starve the monster recall (and vice-versa) — the reason the recall
        // was Mob-only before. Monsters and NPCs each get their own header and
        // row/char cap. Surfacing NPCs lets the LLM, when SEEKING A KILL-TASK
        // with no quest in hand (see the SEEK A KILL-TASK rule), steer back
        // toward a remembered NPC cluster using the SAME bearing/distance the
        // monster recall already provides. Pure perception — the bot's own
        // out-of-view sighting memory; no priority, no hardcoded NPC/location.
        void RenderBlock(EntityKind kind, string header, string description)
        {
            var candidates = sightings
                .Where(s => s.Kind == kind)
                .Where(s => s.AgeSeconds <= RecentSightingTtlSeconds)
                .Where(s => !CurrentlyVisible(s))
                .GroupBy(s => (Name: s.Name.ToLowerInvariant(), s.Wcid, s.Landblock))
                .Select(g => g.OrderBy(s => s.AgeSeconds).First())
                .OrderBy(s => s.AgeSeconds)
                .ToList();
            if (candidates.Count == 0) return;

            sb.AppendLine(header);
            sb.AppendLine(description);

            int rows = 0;
            int chars = 0;
            foreach (var s in candidates)
            {
                if (rows >= RecentSightingMaxRows) break;
                var row = RenderRecentSightingRow(s, selfLb, selfGlobal);
                int cost = row.Length + 1; // newline AppendLine adds
                if (rows > 0 && chars + cost > RecentSightingCharBudget) break;
                sb.AppendLine(row);
                chars += cost;
                rows++;
            }
            if (rows < candidates.Count)
                sb.AppendLine($"- (+{candidates.Count - rows} more remembered, not shown)");
            sb.AppendLine();
        }

        RenderBlock(
            EntityKind.Mob,
            "## Recently sighted (out of view)",
            "Monsters you have seen that are NOT currently in view, from your own " +
            "memory. Not recommendations — the bot assigns no priority. To return " +
            "to one, target it by name; the bot will navigate to where it was last seen.");
        RenderBlock(
            EntityKind.NPC,
            "## Recently sighted NPCs (out of view)",
            "NPCs you have seen that are NOT currently in view, from your own memory. " +
            "Not recommendations — the bot assigns no priority. To return to one (for " +
            "example to Talk it and check whether it offers a task, or — if a row is " +
            "marked 'vendor' and you have no weapon to wield or buy in view — to buy " +
            "arms there), target it by name or Explore toward its bearing; the bot will " +
            "navigate to where it was last seen.");
        RenderBlock(
            EntityKind.Portal,
            "## Recently sighted portals (out of view)",
            "Portals / area transitions you have seen that are NOT currently in view, " +
            "from your own memory. Not recommendations — the bot assigns no priority. " +
            "To return to one, `Use` it by name (or Explore toward its bearing); the bot " +
            "will navigate to where it was last seen.");
    }

    private static string RenderRecentSightingRow(
        SightedRecallProjection s, uint? selfLb, (float X, float Y)? selfGlobal)
    {
        var age = $"last seen {s.AgeSeconds:F0}s ago";
        string where;
        if (selfGlobal is (float sx, float sy))
        {
            var dx = s.WorldX - sx;
            var dy = s.WorldY - sy;
            var dist = MathF.Sqrt(dx * dx + dy * dy);
            where = $"approx {Compass8(dx, dy)} ~{dist:F0}m";
        }
        else
        {
            where = $"at ~({s.WorldX:F0},{s.WorldY:F0})";
        }
        // Show the landblock only when it differs from the bot's current
        // one (a cross-landblock recall the LLM may want to travel to).
        var lb = (selfLb is uint slb && s.Landblock != slb)
            ? $", landblock 0x{s.Landblock:X4}"
            : "";
        var kindLabel = s.Kind switch
        {
            EntityKind.NPC => "npc",
            EntityKind.Portal => "portal",
            _ => "monster",
        };
        var vendorTag = s.IsVendor ? ", vendor (sells goods)" : "";
        return $"- {s.Name} (kind={kindLabel}{vendorTag}, {age}, {where}{lb})";
    }

    // 8-point compass bearing from a world-space (dx,dy) delta. +Y is
    // north, +X is east in this projection's world frame.
    private static string Compass8(float dx, float dy)
    {
        if (MathF.Abs(dx) < 0.01f && MathF.Abs(dy) < 0.01f) return "here";
        var ang = MathF.Atan2(dx, dy) * (180f / MathF.PI); // 0 = N, 90 = E
        if (ang < 0) ang += 360f;
        string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        int idx = (int)MathF.Round(ang / 45f) % 8;
        return dirs[idx];
    }

    internal static void AppendVisibleNearby(
        StringBuilder sb,
        IReadOnlyList<VisibleObjectProjection> visible,
        IReadOnlyList<CombatHistoryEntry>? combatHistory = null,
        IReadOnlySet<uint>? openedCorpseGuids = null)
    {
        if (visible.Count == 0) { sb.AppendLine("- (nothing)"); return; }

        // Neutral nearest-first inclusion. Rows survive truncation by DISTANCE
        // only — the source does not privilege any in-game object TYPE for
        // prompt real estate (that would be a type-priority; the LLM decides
        // what matters). Distance ordering matches the cp-2316 prompt-ceiling
        // trim, which also sheds the far tail of nearest-first.
        var ordered = visible
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .ToList();

        // Rows must fit within the budget minus the summary headroom so the
        // closing omission-summary line never pushes the section over budget.
        int rowBudget = VisibleSectionCharBudget - VisibleSummaryReserve;
        int chars = 0;
        int shown = 0;
        foreach (var v in ordered)
        {
            if (shown >= VisibleRowSoftCap) break;
            var row = ClampRow(RenderVisibleRow(v, combatHistory, openedCorpseGuids));
            int cost = row.Length + 1; // include the newline AppendLine adds
            // Always render at least the nearest row (clamped); otherwise stop
            // once the next row would exceed the row budget so the section
            // stays bounded even when objects are numerous.
            if (shown > 0 && chars + cost > rowBudget) break;
            sb.AppendLine(row);
            chars += cost;
            shown++;
        }
        if (shown < ordered.Count)
        {
            var omitted = ordered.Skip(shown).ToList();
            // Factual, type-neutral omission telemetry: a raw count plus the
            // decoded-flag breakdown of what was dropped (projection facts, not
            // a priority). Flag counts are NOT a partition — an object may carry
            // several flags (e.g. chest+openable) so the breakdown can exceed N;
            // the wording says so. The LLM can weigh whether to move closer to
            // reveal an omitted object.
            sb.AppendLine($"- (+{omitted.Count} more distant objects not shown due to prompt budget; flag counts among them, an object may match several: {SummarizeOmittedTags(omitted)})");
        }
    }

    /// <summary>
    /// Strip a Markdown code fence wrapper from an LLM response, if present, so
    /// the JSON inside can be parsed. Some chat models (observed: deepseek-v3)
    /// return their JSON goal wrapped as <c>```json\n{...}\n```</c> despite the
    /// prompt asking for raw JSON, which makes the response start with a
    /// backtick and fail JSON parsing at byte 0. This is response sanitisation
    /// (request/response handling, the cp-2391 class), NOT game knowledge.
    ///
    /// ONLY activates when the trimmed content starts with a fence (```), so a
    /// raw-JSON response from every other model is returned byte-for-byte
    /// unchanged (zero behaviour change off the fenced path). Handles a fence
    /// on its own line (with or without a language tag like <c>json</c>) and a
    /// single-line <c>```{...}```</c>. The language-tag guard never consumes
    /// JSON (it bails if the first line contains a brace). Idempotent: a second
    /// call on already-stripped content returns it unchanged.
    /// </summary>
    internal static string StripJsonCodeFence(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content ?? string.Empty;
        var s = content.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal))
            return content; // not fenced — return the ORIGINAL unchanged

        var inner = s.Substring(3); // drop the opening ```
        var firstNl = inner.IndexOf('\n');
        if (firstNl >= 0)
        {
            // Text before the first newline is an optional language tag (e.g.
            // "json"). Only drop it as a tag if it carries no JSON braces, so a
            // single-line fenced object is never truncated here.
            var tag = inner.Substring(0, firstNl);
            if (!tag.Contains('{') && !tag.Contains('}'))
                inner = inner.Substring(firstNl + 1);
        }
        else
        {
            // Single-line fence: drop a leading inline language tag (letters).
            var i = 0;
            while (i < inner.Length && char.IsLetter(inner[i])) i++;
            inner = inner.Substring(i);
        }

        inner = inner.TrimEnd();
        if (inner.EndsWith("```", StringComparison.Ordinal))
            inner = inner.Substring(0, inner.Length - 3);

        return inner.Trim();
    }

    internal static bool TryParseGoal(string json, out Goal? goal, out string? error)
    {
        goal = null; error = null;
        try
        {
            json = StripJsonCodeFence(json);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var parsed = JsonSerializer.Deserialize<Goal>(json, opts);
            if (parsed is null) { error = "deserialized to null"; return false; }
            // An explicit `"target": null` in the LLM JSON deserializes the
            // non-nullable Goal.Target to null. Normalize it back to an empty
            // (non-null) Selector so the emptiness checks below and every
            // downstream `goal.Target.*` consumer stay NPE-safe.
            if (parsed.Target is null)
            {
                parsed = parsed with { Target = new Selector() };
            }
            if (parsed.Kind != GoalKind.Recall && parsed.Target.IsEmpty)
            {
                // Wield's wielded object is logically the `item`, not the
                // `target`: the Motor's Wield dispatch reads goal.Item (or an
                // in-bag target) and already tolerates an empty target. The
                // prompt schema lists both fields but never directs the LLM to
                // set target=self for Wield, so the model legitimately emits the
                // weapon in `item` with target=null. Accept an item-only Wield
                // instead of discarding the LLM's decision to the heuristic
                // fallback. All other verbs still require a target.
                bool wieldHasItem =
                    parsed.Kind == GoalKind.Wield &&
                    parsed.Item is not null && !parsed.Item.IsEmpty;
                // Self-Use (read / activate / "double-click" an inventory item ON
                // yourself) is, like Wield, logically an ITEM action with no world
                // target: the item acts on the user. The prompt directs the model to
                // emit the item in `item` with no target for these, so accept an
                // item-only Use instead of discarding the LLM's decision to the
                // heuristic fallback. The Motor's self-Use dispatch sends the
                // GameActionUse at the item. All other verbs still require a target.
                bool useHasItem =
                    parsed.Kind == GoalKind.Use &&
                    parsed.Item is not null && !parsed.Item.IsEmpty;
                if (!wieldHasItem && !useHasItem)
                {
                    error = "target selector missing or empty";
                    return false;
                }
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
            json = StripJsonCodeFence(json);
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
- COMPLETION PREDICATES: the completion object's discriminator field
  is `type` (e.g. `{"type":"num_deaths_at_least","count":3}`). Prefer
  server-authoritative types (num_deaths, coin_value) when applicable.
  Use *_total_* for absolute thresholds, *_since_push_* for deltas. If
  none fits, use `{"type":"always_false"}` + populate
  `predicate_request` with the predicate type we should add.

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
