// SPDX-License-Identifier: AGPL-3.0-or-later
// Fixed 20-byte AC packet header. Layout matches
//   Source/ACE.Server/Network/PacketHeader.cs

using System;
using System.Buffers.Binary;

using HeadlessAcClient.Crypto;

namespace HeadlessAcClient.Protocol;

internal struct PacketHeader
{
    public const int HeaderSize = 20;

    public uint Sequence;
    public PacketHeaderFlags Flags;
    public uint Checksum;
    public ushort Id;
    public ushort Time;
    public ushort Size;
    public ushort Iteration;

    public void Pack(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(0), Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4), (uint)Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8), Checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(12), Id);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14), Time);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(16), Size);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(18), Iteration);
    }

    public void Unpack(ReadOnlySpan<byte> buffer)
    {
        Sequence  = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(0));
        Flags     = (PacketHeaderFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
        Checksum  = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8));
        Id        = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12));
        Time      = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14));
        Size      = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(16));
        Iteration = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(18));
    }

    public uint CalculateHash32()
    {
        Span<byte> buffer = stackalloc byte[HeaderSize];
        var original = Checksum;
        Checksum = 0xBADD70DD;
        Pack(buffer);
        var checksum = Hash32.Calculate(buffer, HeaderSize);
        Checksum = original;
        return checksum;
    }

    public bool HasFlag(PacketHeaderFlags flag) => (Flags & flag) != 0;

    public override string ToString()
        => $"Seq={Sequence} Id={Id} Iter={Iteration} Flags={Flags} Size={Size} CRC=0x{Checksum:X8}";
}
