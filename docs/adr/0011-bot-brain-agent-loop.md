# 0011. Restructure the bot brain as an explicit agent loop

- **Status:** Proposed
- **Date:** 2026-05-31
- **Deciders:** @darinh
- **Supersedes:** _(none — extends ADRs [0001](0001-start-in-process-then-sidecar.md),
  [0005](0005-pathfinding-reuse-and-build.md),
  [0007](0007-bots-as-player-not-creature.md),
  [0008](0008-bot-tick-via-player-tick.md))_
- **Superseded by:** _(none)_

## Context

The current bot brain on `bots/botplayer-spike` is a reactive
NPC-dialog-to-script compiler. When a bot hears an NPC say something,
the dialog is coalesced, sent to an Ollama model, and compiled into a
`Plan` whose `steps[]` (per
[`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md)) are then
executed in order. Outside of an NPC tell, the bot does nothing.

That pipeline got us to the M1 academy lobby. It is also why the bot
stalls there. The behavior we need for the rest of M1–M4
(navigate the academy, give and receive items, complete quests, equip
gear, exit, attune to lifestones, fight, loot, sell, buy, explore)
requires the bot to keep acting in the absence of NPC speech, to hold
more than one objective at a time, to interrupt the current activity
when something more important happens (combat, low HP, full pack), to
notice when an action failed and try a different approach, and to
remember things across tells, deaths and reboots.

The pipeline cannot do any of this. Its structure is wrong, not its
parameters. Specifically:

- It has no **belief state**. Each compile gets only the recent dialog
  buffer plus a flat list of visible NPC names. There is no place to
  record "I already have the Calling Stone", "the door to the next
  room is closed", "Samuel is in cell `0x860201B4`", "the last time I
  tried to walk to Samuel I got stuck after one step".
- It has no **goal stack**. `_currentGoal` is a single in-memory field
  that the latest compile overwrites. There is no representation for
  "I am completing the Academy because I am completing M1, and right
  now the immediate thing is talking to Samuel."
- It has no **autonomous deliberation**. Nothing chooses a goal when
  no NPC is talking. The two-tick model that the bot tick scheduler
  was designed for ([ADR-0008](0008-bot-tick-via-player-tick.md))
  is currently used only as a heartbeat for plan execution, not for
  thinking.
- It conflates **understanding** with **planning**. The LLM is asked
  to read NPC dialog *and* emit a concrete movement script. Plans go
  stale (an NPC moves, a door closes, a mob respawns, a pack fills
  up), but cached plans don't.
- It has no **critic**. Stuck detection, contradiction detection
  ("plan says walk to X, but X is not visible from here") and
  recovery exist as ad-hoc patches in the executor, not as a separate
  pass that can trigger replanning.
- It has no **fast dev loop**. Until the `/botdirector poke` command
  landed on commit `7c657082`, every iteration on bot behavior
  required an in-game NPC interaction. Even with `poke`, the rebuild
  cycle is the dominant cost. There is no harness that can spawn a
  bot in a known state, tick it N times, and assert behavior in
  seconds.

Why decide now: M1 is gated on the academy traversal, which needs
nested objectives, replanning across closed doors, and inventory-aware
decisions. The current pipeline cannot get there with prompt tweaks.
The "pending P-1 ADRs" referenced in
[`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md) (three-layer
brain, LLM-as-compiler, two-tier LLM, needs engine) were never
written; they predate the learnings from the spike. This ADR collapses
and supersedes those four pending ADRs based on what the spike actually
showed us.

## Decision

The bot brain becomes an explicit, six-stage agent loop, run once per
deliberation tick from
[`OnBrainTick`](0008-bot-tick-via-player-tick.md): **Perception →
Blackboard → Goal Stack → Planner → Executor → Critic**. The loop has
two cadences: a **fast tick** (every `OnBrainTick`, target 100–500 ms,
fully deterministic) that runs Perception, Critic and the Executor's
next step, and a **deliberation tick** (event-driven, plus a heartbeat
floor of a few seconds when idle, target 1–5 s) that runs the Goal
Stack and Planner stages and may call the LLM.

The LLM stops emitting `Plan.steps[]`. Instead, it has three narrowly
scoped jobs: **dialog understanding** (turn NPC dialog into `Facts`
and candidate `Goals`), **deliberation** (when the Goal Stack is empty,
pick the next `Goal` from the Blackboard), and **failure analysis**
(when the Critic flags a stuck or contradicted plan, propose a
recovery `Goal`). The planner is deterministic C# code that consumes
the current Blackboard and the top of the Goal Stack, and emits the
single next executor op. The op vocabulary from
[`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md) carries
forward unchanged.

The Blackboard is the bot's persistent belief state: known NPCs,
known cells, active quests, inventory, equipment, vitals, recent
observations, recent failures with reasons. It is the single source of
truth for the planner and the input to LLM deliberation. It persists
across rehydration through the existing shard-DB path. The Goal Stack
is a typed, prioritized stack with explicit push/pop/interrupt
semantics, also persisted.

The detailed component design, data shapes, pseudocode, phase plan,
and testing strategy live in
[`../design/bot-brain-agent-loop.md`](../design/bot-brain-agent-loop.md).
This ADR records the architectural commitment; the design doc carries
the specifics that will change as the implementation lands.

## Options considered

### Option A — Keep the dialog-compile pipeline; tune prompts and add patches

- Pros:
  - No refactor; current 6/6 harness pass on the v2 prompt is
    preserved.
  - Each new behavior is a localized executor change or a prompt
    tweak, so iterations stay shallow.
  - Honest with the "bots are bots" stance: the LLM stays at the
    Social-ish seam (dialog reading) and never owns the body.
- Cons:
  - Cannot represent multi-goal nesting (Academy → Armor → Samuel →
    door 1) without inventing a parallel goal stack on top of the
    existing `_currentGoal`, which is the agent loop in disguise but
    without a name or test surface.
  - Plans go stale and the bot has no principled way to replan from
    current world state, only the ad-hoc cache invalidations already
    in `BotPlayer.cs` around line 1017.
  - Idle deliberation has nowhere to live. Bots sit still between
    tells, which is the current observed failure mode.
  - Critic / stuck recovery has nowhere to live either; it accretes
    inside the executor and grows brittle.
  - Dev loop stays slow: no harness can drive behavior end-to-end
    without spinning up the full server and an NPC interaction.

### Option B — Explicit six-stage agent loop with two-tick cadence (this proposal)

- Pros:
  - Maps cleanly onto behaviors M1–M4 require: nested goals,
    interrupts, replanning, stuck recovery, idle deliberation.
  - Each stage is independently testable. A scenario harness can
    seed the Blackboard, push a goal, tick the executor N fast
    ticks (100 ms each by default),
    and assert behavior in roughly one second per scenario, which is
    the dev loop the project is missing.
  - Confines the LLM to language tasks (dialog understanding,
    deliberation, failure analysis). The body remains deterministic.
    This is consistent with [`../architecture.md`](../architecture.md)
    and with the project's "bots are bots, not agents" framing in
    [`../../README.md`](../../README.md), reinterpreted as "the LLM
    sits at language seams; everything else is code."
  - Belief and goal state become first-class, persistable, and
    introspectable from `/botdirector` admin commands, which is what
    debugging a real bot population needs.
  - Cleanly accommodates the future split into a sidecar
    ([ADR-0001](0001-start-in-process-then-sidecar.md)): the
    *durable* part of the Blackboard, the Goal Stack, and the LLM
    modes form the natural sidecar boundary. Volatile per-tick
    Perception state stays in-process; the sidecar receives an
    on-demand digest, not the raw 100–500 ms feed.
- Cons:
  - Substantial refactor of `BotPlayer.cs` and
    `OllamaQuestCompiler.cs`. Will introduce regressions in the
    short term against the current 6/6 harness pass.
  - Three LLM modes is more prompt-engineering surface than one.
  - The Blackboard is a new persisted-state object whose schema must
    survive code changes; getting that wrong is expensive.
  - Deliberation that runs in the absence of NPC speech means the
    bot will spend LLM tokens just thinking. Idle cost is no longer
    zero.

### Option C — Hand-rolled per-archetype behavior trees, no LLM in the control path

- Pros:
  - Fully deterministic and debuggable.
  - Zero LLM latency or cost during gameplay.
  - Mirrors VirindiTank's 20-year track record cited in
    [`../architecture.md`](../architecture.md).
- Cons:
  - Contradicts the project's central thesis: NPCs in retail and in
    community content packs are not enumerable; the bot has to
    *read* dialog at runtime. A hand-rolled BT either ships with a
    quest catalog (which we explicitly will not do, see
    [`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md) §
    Purpose) or never participates in quests it has not been hand-
    authored for.
  - Throws away the dialog-understanding capability the spike just
    proved out.

