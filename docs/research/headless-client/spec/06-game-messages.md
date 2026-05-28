# 06 — Game messages

🚧 **Status: stub.** Phase 2 will fill in details as packets
are observed and parsed.

## Concept

Game messages are the application-layer payload that flows
inside packets with the `BlobFragments` flag set in
`PacketHeader.Flags`. The handshake (legs 1-3) does not use
game messages; the first thing the server sends after leg 3
that contains game messages is `GameMessageCharacterList`.

## Packet → fragments → game messages

One UDP packet can carry one or more **fragments**. Each
fragment carries part (or all) of one **game message**. Large
messages (e.g. a fully populated `CharacterList`) are split
across multiple fragments, which may span multiple packets.

```
UDP datagram
  PacketHeader (20 B)
  HeaderOptional (variable, only if non-Flags-zero flags set)
  Body:
    Fragment[0]:
      FragmentHeader
      Fragment payload bytes
    Fragment[1]:
      FragmentHeader
      Fragment payload bytes
    ...
```

The server-side encoder is in
[`ServerPacket.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ServerPacket.cs)
and
[`PacketFragment.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketFragment.cs).

The client-side decoder we need to mirror is in
[`ClientPacket.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ClientPacket.cs)
and
[`ClientPacketFragment.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ClientPacketFragment.cs).

## FragmentHeader (TBD — needs verification)

⚠ Field layout below is from a quick skim of `PacketFragmentHeader.cs`
and **needs verification** by capturing a real fragment in
Phase 2.

| field | size | description |
|---|---|---|
| `Size` | `u16` | Total fragment payload size (including this header)? Or excluding? — verify |
| `Group` | `u16` | Reliability / ordering group |
| `Sequence` | `u32` | Per-fragment sequence within the message |
| `Id` | `u32` | Message id this fragment belongs to |
| `Count` | `u16` | Total fragment count for this message |
| `Index` | `u16` | This fragment's index within the message |
| `Queue` | `u16` | Queue id (e.g. action queue vs control queue) |

Source: [`PacketFragmentHeader.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketFragmentHeader.cs).

## Game-message envelope

Each game message starts with a `u32 opcode` (the
`GameMessageOpcode`). The opcode determines how to parse the
rest of the message's bytes.

The full opcode enumeration is in
[`GameMessageOpcode.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Enum/GameMessageOpcode.cs).
Some opcodes the spike will need:

| opcode | hex | direction | purpose |
|---|---|---|---|
| `CharacterList` | `0x0042` | S → C | Character roster after login |
| `ServerName` | `0x00A4` | S → C | Server display name |
| `DDDInterrogation` | `0x0044` | S → C | Triggers client to declare its data versions |
| `CharacterEnterWorldRequest` | `0xF657` | C → S | Client asks to enter world with chosen character GUID |
| `CharacterCreate` | `0xF646` | C → S | Create new character |
| `CharacterDelete` | `0xF656` | C → S | Delete character |
| `Talk` | `0x0009` | both | Chat |
| `Movement` | `0xF74C` | C → S | Player movement input |

(Many more — full list in source.)

## Reliability and ordering

Game messages are sent in "groups" that determine reliability
and ordering semantics:

- **Reliable, ordered** — e.g., chat, world state changes.
  Each fragment has a sequence number; receiver acks; sender
  retransmits on no-ack.
- **Unreliable, unordered** — e.g., position broadcasts.
- **Control** — e.g., session management.

The `Queue` field on `FragmentHeader` selects the group.

## To be filled in (Phase 2+)

- Exact FragmentHeader layout verified against a real capture
- Per-opcode payload schemas for at least:
  - `CharacterList`
  - `ServerName`
  - `DDDInterrogation`
  - `CharacterEnterWorldRequest`
  - `Movement`
  - `Talk`
- Fragment reassembly algorithm
- Per-queue ack/retransmit timing
