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
| Headless client | **`main`** (consolidated 2026-06-02 from `anvil/portal-walkable-nodes`) | **Builds clean + 440/440 tests pass from the main checkout.** |
| world-nav navmesh | `main` | On `main`; consumed by the client. ACE-bots cross-repo refs now resolve from either a normal checkout or a worktree (dual-depth probe in the csproj). |
| services-common (`AcAiPlayers.ServicesClient`) | **`main`** | Consolidated; build dep of the client. |
| ACE.Mod.Pathfinding | `ACE-bots/.worktrees/pathfinding-mod` | Functional indoor A* + `/pathfind-debug`; git worktree orphaned (gitdir points at a dead `ACE` clone). Indoor-only. |

Build deps of the client (all resolve today in the worktree):
ACE-bots `Source/{ACE.Common,ACE.Entity,ACE.DatLoader}` (protocol /
DAT only), `AcAiPlayers.ServicesClient`, `AcAiPlayers.WorldNav`.
Target `net10.0`; `TreatWarningsAsErrors=true`.

## Ordered slices

Each slice is independently shippable. Verify (build + 440 tests
and, where relevant, a live client run) before committing.

1. **Consolidate to `main`.** ✅ DONE 2026-06-02 (commits `2a9174d`
   merge, `de520bd` build fix). Merged `experiments/{headless-client,
   services-common,api-host}` + world-nav deltas + research specs from
   `anvil/portal-walkable-nodes` onto `main`; build + 440 tests green
   from the main checkout; world-nav csproj now probes both checkout
   depths for the ACE-bots sibling.
2. **Live smoke.** ✅ DONE 2026-06-02. A client (`dotnet run` from
   `main`, account `spike-bot`, fresh char name) drove the full chain
   against the live `ACEServer`: CharacterCreate Ok
   (`guid=0x5000005C`) -> EnterWorld -> IndoorNav loaded the academy
   navmesh (`landblock 0x8602: cells=568 bridges=756
   walk-nodes=31910`) -> perception (Door, Bruised Apple) -> LLM goal
   compile (`success goal=kind=Use target="Door"`, llama-3.3-70b) ->
   movement via GameActionMoveToState. Evidence:
   `files/smoke-run-04.log` (session folder). Two minor follow-ups
   filed: CharacterList=0-vs-NameInUse create loop; first-call LLM
   goal-id parse-error fallback.
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
