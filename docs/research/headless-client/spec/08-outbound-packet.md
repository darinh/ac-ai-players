# 08 — Outbound packet (client → server)

This document covers everything a headless client implementer
needs to construct a server-accepted UDP packet, with special
attention to the **encrypted `BlobFragments`** path that carries
all real game messages.

Verified against `Source/ACE.Server/Network/` (ACE-bots branch
`botplayer-spike`) and against live `ACEmulator` running locally.
End-to-end evidence:
[`phase3-probe-run-01.log`](../phase3-probe-run-01.log).

## Packet structure (recap)

Every outbound UDP datagram is:

```
[ PacketHeader 20 bytes ]
[ Optional bytes  variable ]    // 0–N depending on header flags
[ Fragment 1  16 hdr + data ]   // present iff BlobFragments flag set
[ Fragment 2  16 hdr + data ]   // …
[ Fragment N  16 hdr + data ]
```

`PacketHeader` schema (matches `spec/02-network.md`):

| Offset | Size | Field          | Notes |
|-------:|-----:|----------------|-------|
|      0 |    4 | Sequence       | u32 LE. See "Sequence rules" below. |
|      4 |    4 | Flags          | u32 LE bitfield (see `PacketHeaderFlags`). |
|      8 |    4 | Checksum       | u32 LE. See "Checksum" below. |
|     12 |    2 | Id             | u16 LE. Client ID assigned by server in `ConnectRequest`. |
|     14 |    2 | Time           | u16 LE. Server ignores; we send `0`. |
|     16 |    2 | Size           | u16 LE. **Body length only** — excludes the 20-byte header. |
|     18 |    2 | Iteration      | u16 LE. From `ConnectRequest`. |

### Body field ordering

Optional fields appear **in flag-bit order**, lowest bit first:

| Flag (bit) | Field         | Size | Encoding |
|-----------:|---------------|-----:|----------|
| `Retransmit` (0x0001) | retransmit seqs | 4 + 4·N | u32 count + N×u32 |
| `RejectRetransmit` (0x0002) | reject seqs | 4 + 4·N | u32 count + N×u32 |
| `AckSequence` (0x0004) | ack          | 4   | u32 last-received-seq |
| `Login` etc. | … | … | (handshake-only; see `spec/04-handshake.md`) |
| `TimeSync` (0x1000000) | timestamp | 8   | f64 LE seconds |
| `EchoRequest`/`EchoResponse` | … | … | (TBD) |
| `BlobFragments` (0x40000000) | fragments | variable | sequence of 16-byte headers + data |

`OutboundPacket.Pack()` writes optional fields strictly in the
order shown. Servers reject out-of-order optional fields.

### Per-fragment header

```
struct PacketFragmentHeader {        // 16 bytes
    u32 Sequence;   // per-MESSAGE sequence; all fragments of one
                    // game message share this value
    u32 Id;         // logical message id; server uses 0x80000000
                    // as a marker, client can use any 32-bit value
                    // — it appears to be ignored by inbound dispatch
    u16 Count;      // total fragments in this message
    u16 Size;       // **INCLUDES** the 16-byte header
    u16 Index;      // 0-based, must be < Count
    u16 Queue;      // GameMessageGroup (UIQueue=0x09 for char-mgmt)
}
```

Followed by `Size − 16` bytes of game-message payload (the
first 4 bytes of which are the LE u32 opcode).

## Sequence rules (single most error-prone area)

These rules are derived from `NetworkSession.cs:57, 362-367, 496`.

1. **Packet `Sequence`** is the OUTER UDP packet number.
2. **Server's `lastReceivedPacketSequence` starts at `1`** at
   session creation. The server REJECTS any sequenced packet
   with `Sequence <= lastReceivedPacketSequence` (except the
   special bare-`AckSequence` case below).
3. Therefore the **first non-zero outbound `Sequence` must be
   `2`**, not `1`.
4. `Sequence = 0` is reserved for unsequenced control packets:
   bare `AckSequence` and bare `TimeSync`. These do NOT advance
   the server's counter, so they are safe to send as often as
   needed without burning a slot.
5. After a non-zero `Sequence` is accepted, increment by 1 for
   every subsequent non-zero packet. Never reuse, never skip.
6. **Fragment `Sequence`** (inside `PacketFragmentHeader`) is
   a **per-MESSAGE** counter, separate from the packet sequence.
   All fragments of one game message share the same value;
   `Index` differentiates them. After a complete message is
   reassembled, increment by 1 for the next message.

### What goes wrong if you mis-sequence

| Mistake | Server behavior |
|---|---|
| Send first `Sequence = 1` | Silently dropped at `NetworkSession.cs:362`. Looks like packet loss; client retransmits forever. |
| Skip a sequence number | Server queues out-of-order, requests retransmit via `Retransmit` flag (which our spike does NOT implement yet — see "Open issues"). |
| Reuse a sequence number | Server treats as duplicate, drops silently. |
| Send fragment `Index >= Count` | Reassembly fails; message dropped. |
| Send fragment shorter than 4 bytes | Silently dropped at `NetworkSession.cs:545-546` AND fails to advance per-message sequence. |

## Per-fragment size cap

**Verified at `PacketFragment.cs:6-7`:**

```
MaxFragmentSize     = 464   // 16-byte header + 448 data
MaxFragmentDataSize = 448
```

