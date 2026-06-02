# Headless AC1 Client — Research Spike

## Status

Research spike. Lives on branch `anvil/headless-client-spike` in
worktree `.worktrees/headless-client-spike/`. Exploratory.

Goal: prove we can run many autonomous bots that play AC1 by
speaking the same wire protocol a real client speaks — without
rendering, without cheats, without the shortcuts the in-process
`BotPlayer` work has been tempted into.

If Phase 1 (network handshake) fails, the spike is dead and we
fall back to constraining `BotPlayer` to client-equivalent server
entry points. The phased plan below has hard gates exactly so we
find out cheaply.

## Why a headless client and not the in-process BotPlayer

`BotPlayer` lives inside `ACE.Server`. It has been tempted into
shortcuts because it CAN — it has direct access to
`Player.Location`, `EmoteManager`, `WorldManager`. Symptoms in the
current code: walking through walls, gliding without animation,
`{p}` chat substitution skipped, auto-facing nearby players. Each
fix is a discipline problem, not an architecture one.

A headless client cannot cheat. It only knows what packets the
server sends it. It can only do what packets it sends back can
express. The protocol is the cheat-prevention boundary.

## Architecture: shared core, isolated bots

Many bots in one process. Expensive resources shared. Per-bot
state isolated. Adding a bot costs one socket and one perception
table, not a whole LLM instance or a whole navmesh.

