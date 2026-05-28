# 01 — Protocol overview

## What AC1 is

Asheron's Call 1 (Turbine, 1999) uses a custom UDP-based binary
protocol. There is no HTTP, no WebSocket, no TLS, no auth
service. The client opens a single UDP socket to a single
server endpoint, completes a three-leg handshake, and from then
on exchanges length-framed binary packets containing game
messages.

## Layer model

The protocol has four conceptual layers. Each is documented in
its own spec file.

```
+----------------------------------------+
| Layer 4: Game messages                 |  06-game-messages.md
|   character list, character enter      |
|   world, movement, chat, combat,       |
|   inventory, magic, etc.               |
+----------------------------------------+
| Layer 3: Packet framing                |  02-network.md
|   PacketHeader + PacketHeaderOptional  |
|   + Fragments (BlobFragments) +        |
|   ACK / sequence numbers / retransmit  |
+----------------------------------------+
| Layer 2: Cryptography                  |  03-crypto.md
|   Hash32 (checksum), ISAAC (xor key    |
|   stream), CryptoSystem (lookahead     |
|   window)                              |
+----------------------------------------+
| Layer 1: UDP transport                 |  02-network.md
|   single UDP socket, two server ports  |
|   (port and port+1)                    |
+----------------------------------------+
```

- **Layer 1** is just UDP. The client picks an ephemeral local
  port; the server listens on two adjacent ports (default 9000
  and 9001).
- **Layer 2** wraps every packet in a 4-byte XOR-stream
  checksum keyed by ISAAC. The handshake itself is unencrypted;
  the encrypted-checksum flag turns on per-packet after the
  application layer requests it.
- **Layer 3** gives every packet a 20-byte fixed header plus a
  variable optional-header section, plus zero or more fragments
  if the `BlobFragments` flag is set. The optional header
  carries connection metadata (ACK sequence, time sync, echo).
- **Layer 4** is game content. Every payload that isn't pure
  connection metadata travels as one or more fragments. Each
  fragment carries a 4-byte opcode that dispatches to a handler.

## What lives where

| Concern | Layer | Where it shows up |
|---|---|---|
| Connection liveness (keepalive, ack) | 3 | `PacketHeaderOptional` AckSequence/TimeSync |
| Packet loss handling | 3 | `RequestRetransmit` / `RejectRetransmit` flags |
| Out-of-order delivery | 2 + 3 | ISAAC lookahead window (256 keys) + per-packet sequence number |
| Authentication | 4 | LoginRequest body (legacy single-leg auth, no separate auth server) |
| Character selection | 4 | GameMessageCharacterList → GameActionCharacterEnterWorld |
| World presence | 4 | Object create/update/delete messages |

## What this implementation does and does not do

The headless client spike implements:

1. **Layer 1** in full — Phase 1 done.
2. **Layer 2** in full — Phase 2 in progress.
3. **Layer 3** minimum viable subset — Phase 2 will get us to
   "stay connected past 15s without being timed out."
4. **Layer 4** as the bot needs — Phase 3+. Character list,
   enter-world, basic movement, basic chat. Combat / magic /
   trade much later.

## Endianness, alignment, padding

- All multi-byte integers are **little-endian**.
- No structure has internal padding for alignment — fields are
  packed back-to-back.
- Some variable-length encodings (notably the string types) pad
  the total length up to a multiple of 4 bytes. This is alignment
  of the *next* field, not internal alignment of the string
  itself. The padding is included in the parent's "size" field.

## Magic numbers worth memorizing

| Value | What it is | Where |
|---|---|---|
| `1024` | Max packet size (server side hard cap) | `ClientPacket.MaxPacketSize` |
| `464` | Max packet size (client →) the server emits | `ServerPacket.MaxPacketSize` (comment notes "I don't know why this value is 464") |
| `20` | `PacketHeader` size in bytes | `PacketHeader.HeaderSize` |
| `0xBADD70DD` | Magic placeholder for header checksum during computation | `PacketHeader.CalculateHash32` |
| `256` | ISAAC lookahead window depth | `CryptoSystem.MaximumEffortLevel` |
| `1802` | Required `ClientVersion` string in LoginRequest body | `AuthenticationHandler` asserts this |
| `9000`, `9001` | Default server ports (cleartext is `Network.Port` and `Network.Port + 1`) | `Config.Server.Network.Port` |

## Wire byte order convention used in this spec

When we write something like:

```
offset  size  type   field        notes
------  ----  -----  -----------  --------------
0       2     u16    Sequence     little-endian
2       4     u32    Flags        bitfield
6       4     u32    Checksum     little-endian
...
```

bytes go on the wire in the order `byte at offset 0, byte at
offset 1, ...`. A `u32 = 0x12345678` is sent as `78 56 34 12`.
