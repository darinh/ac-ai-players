# Bot brain agent loop: design

- **Status:** Draft (proposed for review, revision 2)
- **Driving ADR:** [ADR-0011](../adr/0011-bot-brain-agent-loop.md)
- **Tracking issue:** _(to be filed)_
- **Date:** 2026-05-31
- **Author:** Anvil (Copilot CLI)

## 1. Why this exists

[ADR-0011](../adr/0011-bot-brain-agent-loop.md) commits the bot brain
to an explicit six-stage agent loop with a two-tick cadence. This
document carries the specifics: what's already in the code, the
threading model, the data shapes, who owns what, how the stages talk
to each other, how the LLM gets called, how it is all tested, and
how it ships in phases without breaking the live academy behavior on
`bots/botplayer-spike`.

The ADR is the contract; this doc is how we build it. Revision 2
incorporates the rubber-duck and code-audit critique passes against
revision 1 (see commit history of this file).

## 2. What we have today

Grounding for the migration. Verified against `bots/botplayer-spike`
at commit `7c657082`.

### 2.1 Threading reality

- `BotPlayer.OnBrainTick(double currentUnixTime)` is invoked from
  `Player.Player_Tick` (`Source/ACE.Server/WorldObjects/Player_Tick.cs`),
  which runs from `Landblock.TickSingleThreadedWork`
  (`Source/ACE.Server/Entity/Landblock.cs`), inside the main world
  loop (`Source/ACE.Server/Managers/WorldManager.cs`).
- **The brain tick runs on the landblock thread.** Any work that
  could touch ACE world state (cells, motion, inventory, packets)
  must run on this thread.
- The existing async escape hatch is a `ConcurrentQueue<Action<double>>`
  named `_pendingInputs`, populated by background threads and drained
  on the next brain tick via `DrainInputs(currentUnixTime)`
  (`BotPlayer.cs:158`, `:3444`).
- The existing LLM call site already uses this pattern: `FlushAndCompile`
  fires a bare `Task.Run` (`BotPlayer.cs:862`) whose continuation
  posts an `Action<double>` back to `_pendingInputs` via
  `ExecutePlanAndUpdateGoal` (`:851`, `:875`, `:894`, `:897`).
- There is **no "brain thread pool."** The `Task.Run` lands on the
  default .NET threadpool.

### 2.2 Existing fields on `BotPlayer` that move under the agent loop

| Existing field/method | Location | New home |
|---|---|---|
| `_currentGoal` + retry/escalation | `BotPlayer.cs:229-230`, `:921-1087` | Goal Stack |
| `_compiledByDialogHash` + lock | `BotPlayer.cs:189`, `:838-840`, `:1046-1053` | LLM output cache (payload changes from `Plan` to `FactsAndGoals`) |
| `_talkToFailedSearches`, `TalkToMaxFailedSearches=3` | `BotPlayer.cs:2922-2987` | Critic (RepeatedFailure cap) + Blackboard `RecentFailures` |
| Dialog coalesce buffer (per-source, ~2s flush window) | `BotPlayer.cs:217-227`, `:729-907` | Stays — Dialog ingest path is unchanged in shape; only the LLM output schema changes |
| `SnapshotVisibleNpcNames()` | `BotPlayer.cs:~1450` | Perception input to Blackboard.VisibleNpcs |
| `[perception:nearby/env/cell/items/doors-closed/cell-portals]` log lines | `BotPlayer.cs:2558-2759` | Promote from log-only to Blackboard fields; logs stay for ops debugging |
| `OnHeardNpcTell` → `IngestDialog` | `BotPlayer.cs:693`, `:729` | Stays as Dialog ingestion entry point; output schema changes |
| `DevInjectDialogFromHarness` | `BotPlayer.cs:722` | Stays — `/botdirector poke` is the live-iteration tool throughout the migration |
| `ExecutePlanOpTalkTo / GoTo / Collect / GiveItem / ReturnTo` | `BotPlayer.cs:2893-3137` | Wrapped behind `IBotActionAdapter` (see §10) so Planner-only tests can run without world; live calls unchanged |
| `TickNavStepPhysics` + `AdjustCell` cell crossing | `BotPlayer.cs:2286-2354` | Stays in the Executor adapter; consumed by Planner via `Op.GoTo` |
| Rehydration via `RehydratePersistedBotPlayers` | `BotManager.cs:535-635` | Hooks added to restore Blackboard + Goal Stack from biota properties |
| Archetype chat / follow-target / aggro | E7, E8 work | Untouched by the agent loop refactor; orthogonal |

### 2.3 Persistence reality

- Bot character + biota write through `SavePlayerToDatabase()`
  (`BotPlayer.cs:3516-3535`) and `Player_Database.cs:80-113`. This
  goes through `ShardDbContext` and writes the standard
  `Character` + `biota_properties_*` tables.
- Bot rehydrate is driven by `character.is_Bot` + offline biota in
  `BotManager.cs:535-635`.
- There is **no custom bot table today.** Adding `bot_blackboard`
  would require ShardDbContext changes, migration scripts, and a
  read/write hook in the bot save/rehydrate path. Piggybacking on
  `biota_properties_string` (one row per durable belief category,
  keyed by a new `PropertyString` enum value) rides the existing
  save/rehydrate path for free.

### 2.4 Test infrastructure reality

- `Source/ACE.Server.Tests/ACE.Server.Tests.csproj` exists today and
  uses **MSTest** (`MSTest.TestAdapter` 4.2.3, `MSTest.TestFramework`
  4.2.3), not xUnit. Phase 1 of this design uses MSTest, not xUnit;
  no new test project is needed.

## 3. The six stages, at a glance

```
                ┌───────────────┐
   world ─────▶ │  Perception   │ ──┐
                └───────────────┘   │  facts
                                    ▼
                ┌─────────────────────────────────┐
                │           Blackboard            │
                │  (volatile + durable beliefs)   │
                └─────────────────────────────────┘
                   ▲                ▲           │
                   │                │           │ snapshot
                   │ fact updates   │           ▼
   NPC tell ──▶ ┌──────────────┐  ┌─────────────────┐
                │  Dialog LLM  │  │   Goal Stack    │
                └──────────────┘  │  (typed, prio)  │
                   │              └─────────────────┘
                   │ candidate goals      │
                   ├─────────────────────▶│  push
                   │                      │
   idle/tick ──▶ ┌────────────────┐       │
                 │ Deliberator    │       │  push
                 │ (LLM | script) │ ─────▶│
                 └────────────────┘       │
                                          ▼
                                  ┌──────────────┐
                                  │   Planner    │  every fast tick
                                  └──────────────┘
                                         │ next op
                                         ▼
                                  ┌──────────────┐
                                  │   Executor   │  motion-continuity aware
                                  └──────────────┘
                                         │ outcome
                                         ▼
                                  ┌──────────────┐
                                  │    Critic    │
                                  └──────────────┘
                                         │
                       stuck / contradicted ─▶ Failure LLM
                                         │           │
                                         ▼           │
                                   (re-plan) ◀──────┘
                                                  recovery goal
```

| # | Stage | Lives in | Job | Runs on |
|---|---|---|---|---|
| 1 | Perception | `BotPlayer.cs` (existing snapshots) | Read the world; emit facts about what is visible right now | landblock thread, fast tick |
| 2 | Blackboard | `Bots/Brain/Blackboard.cs` (new) | Hold the bot's complete belief state; persist durable parts | landblock thread, all writes |
| 3 | Goal Stack | `Bots/Brain/GoalStack.cs` (new) | Hold the ordered set of objectives; push, pop, interrupt | landblock thread, all mutations |
| 4 | Planner | `Bots/Brain/Planner.cs` + per-goal planner files (new) | From (top goal, blackboard), emit the immediate next executor op | **landblock thread, every fast tick** |
| 5 | Executor | `BotPlayer.cs` (existing `ExecutePlanOp*`) wrapped by `IBotActionAdapter` | Run one executor op against the live `Player` API; track in-flight op for continuity | landblock thread |
| 6 | Critic | `Bots/Brain/Critic.cs` (new) | Detect stuck / contradicted / oscillation / reactive vitals / repeated failure | landblock thread, fast tick |

