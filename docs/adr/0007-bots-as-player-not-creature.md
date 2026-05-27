# 0007. Subclass `Player`, not `Creature`, for in-server bots (reverses ADR-0003)

- **Status:** Proposed
- **Date:** 2026-05-30
- **Deciders:** @darinh
- **Supersedes:** [0003](0003-botcreature-not-botplayer.md)
- **Superseded by:** _(none)_

## Context

[ADR-0003](0003-botcreature-not-botplayer.md) chose `BotCreature : Creature`
on the grounds that `Player` was pervasively coupled to `Session.Network.EnqueueSend(...)`
and a session-less `Player` would force a "fake session" pattern that
fights ACE upstream forever. The M1 spike on `ACE-bots@botplayer-spike`
shipped that decision: bots stand in the world, chat, follow, persist,
and hot-reload.

Two things have changed since ADR-0003:

1. **User directive (2026-05-30).** Verbatim:

   > "Be sure that your bots are spawned like REAL PLAYERS in the server,
   > not creatures. They should interact with the environment and other
   > NPCs just like players. They should aggro mobs, etc."

   The original M1 deliverables in [`../../roadmap/milestones.md`](../../roadmap/milestones.md)
   literally said `BotPlayer`. ADR-0003 took the `Creature` shortcut for
   tractability; the spike validated that the shortcut works for *some*
   capabilities — but the user is explicit that the north-star
   requirement is to be a Player, not a Creature-shaped lookalike.

2. **A constructor ADR-0003 overlooked.** `Player` has two constructors
   (`Source/ACE.Server/WorldObjects/Player.cs`):

   - **Login path** (line 114-127):
     `Player(Biota, IEnumerable<Biota>, IEnumerable<Biota>, Character, Session)`
     — `Session` is required by signature, takes a real network session.
   - **Character-create path** (line 83-93, the one ADR-0003 missed):
     `Player(Weenie weenie, ObjectGuid guid, uint accountId)` — no
     `Session` parameter, builds a fresh `Character` row from a Weenie.
     This is the path ACE uses when a human creates a new character;
     the `Session` field is set later when the human first logs in.

   That second constructor is the entry point for session-less `Player`
   instantiation that ADR-0003 said didn't exist. It does exist. The
   bot just has to leave `Session` as the no-op pattern described below.

The pervasive `Session.Network.EnqueueSend(...)` coupling in Player
partials is still real. The cost ADR-0003 worried about hasn't gone
away. But the *capability* of being a real Player — recognised by
mob aggro tables, NPC interaction handlers, allegiance routing,
`/who`, `/whotitled`, chat distance, faction targeting — is the
critical use case that `Creature` can't deliver cleanly. The user
is willing to pay the upstream-divergence cost to get it.

Why decide now: M1 work already shipped on the `Creature` path. Every
additional bot capability built on `BotCreature` either makes the
eventual migration harder or has to be re-validated post-migration.
The longer we wait, the more rework we accumulate.

## Decision

