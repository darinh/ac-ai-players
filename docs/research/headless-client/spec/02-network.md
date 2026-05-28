# 02 — Network layer

## Transport

AC1 uses **UDP** only. There is no TCP fallback. A single
client socket talks to two adjacent server ports.

- **Server `Port`** (default `9000`) — receives `LoginRequest`
  from new clients and ALL subsequent in-session traffic.
- **Server `Port + 1`** (default `9001`) — receives ONLY
  `ConnectResponse` (third handshake leg) and `CICMDCommand`.
  Anything else arriving on this port is logged as a protocol
  error and dropped.

The client picks any ephemeral local port. The same client
socket is used for both server ports — only the destination
port changes.

**Source**:
[`Source/ACE.Server/Network/Managers/NetworkManager.cs:45-160`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/NetworkManager.cs).

### Why two ports?

The two-port design dates to the original Turbine protocol.
Practical effect: the third handshake leg arrives on a different
listener, which is how the server distinguishes a brand-new
`LoginRequest` from a handshake-completion ACK for a
previously-initiated session. It also lets the server cheaply
route `CICMDCommand` (admin RPC) to a separate handler.

For implementers: a client need not bind two sockets. One
sending socket suffices — just send the `ConnectResponse`
datagram with destination port `Port + 1` instead of `Port`.

### Packet size limits

| Direction | Limit | Source |
|---|---|---|
| Client → Server | 1024 bytes | `ClientPacket.MaxPacketSize` |
| Server → Client | 464 bytes | `ServerPacket.MaxPacketSize` (the source carries a "TODO: I don't know why this value is 464" comment — treat as a hard constant for now) |

Implementers should plan for ≤ 1024-byte receive buffers and
≤ 464-byte send buffers. Use the standard rented-buffer pattern
(`ArrayPool<byte>.Shared`) to avoid per-packet allocations.

## Packet anatomy

Every UDP datagram contains exactly one AC packet. The packet
has three sections in order:

1. **Fixed header** (`PacketHeader`) — 20 bytes, always present.
2. **Optional header** (`PacketHeaderOptional`) — 0 to ~64
   bytes, present in pieces selected by the flags in the fixed
   header.
3. **Fragments** — present iff the `BlobFragments` flag is set.
   See `06-game-messages.md`.

```
+---------------------+
| PacketHeader (20 B) |
+---------------------+
| Optional sections   |
|   AckSequence       |  if 0x00004000 set
|   TimeSync          |  if 0x01000000 set
|   EchoRequest       |  if 0x02000000 set
|   Flow              |  if 0x08000000 set
|   ConnectResponse   |  if 0x00080000 set
|   ...               |
+---------------------+
| Fragment 1          |  if BlobFragments (0x00000004) set
| Fragment 2          |
| ...                 |
+---------------------+
```

`PacketHeader.Size` is the byte count of everything **after**
the 20-byte fixed header.

## PacketHeader

20 bytes. Always present. Little-endian throughout.

| offset | size | type | field | notes |
|--------|------|------|-------|-------|
| 0 | 4 | `u32` | `Sequence` | Per-stream sequence number. 0 for handshake legs; for in-session packets, monotonically incremented per direction. |
| 4 | 4 | `u32` | `Flags` | Bitfield of `PacketHeaderFlags`. See table below. |
| 8 | 4 | `u32` | `Checksum` | Plain `headerChecksum + payloadChecksum`, or encrypted `headerChecksum + (payloadChecksum XOR isaacKey)` when `EncryptedChecksum` flag is set. See `03-crypto.md`. |
| 12 | 2 | `u16` | `Id` | Session ID. Server-assigned. 0 in client's LoginRequest; non-zero in server's ConnectRequest reply and all subsequent packets. The same `Id` is echoed by both peers throughout the session. |
| 14 | 2 | `u16` | `Time` | Packet-emitter timestamp (server uses `(ushort)((Timers.PortalYearTicks - SomeBase) & 0xFFFF)`). Used for time sync. Clients typically write 0 outbound until they receive a TimeSync. |
| 16 | 2 | `u16` | `Size` | Byte count of payload (everything after this header). |
| 18 | 2 | `u16` | `Iteration` | Session iteration counter. Increments on each reconnect within a session id. Acts as a tie-breaker if `Id` is reused. |

