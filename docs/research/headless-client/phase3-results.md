# Phase 3 results — encrypted outbound, character bootstrap, world entry

**Status**: 3.1 PASS. 3.2 PASS. 3.3 PASS. 3.4 documentation
rolling up as each sub-phase lands.

Phase 3 takes the spike from "observer that can keep a session
alive" ([Phase 2](phase2-results.md)) to "active player that can
create a character and enter the world."

## Sub-phases

| ID | Goal | Status |
|---|---|---|
| 3.1 | Send an encrypted `BlobFragments` packet that the server accepts (CRC valid, ISAAC in sync, sequence rules right). Use an unknown opcode so dispatch can't side-effect game state. | PASS |
| 3.2 | Send a real `CharacterCreate` (opcode `0xF656`) so the spike bootstraps its own character on the `headless-test` account. | PASS |
| 3.3 | Two-step world-entry handshake: send `CharacterEnterWorldRequest` (opcode `0xF7C8`), receive `CharacterEnterWorldServerReady` (`0xF7DF`), commit with `CharacterEnterWorld` (`0xF657`), observe the world-state firehose. | PASS |
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

## Sub-phase 3.2 evidence — `CharacterCreate` PASS

Run after the Phase 3.2 commit:
[`phase3-charcreate-run-01.log`](phase3-charcreate-run-01.log).

The packet sent:

```
encrypted BlobFragments(
    queue   = 0x09 (UIQueue),
    pktSeq  = 2,                 // first sequenced C->S after CharList
    fragSeq = 1,                 // first per-message fragment sequence
    payload = 420 bytes:
       u32      opcode = 0xF656
       str16L   account = "headless-test"
       u32      unknown = 1
       u32      heritage = 1 (Aluvian)
       u32      gender   = 1 (Male)
       Appearance (104 B) - all zeros
       i32      templateOption = 0
       u32×6    attributes = {10, 10, 10, 10, 10, 10}
       u32      slot = 0, u32 classId = 0
       u32      numSkills = 55
       u32×55   skillAdvancementClass = 0 (Inactive)
       str16L   name = "Headless01"
       u32      startArea  = 0
       u32      isAdmin    = 0
       u32      isSentinel = 0
)
total wire bytes: 460  (16 header + 16 frag header + 8 BlobFragments header + 420 payload)
```

### Acceptance signals (all observed)

1. `[selfcheck] OutboundPacket round-trip OK` — the new
   `RunCharacterCreateRoundTrip` self-check mirror-decodes every
   field through `BinaryReader`-style cursors and asserts
   field-by-field equality, catching any alignment bug before the
   bytes hit the wire.

2. **Server replied with `CharacterCreateResponse(Ok)` in 1 packet
   round-trip**:
   ```
   [observe] #4 ... BlobFragments Size=44 ... [CRC_OK]
     Frag Seq=3 Id=0x80000000 Count=1 Size=44 Idx=0 Q=9 payload[28]:
       43 f6 00 00 01 00 00 00 06 00 00 50 0a 00 48 65 61 64 6c 65 73 73 30 31 00 00 00 00
     -> CharacterCreateResponse: Ok guid=0x50000006 name="Headless01"
   ```
   Payload decoded byte-by-byte: opcode `0xF643`, response `Ok=1`,
   guid `0x50000006`, length-prefix `0x000A=10`, name `Headless01`,
   trailing `0x00000000`. Matches the conditional schema in
   `GameMessageCharacterCreateResponse.cs:8-19`.

3. **Character persisted in Shard DB** (`ace_shard.character`):
   ```
   id          name         account_id   is_deleted   delete_time
   1342177286  Headless01   6            0            0
   ```
   `id = 0x50000006` matches the guid returned on the wire. Account
   id `6` is the `headless-test` account in `ace_auth.account`.

4. **Session stayed alive** — 35 packets observed, 35/35 CRC pass,
   0 fail, full 65s observation window. After the create,
   the server emitted 10× `AckSequence=2` packets confirming our
   create packet was accepted, then 3× `TimeSync` keepalive cycles
   went through cleanly.

