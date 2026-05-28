// SPDX-License-Identifier: AGPL-3.0-or-later
// MotionBodyDecoder — Phase 5 polymorphic decoder for the Motion
// (0xF74C) message body. The header (guid + sequences + autonomous
// + movementType + motionFlags + currentStyle) is decoded in
// GameMessageDecoder.DecodeMotion; this file picks up after that
// and parses the variable body that depends on MovementType.
//
// Authoritative server-side sources (paths in ACE-bots fork):
//   Source/ACE.Server/Network/Motion/MovementData.cs:182-230
//     (header + body switch)
//   Source/ACE.Server/Network/Motion/InterpretedMotionState.cs:125-164
//     (InterpretedMotionState wire layout — used by MovementInvalid)
//   Source/ACE.Server/Network/Motion/MovementInvalid.cs:39-48
//   Source/ACE.Server/Network/Motion/MoveToObject.cs
//   Source/ACE.Server/Network/Motion/MoveToPosition.cs
//   Source/ACE.Server/Network/Motion/TurnToObject.cs
//   Source/ACE.Server/Network/Motion/TurnToHeading.cs
//   Source/ACE.Server/Network/Motion/MoveToParameters.cs
//   Source/ACE.Server/Network/Motion/TurnToParameters.cs
//   Source/ACE.Server/Network/Motion/MotionItem.cs:37-51 (reader)
//   Source/ACE.Server/Network/Structure/Origin.cs (cell + xyz)
//   Source/ACE.Entity/Enum/MovementStateFlag.cs (the 7 flag bits)
//
// Wire layouts (one of these follows the header per MovementType):
//
//   MovementType.Invalid (0x0):
//     InterpretedMotionState                          (variable)
//     if (motionFlags & StickToObject):  u32 stickyGuid
//
//   MovementType.MoveToObject (0x6):
//     u32  targetGuid
//     Origin (u32 cell + Vec3 xyz)                    16B
//     MoveToParameters (u32 flags + 6 floats)         28B
//     f32  runRate                                    4B
//     ----------------------------------------------- = 52B fixed
//
//   MovementType.MoveToPosition (0x7):
//     Origin                                          16B
//     MoveToParameters                                28B
//     f32  runRate                                    4B
//     ----------------------------------------------- = 48B fixed
//
//   MovementType.TurnToObject (0x8):
//     u32  targetGuid                                 4B
//     f32  desiredHeading                             4B
//     TurnToParameters (u32 flags + 2 floats)         12B
//     ----------------------------------------------- = 20B fixed
//
//   MovementType.TurnToHeading (0x9):
//     TurnToParameters                                12B fixed
//
// InterpretedMotionState wire layout (matches InterpretedMotionStateExtensions.Write):
//   u32   packed                  // (low 7 bits = MovementStateFlag,
//                                 //  remaining bits = numCommands << 7)
//   if (flags & CurrentStyle):     u16 currentStyle
//   if (flags & ForwardCommand):   u16 forwardCommand
//   if (flags & SideStepCommand):  u16 sidestepCommand
//   if (flags & TurnCommand):      u16 turnCommand
//   if (flags & ForwardSpeed):     f32 forwardSpeed
//   if (flags & SideStepSpeed):    f32 sidestepSpeed
//   if (flags & TurnSpeed):        f32 turnSpeed
//   numCommands × MotionItem  (u16 rawCmd + u16 packedSeq + f32 speed = 8B each)
//   align to 4-byte boundary RELATIVE TO BODY START
//
// Note that the InterpretedMotionState flag-bit order is NOT
// numeric — the server writes commands (ushorts) first, then speeds
// (floats), as: CurrentStyle(0x1), ForwardCommand(0x2),
// SideStepCommand(0x8), TurnCommand(0x20), ForwardSpeed(0x4),
// SideStepSpeed(0x10), TurnSpeed(0x40).
//
// MoveToParameters fields (in order):
//   u32 movementParameters (ACE.Entity.Enum.MovementParams)
//   f32 distanceToObject
//   f32 minDistance
//   f32 failDistance
//   f32 speed
//   f32 walkRunThreshold
//   f32 desiredHeading
//
// TurnToParameters fields (in order):
//   u32 movementParameters
//   f32 speed
//   f32 desiredHeading

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace HeadlessAcClient.Protocol.GameMessages;

