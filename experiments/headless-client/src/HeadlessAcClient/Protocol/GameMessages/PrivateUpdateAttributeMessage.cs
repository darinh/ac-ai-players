// SPDX-License-Identifier: AGPL-3.0-or-later
// PrivateUpdateAttribute (0x02E3) — server tells the receiving session
// that one of its own primary attributes changed (a RaiseAttribute
// 0x0045 rank-raise, or a full re-sync). "Private" = no guid on the
// wire; implicitly the bot's own player.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdateAttribute.cs
//
// Wire layout (21 bytes total, packed):
//   u32  opcode  = 0x02E3
//   u8   sequence (per-attribute ByteSequence; keyed by
//                  (SequenceType.UpdateAttribute, attrId) so EACH
//                  attribute has an INDEPENDENT 1-byte counter — gate
//                  per attribute id)
//   u32  attrId  (PropertyAttribute: 1=Strength..6=Self)
//   u32  Ranks
//   u32  StartingValue (creation base; Base value = StartingValue+Ranks)
//   u32  ExperienceSpent
//
// This is ONLY the six primary attributes. Vital max-pools (Health/
// Stamina/Mana) ride the separate 0x02E7 PrivateUpdateVital descriptor
// and the 0x02E9 2nd-level current update — neither is decoded into the
// self attribute list here.

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record PrivateUpdateAttributeMessage(
    byte Sequence,
    uint Attribute,
    uint Ranks,
    uint StartingValue,
    uint ExperienceSpent)
{
    /// <summary>21 = u32 opcode + u8 seq + u32 id + u32 ranks + u32
    /// startingValue + u32 xp.</summary>
    public const int PackedSize = 21;
}
