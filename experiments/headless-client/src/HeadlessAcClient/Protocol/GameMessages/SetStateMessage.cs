// SPDX-License-Identifier: AGPL-3.0-or-later
// SetState (0xF74B) - broadcasts an updated PhysicsState bitfield
// for a WorldObject, plus the ObjectInstance and ObjectState sequence
// counters needed for the client to validate ordering.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageSetState.cs
//
// Wire layout (fixed 16 bytes):
//   u32 opcode (0xF74B)
//   u32 guid
//   u32 state (PhysicsState [Flags] enum — kept as raw u32 here)
//   u16 instanceSequence  (ObjectInstance)
//   u16 stateSequence     (ObjectState)
//
// PhysicsState enum source for future use:
//   Source/ACE.Entity/Enum/PhysicsState.cs

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record SetStateMessage(
    uint Guid,
    uint State,
    ushort InstanceSequence,
    ushort StateSequence)
{
    public const int PackedSize = 16;
}