## Consequences

- **Easier:**
  - The bot can keep acting without NPC speech, because the Goal
    Stack and Planner run every tick from current world state.
  - Nested objectives, interrupts, replanning, and stuck recovery
    each have a named home with one job, instead of being scattered
    inside the executor.
  - Behavior changes can be tested in seconds via a scenario harness
    that drives the agent loop directly, without needing the live
    server or an NPC. This is the dev-loop fix the spike has been
    blocked on.
  - The LLM has three narrow contracts (facts/goals out of dialog,
    next goal from beliefs, recovery goal from failure) instead of
    one underspecified one (a complete plan from a paragraph). Each
    contract can be prompt-tuned and evaluated independently against
    the existing `botbrain-training.jsonl` corpus
    ([#53](https://github.com/darinh/ac-ai-players/issues/53)).
  - Future work splits cleanly: pathfinding behind
    `IPathfindingService` ([ADR-0010](0010-pathfinding-as-standalone-mod.md)),
    perception expansion, executor ops, prompt iteration, and
    persistence all become independent issues.
- **Harder:**
  - `BotPlayer.cs` grows a real internal architecture instead of an
    accreted sequence of helpers. Migration must keep the current
    pipeline working until the new loop has parity on the academy
    scenario, which means a flagged migration, not a big-bang
    rewrite.
  - The Blackboard's schema becomes a versioned, persisted
    artifact. Schema drift will cause rehydration failures unless
    each new field is optional with a sane default.
  - Idle bots consume LLM tokens for deliberation. Budgeting
    (deliberation cooldown, token caps, "scripted-only when idle"
    archetype mode) becomes a real design surface, not a future
    worry.
  - The "bots are bots, not agents" framing in
    [`../../README.md`](../../README.md) was already superseded by
    the Pilot Track directive in
    [`../../AGENTS.md`](../../AGENTS.md). "Agent loop" here is the
    internal architecture term (perception → belief → goal → plan
    → act → critic), consistent with that supersession. The LLM
    stays at language seams (dialog reading, goal selection,
    failure analysis); the body is deterministic C#.
- **Follow-ups:**
  - Write [`../design/bot-brain-agent-loop.md`](../design/bot-brain-agent-loop.md)
    with the concrete data shapes, pseudocode, phase plan, and
    test strategy (this ADR lands together with that doc).
  - File a tracking issue per phase (scenario harness, Blackboard,
    Goal Stack, LLM-emits-goals refactor, Planner, Critic,
    Deliberation tick, expanded Perception, executor migration)
    and link them under an "M1.5 — bot brain refactor" epic.
  - Update the README "Status" section so it reflects that the bot
    brain is being restructured before M1 closes.
  - Reconcile [`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md)
    so its references to the unwritten 0010-three-layer-brain /
    0011-LLM-as-compiler / 0012-two-tier-LLM / 0013-needs-engine
    ADRs point at this ADR instead.

## References

- Related ADR(s):
  - [ADR-0001](0001-start-in-process-then-sidecar.md) — in-process
    for M1–M4; the durable Blackboard + Goal Stack + LLM modes
    form the natural later sidecar boundary.
  - [ADR-0005](0005-pathfinding-reuse-and-build.md) — reuse motion
    primitives, build the planner. The agent-loop Planner consumes
    `IPathfindingService` but does not own routing.
  - [ADR-0007](0007-bots-as-player-not-creature.md) — `BotPlayer :
    Player`. The agent loop runs inside `BotPlayer`.
  - [ADR-0008](0008-bot-tick-via-player-tick.md) — `OnBrainTick`.
    The fast tick uses this hook; the deliberation tick is event-
    driven plus a heartbeat floor, dispatched via the existing
    `_pendingInputs` queue on the landblock thread.
  - [ADR-0010](0010-pathfinding-as-standalone-mod.md) — pathfinding
    behind `IPathfindingService`. The Planner calls it.
- Related doc(s):
  - [`../architecture.md`](../architecture.md) — layered brain
    model. This ADR refines the Motor / Tactical / Strategic
    breakdown into the explicit six-stage loop and assigns the
    Social-layer LLM to three named jobs.
  - [`../pilot/plan-vocabulary.md`](../pilot/plan-vocabulary.md) —
    op vocab carries forward; the question of *who* emits the
    `steps[]` changes from LLM to deterministic planner.
  - [`../design/bot-brain-agent-loop.md`](../design/bot-brain-agent-loop.md)
    — the detailed design behind this ADR.
- Related issue(s) / PR(s):
  - [#53](https://github.com/darinh/ac-ai-players/issues/53) — LLM
    fine-tuning corpus; the three LLM modes are each separable
    fine-tuning targets.
  - Follow-up tracking issues to be filed under the M1.5 epic.
