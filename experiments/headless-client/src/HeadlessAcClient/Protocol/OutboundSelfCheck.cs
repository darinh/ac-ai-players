// SPDX-License-Identifier: AGPL-3.0-or-later
// Round-trip self-check for OutboundPacket.
//
// Builds a small set of representative packets, then runs them back through
// InboundPacket.Unpack + VerifyCRC to confirm the body layout, fragment
// header encoding, and checksum chain match what the server's ClientPacket
// decoder expects. Cheap insurance: a corrupt packer would otherwise show
// up only as silent server-side drops with no diagnostic.
//
// Run once at startup from Program.cs. Throws on any mismatch.

using System;
using System.Net;
using System.Text;

using HeadlessAcClient.Crypto;

namespace HeadlessAcClient.Protocol;

internal static class OutboundSelfCheck
{
    public static void Run()
    {
        Console.WriteLine("[selfcheck] OutboundPacket round-trip ...");

        RunPlainOptionalOnly();
        RunPlainSingleFragment();
        RunPlainMultipleFragments();
        RunPlainOptionalPlusFragment();
        RunEncryptedSingleFragment();
        RunFragmentSizeGuards();

        Console.WriteLine("[selfcheck] OutboundPacket round-trip OK");
    }

    private static void RunPlainOptionalOnly()
    {
        var p = new OutboundPacket();
        p.AddAckSequence(42);
        p.AddTimeSync(123456.789);

        var buf = new byte[1024];
        var len = p.Pack(buf, clientId: 0x1234, sequence: 0, iteration: 1,
                         encrypt: false, cryptoSend: null);

        var inb = UnpackOrFail(buf, len, "plain optional-only");
        if (!inb.VerifyCRC(MakeCryptoSystem()))
            throw new InvalidOperationException("plain optional-only: VerifyCRC failed");
        if (inb.Optional.AckSequence != 42)
            throw new InvalidOperationException($"plain optional-only: AckSequence={inb.Optional.AckSequence}");
        if (Math.Abs(inb.Optional.TimeSync - 123456.789) > 1e-9)
            throw new InvalidOperationException($"plain optional-only: TimeSync={inb.Optional.TimeSync}");
        if (inb.Fragments.Count != 0)
            throw new InvalidOperationException("plain optional-only: unexpected fragments");
    }

    private static void RunPlainSingleFragment()
    {
        var payload = MakeOpcodePayload(0xFFFE, suffix: new byte[] { 0xDE, 0xAD });
        var p = new OutboundPacket();
        p.AddBlobFragment(fragSequence: 7, fragId: 1, queue: 3, gameMessagePayload: payload);

        var buf = new byte[1024];
        var len = p.Pack(buf, clientId: 0xABCD, sequence: 2, iteration: 1,
                         encrypt: false, cryptoSend: null);

        var inb = UnpackOrFail(buf, len, "plain single-fragment");
        if (!inb.VerifyCRC(MakeCryptoSystem()))
            throw new InvalidOperationException("plain single-fragment: VerifyCRC failed");
        if (inb.Fragments.Count != 1)
            throw new InvalidOperationException($"plain single-fragment: Fragments.Count={inb.Fragments.Count}");
        var (fh, data) = inb.Fragments[0];
        if (fh.Sequence != 7 || fh.Id != 1 || fh.Count != 1 || fh.Index != 0 || fh.Queue != 3)
            throw new InvalidOperationException($"plain single-fragment: header mismatch {fh}");
        if (data.Length != payload.Length || !data.AsSpan().SequenceEqual(payload))
            throw new InvalidOperationException("plain single-fragment: data mismatch");
    }

    private static void RunPlainMultipleFragments()
    {
        var a = MakeOpcodePayload(0xFFFE, suffix: new byte[] { 0xAA, 0xBB });
        var b = MakeOpcodePayload(0xFFFD, suffix: new byte[] { 0xCC });
        var p = new OutboundPacket();
        p.AddBlobFragment(fragSequence: 10, fragId: 1, queue: 3, gameMessagePayload: a);
        p.AddBlobFragment(fragSequence: 11, fragId: 1, queue: 3, gameMessagePayload: b);

        var buf = new byte[1024];
        var len = p.Pack(buf, clientId: 0x4242, sequence: 3, iteration: 1,
                         encrypt: false, cryptoSend: null);

        var inb = UnpackOrFail(buf, len, "plain multi-fragment");
        if (!inb.VerifyCRC(MakeCryptoSystem()))
            throw new InvalidOperationException("plain multi-fragment: VerifyCRC failed");
        if (inb.Fragments.Count != 2)
            throw new InvalidOperationException($"plain multi-fragment: Fragments.Count={inb.Fragments.Count}");
        if (inb.Fragments[0].Header.Sequence != 10 || inb.Fragments[1].Header.Sequence != 11)
            throw new InvalidOperationException("plain multi-fragment: sequence mismatch");
    }

