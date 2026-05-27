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

**Findings (issue [#1](https://github.com/darinh/ac-ai-players/issues/1), resolved):**

`Player` has two constructors. The login/load ctor takes a `Session`
(`Source/ACE.Server/WorldObjects/Player.cs:114-127`) and stores it in
a non-nullable `Session` property — but the code treats it as
effectively nullable through pervasive `Session?` and `Session != null`
checks. Session is dereferenced everywhere: tick
(`Player_Tick.cs:36-82`), combat (`Player_Combat.cs:117-163`), use
(`Player_Use.cs:29-90`), packet send helpers
(`Player_Networking.cs:214-263`), world entry, and many more — every
visible side effect calls `Session.Network.EnqueueSend(...)`.

ACE already ships `OfflinePlayer`
(`Source/ACE.Server/Entity/OfflinePlayer.cs:13-44`) and an `IPlayer`
interface (`Source/ACE.Server/Entity/IPlayer.cs:10-13`) as first-class
concepts — but for human players who are logged out, not for
autonomous in-world bots. They are still backed by a `Character` row
and rejoin via Session on login. They cannot tick or act.

`Creature` already has the AI primitives bots need: per-tick AI hook
(`Creature_Tick.cs:14-49`, `Monster_Tick.cs:17-174`), combat
plumbing (`Creature_Combat.cs:52-125`), and movement helpers
(`Creature_Navigation.cs`). It has no `Session` field.

**Decision:** subclass `Creature`, not `Player`. See
[`../adr/0003-botcreature-not-botplayer.md`](../adr/0003-botcreature-not-botplayer.md).

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

**Findings (issue [#2](https://github.com/darinh/ac-ai-players/issues/2), resolved):**

A dedicated `Thread` started in `WorldManager`
(`Source/ACE.Server/Managers/WorldManager.cs:58-65`) drives the loop
at ~60 Hz via `RateLimiter(60, TimeSpan.FromSeconds(1))`. Each tick,
`LandblockManager.Tick(...)`
(`Source/ACE.Server/Managers/LandblockManager.cs:265-284,366-425`)
runs `TickPhysics` then `TickMultiThreadedWork` (groups in
`Parallel.ForEach`) then `TickSingleThreadedWork` then unloads. Loaded
landblocks are grouped by adjacency; **groups** run in parallel,
**landblocks within a group** run serially on the same thread.

Per-creature AI runs through a per-landblock `sortedCreaturesByNextTick`
linked list
(`Source/ACE.Server/Entity/Landblock.cs:425-470`). Each pop calls
`Creature.Monster_Tick(currentUnixTime)`
(`Source/ACE.Server/WorldObjects/Monster_Tick.cs:17-174`), which
handles `IsAwake`, `IsDead`, target selection, home check, attack, and
movement. The next tick time is reset before reinsertion. Two
creatures in the same landblock cannot tick simultaneously; two in
different landblocks can if their landblocks are in different groups.

**Decision:** plug bots into `Monster_Tick` via a per-creature
extension hook; brain work is async-by-contract. See
[`../adr/0004-bot-tick-via-monster-tick.md`](../adr/0004-bot-tick-via-monster-tick.md).

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

**Findings (issue [#3](https://github.com/darinh/ac-ai-players/issues/3), resolved):**

Every Player action entry point is session-coupled at the seams:

- **Movement** — `Player.MoveTo(...)` (`Player_Move.cs:156-180`) is
  reachable without Session, but `BroadcastMovement` and error paths
  send via `Session.Network`.
- **Combat** — `Player.HandleActionTargetedMeleeAttack(...)`
  (`Player_Melee.cs:51-164`) and `HandleActionTargetedMissileAttack(...)`
  (`Player_Missile.cs:40-139`) both ultimately call
  `Session.Network.EnqueueSend(new GameEventCombatCommenceAttack(Session))`.
- **Magic** — `Player.HandleActionCastTargetedSpell(...)`
  (`Player_Magic.cs:80-190`) goes through `SendUseDoneEvent(...)` →
  `Session.Network.EnqueueSend(...)` on every error/success path.
- **Chat** — `/local` via `Player.HandleActionTalk(...)`
  (`Player.cs:792-802`) uses `EnqueueBroadcast(GameMessageHearSpeech)`
  which is session-free, but error paths (gag) need session. `/tell`
  via `GameActionTell.Handle` (`GameActionTell.cs:15-54`) dereferences
  `targetPlayer.Session`. `/allegiance` via
  `TurbineChatHandler.cs:114-247` iterates members and calls
  `online.Session.Network.EnqueueSend(...)`. Emotes are session-free in
  the broadcast path (`Player.cs:822-845`).
- **Inventory / use** — `Player_Use.cs`, `Player_Inventory.cs`,
  `Player_Trade.cs` are full of `Session.Network.EnqueueSend(...)`.

The shape this forces: for outbound bot actions, we can call the
underlying broadcast primitives (`EnqueueBroadcast(...)`,
`PhysicsObj.MoveToObject(...)`) directly from `BotCreature` rather than
trying to make Player session-optional. For inbound channels that
deliver to a target's session (notably `/tell`), we add a small guid
shim before the dereference.

**Decisions:**
- Chat: see
  [`../adr/0006-chat-via-creature-broadcast.md`](../adr/0006-chat-via-creature-broadcast.md).
- Combat/magic/inventory: bots reuse `Creature`-level primitives that
  monsters already use; player-only actions (trade, equip-via-UI) are
  out of scope for M1–M5.

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

**Findings (issue [#4](https://github.com/darinh/ac-ai-players/issues/4), resolved):**

ACE loads landblock collision/geometry from the cell DAT and portal
DAT at startup
(`Source/ACE.Server/Program.cs:239`,
`Source/ACE.Server/Entity/Landblock.cs:174-180`,
`Source/ACE.Server/Entity/LandblockMesh.cs:53-96`). Walkable geometry
is a triangulated mesh per landblock plus a BSP for collision
(`Source/ACE.Server/Physics/BSP/BSPTree.cs:17-31`).

**There is no pathfinding.** No A\*, no navmesh, no Recast/Detour
anywhere in `Source/ACE.Server`. Monsters drive movement directly via
`MoveToObject(...)` and `TurnTo(...)` and let the physics engine
handle collision; if the target moves outside `MaxChaseRange`, the
chase cancels (`Monster_Tick.cs:51-64`, `Monster_Navigation.cs:239-253`).

Line-of-sight is available via `WorldObject.IsVisibleTarget(...)` and
the `PhysicsObj.transition(...)` collision/LOS test
(`WorldObject.cs:290-296,330-398`). Portals teleport via
`WorldManager.ThreadSafeTeleport(...)` invoked through `Portal.OnActivate`
(`Portal.cs:103-292`).

So for M2 bots, we can reuse: motion execution, collision, LOS,
portal traversal. We must build: actual path planning. The single
biggest missing piece is a navigation graph over landblock geometry.

**Decision:** reuse motion/collision primitives, build a simple
LOS+waypoint planner, reserve Recast/Detour for M7 if telemetry
forces it. See
[`../adr/0005-pathfinding-reuse-and-build.md`](../adr/0005-pathfinding-reuse-and-build.md).

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

**Findings (issue [#5](https://github.com/darinh/ac-ai-players/issues/5), resolved):**

`Character` (`Source/ACE.Database/Models/Shard/Character.cs:9-74`,
`ShardDbContext.cs:978-1020`) is keyed by `AccountId` and has no
bot-ish columns today; closest existing flags are `IsPlussed`,
`IsDeleted`, `DeleteTime`. `Account`
(`Source/ACE.Database/Models/Auth/Account.cs:6-45`,
`AuthDbContext.cs:71-130`) is its parent.

In-world state is serialized via Biota
(`Source/ACE.Database/Models/Shard/Biota.cs:9-76`); players save every
`PlayerSaveIntervalSecs` (default 300s) via
`Player_Database.cs:17-18,38-43,77-113` and `Player_Tick.cs:128-134`;
offline players save hourly via `PlayerManager.cs:42-45,108-117,153-177`.
Save path is `ShardDatabase.SaveBiota` (`ShardDatabase.cs:342-382`) and
`WorldObject_Database.SaveBiotaToDatabase` (`WorldObject_Database.cs:48-75`).

Account-character relationship is one-to-many (`PlayerManager.cs:38-40,65-75`).
Character count limit is checked via `IsAccountAtMaxCharacterSlots`
(`PlayerManager.cs:894-911`); bots will need to be exempted.

Smallest invasive marker: a new `is_Bot` BIT column on `Character`,
defaulted to 0. Schema change is additive and visible from queries.

The natural ACE/sidecar split: ACE owns "what the world must simulate"
(position, inventory, skills, attributes, allegiance, house, biota in
general); sidecar owns "why/how the bot thinks" (personality, memories,
journal, planner state, conversation history).

On biota load failure, `GetBiota` returns null
(`ShardDatabase.cs:225-231,604-607`) and the character won't
materialize. For bots this means a corrupted biota = a no-spawn for
that bot; the bot's account-level identity survives.

Bot rehydration: treat sidecar brain state as a disposable cache. If
the sidecar crashes, rehydrate personality from seed config + journal
snapshots, then bind back to the existing ACE `Character`/`Biota`.

**Decision (provisional):** use a normal ACE `Character`+`Biota` per
bot, with a new `Character.is_Bot` BIT column added in a migration
under `Source/ACE.Database/DatabaseSetupScripts/Updates/`. Bot
character count is exempt from `IsAccountAtMaxCharacterSlots`. Brain
state in sidecar DB starting M6. To be reified as an ADR before M6.

## How to investigate

For each question:

1. Open a `research-question` issue from the template.
2. Link the specific files / classes / methods examined.
3. Note unknowns and any follow-up questions.
4. Close with a short summary that the architecture docs can reference.

A running local ACE server makes these questions much easier to answer
— you can read the source, set breakpoints, watch the log, and inspect
the database while the world is live. See
[`../local-install.md`](../local-install.md) for the verified setup
procedure.

We do **not** fork ACE until Q1–Q3 are answered. Q4 and Q5 can be answered
in parallel with early M2/M3 work.

**Status update:** Q1–Q5 are answered (see Findings sections above).
Q5's `Character.is_Bot` persistence shape will be reified as a
separate ADR before M6.

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
