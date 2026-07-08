// SPDX-License-Identifier: AGPL-3.0-or-later
// PublicUpdateInstanceId (0x02DA) - server tells clients that a guid-valued
// (InstanceId) WorldObject property changed on a NAMED object. Unlike the
// Private* self-updates, this carries the target object's guid, so it reports
// which object's property changed. The server's
// UpdateProperty(PropertyInstanceId,...) helper sends THIS message (the Public
// variant) - e.g. when a player's allegiance Monarch (PropertyInstanceId 26)
// changes on swear/break, or Patron (25) / Allegiance (24).
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePublicUpdateInstanceID.cs
//     Writer.Write(sequence)            // ByteSequence -> single byte
//     Writer.WriteGuid(worldObject.Guid) // u32
//     Writer.Write((uint)property)       // u32
//     Writer.WriteGuid(instanceGuid)     // u32 (0 when the property was removed)
//
// Wire layout (17 bytes total, packed, no alignment padding):
//   u32  opcode     = 0x02DA
//   u8   sequence   (per-(object,property) ByteSequence; single byte, like the
//                    rest of the PrivateUpdate*/PublicUpdate* property family -
//                    see PrivateUpdatePropertyIntMessage's BYTE-SEQUENCE NOTE)
//   u32  objectGuid (the object whose InstanceId property changed)
//   u32  property   (PropertyInstanceId enum value; source enum base is ushort
//                    but the writer promotes to u32 on the wire)
//   u32  value      (the instance guid; 0 means the property was REMOVED)
//
// Queue: UIQueue (10). Base ctor passes 17 as the precomputed size.

using System.Collections.Generic;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record PublicUpdateInstanceIdMessage(
    byte Sequence,
    uint ObjectGuid,
    uint Property,
    uint Value)
{
    /// <summary>17 = u32 opcode + u8 seq + u32 objectGuid + u32 prop + u32 value (packed).</summary>
    public const int PackedSize = 17;

    /// <summary>PropertyInstanceId.Monarch (26): the guid at the top of the object's allegiance tree.</summary>
    public const uint MonarchProperty = 26;

    /// <summary>
    /// Pretty-print the property as a known name when we have one, else "0xNNNN".
    /// Only the allegiance-relevant InstanceId properties are named; extend as
    /// more become relevant. Source: ACE-bots
    /// Source/ACE.Entity/Enum/Properties/PropertyInstanceId.cs.
    /// </summary>
    public string PropertyName =>
        KnownProperties.TryGetValue(Property, out var n)
            ? n
            : $"0x{Property:X4}";

    private static readonly Dictionary<uint, string> KnownProperties = new()
    {
        { 24, "Allegiance" },
        { 25, "Patron" },
        { 26, "Monarch" },
    };
}
