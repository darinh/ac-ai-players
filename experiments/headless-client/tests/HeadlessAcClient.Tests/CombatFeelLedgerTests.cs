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
}
