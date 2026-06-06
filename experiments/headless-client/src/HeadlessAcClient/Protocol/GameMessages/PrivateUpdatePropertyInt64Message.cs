// SPDX-License-Identifier: AGPL-3.0-or-later
// PrivateUpdatePropertyInt64 (0x02CF) - server tells client that an
// i64-valued WorldObject property changed. The player-relevant ones
// are experience totals (TotalExperience / AvailableExperience),
// which are 64-bit because lifetime XP exceeds 2^31. "Private" means
// it's only visible to the receiving session, so (like the 32-bit
// PrivateUpdatePropertyInt 0x02CD) it carries NO guid and implicitly
// targets the receiving session's own player.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdatePropertyInt64.cs
//
// Wire layout (17 bytes total, packed, no alignment padding):
//   u32  opcode   = 0x02CF
//   u8   sequence (per-(type,property) ByteSequence auto-increment;
//                  single byte - see ACE-bots
//                  Source/ACE.Server/Network/Sequence/ByteSequence.cs.
//                  NOTE: the server keys this counter by
//                  (SequenceType.UpdatePropertyInt64, property), so
//                  each Int64 property has an INDEPENDENT counter -
//                  do NOT gate all Int64 properties against one max.)
//   u32  property (PropertyInt64 enum value; writer promotes to u32)
//   i64  value
//
// Queue: UIQueue (10). Base ctor passes 17 as the precomputed size
// (GameMessagePrivateUpdatePropertyInt64.cs:9).
//
// PropertyInt64 ids (Source/ACE.Entity/Enum/Properties/PropertyInt64.cs):
//   1 = TotalExperience, 2 = AvailableExperience. These are DISTINCT
// from the 32-bit PropertyInt TotalExperience=21 / AvailableSkill-
// Credits=24 - the server sends player XP via the Int64 path only.

using System.Collections.Generic;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record PrivateUpdatePropertyInt64Message(
    byte Sequence,
    uint Property,
    long Value)
{
    /// <summary>17 = u32 opcode + u8 seq + u32 prop + i64 value (packed).</summary>
    public const int PackedSize = 17;

    /// <summary>TotalExperience PropertyInt64 id.</summary>
    public const uint TotalExperienceId = 1;

    /// <summary>AvailableExperience PropertyInt64 id (unspent XP).</summary>
    public const uint AvailableExperienceId = 2;

    /// <summary>
    /// Pretty-print the property as a known name when we have one,
    /// else "0xNNNN". Source: ACE-bots
    /// Source/ACE.Entity/Enum/Properties/PropertyInt64.cs.
    /// </summary>
    public string PropertyName =>
        KnownProperties.TryGetValue(Property, out var n)
            ? n
            : $"0x{Property:X4}";

    private static readonly Dictionary<uint, string> KnownProperties = new()
    {
        { TotalExperienceId,     "TotalExperience" },
        { AvailableExperienceId, "AvailableExperience" },
    };
}
