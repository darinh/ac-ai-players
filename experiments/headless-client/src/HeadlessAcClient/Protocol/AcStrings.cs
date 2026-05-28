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
        var packedLen = PackedPrefixLen(s.Length);
        var raw = sizeof(uint) + packedLen + s.Length;
        return AlignTo4(raw);
    }

    public static int WriteString32L(Span<byte> dest, ReadOnlySpan<char> s)
    {
        // ReadString32L decodes char-count as (dataLen - 1) when
        // (dataLen - 1) <= 255 (1-byte prefix), else as
        // (dataLen - 2) (2-byte prefix). Threshold is therefore
        // char_count > 254 -> 2-byte prefix. See
        // ACE.Common BinaryReaderExtensions.cs lines 12-39 and
        // spec/05-data-types.md.
        var packedLen = PackedPrefixLen(s.Length);
        var dataLen = (uint)(packedLen + s.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(dest, dataLen);
        var pos = sizeof(uint);

        if (packedLen == 1)
        {
            dest[pos++] = (byte)s.Length;
        }
        else
        {
            // The reader skips these two bytes without decoding;
            // their content is ignored. We write the count split
            // across them for legibility, but any pair would work.
            dest[pos++] = (byte)(s.Length & 0xFF);
            dest[pos++] = (byte)((s.Length >> 8) & 0xFF);
        }

        for (var i = 0; i < s.Length; i++)
            dest[pos++] = (byte)s[i];

        var raw = sizeof(uint) + packedLen + s.Length;
        var padded = AlignTo4(raw);
        for (var i = raw; i < padded; i++)
            dest[i] = 0;

        return padded;
    }

    private static int PackedPrefixLen(int charCount) => charCount <= 254 ? 1 : 2;

    private static int AlignTo4(int n) => (n + 3) & ~3;

    // Self-check: round-trips writer through a mirror of the ACE
    // reader (BinaryReaderExtensions.ReadString32L / ReadString16L)
    // for boundary char-counts. Cheap to run at startup; catches
    // any future regression of the >254 packed-prefix threshold.
    // Throws InvalidOperationException on any mismatch.
    public static void RunSelfChecks()
    {
        int[] lengths = { 0, 1, 5, 9, 64, 127, 128, 200, 254, 255, 256, 300, 1000 };
        foreach (var len in lengths)
        {
            var s = new string('A', len);

            // String32L round-trip
            var buf32 = new byte[MeasureString32L(s)];
            var n32 = WriteString32L(buf32, s);
            if (n32 != buf32.Length)
                throw new InvalidOperationException($"String32L len={len} written {n32} != measured {buf32.Length}");
            var got32 = MirrorReadString32L(buf32);
            if (got32 != s)
                throw new InvalidOperationException($"String32L len={len} round-trip mismatch (got len={got32.Length})");

            // String16L round-trip (only sizes a ushort can hold)
            if (len <= ushort.MaxValue)
            {
                var buf16 = new byte[MeasureString16L(s)];
                var n16 = WriteString16L(buf16, s);
                if (n16 != buf16.Length)
                    throw new InvalidOperationException($"String16L len={len} written {n16} != measured {buf16.Length}");
                var got16 = MirrorReadString16L(buf16);
                if (got16 != s)
                    throw new InvalidOperationException($"String16L len={len} round-trip mismatch (got len={got16.Length})");
            }
        }
    }

    // Mirrors ACE.Common BinaryReaderExtensions.ReadString32L
    // (lines 12-39) so we can verify the writer against the same
    // logic the server uses, without taking a dep on ACE.Common.
    private static string MirrorReadString32L(ReadOnlySpan<byte> src)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(src);
        var pos = sizeof(uint);
        if (length == 0) return "";

        pos += 1; length--;
        if (length > 255) { pos += 1; length--; }

        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)src[pos + i];
        return new string(chars);
    }

    private static string MirrorReadString16L(ReadOnlySpan<byte> src)
    {
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(src);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)src[sizeof(ushort) + i];
        return new string(chars);
    }

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
