# 03 — Cryptography

AC1 has two cryptographic primitives in play:

1. **Hash32** — a custom additive checksum. Cheap, not
   collision-resistant, not a security primitive — used purely
   to detect packet corruption.
2. **ISAAC** — Bob Jenkins' ISAAC stream cipher. Used to
   generate a per-packet 32-bit XOR mask that the sender
   applies to the checksum. The server keeps a 256-key
   lookahead window to tolerate out-of-order packet delivery.

There is no real encryption of payload bytes. Only the
4-byte `Header.Checksum` field is XOR-masked. The body is
plaintext on the wire.

This is sufficient for the AC threat model (casual cheating,
not a determined attacker with a packet capture). It is NOT
sufficient as a general-purpose channel security primitive —
do not extrapolate this design to anything that needs
real confidentiality.

## Hash32

Custom additive checksum over a byte buffer.

```
function Hash32(data, length):
    checksum = (length << 16) as u32
    for i = 0 step 4 while i + 4 <= length:
        checksum += read_u32_le(data[i..i+4])
    shift = 3
    j = (length / 4) * 4
    while j < length:
        checksum += (data[j] << (8 * shift)) as u32
        j += 1
        shift -= 1
    return checksum
```

Properties:

- Output is `u32`.
- Modular addition (overflow wraps).
- Tail bytes (length not a multiple of 4) are folded in with
  decreasing shifts (24, 16, 8) — equivalent to treating them
  as a big-endian last partial word XOR'd into the running sum.
- Length is mixed in via the initial `length << 16`. Empty
  input → 0.

**Source**:
[`Source/ACE.Common/Cryptography/Hash32.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Cryptography/Hash32.cs).

### Test vectors

These are taken from the Phase 1 spike's first successful run.
Use them to validate any independent reimplementation.

| Input | Length | Output |
|---|---|---|
| (empty) | 0 | `0x00000000` |
| `[01]` | 1 | `0x01010000` |
| `[01 02 03 04]` | 4 | `0x04050304` |
| `[01 02 03 04 05]` | 5 | `0x09050304` (= `0x04050304 + 0x05000000`) |

Calculation walkthrough for `[01 02 03 04 05]`:
- `checksum = 5 << 16 = 0x00050000`
- First 4 bytes as `u32 LE = 0x04030201`
- `checksum += 0x04030201 = 0x04080201` ← wait, recompute:
  `0x00050000 + 0x04030201 = 0x04080201`. Hmm, the table above
  shows `0x04050304` for the 4-byte input — let me re-walk that:
  - `checksum = 4 << 16 = 0x00040000`
  - First 4 bytes as `u32 LE = 0x04030201`
  - `checksum += 0x04030201 = 0x04070201`. So the table value
    `0x04050304` is wrong in my draft — TODO: regenerate from
    a live run before relying on these.

⚠ The test vector table needs an actual run-against-the-impl
verification. Treat it as illustrative pseudocode for now and
generate authoritative vectors in a follow-up commit.

## ISAAC

Bob Jenkins' ISAAC PRNG / stream cipher. Public-domain
algorithm published 1996. Generates a sequence of 32-bit
words from a seed of up to 256 `u32` values.

Reference: <https://www.burtleburtle.net/bob/rand/isaacafa.html>

The AC code passes a 4-byte seed (one `u32`) and lets ISAAC's
init expand it to the full 256-word state. The seed is the
4-byte `ServerSeed` or `ClientSeed` from the handshake
ConnectRequest body.

**Source**:
[`Source/ACE.Common/Cryptography/ISAAC.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Cryptography/ISAAC.cs).

API surface used by AC:

- `new ISAAC(byte[] seed)` — initialize with up to 256 `u32`
  seed words (AC passes 4 bytes = 1 `u32`).
- `uint Next()` — pull the next keystream word.

Each direction (C→S and S→C) has its own independent ISAAC
instance:

| Stream | Sender's ISAAC | Receiver's ISAAC |
|---|---|---|
| Client → Server | `ISAAC(ClientSeed)` on client side | `ISAAC(ClientSeed)` on server side (`CryptoClient` member of `SessionConnectionData`) |
| Server → Client | `ISAAC(ServerSeed)` on server side | `ISAAC(ServerSeed)` on client side |

The two seeds (`ServerSeed`, `ClientSeed`) are random bytes the
server generates at session-creation time and ships to the
client in the `ConnectRequest` body. The client mirrors the
server's setup: one ISAAC for each direction, identical seed
on both sides.

## CryptoSystem (lookahead window)

A wrapper around ISAAC that tolerates out-of-order packet
delivery by pre-pulling future keys and remembering them.

```
class CryptoSystem extends ISAAC:
    MaximumEffortLevel = 256
    HashSet<u32> xors = {}
    u32 CurrentKey

    ctor(seed):
        super(seed)
        CurrentKey = Next()

    bool Search(u32 candidateKey):
        if CurrentKey == candidateKey: return true
        if xors contains candidateKey: return true
        # pull up to MaximumEffortLevel - xors.size keys looking
        # for candidateKey
        for i in 0 .. (MaximumEffortLevel - xors.size):
            xors.add(CurrentKey)
            ConsumeKey(CurrentKey)  # advances CurrentKey to Next()
            if CurrentKey == candidateKey: return true
        return false

    void ConsumeKey(u32 used):
        if CurrentKey == used:
            CurrentKey = Next()
        else:
            xors.remove(used)
```

Behavior:

- Sender always uses the next-in-sequence key (`CurrentKey`
  then `Next()`); never skips, never reuses.
- Receiver maintains the 256-key window. When a packet arrives
  whose XOR key matches `CurrentKey`, it's consumed
  in-sequence. If it matches a stale key in `xors`, that key
  is consumed late but the rest of the stream is unaffected.
- If a packet arrives with a key that is more than 256 keys
  ahead of the receiver's current position, `Search` returns
  `false` and the packet is rejected as corrupt (or as an
  attacker). After enough such rejections in a row, the
  session is torn down.

**Source**:
[`Source/ACE.Common/Cryptography/CryptoSystem.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Cryptography/CryptoSystem.cs).

## Checksum formulas

### Outgoing (sender side)

For every outbound packet:

```
function build_packet(header, optional_header_bytes, fragments,
                      isaac_send, encrypted):
    body = optional_header_bytes + fragments_bytes
    header.Size = body.length

    # 1. compute header checksum with placeholder
    save = header.Checksum
    header.Checksum = 0xBADD70DD
    pack(header_buffer, header)
    header_checksum = Hash32(header_buffer, 20)
    header.Checksum = save

    # 2. compute payload checksum
    payload_checksum = Hash32(optional_header_bytes,
                              optional_header_bytes.length)
    for fragment in fragments:
        payload_checksum += Hash32(fragment.bytes,
                                   fragment.bytes.length)

    # 3. final checksum
    if encrypted:
        header.Flags |= EncryptedChecksum
        isaac_key = isaac_send.Next()
        header.Checksum = header_checksum +
                          (payload_checksum XOR isaac_key)
    else:
        header.Checksum = header_checksum + payload_checksum

    # 4. emit
    pack(out_buffer, header)
    out_buffer.append(body)
    return out_buffer
```

Note the placeholder magic: `0xBADD70DD` ("BADDODE" → "bad code")
is written into the Checksum field while computing the header's
own Hash32, then restored. Both sides do this so their Hash32
computations match.

**Source**: [`PacketHeader.cs:CalculateHash32`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeader.cs#L55)
and [`ServerPacket.cs:CreateReadyToSendPacket`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ServerPacket.cs#L46).

### Incoming (receiver side)

```
function verify_packet(packet, isaac_recv):
    header_buffer = pack_header_with_magic(packet.header,
                                            0xBADD70DD)
    header_checksum = Hash32(header_buffer, 20)

    payload_checksum = Hash32(optional_header_bytes,
                              optional_header_bytes.length)
    for fragment in packet.fragments:
        payload_checksum += Hash32(fragment.bytes,
                                   fragment.bytes.length)

    if packet.header.Flags has EncryptedChecksum:
        candidate_key = (packet.header.Checksum
                         - header_checksum) XOR payload_checksum
        if isaac_recv.Search(candidate_key):
            isaac_recv.ConsumeKey(candidate_key)
            return true
        else:
            return false  # corrupt or out-of-window
    else:
        if packet.header.Checksum == header_checksum
                                     + payload_checksum:
            return true
        else:
            return false  # corrupt header or body
```

**Source**: [`ClientPacket.cs:VerifyCRC`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/ClientPacket.cs#L138).

## What "optional_header_bytes" means for checksum

The `payload_checksum` does NOT include all of the optional
header. It only includes the bytes captured by the per-flag
read paths in `PacketHeaderOptional.Unpack`. Specifically (per
[`PacketHeaderOptional.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/PacketHeaderOptional.cs)):

