# 06 — Game messages

**Status**: Phase 4 complete (all post-EnterWorld opcodes the spike
observes are decoded). The handshake messages (Phase 2.3), the
character-management round-trip (Phase 3.2 + 3.3), and the
world-state firehose opcodes (Phase 4.1 through 4.10) are all
documented below. Each schema in this file is verified against both
the ACE source and against captured wire bytes — see
[`phase2-results.md`](../phase2-results.md), [`phase3-results.md`](../phase3-results.md),
and [`phase4-results.md`](../phase4-results.md) for the captures
and decoded output.

This file replaces the earlier opcode table that contained fabricated
hex values; every opcode in the table below is now cited from
[`GameMessageOpcode.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/GameMessageOpcode.cs).

## Concept

Game messages are the application-layer payload that flows inside
packets with the `BlobFragments` flag set in `PacketHeader.Flags`.
The handshake (legs 1-3) does not use game messages; the first
packets with `BlobFragments` set arrive **after** the client sends
`ConnectResponse` (leg 3).

## Packet → fragments → game messages

One UDP packet can carry one or more **fragments**. Each fragment
carries part (or all) of one **game message**. Large messages (e.g.
a fully populated `CharacterList`) are split across multiple
fragments, which may span multiple packets.

```
UDP datagram
  PacketHeader (20 B)
  HeaderOptional (variable, only if non-Flags-zero flags set)
  Body:
    Fragment[0]:
      FragmentHeader (16 B)
      Fragment payload bytes (game-message data, possibly partial)
    Fragment[1]:
      FragmentHeader (16 B)
      Fragment payload bytes
    ...
```

The server-side encoder is
[`ServerPacket.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ServerPacket.cs)
+
[`ServerPacketFragment.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ServerPacketFragment.cs).
The client-side decoder we mirror is
[`ClientPacket.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ClientPacket.cs)
+
[`ClientPacketFragment.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ClientPacketFragment.cs).

## FragmentHeader (verified)

16 bytes, all little-endian. Source:
[`PacketFragmentHeader.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketFragmentHeader.cs).

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `Sequence` | Per-stream fragment sequence number (independent of packet sequence) |
| 4 | `u32` | `Id` | Message id this fragment belongs to. Server-originated message ids have the high bit set (`>= 0x80000000`); client-originated ids start at `0x00000001`. |
| 8 | `u16` | `Count` | Total fragment count for this message (1 if this message fits in one fragment) |
| 10 | `u16` | `Size` | **Total fragment size INCLUDING this 16-byte header**. Subtract 16 to get payload length. |
| 12 | `u16` | `Index` | This fragment's index within the message (0-based) |
| 14 | `u16` | `Queue` | Server-side ordering bucket. See `GameMessageGroup` below. |

> ⚠ `Size` is the on-wire fragment size, not the game-message payload
> length. Real captures: a `BlobFragments` packet `Size=44` means a
> 28-byte game-message payload (44 - 16). Confirmed in
> [`ServerPacketFragment.PackAndReturnHash32`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ServerPacketFragment.cs)
> which sets `Header.Size = HeaderSize + Data.Length`.

### GameMessageGroup → Queue values

Source:
[`GameMessageGroup.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessageGroup.cs).

| Value | Group | Notes |
|---|---|---|
| `0x00` | `InvalidQueue` | (reserved) |
| `0x01` | `EventQueue` | Event-broadcast game messages |
| `0x02` | `ControlQueue` | Session/connection control |
| `0x03` | `WeenieQueue` | Per-weenie (game-object) updates |
| `0x04` | `LoginQueue` | Login-flow messages (pre-EnterWorld) |
| `0x05` | `DatabaseQueue` | DDDInterrogation and other DB-tier responses |
| `0x06` | `SecureControlQueue` | Secure control variant |
| `0x07` | `SecureWeenieQueue` | Autonomous-position updates |
| `0x08` | `SecureLoginQueue` | Secure login variant |
| `0x09` | `UIQueue` | UI-bound: CharacterList, ServerName, login chrome |
| `0x0A` | `SmartboxQueue` | Smartbox subsystem |
| `0x0B` | `ObserverQueue` | Observer/spectator stream |
| `0x0C` | `QueueMax` | sentinel |

The client doesn't have to honour the bucket — the receiver just
needs to ack the packet sequence and feed each fragment to the
game-message reassembler. We only log `Queue` for debugging.

## Game-message envelope

Each game message starts with a `u32 opcode` (little-endian). The
opcode determines how to parse the rest of the message's bytes.

> ⚠ The opcode is `u32`, not `u16`. ACE writes it as
> `writer.Write((uint)Opcode)` in
> [`GameMessage.cs:26`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/GameMessage.cs#L26).
> Earlier revisions of this doc claimed `u16`; that was wrong.

### Verified opcodes (Phase 2)

All opcodes below were verified by searching
[`GameMessageOpcode.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/GameMessageOpcode.cs)
directly. Note in particular that `CharacterEnterWorldRequest`
(C → S, opcode `0xF7C8`) and `CharacterEnterWorld` (S → C, opcode
`0xF657`) are NOT the same message — the request is what the client
sends, the bare name is the server's "you're in" confirmation.

