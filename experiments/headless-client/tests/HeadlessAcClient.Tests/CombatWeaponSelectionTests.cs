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

    // Equip-slot bit constants mirroring WeaponSwap constants.
    private const uint MeleeSlot   = 0x00100000u; // EquipMask.MeleeWeapon slot
    private const uint MissileSlot = 0x00400000u; // EquipMask.MissileWeapon slot
    private const uint AmmoSlot    = 0x00800000u; // EquipMask.MissileAmmo slot (= ItemTypeMasks.MissileAmmoSlot)

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

    // ── ClassifyWeaponState tests ──────────────────────────────────────────

    [Fact]
    public void Classify_NoItems_UnarmedMeleeOnly()
    {
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(
            System.Array.Empty<WeaponStateItem>());
        Assert.Equal(WeaponReadiness.UnarmedMeleeOnly, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_WieldedMeleeWeapon_HasUsableWeapon()
    {
        var items = new WeaponStateItem[]
        {
            new(0x1001u, ItemType: Melee, WieldedAt: MeleeSlot, AmmoType: null),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.HasUsableWeapon, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_WieldedThrownWeapon_HasUsableWeapon()
    {
        // A thrown weapon has ItemType=MissileWeapon, AmmoType=null (no launcher
        // AmmoType), is its own projectile — combat-capable on its own.
        var items = new WeaponStateItem[]
        {
            new(0x1002u, ItemType: Missile, WieldedAt: MissileSlot, AmmoType: null),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.HasUsableWeapon, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_WieldedLauncherWithAmmoLoaded_HasUsableWeapon()
    {
        // Launcher in main-weapon slot + ammo in ammo slot → combat-capable.
        var items = new WeaponStateItem[]
        {
            new(0x1003u, ItemType: Missile, WieldedAt: MissileSlot, AmmoType: 5),
            new(0x1004u, ItemType: Missile, WieldedAt: AmmoSlot,    AmmoType: 5),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.HasUsableWeapon, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_WieldedLauncherNoAmmo_LauncherNeedsDequip()
    {
        // Launcher in main-weapon slot, no ammo slot item → must dequip.
        var items = new WeaponStateItem[]
        {
            new(0x1005u, ItemType: Missile, WieldedAt: MissileSlot, AmmoType: 5),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.LauncherNeedsDequip, state);
        Assert.Equal(0x1005u, guid);
    }

    [Fact]
    public void Classify_LauncherNeedsDequip_ReturnsCorrectLauncherGuid()
    {
        // The returned guid must identify the launcher, not any other item.
        const uint launcherGuid = 0xDEADBEEFu;
        var items = new WeaponStateItem[]
        {
            new(launcherGuid, ItemType: Missile, WieldedAt: MissileSlot, AmmoType: 3),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.LauncherNeedsDequip, state);
        Assert.Equal(launcherGuid, guid);
    }

    [Fact]
    public void Classify_LauncherAndMeleeWielded_HasUsableWeapon()
    {
        // If a melee weapon is ALSO wielded alongside an ammoless launcher,
        // the bot is combat-capable via the melee weapon.
        var items = new WeaponStateItem[]
        {
            new(0x1006u, ItemType: Missile, WieldedAt: MissileSlot, AmmoType: 5),
            new(0x1007u, ItemType: Melee,   WieldedAt: MeleeSlot,   AmmoType: null),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.HasUsableWeapon, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_LoadedAmmoInAmmoSlot_NotMisclassifiedAsLauncher()
    {
        // Ammo item in ammo slot (WieldedAt=AmmoSlot) carries MissileWeapon bit
        // but is NOT a wielded launcher (fails MainWeaponSlotMask check).
        // With no actual weapon in a main-weapon slot this is UnarmedMeleeOnly.
        var items = new WeaponStateItem[]
        {
            new(0x1008u, ItemType: Missile, WieldedAt: AmmoSlot, AmmoType: 5),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.UnarmedMeleeOnly, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_UnwieldedLauncher_UnarmedMeleeOnly()
    {
        // A launcher in the bag (WieldedAt=null/0) does not affect readiness.
        var items = new WeaponStateItem[]
        {
            new(0x1009u, ItemType: Missile, WieldedAt: null, AmmoType: 5),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.UnarmedMeleeOnly, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_WieldedLauncherNoAmmo_InBagAmmoDoesNotMakeItCapable()
    {
        // A launcher wielded in main-weapon slot with ammo in the BAG (not
        // in the ammo slot = not loaded) is NOT combat-capable.
        // Bag ammo: WieldedAt=null (not wielded at all).
        var items = new WeaponStateItem[]
        {
            new(0x100Au, ItemType: Missile, WieldedAt: MissileSlot, AmmoType: 5),
            new(0x100Bu, ItemType: Missile, WieldedAt: null,         AmmoType: 5),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.LauncherNeedsDequip, state);
        Assert.Equal(0x100Au, guid);
    }

    // ── Part 3 dequip-then-melee chain tests ──────────────────────────────

    [Fact]
    public void Classify_LauncherOnly_LauncherNeedsDequip_GuidReturned()
    {
        // The exact Motor trigger condition: a launcher wielded with no ammo
        // anywhere. The classifier must return LauncherNeedsDequip and expose
        // the launcher GUID so the motor knows what to dequip.
        var launcherGuid = 0xABC1u;
        var items = new WeaponStateItem[]
        {
            new(launcherGuid, ItemType: Missile, WieldedAt: MissileSlot, AmmoType: 12),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.LauncherNeedsDequip, state);
        Assert.Equal(launcherGuid, guid);
    }

    [Fact]
    public void Classify_AfterDequip_OnlyBagItemsRemain_UnarmedMeleeOnly()
    {
        // After the Motor sends PutItemInContainer for the launcher, the
        // server removes the item from the wielded location. The next world-
        // state snapshot has only bag items left → UnarmedMeleeOnly, which
        // enables CanUnarmedMelee and unblocks the TargetedMeleeAttack path.
        var items = new WeaponStateItem[]
        {
            // The launcher is now in the bag (WieldedAt null).
            new(0xABC1u, ItemType: Missile, WieldedAt: null, AmmoType: 12),
            // Some ammo still in the bag.
            new(0xABC2u, ItemType: Missile, WieldedAt: null, AmmoType: 12),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.UnarmedMeleeOnly, state);
        Assert.Null(guid);
    }

    [Fact]
    public void Classify_DequipDoesNotAffectOtherWieldedNonWeaponItems()
    {
        // Armor / clothing worn (WieldedAt != 0 but not a main-weapon slot)
        // must not prevent UnarmedMeleeOnly after the launcher is dequipped.
        var items = new WeaponStateItem[]
        {
            // Armor on the character (not in a main-weapon slot).
            new(0x2001u, ItemType: 0x2u /* Armor */, WieldedAt: 0x020000u, AmmoType: null),
            // Launcher now in bag.
            new(0xABC1u, ItemType: Missile,           WieldedAt: null,      AmmoType: 12),
        };
        var (state, guid) = CombatWeaponSelection.ClassifyWeaponState(items);
        Assert.Equal(WeaponReadiness.UnarmedMeleeOnly, state);
        Assert.Null(guid);
    }
}
