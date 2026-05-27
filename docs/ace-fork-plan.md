# ACE fork plan

A one-page summary of what we are going to change in our forked copy of
[ACEmulator/ACE](https://github.com/ACEmulator/ACE), and why. This doc
is the final M0 deliverable — see
[`../roadmap/m0-checklist.md`](../roadmap/m0-checklist.md).

This plan is grounded in the Q1–Q5 findings in
[`research/ace-investigation.md`](research/ace-investigation.md), in
the design in [`architecture.md`](architecture.md), and in the ADRs
listed under [See also](#see-also). Citations into ACE source
reference master @ commit `9bc20cbd`; see also the verified local
install procedure in [`local-install.md`](local-install.md).

## What we are changing in ACE

A small set of additive changes:

1. **New class `BotCreature : Creature`** at
   `Source/ACE.Server/WorldObjects/BotCreature.cs`. Session-less by
   construction. Exposes an `OnBrainTick` hook called from
   `Monster_Tick`. Implements `IBot`.
2. **New interface `IBot`** at `Source/ACE.Server/Bots/IBot.cs` (new
   folder). Defines `OnBrainTick(double currentUnixTime)` plus a
   minimal action surface (`Say`, `Move`, `Attack`) that wraps
   existing `Creature`/`WorldObject` primitives.
3. **Per-tick brain hook in `Creature.Monster_Tick`** — one
   `if (this is IBot bot) bot.OnBrainTick(currentUnixTime);` near the
   top, after `IsAwake`/`IsDead` checks. Upstream-justifiable as
   "expose a per-creature AI extensibility point" (any content mod
   benefits).
4. **Inbound `/tell` shim in `GameActionTell.Handle`** — a Guid check
   before the `targetPlayer.Session.Network.EnqueueSend(...)` call
   that routes to `BotCreature.OnTellReceived(...)` if the target is
   a bot.
5. **New console-command handler at
   `Source/ACE.Server/Command/Handlers/BotCommands.cs`** — Developer/
   Admin only: `/spawnbot <archetype>` (spawn at caller location),
   `/despawnbot <guid>` (despawn one), `/listbots` (list all live
   bots).
6. **One additive schema column `Character.is_Bot` (BIT, default 0)**
   in a new migration under `Source/ACE.Database/DatabaseSetupScripts/Updates/`.
   Bots are exempt from `IsAccountAtMaxCharacterSlots`. Added when
   bots first persist (M2+), not in M1.

That is the complete fork surface. Anything not on this list lives
in the BotBrain code (out-of-fork) per ADR-0001
([`adr/0001-start-in-process-then-sidecar.md`](adr/0001-start-in-process-then-sidecar.md)).

## Why

We need a session-less entity that ACE ticks for us, that can move,
attack, and chat like any other denizen — without inheriting the
assumption of a live network session.

Q1 ([`research/ace-investigation.md`](research/ace-investigation.md#q1-how-is-a-player-instantiated-and-what-assumptions-about-a-network-session-does-it-carry))
found that `Player` is pervasively session-coupled (every action
method calls `Session.Network.EnqueueSend(...)`), while `Creature`
already has the AI tick (`Monster_Tick`), movement, and combat we
need without that coupling. ACE also ships an `OfflinePlayer`
first-class concept — but for offline humans, not autonomous bots.
Different semantics; not reusable for bots.

Doing this from outside ACE is not feasible because `Creature`
behaviors (movement, combat resolution, landblock membership) live
behind internal methods that aren't exposed to out-of-process callers
without packet-faking we explicitly don't want
([`architecture.md`](architecture.md) "Where the ACE fork actually
changes").

## Minimal-diff justification

Governed by the bar in
[`adr/0002-minimal-fork-bar.md`](adr/0002-minimal-fork-bar.md):
"could this be opened as a PR against upstream ACE without being
laughed out of the room?" Each change above gets a one-paragraph
upstream justification when its PR opens. Sketches:

- `BotCreature` + `IBot` — additive, new types, doesn't touch
  existing semantics. PR-able as "add an extension class for
  server-side autonomous creatures".
- `Monster_Tick` brain hook — one `if`/call inserted. PR-able as
  "expose a per-creature AI extensibility point any content mod can
  use."
- `GameActionTell.Handle` shim — defensive Guid check. PR-able as
  "support non-session chat targets for server-side bots."
- `BotCommands` — same shape as existing `/create`
  (`AdminCommands.cs:2175-2187`). PR-able as a diagnostic command
  set.
- `Character.is_Bot` column — additive schema with default 0;
  existing characters and existing queries unaffected. PR-able as a
  general "mark non-human characters" capability.

If any change cannot pass that bar, it moves out of the fork into
BotBrain.

## Threading model

Per [`adr/0004-bot-tick-via-monster-tick.md`](adr/0004-bot-tick-via-monster-tick.md):

Bots tick on the thread currently driving their landblock — the
landblock's `sortedCreaturesByNextTick` scheduler treats them like
any other creature. Within a landblock, ticks are serial; across
landblocks, ticks may be parallel (groups under `Parallel.ForEach`).

Bot brain work that could exceed ~5 ms (LLM call, multi-landblock
plan, blocking I/O) is **async-by-contract**: submitted during a
tick and **collected** on a later tick via a `BrainTickResult`
pending-future. A slow brain stalls one landblock at most, and only
briefly. ADR-0001 splits the Social layer into a sidecar before M5
to keep model latency off the world thread entirely.

## BotCreature vs. BotPlayer decision

Per [`adr/0003-botcreature-not-botplayer.md`](adr/0003-botcreature-not-botplayer.md):
we subclass `Creature`. We do not subclass `Player`. The fight to
make `Player` session-safe across login, tick, combat, magic, use,
and packet-send paths is not worth winning when `Creature` gives us
the AI infrastructure for free.

## Chat hook strategy

Per [`adr/0006-chat-via-creature-broadcast.md`](adr/0006-chat-via-creature-broadcast.md):

- **Outbound** — `BotCreature.Say(message)` directly calls
  `EnqueueBroadcast(new GameMessageHearSpeech(message,
  GetNameWithSuffix(), Guid.Full, ChatMessageType.Speech),
  LocalBroadcastRange, ChatMessageType.Speech)` — the same primitive
  `Player.HandleActionTalk` uses internally. Bot speech is
  wire-identical to player speech.
- **Inbound** — `GameActionTell.Handle` shim: Guid check on target
  before dereferencing `Session`. Bot target → routed to
  `BotCreature.OnTellReceived(senderGuid, message)`.
- **Outbound /tell** (M5) — `BotCreature.SendTellTo(targetGuid, message)`
  constructs the standard `GameEventTell` packet and sends via the
  human target's `Session`. Sender doesn't need a session.

Allegiance, trade, and other player-only channels are out of scope
for M1–M4.

## Pathfinding

Per [`adr/0005-pathfinding-reuse-and-build.md`](adr/0005-pathfinding-reuse-and-build.md):

ACE has no real pathfinding (Q4 confirmed: no A\*, no navmesh, no
Recast/Detour). For M2–M3 bots reuse ACE's motion execution
(`MoveToObject`, `TurnTo`, `PhysicsObj.transition`) and LOS
(`WorldObject.IsVisibleTarget`). We build a simple LOS+waypoint path
planner in BotBrain (out of fork). Recast/Detour is reserved for M7
if telemetry forces it.

## Persistence

Q5 finding (resolution to be reified as a pre-M6 ADR): bots are
stored as `Character` + `Biota` rows in `ace_shard` like any human,
parented by a single shared `bot-system` account in `ace_auth`,
flagged via the new `Character.is_Bot` BIT column. Reuses ACE's
existing biota save cadence
(`Source/ACE.Server/WorldObjects/Player_Database.cs:38-43`). Brain
state (memories, archetype config, journal) lives in a separate
sidecar DB starting M6.

`IsAccountAtMaxCharacterSlots` (`PlayerManager.cs:894-911`) is
modified to skip `is_Bot=1` characters in the count. That's the only
non-additive change in the persistence area.

## Open risks

- **The `Monster_Tick` brain hook will be the most-scrutinized
  upstream change.** If maintainers reject the hook shape, we may
  need a different extension point (per-Landblock subscriber list, or
  a creature-type registry). Cost of rejection: medium — the rest of
  the fork is shaped to be hook-shape-agnostic.

- **Bot chat client-side rendering.** Q3 verified `EnqueueBroadcast`
  produces the same packet humans send, but we have not verified
  client-side rendering (nameplate color, prefix, `/who` entry). If
  the client distinguishes `Creature` from `Player` by Guid range or
  flag, we may need a small visual override. Investigated in M1.

- **`is_Bot` column collision with downstream forks.** Some ACE
  forks already extend the `Character` table. We need to verify our
  column name and migration ID don't collide. Mitigated by adopting
  a fork-namespaced migration ID prefix.

- **Simple path planner stuck-rate.** LOS+waypoint may fail too often
  in tight dungeon geometry. We've reserved Recast/Detour as the
  fallback. Telemetry from M2–M3 decides.

- **`Session?.Network?.EnqueueSend(...)` guards in `/allegiance` and
  similar channels.** When bots eventually join allegiances (M5+),
  we'll need defensive null-checks on Session in
  `TurbineChatHandler.cs:114-247`. Not in scope for M1–M4 but worth
  remembering.

## See also

- [`research/ace-investigation.md`](research/ace-investigation.md) —
  Q1–Q5 findings
- [`local-install.md`](local-install.md) — Windows 11 ACE local
  install procedure (the running server this plan was investigated
  against)
- [`architecture.md`](architecture.md) — overall design this fork
  plan implements
- [`adr/0001-start-in-process-then-sidecar.md`](adr/0001-start-in-process-then-sidecar.md)
- [`adr/0002-minimal-fork-bar.md`](adr/0002-minimal-fork-bar.md)
- [`adr/0003-botcreature-not-botplayer.md`](adr/0003-botcreature-not-botplayer.md)
- [`adr/0004-bot-tick-via-monster-tick.md`](adr/0004-bot-tick-via-monster-tick.md)
- [`adr/0005-pathfinding-reuse-and-build.md`](adr/0005-pathfinding-reuse-and-build.md)
- [`adr/0006-chat-via-creature-broadcast.md`](adr/0006-chat-via-creature-broadcast.md)
- [`../roadmap/m0-checklist.md`](../roadmap/m0-checklist.md)

