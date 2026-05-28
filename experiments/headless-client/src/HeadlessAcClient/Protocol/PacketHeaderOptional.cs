// SPDX-License-Identifier: AGPL-3.0-or-later
// Mirrors Source/ACE.Server/Network/PacketHeaderOptional.cs
//
// "Optional headers" are variable-size sub-headers selected by flag
// bits in the main PacketHeader. Their bytes (in flag-order) form
// the input to a Hash32 that contributes to the per-packet checksum.
//
// We must capture the same bytes the SERVER reads when it computes
// its own outbound checksum, because we recompute the same Hash32
// to verify the inbound packet's checksum.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using HeadlessAcClient.Crypto;

namespace HeadlessAcClient.Protocol;

internal sealed class PacketHeaderOptional
{
    public uint   AckSequence;
    public double TimeSync;
    public float  EchoRequestClientTime;
    public uint   FlowBytes;
    public ushort FlowInterval;
    public List<uint>? RetransmitData;

    public bool IsValid { get; private set; } = true;

    /// <summary>Total number of optional-header bytes consumed.</summary>
    public int Size { get; private set; }

    private readonly MemoryStream _captured = new();

    /// <summary>
    /// Parse the optional-header section starting at <paramref name="cursor"/>
    /// of <paramref name="packet"/>. Advances <paramref name="cursor"/> past
    /// the consumed bytes. Captures the consumed bytes verbatim into
    /// <c>_captured</c> for later <see cref="CalculateHash32"/>.
    /// </summary>
    public void Unpack(ReadOnlySpan<byte> packet, ref int cursor, PacketHeaderFlags flags)
    {
        var start = cursor;
        _captured.SetLength(0);
        var writer = new BinaryWriter(_captured);

        if ((flags & PacketHeaderFlags.ServerSwitch) != 0) // 0x100
        {
            if (!Take(packet, ref cursor, 8, out var bytes)) return;
            writer.Write(bytes);
        }

        if ((flags & PacketHeaderFlags.RequestRetransmit) != 0) // 0x1000
        {
            if (!Take(packet, ref cursor, 4, out var countBytes)) return;
            writer.Write(countBytes);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
            RetransmitData = new List<uint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (!Take(packet, ref cursor, 4, out var seqBytes)) return;
                writer.Write(seqBytes);
                RetransmitData.Add(BinaryPrimitives.ReadUInt32LittleEndian(seqBytes));
            }
        }

        if ((flags & PacketHeaderFlags.RejectRetransmit) != 0) // 0x2000
        {
            if (!Take(packet, ref cursor, 4, out var countBytes)) return;
            writer.Write(countBytes);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
            for (var i = 0; i < count; i++)
            {
                if (!Take(packet, ref cursor, 4, out var seqBytes)) return;
                writer.Write(seqBytes);
            }
        }

        if ((flags & PacketHeaderFlags.AckSequence) != 0) // 0x4000
        {
            if (!Take(packet, ref cursor, 4, out var b)) return;
            writer.Write(b);
            AckSequence = BinaryPrimitives.ReadUInt32LittleEndian(b);
        }

        // LoginRequest / WorldLoginRequest / ConnectResponse / CICMDCommand
        // optional headers are server-side-only inputs for this spike — we
        // never RECEIVE packets with those flags set, so we omit them here.
        // If you add inbound handling for those, port the corresponding
        // blocks from the ACE-bots source.

        if ((flags & PacketHeaderFlags.TimeSync) != 0) // 0x1000000
        {
            if (!Take(packet, ref cursor, 8, out var b)) return;
            writer.Write(b);
            TimeSync = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b));
        }

        if ((flags & PacketHeaderFlags.EchoRequest) != 0) // 0x2000000
        {
            if (!Take(packet, ref cursor, 4, out var b)) return;
            writer.Write(b);
            EchoRequestClientTime = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b));
        }

        if ((flags & PacketHeaderFlags.Flow) != 0) // 0x8000000
        {
            if (!Take(packet, ref cursor, 6, out var b)) return;
            writer.Write(b);
            FlowBytes    = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0, 4));
            FlowInterval = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4, 2));
        }

        Size = cursor - start;
    }

    private bool Take(ReadOnlySpan<byte> packet, ref int cursor, int count, out byte[] bytes)
    {
        if (packet.Length - cursor < count)
        {
            IsValid = false;
            bytes = Array.Empty<byte>();
            return false;
        }
        bytes = packet.Slice(cursor, count).ToArray();
        cursor += count;
        return true;
    }

    public uint CalculateHash32()
    {
        if (Size == 0) return 0u;
        return Hash32.Calculate(_captured.GetBuffer(), Size);
    }
}
