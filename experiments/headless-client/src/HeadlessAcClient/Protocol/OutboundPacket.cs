// SPDX-License-Identifier: AGPL-3.0-or-later
// Outbound packet builder. Mirrors the relevant bits of
//   Source/ACE.Server/Network/ServerPacket.cs
// for the client side.
//
// PACKET BODY LAYOUT (wire order):
//
//   [optional-header bytes][frag1 hdr(16B)][frag1 data][frag2 hdr][frag2 data]...
//
// Optional-header bytes go directly into the body in this strict
// order (the same order PacketHeaderOptional.Unpack consumes them
// on the receive side):
//     ServerSwitch, RequestRetransmit, RejectRetransmit,
//     AckSequence, LoginRequest, WorldLoginRequest, ConnectResponse,
//     CICMDCommand, TimeSync, EchoRequest, Flow
//
// CHECKSUM (matches ClientPacket.VerifyCRC + ServerPacket.CreateReadyToSendPacket):
//     headerChecksum   = Hash32(header with Checksum=0xBADD70DD)
//     payloadChecksum  = Hash32(optional-header bytes)
//                      + Σ_per_frag( Hash32(16B fragHeader) + Hash32(fragData) )
//     header.Checksum  =
//       unencrypted: headerChecksum + payloadChecksum
//       encrypted  : headerChecksum + (payloadChecksum ^ isaacKey)
//
// One ISAAC key is drawn per encrypted PACKET (not per fragment) —
// confirmed at ClientPacket.cs:140-146 + NetworkSession.cs:763-768.
//
// FRAGMENT INVARIANTS (verified via rubber-duck + source dive):
//   * Per-fragment wire cap: PacketFragmentHeader.HeaderSize + data.Length
//     MUST be <= 464 (PacketFragment.MaxFragementSize). Data alone <= 448.
//   * data.Length MUST be >= 4 — server's HandleFragment skips messages with
//     payload < 4 bytes (NetworkSession.cs:545-546), leaving the per-message
//     fragment sequence forever unadvanced and the next real message stuck.
//     A bare opcode (4 bytes) is the minimum legal probe.
//   * Per-message Sequence is shared across all fragments of one game message;
//     Index varies 0..Count-1. Server-side reassembly keys off Header.Sequence
//     (NetworkSession.cs:42, 515-537).
//
// All checksum additions are wrapped in `unchecked` so the modulo-2^32
// behavior is explicit and immune to a future <CheckedArithmetic>true</> flip.

using System;
using System.Collections.Generic;
using System.IO;

using HeadlessAcClient.Crypto;

namespace HeadlessAcClient.Protocol;

internal sealed class OutboundPacket
{
    private const int MaxFragmentWireSize = 464;
    private const int MaxFragmentDataSize = MaxFragmentWireSize - PacketFragmentHeader.HeaderSize; // 448
    private const int MinFragmentDataSize = 4;  // see NetworkSession.cs:545

    private readonly MemoryStream _optionalBytes = new();
    private readonly BinaryWriter _ow;
    private readonly List<Fragment> _fragments = new();
    private PacketHeaderFlags _flags;

    public OutboundPacket() { _ow = new BinaryWriter(_optionalBytes); }

    public PacketHeaderFlags Flags => _flags;
    public int OptionalLength => (int)_optionalBytes.Length;
    public int FragmentCount  => _fragments.Count;

    public void AddAckSequence(uint lastReceivedSeq)
    {
        _flags |= PacketHeaderFlags.AckSequence;
        _ow.Write(lastReceivedSeq);
    }

    public void AddTimeSync(double timestamp)
    {
        _flags |= PacketHeaderFlags.TimeSync;
        _ow.Write(timestamp);
    }

