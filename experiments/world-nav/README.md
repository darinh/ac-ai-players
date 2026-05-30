# world-nav

Static indoor navmesh extracted from the AC1 DAT files, plus a
diagnostic SVG renderer to eyeball the result.

This is the proof-of-concept for the long-term plan in
[ADR-0010](../../docs/adr/0010-pathfinding-as-standalone-mod.md)
and tracker issue
[ac-ai-players#47](https://github.com/darinh/ac-ai-players/issues/47):

> Each `CellPortal` carries `PolygonId`, `OtherCellId`,
> `OtherPortalId` — this is effectively a free navmesh.
> Portal polygon centroids become waypoints.

## Why this exists

The bot has been doing straight-line dead-reckoning to whatever
target the picker (or LLM) picks. With server-side collision
clamping live (`anvil/bot-collision-enforcement`), straight-line
attempts at any target with a wall in the way are now correctly
rejected by the server. The next step is to give the bot a real
mental map of the world so it knows to route around walls and
through doorways instead of mashing itself against geometry.

Per the user's design call:

- The map MAY use the AC1 DAT files. The DAT files are the game
  client's own visual data; using them is what a human player
  also has. Using them is not cheating.
- The map MUST NOT include dynamic entities the bot is supposed
  to discover by exploring: mob spawn points, NPC patrol points,
  treasure positions, etc. Those become per-bot memory layered
  on top of the static map.

## What's in here

- **`src/AcAiPlayers.WorldNav/`** — library. Loads landblock
  EnvCell records, walks the CellPortal graph, returns an
  `IndoorNavGraph` with cell centroids, cell bounds, and
  world-space portal centroids.
- **`src/WorldNavBuilder/`** — CLI. Runs the loader against a
  single landblock and writes a diagnostic SVG.
- **`tests/AcAiPlayers.WorldNav.Tests/`** — unit tests for the
  data model. Geometry tests against real DAT data are too
  expensive for unit-test runs; cover those via the CLI
  output instead.

## Cross-repo dependency

This project takes `ProjectReference`s on the ACE-bots fork's
`ACE.DatLoader`, `ACE.Entity`, and `ACE.Common` projects via a
relative path. It assumes the repos are checked out as siblings:

```
~/repos/ac-ai-players/   (this repo)
~/repos/ACE-bots/        (sibling clone of the ACE fork)
```

If you move them, edit the paths in
`src/AcAiPlayers.WorldNav/AcAiPlayers.WorldNav.csproj`.

This is fine for a spike. The long-term plan is for the navmesh
to live inside the `ACE.Mod.Pathfinding` Harmony mod (ADR-0010),
which gets a clean intra-repo reference to `ACE.DatLoader`. The
headless client will consume the same library via its mod-package
output.

## Usage

Build:

```pwsh
dotnet build experiments/world-nav/WorldNav.slnx -c Release
```

Render the training academy landblock (`0x8602`):

```pwsh
dotnet run --project experiments/world-nav/src/WorldNavBuilder `
    -c Release `
    -- --dat C:\ACE\Dats --landblock 8602 --out academy.svg
```

The output SVG is top-down, north-up. Cells are blue rectangles,
cell centroids are dark blue dots, portal centroids are green
dots, portal links are grey lines, dangling portals (other side
not loaded in this landblock) are dashed red.
