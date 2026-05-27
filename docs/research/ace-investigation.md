# ACE investigation

Before we fork [ACEmulator/ACE](https://github.com/ACEmulator/ACE), we need
to answer a handful of questions about how it's built. The answers determine
whether our extension is a small surgical diff or a large invasive rewrite.

This doc is the *checklist*. Each question gets its own GitHub issue
(`research-question` template) where the actual findings live.

## The five questions

### Q1. How is a `Player` instantiated, and what assumptions about a network session does it carry?

We need a "headless player" — a Player-like entity that exists in the world
but isn't backed by a client connection. The cleanest design is for our
`BotPlayer` to subclass or mirror `Player` without ever needing a session.

**Things to find:**
- The `Player` constructor(s) and what they expect from `Session`.
- Where in the lifecycle the session is dereferenced (login, tick, action
  handlers, packet sends).
- Whether there's already an "offline player" or "GM puppet" concept we
  can crib from.
- Whether `WorldObject` / `Creature` is closer to what we actually want
  (i.e., a bot might be better modeled as a sophisticated `Creature` than
  as a `Player`).

**Why it matters:** Decides the entire shape of the ACE fork diff.

---

### Q2. What's the world tick loop, and where do per-entity decisions get made?

We need a hook where, every tick (or every N ticks), each bot's brain can
observe the world and decide on actions.

**Things to find:**
- The main world/landblock update loop.
- Where AI for existing creatures currently runs (monster AI is the obvious
  precedent for "code that drives an in-world entity each tick").
- Whether the loop is single-threaded per landblock or fully parallel —
  this determines our threading model for the BotBrain.

**Why it matters:** Determines how we plug in the Motor/Tactical layers.

---

### Q3. What's the action surface? How does code in the server move, attack, cast, chat as a player?

We don't want to fake packets. We want to call the same internal methods
that handling a client packet would call.

**Things to find:**
- Movement: how does the server execute a move request from a client?
- Combat: how is an attack initiated, resolved, and broadcast?
- Magic: how is a spell cast?
- Chat: how are `/local`, `/tell`, `/allegiance`, emotes routed?
- Inventory / use: how are items used, equipped, traded?

Ideally we can express bot behavior as: "call `Player.MoveTo(...)`" and not
"construct and inject a `MoveRequest` packet."

**Why it matters:** Determines the size and shape of the bot Action API.

---

### Q4. Pathfinding and the world representation

Bots need to walk through Dereth without getting stuck on terrain or running
through walls.

**Things to find:**
- What collision / nav data does the server already have?
- Do existing monsters pathfind, and if so, how? (A*? Waypoints? Tethered
  to a spawn point?)
- Is there a navmesh anywhere, or do we need to build one from landblock
  geometry?
- Is portal traversal a special case, or just "use the portal object"?

**Why it matters:** Pathfinding will be the single biggest piece of Motor-
layer work. We want to reuse, not rebuild.

---

### Q5. Persistence and characters

Bots have state that should survive restarts. ACE already has a database
schema for characters.

**Things to find:**
- Do we store bots in the same `Character` table as humans (flagged as
  `is_bot`), or in a separate table?
- What goes in ACE's DB vs. our own sidecar DB?
  - **ACE DB:** in-world stuff — position, inventory, attributes, skills.
  - **Sidecar DB:** brain stuff — personality traits, memories, journal.
- What happens if the same bot is "alive" in ACE but the sidecar has lost
  its brain state? (e.g., sidecar crashed and restarted.)

**Why it matters:** Decides our data model and how the two processes
recover from each other's failures.

## How to investigate

For each question:

1. Open a `research-question` issue from the template.
2. Link the specific files / classes / methods examined.
3. Note unknowns and any follow-up questions.
4. Close with a short summary that the architecture docs can reference.

We do **not** fork ACE until Q1–Q3 are answered. Q4 and Q5 can be answered
in parallel with early M2/M3 work.

## Useful starting points

(These are guesses based on typical MMO server layouts — verify before
trusting.)

- `Source/ACE.Server/WorldObjects/Player*.cs` — Player class hierarchy
- `Source/ACE.Server/WorldObjects/Creature*.cs` — Creature / monster AI
- `Source/ACE.Server/Managers/` — world manager, landblock manager
- `Source/ACE.Server/Network/Handlers/` — packet handlers (shows how client
  actions enter the server — useful for finding the action surface)
- `Source/ACE.Database/` — persistence schema and data access

See [`related-work.md`](related-work.md) for how other emulators (notably
WoW playerbots) solved analogous problems.