**Source**: [`PacketHeader.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeader.cs).

### Observed example (Phase 1 spike, ConnectRequest reply)

```
hex: 00 00 00 00  00 00 04 00  33 F0 FC 44  0B 00 00 00  20 00 01 00
     ----------- Sequence=0   ------ Flags=0x00040000   Id=11 (0x0B)
                              (ConnectRequest)
                                          ------ Checksum=0x44FCF033
                              -----------                Time=0
                                                        ----- Size=0x20=32
                                                              ----- Iteration=1
```

This was the server's reply to a successful `LoginRequest`.
`Flags=0x40000` is `ConnectRequest`. `Size=32` covers the
ConnectRequest body that follows. `Id=11` is our session id.

## PacketHeaderFlags

`u32` bitfield. Multiple bits can be set. The order of bits
maps directly to the order in which the corresponding optional
header sections are written / read.

| bit | hex | name | parses how many bytes after header | notes |
|-----|-----|------|------------------------------------|-------|
| 0 | `0x00000001` | `Retransmission` | 0 | Set on a packet being retransmitted in response to a `RequestRetransmit`. |
| 2 | `0x00000004` | `BlobFragments` | variable | Payload contains one or more game-message fragments. See `06-game-messages.md`. |
| 8 | `0x00000100` | `ServerSwitch` | 8 | Server says "switch to a new server endpoint" (used for world transfer between server nodes). 8 bytes captured into checksum. |
| 12 | `0x00001000` | `RequestRetransmit` | 4 + (4 × N) | Body starts with `u32 count`, then `count × u32` sequence numbers being requested. |
| 13 | `0x00002000` | `RejectRetransmit` | 4 + (4 × N) | Same layout as `RequestRetransmit`, but tells the other side "those sequences are gone, don't ask again." |
| 14 | `0x00004000` | `AckSequence` | 4 | `u32 AckSequence` — peer confirms receipt of all sequences ≤ this number. |
| 16 | `0x00010000` | `LoginRequest` | variable (whole remaining body) | First handshake leg. Body layout is the LoginRequest struct (see `04-handshake.md`). Entire body is captured into the optional-header checksum. |
| 17 | `0x00020000` | `WorldLoginRequest` | 8 | Used for world-server re-login (the server-switch follow-up). 8 bytes captured. |
| 18 | `0x00040000` | `ConnectRequest` | (server reply only) | The server's reply to LoginRequest. Body is 32 bytes (see `04-handshake.md`). NOT captured into the inbound headerOptional checksum by the receiving side. |
| 19 | `0x00080000` | `ConnectResponse` | 8 | Third handshake leg. Body is `u64 cookie` — must echo the cookie from the ConnectRequest. 8 bytes captured. |
| 22 | `0x00400000` | `CICMDCommand` | 8 | Admin RPC. 8 bytes captured. |
| 24 | `0x01000000` | `TimeSync` | 8 | `f64 ServerTime`. Server periodically pushes its clock; client echoes back on next TimeSync. |
| 25 | `0x02000000` | `EchoRequest` | 4 | `f32 ClientTime`. Round-trip latency probe. |
| 26 | `0x04000000` | `EchoResponse` | 8 | `f32 ClientTime` + `f32 ServerTime`. Reply to an EchoRequest. |
| 27 | `0x08000000` | `Flow` | 6 | `u32 FlowBytes` + `u16 FlowInterval`. Flow control hint. |
| 29 | `0x20000000` | `EncryptedChecksum` | 0 | Indicates `Header.Checksum` was XOR-masked with an ISAAC key before sending. Receiver must XOR back to validate. Set per-packet once the session crosses into encrypted-traffic mode. |
| 30 | `0x40000000` | `Disconnect` | 0 | Peer is disconnecting cleanly. |
| 31 | `0x80000000` | `LogOff` | 0 | Application-level log off (vs. low-level disconnect). |

**Source**: [`PacketHeaderFlags.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeaderFlags.cs)
and the bit-by-bit read order in
[`PacketHeaderOptional.cs:Unpack`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeaderOptional.cs).

### Optional-header read order

The receiver reads optional sections in flag-bit ascending
order, regardless of what order the sender thought of them. So
the sender must serialize sections in the same order:

