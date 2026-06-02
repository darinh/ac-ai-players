# Phase 4 — world-state decoders

Goal: turn the post-`0xF657 CharacterEnterWorld` firehose of game
messages into typed records the bot can reason about. Phase 3.3
(PASS) confirmed the firehose arrives; this phase turns the raw
`0xFxxx (no decoder yet)` log lines into named records.

**Status: COMPLETE (all sub-phases PASS).** Final firehose
verification: [`phase4-state-speech-run-01.log`](phase4-state-speech-run-01.log)
— 616 packets over ~120s, **CRC pass=616 / fail=0**, zero
fall-through to "no decoder yet" for any opcode the server emitted
in the observation window.

## Decoder delivery order (actual)

Originally drafted as 4.1→4.6 in the order shown in early commits
of this doc; the ObjectCreate keystone forced a reshuffle. Actual
sequencing of the merged commits:

| Sub-phase | Opcode | Name | Commit |
|---|---|---|---|
| 4.1 | `0xF746` | `PlayerCreate` | `527d86c` |
| 4.2 | `0xF7E0` | `ServerMessage` | `527d86c` |
| 3.4 | `0x00A1` | (C→S) `GameActionLoginComplete` | `6cfae55` |
| 4.3 | `0xF745` | `ObjectCreate` (header + Model + Physics + Weenie) | `0e7276c` |
| 4.5 | `0xF7B0` | `GameEvent` envelope | `2f9b577` |
| 4.7 | `0x02CD` | `PrivateUpdatePropertyInt` | `442be8d` |
| 4.8 | `0xF748` | `UpdatePosition` | `f34d869` |
| 4.6 | `0xF74C` | `Motion` (`MovementEvent`) header | `a1727ca` |
| 4.9 | `0xF74B` | `SetState` | `7d16c85` |
| 4.10 | `0x02BB` | `HearSpeech` | `7d16c85` |

(`0xF659 CharacterError` was decoded earlier in Phase 3.2 as part
of the EnterWorld validation path — counted separately.)

## 4.1 — PlayerCreate (PASS)

**Wire layout**: `u32 opcode + u32 guid`, 8 bytes total. Group
`SmartboxQueue` (6). Encoder:
`Source/ACE.Server/Network/GameMessages/Messages/GameMessagePlayerCreate.cs`
(a single `WriteGuid(guid)` call after the base ctor writes the
opcode).

Decoder: `GameMessageDecoder.DecodePlayerCreate`. Record:
`PlayerCreateMessage(uint Guid)`.

**Evidence** (`phase4-decoders-run-01.log`):

```
[observe]   -> PlayerCreate: guid=0x50000006
```

Matches the `Headless01` character GUID the spike sent in the
preceding `0xF657 CharacterEnterWorld` commit. Fired exactly once
per run — confirms the one-shot semantics.

## 4.2 — ServerMessage (PASS)

**Wire layout**: `u32 opcode + string16L text + i32 chatMessageType`.
Group `UIQueue` (9). Encoder:
`Source/ACE.Server/Network/GameMessages/Messages/GameMessageSystemChat.cs`
(two writes: `WriteString16L(message)` then
`Write((int)chatMessageType)`).

Decoder: `GameMessageDecoder.DecodeServerMessage`. Reuses
`AcStrings.ReadString16L` (handles the 1-byte-per-char Latin-1
body and the align-to-4 pad before reading the trailing i32).
Record: `ServerMessageMessage(string Text, int ChatMessageType)`.

**Evidence** (`phase4-decoders-run-01.log`):

```
[observe]   -> ServerMessage(chatType=0x0): "Welcome to Asheron's Call..."
```

ChatMessageType `0x00` = `Broadcast` (verified against
`ACE.Entity/Enum/ChatMessageType.cs`; full enum table in
[spec/06](spec/06-game-messages.md)).

## 3.4 — GameActionLoginComplete (PASS, hidden prerequisite for 4.3+)

