# Phase 4 — world-state decoders

Goal: decode the messages the server pushes after `0xF657
CharacterEnterWorld` so the bot has a structured view of its avatar
and surroundings. Phase 3.3 (PASS) confirmed the firehose arrives;
this phase turns the raw `0xFxxx (no decoder yet)` log lines into
typed records.

Status: 4.1 PASS, 4.2 PASS, 4.3 deferred (see "ObjectCreate keystone"
below).

## Decoder priority order

Driven by the 65-second firehose captured in Phase 3.3
(`phase3-enterworld-run-01.log`, 221 packets, 100% CRC pass):

| opcode | name | first-touch in Phase 4 | reasoning |
|---|---|---|---|
| `0xF746` | `PlayerCreate` | **4.1 (this doc)** | One-shot, 8 bytes. Smallest possible scope to prove the decoder wiring. |
| `0xF7E0` | `ServerMessage` | **4.2 (this doc)** | string16L + i32 — exercises the variable-length read path. |
| `0xF745` | `ObjectCreate` | **4.3 (deferred)** | Highest volume but flag-driven serializer with ~50 conditional fields. Requires a spec draft + rubber-duck pass before implementation. |
| `0xF74C` | `Motion` | 4.4 | Per-tick movement. Needs `MovementData` shape investigation. |
| `0x02CD` | `PrivateUpdatePropertyInt` | 4.5 | Stat/state changes. ~13 byte body. |
| `0xF7B0` | `GameEvent` | 4.6 | Dispatch envelope — sub-opcode driven. Needs its own opcode table. |

## 4.1 — PlayerCreate (PASS)

**Wire layout**: `u32 opcode + u32 guid`, 8 bytes total.

Encoder: `Source/ACE.Server/Network/GameMessages/Messages/GameMessagePlayerCreate.cs`
(a single `WriteGuid(guid)` call).

Decoder added to `GameMessageDecoder.Decode`. Record type:
`PlayerCreateMessage(uint Guid)`.

**Evidence** (`phase4-decoders-run-01.log`):

```
[observe]   -> PlayerCreate: guid=0x50000006
```

Matches the `Headless01` character GUID the spike sent in the
preceding `0xF657 CharacterEnterWorld` commit. Fired exactly once
in the 60-second window — confirms the one-shot semantics.

## 4.2 — ServerMessage (PASS)

**Wire layout**: `u32 opcode + string16L text + i32 chatMessageType`.

Encoder: `Source/ACE.Server/Network/GameMessages/Messages/GameMessageSystemChat.cs`
(two writes: `WriteString16L(message)` then `Write((int)chatMessageType)`).

Decoder reuses `AcStrings.ReadString16L` (handles the 1-byte-per-char
Latin-1 body and the align-to-4 pad before reading the trailing i32).
Record type: `ServerMessageMessage(string Text, int ChatMessageType)`.

**Evidence** (`phase4-decoders-run-01.log`):

```
[observe]   -> ServerMessage(chatType=0x0): "Welcome to Asheron's Call
```

ChatMessageType `0x00` = `Broadcast` (verified against
`ACE.Entity/Enum/ChatMessageType.cs` — see spec/06). Welcome banner
arrives from the post-EnterWorld dispatcher.

## 4.3 — ObjectCreate (DEFERRED)

`0xF745 ObjectCreate` accounts for the bulk of the post-EnterWorld
traffic. Decoding it is the keystone for "what does the bot see?"
but the encoder is a flag-driven beast with ~50 conditional fields
across three flag enums (`WeenieHeaderFlag`, `WeenieHeaderFlag2`,
`ObjectDescriptionFlag`) plus two complex sub-payloads
(`SerializeModelData`, `SerializePhysicsData`).

Before writing code we will:

1. Read `WorldObject_Networking.cs:56-300+` in full (the header
   serializer) plus `SerializeModelData` and `SerializePhysicsData`.
2. Port the three flag enums into `experiments/headless-client`
   (read-only consumers; we never emit ObjectCreate from the client).
3. Draft `spec/07-world-state.md` with the full byte-by-byte layout
   and a list of which conditional fields apply to which object
   archetypes (player vs creature vs static vs container).
4. Run a rubber-duck pre-implementation review.
5. Decode in two passes: fixed-prefix header first (guid, flags,
   name, WCID, IconId, itemType, objDescFlags), then conditional
   fields. The model/physics sub-payloads can land in 4.3.3 once
   the header is stable.

## Validation gate (Phase 4.1+4.2)

| signal | observed | expected |
|---|---|---|
| Build clean | yes | 0 warnings, 0 errors |
| Phase 3.3 still PASS | yes | EnterWorld two-step intact |
| `PlayerCreate` decoded | yes | guid matches the EnterWorld commit guid |
| `ServerMessage` decoded | yes | non-empty text, valid `ChatMessageType` |
| Packet CRC | 219 pass / 0 fail | 100% pass |
| Capture log committed | `phase4-decoders-run-01.log` | yes |

## Files touched (Phase 4.1+4.2)

- `experiments/headless-client/src/HeadlessAcClient/Protocol/GameMessages/PlayerCreateMessage.cs` — new record type.
- `experiments/headless-client/src/HeadlessAcClient/Protocol/GameMessages/ServerMessageMessage.cs` — new record type.
- `experiments/headless-client/src/HeadlessAcClient/Protocol/GameMessages/GameMessageOpcode.cs` — added 0xF746, 0xF7E0, plus Phase 4 opcode placeholders we will need (`ObjectCreate`, `Motion`, `GameEvent`, `PrivateUpdatePropertyInt`).
- `experiments/headless-client/src/HeadlessAcClient/Protocol/GameMessages/GameMessageDecoder.cs` — switch arms + `DecodePlayerCreate` / `DecodeServerMessage`.
- `experiments/headless-client/src/HeadlessAcClient/Handshake/HandshakeDriver.cs` — log lines for the new record types.
- `docs/research/headless-client/spec/06-game-messages.md` — verified wire schemas for both opcodes + corrected `ChatMessageType` enum table.
- `docs/research/headless-client/phase4-decoders-run-01.log` — canonical capture (219 packets).
