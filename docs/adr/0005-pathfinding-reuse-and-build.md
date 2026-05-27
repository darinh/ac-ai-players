# 0005. Reuse ACE motion primitives; build our own path planner

- **Status:** Proposed
- **Date:** 2026-05-27
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

[`docs/research/ace-investigation.md`](../research/ace-investigation.md)
Q4 asked what pathfinding ACE has today and what bots can reuse.

Investigation of `C:\Users\darin\repos\ACE` master @ `9bc20cbd` found:

- ACE loads landblock collision/geometry from the cell DAT and portal
  DAT at startup (`Source/ACE.Server/Program.cs:239`,
  `Source/ACE.Server/Entity/Landblock.cs:174-180`,
  `Source/ACE.Server/Entity/LandblockMesh.cs:53-96`). Walkable
  geometry exists as a triangulated mesh per landblock and a BSP for
  collision (`Source/ACE.Server/Physics/BSP/BSPTree.cs:17-31`).
- Existing monster AI does **not** pathfind. `Monster_Tick` issues
  `MoveToObject`/`TurnTo` and lets the physics engine handle
  collision; if the target moves out of `MaxChaseRange`, the chase
  is cancelled
  (`Source/ACE.Server/WorldObjects/Monster_Tick.cs:51-64`,
  `Source/ACE.Server/WorldObjects/Monster_Navigation.cs:239-253`).
- No A\* implementation, no navmesh, no Recast/Detour anywhere in the
  source tree. Search for `navmesh`, `Recast`, `Detour`, `pathfind`
  returns no real hits in `Source/ACE.Server`.
- Line-of-sight is available via
  `WorldObject.IsVisibleTarget(...)` and the `PhysicsObj.transition(...)`
  collision/LOS test
  (`Source/ACE.Server/WorldObjects/WorldObject.cs:290-296, 330-398`).
- Portals teleport via `WorldManager.ThreadSafeTeleport(...)` and are
  invoked through `Portal.CheckUseRequirements` then
  `Portal.OnActivate` (`Source/ACE.Server/WorldObjects/Portal.cs:103-292`).

Why decide now: pathfinding is the single largest piece of Motor-layer
work in [`milestones.md`](../../roadmap/milestones.md) M2. The
reuse-vs-build decision determines M2 scope and whether we owe a
third-party dependency to the fork.

## Decision

For M2 and M3, bots reuse ACE's motion execution (`MoveToObject`,
`TurnTo`, `PhysicsObj.transition`) and collision/LOS primitives. We
build our own simple path planner — line-of-sight first, with authored
waypoint graphs per dungeon zone where LOS isn't enough. We do not
import a third-party navmesh library.

Portal traversal reuses ACE's existing portal-use code path. Bots
"use" portals the same way the player-`HandleActionUseItem` path does.

Before M4, we instrument the planner's "stuck rate" (% of move
attempts that fail to reach destination within a budget) per zone. If
stuck rate exceeds 5% in any zone we care about, we revisit and
consider importing Recast/Detour for that zone's geometry.

## Options considered

### Option A — Reuse motion + collision; build a simple LOS+waypoint planner

- Pros:
  - No third-party dependency in the fork.
  - Smallest M2 scope: a planner that handles "open terrain by LOS,
    dungeons by authored waypoints" is achievable in M2 budget.
  - Authored waypoints per zone double as content for the BotDirector
    spawn rules in M3.
  - Aligns with the minimum-fork-bar
    ([`0002-minimal-fork-bar.md`](0002-minimal-fork-bar.md)): no
    motion code change required upstream; everything is additive in
    our planner.
- Cons:
  - Authored waypoints are content work that scales with zones added.
  - LOS-first planning will look stupid in maze-like dungeon geometry
    until waypoints are authored for it.
  - We own the planner's edge cases.

### Option B — Import Recast/Detour; build a navmesh from landblock geometry

- Pros:
  - Industry-standard navmesh approach used by most modern games.
  - No per-zone authoring needed — generated from geometry.
  - Handles arbitrary dungeon shapes well.
- Cons:
  - Recast/Detour is C++. Bindings exist for .NET but add a native
    interop surface to the fork.
  - Navmesh generation from ACE's cell/portal DAT geometry is real
    engineering — the mesh format isn't a clean input for Recast.
  - Storage: a generated navmesh per landblock is non-trivial.
  - Heavy for M2 scope. Likely pushes M2 by months.

### Option C — Copy monsters' tether-to-spawn approach with no real planner

- Pros:
  - Nothing new to write or import.
- Cons:
  - Tethered bots can't follow a human player out of their bubble.
  - Tethered bots can't form groups across rooms.
  - Doesn't meet the M2 success criterion: "go to X, kill Y, come
    back" requires navigation that current monster AI cannot do.

### Option D — Steal a playerbot pathfinding implementation from a WoW emulator

- Pros:
  - Possibly faster than from-scratch.
- Cons:
  - WoW's coordinate system, navmesh format, and zone topology differ
    from AC's. Lift-and-shift is likely more work than it saves.
  - License compatibility is a question per fork.

## Consequences

- **Easier:**
  - No third-party dependency to vet or maintain in the fork.
  - M2 has a concrete, scoped path planner: LOS in open terrain,
    authored waypoints in dungeons.
  - Authored waypoints are reused for spawn rules — content does
    double duty.
- **Harder:**
  - We own a path planner. Bugs in it manifest as stuck or
    line-walking bots, which are visible to humans.
  - Every new zone (or significant dungeon) needs waypoint authoring
    until the planner can handle it without authored input.
  - We may have to revisit at M4 if telemetry shows the simple
    planner can't carry the milestone.
- **Follow-ups:**
  - In M2, build the planner as a separate `BotPathfinder` class with
    a narrow interface (`PlanPath(from, to, archetype)` →
    `IEnumerable<Waypoint>`) so we can swap in a navmesh-backed
    implementation later without touching brain code.
  - Add stuck-rate metric per zone in M3.
  - Reserve Option B (Recast/Detour) for M7 if telemetry forces it.

## References

- Related doc(s):
  - [`../research/ace-investigation.md`](../research/ace-investigation.md) (Q4)
  - [`../ace-fork-plan.md`](../ace-fork-plan.md)
  - [`../bot-director.md`](../bot-director.md)
  - [`../../roadmap/milestones.md`](../../roadmap/milestones.md) (M2)
- Related open question(s):
  - "Pathfinding reuse vs. replacement" item in
    [`../../roadmap/m0-checklist.md`](../../roadmap/m0-checklist.md)
  - Resolved by this ADR
- Related issue(s) / PR(s):
  - [#4](https://github.com/darinh/ac-ai-players/issues/4) — Q4 research issue