| Resource | Scope | Why |
|----------|-------|-----|
| LLM client (Ollama) + request queue | shared | LLM calls are slow and rate-limited. Pool them. |
| Pathfinder (navmesh per landblock) | shared | Navmesh is read-mostly and large. Build once per cell, query from many bots. |
| World database (weenies, portals, terrain) | shared | Read-only. Loaded once from `ace_world`. |
| Memory store (SQLite) | shared db, per-bot namespace | One file, `bot_id` column. Cheaper than N files. |
| Training data sink | shared | One append-only writer, all bots produce events. |
| ISAAC encryption code | shared code | Algorithm is shared; key material is per-bot. |
| Socket, sequence numbers, ISAAC keys | per-bot | Each bot is its own session. |
| Perception (this bot's PVS) | per-bot | What this bot can see. |
| Avatar state, inventory, vitals | per-bot | Obvious. |
| Brain (goal, plan, motor target) | per-bot | Each bot has its own intent. |

The kernel/bot split is the central architectural decision. It is
what makes "fully autonomous players, many of them" tractable on a
single machine.

## Agents

An "agent" is a single responsibility, not necessarily a thread.
Within a bot, agents talk via in-process channels. Bot → kernel is
async method calls with cancellation; the kernel serves requests
with bounded queues and per-bot fairness.

### Kernel-scope (singleton per process)

- **LlmService** — wraps Ollama. Exposes `CompilePlan(state, goal)
  → Plan` with a bounded request queue. Reuses the existing
  `OllamaQuestCompiler` prompt schema in ACE-bots so the corpus
  stays one corpus across both bot paths.
- **Pathfinder** — exposes `FindPath(cell, from, to) → Waypoint[]`.
  Lazy-builds navmesh per landblock. Caches. Pure over read-only
  inputs; safe for many concurrent callers.
- **WorldDb** — read-only weenie / landblock / portal / terrain
  lookups served from `ace_world`. Loaded once at startup.
- **MemoryStore** — SQLite at `data/memory.db`. Persists durable
  facts the bots learn ("Samuel is at (cell, x, y, z)", "this
  portal goes to Holtburg", "Calling Stone weenie 12345 gives an
  Exit Token"). Survives restarts.
- **TrainingSink** — append-only event log at
  `data/training/<date>/<bot_id>.jsonl`. One line per event.
  Schema kept intentionally simple so we can change it later.
- **Supervisor** — spawns and reaps bots, watches for crashes,
  exposes a small admin surface (start, stop, list, snapshot).

### Bot-scope (per bot, isolated)

- **Network** — owns the UDP socket and ISAAC encryption.
  Serializes outgoing client packets, deserializes incoming server
  packets into typed events.
- **Perception** — consumes `Network` events. Maintains the
  bot's visible-object set, avatar state, inventory, journal,
  chat log. The bot's "screen", as data.
- **Motor** — movement primitives. Translates "go to position" or
  "go to object" into `MoveTo` packet sequences. Honors
  server-side collision (the server bounces us off walls; we
  obey). Calls `Pathfinder` for cross-cell routes.
- **Action** — non-movement actions: use, give, equip, talk, cast,
  attack. Each is the exact packet sequence a real client would
  send.
- **Brain** — strategy. Reads `Perception`, picks a goal, asks
  `LlmService` for a plan when needed, hands steps to `Motor` and
  `Action`. Cadence: 1-2 Hz.
- **MemoryView** — per-bot facade over `MemoryStore`. Saves facts
  the brain learns. Loads on bot startup.
- **Recorder** — subscribes to interesting agents, emits training
  samples to `TrainingSink`. The training corpus is the durable
  output of the experiment.

### Wiring sketch

```
              ┌──────────────────────────────────┐
              │  Kernel (one per process)        │
              │   LlmService                     │
              │   Pathfinder                     │
              │   WorldDb                        │
              │   MemoryStore                    │
              │   TrainingSink                   │
              │   Supervisor                     │
              └────────────┬─────────────────────┘
                           │ injected
       ┌───────────────────┴───────────────────┐
       │                                       │
   ┌───▼────────────────┐                ┌─────▼──────────────┐
   │  Bot #1            │                │  Bot #2            │
   │   Network          │                │   Network          │
   │   Perception       │                │   Perception       │
   │   Motor            │                │   Motor            │
   │   Action           │                │   Action           │
   │   Brain            │                │   Brain            │
   │   MemoryView       │                │   MemoryView       │
   │   Recorder         │                │   Recorder         │
   └────────────────────┘                └────────────────────┘
```

## Integration with existing systems

- **LLM** — reuse the prompt and parser from
  `Source/ACE.Server/Bots/Brain/OllamaQuestCompiler.cs` in ACE-bots.
  Lift them into the kernel `LlmService` so the headless-client
  path and the in-process `BotPlayer` path call the same compiler
  with the same vocabulary. The compiler stays a quest compiler,
  not a controller.
- **Pathfinding** — the dungeon-nav work happening in ACE-bots
  issues #45-#52 is the source of truth. The kernel `Pathfinder`
  uses the same navmesh builder, lifted out as a service. If that
  work is not yet shippable when we get to Phase 7, the spike
  falls back to A* on a coarse cell grid and graduates later.
- **Memory** — SQLite, one file, `bot_id`-keyed rows. Distinct
  from the developer-facing `store_memory` agent tool — that one
  is for agents reasoning about the codebase, this one is for
  bots reasoning about the game world.
- **Training data** — JSONL files under `data/training/`. Used
  offline to fine-tune the LLM compiler or to train a smaller
  policy model. Format kept intentionally simple to defer schema
  decisions.

## Technology

- .NET 10 / C#. Same runtime as ACE, so we can lift packet
  definitions and ISAAC code straight from the ACE server source
  as a starting point.
- xUnit for tests; `Verify` for snapshot tests against captured
  server packets.
- One solution: `experiments/headless-client/HeadlessAcClient.sln`.
  Projects mirror the agent split.

## Resource discipline (early-90s style)

The goal is many bots on one server. The constraints below are
the design contract, not aspirational targets. Code reviews
reject anything that breaks them on a hot path.

- **Per-bot working set target: < 5 MB.** That is the entire
  per-bot footprint including network buffers, perception state,
  inventory, brain.
- **Process-level shared overhead independent of bot count.** A
  100-bot server should not pay 100x the LLM client cost or 100x
  the navmesh cost.
- **No garbage in the per-tick path.** Per-bot ticks at 5-20 Hz.
  No `string.Format`, no LINQ, no `foreach` over `IEnumerable<T>`,
  no `async/await` state machines on the hot path. Use
  `Span<byte>`, `ReadOnlySpan<char>`, value tuples, `ValueTask<T>`
  where async is unavoidable.
- **Pool packet buffers.** `ArrayPool<byte>.Shared` for receive,
  per-bot ring buffer for outgoing. No `new byte[]` in the send
  or recv path.
- **Pool messages.** Per-bot pools for outbound `GameAction`
  bodies. No allocation per outbound packet steady-state.
- **Fixed-capacity structures.** Visible-object set is a
  `Dictionary<uint, ObjectState>` with a fixed initial capacity
  sized to "expected PVS upper bound" (a few hundred). Inventory
  is a small array. No unbounded growth without a reason.
- **Struct over class for hot data.** `ObjectState`, `Waypoint`,
  `PacketHeader` are structs. Pass by `ref` or `in`. Avoid boxing.
- **No reflection or DI surprises on the hot path.** DI resolves
  services once at bot construction; cache references in fields.
- **One thread per bot, max.** Not "one thread per agent per bot".
  Inside a bot, agents run cooperatively on the bot's own scheduler.
  A separate IO thread handles socket reads for all bots together.
- **No per-tick logging at info level.** Trace only, behind a
  compile-time flag. Logging allocates strings.

Concrete budget for the headline scenario "100 bots, one server":
- 500 MB total working set across all bot state.
- 1 OS thread for IO + 1 thread for the brain pool + the .NET
  thread pool for LLM and pathfinding queues.
- < 5% of one core per idle bot, < 25% under a quest-execution
  spike, averaged.

These numbers are deliberately conservative. If a phase blows
through them, the phase fails review and we either fix it or
revise the budget with a written justification.

## License posture

ACE is AGPL3. Code copied verbatim from ACE (such as ISAAC and
Hash32) carries its license forward. Each copied file in this
spike preserves the original copyright header and an attribution
comment pointing back to the source in ACE-bots. The spike as a
whole is therefore AGPL3 by inheritance.

If this spike ever graduates to a separately distributed
artifact, the copied cryptography and hashing code needs to be
re-derived clean-room from public protocol documentation (Decal
SDK and the AC protocol reverse-engineering corpus) rather than
lifted from ACE. Document the re-derivation in a `LICENSE_NOTES.md`
when that work happens.

## Phases

Hard gates. Phase N must demonstrate the named outcome before
Phase N+1 begins. Missing a gate moves the spike to "dead" and we
revert to in-process `BotPlayer` discipline.

| Phase | Outcome | Estimate |
|-------|---------|----------|
| 1 | Hand-rolled login + ISAAC handshake against a local ACE dev server. Receive the first MOTD packet. | 1-3 days |
| 2 | Character logon. Receive own avatar spawn. Send heartbeat. Stay connected. | 3-5 days |
| 3 | `Perception` populated from spawn / despawn / move packets. Bot can describe its surroundings in text. | ~1 week |
| 4 | `Motor` + `Action` ship: bot walks to a target object via `MoveTo` packets and uses it. Server-side collision respected. | ~1 week |
| 5 | `Brain` glue: `LlmService` picks a goal, plan executes end-to-end on a no-op quest. | 3-5 days |
| 6 | `Recorder` + `TrainingSink` record one tutorial run end-to-end as training data. | 2-3 days |
| 7 | `Pathfinder` integrated with the ACE-bots dungeon-nav work. | open-ended |
| 8 | `Supervisor` runs N bots in one process. Resource sharing measured (LLM queue depth, navmesh cache hit rate). | 2-3 days |

### Phase results

- **Phase 1** — [`phase1-results.md`](phase1-results.md). PASS. Three-leg
  handshake (`LoginRequest` → `ConnectRequest` → `ConnectResponse`)
  works against a vanilla ACE server. Required a client-side
  retransmit of `ConnectResponse` to cover the server's bcrypt-vs-
  loopback-RTT race; documented in
  [`spec/04-handshake.md`](spec/04-handshake.md).
- **Phase 2** — [`phase2-results.md`](phase2-results.md). PASS. CRC
  verification, encrypted-checksum keepalive (ack + timesync echo),
  and decode of the three S→C messages the server pushes between
  handshake-leg-3 and EnterWorld (`DDDInterrogation`,
  `CharacterList`, `ServerName`). Session lives indefinitely with
  zero CRC failures.
- **Phase 3** — [`phase3-results.md`](phase3-results.md). In progress.
  3.1 (encrypted outbound `BlobFragments`) PASS — first C→S game
  message accepted by server (`AckSequence=2` flood + dispatch log
  entry). 3.2 (`CharacterCreate`) and 3.3 (`CharacterEnterWorldRequest`)
  pending. See also [`spec/08-outbound-packet.md`](spec/08-outbound-packet.md)
  for the C→S packet construction rules.

## Risks

- **ISAAC encryption** is the Phase 1 deal-breaker. If the ACE
  server-side ISAAC code relies on state we cannot reconstruct on
  the client side, Phase 1 fails. Mitigation: read
  `Source/ACE.Server/Network/` and the ACE crypto code first.
- **Physics replication** — the real client interpolates between
  server position updates. A headless client that snaps to the
  latest server position is fine for AI behavior but may look odd
  to humans watching. Acceptable for the spike.
- **Anti-cheat / sanity checks** — ACE may flag unusual movement
  velocities or impossible packet sequences. Mitigation: drive
  motor with the same velocity envelopes a real client uses.
- **Legal** — AC1 is a dead commercial game; Turbine / WB still
  hold the IP. We use ACE (already a community emulator) as the
  server. The client we are writing speaks the same protocol the
  Turbine `acclient.exe` spoke. We do not redistribute any Turbine
  binary. Same legal posture as ACE itself.
- **Scope creep** — the temptation to write a "real" client is
  high. The spike is a bot, not a game client. No rendering. No
  inventory UI. No anything a human would look at.

## What this spike does NOT do

- Does not modify `BotPlayer` in ACE-bots. The known bugs there
  (wall-walking, gliding, `{p}` leak, auto-face-player) remain
  open issues. The spike either makes them irrelevant by
  replacing the bot path, or proves it cannot, in which case
  those bugs become priority.
- Does not redistribute any Turbine binary or asset.
- Does not promise a working bot at the end. Phase gates are real.

## Repository layout (planned)

```
.worktrees/headless-client-spike/
  docs/research/headless-client/
    README.md          # this doc
  experiments/headless-client/    # added during Phase 1
    HeadlessAcClient.sln
    src/Kernel/
      LlmService/
      Pathfinder/
      WorldDb/
      MemoryStore/
      TrainingSink/
      Supervisor/
    src/Bot/
      Network/
      Perception/
      Motor/
      Action/
      Brain/
      Memory/
      Recorder/
    tests/
    data/              # gitignored; memory.db, training/
```

## Related work in this repo

- `docs/research/ace-investigation.md` — earlier ACE server
  investigation.
- `docs/research/networksession-virtualization.md` — prior work
  on virtualizing the server-side `NetworkSession`, which is the
  inverse problem to the one this spike attacks.
- `docs/research/related-work.md` — open-source AC1 tooling and
  bots.
- `docs/research/session-handoff-2026-05-28.md` — prior session
  context on the in-process `BotPlayer` work this spike runs
  alongside.
