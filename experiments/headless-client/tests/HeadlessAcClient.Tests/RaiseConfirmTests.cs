// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for the robust XP-raise confirmation
// (HandshakeDriver.IsPendingRaiseConfirmed / TryGetSelfAttributeExperienceSpentById).
//
// Why this exists: the in-flight raise was confirmed by the bot's AvailableExperience
// dropping, which concurrent kill-XP income masks (false timeouts). For an ATTRIBUTE
// raise the target attribute's EXPERIENCE-SPENT rising is income-immune (it changes
// only when XP is spent on that attribute) AND it confirms a PARTIAL-rank spend (the
// server adds the amount to ExperienceSpent even when the rank/base does not move) --
// which a base-only signal would miss, re-introducing a false-timeout throttle. It
// also serializes same-attribute re-raises (no stale-baseline false-confirm).

using System.Collections.Generic;
using HeadlessAcClient.Protocol;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RaiseConfirmTests
{
    // PdAttribute is (Name, Base, Ranks, ExperienceSpent); the confirm keys on the
    // 4th field (ExperienceSpent).
    private static List<PdAttribute> Attrs(params (string Name, uint ExpSpent)[] a)
    {
        var list = new List<PdAttribute>();
        foreach (var x in a) list.Add(new PdAttribute(x.Name, 0, 0, x.ExpSpent));
        return list;
    }

    [Fact]
    public void TryGetSelfAttributeExperienceSpentById_FindsByResolvedName()
    {
        var attrs = Attrs(("strength", 7200), ("coordination", 4800));
        Assert.True(HandshakeDriver.TryGetSelfAttributeExperienceSpentById(attrs, 1, out var str)); // 1 = strength
        Assert.Equal(7200u, str);
        Assert.True(HandshakeDriver.TryGetSelfAttributeExperienceSpentById(attrs, 4, out var coord)); // 4 = coordination
        Assert.Equal(4800u, coord);
    }

    [Fact]
    public void TryGetSelfAttributeExperienceSpentById_NullOrAbsent_ReturnsFalse()
    {
        Assert.False(HandshakeDriver.TryGetSelfAttributeExperienceSpentById(null, 1, out _));
        var attrs = Attrs(("strength", 7200));
        Assert.False(HandshakeDriver.TryGetSelfAttributeExperienceSpentById(attrs, 2, out _)); // 2 = endurance, absent
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeWithValue_ConfirmsOnExpSpentRise_NotOnXpDropAlone()
    {
        // Confirm ONLY when the attribute's ExperienceSpent rises -- NOT on an
        // available-XP drop alone (which would let the next same-attribute raise
        // capture a stale baseline before this raise's echo arrives).
        var rose = Attrs(("coordination", 4800));
        var same = Attrs(("coordination", 4000));
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 1000, selfAttributes: rose));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 800, selfAttributes: same));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_PartialRankSpend_ConfirmsViaExpSpent()
    {
        // The regression this fixes: a spend that does NOT complete a whole rank still
        // raises ExperienceSpent (the server adds the amount), so it confirms -- even
        // though the attribute's base/Ranks did not move. A base-only signal would miss
        // this and re-introduce a false-timeout throttle.
        var partial = Attrs(("coordination", 4800)); // +800 ExperienceSpent (partial rank)
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 1200, selfAttributes: partial));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_ExpSpentRose_ConfirmsEvenWhenIncomeMasksXp()
    {
        // Concurrent kill-XP income raised availXpNow ABOVE the pre value (masking the
        // spend), but ExperienceSpent rose -> confirmed.
        var attrs = Attrs(("coordination", 4800));
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 1200, selfAttributes: attrs));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_ExpSpentUnchanged_NotConfirmed()
    {
        var attrs = Attrs(("coordination", 4000));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 1200, selfAttributes: attrs));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 800, selfAttributes: attrs));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeFirstRaise_NoPreValue_FallsBackToAvailXpDrop()
    {
        // The attribute's first-ever raise: not yet in SelfAttributes, so no pre-value
        // recorded (preExpSpent=null). With no prior same-attribute raise to stale a
        // baseline, the available-XP drop is a safe confirm signal.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: null, availXpNow: 800, selfAttributes: null));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: null, availXpNow: 1000, selfAttributes: null));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_NonAttributeKind_UsesOnlyAvailableXp()
    {
        // A Vital/Skill raise has no attribute ExperienceSpent signal; only the availXp
        // drop confirms it. A rising (unrelated) attribute ExperienceSpent must NOT.
        var attrs = Attrs(("strength", 9900));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Vital", id: 1, preAvailableXp: 1000, preExpSpent: null, availXpNow: 1200, selfAttributes: attrs));
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Vital", id: 1, preAvailableXp: 1000, preExpSpent: null, availXpNow: 800, selfAttributes: attrs));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_IncomeMaskedSpend_ConfirmsViaTotalDelta()
    {
        // THE regression this fixes: a Skill raise spends 1000 XP while a concurrent kill
        // adds 1500 income. AvailableExperience nets 2000 -> 2500 (UP), so the availXp-drop
        // signal alone false-times-out the landed raise. The income-immune net spend
        // (deltaTotal - deltaAvail) = (1500) - (2500-2000) = 1000 == amount -> confirmed.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 2500,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 101500, amount: 1000));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_PureKillIncome_NotConfirmed()
    {
        // A kill alone (no raise) moves BOTH counters equally: avail 2000 -> 2800, total
        // T -> T+800. Net spend (800) - (2800-2000) = 0 < amount -> NOT confirmed.
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 2800,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100800, amount: 1000));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_PureSpendNoIncome_Confirms()
    {
        // A raise with no concurrent income: avail 2000 -> 1000, total unchanged. Net
        // spend (0) - (1000-2000) = 1000 == amount -> confirmed.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 1000,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100000, amount: 1000));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Vital_IncomeMaskedSpend_ConfirmsViaTotalDelta()
    {
        // Vitals share the same income-immune path as skills. spent (400)-(200)=200 == amount.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Vital", id: 1, preAvailableXp: 500, preExpSpent: null, availXpNow: 700,
            selfAttributes: null, preTotalXp: 5000, totalXpNow: 5400, amount: 200));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_TornSnapshot_TotalFreshAvailStale_NotConfirmed()
    {
        // The torn-read the amount threshold defends against: a kill's TotalExperience
        // update was applied (T -> T+800) but the paired AvailableExperience update has NOT
        // yet landed (avail stays at the pre value), and NO raise actually spent. Net
        // (800) - (0) = 800 != amount(1000) -> NOT confirmed. A `> 0` check would have
        // FALSELY confirmed a failed raise here.
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 2000,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100800, amount: 1000));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_TornSnapshot_SmallClampedAmount_NotConfirmed()
    {
        // The small/clamped-amount torn read: unspent XP was low so the raise amount clamped
        // to 50 and the raise did NOT land; a kill then adds +800 whose TotalExperience
        // update is applied while AvailableExperience is still stale. Net (800) - (0) = 800.
        // With the exact-match confirm 800 != 50 -> NOT confirmed. (A `>= amount` check would
        // have FALSELY confirmed here because the un-cancelled income 800 >= 50.)
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 50, preExpSpent: null, availXpNow: 50,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100800, amount: 50));
        // ...and the genuine small landed spend of 50 (no income) still confirms: net
        // (0) - (50-... ) here avail 50 -> 0 with total flat -> net (0) - (-50) = 50 == amount.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 50, preExpSpent: null, availXpNow: 0,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100000, amount: 50));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_TornSnapshot_AvailFreshTotalStale_NotConfirmed()
    {
        // The opposite torn shape: a kill's AvailableExperience update applied (+800) but the
        // paired TotalExperience update has NOT yet landed (total stale), and NO raise spent.
        // Net = deltaTotal(0) - deltaAvail(+800) = -800 != amount(1000) -> NOT confirmed. This
        // torn direction likewise cannot false-confirm; it can only (transiently) delay a real
        // confirmation, which self-corrects on the next reconcile once Total catches up.
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 2800,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100000, amount: 1000));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_NoAmount_FallsBackToAvailXpDrop()
    {
        // With amount unknown (0) the income-immune net-spend threshold cannot be applied,
        // so the confirm falls back to the raw available-XP drop even when totals are given.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 1000,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100000, amount: 0));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_Skill_NoTotalObserved_FallsBackToAvailXpDrop()
    {
        // With TotalExperience unobserved (preTotalXp/totalXpNow null) the confirm falls
        // back to the raw available-XP drop — byte-identical to the pre-change behavior.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 1000, selfAttributes: null, amount: 1000));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Skill", id: 7, preAvailableXp: 2000, preExpSpent: null, availXpNow: 2000, selfAttributes: null, amount: 1000));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeFirstRaise_NoExpSpent_UsesIncomeImmuneTotalDelta()
    {
        // An attribute's first-ever raise (not yet in SelfAttributes, preExpSpent null) now
        // rides the income-immune net-spend path too: masked spend confirms, pure kill does not.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 2000, preExpSpent: null, availXpNow: 2500,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 101500, amount: 1000)); // spent 1000
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 2000, preExpSpent: null, availXpNow: 2800,
            selfAttributes: null, preTotalXp: 100000, totalXpNow: 100800, amount: 1000)); // pure kill
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeExpSpentSignal_TakesPrecedenceOverTotalDelta()
    {
        // For an attribute WITH an observed pre-ExperienceSpent, the ExperienceSpent-rise
        // signal still governs (income-immune AND partial-rank-aware); the total/avail
        // deltas are not consulted for that path even when supplied.
        var rose = Attrs(("coordination", 4800));
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 5000,
            selfAttributes: rose, preTotalXp: 100000, totalXpNow: 200000, amount: 1000));
        var same = Attrs(("coordination", 4000));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 4, preAvailableXp: 1000, preExpSpent: 4000, availXpNow: 1000,
            selfAttributes: same, preTotalXp: 100000, totalXpNow: 100000, amount: 1000));
    }
}