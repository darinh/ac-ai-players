// SPDX-License-Identifier: AGPL-3.0-or-later
// CombatWeaponSelection — pick the attack opcode family (melee vs
// missile) from the player's currently WIELDED weapon.
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
}
