# 0012. Bot world knowledge comes from play, not from static client data

- **Status:** Accepted
- **Date:** 2026-05-28
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

The bot's architecture (see ADR
[0005](0005-pathfinding-reuse-and-build.md),
[0010](0010-pathfinding-as-standalone-mod.md), and
[0011](0011-bot-brain-agent-loop.md)) treats the world as something
the bot **discovers and represents over time**: a per-bot NavGraph
of nodes and edges, learned landmarks, observed NPCs and item
locations, walkability evidence from successful cell crossings. The
LLM compiler in the Strategy layer operates on this constructed
representation, not on omniscient ground truth.

Multiple times during M1.6 design discussions, agents (including
this one) have proposed shortcuts that pre-bake environmental
knowledge from sources a human player does not have access to.
Concrete examples surfaced in
[#77](https://github.com/darinh/ac-ai-players/issues/77),
[#78](https://github.com/darinh/ac-ai-players/issues/78):

- Extracting `EnvCell` and `BldPortal` adjacency from
  `client_cell_1.dat` via `ACE.DatLoader` and shipping a
  `data/nav-seed/doorways.jsonl` so bots know building interior
  topology before walking through it.
- Looking up `WeenieType.Door` on the server side and surfacing it
  to the bot through a side channel rather than via the wire.
- Pre-baking weenie database dumps to give the bot eager knowledge
  of every door wcid in the game.

Each of these makes the *first* run easier and the *whole project*
pointless: if static extraction is the answer, the NavGraph, the
walkability gate, the per-bot persistent journal, and most of the
agent-loop scaffolding are dead weight. The user has rejected this
class of shortcut explicitly and repeatedly.

This ADR exists so the rule survives across agent sessions and
future contributors don't have to relitigate it.

## Decision

The bot may only derive environmental knowledge from sources a
real human player has access to. Specifically:

**Allowed:**

1. The headless client wire stream — every packet a normal AC1
   client receives, including `ObjectCreate`, `SetState`, position
   updates, chat, NPC tells, `WeenieError` / `WeenieErrorWithString`
   responses, etc.
2. The bot's own constructed state derived from gameplay — NavGraph
   nodes and edges, journals, inventory contents, chat memory,
   walkability evidence.
3. In-game UI surfaces a human player can use at the keyboard —
   the world map, the radar, the `@` command output, item examine
   text. (The canonical landmark seed in #77 part 2 is the
   archetype: every human player can open the world map, so the
   bot may have the same map data pre-loaded.)

**Forbidden:**

- Static client DAT extracts: `EnvCell`, `BldPortal`, wall
  geometry, cell adjacency, weenie database dumps, anything from
  `ACE.DatLoader`, `client_cell_1.dat`, `client_portal.dat`, etc.
- ACE server-internal types not exposed on the wire: `WeenieType`,
  full `Biota` property bags, anything the server keeps private
  from clients in normal operation.
- Pre-baked "the bot already knows about this dungeon" lookup
  tables built from server-side or client-DAT data.

The bright-line test: *"Could a human player at the keyboard
discover this from in-game UI alone, without modding the client or
reading server logs?"* If yes, allowed. If no, forbidden.

## Options considered

### Option A — No cheating (this ADR)

- Pros:
  - Preserves the entire architecture. NavGraph, walkability gate,
    per-bot journals, and exploration-driven learning all have a
    purpose.
  - Bot behavior generalizes to custom-content ACE servers
    (different DAT contents, server-modified weenies) without
    re-extraction.
  - Forces the team to solve exploration robustness, which is on
    the critical path to the social/raid endgame anyway.
- Cons:
  - First-visit-to-a-building cost is paid by every bot the first
    time it walks in. (Mitigated by the persistent NavGraph: paid
    once per bot per region, not per session.)
  - LLM has less context on first encounters with a new area.

### Option B — DAT seed map, runtime augmentation

- Pros:
  - Bot starts with full canonical-server topology; no per-bot
    discovery cost on canonical content.
  - LLM has richer context on first encounter.
- Cons:
  - Defeats the architecture; most of the NavGraph machinery
    becomes unused.
  - Breaks on custom-content servers (different DAT cell IDs,
    different building layouts).
  - DAT redist licensing is unclear — we'd either commit derived
    data or require contributors to run a local extraction step.
  - User has rejected this approach explicitly.

### Option C — Server-side feed (out-of-band)

- Pros:
  - Bot doesn't need DAT access; server pushes "you can see this
    cell adjacency" alongside normal wire traffic.
- Cons:
  - Requires a non-trivial ACE-server modification that no real
    AC1 client benefits from — a fork-only feature.
  - Still cheating: a human player at a normal client doesn't have
    this channel.
  - Couples bot capability to the specific ACE-bots fork, blocking
    use against vanilla ACE servers.

## Consequences

- Easier:
  - Architectural reasoning. There's one rule, it applies
    everywhere, and it lines up with the user's mental model of
    "the bot is a player."
  - Cross-server portability. The same bot binary can connect to
    canonical and custom-content servers without per-server data
    bundles.
  - Code review. Reviewers can flag any PR that imports
    `ACE.DatLoader` or reads `client_*.dat` files.
- Harder:
  - First-visit interior navigation. Until the bot has walked a
    building, it knows nothing about its interior topology.
    Doorway discovery is incremental from walkability evidence
    (see #78).
  - Onboarding new agents. New contributors and agent sessions
    will be tempted by the shortcut and need to be redirected.
    This ADR plus the AGENTS.md section is the redirect.
- Follow-ups:
  - Implement walkability-evidence doorway nodes (#78 design).
  - Make sure the Path Executor (#75) only consults the constructed
    NavGraph plus wire-derived state — no DAT lookups.
  - When the LLM compiler is extended (e.g., quest comprehension),
    it must only see the projected world state, not raw DAT data.

## References

- Related doc(s):
  - [`AGENTS.md` § Bot world knowledge — no cheating](../../AGENTS.md)
  - [`docs/pilot/improvement-loop.md` § Anti-patterns](../pilot/improvement-loop.md#anti-patterns-dont-do-these)
- Related ADRs:
  - [0005 — Pathfinding reuse and build](0005-pathfinding-reuse-and-build.md)
  - [0010 — Pathfinding as standalone mod](0010-pathfinding-as-standalone-mod.md)
  - [0011 — Bot brain agent loop](0011-bot-brain-agent-loop.md)
- Related issue(s):
  - [#77 — NavGraph topology + world coords](https://github.com/darinh/ac-ai-players/issues/77)
  - [#78 — Movement+pathfinding tactics (door collision, cadence, NPC avoidance)](https://github.com/darinh/ac-ai-players/issues/78)
  - [#79 — LLM rejection-blindness + `Use{inventory-item}` goal kind](https://github.com/darinh/ac-ai-players/issues/79)
