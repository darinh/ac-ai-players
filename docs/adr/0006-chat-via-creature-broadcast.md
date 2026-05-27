# 0006. Bot chat via direct `EnqueueBroadcast`; `/tell` to bots via a guid shim

- **Status:** Proposed
- **Date:** 2026-05-27
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

[`docs/research/ace-investigation.md`](../research/ace-investigation.md)
Q3 asked how chat is routed in ACE and what shape the bot chat hook
should take. Bots need both directions: to **say** things humans hear,
and to **receive** /tells from humans the bot can answer.

Investigation of `C:\Users\darin\repos\ACE` master @ `9bc20cbd` found:

- `Player.HandleActionTalk(string)`
  (`Source/ACE.Server/WorldObjects/Player.cs:792-802`) broadcasts
  local chat via `EnqueueBroadcast(new GameMessageHearSpeech(...),
  LocalBroadcastRange, ChatMessageType.Speech)`. This is the
  primitive — it doesn't require `Session`, it's per-`WorldObject`.
- Player emote/soul-emote work the same way
  (`Player.cs:822-845` — `EnqueueBroadcast(new GameMessageSoulEmote(...))`).
- `/tell` (`Source/ACE.Server/Network/GameAction/Actions/GameActionTell.cs:15-54`)
  does `targetPlayer.Session.Network.EnqueueSend(new GameEventTell(...))`
  — i.e., it dereferences the target's `Session`. A bot target with no
  `Session` would NPE here.
- `/allegiance`
  (`Source/ACE.Server/Network/Handlers/TurbineChatHandler.cs:114-247`)
  iterates allegiance members and `online.Session.Network.EnqueueSend(...)`
  per member — same NPE risk for bot members.
- `EmoteManager` already lets non-Player creatures speak via
  `GameMessageCreatureMessage` and emotes — i.e., the engine already
  models "speech from a non-Player entity" cleanly.

Per [`0003-botcreature-not-botplayer.md`](0003-botcreature-not-botplayer.md)
the bot is `BotCreature : Creature`, so calling `Player`
chat methods directly is not an option (we don't inherit them, and
those methods are session-coupled anyway).

Why decide now: chat is on the M2/M4 critical path (scripted lines for
archetypes), and the M5 LLM Social layer rests on whatever bot chat
shape we pick now.

## Decision

**Bot speaks (outbound)**: `BotCreature.Say(message)` directly calls
`EnqueueBroadcast(new GameMessageHearSpeech(message, GetNameWithSuffix(),
Guid.Full, ChatMessageType.Speech), LocalBroadcastRange,
ChatMessageType.Speech)` — the same primitive `Player.HandleActionTalk`
uses internally. Soul-emote uses the same pattern with
`GameMessageSoulEmote`. No `Session` involved at any step.

**Bot is told (inbound /tell)**: a small shim in `GameActionTell.Handle`
checks the target's Guid against the bot registry before dereferencing
`targetPlayer.Session`. If the target is a bot, the message is handed
to the bot's `BotBrain.OnTellReceived(senderGuid, message)` instead of
sent over the wire. The bot's brain decides whether/how to reply.

**Bot sends /tell to a human (M5)**: the brain calls
`BotCreature.SendTellTo(targetPlayerGuid, message)`, which constructs
the same `GameEventTell` packet and sends it over the target human's
`Session`. The bot is the sender by Guid; no session is required on
the sender side, because `GameEventTell` only needs the target's
session to deliver.

**Allegiance chat (deferred to M5+)**: bots do not join allegiances
in M1–M4. When they do, the allegiance-chat broadcast loop gets a null
check on `online.Session?.Network?.EnqueueSend(...)`, with bot
allegiance members receiving via `OnTellReceived`-equivalent shim.

## Options considered

### Option A — Reuse internal broadcast primitives directly from `BotCreature`; add a guid-shim for inbound /tell

- Pros:
  - Outbound chat uses the exact same packet shape as players, so
    clients render it identically (font, color, prefix).
  - No `Session` coupling on the bot side.
  - Inbound shim is a single `if` in `GameActionTell.Handle`, easy to
    review and easy to upstream as "support non-session chat
    targets".
  - Aligns with [`0002-minimal-fork-bar.md`](0002-minimal-fork-bar.md).
- Cons:
  - Any future chat behavior added to `Player.HandleActionTalk` does
    not auto-apply to bots. We track upstream and replicate by hand.

### Option B — Make `Player` chat methods session-optional; have `BotPlayer` call them

- Pros:
  - Single chat code path for bots and humans.
- Cons:
  - Requires `BotPlayer` (rejected by
    [`0003-botcreature-not-botplayer.md`](0003-botcreature-not-botplayer.md)).
  - Requires guarding `Session?.Network?.EnqueueSend(...)` at every
    chat site in Player — Q3 found these are pervasive.

### Option C — Custom packet emit from `BotCreature` (build our own GameMessage)

- Pros:
  - Total control over wire shape.
- Cons:
  - Diverges from upstream — clients may render bot speech
    differently from player speech.
  - Defeats the point of reusing ACE infrastructure.

## Consequences

- **Easier:**
  - Bots produce wire-identical chat to humans — clients can't
    distinguish on packet shape (only on Guid range or `is_Bot`
    column, if exposed).
  - No `Session` plumbing for chat.
  - Inbound /tell shim is one tiny change in one file.
- **Harder:**
  - The team must remember to replicate any new chat behavior added
    to `Player.HandleActionTalk` into `BotCreature.Say`. We add a
    "see ADR-0006" comment on `Player.HandleActionTalk` in the fork
    to remind future contributors.
  - Allegiance and other Player-only chat channels will need similar
    shims when bots eventually use them (M5+).
- **Follow-ups:**
  - In M1, implement `BotCreature.Say` + scripted archetype lines.
  - In M2, add the `GameActionTell.Handle` guid shim for inbound
    /tell. Stub `BotBrain.OnTellReceived` to log only.
  - In M5, add `BotCreature.SendTellTo` for outbound /tell.
  - Document the upstream-replication rule for `Player` chat changes
    in the fork's `CONTRIBUTING.md` once the fork repo exists.

## References

- Related doc(s):
  - [`../research/ace-investigation.md`](../research/ace-investigation.md) (Q3)
  - [`../ace-fork-plan.md`](../ace-fork-plan.md)
  - [`../brain-providers.md`](../brain-providers.md) (Social layer)
  - [`0003-botcreature-not-botplayer.md`](0003-botcreature-not-botplayer.md)
- Related open question(s):
  - "Chat hook strategy" item in
    [`../../roadmap/m0-checklist.md`](../../roadmap/m0-checklist.md)
  - Resolved by this ADR
- Related issue(s) / PR(s):
  - [#3](https://github.com/darinh/ac-ai-players/issues/3) — Q3 research issue
