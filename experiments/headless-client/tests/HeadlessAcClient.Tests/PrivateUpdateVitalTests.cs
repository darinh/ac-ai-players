// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for PrivateUpdateVital (0x02E7) decoding and WorldState
// self-health integration:
//   - byte-level decode of a hand-built 25-byte packet
//   - the Health vital is identified by Vital == MaxHealth (1)
//   - non-health vitals (Stamina/Mana) do not drive self-health state
//   - peak-observed max tracking + projection HealthFraction
//   - vital arriving before SelfGuid is known is dropped
//   - stale vital byte-sequence is dropped (separate from the
//     property-update family sequence)

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class PrivateUpdateVitalTests
{
    private const uint TestGuid = 0x5000005C;

    private static byte[] BuildWire(
        byte sequence, uint vital, uint ranks, uint starting,
        uint expSpent, uint current)
    {
        var buf = new byte[PrivateUpdateVitalMessage.PackedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.PrivateUpdateVital);
        buf[4] = sequence;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5), vital);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(9), ranks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(13), starting);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(17), expSpent);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(21), current);
        return buf;
    }

    // ---- byte-level decode ----

    [Fact]
    public void Decode_ParsesAllFields()
    {
        // Health vital (Vital == MaxHealth == 1), current 73 HP.
        var wire = BuildWire(
            sequence: 7, vital: (uint)VitalKind.MaxHealth,
            ranks: 3, starting: 60, expSpent: 12345, current: 73);
        var msg = GameMessageDecoder.Decode(wire) as PrivateUpdateVitalMessage;
        Assert.NotNull(msg);
        Assert.Equal((byte)7, msg!.Sequence);
        Assert.Equal((uint)VitalKind.MaxHealth, msg.Vital);
        Assert.Equal(3u, msg.Ranks);
        Assert.Equal(60u, msg.StartingValue);
        Assert.Equal(12345u, msg.ExperienceSpent);
        Assert.Equal(73u, msg.Current);
        Assert.True(msg.IsHealth);
    }

    [Fact]
    public void Decode_StaminaVital_IsNotHealth()
    {
        var wire = BuildWire(
            sequence: 1, vital: (uint)VitalKind.MaxStamina,
            ranks: 0, starting: 0, expSpent: 0, current: 50);
        var msg = GameMessageDecoder.Decode(wire) as PrivateUpdateVitalMessage;
        Assert.NotNull(msg);
        Assert.False(msg!.IsHealth);
        Assert.True(msg.IsStamina);   // descriptor keys stamina by MaxStamina (3)
    }

    [Fact]
    public void Decode_ShortPayload_ReturnsNull()
    {
        var wire = BuildWire(1, (uint)VitalKind.MaxHealth, 0, 0, 0, 10);
        var truncated = wire.AsSpan(0, PrivateUpdateVitalMessage.PackedSize - 1).ToArray();
        Assert.Null(GameMessageDecoder.Decode(truncated));
    }

    // ---- PrivateUpdateAttribute2ndLevel (0x02E9) — per-tick current HP ----

    private static byte[] BuildLevelWire(byte sequence, uint vital, uint current)
    {
        var buf = new byte[PrivateUpdateAttribute2ndLevelMessage.PackedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.PrivateUpdateAttribute2ndLevel);
        buf[4] = sequence;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5), vital);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(9), current);
        return buf;
    }

    [Fact]
    public void DecodeLevel_ParsesFields_HealthKeyedByHealthTwo()
    {
        // The CURRENT-level health packet keys health by Health (2),
        // NOT MaxHealth (1) like the descriptor packet.
        var wire = BuildLevelWire(sequence: 4, vital: (uint)VitalKind.Health, current: 55);
        var msg = GameMessageDecoder.Decode(wire) as PrivateUpdateAttribute2ndLevelMessage;
        Assert.NotNull(msg);
        Assert.Equal((byte)4, msg!.Sequence);
        Assert.Equal((uint)VitalKind.Health, msg.Vital);
        Assert.Equal(55u, msg.Current);
        Assert.True(msg.IsHealth);
    }

    [Fact]
    public void DecodeLevel_RealWireBytes_StaminaDecodesCorrectly()
    {
        // Regression guard pinned to bytes captured LIVE off the ACE
        // server during a melee fight (self-health-perception live-verify,
        // selfhealth-live.log). The bot's swings consumed stamina, so the
        // server emitted 0x02E9 current-level packets keyed by Stamina(4):
        //   e9 02 00 00 | 00 | 04 00 00 00 | 08 00 00 00  (seq 0, stamina 8)
        //   e9 02 00 00 | 01 | 04 00 00 00 | 09 00 00 00  (seq 1, stamina 9)
        // This proves the on-wire layout matches our decoder field offsets.
        // A health (damage/regen/death) packet is byte-identical except the
        // vital field is Health(2); that path is covered by the Health tests.
        var liveStamina8 = new byte[]
        {
            0xe9, 0x02, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x08, 0x00, 0x00, 0x00,
        };
        var msg = GameMessageDecoder.Decode(liveStamina8) as PrivateUpdateAttribute2ndLevelMessage;
        Assert.NotNull(msg);
        Assert.Equal((byte)0, msg!.Sequence);
        Assert.Equal((uint)VitalKind.Stamina, msg.Vital);
        Assert.Equal(8u, msg.Current);
        Assert.False(msg.IsHealth);
    }

    [Fact]
    public void DecodeLevel_MaxHealthVital_IsNotHealthCurrent()
    {
        // A MaxHealth(1)-keyed current-level update is NOT the current-HP
        // signal in the 0x02E9 packet (that is Health==2).
        var wire = BuildLevelWire(sequence: 1, vital: (uint)VitalKind.MaxHealth, current: 100);
        var msg = GameMessageDecoder.Decode(wire) as PrivateUpdateAttribute2ndLevelMessage;
        Assert.NotNull(msg);
        Assert.False(msg!.IsHealth);
    }

    [Fact]
    public void DecodeLevel_ShortPayload_ReturnsNull()
    {
        var wire = BuildLevelWire(1, (uint)VitalKind.Health, 10);
        var truncated = wire.AsSpan(0, PrivateUpdateAttribute2ndLevelMessage.PackedSize - 1).ToArray();
        Assert.Null(GameMessageDecoder.Decode(truncated));
    }

    [Fact]
    public void HealthLevel_AppliedToSelf_TracksCurrentAndPeakMax()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        // Full-health update seeds peak max.
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Health, Current: 100)));
        Assert.Equal(100u, ws.Self!.HealthCurrent);
        Assert.Equal(100u, ws.Self!.HealthMax);

        // Combat damage lowers current, not peak.
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 2, Vital: (uint)VitalKind.Health, Current: 18)));
        Assert.Equal(18u, ws.Self!.HealthCurrent);
        Assert.Equal(100u, ws.Self!.HealthMax);
    }

    [Fact]
    public void HealthLevel_BeforeSelfGuid_Dropped()
    {
        var ws = new WorldState();
        Assert.False(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Health, Current: 50)));
        Assert.Null(ws.Self);
    }

    [Fact]
    public void HealthLevel_NonHealthVital_Ignored()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        Assert.False(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Mana, Current: 40)));
        Assert.Null(ws.Self?.HealthCurrent);
    }

    [Fact]
    public void HealthLevel_SeparateSequenceFromDescriptor()
    {
        // 0x02E9 Health(2) and 0x02E7 MaxHealth(1) use DISTINCT sequence
        // counters; a low 0x02E9 seq must not be gated by a high 0x02E7
        // seq applied just before it.
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 200, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 100)));
        // First 0x02E9 health-level update (seq 1) is accepted despite
        // the descriptor seq being 200.
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Health, Current: 60)));
        Assert.Equal(60u, ws.Self!.HealthCurrent);
    }

    // ---- WorldState self-health routing ----

    [Fact]
    public void HealthVital_BeforeSelfGuid_Dropped()
    {
        var ws = new WorldState();
        var msg = new PrivateUpdateVitalMessage(
            Sequence: 1, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 73);
        Assert.False(ws.Apply(msg));
        Assert.Null(ws.Self);
    }

    [Fact]
    public void HealthVital_AppliedToSelf_TracksCurrentAndPeakMax()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        // First (full-health) update seeds the peak max.
        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 1, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 100)));
        Assert.Equal(100u, ws.Self!.HealthCurrent);
        Assert.Equal(100u, ws.Self!.HealthMax);

        // Taking damage lowers Current but NOT the peak max.
        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 2, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 35)));
        Assert.Equal(35u, ws.Self!.HealthCurrent);
        Assert.Equal(100u, ws.Self!.HealthMax);

        // Levelling up at full health raises the peak max.
        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 3, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 120)));
        Assert.Equal(120u, ws.Self!.HealthCurrent);
        Assert.Equal(120u, ws.Self!.HealthMax);
    }

    [Fact]
    public void NonHealthVital_DoesNotSetSelfHealth()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        Assert.False(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 1, Vital: (uint)VitalKind.MaxMana,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 50)));
        // Self snapshot may exist (SetSelf), but health stays null.
        Assert.Null(ws.Self?.HealthCurrent);
        Assert.Null(ws.Self?.HealthMax);
    }

    [Fact]
    public void HealthVital_StaleByteSequence_Dropped()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 100, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 80)));
        Assert.Equal(80u, ws.Self!.HealthCurrent);

        // Seq 50 after 100 is backward (50-100 mod 256 = 206, beyond the
        // 128 wrap window) — stale, dropped, value unchanged.
        Assert.False(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 50, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 999)));
        Assert.Equal(80u, ws.Self!.HealthCurrent);
    }

    [Fact]
    public void HealthVital_SeparateSequenceFromPropertyFamily()
    {
        // The vital byte-seq is INDEPENDENT of the property-update
        // family seq: a low vital seq must NOT be gated by a high
        // property seq applied just before it.
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        Assert.True(ws.Apply(new PrivateUpdatePropertyIntMessage(
            Sequence: 200, Property: 25 /*Level*/, Value: 7)));
        // A health vital with seq 1 is the FIRST vital update — accepted
        // despite the property seq being 200.
        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 1, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 73)));
        Assert.Equal(73u, ws.Self!.HealthCurrent);
    }

    // ---- projection fraction ----

    [Fact]
    public void Projection_ComputesHealthFractionFromPeakMax()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PlayerCreateMessage(TestGuid));
        ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 1, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 100));
        ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 2, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 30));

        var proj = WorldStateProjection.FromWorldState(ws, null);
        Assert.NotNull(proj);
        Assert.NotNull(proj!.Self.HealthFraction);
        Assert.Equal(0.30f, proj.Self.HealthFraction!.Value, 3);
    }

    [Fact]
    public void Projection_HealthFractionNull_BeforeAnyVital()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PlayerCreateMessage(TestGuid));
        var proj = WorldStateProjection.FromWorldState(ws, null);
        Assert.NotNull(proj);
        Assert.Null(proj!.Self.HealthFraction);
    }

    // ---- raw self-health trend + absolute facts (self-health-raw-facts) ----

    [Fact]
    public void HealthRising_NullOnFirstReading_TrueWhenClimbing_FalseWhenDropping()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        // First reading: no prior, so trend is unknown (null).
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Health, Current: 1)));
        Assert.Null(ws.Self!.HealthRising);

        // Regen up-tick: rising.
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 2, Vital: (uint)VitalKind.Health, Current: 2)));
        Assert.True(ws.Self!.HealthRising);

        // Took damage: not rising.
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 3, Vital: (uint)VitalKind.Health, Current: 1)));
        Assert.False(ws.Self!.HealthRising);

        // Flat (no change): trend left unchanged (still false).
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 4, Vital: (uint)VitalKind.Health, Current: 1)));
        Assert.False(ws.Self!.HealthRising);
    }

    [Fact]
    public void HealthRising_NotClobberedByDuplicateFromAlternateSource()
    {
        // Both 0x02E9 (current-level) and 0x02E7 (descriptor) report the same
        // Health current through separate sequence gates. A genuine up-tick
        // followed by a redundant same-value report from the other source
        // must NOT reset the rising signal to false (regression guard for the
        // dual-source false-negative).
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Health, Current: 1));
        // Regen up-tick via the current-level packet.
        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 2, Vital: (uint)VitalKind.Health, Current: 2));
        Assert.True(ws.Self!.HealthRising);

        // Descriptor re-reports the SAME current (2) — must not clobber.
        ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 1, Vital: (uint)VitalKind.MaxHealth,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 2));
        Assert.True(ws.Self!.HealthRising);
    }

    [Fact]
    public void Projection_SurfacesAbsoluteCurrentPeakAndRising()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PlayerCreateMessage(TestGuid));

        // Damaged-login regression scenario: first reading is sub-max (1 HP),
        // so the peak under-estimates the true max. Then health regenerates.
        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Health, Current: 1));
        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 2, Vital: (uint)VitalKind.Health, Current: 2));

        var proj = WorldStateProjection.FromWorldState(ws, null);
        Assert.NotNull(proj);
        // Absolute current is wire-authoritative even though the fraction
        // (computed from the under-estimated peak) reads a misleading 100%.
        Assert.Equal(2, proj!.Self.HealthCurrent);
        Assert.Equal(2, proj.Self.HealthObservedPeak);
        Assert.True(proj.Self.HealthRising);
        Assert.Equal(1.0f, proj.Self.HealthFraction!.Value, 3);
    }

    [Fact]
    public void FormatSelfHealth_DamagedLogin_ShowsAbsoluteHpNotJustPercent()
    {
        // The evidenced bug: at current=1/peak=1 the old render showed only
        // "health: 100%". The fix surfaces absolute HP so the LLM is not misled.
        var line = HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfHealth(
            current: 1, observedPeak: 1, fraction: 1.0f, rising: null);
        Assert.NotNull(line);
        Assert.Contains("1/1 HP", line);
        Assert.Contains("100", line);
    }

    [Fact]
    public void FormatSelfHealth_Regenerating_AppendsRisingNote()
    {
        var line = HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfHealth(
            current: 2, observedPeak: 2, fraction: 1.0f, rising: true);
        Assert.NotNull(line);
        Assert.Contains("2/2 HP", line);
        Assert.Contains("rising", line);
    }

    [Fact]
    public void FormatSelfHealth_HealthyMidFight_ShowsFractionAndAbsolute_NoRising()
    {
        var line = HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfHealth(
            current: 2, observedPeak: 5, fraction: 0.4f, rising: false);
        Assert.NotNull(line);
        Assert.Contains("2/5 HP", line);
        Assert.Contains("40", line);
        Assert.DoesNotContain("rising", line);
    }

    [Fact]
    public void FormatSelfHealth_NoData_ReturnsNull()
    {
        Assert.Null(HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfHealth(
            current: null, observedPeak: null, fraction: null, rising: null));
    }

    // ---- stamina perception (mirrors the health pair) ----

    [Fact]
    public void Stamina_VitalUpdates_FlowToProjection_AndTrackPeakMax()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PlayerCreateMessage(TestGuid));

        // First (full) stamina reading seeds the peak max; then it depletes.
        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Stamina, Current: 100));
        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 2, Vital: (uint)VitalKind.Stamina, Current: 40));

        var proj = WorldStateProjection.FromWorldState(ws, null);
        Assert.NotNull(proj);
        Assert.Equal(40, proj!.Self.StaminaCurrent);     // current is wire-authoritative
        Assert.Equal(100, proj.Self.StaminaObservedPeak); // peak never shrinks from depletion
    }

    [Fact]
    public void Stamina_VitalUpdate_DoesNotDriveHealth()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PlayerCreateMessage(TestGuid));
        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Stamina, Current: 30));
        var proj = WorldStateProjection.FromWorldState(ws, null);
        // Stamina must NOT populate health state (separate vital + counter).
        Assert.Null(proj!.Self.HealthCurrent);
        Assert.Equal(30, proj.Self.StaminaCurrent);
    }

    [Fact]
    public void FormatSelfStaminaWhenLow_Depleted_RendersSignal()
    {
        var line = HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfStaminaWhenLow(
            current: 40, observedPeak: 100);   // 40% <= 50% low threshold
        Assert.NotNull(line);
        Assert.Contains("stamina:", line);
        Assert.Contains("40/100", line);
        Assert.Contains("is LOW", line);
    }

    [Fact]
    public void FormatSelfStaminaWhenLow_NearFull_ReturnsNull()
    {
        // 99/100 is NOT meaningfully low -> omitted (no noise / over-caution).
        Assert.Null(HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfStaminaWhenLow(
            current: 99, observedPeak: 100));
        // Exactly at the threshold (50%) still renders (boundary inclusive).
        Assert.NotNull(HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfStaminaWhenLow(
            current: 50, observedPeak: 100));
    }

    [Fact]
    public void FormatSelfStaminaWhenLow_Full_ReturnsNull()
    {
        // Full (or peak under-estimate where cur>=max) -> no signal, no noise.
        Assert.Null(HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfStaminaWhenLow(
            current: 100, observedPeak: 100));
    }

    [Fact]
    public void FormatSelfStaminaWhenLow_Unknown_ReturnsNull()
    {
        Assert.Null(HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfStaminaWhenLow(
            current: null, observedPeak: null));
        Assert.Null(HeadlessAcClient.Strategy.LlmGoalPolicy.FormatSelfStaminaWhenLow(
            current: 50, observedPeak: null));
    }

    [Fact]
    public void Stamina_DescriptorPacket_UpdatesStamina()
    {
        // The 0x02E7 descriptor keys stamina by MaxStamina(3) and carries the
        // CURRENT value (like the health descriptor keyed by MaxHealth(1)).
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PlayerCreateMessage(TestGuid));
        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 1, Vital: (uint)VitalKind.MaxStamina,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 80)));
        Assert.Equal(80u, ws.Self?.StaminaCurrent);
        Assert.Null(ws.Self?.HealthCurrent);   // stamina must not touch health
    }

    [Fact]
    public void Stamina_SequenceCountersIndependentOfHealthAndEachOther()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PlayerCreateMessage(TestGuid));
        // A high HEALTH seq must NOT gate a low STAMINA seq (distinct counters).
        ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 200, Vital: (uint)VitalKind.Health, Current: 5));
        Assert.True(ws.Apply(new PrivateUpdateAttribute2ndLevelMessage(
            Sequence: 1, Vital: (uint)VitalKind.Stamina, Current: 70)));
        Assert.Equal(70u, ws.Self?.StaminaCurrent);
        // The 0x02E7 (MaxStamina) seq is DISTINCT from the 0x02E9 (Stamina) seq:
        // a high 0x02E9 seq must not gate a low 0x02E7 seq.
        Assert.True(ws.Apply(new PrivateUpdateVitalMessage(
            Sequence: 2, Vital: (uint)VitalKind.MaxStamina,
            Ranks: 0, StartingValue: 0, ExperienceSpent: 0, Current: 65)));
        Assert.Equal(65u, ws.Self?.StaminaCurrent);
    }
}
