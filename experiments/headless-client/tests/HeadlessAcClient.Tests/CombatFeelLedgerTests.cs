// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for CombatFeelLedger — the pure, per-session, per-mob-identity
// record of the bot's OWN observed combat outcomes. The ledger stores
// RAW counts only (no danger label, no avoidance decision — those belong
// to the LLM). These tests pin the identity keying (wcid-preferred, name
// fallback, null when neither), the recording mutators, and the Snapshot
// projection (danger-first ordering, zero-signal omission, cap).

using System.Linq;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatFeelLedgerTests
{
    private static CombatFeelLedger.MobIdentity Wcid(uint w, string? name = null)
        => new(w, name);

    private static CombatFeelLedger.MobIdentity Named(string name)
        => new(null, name);

    // ---- KeyOf identity ----------------------------------------------

    [Fact]
    public void KeyOf_PrefersWcid_OverName()
    {
        // Same wcid, different display names -> same bucket.
        var a = CombatFeelLedger.KeyOf(new(19261u, "Creeper Mosswart"));
        var b = CombatFeelLedger.KeyOf(new(19261u, "creeper mosswart  "));
        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void KeyOf_FallsBackToNormalizedName_WhenNoWcid()
    {
        var a = CombatFeelLedger.KeyOf(Named("Drudge Skulker"));
        var b = CombatFeelLedger.KeyOf(Named("  drudge   skulker "));
        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void KeyOf_NullWhenNoWcidAndNoName()
    {
        Assert.Null(CombatFeelLedger.KeyOf(new(null, null)));
        Assert.Null(CombatFeelLedger.KeyOf(new(0u, "   ")));
        Assert.Null(CombatFeelLedger.KeyOf(new(0u, null)));
    }

    [Fact]
    public void KeyOf_WcidZeroIsNotUsable_FallsToName()
    {
        // wcid 0 is the wire "unknown" -> must not key as w:0.
        var k = CombatFeelLedger.KeyOf(new(0u, "Cow"));
        Assert.Equal("n:cow", k);
    }

    // ---- recording + snapshot ----------------------------------------

    [Fact]
    public void RecordKill_SurfacesInSnapshot()
    {
        var l = new CombatFeelLedger();
        Assert.True(l.IsEmpty);
        l.RecordKill(Wcid(24937u, "The Chicken"));
        Assert.False(l.IsEmpty);

        var snap = l.Snapshot();
        Assert.NotNull(snap);
        var e = Assert.Single(snap!);
        Assert.Equal("The Chicken", e.Name);
        Assert.Equal(24937u, e.Wcid);
        Assert.Equal(1, e.Kills);
        Assert.Equal(0, e.Deaths);
        Assert.Equal("kill", e.LastOutcome);
    }

    [Fact]
    public void RecordDeath_SurfacesInSnapshot()
    {
        var l = new CombatFeelLedger();
        l.RecordDeath(Named("Drudge Skulker"));
        var snap = l.Snapshot();
        var e = Assert.Single(snap!);
        Assert.Equal(1, e.Deaths);
        Assert.Equal("death", e.LastOutcome);
    }

    [Fact]
    public void RecordIneffective_SurfacesInSnapshot()
    {
        // A non-lethal abandon (out-defended, no kill, no death) is its own
        // significant outcome so the LLM learns the kind without dying.
        var l = new CombatFeelLedger();
        Assert.True(l.IsEmpty);
        l.RecordIneffective(Wcid(20u, "Auroch Bull"));
        Assert.False(l.IsEmpty);

        var snap = l.Snapshot();
        Assert.NotNull(snap);
        var e = Assert.Single(snap!);
        Assert.Equal("Auroch Bull", e.Name);
        Assert.Equal(1, e.Ineffective);
        Assert.Equal(0, e.Kills);
        Assert.Equal(0, e.Deaths);
        Assert.Equal("ineffective", e.LastOutcome);
    }

    [Fact]
    public void RecordIneffective_AccumulatesPerKind()
    {
        var l = new CombatFeelLedger();
        l.RecordIneffective(Wcid(20u, "Auroch Bull"));
        l.RecordIneffective(Wcid(20u, "Auroch Bull"));
        var e = Assert.Single(l.Snapshot()!);
        Assert.Equal(2, e.Ineffective);
    }

    [Fact]
    public void RecordFightStart_AloneIsNotSignificant()
    {
        // A fight that produced no kill/death/near-death is zero-signal:
        // it must NOT show in the snapshot and must keep IsEmpty true.
        var l = new CombatFeelLedger();
        l.RecordFightStart(Named("Wasp"));
        Assert.True(l.IsEmpty);
        Assert.Null(l.Snapshot());
    }

    [Fact]
    public void Snapshot_OmitsZeroSignalEntries_KeepsSignificant()
    {
        var l = new CombatFeelLedger();
        l.RecordFightStart(Named("Wasp"));      // zero-signal
        l.RecordKill(Named("Chicken"));         // significant
        var snap = l.Snapshot();
        var e = Assert.Single(snap!);
        Assert.Equal("Chicken", e.Name);
    }

    [Fact]
    public void Snapshot_OrdersByRecency()
    {
        var l = new CombatFeelLedger();
        // Kill recorded first (older), death recorded second (newer).
        l.RecordKill(Named("Chicken"));
        l.RecordDeath(Named("Drudge"));
        var snap = l.Snapshot();
        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Count);
        // Neutral recency ordering — the most-recently-updated kind first.
        // Source assigns NO danger priority; the LLM judges the rows.
        Assert.Equal("Drudge", snap[0].Name);
        Assert.Equal("Chicken", snap[1].Name);
    }

    [Fact]
    public void Snapshot_RespectsCap()
    {
        var l = new CombatFeelLedger();
        for (uint i = 1; i <= 10; i++)
            l.RecordKill(Wcid(i, "Mob" + i));
        var snap = l.Snapshot(max: 3);
        Assert.NotNull(snap);
        Assert.Equal(3, snap!.Count);
    }

    [Fact]
    public void Record_NullIdentity_IsIgnored()
    {
        var l = new CombatFeelLedger();
        l.RecordKill(new(null, null));   // unidentifiable -> must be dropped
        Assert.True(l.IsEmpty);
        Assert.Null(l.Snapshot());
    }

    [Fact]
    public void RepeatedKills_AccumulateInOneBucket()
    {
        var l = new CombatFeelLedger();
        l.RecordKill(Wcid(24937u, "The Chicken"));
        l.RecordKill(Wcid(24937u, "The Chicken"));
        l.RecordFightStart(Wcid(24937u, "The Chicken"));
        var e = Assert.Single(l.Snapshot()!);
        Assert.Equal(2, e.Kills);
        Assert.Equal(1, e.Fights);
    }

    // ---- cross-session persistence (ToJson / FromJson) ----------------

    [Fact]
    public void JsonRoundTrip_PreservesAllCountsAndRecency()
    {
        var l = new CombatFeelLedger();
        l.RecordFightStart(Wcid(211u, "Mudlurk Mosswart"));
        l.RecordNearDeath(Wcid(211u, "Mudlurk Mosswart"));
        l.RecordDeath(Wcid(211u, "Mudlurk Mosswart"));
        l.RecordKill(Wcid(24937u, "The Chicken"));          // older outcome
        l.RecordIneffective(Named("Drudge Skulker"));        // newest outcome

        var restored = CombatFeelLedger.FromJson(l.ToJson());

        // Same significant rows, same danger-first/recency ordering.
        var orig = l.Snapshot()!;
        var round = restored.Snapshot()!;
        Assert.Equal(orig.Count, round.Count);
        Assert.Equal(
            orig.Select(e => (e.Name, e.Wcid, e.Kills, e.Deaths, e.NearDeaths, e.Ineffective, e.Fights, e.LastOutcome)),
            round.Select(e => (e.Name, e.Wcid, e.Kills, e.Deaths, e.NearDeaths, e.Ineffective, e.Fights, e.LastOutcome)));
    }

    [Fact]
    public void Restored_NewOutcomeRanksAboveAllPersistedRows()
    {
        // The recency counter must resume past the highest persisted order so a
        // newly recorded outcome sorts as most-recent (top of the Snapshot).
        var l = new CombatFeelLedger();
        l.RecordKill(Wcid(1u, "A"));
        l.RecordKill(Wcid(2u, "B"));
        var restored = CombatFeelLedger.FromJson(l.ToJson());

        restored.RecordKill(Wcid(3u, "C"));
        Assert.Equal("C", restored.Snapshot()![0].Name);
    }

    [Fact]
    public void FromJson_LoadedLedgerIsNotDirty()
    {
        var l = new CombatFeelLedger();
        l.RecordKill(Wcid(1u, "A"));
        Assert.True(l.Dirty);

        var restored = CombatFeelLedger.FromJson(l.ToJson());
        Assert.False(restored.Dirty);
    }

    [Fact]
    public void Dirty_SetOnRecord_ClearedOnMarkClean()
    {
        var l = new CombatFeelLedger();
        Assert.False(l.Dirty);
        l.RecordDeath(Wcid(7u, "Banderling Scout"));
        Assert.True(l.Dirty);
        l.MarkClean();
        Assert.False(l.Dirty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {{{")]
    [InlineData("{\"Version\":999,\"Order\":0,\"Entries\":[]}")] // version mismatch
    public void FromJson_BadOrEmptyInput_YieldsEmptyLedger(string? json)
    {
        var l = CombatFeelLedger.FromJson(json);
        Assert.True(l.IsEmpty);
        Assert.Null(l.Snapshot());
    }

    [Fact]
    public void MergeFrom_TakesMaxPerCounter_AndAdoptsDiskOnlyKinds()
    {
        var mine = new CombatFeelLedger();
        mine.RecordDeath(Wcid(211u, "Mudlurk Mosswart"));  // deaths 1
        mine.RecordKill(Wcid(211u, "Mudlurk Mosswart"));   // kills 1

        var other = new CombatFeelLedger();
        other.RecordDeath(Wcid(211u, "Mudlurk Mosswart"));
        other.RecordDeath(Wcid(211u, "Mudlurk Mosswart")); // deaths 2 (higher)
        other.RecordKill(Wcid(99u, "Cow"));                // a kind only "other" has

        mine.MergeFrom(other);

        var rows = mine.Snapshot()!;
        var mud = rows.Single(r => r.Name == "Mudlurk Mosswart");
        Assert.Equal(2, mud.Deaths); // took the higher count
        Assert.Equal(1, mud.Kills);  // kept our higher count (other had 0)
        Assert.Contains(rows, r => r.Name == "Cow" && r.Kills == 1); // adopted
    }

    [Fact]
    public void MergeFrom_NeverDecreasesCounts_AndConverges()
    {
        var a = new CombatFeelLedger();
        a.RecordKill(Wcid(1u, "A"));
        a.RecordKill(Wcid(1u, "A")); // kills 2
        var b = new CombatFeelLedger();
        b.RecordKill(Wcid(1u, "A")); // kills 1

        b.MergeFrom(a);                 // b should rise to 2
        Assert.Equal(2, b.Snapshot()!.Single().Kills);
        b.MergeFrom(a);                 // idempotent
        Assert.Equal(2, b.Snapshot()!.Single().Kills);
    }

    // ---- MaxLossBotLevel (adaptive beaten-kind re-test signal) --------

    [Fact]
    public void RecordLoss_WithBotLevel_StampsMaxLossBotLevel()
    {
        var l = new CombatFeelLedger();
        l.RecordDeath(Wcid(211u, "Mudlurk Mosswart"), botLevel: 5);
        Assert.Equal(5, Assert.Single(l.Snapshot()!).MaxLossBotLevel);

        var n = new CombatFeelLedger();
        n.RecordNearDeath(Wcid(212u, "Auroch"), botLevel: 4);
        Assert.Equal(4, Assert.Single(n.Snapshot()!).MaxLossBotLevel);

        var i = new CombatFeelLedger();
        i.RecordIneffective(Wcid(213u, "Wasp"), botLevel: 6);
        Assert.Equal(6, Assert.Single(i.Snapshot()!).MaxLossBotLevel);
    }

    [Fact]
    public void LossLevel_TakesMaxAcrossRepeatedLosses_NotLatest()
    {
        // A later loss at a LOWER level must not lower the recorded max — the
        // re-test trigger compares the bot's current level to the HIGHEST level
        // it ever lost at (monotonic, conservative).
        var l = new CombatFeelLedger();
        l.RecordNearDeath(Wcid(7u, "Drudge"), botLevel: 3);
        l.RecordNearDeath(Wcid(7u, "Drudge"), botLevel: 7);
        l.RecordIneffective(Wcid(7u, "Drudge"), botLevel: 5);
        Assert.Equal(7, Assert.Single(l.Snapshot()!).MaxLossBotLevel);
    }

    [Fact]
    public void RecordKill_DoesNotStampLossLevel_AndLossWithoutLevelIsNull()
    {
        // A kill carries no loss level; a loss recorded with an unknown level
        // (default null) leaves the field null -> re-test disabled, kind stays
        // beaten exactly as before this feature.
        var l = new CombatFeelLedger();
        l.RecordKill(Wcid(24937u, "The Chicken"));
        Assert.Null(Assert.Single(l.Snapshot()!).MaxLossBotLevel);

        var d = new CombatFeelLedger();
        d.RecordDeath(Named("Drudge Skulker")); // no botLevel
        Assert.Null(Assert.Single(d.Snapshot()!).MaxLossBotLevel);
    }

    [Fact]
    public void JsonRoundTrip_PreservesMaxLossBotLevel()
    {
        var l = new CombatFeelLedger();
        l.RecordNearDeath(Wcid(211u, "Mudlurk Mosswart"), botLevel: 8);
        var restored = CombatFeelLedger.FromJson(l.ToJson());
        Assert.Equal(8, Assert.Single(restored.Snapshot()!).MaxLossBotLevel);
    }

    [Fact]
    public void FromJson_OldLedgerWithoutLossLevelField_DeserializesToNull()
    {
        // A ledger persisted before MaxLossBotLevel existed has no such key.
        // The optional DTO field must deserialize to null (NOT crash, NOT wipe
        // the entry) so old learning survives the upgrade and behaves as today.
        const string oldJson =
            "{\"Version\":1,\"Order\":1,\"Entries\":[{\"Key\":\"w:211\"," +
            "\"DisplayName\":\"Mudlurk Mosswart\",\"Wcid\":211,\"Kills\":0," +
            "\"Deaths\":1,\"NearDeaths\":0,\"Ineffective\":0,\"Fights\":1," +
            "\"LastOutcome\":\"death\",\"LastOutcomeOrder\":1}]}";
        var restored = CombatFeelLedger.FromJson(oldJson);
        var e = Assert.Single(restored.Snapshot()!);
        Assert.Equal(1, e.Deaths);           // entry preserved
        Assert.Null(e.MaxLossBotLevel);      // missing field -> null
    }

    [Fact]
    public void MergeFrom_MaxMergesLossLevel()
    {
        var mine = new CombatFeelLedger();
        mine.RecordNearDeath(Wcid(7u, "Drudge"), botLevel: 3);
        var other = new CombatFeelLedger();
        other.RecordNearDeath(Wcid(7u, "Drudge"), botLevel: 7); // higher
        mine.MergeFrom(other);
        Assert.Equal(7, Assert.Single(mine.Snapshot()!).MaxLossBotLevel);

        // Reverse direction must not lower it.
        var lower = new CombatFeelLedger();
        lower.RecordNearDeath(Wcid(7u, "Drudge"), botLevel: 2);
        mine.MergeFrom(lower);
        Assert.Equal(7, Assert.Single(mine.Snapshot()!).MaxLossBotLevel);
    }

    // ---- SwungZeroDamage (out-defended signal) ------------------------

    [Fact]
    public void RecordIneffective_SwungZeroDamage_BumpsBothCounters()
    {
        // A swung-but-0-damage abandon bumps BOTH Ineffective and the tighter
        // SwungZeroDamage; a plain ineffective (e.g. a no-swing can't-close
        // abandon) bumps ONLY Ineffective.
        var l = new CombatFeelLedger();
        l.RecordIneffective(Wcid(20u, "Auroch Bull"), swungZeroDamage: true);
        l.RecordIneffective(Wcid(20u, "Auroch Bull"), swungZeroDamage: true);
        l.RecordIneffective(Wcid(20u, "Auroch Bull")); // can't-close: not swung-zero-damage
        var e = Assert.Single(l.Snapshot()!);
        Assert.Equal(3, e.Ineffective);
        Assert.Equal(2, e.SwungZeroDamage);
    }

    [Fact]
    public void JsonRoundTrip_PreservesSwungZeroDamage()
    {
        var l = new CombatFeelLedger();
        l.RecordIneffective(Wcid(20u, "Auroch Bull"), botLevel: 11, swungZeroDamage: true);
        var restored = CombatFeelLedger.FromJson(l.ToJson());
        Assert.Equal(1, Assert.Single(restored.Snapshot()!).SwungZeroDamage);
    }

    [Fact]
    public void FromJson_OldLedgerWithoutSwungZeroDamageField_DeserializesToZero()
    {
        // A ledger persisted before SwungZeroDamage existed has no such key. The
        // optional DTO field must deserialize to 0 (NOT crash) so old learning
        // survives the upgrade and the new veto simply does not fire on it.
        const string oldJson =
            "{\"Version\":1,\"Order\":1,\"Entries\":[{\"Key\":\"w:211\"," +
            "\"DisplayName\":\"Mudlurk Mosswart\",\"Wcid\":211,\"Kills\":0," +
            "\"Deaths\":0,\"NearDeaths\":0,\"Ineffective\":8,\"Fights\":8," +
            "\"LastOutcome\":\"ineffective\",\"LastOutcomeOrder\":8,\"MaxLossBotLevel\":15}]}";
        var restored = CombatFeelLedger.FromJson(oldJson);
        var e = Assert.Single(restored.Snapshot()!);
        Assert.Equal(8, e.Ineffective);     // entry preserved
        Assert.Equal(0, e.SwungZeroDamage); // missing field -> 0
    }

    [Fact]
    public void MergeFrom_MaxMergesSwungZeroDamage()
    {
        var mine = new CombatFeelLedger();
        mine.RecordIneffective(Wcid(7u, "Drudge"), swungZeroDamage: true); // 1
        var other = new CombatFeelLedger();
        other.RecordIneffective(Wcid(7u, "Drudge"), swungZeroDamage: true);
        other.RecordIneffective(Wcid(7u, "Drudge"), swungZeroDamage: true); // 2 (higher)
        mine.MergeFrom(other);
        Assert.Equal(2, Assert.Single(mine.Snapshot()!).SwungZeroDamage);

        // Reverse direction must not lower it.
        var lower = new CombatFeelLedger();
        lower.RecordIneffective(Wcid(7u, "Drudge"), swungZeroDamage: true); // 1
        mine.MergeFrom(lower);
        Assert.Equal(2, Assert.Single(mine.Snapshot()!).SwungZeroDamage);
    }
}
