# 06 — Game messages

**Status**: Phase 2.3 PASS. The three messages the server pushes
between the handshake and the `EnterWorld` request are fully decoded
and documented below. Each schema in this file is verified against
both the ACE source and against captured wire bytes — see
[`phase2-results.md`](../phase2-results.md) for the captures and
decoded output.

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
| `0xF7C8` | `CharacterEnterWorldRequest` | C → S | (TBD Phase 3) | Client asks to enter world with a chosen character GUID + name |
| `0xF657` | `CharacterEnterWorld` | S → C | (TBD Phase 3) | Server confirms entry and starts world stream |
| `0xF656` | `CharacterCreate` | C → S | (TBD Phase 3+) | Create new character (request side) |
| `0xF643` | `CharacterCreateResponse` | S → C | (TBD) | Outcome of `CharacterCreate` |
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

These are written up in `spec/07-outbound-packet.md` once that file
exists (TODO Phase 3).