The in-server bot is a class `BotPlayer : Player`, persisted to
`ace_shard` as a `Character` + `Biota` row with `IsBot = 1`
(additive BIT column — see [#24](https://github.com/darinh/ac-ai-players/issues/24)).
Bot accounts are parented by a shared `bot-system` account in
`ace_auth`. On spawn, the bot transitions from `OfflinePlayer` to
online via the existing `PlayerManager.SwitchPlayerFromOfflineToOnline`
path — so it registers in `PlayerManager.onlinePlayers` and is
visible to every system that iterates that collection (`/tell`
lookup, `pop`, `listplayers`, shutdown logout loops).

To avoid sending packets to a network endpoint that doesn't exist,
the bot carries a real `Session` (subclass `BotSession`) holding a
real `NetworkSession` (subclass `BotNetworkSession`) whose send
methods are overridden to no-op and dispatch to the brain instead.
This requires a small additive ACE-fork change: mark a handful of
`NetworkSession` send methods as `virtual` so they can be overridden
in the subclass. The non-virtual default in upstream ACE is the
single biggest reason the naive "NullSession" pattern doesn't
work; making four-to-six methods virtual is the minimum fork delta
that closes the gap.

`BotPlayer` overrides `LogOffPlayer` / `Terminate` semantics to
remove cleanly from `PlayerManager.onlinePlayers` and signal
`BotManager` — so server shutdown does not hang waiting for a
network ack the bot will never send.

The existing inbound-`/tell` shim in `GameActionTell.Handle` is
**retained** under the new design. Even after `EnqueueSend` is
overridden to a no-op, the bot needs an explicit dispatch into its
brain (`OnReceivedTell`) — the no-op alone drops the message
without notification. The shim becomes a one-line check: if the
target `IsBot`, call its tell hook in addition to (or instead of)
the packet send.

We do not keep `BotCreature` for the long term. During the
migration it stays in parallel under a feature flag for rollback;
once `BotPlayer` reaches parity on all M1 shipped capabilities,
`BotCreature` is removed and ADR-0003 is fully retired.

### Bot lifecycle (the part ADR-0003 didn't have to think about)

1. **First spawn (new bot):** `BotManager.SpawnBot(archetype, name)`
   → write `Character` row (`IsBot=1`) + `Biota` to `ace_shard`
   under the shared `bot-system` account → load via
   `PlayerManager.AddOfflinePlayer` → call
   `PlayerManager.SwitchPlayerFromOfflineToOnline(bot)` →
   `BotPlayer` enters its starting landblock.
2. **Respawn (existing bot, e.g. on `autoSpawnFromRoster`):**
   `PlayerManager` already loaded the `OfflinePlayer` on
   server-start; `BotManager` just calls
   `SwitchPlayerFromOfflineToOnline`.
3. **Despawn / brain-initiated logout:** `BotPlayer.LogOffBot()`
   removes from `onlinePlayers`, persists biota via the existing
   biota cadence, leaves the `Character` row in place. Next
   spawn rehydrates.
4. **Server shutdown:** `BotManager` hooks the shutdown sequence
   to log all bots off cleanly **before** the shutdown loop
   reaches the network-ack wait, so the `Session.LogOffPlayer(true)`
   path that humans take is bypassed for bots.
5. **`BootAllPlayers` / forced disconnect:** `BotPlayer` provides
   sane defaults for the `AccessLevel` and account checks that
   `BootAllPlayers` reads (`PlayerManager.cs:815-818`) — bots
   are bootable or skipped by explicit policy, not by accidental
   null-default semantics.

### Acceptance criteria (must pass before `BotCreature` is removed)

Adapted from the rubber-duck review:

- [ ] Bot appears in `PlayerManager.GetOnlinePlayer(name)` —
  `/tell` lookup finds it
- [ ] Inbound `/tell` reaches `BotPlayer.OnReceivedTell` (brain
  receives the message; packet send is no-op)
- [ ] `pop` and `listplayers` include bot count
- [ ] Server shutdown does not hang with bots online — completes
  in the same time it would with the same number of human
  players logged in cleanly
- [ ] `BootAllPlayers` does not corrupt bot state or NRE
- [ ] A monster with `Tolerance.Monster` aggros a `BotPlayer`
  standing in range (the killer use case)
- [ ] Vendor `ApproachVendor(Player)` accepts a `BotPlayer`
- [ ] NPC direct-talk emote dispatches to a `BotPlayer` like a
  human player
- [ ] All 10 M1 shipped capabilities (issues
  [#6](https://github.com/darinh/ac-ai-players/issues/6)–[#15](https://github.com/darinh/ac-ai-players/issues/15))
  pass their original smoke tests under `BotPlayer`

## Options considered

### Option A — `BotPlayer : Player` with `BotSession`/`BotNetworkSession` + OfflinePlayer→Online lifecycle (selected)

- **Pros:**
  - Bots are first-class players for every system that iterates
    `PlayerManager.onlinePlayers` (`/tell` lookup, `pop`,
    `listplayers`, shutdown loops, faction targeting).
  - Mob targeting that filters by `Tolerance.Monster` (excludes
    Player from the tolerated list — so monsters DO aggro players)
    works for `BotPlayer` automatically.
  - Vendor (`ApproachVendor(Player ...)`), direct-talk emotes
    (`EmoteManager.cs:2047-2053`), local chat
    (`EmoteManager.cs:2074-2079`), and `/tell` lookup all accept
    `BotPlayer` because they're typed against `Player`.
  - Matches the original M1 deliverable text in
    `roadmap/milestones.md` and the architecture diagram in
    `docs/architecture.md`.
  - `Player(Weenie, ObjectGuid, accountId)` constructor exists
    for the first-spawn path — no new constructor needed (just
    the lifecycle wrapper around it).
  - Persistence becomes the existing `Character` + `Biota` cadence
    — closes issue [#24](https://github.com/darinh/ac-ai-players/issues/24)'s ADR shape.

- **Cons:**
  - Requires marking ~4-6 `NetworkSession` send methods as
    `virtual` in the ACE fork (`EnqueueSend`, `Update`,
    `ProcessPacket`, `ReleaseResources`, plus any related ones).
    Additive change but pulls the fork outside ADR-0002's
    upstreamable-PR bar. Maintenance: a few minutes per ACE
    upstream rebase.
  - Bot lifecycle (Character row + OfflinePlayer transition +
    custom shutdown path) is the part ADR-0003 didn't have to
    think about. It's tractable but larger than a single class.
  - All M1 capabilities must be re-validated under `BotPlayer`
    (see Acceptance Criteria above). Most should transfer cleanly
    (chat, follow, persistence, hot-reload, auto-spawn);
    greeter-mute and tell-dispatch need re-investigation.
  - `Player` partials have player-only state (deeds, allegiance
    officer-ship, house ownership) that bots will carry as no-op
    defaults. Inert but adds memory footprint per bot.
  - The tick mechanism for `BotPlayer` (`Player_Tick.Tick` +
    `Heartbeat`, not `Monster_Tick`) means ADR-0004's mechanism
    doesn't carry forward. **This is in scope for the migration,
    not deferred** — ADR-0008 lands as part of the migration epic,
    most likely choosing a brain hook inside `Player_Tick.Tick`
    or a dedicated `BotManager`-driven scheduler.

### Option B — Keep `BotCreature : Creature` (ADR-0003 status quo)

- **Pros:**
  - Already shipped. No migration work.
  - Stays inside ADR-0002's minimal-fork bar.
  - No `Session` coupling to manage.

- **Cons:**
  - Doesn't satisfy the user directive. Bots don't appear in
    `PlayerManager.onlinePlayers` so `/tell` lookup misses them
    (verified — this is the bug behind
    `files/plan-fix-tell-to-bot-routing.md`). Bots don't show
    in `pop` or `listplayers`. Vendor and quest-NPC interactions
    refuse the bot because the Player typecheck fails.
  - The original M1 vision (`roadmap/milestones.md` says
    `BotPlayer`) was always Player; ADR-0003 was a tactical
    detour, not the destination.
  - Indefinitely accumulates shims (chat receive, /who listing,
    nameplate override, vendor override, NPC interaction override)
    to fake being a player. Each shim a new place where bots
    behave subtly unlike players.

### Option C — `BotPlayer : Player` with per-callsite `Session?.` guards (no virtualization)

- **Pros:**
  - No new `BotSession`/`BotNetworkSession` classes — just
    defensive null-checks on every `Session.Network.EnqueueSend(...)`
    call site.

- **Cons:**
  - The defensive-null change has to be applied to every send
    site in every `Player_*.cs` partial. ADR-0003 estimated this
    surface as dozens of sites; spread across tick, combat,
    magic, use, chat, world-entry, packet-send helpers.
  - Every upstream Player change risks introducing a new
    unguarded `EnqueueSend` site — silent regression that NREs
    the server. `BotNetworkSession.EnqueueSend` override makes
    the no-op pattern explicit and centralised; null-guards
    diffuse it across the codebase.
  - Loses the chance to capture useful "what would have been
    sent" telemetry in one place for future debugging.
  - Doesn't solve the lifecycle problem (Finding 2-3 from the
    rubber-duck review) — that's separate from the send story
    regardless of which send pattern we pick.

### Option D — Headless real `Session` connected to a fake transport sink (no overrides)

- **Pros:**
  - Player code paths run unchanged — they really do construct
    and send packets.
  - No virtualization required.

- **Cons:**
  - Significant runtime overhead: every bot pays
    packet-construction, serialisation, and queue costs for
    packets nobody reads.
  - The "fake transport sink" still has to be a working
    `INetwork` / `NetworkSession` — almost the same code as
    `BotNetworkSession`, plus a fake socket and connection
    state machine.
  - Bots would acquire a slot in `NetworkManager` session
    tracking — risks colliding with the real-player
    connection-count metrics and rate-limits.
  - Brain still needs an explicit dispatch path for `/tell`
    and other player-targeted events — the network packet
    being sent doesn't translate into a brain notification
    without explicit dispatch.

### Option E — A new `WorldObject` subclass that is neither Player nor Creature

- **Pros:**
  - Maximum freedom to design from scratch.

- **Cons:**
  - Doesn't satisfy the user directive (bot isn't a Player to
    any of the systems that key off Player).
  - Abandons ACE's existing AI/combat/movement code. We rebuild
    everything `Creature` and `Player` already do.
  - Largest fork by far.

## Consequences

### Easier

- Mob targeting via `Tolerance.Monster` (`Monster_Awareness.cs:269-271`)
  accepts `BotPlayer` automatically — bots get aggroed by hostile
  creatures the way players do, no shim required.
- Vendor (`Vendor.ApproachVendor(Player ...)`,
  `Vendor.cs:280-295`), direct-talk emotes
  (`EmoteManager.cs:2047-2053`), local-chat NPC reactions
  (`EmoteManager.cs:2074-2079`), and `/tell` lookup
  (`GameActionTell.cs:27-53`) all accept `BotPlayer` by type.
- Online-player surfaces (`PlayerManager.GetOnlinePlayer(name)`,
  `GetAllOnline()`, `GetOnlineCount()` — `PlayerManager.cs:378-386`)
  include bots automatically once the OfflinePlayer→Online
  transition runs.
- Persistence becomes natural: bots are real `Character` + `Biota`
  rows with `IsBot=1`, saved by the existing biota cadence (issue
  [#24](https://github.com/darinh/ac-ai-players/issues/24) ADR shape
  becomes simpler).
- The TSV roster from `BotPersistence` either retires (Character
  rows are now the source of truth) or shrinks to just-the-spawn-list.

### Harder

- The fork has to own `BotSession`, `BotNetworkSession`, and the
  `NetworkSession`-method-virtualization diff permanently.
  Documented maintenance task: re-apply on each ACE upstream
  rebase; expect a few minutes of fix-up per rebase.
- The tick mechanism (ADR-0004 — `Monster_Tick`-driven bot brain)
  does **not** carry forward. `Player` ticks via `Player_Tick.Tick`
  + `Heartbeat` (`Player_Tick.cs:112-144`, `Landblock.cs:592-593`).
  **ADR-0008 lands as part of the migration epic** with the
  decision: brain hook in `Player_Tick.Tick`, or a separate
  landblock-level bot scheduler driven by `BotManager`.
- Bot lifecycle is more involved (Character row creation +
  OfflinePlayer transition + custom shutdown). The compensation
  is that `/tell`, `pop`, and `listplayers` all start working
  with zero further shims.
- Every M1 shipped capability must be re-validated under
  `BotPlayer`. Acceptance criteria above are the checklist.
- Larger fork patch than ADR-0002's upstreamable-PR bar.
  Explicitly accepted — the user has opted into the maintenance
  cost.

### Follow-ups

- **ADR-0008 (in scope for migration, not deferred):** tick
  mechanism for `BotPlayer`. Options: brain hook in
  `Player_Tick.Tick`; `BotManager`-driven landblock-level
  scheduler; async coroutine pump.
- **Migration epic** lays out the staged reversal: write ADR
  (this doc, plus ADR-0008) → audit `EnqueueSend` surface and
  identify virtualization targets → build `BotSession` /
  `BotNetworkSession` → build `BotPlayer` lifecycle wrapper →
  migrate BotManager / BotPersistence / `/spawnbot` →
  re-validate against acceptance criteria → remove `BotCreature`.
- **Update `docs/ace-fork-plan.md`** to reflect Player-not-Creature
  (BotCreature references throughout become BotPlayer; add the
  NetworkSession virtualization line; add the OfflinePlayer
  transition).
- **Update `docs/architecture.md`** — the original text already
  said `BotPlayer subclasses Player without a session`; verify
  and align.
- **`/tell` plan** in `files/plan-fix-tell-to-bot-routing.md`:
  the lookup half becomes obsolete (`PlayerManager.GetOnlinePlayer`
  finds bots automatically); the dispatch half (calling
  `BotPlayer.OnReceivedTell` from `GameActionTell.Handle`)
  **is retained**.
- **Verify in migration:** client-side rendering, nameplate
  colour, `pop`/`listplayers` listing, allegiance chat routing,
  faction targeting, Pet.cs follow-distance constants under
  Player movement, `BootAllPlayers` interaction.

## References

- Related doc(s):
  - [0003-botcreature-not-botplayer.md](0003-botcreature-not-botplayer.md) (superseded)
  - [0002-minimal-fork-bar.md](0002-minimal-fork-bar.md) (explicitly relaxed for this decision)
  - [0004-bot-tick-via-monster-tick.md](0004-bot-tick-via-monster-tick.md) (superseded by [ADR-0008](0008-bot-tick-via-player-tick.md))
  - [0008-bot-tick-via-player-tick.md](0008-bot-tick-via-player-tick.md) (the replacement tick mechanism)
  - [`../ace-fork-plan.md`](../ace-fork-plan.md) (needs update)
  - [`../architecture.md`](../architecture.md) (needs re-alignment)
  - [`../research/ace-investigation.md`](../research/ace-investigation.md) Q1
- Related issue(s) / PR(s):
  - [#1](https://github.com/darinh/ac-ai-players/issues/1) — Q1 research issue
  - [#24](https://github.com/darinh/ac-ai-players/issues/24) — `Character.IsBot` ADR shape (now simpler — natural fit for OfflinePlayer transition)
  - bots-as-real-players migration epic (filed alongside this ADR)
- Source citations (ACE master @ commit `9bc20cbd`):
  - `Source/ACE.Server/WorldObjects/Player.cs:83-93` — character-create constructor (no `Session`)
  - `Source/ACE.Server/WorldObjects/Player.cs:114-127` — login constructor (`Session` required)
  - `Source/ACE.Server/WorldObjects/Player_Networking.cs:22-24` — `PlayerEnterWorld` → `SwitchPlayerFromOfflineToOnline`
  - `Source/ACE.Server/Managers/PlayerManager.cs:402-417` — `SwitchPlayerFromOfflineToOnline` requires existing `OfflinePlayer`
  - `Source/ACE.Server/Managers/PlayerManager.cs:378-386` — `GetAllOnline` returns `onlinePlayers`
  - `Source/ACE.Server/Managers/PlayerManager.cs:815-818` — `BootAllPlayers` reads `Session.AccessLevel`
  - `Source/ACE.Server/Network/Session.cs:32` — `Session.Network` typed as concrete `NetworkSession`
  - `Source/ACE.Server/Network/NetworkSession.cs:117,141,165,182,269,958` — send methods that must be virtualized
  - `Source/ACE.Server/Managers/ServerManager.cs:116-149` — shutdown loops over `GetAllOnline` and waits for logout
  - `Source/ACE.Server/WorldObjects/Monster_Awareness.cs:238-271,351-357` — monster targeting + `Tolerance.Monster` filtering
  - `Source/ACE.Server/Network/GameAction/Actions/GameActionTell.cs:27-53` — `/tell` lookup + dispatch
  - `Source/ACE.Server/WorldObjects/Player_Tick.cs:112-144` — Player tick + heartbeat
  - `Source/ACE.Server/Entity/Landblock.cs:592-593,640-643` — Player tick scheduling
  - `Source/ACE.Server/WorldObjects/EmoteManager.cs:2021-2079` — NPC interaction (Creature vs Player typing)
  - `Source/ACE.Server/WorldObjects/Vendor.cs:280-295` — `ApproachVendor(Player)`
- Rubber-duck critique (gpt-5.5) — 2026-05-30 — identified the
  non-virtual `NetworkSession` methods, the `PlayerManager.onlinePlayers`
  registration gap, the shutdown-hang risk, and the `/tell` dispatch
  gap. This ADR is the revision incorporating those findings.
