// SPDX-License-Identifier: AGPL-3.0-or-later
// PrivateUpdateVital (0x02E7) - server tells the receiving session
// that one of ITS OWN player's vitals (Health/Stamina/Mana) changed.
// "Private" means it is sent only to that session, so - exactly like
// PrivateUpdatePropertyInt (0x02CD) - it carries no guid and is
// implicitly scoped to the bot's own player. The server sends this on
// every damage tick AND every regen tick (see ACE-bots
// Source/ACE.Server/WorldObjects/Player_Vitals.cs / Creature_Vitals.cs),
// so it is the authoritative, timely source for the bot's own health.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdateVital.cs
//     Writer.Write(Sequences.GetNextSequence(UpdateAttribute2ndLevel, Vital)); // u8 ByteSequence
//     Writer.Write((uint)creatureVital.Vital);   // PropertyAttribute2nd
//     Writer.Write(creatureVital.Ranks);
//     Writer.Write(creatureVital.StartingValue);
//     Writer.Write(creatureVital.ExperienceSpent);
//     Writer.Write(creatureVital.Current);
//
// Wire layout (25 bytes total, packed, no alignment padding):
//   u32  opcode         = 0x02E7
//   u8   sequence       (per-(type,vital) ByteSequence; single byte -
//                        the base ctor passes 25 as the size, and
//                        4 + 1 + 5*4 = 25 confirms a 1-byte sequence,
//                        consistent with the PrivateUpdate* family)
//   u32  vital          (PropertyAttribute2nd; the source enum base is
//                        ushort but the writer promotes to u32)
//   u32  ranks
//   u32  startingValue
//   u32  experienceSpent
//   u32  current        (the vital's CURRENT absolute value)
//
// NOTE on which vital is "Health": ACE keys each CreatureVital by its
// MAX attribute2nd, so the Health vital reports Vital == MaxHealth (1),
// NOT Health (2). See ACE-bots Creature_Vitals.cs:
//   public CreatureVital Health => Vitals[PropertyAttribute2nd.MaxHealth];
// (PropertyAttribute2nd: MaxHealth=1, Health=2, MaxStamina=3, ...).
//
// The MAX value is NOT carried in this message (it is derived
// server-side from StartingValue + Ranks + the linked attribute, e.g.
// Endurance, plus buffs/vitae). WorldState therefore tracks the max as
// the peak observed Current, which on a full-health login/respawn seeds
// to the true max without reimplementing AC's max-vital formula.

namespace HeadlessAcClient.Protocol.GameMessages;

/// <summary>PropertyAttribute2nd values relevant to vital decoding.
/// Verbatim subset of ACE-bots
/// Source/ACE.Entity/Enum/Properties/PropertyAttribute2nd.cs.</summary>
internal enum VitalKind : uint
{
    Undef      = 0,
    MaxHealth  = 1,
    Health     = 2,
    MaxStamina = 3,
    Stamina    = 4,
    MaxMana    = 5,
    Mana       = 6,
}

internal sealed record PrivateUpdateVitalMessage(
    byte Sequence,
    uint Vital,
    uint Ranks,
    uint StartingValue,
    uint ExperienceSpent,
    uint Current)
{
    /// <summary>25 = u32 opcode + u8 seq + 5 * u32 (packed).</summary>
    public const int PackedSize = 25;

    /// <summary>True when this update is for the player's HEALTH vital.
    /// AC keys the health vital by its MAX attribute2nd, so the wire
    /// value is MaxHealth (1).</summary>
    public bool IsHealth => Vital == (uint)VitalKind.MaxHealth;

    /// <summary>True when this update is for the player's STAMINA vital.
    /// Like health, AC keys it by the MAX attribute2nd (MaxStamina = 3) in
    /// this 0x02E7 descriptor; the carried Current is the CURRENT stamina.</summary>
    public bool IsStamina => Vital == (uint)VitalKind.MaxStamina;
}

// PrivateUpdateAttribute2ndLevel (0x02E9) - the per-tick CURRENT-LEVEL
// update for a single vital. Unlike PrivateUpdateVital (0x02E7, the full
// descriptor sent on rank-raises/sync), this is what ACE sends on EVERY
// damage, regen, death, and respawn tick - the timely, authoritative
// source for the bot's own current HP. Like the other Private* messages
// it carries no guid and is implicitly scoped to the receiving session.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdateAttribute2ndLevel.cs
//     base(..., size 13)
//     Writer.Write(Sequences.GetNextSequence(UpdateAttribute2ndLevel, vital)); // u8 ByteSequence
//     Writer.Write((uint)vital);   // Vital enum
//     Writer.Write(current);       // the vital's CURRENT absolute value
//
// Wire layout (13 bytes total, packed):
//   u32  opcode  = 0x02E9
//   u8   sequence
//   u32  vital   (Vital enum)
//   u32  current
//
// IMPORTANT keying difference vs 0x02E7: here the CURRENT-HEALTH update
// is keyed by Vital.Health (2), NOT MaxHealth (1). The server calls
// GameMessagePrivateUpdateAttribute2ndLevel(this, Vital.Health, current)
// (see ACE-bots Player_Vitals.cs / Player_Death.cs). The sequence is a
// per-(type, vital) ByteSequence, so the Health(2) counter here is
// DISTINCT from the MaxHealth(1) counter on 0x02E7.

internal sealed record PrivateUpdateAttribute2ndLevelMessage(
    byte Sequence,
    uint Vital,
    uint Current)
{
    /// <summary>13 = u32 opcode + u8 seq + 2 * u32 (packed).</summary>
    public const int PackedSize = 13;

    /// <summary>True when this current-level update is for HEALTH. The
    /// current-level packet keys health by Health (2), unlike the
    /// descriptor packet (0x02E7) which keys it by MaxHealth (1).</summary>
    public bool IsHealth => Vital == (uint)VitalKind.Health;

    /// <summary>True when this current-level update is for STAMINA. The
    /// current-level packet keys it by Stamina (4); the carried Current is
    /// the bot's CURRENT stamina (the timely per-tick source).</summary>
    public bool IsStamina => Vital == (uint)VitalKind.Stamina;
}