| Opcode | Name | Dir | Group | Notes |
|---|---|---|---|---|
| `0xF658` | `CharacterList` | S → C | `UIQueue` (9) | Character roster + account name + slot count |
| `0xF7E1` | `ServerName` | S → C | `UIQueue` (9) | Server display name + connection counts |
| `0xF7E5` | `DDD_Interrogation` | S → C | `DatabaseQueue` (5) | Triggers client to declare its data versions |
| `0xF7C8` | `CharacterEnterWorldRequest` | C → S | `UIQueue` (9) | "May I enter?" probe; opcode-only payload; verified Phase 3.3 |
| `0xF7DF` | `CharacterEnterWorldServerReady` | S → C | `UIQueue` (9) | Server "yes, commit now"; opcode-only payload; verified Phase 3.3 |
| `0xF657` | `CharacterEnterWorld` | C → S | `UIQueue` (9) | Client commits to a character GUID + account; verified Phase 3.3 |
| `0xF659` | `CharacterError` | S → C | `UIQueue` (9) | Failure ack for character-mgmt ops; `u32` error code; verified Phase 3.3 |
| `0xF656` | `CharacterCreate` | C → S | `UIQueue` (9) | Create new character (request side); verified Phase 3.2 |
| `0xF643` | `CharacterCreateResponse` | S → C | `UIQueue` (9) | Outcome of `CharacterCreate`; conditional body, verified Phase 3.2 |
| `0xF655` | `CharacterDelete` | C → S | (TBD) | Mark character for deletion |
| `0xF653` | `CharacterLogOff` | C → S | (TBD) | Exit to character-select |

The full enumeration is in
[`GameMessageOpcode.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/GameMessageOpcode.cs).
There are ~600 entries total — most are pre-Phase-3 noise from the
spike's standpoint.

## `WriteString16L` / `ReadString16L`

ACE's short-string encoding, used in every message below.

Wire layout:

```
u16 length            (LE, byte count of the body — NOT a code-point count)
body[length] bytes    (Windows-1252 / Latin-1)
pad[k] zero bytes     (k chosen so that (2 + length + k) % 4 == 0)
```

The Windows-1252 fallback is intentional — pre-Unicode ACE.

Source: `Extensions.WriteString16L` and `CalculatePadMultiple` in
[`Extensions.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Entity/Extensions.cs).
The spike's reader is `AcStrings.ReadString16L`.

## Per-message wire formats (verified)

All offsets in the tables below are byte offsets from the start of
the **game-message payload** (i.e. relative to the opcode, not the
fragment header).

### `0xF7E5` `GameMessageDDDInterrogation` (S → C)