[Flags]
internal enum MovementStateFlag : uint
{
    Invalid         = 0x0,
    CurrentStyle    = 0x1,
    ForwardCommand  = 0x2,
    ForwardSpeed    = 0x4,
    SideStepCommand = 0x8,
    SideStepSpeed   = 0x10,
    TurnCommand     = 0x20,
    TurnSpeed       = 0x40,
}

internal sealed record MotionItemBody(
    ushort RawCommand,
    ushort PackedSequence,
    float  Speed)
{
    public ushort ServerActionSequence => (ushort)(PackedSequence & 0x7FFF);
    public bool   IsAutonomous          => (PackedSequence >> 15) == 1;
}

internal sealed record InterpretedMotionStateBody(
    MovementStateFlag Flags,
    ushort  NumCommands,
    ushort? CurrentStyle,
    ushort? ForwardCommand,
    ushort? SidestepCommand,
    ushort? TurnCommand,
    float?  ForwardSpeed,
    float?  SidestepSpeed,
    float?  TurnSpeed,
    IReadOnlyList<MotionItemBody> Commands);

internal sealed record OriginBody(uint CellId, Vector3 Position);

internal sealed record MoveToParametersBody(
    uint  MovementParameters,
    float DistanceToObject,
    float MinDistance,
    float FailDistance,
    float Speed,
    float WalkRunThreshold,
    float DesiredHeading);

internal sealed record TurnToParametersBody(
    uint  MovementParameters,
    float Speed,
    float DesiredHeading);

internal sealed record MovementInvalidBody(
    InterpretedMotionStateBody State,
    uint? StickyObjectGuid);

internal sealed record MoveToObjectBody(
    uint TargetGuid,
    OriginBody Origin,
    MoveToParametersBody Parameters,
    float RunRate);

internal sealed record MoveToPositionBody(
    OriginBody Origin,
    MoveToParametersBody Parameters,
    float RunRate);

internal sealed record TurnToObjectBody(
    uint TargetGuid,
    float DesiredHeading,
    TurnToParametersBody Parameters);

internal sealed record TurnToHeadingBody(
    TurnToParametersBody Parameters);

/// <summary>
/// Discriminated-union view of the decoded Motion body. Exactly
/// one of the variant properties is non-null, matching the
/// MovementType from the Motion header.
/// </summary>
internal sealed record MotionBody(
    MovementType MovementType,
    MovementInvalidBody?  Invalid,
    MoveToObjectBody?     MoveToObject,
    MoveToPositionBody?   MoveToPosition,
    TurnToObjectBody?     TurnToObject,
    TurnToHeadingBody?    TurnToHeading)
{
    public override string ToString() => MovementType switch
    {
        Protocol.GameMessages.MovementType.Invalid =>
            $"Invalid(flags=0x{(uint)(Invalid?.State.Flags ?? 0):X2} " +
            $"numCmds={Invalid?.State.NumCommands ?? 0} " +
            $"sticky={(Invalid?.StickyObjectGuid is uint s ? $"0x{s:X8}" : "-")})",
        Protocol.GameMessages.MovementType.MoveToObject when MoveToObject is { } m =>
            $"MoveToObject(target=0x{m.TargetGuid:X8} " +
            $"origin=({m.Origin.Position.X:F2},{m.Origin.Position.Y:F2},{m.Origin.Position.Z:F2}) " +
            $"runRate={m.RunRate:F2})",
        Protocol.GameMessages.MovementType.MoveToPosition when MoveToPosition is { } m =>
            $"MoveToPosition(origin=({m.Origin.Position.X:F2},{m.Origin.Position.Y:F2},{m.Origin.Position.Z:F2}) " +
            $"runRate={m.RunRate:F2})",
        Protocol.GameMessages.MovementType.TurnToObject when TurnToObject is { } t =>
            $"TurnToObject(target=0x{t.TargetGuid:X8} heading={t.DesiredHeading:F2})",
        Protocol.GameMessages.MovementType.TurnToHeading when TurnToHeading is { } t =>
            $"TurnToHeading(heading={t.Parameters.DesiredHeading:F2} speed={t.Parameters.Speed:F2})",
        _ => $"{MovementType}",
    };
}