5. **No exceptions in `C:\ACE\Logs\ACE_Log.txt`** during the
   create window. AuthenticationHandler logged the connect; no
   CharacterHandler or PlayerFactory ERROR/WARN lines fired.

### What this unlocks

- The spike can self-bootstrap a character. Phase 3.3 can now use
  the returned guid `0x50000006` directly in
  `CharacterEnterWorldRequest` — no out-of-band `/createchar` or
  manual SQL is needed.

- This is also the first concrete proof that our outbound
  `BlobFragments` carrying a **real, dispatched** game message
  works end-to-end: payload framing, queue routing
  (`UIQueue=0x09`), ISAAC keystream sync, sequence rules, and the
  server's response decoder are all aligned with our packer.

- The CharacterCreate response decoder in `GameMessageDecoder`
  correctly handles the conditional body (per rubber-duck
  recommendation): non-Ok responses do NOT eagerly read the optional
  `guid + name + trailing` fields that the server only emits on Ok.

### Design notes captured during 3.2

- **String round-trip works without padding bugs.** The
  `string16L` writer (u16 length + ASCII + 4-byte pad) emits the
  correct trailing zeros: `account = "headless-test"` (13 chars,
  pad 1 → 16B total) and `name = "Headless01"` (10 chars, pad 0
  → 12B total). The server-side reader unwound them at the
  expected offsets.

- **All-10s attributes pass `ValidateAttributeCredits`.** Sum =
  60, well under Aluvian's `AttributeCredits = 290`. Rubber-duck
  was right to push for this over the boundary case.

- **All-zero `Appearance` indices are safe.** Server's
  `sex.GetEyeTexture(0)` etc. did not throw — DAT files have
  valid index-0 entries for every Aluvian Male appearance field.
  The open question from Phase 3.2 planning is now resolved.

- **Account string from session, not hardcoded.** We pull
  `charList.Account` from the inbound CharacterList rather than
  re-using the CLI `account` argument. Matches the server's
  `session.Account` check in `CharacterHandler.cs:31-32`.

- **Payload size = 420B**, well under the single-fragment cap
  of 448B (29-byte name would still fit). The 448B runtime guard
  in `HandshakeDriver` would catch any future field added that
  pushes us over.

- **Re-run note**: `Headless01` now exists in the Shard DB. A
  second run with the same name would yield
  `CharacterCreateResponse(NameInUse=3)`. The spike's decoder
  handles this path; future runs that need a fresh character
  should bump the name suffix or use a different test account.

### Files touched in 3.2

- `Protocol/GameMessages/CharacterCreateMessage.cs` (new) — packer
  with `Options` record, `Pack(Span<byte>, Options) -> int`,
  `MeasurePackedSize(Options)`. Hard runtime guard on
  `RequiredSkillCount=55` (silent session termination if wrong).
- `Protocol/GameMessages/CharacterCreateResponseMessage.cs` (new)
  — record + `CharacterCreateResponse` enum mirroring
  `CharacterGenerationVerificationResponse`.
- `Protocol/GameMessages/GameMessageOpcode.cs` — added 5 opcodes
  for Phase 3.2/3.3.
- `Protocol/GameMessages/GameMessageGroup.cs` (new) — enum mirror;
  `UIQueue=0x09` is the queue character-management messages use.
- `Protocol/GameMessages/GameMessageDecoder.cs` — added
  conditional `DecodeCharacterCreateResponse` branch.
- `Protocol/OutboundSelfCheck.cs` — added
  `RunCharacterCreateRoundTrip` that mirrors the server's reader.
- `Handshake/HandshakeDriver.cs` — replaced Phase 3.1 0xFFFE
  probe with CharacterCreate send block; gated on
  `charList.Characters.Count == 0 && !characterCreateSent`; added
  `ObserveResult.CharacterCreateResponse` field.
- `HandshakeResult` record + `Program.cs` PASS banner now print
  Phase 3.2 status.

## Sub-phase 3.3 evidence — `CharacterEnterWorld` two-step handshake PASS

Run after the Phase 3.3 commit:
[`phase3-enterworld-run-01.log`](phase3-enterworld-run-01.log).

