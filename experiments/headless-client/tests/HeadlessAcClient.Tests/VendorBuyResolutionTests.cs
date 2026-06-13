// SPDX-License-Identifier: AGPL-3.0-or-later
// Vendor-buy motor helpers (review-hardened). These lock the three WorldState
// predicates the Buy motor relies on, which the council flagged:
//   - ResolveVendorItemExact: EXACT-only item resolution (a Buy spends currency
//     irreversibly, so a substring/fuzzy match must NOT silently buy a
//     different item — HK + correctness).
//   - TryGetLiveOpenVendor: the single live-panel predicate shared by the
//     prompt projection and the Buy motor (open + same landblock + in range),
//     so the bot can't buy from a stale/out-of-range panel.
//   - CountOwnedInventoryByWcid: the currency-agnostic "purchase landed" signal
//     used to reconcile an in-flight buy (works for coin AND alternate
//     currency, unlike a coin-balance delta).

using System;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VendorBuyResolutionTests
{
    private const uint SelfGuid = 0x5000000A;
    private const uint VendorGuid = 0x7A9B404E;

    private static VendorItemInfo Item(uint guid, uint wcid, string name) =>
        new VendorItemInfo(guid, wcid, 0u, name, 100u, -1);

    private static VendorInfoPayload Payload(params VendorItemInfo[] items) =>
        new VendorInfoPayload(
            VendorGuid: VendorGuid, MerchandiseItemTypes: 0u, MerchandiseMinValue: 0u,
            MerchandiseMaxValue: 0u, DealMagicalItems: false, BuyPrice: 0.75f, SellPrice: 1.0f,
            AlternateCurrency: 0u, AlternateCurrencyAmount: 0u, AlternateCurrencyName: "",
            ItemCount: items.Length)
        { Items = items };

    private static WorldState AtVendor(
        uint selfCell, Vector3 selfPos, uint? vendorCell, Vector3? vendorPos, VendorInfoPayload vendor)
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(ws, SelfGuid, "Headless", 1u, 0u, selfCell, null, position: selfPos);
        if (vendorCell is uint vc && vendorPos is Vector3 vp)
            SnapshotSeeding.Seed(ws, VendorGuid, "Provisioner", 9999u, 0u, vc, null, position: vp);
        ws.ApplyVendorInfo(vendor);
        return ws;
    }

    // ── ResolveVendorItemExact (HK: exact only, never substring) ──────────

    [Fact]
    public void ResolveVendorItemExact_MatchesExactNameCaseInsensitively()
    {
        var items = new[]
        {
            Item(0x1u, 100u, "Health Kit"),
            Item(0x2u, 101u, "Drudge Slaying Contract"),
        };

        var hit = WorldState.ResolveVendorItemExact(items, "drudge slaying contract");
        Assert.NotNull(hit);
        Assert.Equal(0x2u, hit!.Guid);
    }

    [Fact]
    public void ResolveVendorItemExact_DoesNotSubstringMatch()
    {
        // Critical HK guard: "Health" must NOT resolve to "Health Kit", and
        // "Contract" must NOT resolve to "Drudge Slaying Contract". Buying a
        // different item than the LLM named is a forbidden autonomous choice.
        var items = new[]
        {
            Item(0x1u, 100u, "Health Kit"),
            Item(0x2u, 101u, "Drudge Slaying Contract"),
        };

        Assert.Null(WorldState.ResolveVendorItemExact(items, "Health"));
        Assert.Null(WorldState.ResolveVendorItemExact(items, "Contract"));
        Assert.Null(WorldState.ResolveVendorItemExact(items, "Slaying"));
    }

    [Fact]
    public void ResolveVendorItemExact_NullOrEmptyOrNoMatch_ReturnsNull()
    {
        var items = new[] { Item(0x1u, 100u, "Health Kit") };
        Assert.Null(WorldState.ResolveVendorItemExact(items, null));
        Assert.Null(WorldState.ResolveVendorItemExact(items, "   "));
        Assert.Null(WorldState.ResolveVendorItemExact(items, "Mana Kit"));
        Assert.Null(WorldState.ResolveVendorItemExact(System.Array.Empty<VendorItemInfo>(), "Health Kit"));
    }

    // ── TryGetLiveOpenVendor (shared prompt+motor liveness gate) ──────────

    [Fact]
    public void TryGetLiveOpenVendor_True_WhenStandingAtVendor()
    {
        var ws = AtVendor(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            0xAAB50003u, new Vector3(7f, 96f, 0f), // ~2u
            Payload(Item(0x1u, 100u, "Health Kit")));

        Assert.True(ws.TryGetLiveOpenVendor(out var ov));
        Assert.NotNull(ov);
        Assert.Equal(VendorGuid, ov!.VendorGuid);
    }

    [Fact]
    public void TryGetLiveOpenVendor_False_WhenNoPanelOpen()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(ws, SelfGuid, "Headless", 1u, 0u, 0xAAB50003u, null,
            position: new Vector3(5f, 96f, 0f));

        Assert.False(ws.TryGetLiveOpenVendor(out var ov));
        Assert.Null(ov);
    }

    [Fact]
    public void TryGetLiveOpenVendor_False_WhenWalkedAwayOrVendorGone()
    {
        // Far away in the same landblock (proximity gate)...
        var far = AtVendor(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            0xAAB50003u, new Vector3(60f, 96f, 0f),
            Payload(Item(0x1u, 100u, "Health Kit")));
        Assert.False(far.TryGetLiveOpenVendor(out _));

        // ...and with no vendor object tracked at all (despawn).
        var gone = AtVendor(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            vendorCell: null, vendorPos: null,
            Payload(Item(0x1u, 100u, "Health Kit")));
        Assert.False(gone.TryGetLiveOpenVendor(out _));
    }

    // ── CountOwnedInventoryByWcid (currency-agnostic buy confirmation) ────

    [Fact]
    public void CountOwnedInventoryByWcid_CountsOnlySelfContainedMatchingWcid()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(ws, SelfGuid, "Headless", 1u, 0u, 0xAAB50003u, null);
        // two owned wcid-700, one owned wcid-701, one wcid-700 in another container
        SnapshotSeeding.Seed(ws, 0x111u, "Contract", 700u, 0u, 0xAAB50003u, SelfGuid);
        SnapshotSeeding.Seed(ws, 0x112u, "Contract", 700u, 0u, 0xAAB50003u, SelfGuid);
        SnapshotSeeding.Seed(ws, 0x113u, "Other", 701u, 0u, 0xAAB50003u, SelfGuid);
        SnapshotSeeding.Seed(ws, 0x114u, "Contract", 700u, 0u, 0xAAB50003u, 0x9999u); // in a chest, not owned

        Assert.Equal(2, ws.CountOwnedInventoryByWcid(700u));
        Assert.Equal(1, ws.CountOwnedInventoryByWcid(701u));
        Assert.Equal(0, ws.CountOwnedInventoryByWcid(702u));
    }
}
