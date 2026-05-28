// SPDX-License-Identifier: AGPL-3.0-or-later
// Motion / MovementEvent (0xF74C) - per-tick broadcast of a
// WorldObject's movement intent. Wraps a MovementData payload
// whose body shape depends on MovementType. Phase 4.6 decodes
// the header only; the polymorphic body (MovementInvalid /
// MoveToObject / MoveToPosition / TurnToObject / TurnToHeading)
// is kept as raw bytes for later phases.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageUpdateMotion.cs
//   Source/ACE.Server/Network/Structure/MovementData.cs
//
// Wire layout (variable):
//   u32  opcode (0xF74C)
//   u32  guid (ObjectGuid.Full)
//   u16  instanceSequence  (UShortSequence: ObjectInstance)
//   u16  movementSequence  (UShortSequence: ObjectMovement)
//   u16  serverControlSeq  (UShortSequence: ObjectServerControl)
//   u8   isAutonomous      (0 = server-initiated, 1 = client)
//   PAD  to next 4-byte boundary relative to PAYLOAD start
//        (writer.Align() pads stream length to multiple of 4 -
//        after the 4 prior bytes (8) + 2+2+2+1 = 15 bytes in
//        payload, we land on offset 15 and pad 1 byte to 16).
//   u8   movementType (MovementType enum)
//   u8   motionFlags  (MotionFlags enum, [Flags])
//   u16  currentStyle (MotionStance, written as ushort)
//   ---- body (variable, polymorphic on movementType) ----
//   case Invalid:        MovementInvalid blob
//   case MoveToObject:   u32 targetGuid + MoveToPosition (Origin + heading?)
//   case MoveToPosition: Origin + heading?
//   case TurnToObject:   u32 targetGuid + heading data
//   case TurnToHeading:  heading data
//
// Phase 4.6 scope: header fields surfaced + raw body bytes
// preserved for downstream inspection.

using System;

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum MovementType : byte
{
    Invalid                = 0x0,
    RawCommand             = 0x1,
    InterpretedCommand     = 0x2,
    StopRawCommand         = 0x3,
    StopInterpretedCommand = 0x4,
    StopCompletely         = 0x5,
    MoveToObject           = 0x6,
    MoveToPosition         = 0x7,
    TurnToObject           = 0x8,
    TurnToHeading          = 0x9,
}

[Flags]
internal enum MotionFlags : byte
{
    None             = 0x0,
    StickToObject    = 0x1,
    StandingLongJump = 0x2,
}

internal sealed record MotionMessage(
    uint Guid,
    ushort InstanceSequence,
    ushort MovementSequence,
    ushort ServerControlSequence,
    bool IsAutonomous,
    MovementType MovementType,
    MotionFlags MotionFlags,
    ushort CurrentStyle,
    byte[] BodyBytes)
{
    /// <summary>Minimum bytes required to parse the header alone:
    /// 4 opcode + 4 guid + 2+2+2 sequences + 1 autonomous + 1 align
    /// + 1 movementType + 1 motionFlags + 2 currentStyle = 20.
    /// </summary>
    public const int HeaderSize = 20;
}