The real flow is **two messages from the client and one ack from
the server before any world state moves.** The handshake is split
this way (vs. a single "enter world" command) because the server
treats the request as a probe — it lets the client know if the
world is open BEFORE the client commits to a particular character
GUID. Once the client gets the ready signal, it sends the commit
message carrying both the chosen GUID and the account name; the
server validates ownership and only then transitions the session
to `WorldConnected` and starts streaming world state.

### Wire flow

```
C → S: BlobFragments
         opcode = 0xF7C8  CharacterEnterWorldRequest
         payload  = 4 bytes (opcode only - no body)
         queue   = 0x09  UIQueue
         pktSeq  = 2     (first non-zero outbound; 1 was used by 3.2's create)
         fragSeq = 1
         total bytes on wire = 44

S → C: BlobFragments
         opcode = 0xF7DF  CharacterEnterWorldServerReady
         payload  = 4 bytes (opcode only - no body)
         queue   = 0x09  UIQueue

C → S: BlobFragments
         opcode = 0xF657  CharacterEnterWorld
         body    = u32 guid (0x50000006) + string16L account ("headless-test", padded to 16)
         payload = 24 bytes total
         queue   = 0x09  UIQueue
         pktSeq  = 3
         fragSeq = 2
         total bytes on wire = 64

S → C: (world-state firehose - see "post-EnterWorld stream" below)
```

### Acceptance signals (all five required, all five observed)

1. **Self-check round-trip PASS** for both new packers at startup:
   ```
   [selfcheck] OutboundPacket round-trip ...
   [selfcheck] OutboundPacket round-trip OK
   ```
   `RunCharacterEnterWorldRequestRoundTrip` and
   `RunCharacterEnterWorldRoundTrip` mirror the server's read path
   byte-by-byte through `BinaryPrimitives` + `AcStrings.ReadString16L`.

2. **`CharacterEnterWorldRequest` accepted by the server** —
   no CharacterError 0xF659 came back; instead the server replied
   with the documented 0xF7DF:
   ```
   [observe]   -> CharacterEnterWorldServerReady (server ready, send 0xF657)
   ```

3. **`CharacterEnterWorld` validated** — account string matched
   `session.Account`, character GUID `0x50000006` was in
   `session.Characters`, and the character was not flagged
   deleted/in-world/Olthoi-disabled. No CharacterError, transition
   to `WorldConnected` succeeded.

4. **World-state firehose started** — the moment the server
   committed the entry, the per-tick stream began. Captured opcode
   distribution from the 65-second window (221 packets, 100% CRC
   pass):

   | Opcode | Name | Role | Observations |
   |---|---|---|---|
   | `0xF7B0` | `GameEvent` | Login-completion bring-up | initial burst of ~12 packets |
   | `0xF7E0` | `ServerMessage` | Welcome / system text | 1 packet shortly after entry |
   | `0xF746` | `PlayerCreate` | The player avatar materialised | 1 packet, carries our GUID |
   | `0xF745` | `ObjectCreate` | World objects entering visibility | dominant volume — every static/dynamic object the player can see |
   | `0xF74C` | `Motion` / `MovementEvent` | Per-player motion updates | repeats ~once/sec; payload contains our GUID `06 00 00 50` |
   | `0x02CD` | `PrivateUpdatePropertyInt` | Server pushing private int properties to client | periodic |

5. **Session stayed alive** the full 65-second observation window
   without disconnect or duplicate-login bounce. Final summary:
   ```
   [observe] total packets observed: 221 (CRC pass=221, fail=0)
   [observe] sent: 192 acks, 3 timesync echoes, characterCreate=False, enterWorldRequest=True, enterWorld=True
   [observe] CharacterEnterWorldServerReady received
   [main] PHASE 3.3 PASS — EnterWorld two-step handshake committed (guid=0x50000006); world-state firehose should follow.
   ```
   (`characterCreate=False` is expected on this run because
   `Headless01` was already created in the Phase 3.2 run, so the
   spike correctly skipped re-create and used the existing GUID.)

### Design notes

