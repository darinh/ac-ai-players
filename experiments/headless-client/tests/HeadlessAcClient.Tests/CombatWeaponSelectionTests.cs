// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for CombatWeaponSelection — the pure attack-mode picker
// that the motor uses to decide melee vs missile dispatch from the
// player's currently wielded weapon. No game knowledge: just the
// wire-derived ItemType bits.

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatWeaponSelectionTests
{
    private const uint Melee = ItemTypeMasks.MeleeWeapon;     // 0x0001
    private const uint Missile = ItemTypeMasks.MissileWeapon; // 0x0100

    [Fact]
    public void NoItems_DefaultsToMelee()
    {
        Assert.Equal(AttackMode.Melee,
            CombatWeaponSelection.SelectAttackMode(System.Array.Empty<(uint?, bool)>()));
    }

    [Fact]
    public void UnwieldedMissileWeapon_DefaultsToMelee()
    {
        // Carried-but-not-wielded missile weapon does not change the mode.
        var items = new (uint?, bool)[] { (Missile, false) };
        Assert.Equal(AttackMode.Melee, CombatWeaponSelection.SelectAttackMode(items));
    }

    [Fact]
    public void WieldedMeleeWeapon_SelectsMelee()
    {
        var items = new (uint?, bool)[] { (Melee, true) };
        Assert.Equal(AttackMode.Melee, CombatWeaponSelection.SelectAttackMode(items));
    }

    [Fact]
    public void WieldedMissileWeapon_NoMelee_SelectsMissile()
    {
        var items = new (uint?, bool)[] { (Missile, true) };
        Assert.Equal(AttackMode.Missile, CombatWeaponSelection.SelectAttackMode(items));
    }

    [Fact]
    public void WieldedMissileWeapon_WithWieldedMelee_PrefersMelee()
    {
        var items = new (uint?, bool)[] { (Missile, true), (Melee, true) };
        Assert.Equal(AttackMode.Melee, CombatWeaponSelection.SelectAttackMode(items));
    }

    [Fact]
    public void NullItemType_Ignored()
    {
        var items = new (uint?, bool)[] { ((uint?)null, true), (Missile, true) };
        Assert.Equal(AttackMode.Missile, CombatWeaponSelection.SelectAttackMode(items));
    }

    [Fact]
    public void CombatModeValue_MapsToServerEnum()
    {
        Assert.Equal(2u, CombatWeaponSelection.CombatModeValue(AttackMode.Melee));
        Assert.Equal(4u, CombatWeaponSelection.CombatModeValue(AttackMode.Missile));
    }

    [Fact]
    public void HasWieldedMissileWeapon_WieldedMissile_True()
    {
        var items = new (uint?, bool)[] { (Missile, true) };
        Assert.True(CombatWeaponSelection.HasWieldedMissileWeapon(items));
    }

    [Fact]
    public void HasWieldedMissileWeapon_NoMissile_False()
    {
        // The consume case: the thrown weapon is gone, only a melee weapon (or
        // nothing) remains wielded → no missile weapon to sustain a throw.
        var items = new (uint?, bool)[] { (Melee, true) };
        Assert.False(CombatWeaponSelection.HasWieldedMissileWeapon(items));
        Assert.False(CombatWeaponSelection.HasWieldedMissileWeapon(System.Array.Empty<(uint?, bool)>()));
    }

    [Fact]
    public void HasWieldedMissileWeapon_UnwieldedMissile_False()
    {
        // A missile weapon still in the bag (un-wielded) cannot sustain an
        // in-flight missile attack.
        var items = new (uint?, bool)[] { (Missile, false) };
        Assert.False(CombatWeaponSelection.HasWieldedMissileWeapon(items));
    }

    [Fact]
    public void HasWieldedMissileWeapon_NullItemType_Ignored()
    {
        var items = new (uint?, bool)[] { ((uint?)null, true) };
        Assert.False(CombatWeaponSelection.HasWieldedMissileWeapon(items));
    }
}
