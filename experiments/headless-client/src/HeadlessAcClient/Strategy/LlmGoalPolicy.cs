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
    }

    // Fire on the Nth bare-Use emission against the SAME world-object identity
    // within one landblock episode (across intervening moves). 3 mirrors the
    // StationaryUseRepeatThreshold / MultiNpcTalkChurnStaleThreshold: the LLM
    // has chosen the same object three times without the landblock changing or
    // inventory advancing — strong loop evidence.
    private const int LandblockWorldUseChurnThreshold = 3;

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
        public readonly HashSet<string> SeenDialogFingerprints = new(StringComparer.Ordinal);
    }

    private const int RovingNpcTalkLoopStaleThreshold = 4;

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
        => ApplyHuntEgressOverride(ProposeGoalCore(world, events, currentGoal), world, events);

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
        // (still in the same landblock, no hostile, no fresh server directive),
        // keep substituting the social dwell-extending verbs the LLM loops on
        // (Talk/Give) with Explore so the bot actually walks OUT instead of the
        // picker re-parking it on the dead NPC every tick. The latch self-clears
        // on a landblock change (loop broken — bot left), a hostile appearing, a
        // fresh directive, or timeout. Non-social verbs (the bot's own Explore/
        // Pickup/Attack) pass through and themselves break the loop.
        if (IsTalkLoopEgressActive(
                nowUtc, _talkLoopEgressUntilUtc, _talkLoopEgressLandblock, lb,
                AnyHostileInView(world), RecentFreshDirective(events, nowUtc)))
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
    // gained) and faces no active attacker has nothing left to gain in this
    // zone, so a proven no-progress interaction loop should send it away rather
    // than to the fallback (which re-picks the same dead-end class of object).
    // Extracted for deterministic unit testing. Own signals only — no game
    // content.
    internal static bool ShouldEscapeStuckLoop(
        bool combatReady, bool tappedOut, bool hostileInView)
        => combatReady && tappedOut && !hostileInView;

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
    //   - no ObservedHostile attacker in view — defend/flee a real fight, never
    //     turn away from it.
    // Own signals only (typed wield, own dwell/level, own ObservedHostile
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
        var hostileInView = world.Visible.Any(
            v => v.IsMonster && !v.IsCorpse && v.ObservedHostile);
        return ShouldEscapeStuckLoop(combatReady, tappedOut, hostileInView);
    }

    // A fixation guard fired (proven no-progress interaction loop). Either send
    // a tapped-out combat-ready bot away with Explore (stuck-loop egress) or, if
    // not in that state, defer to the fallback as before.
    private Goal? EscapeOrFallback(
        WorldStateProjection world, EventStream events, Goal? currentGoal,
        DateTimeOffset nowUtc, string loopKind)
    {
        if (ShouldEscapeStuckLoopWithExplore(world, nowUtc))
        {
            Console.WriteLine(
                "[llm-override] stuck-loop egress: tapped-out combat-ready bot looping " +
                $"{loopKind} with no progress and no hostile attacker in view — " +
                "substituting Explore{anywhere} to leave the zone.");
            return MakeEgressExploreGoal(
                nowUtc, "override:stuck-loop-egress",
                $"mechanical stuck-loop egress: tapped-out, looping {loopKind} with no " +
                "progress; leaving to find monsters (enforces the LOOP-BREAK rules)");
        }
        // Early Talk-loop egress: a PROVEN stationary NPC Talk fixation is a
        // dead end regardless of dwell time, so break it now (before the 5-min
        // tapped-out gate the general egress needs) unless a hostile is in view
        // (defend/flee that) or the server is actively guiding the bot. Latch it
        // briefly so the picker cannot re-park on the same dead NPC next tick.
        if (ShouldEarlyEscapeTalkLoop(
                loopKind, AnyHostileInView(world), RecentFreshDirective(events, nowUtc)))
        {
            _talkLoopEgressUntilUtc = nowUtc + TalkLoopEgressDuration;
            _talkLoopEgressLandblock = world.Self.Landblock;
            Console.WriteLine(
                "[llm-override] talk-loop egress: proven stationary NPC Talk fixation, " +
                "no hostile in view — substituting Explore{anywhere} " +
                $"(latched {TalkLoopEgressDuration.TotalSeconds:F0}s) to break the loop.");
            return MakeEgressExploreGoal(
                nowUtc, "override:talk-loop-egress",
                "mechanical talk-loop egress: proven stationary NPC Talk fixation with no " +
                "hostile in view; leaving to break the dead-end conversation loop");
        }
        return _fallback.ProposeGoal(world, events, currentGoal);
    }

    // True iff the bot has any non-corpse monster in view that is actively
    // hostile (attacking it). Own-perception wire flags only — no game content.
    private static bool AnyHostileInView(WorldStateProjection world)
        => world.Visible.Any(v => v.IsMonster && !v.IsCorpse && v.ObservedHostile);

    // Pure decision: a freshly PROVEN stationary NPC Talk fixation should break
    // the loop immediately (early egress) when no hostile is in view and the
    // server is not actively guiding the bot with a fresh directive. Scoped to
    // the Talk loop kind ONLY — a world-object Use loop may be a genuine
    // early-zone progress attempt, so it keeps the dwell-gated path. Extracted
    // for deterministic unit testing; own-signal only, no game content.
    internal static bool ShouldEarlyEscapeTalkLoop(
        string loopKind, bool hostileInView, bool freshDirective)
        => loopKind == NpcTalkLoopKind && !hostileInView && !freshDirective;

    // Pure decision: the early Talk-loop egress latch is still ACTIVE this tick.
    // Active while within the latch window AND still in the same landblock the
    // loop was detected in (leaving the landblock means the loop is broken) AND
    // no hostile has appeared AND the server is not freshly guiding the bot.
    // Extracted for deterministic unit testing; own-signal only, no game content.
    internal static bool IsTalkLoopEgressActive(
        DateTimeOffset nowUtc, DateTimeOffset until, uint? latchLandblock,
        uint? currentLandblock, bool hostileInView, bool freshDirective)
        => nowUtc < until
           && latchLandblock is uint lb && currentLandblock is uint cur && lb == cur
           && !hostileInView
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
            && !string.Equals(v.Name, sel.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(sel.NameContains)
            && (v.Name is null
                || !v.Name.Contains(sel.NameContains, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (sel.Wcid is uint w && !(v.Wcid is uint vw && vw == w)) return false;
        return true;
    }

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
        // stalefix-run-01 where the Society Greeter kept rejecting
        // the Calling Stone with TradeAiDoesntWant and the LLM kept
        // re-emitting Give(Society Greeter, Calling Stone) forever.
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
            if (stuck || landblockChangedSinceLook || semanticRejectSinceLook
                || inventoryChangedSinceLook || leftTop || topInactive || topDeadlinePassed
                || budgetExhausted)
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
            && !stickyAlreadyAttempted)
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
        var userPrompt = BuildUserPrompt(world, events, currentGoal, _stack, _currentPickerActivity, _currentExplorationCandidates, dwellEntry, _currentRecentSightings, _levelAtCurrentLandblockEntry, SecondsSinceLastOwnDeath(nowUtc), BuildGoalProgressSnapshot(), _currentUnreachableTargets, _currentApproachDistance, _currentExcursionCoverage, _currentFreshKillCorpses, _currentLootedEmptyCorpses, localUseChurn);
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
                        $"applied={outcome.AppliedLog.Count} " +
                        $"revision_after={_stack.Revision} depth_after={_stack.Depth}");
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
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Use target={goal.Target}" +
                " — stationary world-object Use repeated with no progress; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: stationary no-op world-object Use loop");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "world-object Use");
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
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Use target={goal.Target}" +
                " — world-object Use churn within one landblock (same target re-Used, or too many distinct" +
                " objects toured, with no egress); deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: landblock world-object Use churn (no egress)");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "world-object Use");
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
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind);
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
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind);
        }

        // Roving single-NPC Talk-loop break (cp-2365): the stationary single-NPC
        // guard resets on MOVEMENT and the multi-NPC guard needs >=2 targets, so a
        // bot that keeps walking up to the SAME NPC loops past both. This fires on
        // N consecutive STALE (no dialog-novelty, no progress) Talks to ONE NPC
        // regardless of position, and breaks it via the SAME egress so the bot
        // does something else (train / explore / equip) instead of re-greeting a
        // dead conversation.
        if (IsRovingNpcTalkLoop(goal, world, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Talk target={goal.Target}" +
                " — roving single-NPC Talk loop with no progress or dialog novelty; deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: roving no-progress single-NPC Talk loop");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, NpcTalkLoopKind);
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

        // Unreachable-Attack repeat loop-break (2026-06-05): cp-2272 cut the
        // motor's no-lock fast-fail from 30s to 6s, which ~5x's the rate at
        // which a weak model can re-emit the SAME Attack on a monster that has
        // wandered OUT of PVS (no live snapshot, no explored sighting route,
        // no frontier → motor emits a terminal GoalFailed "selector resolved
        // to no live object"). Each re-emit just re-fails instantly and wakes
        // another no-current-goal LLM call — burning quota. Drop the repeat
        // and defer to the fallback (a real Explore that MOVES the bot) once
        // the motor has failed to reach this exact target twice. Skipped the
        // moment the target re-enters PVS (let the real engagement proceed)
        // and self-expiring as the failures age out of the event window.
        if (IsUnreachableTargetRepeat(goal, world, events))
        {
            Console.WriteLine(
                $"[llm-dedup] dropping LLM Attack target={goal.Target}" +
                " — target repeatedly unreachable (out of PVS, no route); deferring to fallback.");
            _training?.RecordParseError(decisionId,
                "dropped-by-dedup: repeatedly-unreachable out-of-PVS Attack target");
            return EscapeOrFallback(world, events, currentGoal, nowUtc, "unreachable Attack");
        }

        _training?.RecordEmittedGoal(decisionId, goal);
        // Sticky-objective bookkeeping: remember this LLM-authored goal
        // so ProposeGoal can re-drive it without another LLM call while
        // it remains unfinished, and give the new objective a fresh
        // re-emit budget.
        _lastLlmGoal = goal;
        _stickyReEmitCount = 0;

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

    /// <summary>
    /// True iff an LLM <see cref="GoalKind.Attack"/> is re-proposing a target
    /// the motor has REPEATEDLY (≥ <c>UnreachableRepeatThreshold</c>) failed
    /// to reach with a terminal "selector resolved to no live object"
    /// GoalFailed — i.e. the named monster is out of PVS, the bot has no
    /// explored sighting route to it, and frontier exploration found nothing.
    /// Re-emitting it just re-fails instantly (cp-2272's 6s no-lock fast-fail)
    /// and wakes another no-current-goal LLM call, burning per-day quota.
    ///
    /// Pure, no policy state: the implicit cooldown is the recent-event
    /// window — once the failures age out the Attack flows again (and the
    /// motor's cp-2271 sighting-route path gets another try). Skipped the
    /// instant the target re-enters PVS (<see cref="VisibleMatchesSelector"/>)
    /// so a real engagement is never suppressed. Correlates by the failed
    /// goal's OWN selector name (carried on the GoalFailed event) — no server
    /// dialogue text, no hardcoded names/wcids/landblocks.
    /// </summary>
    internal static bool IsUnreachableTargetRepeat(
        Goal goal, WorldStateProjection world, EventStream events)
    {
        if (goal.Kind != GoalKind.Attack) return false;
        var target = goal.Target;
        var targetName = target?.Name;
        if (target is null || string.IsNullOrWhiteSpace(targetName)) return false;

        // Target currently in view → never suppress; the motor will resolve a
        // live snapshot and the real Attack can fire.
        if (world.Visible.Any(v => VisibleMatchesSelector(target, v)))
            return false;

        const int LookbackEvents = 30;
        const int UnreachableRepeatThreshold = 2;
        int count = 0;
        foreach (var ev in events.Recent(LookbackEvents))
        {
            if (ev.Kind != EventKind.GoalFailed) continue;
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
            ep = new WorldUseChurnEpisode { Landblock = self.Landblock, FloorSequence = events.NextSequence };
            ep.UseCounts[key] = 1;
            _worldUseChurnEpisode = ep;
            return false;
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

        var key = CanonicalUseTargetKey(goal.Target);
        if (key is null) return false;
        // GUID-backed identity ONLY. CanonicalUseTargetKey falls back to a
        // name when no guid is present; because this guard (unlike the
        // stationary ones) does NOT reset on movement, a name-only key would
        // conflate DISTINCT same-named NPC instances into one bogus loop. Skip
        // name-only targets entirely.
        if (!key.StartsWith("guid=", StringComparison.Ordinal)) return false;

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

    // break-sticky-on-self-interact: true iff the Motor emitted a
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
            _lastOwnDeathUtc = nowUtc;
        _lastObservedDeaths = nd;
    }

    // Whole seconds since the last observed in-session own-death, or null if the
    // bot has not died this session. Clamped to >= 0. Handed to the prompt
    // builder as recency telemetry.
    private int? SecondsSinceLastOwnDeath(DateTimeOffset nowUtc)
        => _lastOwnDeathUtc is DateTimeOffset d
            ? Math.Max(0, (int)(nowUtc - d).TotalSeconds)
            : (int?)null;

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
        (int Distinct, bool Latched)? localUseChurn = null)
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
  "kind": "Give" | "Use" | "Attack" | "Pickup" | "Wield" | "GoTo" | "Talk" | "Wait" | "Explore" | "RaiseAttribute" | "RaiseVital" | "RaiseSkill" | "Recall",
  "target": { "name"?: string, "name_contains"?: string, "wcid"?: number, "item_type_mask"?: number, "short_desc_contains"?: string, "guid"?: number },
  "item":   { ...same as target... } | null,
  "amount": number | null,   // Raise* only: whole positive XP; target.name = the attribute/vital/skill
  "direction": "north"|"northeast"|"east"|"southeast"|"south"|"southwest"|"west"|"northwest" | null,   // Explore only: OPTIONAL compass bearing that steers the search (short forms n/ne/e/se/s/sw/w/nw also accepted); omit to wander undirected
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
  "kind": "Give" | "Use" | "Attack" | "Pickup" | "Wield" | "GoTo" | "Talk" | "Wait" | "Explore" | "RaiseAttribute" | "RaiseVital" | "RaiseSkill" | "Recall",
  "target": { "name"?: string, "name_contains"?: string, "wcid"?: number, "item_type_mask"?: number, "short_desc_contains"?: string, "guid"?: number },
  "item":   { ...same as target... } | null,
  "amount": number | null,   // Raise* only: whole positive XP; target.name = the attribute/vital/skill
  "direction": "north"|"northeast"|"east"|"southeast"|"south"|"southwest"|"west"|"northwest" | null,   // Explore only: OPTIONAL compass bearing that steers the search (short forms n/ne/e/se/s/sw/w/nw also accepted); omit to wander undirected
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
        sb.AppendLine("- If an inventory item's short_desc says what to do with it, follow it. 'Give' requires BOTH target (the NPC) and item (the thing given).");
        sb.AppendLine("- `ActionRejected` = the server refused that exact (kind, target, item). Do NOT immediately retry the same combo; read its `label`/`message`, then pick a different verb, item, or NPC. TWO+ rejections of the same target+item (any verb) = BLOCKED (unmet prerequisite). Items whose `short_desc` says 'double-click', 'read', or 'activate' must be Use'd on yourself FIRST (target = your own name from `## Self`) before related Give/Talk unlock — prefer `Use{target: name=\"<your-name>\", item: name=\"<that item>\"}` over retrying a blocked combo.");
        sb.AppendLine("- Read `## Server hints`: phrases like \"Double click X\" or \"Use X to ...\" tell you the exact verb+target. If that object is visible AND the server instructed it, emit `Use{target: name=\"X\"}`. The server is your tutorial; don't ignore it for pure exploration.");
        sb.AppendLine("- Combat targets: `monster`-tagged creatures are valid combat targets (grant XP + loot); `npc`-tagged are civilians — talk/trade, do NOT attack. Combat is the primary XP source outside NPC quests.");
        sb.AppendLine("- SELF-ARM before fighting: if `Combat readiness` says `UNARMED` you cannot win fights — arm yourself before OPTIONAL combat. If it lists a `melee weapon in your inventory`, emit `Wield` for that item; else if it lists a `melee weapon nearby`, emit `Pickup` for it. If a `missile weapon` is wielded but `missile ammo: EMPTY`, you cannot fire — if it lists `missile ammo in your inventory`, emit `Wield` for that ammo before attacking. Do NOT re-emit a `Wield`/`Pickup` the policy rejected or that is unreachable — try the other source or move on. If NO weapon/ammo is available anywhere, keep doing quests/`Explore` (do not stall waiting for one). A `HOSTILE` attacker still takes priority — defend or flee even while unarmed.");
        sb.AppendLine("- LEVELING is core progress — be PROACTIVE, not reactive. When combat-ready (`Combat readiness` does NOT say `UNARMED`) AND not mid an explicit server/quest directive: if a `monster` is in view, `Attack` it (per COMBAT SAFETY below); if NO `monster` is in view, do NOT loiter among town `npc`s once their dialog is exhausted — emit `Explore{target: {name: \"anywhere\"}}` toward open areas where monsters live. Do not wait to be attacked first.");
        // The NON-HOSTILE rule is entirely about what to do WHEN a monster is
        // visible (it references `nearest monster` and `monsters in view` > 0),
        // so render it ONLY when a monster is actually in view — the SAME wire
        // fact (`!IsCorpse && (IsMonster || ObservedHostile)`) that produces the
        // `monsters in view`/`nearest monster` lines it cites. With none in view
        // the rule references absent telemetry = noise that buries the rules
        // that DO apply (cp-2331 section-presence gating). Render gate on an
        // observed fact; the LLM still decides; no game knowledge.
        if (world.Visible.Any(v => !v.IsCorpse && (v.IsMonster || v.ObservedHostile)))
        sb.AppendLine("- NON-HOSTILE IS NOT NON-TARGET: a visible `monster` is a valid XP target whether or not it has attacked you — `0 attacking you now` means none are on you YET, NOT \"nothing to fight\". If `Combat readiness` lists a `nearest monster`, you ALREADY have a target, so do NOT emit `Explore` \"to find monsters\" while `monsters in view` is above 0 — `Attack` a killable/nearest `monster` instead (per COMBAT SAFETY: prefer a KIND you can defeat and skip one that has already beaten you; low `health`, a fresh `corpse` to loot, or an explicit server/quest directive still take priority).");
        sb.AppendLine("- NPC REPEAT EXHAUSTION — a repeating conversation is not progress: when `## Server hints` tags an NPC's dialog `repeated xN` (the count it shows) with N>=3, OR the recency note shows `recent Talk emissions: <that NPC> xN` with N>=3 (this ALSO covers a SILENT NPC that returns no dialog), OR the SAME NPC keeps producing recent lines that add no new item, hint, inventory, location, or change, that conversation is EXHAUSTED — re-`Talk`ing it will NOT advance anything even if it alternates 2-3 canned lines, so do NOT keep Talking just because the latest line looks new. PIVOT to a DIFFERENT verb/target: if the dialog named a concrete next action, follow it with a NON-Talk verb (`Use` the object it pointed at, `Give` a held item, `Pickup` visible loot, or `Talk` a DIFFERENT not-yet-talked NPC); else `Explore` away. Re-Talking the same NPC is NEVER how you follow an exhausted directive. If killable `monster`s are in view and no concrete non-Talk action is actionable, `Attack` one for XP instead — but only AFTER the conversation is exhausted; an NPC with genuinely NEW dialog still takes priority.");
        // The SPEND XP rule (~1.1KB) is inapplicable when there is nothing to
        // spend, so render it ONLY when unspent XP > 0 — the SAME factual gate
        // the `## Self` "invest NOW" cue and the priority-band phrase use. This
        // trims prompt bytes (payload/tempo) and stops a long rule burying the
        // combat guidance in the common zero-unspent-XP case. No game knowledge:
        // a render gate on an observed fact, the LLM still decides.
        if (world.Self.AvailableExperience is long spendableXp && spendableXp > 0)
        sb.AppendLine("- SPEND XP is a FIRST-CLASS action, not an afterthought: investing unspent XP permanently improves your character, so whenever `## Self` shows unspent XP and it is safe to deliberate (you are not mid a losing fight and no `HOSTILE` is on you), weigh investing some BEFORE choosing an OPTIONAL combat/explore action — do not let XP sit unspent run after run. `## Self` shows `experience: N total, M unspent`. Unspent XP is wasted until invested. Verbs: `RaiseAttribute{target: {name: \"<attribute>\"}, amount: <positive whole XP>}` (names: strength, endurance, quickness, coordination, focus, self), `RaiseVital{target: {name: \"<vital>\"}, amount: <XP>}` (names: health, stamina, mana), or `RaiseSkill{target: {name: \"<skill>\"}, amount: <XP>}` (use a name from `trained skills` in `## Self`; the server rejects untrained skills). A positive `amount` is REQUIRED; it may be any positive whole number up to your full unspent balance — one raise can invest your entire unspent total in a single action, or any part of it. Attribute effects are MECHANICS; the allocation is YOUR call and there is NO fixed build: strength and coordination drive MELEE offense (how hard and how often your swings land); quickness aids defense and missile play; focus and self power magic; endurance and health raise MAX HEALTH. Do NOT pour every point into ONE attribute — spread XP across the attributes your actual skills depend on, and read the bottleneck from evidence: if you die too fast, survivability (endurance/health) is the limit; if `current fight` shows hits `evaded` or 0 damage `landed`, melee offense (strength/coordination) is the limit; if you fight with spells, raise magic attributes/skills. E.g. raise coordination/strength when melee swings miss or barely hurt; raise endurance/health when low max HP is killing you.");
        sb.AppendLine("- TAPPED OUT means MOVE ON: a `tapped out` line in `Combat readiness` means you have NOT gained a level here for a while. Emit `Explore{target: {name: \"anywhere\"}}` to travel to a new area with monsters you can DEFEAT. Prefer a monster you can actually kill over a tougher one — XP comes from KILLS, and a monster that defeats you sets you back, so do NOT chase `tougher` monsters for more XP. (Looting a fresh corpse or an explicit server/quest directive still comes first.)");
        sb.AppendLine("- COMBAT SAFETY & PACE: fight roughly one `monster` at a time — if several cluster or more than one is `HOSTILE`, back off and pull them singly (the `monsters in view` line counts them: `H actively HOSTILE` of 2+ means you are SWARMED — break away with `Explore`). Danger signals you have: your `deaths` count and, when shown, `health` in `## Self` (monster levels are NOT given — judge from OUTCOMES, not numbers). The `health` line shows BOTH a percentage AND absolute HP (e.g. `100% (1/1 HP, rising)`) — trust the ABSOLUTE HP: a handful of HP is lethal even at a high %, and `rising` means you are still regenerating BELOW full strength, so finish recovering before STARTING an OPTIONAL fight (a `HOSTILE` attacker still takes priority). The `recent inbound damage` line shows hits you have TAKEN and total damage in the last few seconds — if it climbs while your absolute `health` is low you are losing: disengage (`Explore`) or `Recall` rather than fight to 0 HP. The `current fight` line in `Combat readiness` shows swings `landed` vs `evaded`: many `evaded` with 0 `landed` (0 damage dealt) means that target out-defends you and you CANNOT win — DISENGAGE now (emit `Explore` to break away) and try a different, weaker, or more distant `monster`. The `current fight` line also shows the `target health now N%` and, once the fight has run, `(was M% when this fight began)`: if AFTER MANY swings the target's health is still barely below its fight-start value AND you are taking `recent inbound damage` or your own `health` is low or falling, the exchange is going AGAINST you even though some swings land — DISENGAGE (`Explore`) and pick a weaker `monster`. Early in a fight a small health drop is NORMAL, so judge from a SUSTAINED run of swings, not the first few; and if the target's health is steadily DROPPING toward 0 while your health holds, a SLOW fight is still winnable — KEEP attacking until it dies (slow is fine, losing is not). The `combat history` lines in `Combat readiness` are your own past outcomes per monster KIND this session — and each `monster` row in `Visible nearby` now carries its own `[your record: ...]` inline — before engaging a visible monster, read its inline record (or match its name in `combat history`): prefer a KIND you have `kills` against; AVOID a KIND with `deaths`/`near-deaths`/`ineffective` (you fought it but could not kill it — it out-defends you) and no `kills` (it has beaten you — pick a different, weaker monster or Explore on). Likewise if `deaths` rises or `health` is low, disengage and AVOID re-attacking the same KIND of monster that just defeated you. Explicit server/quest directives and looting fresh corpses outrank optional combat; don't grind one spot forever.");
        // The corpse-looting rule only applies when a corpse is actually in
        // view (the `corpse` tag is rendered from the same IsCorpse projection
        // flag). With no corpse visible the rule references a target that isn't
        // there; omit it to save prompt bytes and unbury the applicable rules.
        if (world.Visible.Any(v => v.IsCorpse))
        sb.AppendLine("- Looting: a dead monster becomes a `corpse` (a container that DECAYS). `Use{target: name=\"<corpse>\"}` to open, then `Pickup{target: name=\"<item>\"}` items that appear. NEVER skip a fresh corpse to chase the next NPC.");
        // The chest-looting rule only applies when a chest is in view (same
        // IsChest projection flag that renders the `chest` tag). Omit otherwise.
        if (world.Visible.Any(v => v.IsChest))
        sb.AppendLine("- Loot containers: `chest`-tagged openables (Container + Openable, don't decay). `Use` to open, then `Pickup` contents. NEVER skip an unopened chest to chase the next NPC.");
        sb.AppendLine("- Writables: a `sign` (stuck) is read in place with `Use{target: name=\"<sign>\"}`; a `book` (not stuck) is `Pickup`-able — prefer Pickup.");
        sb.AppendLine("- LOOP-BREAK — do not repeat an action that produced no change (see the `Location & recency` section): (a) Talk: if you emitted `Talk{X}` 3+ times in the last 10 emissions with no new item and no new server hint, talk to a different NPC or Use/Give/Explore. (b) inventory-USE: if `Recently used inventory items` lists an item as `still in inventory (not consumed)`, the policy WILL drop a Use against it — do not re-emit unless a new event (ActionRejected recovery hint, new dialog/hint, inventory change) justifies it; when broken, pick a DIFFERENT action (a `monster` in view + weapon wielded → `Attack`; a not-yet-talked visible NPC → `Talk`; a visible pickup item → `Pickup`; else `Explore`). (c) world-object USE: the `Location & recency` section lists `recent Use emissions` per target; 3+ Uses of the SAME target with no change (same landblock per `minutes in current landblock`, no new hint/item) is a dead end → `Explore{target: {name: \"anywhere\"}}` or pick a different target. Re-Use ONLY if something concrete changed (you crossed into a new landblock, a new hint/item appeared, or an `ActionRejected` told you to retry).");
        sb.AppendLine("- PASSAGE-OPENED is not progress: opening a `door` (or any non-container `openable`) does NOT move you. Only MOVING to a new area counts (current cell/landblock changing, or previously-unseen objects in `Visible nearby`). After Using a door once, do NOT Use it again from the same spot — emit `Explore{target: {name: \"anywhere\"}}` (or a goal beyond it) to travel THROUGH it. (Does NOT apply to `chest`/`corpse` containers, which you Use to reveal loot then `Pickup`.)");
        sb.AppendLine("- LOOP-BREAK (town-stuck): if `minutes in current landblock` > 5 AND `nearest monster: (none in view)` AND every visible creature is `npc` (no `monster` tag anywhere), you are STUCK in a town — emit `Explore{target: {name: \"anywhere\"}}` immediately. The picker walks you through visible Doors/portals to new areas. This OVERRIDES Talk/Give even when a new NPC is visible — talk-to-every-NPC is not progress with no monsters in view.");
        sb.AppendLine("- HUNT EXCURSION (leave a tapped-out safe zone to find monsters): monsters do NOT spawn in safe zones — you must travel OUT to surrounding open country. When combat-ready, NO `monster` anywhere in `Visible nearby`, NO un-acted server/quest directive naming a specific next target (re-talking an NPC with no NEW dialog and browsing vendors do NOT count), AND `minutes in current landblock` is more than a few with local progress dried up (no new level, quest item, or unique hint), the zone is TAPPED OUT. Emit `Explore{target: {name: \"anywhere\"}}` — crossing out takes MANY ticks, so KEEP emitting it every cycle (your own recent `Explore` does NOT mean the excursion is done; do NOT revert to talking the same town NPCs mid-excursion) until your `landblock` actually changes OR a `monster` appears (then `Attack` it). A NEW server/quest directive, quest item, danger, or fresh dialog step interrupts the hunt — act on it. Quest progress outranks an optional hunt.");
        sb.AppendLine("- STEER A BARREN EXCURSION: `Explore` accepts an OPTIONAL `direction` — one of `north`, `northeast`, `east`, `southeast`, `south`, `southwest`, `west`, `northwest` — that biases WHICH way the excursion heads (the motor walks roughly that bearing toward unexplored ground). Plain `Explore{target: {name: \"anywhere\"}}` wanders UNDIRECTED, which can keep drifting the SAME way and re-cover empty country. So if a hunt excursion has already crossed SEVERAL `landblock`s (watch `minutes in current landblock` resetting and your own repeated recent `Explore`s) and STILL no `monster` has appeared, that bearing is barren — emit `Explore{target: {name: \"anywhere\"}, direction: \"<a DIFFERENT or opposite compass heading>\"}` to search NEW country instead of drifting the same way. Vary the heading across excursions until a `monster` appears (then `Attack` it). You have NO map — you are choosing a SEARCH direction to try, not a known monster location; `direction` is optional and only steers, so an undirected `Explore` still works when you have no reason to prefer a bearing.");
        sb.AppendLine("- BLOCKED targets: `ActionRejected` label `Blocked`/`Unreachable` = server physics held the bot against geometry (wall, closed door, barrier). Do NOT re-emit the same target. Prefer a visible Door (walk to / Use it — it likely leads where you were going); else `Explore` to route around. The bot cannot clip through obstacles.");
        sb.AppendLine("- STUCK ESCAPE (last resort): `Recall{}` teleports you to your attuned lifestone. Use it ONLY when you are physically unable to move at all — e.g. the movement report (when shown) says the server held you at the same position across repeated attempts AND no visible Door or `Explore` route frees you (a ledge/cliff with your target far BELOW is a classic trap: every step is mid-air and rejected). It requires an attuned lifestone (Use a `Life Stone` to attune); the server refuses it inside the training academy and right after PvP, and it costs half your mana — so it is an escape hatch, NOT routine travel. Try a Door or `Explore` first; reach for `Recall` only when those cannot move you.");
        // cp-2346 — does the recent event window carry server/NPC text the LLM
        // might need to compile (a task directive)? Pure presence check on the
        // same dialog kinds the `## Server hints` section renders; gates the
        // QUEST-DIALOG COMPILER rule + the `## Recent directive check` capsule
        // so they cost zero prompt bytes when no dialog is present.
        var hasRecentServerDialog = events.Recent(EventStream.DefaultCapacity).Any(e =>
            (e.Kind == EventKind.NpcDialog || e.Kind == EventKind.ServerMessage || e.Kind == EventKind.PopupString)
            && !string.IsNullOrEmpty(e.Text));
        if (stack is not null)
        {
            sb.AppendLine("- STRATEGIC STACK: `## Intent stack` is the current plan; TOP is the active sub-goal, ancestors paused. Per-cycle goals advance TOP. PUSH on a discovered sub-task; POP_TOP when done and no predicate caught it (rare — predicates auto-pop); REPLACE_TOP when right-frame-wrong-target; MARK_TOP_BLOCKED when stuck. Always echo `stack_revision`.");
            sb.AppendLine("- COMPLETION PREDICATES: pick the typed predicate matching your termination criterion (the discriminator field is `type`, e.g. `{\"type\":\"kill_count_total_at_least\",\"count\":3}`); prefer server-authoritative (num_deaths, coin_value). *_total_* for absolute thresholds, *_since_push_* for deltas. A hunt excursion completes when a monster is finally in view: `{\"type\":\"visible_tag\",\"tag\":\"monster\"}` (set `deadline_seconds` too, as a liveness backstop). If none fits, `{\"type\":\"always_false\"}` + `predicate_request`.");
            sb.AppendLine("- PERSIST A HUNT EXCURSION ON THE STACK: when you begin a hunt excursion (per the HUNT EXCURSION rule) and TOP is not already one, in the SAME response PUSH an intent — `kind`:\"hunt-excursion\", completion `{\"type\":\"visible_tag\",\"tag\":\"monster\"}`, and set `deadline_seconds` to a few minutes — alongside your `Explore`. The completion auto-pops the intent the instant a `monster` appears (then `Attack` it); the deadline is just a backstop so a wedged excursion eventually re-deliberates. While it stays TOP you are EN ROUTE: keep emitting `Explore{target: {name: \"anywhere\"}}`. Merely crossing into a new `landblock` is NOT done — if the new area is ALSO a monster-free town, the excursion stays TOP, so keep exploring OUT (do not start working town objects again). The ONLY things that interrupt are a genuinely NEW server/quest directive, a newly-acquired quest item, or danger — stale NPCs, vendors, and already-seen doors are NOT interrupts. This makes the excursion survive across ticks instead of being re-decided (and abandoned) every cycle.");
            if (hasRecentServerDialog)
                sb.AppendLine("- QUEST-DIALOG COMPILER: if observed NPC/server text (see `## Server hints`) ASSIGNS a task — names target creature(s) to kill, an item to fetch, or a place to reach — compile it onto the stack BEFORE optional grinding: PUSH (or REPLACE_TOP if TOP is a generic hunt-excursion) an intent whose `kind`/`target_name`/`rationale` COPY the named target(s) verbatim FROM the observed text, and prefer `Attack` only against visible monsters whose names match those target words (unrelated monsters are optional fallback, NOT quest progress). Use a `kill_count_*` completion ONLY when the required count is explicitly stated in the observed text — NEVER invent a count; if no predicate fits, use `{\"type\":\"always_false\"}` + `predicate_request`, and set `deadline_seconds` as a liveness backstop so an unreachable/unfound target eventually re-deliberates. When observed progress/dialog indicates the task is done, RETURN to the task-giver and `Talk`/`Use` it to turn in. Greetings, lore, vendor flavor, and descriptions with no requested action are NOT tasks — do NOT invent a task, target, count, NPC, or location.");
        }
        // The AUTONOMOUS PICKER rule only makes sense when the
        // `## Autonomous picker activity` section is present (same gate the
        // section itself uses below). Rendering it otherwise wastes prompt
        // bytes and buries the rules that DO apply. No game knowledge: a render
        // gate on whether the picker is currently active.
        if (pickerActivity is not null)
        sb.AppendLine("- AUTONOMOUS PICKER: when `## Autonomous picker activity` is present, the schema-only picker auto-drives WHERE TO WALK (nearest eligible candidate by distance) because you had no goal that tick; it OWNS NO VERBS. On arrival the motor sends NOTHING unless your Goal's Kind names a verb (`Use`/`Talk`/`Pickup`/`Attack`/`Give`). `picker has ARRIVED at target X` = parked next to X awaiting a verb — emit `Use`/`Talk`/`Pickup`/`Attack{target: name=\"X\"}`, or `Explore{target: name=\"<other>\"}` to redirect. Doing nothing parks ~2s then picks the next candidate.");
        sb.AppendLine("- TRANSITIONS — doors and portals: `door`/`portal`-tagged objects are activated with `Use{target: name=\"<name>\"}` (the picker never auto-opens them). When parked at a door/portal with no better verb, `Use` it — that's how the bot moves between rooms/buildings/landblocks. If a door rejects Use as Locked and you hold an item whose `short_desc`/name says key, retry `Use{target: name=\"<door>\", item: name=\"<key>\"}`.");
        // The EXPLORATION CANDIDATES rule only applies when that section is
        // present (same gate the section uses below). Omit it otherwise.
        if (explorationCandidates is not null && explorationCandidates.Count > 0)
        sb.AppendLine("- EXPLORATION CANDIDATES: when `## Exploration candidates` is present, the in-range queue is empty and the fallback walks to the nearest off-screen object; the TOP entry is the default. Each line shows `kind=mob|npc|object` (raw perception; `object`=non-creature). To pick a DIFFERENT one (e.g. an off-screen `mob` to hunt, backtrack through a visited door, or skip a distant pickup for a closer visited NPC), emit `Explore{target: {guid: \"0x...\"}}` (guid is the most reliable selector) or `{name: \"...\"}`.");
        sb.AppendLine("- PURSUE UNSEEN OBJECTIVES: when dialog or a hint tells you to find/reach/talk-to someone NOT in `Visible nearby` (e.g. \"talk to the trainer in the next room\", \"find the captain\"), emit a goal NAMING it — `Talk`/`Give`/`Explore{target: {name: \"<role-or-name>\"}}` — even though it is not yet visible; the bot walks through rooms to discover it. A role phrase (\"the guard\", \"the trainer\") is a valid target name when no proper name is given. Do NOT keep re-talking an NPC whose dialog you already got — pursue the objective that dialog gave you. With no named objective and nothing useful visible, emit `Explore{target: {name: \"anywhere\"}}`.");
        sb.AppendLine("- CLOSED DOORS ARE BARRIERS: a `door closed` row in `Visible nearby` is shut and blocks the rooms beyond it. If you are pursuing a target you cannot see or reach (e.g. the search-progress note says a named target is still not visible after several moves) and a `door closed` is nearby, `Use{target: name=\"<door>\"}` to open it, THEN `Explore` to travel through — a closed door is the usual reason the next room's occupant never appears. A `door open` row is already passable (just `Explore` through it); do not Use it again.");
        sb.AppendLine("- SERVER-INSTRUCTION PRECEDENCE: `## Server hints` text that tells you how to LEAVE, EXIT, PROCEED PAST, or ADVANCE BEYOND the area — especially naming a person/place or warning the step is irreversible — OUTRANKS repeating a local interaction you already observed (re-picking an item you hold, re-talking an NPC who gave no new dialog, re-using an object that didn't change). When such an instruction is present and unacted, emit a `Talk`/`Use`/`Explore` toward the named target (even if not visible) INSTEAD of looping completed steps.");
        sb.AppendLine("- FINISH MULTI-STEP DIRECTIVES: if you hold an item the server gave you for an unfinished objective (\"take this and bring it back\", \"give X to Y\", \"use this to leave\"), completing it OUTRANKS incidental looting/exploration — return to the NAMED npc/object and `Give`/`Use` it. Treat an unused objective item as an open task, not as done.");
        sb.AppendLine(world.Self.AvailableExperience is long bandXp && bandXp > 0
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
            var avail = world.Self.AvailableExperience is long axp
                ? (axp > 0
                    ? $", {axp} unspent (available to invest NOW via RaiseAttribute/RaiseVital/RaiseSkill — see SPEND XP)"
                    : $", {axp} unspent")
                : string.Empty;
            sb.AppendLine($"- experience: {txp} total{avail}");
        }
        if (world.Self.Attributes is { Count: > 0 } selfAttrs)
        {
            // RAW base attribute values (unbuffed), seeded at login and kept
            // live by discrete PrivateUpdateAttribute (0x02E3) after a raise.
            sb.AppendLine($"- attributes: {string.Join(", ", selfAttrs.Select(a => $"{a.Name} {a.Base}"))}");
        }
        if (world.Self.TrainedSkills is { Count: > 0 } selfSkills)
        {
            // RAW list of the skills the character actually has (wire
            // AdvancementClass Trained/Specialized) — the only valid RaiseSkill
            // targets. Seeded at login, kept live by discrete
            // PrivateUpdateSkill (0x02DD) after a raise.
            sb.AppendLine(
                "- trained skills (valid RaiseSkill targets): " +
                string.Join(", ", selfSkills.Select(s =>
                    $"{s.Name} ({s.Advancement}, raised {s.RaisedRanks})")));
        }
        if (FormatSelfHealth(world.Self.HealthCurrent, world.Self.HealthObservedPeak, world.Self.HealthFraction, world.Self.HealthRising) is string selfHealthLine)
            sb.AppendLine(selfHealthLine);
        if (world.Self.NumDeaths is int nd)
        {
            // Cumulative total + (when known) how long ago the most recent
            // in-session death was, so the LLM can tell a fresh respawn from an
            // old count and decide whether to head back out. Raw telemetry — no
            // urgency/recommendation baked in by source.
            var recency = secondsSinceLastDeath is int ds
                ? $" (most recent observed ~{ds}s ago)"
                : "";
            sb.AppendLine($"- deaths (server-tracked): {nd}{recency}");
        }
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
            var invCounts = new Dictionary<(string Name, uint Wcid, string ShortDesc, uint Wielded), int>();
            var invOrder = new List<(string Name, uint Wcid, string ShortDesc, uint Wielded)>();
            foreach (var i in world.Inventory)
            {
                var sd = string.IsNullOrWhiteSpace(i.ShortDesc) ? "" : i.ShortDesc!.Trim();
                var wielded = i.WieldedAt is uint iw ? iw : 0u;
                var key = (i.Name, i.Wcid, sd, wielded);
                if (invCounts.TryGetValue(key, out var c)) invCounts[key] = c + 1;
                else { invCounts[key] = 1; invOrder.Add(key); }
            }
            foreach (var key in invOrder)
            {
                var n = invCounts[key];
                sb.Append($"- {key.Name} (wcid={key.Wcid}");
                if (key.Wielded != 0) sb.Append($", wielded@0x{key.Wielded:X}");
                sb.Append(")");
                if (n > 1) sb.Append($" x{n}");
                sb.AppendLine();
                if (key.ShortDesc.Length > 0)
                    sb.AppendLine($"    short_desc: {key.ShortDesc}");
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
        // loop: the bot Used the same Holtburg chest 3x + revisited the same
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
                sb.AppendLine($"- {nm}{wcidStr} guid=0x{o.Guid:X8}: interacted x{o.Count} recently (still visible)");
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
        var missileWeaponWielded = world.Inventory.Any(i =>
            i.WieldedAt is uint mw && mw != 0 &&
            i.ItemType is uint mit && (mit & ItemTypeMasks.MissileWeapon) != 0);
        var ammoLoaded = world.Inventory.Any(i =>
            i.WieldedAt is uint aw && aw == ItemTypeMasks.MissileAmmoSlot);
        var bagAmmo = (!missileWeaponWielded || ammoLoaded) ? null : world.Inventory.FirstOrDefault(i =>
            (i.WieldedAt is not uint baw || baw == 0) &&
            i.ValidLocations is uint vl && (vl & ItemTypeMasks.MissileAmmoSlot) != 0);
        var armed = meleeWeaponWielded || missileWeaponWielded;
        // Acquisition affordances surfaced ONLY when unarmed, so the LLM
        // can act on "arm yourself" instead of merely noting it is
        // unarmed (the live failure mode): an unwielded melee weapon
        // already in the bag (→ Wield it) and the nearest pickup-able
        // melee weapon lying in the world (→ Pickup it). Both are pure
        // typed-affordance projections (ItemType MeleeWeapon bit), no
        // names/wcids/landblocks.
        var bagWeapon = armed ? null : world.Inventory.FirstOrDefault(i =>
            (i.WieldedAt is not uint bw || bw == 0) &&
            i.ItemType is uint bit && (bit & ItemTypeMasks.MeleeWeapon) != 0);
        var groundWeapon = armed ? null : world.Visible
            .Where(v => !v.IsMonster &&
                        v.ItemType is uint vit && (vit & ItemTypeMasks.MeleeWeapon) != 0)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        var nearestMonster = world.Visible
            .Where(v => v.IsMonster)
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
        string weaponLine;
        if (meleeWeaponWielded)
            weaponLine = "melee weapon wielded";
        else if (missileWeaponWielded)
            weaponLine = $"missile weapon wielded; missile ammo: {(ammoLoaded ? "loaded" : "EMPTY (wield ammo to fire)")}";
        else
            weaponLine = "NONE wielded - UNARMED";
        sb.AppendLine($"- weapon: {weaponLine}");
        if (FormatSelfHealth(world.Self.HealthCurrent, world.Self.HealthObservedPeak, world.Self.HealthFraction, world.Self.HealthRising) is string crHealthLine)
            sb.AppendLine(crHealthLine);
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
        if (groundWeapon is not null)
        {
            var gwd = groundWeapon.Distance is float gd ? $" d={gd:F1}" : "";
            sb.AppendLine($"- melee weapon nearby (Pickup it to arm): {groundWeapon.Name}{gwd}");
        }
        if (bagAmmo is not null)
            sb.AppendLine($"- missile ammo in your inventory (Wield it to load): {bagAmmo.Name}");
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
        var recentGoalEmits = hintPoolForRecency
            .Where(e => e.Kind == EventKind.GoalEmitted && !string.IsNullOrEmpty(e.Text))
            .Take(10)
            .ToList();
        var talkByKey = new Dictionary<string, (int Count, string Display, string? Guid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ge in recentGoalEmits)
        {
            var txt = ge.Text!;
            if (!txt.StartsWith("Talk ", StringComparison.Ordinal)) continue;
            var sm = System.Text.RegularExpressions.Regex.Match(txt, "target=(.*?) item=.*? source=");
            if (!sm.Success) continue;
            var sel = sm.Groups[1].Value.Trim();
            if (sel.Length == 0 || sel == "<empty>") continue;
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
        static string RepeatSuffix(int count) => count > 1 ? $" (repeated x{count})" : "";
        if (serverHints.Count > 0 || npcHints.Count > 0 || popupHints.Count > 0)
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

        // Distill the bot's OWN goal-lifecycle outcomes (GoalCompleted /
        // GoalFailed) into a dedicated section. These already appear in the
        // 25-event "## Recent events" tail, but in a busy area high-volume
        // observe noise (Motion/UpdatePosition/ObjectCreate) evicts them long
        // before the next decision — the same eviction problem that justified
        // the "## Recent rejections" pull-out. Dedup by (kind, target) keeping
        // the most recent of each (events are newest-first) so a repeatedly-
        // failing engagement — e.g. an Attack on a fleeing/far mob that keeps
        // timing out — surfaces once and clearly instead of either flooding
        // the list or being lost. Pure echo of own bookkeeping the LLM
        // generated; it decides whether to retry or pick a different target.
        var goalOutcomes = events.Recent(120)
            .Where(e => e.Kind == EventKind.GoalCompleted || e.Kind == EventKind.GoalFailed)
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
                "- `## Server hints` above contains recent NPC/server text. If that text ASSIGNS a task " +
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
        if (world.Self.AvailableExperience is long endcapUnspent && endcapUnspent > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Unspent XP");
            sb.AppendLine(
                $"- you have {endcapUnspent} unspent experience available this tick; the goal " +
                "verbs RaiseAttribute, RaiseVital, and RaiseSkill are executable right now, the " +
                "same as Talk/Use/Pickup/Attack/Explore.");
            sb.AppendLine(
                "- raw fact, not a recommendation: see `## Self` above for your current attribute " +
                "values and trained skills, and the SPEND XP rule for what each verb does. Whether, " +
                "how much, and which to raise is your call.");
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
            var endcapMonsterList = string.Join(", ", endcapMonsters
                .GroupBy(v => string.IsNullOrWhiteSpace(v.Name) ? "(unknown)" : v.Name!,
                         StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)
                .Take(6));
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
            sb.AppendLine(
                "- raw fact, not a recommendation: see `## Combat readiness` above for your arms/" +
                "health and your own recorded outcomes per monster kind. Whether to engage, and " +
                "which target, is your call.");
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
        // ── ## Server-refused interaction targets (end-of-prompt capsule) ─
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
                "`Explore` `direction` is optional and only steers the search. Your call.");
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

        var assembled = sb.ToString();
        var salienceTail = assembled.Substring(salienceTailStart);
        var body = assembled.Substring(0, salienceTailStart);
        return FitPromptToCeiling(body, salienceTail);
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
        "## Recent events (newest first)",
        "## Visible nearby",
        "## Inventory",
    };

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
            LastOutcome: lastOutcome ?? "");
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

        var candidates = sightings
            .Where(s => s.Kind == EntityKind.Mob)
            .Where(s => s.AgeSeconds <= RecentSightingTtlSeconds)
            .Where(s => !CurrentlyVisible(s))
            .GroupBy(s => (Name: s.Name.ToLowerInvariant(), s.Wcid, s.Landblock))
            .Select(g => g.OrderBy(s => s.AgeSeconds).First())
            .OrderBy(s => s.AgeSeconds)
            .ToList();

        if (candidates.Count == 0) return;

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

        sb.AppendLine("## Recently sighted (out of view)");
        sb.AppendLine(
            "Monsters you have seen that are NOT currently in view, from your own " +
            "memory. Not recommendations — the bot assigns no priority. To return " +
            "to one, target it by name; the bot will navigate to where it was last seen.");

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
        return $"- {s.Name} (kind=monster, {age}, {where}{lb})";
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

    internal static bool TryParseGoal(string json, out Goal? goal, out string? error)
    {
        goal = null; error = null;
        try
        {
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
                if (!wieldHasItem)
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