Not strictly a Phase 4 decoder but landed in the middle of 4.3
debugging. The spike was reaching the world but staying as a
purple-haze portal effect — never "loading complete" from the
server's POV — which meant a swath of post-EnterWorld messages
were never sent. Sending C→S `0x00A1 GameActionLoginComplete`
(empty body) clears the server-side `Teleporting=true` flag and
unblocks the rest of the firehose.

Encoder reference: `Source/ACE.Server/Network/Handlers/GameActionLoginComplete.cs`.

The spike now sends this exactly once, ~1.5s after
`CharacterEnterWorldServerReady` is received (delay tuned to let
the initial ObjectCreate burst land first).

**Evidence**: `phase4-logincomplete-run-02.log` reaches 614 packets
without the server timing the session out, vs ~220 in pre-fix
runs. The `loginComplete=True` flag in the final summary line is
the spike's own bookkeeping confirming the message was sent.

## 4.3 — ObjectCreate (PASS — keystone)

The largest decoder by far. `0xF745 ObjectCreate` accounts for the
"what is in my vision range?" stream: avatar, equipment, items,
NPCs, doors, decorative props. The encoder is flag-driven with
three flag enums (`WeenieHeaderFlag`, `WeenieHeaderFlag2`,
`PhysicsDescriptionFlag`) and two complex sub-payloads
(`SerializeModelData`, `SerializePhysicsData`).

**Wire schema**: see [spec/07-world-state.md](spec/07-world-state.md)
for the full byte-by-byte spec. Top-level structure:

```
u32 opcode = 0xF745
u32 guid
ModelData    block  (visual)
PhysicsData  block  (position, sequences, optional movement)
WeenieHeader block  (gameplay properties, flag-gated)
Align()
```

Decoder: `GameMessageDecoder.DecodeObjectCreate` + records
`ObjectCreateMessage` / `ObjectModelData` / `ObjectPhysicsData` /
`ObjectWeenieHeader` in
`experiments/headless-client/src/HeadlessAcClient/Protocol/GameMessages/ObjectCreateMessage.cs`.

