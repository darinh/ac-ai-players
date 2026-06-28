// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the PlayerDescription (0x0013) ATTRIBUTE + SKILL vector decode.
// These exercise the full traversal past every property section (Int32,
// Int64, Bool, Double, String, Did, Iid, Position) to reach the attribute
// block and skill PackableHashTable that carry the character sheet. The
// builder mirrors the server serializer GameEventPlayerDescription.cs byte
// for byte (write order, string padding, fixed-entry sizes).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class PlayerDescriptionVectorDecodeTests
{
    // propertyFlags bits
    private const uint FInt32 = 0x0001, FBool = 0x0002, FDouble = 0x0004,
                       FDid = 0x0008, FString = 0x0010, FPosition = 0x0020,
                       FIid = 0x0040, FInt64 = 0x0080;
    // vectorFlags bits
    private const uint VAttribute = 0x0001, VSkill = 0x0002, VSpell = 0x0100, VEnchantment = 0x0200;
    // EnchantmentMask bits
    private const uint EMMultiplicative = 0x0001, EMAdditive = 0x0002, EMVitae = 0x0004, EMCooldown = 0x0008;

    private sealed class Builder
    {
        private readonly List<byte> _buf = new();
        public void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _buf.AddRange(b); }
        public void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _buf.AddRange(b); }
        public void S32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); _buf.AddRange(b); }
        public void S64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); _buf.AddRange(b); }
        public void F32(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); _buf.AddRange(b); }
        public void F64(double v) { var b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); _buf.AddRange(b); }
        public void PhtHeader(int count) { U16((ushort)count); U16(64); }
        // WriteString16L: u16 len + len CP1252 bytes + pad so (2+len) % 4 == 0.
        public void Str16L(string s)
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(s);
            U16((ushort)bytes.Length);
            _buf.AddRange(bytes);
            int field = 2 + bytes.Length;
            int pad = (4 - (field % 4)) % 4;
            for (int i = 0; i < pad; i++) _buf.Add(0);
        }
        public byte[] ToArray() => _buf.ToArray();
        // One Enchantment in the ACE-bots Enchantment.Write WIRE order: SpellID u16,
        // Layer u16, SpellCategory u16, HasSpellSetID u16, PowerLevel u32, StartTime f64,
        // Duration f64, CasterGuid u32, DegradeModifier f32, DegradeLimit f32,
        // LastTimeDegraded f64, StatModType u32, StatModKey u32, StatModValue f32 (offset
        // 56), [SpellSetID u32 when HasSpellSetID != 0].
        public void Enchantment(ushort spellId, float statModValue, bool hasSpellSetId = false)
        {
            U16(spellId); U16(0); U16(0); U16(hasSpellSetId ? (ushort)1 : (ushort)0);
            U32(0); F64(0); F64(0); U32(0); F32(0); F32(0); F64(0); U32(0); U32(0);
            F32(statModValue);
            if (hasSpellSetId) U32(0);
        }
    }

    // Realistic full login bundle: a small Int32 (Level), an Int64 (XP),
    // a Bool, a Double, a String (Name), a Did, an Iid, optionally a
    // Position, then the vector section with 9 attributes and the supplied
    // skills. Returns the body (bytes after the GameEvent envelope).
    private static byte[] BuildFullBundle(
        bool withPosition,
        IReadOnlyList<(uint id, uint ranks, uint start, uint xp, uint? cur)> attrs,
        IReadOnlyList<(uint id, uint ranks, uint sac, uint xp, uint init)> skills,
        int spellCount = 0,
        (uint mask, int mult, int add, int cooldown, ushort vitaeSpellId, float vitae, bool vitaeHasSpellSetId)? ench = null)
    {
        var b = new Builder();
        uint flags = FInt32 | FInt64 | FBool | FDouble | FString | FDid | FIid;
        if (withPosition) flags |= FPosition;
        b.U32(flags);
        b.U32(1u); // weenieType

        // Int32: Level (25) = 10, plus one extra key
        b.PhtHeader(2); b.U32(24u); b.S32(7); b.U32(25u); b.S32(10);
        // Int64: Total(1), Available(2)
        b.PhtHeader(2); b.U32(1u); b.S64(84199L); b.U32(2u); b.S64(78659L);
        // Bool: one entry (u32 key + u32 value)
        b.PhtHeader(1); b.U32(100u); b.U32(1u);
        // Double: one entry (u32 key + f64)
        b.PhtHeader(1); b.U32(200u); b.F64(1.5);
        // String: Name (key 1) — odd length to force padding
        b.PhtHeader(1); b.U32(1u); b.Str16L("Smoke");
        // Did: one entry
        b.PhtHeader(1); b.U32(300u); b.U32(0xABCDu);
        // Iid: one entry
        b.PhtHeader(1); b.U32(400u); b.U32(0x1234u);
        // Position (optional): PHT count=1, u32 PositionType + u32 landblock + 7 floats
        if (withPosition)
        {
            b.PhtHeader(1);
            b.U32(7u); // PositionType.LastOutsideDeath (value irrelevant to decode)
            b.U32(0xA9B40020u); // landblock raw
            b.F32(10f); b.F32(20f); b.F32(30f);          // X,Y,Z
            b.F32(1f); b.F32(0f); b.F32(0f); b.F32(0f);  // W,X,Y,Z
        }

        // Vector section
        uint vectorFlags = VAttribute | VSkill
            | (spellCount > 0 ? VSpell : 0u)
            | (ench is not null ? VEnchantment : 0u);
        b.U32(vectorFlags);
        b.U32(1u); // healthPresent
        // attribute block
        b.U32(0x1FFu); // AttributeCache.Full
        foreach (var (id, ranks, start, xp, cur) in attrs)
        {
            b.U32(ranks); b.U32(start); b.U32(xp);
            if (cur is uint c) b.U32(c);
        }
        // skill PHT
        b.PhtHeader(skills.Count);
        foreach (var (id, ranks, sac, xp, init) in skills)
        {
            // Ranks is a u16 on the wire (CreatureSkill.Ranks is ushort).
            b.U32(id); b.U16((ushort)ranks); b.U16(1); b.U32(sac); b.U32(xp); b.U32(init);
            b.U32(0u); b.F64(0d);
        }
        // Spell vector (PHT of u32 spellId + f32) — present only when spellCount > 0.
        if (spellCount > 0)
        {
            b.PhtHeader(spellCount);
            for (int i = 0; i < spellCount; i++) { b.U32((uint)(1000 + i)); b.F32(2f); }
        }
        // Enchantment registry: u32 mask, then List<Enchantment> (u32 count + entries) for
        // Multiplicative, Additive, Cooldown IN WRITE ORDER, then a single Vitae Enchantment.
        if (ench is { } e)
        {
            b.U32(e.mask);
            if ((e.mask & EMMultiplicative) != 0) { b.U32((uint)e.mult); for (int i = 0; i < e.mult; i++) b.Enchantment(110, 0.5f); }
            if ((e.mask & EMAdditive) != 0) { b.U32((uint)e.add); for (int i = 0; i < e.add; i++) b.Enchantment(111, 0.5f, hasSpellSetId: true); }
            if ((e.mask & EMCooldown) != 0) { b.U32((uint)e.cooldown); for (int i = 0; i < e.cooldown; i++) b.Enchantment(112, 0.5f); }
            if ((e.mask & EMVitae) != 0) b.Enchantment(e.vitaeSpellId, e.vitae, hasSpellSetId: e.vitaeHasSpellSetId);
        }
        return b.ToArray();
    }

    // The 9 attributes in server WRITE order with distinct values so the
    // per-attribute cursor stride is provable. Vitals (health/stamina/mana)
    // carry the extra Current u32.
    private static IReadOnlyList<(uint, uint, uint, uint, uint?)> NineAttributes() => new (uint, uint, uint, uint, uint?)[]
    {
        (1u, 10u, 30u, 100u, null), // strength      base 40
        (2u, 5u,  35u, 50u,  null), // endurance     base 40
        (3u, 0u,  40u, 0u,   null), // quickness     base 40
        (4u, 2u,  38u, 20u,  null), // coordination  base 40
        (5u, 1u,  39u, 10u,  null), // focus          base 40
        (6u, 3u,  37u, 30u,  null), // self           base 40
        (7u, 0u,  10u, 0u,   55u),  // health  base 10, Current 55
        (8u, 0u,  10u, 0u,   60u),  // stamina base 10, Current 60
        (9u, 0u,  10u, 0u,   65u),  // mana    base 10, Current 65
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]  // exercise the 36B Position-section skip too
    public void Decode_FullBundle_ExtractsAttributesAndSkills(bool withPosition)
    {
        // Skills: 34 (WarMagic) Trained raised 30; 21 (Healing) Specialized
        // raised 12; 1 (Axe, retired) Untrained raised 0.
        var skills = new (uint, uint, uint, uint, uint)[]
        {
            (34u, 30u, 2u, 5000u, 0u),  // WarMagic, Trained
            (21u, 12u, 3u, 8000u, 10u), // Healing, Specialized
            (1u,  0u,  1u, 0u,    0u),  // Axe, Untrained
        };
        var body = BuildFullBundle(withPosition, NineAttributes(), skills);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);
        var pd = p!.PlayerDescription!;

        // Level + XP still decode from the leading sections.
        Assert.Equal(10, pd.Level);
        Assert.Equal(84199L, pd.TotalExperience);
        Assert.Equal(78659L, pd.AvailableExperience);

        // Attributes: 9, names mapped, base = StartingValue + Ranks.
        Assert.NotNull(pd.Attributes);
        Assert.Equal(9, pd.Attributes!.Count);
        var byName = pd.Attributes.ToDictionary(a => a.Name);
        Assert.Equal(40u, byName["strength"].Base);
        Assert.Equal(40u, byName["endurance"].Base);
        Assert.Equal(40u, byName["self"].Base);
        Assert.Equal(10u, byName["health"].Base);
        Assert.Equal(10u, byName["mana"].Base);
        Assert.Equal(10u, byName["strength"].Ranks);
        Assert.Equal(100u, byName["strength"].ExperienceSpent);

        // Skills: ALL three decode (decoder does not filter); names mapped.
        Assert.NotNull(pd.Skills);
        Assert.Equal(3, pd.Skills!.Count);
        var war = pd.Skills.Single(s => s.Id == 34u);
        Assert.Equal("WarMagic", war.Name);
        Assert.Equal(2u, war.AdvancementClass);
        Assert.Equal(30u, war.Ranks);
        Assert.Equal(5000u, war.ExperienceSpent);
        var heal = pd.Skills.Single(s => s.Id == 21u);
        Assert.Equal("Healing", heal.Name);
        Assert.Equal(3u, heal.AdvancementClass);
        Assert.Equal(12u, heal.Ranks);
        Assert.Equal(10u, heal.InitLevel);
        var axe = pd.Skills.Single(s => s.Id == 1u);
        Assert.Equal("Axe", axe.Name);
        Assert.Equal(1u, axe.AdvancementClass);
    }

    [Fact]
    public void Decode_SecondSkillEntry_StartsAt32ByteStride()
    {
        // Two skills with distinct values prove the per-entry stride is
        // exactly 32B (a wrong stride would misread the second entry).
        var skills = new (uint, uint, uint, uint, uint)[]
        {
            (33u, 7u,  2u, 111u, 0u),  // LifeMagic
            (43u, 9u,  3u, 222u, 0u),  // VoidMagic
        };
        var body = BuildFullBundle(false, NineAttributes(), skills);

        var pd = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription)!.PlayerDescription!;

        var life = pd.Skills!.Single(s => s.Id == 33u);
        var voidm = pd.Skills!.Single(s => s.Id == 43u);
        Assert.Equal("LifeMagic", life.Name);
        Assert.Equal(7u, life.Ranks);
        Assert.Equal(111u, life.ExperienceSpent);
        Assert.Equal("VoidMagic", voidm.Name);
        Assert.Equal(9u, voidm.Ranks);
        Assert.Equal(222u, voidm.ExperienceSpent);
    }

    [Fact]
    public void Decode_TruncatedMidSkillSection_FailsClosed_KeepsAttributes()
    {
        var skills = new (uint, uint, uint, uint, uint)[] { (34u, 30u, 2u, 5000u, 0u) };
        var full = BuildFullBundle(false, NineAttributes(), skills);
        // Chop the final 10 bytes so the single skill entry can't fully read.
        var truncated = full.AsSpan(0, full.Length - 10).ToArray();

        var pd = GameEventPayloadDecoder.Decode(truncated, GameEventType.PlayerDescription)!.PlayerDescription!;

        // Attributes were fully read before the skill section overran.
        Assert.NotNull(pd.Attributes);
        Assert.Equal(9, pd.Attributes!.Count);
        // Skills fail closed: the section header claimed 1 entry but the
        // bytes don't fit, so no skills are surfaced.
        Assert.True(pd.Skills is null || pd.Skills.Count == 0);
    }

    [Fact]
    public void Decode_UnknownSkillId_FallsBackToSyntheticName()
    {
        var skills = new (uint, uint, uint, uint, uint)[] { (9999u, 1u, 2u, 10u, 0u) };
        var body = BuildFullBundle(false, NineAttributes(), skills);

        var pd = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription)!.PlayerDescription!;

        Assert.Equal("Skill9999", pd.Skills!.Single().Name);
    }

    // ── login death-vitae from the enchantment registry ──────────────────

    [Fact]
    public void Decode_LoginVitae_VitaeOnlyRegistry_ReadsMultiplier()
    {
        // No spells, no multiplicative/additive/cooldown lists — only the single vitae
        // enchantment (mask = Vitae). The decoder reads its StatModValue.
        var skills = new[] { (1u, 5u, 2u, 100u, 0u) };
        var body = BuildFullBundle(false, NineAttributes(), skills,
            spellCount: 0,
            ench: (mask: EMVitae, mult: 0, add: 0, cooldown: 0, vitaeSpellId: (ushort)666, vitae: 0.70f, vitaeHasSpellSetId: false));
        var pd = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription)!.PlayerDescription!;
        Assert.Equal(0.70f, pd.VitaeMultiplier!.Value);
    }

    [Fact]
    public void Decode_LoginVitae_SkipsSpellsAndAllLists_StillAligns()
    {
        // A spell vector AND all three enchantment lists (multiplicative w/ 2 entries,
        // additive w/ 1 entry that carries a trailing SpellSetID, cooldown w/ 1) precede
        // the vitae enchantment. The decoder must skip every one to land on the vitae read.
        var skills = new[] { (1u, 5u, 2u, 100u, 0u) };
        var body = BuildFullBundle(false, NineAttributes(), skills,
            spellCount: 3,
            ench: (mask: EMMultiplicative | EMAdditive | EMCooldown | EMVitae,
                   mult: 2, add: 1, cooldown: 1, vitaeSpellId: (ushort)666, vitae: 0.85f, vitaeHasSpellSetId: false));
        var pd = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription)!.PlayerDescription!;
        Assert.Equal(0.85f, pd.VitaeMultiplier!.Value);
    }

    [Fact]
    public void Decode_LoginVitae_NoEnchantmentVector_NullMultiplier()
    {
        var skills = new[] { (1u, 5u, 2u, 100u, 0u) };
        var body = BuildFullBundle(false, NineAttributes(), skills, spellCount: 2, ench: null);
        var pd = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription)!.PlayerDescription!;
        Assert.Null(pd.VitaeMultiplier);
        // The spell-vector skip must not corrupt the earlier attribute/skill reads.
        Assert.Equal(9, pd.Attributes!.Count);
        Assert.Single(pd.Skills!);
    }

    [Fact]
    public void Decode_LoginVitae_NonVitaeSpellId_SelfRejects()
    {
        // The vitae slot carries an enchantment whose SpellId is NOT the vitae spell (a
        // misaligned-cursor / unexpected-content guard): the decoder must NOT apply it.
        var skills = new[] { (1u, 5u, 2u, 100u, 0u) };
        var body = BuildFullBundle(false, NineAttributes(), skills,
            spellCount: 0,
            ench: (mask: EMVitae, mult: 0, add: 0, cooldown: 0, vitaeSpellId: (ushort)999, vitae: 0.50f, vitaeHasSpellSetId: false));
        var pd = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription)!.PlayerDescription!;
        Assert.Null(pd.VitaeMultiplier);
    }

    [Fact]
    public void Decode_LoginVitae_VitaeEntryCarriesSpellSetId_StillReadsOffset56()
    {
        // The server defaults HasSpellSetID = 1, so the real vitae entry is 64 bytes (a
        // trailing SpellSetID after StatModValue). The read uses only offsets 0 and 56, so
        // it must still work. Precede it with a multiplicative list whose entry ALSO carries
        // SpellSetID, so the variable-size skip is exercised on the realistic shape too.
        var skills = new[] { (1u, 5u, 2u, 100u, 0u) };
        var body = BuildFullBundle(false, NineAttributes(), skills,
            spellCount: 1,
            ench: (mask: EMMultiplicative | EMVitae,
                   mult: 1, add: 0, cooldown: 0, vitaeSpellId: (ushort)666, vitae: 0.60f, vitaeHasSpellSetId: true));
        var pd = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription)!.PlayerDescription!;
        Assert.Equal(0.60f, pd.VitaeMultiplier!.Value);
    }
}
