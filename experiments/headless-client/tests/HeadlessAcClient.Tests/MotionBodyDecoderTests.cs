// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for MotionBodyDecoder — the polymorphic decoder for the
// Motion (0xF74C) message body.
//
// Each variant test hand-rolls the wire bytes (matching the ACE
// server's BinaryWriter output) and verifies the decoder
// reconstructs the same fields with the right types and ordering.
//
// Cross-reference:
//   Source/ACE.Server/Network/Motion/MovementData.cs:182-230
//   Source/ACE.Server/Network/Motion/InterpretedMotionState.cs:125-164
//   Source/ACE.Server/Network/Motion/MoveToObject.cs
//   Source/ACE.Server/Network/Motion/MoveToPosition.cs
//   Source/ACE.Server/Network/Motion/TurnToObject.cs
//   Source/ACE.Server/Network/Motion/TurnToHeading.cs
//   Source/ACE.Server/Network/Motion/MoveToParameters.cs
//   Source/ACE.Server/Network/Motion/TurnToParameters.cs

using System;
using System.Buffers.Binary;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class MotionBodyDecoderTests
{
    private static byte[] BuildInterpretedState(
        uint flagsAndCount,
        Action<MemoryWriter> fields)
    {
        var w = new MemoryWriter();
        w.WriteU32(flagsAndCount);
        fields(w);
        w.Align(4);
        return w.ToArray();
    }

    [Fact]
    public void Decode_Invalid_AllFlagsZero_NoCommands_ReturnsEmptyState()
    {
        // Just the 4B packed=0, then aligned to 4 (no padding needed).
        var body = BuildInterpretedState(0, _ => { });
        Assert.Equal(4, body.Length);

        var decoded = MotionBodyDecoder.Decode(body, MovementType.Invalid, MotionFlags.None);

        Assert.NotNull(decoded);
        Assert.Equal(MovementType.Invalid, decoded!.MovementType);
        Assert.NotNull(decoded.Invalid);
        Assert.Equal(MovementStateFlag.Invalid, decoded.Invalid!.State.Flags);
        Assert.Equal(0, decoded.Invalid.State.NumCommands);
        Assert.Empty(decoded.Invalid.State.Commands);
        Assert.Null(decoded.Invalid.StickyObjectGuid);
    }

    [Fact]
    public void Decode_Invalid_CurrentStyleAndForwardCommand_ReadsUshortsInOrder()
    {
        // flags = CurrentStyle(0x1) | ForwardCommand(0x2) = 0x3
        // numCommands = 0
        // packed = 0x3
        // Then: u16 currentStyle, u16 forwardCommand, align (already 8 -> 4-aligned).
        var body = BuildInterpretedState(0x3, w =>
        {
            w.WriteU16(0x003D);  // currentStyle (low 16 bits of NonCombat=0x8000003D)
            w.WriteU16(0x0005);  // forwardCommand (low 16 bits of WalkForward=0x45000005)
        });

        var decoded = MotionBodyDecoder.Decode(body, MovementType.Invalid, MotionFlags.None);
        Assert.NotNull(decoded?.Invalid);
        var s = decoded!.Invalid!.State;
        Assert.Equal(MovementStateFlag.CurrentStyle | MovementStateFlag.ForwardCommand, s.Flags);
        Assert.Equal((ushort)0x003D, s.CurrentStyle);
        Assert.Equal((ushort)0x0005, s.ForwardCommand);
        Assert.Null(s.SidestepCommand);
        Assert.Null(s.TurnCommand);
        Assert.Null(s.ForwardSpeed);
    }

    [Fact]
    public void Decode_Invalid_AllSevenFlags_ReadsCommandsFirstThenSpeeds()
    {
        // flags = all 7 = 0x7F, numCommands = 0, packed = 0x7F.
        // Wire order: CurrentStyle, ForwardCommand, SidestepCommand,
        // TurnCommand (all u16), THEN ForwardSpeed, SidestepSpeed,
        // TurnSpeed (all f32). This is NOT numeric bit order.
        var body = BuildInterpretedState(0x7F, w =>
        {
            w.WriteU16(0x1111);  // CurrentStyle
            w.WriteU16(0x2222);  // ForwardCommand
            w.WriteU16(0x3333);  // SidestepCommand
            w.WriteU16(0x4444);  // TurnCommand
            w.WriteF32(1.5f);    // ForwardSpeed
            w.WriteF32(2.5f);    // SidestepSpeed
            w.WriteF32(3.5f);    // TurnSpeed
        });

        var decoded = MotionBodyDecoder.Decode(body, MovementType.Invalid, MotionFlags.None);
        Assert.NotNull(decoded?.Invalid);
        var s = decoded!.Invalid!.State;
        Assert.Equal((ushort)0x1111, s.CurrentStyle);
        Assert.Equal((ushort)0x2222, s.ForwardCommand);
        Assert.Equal((ushort)0x3333, s.SidestepCommand);
        Assert.Equal((ushort)0x4444, s.TurnCommand);
        Assert.Equal(1.5f, s.ForwardSpeed);
        Assert.Equal(2.5f, s.SidestepSpeed);
        Assert.Equal(3.5f, s.TurnSpeed);
    }

    [Fact]
    public void Decode_Invalid_WithStickyObject_ReadsTrailingGuid()
    {
        // No flags, no commands, but MotionFlags.StickToObject set
        // → trailing 4B sticky guid.
        var w = new MemoryWriter();
        w.WriteU32(0);                 // packed: no flags, no commands
        w.Align(4);
        w.WriteU32(0xDEADBEEF);        // sticky object guid
        var body = w.ToArray();

        var decoded = MotionBodyDecoder.Decode(body, MovementType.Invalid, MotionFlags.StickToObject);
        Assert.NotNull(decoded?.Invalid);
        Assert.Equal(0xDEADBEEFu, decoded!.Invalid!.StickyObjectGuid);
    }

    [Fact]
    public void Decode_Invalid_NumCommandsEncodedAboveBit7()
    {
        // numCommands = 2 → packed = (0 << 0) | (2 << 7) = 0x100.
        // Each MotionItem is 8 bytes: u16 rawCmd + u16 packedSeq + f32 speed.
        // Total: 4 (packed) + 16 (2 items) = 20, 4-aligned already.
        var w = new MemoryWriter();
        w.WriteU32(2u << 7);
        w.WriteU16(0x1234);  // item 0: rawCmd
        w.WriteU16(0x8056);  // item 0: packedSeq (MSB=autonomous, low15=0x0056)
        w.WriteF32(1.5f);    // item 0: speed
        w.WriteU16(0x5678);
        w.WriteU16(0x00AB);  // not autonomous, seq=0xAB
        w.WriteF32(2.5f);
        var body = w.ToArray();

        var decoded = MotionBodyDecoder.Decode(body, MovementType.Invalid, MotionFlags.None);
        Assert.NotNull(decoded?.Invalid);
        var s = decoded!.Invalid!.State;
        Assert.Equal(2, s.NumCommands);
        Assert.Equal(2, s.Commands.Count);
        Assert.Equal((ushort)0x1234, s.Commands[0].RawCommand);
        Assert.Equal((ushort)0x0056, s.Commands[0].ServerActionSequence);
        Assert.True(s.Commands[0].IsAutonomous);
        Assert.Equal(1.5f, s.Commands[0].Speed);
        Assert.Equal((ushort)0x5678, s.Commands[1].RawCommand);
        Assert.Equal((ushort)0x00AB, s.Commands[1].ServerActionSequence);
        Assert.False(s.Commands[1].IsAutonomous);
        Assert.Equal(2.5f, s.Commands[1].Speed);
    }

    [Fact]
    public void Decode_Invalid_OddAlignment_PadsToMultipleOf4()
    {
        // flags = CurrentStyle only (0x1) → 1 ushort after packed.
        // Bytes: 4 (packed) + 2 (currentStyle) = 6, pads to 8.
        var w = new MemoryWriter();
        w.WriteU32(0x1);
        w.WriteU16(0xAAAA);
        w.WriteU16(0x0000);  // pad
        var body = w.ToArray();
        Assert.Equal(8, body.Length);

        var decoded = MotionBodyDecoder.Decode(body, MovementType.Invalid, MotionFlags.None);
        Assert.NotNull(decoded?.Invalid);
        Assert.Equal((ushort)0xAAAA, decoded!.Invalid!.State.CurrentStyle);
    }

    [Fact]
    public void Decode_MoveToObject_ReadsAllFields()
    {
        var w = new MemoryWriter();
        w.WriteU32(0xC0FFEE01);                     // target guid
        // Origin
        w.WriteU32(0x860201ADu);                    // cellId
        w.WriteF32(12.5f); w.WriteF32(-28.25f); w.WriteF32(0.125f);  // pos xyz
        // MoveToParameters
        w.WriteU32(0xDEADBEEF);                     // movementParameters
        w.WriteF32(0.6f);                           // distanceToObject
        w.WriteF32(0.1f);                           // minDistance
        w.WriteF32(float.MaxValue);                 // failDistance
        w.WriteF32(1.0f);                           // speed
        w.WriteF32(15.0f);                          // walkRunThreshold
        w.WriteF32(1.5708f);                        // desiredHeading
        w.WriteF32(0.85f);                          // runRate (outer)
        var body = w.ToArray();
        Assert.Equal(MotionBodyDecoder.MoveToObjectBodySize, body.Length);

        var decoded = MotionBodyDecoder.Decode(body, MovementType.MoveToObject, MotionFlags.None);
        Assert.NotNull(decoded?.MoveToObject);
        var m = decoded!.MoveToObject!;
        Assert.Equal(0xC0FFEE01u, m.TargetGuid);
        Assert.Equal(0x860201ADu, m.Origin.CellId);
        Assert.Equal(new Vector3(12.5f, -28.25f, 0.125f), m.Origin.Position);
        Assert.Equal(0xDEADBEEFu, m.Parameters.MovementParameters);
        Assert.Equal(0.6f,        m.Parameters.DistanceToObject);
        Assert.Equal(0.1f,        m.Parameters.MinDistance);
        Assert.Equal(float.MaxValue, m.Parameters.FailDistance);
        Assert.Equal(1.0f,        m.Parameters.Speed);
        Assert.Equal(15.0f,       m.Parameters.WalkRunThreshold);
        Assert.Equal(1.5708f,     m.Parameters.DesiredHeading);
        Assert.Equal(0.85f,       m.RunRate);
    }

    [Fact]
    public void Decode_MoveToPosition_ReadsAllFields()
    {
        var w = new MemoryWriter();
        w.WriteU32(0x860201ADu);
        w.WriteF32(1f); w.WriteF32(2f); w.WriteF32(3f);
        w.WriteU32(0x11112222);
        w.WriteF32(0.5f); w.WriteF32(0.0f); w.WriteF32(1000f);
        w.WriteF32(1.25f); w.WriteF32(15f); w.WriteF32(0.0f);
        w.WriteF32(0.95f);
        var body = w.ToArray();
        Assert.Equal(MotionBodyDecoder.MoveToPositionBodySize, body.Length);

        var decoded = MotionBodyDecoder.Decode(body, MovementType.MoveToPosition, MotionFlags.None);
        Assert.NotNull(decoded?.MoveToPosition);
        var m = decoded!.MoveToPosition!;
        Assert.Equal(0x860201ADu, m.Origin.CellId);
        Assert.Equal(new Vector3(1f, 2f, 3f), m.Origin.Position);
        Assert.Equal(0x11112222u, m.Parameters.MovementParameters);
        Assert.Equal(1.25f, m.Parameters.Speed);
        Assert.Equal(0.95f, m.RunRate);
    }

    [Fact]
    public void Decode_TurnToObject_ReadsAllFields()
    {
        var w = new MemoryWriter();
        w.WriteU32(0x12345678);          // targetGuid
        w.WriteF32(1.234f);              // desiredHeading (outer)
        w.WriteU32(0xAAAA5555);          // turnTo movementParameters
        w.WriteF32(2.0f);                // turnTo speed
        w.WriteF32(0.5f);                // turnTo desiredHeading (inner)
        var body = w.ToArray();
        Assert.Equal(MotionBodyDecoder.TurnToObjectBodySize, body.Length);

        var decoded = MotionBodyDecoder.Decode(body, MovementType.TurnToObject, MotionFlags.None);
        Assert.NotNull(decoded?.TurnToObject);
        var t = decoded!.TurnToObject!;
        Assert.Equal(0x12345678u, t.TargetGuid);
        Assert.Equal(1.234f, t.DesiredHeading);
        Assert.Equal(0xAAAA5555u, t.Parameters.MovementParameters);
        Assert.Equal(2.0f, t.Parameters.Speed);
        Assert.Equal(0.5f, t.Parameters.DesiredHeading);
    }

    [Fact]
    public void Decode_TurnToHeading_ReadsAllFields()
    {
        var w = new MemoryWriter();
        w.WriteU32(0x76543210);
        w.WriteF32(3.5f);
        w.WriteF32(2.71f);
        var body = w.ToArray();
        Assert.Equal(MotionBodyDecoder.TurnToHeadingBodySize, body.Length);

        var decoded = MotionBodyDecoder.Decode(body, MovementType.TurnToHeading, MotionFlags.None);
        Assert.NotNull(decoded?.TurnToHeading);
        var t = decoded!.TurnToHeading!;
        Assert.Equal(0x76543210u, t.Parameters.MovementParameters);
        Assert.Equal(3.5f, t.Parameters.Speed);
        Assert.Equal(2.71f, t.Parameters.DesiredHeading);
    }

    [Fact]
    public void Decode_TooShortBody_ReturnsNull_DoesNotThrow()
    {
        // MoveToObject needs 52B; provide only 10.
        var body = new byte[10];
        var decoded = MotionBodyDecoder.Decode(body, MovementType.MoveToObject, MotionFlags.None);
        Assert.Null(decoded);
    }

    [Fact]
    public void Decode_UnknownMovementType_ReturnsNull()
    {
        // 0xFE is not a valid MovementType.
        var body = new byte[64];
        var decoded = MotionBodyDecoder.Decode(body, (MovementType)0xFE, MotionFlags.None);
        Assert.Null(decoded);
    }

    [Fact]
    public void Decode_Invalid_OurOwnStartStopShape_DecodesToFlagsZero()
    {
        // Our own MoveToState START/STOP causes the server to
        // broadcast back a Motion(Invalid) with body[8] composed of:
        // 4B packed (almost always 0) + 4B align.
        // This is the most common Motion body shape seen for our own
        // guid (per phase5-selfmotion-run-02.log).
        var body = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

        var decoded = MotionBodyDecoder.Decode(body, MovementType.Invalid, MotionFlags.None);
        Assert.NotNull(decoded?.Invalid);
        Assert.Equal(MovementStateFlag.Invalid, decoded!.Invalid!.State.Flags);
        Assert.Equal(0, decoded.Invalid.State.NumCommands);
    }

    /// <summary>
    /// Small in-memory writer to construct expected wire bytes.
    /// Mirrors the ACE server's BinaryWriter convention: little-
    /// endian everywhere, with explicit alignment padding.
    /// </summary>
    private sealed class MemoryWriter
    {
        private readonly System.Collections.Generic.List<byte> _bytes = new();
        public void WriteU16(ushort v)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, v);
            _bytes.Add(buf[0]); _bytes.Add(buf[1]);
        }
        public void WriteU32(uint v)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
            for (var i = 0; i < 4; i++) _bytes.Add(buf[i]);
        }
        public void WriteF32(float v)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(buf, v);
            for (var i = 0; i < 4; i++) _bytes.Add(buf[i]);
        }
        public void Align(int boundary)
        {
            while (_bytes.Count % boundary != 0) _bytes.Add(0);
        }
        public byte[] ToArray() => _bytes.ToArray();
    }
}