Implementation strategy (from spec/07 §"Phase 4.3 implementation
strategy") landed as drafted: shared `MessageReader` primitive
(origin-aware cursor with `Align4` against the absolute offset)
+ five staged sub-decoders. Movement body left opaque inside the
PhysicsData section; full decode deferred to Phase 4.6 / future
Motion body work.

**Evidence** (`phase4-objectcreate-run-13.log`, the first capture
where every ObjectCreate in the run decoded cleanly):

```
-> ObjectCreate: guid=0x50000006 wcid=1 itemType=0x10 name="Headless01" wFlags=0x00800036/0x00000000 pFlags=0x019803 lb=0x860201AD xyz=(12.3,-28.5,0.0)
-> ObjectCreate: guid=0x8000024A wcid=31000 itemType=0x800 name="Blackmoor's Favor" wFlags=0x10497090/0x00000006 pFlags=0x021801
-> ObjectCreate: guid=0x80000249 wcid=115  itemType=0x2   name="Leather Boots"     wFlags=0x00278018/0x00000000 pFlags=0x021801
-> ObjectCreate: guid=0x80000248 wcid=2604 itemType=0x4   name="Wide Breeches"     wFlags=0x00278018/0x00000000 pFlags=0x021801
-> ObjectCreate: guid=0x80000247 wcid=130  itemType=0x4   name="Shirt"             wFlags=0x00278018/0x00000000 pFlags=0x021801
-> ObjectCreate: guid=0x80000246 wcid=118  itemType=0x4   name="Cap"               wFlags=0x10278018/0x00000000 pFlags=0x021801
-> ObjectCreate: guid=0x800001D1 wcid=30991 itemType=0x10 name="Society Greeter"   wFlags=0x00900036/0x00000000 pFlags=0x018803 lb=0x860201AD xyz=(9.8,-31.7,0.0)
-> ObjectCreate: guid=0x800001FE wcid=5090 itemType=0x20 name="Bruised Apple"     wFlags=0x00213010/0x00000000 pFlags=0x029805 lb=0x860201AD xyz=(7.7,-30.1,0.9)
```

The capture confirms the spike sees its own avatar, its starting
inventory (cap / shirt / breeches / leather boots) and starter
quest item ("Blackmoor's Favor"), the academy greeter NPC, and
nearby pickups ("Bruised Apple"). Subsequent runs see 30+ objects
in the same observation window with 100% decode.

**Key wire-format findings captured to spec/07**:

- `Align()` aligns against the absolute origin offset (start of
  message including the 4-byte opcode), NOT the offset within a
  given section. The decoder's `MessageReader.Align4()` must use
  the absolute cursor.
- `WeenieHeaderFlag` conditional fields must be walked in
  ENCODER order (not bit-numeric order) — re-ordering by bit
  number desynchronizes the cursor.
- `HouseRestrictions` (`0x04000000` in `WeenieHeaderFlag`) is a
  variable-length `RestrictionDB` blob the spike intentionally
  refuses to decode (fail-fast → returns `null`). House objects
  don't exist in the academy where the spike runs.
- Movement body (when `PhysicsDescriptionFlag & Movement` is set)
  is length-prefixed AND followed by a trailing `u32 isAutonomous`
  that is NOT included in the length prefix. Easy to over-read.

## 4.5 — GameEvent envelope (PASS)

The 16-byte common header for all `0xF7B0` events. Per-event
payload kept opaque at this layer; subtype decoders are queued
for Phase 5.

**Wire layout**:

```
u32 opcode = 0xF7B0
u32 receiverGuid       (session.Player.Guid, or 0 when logged out)
u32 serverEventSequence (auto-increment per server-side GameEventSequence)
u32 eventType           (GameEventType enum)
... event-specific payload (length varies; preserved as raw bytes)
```

Encoder reference: `Source/ACE.Server/Network/GameEvent/GameEventMessage.cs:14-26`.

Decoder: `GameMessageDecoder.DecodeGameEvent`. Record:
`GameEventMessage(uint ReceiverGuid, uint ServerEventSequence,
GameEventType EventType, ReadOnlyMemory<byte> PayloadBytes)`.
`GameEventType` enum cloned verbatim from
`Source/ACE.Server/Network/GameEvent/GameEventType.cs` into
`Protocol/GameMessages/GameEventType.cs`.

**Evidence** (`phase4-gameevent-run-01.log`):

```
-> GameEvent: type=PlayerDescription (0x0013) recv=0x50000006 seq=1 payload[668]
-> GameEvent: type=CharacterTitle    (0x0029) recv=0x50000006 seq=2 payload[16]
-> GameEvent: type=FriendsListUpdate (0x0021) recv=0x50000006 seq=3 payload[8]
-> GameEvent: type=WeenieError       (0x028A) recv=0x50000006 seq=4 payload[4]
-> GameEvent: type=WeenieErrorWithString (0x028B) recv=0x50000006 seq=5 payload[16]
-> GameEvent: type=SetTurbineChatChannels (0x0295) recv=0x50000006 seq=6 payload[40]
```

The login burst is the highest-value source of game state we have
not yet decoded; `PlayerDescription` (668 bytes) carries character
sheet data and is the obvious first Phase 5 target.

## 4.7 — PrivateUpdatePropertyInt (PASS)

Server tells the client that an int-valued WorldObject property
changed (e.g. `CurrentHealth`, `Level`, `Coinage`, `Age`).
"Private" = only visible to the receiving session, vs. the
broadcast variant `0x019B PublicUpdatePropertyInt`.

**Wire layout** (13 bytes, packed, no padding):

```
u32 opcode = 0x02CD
u8  sequence    (ByteSequence — single-byte auto-increment for ordering)
u32 property    (PropertyInt enum; underlying ushort promoted to u32 by writer)
i32 value
```

Group: `UIQueue`. Encoder:
`Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdatePropertyInt.cs`.

Decoder: `GameMessageDecoder.DecodePrivateUpdatePropertyInt`.
Record: `PrivateUpdatePropertyIntMessage(byte Sequence,
uint Property, int Value)` with a small hand-picked
`KnownProperties` dictionary to pretty-print common entries
(`Age`, `Level`, `Coinage`, `EncumbranceVal`, etc.).

**Evidence** (`phase4-state-speech-run-01.log` ticker tail):

```
-> PrivateUpdatePropertyInt: Age = 3473 (seq=0)
-> PrivateUpdatePropertyInt: Age = 3480 (seq=1)
-> PrivateUpdatePropertyInt: Age = 3487 (seq=2)
... (one every ~7s, monotone increasing seq + value)
```

**CRITICAL FINDING** captured in the message file header: the
1-byte sequence is shared by the ENTIRE PrivateUpdateProperty* /
PublicUpdateProperty* family. Easy to mis-decode as `u32`. See
the doc-comment block at the top of `PrivateUpdatePropertyIntMessage.cs`
for the full size table (Bool=13, Int=13, Float=17, Int64=17,
String=variable, Public* variants add a `u32 sender guid`).

## 4.8 — UpdatePosition (PASS)

The highest-volume world-state message: one per visible moving
object per tick. In the academy, dominated by Pilot-01
(`0x50000005`) doing idle wander.

**Wire layout** (variable, 44-68 bytes):

```
u32 opcode = 0xF748
u32 guid
u32 flags       (PositionFlags - see below)
u32 cellId      (Origin.CellID, "landblock")
f32 pos.x, pos.y, pos.z         (always)
f32 rot.w   only if !OrientationHasNoW
f32 rot.x   only if !OrientationHasNoX
f32 rot.y   only if !OrientationHasNoY
f32 rot.z   only if !OrientationHasNoZ
f32 vel.x, vel.y, vel.z   only if HasVelocity
u32 placementId           only if HasPlacementID
u16 instanceSequence       (UShortSequence)
u16 positionSequence       (UShortSequence)
u16 teleportSequence       (UShortSequence)
u16 forcePositionSequence  (UShortSequence)
```

Encoder reference:
`Source/ACE.Server/Network/GameMessages/Messages/GameMessageUpdatePosition.cs`
+ `Source/ACE.Server/Network/Structure/PositionPack.cs`.

`PositionFlags` (uint, [Flags]):

| value | name | meaning |
|---|---|---|
| `0x01` | `HasVelocity` | Velocity vector present |
| `0x02` | `HasPlacementID` | u32 placementId present |
| `0x04` | `IsGrounded` | Object grounded (no z-velocity) |
| `0x08` | `OrientationHasNoW` | rot.W component OMITTED |
| `0x10` | `OrientationHasNoX` | rot.X component OMITTED |
| `0x20` | `OrientationHasNoY` | rot.Y component OMITTED |
| `0x40` | `OrientationHasNoZ` | rot.Z component OMITTED |

**INVERSE-PRESENCE TRAP**: the orientation flags use the opposite
convention from the velocity / placement flags. `HasVelocity SET`
means "velocity is present", but `OrientationHasNoW SET` means
"rot.W is ABSENT" (the server omits zero components as a
compression). When reconstructing the quaternion, default missing
components to `0.0`.

Decoder: `GameMessageDecoder.DecodeUpdatePosition`. Record:
`UpdatePositionMessage(uint Guid, PositionFlags Flags, uint CellId,
Vector3 Position, Quaternion Rotation, Vector3? Velocity,
uint? PlacementId, ushort InstanceSequence, ushort PositionSequence,
ushort TeleportSequence, ushort ForcePositionSequence)`.

**Evidence** (`phase4-updateposition-run-01.log` tail):

```
-> UpdatePosition: guid=0x50000005 lb=0x860201DE xyz=(25.48,-30.00,0.00) rot=(0.707,0.000,0.000,-0.707) flags=0x34 seq=(inst=4,pos=38561,tp=0,fp=0)
-> UpdatePosition: guid=0x50000005 lb=0x860201DE xyz=(25.01,-30.00,0.00) rot=(0.707,0.000,0.000,-0.707) flags=0x34 seq=(inst=4,pos=38563,tp=0,fp=0)
```

`flags=0x34` = `IsGrounded | OrientationHasNoY | OrientationHasNoZ`,
which decoded with W=0.707, X=0.000, then Y and Z defaulted to 0,
then the trailing-Z replaced by the second present component
(W and X). 348 UpdatePosition messages decoded in the
`phase4-state-speech-run-01.log` window (largest single source).

## 4.6 — Motion / `MovementEvent` (PASS, header only)

Per-tick broadcast of a `WorldObject`'s movement intent. Wraps a
`MovementData` payload whose body shape depends on `MovementType`.
Phase 4.6 decodes the header; the polymorphic body
(`MovementInvalid` / `MoveToObject` / `MoveToPosition` /
`TurnToObject` / `TurnToHeading`) is preserved as raw bytes for
later phases.

**Wire layout** (variable):

```
u32 opcode = 0xF74C
u32 guid
u16 instanceSequence   (UShortSequence: ObjectInstance)
u16 movementSequence   (UShortSequence: ObjectMovement)
u16 serverControlSeq   (UShortSequence: ObjectServerControl)
u8  isAutonomous       (0 = server-initiated, 1 = client)
PAD to next 4-byte boundary  -- absolute-stream-length alignment
u8  movementType       (MovementType enum)
u8  motionFlags        (MotionFlags [Flags] enum)
u16 currentStyle       (MotionStance, written as ushort)
... body (polymorphic on movementType, kept as raw bytes)
```

**ALIGNMENT TRAP**: `writer.Align()` in ACE pads the stream
LENGTH to the next multiple of 4, NOT the current write position.
After the 4-byte opcode + 4-byte guid + (2+2+2)-byte sequence trio
+ 1-byte `isAutonomous`, the stream is at length 15. `Align()`
writes 1 pad byte to land at 16 before `movementType`. Forgetting
this pad shifts the rest of the header read by 1 byte and
corrupts everything downstream. This is the single most expensive
finding of Phase 4.

Encoder reference:
`Source/ACE.Server/Network/GameMessages/Messages/GameMessageUpdateMotion.cs`
+ `Source/ACE.Server/Network/Structure/MovementData.cs`.
`Align()` impl: `Source/ACE.Server/Network/Extensions.cs:54-63`.

Decoder: `GameMessageDecoder.DecodeMotion`. Record:
`MotionMessage(uint Guid, ushort InstanceSequence, ushort MovementSequence,
ushort ServerControlSequence, bool IsAutonomous,
MovementType MovementType, MotionFlags MotionFlags,
ushort CurrentStyle, byte[] BodyBytes)`.

`MovementType` enum (verbatim from
`Source/ACE.Entity/Enum/MovementType.cs`):

| value | name |
|---|---|
| `0x0` | `Invalid` |
| `0x1` | `RawCommand` |
| `0x2` | `InterpretedCommand` |
| `0x3` | `StopRawCommand` |
| `0x4` | `StopInterpretedCommand` |
| `0x5` | `StopCompletely` |
| `0x6` | `MoveToObject` |
| `0x7` | `MoveToPosition` |
| `0x8` | `TurnToObject` |
| `0x9` | `TurnToHeading` |

`MotionFlags [Flags]`:

| bit | name |
|---|---|
| `0x1` | `StickToObject` |
| `0x2` | `StandingLongJump` |

**Evidence** (`phase4-motion-run-01.log`, 119 Motion messages
decoded; distribution `TurnToObject:94, Invalid:19, MoveToObject:6`):

```
-> Motion: guid=0x50000005 type=TurnToObject flags=None style=0x003D autonomous=False seq=(inst=4,mov=3178,srv=3178) body[20]
-> Motion: guid=0x50000005 type=Invalid      flags=None style=0x003D autonomous=False seq=(inst=4,mov=3180,srv=3180) body[12]
```

Style `0x003D` = `NonCombat` (from MotionStance enum); the bot
sees Pilot-01 idling and turning toward Headless01 (target
`0x50000006`).

## 4.9 — SetState (PASS)

Broadcast updated `PhysicsState` bitfield for a WorldObject + the
two sequences needed for ordering validation.

**Wire layout** (fixed 16 bytes):

```
u32 opcode = 0xF74B
u32 guid
u32 state            (PhysicsState [Flags] bitfield - kept as raw u32)
u16 instanceSequence (ObjectInstance)
u16 stateSequence    (ObjectState)
```

Encoder reference:
`Source/ACE.Server/Network/GameMessages/Messages/GameMessageSetState.cs`.
`PhysicsState` enum at `Source/ACE.Entity/Enum/PhysicsState.cs`
(not yet enumerated in the spike — value preserved as raw u32 for
future per-bit interpretation).

Decoder: `GameMessageDecoder.DecodeSetState`. Record:
`SetStateMessage(uint Guid, uint State, ushort InstanceSequence,
ushort StateSequence)`.

**Evidence** (`phase4-state-speech-run-01.log`):

```
-> SetState: guid=0x50000006 state=0x00400408 seq=(inst=23,state=1)
-> SetState: guid=0x7860202C state=0x0001001C seq=(inst=0,state=355)
-> SetState: guid=0x7860202D state=0x00010018 seq=(inst=0,state=327)
```

12 SetState messages decoded in the final firehose run. The first
of those (`0x50000006`) updates our own avatar's physics state on
EnterWorld; subsequent ones (`0x7860202C`, `0x7860202D`) are
door / static-object state changes in the academy lobby.

## 4.10 — HearSpeech (PASS)

Chat-text broadcast received by the player — both NPC dialogue
AND other players' (and bots') chat in /say range.