internal static class MotionBodyDecoder
{
    public static MotionBody? Decode(
        ReadOnlySpan<byte> body,
        MovementType movementType,
        MotionFlags  motionFlags)
    {
        try
        {
            return movementType switch
            {
                MovementType.Invalid           => new MotionBody(movementType,
                                                    Invalid: DecodeInvalid(body, motionFlags),
                                                    MoveToObject: null, MoveToPosition: null,
                                                    TurnToObject: null, TurnToHeading: null),
                MovementType.MoveToObject      => new MotionBody(movementType,
                                                    Invalid: null,
                                                    MoveToObject: DecodeMoveToObject(body),
                                                    MoveToPosition: null,
                                                    TurnToObject: null, TurnToHeading: null),
                MovementType.MoveToPosition    => new MotionBody(movementType,
                                                    Invalid: null, MoveToObject: null,
                                                    MoveToPosition: DecodeMoveToPosition(body),
                                                    TurnToObject: null, TurnToHeading: null),
                MovementType.TurnToObject      => new MotionBody(movementType,
                                                    Invalid: null, MoveToObject: null,
                                                    MoveToPosition: null,
                                                    TurnToObject: DecodeTurnToObject(body),
                                                    TurnToHeading: null),
                MovementType.TurnToHeading     => new MotionBody(movementType,
                                                    Invalid: null, MoveToObject: null,
                                                    MoveToPosition: null, TurnToObject: null,
                                                    TurnToHeading: DecodeTurnToHeading(body)),
                _ => null,
            };
        }
        catch
        {
            // Don't let a malformed body block decoding of the rest
            // of the firehose. The caller can fall back to raw bytes.
            return null;
        }
    }

    private static MovementInvalidBody DecodeInvalid(
        ReadOnlySpan<byte> body, MotionFlags motionFlags)
    {
        var (state, consumed) = ReadInterpretedMotionState(body);

        uint? sticky = null;
        if ((motionFlags & MotionFlags.StickToObject) != 0)
        {
            if (body.Length - consumed < 4)
                throw new InvalidOperationException("body too short for StickyObject guid");
            sticky = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(consumed, 4));
        }