The Deliberator (LLM or scripted) and the Dialog LLM and the Failure
LLM are **services**, not stages. They run async on the threadpool
and post results back via `_pendingInputs`.

## 4. The two-tick cadence

Both ticks logically share `OnBrainTick`; the deliberation-tick work
is async, scheduled from the fast tick.

### 4.1 Fast tick — every `OnBrainTick`

Target wall-clock budget per tick: < 5 ms. Fully deterministic. No
LLM calls. No I/O.

1. **DrainInputs** — apply any continuations posted by background
   work (LLM results, async pathfinding) under the per-bot lock.
   Each continuation runs the §5.8 merge rule before mutating the
   Blackboard or Goal Stack.
2. **Perception** — refresh the volatile section of the Blackboard
   from the existing snapshot methods (visible NPCs / doors /
   items / portals / cell / vitals / inventory / equipment).
3. **Critic** — run the five checks (§7) against the Blackboard
   and the in-flight Executor op. The Critic may mark the top goal
   `Stuck`, mark a `RepeatedFailure` cap reached, push an interrupt
   goal for a reactive threshold, or pop a goal `Done` by side-
   effect. The Critic does **not** call the LLM directly; it sets
   state that the next deliberation request will observe.
4. **Planner** — read the top of the Goal Stack and the Blackboard;
   emit `PlannerStep` (either a new `Op`, `Op.ContinueCurrent` if
   the in-flight op still serves the goal, or
   `Op.NoneWithReason(...)`). The Planner runs *every* fast tick.
5. **Executor** — apply the `PlannerStep`. If `ContinueCurrent`,
   no-op. If a new `Op`, cancel any conflicting in-flight motion
   (using existing `CancelBotMoveTo`) and start the new op.
   Records `CurrentOp` in the volatile Blackboard.
6. **Schedule deliberation** if any of these are true and no
   deliberation request for this bot is currently in flight (see
   §4.3): Goal Stack empty, top goal `Stuck` and no proposed
   recovery, `LastDeliberationAt` older than
   `MaxIdleDeliberationInterval`.

### 4.2 Deliberation tick — async, scheduled

Target wall-clock budget per call: 1–5 s. May call the LLM. **Never
mutates the Blackboard or Goal Stack directly from the worker
thread.** Instead, posts a continuation to `_pendingInputs` that
applies the merge rule on the next fast tick.

Triggers:

- A request scheduled from the fast tick (Goal Stack empty, top goal
  `Stuck`, or `MaxIdleDeliberationInterval` heartbeat).
- An NPC tell ingested via `IngestDialog`. This path already runs
  through `FlushAndCompile`; the worker is now a Dialog-mode
  Deliberator instead of a `Plan` compiler.

### 4.3 Threading model

Concrete mechanism, replacing the "brain thread pool" phrasing in
revision 1.

- **One async slot per (mode, mode-specific key) per bot.** Each
  mode has its own slot table keyed by the column 2 of §4.4. Slot
  contents are gated by `Interlocked.Exchange` against the slot
  pointer (or, for per-key modes, against a `ConcurrentDictionary`
  entry created and removed under that gate). Concretely:
  - Dialog: `ConcurrentDictionary<uint, DialogRequest>`
    `_dialogSlotsByNpc`. The key is the NPC source guid; this
    preserves today's per-NPC pending coalesce buffer behavior
    (§2.2). Two NPCs talking at once produce two independent slots,
    not eviction. Within a single NPC's slot, newer dialog replaces
    pending.
  - Deliberate: one slot `_deliberateRequestSlot`. The key is the
    bot itself. New requests dropped while one is in-flight (bot is
    already thinking).
  - Failure: `ConcurrentDictionary<Guid, FailureRequest>`
    `_failureSlotsByGoal`. The key is the failed goal id. Recovery
    on the same goal is dropped if pending; recovery on a different
    goal opens its own slot.
  - Pathfinding: `ConcurrentDictionary<(uint fromCell, uint toCell),
    PathRequest> _pathSlots`. New request for the same pair is
    dropped if pending; different pair opens its own slot.

  Submission reads/writes the appropriate slot via
  `Interlocked.Exchange` for single-slot modes, or
  `TryAdd` / `TryUpdate` under a per-key gate for dictionary modes.
- **Monotonic request id.** Each accepted submission gets the next
  `Interlocked.Increment(ref _nextRequestId)`. The id is captured
  by the continuation. The merge rule (§5.8) rejects the
  continuation if the slot's current id no longer matches.
- **Cancellation.** Each `BotPlayer` owns a single
  `CancellationTokenSource _brainCts`. It is `Cancel()`-ed in the
  teardown path (`ForceLogoff`, despawn, server stop). All LLM /
  pathfinding tasks receive `_brainCts.Token`. Continuations
  short-circuit if `_brainCts.IsCancellationRequested`.
- **Per-bot read/write coordination.** The Blackboard exposes a
  single `ReaderWriterLockSlim` (`_bbLock`). Writers on the
  landblock thread take the write lock. The DrainInputs path is
  the only writer for fields that LLM continuations touch
  (durable beliefs, Goal Stack). Reads from continuations *for
  digest construction* are done on the landblock thread before
  the request is submitted, packaged into the request payload, and
  travel with it — the worker thread never reaches back into the
  Blackboard.

### 4.4 Single-flight summary

| Mode | Slot semantics | Coalescing rule |
|---|---|---|
| Dialog | per-NPC source guid | Replace pending with newer (later dialog wins for that NPC) |
| Deliberate | per-bot | Drop new if pending; bot is already thinking |
| Failure | per-bot, per (failed goal id) | Drop new if pending for same goal |
| Pathfinding | per-bot, per (from-cell, to-cell) tuple | Drop new if pending for same query |

## 5. The Blackboard

The Blackboard is the bot's full belief state. Everything the brain
knows about the world flows through it. The Planner reads only from
it; the Executor writes outcomes only into it; the LLM consumes
serialized snapshots of it.

### 5.1 Schema (v1, illustrative)

Final field set lands in the implementation PR; this is the contract
the Planner / Critic / LLM modes consume.

```csharp
public sealed class Blackboard
{
    public uint BotGuid;
    public int  SchemaVersion;

    // ============ VOLATILE (rebuilt every fast tick) ============
    public Vector3            Position;
    public CellRef            CurrentCell;
    public CombatMode         CombatMode;
    public Vitals             Vitals;                  // HP/Stam/Mana, max + current
    public Stats              Stats;                   // attributes + skills + level + XP + training credits
    public List<NpcSighting>    VisibleNpcs;
    public List<MobSighting>    VisibleMobs;           // hostile / disposition / hp%
    public List<DoorSighting>   VisibleDoors;          // open/closed/locked
    public List<PortalSighting> VisiblePortals;        // dest hint
    public List<ItemSighting>   VisibleItems;          // container vs ground
    public List<CorpseSighting> VisibleCorpses;        // decay timer
    public List<CellRef>        AdjacentCells;
    public List<CellRef>        RecentlySeenCells;
    public Op?                  CurrentExecutorOp;     // in-flight op, for continuity
    public PathPlan?            ActivePathPlan;        // cached pathfinding output
    public PendingNpcPrompt?    PendingNpcPrompt;      // mid-dialog InqYesNo etc.

    // ============ INVENTORY / EQUIPMENT (refreshed on change) ============
    public List<ItemStack>      Inventory;
    public List<EquippedItem>   Equipment;
    public List<SpellRef>       SpellBook;
    public int                  PackSpaceFree;

    // ============ DURABLE (persisted; survives reboot) ============
    public Dictionary<string, NpcMemory>      KnownNpcs;       // by name
    public Dictionary<string, PlayerMemory>   KnownPlayers;    // by name
    public Dictionary<string, VendorMemory>   KnownVendors;    // by NPC name
    public Dictionary<uint,   CellMemory>     KnownCells;      // by cellId
    public Dictionary<string, QuestMemory>    ActiveQuests;    // by quest name
    public List<QuestRef>                     CompletedQuests;
    public List<TypedFact>                    Facts;           // closed FactKind enum (see §5.2)
    public List<FailureRecord>                RecentFailures;  // executor op + reason + retry count + ts
    public FellowshipState                    Fellowship;
    public AllegianceState                    Allegiance;
    public LifestoneBinding?                  Lifestone;       // current bind point
    public CorpseMemory?                      OwnCorpse;       // for death recovery (M2+)
    public List<InteractionRecord>            InteractionLog;  // bounded recent player/NPC interactions

    // ============ TRANSIENT ============
    public DateTime  LastDeliberationAt;
    public DateTime  LastNpcTellAt;
    public DateTime  LastFastTickAt;
}
```