The 464 figure is enforced for inbound at `ClientPacketFragment.cs:17`.

Do **not** confuse this with `ClientPacket.MaxPacketSize = 1024`,
which is the full UDP datagram cap (header + body, multiple
fragments allowed).

Implementer rule: any game-message payload larger than 448
bytes must be split into N fragments where every fragment
data ≤ 448, and each fragment carries `Index = 0..N-1`,
`Count = N`, the same `Sequence`. The spike enforces
`4 ≤ data.Length ≤ 448` in `OutboundPacket.AddBlobFragment` and
throws on violation — see
[`OutboundPacket.cs`](../../../experiments/headless-client/src/HeadlessAcClient/Protocol/OutboundPacket.cs).

## Checksum

Identical formula in both directions. Reference implementation
in `ClientPacket.cs:127-146` (server verifying client) — the
client mirror is in
[`OutboundPacket.cs`](../../../experiments/headless-client/src/HeadlessAcClient/Protocol/OutboundPacket.cs)
and is round-trip-verified by `OutboundSelfCheck.cs`.

```
headerChecksum    = Hash32(header bytes, Checksum field zeroed)
payloadChecksum   = Hash32(optionalBytes)
                  + Σ_per_frag( Hash32(16B fragHeader) + Hash32(fragData) )

if (EncryptedChecksum flag set):
    isaacKey = cryptoSend.ConsumeKey(cryptoSend.PeekCurrentKey())
    header.Checksum = unchecked(headerChecksum + (payloadChecksum XOR isaacKey))
else:
    header.Checksum = unchecked(headerChecksum + payloadChecksum)
```

All additions are explicitly `unchecked` (modulo 2³²). Failing
to mark the C# additions `unchecked` will throw on overflow in
debug builds; release builds silently produce a wrong checksum,
which the server then rejects.

`Hash32` is `ACE.Common.Cryptography.Hash32.Calculate` — a small
custom checksum, NOT CRC32. Don't substitute.

## Encryption (`EncryptedChecksum` flag)

Each side gets a 4-byte ISAAC seed from the handshake's
`ConnectRequest` data. Wire-level mirror (verified at
`SessionConnectionData.cs:60-61`):

| Client field | Constructed from | Used to |
|---|---|---|
| `cryptoRecv` | `ConnectRequest.ServerSeed` | verify CRC on inbound encrypted packets |
| `cryptoSend` | `ConnectRequest.ClientSeed` | encrypt CRC on outbound packets |

Symmetry comes from running `CryptoSystem` (ISAAC variant) with
identical seeds on both ends. `CryptoSystem.PeekCurrentKey()`
followed by `ConsumeKey(currentKey)` consumes one 32-bit ISAAC
output. **One key per encrypted PACKET**, not per fragment.

The encrypted checksum chain is **stateful**: out-of-order or
dropped encrypted packets desynchronize ISAAC and every
subsequent packet will fail CRC. There is currently no recovery
path — see "Open issues."

## What the spike currently does NOT handle

- **Retransmit requests.** If the server sends a `Retransmit`
  optional asking for an out-of-window packet, the spike ignores
  it. For Phase 3.1 this is fine; for sustained play we must
  buffer the last N outbound encrypted packets and retransmit
  on request. Sketched in `next_steps.md` (Phase 4).
- **CICMDCommand on port `+1`.** Out of scope for bots.
- **Sequence wraparound at 2³²-1.** Not a real concern; a 100 Hz
  bot would need ~16 months of uptime.
- **Multi-fragment outbound messages.** The `AddBlobFragment`
  path supports them mechanically (caller controls `Index`,
  `Count`, `Sequence`), but no game message we send yet exceeds
  448 bytes. `CharacterCreate` in Phase 3.2 is the first
  potential candidate.

## End-to-end evidence

`phase3-probe-run-01.log` shows a complete Phase 3.1 cycle:

1. Self-check round-trip OK across 6 patterns (plain optional,
   plain single/multi fragment, plain optional + fragment,
   encrypted single fragment, size-guard throws).
2. Probe transmitted: encrypted `BlobFragments(opcode=0xFFFE,
   pktSeq=2, fragSeq=1)` (44 bytes).
3. Server replies with 10× bare `AckSequence(2)` packets within
   ~20 ms, confirming the server validated header CRC, decrypted
   payload CRC, and accepted packet `Sequence = 2`.
4. Server log entry (`C:\ACE\Logs\ACE_Log.txt`):
   `WARN (InboundMessageManager) Received unhandled fragment
   opcode: 0xFFFE - 65534` — proves the fragment reassembled
   and dispatched.
5. Session stays alive 65 s past the probe with 0/34 CRC
   failures and full Phase 2 PASS banner.

## See also

- [`02-network.md`](02-network.md) — header layout, port routing
- [`03-crypto.md`](03-crypto.md) — ISAAC + Hash32 details
- [`06-game-messages.md`](06-game-messages.md) — game-message
  opcode catalog; outbound payload schemas land here as we
  implement them.
- [`OutboundPacket.cs`](../../../experiments/headless-client/src/HeadlessAcClient/Protocol/OutboundPacket.cs)
  — reference implementation
- [`OutboundSelfCheck.cs`](../../../experiments/headless-client/src/HeadlessAcClient/Protocol/OutboundSelfCheck.cs)
  — round-trip verification harness
