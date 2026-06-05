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
    private const uint ShieldSlot  = 0x00200000u; // NOT a weapon slot
    private const uint AmmoSlot    = 0x00800000u; // NOT a weapon slot

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
}
