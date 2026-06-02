# Pilot Track — autonomous improvement loop

**Status:** Active directive. Supersedes default agent posture in
[`../../AGENTS.md`](../../AGENTS.md) until the success criteria below are
met. **Do not stop and wait for the user** during normal loop execution.

> **RUNTIME (read first):** the bot is a **distributed headless
> client** ([`../../experiments/headless-client/`](../../experiments/headless-client)),
> not a server-side `BotPlayer` object. The server-side
> `BotPlayer.cs` track in the `darinh/ACE-bots` fork is **retired**.
> The client's source is being consolidated from
> `anvil/portal-walkable-nodes` onto `main`; until that lands and the
> gameplay loop resumes, the current work is the consolidation
> slices in [`headless-revival.md`](headless-revival.md).

## North star

Pilot-01 — a single autonomous bot — plays Asheron's Call competently
on any ACE server (canonical and custom-content) from the tutorial
through endgame, then recruits and coordinates other bots through every
social system the game supports.

## Done means all of these are true

1. Pilot-01 spawns at the canonical tutorial and finishes it unassisted.
2. Pilot-01 navigates from a starter town to a hunting zone of
   appropriate level, completes a kill task, and turns it in.
3. Pilot-01 reads NPC tells, the LLM compiler emits a Plan, the BT
   executes it, and the reward is collected.
4. Pilot-01 dies, recovers at lifestone, and returns to hunting
   without operator help.
5. Pilot-01 forms a fellowship with at least one other bot.
6. A monarch bot accepts allegiance from at least one vassal bot.
7. A 6-bot fellowship clears a known low-tier dungeon end-to-end.
8. A 6-bot fellowship clears a known endgame raid (e.g., Bandit Castle
   tier or equivalent on the running server).
9. Bot players use the in-game guild / monarchy / chat-channel
   features the same way human players do — joining, swearing,
   sharing chat, breaking allegiance, etc.

When all nine hold, the loop exits and the user is notified.

## Where state lives (read before each loop iteration)

| Kind | Location | Purpose |
|---|---|---|
| Session plan | `~/.copilot/session-state/<id>/plan.md` (v5) | Authoritative architectural plan, Needs Catalog, Preemption Matrix |
| Session checkpoints | `~/.copilot/session-state/<id>/checkpoints/` | What prior sessions accomplished; current loop position |
| Live server log | `C:\ACE\Logs\ACE_Log.txt` | Ground truth for bot behavior |
| Live service | `Get-Service ACEServer` | NSSM-wrapped ACE.Server.exe |
| Code | `experiments/headless-client/` in THIS repo (canonical branch `anvil/portal-walkable-nodes`, consolidating to `main`) | Bot implementation (distributed headless client) |
| Docs progress | `docs/pilot/` in this repo | Vocabulary, ADRs (when written), this loop |
| Memories | Copilot memory store, subject `pilot-loop` | Cross-session learnings |

## The loop

Each iteration follows these phases. Do all of them; do not skip
verification.

### Phase 0 — Orient (≤ 2 min)

- Read `plan.md` if not yet loaded this session.
- Read the most recent 1–2 checkpoints.
- Read this doc.
- `git --no-pager log -5 --oneline` in this repo to know the latest headless-client code.
- Check `Get-Service ACEServer` status.
- Tail the last 60 lines of `C:\ACE\Logs\ACE_Log.txt`.

### Phase 1 — Observe (≤ 5 min)

Goal: state, in one sentence, **what the bot is currently failing to do
that a real player would do here**.

