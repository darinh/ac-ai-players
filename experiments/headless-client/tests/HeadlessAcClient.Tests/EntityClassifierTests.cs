// SPDX-License-Identifier: AGPL-3.0-or-later
// EntityClassifier tests — wire-bit -> EntityKind classification used
// by both the visible projection (IsMonster) and the sighting-recall
// memory. Single source of truth, so it cannot drift.

using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class EntityClassifierTests
{
    private const uint Creature = ItemTypeMasks.Creature;        // 0x10
    private const uint Attackable = (uint)ObjectDescriptionFlag.Attackable; // 0x10
    private const uint Vendor = (uint)ObjectDescriptionFlag.Vendor;         // 0x200
    private const uint Healer = (uint)ObjectDescriptionFlag.Healer;         // 0x10000
    private const uint Corpse = (uint)ObjectDescriptionFlag.Corpse;         // 0x2000
    private const uint RadarBlip = (uint)WeenieHeaderFlag.RadarBlipColor;   // 0x100000

    [Fact]
    public void IsMonster_AttackableCreatureNoBlip_True()
    {
        // A Chicken / Sparring Golem: creature + attackable, no friendly
        // radar-blip color, not a vendor/healer/corpse.
        Assert.True(EntityClassifier.IsMonster(Creature, Attackable, 0u));
        Assert.Equal(EntityKind.Mob, EntityClassifier.ClassifySighting(Creature, Attackable, 0u));
    }

    [Fact]
    public void IsMonster_VendorCreature_False()
    {
        // A vendor NPC carries creature+attackable but the Vendor desc
        // bit must exclude it from the monster composite.
        Assert.False(EntityClassifier.IsMonster(Creature, Attackable | Vendor, 0u));
        Assert.Equal(EntityKind.NPC, EntityClassifier.ClassifySighting(Creature, Attackable | Vendor, 0u));
    }

    [Fact]
    public void IsMonster_HealerCreature_False()
    {
        Assert.False(EntityClassifier.IsMonster(Creature, Attackable | Healer, 0u));
        Assert.Equal(EntityKind.NPC, EntityClassifier.ClassifySighting(Creature, Attackable | Healer, 0u));
    }

    [Fact]
    public void IsMonster_RadarBlipCreature_False()
    {
        // A friendly creature flagged with a radar-blip color (the
        // server's friend/foe hint) is not a monster.
        Assert.False(EntityClassifier.IsMonster(Creature, Attackable, RadarBlip));
        Assert.Equal(EntityKind.NPC, EntityClassifier.ClassifySighting(Creature, Attackable, RadarBlip));
    }

    [Fact]
    public void IsMonster_CorpseCreature_False()
    {
        // Corpses can retain creature+attackable bits; excluded.
        Assert.False(EntityClassifier.IsMonster(Creature, Attackable | Corpse, 0u));
    }

    [Fact]
    public void IsMonster_NonAttackableCreature_False()
    {
        Assert.False(EntityClassifier.IsMonster(Creature, 0u, 0u));
        Assert.Equal(EntityKind.NPC, EntityClassifier.ClassifySighting(Creature, 0u, 0u));
    }

    [Fact]
    public void ClassifySighting_NonCreature_Unknown()
    {
        // An item / door / portal (no creature bit) is not surfaced in
        // the creature-recall section, so its kind stays Unknown.
        Assert.Equal(EntityKind.Unknown, EntityClassifier.ClassifySighting(0u, 0u, 0u));
        Assert.Equal(EntityKind.Unknown, EntityClassifier.ClassifySighting(0x1u /*MeleeWeapon*/, 0u, 0u));
    }
}
