# 07 — World state

Wire schemas for the messages the server pushes after
`0xF657 CharacterEnterWorld` to keep the client's local model of
"what's around me" up to date. The bot must consume these to know
where it is, what its avatar looks like, what objects exist nearby,
and how those objects move.

Status: **DRAFT** for Phase 4.3 (`0xF745 ObjectCreate`). Other
opcodes are stubbed; will be expanded as each decoder lands and is
verified against capture logs.

## Decoder priority

From the Phase 3.3 firehose (`phase3-enterworld-run-01.log`, 221
packets in 65s):

| opcode | name | volume | first decoded |
|---|---|---|---|
| `0xF745` | `ObjectCreate` | high | Phase 4.3 (this doc, in-flight) |
| `0xF74C` | `Motion` | per-tick | Phase 4.4 (deferred) |
| `0xF7B0` | `GameEvent` | login-burst then sparse | Phase 4.6 (deferred) |
| `0x02CD` | `PrivateUpdatePropertyInt` | event-driven | Phase 4.5 (deferred) |

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
keep cursor alignment. When fully decoded (Phase 4.4) the body
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
6. **Stage E** — Full Movement body decode (Phase 4.4).

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

## Open questions / verification targets

- The `Velocity`/`Acceleration`/`Omega` ordering in the encoder
  (lines 389-402) lists them AFTER `Translucency`. The encoder's
  textual order is what the wire format actually is — confirm
  with capture during Stage B.
- `Children` count is `int` (`writer.Write(Children.Count)`), not
  `u32`. Probably equivalent on wire but document the signed-ness
  on first capture.
- `Translucency` is conditionally rewritten for cloaked admins —
  decoder reads `f32` regardless.
- Verify `marker = 0x11` for ModelData against captured bytes
  before committing Stage A.
