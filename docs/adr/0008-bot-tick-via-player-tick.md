# 0008. Bots tick on the existing per-landblock `Player_Tick` loop

- **Status:** Proposed
- **Date:** 2026-05-27
- **Deciders:** @darinh
- **Supersedes:** [`0004-bot-tick-via-monster-tick.md`](0004-bot-tick-via-monster-tick.md)
- **Superseded by:** _(none)_

## Context

[ADR-0007](0007-bots-as-player-not-creature.md) reverses
[ADR-0003](0003-botcreature-not-botplayer.md) and moves bots from
`BotCreature : Creature` to `BotPlayer : Player`. That reversal
also invalidates [ADR-0004](0004-bot-tick-via-monster-tick.md),
which assumed bots would appear in the landblock's
`sortedCreaturesByNextTick` list and be invoked via
`Monster_Tick`.

`Player` does **not** tick via `Monster_Tick`. Investigation of
`C:\Users\darin\repos\ACE-bots` branch `botplayer-spike`
(downstream of ACE master @ `9bc20cbd`) found:

- Each landblock maintains a separate `players` collection that is
  walked every tick by
  `foreach (var player in players) player.Player_Tick(currentUnixTime);`
  ([`Source/ACE.Server/Entity/Landblock.cs:591-594`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Entity/Landblock.cs#L591-L594)).
- `Player_Tick` is the per-player heartbeat
  ([`Source/ACE.Server/WorldObjects/Player_Tick.cs:36-144`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/WorldObjects/Player_Tick.cs#L36-L144)).
  It runs the player's `ActionQueue`, ages the character, updates
  house-rent warnings, runs fellowship vitals, and (separately, via
  the `Heartbeat()` virtual on `WorldObject`) advances regenerators
  and a few other longer-cadence concerns.
- Pending world-object additions assign a new `Player` to
  `players` and a `Creature` to `sortedCreaturesByNextTick` via
  `if (kvp.Value is Player ...) else if (kvp.Value is Creature ...)`
  ([`Landblock.cs:640-643`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Entity/Landblock.cs#L640-L643)).
  A `BotPlayer : Player` will be classified as a player and land
  in the players loop automatically — no scheduler hook required.
- Like creatures, players within one landblock tick serially on
  that landblock's thread. Landblock groups tick in parallel via
  `Parallel.ForEach`. Adjacency affinity is preserved.

Two design questions remain after this:

1. **Where is the bot brain invoked?** `Player_Tick` itself does
   not call a virtual brain method.
2. **How fast can brains tick?** `Player_Tick` runs every world
   tick (~60 Hz). `Monster_Tick` had a per-creature
   `NextMonsterTickTime` allowing a slower cadence per creature.
   `Player_Tick` does not.

Why decide now: the migration epic (#27) cannot land `BotPlayer`
(E4) without a known brain-tick mechanism, and the choice
determines whether the fork's `Player_Tick.cs` diff is one line
(a virtual hook) or zero (an external scheduler).

## Decision

The bot brain ticks via a `protected virtual void OnBrainTick(double
currentUnixTime)` hook added at the end of `Player.Player_Tick(...)`
in the fork. `Player.OnBrainTick` is empty. `BotPlayer.OnBrainTick`
invokes the bot's `IBrainProvider` for that tick, respecting an
internal `nextBrainTickTime` that the brain itself advances per
its archetype's think-cadence (e.g., Buffbot every 2 s, Hardcore
Raider every 200 ms during combat).

The brain tick must complete quickly. The async-by-contract rule
from ADR-0004 carries forward unchanged: any work that could
exceed ~5 ms is submitted during the tick and collected on a later
tick. The brain interface returns `BrainTickResult { Action[]?
executed, Future? pending }`; pending work runs off-thread and is
collected when its landblock next ticks the bot.

## Options considered

### Option A — Reuse `Player_Tick`; add a virtual `OnBrainTick` hook

- Pros:
  - Zero new scheduler code; ACE already optimized the players
    loop.
  - Bots auto-balance with player load on their landblock — when
    a landblock is busy, everything slows together, fairly.
  - Adjacency affinity is preserved: the bot's landblock-local
    state is accessed from the landblock's thread, no
    cross-thread races.
  - Smallest fork: one virtual method on `Player` and one empty
    body. Upstream-justifiable as a generic AI extension point.
  - Mirrors ADR-0004's pattern at the Player layer, so the rule
    set developers internalize for monster AI carries forward
    to bot AI.
- Cons:
  - A slow brain tick blocks the entire landblock's player loop.
    We mitigate by making brain work async-by-contract.
  - Bot brain wakeups can't happen more often than the landblock
    tick rate (~60 Hz). In practice landblocks tick at 60 Hz,
    which is plenty for combat reaction (humans react at ~5–10 Hz)
    and far more than needed for movement.
  - The fork carries a small additive diff on `Player.cs` (the
    virtual method) that needs to be re-merged on each upstream
    pull. Mitigation: keep the diff to a single virtual stub plus
    a single call site so re-merges are mechanical.

### Option B — Hook in `Landblock.Tick` after the players loop

- Pros:
  - No diff inside `Player_Tick` at all; the hook is at the
    landblock layer where bot/player separation is already
    visible.
- Cons:
  - Requires the landblock to know about `BotPlayer` (or `IBot`)
    specifically, which couples a general-purpose engine class to
    a bot-system concept.
  - Brain ticks happen *after* `Player_Tick`, so the bot can't
    read the result of the same-tick `ActionQueue` until next
    tick. A 16 ms latency floor on every brain decision.
  - Larger fork: `Landblock.cs` is a hot path for upstream merge
    conflicts. Adding code here is more brittle than adding a
    virtual to `Player`.

### Option C — Dedicated `BotManager` on its own thread, polling all bots

- Pros:
  - Brain work cannot block ACE landblocks.
  - Easier to instrument bot-only metrics.
- Cons:
  - Every action a bot takes touches a landblock's state. From a
    separate thread, every action needs to be marshalled back onto
    the landblock's thread — exactly the kind of cross-thread
    coupling ACE's landblock-groups design exists to avoid.
  - Two schedulers running at independent rates means bot actions
    and other players' reactions are no longer naturally
    interleaved; we'd be inventing a new synchronization story.
  - Larger fork: a new manager, a new thread, new locks.
  - Same set of cons as the rejected Option B in ADR-0004; the
    Player-layer reversal doesn't change the calculus.

### Option D — Hook `WorldManager.UpdateGameWorld` directly

- Pros:
  - Bots tick once per world tick, deterministic cadence.
- Cons:
  - Same cross-thread/landblock problem as Option C: bots are not
    naturally affined to their landblock's thread.
  - Bot ticks become global, not local — a quiet zone's bot still
    consumes a slot in a busy world tick.
  - Same set of cons as the rejected Option C in ADR-0004.

### Option E — Use `WorldObject.Heartbeat` instead of a new hook

- Pros:
  - `Heartbeat` is already a virtual on `WorldObject` with a
    per-object `NextHeartbeatTime`, supporting variable cadence
    (the `Monster_Tick` per-creature cadence story, except at the
    WorldObject layer). No fork diff at all.
  - Each landblock maintains a `sortedWorldObjectsByNextHeartbeat`
    list that pops only objects whose time has passed, so cadence
    is enforced for free
    ([`Landblock.cs:597-612`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Entity/Landblock.cs#L597-L612)).
- Cons:
  - `Player.Heartbeat()` already does meaningful work (vitals tick,
    house-rent warning, container update timestamps, ...). A bot
    override would have to call `base.Heartbeat()` then run the
    brain, and any future upstream change to `Player.Heartbeat`
    that adds preconditions on `Session.Network` becomes a fork
    surprise.
  - Heartbeat default interval is 5 seconds (longer than a tick).
    Acceptable for slow brains, too slow for combat brains.
  - Conceptually muddies the distinction between "heartbeat"
    (housekeeping at low cadence) and "brain tick" (decision loop
    at higher cadence).

## Consequences

- **Easier:**
  - Bot tick scheduling is free; ACE already does it.
  - Bot state touched during a tick is on the right thread for
    that landblock — no cross-thread races between `BotBrain` and
    the player/landblock state it reads.
  - Brain bugs that hang a single tick affect at most one landblock,
    not the whole server (assuming the brain respects the
    async-by-contract rule).
  - The brain-author mental model is identical to ADR-0004's:
    "your brain runs once per tick, do quick work, schedule slow
    work, look at results next tick." Continuity even though the
    base class changes.
- **Harder:**
  - The brain interface MUST be async-by-contract. A blocking model
    call from inside `OnBrainTick` will stall a landblock. We need
    a lint/review check that flags synchronous expensive calls in
    brain code.
  - Bot brain wakeups can't happen faster than ~60 Hz. For combat
    reaction it's adequate; for sub-frame timing (e.g., per-packet
    network reactions) it's not. We are not targeting sub-frame
    bot behavior.
  - The fork now carries a one-line `OnBrainTick();` call at the
    end of `Player_Tick`, plus a virtual stub. Each upstream pull
    must re-apply that diff. Keep the diff mechanical: empty stub
    near the other partial-class `protected virtual` definitions,
    single call site at the end of `Player_Tick`, no other
    refactor.
  - The bot brain cannot reuse `NextMonsterTickTime` as its
    cadence variable. `BotPlayer` owns its own `nextBrainTickTime`
    and gates `OnBrainTick` internally; the virtual is called
    every tick but the brain only runs every N.
- **Follow-ups:**
  - Define `IBrainProvider.OnBrainTick(...)` to return
    `BrainTickResult` with `pending` future. Submit-collect
    pattern, not call-and-block. (Same contract as ADR-0004.)
  - Add per-tick budget metric in M3: histogram of bot brain tick
    durations per archetype. (Same as ADR-0004.)
  - Add a review check (linter rule or PR-template question) for
    `OnBrainTick` implementations: no blocking I/O, no synchronous
    LLM calls, no cross-landblock state access. (Same as ADR-0004.)
  - When the fork is re-merged with upstream, the
    `Player.OnBrainTick` virtual is the kind of small change that
    might be upstreamable to ACEmulator/ACE proper — file an
    upstream issue once the BotPlayer migration is stable.

## References

- Related ADR(s):
  - [`0003-botcreature-not-botplayer.md`](0003-botcreature-not-botplayer.md)
    (superseded by ADR-0007)
  - [`0004-bot-tick-via-monster-tick.md`](0004-bot-tick-via-monster-tick.md)
    (superseded by this ADR)
  - [`0007-bots-as-player-not-creature.md`](0007-bots-as-player-not-creature.md)
    (the reversal that forces this rethink)
  - [`0001-start-in-process-then-sidecar.md`](0001-start-in-process-then-sidecar.md)
    (the threading-model parent)
- Related doc(s):
  - [`../research/ace-investigation.md`](../research/ace-investigation.md)
    (Q2, originally answered by ADR-0004; now answered by this ADR
    for the BotPlayer world)
  - [`../ace-fork-plan.md`](../ace-fork-plan.md)
- Related open question(s):
  - "Threading model" in
    [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md)
    (resolved by this ADR, replacing the ADR-0004 resolution)
- Related issue(s) / PR(s):
  - [#27](https://github.com/darinh/ac-ai-players/issues/27) —
    Migration epic
  - [#28](https://github.com/darinh/ac-ai-players/issues/28) —
    E0: ADR-0008 tick mechanism for `BotPlayer`
- Related ACE source:
  - [`Source/ACE.Server/WorldObjects/Player_Tick.cs:36-144`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/WorldObjects/Player_Tick.cs#L36-L144) — the `Player_Tick` method
  - [`Source/ACE.Server/Entity/Landblock.cs:591-594`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Entity/Landblock.cs#L591-L594) — the per-landblock players loop
  - [`Source/ACE.Server/Entity/Landblock.cs:640-643`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Entity/Landblock.cs#L640-L643) — `Player` is classified into `players`, `Creature` into `sortedCreaturesByNextTick`; `BotPlayer` inherits the player path
  - [`Source/ACE.Server/Entity/Landblock.cs:597-612`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Entity/Landblock.cs#L597-L612) — `Heartbeat` scheduler (Option E)
