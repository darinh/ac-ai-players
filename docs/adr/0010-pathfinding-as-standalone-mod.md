# 0010. Build pathfinding as a standalone ACE mod

- **Status:** Proposed
- **Date:** 2026-05-28
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

[ADR-0005](0005-pathfinding-reuse-and-build.md) decided that bots
reuse ACE motion and collision primitives while we build our own path
planner. Since then, the scope has widened: the same path planner is
useful for BotPlayer movement and for human-player quest helpers, such
as a `/path` command that points toward a quest target.

Putting pathfinding directly in `ACE.Server` would work for bots, but
it would make every planner iteration a fork change. Keeping it inside
bot-only code would make player quest helpers either duplicate the
planner or depend on bot internals. ACE already has a Harmony mod
loader with hot reload and shared ACE types, so pathfinding can live at
that boundary instead.

Why decide now: issues
[#50](https://github.com/darinh/ac-ai-players/issues/50) and
[#47](https://github.com/darinh/ac-ai-players/issues/47) introduce the
first reusable indoor planner. The packaging decision needs to be fixed
before BotPlayer starts taking a hard dependency on it.

## Decision

We build pathfinding as `ACE.Mod.Pathfinding`, a standalone ACE Harmony
mod that exposes a narrow `IPathfindingService`. ACE loads the mod in
process. Bots discover or receive the service at startup through a thin
accessor, and player-facing helpers call the same service instead of
owning separate pathfinding code.

The first implementation plans indoor paths over ACE's existing
`EnvCell` / `CellPortal` topology graph. Outdoor routing and higher-level
quest-target routing remain separate follow-up work.

## Options considered

### Option A — Standalone ACE Harmony mod

- Pros:
  - Reusable by BotPlayer and player quest helpers.
  - Keeps planner churn out of `ACE.Server` source files.
  - Uses ACE's existing mod loader, shared types, and command
    registration.
  - Can be deployed, disabled, or hot-reloaded independently from the
    fork.
- Cons:
  - Consumers need a small service-discovery layer instead of a direct
    project reference.
  - The mod must track ACE server type changes across upstream merges.
  - Test setup is still ACE-heavy because the useful geometry lives in
    ACE's DAT-backed runtime objects.

### Option B — In-tree `ACE.Server` pathfinding service

- Pros:
  - Direct calls from BotPlayer and commands.
  - Normal server build catches API drift immediately.
  - Easier to inject through server-owned services if ACE grows a DI
    container.
- Cons:
  - Every pathfinding change is a fork change.
  - Harder to reuse on servers that want quest helpers but not bots.
  - Larger upstream merge surface in hot server code.

### Option C — Bot-only planner in the bot implementation

- Pros:
  - Fastest path if only BotPlayer needed it.
  - No mod packaging or loader behavior to account for.
- Cons:
  - Duplicates work for player helpers.
  - Couples pathfinding to bot-specific brain and motor code.
  - Makes it harder to debug pathfinding from an in-game command.

## Consequences

- **Easier:**
  - One pathfinding implementation serves bots and human-player helper
    commands.
  - The fork stays smaller: the initial planner lands under `Mods/`
    instead of changing `Source/ACE.Server`.
  - The service boundary is explicit: `IPathfindingService` is the
    contract BotPlayer consumes.
- **Harder:**
  - Bot startup needs to locate the mod service, either by reflection or
    by a thin accessor once the mod reference story is settled.
  - Cross-component wiring goes through that accessor/DI boundary rather
    than direct `new IndoorPathfinder()` calls.
  - The mod can only be tested meaningfully against an ACE runtime with
    loaded cell data.
- **Follow-ups:**
  - Add the BotPlayer-side accessor that reads
    `ACE.Mod.Pathfinding.PathfindingMod.Service` and fails closed when
    the mod is absent.
  - Add a player `/path` helper on top of the same service.
  - Extend the planner beyond indoor portal graphs once outdoor routing
    is needed.

## References

- Related ADR(s):
  - [ADR-0005](0005-pathfinding-reuse-and-build.md) — reuse ACE motion
    primitives; build our own path planner
  - [ADR-0007](0007-bots-as-player-not-creature.md) — BotPlayer consumes
    this as a player-side service
- Related issue(s) / PR(s):
  - [#45](https://github.com/darinh/ac-ai-players/issues/45) — ADR-0010
    tracker
  - [#47](https://github.com/darinh/ac-ai-players/issues/47) — indoor
    portal-graph A*
  - [#50](https://github.com/darinh/ac-ai-players/issues/50) — mod
    scaffold
