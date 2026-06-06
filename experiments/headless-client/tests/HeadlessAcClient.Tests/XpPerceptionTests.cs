// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for PrivateUpdatePropertyInt64 (0x02CF) decoding and the
// experience (XP) perception it feeds:
//   - byte-level decode of a hand-built 17-byte packet
//   - WorldState routing (dropped before SelfGuid; applied after)
//   - PER-PROPERTY sequence independence: TotalExperience(1) and
//     AvailableExperience(2) advance INDEPENDENT server byte counters,
//     so a low-seq update on one property must NOT be stale-dropped
//     just because the other property reached a higher seq.
//   - projection surfaces TotalExperience / AvailableExperience
//   - the LLM prompt renders the experience line

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class XpPerceptionTests
{
    private const uint TestGuid = 0x5000007E;

    private static byte[] BuildWire(byte sequence, uint property, long value)
    {
        var buf = new byte[PrivateUpdatePropertyInt64Message.PackedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.PrivateUpdatePropertyInt64);
        buf[4] = sequence;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5), property);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(9), value);
        return buf;
    }

    // ---- byte-level decode ----

    [Fact]
    public void Decode_ParsesAllFields()
    {
        // Lifetime XP that exceeds 2^31 to prove the i64 path.
        const long bigXp = 5_000_000_000L;
        var wire = BuildWire(sequence: 9, property: PrivateUpdatePropertyInt64Message.TotalExperienceId, value: bigXp);
        var msg = GameMessageDecoder.Decode(wire) as PrivateUpdatePropertyInt64Message;
        Assert.NotNull(msg);
        Assert.Equal((byte)9, msg!.Sequence);
        Assert.Equal(PrivateUpdatePropertyInt64Message.TotalExperienceId, msg.Property);
        Assert.Equal(bigXp, msg.Value);
        Assert.Equal("TotalExperience", msg.PropertyName);
    }

    [Fact]
    public void Decode_TruncatedPacket_ReturnsNull()
    {
        var wire = BuildWire(sequence: 1, property: 1, value: 100);
        Assert.Null(GameMessageDecoder.Decode(wire.AsSpan(0, PrivateUpdatePropertyInt64Message.PackedSize - 1).ToArray()));
    }

    // ---- WorldState routing ----

    [Fact]
    public void PrivatePropertyInt64_BeforeSelfGuid_Dropped()
    {
        var ws = new WorldState();
        var msg = new PrivateUpdatePropertyInt64Message(Sequence: 1, Property: 1, Value: 800);
        Assert.False(ws.Apply(msg));
        Assert.Null(ws.Self);
    }

    [Fact]
    public void PrivatePropertyInt64_AfterSetSelf_Applied()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        Assert.True(ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 1, Property: 1, Value: 1234)));
        Assert.Equal(1234L, ws.Self!.PropertyInt64s![1]);
    }

    [Fact]
    public void PrivatePropertyInt64_StaleSequence_SameProperty_Dropped()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        Assert.True(ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 100, Property: 1, Value: 5000)));
        // seq 50 after 100 on the SAME property is backward (not a wrap).
        Assert.False(ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 50, Property: 1, Value: 9)));
        Assert.Equal(5000L, ws.Self!.PropertyInt64s![1]); // unchanged
    }

    [Fact]
    public void PrivatePropertyInt64_SequenceIsPerProperty_NoCrossDrop()
    {
        // The rubber-duck blocker: TotalExperience(1) and
        // AvailableExperience(2) have INDEPENDENT server byte counters.
        // A low-seq AvailableExperience must NOT be dropped just because
        // TotalExperience already reached a higher seq.
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        Assert.True(ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 5, Property: 1, Value: 8000)));
        // AvailableExperience at seq 1 — would be "stale" under a single
        // shared counter (1 < 5), but is a fresh first reading for prop 2.
        Assert.True(ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 1, Property: 2, Value: 300)));
        Assert.Equal(8000L, ws.Self!.PropertyInt64s![1]);
        Assert.Equal(300L, ws.Self.PropertyInt64s[2]);
    }

    // ---- projection ----

    [Fact]
    public void Projection_SurfacesExperienceTotals()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25 /*Level*/, Value: 3));
        ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 1, Property: 1, Value: 9001));
        ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 1, Property: 2, Value: 42));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        Assert.Equal(3, proj!.Self.Level);
        Assert.Equal(9001L, proj.Self.TotalExperience);
        Assert.Equal(42L, proj.Self.AvailableExperience);
    }

    [Fact]
    public void Projection_NoInt64_ExperienceNull()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25, Value: 1));
        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);
        Assert.Null(proj!.Self.TotalExperience);
        Assert.Null(proj.Self.AvailableExperience);
    }

    // ---- prompt ----

    [Fact]
    public void BuildUserPrompt_RendersExperienceLine()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25, Value: 2));
        ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 1, Property: 1, Value: 1500));
        ws.Apply(new PrivateUpdatePropertyInt64Message(Sequence: 1, Property: 2, Value: 250));
        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);

        var prompt = LlmGoalPolicy.BuildUserPrompt(proj!, new EventStream(), currentGoal: null);
        Assert.Contains("- experience: 1500 total, 250 unspent", prompt);
    }

    [Fact]
    public void BuildUserPrompt_NoExperience_OmitsLine()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25, Value: 2));
        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);
        Assert.NotNull(proj);

        var prompt = LlmGoalPolicy.BuildUserPrompt(proj!, new EventStream(), currentGoal: null);
        Assert.DoesNotContain("- experience:", prompt);
    }
}