**Wire layout** (variable):

```
u32       opcode = 0x02BB
String16L messageText      (u16 length + Latin-1 bytes + align-to-4 pad)
String16L senderName       (same encoding)
u32       senderId
u32       chatMessageType  (ChatMessageType enum; see spec/06)
```

Encoder reference:
`Source/ACE.Server/Network/GameMessages/Messages/GameMessageHearSpeech.cs`.
Sibling `0x02BC HearRangedSpeech` is identical with an extra
`f32 range` field between `senderId` and `chatMessageType` — not
yet observed; deferred.

Decoder: `GameMessageDecoder.DecodeHearSpeech`. Record:
`HearSpeechMessage(string Message, string SenderName,
uint SenderId, uint ChatMessageType)`.

**Evidence** (`phase4-state-speech-run-01.log`):

```
-> HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "lol nice hat {p}"
-> HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "oh hey Headless01"
-> HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "anyone selling a good bow? {p}"
-> HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "where is the best xp around here?"
-> HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "i need a bag, anyone got one?"
-> HearSpeech: <Pilot-01> (0x50000005, chatType=0x2): "wow this place is huge"
```

`chatType=0x02` = `Speech` (local /say). The Pilot-01 BotPlayer
is broadcasting scripted NPC-style chat from a HeartBeat emote
chain; the headless client receiving and decoding that chat is
the first hop toward closing the brain's perception loop.

