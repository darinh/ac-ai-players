# 05 — Data types

All multi-byte integers are **little-endian** on the wire
unless explicitly noted. Floats are IEEE 754 little-endian.
ASCII strings are 8-bit, not UTF-8 (the AC1 client predates
widespread UTF-8 adoption).

## Primitive types

| Spec name | C# type | Wire size | Notes |
|---|---|---|---|
| `u8` | `byte` | 1 | |
| `u16` | `ushort` | 2 | |
| `u32` | `uint` | 4 | |
| `u64` | `ulong` | 8 | |
| `i8` | `sbyte` | 1 | |
| `i16` | `short` | 2 | |
| `i32` | `int` | 4 | |
| `i64` | `long` | 8 | |
| `f32` | `float` | 4 | |
| `f64` | `double` | 8 | Used for `PortalYearTicks` server time |
| `guid` | `uint` | 4 | Object IDs in the game world. See §GUID ranges below. |

## Padding rules

Strings (both `String16L` and `String32L`) pad the **total
field** (length prefix + char bytes) to the next multiple of
4 bytes. The padding bytes are not part of the string.

The pad formula:

```
pad_bytes = (4 - ((length_prefix_size + char_count) % 4)) % 4
```

Equivalently in C#:

```csharp
private static uint CalculatePadMultiple(uint length, uint multiple)
{
    return multiple * ((length + multiple - 1u) / multiple) - length;
}
```

Source: [`BinaryReaderExtensions.cs:7-10`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Extensions/BinaryReaderExtensions.cs).

The packet header itself does not require any extra padding;
its fixed 20-byte size is naturally aligned.

## `String16L` — short string with 16-bit length prefix

**Use**: account names, client version, server names, chat
messages, most text fields.

**Wire layout**:

```
+--------+--------+--------+--------+--------+...+--------+
|     u16 length      | char_0 | char_1 |...| char_n-1   |
+--------+--------+--------+--------+--------+...+--------+
| padding 0..3 bytes to align total field to 4-byte boundary |
+--------+...
```

| offset | size | meaning |
|---|---|---|
| 0 | 2 | `length` — character count (u16 LE) |
| 2 | `length` | ASCII bytes (no null terminator) |
| `2 + length` | 0..3 | zero padding to next 4-byte boundary |

**Total field size**: `align4(2 + length)` bytes.

**Write algorithm**:

```csharp
public static int WriteString16L(Span<byte> dest, ReadOnlySpan<char> s)
{
    BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)s.Length);
    var written = sizeof(ushort);
    for (var i = 0; i < s.Length; i++)
        dest[written++] = (byte)s[i];

    var raw = sizeof(ushort) + s.Length;
    var padded = (raw + 3) & ~3;
    for (var i = raw; i < padded; i++) dest[i] = 0;
    return padded;
}
```

**Read algorithm**:

```csharp
public static string ReadString16L(this BinaryReader reader)
{
    ushort length = reader.ReadUInt16();
    string s = length != 0
        ? new string(reader.ReadChars(length))
        : string.Empty;
    reader.Skip(CalculatePadMultiple(sizeof(ushort) + (uint)length, 4u));
    return s;
}
```

Source: [`BinaryReaderExtensions.cs:41-49`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Extensions/BinaryReaderExtensions.cs).

### Examples

| Input | Hex |
|---|---|
| `""` | `00 00` then 2 pad bytes → `00 00 00 00` |
| `"1802"` | `04 00 31 38 30 32` then 2 pad → `04 00 31 38 30 32 00 00` |
| `"AB"` | `02 00 41 42` (already aligned, no pad) |
| `"hello"` | `05 00 68 65 6C 6C 6F` then 1 pad → `05 00 68 65 6C 6C 6F 00` |

## `String32L` — long string with packed length prefix

**Use**: passwords, GLS tickets. The encoding is intentionally
awkward (see comment in source: "32L strings are crazy"). It
appears only in the `LoginRequest` body.

**Wire layout**:

```
+--------+--------+--------+--------+---packed-len---+chars---+padding---+
|     u32 dataLen                  | 1 or 2 bytes   | n bytes| 0..3     |
+--------+--------+--------+--------+----------------+--------+----------+
```

| offset | size | meaning |
|---|---|---|
| 0 | 4 | `dataLen` — `packed_len_bytes + char_count` (u32 LE) |
| 4 | 1 or 2 | packed length (see below) |
| `4 + packed_len_bytes` | `char_count` | ASCII bytes |
| ... | 0..3 | zero padding to next 4-byte boundary |

**The packed-length rule**:

The number of bytes in the packed-length prefix is determined
by `dataLen`, not by inspecting any bits:

- If `dataLen <= 256` (which means `char_count <= 255`): 1
  byte packed-length.
- If `dataLen > 256` (which means `char_count > 254`): 2 byte
  packed-length.

The reader does:

```csharp
public static string ReadString32L(this BinaryReader reader)
{
    uint length = reader.ReadUInt32();   // length = dataLen
    if (length == 0) return "";

    reader.Skip(1);
    length--;                            // length = dataLen - 1

    if (length > 255)                    // i.e., dataLen > 256
    {
        reader.Skip(1);
        length--;
    }

    string s = length != 0
        ? new string(reader.ReadChars((int)length))
        : string.Empty;

    reader.Skip(CalculatePadMultiple(sizeof(uint) + length, 4u));
    return s;
}
```

Source: [`BinaryReaderExtensions.cs:12-39`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Common/Extensions/BinaryReaderExtensions.cs).

⚠ Critical reader-side observation: the **content of the
packed-length bytes is never read**. They're skipped. So a
writer can fill them with anything (zeros are fine). What
matters is that the COUNT of packed-length bytes (1 vs 2)
matches the size implied by `dataLen`.

**Total field size**:
`align4(4 + packed_len_bytes + char_count)`.

**Write algorithm** (correct version):

```csharp
public static int WriteString32L(Span<byte> dest, ReadOnlySpan<char> s)
{
    // Reader switches to 2-byte packed length when dataLen > 256,
    // i.e., when char_count > 254. Mirror that here.
    var packedLen = s.Length <= 254 ? 1 : 2;
    var dataLen = (uint)(packedLen + s.Length);

    BinaryPrimitives.WriteUInt32LittleEndian(dest, dataLen);
    var pos = sizeof(uint);

    // Content of packed-length bytes is never read; zeros suffice.
    for (var i = 0; i < packedLen; i++) dest[pos++] = 0;

    for (var i = 0; i < s.Length; i++) dest[pos++] = (byte)s[i];

    var raw = sizeof(uint) + packedLen + s.Length;
    var padded = (raw + 3) & ~3;
    for (var i = raw; i < padded; i++) dest[i] = 0;
    return padded;
}
```

⚠ KNOWN BUG in the Phase 1 spike code at
`experiments/headless-client/src/HeadlessAcClient/Protocol/AcStrings.cs`:
the writer there switches to 2-byte packed length at
`s.Length >= 128` (high-bit threshold), but the reader's
threshold is `s.Length > 254`. For passwords in
`128..254` chars the spike writes 2 packed bytes but the
reader expects 1, misaligning everything that follows.
**Untested in Phase 1** (test password `"Test1234!"` is 9
chars). Fix before sending long strings.

### Examples

| Input | dataLen | packed_len | hex (after dataLen) |
|---|---|---|---|
| `""` | 0 | 0 | (no packed-len, no padding needed beyond the 4-byte dataLen field) → `00 00 00 00` total |
| `"x"` | 2 | 1 | `02 00 00 00 00 78` + 2 pad → `02 00 00 00 00 78 00 00` |
| `"Test1234!"` | 10 | 1 | `0A 00 00 00 00 54 65 73 74 31 32 33 34 21` + 2 pad → 16 bytes total |

## GUID ranges

ACE uses 32-bit integer GUIDs to identify in-game objects.
Ranges are reserved per object class so a parser can do
range-based dispatch:

| Range start (hex) | Class | Purpose |
|---|---|---|
| `0x10000000` | Item | Loose items in the world |
| `0x40000000` | Container | Chests, bags |
| `0x50000000` | Creature | Monsters, NPCs |
| `0xF0000000` | Player | Player characters |

(These are illustrative; full table is in
[`ACE.Entity.GuidType`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Entity/Enum/GuidType.cs).)

The character GUID returned in `GameMessageCharacterList`
will be in the Player range.

## Time

ACE uses two time units interchangeably depending on context:

- **Unix time** (`u32` seconds since 1970-01-01 UTC) — used
  for account-level timestamps.
- **PortalYearTicks** (`f64` seconds since a fixed AC-world
  epoch, roughly 2002) — used for in-game time, sequencing,
  and synchronization. The `ServerTime` field in
  `ConnectRequest` is this format.

Source: `Source/ACE.Server/Managers/Timers.cs`.

## Endianness summary

| Type | Order on wire |
|---|---|
| All numeric primitives | little-endian |
| ASCII string chars | byte-for-byte, in order |
| Hash32 / checksum result | computed as a single u32, written little-endian |

If your platform is big-endian, use `BinaryPrimitives` (.NET)
or equivalent explicit-endian primitives. Do not rely on
`BitConverter.ToInt32` style calls without checking
`BitConverter.IsLittleEndian`.
