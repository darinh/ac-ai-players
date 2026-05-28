// SPDX-License-Identifier: AGPL-3.0-or-later
// UpdatePosition (0xF748) - per-tick broadcast of a WorldObject's
// position. Highest-volume world-state message: the server sends
// one per visible moving object per tick. In the academy this is
// dominated by the Pilot-01 BotPlayer (0x50000005) doing idle
// wander.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageUpdatePosition.cs
//   Source/ACE.Server/Network/Structure/PositionPack.cs
//
// Wire layout (variable, 44 to 68 bytes depending on flags):
//   u32  opcode         = 0xF748
//   u32  guid           (ObjectGuid.Full)
//   u32  flags          (PositionFlags - see below)
//   u32  cellId         (Origin.CellID)
//   f32  pos.x / pos.y / pos.z         (Vector3, always present)
//   f32  rot.w   only if !OrientationHasNoW
//   f32  rot.x   only if !OrientationHasNoX
//   f32  rot.y   only if !OrientationHasNoY
//   f32  rot.z   only if !OrientationHasNoZ
//   f32  vel.x/y/z       only if HasVelocity
//   u32  placementId     only if HasPlacementID
//   u16  instanceSequence       (UShortSequence)
//   u16  positionSequence       (UShortSequence)
//   u16  teleportSequence       (UShortSequence)
//   u16  forcePositionSequence  (UShortSequence)
//
// PositionFlags (uint, [Flags] enum from ACE.Entity):
//   HasVelocity       = 0x01
//   HasPlacementID    = 0x02
//   IsGrounded        = 0x04
//   OrientationHasNoW = 0x08
//   OrientationHasNoX = 0x10
//   OrientationHasNoY = 0x20
//   OrientationHasNoZ = 0x40
//
// Note the inverse semantics of the orientation flags: a flag
// being SET means the corresponding quaternion component is
// ABSENT from the wire (the server omits zero components as a
// compression). When reconstructing the quaternion, default
// missing components to 0.0.
//
// Sequence base type per PositionPack comments + SequenceManager:
// all four are UShortSequence (u16). Packed, no alignment padding.

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace HeadlessAcClient.Protocol.GameMessages;

[Flags]
internal enum PositionFlags : uint
{
    None              = 0x00,
    HasVelocity       = 0x01,
    HasPlacementID    = 0x02,
    IsGrounded        = 0x04,
    OrientationHasNoW = 0x08,
    OrientationHasNoX = 0x10,
    OrientationHasNoY = 0x20,
    OrientationHasNoZ = 0x40,
}

internal sealed record UpdatePositionMessage(
    uint Guid,
    PositionFlags Flags,
    uint CellId,
    Vector3 Position,
    Quaternion Rotation,
    Vector3? Velocity,
    uint? PlacementId,
    ushort InstanceSequence,
    ushort PositionSequence,
    ushort TeleportSequence,
    ushort ForcePositionSequence)
{
    /// <summary>14 ints/u32 + u16x4 = absolute minimum 44 bytes
    /// (no velocity, no placement, all-zero orientation kept the
    /// W component only - actually min seen is 44 with one rotation
    /// component present).</summary>
    public const int MinPackedSize = 4 /* opcode */ + 4 /* guid */
        + 4 /* flags */ + 4 /* cell */ + 12 /* pos */ + 8 /* 4x u16 seq */;
}