## Validation gate (full Phase 4)

| signal | observed | expected |
|---|---|---|
| Build clean | yes | 0 warnings, 0 errors |
| Phase 3.3 still PASS | yes | EnterWorld two-step intact |
| `PlayerCreate` decoded | yes | guid matches the EnterWorld commit guid |
| `ServerMessage` decoded | yes | valid `ChatMessageType` |
| `ObjectCreate` decoded | yes | 30 objects in final firehose; names match academy contents |
| `GameEvent` decoded (envelope) | yes | login burst surfaces 10 distinct event types |
| `PrivateUpdatePropertyInt` decoded | yes | `Age` ticker monotone increasing |
| `UpdatePosition` decoded | yes | 348 positions; inverse-presence quaternion correct |
| `Motion` decoded (header) | yes | TurnToObject targets Headless01 from Pilot-01 |
| `SetState` decoded | yes | 12 state transitions; our avatar's first |
| `HearSpeech` decoded | yes | 7 chat lines from Pilot-01 readable |
| `0xFxxx (no decoder yet)` fall-through | **0** | zero in `phase4-state-speech-run-01.log` |
| Packet CRC pass rate | 616 / 616 | 100% |

## Files touched (Phase 4 total)

**New record types** (under
`experiments/headless-client/src/HeadlessAcClient/Protocol/GameMessages/`):