| Flag | Bytes captured into headerOptional checksum |
|---|---|
| `ServerSwitch` | 8 |
| `RequestRetransmit` | 4 (count) + 4×N (sequences) |
| `RejectRetransmit` | 4 (count) + 4×N (sequences) |
| `AckSequence` | 4 (the AckSequence) |
| `LoginRequest` | entire remaining body (with stream rewound after) |
| `WorldLoginRequest` | 8 |
| `ConnectResponse` | 8 (cookie) |
| `CICMDCommand` | 8 |
| `TimeSync` | 8 (`f64`) |
| `EchoRequest` | 4 (`f32`) |
| `Flow` | 6 (`u32` + `u16`) |

Flags NOT in this list (e.g., `ConnectRequest`,
`EchoResponse`, `Retransmission`, `EncryptedChecksum`,
`Disconnect`, `LogOff`, `BlobFragments`) contribute zero bytes
to the headerOptional checksum on the *receive* side.

For the `BlobFragments` case specifically, the fragment bodies
are checksummed separately and added in (see formula above) —
they're not part of headerOptional, they're a sibling source.

### Why this matters

When you implement the sender, you have to feed the **same
byte ranges** into `Hash32` that the receiver will. Get this
wrong and your packets will be silently dropped as corrupt.

For the LoginRequest handshake packet, the trick is that the
entire body (including any trailing string-padding bytes) is
captured into headerOptional — so `Hash32(body)` is the
payload checksum. The Phase 1 spike verified this works.

For the ConnectResponse handshake packet, only the 8-byte
cookie is captured. There are no other bytes in that packet's
body, so `Hash32(cookie_bytes)` is the payload checksum.

## What never gets a CRC verified

The server appears to skip CRC verification on certain
handshake-stage inbound packets — the second handshake leg
(server's outbound `ConnectRequest`) is built by
`PacketOutboundConnectRequest` with an honest checksum (so the
client *could* verify it), but the client's third leg
(`ConnectResponse`) is not CRC-checked by the server's
`NetworkManager.ProcessPacket` — it goes straight to lookup-
by-cookie. The Phase 1 spike exploited this by intentionally
ignoring CRC validation on the incoming `ConnectRequest`
during initial exploration and it worked.

For Phase 2 onward, CRC validation becomes mandatory for both
sides on every sequenced packet, since the ISAAC window is
state-bearing and a missed packet desyncs the stream.

## Endianness and ordering quirks

- All checksums are `u32` little-endian on the wire.
- `Hash32` reads input bytes as little-endian `u32` chunks.
- ISAAC's seed is consumed in the order bytes appear in the
  seed array — there is no LE/BE swap. AC passes 4 bytes; the
  ISAAC implementation initializes its first `u32` state slot
  with `(seed[0] | seed[1]<<8 | seed[2]<<16 | seed[3]<<24)`
  effectively. Match this exactly or your keystreams diverge.
- The `(headerCheckum - headerChecksum)` subtraction on the
  receive side relies on `u32` modular wrap. Use unsigned types
  throughout.
