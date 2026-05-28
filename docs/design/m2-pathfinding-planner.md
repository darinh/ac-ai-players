# M2 pathfinding planner: design spike

- **Status:** Draft (proposed for review)
- **Tracking issue:** [#16](https://github.com/darinh/ac-ai-players/issues/16)
- **Driving ADR:** [ADR-0005](../adr/0005-pathfinding-reuse-and-build.md)
- **Date:** 2026-05-27
- **Author:** Anvil (Copilot CLI)

## 1. Why this exists

ADR-0005 commits to "reuse motion + collision; build a simple LOS+waypoint
planner." The planner does not exist yet. M2 (motor layer: movement and
combat) cannot start until it does, because the M2 success criterion is
"go to X, kill Y, come back" — and neither "go to" nor "come back" is
possible without it.

This document is the design spike that unblocks
[#16](https://github.com/darinh/ac-ai-players/issues/16). It does NOT
implement the planner; it answers the implementation questions so the
implementation PR is straightforward.

## 2. What ACE gives us today

Verified in `darinh/ACE-bots` `botplayer-spike` at `12da3bbb`:

| Capability | API | Notes |
|---|---|---|
| Move-to-target physics | `PhysicsObj.MoveToObject(target, MovementParameters)` | Already used by `BotPlayer.TickFollow` (BotPlayer.cs:725) and `Pet.StartFollow` (Pet.cs:249) |
| Move-to-position physics | `Creature.MoveTo(Position, runRate, ...)` (Creature_Navigation.cs:341) | Sends `MoveToPosition` message; client interpolates |
| Cancel motion | `PhysicsObj.cancel_moveto()` | Null-safe; see `BotPlayer.CancelBotMoveTo` for the safe-for-bot wrapper |
| Collision / slide test | `PhysicsObj.transition(oldPos, newPos, adminMove)` | Returns null on collision. **Initializes from `this.CurCell`, NOT from `oldPos.ObjCellID`** — so it must be called on a PhysicsObj whose `CurCell` is already correct for `oldPos` |
| Visual LOS (object-to-object) | `WorldObject.IsDirectVisible(WorldObject)` (WorldObject.cs:290+) | Creates a sight object inheriting the caller's `CurCell`, applies eye-height offsets, requires `GetBlockDist <= 1` (caller and target in adjacent landblocks at most) |
| Visual LOS (object-to-position) | `WorldObject.IsDirectVisible(Position)` (WorldObject.cs:330-339) | Reverses ray from target to actor; same adjacency limit |
| Move-complete callback | `Player.OnMoveComplete(WeenieError)` (overridden by `BotPlayer.OnMoveComplete`) | Fires once per `MoveToObject` resolution; success = `WeenieError.None`. **Does NOT carry a sequence id** — completions from a cancelled motion can arrive after a new motion has started (see §5.3 generation handling) |
| Portal traversal | `Portal.CheckUseRequirements` + `Portal.OnActivate` (Portal.cs:103-292) → `WorldManager.ThreadSafeTeleport` | Player-side `HandleActionUseItem(uint itemGuid)` path takes a runtime GUID, not a WCID. Has a 3.5s recent-teleport gate per player |

**Critical implication of the `transition` semantics:** there is no
existing "arbitrary point-to-point traversability" primitive. The
existing visibility helpers all assume the test originates from a
PhysicsObj that is already in the world (so its `CurCell` is set). For
the planner we have to either:

(a) Probe traversability from the bot's CURRENT position only, using
the bot's own PhysicsObj (CurCell is valid). This is fine for the
direct-from-bot Tier 1 check.

(b) For arbitrary segment validation (waypoint-to-waypoint edge load-
time validation, anchor selection from a remote `to` point), resolve
`ObjCell` from the position's `ObjCellID` via `LandblockManager.GetLandblock(...).GetCell(...)` and inject it into a transient probe object before calling `transition`. See §3.2.

We do NOT have:

- A* / Dijkstra anywhere in `Source/ACE.Server`
- A navmesh
- A waypoint graph format
- Any per-zone authored data
- A "stuck rate" metric
- A "is this segment walkable" primitive that's safe to call from
  arbitrary positions — we have to build one

## 3. Algorithm

### 3.1 The hybrid

A two-tier algorithm. Tier 1 handles the easy case; tier 2 handles the
hard case. Both tiers feed the same execution loop.

**Terminology:** "traversable" = a body-sized probe can slide along the
segment without collision AND the Z delta and slope are within walk
limits. "Visual LOS" = a line from eye-height to eye-height is
unobstructed. These are NOT the same — a balcony has visual LOS to the
floor below but is not traversable in one segment. Tier 1 needs
**traversability**, not LOS.

**Tier 1 — direct traversability:**

```
PlanPath(bot, from, to):
    if from.Landblock != bot.Location.Landblock:
        # from must equal the bot's current position; bot can't plan
        # from arbitrary points
        return null
    if CanTraverseSegment(bot, from, to):     # uses bot's PhysicsObj
        return [from, to]
    else:
        fall through to Tier 2
```

This handles open-terrain travel (the common case in starter zones) with
zero authored content. `CanTraverseSegment` enforces (a) segment length
≤ `maxDirectSegmentMeters` (default 60m — covers typical "walk across
the town square" distance, short enough to keep the transition cost
bounded), (b) Z delta ≤ `maxStepZDelta` (default 4m — anything taller
needs an authored WP), and (c) the actual `PhysicsObj.transition` does
not return null.

**Tier 2 — atlas-wide waypoint graph:**

```
PlanPath(bot, from, to):
    atlas = currentAtlas
    if atlas is null or empty:
        return null

    # Anchor selection. ClosestTraversableWaypoint scans WPs in the
    # FROM landblock (and one ring of adjacent landblocks for the dest
    # if it's not the from landblock) and returns the closest one whose
    # segment to the anchor point is traversable.
    startWP = ClosestTraversableWaypoint(bot, from, atlas)
    endWP   = ClosestTraversableWaypointForPosition(to, atlas)
    if startWP is null or endWP is null:
        return null

    if startWP == endWP:
        # Both endpoints anchor to the same WP, but direct traversability
        # from `from` to `to` already failed (else we wouldn't be in
        # Tier 2). So we must route via the WP, not direct.
        return [from, startWP.Position, to]

    wpPath  = AStar(atlas, startWP, endWP)    # A* over the WHOLE atlas
                                              # (cross-landblock via
                                              # portal edges)
    if wpPath is null:
        return null
    return Expand(from, wpPath, to)
```

A* with Euclidean-distance heuristic is **inadmissible** when the
graph includes portal shortcuts (a portal can jump 10km for a 5m edge
cost). For that reason A* in this planner uses a hybrid:

- Within a landblock: Euclidean heuristic (admissible, fast convergence)
- When the path must cross landblocks via portals: heuristic set to
  zero for the cross-landblock segment, degrading gracefully to
  Dijkstra-equivalent for that hop. Detected by `startWP.Landblock !=
  endWP.Landblock`

This gives optimal paths in the common (intra-landblock) case while
remaining correct for portal-routed paths.

### 3.2 Traversability check shape

We need TWO primitives:

**`HasVisualLOS(observer, fromPos, toPos)`** — used for combat-style
sight checks. Already exists as `WorldObject.IsDirectVisible(...)`. NOT
used in path planning.

**`CanTraverseSegment(probeObj, fromPos, toPos)`** — new, used by the
planner. Walks the segment via `transition` using a body-sized probe:

```csharp
public static bool CanTraverseSegment(PhysicsObj probe,
                                      Position fromPos, Position toPos,
                                      float maxLen, float maxZDelta)
{
    if (Vector3.Distance(fromPos.Pos, toPos.Pos) > maxLen) return false;
    if (Math.Abs(toPos.Pos.Z - fromPos.Pos.Z) > maxZDelta) return false;

    // probe MUST have its CurCell aligned with fromPos. For the bot's
    // own PhysicsObj this is true when fromPos == bot.Location. For an
    // arbitrary probe used at atlas-load time, the caller must have set
    // CurCell from LandblockManager.GetLandblock(fromPos.LandblockId)
    //                                  .GetCell(fromPos.ObjCellID).
    var t = probe.transition(fromPos.PhysPosition(), toPos.PhysPosition(), false);
    if (t == null) return false;
    if (t.SpherePath.CurCell == null) return false;
    // Reached the target cell with no collision rejection.
    return true;
}
```

There are two callers and they handle probe-CurCell differently:

1. **Live planning from the bot.** `from = bot.Location`. Probe is
   `bot.PhysicsObj`. CurCell is already correct.

2. **Atlas load-time edge validation.** For each authored edge A→B,
   construct a transient probe via `PhysicsObj.makeObject(0x02000124, 0,
   false, true)` (the same sphere class IsDirectVisible uses), set its
   CurCell from `LandblockManager.GetLandblock(A.LandblockId).GetCell(A.ObjCellID)`,
   and call `CanTraverseSegment`. Destroy the probe after each edge.
   Edges that fail traversability at load time are dropped from the
   in-memory graph with a warn log. This is cheap because it runs once
   at startup and on `/botdirector waypoints reload`.

3. **Anchor selection for a remote `to` point.** Symmetric to atlas-
   load probing; same transient-probe pattern. Limit candidates to WPs
   within `maxAnchorMeters` (default 80m) of `to`.

**Cost concerns:**

| Operation | Frequency | Cost |
|---|---|---|
| Tier 1 `CanTraverseSegment` from bot | ~1 per plan | <1ms (single transition over ≤60m) |
| Anchor selection (ClosestTraversableWaypoint) | ~2 per plan (one for from, one for to) | scans candidates within `maxAnchorMeters`; capped at `maxAnchorsTested` (default 16). Each candidate = 1 transition |
| A* graph search | 1 per plan | <5ms on ~200-node graph |
| Edge validation at atlas load | once per `/botdirector waypoints reload` | linear in edge count |

The 250ms brain-tick budget tolerates this comfortably as long as we
**do not re-plan every tick** (see §5.3 generation handling) and as
long as the per-bot plan rate is bounded.



### 3.3 A* on the waypoint graph

Standard A* over the **atlas-wide** graph (NOT per-landblock):

- `g(n)` = sum of edge weights from start to `n`
- `h(n)` = `Vector3.Distance(n.Position, end.Position)` when start.Landblock == end.Landblock; **`h(n) = 0`** when the path must cross landblocks via portals (preserves admissibility — Euclidean distance is not a lower bound when portals exist)
- Open set = binary min-heap keyed on `g + h`
- Closed set = `HashSet<string>` keyed on fully-qualified WP id (`<landblock>:<id>`)

Edge weight = `Vector3.Distance(a.Position, b.Position)` for normal
edges. Portal edges have a fixed `portalTraversalCost` (default 5m)
PLUS the geometric distance from the source portal WP to the
destination arrival WP **set to zero** (because the geometric distance
across a portal is meaningless — the portal teleports). The 5m
constant discourages portal use when ground travel is competitive.

## 4. Data structures

### 4.1 Waypoint graph format

JSON files, one per landblock, shipped under
`Source/ACE.Server/Bots/data/waypoints/<landblock-hex>.json`. The atlas
loader concatenates them into ONE logical graph at startup.

```jsonc
{
    "landblock": "0xA9B4",            // matches LandblockId.Landblock (high 16 bits), NOT LandblockId.Raw
    "name": "Holtburg",
    "comment": "Open-terrain town; LOS handles most travel. WPs cover the indoor inn + bank.",
    "waypoints": [
        {
            "id": "inn-door",                          // unique WITHIN this file
            "position": {
                "objCellId": "0xA9B40103",             // required for ObjCell resolution
                "x": 80.0, "y": 100.0, "z": 12.5,
                "headingDeg": 0.0
            },
            "neighbors": [
                { "id": "inn-bar",     "cost": null },                              // intra-file ref
                { "id": "square",      "cost": null },
                { "id": "lugian:arrival", "cost": 5.0, "kind": "portal",            // cross-landblock portal
                  "portalHint": { "wcid": 4660, "name": "Lugian Mines Portal" } }
            ]
        }
    ]
}
```

- **Globally-unique WP IDs** are formed at load as `"<landblock-hex>:<id>"`
  (e.g., `"0xA9B4:inn-door"`). Neighbor refs inside the same file may
  use the bare local id; cross-file refs MUST use the fully-qualified
  form. The loader rewrites bare refs to fully-qualified form at load.
- `cost: null` → computed at load time from Euclidean distance
  between the two WPs. Explicit cost overrides allowed for cases where
  a short geometric edge crosses unwalkable terrain (e.g., a chasm-
  jump that requires a long walk-around).
- Edges are duplicated (A → B and B → A) so authoring can be
  partial and validated at load. Load-time validator rejects
  asymmetric NON-portal edges with a log warning. **Portal edges may
  be asymmetric** (a one-way portal is legal).
- **Atlas-load validation** runs `CanTraverseSegment` (§3.2 caller
  pattern 2) on every non-portal edge. Failed edges are dropped from
  the in-memory graph with a warning log. Portal edges are not
  traversability-validated (the geometric distance is meaningless);
  they are validated at execution time by `Portal.CheckUseRequirements`.

### 4.2 Portals — authoring vs. runtime identity

**Authoring data** (in JSON) is a HINT, not durable identity:
- `wcid` (uint) — the portal weenie class id
- `name` (string, optional) — for log readability
- The WP's `position` — the spot the bot stands to use the portal

**Runtime resolution.** At execution time, when the `PathExecutor`
encounters a portal step, it:

1. Scans landblock objects in the WP's landblock for a `Portal` whose
   `WeenieClassId == hint.wcid` and whose `Location` is within
   `portalResolveRadius` (default 5m) of the WP.
2. If found, captures the runtime `Guid.Full` into the active `PathStep`.
3. Calls `Player.HandleActionUseItem(resolvedGuid)`.
4. Handles `Portal.CheckUseRequirements` failure (e.g., level
   requirement) by failing the step → triggering replan WITHOUT that
   portal edge (the executor tells the planner to blacklist
   `(wpId, "portal")` for the current goal).
5. Handles the 3.5s `RecentTeleport` gate by waiting that long before
   the next portal attempt for the same bot.

**Why hint, not durable identity:** runtime portal GUIDs differ from
authored data; multiple portals can share a WCID (e.g., several copies
of the same recall portal); landblocks reload and reassign GUIDs.
Treating the wcid+position as a hint is robust.

### 4.3 In-memory representation

```csharp
public sealed record Waypoint(
    string Id,                       // fully-qualified "<landblock-hex>:<id>"
    ushort LandblockId,              // LandblockId.Landblock (high 16 bits)
    Position Position,
    IReadOnlyList<WaypointEdge> Neighbors,
    PortalHint? PortalHint);         // non-null iff this WP is a portal source

public sealed record WaypointEdge(
    string TargetId,                 // fully-qualified
    float Cost,
    WaypointEdgeKind Kind);          // Walk, Portal

public sealed record PortalHint(uint Wcid, string? Name);

public sealed class WaypointAtlas {
    // Keyed by LandblockId.Landblock (NOT Raw)
    public IReadOnlyDictionary<ushort, IReadOnlyList<Waypoint>> ByLandblock { get; }
    // Keyed by fully-qualified WP id
    public IReadOnlyDictionary<string, Waypoint> ById { get; }
    public IEnumerable<Waypoint> Within(Position p, float radius) { ... }
}
```

**Atlas is immutable.** Hot-reload swaps a `volatile WaypointAtlas`
reference — same pattern as `BotData` (ADR-0008 / #10). Readers always
see a consistent snapshot.

### 4.4 PlannedPath

```csharp
public sealed class PlannedPath {
    public IReadOnlyList<PathStep> Steps { get; }
    public int CurrentIndex { get; private set; }
    public DateTime PlannedAt { get; }
    public Position Goal { get; }
    public PathStep Current => Steps[CurrentIndex];
    public bool IsComplete => CurrentIndex >= Steps.Count;
    public void Advance() { CurrentIndex++; }
}

public sealed record PathStep(
    Position Position,
    PathStepKind Kind,            // Walk, UsePortal
    PortalHint? PortalHint,       // populated for UsePortal steps
    uint? ResolvedPortalGuid);    // populated at execution time, not authoring
```

## 5. Interface and execution

### 5.1 The planner

```csharp
namespace ACE.Server.Bots.Pathfinding {

    public interface IBotPathfinder {
        /// <summary>
        /// Plan a path from `from` to `to` for `bot`. Returns null if
        /// no plan exists.
        ///
        /// Thread-safe (atlas is immutable; per-call working state
        /// only). Call site: BotPlayer brain tick. Cost budget:
        /// &lt;1ms for Tier 1; &lt;5ms for Tier 2 on ~200 WPs.
        /// </summary>
        PlannedPath? PlanPath(BotPlayer bot, Position from, Position to,
                              ISet<string>? blacklistedPortalEdges = null);

        /// <summary>Reload the waypoint atlas from disk. Atomic swap.</summary>
        void ReloadAtlas();

        /// <summary>Current atlas snapshot (for telemetry / debug commands).</summary>
        WaypointAtlas CurrentAtlas { get; }
    }

    public sealed class BotPathfinder : IBotPathfinder { ... }
}
```

**Why an interface, not static.** ADR-0005 calls out future swap to
navmesh-backed implementation. Static state also makes unit testing
awkward (need a fake atlas + injectable LOS). The static convenience
facade `BotPathfinderHost.Default` is provided for call sites that
don't want DI, with an internal setter for tests.

### 5.2 BotPlayer integration

Three new methods on `BotPlayer`:

```csharp
public void GoToPosition(Position destination, Action<bool> onComplete = null);
public void GoToObject(WorldObject target, Action<bool> onComplete = null);
public void CancelGoTo();
```

`GoToObject` re-evaluates the target's `Location` once per
`pathReevalCadenceSec` (default 1s — NOT every brain tick) and replans
only if the target has moved more than `goalDriftThreshold` (default
10m) since the last plan.

E8 follow code (`BotPlayer.StartFollow`) stays as-is for short-range
LOS follow; `GoToObject` is the long-range variant that engages the
planner.

### 5.3 PathExecutor and motion generation handling

The biggest correctness risk is stale `OnMoveComplete` callbacks. The
executor owns a monotonic generation counter; every `MoveTo` issue
captures a new generation, and `OnMoveComplete` callbacks that don't
match the current generation are dropped.

```csharp
public sealed class PathExecutor {
    private readonly BotPlayer _bot;
    private PlannedPath? _path;
    private int _generation;             // monotonic; incremented on every issue/cancel
    private int _activeSegmentGen;       // the generation of the in-flight segment
    private ExecutorState _state = ExecutorState.Idle;
    private double _lastProgressAt;
    private float  _lastDistanceToStep;  // distance-to-current-step (NOT absolute pos)
    private int _replanCount;
    private readonly HashSet<string> _blacklistedPortals = new(); // per-goal

    public enum ExecutorState { Idle, MovingSegment, UsingPortal, Complete, Failed }

    public void Start(PlannedPath path) {
        Cancel();                         // bumps _generation, clears stale callbacks
        _path = path;
        _state = ExecutorState.Idle;
        _replanCount = 0;
        _blacklistedPortals.Clear();
    }

    public void Cancel() {
        Interlocked.Increment(ref _generation);
        _bot.CancelBotMoveTo();           // existing safe-cancel helper
        _state = ExecutorState.Idle;
        _path = null;
    }

    public void Tick() {
        if (_path == null || _state == ExecutorState.Complete || _state == ExecutorState.Failed) return;
        if (_path.IsComplete) { Finish(success: true); return; }

        var step = _path.Current;

        switch (_state) {
            case ExecutorState.Idle:
                IssueStep(step);
                break;
            case ExecutorState.MovingSegment:
                CheckStuck(step);         // distance-to-step regression
                break;
            case ExecutorState.UsingPortal:
                // Wait for OnTeleportComplete (E4 hook). Timeout at 5s.
                break;
        }
    }

    private void IssueStep(PathStep step) {
        var gen = Interlocked.Increment(ref _generation);
        _activeSegmentGen = gen;
        _lastProgressAt = Time.GetUnixTime();
        _lastDistanceToStep = Vector3.Distance(_bot.Location.Pos, step.Position.Pos);

        if (step.Kind == PathStepKind.UsePortal) {
            _state = ExecutorState.UsingPortal;
            // Resolve portal at execution time
            var portal = ResolvePortalInLandblock(_bot.CurrentLandblock, step.PortalHint!);
            if (portal == null) { FailStep(); return; }
            _bot.HandleActionUseItem(portal.Guid.Full);
        } else {
            _state = ExecutorState.MovingSegment;
            _bot.MoveTo(step.Position, runRate: 1.0f);
        }
    }

    public void OnMoveComplete(WeenieError status, int generation) {
        if (generation != _activeSegmentGen) return;   // stale; drop
        if (_state != ExecutorState.MovingSegment)    return;

        if (status == WeenieError.None) {
            _path!.Advance();
            _state = ExecutorState.Idle;              // next Tick issues next step
        } else {
            FailStep();
        }
    }

    public void OnTeleportComplete() {
        if (_state != ExecutorState.UsingPortal) return;
        _path!.Advance();
        _state = ExecutorState.Idle;
    }

    private void CheckStuck(PathStep step) {
        var now = Time.GetUnixTime();
        var dist = Vector3.Distance(_bot.Location.Pos, step.Position.Pos);
        // Progress = reduction in distance-to-step (NOT absolute movement)
        if (dist < _lastDistanceToStep - 0.5f) {
            _lastDistanceToStep = dist;
            _lastProgressAt = now;
            return;
        }
        if (now - _lastProgressAt > stuckTimeoutSec) {
            FailStep();
        }
    }

    private void FailStep() {
        if (_path == null) return;
        if (_path.Current.Kind == PathStepKind.UsePortal) {
            // Blacklist this portal hint for the current goal
            _blacklistedPortals.Add(_path.Current.PortalHint!.Wcid + ":" + _bot.CurrentLandblock);
        }
        if (++_replanCount > maxReplansPerGoal) { Finish(success: false); return; }
        // Replan from current position with blacklist
        var newPath = pathfinder.PlanPath(_bot, _bot.Location, _path.Goal, _blacklistedPortals);
        if (newPath == null) { Finish(success: false); return; }
        _path = newPath;
        _state = ExecutorState.Idle;
    }
}
```

`BotPlayer.OnMoveComplete` is overridden to call
`PathExecutor.OnMoveComplete(status, currentGeneration)`. The
executor's generation match drops stale callbacks safely.

`BotPlayer.OnTeleportComplete` (the existing E4
`SimulateClientLoginComplete` hook + post-teleport rising-edge in #43)
also notifies the executor so portal step advancement works.

**Single source of truth for movement state.** `_bot.IsMoving` is set
and cleared only by the executor (or by `CancelBotMoveTo` on cancel).
The executor never issues a `MoveTo` while `_state ==
MovingSegment` — duplicate-issue races are structurally prevented.



## 6. Commands

New `/botdirector` subcommands (parallel to the existing follow surface):

| Command | Effect |
|---|---|
| `/botdirector goto x y z [landblock]` | Plan a path to (x,y,z). Defaults to caller's landblock |
| `/botdirector goto-me` | Plan a path to caller's current position |
| `/botdirector goto-wp <waypoint-id>` | Plan a path to a named waypoint (fully-qualified or local if unambiguous) |
| `/botdirector cancel` | Cancel current path |
| `/botdirector waypoints [reload]` | Print loaded atlas summary; `reload` re-reads disk |

## 7. Waypoint authoring workflow

For M2 we need at minimum one zone's worth of authored waypoints —
suggestion: Holtburg (starter town, indoor inn, bank, portal to Lugian
Mines) since it's the live deployment's spawn zone.

Authoring is manual:

1. Log in as admin in the zone.
2. Stand at each candidate WP, run `/loc`, paste position +
   `objCellId` into JSON.
3. Author edges (which WPs can the bot walk between in straight,
   walkable segments — atlas-load validation will drop edges that
   fail `CanTraverseSegment`).
4. `/botdirector waypoints reload`, confirm atlas loaded and check log
   for any dropped edges.
5. `/spawnplayerbot newbie 1`, `/botdirector goto-wp <id>`, watch.

For M3+ we may build a `/wp add`, `/wp link`, `/wp save` capture tool
to speed authoring. Out of scope for M2.

## 8. Testing

### 8.1 Unit tests (`Source/ACE.Server.Tests/Bots/Pathfinding/`)

- `WaypointAtlasLoaderTests` — JSON parse, asymmetric-edge validation
  (non-portal edges rejected, portal edges allowed), per-landblock
  grouping, malformed-file resilience (one bad file doesn't break the
  atlas), fully-qualified ID rewrite from local refs.
- `AStarTests` — known small graphs with known shortest paths; ties;
  unreachable target; single-node start==goal; portal edges with
  heuristic=0; intra-landblock heuristic=Euclidean.
- `BotPathfinderTests` — mocked `CanTraverseSegment` + graph; verifies
  tier-1 short-circuit, tier-2 anchor selection,
  `startWP == endWP` → `[from, startWP.Position, to]`, portal-edge
  blacklisting via the `blacklistedPortalEdges` arg, cross-landblock
  routing via portal edges.
- `PathExecutorTests` — generation-handling: stale `OnMoveComplete` is
  dropped; new MoveTo not issued while in `MovingSegment` state;
  cancel-while-moving bumps generation cleanly; stuck-detection fires
  on distance-to-step regression (not absolute movement); replan
  budget enforced; portal-step failure blacklists the hint for the
  current goal.

### 8.2 Integration tests (live server)

Manual, per zone:

1. Bot in Holtburg square: `/botdirector goto-wp 0xA9B4:inn-bar` →
   walks the door, around tables, sits at bar. No teleporting; no
   line-walking through walls.
2. Bot in Holtburg inn: `/botdirector goto-wp 0xA9B5:lugian-anvil` →
   walks to portal, uses portal, walks to anvil (cross-landblock).
3. Bot mid-walk: `/botdirector cancel` → stops within 1 tick;
   subsequent `OnMoveComplete` from the cancelled move does NOT
   advance any future plan.
4. Bot mid-walk: kill bot → corpse spawns at last known position; on
   respawn from lifestone, no in-flight path persists.
5. Bot with goal out of LOS and no graph: replies `"I don't know how to
   get there."` and does not move.
6. Bot routed through a portal it lacks a level requirement for:
   portal use fails → replan without that portal edge succeeds (or
   reports unreachable if no alternative).

### 8.3 Telemetry

Per ADR-0005's "stuck rate" follow-up:

```
[bot-spike] PathfinderTelemetry: zone=0xA9B4 planned=42 succeeded=40 stuck=2 (4.8%) replans=3 losChecks=120 avgPlanMs=2.1
```

Log every minute. Rolling 5-minute window. Stuck rate >5% triggers a
warning per ADR-0005.

## 9. Open questions

| Q | Default if not answered |
|---|---|
| Should the executor short-circuit Tier 1 traversability check on every tick? Or trust the plan and re-check only on `OnMoveComplete` failure? | Trust the plan; re-check only on failure (cheaper) |
| Portal use: do bots need to handle "portal denied" responses (e.g., level requirement)? | M2 yes; if portal use fails, blacklist that portal hint for the current goal and replan; M3 may filter at plan time using `Portal.CheckUseRequirements` simulation |
| Z-coordinate handling: does the planner respect Z in the heuristic? | Use 3D for traversability; 2D for A* heuristic (most AC geometry is "roughly flat per landblock chunk") |
| Concurrency: can two bots plan simultaneously? | Yes — atlas is immutable, planner has no shared mutable state, each plan call gets its own working set |
| Replan budget: how many replans before giving up? | 3 per goal |
| Stuck timeout: how long without distance-to-step progress before replan? | 5 seconds, measured as `distance-to-current-step regression by ≥ 0.5m` |
| Goal drift threshold for `GoToObject`: how far does the target move before replan? | 10 meters |
| Re-evaluation cadence for `GoToObject` target position | 1 second (NOT every 250ms brain tick) |
| Max direct-traversability segment length (Tier 1) | 60 meters |
| Max Z delta for direct traversability | 4 meters (anything taller needs an authored WP) |
| Max anchor-search distance (Tier 2) | 80 meters |
| Max anchors tested per endpoint | 16 (closest 16 by 2D distance) |
| Where do waypoints live on disk: in the fork or in the planning repo? | In the fork (`Bots/data/waypoints/`) — same hot-reload path as `botdata.json` |
| Static `BotPathfinderHost.Default` vs DI-injected `IBotPathfinder`? | Both — DI-friendly interface, static facade for legacy call sites; tests use the interface |

## 10. Out of scope (M2)

- Navmesh generation from landblock geometry (ADR-0005 Option B; held
  for M7 if telemetry forces it)
- Multi-zone macro routing across many portals ("walk from Holtburg to
  Yaraq via 4 dungeons") — possible with the atlas-wide A* but
  prohibitively expensive without zone-level abstraction. M3+
- Crowd avoidance (multiple bots converging on the same WP) — M3+
- Dynamic obstacles (player blocking a doorway) — falls under
  replan-on-fail; not a special case
- Authoring UI / capture commands — manual JSON for M2
- LLM-driven destination selection — that's M5 Social-layer plumbing;
  the planner just receives a `Position`

## 11. Estimated effort

Revised after rubber-duck pass (more careful traversability + generation
handling):

- Atlas loader + JSON schema + ID qualification + load-time edge
  validation: 1.5 days
- A* with admissibility-preserving heuristic + portal-edge handling: 1 day
- `CanTraverseSegment` primitive + transient probe wiring + caching: 1 day
- `PathExecutor` (state machine + generation handling + distance-to-step
  stuck detection): 1.5 days
- `BotPlayer.GoToPosition/GoToObject/CancelGoTo` + executor wiring +
  OnTeleportComplete hook: 0.5 day
- `/botdirector goto*` commands: 0.5 day
- Unit tests (loader, A*, planner, executor): 1.5 days
- Integration smoke + telemetry + dropped-edge logging: 1 day
- **Total: ~8.5 days** for the planner foundation

Authoring waypoints for one zone (Holtburg) is additional content work,
likely ~2-4 hours per zone depending on geometry complexity.

## 12. References

- [ADR-0005](../adr/0005-pathfinding-reuse-and-build.md) — the
  "build a planner" decision
- [ADR-0008](../adr/0008-bot-tick-via-player-tick.md) — tick cadence the
  planner runs against
- [`bot-director.md`](../bot-director.md) — M3 spawn rules will
  consume the same waypoint atlas
- [`milestones.md`](../../roadmap/milestones.md) — M2 deliverables
- [#16](https://github.com/darinh/ac-ai-players/issues/16) — tracking
  issue
- ACE motion code references:
  - `Source/ACE.Server/WorldObjects/Creature_Navigation.cs:290-350` —
    `MoveTo(WorldObject, ...)` and `MoveTo(Position, ...)`
  - `Source/ACE.Server/Physics/PhysicsObj.cs:4066` — `transition(...)`
    (initializes from `this.CurCell`)
  - `Source/ACE.Server/WorldObjects/WorldObject.cs:285-400` —
    `IsDirectVisible(...)` (the visual-LOS primitive — NOT used in
    planning; the planner uses `CanTraverseSegment`)
  - `Source/ACE.Server/WorldObjects/BotPlayer.cs:573-586` —
    `CancelBotMoveTo` (the safe-for-bot cancel pattern this design reuses)
  - `Source/ACE.Server/WorldObjects/Monster_Navigation.cs:501` —
    `CancelMoveTo` (the monster-only version this design explicitly
    avoids, per E8 lessons)

## Appendix A — Rubber-duck pass adoptions

This spike went through one rubber-duck review (claude-opus). All six
BLOCKING findings were adopted; the doc reflects them:

1. **LOS ≠ traversability.** Introduced `CanTraverseSegment` as a
   separate primitive distinct from `IsDirectVisible`. The arbitrary-
   point cell-handling concern is addressed by the "probe must have
   `CurCell` aligned with `fromPos`" rule and the two probe patterns
   in §3.2.
2. **Tier 1 renamed** to "direct traversability" with explicit
   max-segment-length and max-Z-delta gating.
3. **`startWP == endWP` bug fixed** in the pseudocode in §3.1 — now
   returns `[from, startWP.Position, to]` because Tier 2 is only
   entered after Tier 1 already failed.
4. **A* is atlas-wide**, not per-landblock. WP IDs are globally
   qualified as `<landblock>:<id>`. Portal edges cross landblocks.
   Heuristic adjusted to preserve admissibility across portals.
5. **`PathExecutor` got a full state machine** with a monotonic
   generation counter so stale `OnMoveComplete` callbacks are dropped
   safely. `IsMoving` has a single owner. Stuck detection is
   distance-to-step regression, not absolute movement.
6. **Portal identity** is authoring HINT (wcid + position), runtime
   resolution looks up the actual `Portal` object by wcid + proximity,
   and execution handles `CheckUseRequirements` failure + the 3.5s
   recent-teleport gate.

Non-blocking adoptions: per-landblock JSON key is
`LandblockId.Landblock` (NOT `Raw`); telemetry tracks LOS-check count
and avg-plan-ms; `IBotPathfinder` interface added for testability.

Two NITS not adopted: the in-memory schema typo (`ByDx` →
`ById`) was fixed; the LOS-pseudocode example was replaced wholesale
by the `CanTraverseSegment` shape so the `WorldObjectFactory` /
`makeObject` ambiguity is moot.
