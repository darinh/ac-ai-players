# 0004. Bots tick on the existing per-landblock `Monster_Tick` scheduler

- **Status:** Proposed
- **Date:** 2026-05-27
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

[`docs/research/ace-investigation.md`](../research/ace-investigation.md)
Q2 asked where in ACE's world loop a bot's brain should be ticked, and
what threading model that implies for `BotBrain`.

Investigation of `C:\Users\darin\repos\ACE` master @ `9bc20cbd` found:

- The main world loop runs on a dedicated `Thread`
  (`Source/ACE.Server/Managers/WorldManager.cs:58-65`) rate-limited to
  ~60 Hz (`updateGameWorldRateLimiter = new RateLimiter(60,
  TimeSpan.FromSeconds(1))`).
- Each tick, `LandblockManager.Tick(...)` runs
  `TickPhysics → TickMultiThreadedWork → TickSingleThreadedWork →
  UnloadLandblocks` over the loaded landblocks
  (`LandblockManager.cs:265-284, 366-425`).
- Landblocks are grouped; **groups** run in parallel via
  `Parallel.ForEach`; within a group, landblocks tick serially on the
  same thread (adjacency-affinity to avoid cross-landblock locking).
- Per-creature AI lives in `Creature.Monster_Tick(currentUnixTime)`
  (`Source/ACE.Server/WorldObjects/Monster_Tick.cs:17-174`). Each
  landblock maintains a `sortedCreaturesByNextTick` linked list
  (`Source/ACE.Server/Entity/Landblock.cs:425-470`) and pops creatures
  whose `NextMonsterTickTime` has passed.
- Two creatures in the same landblock cannot tick simultaneously. Two
  creatures in different landblocks can, if those landblocks are in
  different groups.

Why decide now: how the bot's brain is invoked determines the
synchronization story (does brain code need to be thread-safe relative
to itself? to ACE? to other bots?), the latency budget for a single
tick, and where async brain work must yield.

## Decision

`BotCreature` (per
[`0003-botcreature-not-botplayer.md`](0003-botcreature-not-botplayer.md))
appears in its landblock's `sortedCreaturesByNextTick` list like any
other creature. ACE's existing scheduler calls `Monster_Tick` on it.
Inside that method, the bot invokes its `BotBrain` for that tick.

`NextMonsterTickTime` is set by the bot per its archetype's
think-cadence (e.g., Buffbot ticks every 2 seconds, Hardcore Raider
every 200 ms during combat). The bot is responsible for choosing its
own cadence.

The brain tick must complete quickly. The hard rule, enforced at the
API level: any work that could exceed ~5 ms (LLM call, pathfind across
multiple landblocks, blocking I/O) is **submitted** during the tick
and **collected** on a later tick. The brain interface returns
`BrainTickResult { Action[]? executed, Future? pending }`; pending
work runs off-thread (Task or sidecar RPC) and is collected when its
landblock next ticks the bot.

## Options considered

### Option A — Reuse `Monster_Tick` scheduler; bots live in the landblock's tick list

- Pros:
  - Zero new scheduler code; ACE already optimized this path.
  - Bots auto-balance with monster load on their landblock — when a
    landblock is busy, everything slows together, fairly.
  - Adjacency affinity is preserved: bot's landblock-local state is
    accessed from the landblock's thread, no cross-thread races.
  - Smallest fork: the hook is one virtual or interface call inside
    `Monster_Tick`, which is upstream-justifiable as a generic AI
    extension point.
- Cons:
  - A slow brain tick blocks the entire landblock. We mitigate by
    making brain work async-by-contract.
  - Bot tick cadence can't be tighter than the landblock's tick
    cadence. In practice landblocks tick at 60 Hz, which is plenty.

### Option B — Dedicated `BotManager` on its own thread, polling all bots

- Pros:
  - Brain work cannot block ACE landblocks.
  - Easier to instrument bot-only metrics.
- Cons:
  - Every action a bot takes touches a landblock's state. From a
    separate thread, every action needs to be marshalled back onto
    the landblock's thread — exactly the kind of cross-thread coupling
    ACE's landblock-groups design exists to avoid.
  - Two schedulers running at independent rates means bot actions and
    monster reactions are no longer naturally interleaved; we'd be
    inventing a new synchronization story.
  - Larger fork: a new manager, a new thread, new locks.

### Option C — Hook `WorldManager.UpdateGameWorld` directly

- Pros:
  - Bots tick once per world tick, deterministic cadence.
- Cons:
  - Same cross-thread/landblock problem as Option B: bots are not
    naturally affined to their landblock's thread.
  - Bot ticks become global, not local — a quiet zone's bot still
    consumes a slot in a busy world tick.

## Consequences

- **Easier:**
  - Bot tick scheduling is free; ACE already does it.
  - Bot state touched during a tick is on the right thread for that
    landblock — no cross-thread races between BotBrain and the
    creature/landblock state it reads.
  - Brain bugs that hang a single tick affect at most one landblock,
    not the whole server (assuming the brain respects the async-by-
    contract rule).
- **Harder:**
  - The brain interface MUST be async-by-contract. A blocking model
    call from inside `OnBrainTick` will stall a landblock. We need a
    lint/review check that flags synchronous expensive calls in brain
    code.
  - Bots can't tick faster than 60 Hz. For movement that's far more
    than needed; for combat reaction it's adequate (humans react at
    ~5–10 Hz).
  - Per-tick budget instrumentation is needed by M3 to catch
    misbehaving brains.
- **Follow-ups:**
  - Define `IBrainProvider.OnBrainTick(...)` to return
    `BrainTickResult` with `pending` future. Submit-collect pattern,
    not call-and-block.
  - Add per-tick budget metric in M3: histogram of bot brain tick
    durations per archetype.
  - Add a review check (linter rule or PR-template question) for
    `OnBrainTick` implementations: no blocking I/O, no synchronous
    LLM calls, no cross-landblock state access.

## References

- Related doc(s):
  - [`../research/ace-investigation.md`](../research/ace-investigation.md) (Q2)
  - [`../ace-fork-plan.md`](../ace-fork-plan.md)
  - [`0001-start-in-process-then-sidecar.md`](0001-start-in-process-then-sidecar.md)
  - [`0003-botcreature-not-botplayer.md`](0003-botcreature-not-botplayer.md)
- Related open question(s):
  - "Threading model" in
    [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md)
  - Resolved by this ADR
- Related issue(s) / PR(s):
  - [#2](https://github.com/darinh/ac-ai-players/issues/2) — Q2 research issue