    /// <summary>
    /// Append a single fragment carrying one complete game message.
    /// For Phase 3 we always emit Count=1, Index=0 (no multi-fragment splits
    /// yet — that lands when we need to ship a message > 448 bytes).
    /// </summary>
    /// <param name="fragSequence">Per-message sequence; caller advances by 1 per
    /// complete game message (NOT per fragment within a message).</param>
    /// <param name="fragId">Server uses 0x80000000 as a constant marker
    /// (MessageFragment.cs:94). Client-side, the field appears to be ignored on
    /// inbound — pick any non-high-bit value. Keep as uint so future probes
    /// can experiment with 0x80000001 etc.</param>
    /// <param name="queue">GameMessageGroup. Use ControlQueue (0x02) /
    /// WeenieQueue (0x03) / etc. depending on the message class.</param>
    /// <param name="gameMessagePayload">Full game-message bytes (u32 opcode LE
    /// + body). Must be 4..448 bytes for a single-fragment packet.</param>
    public void AddBlobFragment(
        uint fragSequence,
        uint fragId,
        ushort queue,
        ReadOnlySpan<byte> gameMessagePayload)
    {
        if (gameMessagePayload.Length < MinFragmentDataSize)
            throw new ArgumentException(
                $"fragment data must be >= {MinFragmentDataSize} bytes (server drops shorter messages without advancing fragment sequence); got {gameMessagePayload.Length}",
                nameof(gameMessagePayload));
        if (gameMessagePayload.Length > MaxFragmentDataSize)
            throw new ArgumentException(
                $"fragment data must be <= {MaxFragmentDataSize} bytes for a single-fragment packet (per-fragment wire cap {MaxFragmentWireSize}); got {gameMessagePayload.Length}. Multi-fragment splitting not yet implemented.",
                nameof(gameMessagePayload));

        _flags |= PacketHeaderFlags.BlobFragments;

        var fh = new PacketFragmentHeader
        {
            Sequence = fragSequence,
            Id       = fragId,
            Count    = 1,
            Size     = (ushort)(PacketFragmentHeader.HeaderSize + gameMessagePayload.Length),
            Index    = 0,
            Queue    = queue,
        };
        var headerBytes = new byte[PacketFragmentHeader.HeaderSize];
        fh.Pack(headerBytes);

        // Copy payload — caller's span may be backed by a buffer that mutates
        // between AddBlobFragment and Pack.
        var dataCopy = gameMessagePayload.ToArray();

        _fragments.Add(new Fragment(headerBytes, dataCopy));
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
        if (encrypt)
        {
            if (cryptoSend is null)
                throw new ArgumentNullException(nameof(cryptoSend),
                    "encrypted packets require a CryptoSystem to draw an ISAAC key from");
            _flags |= PacketHeaderFlags.EncryptedChecksum;
        }

        var optionalLen = (int)_optionalBytes.Length;
        var optionalBuf = _optionalBytes.GetBuffer();

        var fragsLen = 0;
        foreach (var f in _fragments)
            fragsLen += f.HeaderBytes.Length + f.Data.Length;

        var bodyLen = optionalLen + fragsLen;
        if (PacketHeader.HeaderSize + bodyLen > buffer.Length)
            throw new InvalidOperationException(
                $"output buffer too small: need {PacketHeader.HeaderSize + bodyLen} bytes, have {buffer.Length}");

        // Lay out body: [optional bytes][frag1 hdr+data][frag2 hdr+data]...
        var pos = PacketHeader.HeaderSize;
        if (optionalLen > 0)
        {
            optionalBuf.AsSpan(0, optionalLen).CopyTo(buffer.Slice(pos, optionalLen));
            pos += optionalLen;
        }
        foreach (var f in _fragments)
        {
            f.HeaderBytes.AsSpan().CopyTo(buffer.Slice(pos, f.HeaderBytes.Length));
            pos += f.HeaderBytes.Length;
            f.Data.AsSpan().CopyTo(buffer.Slice(pos, f.Data.Length));
            pos += f.Data.Length;
        }

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

        var headerChecksum = header.CalculateHash32();

        // payloadChecksum = Hash32(optional-bytes) + Σ_per_frag(Hash32(fragHdr) + Hash32(data))
        // All adds in `unchecked` for explicit modulo-2^32 (mirrors server).
        uint payloadChecksum;
        unchecked
        {
            payloadChecksum = optionalLen > 0
                ? Hash32.Calculate(optionalBuf.AsSpan(0, optionalLen), optionalLen)
                : 0u;
            foreach (var f in _fragments)
            {
                payloadChecksum += Hash32.Calculate(f.HeaderBytes, f.HeaderBytes.Length);
                payloadChecksum += Hash32.Calculate(f.Data, f.Data.Length);
            }
        }

        unchecked
        {
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
        }

        header.Pack(buffer.Slice(0, PacketHeader.HeaderSize));
        return PacketHeader.HeaderSize + bodyLen;
    }

    private readonly record struct Fragment(byte[] HeaderBytes, byte[] Data);
}
