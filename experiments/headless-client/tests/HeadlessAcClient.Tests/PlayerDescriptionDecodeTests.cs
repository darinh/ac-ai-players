// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the PlayerDescription (0x0013) login-bundle partial decode:
// extract initial Level (PropertyInt 25) + experience totals
// (PropertyInt64 TotalExperience=1 / AvailableExperience=2) from the
// leading flags + Int32 + Int64 PackableHashTable sections, and seed
// them into self world-state without disturbing the discrete-update
// stale-gating maps.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class PlayerDescriptionDecodeTests
{
    private const uint FlagInt32 = 0x0001;
    private const uint FlagInt64 = 0x0080;

    // Builds a PlayerDescription body (the bytes AFTER the 16B GameEvent
    // envelope): u32 flags, u32 weenieType, then the present Int32 and
    // Int64 PackableHashTable sections. The flags dword is derived from
    // which sections are supplied. Keys are sorted to match the server.
    private static byte[] BuildBody(
        IReadOnlyList<(uint key, int val)>? int32 = null,
        IReadOnlyList<(uint key, long val)>? int64 = null,
        uint weenieType = 1u)
    {
        var buf = new List<byte>();
        uint flags = 0;
        if (int32 is { Count: > 0 }) flags |= FlagInt32;
        if (int64 is { Count: > 0 }) flags |= FlagInt64;

        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); buf.AddRange(b); }
        void U32(uint v)   { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); buf.AddRange(b); }
        void S32(int v)    { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v);  buf.AddRange(b); }
        void S64(long v)   { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v);  buf.AddRange(b); }

        U32(flags);
        U32(weenieType);

        if (int32 is { Count: > 0 })
        {
            var sorted = new List<(uint key, int val)>(int32);
            sorted.Sort((a, b) => a.key.CompareTo(b.key));
            U16((ushort)sorted.Count);
            U16(64); // numBuckets (ignored by the decoder)
            foreach (var (key, val) in sorted) { U32(key); S32(val); }
        }

        if (int64 is { Count: > 0 })
        {
            var sorted = new List<(uint key, long val)>(int64);
            sorted.Sort((a, b) => a.key.CompareTo(b.key));
            U16((ushort)sorted.Count);
            U16(64);
            foreach (var (key, val) in sorted) { U32(key); S64(val); }
        }

        return buf.ToArray();
    }

    [Fact]
    public void Decode_FullBundle_ExtractsLevelAndExperience()
    {
        var body = BuildBody(
            int32: new (uint, int)[] { (24u, 9), (25u, 10), (27u, 120) }, // includes Level=25
            int64: new (uint, long)[] { (1u, 5000L), (2u, 1200L) });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);

        Assert.NotNull(p?.PlayerDescription);
        Assert.Equal(10, p!.PlayerDescription!.Level);
        Assert.Equal(5000L, p.PlayerDescription.TotalExperience);
        Assert.Equal(1200L, p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_LargeUnspentXp_RoundTripsAsSigned64()
    {
        // The exact cp-2278 precondition magnitude (~82k unspent).
        var body = BuildBody(
            int32: new (uint, int)[] { (25u, 1) },
            int64: new (uint, long)[] { (1u, 90000L), (2u, 82659L) });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);

        Assert.Equal(1, p!.PlayerDescription!.Level);
        Assert.Equal(90000L, p.PlayerDescription.TotalExperience);
        Assert.Equal(82659L, p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_Int32WithoutLevelKey_LeavesLevelNull_StillReadsXp()
    {
        var body = BuildBody(
            int32: new (uint, int)[] { (24u, 5), (27u, 60) }, // no key 25
            int64: new (uint, long)[] { (1u, 700L), (2u, 700L) });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);

        Assert.Null(p!.PlayerDescription!.Level);
        Assert.Equal(700L, p.PlayerDescription.TotalExperience);
        Assert.Equal(700L, p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_Int64SectionOnly_NoInt32Flag()
    {
        var body = BuildBody(
            int64: new (uint, long)[] { (1u, 42L), (2u, 17L) });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);

        Assert.Null(p!.PlayerDescription!.Level);
        Assert.Equal(42L, p.PlayerDescription.TotalExperience);
        Assert.Equal(17L, p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_Int32SectionOnly_NoInt64Flag_LeavesXpNull()
    {
        var body = BuildBody(int32: new (uint, int)[] { (25u, 3) });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);

        Assert.Equal(3, p!.PlayerDescription!.Level);
        Assert.Null(p.PlayerDescription.TotalExperience);
        Assert.Null(p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_OnlyAvailableExperiencePresent()
    {
        var body = BuildBody(int64: new (uint, long)[] { (2u, 555L) });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);

        Assert.Null(p!.PlayerDescription!.TotalExperience);
        Assert.Equal(555L, p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_BodyTooShortForHeader_ReturnsAllNull()
    {
        var p = GameEventPayloadDecoder.Decode(new byte[6], GameEventType.PlayerDescription);

        Assert.NotNull(p?.PlayerDescription);
        Assert.Null(p!.PlayerDescription!.Level);
        Assert.Null(p.PlayerDescription.TotalExperience);
        Assert.Null(p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_TruncatedBeforeInt64Header_ReturnsLevelOnly()
    {
        // Full bundle then chop the trailing bytes so the Int64 header
        // can't be read: the Int32 Level still parses, XP stays null.
        var full = BuildBody(
            int32: new (uint, int)[] { (25u, 8) },
            int64: new (uint, long)[] { (1u, 1L), (2u, 1L) });
        // Int32 section ends at 8 (header+weenie) + 4 (table header) + 8
        // (one entry) = 20. Truncate to 22 so only 2 of the 4 Int64
        // header bytes survive.
        var truncated = full.AsSpan(0, 22).ToArray();

        var p = GameEventPayloadDecoder.Decode(truncated, GameEventType.PlayerDescription);

        Assert.Equal(8, p!.PlayerDescription!.Level);
        Assert.Null(p.PlayerDescription.TotalExperience);
        Assert.Null(p.PlayerDescription.AvailableExperience);
    }

    [Fact]
    public void Decode_Int64CountExceedsBody_FailsClosed_LeavesXpNull()
    {
        // Hand-build a body whose Int64 count claims 5 entries but only
        // one entry's worth of bytes follows. Because the decoder now
        // continues past Int64 to the attribute/skill vectors, a bogus
        // count must FAIL CLOSED (return partial with XP null) rather than
        // clamp-and-continue — a desynced cursor could otherwise decode
        // garbage as attributes/skills.
        var buf = new List<byte>();
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); buf.AddRange(b); }
        void U32(uint v)   { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); buf.AddRange(b); }
        void S64(long v)   { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v);  buf.AddRange(b); }
        U32(FlagInt64);   // flags: Int64 only
        U32(1u);          // weenieType
        U16(5);           // count: LIES — claims 5
        U16(64);          // numBuckets
        U32(2u); S64(999L); // exactly ONE (key=Available, val) entry

        var p = GameEventPayloadDecoder.Decode(buf.ToArray(), GameEventType.PlayerDescription);

        Assert.NotNull(p?.PlayerDescription);
        Assert.Null(p!.PlayerDescription!.AvailableExperience);
        Assert.Null(p.PlayerDescription.Attributes);
        Assert.Null(p.PlayerDescription.Skills);
    }

    [Fact]
    public void Decode_ToString_RendersFields()
    {
        var body = BuildBody(
            int32: new (uint, int)[] { (25u, 4) },
            int64: new (uint, long)[] { (1u, 200L), (2u, 50L) });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.PlayerDescription);

        Assert.Equal(
            "PlayerDescription(level=4 totalXp=200 unspentXp=50)",
            p!.PlayerDescription!.ToString());
    }
}

public class PlayerDescriptionSeedTests
{
    private const uint Self = 0x5000005Cu;
    private const uint LevelId = 25u;
    private const uint TotalId = PrivateUpdatePropertyInt64Message.TotalExperienceId;       // 1
    private const uint AvailId = PrivateUpdatePropertyInt64Message.AvailableExperienceId;   // 2

    [Fact]
    public void Seed_WritesLevelAndXp_IntoSelfSnapshot()
    {
        var ws = new WorldState();
        ws.SetSelf(Self);

        Assert.True(ws.SeedSelfPropertyInt(LevelId, 6));
        Assert.True(ws.SeedSelfPropertyInt64(TotalId, 12345L));
        Assert.True(ws.SeedSelfPropertyInt64(AvailId, 678L));

        var snap = ws.TryGet(Self)!;
        Assert.Equal(6, snap.PropertyInts![LevelId]);
        Assert.Equal(12345L, snap.PropertyInt64s![TotalId]);
        Assert.Equal(678L, snap.PropertyInt64s[AvailId]);
    }

    [Fact]
    public void Seed_DoesNotPoisonStaleGate_LaterDiscreteSeqZeroStillApplies()
    {
        // The load-bearing invariant: a bundle seed must NOT advance the
        // per-property byte-seq high-water map, so the first real discrete
        // update (which starts at its own low sequence, often 0) is still
        // accepted and overwrites the seed.
        var ws = new WorldState();
        ws.SetSelf(Self);
        ws.SeedSelfPropertyInt64(AvailId, 82659L);

        var applied = ws.Apply(new PrivateUpdatePropertyInt64Message(
            Sequence: 0, Property: AvailId, Value: 82559L));

        Assert.True(applied);
        Assert.Equal(82559L, ws.TryGet(Self)!.PropertyInt64s![AvailId]);
    }

    [Fact]
    public void Seed_SkippedWhenDiscreteAlreadyApplied()
    {
        // If a discrete update somehow arrives first, a later bundle must
        // NOT clobber the fresher discrete value.
        var ws = new WorldState();
        ws.SetSelf(Self);
        Assert.True(ws.Apply(new PrivateUpdatePropertyInt64Message(
            Sequence: 3, Property: AvailId, Value: 1000L)));

        Assert.False(ws.SeedSelfPropertyInt64(AvailId, 9999L));
        Assert.Equal(1000L, ws.TryGet(Self)!.PropertyInt64s![AvailId]);
    }

    [Fact]
    public void Seed_DroppedWhenSelfGuidUnknown()
    {
        var ws = new WorldState();
        Assert.False(ws.SeedSelfPropertyInt(LevelId, 5));
        Assert.False(ws.SeedSelfPropertyInt64(TotalId, 5L));
    }

    [Fact]
    public void SeedAttributesAndSkills_WriteIntoSnapshot_MergeContract()
    {
        var ws = new WorldState();
        ws.SetSelf(Self);

        var attrs = new[] { new PdAttribute("endurance", 40u, 5u, 50u) };
        var skills = new[] { new PdSkill("WarMagic", 34u, 2u, 30u, 0u, 5000u) };

        Assert.True(ws.SeedSelfAttributes(attrs));
        Assert.True(ws.SeedSelfSkills(skills));

        var snap = ws.TryGet(Self)!;
        Assert.Same(attrs, snap.SelfAttributes);
        Assert.Same(skills, snap.SelfSkills);

        // Re-sending an ALREADY-PRESENT key is a no-op and must NOT clobber
        // the existing value (returns false, reference unchanged).
        Assert.False(ws.SeedSelfAttributes(new[] { new PdAttribute("endurance", 99u, 9u, 9u) }));
        Assert.False(ws.SeedSelfSkills(new[] { new PdSkill("WarMagic", 34u, 2u, 99u, 0u, 9u) }));
        Assert.Same(attrs, ws.TryGet(Self)!.SelfAttributes);
        Assert.Same(skills, ws.TryGet(Self)!.SelfSkills);
        Assert.Equal(40u, ws.TryGet(Self)!.SelfAttributes!.Single(a => a.Name == "endurance").Base);
    }

    [Fact]
    public void SeedAttributesAndSkills_DroppedWhenSelfGuidUnknown()
    {
        var ws = new WorldState();
        Assert.False(ws.SeedSelfAttributes(new[] { new PdAttribute("self", 10u, 0u, 0u) }));
        Assert.False(ws.SeedSelfSkills(new[] { new PdSkill("Healing", 21u, 2u, 0u, 0u, 0u) }));
    }

    [Theory]
    [InlineData(0u, false)]  // Inactive
    [InlineData(1u, false)]  // Untrained
    [InlineData(2u, true)]   // Trained
    [InlineData(3u, true)]   // Specialized
    public void PdSkill_IsRaisable_TrueOnlyForTrainedOrSpecialized(uint advancementClass, bool expected)
    {
        // IsRaisable is the single source of truth for "RaiseSkill can target
        // this skill" — read by both the `trained skills` prompt projection and
        // the login diagnostic. Only the wire SkillAdvancementClass Trained(2)
        // and Specialized(3) are raisable; Inactive(0)/Untrained(1) are not.
        var skill = new PdSkill("anything", 1u, advancementClass, 0u, 0u, 0u);
        Assert.Equal(expected, skill.IsRaisable);
    }
}
