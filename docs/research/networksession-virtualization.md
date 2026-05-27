# NetworkSession virtualization audit (E1)

- **Status:** Audit complete
- **Date:** 2026-05-27
- **Author:** Anvil (Copilot CLI)
- **Issue:** [#29](https://github.com/darinh/ac-ai-players/issues/29) (E1)
- **Parent epic:** [#27](https://github.com/darinh/ac-ai-players/issues/27)
- **Drives ADR-0007 implementation work** (E2 below)

## TL;DR

To make a `BotNetworkSession : NetworkSession` work as
[ADR-0007](../adr/0007-bots-as-player-not-creature.md) requires, the
fork must mark exactly **6** methods on `NetworkSession` as
`virtual` (currently all non-virtual `public void`), plus mark **2**
methods on `Session` as `virtual` to give `BotSession` clean
override points for the shutdown-hang concern from ADR-0007.

The 919 `Session.Network.EnqueueSend(...)` call sites in
`Player_*.cs` partials all fall into one of two buckets:
**(a)** outbound-to-client message that is safe to drop for a bot —
~99% of sites — handled by a no-op override; or
**(b)** outbound-to-client message that is also a logical inbound
event for the receiving player — only **2** call sites exist and
they are both `EnqueueSend(GameEventTell)` outside Player partials.
Those 2 sites are reshaped via a one-`if` shim, not via the
NetworkSession layer.

## Audit method

Investigation against `darinh/ACE-bots` branch `botplayer-spike`,
SHA captured in the Anvil ledger row for task
`e1-networksession-audit` phase `after`.

1. Enumerated `^public\s+void\s+\w+\(` definitions in
   `Source/ACE.Server/Network/NetworkSession.cs`.
2. Enumerated `Session\.LogOffPlayer|\.Terminate|SendCharacterError`
   call sites across the server.
3. Grepped all `Session\.Network\.EnqueueSend\(new (Game\w+)` in
   `Source/ACE.Server/WorldObjects/Player*.cs`, bucketed by
   GameMessage / GameEvent type prefix, and inspected the top 40
   types by frequency (covered 95 % of call sites).
4. Verified all `*.Session.Network.EnqueueSend(new GameEventTell(`
   sites server-wide to find inbound-tell paths the bot brain
   must observe.
5. Verified all `*.Network.Update|ProcessPacket|ReleaseResources`
   external call sites to confirm the no-op override choice does
   not break the bot's tick path.

## 1. `NetworkSession` virtualization plan

All in
[`Source/ACE.Server/Network/NetworkSession.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/NetworkSession.cs).
The diff is purely additive — change `public void` to `public
virtual void`. No body changes. Six lines touched.

| Line | Signature | Virtualize? | `BotNetworkSession` override | Why |
|------|-----------|:---:|------------------------------|-----|
| 117 | `public void EnqueueSend(params GameMessage[] messages)` | **yes** | no-op | The 919 Player-partial call sites are all outbound-to-client UI updates / chat echoes / error popups / property syncs. The bot has no client to deliver them to. |
| 141 | `public void EnqueueSend(IEnumerable<GameMessage> messages)` | **yes** | no-op | Same as 117. |
| 165 | `public void EnqueueSend(params ServerPacket[] packets)` | **yes** | no-op | Raw `ServerPacket` (not `GameMessage`) — login flow, low-level UDP. Bot has no UDP connection. |
| 182 | `public void Update()` | **yes** | no-op | Called once per world tick from [`NetworkManager.cs:192`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/NetworkManager.cs#L192) — drains queued packets to UDP. For bots this drain is meaningless. (Critical: a no-throw no-op here is the difference between the bot existing and crashing the network thread every tick.) |
| 269 | `public void ProcessPacket(ClientPacket packet)` | **yes** | no-op | Inbound UDP from the client. Bot has no client, never receives packets. |
| 958 | `public void ReleaseResources()` | **yes** | no-op | Releases UDP buffers + connection tracking. Bot has no resources to release. |

**Upstream-justification language for the diff (per
[ADR-0002](../adr/0002-minimal-fork-bar.md)):** "Mark the
client-facing send / process / lifecycle methods on `NetworkSession`
as `virtual` to enable test fakes and headless / AI session
implementations. No behavior change; only the access level is
adjusted to allow subclassing." This is a defensible
extension-point change and meets the upstreamable-PR bar.

## 2. `Session` virtualization plan

All in
[`Source/ACE.Server/Network/Session.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Session.cs).
Two methods. Same additive `public void` → `public virtual void`
change. Necessary because of ADR-0007's shutdown-hang concern.

| Line | Signature | Virtualize? | `BotSession` override | Why |
|------|-----------|:---:|----------------------|-----|
| 231 | `public void LogOffPlayer(bool forceImmediate = false)` | **yes** | dispatch to `BotManager.LogOffBot(this.Player as BotPlayer)`, do not wait for client ack | Called from [`ServerManager.cs:139`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Managers/ServerManager.cs#L139) shutdown loop, [`AdminCommands.cs:3565,4079`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Command/Handlers/AdminCommands.cs#L3565), [`CharacterCommands.cs:72`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Command/Handlers/CharacterCommands.cs#L72), [`CharacterHandler.cs:269`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Handlers/CharacterHandler.cs#L269), [`NetworkSession.cs:629`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/NetworkSession.cs#L629), and [`Player_Tick.cs:138`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/WorldObjects/Player_Tick.cs#L138). The base impl waits for a final packet ack — bot will never ack, server hangs. |
| 281 | `public void Terminate(SessionTerminationReason reason, ...)` | **yes** | dispatch to `BotManager.LogOffBot(...)` immediately, no message send | Called from [`Player_Tick.cs:45,59`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/WorldObjects/Player_Tick.cs#L45) on save failure. Base sends `GameMessageCharacterError` then terminates the UDP connection. |

**Upstream-justification language:** "Mark `LogOffPlayer` and
`Terminate` on `Session` as `virtual` to enable AI / headless
session subclasses to control their own teardown. No behavior
change for existing subclasses." Defensible.

**Not needed to virtualize** (intentional):

- `Session.InitSessionForWorldLogin` (line 212) — `BotSession` does
  not flow through the network login path; it is constructed
  directly by `BotPlayer` lifecycle code. Never called.
- `Session.SendCharacterError` (line 337) — only called from
  `Player_Tick.cs:45,59` save-failed branches, both via
  `Session.Terminate(...)` first. The `Terminate` override above
  short-circuits before this is reached.

## 3. The 919 `EnqueueSend(...)` call sites — bucketed

Top 15 message types in `Source/ACE.Server/WorldObjects/Player*.cs`,
covering ~94 % of the 919 sites:

| Count | Type | Bucket | Reason |
|------:|------|--------|--------|
| 239 | `GameMessageSystemChat` | **drop** | Client UI text the bot has no UI to display. |
| 176 | `GameEventInventoryServerSaveFailed` | **drop** | Client error popup. |
| 164 | `GameEventWeenieError` | **drop** | Client error popup. |
| 104 | `GameEventCommunicationTransientString` | **drop** | Client transient text overlay. |
| 28 | `GameMessagePrivateUpdatePropertyInt` | **drop** | Client property-cache sync. The server already has the canonical value on the `Player` object the bot inherits from. |
| 17 | `GameEventWeenieErrorWithString` | **drop** | Client error popup with substitution. |
| 15 | `GameMessageSetStackSize` | **drop** | Client inventory render. |
| 10 | `GameMessageCreateObject` | **drop** | Client visibility / spawn render. |
| 7 | `GameEventTell` | **drop** | **All 7 sites are outbound tells the bot itself is sending** (e.g. IOU vendor flavor in `Player_Inventory.cs:3487-3552`). These are bot-as-author, not bot-as-receiver. Drop. (The inbound-tell case is §4 below.) |
| 6 | `GameMessageDeleteObject` | **drop** | Client visibility update. |
| 6 | `GameMessageSound` | **drop** | Client sound effect. |
| 4 | `GameEventItemServerSaysContainId` | **drop** | Client inventory sync. |
| 4 | `GameEventIdentifyObjectResponse` | **drop** | Client appraisal panel. |
| 4 | `GameMessagePrivateUpdateDataID` | **drop** | Client property-cache sync. |
| 4 | `GameMessagePrivateUpdateAttribute` | **drop** | Client property-cache sync. |

The remaining ~6 % long tail (`GameEventTradeFailure`,
`GameEventFriendsListUpdate`, `GameEventViewContents`,
`GameEventAttackDone`, `GameEventCombatCommenceAttack`,
`GameEventMagicUpdateEnchantment`, `GameEventFellowshipFullUpdate`,
`GameMessagePrivateUpdate*`, `GameMessageScript`,
`GameMessagePlayerTeleport`, `GameMessageAutonomousPosition`,
`GameMessageObjDescEvent`, ...) is the same shape: every one is a
client-facing UI / state sync. All bucket: **drop**.

**Conclusion: a single no-op override of each `EnqueueSend`
overload handles all 919 Player-partial sites.** The bot brain does
not need to inspect outbound messages.

## 4. Inbound-tell paths (the only "must dispatch to brain" sites)

Verified by `git grep -nE 'Session\.Network\.EnqueueSend\(new
GameEventTell\(' Source/ACE.Server` — server-wide, not just Player
partials. Two unique inbound-tell sites exist (one is the player
typing `/tell BotName ...`, the other is an NPC speaking via
emote):

| File | Line | Caller | Bot involvement | Fix |
|------|------|--------|-----------------|-----|
| `Source/ACE.Server/Network/GameAction/Actions/GameActionTell.cs` | 53 | `GameActionTell.Handle` (a human types `/tell BotName ...`) | `targetPlayer` may be a `BotPlayer` | One-`if` shim in `GameActionTell.Handle`: after the `targetPlayer != null` and squelch checks pass, if `targetPlayer is BotPlayer bot`, call `bot.OnReceivedTell(session.Player, message)` **then** `targetPlayer.Session.Network.EnqueueSend(tell)` (the latter is the no-op for bots; harmless to still call so non-bot paths are unchanged). |
| `Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs` | 1376 | NPC's `Tell` emote (`player.Session.Network.EnqueueSend(new GameEventTell(WorldObject, message, player, ChatMessageType.Tell))`) | `player` may be a `BotPlayer` | Same one-`if` shim: `if (player is BotPlayer bot) bot.OnReceivedTell(WorldObject, message);`. |

**Both shims live in the ACE-bots fork as small additive diffs**,
flagged with `// bot-system: see ADR-0007 finding #4`. They are
the dispatch half of the `/tell` integration retained per ADR-0007.
The lookup half (originally a guid → bot map in ADR-0006) is now
free: `PlayerManager.GetOnlinePlayer(target)` finds the bot once
the lifecycle in E4 lands it in `onlinePlayers`.

> Equivalent extension for `EmoteManager.NPC speech` (i.e. non-Tell
> emotes the bot's brain might also want to hear — yells,
> `GameMessageHearSpeech` broadcasts) is **deferred**. If the bot
> brain ever needs to react to ambient NPC chatter we can add
> equivalent shims at the same point. Out of scope for E1.

## 5. The "hidden" call sites: callers of `session.Network.X` from outside `Player_*.cs`

Three lookups verified clear by the virtualization plan:

- **`NetworkManager.Update(...)` calls `session.Network.Update()`**
  ([`NetworkManager.cs:192`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/NetworkManager.cs#L192))
  every tick on every session. Bot's `BotNetworkSession.Update` is
  a no-op. **Question:** does `BotSession` need to be enumerated by
  `NetworkManager.GetAuthenticatedSessions` at all? If not, this
  call never happens. If yes (e.g. for `pop` / `listplayers`
  visibility), the no-op is needed. **Decision for E2:** keep
  `BotSession` out of `NetworkManager`'s session table; bot
  visibility comes from `PlayerManager.onlinePlayers` (per E4).
  The no-op `Update` override is a defense-in-depth safety net
  if a stray reference does surface.
- **`AdminCommands` / `CharacterCommands` / `CharacterHandler` /
  `ServerManager` / `Player_Tick` call `Session.LogOffPlayer(...)`**
  (6 sites). Bot's `BotSession.LogOffPlayer` override dispatches
  to `BotManager.LogOffBot(...)`.
- **`Player_Tick.cs:45,59` call `Session.Terminate(...)`** on
  save-failure. Bot's `BotSession.Terminate` override dispatches
  to `BotManager.LogOffBot(...)` and logs the failure. No client
  packet sent.

## 6. The unavoidable: `Player_Tick` accesses `Session.*` directly

Per `Player_Tick.cs:43` and `:57`, the save-failed branches read
`Session.Player.Name` and `Account.AccountName`. For a `BotSession`
these must return sensible values:

- `Session.Player` — set by `Session` constructor; `BotSession`
  constructs with the `BotPlayer` reference. ✓
- `Session.Account` — currently set via login. `BotSession` must
  populate it with a fork-defined `bot-system` account row at
  construction. E3 (DB migration) creates this account row.

The age-update branch at `Player_Tick.cs:81` calls
`Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt
(this, PropertyInt.Age, Age ?? 1))`. This is a no-op for bots via
the virtualized `EnqueueSend`. ✓

The fellowship-vital branch at line 86 calls `Fellowship.OnVitalUpdate(this)`.
Bots may join fellowships in M3+; not a concern for E1.

The house-rent branch at line 90+ checks `House != null`. Bots
do not own houses; this branch is dormant. ✓

## 7. Required diff summary (for E2)

```
Source/ACE.Server/Network/NetworkSession.cs:
  Line 117: public void EnqueueSend(params GameMessage[] messages)
         -> public virtual void EnqueueSend(params GameMessage[] messages)
  Line 141: public void EnqueueSend(IEnumerable<GameMessage> messages)
         -> public virtual void EnqueueSend(IEnumerable<GameMessage> messages)
  Line 165: public void EnqueueSend(params ServerPacket[] packets)
         -> public virtual void EnqueueSend(params ServerPacket[] packets)
  Line 182: public void Update()
         -> public virtual void Update()
  Line 269: public void ProcessPacket(ClientPacket packet)
         -> public virtual void ProcessPacket(ClientPacket packet)
  Line 958: public void ReleaseResources()
         -> public virtual void ReleaseResources()

Source/ACE.Server/Network/Session.cs:
  Line 231: public void LogOffPlayer(bool forceImmediate = false)
         -> public virtual void LogOffPlayer(bool forceImmediate = false)
  Line 281: public void Terminate(SessionTerminationReason reason, ...)
         -> public virtual void Terminate(SessionTerminationReason reason, ...)
```

8 lines of access-level change. No body changes. Fully reversible.
Upstream-justifiable.

Plus the two one-`if` shims:

```
Source/ACE.Server/Network/GameAction/Actions/GameActionTell.cs:
  After line 51 (before the EnqueueSend), insert:
    // bot-system: see ADR-0007 finding #4
    if (targetPlayer is BotPlayer botTell)
        botTell.OnReceivedTell(session.Player, message);

Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs:
  Before line 1376, insert:
    // bot-system: see ADR-0007 finding #4
    if (player is BotPlayer botEmote)
        botEmote.OnReceivedTell(WorldObject, message);
```

## 8. Open items deferred from this audit

- **`Session.LogOffPlayer1` / `LogOffPlayer2`** — there are two
  helper methods at `Session.cs:251,261` invoked by
  `LogOffPlayer`. They are `private`, so the public override of
  `LogOffPlayer` short-circuits them. No virtualization needed.
  (Verified by re-reading `Session.cs:231-275`.)
- **`NetworkSession.HandleRequestRetransmission`** and other
  inbound-only protocol methods — bot never receives, never
  needed. Not virtualizing.
- **Inbound non-Tell speech** (yells, area chat) — bots receive
  via `Creature.HandleSpeech`-style paths that are not
  `EnqueueSend`-mediated; out of scope for E1. Will be revisited
  during E7.

## 9. References

- [ADR-0007](../adr/0007-bots-as-player-not-creature.md) — drives this audit
- [ADR-0008](../adr/0008-bot-tick-via-player-tick.md) — companion (tick mechanism)
- [Issue #29](https://github.com/darinh/ac-ai-players/issues/29) — E1 (this audit)
- [Issue #30](https://github.com/darinh/ac-ai-players/issues/30) — E2 (will implement the diff above)
- [Issue #35](https://github.com/darinh/ac-ai-players/issues/35) — E7 (retains the inbound-tell shim per §4)
