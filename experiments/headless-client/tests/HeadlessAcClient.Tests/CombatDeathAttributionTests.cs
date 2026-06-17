// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for CombatDeathAttribution — the pure helpers that decide whether a
// self-death should be recorded against the monster KIND the bot was last
// fighting. These pin the identity-match policy (wcid-definitive, name
// fallback, no spurious match) and the freshness window, so the
// HandshakeDriver wiring that refreshes the death-attribution anchor on
// server-driven combat signals stays honest and game-knowledge-free.

using System;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatDeathAttributionTests
{
    // ---- SignalMatchesFoe: wcid-definitive -----------------------------

    [Fact]
    public void SignalMatchesFoe_SameWcid_Matches()
    {
        Assert.True(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 19258, foeName: "Drudge Slinker",
            observedWcid: 19258, observedName: "Drudge Slinker"));
    }

    [Fact]
    public void SignalMatchesFoe_DifferentWcid_DoesNotMatch_EvenIfNameEqual()
    {
        // wcid is the definitive kind key: equal name but different wcid is a
        // different kind and must NOT match (no name fallback in that case).
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 19258, foeName: "Drudge",
            observedWcid: 19257, observedName: "Drudge"));
    }

    // ---- SignalMatchesFoe: name fallback -------------------------------

    [Fact]
    public void SignalMatchesFoe_NoWcids_FallsBackToName()
    {
        Assert.True(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: null, foeName: "Drudge Slinker",
            observedWcid: null, observedName: "Drudge Slinker"));
    }

    [Fact]
    public void SignalMatchesFoe_OnlyOneSideHasWcid_FallsBackToName()
    {
        // The inbound swing notification carries only a defender NAME (no
        // wcid). Falling back to name lets it still match the locked foe.
        Assert.True(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 19258, foeName: "Drudge Slinker",
            observedWcid: null, observedName: "Drudge Slinker"));
    }

    [Fact]
    public void SignalMatchesFoe_NameMatch_IsCaseAndWhitespaceInsensitive()
    {
        Assert.True(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: null, foeName: "Drudge   Slinker",
            observedWcid: null, observedName: "drudge slinker"));
    }

    [Fact]
    public void SignalMatchesFoe_DifferentName_DoesNotMatch()
    {
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: null, foeName: "Drudge Slinker",
            observedWcid: null, observedName: "Drudge Skulker"));
    }

    [Fact]
    public void SignalMatchesFoe_LockedFoeHasWcid_DelayedNotificationForDifferentName_DoesNotMatch()
    {
        // Regression: the inbound refresh passes observedWcid:null and the
        // notification's DefenderName, so a swing notification delayed across
        // an A->B target switch (locked foe B has a wcid; the notification
        // still reports A's name) must NOT refresh B. Name-only fallback when
        // observedWcid is null is what makes that gate hold.
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 19257, foeName: "Drudge Skulker",
            observedWcid: null, observedName: "Drudge Slinker"));
        // Same foe (name matches) still refreshes even though only the foe
        // side carries a wcid.
        Assert.True(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 19257, foeName: "Drudge Skulker",
            observedWcid: null, observedName: "drudge skulker"));
    }

    [Fact]
    public void SignalMatchesFoe_WcidZeroIsNotUsable_FallsBackToName()
    {
        // wcid 0 is the "unknown" sentinel (KeyOf treats it as unusable); a
        // zero on either side must not be compared as a wcid.
        Assert.True(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 0u, foeName: "Drudge Slinker",
            observedWcid: 0u, observedName: "Drudge Slinker"));
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 0u, foeName: "Drudge Slinker",
            observedWcid: 0u, observedName: "Cow"));
    }

    [Fact]
    public void SignalMatchesFoe_NoComparableIdentity_DoesNotMatch()
    {
        // No wcids and an unusable name on at least one side -> cannot
        // confirm same kind -> must not refresh (avoids poisoning).
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: null, foeName: "Drudge Slinker",
            observedWcid: null, observedName: null));
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: null, foeName: null,
            observedWcid: null, observedName: "Drudge Slinker"));
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: null, foeName: "(unknown)",
            observedWcid: null, observedName: "(unknown)"));
    }

    [Fact]
    public void SignalMatchesFoe_DefenderHitDuringFlee_RefreshesTrackedFoe()
    {
        // cp-2312: while the bot flees it stops swinging, so only inbound
        // mob-hits-bot DefenderNotifications keep firing — carrying the
        // attacker NAME (no wcid). The tracked foe has a wcid from the
        // engage. Name-only fallback must still confirm the same foe so the
        // death-attribution window stays fresh up to a flee-then-die death.
        Assert.True(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 19261, foeName: "Creeper Mosswart",
            observedWcid: null, observedName: "Creeper Mosswart"));
        // A DIFFERENT mob landing a hit while we track the original foe must
        // NOT refresh it (precision over recall — never poison the ledger
        // with a death the tracked foe did not cause).
        Assert.False(CombatDeathAttribution.SignalMatchesFoe(
            foeWcid: 19261, foeName: "Creeper Mosswart",
            observedWcid: null, observedName: "Drudge Slinker"));
    }

    // ---- IsFresh -------------------------------------------------------

    [Fact]
    public void IsFresh_WithinWindow_True()
    {
        var now = DateTime.UtcNow;
        Assert.True(CombatDeathAttribution.IsFresh(
            foeAt: now - TimeSpan.FromSeconds(5), now: now,
            freshness: CombatDeathAttribution.DefaultFreshness));
    }

    [Fact]
    public void IsFresh_AtOrBeyondWindow_False()
    {
        var now = DateTime.UtcNow;
        Assert.False(CombatDeathAttribution.IsFresh(
            foeAt: now - TimeSpan.FromSeconds(13), now: now,
            freshness: CombatDeathAttribution.DefaultFreshness));
        // Exactly at the boundary is NOT fresh (strict less-than).
        Assert.False(CombatDeathAttribution.IsFresh(
            foeAt: now - CombatDeathAttribution.DefaultFreshness, now: now,
            freshness: CombatDeathAttribution.DefaultFreshness));
    }

    [Fact]
    public void DefaultFreshness_IsTwelveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), CombatDeathAttribution.DefaultFreshness);
    }

    // ---- ChooseDeathFoe: engaged-foe primary, recent-damager fallback ----

    [Fact]
    public void ChooseDeathFoe_FreshEngagedFoe_PrefersIt()
    {
        var now = DateTime.UtcNow;
        var foe = CombatDeathAttribution.ChooseDeathFoe(
            lastCombatFoe: (1234u, "Foe Alpha", now - TimeSpan.FromSeconds(3)),
            lastDamager: (null, "Foe Beta", now - TimeSpan.FromSeconds(1)),
            now, CombatDeathAttribution.DefaultFreshness);
        Assert.NotNull(foe);
        Assert.Equal(1234u, foe!.Value.Wcid);
        Assert.Equal("Foe Alpha", foe.Value.Name);
    }

    [Fact]
    public void ChooseDeathFoe_StaleEngagedFoe_FallsBackToFreshDamager()
    {
        // THE FIX: the bot was last swinging at a (now-stale) foe but died to a
        // DIFFERENT foe that landed damage moments ago (a swarm add / a mob that
        // aggroed mid-travel). Attribute to the damager so the lethal kind is
        // learned instead of dropping the death entirely.
        var now = DateTime.UtcNow;
        var foe = CombatDeathAttribution.ChooseDeathFoe(
            lastCombatFoe: (1234u, "Foe Alpha", now - TimeSpan.FromSeconds(30)),
            lastDamager: (null, "Foe Beta", now - TimeSpan.FromSeconds(2)),
            now, CombatDeathAttribution.DefaultFreshness);
        Assert.NotNull(foe);
        Assert.Null(foe!.Value.Wcid);
        Assert.Equal("Foe Beta", foe.Value.Name);
    }

    [Fact]
    public void ChooseDeathFoe_NoEngagedFoe_UsesFreshDamager()
    {
        var now = DateTime.UtcNow;
        var foe = CombatDeathAttribution.ChooseDeathFoe(
            lastCombatFoe: null,
            lastDamager: (null, "Foe Beta", now - TimeSpan.FromSeconds(2)),
            now, CombatDeathAttribution.DefaultFreshness);
        Assert.NotNull(foe);
        Assert.Equal("Foe Beta", foe!.Value.Name);
    }

    [Fact]
    public void ChooseDeathFoe_BothStale_ReturnsNull()
    {
        // Neither anchor fresh -> leave the ledger untouched rather than guess.
        var now = DateTime.UtcNow;
        Assert.Null(CombatDeathAttribution.ChooseDeathFoe(
            lastCombatFoe: (1234u, "Foe Alpha", now - TimeSpan.FromSeconds(30)),
            lastDamager: (null, "Foe Beta", now - TimeSpan.FromSeconds(30)),
            now, CombatDeathAttribution.DefaultFreshness));
    }

    [Fact]
    public void ChooseDeathFoe_BothNull_ReturnsNull()
    {
        Assert.Null(CombatDeathAttribution.ChooseDeathFoe(
            lastCombatFoe: null, lastDamager: null,
            DateTime.UtcNow, CombatDeathAttribution.DefaultFreshness));
    }

    [Fact]
    public void ChooseDeathFoe_EngagedFoeUnresolvable_FallsBackToDamager()
    {
        // A fresh engaged foe whose identity does NOT resolve (no wcid, unusable
        // "(unknown)" name) must not be used; fall through to a resolvable fresh
        // damager rather than recording against a non-key.
        var now = DateTime.UtcNow;
        var foe = CombatDeathAttribution.ChooseDeathFoe(
            lastCombatFoe: (null, "(unknown)", now - TimeSpan.FromSeconds(1)),
            lastDamager: (null, "Foe Beta", now - TimeSpan.FromSeconds(1)),
            now, CombatDeathAttribution.DefaultFreshness);
        Assert.NotNull(foe);
        Assert.Equal("Foe Beta", foe!.Value.Name);
    }

    [Fact]
    public void ChooseDeathFoe_FreshDamagerUnresolvable_ReturnsNull()
    {
        // A fresh damager with no usable identity cannot key the ledger -> null.
        var now = DateTime.UtcNow;
        Assert.Null(CombatDeathAttribution.ChooseDeathFoe(
            lastCombatFoe: null,
            lastDamager: (null, null, now - TimeSpan.FromSeconds(1)),
            now, CombatDeathAttribution.DefaultFreshness));
    }
}
