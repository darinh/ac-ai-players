// SPDX-License-Identifier: AGPL-3.0-or-later
// CombatWeaponSelection — pick the attack opcode family (melee vs
// missile) from the player's currently WIELDED weapon; classify the
// bot's weapon readiness state for the unarmed-melee fallback path.
//
// This is pure, mechanical projection: it reads the wire-derived
// ItemType of the items the player has equipped and chooses the
// matching attack action + combat mode. It encodes NO game knowledge
// (no wcids/names/landblocks, no "prefer missile at range" tactics) —
// it only mirrors the server's own precondition (HandleActionTargeted*
// validates CombatMode + the equipped weapon's type). The LLM still
// decides WHETHER and WHOM to attack; this only decides HOW to dispatch
// the attack it asked for, given the weapon already in hand.

using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal enum AttackMode
{
    Melee,
    Missile,
}

/// <summary>
/// Three-way classification of the bot's weapon readiness, used by the
/// unarmed-melee fallback path. Mirrors <see cref="LlmGoalPolicy.IsCombatCapable"/>
/// slot-mask logic exactly so the two never diverge.
/// </summary>
internal enum WeaponReadiness
{
    /// <summary>
    /// A usable weapon is wielded right now: a melee weapon, a thrown missile
    /// weapon (no AmmoType — fires itself), or a missile launcher WITH loaded
    /// ammo. Equivalent to <c>IsCombatCapable == true</c>.
    /// </summary>
    HasUsableWeapon,

    /// <summary>
    /// No weapon is wielded in a main-weapon slot. The server allows a melee
    /// attack with no weapon equipped (uses the unarmed / fist skill), so the
    /// bot can attack RIGHT NOW via <see cref="AttackMode.Melee"/>.
    /// </summary>
    UnarmedMeleeOnly,

    /// <summary>
    /// A missile launcher (has AmmoType) is wielded in the main-weapon slot
    /// with NO ammo loaded and NO other usable weapon. The launcher cannot fire
    /// (server cancels), and a wielded launcher forces Missile combat mode, so
    /// the bot must DEQUIP the launcher before it can melee-unarmed. The Motor
    /// handles this autonomously when combat is warranted.
    /// </summary>
    LauncherNeedsDequip,
}

/// <summary>
/// Wire facts for one inventory item as the weapon-readiness classifier
/// needs them. Fields mirror the same projection fields used by
/// <see cref="LlmGoalPolicy.IsCombatCapable"/>: WieldedAt is the
/// CurrentWieldedLocation equip-slot mask, AmmoType (W_AMMO_TYPE)
/// discriminates a launcher (non-null) from a thrown weapon (null).
/// </summary>
internal readonly record struct WeaponStateItem(
    uint Guid, uint? ItemType, uint? WieldedAt, ushort? AmmoType);

internal static class CombatWeaponSelection
{
    /// <summary>
    /// Choose the attack mode from the player's wielded weapon types.
    /// A missile weapon wielded with NO melee weapon wielded → Missile;
    /// otherwise Melee (covers a wielded melee weapon AND the unarmed
    /// case — the server resolves an unarmed strike via the melee
    /// action). If somehow both are wielded, melee wins (the proven
    /// path); that combination is not normally reachable.
    /// </summary>
    public static AttackMode SelectAttackMode(IEnumerable<(uint? ItemType, bool Wielded)> items)
    {
        var melee = false;
        var missile = false;
        foreach (var it in items)
        {
            if (!it.Wielded || it.ItemType is not uint t) continue;
            if ((t & ItemTypeMasks.MeleeWeapon) != 0) melee = true;
            if ((t & ItemTypeMasks.MissileWeapon) != 0) missile = true;
        }
        return missile && !melee ? AttackMode.Missile : AttackMode.Melee;
    }

