# Road to endgame — headless-client plan

The ordered work to take the headless network client from "stuck in the
starter academy" to the north star. Refined coarse-to-fine and ratified
by a three-model adversarial review (GPT-5.3-Codex, Gemini 3.1 Pro,
Claude Opus 4.6; all APPROVE-WITH-CHANGES, changes folded in below).

See also: [`improvement-loop.md`](improvement-loop.md),
[`headless-revival.md`](headless-revival.md),
[`plan-vocabulary.md`](plan-vocabulary.md).

## North star (standing directive)

The headless client is complete when an autonomous player can:

1. Use a SHARED world navmesh (static; world-nav / ACE.Mod.Pathfinding)
   PLUS a PER-BOT personal navmesh populated by its own exploration.
2. Beat the game as ANY race / heritage / player class.

The LLM is a quest COMPILER (decides WHAT), not a controller. The motor
executes the mechanical HOW. No hardcoded game knowledge (NPC names,
wcids, quest names, landblock ids, type priorities) in source — that
lives in the prompt or is learned from in-game text, enforced by the
`audit-hardcoded-knowledge` skill on every decision-code commit.

## Where we are (evidence, 2026-06-03)

- LLM quest-compiler pipeline is healthy and live (PR #125 fixed the
  goal_id parse bug; verified `source=llm:...`, parse-error=0).
