// SPDX-License-Identifier: AGPL-3.0-or-later
// PrivateUpdatePropertyInt (0x02CD) - server tells client that an
// int-valued WorldObject property changed (e.g. CurrentHealth,
// Level, Coinage). "Private" means it's only visible to the
// receiving session (vs. the broadcast variant 0x019B).
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdatePropertyInt.cs
//
// Wire layout (13 bytes total, packed, no alignment padding):
//   u32  opcode   = 0x02CD
//   u8   sequence (per-property auto-increment; lets client reorder.
//                  Server uses ByteSequence whose NextBytes is a
//                  single byte - see ACE-bots
//                  Source/ACE.Server/Network/Sequence/ByteSequence.cs)
//   u32  property (PropertyInt enum value; the source enum's base
//                  is ushort but the writer Write((uint)property)
//                  promotes to u32 on the wire)
//   i32  value
//
// Queue: UIQueue (10). Base ctor passes 13 as the precomputed size
// (GameMessagePrivateUpdatePropertyInt.cs:10).
//
// Phase 4.7 scope: decode raw fields. PropertyName is best-effort
// from a tiny hand-picked subset; unknown values render as hex.
// Add more entries as they become relevant - the full server enum
// is ~660 entries (Source/ACE.Entity/Enum/Properties/PropertyInt.cs)
// and not worth verbatim copying yet.
//
// ! BYTE-SEQUENCE NOTE !
// The 1-byte sequence (NOT u32) is used by the ENTIRE
// PrivateUpdate* / PublicUpdate* property-update family:
//   PrivateUpdatePropertyBool   (size 13)
//   PrivateUpdatePropertyInt    (size 13) <-- this one
//   PrivateUpdatePropertyFloat  (size 17, f64 value)
//   PrivateUpdatePropertyInt64  (size 17, i64 value)
//   PrivateUpdatePropertyString (variable, includes Writer.Align())
//   PublicUpdatePropertyInt     (size 17, ADDS u32 sender guid)
//   PublicUpdatePropertyBool    (size 17, ADDS u32 sender guid)
//   PublicUpdatePropertyFloat   (size 21, ADDS u32 sender guid)
//   PublicUpdatePropertyInt64   (size 21, ADDS u32 sender guid)
//   PublicUpdatePropertyString  (variable, ADDS u32 sender guid)
// All use ByteSequence (NextBytes is a single byte), but the
// counter is NOT shared across them. The server keys every
// sequence by (SequenceType << 16 | property) — see
// SequenceManager.GetSequence — so each message type AND each
// property within it advances an INDEPENDENT 1-byte counter. The
// client must therefore stale-gate per (decoded family, property),
// not against one global max (live-proven for Int64: TotalExperience
// and AvailableExperience carry independent sequences). Public
// variants insert a u32 sender guid AFTER the sequence (for the
// Int/Bool/Float/Int64 versions); the String variant swaps the
// guid/property field order, so check the writer when you get
// to it. Do NOT assume u32 sequence when decoding these.

using System.Collections.Generic;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record PrivateUpdatePropertyIntMessage(
    byte Sequence,
    uint Property,
    int Value)
{
    /// <summary>13 = u32 opcode + u8 seq + u32 prop + i32 value (packed).</summary>
    public const int PackedSize = 13;

    /// <summary>
    /// Pretty-print the property as a known name when we have one,
    /// else "0xNNNN". Keep this list short — only entries we actively
    /// surface in logs or use in bot reasoning. Source: ACE-bots
    /// Source/ACE.Entity/Enum/Properties/PropertyInt.cs.
    /// </summary>
    public string PropertyName =>
        KnownProperties.TryGetValue(Property, out var n)
            ? n
            : $"0x{Property:X4}";

    private static readonly Dictionary<uint, string> KnownProperties = new()
    {
        // Carrying / inventory
        { 5,    "EncumbranceVal" },
        { 6,    "ItemsCapacity" },
        { 7,    "ContainersCapacity" },
        { 20,   "CoinValue" },
        // 21/24 are the 32-bit slots; player XP is sent via the i64
        // PrivateUpdatePropertyInt64 path (TotalExperience=1,
        // AvailableExperience=2), NOT here. 24 is AvailableSkillCredits.
        { 21,   "TotalExperience(int32)" },
        { 24,   "AvailableSkillCredits" },
        { 25,   "Level" },

        // Combat / vitals
        { 16,   "ItemUseable" },
        { 27,   "ArmorLevel" },
        { 29,   "AttackHeight" },
        { 33,   "PhysicalScore" },
        { 41,   "Allegiance_CPCached" },
        { 43,   "NumDeaths" },

        // Ticker properties (visible every server heartbeat)
        { 125,  "Age" },

        // Skills (small sample; full list in server enum)
        { 100,  "AlchemyBase" },

        // Common gameplay state changes seen in login burst
        { 113,  "PhysicsState" },
        { 158,  "WieldRequirements" },
        { 218,  "Burden" },
        { 220,  "AppraisalLongDescDecoration" },
        { 261,  "EquipmentSetId" },

        // Bot-system reserved (ac-ai-players#40 / ADR-0007)
        { 9016, "BotArchetype" },
    };
}
