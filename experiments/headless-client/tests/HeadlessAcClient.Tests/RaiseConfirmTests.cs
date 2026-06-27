// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for the robust XP-raise confirmation
// (HandshakeDriver.IsPendingRaiseConfirmed / TryGetSelfAttributeBaseById).
//
// Why this exists: the in-flight raise was confirmed ONLY by the bot's
// AvailableExperience dropping. During rapid kill-XP flux, concurrent income
// refills the pool between dispatch and the next reconcile, so a raise that
// actually landed reads as a false "timed out" — undercounting raises and
// (via the one-in-flight dedup) throttling the spend rate so the unspent-XP
// hoard grows. For an ATTRIBUTE raise the target attribute's BASE rising is an
// income-immune confirmation (an attribute base changes only when raised), so
// it is added as a second confirm signal.

using System.Collections.Generic;
using HeadlessAcClient.Protocol;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RaiseConfirmTests
{
    private static List<PdAttribute> Attrs(params (string Name, uint Base)[] a)
    {
        var list = new List<PdAttribute>();
        foreach (var x in a) list.Add(new PdAttribute(x.Name, x.Base, 0, 0));
        return list;
    }

    [Fact]
    public void TryGetSelfAttributeBaseById_FindsBaseByResolvedName()
    {
        var attrs = Attrs(("strength", 72), ("coordination", 48));
        Assert.True(HandshakeDriver.TryGetSelfAttributeBaseById(attrs, 1, out var str)); // 1 = strength
        Assert.Equal(72u, str);
        Assert.True(HandshakeDriver.TryGetSelfAttributeBaseById(attrs, 4, out var coord)); // 4 = coordination
        Assert.Equal(48u, coord);
    }

    [Fact]
    public void TryGetSelfAttributeBaseById_NullOrAbsent_ReturnsFalse()
    {
        Assert.False(HandshakeDriver.TryGetSelfAttributeBaseById(null, 1, out _));
        var attrs = Attrs(("strength", 72));
        Assert.False(HandshakeDriver.TryGetSelfAttributeBaseById(attrs, 2, out _)); // 2 = endurance, not present
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeWithBase_ConfirmsOnBaseRise_NotOnXpDropAlone()
    {
        // The serialization invariant: an attribute raise whose pre-base was observed
        // confirms ONLY when that attribute's base rises — NOT on an available-XP drop
        // alone. (Confirming on the XP drop would let the next same-attribute raise
        // capture a stale baseline before this raise's base echo arrives, so a prior
        // raise's delayed echo could false-confirm it.)
        var rose = Attrs(("strength", 73));
        var same = Attrs(("strength", 72));
        // Base rose -> confirmed (no availXp drop even passed).
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 1, preAvailableXp: 1000, preBase: 72, availXpNow: 1000, selfAttributes: rose));
        // availXp dropped but the base is unchanged -> NOT confirmed (waits for the
        // base echo so the next same-attribute raise's baseline is post-this-raise).
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 1, preAvailableXp: 1000, preBase: 72, availXpNow: 800, selfAttributes: same));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeBaseRose_ConfirmsEvenWhenIncomeMasksXp()
    {
        // Concurrent kill-XP income raised availXpNow ABOVE the pre-dispatch value,
        // masking the spend — but the target attribute's base rose, so the raise is
        // still confirmed (the whole point of the base signal).
        var attrs = Attrs(("strength", 73));
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 1, preAvailableXp: 1000, preBase: 72, availXpNow: 1200, selfAttributes: attrs));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeBaseUnchanged_NotConfirmed()
    {
        // Base unchanged -> not confirmed, regardless of the availXp direction (the
        // attribute-with-base path ignores availXp entirely).
        var attrs = Attrs(("strength", 72));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 1, preAvailableXp: 1000, preBase: 72, availXpNow: 1200, selfAttributes: attrs));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 1, preAvailableXp: 1000, preBase: 72, availXpNow: 800, selfAttributes: attrs));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_AttributeFirstRaise_NoPreBase_FallsBackToAvailXpDrop()
    {
        // The attribute's first-ever raise: not yet in SelfAttributes, so no pre-base
        // was recorded (preBase=null). With no prior same-attribute raise to stale a
        // baseline, the available-XP drop is a safe confirm signal.
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 1, preAvailableXp: 1000, preBase: null, availXpNow: 800, selfAttributes: null));
        // No drop -> not confirmed.
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Attribute", id: 1, preAvailableXp: 1000, preBase: null, availXpNow: 1000, selfAttributes: null));
    }

    [Fact]
    public void IsPendingRaiseConfirmed_NonAttributeKind_UsesOnlyAvailableXp()
    {
        // A Vital/Skill raise has no attribute preBase; the base-rise path must not
        // apply to it, so only the availXp drop can confirm it. A rising attribute
        // base (unrelated to the vital) must NOT confirm a Vital raise.
        var attrs = Attrs(("strength", 99));
        Assert.False(HandshakeDriver.IsPendingRaiseConfirmed(
            "Vital", id: 1, preAvailableXp: 1000, preBase: null, availXpNow: 1200, selfAttributes: attrs));
        Assert.True(HandshakeDriver.IsPendingRaiseConfirmed(
            "Vital", id: 1, preAvailableXp: 1000, preBase: null, availXpNow: 800, selfAttributes: attrs));
    }
}