- Per-bot personal navmesh ships and is populated from each bot's own
  walking (PR #126; `data/nav/<CharName>/`).
- Revival Slices 1-8 (FOV discovery, navgraph wiring, cross-cell /
  cross-landblock / seam-edge on-foot traversal) are merged.
- HARD BLOCKER: a bot cannot get OUT of the starter academy. It is stuck
  in landblock 0x8602, indoors. Everything downstream (outdoor combat,
  leveling, world navmesh, beating the game) is unobservable until a bot
  gets outdoors.

### The academy blocker, characterized

Live run `ExitWatch65309` (~12 min): the Society Greeter Tells the bot
"Why don't you go talk to the Agent in the next room? Double-click the
doors to open them." The LLM correctly emits `Use{Door}`; the door opens
(`UseDone(ok)`, `door-USE ... distFromDoorway=0.05u waypoint=2/10`). But:

- No NPC named "Agent" ever appears in the bot's perception (only the
  Greeter's Tell text mentions it).
- The bot re-locks the already-visited Society Greeter (action cycles
  #3,4,6) and never reaches the next room.
- It never gets the `AcademyTokenGiven` stamp (from academyguard-
  trainingmaster WCID 29320) that the exit portal WCID 31061 requires.

"Agent" is a ROLE/TYPE ("the employee in the next room"), NOT a proper
name to Name-match — the real NPC has a different name. So comprehension
splits cleanly:

- SPATIAL (the gap): go through the door into the next room. This is all
  the motor needs — a purely spatial "traverse the doorway" directive.
- ROLE RESOLUTION (already works, deferred): once in the next room and
  its NPCs become perceivable, the LLM picks the role-appropriate one to
  Talk to, from in-game text. No name match, no hardcoded role table.

The gap is narrow and spatial: nothing converts "go to the next room"
into motion into an UNPERCEIVED adjacent room. Today's `Explore` walks to
ALREADY-KNOWN objects in the current landblock, and the indoor
pathfinder needs a live target entity with a CellId — neither can target
"the room on the far side of this door" before anything in it has been
perceived.

## Coarse phases (high level)

- Phase A — ESCAPE THE ACADEMY. Complete the gated indoor intro chain
  and step outdoors. First slice: spatial "go through a door into an
  unperceived next room."
- Phase B — OUTDOOR SURVIVAL LOOP. Perceive monsters, fight, loot, heal,
  survive a leveling loop across landblocks.
- Phase C — NAVMESH MATURATION. Consume the personal navmesh for route
  planning (with minimal edge invalidation), then full edge lifecycle,
  then integrate the shared world navmesh.
- Phase D — PROGRESSION GENERALITY. Beat the game across races /
  heritages / classes per the frozen success criterion below.
- Phase E — ROBUSTNESS & SCALE. Multi-bot, crash recovery, anti-stuck,
  observability, training-data capture.

Each slice ships independently: build clean + full test suite + at least
one LIVE run as a verification signal + hardcoded-knowledge audit before
push.

---

## Phase A — Escape the academy (refined)

### A0. Root-cause the stall precisely (investigation, no code)

Run the academy intro MULTIPLE times (not one noisy trace), instrumented
on the door-open moment, to decide which hypotheses hold. They imply
DIFFERENT fixes, so this gates A1 — do NOT write A1 code until A0 is
resolved with a ledger row citing the proving log lines.

- H1 — No step-through: the door opens but the bot never advances past
  it; motion completes and re-deliberation re-locks the nearest visited
  NPC.
- H2 — PVS requires physical cell entry: opening a door does NOT load the
  next cell's objects; the server's visible set is driven by the player's
  current `EnvCell`, so the bot must physically enter the doorway/next
  cell before the Agent's ObjectCreate arrives. (Document this AC
  assumption explicitly; the instrumented run must look for ObjectCreate
  messages BETWEEN the door-USE and the next position update. If some
  ACE build DOES broadcast adjacent-cell objects on door-open, A1
  simplifies.)
- H3 — Perceived but deprioritized: the Agent IS briefly perceived but
  the LLM keeps choosing the visited Greeter (a prompt problem).
- H4 — Near-side destination: the walk DESTINATION is the door entity's
  position (or the Greeter), which sits on the bot's side of the
  threshold. The motor walked exactly where it was told; the door opened
  as a side effect of the pre-emptor. Fix differs from H1: the
  destination must be retargeted to the FAR side, not "continue after
  open". The run must record the bot's `motionTarget` position relative
  to the door (before vs beyond).
- H5 — Occlusion / radar range: the bot steps through and the next cell
  loads, but the Agent is occluded (around a corner / pillar) or beyond
  perception range within the room. Implies an in-room EXPLORE SWEEP is
  needed, not just a one-step doorway crossing.

Deliverable: a ledger row naming the hypotheses that hold, with log
evidence. Likely a combination (e.g. H4+H2).

### A1. Go THROUGH a door into an unperceived next room (spatial only)

Goal: a purely SPATIAL "traverse this doorway into the next room"
capability. The LLM, reading a hint like "the Agent in the next room —
open the doors", selects a VISIBLE door as an `Explore` target in an
explicit DOOR-TRANSITION mode; the motor walks to the doorway, opens it,
crosses into the adjacent cell, waits for that room to load, then
releases the lock so the next deliberation sees the new room. Target/role
resolution is NOT in this slice — once new NPCs are perceived, the LLM
picks the role-appropriate one (A2).

Hard constraints:

- The `Explore` target is a VISIBLE DOOR (already perceived; has a Guid +
  CellId) — NOT the unseen "Agent". This sidesteps "can't path to an
  unperceived entity" entirely: path to the doorway, then cross.
- Step-through is legal ONLY in the explicit door-transition Explore mode
  the LLM requested — NEVER as a side effect of a generic `Use{Door}`.
  Ship a regression test proving plain "open the door, don't traverse"
  still leaves the bot on the near side.
- NO hardcoded "Agent" / academy / room-layout / role knowledge. The LLM
  chooses WHICH door from the visible list based on the hint; the motor
  only mechanically traverses the chosen door. Door is a wire-protocol
  flag, not game knowledge (audit-safe).
- Crossing a doorway is MECHANICAL nav infrastructure, consistent with
  the existing door-USE pre-emptor's documented audit override.

Mechanism (consensus of the review — do NOT use a blind vector):

1. Match the selected Door entity to a `WalkableNodeKind.Doorway` node in
   the static `IndoorNavGraph` (reuse the 3u match the door-USE
   pre-emptor already does at HandshakeDriver.cs ~4254-4276). AC dungeon
   cells are non-Euclidean BSP/portal spaces — "one cell past along a
   normal" is mathematically unsound and will clip into walls / void.
2. From the doorway's connection data, identify the TWO cells it
   connects. Determine which the bot currently occupies (by `CellId`).
   The step-through target is a VALID interior coordinate (a WalkableNode
   centroid) of the OTHER cell — never raw vector math.
3. Walk to the DOORWAY centroid (the cell-connection centroid), NOT the
   individual Door object's centroid: "Double-click the doors" implies
   split double-doors whose object centroids are offset from the actual
   opening; aiming at a leaf snags the bot on the frame.
4. Open the door (existing pre-emptor); wait for `UseDone(ok)` and the
   door's open state before advancing, or the server rubberbands the bot
   back outside.
5. Advance to the far-cell interior coordinate. SUCCESS = the bot's
   `CellId` changes to the far cell AND/OR a new-room ObjectCreate / PVS
   delta arrives. Use a bounded attempt budget; on failure log a
   diagnostic and release the lock (do not thrash).
6. Fallback: if the bot's current cell is neither of the two cells the
   doorway connects (e.g. it is looking through an already-open door from
   an adjacent room), reject the traversal and log it rather than guess.

LLM/prompt contract (prevents the loop the review flagged):

- Add a rule: when a server hint says a target (by role or name) is in
  "the next room" / "through" / "open the doors" and you do NOT see that
  target, emit `Explore{door-transition, target = a VISIBLE door}` to
  cross; do not re-Talk a visited NPC.
- Distinguish "I need to ENTER the next room" from "I am now IN the next
  room and should sweep its corners before leaving". After crossing, if
  the role target is not visible, prefer an UNVISITED door/NPC in the new
  room over the door just used; only cross another threshold once the
  current room is swept. Leverage the existing `Visited` candidate flag.
- Soft bound: after N (~5) door traversals without finding the target,
  emit a diagnostic and fall back to undirected `Explore`.

Mechanical guard (motor, audit-safe): a short-term "just-traversed" memory
so the bot cannot immediately cross back through the doorway it just used
(time-boxed, e.g. a few seconds) — prevents oscillation when the LLM is
momentarily blind. This is a rate-limit, not a strategy.

Files (expected): `HandshakeDriver.cs` (door-transition step-through +
just-traversed guard), `LlmGoalPolicy.cs` (prompt rule + door-transition
Explore surfacing), `ExplorationCandidate.cs` (TRANSITION/door tag; update
the "no door hint" comment). Risk: 🟡 (motion state machine). Tests:
far-cell-target resolution from connection data; doorway-centroid vs
door-object-centroid; open-only regression (no traversal); just-traversed
no-backtrack; multi-door candidate selection. Verify (live): the bot
enters a NEW cell/room it had not perceived, then PERCEIVES a new NPC.

### A2. Complete the academy intro chain to the exit stamp

With A1 unblocking room-to-room discovery, let the LLM drive the full
chain off in-game text: read the Letter From Home, give the Calling Stone
where directed, reach the trainer NPC (resolved by ROLE/dialog, not a
Name match), obtain the `AcademyTokenGiven` stamp, then use exit portal
WCID 31061. No new hardcoding if A1 works. If a step stalls, root-cause
it the A0 way. Verify: the bot leaves landblock 0x8602 (position updates
show a different, outdoor landblock).

### A3. Indoor-loop guardrails (only if A1/A2 expose them)

If runs show pathological loops, tighten the existing LOOP-BREAK prompt
rules, the door-USE cooldown, and the just-traversed guard — prefer
prompt + mechanical rate-limit over new content rules.

---

## Phase B — Outdoor survival loop (coarse → first refinement)

Prereq: Phase A (a bot outdoors). First observable goal: perceive a
monster, attack it, survive, loot a corpse outdoors.

- B1. Outdoor perception + threat surfacing: monsters (vs NPCs) tagged in
  the prompt outdoors; verify `Attack` dispatch outdoors.
- B2. Combat sustain. RISK FLAG: AC combat is a server-validated
  real-time motion-state dance (CM_Combat: attack height, power bar,
  weapon-speed timing). The headless client sends a targeted-attack
  opcode but does not yet drive the combat motion-state machine — B2 is
  significantly more involved than door traversal and may be its own
  multi-slice effort. Heal/flee at health-critical (the LLM already has
  priority 9-10 rules; verify they fire on real outdoor damage).
- B3. Outdoor pathing to a sighted target across the open landblock,
  exercising the Slice 5-8 seam-crossing machinery LIVE for the first
  time.
- B4. Minimal leveling loop: find → kill → loot → repeat, gaining XP.

Refine B1-B4 after A ships and outdoor behavior is observable.

---

## Phase C — Navmesh maturation (coarse)

- C1. Consume the personal navmesh for planning (route the picker/motor
  through `NavGraph.FindRoute`) — BUNDLED WITH minimal edge invalidation.
  Per the review, a consumer without ANY edge validity will blindly route
  through closed doors, one-way drops, and dead routes and stuck-loop. So
  C1 includes a minimal `confirmed / failed` edge state with a TTL /
  penalty applied by the executor on a failed traversal. This is the
  smallest validity model that makes the planner usable.
- C2. FULL edge lifecycle (NavGraph.cs:70-130 TODOs): the orthogonal
  `NavEdgeLifecycle {Permanent, SingleUse, Cooldown, Ephemeral,
  Conditional, Unknown}` taxonomy + portal subtype, populated from the
  LLM compiler and observed failures, hard-filtered by the planner. This
  is the richer model built ON TOP of C1's minimal invalidation — not a
  prerequisite re-derivation.
- C3. Shared world navmesh: integrate world-nav / ACE.Mod.Pathfinding as
  the L0 static layer beneath the personal graph.

Refine after Phase B proves a bot survives long enough to accumulate a
useful personal graph.

---

## Phase D — Progression generality (coarse)

- D1. Class-aware combat (melee / missile / caster) driven by perceived
  equipment + spells, not hardcoded class tables.
- D2. Longer quest chains: the LLM compiler following multi-step quests
  end-to-end; predicate-based completion already exists.
- D3. Race/heritage coverage: verify the intro + early game across
  starter towns (not only Aluvian/Holtburg). Death + lifestone recovery.

### Beating the game — FROZEN success criterion

Per the review, this is pinned now so D/E cannot drift:

- PRIMARY (D-def-1): from a FRESH roll, autonomously reach a leveling
  milestone (target: a defined character level — start gate at level 10,
  stretch to higher milestones), REPEATABLE across a class/heritage
  MATRIX (at least one of each: melee / missile / caster archetype, on at
  least two heritages). Leveling is isolated, mathematically verifiable
  (server-authoritative level/XP), and proves self-sustainability —
  unlike flagship questlines, which carry multi-hour respawn timers and
  multi-bot concurrency hazards.
- FALLBACK / STRETCH (D-def-2): complete one canonical flagship
  questline end-to-end.

Phase gates tie to D-def-1: a slice in B/C/D is "done for the endgame"
only insofar as it advances a fresh roll toward the milestone, measured
by server-authoritative level/XP. (User may override the criterion; this
is the assumed default — see open question 1.)

---

## Phase E — Robustness & scale (coarse)

- E1. Crash/reconnect recovery (the charlist-quirk retry todo lives here).
- E2. Anti-stuck: lift the retired server-side recovery ticks' INTENT
  into the client as LLM-driven recovery, not hardcoded rules.
- E3. Multi-bot operation + per-bot isolation (personal navmesh already
  per-character; verify no cross-talk under concurrency).
- E4. Observability + training-data capture for the improvement loop.

---

## Sequencing and gates

1. A0 (investigate, multiple runs) → A1 (door-transition) → A2 (exit) —
   STRICT order; A1 design is gated on A0's hypotheses (H1/H4 vs H2 vs H5
   imply different mechanisms).