Every durable record below has a `ConfidenceTimestamp`:

```csharp
public abstract class Memory {
    public DateTime ConfidenceTimestamp;   // last observed / written
    public string   Source;                // "perception" / "dialog" / "rehydrate"
}
public sealed class NpcMemory     : Memory { /* name, weenie hint, last cell, last dialog facts, etc. */ }
public sealed class PlayerMemory  : Memory { /* name, last seen, fellowship, allegiance role, etc. */ }
public sealed class VendorMemory  : Memory { /* npc name, location hint, known categories, etc. */ }
public sealed class CellMemory    : Memory { /* cellId, doors, portals, named landmarks */ }
public sealed class QuestMemory   : Memory { /* quest name, last stage bits, NPC chain, deadlines */ }
```

The Planner declares a minimum freshness per goal type (e.g.
`AcquireItem` requires the inventory snapshot to be < 2 fast ticks
old; `TalkTo` requires `KnownNpcs[name].ConfidenceTimestamp` within
60 s for the "walk to last-known" branch). Stale beliefs trigger
an immediate re-perception request before the Planner emits an op,
or a `RecoverFromStuck` push if perception cannot refresh them.

### 5.2 Typed facts, not free-form beliefs

Revision 1 had `Beliefs: List<FactBelief>` (free-form). That is a
typed-coupling footgun: the LLM emits strings, the Planner switches
on strings, and the schema migrates per fact kind. Revision 2:

```csharp
public enum FactKind {
    DoorState,            // (objectGuid, open|closed|locked)
    QuestStageBit,        // (questName, bitIndex, set)
    ItemHeld,             // (itemName, count)
    ItemDelivered,        // (npcName, itemName, count)
    NpcDisposition,       // (npcName, friendly|neutral|hostile)
    KeywordLearned,       // (npcName, keyword)
    PortalUnlocked,       // (portalName)
    SpellLearned,         // (spellId)
    TitleGranted,         // (titleId)
    // Add explicitly per phase; new kinds require a schema bump.
}

public sealed class TypedFact {
    public FactKind Kind;
    public string   Key;          // dedupe key within Kind
    public object?  Payload;
    public DateTime ObservedAt;
    public string   Source;       // "DialogLLM" | "Perception" | "Executor" | "Critic"
}
```

The Planner can switch on `FactKind` but only the closed enum. New
fact shapes are explicit schema changes, reviewed alongside the
Planner branch that consumes them.

### 5.3 Persistence

**Decision:** piggyback on `biota_properties_string` (and
`biota_properties_int64` for counters and timestamps) via new
`PropertyString` enum values, one per durable belief category:

| PropertyString | Payload |
|---|---|
| `BotBlackboardSchemaVersion` (int64) | int |
| `BotKnownNpcsJson` | JSON map name → NpcMemory |
| `BotKnownPlayersJson` | JSON map name → PlayerMemory |
| `BotKnownVendorsJson` | JSON map name → VendorMemory |
| `BotKnownCellsJson` | JSON map cellId → CellMemory |
| `BotActiveQuestsJson` | JSON map name → QuestMemory |
| `BotCompletedQuestsJson` | JSON array |
| `BotFactsJson` | JSON array of TypedFact |
| `BotRecentFailuresJson` | JSON array, bounded length |
| `BotFellowshipJson` | JSON object |
| `BotAllegianceJson` | JSON object |
| `BotLifestoneJson` | JSON object |
| `BotOwnCorpseJson` | JSON object |
| `BotInteractionLogJson` | JSON array, bounded length |
| `BotGoalStackJson` | JSON ordered list of Goal |

This rides the existing `SavePlayerToDatabase()` and
`RehydratePersistedBotPlayers` paths with zero new ShardDbContext
work. Trade-off: rows are read/written as a blob, not query-able
from SQL — acceptable for v1; revisit at M5+ if `/botdirector
inspect` wants SQL-side joins.

**Cadence:** write on Goal Stack mutations (push, pop, interrupt),
on bot logoff (`SavePlayerToDatabase` already), on `_brainCts`
cancellation, and at most every 30 s via a throttled write timer
(coalesces multiple in-tick mutations into one row write).

**Migration:** a `MigrationRegistry` runs on rehydrate when
`SchemaVersion` mismatches. Additive-only changes (new optional
field) cost nothing. Renames and removals get an explicit migration
step. The registry is in `Bots/Brain/Migrations/` with one file per
version bump.

**Constraints (the in-fork code work this implies):**

- `PropertyString` is a `ushort` enum defined in
  `ACE.Entity/Enum/Properties/PropertyString.cs`. Adding new values
  is a code change in `ACE.Entity` (not `ACE.Server`); the bot fork
  claims the contiguous range **9100–9120** for brain payloads so
  rebases against upstream do not collide with custom strings in
  the existing 9001+ range used elsewhere in the fork.
- `biota_properties_string.value` is `TEXT` (effective ≤ 64 KB per
  row in MariaDB / MySQL). Each JSON payload above carries a hard
  cap: `KnownNpcs`/`KnownPlayers` cap at 256 entries each;
  `InteractionLog` and `RecentFailures` cap at 100 entries each;
  `Facts` caps at 500 entries; `KnownCells` caps at 1024 entries.
  Caps are documented inline in the schema and enforced by the
  Blackboard writer (eviction policy = oldest by
  `ConfidenceTimestamp`, except for `Facts` which evict by lowest
  `FactKind` priority then oldest). Hitting a cap emits a
  `bot.brain.bb.cap_hit` metric (§14).
- `SavePlayerToDatabase` rewrites the bot's full property set per
  save, not delta-encoded. The 30 s throttle bounds amortized cost
  at one full re-serialize per window per bot. At ~50 bots × ~30 KB
  payload that is ~1.5 MB of writes per 30 s = ~50 KB/s sustained,
  well inside the shard DB envelope. Revisit at M5+ if bot counts
  rise an order of magnitude.

### 5.4 Concurrency

- All Blackboard writes happen on the landblock thread.
- LLM continuations post writes via `_pendingInputs`; the
  DrainInputs path runs the merge rule first.
- Reads from the Planner / Critic are on the landblock thread,
  uncontested with writers (single-threaded apart from the merge
  point).
- The `_bbLock` (ReaderWriterLockSlim) exists for the rare case
  where the JSON serializer runs on a background thread for the
  durable-write cadence (§5.3); the snapshot is taken under the
  write lock and serialized off-thread under the read lock.

## 6. The Goal Stack