Source:
[`GameMessageDDDInterrogation.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageDDDInterrogation.cs).

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF7E5` |
| 4 | `u32` | `ServersRegion` | Always `1` on the captured ACE server |
| 8 | `u32` | `NameRuleLanguage` | Always `1` (English) on the captured server |
| 12 | `u32` | `ProductId` | Always `1` on the captured server |
| 16 | `u32` | `SupportedLanguagesCount` | List length; observed `2` |
| 20 | `u32 × count` | `SupportedLanguages[]` | Observed `[0, 1]` = `[Invalid, English]` |

Captured payload (28 bytes total, from `phase2-charlist-run-01.log`
fragment Q=5):

```
e5 f7 00 00  01 00 00 00  01 00 00 00  01 00 00 00
02 00 00 00  00 00 00 00  01 00 00 00
```

### `0xF658` `GameMessageCharacterList` (S → C)

Source:
[`GameMessageCharacterList.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterList.cs).

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF658` |
| 4 | `u32` | `unknown_zero_1` | Always `0`; purpose unconfirmed |
| 8 | `u32` | `characters.Count` | Number of `CharacterEntry` records that follow |
| 12 | variable | `CharacterEntry[]` | See sub-schema below; only present when `Count > 0` |
| ? | `u32` | `unknown_zero_2` | Always `0`; purpose unconfirmed |
| ? | `u32` | `slotCount` | Account-level `max_chars_per_account` (default 11) |
| ? | `String16L` | `accountName` | The account name, **not** a character name |
| ? | `u32` | `useTurbineChat` | Boolean, `1` = enabled |
| ? | `u32` | `hasThroneOfDestiny` | Boolean, always `1` on ACE (the expansion is always on) |

#### `CharacterEntry` sub-schema

| Offset | Size | Field | Notes |
|---|---|---|---|
| +0 | `u32` | `GUID` | The full character GUID. **This is the value Phase 3 sends in `CharacterEnterWorldRequest`.** |
| +4 | `String16L` | `Name` | Character name. May have a leading `"+"` if the character is "plussed" (admin-flagged). |
| +(after name+pad) | `u32` | `secondsToDelete` | `0` unless the character is in delete-cooldown |

Captured payload (44 bytes, zero characters, account `headless-test`):

```
58 f6 00 00            # opcode 0xF658
00 00 00 00            # unknown_zero_1 = 0
00 00 00 00            # characters.Count = 0
00 00 00 00            # unknown_zero_2 = 0
0b 00 00 00            # slotCount = 11
0d 00                  # String16L length = 13
68 65 61 64 6c 65 73 73 2d 74 65 73 74    # "headless-test"
00                     # 1 byte zero pad (2 + 13 = 15 → +1 = 16)
xx xx xx xx            # useTurbineChat
01 00 00 00            # hasThroneOfDestiny = 1
```

> ⚠ Zero-character lists are normal for fresh accounts. The
> `slotCount` field is a per-account maximum, not the character
> count.

### `0xF7E1` `GameMessageServerName` (S → C)

Source:
[`GameMessageServerName.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageServerName.cs).

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF7E1` |
| 4 | `u32` | `currentConnections` | Live session count at send time |
| 8 | `u32` | `maxConnections` | Server config max |
| 12 | `String16L` | `serverName` | Display name (observed: `"ACEmulator"`) |

Captured payload (24 bytes):

```
e1 f7 00 00            # opcode 0xF7E1
01 00 00 00            # currentConnections = 1
80 00 00 00            # maxConnections = 128
0a 00                  # String16L length = 10
41 43 45 6d 75 6c 61 74 6f 72             # "ACEmulator"
# no pad needed: 2 + 10 = 12, already a 4-multiple
```

### `0xF656` `GameMessageCharacterCreate` (C → S)

Sources:
[`CharacterHandler.cs:26-44`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Handlers/CharacterHandler.cs#L26-L44)
(opcode dispatch and account-string gate) +
[`CharacterCreateInfo.cs:37-68`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Entity/CharacterCreateInfo.cs#L37-L68)
(payload structure) +
[`Appearance.cs:28-50`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Entity/Appearance.cs#L28-L50)
(the appearance sub-record).

Queue: `UIQueue` (9). Reliability: implicitly reliable (server
calls `Reliable*` on the response).

**Critical**: the very first field is the session's account name.
If it does not exactly match `session.Account`, the handler
**silently returns** with no error packet at all. This is the
single most opaque failure mode for this message.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF656` |
| 4 | `String16L` | `account` | MUST equal `session.Account`. Drawn from the inbound `CharacterList.account`. |
| varies | `u32` | `unknown` | Source comment: "this is a u32 we don't use". Server reads and discards. Write `1`. |
| +4 | `u32` | `heritage` | `HeritageGroup` enum. Aluvian=1, Gharu'ndim=2, Sho=3, Viamontian=4, Empyrean=5, Umbraen=6, Penumbraen=7, Lugian=8, Tumerok=9, Olthoi=10, OlthoiAcid=11. Must index into `CharGen.HeritageGroups`. |
| +4 | `u32` | `gender` | 0=Female, 1=Male. Must index into `heritageGroup.Genders`. |
| +4 | `Appearance` | `appearance` | See sub-schema below (104 bytes). |
| +104 | `i32` | `templateOption` | Index into `heritageGroup.Templates`. 0 = first template. |
| +4 | `u32 × 6` | `attributes` | Strength, Endurance, Coordination, Quickness, Focus, Self. Each ∈ [10, 100]. Sum ≤ `heritage.AttributeCredits` (Aluvian = 290). |
| +24 | `u32` | `characterSlot` | Display slot index (0-10). |
| +4 | `u32` | `classId` | Custom class id; 0 is safe. |
| +4 | `u32` | `numOfSkills` | **MUST be 55 exactly.** Wrong count → `ClientServerSkillsMismatch` → session terminated via `GameMessageBootAccount`. Hard runtime guard required (Debug.Assert is not enough). |
| +4 | `u32 × 55` | `skillAdvancementClass` | Per-skill `SkillAdvancementClass`: Inactive=0, Untrained=1, Trained=2, Specialized=3. All-Inactive (zeros) is the safe default. |
| +220 | `String16L` | `name` | Character name. Checked against the taboo table (`NameBanned`) and existing-character index (`NameInUse`). |
| varies | `u32` | `startArea` | Index into `CharGen.StarterAreas`. 0 = first area. |
| +4 | `u32` | `isAdmin` | Boolean. Must be 0 unless the account is flagged admin. |
| +4 | `u32` | `isSentinel` | Boolean. Must be 0 unless the account is flagged sentinel. |

#### `Appearance` sub-schema (104 bytes, all little-endian)

| Offset | Size | Field |
|---|---|---|
| 0 | `u32` | `Eyes` (texture index) |
| 4 | `u32` | `Nose` |
| 8 | `u32` | `Mouth` |
| 12 | `u32` | `EyeColor` (palette) |
| 16 | `u32` | `HairColor` |
| 20 | `u32` | `HairStyle` |
| 24 | `u32` | `HairHue` |
| 28 | `u32` | `SkinHue` |
| 32 | `u32` | `HeadgearStyle` |
| 36 | `u32` | `HeadgearColor` |
| 40 | `u32` | `ShirtStyle` |
| 44 | `u32` | `ShirtColor` |
| 48 | `u32` | `PantsStyle` |
| 52 | `u32` | `PantsColor` |
| 56 | `f64` | `HeadgearHue` |
| 64 | `f64` | `ShirtHue` |
| 72 | `f64` | `PantsHue` |
| 80 | `f64` | `FootwearHue` |
| 88 | `f64` | `HeadgearHue2` |
| 96 | `f64` | `ShirtHue2` |

Empirically (Phase 3.2 PASS), all-zero Appearance is valid for an
Aluvian Male: `sex.GetEyeTexture(0)` and the rest of the indexed
DAT lookups succeed with index 0.

#### Validation rules (all silent unless flagged)

| Server check | Failure response |
|---|---|
| `session.Account != payloadAccount` | **silent return** — no response packet |
| Heritage index out of range | `Corrupt` (=5) |
| Gender index out of range | `Corrupt` |
| TemplateOption index out of range | `Corrupt` |
| Any attribute outside [10, 100] | `InvalidSkillRequested` (misleading name; this is the attribute path) |
| Sum of attributes > `heritage.AttributeCredits` | `TooManySkillCreditsUsed` |
| `numOfSkills != 55` | `ClientServerSkillsMismatch` → **session terminated** |
| Non-Inactive skill not in DAT `SkillBaseHash` | `InvalidSkillRequested` |
| Name in taboo table | `NameBanned` (=4) |
| Name already in use | `NameInUse` (=3) |
| Any other exception inside `PlayerFactory.Create` | caught + logged server-side; **no response packet** |

Verified Phase 3.2 payload (420 bytes, account `"headless-test"`,
name `"Headless01"`, all-10s attributes, all-Inactive skills):
see [`phase3-charcreate-run-01.log`](../phase3-charcreate-run-01.log)
for the captured wire bytes and server response.

### `0xF643` `GameMessageCharacterCreateResponse` (S → C)

Source:
[`GameMessageCharacterCreateResponse.cs:8-19`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterCreateResponse.cs#L8-L19).

Queue: `UIQueue` (9).

**Conditional body** — the optional fields are only present when
`response == Ok` (=1). A decoder that eagerly reads them on any
non-Ok response will overrun the fragment and corrupt downstream
parsing of batched messages.

| Offset | Size | Field | Always present? |
|---|---|---|---|
| 0 | `u32` | `opcode` | yes (`0xF643`) |
| 4 | `u32` | `response` | yes — `CharacterGenerationVerificationResponse` enum |
| 8 | `u32` | `guid` | **only if `response == Ok`** — character's `ObjectGuid.Full` |
| 12 | `String16L` | `name` | only if `response == Ok` |
| varies | `u32` | `trailingZero` | only if `response == Ok` — server always writes `0` |

`CharacterGenerationVerificationResponse` values:
| Code | Name | Meaning |
|---|---|---|
| 0 | Undef | Unused. |
| 1 | **Ok** | Character created; body fields populated. |
| 2 | Pending | Async check in progress (rare). |
| 3 | NameInUse | Name already taken. |
| 4 | NameBanned | Name in taboo table. |
| 5 | Corrupt | One of the validation gates above failed. |
| 6 | DatabaseDown | Shard DB unreachable. |
| 7 | AdminPrivilegeDenied | Requested admin/sentinel without account flag. |
| 8 | Count | Sentinel. |

Captured payload from Phase 3.2 (28 bytes, full fragment Size=44
including the 16-byte FragmentHeader):

```
43 f6 00 00                                # opcode 0xF643
01 00 00 00                                # response = Ok (1)
06 00 00 50                                # guid = 0x50000006
0a 00                                      # String16L length = 10
48 65 61 64 6c 65 73 73 30 31              # "Headless01"
00 00                                      # pad (2 + 10 = 12, no pad needed; these two bytes are part of trailing)
00 00 00 00                                # trailingZero
```

(Note the 2-byte alignment: `2 + 10 = 12` is already a 4-multiple,
so the String16L has zero pad bytes; the four `00` bytes that
follow are the `trailingZero` field, not padding.)

### `0xF7C8` `GameMessageCharacterEnterWorldRequest` (C → S)

Server read path:
[`CharacterHandler.cs:184-196`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Handlers/CharacterHandler.cs#L184-L196).

This is the "may I enter the world?" probe. The handler reads
NOTHING from the payload — it only checks
`ServerManager.ShutdownInProgress` and
`WorldManager.WorldStatus == Open`. On accept the server replies
with `0xF7DF CharacterEnterWorldServerReady` (also empty); on
shutdown it replies with `0xF659 CharacterError(LogonServerFull)`.

**Wire layout (4 bytes total):**

| Offset | Type | Field | Value |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF7C8` |

**Captured wire bytes** (`phase3-enterworld-run-01.log`):

```
c8 f7 00 00                                # opcode 0xF7C8 (no body)
```

The fragment header on this message is the same envelope used for
every other game message (Q=`UIQueue`=9, count=1, idx=0); the
fragment payload is exactly 4 bytes.

### `0xF7DF` `GameMessageCharacterEnterWorldServerReady` (S → C)

Server encode path:
[`GameMessageCharacterEnterWorldServerReady.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterEnterWorldServerReady.cs).
Constructor passes `GameMessageGroup.UIQueue` (9) and a size of 4
(opcode only).

**Wire layout (4 bytes total):**

| Offset | Type | Field | Value |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF7DF` |

The decoder returns an empty marker record
(`CharacterEnterWorldServerReadyMessage()`) so the state machine
can pattern-match on the type without parsing zero bytes.

### `0xF657` `GameMessageCharacterEnterWorld` (C → S)

Server read path:
[`CharacterHandler.cs:198-263`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Handlers/CharacterHandler.cs#L198-L263).
Reads `u32 guid` first, then `string16L account`.

**Wire layout (variable, 8 + string16L bytes):**

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF657` |
| 4 | `u32` | `characterGuid` | Must exist in `session.Characters` (sourced from `CharacterList`). |
| 8 | `string16L` | `account` | Must `==` `session.Account` (case-sensitive). Padded to next 4-multiple. |

**Validation gates** (server replies `CharacterError(code)` on
any failure, no per-character side effects):

| Failure | Code |
|---|---|
| `ServerManager.ShutdownInProgress` | `LogonServerFull = 0x0F` |
| `account != session.Account` | `EnterGameCharacterNotOwned = 0x06` |
| `characterGuid` not in `session.Characters` | `EnterGameCharacterNotOwned = 0x06` |
| Character marked deleted | `EnterGameCharacterNotOwned = 0x06` |
| Character already in world (other session) | `EnterGameCharacterInWorld = 0x07` |
| Offline player record missing | `EnterGameGeneric = 0x08` |
| Olthoi heritage but Olthoi-play disabled | `EnterGameCouldntPlaceCharacter = 0x09` |

On success: `session.State` → `WorldConnected`,
`WorldManager.PlayerEnterWorld` is invoked, and the per-tick
world-state firehose begins (no single "ok" message — entry is
confirmed implicitly by the stream starting).

**Captured wire bytes** for `guid=0x50000006` +
`account="headless-test"` (13 chars):

```
57 f6 00 00                                # opcode 0xF657
06 00 00 50                                # characterGuid = 0x50000006
0d 00                                      # String16L length = 13
68 65 61 64 6c 65 73 73 2d 74 65 73 74     # "headless-test"
00                                         # pad (2 + 13 = 15, +1 byte → 16)
```

Total payload: 24 bytes. Plus the 8-byte BlobFragments envelope +
16-byte FragmentHeader + 16-byte PacketHeader = 64 bytes on the
wire.

### `0xF659` `GameMessageCharacterError` (S → C)

Server encode path:
[`GameMessageCharacterError.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterError.cs).
Constructor passes `GameMessageGroup.UIQueue` (9) and a size of 8.

**Wire layout (8 bytes total):**

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF659` |
| 4 | `u32` | `errorCode` | `CharacterError` enum value |

**`CharacterError` enum** (from
[`CharacterError.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Enum/CharacterError.cs)
— spike decodes raw `u32` so unknown values are logged not dropped):

| Code | Name | Meaning |
|---|---|---|
| `0x01` | `Logon` | Two accounts logged on simultaneously |
| `0x03` | `AccountLogin` | Server cannot access account info |
| `0x04` | `ServerCrash1` | Server disconnected |
| `0x05` | `Logoff` | Server cannot log off character |
| `0x06` | `EnterGameCharacterNotOwned` | GUID/account mismatch on EnterWorld |
| `0x07` | `EnterGameCharacterInWorld` | Character already in world |
| `0x08` | `EnterGameGeneric` | Generic enter-world failure |
| `0x09` | `EnterGameCouldntPlaceCharacter` | Heritage disabled etc. |
| `0x0F` | `LogonServerFull` | World closed or shutting down |

(Full enum has more values for cross-server transfer, name policy
violations, etc. — not yet observed.)

### 0xF746 `PlayerCreate` (server → client)

Verified Phase 4.1, group SmartboxQueue (6).

Encoder: `Source/ACE.Server/Network/GameMessages/Messages/GameMessagePlayerCreate.cs`.

```
offset  size  field      notes
0       4     opcode     0xF746 little-endian
4       4     guid       u32; player's avatar ObjectGuid
```

Total: 8 bytes.

Semantics: one-shot. Arrives once per session, immediately after the
client commits `0xF657 CharacterEnterWorld`. Carries the GUID the
player is now embodying — must match the GUID the client sent in
the commit. Phase 4.1 capture (`phase4-decoders-run-01.log`):
`46 f7 00 00 06 00 00 50` → `PlayerCreate guid=0x50000006` against
the `Headless01` character.

Subsequent `0xF74C Motion` and `0xF745 ObjectCreate` messages
targeting this same GUID describe our avatar's animation state.

### 0xF7E0 `ServerMessage` (server → client)

Verified Phase 4.2, group UIQueue (9).

Encoder: `Source/ACE.Server/Network/GameMessages/Messages/GameMessageSystemChat.cs`.

```
offset      size            field             notes
0           4               opcode            0xF7E0 little-endian
4           2               textLength        u16; byte count for string16L body
6           textLength      text (Latin-1)    AC string16L body, 1 byte per char
6+n         pad             align-to-4        (2 + n) padded up to next mult of 4
...         4               chatMessageType   i32; channel id (see below)
```

Total: 8 + AlignTo4(2 + textLength).

string16L details: see [spec/05-data-types.md](05-data-types.md). The
prefix counts characters, not bytes; characters are written as
single 8-bit Latin-1 code units (NOT UTF-16). Padding zero-fills
the prefix+body block to a 4-byte multiple so the trailing i32
lands on alignment.

ChatMessageType wire values (from
`Source/ACE.Entity/Enum/ChatMessageType.cs`, verified — enum is
`uint`, sequential, NOT a bit-field):

| value | name | notes |
|---|---|---|
| `0x00` | `Broadcast` | Welcome banner, MOTD, default channel |
| `0x01` | `AllChannels` | Broadcast to all chat channels |
| `0x02` | `Speech` | Local /say |
| `0x03` | `Tell` | Incoming `/tell` |
| `0x04` | `OutgoingTell` | Echo of `/tell` sent by us |
| `0x05` | `System` | Server system notices |
| `0x06` | `Combat` | Combat log entries |
| `0x07` | `Magic` | Spell messages |
| `0x08` | `Channel` | Custom channels |
| `0x0C` | `Emote` | `/emote` text |
| `0x0F` | `Help` | `@help` output |
| `0x12` | `Allegiance` | Allegiance chat |
| `0x13` | `Fellowship` | Fellowship chat |
| `0x14` | `WorldBroadcast` | Server-wide announcements |

Phase 4.2 capture: `ServerMessage(chatType=0x0): "Welcome to
Asheron's Call..."` — Broadcast/MOTD on the login firehose. Decoded
from `phase4-decoders-run-01.log`.

### 0xF7B0 `GameEvent` envelope (server → client)

Verified Phase 4.5. Base class for all `GameEvent*` messages — every
event the server sends shares the same 16-byte envelope, then
appends an event-specific payload. The payload is NOT decoded at
this layer (see Phase 5 backlog); the spike preserves it as raw
bytes alongside the decoded envelope.

Encoder (base ctor):
[`GameEventMessage.cs:14-26`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameEvent/GameEventMessage.cs#L14-L26).

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF7B0` |
| 4 | `u32` | `receiverGuid` | `session.Player.Guid`, or `0` when logged out |
| 8 | `u32` | `serverEventSequence` | Auto-increment per server-side `GameEventSequence++`. Lets the client reorder out-of-order events. The spike logs it but does not enforce ordering. |
| 12 | `u32` | `eventType` | `GameEventType` enum value (see source listing) |
| 16 | `bytes[N]` | `payload` | Event-specific body, kept opaque |

`GameEventType` enum source:
[`GameEventType.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameEvent/GameEventType.cs).
Verbatim copy lives at
`experiments/headless-client/src/HeadlessAcClient/Protocol/GameMessages/GameEventType.cs`
— keep in sync if the server adds new event types or the decoder
will print "Unknown".

Captured login burst (`phase4-gameevent-run-01.log`):

```
PlayerDescription        (0x0013) seq=1  payload[668]
CharacterTitle           (0x0029) seq=2  payload[16]
FriendsListUpdate        (0x0021) seq=3  payload[8]
WeenieError              (0x028A) seq=4  payload[4]
WeenieErrorWithString    (0x028B) seq=5  payload[16]
SetTurbineChatChannels   (0x0295) seq=6  payload[40]
...
```

The 668-byte `PlayerDescription` carries the full character sheet
and is the obvious next sub-decoder target.

### 0x02CD `PrivateUpdatePropertyInt` (server → client)

Verified Phase 4.7. Server informs the client that an int-valued
property on a WorldObject changed (`CurrentHealth`, `Level`,
`Coinage`, `Age`, etc.). "Private" = only visible to the receiving
session, unlike the broadcast variant `0x019B PublicUpdatePropertyInt`.

Encoder:
[`GameMessagePrivateUpdatePropertyInt.cs:6-15`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdatePropertyInt.cs#L6-L15).

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0x02CD` |
| 4 | `u8` | `sequence` | `ByteSequence` (NextBytes returns a single byte). Per-property auto-increment for client-side ordering. |
| 5 | `u32` | `property` | `PropertyInt` enum value. Underlying server enum is `ushort` but the writer `Write((uint)property)` promotes to `u32` on the wire. |
| 9 | `i32` | `value` | The new property value |

Total 13 bytes, packed, no alignment padding. Group: `UIQueue`.

> **PROPERTY-FAMILY TRAP (CRITICAL)** — the 1-byte `sequence` is
> shared by the ENTIRE `PrivateUpdateProperty*` / `PublicUpdateProperty*`
> family. The naive "u32 sequence" assumption silently mis-decodes
> every message of this family. Confirmed sizes per message:
>
> | Opcode | Name | Size (bytes) |
> |---|---|---|
> | `0x02CC` | `PrivateUpdatePropertyBool` | 13 |
> | `0x02CD` | `PrivateUpdatePropertyInt` (this) | 13 |
> | `0x02CE` | `PrivateUpdatePropertyFloat` (f64 value) | 17 |
> | `0x02CF` | `PrivateUpdatePropertyInt64` (i64 value) | 17 |
> | `0x02D0` | `PrivateUpdatePropertyString` (variable; includes Align) | variable |
> | `0x019A` | `PublicUpdatePropertyBool` (+ u32 sender) | 17 |
> | `0x019B` | `PublicUpdatePropertyInt` (+ u32 sender) | 17 |
> | `0x019C` | `PublicUpdatePropertyFloat` (+ u32 sender) | 21 |
> | `0x019D` | `PublicUpdatePropertyInt64` (+ u32 sender) | 21 |
> | `0x019E` | `PublicUpdatePropertyString` (+ u32 sender, variable) | variable |
>
> Public variants insert a `u32 sender guid` AFTER the sequence for
> Int / Bool / Float / Int64; the String variant swaps the
> guid/property field order, so re-check the writer when those
> decoders land.

`PropertyInt` enum: full list (~660 entries) in
[`PropertyInt.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Entity/Enum/Properties/PropertyInt.cs).
The spike maintains a small hand-picked `KnownProperties` dictionary
for log readability; the decoded record stores the raw `u32` so
unknown properties survive round-trip.

Captured ticker (`phase4-state-speech-run-01.log`):

```
PrivateUpdatePropertyInt: Age = 3473 (seq=0)
PrivateUpdatePropertyInt: Age = 3480 (seq=1)
PrivateUpdatePropertyInt: Age = 3487 (seq=2)
... (one every ~7s, monotone increasing)
```

`Age` (`PropertyInt` value `125`) ticks once per server heartbeat
on the player.

### 0xF74B `SetState` (server → client)

Verified Phase 4.9. Broadcasts updated `PhysicsState` bitfield for a
WorldObject + the two sequences needed for ordering validation.

Encoder:
[`GameMessageSetState.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageSetState.cs).

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF74B` |
| 4 | `u32` | `guid` | `ObjectGuid.Full` of the object whose state changed |
| 8 | `u32` | `state` | `PhysicsState` `[Flags]` bitfield, kept as raw u32 in the spike |
| 12 | `u16` | `instanceSequence` | `UShortSequence: ObjectInstance` |
| 14 | `u16` | `stateSequence` | `UShortSequence: ObjectState` |

Total fixed 16 bytes, packed. `PhysicsState` enum source:
[`PhysicsState.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Entity/Enum/PhysicsState.cs).

Captured (`phase4-state-speech-run-01.log`):

```
SetState: guid=0x50000006 state=0x00400408 seq=(inst=23,state=1)
SetState: guid=0x7860202C state=0x0001001C seq=(inst=0,state=355)
SetState: guid=0x7860202D state=0x00010018 seq=(inst=0,state=327)
```

The first is our own avatar's physics state on EnterWorld; the
`0x7860202x` series is door / static-object state changes in the
academy lobby.

### 0x02BB `HearSpeech` (server → client)

Verified Phase 4.10. Chat text the player hears — both NPC dialogue
AND other players' (and bots') chat in /say range.

Encoder:
[`GameMessageHearSpeech.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageHearSpeech.cs).

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0x02BB` |
| 4 | `String16L` | `messageText` | The chat line. Padded to 4-multiple. |
| variable | `String16L` | `senderName` | Speaker's display name. Padded to 4-multiple. |
| variable | `u32` | `senderId` | Speaker's `ObjectGuid.Full` |
| +4 | `u32` | `chatMessageType` | `ChatMessageType` enum (see below) |

Sibling `0x02BC HearRangedSpeech` is structurally identical with an
extra `f32 range` field between `senderId` and `chatMessageType` —
not yet observed in the spike's firehose, so deferred.

Captured (`phase4-state-speech-run-01.log`):

```
HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "oh hey Headless01"
HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "lol nice hat {p}"
HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "anyone selling a good bow? {p}"
HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "where is the best xp around here?"
HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "i need a bag, anyone got one?"
HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "wow this place is huge"
```

`chatType=0x02` = `Speech` (local /say). Pilot-01 is broadcasting
scripted chat from a HeartBeat emote chain; the headless client
receiving and decoding that chat closes the first hop of the bot's
perception loop.

## Reliability and ordering

Server-side, every game message we observed in Phase 2 was sent on
the reliable path (`AddPacketTail` in `NetworkSession.cs` followed
the `Reliable*` branch). Reliability semantics are determined
*server-side* by the dispatch class on the send call, not by a flag
the client reads. From the client's perspective:

- **Inbound packets with `BlobFragments`** carry game messages and
  MUST be acked by their packet `Sequence`.
- **Inbound packets with `AckSequence`** are server reminders that
  it wants an ack from us. The reminder repeats until we ack.
- **Inbound packets with `TimeSync`** carry a `double` server clock.
  Echoing it back is enough to keep the session alive; the server
  ignores the content (see `phase2-results.md` learning #3).

## Fragment reassembly (Phase 3 concern)

In Phase 2 every game message we observed fit in one fragment
(`Count = 1`, `Index = 0`). Phase 3 will need fragment reassembly
when:

- The client sends a character creation payload that exceeds the
  MTU.
- The server sends a fully populated `CharacterList` with many
  characters (each entry can be ~30+ bytes).
- World state messages start flowing.

Reassembly key: `(Header.Id, Fragment.Id)` per direction. Each
arriving fragment slots into its `Index` position; the message is
complete once `Count` distinct indexes have arrived. Order of
arrival within a message is not guaranteed across packets, but is
guaranteed within one packet.

## Notes on outbound game messages (Phase 3 preview)

When the spike starts emitting game messages:

- Outbound message ids start at `0x00000001` and increment per
  message (the server's start at `0x80000000`).
- Outbound packets carrying game messages MUST have the
  `EncryptedChecksum` flag set, which forces a real XOR-key draw
  from `cryptoSend`. Bare-control packets (Phase 2's keepalive)
  could skip the cipher entirely; game-message packets cannot.
- Game-message packets MUST use `Sequence >= 1` (the `Sequence = 0`
  bare-control rule does not apply when `BlobFragments` is set;
  see `spec/02-network.md`).

These are written up in [`spec/08-outbound-packet.md`](08-outbound-packet.md)
(landed in Phase 3 — outbound packet framing, sequence rules, and
the encrypted-checksum chain).

## See also

- [`spec/07-world-state.md`](07-world-state.md) — verified schemas
  for the world-state opcodes that share this game-message envelope:
  `0xF745 ObjectCreate`, `0xF748 UpdatePosition`, `0xF74C Motion`
  (header). Those messages are factored into a separate file because
  their schemas dominate the world-state model.
