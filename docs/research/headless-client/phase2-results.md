# Phase 2 results — encryption, CRC verification, keepalive

**Status**: in progress (sub-phases 2.1 + 2.2 + 2.3 PASS,
2.4 documentation roll-up in flight).

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
| 2.3 | Parse `CharacterList`, `ServerName`, `DDDInterrogation` payloads so a brain can pick a character | PASS |
| 2.4 | Roll-up documentation: CryptoSystem semantics, outbound packet wire format, keepalive timing, game-message wire formats | pending (in flight) |

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

Real game-message opcodes decoded out of the verified fragments
(opcode mapping verified against
[`GameMessageOpcode.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/GameMessageOpcode.cs)):

- `0xF7E5` `GameMessageDDDInterrogation` — fragment Queue=5 (`DatabaseQueue`)
- `0xF658` `GameMessageCharacterList`     — fragment Queue=9 (`UIQueue`), carries ASCII `"headless-test"` (the account name)
- `0xF7E1` `GameMessageServerName`        — fragment Queue=9 (`UIQueue`), carries ASCII `"ACEmulator"`

> ⚠ **Opcode mapping correction.** An earlier revision of this
> document and `spec/06-game-messages.md` listed these three opcodes
> with the wrong message names (rotated by one). The correct mapping
> above is the authoritative one and was verified by decoding live
> wire bytes against the source schemas. See `phase2-charlist-run-01.log`.

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

## Sub-phase 2.3 evidence — game-message parsing PASS

Spike run after `<this commit>` (the `Protocol/GameMessages/`
decoder + `HandshakeDriver.ObservePostHandshakePackets` integration).
Capture: `phase2-charlist-run-01.log` in the session-state files
folder.

Decoded output for the three messages the server pushes during
character-list delivery:

```
[observe] #2 ... Flags=EncryptedChecksum, BlobFragments Size=44 ... [CRC_OK]
[observe]   Frag Seq=0 Id=0x80000000 Count=1 Size=44 Idx=0 Q=5 payload[28]: e5 f7 00 00 ...
[observe]   -> DDDInterrogation: region=1 lang=1 product=1 supportedLangs=[0,1]

[observe] #3 ... Flags=EncryptedChecksum, BlobFragments Size=100 ... [CRC_OK]
[observe]   Frag Seq=1 Id=0x80000000 Count=1 Size=60 Idx=0 Q=9 payload[44]: 58 f6 00 00 ...
[observe]   -> CharacterList: account="headless-test" slots=11 characters=0 turbineChat=1 tod=1
[observe]   Frag Seq=2 Id=0x80000000 Count=1 Size=40 Idx=0 Q=9 payload[24]: e1 f7 00 00 ...
[observe]   -> ServerName: name="ACEmulator" connections=1/128
```

Verified facts about each message's wire format (full payload schemas
documented in `spec/06-game-messages.md`):

- All three game messages encode the opcode as `u32 LE` (4 bytes), not
  the `u16` you might expect — `GameMessage.cs:26` is the canonical
  authority.
- `WriteString16L` (the string encoding ACE uses for every short
  string) is `u16 length` + body bytes in Windows-1252/Latin-1 + zero
  padding so `(2 + length + pad) % 4 == 0`. Mirror is `ReadString16L`.
- The fragment-header `Size` field on the wire **includes the 16-byte
  fragment header itself**. So a 44-byte server fragment carries a
  28-byte payload. Source: `ServerPacketFragment.PackAndReturnHash32`.
- A `BlobFragments`-flagged packet may carry multiple fragments back-
  to-back (packet #3 above carries `CharacterList` + `ServerName` in
  one UDP datagram).
- Fragment `Id = 0x80000000` is the per-message id assigned by the
  server. The high bit is set because the server numbers its own
  messages starting at `0x80000000`; the client numbers its outbound
  messages starting at `0x00000001`. The split keeps the two streams
  independent.

### Three things we learned implementing 2.3

**1. The `CharacterList` payload contains the *account* name, not a
character name.** Source:
[`GameMessageCharacterList.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageCharacterList.cs)
writes `session.Account` (here `"headless-test"`) at offset 16. There
is no separate `AccountInfo` message — `CharacterList` is the only
authoritative source of the account name post-handshake. A
spike that mis-decodes this field could pick the wrong identity.

**2. The "slot count" is a per-account max, not a per-character
counter.** ACE's `max_chars_per_account` config (default 11) appears
verbatim in every `CharacterList` push, regardless of how many
characters exist. The `characters.Count` is a separate field. Two
zero-length character lists at different slot counts are normal.

**3. Fragment `Queue` selects the inbound delivery semantics, NOT the
game-message reliability class.** Server-side, every game message we
observed was sent reliably (`AddPacketTail` in `NetworkSession.cs`
followed the `Reliable*` path). The `Queue` value (`5 =
DatabaseQueue`, `9 = UIQueue`) is the per-stream ordering bucket on
the *send* side — the receiver doesn't have to interpret it; it just
needs to ack the packet sequence. So our spike currently ignores it
on the parse side and only logs it for debugging.

## Sub-phase 2.4 evidence — documentation roll-up

This document covers Phase 2 end-to-end. Companion files:

- `spec/03-crypto.md` — CryptoSystem (ISAAC) port + CRC chain
- `spec/02-network.md` — Header.Id asymmetry + `Sequence=0` control rule
- `spec/04-handshake.md` — ConnectResponse race + retransmit + ClientId
- `spec/06-game-messages.md` — verified per-opcode wire formats
- `spec/07-outbound-packet.md` — outbound packet wire format + checksum chain
  (TODO if not yet written)

### Operational caveat — back-to-back reconnects

Reconnecting on the same account inside the server's session-expiry
window (~17s after the last packet of the previous session) triggers
ACE's duplicate-login handling: the new session boots the old one
("Account was logged in, booting currently connected account in
favor of new connection") and then the second connection itself can
be dropped ("Account In Use: Found another session already logged in
for this account"). Reproducible: run the spike, exit, run it again
within ~10s.

Symptoms client-side: server pushes a single `BlobFragments` carrying
opcode `0xF659` (`CharacterError`) before dropping the link. To avoid
this during dev: wait ~30s between runs, exit cleanly via a logoff
packet (Phase 3+ TODO), or use a different account per run.

## What unblocks now that Phase 2 PASSes

Picking a character from the parsed `CharacterList` lets the client
emit `GameActionCharacterEnterWorldRequest` (opcode `0xF7C8`, C → S).
That is the first packet the spike will need to actually encrypt
(it's a `BlobFragments`-carrying packet, so `EncryptedChecksum` is
mandatory), which exercises the `cryptoSend` ISAAC instance for the
first time. The server replies with `CharacterEnterWorld` (opcode
`0xF657`, S → C) once it has hydrated the character. Phase 3 picks
up from there.

Note: the `headless-test` account currently has zero characters.
Either run `/createchar` via an ACE admin session before Phase 3 test
runs, or implement `GameMessageCharacterCreate` (opcode `0xF656`,
C → S) first so the spike is self-sufficient.