- `PlayerCreateMessage.cs` (4.1)
- `ServerMessageMessage.cs` (4.2)
- `ObjectCreateMessage.cs` + `ObjectCreateFlags.cs` (4.3)
- `MessageReader.cs` (4.3 — shared cursor primitive used by all
  subsequent decoders)
- `GameEventMessage.cs` + `GameEventType.cs` (4.5)
- `PrivateUpdatePropertyIntMessage.cs` (4.7)
- `UpdatePositionMessage.cs` (4.8)
- `MotionMessage.cs` (4.6)
- `SetStateMessage.cs` (4.9)
- `HearSpeechMessage.cs` (4.10)
- `CharacterErrorMessage.cs` (carried over from 3.2)

**Modified each commit**:

- `Protocol/GameMessages/GameMessageOpcode.cs` — opcode constants
  registered as decoders shipped.
- `Protocol/GameMessages/GameMessageDecoder.cs` — central dispatch,
  one `Decode*` method per opcode.
- `Handshake/HandshakeDriver.cs` — log lines surfacing each decoded
  record into the per-run observation log.

**Specification updates**:

- `docs/research/headless-client/spec/06-game-messages.md` —
  verified wire schemas for all post-2.x S→C opcodes plus
  ServerMessage, GameEvent envelope, SetState, HearSpeech,
  PrivateUpdatePropertyInt.