        return new MovementInvalidBody(state, sticky);
    }

    private static (InterpretedMotionStateBody State, int Consumed) ReadInterpretedMotionState(
        ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            throw new InvalidOperationException("body too short for packed flags");

        var cursor = 0;
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        cursor += 4;

        // Low 7 bits = MovementStateFlag, remaining bits = numCommands.
        var flags       = (MovementStateFlag)(packed & 0x7F);
        var numCommands = (ushort)(packed >> 7);

        ushort? currentStyle    = null;
        ushort? forwardCommand  = null;
        ushort? sidestepCommand = null;
        ushort? turnCommand     = null;
        float?  forwardSpeed    = null;
        float?  sidestepSpeed   = null;
        float?  turnSpeed       = null;

        // Order matches InterpretedMotionStateExtensions.Write:
        // all command ushorts first (CurrentStyle, ForwardCommand,
        // SidestepCommand, TurnCommand), then all speed floats.
        // This is NOT numeric flag order.
        if ((flags & MovementStateFlag.CurrentStyle) != 0)
        {
            currentStyle = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor += 2;
        }
        if ((flags & MovementStateFlag.ForwardCommand) != 0)
        {
            forwardCommand = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor += 2;
        }
        if ((flags & MovementStateFlag.SideStepCommand) != 0)
        {
            sidestepCommand = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor += 2;
        }
        if ((flags & MovementStateFlag.TurnCommand) != 0)
        {
            turnCommand = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor += 2;
        }
        if ((flags & MovementStateFlag.ForwardSpeed) != 0)
        {
            forwardSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(cursor, 4));
            cursor += 4;
        }
        if ((flags & MovementStateFlag.SideStepSpeed) != 0)
        {
            sidestepSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(cursor, 4));
            cursor += 4;
        }
        if ((flags & MovementStateFlag.TurnSpeed) != 0)
        {
            turnSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(cursor, 4));
            cursor += 4;
        }

        var commands = new List<MotionItemBody>(numCommands);
        for (var i = 0; i < numCommands; i++)
        {
            var raw    = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2)); cursor += 2;
            var pseq   = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2)); cursor += 2;
            var speed  = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(cursor, 4)); cursor += 4;
            commands.Add(new MotionItemBody(raw, pseq, speed));
        }

        // Align to 4-byte boundary RELATIVE TO BODY START. The
        // server's BinaryWriter.Align() pads the stream to a multiple
        // of 4 from the stream origin, but the body itself begins at
        // a 4-byte boundary in the parent message (because the
        // pre-body header is padded to alignment), so a body-relative
        // mod-4 alignment is equivalent.
        var pad = (4 - (cursor % 4)) % 4;
        cursor += pad;

        var state = new InterpretedMotionStateBody(
            flags, numCommands,
            currentStyle, forwardCommand, sidestepCommand, turnCommand,
            forwardSpeed, sidestepSpeed, turnSpeed,
            commands);
        return (state, cursor);
    }

    private static OriginBody ReadOrigin(ReadOnlySpan<byte> src)
    {
        var cell = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        var x = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(4, 4));
        var y = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(8, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(12, 4));
        return new OriginBody(cell, new Vector3(x, y, z));
    }

    private static MoveToParametersBody ReadMoveToParameters(ReadOnlySpan<byte> src)
    {
        var mp  = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        var d2o = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(4, 4));
        var min = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(8, 4));
        var fai = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(12, 4));
        var spd = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(16, 4));
        var wrt = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(20, 4));
        var dhd = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(24, 4));
        return new MoveToParametersBody(mp, d2o, min, fai, spd, wrt, dhd);
    }

    private static TurnToParametersBody ReadTurnToParameters(ReadOnlySpan<byte> src)
    {
        var mp  = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        var spd = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(4, 4));
        var dhd = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(8, 4));
        return new TurnToParametersBody(mp, spd, dhd);
    }

    private const int OriginSize           = 16;
    private const int MoveToParametersSize = 28;
    private const int TurnToParametersSize = 12;

    public const int MoveToObjectBodySize     = 4 + OriginSize + MoveToParametersSize + 4;  // 52
    public const int MoveToPositionBodySize   = OriginSize + MoveToParametersSize + 4;       // 48
    public const int TurnToObjectBodySize     = 4 + 4 + TurnToParametersSize;                // 20
    public const int TurnToHeadingBodySize    = TurnToParametersSize;                        // 12

    private static MoveToObjectBody DecodeMoveToObject(ReadOnlySpan<byte> body)
    {
        if (body.Length < MoveToObjectBodySize)
            throw new InvalidOperationException($"body too short for MoveToObject: need {MoveToObjectBodySize}, got {body.Length}");
        var target  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        var origin  = ReadOrigin(body.Slice(4, OriginSize));
        var parms   = ReadMoveToParameters(body.Slice(4 + OriginSize, MoveToParametersSize));
        var runRate = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(4 + OriginSize + MoveToParametersSize, 4));
        return new MoveToObjectBody(target, origin, parms, runRate);
    }

    private static MoveToPositionBody DecodeMoveToPosition(ReadOnlySpan<byte> body)
    {
        if (body.Length < MoveToPositionBodySize)
            throw new InvalidOperationException($"body too short for MoveToPosition: need {MoveToPositionBodySize}, got {body.Length}");
        var origin  = ReadOrigin(body.Slice(0, OriginSize));
        var parms   = ReadMoveToParameters(body.Slice(OriginSize, MoveToParametersSize));
        var runRate = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(OriginSize + MoveToParametersSize, 4));
        return new MoveToPositionBody(origin, parms, runRate);
    }

    private static TurnToObjectBody DecodeTurnToObject(ReadOnlySpan<byte> body)
    {
        if (body.Length < TurnToObjectBodySize)
            throw new InvalidOperationException($"body too short for TurnToObject: need {TurnToObjectBodySize}, got {body.Length}");
        var target  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        var heading = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(4, 4));
        var parms   = ReadTurnToParameters(body.Slice(8, TurnToParametersSize));
        return new TurnToObjectBody(target, heading, parms);
    }

    private static TurnToHeadingBody DecodeTurnToHeading(ReadOnlySpan<byte> body)
    {
        if (body.Length < TurnToHeadingBodySize)
            throw new InvalidOperationException($"body too short for TurnToHeading: need {TurnToHeadingBodySize}, got {body.Length}");
        var parms = ReadTurnToParameters(body.Slice(0, TurnToParametersSize));
        return new TurnToHeadingBody(parms);
    }
}