1. `ServerSwitch` (8B)
2. `RequestRetransmit` (4+4N)
3. `RejectRetransmit` (4+4N)
4. `AckSequence` (4B)
5. `LoginRequest` (whole body capture)
6. `WorldLoginRequest` (8B)
7. `ConnectResponse` (8B captured but stream NOT advanced for outbound generation — peeked)
8. `CICMDCommand` (8B)
9. `TimeSync` (8B)
10. `EchoRequest` (4B)
11. `Flow` (6B)

`EchoResponse`, `EncryptedChecksum`, `Disconnect`, `LogOff` have
no body bytes — they're pure flags.

## Sequencing and ACK protocol

After the handshake, both sides start emitting sequenced
packets:

- Each sender independently maintains an outbound counter,
  starting at `Sequence = 1` (handshake legs use `Sequence = 0`).
- The sender increments `Sequence` for every outbound packet
  that needs reliable delivery (i.e. carrying fragments or
  certain optional sections).
- The receiver, on receipt of `Sequence = N`, sets a pending
  `AckSequence = N` to send back at the next opportunity.
- The receiver bundles the `AckSequence` flag onto its next
  outbound packet (or sends a bare `AckSequence`-only packet
  with `Sequence = 0`).
- If the sender does not see an ACK for `Sequence = N` within
  a timeout (~5 seconds based on observation), it should
  request retransmit via `RequestRetransmit`.
- If the receiver gets a `RequestRetransmit` for a sequence it
  no longer has buffered, it replies with `RejectRetransmit`
  and the connection effectively dies.

⚠ **Empirical observation (Phase 1 spike, 30-second silent
client)**: after a successful handshake, an ACE server sends
the client a `Flags=AckSequence Size=4 body=01 00 00 00`
packet every ~3.7 seconds — i.e., it ACKs `1` over and over
even though the client has sent nothing past the handshake.
The connection survived the full 30s observation window
without a TCP-style RST or session termination. We do not yet
know the actual server-side timeout for a fully-silent client;
plan as if it's 15-30s and send our own keepalive (either an
`AckSequence` or a `TimeSync` echo) at least every 5s once
sequenced traffic begins.

## Retransmit protocol (server-driven)

When the server has sent `Sequence = N` and not received an ack
within the server's tick window, it expects the client to
request retransmit. The flow:

1. Client receives, say, `Sequence = 7` but never got
   `Sequence = 6` (out of order or lost).
2. Client builds an outbound packet with
   `Flags = RequestRetransmit` and body `u32 1; u32 6` — "I'm
   missing sequence 6."
3. Server replies with an outbound packet flagged
   `Retransmission` carrying the original `Sequence = 6` body.
4. If the server no longer has 6 buffered, it instead replies
   with `RejectRetransmit` body `u32 1; u32 6` — "gone."
5. On `RejectRetransmit`, the receiver should terminate the
   session and reconnect.

The ISAAC keystream window (`CryptoSystem.Search`, see
`03-crypto.md`) is what tolerates out-of-order delivery on the
crypto side — a key consumed late doesn't break decryption of
keys that arrived earlier.

## TimeSync

Server emits `Flags = TimeSync` with body `f64 ServerTime`
roughly every 5 seconds.  This is the server's
`Timers.PortalYearTicks` value (seconds since some epoch known
to AC). Client echoes back its own `TimeSync` in response to
maintain a rough clock offset and acts as a keepalive.

Source: [`PacketHeaderOptional.cs:103-108`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeaderOptional.cs#L103).

## EchoRequest / EchoResponse

`EchoRequest` carries one `f32 ClientTime`. The receiver replies
with `EchoResponse` carrying that same `f32 ClientTime` plus its
own `f32 ServerTime`. Used for RTT measurement.

## Flow control

`Flow` body is `u32 FlowBytes` + `u16 FlowInterval`. Hint from
the sender telling the receiver "you can send me up to
`FlowBytes` bytes in the next `FlowInterval` milliseconds." In
practice an honest client respects this; an unbothered client
gets dropped by the server's per-session rate limiter.

## Session termination

A session ends on any of:

- `Disconnect` flag from either side
- `LogOff` flag from the client
- Server-side timeout (no inbound traffic for some interval —
  appears to be ~15s based on common AC client behavior)
- `RejectRetransmit` (gap-too-large)
- Crypto failure (`CryptoSystem.Search` fails to find the key
  in the 256-key window)

On termination, the server's `Session.Terminate(reason)` emits
a final `GameMessageBootAccount` or `GameMessageCharacterError`
on the wire, then unbinds.
