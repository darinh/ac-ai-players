// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the GameActionMoveToState packer (0xF61C).
// Verifies byte-exact wire layout against a hand-rolled reference
// derived from the ACE server's RawMotionState reader.
//
// Why these tests matter:
//   Unlike AutonomousPosition (fixed-size payload), MoveToState has
//   a variable-length RawMotionState prefix whose fields appear in
//   strict NUMERIC flag-bit order regardless of how the caller
//   passes them in. A bug in either the ordering or the
//   packedFlags bit-layout (low 11 bits = flags, high 21 bits =
//   commandListLength) would silently misparse server-side.
//
// Cross-reference:
//   Source/ACE.Server/Network/Motion/RawMotionState.cs:49-96
//     (the constructor that reads the wire format)
//   Source/ACE.Server/Network/GameAction/Actions/GameActionMoveToState.cs
//     (the server-side handler)
//   Source/ACE.Server/Network/Motion/MoveToState.cs
//     (the outer envelope: RawMotionState + Position + 4 ushorts +
//      ContactLongJump byte + align)

using System;
using System.Buffers.Binary;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionMoveToStateMessageTests
{
    // Flags=CurrentHoldKey|CurrentStyle|ForwardCommand|ForwardSpeed = 0x17.
    // Four flagged fields × 4B = 16B.
    // Total: 12B header + 4B packedFlags + 16B flagged + 32B position
    // + 8B seqs + 1B contact + 3B align = 76B.
    private const int ForwardWalkPackedSize = 76;

    [Fact]
    public void CalcPackedSize_ForwardWalk_Is76Bytes()
    {
        var motion = RawMotionStatePayload.ForwardMotion(
            HoldKey.None, MotionStance.NonCombat, MotionCommand.WalkForward, 1.0f);
        Assert.Equal(ForwardWalkPackedSize, GameActionMoveToStateMessage.CalcPackedSize(motion.Flags));
    }

    [Fact]
    public void CalcPackedSize_StopMotion_Is72Bytes()
    {
        // Stop = CurrentHoldKey|CurrentStyle|ForwardCommand (3 fields × 4B = 12B).
        // 12 + 4 + 12 + 32 + 8 + 1 + 3 = 72.
        var motion = RawMotionStatePayload.Stop(MotionStance.NonCombat);
        Assert.Equal(72, GameActionMoveToStateMessage.CalcPackedSize(motion.Flags));
    }

    [Fact]
    public void CalcPackedSize_NoFlags_Is60Bytes()
    {
        // No flagged fields: 12 + 4 + 0 + 32 + 8 + 1 + 3 = 60.
        Assert.Equal(60, GameActionMoveToStateMessage.CalcPackedSize(RawMotionFlags.None));
    }

    [Fact]
    public void Pack_ForwardWalk_WritesExpectedByteLayout()
    {
        var motion = RawMotionStatePayload.ForwardMotion(
            holdKey: HoldKey.None,
            stance:  MotionStance.NonCombat,
            command: MotionCommand.WalkForward,
            speed:   1.0f);

        var dest = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
        var cellId = 0x860201ADu;
        var pos    = new Vector3(12.5f, -28.25f, 0.125f);
        var rot    = new Quaternion(x: 0.1f, y: 0.2f, z: 0.3f, w: 0.4f);

        var written = GameActionMoveToStateMessage.Pack(
            dest,
            motion,
            cellId, pos, rot,
            instanceSequence:      0x1234,
            serverControlSequence: 0x5678,
            teleportSequence:      0x9ABC,
            forcePositionSequence: 0xDEF0,
            contact: true,
            standingLongJump: false,
            actionSequence: 1);

        Assert.Equal(dest.Length, written);

        // GameAction envelope: 4B opcode (0xF7B1) + 4B actionSeq + 4B actionOpcode.
        Assert.Equal(0xF7B1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(0, 4)));
        Assert.Equal(1u,      BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4, 4)));
        Assert.Equal(0xF61Cu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8, 4)));

        // packedFlags: low 11 bits = 0x17, high 21 bits = 0 (no commandList).
        Assert.Equal(0x17u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12, 4)));

        // Flagged fields in numeric order: 0x1 CurrentHoldKey, 0x2 CurrentStyle,
        // 0x4 ForwardCommand, 0x10 ForwardSpeed (no 0x8 ForwardHoldKey).
        Assert.Equal((uint)HoldKey.None,                BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16, 4)));
        Assert.Equal((uint)MotionStance.NonCombat,      BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(20, 4)));
        Assert.Equal((uint)MotionCommand.WalkForward,   BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(24, 4)));
        Assert.Equal(1.0f,                              BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(28, 4)));

        // 32B Position block at offset 32.
        Assert.Equal(cellId, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(32, 4)));
        Assert.Equal(pos.X,  BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(36, 4)));
        Assert.Equal(pos.Y,  BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(40, 4)));
        Assert.Equal(pos.Z,  BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(44, 4)));
        // W,X,Y,Z quaternion order — NOT C#'s default X,Y,Z,W.
        Assert.Equal(rot.W,  BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(48, 4)));
        Assert.Equal(rot.X,  BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(52, 4)));
        Assert.Equal(rot.Y,  BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(56, 4)));
        Assert.Equal(rot.Z,  BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(60, 4)));

        // 4 ushort sequences at offset 64.
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(64, 2)));
        Assert.Equal((ushort)0x5678, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(66, 2)));
        Assert.Equal((ushort)0x9ABC, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(68, 2)));
        Assert.Equal((ushort)0xDEF0, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(70, 2)));

        // ContactLongJump byte + 3B zero align.
        Assert.Equal(0x01, dest[72]);
        Assert.Equal(0,    dest[73]);
        Assert.Equal(0,    dest[74]);
        Assert.Equal(0,    dest[75]);
    }

    [Fact]
    public void Pack_ContactLongJumpByte_PacksBitsCorrectly()
    {
        // bit 0 = Contact, bit 1 = StandingLongJump.
        foreach (var (contact, longJump, expected) in new[]
        {
            (false, false, (byte)0x00),
            (true,  false, (byte)0x01),
            (false, true,  (byte)0x02),
            (true,  true,  (byte)0x03),
        })
        {
            var motion = RawMotionStatePayload.ForwardMotion(
                HoldKey.None, MotionStance.NonCombat, MotionCommand.WalkForward, 1.0f);
            var dest = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
            GameActionMoveToStateMessage.Pack(
                dest, motion,
                cellId: 0x86020000, pos: Vector3.Zero, rot: Quaternion.Identity,
                instanceSequence: 0, serverControlSequence: 0,
                teleportSequence: 0, forcePositionSequence: 0,
                contact: contact, standingLongJump: longJump);

            // ContactLongJump sits at dest.Length - 4.
            Assert.Equal(expected, dest[dest.Length - 4]);
        }
    }

    [Fact]
    public void Pack_FlagBitOrder_EmittedInStrictNumericOrder()
    {
        // Build a payload with all 11 flags set. Confirm each flagged
        // field comes out in numeric order (0x1, 0x2, ..., 0x400),
        // not in field-declaration order or any caller-influenced
        // order. We use sentinel values per slot so we can identify
        // which one we read at each offset.
        var motion = new RawMotionStatePayload(
            Flags:
                RawMotionFlags.CurrentHoldKey | RawMotionFlags.CurrentStyle |
                RawMotionFlags.ForwardCommand | RawMotionFlags.ForwardHoldKey |
                RawMotionFlags.ForwardSpeed   | RawMotionFlags.SideStepCommand |
                RawMotionFlags.SideStepHoldKey | RawMotionFlags.SideStepSpeed |
                RawMotionFlags.TurnCommand    | RawMotionFlags.TurnHoldKey |
                RawMotionFlags.TurnSpeed,
            CurrentHoldKey:  (HoldKey)0xAAAAAAAA,
            CurrentStyle:    (MotionStance)0xBBBBBBBB,
            ForwardCommand:  (MotionCommand)0xCCCCCCCC,
            ForwardHoldKey:  (HoldKey)0xDDDDDDDD,
            ForwardSpeed:    2.5f,
            SidestepCommand: (MotionCommand)0xEEEEEEEE,
            SidestepHoldKey: (HoldKey)0xFFFFFFF1,
            SidestepSpeed:   3.5f,
            TurnCommand:     (MotionCommand)0x12345678,
            TurnHoldKey:     (HoldKey)0x9ABCDEF0,
            TurnSpeed:       4.5f);

        var dest = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
        GameActionMoveToStateMessage.Pack(
            dest, motion,
            cellId: 0x86020000, pos: Vector3.Zero, rot: Quaternion.Identity,
            instanceSequence: 0, serverControlSequence: 0,
            teleportSequence: 0, forcePositionSequence: 0,
            contact: true);

        // After 12B header + 4B packedFlags, the flagged fields begin
        // at offset 16 and are 4B each, in numeric flag order.
        var off = 16;
        Assert.Equal(0xAAAAAAAAu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(0xBBBBBBBBu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(0xCCCCCCCCu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(0xDDDDDDDDu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(2.5f,        BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(0xEEEEEEEEu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(0xFFFFFFF1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(3.5f,        BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(0x9ABCDEF0u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(off, 4))); off += 4;
        Assert.Equal(4.5f,        BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(off, 4))); off += 4;
    }

    [Fact]
    public void Pack_NonzeroCommandListLength_ShiftsBy11_NotBy12()
    {
        // The misleading comment in MoveToState.cs hints at bit 12,
        // but the parser at RawMotionState.cs:56-58 actually does
        // (packed & 0x7FF) for flags and (packed >> 11) for the
        // command list length. This test pins the shift at 11.
        var motion = RawMotionStatePayload.ForwardMotion(
            HoldKey.None, MotionStance.NonCombat, MotionCommand.WalkForward, 1.0f)
            with { CommandListLength = 5 };

        var dest = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
        GameActionMoveToStateMessage.Pack(
            dest, motion,
            cellId: 0x86020000, pos: Vector3.Zero, rot: Quaternion.Identity,
            instanceSequence: 0, serverControlSequence: 0,
            teleportSequence: 0, forcePositionSequence: 0,
            contact: true);

        var packedFlags = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12, 4));
        Assert.Equal(0x17u, packedFlags & 0x7FFu);
        Assert.Equal(5u,    packedFlags >> 11);
        // And specifically NOT shift-by-12:
        Assert.NotEqual(5u, packedFlags >> 12);
    }

    [Fact]
    public void Pack_FlagsOnlyMaskedToLow11Bits()
    {
        // Defensive check: if a caller somehow passes a flags value
        // with bit 11+ set (currently impossible via the enum, but
        // we mask anyway), the packer should NOT let those bits
        // leak into the commandListLength region.
        var motion = new RawMotionStatePayload(
            Flags: (RawMotionFlags)0xFFFFFFFFu,
            CurrentHoldKey:  HoldKey.None,
            CurrentStyle:    MotionStance.NonCombat,
            ForwardCommand:  MotionCommand.WalkForward,
            ForwardHoldKey:  HoldKey.None,
            ForwardSpeed:    1f,
            SidestepCommand: MotionCommand.Invalid,
            SidestepHoldKey: HoldKey.None,
            SidestepSpeed:   0f,
            TurnCommand:     MotionCommand.Invalid,
            TurnHoldKey:     HoldKey.None,
            TurnSpeed:       0f,
            CommandListLength: 3);

        var dest = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags)];
        GameActionMoveToStateMessage.Pack(
            dest, motion,
            cellId: 0x86020000, pos: Vector3.Zero, rot: Quaternion.Identity,
            instanceSequence: 0, serverControlSequence: 0,
            teleportSequence: 0, forcePositionSequence: 0,
            contact: true);

        var packedFlags = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12, 4));
        Assert.Equal(0x7FFu, packedFlags & 0x7FFu);
        Assert.Equal(3u,     packedFlags >> 11);
    }

    [Fact]
    public void Pack_BufferTooSmall_Throws()
    {
        var motion = RawMotionStatePayload.ForwardMotion(
            HoldKey.None, MotionStance.NonCombat, MotionCommand.WalkForward, 1.0f);
        var tooSmall = new byte[GameActionMoveToStateMessage.CalcPackedSize(motion.Flags) - 1];
        Assert.Throws<ArgumentException>(() =>
            GameActionMoveToStateMessage.Pack(
                tooSmall, motion,
                cellId: 0x86020000, pos: Vector3.Zero, rot: Quaternion.Identity,
                instanceSequence: 0, serverControlSequence: 0,
                teleportSequence: 0, forcePositionSequence: 0,
                contact: true));
    }
}