    private static void RunPlainOptionalPlusFragment()
    {
        var payload = MakeOpcodePayload(0xFFFE, suffix: new byte[] { 0x01 });
        var p = new OutboundPacket();
        p.AddAckSequence(99);
        p.AddBlobFragment(fragSequence: 1, fragId: 1, queue: 3, gameMessagePayload: payload);

        var buf = new byte[1024];
        var len = p.Pack(buf, clientId: 0xBEEF, sequence: 2, iteration: 1,
                         encrypt: false, cryptoSend: null);

        var inb = UnpackOrFail(buf, len, "plain optional+fragment");
        if (!inb.VerifyCRC(MakeCryptoSystem()))
            throw new InvalidOperationException("plain optional+fragment: VerifyCRC failed");
        if (inb.Optional.AckSequence != 99)
            throw new InvalidOperationException($"plain optional+fragment: AckSequence={inb.Optional.AckSequence}");
        if (inb.Fragments.Count != 1)
            throw new InvalidOperationException("plain optional+fragment: missing fragment");
    }

    private static void RunEncryptedSingleFragment()
    {
        // Build two CryptoSystem instances seeded identically so the sender
        // consumes key K, then the receiver matches and consumes the same K.
        var sender   = MakeCryptoSystem();
        var receiver = MakeCryptoSystem();

        var payload = MakeOpcodePayload(0xFFFE, suffix: new byte[] { 0xFE });
        var p = new OutboundPacket();
        p.AddBlobFragment(fragSequence: 5, fragId: 1, queue: 3, gameMessagePayload: payload);

        var buf = new byte[1024];
        var len = p.Pack(buf, clientId: 0xCAFE, sequence: 2, iteration: 1,
                         encrypt: true, cryptoSend: sender);

        var inb = UnpackOrFail(buf, len, "encrypted single-fragment");
        if (!inb.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
            throw new InvalidOperationException("encrypted single-fragment: EncryptedChecksum flag not set");
        if (!inb.VerifyCRC(receiver))
            throw new InvalidOperationException("encrypted single-fragment: VerifyCRC failed (ISAAC mismatch)");
    }

    private static void RunFragmentSizeGuards()
    {
        // Sub-4-byte payload must throw — server's HandleFragment silently
        // drops these AND fails to advance the per-message sequence, leaving
        // the next real message stuck forever.
        try
        {
            new OutboundPacket().AddBlobFragment(1, 1, 3, new byte[] { 0xFE, 0xFF, 0x00 });
            throw new InvalidOperationException("guard: 3-byte payload should have thrown");
        }
        catch (ArgumentException) { /* expected */ }

        // 449-byte payload exceeds per-fragment cap (16+449 > 464). Must throw.
        try
        {
            new OutboundPacket().AddBlobFragment(1, 1, 3, new byte[449]);
            throw new InvalidOperationException("guard: 449-byte payload should have thrown");
        }
        catch (ArgumentException) { /* expected */ }
    }

    /// <summary>
    /// Build a game-message payload: little-endian u32 opcode + suffix bytes.
    /// Min payload length is 4 (just the opcode).
    /// </summary>
    private static byte[] MakeOpcodePayload(uint opcode, byte[] suffix)
    {
        var buf = new byte[4 + suffix.Length];
        BitConverterLE.WriteU32(buf, 0, opcode);
        suffix.CopyTo(buf, 4);
        return buf;
    }

    private static InboundPacket UnpackOrFail(byte[] buf, int len, string label)
    {
        var inb = new InboundPacket();
        if (!inb.Unpack(buf, len))
            throw new InvalidOperationException($"{label}: InboundPacket.Unpack failed for {len} bytes");
        return inb;
    }

    /// <summary>
    /// Deterministic ISAAC seed for self-check. Same seed → same keystream,
    /// so sender and receiver instances stay in lockstep.
    /// </summary>
    private static CryptoSystem MakeCryptoSystem()
    {
        // 4-byte seed. Value chosen arbitrarily; what matters is that both
        // ends agree.
        return new CryptoSystem(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
    }
}

internal static class BitConverterLE
{
    public static void WriteU32(Span<byte> buf, int offset, uint value)
    {
        buf[offset]     = (byte)(value      );
        buf[offset + 1] = (byte)(value >> 8 );
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}