A typed structure of `Goal` records with parent-id nesting and
priority-ordered leaf selection. **Flat list with parent-id**, not
stack-of-stacks (revision 1's open question is resolved here).

### 6.1 Goal shape

```csharp
public sealed class Goal {
    public Guid    Id;
    public GoalType Type;            // closed enum, see §6.5
    public ImmutableDictionary<string,object> Args;
    public Guid?   ParentId;
    public int     Priority;         // higher = more urgent
    public GoalStatus Status;        // Pending / Active / Suspended / Stuck / Done / Abandoned
    public DateTime CreatedAt;
    public DateTime? LastTransitionedAt;
    public string  Source;           // "DialogLLM" | "Deliberation" | "Failure" | "Critic" | "Script" | "Admin"
    public List<string> Rationale;
    public string  LogicalKey;       // see §6.2
    public int     ResumeOrder;      // for interrupt/resume (§6.7)
}
```

### 6.2 Identity and dedupe

A goal's `LogicalKey` is computed from `Type` plus a canonical
projection of `Args` (e.g. `TalkTo|npcName=Samuel`,
`AcquireItem|itemName=Leather Gauntlets|count=1`). `Push(goal)`:

1. Compute `LogicalKey`.
2. If a non-terminal goal with the same key already exists in the
   stack (Status in {Pending, Active, Suspended, Stuck}), return
   that existing goal. Do not add a duplicate.
3. Otherwise add the new goal.

This handles duplicate NPC tells, idempotent re-deliberations after
merge-rule rejections, and `/botdirector setgoal` issued twice.

### 6.3 Status transitions

```
   ┌─────────┐  selected by Top()  ┌────────┐  Critic stuck  ┌───────┐
   │ Pending │ ──────────────────▶ │ Active │ ─────────────▶ │ Stuck │
   └─────────┘                     └────────┘                └───────┘
        ▲                            │ │                          │
        │ higher-prio push           │ │ Critic Done/SideEffect   │ Failure LLM
        │   suspends Active          │ ▼                          │   abandon
        │                            │┌────────┐                  ▼
   ┌──────────┐  Pop(Done) of        ││  Done  │              ┌───────────┐
   │Suspended │◀─interrupt resumes   │└────────┘              │ Abandoned │
   └──────────┘  most recent sibling │                        └───────────┘
        │                            │
        └────────────────────────────┘
```

Edges in words (because ASCII state machines are easy to misread):

- `Pending` → `Active`: §6.4 `Top()` selects the highest-priority
  leaf at the start of a fast tick.
- `Active` → `Stuck`: Critic Stuck/RepeatedFailure check (§8).
- `Active` → `Done`: Critic SideEffectDone or Executor success.
- `Active` → `Suspended`: a higher-priority `Push` interrupts it
  (§6.6). The leaf's `ResumeOrder` is recorded.
- `Suspended` → `Active`: the interrupt above it `Pop`s with `Done`;
  the most-recently-suspended sibling at the same parent level is
  selected by `Top()` on the next fast tick.
- `Stuck` → `Abandoned`: Failure LLM returns an abandon recovery.

### 6.4 Operations

- `Push(goal)` — dedupe per §6.2, add. If the new goal's priority
  exceeds the current `Active` leaf's priority by ≥ `InterruptDelta`
  (config; default 100), the current leaf transitions to `Suspended`
  with its `ResumeOrder` set, and the new goal becomes `Active` on
  the next fast tick (`Top()` will select it).
- `Pop(goalId, status)` — closes the goal with `Done` or `Abandoned`.
  All descendants (by `ParentId`) cascade to `Abandoned`. The cascade
  takes effect at the **start of the next fast tick**, never mid-tick;
  the in-flight Executor op for the abandoned leaf is cancelled at
  the same boundary.
- `Top()` — returns the highest-priority `Active` leaf. Ties broken
  by (a) parent depth (deeper wins; a child interrupt outranks its
  parent) and (b) `CreatedAt` (older wins). Returns `null` if the
  stack contains no `Active` leaf; the fast tick then schedules
  deliberation.

### 6.5 Goal types

Scope: explicitly M1–M2. M3 and M4 add types per their phase plan.
Each type maps onto the closed op vocabulary from
[`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md).

M1 (academy traversal):

| Type | Args | Planner emits |
|---|---|---|
| `TalkTo` | `npcName`, optional `npcGuid` | `GoTo` → `Use(npc)` |
| `AcquireItem` | `itemName`, `count`, optional `sourceHint` | `GoTo`(item) → `PickUp`, or interrupt with `Hunt` |
| `DeliverItem` | `npcName`, `itemName`, `count` | `GoTo`(npc) → `GiveItem` |
| `EquipBest` | `slot` or `*` | one or more `Equip` from inventory, archetype-weighted |
| `UseObject` | `objectName` (door, lever, statue) | `GoTo`(obj) → `Use(obj)` |
| `OpenContainer` | `containerName` | `GoTo`(container) → `Use(container)` |
| `DropItem` | `itemName`, `count` | inventory `Drop` |
| `UsePortal` | `portalName` or `destHint` | `GoTo`(portal) → `Use(portal)` |
| `Explore` | `cellId` or `direction` | series of `GoTo` into adjacent unseen cells |
| `RespondToTell` | `playerName`, `prompt` | scripted chat (no executor op) |
| `RecoverFromStuck` | failure record id | calls Failure LLM, pushes recovery child |
| `CompleteAcademy` | (none) | high-level wrapper; pushes children from Dialog facts |

M2 additions (first outdoor quest + combat loop):

| Type | Args | Planner emits |
|---|---|---|
| `AttuneLifestone` | optional `lifestoneName` | `GoTo`(lifestone) → `Use` |
| `Recall` | `kind` (lifestone, primary portal) | `Use`(recall) |
| `Hunt` | `mobName` or `level<=N`, `count` | combat loop ops |
| `Buff` | `spellId` (self) | `Cast` |
| `RestockMana` | (none) | wait or food/comp use |
| `LootCorpse` | corpse id (decay-aware) | `GoTo` → `Loot` |
| `SellLoot` | (none) | route to known vendor; `Sell` |
| `BuySupplies` | category, budget | route to vendor; `Buy` |
| `FleeFrom` | `mobName` or `hpThreshold` | interrupt; `GoTo`(safe) |
| `MarkLandmark` | name | Blackboard write only |

M3 / M4 types (`Follow`, `JoinFellowship`, `LeaveFellowship`,
`InviteToFellowship`, `SwearAllegiance`, `BreakAllegiance`,
`AssessItem`, `TrainSkill`, `WaitFor`) are added when those
milestones start.

### 6.6 Interrupts

`Push` with a high-priority goal (`Interrupt` source flag) suspends
the current `Active` leaf instead of competing with it. Reactive
Critic checks (low HP, full pack, hostile-adjacent) push interrupts.
The Goal Manager records the suspended goal's `ResumeOrder` so it
resumes correctly after the interrupt resolves.

### 6.7 Resume

`Pop(interruptId, Done)` triggers the Goal Manager to look at the
most-recently suspended sibling at the same parent level. If found,
its status transitions back to `Active` on the next fast tick. The
Executor sees its `CurrentOp` is null (interrupt cancelled it) and
the Planner re-derives the next op for the resumed leaf.

### 6.8 Persistence

Persisted alongside the Blackboard (§5.3, `BotGoalStackJson`). On
rehydrate, all `Active` and `Suspended` goals revert to `Pending`
**and the first fast tick after rehydrate promotes the highest-
priority leaf to `Active` by selecting it via the same tie-break
rules as §6.4 `Top()`.** Deliberation is **not** required to bring
the stack back to life: with all leaves Pending, `Top()` would
otherwise return null and trigger deliberation, leaving the stored
plan orphaned. The promotion step (call it `ReactivateAfterRehydrate`)
runs as the first action of `AgentLoop` for one tick when the bot
has just rehydrated, then the loop returns to normal §4.1 cadence.
A subsequent deliberation tick may still preempt the promoted goal
if beliefs disagree with it; that path is the same as any normal
interrupt push.

## 7. The Planner

Pure C# code. Given `(topGoal, blackboard)` it emits one of
`(nextOp, opArgs)`, `Op.ContinueCurrent` (in-flight op still serves
the goal), or `Op.NoneWithReason(reason)`. Runs every fast tick.

### 7.1 No executor-plan cache; cached pathfinding

The Planner has no cache for executor ops; it derives the next op
from the current Blackboard each tick. Cost is bounded (a switch on
`GoalType` + a few O(visible) scans). Target: < 1 ms per tick.

**Pathfinding plans are cached** in the volatile Blackboard as
`ActivePathPlan` because A* over `IPathfindingService` exceeds the
fast-tick budget. The Planner emits `Op.GoTo(target)` and the
Executor's `IBotActionAdapter` (§10) is responsible for:

1. Reusing `ActivePathPlan` if it still leads from current cell to
   target cell.
2. Otherwise submitting a pathfinding request via the same
   `_pendingInputs` async pattern as LLM calls, with
   `ActivePathPlan` set to `Pending` until the result arrives.
3. Invalidating `ActivePathPlan` on cell change, door-state
   change, or Critic `Stuck` / `Contradicted`.

The Planner is unaware of caching; it just emits `Op.GoTo`. The
Executor adapter handles continuity (§7.3).

### 7.2 Pseudocode

```csharp
public PlannerStep Plan(Goal top, Blackboard bb)
{
    switch (top.Type)
    {
        case GoalType.TalkTo:
            var npcName = (string)top.Args["npcName"];
            var npc = bb.VisibleNpcs.MatchByName(npcName);
            if (npc != null)
            {
                if (npc.DistanceMeters > InteractRange)
                    return PlannerStep.Op(Op.GoTo(npc.Position, npc.Cell));
                return PlannerStep.Op(Op.Use(npc.Guid));
            }
            var known = bb.KnownNpcs.GetValueOrDefault(npcName);
            if (known?.LastSeenCell is { } cell &&
                bb.AgeOf(known) < KnownNpcStalenessLimit)
                return PlannerStep.Op(Op.GoTo(known.LastSeenPosition, cell));
            return PlannerStep.NoneWithReason("npc not visible, no fresh last-known location");

        case GoalType.AcquireItem: ...
        case GoalType.UseObject:   ...
        case GoalType.UsePortal:   ...
        // one branch per type in §6.5; each lives in its own file under Bots/Brain/Planners/
    }
}
```

### 7.3 Executor continuity contract

The Executor adapter, not the Planner, owns motion continuity.

- `CurrentOp` is set on every `Start(op)` and cleared on every
  `OnComplete` or `Cancel`.
- When the Planner emits a new `Op`, the adapter compares it to
  `CurrentOp`:
  - Same `Op.Kind`, same target → `ContinueCurrent`, no-op.
  - Different target or different kind → cancel current motion via
    `CancelBotMoveTo`, start the new op.
- When the Planner emits `Op.ContinueCurrent` explicitly, the
  adapter does nothing.
- When the Planner emits `Op.NoneWithReason`, the adapter does
  nothing; the Critic's next pass will likely mark the goal `Stuck`
  if this persists.

This eliminates the revision-1 motion-thrashing risk while keeping
the Planner stateless.

## 8. The Critic

Runs every fast tick. Five check classes:

| Class | Trigger | Action |
|---|---|---|
| `Stuck` | Position delta < `MinProgressMeters` (0.5 m) over `StuckWindowSeconds` (4 s) while `CurrentOp` is motion-class | Mark top goal `Stuck`; record `FailureRecord` |
| `Contradicted` | `CurrentOp` targets an object not in Perception for 2 consecutive fast ticks | Mark top goal `Stuck`; record `FailureRecord` |
| `Oscillation` | Ring buffer of last N (cellId, opKind) shows repeated A→B→A→B | Mark top goal `Stuck`; tag failure as `Oscillation` |
| `Reactive` | Vitals below threshold (HP < `LowHpFrac`, mana < `LowManaFrac`, pack space < 1), or hostile mob within `EngageRadius` | Push interrupt goal (`Recall`, `RestockMana`, `DropItem`, `Hunt` or `FleeFrom`) |
| `SideEffectDone` | Goal predicate satisfied without action (item picked up from elsewhere, NPC walked into range) | Pop goal `Done` |
| `RepeatedFailure` cap | Same (parent-goal, failure-kind) tuple recorded ≥ `MaxRetriesPerGoal` (default 3) | Pop parent goal `Abandoned`; deliberation runs to pick a different parent |

The Critic does not call the LLM. It mutates the Goal Stack
synchronously on the fast tick (push interrupt, pop Done, mark
Stuck). The next deliberation tick observes the state.

**Opportunity preemption** is not a Critic check; it is a property
of deliberation. When the Deliberator runs (heartbeat or event), it
may propose a goal with priority high enough to interrupt the
current `Active` leaf. This is the only path by which "a better
option appeared" is acted on while a non-Stuck goal is running.

## 9. The LLM, three modes (and the Deliberator interface)

### 9.1 Mode split rationale

Three modes is a deliberate partition along **input shape**, not
along output shape. All three emit goals or facts.

| Mode | Input shape | Output |
|---|---|---|
| Dialog | NPC dialog text + visible NPCs + small Blackboard digest | `{ facts[], goals[], confidence, unknowns[] }` |
| Deliberation | Full Blackboard digest + archetype profile | `{ goal, rationale, alternatives[] }` |
| Failure | Failed goal + FailureRecord + relevant Blackboard slice | `{ recovery, reason, abandon: bool }` |

The split is justified because each mode has a distinct
**system-prompt shape** and **few-shot example set**, even though
the output schema overlaps. Two modes (collapse Deliberation +
Failure into one with a `trigger` discriminator) is the strongest
alternative; we chose three because the prompt-engineering
trade-off favors separate corpora that can be fine-tuned
independently per the corpus design in
[#53](https://github.com/darinh/ac-ai-players/issues/53).

### 9.2 Model fit and prompt cost budget

Verified on `qwen3:8b` (current model in
`OllamaQuestCompiler.cs:43`) on RTX 4090 / 24 GB. Numbers below
split prefill (input processing) from decode (output generation)
because conflating them oversells the latency story:

| Mode | Input tokens | Output tokens | Prefill (s) | Decode (s) | Wall-clock budget | Notes |
|---|---|---|---|---|---|---|
| Dialog | 800–1500 | 100–300 | 0.5–1.5 (~1000 tok/s prefill) | 1.5–5 (~60 tok/s decode) | 2–7 s typical, 8–18 s worst | Already in production; Dialog harness 6/6 is the measured baseline |
| Deliberation | 1500–3000 | 100–250 | 1–3 | 1.5–4 | 3–8 s typical, ≤ 16 s worst | **Measured worst case t.b.d.** — Phase 7 cutover requires running the 5-scenario Deliberation harness end-to-end and recording the p95 wall-clock before flipping the flag |
| Failure | 800–1500 | 100–200 | 0.5–1.5 | 1.5–3 | 2–5 s typical, ≤ 12 s worst | Smallest input; should be fastest |

The decode rate (~55–75 tok/s on a 4090 for `qwen3:8b` Q4) is the
binding constraint; prefill is 10–30× faster and rarely the
bottleneck. Earlier "100 tok/s" prose was a hand-waved average and
is replaced by the per-stage numbers above.

Mitigations if Deliberation runs hot:

- Compress Blackboard digest to a fixed-size schema (capped
  collections).
- Stream the response and short-circuit once the goal is parseable.
- Fall back to scripted Deliberator (§9.4) for archetypes that don't
  need LLM reasoning (Grinder, Buffbot per
  [`../brain-providers.md`](../brain-providers.md)).
- Escalate to `gpt-oss:20b` when qwen3 fails the schema validator.

### 9.3 Output schemas

```csharp
public sealed class FactsAndGoals {
    public List<TypedFact>     Facts;
    public List<GoalCandidate> Goals;     // closed-vocab via GoalType (§6.5)
    public float               Confidence;
    public List<string>        Unknowns;  // dialog fragments LLM couldn't classify
}

public sealed class DeliberationResult {
    public GoalCandidate Goal;            // closed-vocab; rejected otherwise
    public string        Rationale;
    public List<GoalCandidate> Alternatives;
}

public sealed class FailureRecovery {
    public GoalCandidate? Recovery;       // null if Abandon=true
    public string         Reason;
    public bool           Abandon;
}

public sealed class GoalCandidate {
    public GoalType Type;
    public ImmutableDictionary<string,object> Args;
    public int      Priority;
    public string?  ParentLogicalKey;     // for nesting
}
```

These shapes land in Phase 1 alongside the harness, not Phase 6.
The Planner / Critic / Goal Stack can write against them with stub
implementations from day one.

### 9.4 `IDeliberator` interface

Promoted from revision 1's open question.

```csharp
public interface IDeliberator {
    Task<DeliberationResult> DeliberateAsync(
        BlackboardDigest digest, ArchetypeProfile profile,
        CancellationToken ct);
}

// Implementations:
//  - LlmDeliberator (qwen3:8b via Ollama, this design's default)
//  - ScriptedDeliberator (decision-table per archetype, no LLM, no
//    cost; chosen by Grinder / Buffbot per
//    ../brain-providers.md)
```

Same pattern for `IDialogInterpreter` and `IFailureAnalyst`.
Archetype config selects one implementation per service at bot
init time. The `BotBrain.UseAgentLoop` flag (§13) selects the
agent-loop pipeline; the deliberator interface selects what runs
inside it.

### 9.5 Merge rule

A request submitted at fast tick `t0` returns at `t1 > t0`. Between
those times the fast tick has continued running. The continuation
posts an `Action<double>` to `_pendingInputs`; when DrainInputs
applies it on a later fast tick:

1. **Cancellation check:** if `_brainCts.IsCancellationRequested`,
   discard.
2. **Request-id check:** if the slot's current id no longer matches
   the captured id, discard.
3. **State precondition check:** for Dialog mode, the dialog hash
   must still match a pending coalesce entry; for Deliberation
   mode, the Goal Stack must still be empty or top still `Stuck`;
   for Failure mode, the specific failed goal must still be in
   `Stuck`.
4. **Apply:** push proposed goals (dedupe per §6.2), upsert facts
   into the Blackboard, record `LastDeliberationAt`.

## 10. The Executor adapter (`IBotActionAdapter`)

The bridge between the Planner's stateless ops and the existing
stateful `ExecutePlanOp*` methods on `BotPlayer`.

### 10.1 Interface

```csharp
public interface IBotActionAdapter {
    Op?          CurrentOp { get; }
    ExecuteResult Execute(Op op);             // start or continue
    void         Cancel();                    // cancels CurrentOp
    void         OnMoveComplete(WeenieError result);  // motion callback bridge
}

public interface IBotWorldView {
    // Read surface used by IBotActionAdapter implementations for
    // in-flight executor predicates (proximity, line of sight,
    // adjacency tests on the live or fake landblock). The Planner
    // reads from Blackboard, not from this interface.
    bool   IsInRangeOf(uint targetGuid, float range);
    float  DistanceTo(uint targetGuid);
    bool   IsCellAdjacent(uint fromCell, uint toCell);
}
```

### 10.2 Live implementation

`BotPlayerActionAdapter : IBotActionAdapter` wraps the existing
`ExecutePlanOpTalkTo / GoTo / Collect / GiveItem / ReturnTo`
methods. The wrapping is **not purely mechanical**; two ops require
decomposition rather than direct delegation:

| Op | Today's method | Adapter strategy |
|---|---|---|
| `Use(npcGuid)` | `ExecutePlanOpTalkTo` does find-by-name + walk-to-object + use inline | **Decompose.** Adapter calls only the use leaf (`TurnToObject` + `OnActivate`). Find-by-name and walk-to are now Planner responsibilities (`GoTo` emitted as a prior op). |
| `GoTo(targetGuid)` | `NavRequestWalkToObject` exists; door-driven exploration uses `NavRequestExploreThroughDoor` | **Wrap.** Live adapter calls `NavRequestWalkToObject` for object targets and `NavRequestExploreThroughDoor` for door crossings. |
| `GoTo(position, cell)` | **No existing primitive.** Today's nav is object-targeted | **New primitive needed.** Either (a) Planner restricts `GoTo` to object-targeted form for M1 (preferred — every M1 destination is a known sighting), deferring position-only nav to M2+ alongside outdoor pathfinding; or (b) add `NavRequestWalkToPosition(Vector3, CellRef)`. Phase 4 (Planner) picks option (a); Phase 10 may revisit. |
| `Give(npcGuid, itemGuid)` | `ExecutePlanOpGiveItem` | **Wrap.** Direct delegation. |
| `PickUp(itemGuid)` | `ExecutePlanOpCollect` | **Wrap.** Direct delegation. |
| `Use(doorGuid)` | inside `ExecutePlanOpGoTo` door cross path | **Decompose.** Adapter exposes a discrete `Use` for doors so the Planner can emit door open + cell traversal as two ops. |

`CurrentOp` tracking is added on the adapter. Where decomposition is
required, the existing inline method bodies are split into private
helpers on `BotPlayer` (e.g. `WalkToAndUse` → `WalkTo` + `TurnAndUse`)
in Phase 4 before the Planner stops calling the composite paths.
The `ClassicLoop` strategy keeps the original composite calls intact,
so live behavior on `bots/botplayer-spike` is unchanged until the
flag flips.

### 10.3 Harness implementation

`FakeBotActionAdapter : IBotActionAdapter` records every `Execute`
call and emits a configurable result (success / failure / in-
progress) to drive Planner + Critic + Goal Stack tests without
touching the world. Harness scenarios assert on the call log.

This is the §11 testing boundary.

## 11. Testing strategy

Three tracks, in order of feedback speed.

| Track | Speed per case | What it covers | Where |
|---|---|---|---|
| Planner / Critic / Goal Stack / Blackboard / Deliberator unit tests | < 100 ms | Pure-C# correctness of the agent loop, with `FakeBotActionAdapter` and `IBrainLLM` stubs | `Source/ACE.Server.Tests` (existing **MSTest**) |
| Adapter integration tests | ~1–3 s | `BotPlayerActionAdapter` against a minimal in-memory `Player` API fake covering proximity, container search, motion stubs. Catches executor regressions invisible to the unit harness | `Source/ACE.Server.Tests/Bots/Adapter/` (new folder) |
| Prompt harness | 1–10 s per case | LLM mode correctness; one harness per mode (Dialog / Deliberation / Failure) | `files/prompt-harness/` (existing; extended) |
| Live smoke | minutes | Real pathfinding, motion, network, LLM. The E9-style live pass and `/botdirector poke` end-to-end | live |

### 11.1 What the unit harness does NOT cover

- Real `Player` / `WorldObject` / `PhysicsObj` interactions. A
  regression in `BotPlayerActionAdapter`'s call into
  `HandleActionUseItem` will not surface in the unit harness.
- Real motion semantics (cancellation timing, `OnMoveComplete`
  ordering without sequence id per the m2 design).
- Real LLM behavior; the unit harness uses stubs.

Adapter tests (the second tier) cover the first two. The prompt
harness covers the third. Live smoke covers what no harness can.

### 11.2 Initial scenario corpus (M1 unit harness)

| ID | Setup | Assert |
|---|---|---|
| `talkto-same-cell` | Samuel visible at 4 m | `Use(Samuel)` within 5 fast ticks |
| `talkto-far-cell` | Samuel visible at 30 m | First op is `GoTo(Samuel.Position)` |
| `talkto-not-visible-known-fresh` | Samuel not visible, KnownNpcs[Samuel] < 60 s old | First op is `GoTo(known cell)` |
| `talkto-not-visible-stale` | KnownNpcs[Samuel] 5 min old | Goal `Stuck`; Failure mode requested |
| `talkto-dedupe` | Push `TalkTo(Samuel)` twice | Stack contains one entry |
| `door-blocks-path` | Door between bot and goal cell | Adapter receives `Op.Use(door)` before next `GoTo` |
| `pickup-then-deliver` | `AcquireItem(armor)` then `DeliverItem(Samuel, armor)` | Sequence: GoTo → PickUp → GoTo → Give |
| `equip-best-armor` | 3 armor pieces in inventory | 3 `Equip` ops in archetype-weighted order |
| `interrupt-combat` | Hostile mob appears mid-`GoTo` | `Hunt` pushed; original `Suspended`; resumes after kill |
| `interrupt-low-hp` | HP drops below threshold mid-task | `Recall` or `FleeFrom` interrupt |
| `interrupt-full-pack` | Pack full mid-`AcquireItem` | `DropItem` or `SellLoot` interrupt |
| `stuck-recovery` | Position unchanged 4 s while op is GoTo | Critic marks Stuck; Failure mode requested; recovery proposes alternate |
| `oscillation-detect` | Bot oscillates A→B→A→B | Critic Stuck via Oscillation; no infinite loop |
| `repeated-failure-cap` | 3 failures on same goal | Goal Abandoned; parent re-deliberates |
| `idle-deliberation` | Empty stack, full Blackboard | Deliberator called within `MaxIdleDeliberationInterval`; goal pushed |
| `merge-rule-stale` | LLM result returns after goal already popped | Continuation discarded; no spurious push |
| `merge-rule-cancelled` | Bot tear-down during in-flight LLM | Continuation discarded; no exception |
| `path-cache-reuse` | Same goal cell across 5 fast ticks | Adapter reuses `ActivePathPlan`; no redundant path queries |
| `path-cache-invalidate-door` | Door state changes mid-path | `ActivePathPlan` invalidated; next tick re-plans |

Each runs in < 100 ms with stubbed LLM and stubbed adapter.

### 11.3 Cutover acceptance checklist (Phase 8 gate)

Adopted from the ADR-0007 checklist pattern.

Harness:

- [ ] All §11.2 scenarios green.
- [ ] Prompt harness Dialog 6/6 still green on the new
      `FactsAndGoals` schema.
- [ ] Prompt harness Deliberation: 5 canonical scenarios green
      (academy idle, post-armor-deliver, lifestone attune, vendor
      stocking, exit portal).
- [ ] Prompt harness Failure: 5 canonical scenarios green (stuck-
      at-door, NPC despawned, item not found, oscillation, vitals
      low with no escape).

Live (in-game smoke on `bots/botplayer-spike` with
`BotBrain.UseAgentLoop=true`, scoped to ops actually delivered by
Phase 8 — `TalkTo`, `GoTo`, `Collect`, `GiveItem`, `Use(door)`):

- [ ] Bot receives Greeter dialog; pushes `TalkTo(Samuel)`.
- [ ] Bot navigates to door, opens door, crosses cell to Samuel.
- [ ] Bot completes armor turn-in (`Collect` + `GiveItem`).
- [ ] Bot rehydrates after server restart with intact Blackboard +
      Goal Stack and the highest-priority leaf re-promoted to
      `Active` on the first fast tick (§6.8).
- [ ] No motion thrash observed in nav-step log over 10 min.
- [ ] `/botdirector inspect <bot>` returns coherent state.

The Phase 10 cutover (`Equip`, `UsePortal`, etc.) has its own
checklist appended to that phase's PR; do not gate the Phase 8
flip on capabilities Phase 10 owns. Specifically deferred items:

- Bot equips best armor without explicit prompting (Phase 10
  `EquipBest` op + archetype weights).
- Bot navigates to exit portal and uses it (Phase 10 `UsePortal`).

Rollback criteria: any harness regression after cutover that traces
to the new pipeline reverts the flag default to `false` while the
fix lands.

## 12. Phased implementation plan

Each phase is independently shippable and reversible. The current
pipeline keeps running until Phase 8 cuts over.

### Phase 0 — strategy object + feature flag

- Add `BotBrain.UseAgentLoop` to `BotSettings` (loaded via the
  existing `BotDataLoader` pipeline). Default `false`.
- Introduce `IBotBrainLoop` strategy object with `ClassicLoop`
  (today's behavior) and `AgentLoop` (the new pipeline). Selected
  once at bot init; no scattered `if (UseAgentLoop)` branches
  inside the body of `BotPlayer`.

### Phase 1 — schemas, scenario harness skeleton, IBotActionAdapter

- Define `Blackboard`, `Goal`, `GoalStack`, `Op`, `FactsAndGoals`,
  `DeliberationResult`, `FailureRecovery`, `GoalCandidate`,
  `TypedFact`, `FactKind` — all the data shapes from §5, §6, §9.
- Define `IBotActionAdapter`, `IBotWorldView`, `IDeliberator`,
  `IDialogInterpreter`, `IFailureAnalyst` interfaces.
- Implement `FakeBotActionAdapter` and stub `IBrainLLM`.
- Stand up `Source/ACE.Server.Tests/Bots/` folder using existing
  MSTest project. Write 3 sentinel scenarios (one talkto, one
  pickup, one stuck-recovery). They fail until later phases land.

### Phase 2 — Blackboard

- Implement `Blackboard.cs` with the §5.1 schema.
- Wire Perception (existing `BotPlayer` snapshot methods) to
  populate volatile fields under the flag.
- Implement durable persistence via `biota_properties_string` per
  §5.3.
- Implement `MigrationRegistry` skeleton.
- Add `/botdirector inspect <bot>` (read-only JSON dump).

### Phase 3 — Goal Stack

- Implement `GoalStack.cs` with §6 semantics (dedupe, status
  transitions, push/pop/interrupt, resume).
- Persist alongside Blackboard.
- Add `/botdirector setgoal <bot> <Type> <args-json>`,
  `/botdirector goals <bot>`, `/botdirector clearstack <bot>`.
  These commands are part of the dev loop, not optional.

### Phase 4 — Planner

- One file per goal type in `Bots/Brain/Planners/`. Pure C#.
- Add tests under `Source/ACE.Server.Tests/Bots/Planners/` per
  type before the type's planner code lands.

### Phase 5 — Executor adapter

- Implement `BotPlayerActionAdapter` (live) and integration tests.
- Wire `CurrentOp` continuity (§7.3) into `_pendingInputs` flow.
- Wire `ActivePathPlan` caching (§7.1).

### Phase 6 — Critic + deliberation scheduler

- Implement five-check Critic (§8) with config thresholds.
- Implement deliberation scheduler with single-flight slots (§4.4)
  and merge rule (§9.5).

### Phase 7 — Three LLM modes

- Refactor `OllamaQuestCompiler` into three implementations:
  `OllamaDialogInterpreter`, `OllamaDeliberator`,
  `OllamaFailureAnalyst`. Shared HTTP client and prompt-building
  utilities. Each emits the §9.3 schema, validated against
  `GoalType` and `FactKind` enums (reject + retry on schema
  violation, same INVARIANT pattern as v2 dialog prompt).
- Update `files/prompt-harness/` to cover all three modes.
- Configure `IDeliberator` selection per archetype.
- Keep `ScriptedDeliberator` viable for cheap archetypes.

### Phase 8 — cutover

- Run the §11.3 acceptance checklist.
- Flip `BotBrain.UseAgentLoop` default to `true` on
  `bots/botplayer-spike`.
- E9-style live smoke pass.
- After two weeks of green live behavior, remove `ClassicLoop`
  and the flag in a follow-up commit.

### Phase 9 — expanded perception (M2)

- Lifestones (binding state), vendors (inventory cache), corpses
  (decay timers), spell book changes from `TeachSpell`, training-
  credit changes.

### Phase 10 — combat / commerce executor ops

- `Equip`, `Buy`, `Sell`, `Cast`, `Attack`, `Loot`, `UsePortal`
  with proper Player-API hooks. Most are stubs today.

## 13. Configuration

All new tunables live on `BotSettings` (loaded via
`BotDataLoader.Current.Settings`, same hot-reload story as today).
Per-archetype overrides live in `archetypes.json` under an
`AgentLoop` object.

| Key | Default | Purpose |
|---|---|---|
| `UseAgentLoop` | `false` | Phase 0 feature flag |
| `MaxIdleDeliberationInterval` | `5s` | Heartbeat between idle deliberations |
| `MinProgressMeters` | `0.5` | Stuck check |
| `StuckWindowSeconds` | `4` | Stuck check |
| `MaxRetriesPerGoal` | `3` | Critic `RepeatedFailure` cap |
| `OscillationWindow` | `10` ticks | Oscillation detector ring buffer length |
| `InterruptDelta` | `100` | Priority gap for interrupt vs compete |
| `KnownNpcStalenessLimit` | `60s` | Planner freshness threshold |
| `LowHpFrac` / `LowManaFrac` | `0.25` / `0.25` | Reactive thresholds |
| `EngageRadius` | `15m` | Hostile-adjacent detection |
| `DeliberationDailyTokenCap` (per bot) | `200_000` | LLM cost ceiling |
| `BlackboardWriteThrottle` | `30s` | Durable write cadence |
| `ModelDialog` / `ModelDeliberation` / `ModelFailure` | `qwen3:8b` | Per-mode model selection |

## 14. Observability and telemetry

Every goal pop and every LLM call emits a structured event. Two
sinks:

- **Operator log** — `[goal] popped Done TalkTo(Samuel) age=4.2s
  attempts=1`, `[deliberate] LlmDeliberator t=1.8s tokens=412/187
  goal=AcquireItem(LeatherGauntlets)`, `[critic] Stuck
  goal=TalkTo(Samuel) reason=no-progress-4s`, `[merge-reject]
  Failure result discarded; slot id changed`. Same log4net surface
  as existing `[npc-tell]`, `[plan-exec]`, `[goal]`.
- **Training corpus** — every Dialog / Deliberation / Failure call
  emits one JSONL row to `C:\ACE\Logs\botbrain-training.jsonl`
  (already used by the current pipeline; see
  [`../pilot/improvement-loop.md`](../pilot/improvement-loop.md))
  with mode, input digest, output, accepted/rejected, latency,
  token counts. Feeds the fine-tuning corpus in
  [#53](https://github.com/darinh/ac-ai-players/issues/53).

Metrics counters (per bot, per minute):

- `goals_pushed`, `goals_popped_done`, `goals_popped_abandoned`,
  `interrupts_fired`.
- `critic_stuck`, `critic_oscillation`, `critic_reactive`,
  `critic_repeated_failure_cap`.
- `deliberate_calls`, `deliberate_accepted`,
  `deliberate_rejected_by_merge`.
- `path_cache_hits`, `path_cache_misses`, `path_cache_invalidates`.
- `executor_continue`, `executor_cancel_and_restart`.

Exposed via `/botdirector metrics <bot>` for live debugging.

## 15. Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Async LLM continuation races with fast-tick mutations | high | bot stuck or thrashes goals | Single-flight slots (§4.4) + merge rule (§9.5); harness scenarios `merge-rule-stale` and `merge-rule-cancelled` |
| Schema drift breaks rehydration | high if sloppy | bot starts with empty memory after restart | `SchemaVersion` + `MigrationRegistry` (§5.3) |
| Idle LLM tokens get expensive | medium | runs up cloud cost on hosted providers | `DeliberationDailyTokenCap` per archetype; `ScriptedDeliberator` for cheap archetypes; cap idle frequency via `MaxIdleDeliberationInterval` |
| Refactor regresses live academy behavior | high during cutover | M1 demo breaks | Phase 0 strategy object + flag; §11.3 cutover checklist; rollback criteria documented |
| Planner branch coverage grows unbounded | medium | hard to reason about, hard to test | one file per goal type; harness scenario per type; new goal types reviewed against §6.5 table |
| Dialog LLM stops parsing the v2 prompt because output schema changed | high during Phase 7 | dialog ingestion breaks | port v2 prompt verbatim; only JSON output schema changes; harness gates the change |
| Bot teardown during in-flight LLM leaks tasks | medium | resource use grows over weeks | `_brainCts` cancellation propagated to every async call |
| Pathfinding A* on fast tick exceeds budget | medium | tick latency spikes | `ActivePathPlan` cache; async pathfinding via `_pendingInputs` for expensive queries |
| `bot_blackboard` blob limits future SQL queryability | low for v1, medium long-term | `/botdirector` queries can't filter on belief fields | Defer normalized tables to M5+; piggyback on `biota_properties_string` works for v1 |
| Strategy-object cohabitation in `BotPlayer.cs` causes maze | medium | hard-to-review diff during Phase 0–7 | Branches are inside `IBotBrainLoop` impls, not interleaved at call sites |

## 16. Open questions

- **Pathfinding interface ownership.** Does `IPathfindingService` live
  in `ACE.Mod.Pathfinding`
  ([ADR-0010](../adr/0010-pathfinding-as-standalone-mod.md)) at the
  time Phase 5 starts? If not, the Adapter falls back to today's
  `AdjustCell` heuristic.
- **Per-archetype priority weights for `EquipBest`.** Defined where?
  Likely an extension to `archetypes.json`. Defer to Phase 3.
- **Deliberation when no archetype-defined goals match perception.**
  E.g. fresh bot in an unfamiliar dungeon. Current lean:
  `Explore(direction=any-unseen)` is the universal fallback;
  scripted, no LLM.
- **Cross-bot coordination.** Two bots both running idle
  deliberation may both pick the same vendor / target. Out of
  scope for M1–M2; revisit at M3 (fellowship).

## 17. References

- [ADR-0011](../adr/0011-bot-brain-agent-loop.md) — the driving
  decision.
- [ADR-0001](../adr/0001-start-in-process-then-sidecar.md) — in-
  process for M1–M4; the durable Blackboard + Goal Stack + LLM
  modes form the sidecar boundary later.
- [ADR-0005](../adr/0005-pathfinding-reuse-and-build.md) — reuse
  motion primitives; Planner consumes `IPathfindingService`.
- [ADR-0007](../adr/0007-bots-as-player-not-creature.md) — bots
  are `Player`s; agent loop lives on `BotPlayer`.
- [ADR-0008](../adr/0008-bot-tick-via-player-tick.md) — fast tick
  via `OnBrainTick`; async results marshalled via `_pendingInputs`.
- [ADR-0010](../adr/0010-pathfinding-as-standalone-mod.md) — the
  Planner calls `IPathfindingService`; routing is not the agent
  loop's job.
- [`../architecture.md`](../architecture.md) — Motor / Tactical /
  Strategic / Social layered model. The agent loop maps to:
  Motor ≈ Executor, Tactical ≈ Critic + Interrupts, Strategic ≈
  Goal Stack + Deliberation, Social ≈ Dialog LLM.
- [`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md) — op
  vocabulary carries forward; *who* emits it changes from LLM to
  deterministic Planner.
- [`../pilot/improvement-loop.md`](../pilot/improvement-loop.md) —
  the active Pilot Track directive; this design fits inside it.
- [`../brain-providers.md`](../brain-providers.md) — scripted
  vs LLM provider tiers; `IDeliberator` implementations live here.
- [`m2-pathfinding-planner.md`](m2-pathfinding-planner.md) — sibling
  design doc, same shape.