- `docs/research/headless-client/spec/07-world-state.md` —
  promoted from DRAFT to VERIFIED; ObjectCreate spec confirmed
  against the shipped decoder; UpdatePosition + Motion-header
  schemas added.

**Capture logs** (canonical evidence, committed alongside their
sub-phases):

- `phase4-decoders-run-01.log` — 4.1 + 4.2
- `phase4-objectcreate-run-13.log` — 4.3 final clean run
- `phase4-gameevent-run-01.log` — 4.5
- `phase4-propertyint-run-02.log` — 4.7
- `phase4-updateposition-run-01.log` — 4.8
- `phase4-motion-run-01.log` — 4.6
- `phase4-state-speech-run-01.log` — 4.9 + 4.10 + full firehose
  validation (ZERO undecoded)
- `phase4-logincomplete-run-02.log` — 3.4 (loginComplete prerequisite)

## See also

- [`phase3-results.md`](phase3-results.md) — preceding phase
  (encrypted outbound → CharacterCreate → EnterWorld two-step).
- [`spec/06-game-messages.md`](spec/06-game-messages.md) — verified
  game-message wire schemas (envelope, character-mgmt, chat, etc.).
- [`spec/07-world-state.md`](spec/07-world-state.md) — verified
  world-state schemas (ObjectCreate, UpdatePosition, Motion header).
- [`spec/08-outbound-packet.md`](spec/08-outbound-packet.md) —
  outbound packet framing + checksum chain.
- Plan: `plan.md` "Phase 5" section lists the next directions
  (world-state model, self-motion, polymorphic Motion body,
  GameEvent subtype decoders).
