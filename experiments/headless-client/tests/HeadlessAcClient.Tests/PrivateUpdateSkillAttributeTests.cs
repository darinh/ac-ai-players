// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the discrete self skill/attribute descriptor updates:
//   PrivateUpdateSkill     (0x02DD) -> upsert PdSkill by id
//   PrivateUpdateAttribute (0x02E3) -> upsert PdAttribute by name
// These keep the login PlayerDescription character sheet LIVE after the
// LLM spends XP via a Raise* verb (the seed is otherwise stale until
// relogin). Both carry a per-id 1-byte ByteSequence (independent counters
// per skill / per attribute), gated wrap-aware like the property-update
// family.

using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class PrivateUpdateSkillAttributeTests
{
    private const uint Self = 0x5000005Cu;

    private static byte[] BuildSkillWire(
        byte sequence, uint skillId, ushort ranks, ushort adjustPp,
        uint advancementClass, uint expSpent, uint initLevel)
    {
        var buf = new byte[PrivateUpdateSkillMessage.PackedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.PrivateUpdateSkill);
        buf[4] = sequence;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5), skillId);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(9), ranks);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(11), adjustPp);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(13), advancementClass);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(17), expSpent);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(21), initLevel);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(25), 0u);  // resistance
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(29), 0d);  // lastUsed
        return buf;
    }

    private static byte[] BuildAttributeWire(
        byte sequence, uint attrId, uint ranks, uint startingValue, uint expSpent)
    {
        var buf = new byte[PrivateUpdateAttributeMessage.PackedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.PrivateUpdateAttribute);
        buf[4] = sequence;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5), attrId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(9), ranks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(13), startingValue);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(17), expSpent);
        return buf;
    }

    private static WorldState SelfWorld()
    {
        var ws = new WorldState();
        ws.SetSelf(Self);
        return ws;
    }

    // ---- byte-level decode ----

    [Fact]
    public void Decode_Skill_ParsesAllFields_SkipsAdjustPp()
    {
        // Jump (id 22) raised to 13, adjustPP=1 (must be consumed, not surfaced).
        var wire = BuildSkillWire(
            sequence: 9, skillId: 22, ranks: 13, adjustPp: 1,
            advancementClass: 2, expSpent: 6000, initLevel: 0);
        var msg = GameMessageDecoder.Decode(wire) as PrivateUpdateSkillMessage;
        Assert.NotNull(msg);
        Assert.Equal((byte)9, msg!.Sequence);
        Assert.Equal(22u, msg.Skill);
        Assert.Equal((ushort)13, msg.Ranks);
        Assert.Equal(2u, msg.AdvancementClass);
        Assert.Equal(6000u, msg.ExperienceSpent);
        Assert.Equal(0u, msg.InitLevel);
    }

    [Fact]
    public void Decode_Skill_ShortPayload_ReturnsNull()
    {
        var wire = BuildSkillWire(1, 22, 1, 1, 2, 0, 0);
        var truncated = wire.AsSpan(0, PrivateUpdateSkillMessage.PackedSize - 1).ToArray();
        Assert.Null(GameMessageDecoder.Decode(truncated));
    }

    [Fact]
    public void Decode_Attribute_ParsesAllFields()
    {
        // Endurance (id 2), ranks 7, startingValue 40, xp 1234.
        var wire = BuildAttributeWire(sequence: 4, attrId: 2, ranks: 7, startingValue: 40, expSpent: 1234);
        var msg = GameMessageDecoder.Decode(wire) as PrivateUpdateAttributeMessage;
        Assert.NotNull(msg);
        Assert.Equal((byte)4, msg!.Sequence);
        Assert.Equal(2u, msg.Attribute);
        Assert.Equal(7u, msg.Ranks);
        Assert.Equal(40u, msg.StartingValue);
        Assert.Equal(1234u, msg.ExperienceSpent);
    }

    [Fact]
    public void Decode_Attribute_ShortPayload_ReturnsNull()
    {
        var wire = BuildAttributeWire(1, 2, 0, 10, 0);
        var truncated = wire.AsSpan(0, PrivateUpdateAttributeMessage.PackedSize - 1).ToArray();
        Assert.Null(GameMessageDecoder.Decode(truncated));
    }

    // ---- apply: skills ----

    [Fact]
    public void ApplySkill_UpsertsExistingById_KeepsOtherSkills()
    {
        var ws = SelfWorld();
        ws.SeedSelfSkills(new[]
        {
            new PdSkill("Jump", 22u, 2u, 12u, 0u, 5000u),
            new PdSkill("Healing", 21u, 2u, 5u, 0u, 1000u),
        });

        // Jump raised 12 -> 13.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildSkillWire(1, 22, 13, 1, 2, 6000, 0))!));

        var skills = ws.TryGet(Self)!.SelfSkills!;
        Assert.Equal(2, skills.Count); // no duplicate added
        var jump = skills.Single(s => s.Id == 22u);
        Assert.Equal(13u, jump.Ranks);
        Assert.Equal(6000u, jump.ExperienceSpent);
        // Healing untouched.
        Assert.Equal(5u, skills.Single(s => s.Id == 21u).Ranks);
    }

    [Fact]
    public void ApplySkill_AddsNewSkillWhenIdAbsent()
    {
        var ws = SelfWorld();
        ws.SeedSelfSkills(new[] { new PdSkill("Jump", 22u, 2u, 12u, 0u, 5000u) });

        // TwoHandedCombat (id 41) not in the seeded set -> appended.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildSkillWire(1, 41, 11, 1, 2, 4000, 0))!));

        var skills = ws.TryGet(Self)!.SelfSkills!;
        Assert.Equal(2, skills.Count);
        var twoH = skills.Single(s => s.Id == 41u);
        Assert.Equal("TwoHandedCombat", twoH.Name);
        Assert.Equal(11u, twoH.Ranks);
        Assert.Equal(2u, twoH.AdvancementClass);
    }

    [Fact]
    public void ApplySkill_StaleSequenceDropped_PerIdIndependent()
    {
        var ws = SelfWorld();
        ws.SeedSelfSkills(new[]
        {
            new PdSkill("Jump", 22u, 2u, 12u, 0u, 5000u),
            new PdSkill("Healing", 21u, 2u, 5u, 0u, 1000u),
        });

        // Jump at seq 5 accepted.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(BuildSkillWire(5, 22, 13, 1, 2, 6000, 0))!));
        // Jump at seq 3 (stale) dropped — value unchanged.
        Assert.False(ws.Apply(GameMessageDecoder.Decode(BuildSkillWire(3, 22, 99, 1, 2, 9999, 0))!));
        Assert.Equal(13u, ws.TryGet(Self)!.SelfSkills!.Single(s => s.Id == 22u).Ranks);

        // Healing carries an INDEPENDENT counter: seq 1 still accepted even
        // though Jump is already at seq 5.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(BuildSkillWire(1, 21, 6, 1, 2, 1500, 0))!));
        Assert.Equal(6u, ws.TryGet(Self)!.SelfSkills!.Single(s => s.Id == 21u).Ranks);
    }

    [Fact]
    public void ApplySkill_AdvancementClassChange_FlowsToProjection()
    {
        var ws = SelfWorld();
        ws.SeedSelfSkills(new[] { new PdSkill("Axe", 1u, 1u, 0u, 0u, 0u) }); // Untrained

        // Pre: Untrained skill filtered out of the trained list.
        var before = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.Null(before!.Self.TrainedSkills);

        // Axe becomes Trained (sac 1 -> 2) with 3 raised ranks.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildSkillWire(1, 1, 3, 1, 2, 800, 0))!));

        var after = WorldStateProjection.FromWorldState(ws, weenies: null);
        var axe = after!.Self.TrainedSkills!.Single(s => s.Name == "Axe");
        Assert.Equal("trained", axe.Advancement);
        Assert.Equal(3u, axe.RaisedRanks);
    }

    [Fact]
    public void ApplySkill_DroppedWhenSelfGuidUnknown()
    {
        var ws = new WorldState(); // no SetSelf
        Assert.False(ws.Apply(GameMessageDecoder.Decode(BuildSkillWire(1, 22, 13, 1, 2, 6000, 0))!));
    }

    // ---- apply: attributes ----

    [Fact]
    public void ApplyAttribute_UpsertsByName_BaseIsStartingPlusRanks()
    {
        var ws = SelfWorld();
        ws.SeedSelfAttributes(new[]
        {
            new PdAttribute("strength", 10u, 0u, 0u),
            new PdAttribute("endurance", 45u, 5u, 50u),
        });

        // Endurance (id 2): startingValue 40 + ranks 8 -> base 48.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildAttributeWire(2, 2, 8, 40, 1500))!));

        var attrs = ws.TryGet(Self)!.SelfAttributes!;
        Assert.Equal(2, attrs.Count); // upserted, not duplicated
        var end = attrs.Single(a => a.Name == "endurance");
        Assert.Equal(48u, end.Base);
        Assert.Equal(8u, end.Ranks);
        Assert.Equal(10u, attrs.Single(a => a.Name == "strength").Base); // untouched
    }

    [Fact]
    public void ApplyAttribute_NonPrimaryId_NoOp()
    {
        var ws = SelfWorld();
        // id 7 is not a primary attribute (1..6); the apply must reject it
        // without advancing any sequence or mutating the snapshot.
        Assert.False(ws.Apply(GameMessageDecoder.Decode(
            BuildAttributeWire(1, 7, 1, 1, 1))!));
        Assert.Null(ws.TryGet(Self)!.SelfAttributes);
    }

    [Fact]
    public void ApplyAttribute_StaleSequenceDropped()
    {
        var ws = SelfWorld();
        ws.SeedSelfAttributes(new[] { new PdAttribute("self", 10u, 0u, 0u) });

        // Self (id 6) at seq 4 accepted.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(BuildAttributeWire(4, 6, 3, 10, 900))!));
        // seq 2 (stale) dropped.
        Assert.False(ws.Apply(GameMessageDecoder.Decode(BuildAttributeWire(2, 6, 99, 10, 9999))!));
        Assert.Equal(13u, ws.TryGet(Self)!.SelfAttributes!.Single(a => a.Name == "self").Base);
    }

    [Fact]
    public void ApplyAttribute_DroppedWhenSelfGuidUnknown()
    {
        var ws = new WorldState();
        Assert.False(ws.Apply(GameMessageDecoder.Decode(BuildAttributeWire(1, 2, 8, 40, 1500))!));
    }

    // ---- discrete-before-login: the login seed must MERGE, not bail ----

    [Fact]
    public void SeedSkills_AfterEarlyDiscrete_MergesAndPreservesDiscrete()
    {
        var ws = SelfWorld();

        // An early discrete PrivateUpdateSkill lands before the login bundle
        // (SelfGuid is known pre-world-entry), creating a 1-entry list.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildSkillWire(7, 22, 13, 1, 2, 6000, 0))!)); // Jump r13, fresh
        Assert.Single(ws.TryGet(Self)!.SelfSkills!);

        // Login bundle arrives with a STALE Jump (r12) plus other skills.
        Assert.True(ws.SeedSelfSkills(new[]
        {
            new PdSkill("Jump", 22u, 2u, 12u, 0u, 5000u),
            new PdSkill("Healing", 21u, 2u, 5u, 0u, 1000u),
        }));

        var skills = ws.TryGet(Self)!.SelfSkills!;
        Assert.Equal(2, skills.Count);
        // Discrete entry preserved (r13, not clobbered to login's r12).
        Assert.Equal(13u, skills.Single(s => s.Id == 22u).Ranks);
        // Missing skill filled in by the seed.
        Assert.Equal(5u, skills.Single(s => s.Id == 21u).Ranks);
    }

    [Fact]
    public void SeedAttributes_AfterEarlyDiscrete_MergesAndPreservesDiscrete()
    {
        var ws = SelfWorld();

        // Early discrete attribute update before login.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildAttributeWire(4, 2, 8, 40, 1500))!)); // endurance base 48, fresh
        Assert.Single(ws.TryGet(Self)!.SelfAttributes!);

        // Login bundle with a stale endurance (base 45) + another attribute.
        Assert.True(ws.SeedSelfAttributes(new[]
        {
            new PdAttribute("endurance", 45u, 5u, 50u),
            new PdAttribute("strength", 10u, 0u, 0u),
        }));

        var attrs = ws.TryGet(Self)!.SelfAttributes!;
        Assert.Equal(2, attrs.Count);
        Assert.Equal(48u, attrs.Single(a => a.Name == "endurance").Base); // preserved
        Assert.Equal(10u, attrs.Single(a => a.Name == "strength").Base);  // filled in
    }

    [Fact]
    public void SeedSkills_ReSentBundle_NoDuplicatesNoChange()
    {
        var ws = SelfWorld();
        var bundle = new[]
        {
            new PdSkill("Jump", 22u, 2u, 12u, 0u, 5000u),
            new PdSkill("Healing", 21u, 2u, 5u, 0u, 1000u),
        };
        Assert.True(ws.SeedSelfSkills(bundle));
        // A re-sent identical login bundle adds nothing (all ids present).
        Assert.False(ws.SeedSelfSkills(bundle));
        Assert.Equal(2, ws.TryGet(Self)!.SelfSkills!.Count);
    }
}