    /// <summary>
    /// True when at least one MISSILE weapon (ItemType MissileWeapon bit) is
    /// currently wielded. Mirrors the missile leg of
    /// <see cref="SelectAttackMode"/> so the two never diverge. Used to detect
    /// that an in-flight missile attack's weapon has DISAPPEARED — a THROWN
    /// weapon is consumed when thrown (the server deletes it, sends "out of
    /// ammunition", and drops the bot to NonCombat), after which no missile
    /// weapon remains and re-sending the missile attack only yields
    /// AttackDone(ActionCancelled) (server: <c>weapon == null</c>). Pure
    /// wire-type projection; no target choice, no object identity, no game
    /// knowledge.
    /// </summary>
    public static bool HasWieldedMissileWeapon(IEnumerable<(uint? ItemType, bool Wielded)> items)
    {
        foreach (var it in items)
        {
            if (it.Wielded && it.ItemType is uint t && (t & ItemTypeMasks.MissileWeapon) != 0)
                return true;
        }
        return false;
    }

    /// <summary>ChangeCombatMode value for the chosen attack mode
    /// (CombatMode enum: Melee=2, Missile=4).</summary>
    public static uint CombatModeValue(AttackMode mode) =>
        mode == AttackMode.Missile ? 4u : 2u;

    /// <summary>
    /// Classify the bot's weapon readiness state from its inventory
    /// projection. Mirrors <see cref="LlmGoalPolicy.IsCombatCapable"/>
    /// slot-mask logic exactly (same WeaponSwap.MainWeaponSlotMask gate,
    /// same ItemTypeMasks.MissileAmmoSlot for loaded ammo, same thrown-weapon
    /// AmmoType==null discriminator) so the two never diverge.
    ///
    /// <para>Returns (<see cref="WeaponReadiness.HasUsableWeapon"/>, null) when
    /// IsCombatCapable would return true.</para>
    /// <para>Returns (<see cref="WeaponReadiness.LauncherNeedsDequip"/>,
    /// launcherGuid) when a launcher is wielded with no ammo and no other
    /// usable weapon — the Motor must dequip the launcher before unarmed
    /// melee is possible.</para>
    /// <para>Returns (<see cref="WeaponReadiness.UnarmedMeleeOnly"/>, null)
    /// when no weapon is wielded in any main-weapon slot — the server
    /// permits a melee attack with fists.</para>
    ///
    /// Pure wire-state projection; no game knowledge, no target choice.
    /// </summary>
    public static (WeaponReadiness State, uint? LauncherGuid) ClassifyWeaponState(
        IEnumerable<WeaponStateItem> items)
    {
        uint? launcherGuid = null;
        bool meleeWielded  = false;
        bool thrownWielded = false;
        bool launcherWielded = false;
        bool ammoLoaded    = false;

        foreach (var i in items)
        {
            // A weapon counts only when wielded in a MAIN-WEAPON slot.
            // Loaded ammo sits in the ammo slot (outside MainWeaponSlotMask)
            // and can carry the MissileWeapon ItemType bit, so the slot mask
            // — not WieldedAt != 0 — is what tells a wielded launcher from
            // loaded ammo (mirrors IsCombatCapable / WeaponSwap.IsWieldedWeapon).
            if (i.WieldedAt is uint w &&
                (w & WeaponSwap.MainWeaponSlotMask) != 0 &&
                i.ItemType is uint it)
            {
                if ((it & ItemTypeMasks.MeleeWeapon) != 0)
                    meleeWielded = true;
                if ((it & ItemTypeMasks.MissileWeapon) != 0)
                {
                    // A THROWN weapon (no AmmoType — the server fires the weapon
                    // itself: `ammo = weapon.IsAmmoLauncher ? GetEquippedAmmo()
                    // : weapon`) is combat-capable on its own. A LAUNCHER (has
                    // AmmoType) needs loaded ammo; without it the server cancels
                    // the attack.
                    if (i.AmmoType is null)
                        thrownWielded = true;
                    else
                    {
                        launcherWielded = true;
                        launcherGuid = i.Guid;
                    }
                }
            }
            // Ammo slot check — mirrors IsCombatCapable's ammoLoaded test.
            if (i.WieldedAt is uint aw && aw == ItemTypeMasks.MissileAmmoSlot)
                ammoLoaded = true;
        }

        if (meleeWielded || thrownWielded || (launcherWielded && ammoLoaded))
            return (WeaponReadiness.HasUsableWeapon, null);
        if (launcherWielded)
            return (WeaponReadiness.LauncherNeedsDequip, launcherGuid);
        return (WeaponReadiness.UnarmedMeleeOnly, null);
    }
}
