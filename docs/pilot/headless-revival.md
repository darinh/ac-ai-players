# Headless-client revival plan

**Status:** Active. This is the current execution track for the
Pilot Track loop ([`improvement-loop.md`](improvement-loop.md)).

## Why this doc exists

Several sessions drifted from the documented architecture (a
distributed headless client plus `world-nav` pathfinding) onto a
server-side `BotPlayer.cs` track in the `darinh/ACE-bots` fork that
walks in straight lines. The user reaffirmed the original intent on
2026-06-02:

> Run the bots against the server from a distributed architecture.
> Finish the pathfinding mod and headless client. The bots discover
> nodes from their visible field of view, remember them, and never
> forget how to reach them.

This doc records the evidence-backed state of the parked work and
the ordered slices to bring it back to life.

## Architecture (target)

- **Runtime:** a distributed headless AC network client
  ([`experiments/headless-client/`](../../experiments/headless-client)).
  Each process is one bot; it logs into the ACE server like a real
  player and plays over the wire. No server-side bot objects, no
  admin commands.
- **Pathfinding (indoor):** `world-nav` static navmesh from the AC1
  DAT files ([`experiments/world-nav/`](../../experiments/world-nav)),
  consumed directly by the client's `IndoorNavService`. Straight
  line is only a fallback.
- **Persistent map memory:** `NavGraph` records visited nodes and
  observed entities and persists them as JSONL ("discover, then
  never forget"). Per-bot, layered on the static navmesh.
- **Optional server hook:** `ACE.Mod.Pathfinding`
  ([ADR-0010](../adr/0010-pathfinding-as-standalone-mod.md)) exposes
  the same indoor A* server-side behind `IPathfindingService`, for a
  player `/path` helper. Not required by the client.

## State of the parked work (verified 2026-06-02)

| Component | Location | State |
|---|---|---|
| Headless client | `anvil/portal-walkable-nodes` (158 commits, canonical; superset of `llm-deliberation-race`) | **Builds clean, 440/440 tests pass.** Source NOT yet on `main` (only `data/`). |
| world-nav navmesh | `main` | On `main`; consumed by the client. |
| services-common (`AcAiPlayers.ServicesClient`) | spike worktrees only | Build dep of the client; NOT on `main`. |
| ACE.Mod.Pathfinding | `ACE-bots/.worktrees/pathfinding-mod` | Functional indoor A* + `/pathfind-debug`; git worktree orphaned (gitdir points at a dead `ACE` clone). Indoor-only. |

Build deps of the client (all resolve today in the worktree):
ACE-bots `Source/{ACE.Common,ACE.Entity,ACE.DatLoader}` (protocol /
DAT only), `AcAiPlayers.ServicesClient`, `AcAiPlayers.WorldNav`.
Target `net10.0`; `TreatWarningsAsErrors=true`.

## Ordered slices

Each slice is independently shippable. Verify (build + 440 tests
and, where relevant, a live client run) before committing.

1. **Consolidate to `main`.** Bring `experiments/headless-client`,
   `experiments/services-common`, and any `world-nav` deltas from
   `anvil/portal-walkable-nodes` onto `main` (merge or subtree-import
   the `experiments/` tree). Confirm build + 440 tests on `main`.
   Settle the ACE-bots cross-repo `ProjectReference`s (siblings must
   be checked out; document the assumption like world-nav's README).
2. **Live smoke.** Run one client process against the live
   `ACEServer`; confirm login then enter-world then perception then
   an LLM goal then movement via `IndoorNavService`. Capture client
   and server log evidence.
3. **FOV node discovery.** Add synthesis of new nav nodes from the
   bot's visible objects (sight-line / radius), persisted in
   `NavGraph` — the missing half of "discover, never forget". Builds
   on the existing `RecordVisit` / `RecordObservation`.
4. **Finish NavGraph wiring** (`nav-graph-wire`,
   `navgraph-doorway-kind` todos): route the picker through NavGraph
   routes; retire `NavGraphRecorder`; add a Doorway node kind.
5. **NavGraph edge lifecycle** (TODOs at `NavGraph.cs:70-130`):
   single-use / ephemeral / time-bounded edges; portal subtype
   schema.
6. **Outdoor routing.** Extend pathfinding beyond indoor portal
   graphs (currently indoor-only in both world-nav and the mod).
7. **ACE.Mod.Pathfinding (optional, parallel).** Re-home the
   orphaned worktree under a valid worktree of `ACE-bots`; build
   against the server; wire the `/path` player helper. Not on the
   client's critical path.

## Operational notes

- The scheduled `pilot-loop` auto-kick was **stopped** on 2026-06-02
  because it drove the retired server-side track. Re-create it
  (pointing at this loop) only after slice 1 lands and the client
  builds and runs from `main`.
- Deploy model: the ACE server stays up (NSSM `ACEServer`); deploying
  a bot change means rebuilding and re-launching the **client**, not
  the server.

## Related

- [`improvement-loop.md`](improvement-loop.md) — the loop, retargeted
  to the headless client.
- [ADR-0010](../adr/0010-pathfinding-as-standalone-mod.md) — pathfinding mod.
- [ADR-0011](../adr/0011-bot-brain-agent-loop.md) and
  [`../design/bot-brain-agent-loop.md`](../design/bot-brain-agent-loop.md)
  — the six-stage brain. NOTE: written around server-side
  `BotPlayer` / `OnBrainTick`; the stage logic is portable to the
  client but the doc framing still needs reconciling (tracked, not
  yet done).
