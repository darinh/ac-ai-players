// SPDX-License-Identifier: AGPL-3.0-or-later
// Combines PacketHeader + PacketHeaderOptional + fragment parsing
// into a single "received packet" object, with a VerifyCRC method
// that uses the ISAAC stream-cipher CryptoSystem to validate the
// EncryptedChecksum on inbound packets.
//
// Mirrors Source/ACE.Server/Network/ClientPacket.cs.

using System;
using System.Collections.Generic;

using HeadlessAcClient.Crypto;

namespace HeadlessAcClient.Protocol;

internal sealed class InboundPacket
{
    public PacketHeader         Header;
    public PacketHeaderOptional Optional { get; } = new();
    public List<(PacketFragmentHeader Header, byte[] Data)> Fragments { get; } = new();

    /// <summary>
    /// Parse `buffer[0..length]` into header / optional headers /
    /// fragments. Returns false on any malformed slice.
    /// </summary>
    public bool Unpack(byte[] buffer, int length)
    {
        if (length < PacketHeader.HeaderSize) return false;

        Header = new PacketHeader();
        Header.Unpack(buffer.AsSpan(0, PacketHeader.HeaderSize));

        if (Header.Size > length - PacketHeader.HeaderSize) return false;

        var cursor = PacketHeader.HeaderSize;
        Optional.Unpack(buffer.AsSpan(0, length), ref cursor, Header.Flags);
        if (!Optional.IsValid) return false;

        // Everything after the optional headers, up through the
        // declared payload size, is fragment territory.
        var fragEnd = PacketHeader.HeaderSize + Header.Size;
        if (Header.HasFlag(PacketHeaderFlags.BlobFragments))
        {
            while (cursor < fragEnd)
            {
                if (fragEnd - cursor < PacketFragmentHeader.HeaderSize) return false;

                var fh = new PacketFragmentHeader();
                fh.Unpack(buffer.AsSpan(cursor, PacketFragmentHeader.HeaderSize));

                if (fh.Size < PacketFragmentHeader.HeaderSize) return false;
                if (fh.Size > 464) return false;

                var dataSize = fh.Size - PacketFragmentHeader.HeaderSize;
                if (fragEnd - cursor - PacketFragmentHeader.HeaderSize < dataSize) return false;

                var data = new byte[dataSize];
                buffer.AsSpan(cursor + PacketFragmentHeader.HeaderSize, dataSize).CopyTo(data);

                Fragments.Add((fh, data));
                cursor += fh.Size;
            }
        }

        return true;
    }

    /// <summary>
    /// Recompute the checksum the server would have written and
    /// compare against <c>Header.Checksum</c>.
    ///
    /// Unencrypted: <c>headerChecksum + payloadChecksum</c>.
    /// Encrypted: header.Checksum is XORed with an ISAAC key from
    /// the receive keystream. Recover the key, then check it lives
    /// inside the lookahead window; consume it on match.
    /// </summary>
    public bool VerifyCRC(CryptoSystem cryptoRecv)
    {
        var headerChecksum   = Header.CalculateHash32();
        var optionalChecksum = Optional.CalculateHash32();
        var fragmentChecksum = 0u;
        foreach (var (fh, data) in Fragments)
            fragmentChecksum += fh.CalculateHash32(data);
        var payloadChecksum = optionalChecksum + fragmentChecksum;

        if (Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
        {
            var key = (Header.Checksum - headerChecksum) ^ payloadChecksum;
            if (cryptoRecv.Search(key))
            {
                cryptoRecv.ConsumeKey(key);
                return true;
            }
            return false;
        }
        return headerChecksum + payloadChecksum == Header.Checksum;
    }
}
