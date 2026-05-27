# 0003. Subclass `Creature`, not `Player`, for in-server bots

- **Status:** Superseded by [ADR-0007](0007-bots-as-player-not-creature.md)
- **Date:** 2026-05-27
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** [0007](0007-bots-as-player-not-creature.md)

> **Note (2026-05-30):** This ADR is superseded by
> [ADR-0007](0007-bots-as-player-not-creature.md). The user directive
> that bots must "aggro mobs and interact with NPCs as real players"
> requires the `Player` path. ADR-0007 also corrects an oversight in
> this ADR: `Player` has a second constructor —
> `Player(Weenie, ObjectGuid, uint accountId)` — that does not take a
> `Session`, which was the basis for the "session coupling is
> unavoidable" reasoning below. The reasoning here is preserved as
> historical context.

## Context

[`docs/research/ace-investigation.md`](../research/ace-investigation.md)
Q1 asked whether our in-server bot should subclass ACE's `Player` (a
"headless player" sharing Player behaviors) or `Creature` (a more
isolated entity that re-implements what it needs). The answer drives
the entire shape of the fork.

Investigation of `C:\Users\darin\repos\ACE` master @ `9bc20cbd` found:

- `Player` has two constructors; the load/login one takes a `Session`
  (`Source/ACE.Server/WorldObjects/Player.cs:114-127`) and the type is
  not nullable in signature.
- `Session.Network.EnqueueSend(...)` is called pervasively from Player
  partials: tick (`Player_Tick.cs:36-82`), combat
  (`Player_Combat.cs:117-163`), action handlers (`Player_Use.cs:29-90`),
  packet send helpers (`Player_Networking.cs:214-263`), and many more.
- ACE already ships a first-class `OfflinePlayer`
  (`Source/ACE.Server/Entity/OfflinePlayer.cs:13-44`,
  `PlayerManager.cs:32-34,184-205,402-420`) and an `IPlayer` interface
  (`Source/ACE.Server/Entity/IPlayer.cs:10-13`) for the offline case —
  but `OfflinePlayer` is for human players who are logged out, not for
  autonomous bots that are present in the world.
- `Creature` already has the AI primitives we want: the per-tick AI
  hook (`Creature_Tick.cs:14-49`, `Monster_Tick.cs:17-174`), combat
  plumbing (`Creature_Combat.cs:52-125`), and movement helpers
  (`Creature_Navigation.cs`). It has no `Session` field.

Why decide now: every line of bot code from M1 onward inherits this
choice. Picking wrong means the M0 fork plan
([`../ace-fork-plan.md`](../ace-fork-plan.md)) ships a class hierarchy
that fights ACE for the rest of the project.

## Decision

The in-server bot is a class `BotCreature : Creature`. It is
session-less by construction. We do not subclass `Player`.

Bot capabilities that ACE today only exposes on `Player` (inventory
manipulation, /tell as sender, certain skill checks) are reached via
small shim layers built on top of `BotCreature` — not by inheriting
them.

## Options considered

### Option A — `BotPlayer : Player`

- Pros:
  - Free inheritance of player combat, chat, /tell, inventory, skills,
    equipment, allegiance behavior.
  - Bots show up identically to humans in `/who`, the world, and the
    client — no rendering or visibility special cases.
- Cons:
  - Every `Session.Network.EnqueueSend(...)` site in the Player
    partials must be guarded or routed. Q1 found this surface is
    pervasive — tick, combat, use, magic, chat, world-entry, packet
    send helpers.
  - Even a "no-op session" pattern requires constructing a fake
    `Session` and `NetworkSession` that no-op every send. That fake
    has to track ACE upstream's session-shape changes forever.
  - Player has player-only state (deeds, allegiance officer-ship,
    house ownership) that has no meaning for bots and that we'd be
    carrying for free.
  - Violates the upstreamable-PR bar in
    [`0002-minimal-fork-bar.md`](0002-minimal-fork-bar.md): "make
    Player work without a Session" is not a change an ACE maintainer
    would accept as a generally-useful PR.

### Option B — `BotCreature : Creature`

- Pros:
  - No `Session` coupling — `Creature` doesn't have one.
  - Reuses the existing per-creature AI tick (`Monster_Tick`), combat,
    and movement that ACE has already debugged.
  - Minimal-fork-bar compliant: the only new ACE class is
    `BotCreature`; the only new hook is per-tick brain invocation,
    which is a generally-useful extension point (any content mod
    benefits).
  - Bots remain conceptually distinct from humans in code, which
    matches how we'll want to treat them in observability and
    moderation.
- Cons:
  - We do not get player-only behaviors (/tell as sender, allegiance
    chat, skill UI, inventory UI) for free. We add small shims for
    the ones we need.
  - Default Creature rendering and client-side visibility may differ
    from Player in subtle ways (nameplate color, /who entry). We'll
    investigate during M1.

### Option C — A new `WorldObject` subclass that is neither Player nor Creature

- Pros:
  - Maximum freedom to design from scratch.
- Cons:
  - Abandons ACE's existing AI/combat/movement code. We rebuild
    everything `Creature` already does.
  - Largest fork by far.
  - No precedent in ACE for an entity that is "alive and acting" but
    is neither a Player nor a Creature. We'd be inventing a new
    category for the engine to support.

## Consequences

- **Easier:**
  - No fight with `Session` assumptions.
  - Existing creature AI tick is the per-bot heartbeat (see
    [`0004-bot-tick-via-monster-tick.md`](0004-bot-tick-via-monster-tick.md)).
  - The fork's central change is one new class and one new hook, both
    additive.
- **Harder:**
  - Bots can't reuse Player-only systems by inheritance. We add small
    shims for chat receive (/tell to a bot — see
    [`0006-chat-via-creature-broadcast.md`](0006-chat-via-creature-broadcast.md)),
    `/who` listing, and any future player-only surface bots need.
  - We need to verify in M1 that a `BotCreature` is visible to clients
    in a way that looks like a player (not a monster nameplate). If
    not, we add a small visual override.
- **Follow-ups:**
  - Decide bot chat shim in
    [`0006-chat-via-creature-broadcast.md`](0006-chat-via-creature-broadcast.md).
  - Decide pathfinding in
    [`0005-pathfinding-reuse-and-build.md`](0005-pathfinding-reuse-and-build.md).
  - Decide threading in
    [`0004-bot-tick-via-monster-tick.md`](0004-bot-tick-via-monster-tick.md).
  - During M1, confirm a `BotCreature` appears in the client as a
    player-like entity (name color, nameplate, `/who` entry). If not,
    add minimum visual override.

## References

- Related doc(s):
  - [`../research/ace-investigation.md`](../research/ace-investigation.md) (Q1)
  - [`../ace-fork-plan.md`](../ace-fork-plan.md)
  - [`../architecture.md`](../architecture.md) ("Where the ACE fork actually changes")
- Related open question(s):
  - "BotPlayer vs. souped-up Creature" in
    [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md)
  - Resolved by this ADR
- Related issue(s) / PR(s):
  - [#1](https://github.com/darinh/ac-ai-players/issues/1) — Q1 research issue