2. B before C: do not build navmesh consumption until a bot survives
   outdoors long enough to accumulate a graph worth planning on.
3. C1 ships WITH minimal edge invalidation (not after); C2's full
   lifecycle taxonomy builds on C1.
4. D's frozen criterion (D-def-1) governs what "endgame progress" means;
   every B/C/D slice is measured against a fresh roll's level/XP.

## Risks and mitigations

- Hardcoding creep (the Slice U incident): every decision-code commit
  runs the audit; A1 forbids NPC/room/role hardcoding and routes
  comprehension through the LLM + in-game text; door/cell flags are
  wire-protocol, not game knowledge.
- Building the wrong thing: A0 is an evidence gate (multiple runs) before
  A1 code; the door-transition mechanism uses existing IndoorNavGraph
  connection data, not invented geometry.
- Loops / rubberbanding: explicit prompt enter-vs-sweep contract, a
  mechanical just-traversed no-backtrack guard, wait-for-UseDone before
  advancing, and a bounded traversal attempt budget.
- Speculative complexity: C2's full lifecycle builds on C1's minimal
  invalidation rather than being designed up front.
- Combat surprise: B2 is flagged as a likely multi-slice combat
  motion-state effort, not a one-liner.
- Unobservable progress: each slice requires a LIVE run signal.

## Open questions

1. Confirm the frozen "beat the game" criterion (D-def-1 level milestone,
   class/heritage matrix). The plan proceeds on this default unless the
   user changes it.
2. A0 outcome: which hypotheses (H1-H5, likely a combination) actually
   hold — resolved by the instrumented runs.
3. Whether the shared world navmesh (C3) is required for "complete" or
   whether the per-bot personal navmesh alone satisfies the directive.
