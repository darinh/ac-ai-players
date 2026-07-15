// SPDX-License-Identifier: AGPL-3.0-or-later
// WeaponSwap — decide whether wielding a target weapon requires first
// dequipping a currently-wielded weapon (the ACE server's
// CheckWeaponCollision refuses a second weapon while one is equipped).
//
// This is pure, mechanical projection: it reads the wire-derived
// ItemType + equip-slot bits of the player's own inventory and answers
// "which currently-wielded items block this wield?". It
// encodes NO game knowledge — no wcids/names/landblocks, no "prefer
// weapon X" tactics. The LLM still decides WHICH weapon to wield; this
// only computes the mechanical prerequisite the server requires (move
// the conflicting weapon to the pack first), mirroring the server's own
// precondition. The empty-slot case returns an empty blocker list and the
// caller wields directly.

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
    /// MissileAmmo (0x00800000): a shield does not conflict with a
    /// one-handed weapon, and ammo lives in its own slot (loaded ammo can
    /// carry the MissileWeapon ItemType bit but is not a weapon occupant).
    /// A shield DOES conflict with a TWO-HANDED weapon — that is handled
    /// separately via <see cref="ShieldSlotMask"/> in
    /// <see cref="FindBlockingWieldedItems"/>.
    /// </summary>
    public const uint MainWeaponSlotMask =
        0x00100000u   // EquipMask.MeleeWeapon slot
        | 0x00400000u // EquipMask.MissileWeapon slot
        | 0x01000000u // EquipMask.Held (caster)
        | 0x02000000u; // EquipMask.TwoHanded

    /// <summary>
    /// EquipMask.Shield SLOT bit (0x00200000) — the off-hand. A wielded
    /// shield blocks a TWO-HANDED weapon wield (the server's
    /// CheckWeaponCollision TwoHanded case refuses while the off-hand is
    /// occupied) but does NOT block a one-handed weapon.
    /// </summary>
    public const uint ShieldSlotMask = 0x00200000u;

    /// <summary>
    /// EquipMask.TwoHanded SLOT bit (0x02000000). A target whose valid
    /// equip slot is TwoHanded occupies the main hand AND needs the
    /// off-hand free, so wielding it requires dequipping both a main-hand
    /// weapon and an off-hand shield.
    /// </summary>
    public const uint TwoHandedSlotMask = 0x02000000u;

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

    /// <summary>True if the item is currently wielded in the off-hand
    /// SHIELD slot (EquipMask.Shield). A wielded shield blocks a
    /// two-handed weapon wield but not a one-handed weapon.</summary>
    public static bool IsWieldedShield(ItemFacts item) =>
        item.WieldedAt is uint w && (w & ShieldSlotMask) != 0;

    /// <summary>True if the target item's valid equip slot is TWO-HANDED
    /// (its ValidLocations carries the TwoHanded bit).</summary>
    public static bool IsTwoHandedTarget(ItemFacts target) =>
        target.ValidLocations is uint vl && (vl & TwoHandedSlotMask) != 0;

    /// <summary>
    /// True if an item in the given equip ROLE occupies BOTH hands, so it
    /// requires the off-hand (shield slot) to be empty: a TWO-HANDED weapon,
    /// a MISSILE weapon (launcher), or a CASTER held item. Mirrors the server
    /// CheckWeaponCollision cases that refuse while the off-hand is occupied
    /// (TwoHanded; MissileWeapon ammo-launcher; Held caster).
    /// <paramref name="slot"/> is the equip slot — ValidLocations for a
    /// not-yet-wielded target, WieldedAt for a wielded item;
    /// <paramref name="itemType"/> distinguishes a caster Held item from a
    /// non-caster one. A MissileWeapon slot is treated as both-hands even for
    /// a thrown weapon that could keep a shield — a harmless over-dequip the
    /// LLM can re-wield around.
    /// </summary>
    private static bool OccupiesBothHands(uint slot, uint? itemType) =>
        (slot & TwoHandedSlotMask) != 0
        || (slot & 0x00400000u) != 0      // EquipMask.MissileWeapon
        || ((slot & 0x01000000u) != 0     // EquipMask.Held
            && itemType is uint it && (it & CasterItemType) != 0);

    /// <summary>True if wielding <paramref name="target"/> requires the
    /// off-hand to be empty (its valid slot is two-handed / missile /
    /// caster-held).</summary>
    public static bool TargetRequiresEmptyOffhand(ItemFacts target) =>
        target.ValidLocations is uint vl && OccupiesBothHands(vl, target.ItemType);

    /// <summary>True if <paramref name="item"/> is currently wielded in a
    /// role that occupies BOTH hands (two-handed / launcher / caster). Such a
    /// wielded item blocks an off-hand SHIELD wield (the server's
    /// CheckWeaponCollision Shield case).</summary>
    public static bool IsWieldedBothHandsOccupant(ItemFacts item) =>
        item.WieldedAt is uint w && w != 0 && OccupiesBothHands(w, item.ItemType);

    /// <summary>
    /// Return the guids of ALL currently-wielded items that must be moved to
    /// the pack before <paramref name="target"/> can be wielded, or an empty
    /// list if none (the wield can proceed directly). Mirrors the server's
    /// CheckWeaponCollision precondition:
    ///  - A MAIN-HAND target (any weapon, OR a held item such as a caster or a
    ///    torch) shares the MAIN hand, so any wielded main-hand occupant blocks
    ///    it; and if the target needs both hands (two-handed / missile launcher
    ///    / caster) a wielded off-hand SHIELD ALSO blocks it.
    ///  - A SHIELD target shares the OFF hand, so a wielded both-hands occupant
    ///    (two-handed / launcher / caster) in the main hand blocks it, AND an
    ///    existing off-hand SHIELD blocks it (the single off-hand slot is
    ///    shared) — but a one-handed main-hand occupant coexists with a shield,
    ///    so it is not moved.
    /// Detection is by equip SLOT (what the server's collision check uses), so
    /// a main-hand occupant that is not a "weapon" by ItemType (a torch / held
    /// quest item) is handled. Returns empty for a target that occupies neither
    /// a main hand nor the off-hand, is already wielded, or has no valid slot.
    /// Pure mechanical mirror of the server precondition — no weapon/shield
    /// preference, no game knowledge; the LLM still chose the target.
    /// </summary>
    public static IReadOnlyList<uint> FindBlockingWieldedItems(
        ItemFacts target, IReadOnlyList<ItemFacts> inventory)
    {
        var blockers = new List<uint>();
        if (target.WieldedAt is uint tw && tw != 0) return blockers;
        if (!(target.ValidLocations is uint vl && vl != 0)) return blockers;

        // Slot-based, mirroring the server: it checks SLOT occupancy, not
        // ItemType, so a main-hand occupant that is not a "weapon" by ItemType
        // (a caster, a torch/held quest item) is still a blocker / still needs
        // the main hand cleared.
        var targetIsMainHand = (vl & MainWeaponSlotMask) != 0;
        var targetIsShield = (vl & ShieldSlotMask) != 0;
        if (!targetIsMainHand && !targetIsShield) return blockers;

        var targetNeedsEmptyOffhand = targetIsMainHand && OccupiesBothHands(vl, target.ItemType);

        foreach (var item in inventory)
        {
            if (item.Guid == target.Guid) continue;
            if (item.WieldedAt is uint w && (w & MainWeaponSlotMask) != 0)
            {
                // A wielded MAIN-HAND occupant (weapon / two-hander / caster /
                // held item) blocks a main-hand target (shared main hand). For
                // a SHIELD target, only a main-hand occupant that needs BOTH
                // hands (two-handed / launcher / caster) blocks it — a
                // one-handed occupant coexists with a shield.
                if (targetIsShield && !IsWieldedBothHandsOccupant(item)) continue;
                blockers.Add(item.Guid);
            }
            else if (IsWieldedShield(item) && (targetNeedsEmptyOffhand || targetIsShield))
            {
                // A wielded off-hand shield blocks a target that needs the
                // off-hand free (two-handed / launcher / caster) AND blocks a
                // new SHIELD target (the single off-hand slot is shared, so the
                // old shield must be moved first).
                blockers.Add(item.Guid);
            }
        }
        return blockers;
    }
}
