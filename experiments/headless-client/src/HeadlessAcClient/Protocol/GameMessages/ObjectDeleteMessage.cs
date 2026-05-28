// SPDX-License-Identifier: AGPL-3.0-or-later
// ObjectDelete (0xF747) — server announces that a WorldObject is
// being removed from the visible world. The bot should drop the
// guid from its WorldState so stale objects stop accumulating in
// the in-memory snapshot.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessageDeleteObject.cs
//
// Wire layout (fixed 12 bytes, body cursor-relative):
//   u32 opcode (0xF747)
//   u32 guid          (via Writer.WriteGuid)
//   u16 instanceSeq   (Sequences[ObjectInstance].CurrentBytes,
//                      a UShortSequence emitting 2 raw bytes)
//   u16 _pad          (Writer.Align() rounds the 10-byte position
//                      up to the next 4-byte boundary = 12)
//
// Instance-sequence semantics: the server emits the CURRENT instance
// sequence (not Next) so any stale ObjectDelete arriving after a
// respawn (which advances the instance counter) can be detected and
// dropped. See WorldState.ApplyObjectDelete for the gating rule.
//
// NOTE: This is distinct from InventoryRemoveObject (0x0024), which
// removes an item from the local-player inventory UI, and PickupEvent
// (0xF74A), which signals a pickup animation. World-space removal
// always flows through ObjectDelete.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record ObjectDeleteMessage(
    uint Guid,
    ushort InstanceSequence)
{
    public const int PackedSize = 12;
}
