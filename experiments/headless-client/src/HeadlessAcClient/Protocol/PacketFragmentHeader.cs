// SPDX-License-Identifier: AGPL-3.0-or-later
// Mirrors Source/ACE.Server/Network/PacketFragmentHeader.cs
//
// Every fragment carried inside a BlobFragments packet starts with
// this 16-byte header followed by `Size - HeaderSize` bytes of game-
// message payload.

using System;
using System.Buffers.Binary;

using HeadlessAcClient.Crypto;

namespace HeadlessAcClient.Protocol;

internal struct PacketFragmentHeader
{
    public const int HeaderSize = 16;

    public uint   Sequence;
    public uint   Id;
    public ushort Count;
    public ushort Size;
    public ushort Index;
    public ushort Queue;

    public void Unpack(ReadOnlySpan<byte> buffer)
    {
        Sequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(0));
        Id       = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
        Count    = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(8));
        Size     = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(10));
        Index    = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12));
        Queue    = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14));
    }

    public void Pack(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(0), Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4), Id);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8), Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(10), Size);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(12), Index);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14), Queue);
    }

    /// <summary>
    /// Hash32 of the packed 16-byte header plus the supplied data
    /// span. Matches the server's
    /// <c>ClientPacketFragment.CalculateHash32</c>.
    /// </summary>
    public uint CalculateHash32(ReadOnlySpan<byte> data)
    {
        Span<byte> headerBytes = stackalloc byte[HeaderSize];
        Pack(headerBytes);
        return Hash32.Calculate(headerBytes, HeaderSize)
             + Hash32.Calculate(data, data.Length);
    }

    public override string ToString()
        => $"Frag Seq={Sequence} Id=0x{Id:X8} Count={Count} Size={Size} Idx={Index} Q={Queue}";
}
