# 99 — References

Authoritative sources for everything in this spec.

## Primary source: the ACE server

All file references below are to the fork at
[darinh/ACE-bots](https://github.com/darinh/ACE-bots/tree/botplayer-spike),
branch `botplayer-spike`. They're the same files as upstream
[ACEmulator/ACE](https://github.com/ACEmulator/ACE) unless
explicitly modified.

### Cryptography (`Source/ACE.Common/Cryptography/`)

| File | Purpose |
|---|---|
| [`Hash32.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Cryptography/Hash32.cs) | 32-bit checksum: word XOR, padded tail, even/odd-position rotations |
| [`ISAAC.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Cryptography/ISAAC.cs) | Bob Jenkins ISAAC PRNG with `Reset` + `Next` |
| [`CryptoSystem.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Cryptography/CryptoSystem.cs) | 256-slot lookahead window + sequencing |

### Network primitives (`Source/ACE.Server/Network/`)

| File | Purpose |
|---|---|
| [`PacketHeader.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeader.cs) | 20-byte fixed header, `0xBADD70DD` magic |
| [`PacketHeaderFlags.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Enum/PacketHeaderFlags.cs) | Bitmask values for header `Flags` field |
| [`PacketHeaderOptional.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeaderOptional.cs) | Read order + body-capture rules per flag |
| [`ClientPacket.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ClientPacket.cs) | Inbound packet parser (`VerifyCRC` at lines 138-163) |
| [`ServerPacket.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ServerPacket.cs) | Outbound packet builder (lines 46-72 are the build path) |
| [`PacketFragment.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketFragment.cs) | Server-side fragment encoder |
| [`PacketFragmentHeader.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketFragmentHeader.cs) | Fragment header layout |
| [`ClientPacketFragment.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ClientPacketFragment.cs) | Inbound fragment decoder |

### Network management (`Source/ACE.Server/Network/Managers/`)

| File | Purpose |
|---|---|
| [`NetworkManager.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/NetworkManager.cs) | Two-port routing, lines 45-160; `FindOrCreateSession` |
| [`ConnectionListener.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/ConnectionListener.cs) | UDP socket binding per port |
| [`Session.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/Session.cs) | Per-client session state |
| [`SessionConnectionData.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/SessionConnectionData.cs) | ISAAC + cookie + seeds |
| [`NetworkSession.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/NetworkSession.cs) | Per-session packet I/O |

### Handshake (`Source/ACE.Server/Network/`)

| File | Purpose |
|---|---|
| [`AuthenticationHandler.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Handlers/AuthenticationHandler.cs) | Server side of legs 1-3; `ClientVersion="1802"` check at line 112 |
| [`PacketInboundLoginRequest.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Packets/PacketInboundLoginRequest.cs) | Exact field order for LoginRequest body |
| [`PacketOutboundConnectRequest.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Packets/PacketOutboundConnectRequest.cs) | 32-byte ConnectRequest body |

### Data types and serialization

| File | Purpose |
|---|---|
| [`BinaryReaderExtensions.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Extensions/BinaryReaderExtensions.cs) | `ReadString16L` and `ReadString32L` — authoritative spec for the two string encodings |

### Game messages (full list)

| File | Purpose |
|---|---|
| [`GameMessageOpcode.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Enum/GameMessageOpcode.cs) | All game-message opcodes |
| `Source/ACE.Server/Network/GameMessages/Messages/` | Server-to-client message classes |
| `Source/ACE.Server/Network/GameAction/Actions/` | Client-to-server action handlers (`GameActionLoginComplete`, etc.) |

## Decal SDK

Decal is the original AC1 client plugin SDK. It was the
de-facto reference for understanding the client side of the
protocol prior to ACE.

- [Decal source archive](https://github.com/DecalPlugins) — multiple
  community plugins; useful for opcode and field-shape
  references when the server source is silent on a detail.

## Other reverse-engineering projects

- [ACEmulator/ACE](https://github.com/ACEmulator/ACE) — upstream of
  the ACE-bots fork; this is where the server logic lives.
- [DerethForever](https://github.com/DerethForever) — historical
  fork; sometimes has comments or branches that explain
  protocol corners. Largely superseded by ACE.
- [ACClient](https://github.com/) — name varies; community
  clients in the past have attempted partial reimplementation.
  Useful as a sanity check but not authoritative.

## Spec history within this repo

- [`../../README.md`](../README.md) — multi-agent plan that
  bootstrapped this spike.
- [`../phase1-results.md`](../phase1-results.md) — handshake
  proof-of-life evidence (server log + client log).
- [`../api-architecture.md`](../api-architecture.md) — separate
  spec for the service API the headless client calls into for
  LLM, pathfinding, training data, world data, and per-bot
  memory.

## Citation convention used in this spec

Every claim that maps to a specific server-side decision should
cite `path/to/file.cs:LINE-LINE`. Where a file has changed
shape, prefer line ranges over single lines so a future reader
can still find the relevant block after refactoring.

A bare `path/to/file.cs` (no line) means "this entire file is
the authoritative source for the concept."
