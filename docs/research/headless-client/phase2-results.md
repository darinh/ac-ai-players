# Phase 2 results — encryption, CRC verification, keepalive

**Status**: in progress (sub-phases 2.1 + 2.2 PASS, 2.3 CharacterList
parsing pending, 2.4 documentation roll-up pending).

Phase 2 builds on the [Phase 1 handshake](phase1-results.md) by
porting the ISAAC stream cipher + Hash32 CRC verification chain and
wiring the bare-keepalive (`AckSequence` + `TimeSync` echo) outbound
path. This is the minimum surface area required to keep an ACE
session alive past the 17-second un-authenticated timeout.

## Sub-phases

| ID | Goal | Status |
|---|---|---|
| 2.1 | Port `CryptoSystem` (ISAAC + 256-key lookahead) and verify every inbound packet's CRC | PASS |
| 2.2 | Build outbound packet path; send bare `AckSequence` + `TimeSync` echo; survive past 60s | PASS |
| 2.3 | Parse `CharacterList` (opcode `0xF7E5`) payload so a brain can pick a character | pending |
| 2.4 | Roll-up documentation: CryptoSystem semantics, outbound packet wire format, keepalive timing | pending (in flight) |

## Sub-phase 2.1 evidence — CRC verification PASS

Spike run after `072bc06` (the CryptoSystem + InboundPacket.VerifyCRC
commit). Observed 11 inbound packets across a 30-second window. All
11 verified, zero CRC failures.

```
[observe] #1  Seq=2 Flags=EncryptedChecksum, TimeSync           Size=8   CRC=0x9ADD9316  [CRC_OK]
[observe] #2  Seq=3 Flags=EncryptedChecksum, BlobFragments      Size=44  CRC=0x26A174E7  [CRC_OK]
[observe] #3  Seq=4 Flags=EncryptedChecksum, BlobFragments      Size=100 CRC=0x8B4F5929  [CRC_OK]
[observe] #4-11 Seq=4 Flags=AckSequence (server retry pattern)             [CRC_OK × 8]
[observe] total post-handshake packets observed: 11 (CRC pass=11, fail=0)
```

Real game-message opcodes decoded out of the verified fragments:

- `0xF7E5` `GameMessageCharacterList`  — fragment Queue=5
- `0xF658` `GameMessageServerName`     — fragment Queue=9, ASCII `"headless-test"`
- `0xF7E1` `GameMessageDDDInterrogation` — fragment Queue=9, ASCII `"ACEmulator"`

Implementation notes (full detail in `spec/03-crypto.md`):

- For encrypted inbound packets, the recovered XOR key is
  `(Header.Checksum - headerChecksum) XOR payloadChecksum`. The
  receiver then calls `cryptoRecv.Search(key)` (walks the keystream
  forward up to 256 slots, caching every step) and `ConsumeKey(key)`.
- `payloadChecksum = optionalChecksum + fragmentChecksum` where
  `optionalChecksum = Hash32(captured-bytes-in-flag-order)` and
  `fragmentChecksum = Σ_per_frag(Hash32(16-byte fragHeader) + Hash32(fragData))`.
- The flag-order capture is server-defined and asymmetric: inbound
  packets omit `LoginRequest`/`WorldLoginRequest`/`ConnectResponse`/
  `CICMDCommand` blocks (those are client-to-server only). Mirror
  exactly or every checksum fails.

## Sub-phase 2.2 evidence — keepalive PASS

Spike run after `e29de19` (OutboundPacket + ack/timesync wiring +
the `Header.Id` fix). Observation window extended to 65s. The
session stayed alive for the full window with zero `Network Timeout`
in the server log (`C:\ACE\Logs\ACE_Log.txt`).

Server-side confirmation:

```
2026-05-28 08:55:53,349 [26] DEBUG  NetworkManager.Login Request from 127.0.0.1:49652
2026-05-28 08:55:53,349 [26] DEBUG  NetworkManager.Creating new session for 127.0.0.1:49652 with id 0
2026-05-28 08:55:53,383 [44] INFO   AuthHandler.client headless-test connected with verified password
# ... 65s elapse, client exits voluntarily ...
# NO "Session 0\127.0.0.1:49652 dropped" entry.
```

