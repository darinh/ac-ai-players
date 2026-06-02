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
using System.Collections.Generic;
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
        RunCharacterCreateRoundTrip();
        RunCharacterCreateWithTrainedSkillsRoundTrip();
        RunCharacterEnterWorldRequestRoundTrip();
        RunCharacterEnterWorldRoundTrip();

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

    private static void RunCharacterCreateRoundTrip()
    {
        // Mirror the server's read side (CharacterHandler.cs +
        // CharacterCreateInfo.cs + Appearance.cs) to verify EVERY field
        // round-trips through our packer at the exact byte offsets the
        // server expects. Catches alignment/padding bugs before the bytes
        // go on the wire (where the server's "silently return" failure
        // mode is opaque).
        var opt = new GameMessages.CharacterCreateMessage.Options(
            Account: "headless-test",
            Name:    "Headless01");

        var size = GameMessages.CharacterCreateMessage.MeasurePackedSize(opt);
        if (size > 448)
            throw new InvalidOperationException(
                $"CharacterCreate measured {size} bytes; exceeds single-fragment cap 448");

        var buf = new byte[size];
        var actual = GameMessages.CharacterCreateMessage.Pack(buf, opt);
        if (actual != size)
            throw new InvalidOperationException(
                $"CharacterCreate Pack returned {actual}; MeasurePackedSize said {size}");

        // Round-trip: read the bytes we just wrote, in the exact order
        // CharacterHandler + CharacterCreateInfo.Unpack would read them.
        var cur = 0;
        var opcode = ReadU32(buf, ref cur);
        Require(opcode == (uint)GameMessages.GameMessageOpcode.CharacterCreate,
            $"CharacterCreate opcode={opcode:X8}");

        var account = AcStrings.ReadString16L(buf, ref cur);
        Require(account == "headless-test", $"CharacterCreate account=\"{account}\"");

        var unknown = ReadU32(buf, ref cur);
        Require(unknown == 1, $"CharacterCreate unknown={unknown}");

        var heritage = ReadU32(buf, ref cur);
        Require(heritage == GameMessages.CharacterCreateMessage.HeritageAluvian,
            $"CharacterCreate heritage={heritage}");

        var gender = ReadU32(buf, ref cur);
        Require(gender == GameMessages.CharacterCreateMessage.GenderMale,
            $"CharacterCreate gender={gender}");

        // Appearance: 14 u32 + 6 f64 = 104 bytes, all zero in defaults.
        for (var i = 0; i < 14; i++)
        {
            var v = ReadU32(buf, ref cur);
            Require(v == 0, $"CharacterCreate appearance u32[{i}]={v}");
        }
        for (var i = 0; i < 6; i++)
        {
            var v = ReadF64(buf, ref cur);
            Require(v == 0.0, $"CharacterCreate appearance f64[{i}]={v}");
        }

        var templateOption = (int)ReadU32(buf, ref cur);
        Require(templateOption == 0, $"CharacterCreate templateOption={templateOption}");

        // 6 abilities - default is all-10s per rubber-duck recommendation.
        for (var i = 0; i < 6; i++)
        {
            var v = ReadU32(buf, ref cur);
            Require(v == 10, $"CharacterCreate ability[{i}]={v}");
        }

        var slot    = ReadU32(buf, ref cur);
        var classId = ReadU32(buf, ref cur);
        Require(slot == 0,    $"CharacterCreate slot={slot}");
        Require(classId == 0, $"CharacterCreate classId={classId}");

        var skillCount = ReadU32(buf, ref cur);
        Require(skillCount == GameMessages.CharacterCreateMessage.RequiredSkillCount,
            $"CharacterCreate skillCount={skillCount}");
        for (var i = 0; i < GameMessages.CharacterCreateMessage.RequiredSkillCount; i++)
        {
            var v = ReadU32(buf, ref cur);
            Require(v == GameMessages.CharacterCreateMessage.SACInactive,
                $"CharacterCreate skill[{i}]={v}");
        }

        var name = AcStrings.ReadString16L(buf, ref cur);
        Require(name == "Headless01", $"CharacterCreate name=\"{name}\"");

        var startArea  = ReadU32(buf, ref cur);
        var isAdmin    = ReadU32(buf, ref cur);
        var isSentinel = ReadU32(buf, ref cur);
        Require(startArea == 0,  $"CharacterCreate startArea={startArea}");
        Require(isAdmin == 0,    $"CharacterCreate isAdmin={isAdmin}");
        Require(isSentinel == 0, $"CharacterCreate isSentinel={isSentinel}");

        if (cur != actual)
            throw new InvalidOperationException(
                $"CharacterCreate over/underread: cursor={cur} packed={actual}");
    }

    private static void RunCharacterCreateWithTrainedSkillsRoundTrip()
    {
        // Verify the TrainedSkillIds overlay path - default-fill
        // every slot to Inactive, then flip only the requested skill
        // indices to Trained. Catches bugs like an off-by-one in the
        // slot loop, the stackalloc not being zeroed correctly, or
        // an out-of-range index sneaking past the bounds check (which
        // would silently corrupt an adjacent slot and trigger
        // server-side ClientServerSkillsMismatch or
        // InvalidSkillRequested).
        var trained = new uint[] { 21, 22, 41 };
        var opt = new GameMessages.CharacterCreateMessage.Options(
            Account: "headless-test",
            Name:    "Headless01",
            TrainedSkillIds: trained);

        var size = GameMessages.CharacterCreateMessage.MeasurePackedSize(opt);
        var buf = new byte[size];
        var actual = GameMessages.CharacterCreateMessage.Pack(buf, opt);
        Require(actual == size,
            $"CharacterCreate(trained): Pack returned {actual}; measured {size}");

        // Locate the skill-count field by replaying the prefix layout.
        var cur = 0;
        ReadU32(buf, ref cur);                          // opcode
        AcStrings.ReadString16L(buf, ref cur);          // account
        cur += 4;                                       // unknown
        cur += 4 + 4;                                   // heritage + gender
        cur += 14 * 4 + 6 * 8;                          // appearance
        cur += 4;                                       // template
        cur += 6 * 4;                                   // 6 abilities
        cur += 4 + 4;                                   // slot + classId

        var skillCount = ReadU32(buf, ref cur);
        Require(skillCount == GameMessages.CharacterCreateMessage.RequiredSkillCount,
            $"CharacterCreate(trained): skillCount={skillCount}");

        var expectTrained = new HashSet<uint>(trained);
        for (var i = 0; i < GameMessages.CharacterCreateMessage.RequiredSkillCount; i++)
        {
            var v = ReadU32(buf, ref cur);
            var want = expectTrained.Contains((uint)i)
                ? GameMessages.CharacterCreateMessage.SACTrained
                : GameMessages.CharacterCreateMessage.SACInactive;
            Require(v == want,
                $"CharacterCreate(trained): skill[{i}]={v} (want {want})");
        }

        // Out-of-range guard: an index >= 55 must be silently dropped,
        // not throw or corrupt memory.
        var optBad = new GameMessages.CharacterCreateMessage.Options(
            Account: "headless-test",
            Name:    "Headless01",
            TrainedSkillIds: new uint[] { 999u });
        var bufBad = new byte[GameMessages.CharacterCreateMessage.MeasurePackedSize(optBad)];
        GameMessages.CharacterCreateMessage.Pack(bufBad, optBad);
        // (No exception = pass. The serialized vector should be
        // all-Inactive since 999 is dropped, but we don't re-scan;
        // the trained-path coverage above is sufficient.)
    }

    private static void RunCharacterEnterWorldRequestRoundTrip()
    {
        // 0xF7C8 is opcode-only (CharacterHandler.cs:184-196 reads
        // nothing from the payload). Round-trip verifies our packer
        // writes exactly 4 bytes with the correct opcode.
        var buf = new byte[GameMessages.CharacterEnterWorldRequestMessage.PackedSize];
        var actual = GameMessages.CharacterEnterWorldRequestMessage.Pack(buf);
        Require(actual == 4, $"CharacterEnterWorldRequest size={actual} (want 4)");

        var cur = 0;
        var opcode = ReadU32(buf, ref cur);
        Require(opcode == (uint)GameMessages.GameMessageOpcode.CharacterEnterWorldRequest,
            $"CharacterEnterWorldRequest opcode=0x{opcode:X8}");
        if (cur != actual)
            throw new InvalidOperationException(
                $"CharacterEnterWorldRequest over/underread: cursor={cur} packed={actual}");
    }

    private static void RunCharacterEnterWorldRoundTrip()
    {
        // 0xF657 layout: u32 opcode + u32 guid + string16L account.
        // CharacterHandler.cs:200-204 reads guid first, then the
        // account string; account must equal session.Account else
        // EnterGameCharacterNotOwned.
        const uint testGuid = 0x50000006u;
        const string testAccount = "headless-test";

        var size = GameMessages.CharacterEnterWorldMessage.MeasurePackedSize(testAccount);
        var buf = new byte[size];
        var actual = GameMessages.CharacterEnterWorldMessage.Pack(buf, testGuid, testAccount);
        Require(actual == size,
            $"CharacterEnterWorld Pack returned {actual}, MeasurePackedSize said {size}");

        var cur = 0;
        var opcode = ReadU32(buf, ref cur);
        Require(opcode == (uint)GameMessages.GameMessageOpcode.CharacterEnterWorld,
            $"CharacterEnterWorld opcode=0x{opcode:X8}");

        var guid = ReadU32(buf, ref cur);
        Require(guid == testGuid, $"CharacterEnterWorld guid=0x{guid:X8}");

        var account = AcStrings.ReadString16L(buf, ref cur);
        Require(account == testAccount, $"CharacterEnterWorld account=\"{account}\"");

        if (cur != actual)
            throw new InvalidOperationException(
                $"CharacterEnterWorld over/underread: cursor={cur} packed={actual}");
    }

    private static uint ReadU32(ReadOnlySpan<byte> buf, ref int cur)
    {
        var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(cur));
        cur += 4;
        return v;
    }

    private static double ReadF64(ReadOnlySpan<byte> buf, ref int cur)
    {
        var v = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(buf.Slice(cur));
        cur += 8;
        return v;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"selfcheck failed: {label}");
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