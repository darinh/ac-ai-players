// SPDX-License-Identifier: AGPL-3.0-or-later
// MessageReader - origin-aware cursor over a game-message payload.
//
// All AC game-message bodies start at byte 0 = the u32 opcode.
// The encoder's `Align()` extension pads against the absolute
// stream position (`BaseStream.Length`), which for a single
// message means "position from the start of the opcode". The
// reader therefore must align against that same origin, NOT
// against a slice-local position.
//
// To stay safe under sub-slices (e.g. when verifying ModelData
// in isolation against a captured byte sample), the reader
// accepts an `absoluteOriginOffset` that is added to the local
// cursor before computing alignment. For end-to-end decoding of
// a full opcode payload this is always 0.
//
// All reads are bounds-checked; under-reads throw
// `EndOfStreamException`. Align skips are checked for zero
// padding so a desynchronized cursor surfaces immediately.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed class MessageReader
{
    private readonly ReadOnlyMemory<byte> _buf;
    private readonly int _absoluteOriginOffset;
    private int _cursor;

    public MessageReader(ReadOnlyMemory<byte> buf, int absoluteOriginOffset = 0)
    {
        _buf = buf;
        _absoluteOriginOffset = absoluteOriginOffset;
        _cursor = 0;
    }

    public int Cursor => _cursor;
    public int Remaining => _buf.Length - _cursor;
    public int Length => _buf.Length;
    public int AbsoluteCursor => _absoluteOriginOffset + _cursor;

    private ReadOnlySpan<byte> SliceForRead(int needed)
    {
        if (Remaining < needed)
            throw new EndOfStreamException(
                $"MessageReader underflow at local offset {_cursor} (absolute {_absoluteOriginOffset + _cursor}): "
                + $"needed {needed}, remaining {Remaining}.");
        var span = _buf.Span.Slice(_cursor, needed);
        _cursor += needed;
        return span;
    }

    public byte ReadU8() => SliceForRead(1)[0];

    public sbyte ReadI8() => (sbyte)SliceForRead(1)[0];

    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(SliceForRead(2));

    public short ReadI16() => BinaryPrimitives.ReadInt16LittleEndian(SliceForRead(2));

    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(SliceForRead(4));

    public int ReadI32() => BinaryPrimitives.ReadInt32LittleEndian(SliceForRead(4));

    public float ReadF32() => BinaryPrimitives.ReadSingleLittleEndian(SliceForRead(4));

    public double ReadF64() => BinaryPrimitives.ReadDoubleLittleEndian(SliceForRead(8));

    public uint ReadGuid() => ReadU32();

    public Vector3 ReadVector3()
    {
        var x = ReadF32();
        var y = ReadF32();
        var z = ReadF32();
        return new Vector3(x, y, z);
    }

    // String16L: u16 length + length bytes (CP1252/Latin-1, 1 byte
    // per char) + pad so (2 + length) is rounded up to a multiple
    // of 4. Pad is aligned LOCAL to the string, not the message.
    // Mirrors AcStrings.ReadString16L.
    public string ReadString16L()
    {
        var length = ReadU16();
        if (length == 0)
        {
            // Even an empty string has 2 bytes consumed (the u16)
            // and pads to 4. Skip the 2 pad bytes.
            SliceForRead(2);
            return string.Empty;
        }
        var bodySpan = SliceForRead(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)bodySpan[i];
        var raw = 2 + length;
        var padded = AlignTo4(raw);
        var pad = padded - raw;
        if (pad > 0) SkipPaddingZeros(pad, context: "String16L tail");
        return new string(chars);
    }

    // PackedDword (see spec/07): 2 OR 4 bytes. If high bit of the
    // first u16 is clear, value is just that u16. Otherwise the
    // first u16 holds (high16 | 0x8000) and the next u16 holds
    // low16; recombine as (high16 << 16) | low16.
    public uint ReadPackedDword()
    {
        var first = ReadU16();
        if ((first & 0x8000) == 0) return first;
        var high = (uint)(first & 0x7FFF);
        var low = ReadU16();
        return (high << 16) | low;
    }

    // PackedDwordOfKnownType: writer subtracts `type` from `value`
    // ONLY when (value & type) != 0. Value 0 stays 0 on the wire.
    // Decoder: read packed dword; if non-zero, re-add the type.
    public uint ReadPackedDwordOfKnownType(uint type)
    {
        var raw = ReadPackedDword();
        return raw == 0 ? 0u : raw + type;
    }

    // Align cursor so AbsoluteCursor is a multiple of 4. Asserts
    // pad bytes are zero so a desync is caught immediately.
    public void Align4()
    {
        var abs = AbsoluteCursor;
        var pad = (4 - (abs & 3)) & 3;
        if (pad == 0) return;
        SkipPaddingZeros(pad, context: $"Align4 at absolute {abs}");
    }

    // Skip `count` bytes without interpretation. Used by the
    // ObjectCreate Movement section opaque body. Caller must
    // know the byte count from a length prefix.
    public ReadOnlySpan<byte> SkipBytes(int count)
    {
        return SliceForRead(count);
    }

    private void SkipPaddingZeros(int count, string context)
    {
        var span = SliceForRead(count);
        for (var i = 0; i < count; i++)
        {
            if (span[i] != 0)
            {
                throw new InvalidDataException(
                    $"MessageReader non-zero pad byte at local offset {_cursor - count + i} "
                    + $"(absolute {_absoluteOriginOffset + _cursor - count + i}, context: {context}): 0x{span[i]:X2}. "
                    + "Cursor is likely desynchronized.");
            }
        }
    }

    private static int AlignTo4(int n) => (n + 3) & ~3;
}
