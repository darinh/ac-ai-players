# 07 — World state

Wire schemas for the messages the server pushes after
`0xF657 CharacterEnterWorld` to keep the client's local model of
"what's around me" up to date. The bot must consume these to know
where it is, what its avatar looks like, what objects exist nearby,
and how those objects move.

Status: **VERIFIED** for `0xF745 ObjectCreate` (Phase 4.3 PASS),
`0xF748 UpdatePosition` (Phase 4.8 PASS), and `0xF74C Motion`
header (Phase 4.6 PASS — body kept as raw bytes, full body decode
deferred to Phase 5). The post-EnterWorld firehose has been
observed end-to-end with zero fall-through to the "no decoder yet"
log line in [`phase4-state-speech-run-01.log`](../phase4-state-speech-run-01.log).

## Decoder priority

From the post-EnterWorld firehose. Counts are from the final
verified run, `phase4-state-speech-run-01.log` (~120s observation
window, 616 packets, 100% CRC pass, ZERO fall-through):

| opcode | name | count | status | section |
|---|---|---|---|---|
| `0xF748` | `UpdatePosition` | 348 | ✅ Phase 4.8 PASS | [§UpdatePosition](#0xf748-updateposition-s--c) |
| `0xF74C` | `Motion` | 121 | ✅ Phase 4.6 PASS (header; body raw) | [§Motion](#0xf74c-motion-s--c-header-only) |
| `0xF745` | `ObjectCreate` | 30 | ✅ Phase 4.3 PASS | [§ObjectCreate](#0xf745-objectcreate-s--c) |
| `0xF7B0` | `GameEvent` | 10 | ✅ Phase 4.5 PASS (envelope; payload raw) | [spec/06 §GameEvent envelope](06-game-messages.md) |
| `0x02CD` | `PrivateUpdatePropertyInt` | 26 | ✅ Phase 4.7 PASS | [spec/06 §PrivateUpdatePropertyInt](06-game-messages.md) |
| `0xF74B` | `SetState` | 12 | ✅ Phase 4.9 PASS | [spec/06 §SetState](06-game-messages.md) |

Note: the lower-volume `0x02xx` messages (`PrivateUpdateProperty*`,
`SetState`, etc.) are documented in [`spec/06-game-messages.md`](06-game-messages.md)
since their schemas are small and fit the per-message wire-format
section there. World-state-heavy opcodes (`ObjectCreate`,
`UpdatePosition`, `Motion`) live here because their schemas
dominate the bot's model of "what's around me".

## Helper primitives (shared by all schemas)

All wire fields little-endian unless noted.

### `WriteGuid` / `ReadGuid`
Always `u32`. `ObjectGuid.Full` is a single 32-bit packed value.

### `WriteString16L` / `ReadString16L`
- `u16 length` — character count.
- `length` bytes of Latin-1 / Windows-1252 (single byte per char).
- Zero-fill so `(2 + length)` is rounded up to a multiple of 4.

(See `experiments/headless-client/src/HeadlessAcClient/Protocol/AcStrings.cs:171`
for the verified reader.)

### `WritePackedDword` / `ReadPackedDword`
Variable-length DWORD compression. Source:
`Source/ACE.Server/Network/Extensions.cs:23-35`.

Writer:
- If `value <= 32767`: write `u16` of value.
- Else: write `u32` = `(value << 16) | ((value >> 16) | 0x8000)`.
  In little-endian byte order, the first 2 bytes encode
  `(value >> 16) | 0x8000` and the next 2 encode `value & 0xFFFF`.

Reader (what we must implement):

```
u16 first = read_u16_le()
if (first & 0x8000) == 0:
    return first                # single-word, 2 bytes consumed
high16 = first & 0x7FFF
low16  = read_u16_le()          # 4 bytes total consumed
return (high16 << 16) | low16
```

The function consumes **2 or 4 bytes** depending on the value
range. It does NOT pad to 4. After reading a small packed dword
the cursor may be 2-byte aligned, not 4-byte aligned — the decoder
must track the exact cursor position, not assume alignment.

### `WritePackedDwordOfKnownType` / `ReadPackedDwordOfKnownType`
Used for ID fields where the high byte is a known "type" prefix
(e.g. `0x6000000` for icon resources, `0x4000000` for palettes,
`0x5000000` for textures, `0x1000000` for animations).

Writer: subtract `type` from `value` if the type bit is set, then
`WritePackedDword(value)`.

Reader: `ReadPackedDword()` then re-add `type` for downstream
consumers (the value `0` should remain `0` — that means "no
resource"; verify against captured data).

### `Align()`
Writer pads with zero bytes so the absolute stream position
(`BaseStream.Length`, includes the opcode) is a multiple of 4.
Reader skips bytes so its position matches.

`Align()` is called explicitly inside the encoders — it is NOT
implicit after every field. The decoder must follow the same
explicit Align points. See per-section schemas below.

### `Vector3`
Three consecutive `f32`. 12 bytes. No padding.

### `Quaternion`
Four consecutive `f32` (W, X, Y, Z order — verified
`Source/ACE.Entity/Position.cs:372-375`). 16 bytes.

## 0xF745 ObjectCreate (S → C)

**Top-level layout**:

```
u32 opcode              = 0xF745
[ ObjectCreatePayload ] = SerializeCreateObject body (variable)
```

The body is produced by `WorldObject.SerializeCreateObject`
(`Source/ACE.Server/WorldObjects/WorldObject_Networking.cs:34, 56-221`).
It has five sections in this order:

1. `u32 guid`
2. **ModelData** block (visual appearance)
3. **PhysicsData** block (position / movement / sequences)
4. **WeenieHeader** block (gameplay properties)
5. Final `Align()`

### Section 2 — ModelData

Reference encoder: `SerializeModelData` (lines 223-256).

```
u8   marker                = 0x11   (constant)
u8   subPaletteCount       = N
u8   textureChangeCount    = T
u8   animPartChangeCount   = A
if N > 0:
    packedDwordOfKnownType paletteID, 0x4000000
repeat N times:
    packedDwordOfKnownType subPaletteId, 0x4000000
    u8 offset
    u8 length
repeat T times:
    u8                     partIndex
    packedDwordOfKnownType oldTexture, 0x5000000
    packedDwordOfKnownType newTexture, 0x5000000
repeat A times:
    u8                     index
    packedDwordOfKnownType animationId, 0x1000000
Align()
```

Notes:
- The leading `0x11` is a magic version byte. We MUST verify it on
  decode (mismatch indicates wrong section).
- All three counts are `u8`, so each ≤ 255.

### Section 3 — PhysicsData

Reference encoder: `SerializePhysicsData` (lines 282-422).

```
u32 physicsDescriptionFlag   (PhysicsDescriptionFlag bitfield)
u32 physicsState             (PhysicsState bitfield)

if (physicsDescriptionFlag & Movement) != 0:
    u32 movementLength = N
    N bytes opaque movement body  (see "Movement body" below)
    if N > 0:
        u32 isAutonomous           (0/1)
else if (physicsDescriptionFlag & AnimationFrame) != 0:
    u32 placement              (Placement enum)

if Position:                u32 landblock + f32 x,y,z + f32 w,x,y,z   (32 bytes)
if MTable:                  u32 motionTableId
if STable:                  u32 soundTableId
if PeTable:                 u32 physicsTableId
if CSetup:                  u32 setupTableId
if Parent:                  u32 wielderId + u32 parentLocation
if Children:
    u32 childCount = K
    repeat K times: u32 guid + u32 locationId
if ObjScale:                f32
if Friction:                f32
if Elasticity:              f32
if Translucency:            f32
if Velocity:                Vector3   (12 bytes)
if Acceleration:            Vector3
if Omega:                   Vector3
if DefaultScript:           u32
if DefaultScriptIntensity:  f32

# Always present, regardless of flags:
u32 seq_ObjectPosition
u32 seq_ObjectMovement
u32 seq_ObjectState
u32 seq_ObjectVector
u32 seq_ObjectTeleport
u32 seq_ObjectServerControl
u32 seq_ObjectForcePosition
u32 seq_ObjectVisualDesc
u32 seq_ObjectInstance

Align()
```

**Movement body** (when `physicsDescriptionFlag & Movement` is set):
Length-prefixed. The decoder can SKIP the body for now and still
keep cursor alignment. When fully decoded (Phase 5 — see
[§Motion](#0xf74c-motion-s--c-header-only) below for the header
layout we already verified) the body
layout is (source: `MovementDataExtensions.Write` with `header=false`,
lines 184-229):

```
u8  movementType    (MovementType enum)
u8  motionFlags     (MotionFlags bitfield)
u16 currentStyle    (MotionStance enum)
[ MovementType-specific sub-section ]
```

The sub-section depends on `movementType`:
- `Invalid` → `MovementInvalid` (most common; sticks animation state)
- `MoveToObject`, `MoveToPosition`, `TurnToObject`, `TurnToHeading`
  → respective payloads (deferred)

`PhysicsDescriptionFlag` bit values (from
`Source/ACE.Entity/Enum/PhysicsDescriptionFlag.cs`):

| bit | name |
|---|---|
| `0x000001` | CSetup |
| `0x000002` | MTable |
| `0x000004` | Velocity |
| `0x000008` | Acceleration |
| `0x000010` | Omega |
| `0x000020` | Parent |
| `0x000040` | Children |
| `0x000080` | ObjScale |
| `0x000100` | Friction |
| `0x000200` | Elasticity |
| `0x000400` | Timestamps (always present in practice) |
| `0x000800` | STable |
| `0x001000` | PeTable |
| `0x002000` | DefaultScript |
| `0x004000` | DefaultScriptIntensity |
| `0x008000` | Position |
| `0x010000` | Movement |
| `0x020000` | AnimationFrame |
| `0x040000` | Translucency |

### Section 4 — WeenieHeader

Reference encoder: `SerializeCreateObject` lines 66-220.

**Fixed prefix** (always present, in this order):

```
u32                       weenieFlags             (WeenieHeaderFlag bitfield)
string16L                 name
packedDword               weenieClassId
packedDwordOfKnownType    iconId, 0x6000000
u32                       itemType                (ItemType enum)
u32                       objDescriptionFlags     (ObjectDescriptionFlag bitfield)
Align()
```

**Conditional second-header** (gated on objDescriptionFlags):

```
if (objDescriptionFlags & IncludesSecondHeader):
    u32 weenieFlags2        (WeenieHeaderFlag2 bitfield)
```

**WeenieHeaderFlag conditional fields** (in encoder order from
`WorldObject_Networking.cs:87-209`):

| bit | field | type / size |
|---|---|---|
| `0x00000001` PluralName | string16L | variable |
| `0x00000002` ItemsCapacity | u8 | 1 |
| `0x00000004` ContainersCapacity | u8 | 1 |
| `0x00000100` AmmoType | u16 (AmmoType enum) | 2 |
| `0x00000008` Value | u32 | 4 |
| `0x00000010` Usable | u32 (ItemUseable enum) | 4 |
| `0x00000020` UseRadius | f32 | 4 |
| `0x00080000` TargetType | u32 (ItemType enum) | 4 |
| `0x00000080` UiEffects | u32 | 4 |
| `0x00000200` CombatUse | u8 | 1 |
| `0x00000400` Structure | u16 | 2 |
| `0x00000800` MaxStructure | u16 | 2 |
| `0x00001000` StackSize | u16 | 2 |
| `0x00002000` MaxStackSize | u16 | 2 |
| `0x00004000` Container | u32 guid | 4 |
| `0x00008000` Wielder | u32 guid | 4 |
| `0x00010000` ValidLocations | u32 (EquipMask) | 4 |
| `0x00020000` CurrentlyWieldedLocation | u32 (EquipMask) | 4 |
| `0x00040000` Priority | u32 (CoverageMask) | 4 |
| `0x00100000` RadarBlipColor | u8 | 1 |
| `0x00800000` RadarBehavior | u8 | 1 |
| `0x08000000` PScript | u16 | 2 |
| `0x01000000` Workmanship | f32 | 4 |
| `0x00200000` Burden | u16 | 2 |
| `0x00400000` Spell | u16 | 2 |
| `0x02000000` HouseOwner | u32 guid | 4 |
| `0x04000000` HouseRestrictions | `RestrictionDB` (variable) | variable — FAIL-FAST |
| `0x20000000` HookItemTypes | u32 | 4 |
| `0x00000040` Monarch | u32 guid | 4 |
| `0x10000000` HookType | u16 | 2 |
| `0x40000000` IconOverlay | packedDwordOfKnownType, 0x6000000 | 2 or 4 |
| `0x80000000` MaterialType | u32 (MaterialType enum) | 4 |

> The encoder ORDER above (PluralName, ItemsCapacity, ...,
> MaterialType) is canonical. Do not re-order by bit number — the
> decoder must walk the bitmask in the same sequence the encoder
> walked the bit-tests, otherwise the cursor desynchronizes.

**WeenieHeaderFlag2 conditional fields** (encoder order, lines
205-218; note `IconUnderlay` and `Cooldown` interleave with
`MaterialType` from header1):

| bit | field | type / size |
|---|---|---|
| `0x01` IconUnderlay | packedDwordOfKnownType, 0x6000000 | 2 or 4 |
| `0x02` Cooldown | u32 | 4 |
| `0x04` CooldownDuration | f64 | 8 |
| `0x08` PetOwner | u32 guid | 4 |

Exact interleaved order from the encoder (CRITICAL for cursor
correctness):

```
WeenieHeaderFlag.IconOverlay        (header1)
WeenieHeaderFlag2.IconUnderlay      (header2)
WeenieHeaderFlag.MaterialType       (header1)
WeenieHeaderFlag2.Cooldown          (header2)
WeenieHeaderFlag2.CooldownDuration  (header2)
WeenieHeaderFlag2.PetOwner          (header2)
```

Final `Align()` closes the WeenieHeader section.

**`ObjectDescriptionFlag` bit list** (informational + gating bit):
See `Source/ACE.Entity/Enum/ObjectDescriptionFlag.cs`. Key bits for
gameplay:
- `0x00000008` Player
- `0x00000010` Attackable
- `0x00000200` Vendor
- `0x00001000` Door
- `0x00002000` Corpse
- `0x00004000` LifeStone
- `0x00040000` Portal
- `0x04000000` IncludesSecondHeader

## Phase 4.3 implementation strategy

Decoder built in stages:

1. **Stage 0** — `MessageReader` infrastructure: origin-aware cursor,
   `ReadU8/ReadU16/ReadI32/ReadU32/ReadF32/ReadF64/ReadGuid/
   ReadString16L/ReadPackedDword/ReadPackedDwordOfKnownType/
   ReadVector3/ReadQuaternion/Align4`. Align is computed against
   the absolute origin offset (start of message body, i.e. position
   0 in the encoded packet, which includes the 4-byte opcode).
   Assert padding bytes are zero on Align skip; record offset
   checkpoints between sections so a desync is caught at the
   nearest boundary, not at the final cursor.
2. **Stage A** — ModelData decoder + unit test against captured
   bytes (one Player, one creature/static).
3. **Stage B** — PhysicsData decoder with Movement as opaque
   length-prefixed byte slice (skip body bytes, preserve
   isAutonomous, do not align inside Movement — the trailing
   isAutonomous u32 is NOT included in the length prefix).
4. **Stage C** — WeenieHeader fixed prefix (guid, weenieFlags,
   name, WCID, IconId, itemType, objDescriptionFlags + Align).
5. **Stage D** — WeenieHeader conditional fields, all in encoder
   order. **`HouseRestrictions` is a fail-fast condition** — if
   `weenieFlags & HouseRestrictions != 0`, the decoder returns
   `null` with an error log and does NOT attempt to continue.
   Variable-length RestrictionDB body cannot be safely skipped.
   (House objects are not expected in the training academy where
   we test.)
6. **Stage E** — Full Movement body decode (deferred to Phase 5;
   header is verified — see [§Motion](#0xf74c-motion-s--c-header-only)).

Acceptance gate for 4.3 (Stages 0-D):
- All `0xF745 ObjectCreate` packets in the next capture decode
  without cursor mismatch — section checkpoints align AND final
  body offset equals declared packet end.
- At least one Player ObjectCreate (our 0x50000006) decodes (last;
  it is one of the more complex cases).
- At least one creature/door/static ObjectCreate decodes (first
  test target; simpler shape).
- Names match human-recognizable training-academy objects
  (e.g. "Apprentice Defender", "Hieromancer Adept", "Bruised Apple").

### Field signedness notes (from rubber-duck pass)

For decoder records, preserve the signedness the server encoder
uses. Cursor sizes are unchanged either way.

- `Children.Count` — `i32`, not `u32`.
- `Children[*].Guid` — `u32` (`HeldItem.Guid` is plain `uint`).
- `Children[*].LocationId` — `i32`.
- `CooldownId` — `i32`.
- `CombatUse` — `sbyte` (one byte, signed).
- `UseRadius` — `f32`.
- `Value`, `Spell` (DID) etc. — `u32` / `u16`.

## 0xF748 UpdatePosition (S → C)

Verified Phase 4.8. Per-tick broadcast of a `WorldObject`'s
position. By far the highest-volume world-state message — one per
visible moving object per server tick. In the academy this is
dominated by the Pilot-01 BotPlayer (`0x50000005`) doing idle
wander.

Encoder:
[`GameMessageUpdatePosition.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageUpdatePosition.cs)
+
[`PositionPack.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Structure/PositionPack.cs).

**Wire layout** (variable, 44 to 68 bytes depending on flags):

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF748` |
| 4 | `u32` | `guid` | `ObjectGuid.Full` |
| 8 | `u32` | `flags` | `PositionFlags` (see below) |
| 12 | `u32` | `cellId` | `Origin.CellID` — the landblock cell the object is in |
| 16 | `Vector3` (12B) | `pos` | Always present — `f32` x/y/z within the cell |
| +12 | `f32` | `rot.w` | Only if `(flags & OrientationHasNoW) == 0` |
| ... | `f32` | `rot.x` | Only if `(flags & OrientationHasNoX) == 0` |
| ... | `f32` | `rot.y` | Only if `(flags & OrientationHasNoY) == 0` |
| ... | `f32` | `rot.z` | Only if `(flags & OrientationHasNoZ) == 0` |
| ... | `Vector3` (12B) | `vel` | Only if `(flags & HasVelocity)` |
| ... | `u32` | `placementId` | Only if `(flags & HasPlacementID)` |
| ... | `u16` | `instanceSequence` | `UShortSequence` |
| +2 | `u16` | `positionSequence` | `UShortSequence` |
| +2 | `u16` | `teleportSequence` | `UShortSequence` |
| +2 | `u16` | `forcePositionSequence` | `UShortSequence` |

All four sequence fields are `u16`, packed, no alignment padding.

**`PositionFlags`** (`uint`, `[Flags]`):

| value | name | meaning |
|---|---|---|
| `0x01` | `HasVelocity` | `vel` triplet is PRESENT on the wire |
| `0x02` | `HasPlacementID` | `placementId` u32 is PRESENT |
| `0x04` | `IsGrounded` | Object grounded (no z-velocity) — informational |
| `0x08` | `OrientationHasNoW` | `rot.W` is **ABSENT** from the wire (inverse-presence!) |
| `0x10` | `OrientationHasNoX` | `rot.X` is ABSENT |
| `0x20` | `OrientationHasNoY` | `rot.Y` is ABSENT |
| `0x40` | `OrientationHasNoZ` | `rot.Z` is ABSENT |

> **INVERSE-PRESENCE TRAP**: the orientation flags use the
> opposite convention from `HasVelocity` / `HasPlacementID`.
> `HasVelocity SET` means "velocity is present"; but
> `OrientationHasNoW SET` means "rot.W is ABSENT" — the server
> omits zero components as a compression. When reconstructing the
> quaternion, default any missing component to `0.0`.

**Verified evidence** (`phase4-updateposition-run-01.log`):

```
-> UpdatePosition: guid=0x50000005 lb=0x860201DE
   xyz=(25.48,-30.00,0.00) rot=(0.707,0.000,0.000,-0.707)
   flags=0x34 seq=(inst=4,pos=38561,tp=0,fp=0)
```

`flags=0x34` = `IsGrounded | OrientationHasNoY | OrientationHasNoZ`:
W and X present, Y and Z reconstructed to 0. The reconstructed
quaternion `(0.707, 0.000, 0.000, -0.707)` is a unit rotation
matching Pilot-01's facing direction.

`lb=0x860201DE` is the landblock cell ID (the upper bytes
`0x860201..` are the landblock; the low byte is the cell within
the landblock).

## 0xF74C Motion (S → C, header only)

Verified Phase 4.6 — header only. Per-tick broadcast of a
`WorldObject`'s movement intent (an animated motion command +
the sequence trio needed to validate ordering). Wraps a
`MovementData` payload whose body shape depends on `MovementType`.
The polymorphic body (`MovementInvalid` / `MoveToObject` /
`MoveToPosition` / `TurnToObject` / `TurnToHeading`) is preserved
as raw bytes for later phases.

Encoder:
[`GameMessageUpdateMotion.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/GameMessages/Messages/GameMessageUpdateMotion.cs)
+
[`MovementData.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Structure/MovementData.cs).
`Align()` impl:
[`Extensions.cs:54-63`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Network/Extensions.cs#L54-L63).

**Wire layout** (variable, ≥20-byte header):

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | `u32` | `opcode` | `0xF74C` |
| 4 | `u32` | `guid` | `ObjectGuid.Full` |
| 8 | `u16` | `instanceSequence` | `UShortSequence: ObjectInstance` |
| 10 | `u16` | `movementSequence` | `UShortSequence: ObjectMovement` |
| 12 | `u16` | `serverControlSequence` | `UShortSequence: ObjectServerControl` |
| 14 | `u8` | `isAutonomous` | `0` = server-initiated, `1` = client |
| 15 | `u8 × 1` | **align pad** | One pad byte to land at offset 16 — see trap below |
| 16 | `u8` | `movementType` | `MovementType` enum |
| 17 | `u8` | `motionFlags` | `MotionFlags [Flags]` enum |
| 18 | `u16` | `currentStyle` | `MotionStance` enum, written as `ushort` |
| 20 | `bytes[N]` | `body` | Polymorphic, kept as raw bytes |

> **ALIGNMENT TRAP (single most expensive Phase 4 finding)**:
> `writer.Align()` in ACE pads the stream **LENGTH** to the next
> multiple of 4, NOT the current write position. After the 4-byte
> opcode + 4-byte guid + (2+2+2)-byte sequence trio + 1-byte
> `isAutonomous`, the stream length is 15. `Align()` writes 1
> pad byte to land at 16 before `movementType`. Forgetting this
> pad shifts the rest of the header read by 1 byte and corrupts
> everything downstream.

**`MovementType` enum** (verbatim from
[`MovementType.cs`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Entity/Enum/MovementType.cs)):

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

**`MotionFlags` `[Flags]`**:

| bit | name |
|---|---|
| `0x1` | `StickToObject` |
| `0x2` | `StandingLongJump` |

**Body shape per `MovementType`** (deferred — Phase 5):

- `Invalid` → `MovementInvalid` blob (most common; sticks animation
  state).
- `MoveToObject` → `u32 targetGuid` + position + heading data.
- `MoveToPosition` → position + heading data.
- `TurnToObject` → `u32 targetGuid` + heading data.
- `TurnToHeading` → heading data.

**Verified evidence** (`phase4-state-speech-run-01.log`):

```
-> Motion: guid=0x50000005 type=TurnToObject flags=None style=0x003D
   autonomous=False seq=(inst=4,mov=3178,srv=3178) body[20]
-> Motion: guid=0x50000005 type=Invalid      flags=None style=0x003D
   autonomous=False seq=(inst=4,mov=3180,srv=3180) body[12]
```

Style `0x003D` = `NonCombat` (from `MotionStance` enum). The bot
sees Pilot-01 alternating between TurnToObject (toward
`0x50000006` = Headless01) and Invalid (idle-pose stick).

## Open questions / verification targets

**Resolved during Phase 4.3 implementation:**

- ✅ `Velocity` / `Acceleration` / `Omega` ordering matches the
  encoder text (after `Translucency`, before the always-present
  sequence trio). Verified by clean ObjectCreate decode of player
  + creatures in `phase4-objectcreate-run-13.log`.
- ✅ `Children.Count` is encoded as `int` (`writer.Write(Children.Count)`)
  and decoded as `i32`. Equivalent to `u32` on the wire for the
  observed ranges; signedness preserved in the record type.
- ✅ ModelData marker byte = `0x11`. Verified in every captured
  ObjectCreate.

**Still open (deferred to later phases):**

- Cloaked-admin `Translucency` rewrite — the decoder reads `f32`
  unconditionally; not yet observed in the academy where no
  cloaked admins exist.
- Full `Motion` body decode (per-`MovementType` payloads). The
  spike currently preserves the body as raw bytes; full decode
  is queued for Phase 5.
- Per-`GameEventType` payload decoders. The envelope is decoded
  (Phase 4.5) but each event's body is preserved as raw bytes;
  the highest-value first target is `PlayerDescription`
  (`0x0013`, 668 bytes — the character sheet).

