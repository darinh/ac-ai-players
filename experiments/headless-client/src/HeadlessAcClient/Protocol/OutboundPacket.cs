// SPDX-License-Identifier: AGPL-3.0-or-later
// Outbound packet builder. Mirrors the relevant bits of
//   Source/ACE.Server/Network/ServerPacket.cs
// for the client side.
//
// Optional-header bytes go directly into the body in this strict
// order (the same order PacketHeaderOptional.Unpack consumes them
// on the receive side):
//     ServerSwitch, RequestRetransmit, RejectRetransmit,
//     AckSequence, LoginRequest, WorldLoginRequest, ConnectResponse,
//     CICMDCommand, TimeSync, EchoRequest, Flow
//
// Checksum (matches ServerPacket.CreateReadyToSendPacket):
//     headerChecksum  = Hash32(header with Checksum=0xBADD70DD)
//     payloadChecksum = Hash32(body, body.Length)
//                       + per-fragment(Hash32(fragHeader)+Hash32(fragData))
//     header.Checksum =
//       unencrypted: headerChecksum + payloadChecksum
//       encrypted  : headerChecksum + (payloadChecksum ^ issacXor)
//
// For Phase 2 we only emit packets with optional-header bytes
// (AckSequence, TimeSync) and no fragments. Fragment emission lands
// in Phase 3 when we send GameActionCharacterEnterWorld.

using System;
using System.IO;

using HeadlessAcClient.Crypto;

namespace HeadlessAcClient.Protocol;

internal sealed class OutboundPacket
{
    private readonly MemoryStream _body = new();
    private readonly BinaryWriter _bw;
    private PacketHeaderFlags _flags;

    public OutboundPacket() { _bw = new BinaryWriter(_body); }

    public PacketHeaderFlags Flags => _flags;
    public int BodyLength => (int)_body.Length;

    public void AddAckSequence(uint lastReceivedSeq)
    {
        _flags |= PacketHeaderFlags.AckSequence;
        _bw.Write(lastReceivedSeq);
    }

    public void AddTimeSync(double timestamp)
    {
        _flags |= PacketHeaderFlags.TimeSync;
        _bw.Write(timestamp);
    }

    /// <summary>
    /// Pack the full datagram (20-byte header + body) into <paramref name="buffer"/>
    /// and return its length. <paramref name="cryptoSend"/> is consulted only when
    /// <paramref name="encrypt"/> is true; in that case the EncryptedChecksum
    /// flag is set and one ISAAC keystream value is consumed.
    /// </summary>
    public int Pack(Span<byte> buffer, ushort clientId, uint sequence, ushort iteration,
                    bool encrypt, CryptoSystem? cryptoSend)
    {
        if (encrypt) _flags |= PacketHeaderFlags.EncryptedChecksum;

        var bodyBytes = _body.GetBuffer();
        var bodyLen   = (int)_body.Length;

        if (PacketHeader.HeaderSize + bodyLen > buffer.Length)
            throw new InvalidOperationException("buffer too small");

        bodyBytes.AsSpan(0, bodyLen).CopyTo(buffer.Slice(PacketHeader.HeaderSize, bodyLen));

        var header = new PacketHeader
        {
            Sequence  = sequence,
            Flags     = _flags,
            Checksum  = 0,
            Id        = clientId,
            Time      = 0,
            Size      = (ushort)bodyLen,
            Iteration = iteration,
        };

        var headerChecksum  = header.CalculateHash32();
        var payloadChecksum = Hash32.Calculate(bodyBytes.AsSpan(0, bodyLen), bodyLen);

        if (encrypt)
        {
            var xor = cryptoSend!.PeekCurrentKey();
            cryptoSend.ConsumeKey(xor);
            header.Checksum = headerChecksum + (payloadChecksum ^ xor);
        }
        else
        {
            header.Checksum = headerChecksum + payloadChecksum;
        }

        header.Pack(buffer.Slice(0, PacketHeader.HeaderSize));
        return PacketHeader.HeaderSize + bodyLen;
    }
}
