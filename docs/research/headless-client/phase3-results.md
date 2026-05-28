# Phase 3 results — encrypted outbound, character bootstrap, world entry

**Status**: 3.1 PASS. 3.2 and 3.3 pending. 3.4 documentation
rolling up as each sub-phase lands.

Phase 3 takes the spike from "observer that can keep a session
alive" ([Phase 2](phase2-results.md)) to "active player that can
create a character and enter the world."

## Sub-phases

| ID | Goal | Status |
|---|---|---|
| 3.1 | Send an encrypted `BlobFragments` packet that the server accepts (CRC valid, ISAAC in sync, sequence rules right). Use an unknown opcode so dispatch can't side-effect game state. | PASS |
| 3.2 | Send a real `CharacterCreate` (opcode `0xF656`) so the spike bootstraps its own character on the `headless-test` account. | pending |
| 3.3 | Send `CharacterEnterWorldRequest` (opcode `0xF7C8`) and observe the resulting world-state stream. | pending |
| 3.4 | Doc roll-up: `spec/08-outbound-packet.md` (done), expand `spec/06-game-messages.md` with C→S payloads, expand `spec/07-world-state.md` as world packets are decoded. | in flight |

## Sub-phase 3.1 evidence — encrypted outbound PASS

Run after the Phase 3.1 commit:
[`phase3-probe-run-01.log`](phase3-probe-run-01.log).

The probe packet:

```
encrypted BlobFragments(
    opcode    = 0xFFFE,       // unhandled; safe acceptance test
    pktSeq    = 2,            // first non-zero outbound sequence
    fragSeq   = 1,            // per-message sequence (first one)
    queue     = 0x09,         // UIQueue, matches real char-mgmt msgs
    payload   = 4 bytes (the LE u32 opcode only),
    bytes_on_wire = 44
)
```

### Acceptance signals (all four required, all four observed)

1. **Self-check round-trip PASS** at startup:
   ```
   [selfcheck] OutboundPacket round-trip OK
   ```
   Six patterns verified: plain optional-only, plain
   single-fragment, plain multi-fragment, plain optional +
   fragment, encrypted single-fragment, and size-guard
   throws (< 4-byte and > 448-byte fragments). See
   [`OutboundSelfCheck.cs`](../../experiments/headless-client/src/HeadlessAcClient/Protocol/OutboundSelfCheck.cs).

2. **Server `AckSequence(2)` flood** immediately following the
   probe (log lines `#4` – `#13`):
   ```
   [observe] #4  Flags=AckSequence  optional: AckSequence=2
   [observe] #5  Flags=AckSequence  optional: AckSequence=2
   ... (10× total in ~20 ms)
   ```
   `AckSequence=2` is the smoking gun. It proves the server:
   - validated the outer header CRC,
   - decrypted the body CRC using ISAAC keystream,
   - accepted `Sequence = 2`,
   - advanced `lastReceivedPacketSequence` from 1 → 2.

3. **Server log entry** in `C:\ACE\Logs\ACE_Log.txt`:
   ```
   2026-05-28 09:29:41,436 [12] WARN
     (ACE.Server.Network.Managers.InboundMessageManager)
     Received unhandled fragment opcode: 0xFFFE - 65534
   ```
   This proves the inbound fragment reassembled correctly and
   reached the per-opcode dispatch table. The dispatch missed
   (by design), the server logged + continued, and we kept the
   session alive.

4. **No session drop, no CRC regression.** The observation
   window ran the full 65 s; final tally:
   ```
   [observe] total packets observed: 34 (CRC pass=34, fail=0)
   [observe] sent: 5 acks, 3 timesync echoes
   [main] PHASE 2 PASS — handshake + crypto + keepalive +
                        game-message decode all working.
   ```

### What this unlocks

With encrypted outbound proven, every subsequent C→S game
message rides the same `OutboundPacket.AddBlobFragment` +
`Pack(encrypt: true, cryptoSend)` path. The protocol-level
work for Phase 3 is the per-message payload schemas, not the
transport.

### Design notes captured during 3.1

The Phase 3.1 design was reviewed by a rubber-duck pass before
implementation. Two BLOCKING issues caught early:

1. **Per-fragment cap is 464 bytes (data ≤ 448), not 1024.**
   Initial design assumed `MaxPacketSize` was the per-fragment
   cap. Fixed by reading `PacketFragment.cs:6-7` and
   `ClientPacketFragment.cs:17`. Now enforced at runtime in
   `OutboundPacket.AddBlobFragment` (throws on violation).
2. **First non-zero outbound `Sequence` must be `2`, not `1`.**
   `NetworkSession.cs:57` initializes `lastReceivedPacketSequence
   = 1`. Lines `362-367` reject `Sequence <= last`. We had been
   planning to send `Sequence = 1`, which would have been
   silently dropped — the worst possible failure mode.

Both blockers are now codified in
[`spec/08-outbound-packet.md`](spec/08-outbound-packet.md)
"Sequence rules" so future contributors don't re-discover them.

A handful of non-blocking findings from the same review were
adopted: `unchecked` arithmetic on all checksum sums, sub-4-byte
fragment guard (server silently drops AND fails to advance
per-message sequence — `NetworkSession.cs:545-546`), and
single-writer outbound emission (only `ObservePostHandshakePackets`
sends after handshake completes).

## Sub-phase 3.2 (pending) — `CharacterCreate`

Goal: spike sends a `GameMessageCharacterCreate` (opcode
`0xF656`, queue `UIQueue=0x09`) and observes the server's
`GameMessageCharacterCreateResponse`. On success the test
account `headless-test` ends a session with 1 character;
on subsequent reconnect the `CharacterList` reflects it.

Server-side schema (read-only reference):
`Source/ACE.Server/Network/Handlers/CharacterHandler.cs:26-175`.

This is the first message where wrong payload bytes have
observable game-state consequences. The spike will round-trip
the request through `OutboundSelfCheck` style verification
before sending it on the wire.

## Sub-phase 3.3 (pending) — `CharacterEnterWorldRequest`

Goal: spike sends opcode `0xF7C8` (NOT `0xF657` — that's the
server's reply). Payload per `CharacterHandler.cs:184-196`:

```
u32     character GUID
string16L  account name (matches the session account)
```

On accept, server replies with
`GameMessageCharacterEnterWorldServerReady` (opcode TBD —
verify against `GameMessageOpcode.cs` before sending) and
the per-tick world-state firehose starts. That stream
populates the world-state model documented in
`spec/07-world-state.md`.

## Sub-phase 3.4 (in flight) — documentation

Done as part of 3.1:
- [`spec/08-outbound-packet.md`](spec/08-outbound-packet.md):
  packet structure, sequence rules, per-fragment cap, checksum
  formula, ISAAC seed wiring, end-to-end evidence pointer.
- Spec README updated to list `08`.

To add as 3.2/3.3 land:
- `spec/06-game-messages.md`: C→S payload schema for each new
  opcode (`CharacterCreate`, `CharacterEnterWorldRequest`),
  with field-by-field tables and the server-handler line cite.
- `spec/07-world-state.md`: object schema, landblock/cell
  coordinate encoding, inventory layout.
