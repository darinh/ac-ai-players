// SPDX-License-Identifier: AGPL-3.0-or-later
// Write side of AC's two string-with-length encodings used in the
// LoginRequest body. Derived from the read side at
//   Source/ACE.Common/Extensions/BinaryReaderExtensions.cs
// String16L: u16 length, ASCII bytes, padded to a multiple of 4
//            (counting the u16).
// String32L: u32 dataLen, packed-word length, ASCII bytes, padded
//            to a multiple of 4. Only used in the LoginRequest;
//            the comment in BinaryReaderExtensions.cs notes the
//            padding "is completely unnecessary as it's the end
//            of the packet" — we still emit it for correctness.

using System;
using System.Buffers.Binary;
using System.Text;

namespace HeadlessAcClient.Protocol;

internal static class AcStrings
{
    public static int MeasureString16L(ReadOnlySpan<char> s)
    {
        var bytes = s.Length;
        var raw = sizeof(ushort) + bytes;
        return AlignTo4(raw);
    }

    public static int WriteString16L(Span<byte> dest, ReadOnlySpan<char> s)
    {
        if (s.Length > ushort.MaxValue)
            throw new ArgumentException("String16L over 65535 chars", nameof(s));

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)s.Length);
        var written = sizeof(ushort);
        for (var i = 0; i < s.Length; i++)
            dest[written++] = (byte)s[i];

        var raw = sizeof(ushort) + s.Length;
        var padded = AlignTo4(raw);
        for (var i = raw; i < padded; i++)
            dest[i] = 0;

        return padded;
    }

    public static int MeasureString32L(ReadOnlySpan<char> s)
    {
        var packedLen = s.Length < 0x80 ? 1 : 2;
        var raw = sizeof(uint) + packedLen + s.Length;
        return AlignTo4(raw);
    }

    public static int WriteString32L(Span<byte> dest, ReadOnlySpan<char> s)
    {
        var packedLen = s.Length < 0x80 ? 1 : 2;
        var dataLen = (uint)(packedLen + s.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(dest, dataLen);
        var pos = sizeof(uint);

        if (packedLen == 1)
        {
            dest[pos++] = (byte)s.Length;
        }
        else
        {
            // Two-byte packed length: high bit of first byte set,
            // big-endian-ish. Matches the read side which detects
            // length>255 after first byte and consumes a second.
            dest[pos++] = (byte)(0x80 | (s.Length & 0x7F));
            dest[pos++] = (byte)((s.Length >> 7) & 0xFF);
        }

        for (var i = 0; i < s.Length; i++)
            dest[pos++] = (byte)s[i];

        var raw = sizeof(uint) + packedLen + s.Length;
        var padded = AlignTo4(raw);
        for (var i = raw; i < padded; i++)
            dest[i] = 0;

        return padded;
    }

    private static int AlignTo4(int n) => (n + 3) & ~3;

    // String16L body bytes (no padding) for Hash32 callers that
    // need to know the unpadded extent. Unused in Phase 1; kept
    // for symmetry with the read side comments.
    internal static int Encoding(ReadOnlySpan<char> s, Span<byte> dest)
    {
        for (var i = 0; i < s.Length; i++)
            dest[i] = (byte)s[i];
        return s.Length;
    }
}
