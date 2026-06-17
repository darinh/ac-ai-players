// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for WeaponSwap — the pure dequip-before-wield collision helper.
// The ACE server refuses to wield a weapon while another weapon is
// equipped; these tests pin the predicate that decides WHEN a wield must
// be preceded by a dequip (and of WHICH currently-wielded weapon), and
// confirm the common no-collision paths return null so the caller wields
// directly (byte-identical to before this slice).

using System.Collections.Generic;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class WeaponSwapTests
{
    // EquipMask slot bits (mirror WeaponSwap.MainWeaponSlotMask members).
    private const uint MeleeSlot   = 0x00100000u;
    private const uint MissileSlot = 0x00400000u;
    private const uint HeldSlot    = 0x01000000u; // caster
    private const uint TwoHandSlot = 0x02000000u;
    private const uint ShieldSlot  = 0x00200000u; // off-hand
    private const uint AmmoSlot    = 0x00800000u; // NOT a weapon slot
    private const uint BodyArmor   = 0x00000008u; // a torso/clothing slot — neither weapon nor shield

    // ItemType bits.
    private const uint Melee   = 0x00000001u;
    private const uint Missile = 0x00000100u;
    private const uint Caster  = 0x00008000u;
    private const uint Armor   = 0x00000002u;

    private static WeaponSwap.ItemFacts Item(
        uint guid, uint? itemType, uint? validLoc, uint? wieldedAt) =>
        new(guid, itemType, validLoc, wieldedAt);

    [Fact]
    public void NoWieldedWeapon_NoBlocker()
    {
        // Wielding a melee weapon into an empty weapon slot — direct wield.
        var target = Item(0x100, Melee, MeleeSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            target,
            Item(0x200, Armor, ShieldSlot, ShieldSlot), // wielded armor, not a weapon
        };
        Assert.Null(WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void MeleeWieldedBlocksMissileWield_ReturnsBlocker()
    {
        // The cp-2242 scenario: melee Spadone equipped, want to wield a
        // missile atlatl. Slots differ (melee 0x100000 vs missile 0x400000)
        // yet the server still rejects — so raw slot-overlap would MISS it;
        // the weapon-group rule catches it.
        var target = Item(0x100, Missile, MissileSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x999, Melee, MeleeSlot, MeleeSlot), // wielded melee weapon
            target,
        };
        Assert.Equal(0x999u, WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void TwoHandedWieldedBlocksMissileWield_ReturnsBlocker()
    {
        // Two-handed melee occupies 0x2000000, missile wants 0x400000 —
        // zero bit-overlap, but still a weapon↔weapon conflict.
        var target = Item(0x100, Missile, MissileSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x777, Melee, TwoHandSlot, TwoHandSlot),
            target,
        };
        Assert.Equal(0x777u, WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void CasterWieldedBlocksMeleeWield_ReturnsBlocker()
    {
        var target = Item(0x100, Melee, MeleeSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x555, Caster, HeldSlot, HeldSlot), // wielded wand/orb
            target,
        };
        Assert.Equal(0x555u, WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void NonWeaponTarget_NeverTriggersDequip()
    {
        // Wielding armor/cloak/hat must never dequip a weapon.
        var target = Item(0x100, Armor, ShieldSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x999, Melee, MeleeSlot, MeleeSlot), // wielded weapon present
            target,
        };
        Assert.Null(WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void WieldedShield_DoesNotBlockWeaponWield()
    {
        // A shield is not a weapon; wielding a one-handed melee alongside it
        // is allowed by the server — no dequip this slice.
        var target = Item(0x100, Melee, MeleeSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x888, Armor, ShieldSlot, ShieldSlot), // wielded shield
            target,
        };
        Assert.Null(WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void LoadedAmmo_IsNotMistakenForBlockingWeapon()
    {
        // Loaded ammo can carry the MissileWeapon ItemType bit but lives in
        // the ammo slot (0x800000, not in MainWeaponSlotMask) — must not be
        // treated as the blocking weapon.
        var target = Item(0x100, Missile, MissileSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x444, Missile, AmmoSlot, AmmoSlot), // a loaded dart, in ammo slot
            target,
        };
        Assert.Null(WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void AlreadyWieldedTarget_NoBlocker()
    {
        // If the target is itself already wielded, nothing to do.
        var target = Item(0x100, Melee, MeleeSlot, MeleeSlot);
        var inv = new List<WeaponSwap.ItemFacts> { target };
        Assert.Null(WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void TargetWithNoValidLocations_NoBlocker()
    {
        var target = Item(0x100, Melee, 0u, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x999, Melee, MeleeSlot, MeleeSlot),
            target,
        };
        Assert.Null(WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void IsWieldedWeapon_Predicate()
    {
        Assert.True(WeaponSwap.IsWieldedWeapon(Item(1, Melee, MeleeSlot, MeleeSlot)));
        Assert.True(WeaponSwap.IsWieldedWeapon(Item(1, Missile, MissileSlot, MissileSlot)));
        Assert.True(WeaponSwap.IsWieldedWeapon(Item(1, Caster, HeldSlot, HeldSlot)));
        // ammo: weapon ItemType bit but ammo slot -> not a wielded weapon
        Assert.False(WeaponSwap.IsWieldedWeapon(Item(1, Missile, AmmoSlot, AmmoSlot)));
        // shield: not a weapon ItemType
        Assert.False(WeaponSwap.IsWieldedWeapon(Item(1, Armor, ShieldSlot, ShieldSlot)));
        // unwielded weapon -> not a wielded weapon
        Assert.False(WeaponSwap.IsWieldedWeapon(Item(1, Melee, MeleeSlot, null)));
    }

    [Fact]
    public void IsTwoHandedTarget_And_IsWieldedShield_Predicates()
    {
        // Target is two-handed iff its valid equip slot carries the TwoHanded bit.
        Assert.True(WeaponSwap.IsTwoHandedTarget(Item(1, Melee, TwoHandSlot, null)));
        Assert.False(WeaponSwap.IsTwoHandedTarget(Item(1, Melee, MeleeSlot, null)));
        // A wielded shield is identified by the off-hand Shield slot.
        Assert.True(WeaponSwap.IsWieldedShield(Item(1, Armor, ShieldSlot, ShieldSlot)));
        Assert.False(WeaponSwap.IsWieldedShield(Item(1, Melee, MeleeSlot, MeleeSlot)));
        Assert.False(WeaponSwap.IsWieldedShield(Item(1, Armor, ShieldSlot, null))); // not wielded
    }

    [Fact]
    public void FindBlockingWieldedItems_TwoHandedTarget_ShieldEquipped_ShieldIsBlocker()
    {
        // The live root cause: a two-handed weapon (e.g. a Spadone, valid slot
        // TwoHanded) is refused while an off-hand shield is wielded — both hands
        // are needed. The weapon-only view misses it; FindBlockingWieldedItems
        // must return the shield so the swap dequips it first.
        var target = Item(0x100, Melee, TwoHandSlot, null);
        var shield = Item(0x8000060C, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { shield, target };

        var blockers = WeaponSwap.FindBlockingWieldedItems(target, inv);
        Assert.Equal(new[] { 0x8000060Cu }, blockers);
        // The weapon-only view still returns null (a shield is not a weapon).
        Assert.Null(WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_TwoHandedTarget_WeaponAndShield_BothBlockers()
    {
        // A one-handed melee weapon in the main hand AND a shield in the off-hand
        // both block a two-handed wield; both must be dequipped.
        var target = Item(0x100, Melee, TwoHandSlot, null);
        var weapon = Item(0x80003620, Melee, MeleeSlot, MeleeSlot);
        var shield = Item(0x8000060C, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { weapon, shield, target };

        var blockers = WeaponSwap.FindBlockingWieldedItems(target, inv);
        Assert.Contains(0x80003620u, blockers);
        Assert.Contains(0x8000060Cu, blockers);
        Assert.Equal(2, blockers.Count);
    }

    [Fact]
    public void FindBlockingWieldedItems_OneHandedTarget_ShieldEquipped_NoBlockers()
    {
        // A one-handed weapon coexists with a shield — the shield is NOT a
        // blocker, so an empty main hand means a direct wield (no dequip).
        var target = Item(0x100, Melee, MeleeSlot, null);
        var shield = Item(0x8000060C, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { shield, target };

        Assert.Empty(WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_TwoHandedTarget_EmptyHands_NoBlockers()
    {
        var target = Item(0x100, Melee, TwoHandSlot, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x200, Armor, 0u, 0u), // worn body armor, not a hand slot
            target,
        };
        Assert.Empty(WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_NonEquippableConflictTarget_Empty()
    {
        // A torso/clothing target shares NEITHER a hand nor the off-hand, so it
        // never dequips a weapon or shield even with both wielded.
        var target = Item(0x100, Armor, BodyArmor, null);
        var inv = new List<WeaponSwap.ItemFacts>
        {
            Item(0x999, Melee, MeleeSlot, MeleeSlot),
            Item(0x888, Armor, ShieldSlot, ShieldSlot),
            target,
        };
        Assert.Empty(WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_ShieldTarget_TwoHandedEquipped_TwoHandedIsBlocker()
    {
        // Wielding a SHIELD while a two-handed weapon occupies both hands: the
        // server refuses (the main hand holds a both-hands weapon), so the 2H
        // weapon must be dequipped. A shield is not a weapon, so the
        // weapon-only view never sees this.
        var target = Item(0x100, Armor, ShieldSlot, null);
        var twoHander = Item(0x777, Melee, TwoHandSlot, TwoHandSlot);
        var inv = new List<WeaponSwap.ItemFacts> { twoHander, target };

        Assert.Equal(new[] { 0x777u }, WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_ShieldTarget_OneHandedEquipped_NoBlocker()
    {
        // A shield coexists with a one-handed main-hand weapon — wielding the
        // shield does not dequip it.
        var target = Item(0x100, Armor, ShieldSlot, null);
        var oneHander = Item(0x999, Melee, MeleeSlot, MeleeSlot);
        var inv = new List<WeaponSwap.ItemFacts> { oneHander, target };

        Assert.Empty(WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_MissileLauncherTarget_ShieldEquipped_ShieldIsBlocker()
    {
        // A missile launcher (bow/atlatl) needs an empty off-hand (server
        // refuses while the off-hand is occupied), so a wielded shield blocks
        // it and must be dequipped.
        var target = Item(0x100, Missile, MissileSlot, null);
        var shield = Item(0x8000060C, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { shield, target };

        Assert.Equal(new[] { 0x8000060Cu }, WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_CasterTarget_ShieldEquipped_ShieldIsBlocker()
    {
        // A CASTER held item (wand/orb, ItemType Caster) needs an empty
        // off-hand per the server's Held case, so a wielded shield blocks it.
        var target = Item(0x100, Caster, HeldSlot, null);
        var shield = Item(0x8000060C, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { shield, target };

        Assert.Equal(new[] { 0x8000060Cu }, WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_NonCasterHeldTarget_ShieldEquipped_NoShieldBlocker()
    {
        // A non-caster Held item (no Caster ItemType, e.g. a held quest item)
        // does NOT require an empty off-hand per the server's Held case, so a
        // wielded shield is not dequipped for it.
        var target = Item(0x100, Armor, HeldSlot, null); // Held slot, NOT a caster
        var shield = Item(0x8000060C, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { shield, target };

        Assert.Empty(WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void TargetRequiresEmptyOffhand_Predicate()
    {
        Assert.True(WeaponSwap.TargetRequiresEmptyOffhand(Item(1, Melee, TwoHandSlot, null)));
        Assert.True(WeaponSwap.TargetRequiresEmptyOffhand(Item(1, Missile, MissileSlot, null)));
        Assert.True(WeaponSwap.TargetRequiresEmptyOffhand(Item(1, Caster, HeldSlot, null)));
        // A one-handed melee weapon does NOT need an empty off-hand.
        Assert.False(WeaponSwap.TargetRequiresEmptyOffhand(Item(1, Melee, MeleeSlot, null)));
        // A non-caster Held item does NOT need an empty off-hand.
        Assert.False(WeaponSwap.TargetRequiresEmptyOffhand(Item(1, Armor, HeldSlot, null)));
    }

    [Fact]
    public void FindBlockingWieldedItems_ShieldTarget_ShieldEquipped_OldShieldIsBlocker()
    {
        // Wielding a NEW shield while one is already in the off-hand: the single
        // off-hand slot is shared, so the server refuses until the old shield is
        // moved. The old shield must be dequipped first.
        var target = Item(0x100, Armor, ShieldSlot, null);
        var oldShield = Item(0x8000060C, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { oldShield, target };

        Assert.Equal(new[] { 0x8000060Cu }, WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_HeldNonCasterTarget_WeaponEquipped_WeaponIsBlocker()
    {
        // A non-caster Held item (e.g. a torch) occupies the MAIN hand, so the
        // server refuses it while a weapon is wielded — detection must be by
        // SLOT, not ItemType. The wielded weapon must be dequipped.
        var target = Item(0x100, Armor, HeldSlot, null); // torch: Held slot, NOT a weapon ItemType
        var weapon = Item(0x999, Melee, MeleeSlot, MeleeSlot);
        var inv = new List<WeaponSwap.ItemFacts> { weapon, target };

        Assert.Equal(new[] { 0x999u }, WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_WeaponTarget_HeldNonCasterEquipped_HeldIsBlocker()
    {
        // The reverse: a wielded non-caster Held item (torch) occupies the main
        // hand and blocks a new weapon. It is not a weapon by ItemType, so a
        // slot-based check is required to dequip it.
        var target = Item(0x100, Melee, MeleeSlot, null);
        var torch = Item(0x888, Armor, HeldSlot, HeldSlot); // wielded torch
        var inv = new List<WeaponSwap.ItemFacts> { torch, target };

        Assert.Equal(new[] { 0x888u }, WeaponSwap.FindBlockingWieldedItems(target, inv));
    }

    [Fact]
    public void FindBlockingWieldedItems_OneHandedMeleeTarget_WeaponEquipped_MatchesWeaponOnlyView()
    {
        // For a one-handed MELEE target, FindBlockingWieldedItems agrees with
        // the weapon-only view: a wielded main-hand weapon is the sole blocker;
        // a shield is NOT added because a 1H weapon does not need the off-hand.
        var target = Item(0x100, Melee, MeleeSlot, null);
        var weapon = Item(0x999, Melee, MeleeSlot, MeleeSlot);
        var shield = Item(0x888, Armor, ShieldSlot, ShieldSlot);
        var inv = new List<WeaponSwap.ItemFacts> { weapon, shield, target };

        Assert.Equal(new[] { 0x999u }, WeaponSwap.FindBlockingWieldedItems(target, inv));
        Assert.Equal(0x999u, WeaponSwap.FindBlockingWieldedWeapon(target, inv));
    }
}
