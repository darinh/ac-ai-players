// SPDX-License-Identifier: AGPL-3.0-or-later
// Skill — the wire enum for the RaiseSkill (0x0046) game action's skill id.
//
// The server casts the opcode's first u32 to ACE.Entity.Enum.Skill, whose
// numeric values are POSITIONAL (the members are unnumbered, so the value is
// the declaration ordinal: None=0, Axe=1, ...). The headless client is
// deliberately decoupled from the server's ACE.Entity assembly, so this is a
// verbatim copy of that enum's member ORDER — including the retired / unused
// placeholders, which (per the server's own comment) "ABSOLUTELY CANNOT" be
// removed without shifting every later ordinal off the wire contract. Do NOT
// reorder or delete members here.
//
// Source of truth: ACE-bots Source/ACE.Entity/Enum/Skill.cs.

namespace HeadlessAcClient.Protocol;

internal enum Skill
{
    None,
    Axe,                 // Retired
    Bow,                 // Retired
    Crossbow,            // Retired
    Dagger,              // Retired
    Mace,                // Retired
    MeleeDefense,
    MissileDefense,
    Sling,               // Retired
    Spear,               // Retired
    Staff,               // Retired
    Sword,               // Retired
    ThrownWeapon,        // Retired
    UnarmedCombat,       // Retired
    ArcaneLore,
    MagicDefense,
    ManaConversion,
    Spellcraft,          // Unimplemented
    ItemTinkering,
    AssessPerson,
    Deception,
    Healing,
    Jump,
    Lockpick,
    Run,
    Awareness,           // Unimplemented
    ArmsAndArmorRepair,  // Unimplemented
    AssessCreature,
    WeaponTinkering,
    ArmorTinkering,
    MagicItemTinkering,
    CreatureEnchantment,
    ItemEnchantment,
    LifeMagic,
    WarMagic,
    Leadership,
    Loyalty,
    Fletching,
    Alchemy,
    Cooking,
    Salvaging,
    TwoHandedCombat,
    Gearcraft,           // Retired
    VoidMagic,
    HeavyWeapons,
    LightWeapons,
    FinesseWeapons,
    MissileWeapons,
    Shield,
    DualWield,
    Recklessness,
    SneakAttack,
    DirtyFighting,
    Challenge,           // Unimplemented
    Summoning,
}
