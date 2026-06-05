// SPDX-License-Identifier: AGPL-3.0-or-later
// WeaponSwap — decide whether wielding a target weapon requires first
// dequipping a currently-wielded weapon (the ACE server's
// CheckWeaponCollision refuses a second weapon while one is equipped).
//
// This is pure, mechanical projection: it reads the wire-derived
// ItemType + equip-slot bits of the player's own inventory and answers
// "which currently-wielded weapon (if any) blocks this wield?". It
// encodes NO game knowledge — no wcids/names/landblocks, no "prefer
// weapon X" tactics. The LLM still decides WHICH weapon to wield; this
// only computes the mechanical prerequisite the server requires (move
// the conflicting weapon to the pack first), mirroring the server's own
// precondition. The empty-slot case (no weapon wielded) returns null and
// the caller wields directly, unchanged.

using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal static class WeaponSwap
{
    /// <summary>
    /// ACE.Entity.Enum.ItemType.Caster bit (0x8000) — wands/orbs/staves
    /// used for the magic combat path. Listed alongside MeleeWeapon
    /// (0x1) and MissileWeapon (0x100) as the ItemType bits that mark an
    /// item as a primary WEAPON for swap-collision purposes.
    /// </summary>
    public const uint CasterItemType = 0x00008000u;

    /// <summary>
    /// ItemType bits that identify a primary weapon. A wield of one of
    /// these is blocked by a currently-wielded item that is also one of
    /// these (the server's CheckWeaponCollision). Mirrors the readiness
    /// projection's melee/missile masks plus the caster bit.
    /// </summary>
    public const uint WeaponItemTypeMask =
        ItemTypeMasks.MeleeWeapon     // 0x00000001
        | ItemTypeMasks.MissileWeapon // 0x00000100
        | CasterItemType;             // 0x00008000

    /// <summary>
    /// EquipMask SLOT bits a primary weapon occupies (NOT ItemType
    /// bits): MeleeWeapon=0x00100000, MissileWeapon=0x00400000,
    /// Held=0x01000000 (wand/orb/caster), TwoHanded=0x02000000. A
    /// currently-wielded item occupying ANY of these is a weapon-slot
    /// occupant. Deliberately EXCLUDES Shield (0x00200000) and
    /// MissileAmmo (0x00800000) — this slice only resolves weapon↔weapon
    /// conflicts; a shield blocking a two-handed weapon, or ammo, is left
    /// to a later slice (and ammo is further excluded because loaded ammo
    /// can carry the MissileWeapon ItemType bit but lives in the ammo
    /// slot).
    /// </summary>
    public const uint MainWeaponSlotMask =
        0x00100000u   // EquipMask.MeleeWeapon slot
        | 0x00400000u // EquipMask.MissileWeapon slot
        | 0x01000000u // EquipMask.Held (caster)
        | 0x02000000u; // EquipMask.TwoHanded

    /// <summary>Wire facts for one inventory item, as the swap logic
    /// needs them. ItemType/ValidLocations/WieldedAt are the same
    /// projection fields surfaced to the LLM (nullable; 0/null = unset).</summary>
    public readonly record struct ItemFacts(
        uint Guid, uint? ItemType, uint? ValidLocations, uint? WieldedAt);

    /// <summary>True if the item's ItemType marks it as a primary weapon
    /// (melee, missile, or caster).</summary>
    public static bool IsWeapon(ItemFacts item) =>
        item.ItemType is uint it && (it & WeaponItemTypeMask) != 0;

    /// <summary>True if the item is currently wielded in a primary-weapon
    /// slot AND its ItemType is a weapon. Loaded ammo (slot
    /// MissileAmmo=0x00800000, not in <see cref="MainWeaponSlotMask"/>)
    /// is excluded even if it carries a weapon ItemType bit.</summary>
    public static bool IsWieldedWeapon(ItemFacts item) =>
        item.WieldedAt is uint w && (w & MainWeaponSlotMask) != 0 && IsWeapon(item);

    /// <summary>
    /// Return the guid of a currently-wielded weapon that blocks wielding
    /// <paramref name="target"/>, or null if none (the wield can proceed
    /// directly). Returns null unless the target is a not-yet-wielded,
    /// equippable weapon — so non-weapons (armor/cloak/hat), already-wielded
    /// items, and items with no valid equip slot never trigger a dequip.
    /// If multiple weapons are somehow wielded, the first encountered is
    /// returned (deterministic by inventory order); a second pass would
    /// dequip the next on a subsequent wield.
    /// </summary>
    public static uint? FindBlockingWieldedWeapon(
        ItemFacts target, IReadOnlyList<ItemFacts> inventory)
    {
        // Target must be a weapon we can equip and is not already wielded.
        if (!IsWeapon(target)) return null;
        if (target.WieldedAt is uint tw && tw != 0) return null;
        if (!(target.ValidLocations is uint vl && vl != 0)) return null;

        foreach (var item in inventory)
        {
            if (item.Guid == target.Guid) continue;
            if (IsWieldedWeapon(item)) return item.Guid;
        }
        return null;
    }
}