- Is the bot spawned? (auto-roster on world-open)
- What is the bot doing in the log? (`grep` for `Bot` / `BotPlayer` /
  the bot's name)
- If the bot is idle, why? What's the next thing a player would do?
- If the bot did something, was it the right thing? Wrong target?
  Wrong action? Right action but failed?

Record findings inline in your response so future sessions can see them.

### Phase 2 — Pick the smallest unblock (≤ 2 min)

Pick **one** capability gap that, if fixed, would let the bot do one
more thing. Bias hard toward small:

- "Bot doesn't attack mobs in range" → smaller than → "bot doesn't
  navigate to mobs"
- "Bot doesn't loot corpses" → smaller than → "bot doesn't sell loot"
- "Bot doesn't accept fellowship invite" → smaller than → "bot doesn't
  send fellowship invite"

If two gaps are equally small, pick the one that unblocks more downstream
behavior. The plan vocabulary doc (`plan-vocabulary.md`) gives a forward
view of what will eventually need to work.

Do NOT pick a multi-day project. If the smallest gap is still 3+ days
of work, decompose it further.

### Phase 3 — Build (variable)

In `experiments/headless-client/` in this repo (on `main` once
consolidated, or an `anvil/<task-id>` worktree branch for risky
work):

- Implement the smallest viable change.
- Keep changes scoped: one capability per commit when possible.
- Reuse the existing client subsystems (Handshake, World/WorldState,
  Strategy, Tactics, NavGraph, IndoorNavService, world-nav) rather
  than reinventing them.
- Behind a config flag when a behavior might regress others.
- Match the existing HeadlessAcClient folder style.

### Phase 4 — Deploy (≤ 3 min)

The bot is a client process, not the server. The ACE server runs
continuously as the NSSM `ACEServer` service — you do NOT rebuild or
restart it to deploy bot changes; you only need it up
(`Get-Service ACEServer`; confirm `World is now open` in
`C:\ACE\Logs\ACE_Log.txt`).

- Build the client: `dotnet build experiments/headless-client/HeadlessAcClient.sln -c Release` (verify exit 0).
- Launch one (or more) client processes against the server:
  `dotnet run --project experiments/headless-client/src/HeadlessAcClient -c Release -- <args>` (see `Program.cs` for the arg / config surface and account credentials).
- Each client instance is one bot; run several for distributed /
  fellowship behavior.

### Phase 5 — Verify (≤ 10 min)

- Run the client's own test suite first:
  `dotnet test experiments/headless-client/tests/HeadlessAcClient.Tests` (baseline: 440 passing). Do not ship a red suite.
- Launch the client against the live server and watch BOTH the
  client's stdout / log AND the server log `C:\ACE\Logs\ACE_Log.txt`.
- Wait 30–120 seconds for the new behavior to have a chance to fire.
- Confirm the targeted log line appears (success) OR the failure mode
  changes to something else (also progress — pick that up next iteration).
- If the change broke an earlier capability (regression), revert or
  fix forward in the same iteration. Do not move on with a regression.

### Phase 6 — Commit + push (≤ 3 min)

- `git add -p` the relevant files in this repo
  (`experiments/headless-client/`).
- Commit message style: imperative title naming the capability, body
  describing what changed and what log evidence proved it works.
  Include the `Co-authored-by: Copilot` trailer.
- `git push origin <branch>` (the consolidated `main`, or the
  `anvil/<task-id>` branch for review-bound work).

### Phase 7 — Checkpoint + assess (≤ 3 min)

- Write a short session checkpoint with: what gap was picked, what was
  built, what the log showed, what the next-smallest gap is.
- Mirror landmark capability gains into `docs/pilot/README.md`'s
  milestone table.
- **Context check:** if remaining context is low (say, < 25% by
  rough estimate), stop after checkpointing. The scheduled kick will
  start a fresh session that resumes from the checkpoint.
- Otherwise: loop back to Phase 1.

## Anti-patterns (don't do these)

- **Writing ADRs about already-decided things.** The plan locks the
  architecture. ADRs get written when their topic actually blocks code.
- **Pre-emptive abstraction.** Don't build an IBotPerception API
  before any code needs to call it. Build the first call site, then
  extract.
- **Claiming done without log verification.** If the log doesn't show
  the new behavior, it doesn't work.
- **Cascading rewrites.** Fix one thing per iteration. Resist
  refactoring on the same commit.
- **Asking the user a question.** The user has delegated. Make a
  decision, state the assumption in the checkpoint, move on.
- **Reading the whole codebase before changing anything.** Read the
  one file you need to change plus its immediate neighbors. Re-read
  later if needed.
- **Stopping at a "good place to break."** Stop only when out of
  context, blocked by a true external dependency, or the success
  criteria are met.

## True blockers (acceptable reasons to stop and notify)

These are rare. Most "blockers" are not.

- ACE.Server.exe won't build for a reason that requires upstream
  schema / dat-file changes.
- The Windows service is in a state only the user can fix
  (e.g., NSSM unregistered itself, credentials expired).
- An external API the bot depends on (when one is added) has changed
  its contract in a way that requires user-side credentials.
- Hardware / OS failure.

If you hit one of these, checkpoint thoroughly with the exact symptom,
the diagnostic steps already taken, and the minimal user action needed.
Then stop.

Everything else — including "I don't know how this ACE subsystem works"
— is solvable by reading code, writing a probe, or trying and observing.

## Scheduled auto-kick

A scheduled prompt named `pilot-loop` previously woke a fresh Copilot
session every 2 hours (see `manage_schedule`). It was **stopped on
2026-06-02** because it drove the now-retired server-side track.
**Do not re-create it** until the headless client is consolidated onto
`main` and builds + runs there (slice 1 in
[`headless-revival.md`](headless-revival.md)). When re-created, each
kick is self-contained: it reads this doc, the latest checkpoint, the
plan, runs one or more loop iterations, and checkpoints out.

## When the loop is done

When all 9 done criteria hold:

1. Stop the scheduled kick (`manage_schedule action=stop`).
2. Write a final checkpoint summarizing the journey, the architecture
   as it actually shipped, and what's left for v2.
3. Update `docs/pilot/README.md` milestone table to "shipped".
4. Notify the user in a final session.

## Related

- [`plan-vocabulary.md`](plan-vocabulary.md) — what the bot's brain
  ultimately compiles to.
- [`README.md`](README.md) — Pilot Track folder index.
- Session plan: `~/.copilot/session-state/<id>/plan.md` (v5).
