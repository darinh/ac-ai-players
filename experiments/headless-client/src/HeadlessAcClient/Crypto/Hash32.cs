// SPDX-License-Identifier: AGPL-3.0-or-later
// Copied verbatim from ACEmulator (AGPL3). Upstream:
//   Source/ACE.Common/Cryptography/Hash32.cs
// See Isaac.cs for the license-inheritance note.

using System;
using System.Buffers.Binary;

namespace HeadlessAcClient.Crypto;

internal static class Hash32
{
    public static uint Calculate(ReadOnlySpan<byte> data, int length)
    {
        uint checksum = (uint)length << 16;

        for (var i = 0; i < length && i + 4 <= length; i += 4)
            checksum += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));

        var shift = 3;
        var j = (length / 4) * 4;

        while (j < length)
            checksum += (uint)(data[j++] << (8 * shift--));

        return checksum;
    }
}