- **The 0xF7C8 request is opcode-only.** This is the protocol's
  "Hey, may I enter?" probe. The handler reads NOTHING from the
  payload — it just checks `ServerManager.ShutdownInProgress` and
  `WorldManager.WorldStatus == Open`. We could put garbage after
  the opcode and it would still succeed; we pack exactly 4 bytes
  to be the smallest legal payload.
- **0xF7DF ServerReady is also opcode-only.** Our decoder returns
  an empty marker record (`CharacterEnterWorldServerReadyMessage()`)
  so the state machine can pattern-match without parsing zero
  bytes.
- **The 0xF657 commit message MUST use `string16L`, not raw chars.**
  `CharacterHandler.cs:202-204` calls `payload.ReadString16L()`. The
  account name follows the same padding rule documented in
  `spec/06-game-messages.md` (pad to 4-multiple after the
  `u16 length + chars`).
- **Re-run path uses an existing GUID.** The state machine prefers
  `createResponse.CharacterGuid` (Phase 3.2 path) and falls back
  to `charList.Characters[0].Id` (re-run path). Both produce the
  same value on the test account because `Headless01` is the only
  character. On re-runs the spike correctly skips CharacterCreate
  (would return `NameInUse=3`) and goes straight to EnterWorld.
- **No retry on `EnterWorldRequest`.** We send it exactly once
  after CharacterList arrives. If the server is shutting down it
  replies with `CharacterError(LogonServerFull=0x0F)` and our
  decoder logs it; the spike does not auto-retry because the
  failure modes are operator-actionable (server restart) not
  packet-loss-recoverable.
- **The server's "you're in" message is implicit.** There is no
  single ack packet — entry is confirmed by the world-state
  firehose beginning. The spike treats `EnterWorldServerReady`
  arriving + `EnterWorldSent` being true as PASS; observation of
  follow-up `0xF74C/F745/F746` packets is the secondary
  confirmation logged for Phase 4 planning.

### Files touched

- New: `Protocol/GameMessages/CharacterEnterWorldMessages.cs`
  (both `CharacterEnterWorldRequestMessage` and
  `CharacterEnterWorldMessage` packers).
- New: `Protocol/GameMessages/CharacterEnterWorldServerReadyMessage.cs`
  (empty marker record).
- New: `Protocol/GameMessages/CharacterErrorMessage.cs` (record
  carrying the `u32` error code).
- Edited: `Protocol/GameMessages/GameMessageDecoder.cs` — added
  branches for `CharacterEnterWorldServerReady` (no body) and
  `CharacterError`.
- Edited: `Protocol/OutboundSelfCheck.cs` — added
  `RunCharacterEnterWorldRequestRoundTrip` and
  `RunCharacterEnterWorldRoundTrip` mirroring server read paths.
- Edited: `Handshake/HandshakeDriver.cs` — two-step state machine
  layered after the CharacterCreate send; `ObserveResult` +
  `HandshakeResult` extended with five new fields
  (`EnterWorldRequestSent`, `EnterWorldServerReady`,
  `EnterWorldSent`, `LastCharacterError`, `ChosenCharacterGuid`).
- Edited: `Program.cs` — Phase 3.3 PASS / PARTIAL banners take
  priority over the Phase 3.2 ones.

## Sub-phase 3.4 (in flight) — documentation

Done as part of 3.1:
- [`spec/08-outbound-packet.md`](spec/08-outbound-packet.md):
  packet structure, sequence rules, per-fragment cap, checksum
  formula, ISAAC seed wiring, end-to-end evidence pointer.
- Spec README updated to list `08`.

Done as part of 3.3:
- `spec/06-game-messages.md`: C→S payload schemas for
  `CharacterEnterWorldRequest` (`0xF7C8`, empty body) and
  `CharacterEnterWorld` (`0xF657`, guid + account); S→C schemas
  for `CharacterEnterWorldServerReady` (`0xF7DF`, empty body) and
  `CharacterError` (`0xF659`, u32 error code) with full enum
  list.

To add as later phases land:
- `spec/07-world-state.md`: object schema, landblock/cell
  coordinate encoding, inventory layout. Will be driven by Phase 4
  decoders for `0xF745`, `0xF746`, `0xF74C`, `0xF7B0`.