Compare to every prior run, which timed out at exactly +17s:

```
2026-05-28 08:48:26,113 connected.
2026-05-28 08:48:43,086 dropped. Reason: Network Timeout    # +17s
2026-05-28 08:52:56,724 connected.
2026-05-28 08:53:13,699 dropped. Reason: Network Timeout    # +17s
```

Client-side observed traffic over the 65s window:

| Direction | Count | Notes |
|---|---|---|
| S→C TimeSync (Seq=2,5,6) | 3 | Server pushes every 20s as expected (`NetworkSession.timeBetweenTimeSync = 20s`) |
| S→C BlobFragments (Seq=3,4) | 2 | CharacterList, ServerName, DDDInterrogation |
| S→C bare AckSequence | 29 | Server reminders, `optional.AckSequence = 1` (= `lastReceivedPacketSequence` default; see "Why the server's AckSequence value stays at 1" below) |
| C→S bare AckSequence | 5 | One per sequenced inbound packet |
| C→S bare TimeSync echo | 3 | One per inbound TimeSync |

### Three things we learned implementing 2.2

**1. `Header.Id` is not symmetric.** The server's outbound packets
carry `Header.Id = ServerId` (a process-wide constant, `11` on the
typical ACE deployment). The client's outbound packets must instead
carry the client's session-map index — which the server delivers as
the `NetID` field inside the `ConnectRequest` **body** (NOT the
header). Wrong client `Id` → server's
[`NetworkManager.ProcessPacket`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Managers/NetworkManager.cs#L147-L156)
silently drops the packet. For the first session created on a fresh
server the index is `0`, which is also the default `Id` value — so
the bug is invisible until a second concurrent session exists. See
`spec/02-network.md` "Header.Id is not symmetric" callout.

**2. Bare-control packets use `Sequence = 0`, not `Sequence = 1`.**
[`NetworkSession.FlushPackets`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/NetworkSession.cs#L736-L745)
shows that bumping the per-stream sequence counter from `0` to `1`
is gated on `EncryptedChecksum`-flagged packets. Bare `AckSequence`
also skips the bump (line 742-745, "If we are only ACKing, then we
don't seem to have to increment the sequence"). The server's
"duplicate sequence" detector at line 362-367 explicitly whitelists
both `Sequence == 0` and the bare-`AckSequence`-only case. So a
client can keep a session alive forever using nothing but
`Sequence=0` unencrypted control packets.

**3. The server completely ignores incoming `TimeSync` content.**
[`NetworkSession.HandlePacket`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/NetworkSession.cs#L470-L477)
carries the comment "`Do something with this... I don't know what to
do with these when we receive them at this point.`" — the only side
effect of receiving a `TimeSync` from the client is the standard
`TimeoutTick` refresh that any valid packet would trigger. So our
echo doesn't need a real client-side clock — echoing back the value
the server just sent works fine.

### Why the server's `AckSequence` value stays at `1`

The server-side `lastReceivedPacketSequence` field is initialised to
`1` (not `0`) at
[`NetworkSession.cs:57`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/NetworkSession.cs#L57)
and only ever updated on inbound packets that have BOTH
`Sequence != 0` AND `Flags != AckSequence`
([line 495](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/NetworkSession.cs#L495-L496)).
Our Phase 2.2 client sends bare control at `Sequence=0`, which never
trips that condition — so the server keeps reporting
`optional.AckSequence = 1` in its bare-ack reminders forever. This
is cosmetic: the session lives because every valid inbound refreshes
the timeout tick, regardless of whether `lastReceivedPacketSequence`
advances. Real game traffic (Phase 3, sending `BlobFragments` from
the client) will naturally bump the value once the client emits its
first encrypted sequenced packet.

## What unblocks once 2.3 lands

Picking a character from the parsed `CharacterList` lets the client
emit `GameActionCharacterEnterWorld`. That is the first packet the
spike will need to actually encrypt (it's a `BlobFragments`-carrying
packet, so `EncryptedChecksum` is mandatory), which exercises the
`cryptoSend` ISAAC instance for the first time. Phase 3 picks up
from there.
