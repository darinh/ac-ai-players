// SPDX-License-Identifier: AGPL-3.0-or-later
// Vendor-perception unit tests (surface-vendor-offerings slice). Audit-safe
// invariants: when the bot is standing at an open vendor, the decoded for-sale
// list is surfaced to the LLM as RAW FACTS in wire order (name + buy cost in the
// vendor's actual currency unit + stack size) — source assigns no priority,
// ranks nothing, and never decides to buy. The panel is only surfaced while the
// vendor object is still tracked and within interaction range in the landblock
// it was opened in (it goes stale the moment the bot walks away, the vendor
// despawns, or the bot changes landblock). The buy cost mirrors the server's
// Vendor.GetSellCost exactly, including its fixed-rate override for promissory
// notes and its float multiply.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VendorPerceptionTests
{
    private const uint SelfGuid = 0x50000007;
    private const uint VendorGuid = 0x7A9B404E;
    private const uint ItemTypePromissoryNote = 0x00040000u;

    // ── builders ─────────────────────────────────────────────────────────

    // A decoded vendor payload with the given for-sale items (guid / wcid /
    // itemType / name / value / stack; -1 = unlimited).
    private static VendorInfoPayload MakeVendor(
        float sellPrice,
        params (uint guid, uint wcid, uint itemType, string name, uint? value, int stack)[] items) =>
        new VendorInfoPayload(
            VendorGuid: VendorGuid,
            MerchandiseItemTypes: 0u, MerchandiseMinValue: 0u, MerchandiseMaxValue: 0u,
            DealMagicalItems: false,
            BuyPrice: 0.75f, SellPrice: sellPrice,
            AlternateCurrency: 0u, AlternateCurrencyAmount: 0u, AlternateCurrencyName: "",
            ItemCount: items.Length)
        {
            Items = items
                .Select(i => new VendorItemInfo(i.guid, i.wcid, i.itemType, i.name, i.value, i.stack))
                .ToList(),
        };

    // A WorldState with self positioned and (optionally) the vendor object
    // nearby, with the vendor panel open (stamped to self's landblock).
    private static WorldState SeedVendorWorld(
        uint selfCell, Vector3 selfPos,
        uint? vendorCell, Vector3? vendorPos,
        VendorInfoPayload vendor)
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        SnapshotSeeding.Seed(ws, SelfGuid, "Headless", 1u, 0u, selfCell, null, position: selfPos);
        if (vendorCell is uint vc && vendorPos is Vector3 vp)
            SnapshotSeeding.Seed(ws, VendorGuid, "Provisioner", 9999u, 0u, vc, null,
                objectDescriptionFlags: (uint)ObjectDescriptionFlag.Vendor, position: vp);
        ws.ApplyVendorInfo(vendor);
        return ws;
    }

    // A bare projection carrying just the vendor (the rest empty) for prompt
    // tests (these bypass the FromWorldState proximity gate).
    private static WorldStateProjection VendorProj(VendorProjection? vendor) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
            PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
        },
        Inventory = System.Array.Empty<InventoryItemProjection>(),
        Visible = System.Array.Empty<VisibleObjectProjection>(),
        Vendor = vendor,
    };

    private static VendorProjection Offers(
        float buyCostMultiplier, params (string name, uint? value, int stack)[] offers) =>
        new VendorProjection
        {
            VendorGuid = VendorGuid,
            BuyCostMultiplier = buyCostMultiplier,
            AlternateCurrency = 0u,
            AlternateCurrencyName = "",
            Offers = offers
                .Select(o => new VendorOfferProjection
                {
                    Name = o.name, Wcid = 0u, ItemType = 0u, Value = o.value, StackSize = o.stack,
                })
                .ToList(),
        };

    private static string Section(string prompt, string header)
    {
        int start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int next = prompt.IndexOf("\n## ", start + header.Length, StringComparison.Ordinal);
        return next < 0 ? prompt.Substring(start) : prompt.Substring(start, next - start);
    }

    // ── WorldState lifecycle / proximity + landblock gate ────────────────

    [Fact]
    public void ApplyVendorInfo_StampsOpenVendorAndLandblock()
    {
        var ws = SeedVendorWorld(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            0xAAB50003u, new Vector3(7f, 96f, 0f),
            MakeVendor(2.0f, (0x1u, 111u, 0u, "Drudge Slaying Contract", 100u, -1)));

        Assert.NotNull(ws.OpenVendor);
        Assert.Equal(VendorGuid, ws.OpenVendor!.VendorGuid);
        Assert.Equal(0xAAB5u, ws.OpenVendorLandblock); // self cell >> 16
    }

    [Fact]
    public void Vendor_FromWorldState_SurfacesOffersWhenStandingAtVendor()
    {
        var ws = SeedVendorWorld(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            0xAAB50003u, new Vector3(7f, 96f, 0f), // ~2u away
            MakeVendor(2.0f, (0x1u, 111u, ItemTypePromissoryNote, "Drudge Slaying Contract", 100u, -1)));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.NotNull(proj);
        Assert.NotNull(proj!.Vendor);
        Assert.Equal(2.0f, proj.Vendor!.BuyCostMultiplier); // = SellPrice, not BuyPrice
        Assert.Equal(0u, proj.Vendor.AlternateCurrency);
        Assert.Single(proj.Vendor.Offers);
        Assert.Equal("Drudge Slaying Contract", proj.Vendor.Offers[0].Name);
        Assert.Equal(100u, proj.Vendor.Offers[0].Value);
        Assert.Equal(-1, proj.Vendor.Offers[0].StackSize);
        Assert.Equal(ItemTypePromissoryNote, proj.Vendor.Offers[0].ItemType); // carried through
    }

    [Fact]
    public void Vendor_FromWorldState_GoesStaleWhenSelfLeavesLandblock()
    {
        var ws = SeedVendorWorld(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            0xAAB50003u, new Vector3(7f, 96f, 0f),
            MakeVendor(2.0f, (0x1u, 111u, 0u, "Drudge Slaying Contract", 100u, -1)));

        // The bot walks to a different landblock (the panel was stamped to the
        // old one) — re-seed self in a new cell.
        SnapshotSeeding.Seed(ws, SelfGuid, "Headless", 1u, 0u, 0xBBC60003u, null,
            position: new Vector3(5f, 96f, 0f));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.NotNull(proj);
        Assert.Null(proj!.Vendor);
    }

    [Fact]
    public void Vendor_FromWorldState_GoesStaleWhenSelfWalksAwayInLandblock()
    {
        // Same landblock, but the bot is now far from the vendor object: the
        // proximity gate must drop the panel even without a landblock change.
        var ws = SeedVendorWorld(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            0xAAB50003u, new Vector3(190f, 96f, 0f), // ~185u away, same cell/landblock
            MakeVendor(2.0f, (0x1u, 111u, 0u, "Drudge Slaying Contract", 100u, -1)));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.NotNull(proj);
        Assert.Null(proj!.Vendor);
    }

    [Fact]
    public void Vendor_FromWorldState_GoesStaleJustBeyondInteractionRange()
    {
        // Same landblock, vendor ~15u away — past /use range but inside the old
        // 25u radius. The tightened interaction-range gate must drop the panel
        // here (regression guard for the stale "vendor open" band).
        var ws = SeedVendorWorld(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            0xAAB50003u, new Vector3(20f, 96f, 0f), // ~15u away, same cell/landblock
            MakeVendor(2.0f, (0x1u, 111u, 0u, "Drudge Slaying Contract", 100u, -1)));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.NotNull(proj);
        Assert.Null(proj!.Vendor);
    }

    [Fact]
    public void Vendor_FromWorldState_DroppedWhenVendorObjectMissing()
    {
        // The vendor despawned / is no longer tracked: nothing to anchor the
        // panel to, so it must not surface.
        var ws = SeedVendorWorld(
            0xAAB50003u, new Vector3(5f, 96f, 0f),
            vendorCell: null, vendorPos: null,
            MakeVendor(2.0f, (0x1u, 111u, 0u, "Drudge Slaying Contract", 100u, -1)));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        Assert.NotNull(proj);
        Assert.Null(proj!.Vendor);
    }

    // ── prompt capsule rendering ─────────────────────────────────────────

    [Fact]
    public void Vendor_Capsule_RendersNameAndBuyCostAsRawFacts()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            VendorProj(Offers(2.0f, ("Drudge Slaying Contract", 100u, -1))),
            new EventStream(), null);

        Assert.Contains("## Vendor offerings", prompt);
        var cap = Section(prompt, "## Vendor offerings");
        Assert.Contains("Drudge Slaying Contract", cap);
        // cost = max(1, ceil(SellPrice*value - 0.1)) = ceil(2.0*100 - 0.1) = 200.
        Assert.Contains("200 coin to buy (value 100)", cap);
        // HK invariant: the capsule states a raw fact, not a recommendation.
        Assert.Contains("raw fact, not a recommendation", cap);
    }

    [Fact]
    public void Vendor_Capsule_CostFloorIsOneCoin()
    {
        // A value-0 item still costs the 1-coin floor.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            VendorProj(Offers(1.0f, ("Free Contract", 0u, -1))),
            new EventStream(), null);

        var cap = Section(prompt, "## Vendor offerings");
        Assert.Contains("1 coin to buy (value 0)", cap);
    }

    [Fact]
    public void Vendor_Capsule_PromissoryNoteUsesFixedServerRate()
    {
        // The server's GetSellCost ignores the vendor's SellPrice for promissory
        // notes and charges a fixed 1.15 rate. Even though this vendor's
        // multiplier is 5.0, the promissory note must price at 1.15*100 = 115,
        // not 5*100 = 500.
        var vendor = new VendorProjection
        {
            VendorGuid = VendorGuid,
            BuyCostMultiplier = 5.0f,
            Offers = new[]
            {
                new VendorOfferProjection
                {
                    Name = "Town Contract", Value = 100u, StackSize = -1,
                    ItemType = ItemTypePromissoryNote,
                },
            },
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(VendorProj(vendor), new EventStream(), null),
            "## Vendor offerings");

        Assert.Contains("115 coin to buy (value 100)", cap);
        Assert.DoesNotContain("500 coin", cap);
    }

    [Fact]
    public void Vendor_Capsule_RendersAlternateCurrencyUnit()
    {
        // An alternate-currency vendor charges the same GetSellCost amount but
        // in its own currency unit — the prompt must say that unit, not "coin".
        var vendor = new VendorProjection
        {
            VendorGuid = VendorGuid,
            BuyCostMultiplier = 1.0f,
            AlternateCurrency = 12345u,
            AlternateCurrencyName = "Memory Crystals",
            Offers = new[]
            {
                new VendorOfferProjection { Name = "Mana Charge", Value = 100u, StackSize = -1 },
            },
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(VendorProj(vendor), new EventStream(), null),
            "## Vendor offerings");

        Assert.Contains("100 Memory Crystals to buy (value 100)", cap);
        Assert.DoesNotContain("100 coin", cap);
    }

    [Fact]
    public void Vendor_Capsule_RendersStackSizeWhenStacked()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            VendorProj(Offers(1.0f,
                ("Healing Kit", 50u, 5),
                ("Single Item", 50u, 1),
                ("Unlimited Item", 50u, -1))),
            new EventStream(), null);

        var cap = Section(prompt, "## Vendor offerings");
        Assert.Contains("sold in stacks of 5", cap);
        // Stack 1 and -1 (unlimited) carry no stack note.
        Assert.DoesNotContain("stacks of 1", cap);
        Assert.DoesNotContain("stacks of -1", cap);
    }

    [Fact]
    public void Vendor_Capsule_NoValue_RendersNameOnly()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            VendorProj(Offers(1.0f, ("Mystery Item", null, -1))),
            new EventStream(), null);

        var cap = Section(prompt, "## Vendor offerings");
        Assert.Contains("- Mystery Item", cap);
        Assert.DoesNotContain("Mystery Item:", cap); // no "name: cost" row
        Assert.DoesNotContain("coin to buy", cap);
    }

    [Fact]
    public void Vendor_Capsule_OmittedWhenNoVendorOrNoOffers()
    {
        // No open vendor.
        Assert.DoesNotContain("## Vendor offerings",
            LlmGoalPolicy.BuildUserPrompt(VendorProj(null), new EventStream(), null));
        // Open vendor with an empty for-sale list.
        Assert.DoesNotContain("## Vendor offerings",
            LlmGoalPolicy.BuildUserPrompt(VendorProj(Offers(1.0f)), new EventStream(), null));
    }

    [Fact]
    public void Vendor_Capsule_CharBudgetCapsAndNotesOverflow()
    {
        // A large for-sale list must not blow the protected char budget: excess
        // items are dropped (in wire order) with a "+N more" note, and the first
        // item always survives.
        var many = Enumerable.Range(0, 60)
            .Select(i => ($"Vendor Item Number {i} with a fairly long descriptive name",
                (uint?)100u, -1))
            .ToArray();

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            VendorProj(Offers(1.0f, many)), new EventStream(), null);
        var cap = Section(prompt, "## Vendor offerings");

        Assert.Contains("Vendor Item Number 0 ", cap);          // first always survives
        Assert.Contains("more for sale, not shown", cap);       // overflow noted
        var rendered = System.Text.RegularExpressions.Regex
            .Matches(cap, @"-   ?\S").Count;
        Assert.True(rendered < 60, "char budget must cap the rendered rows");
    }

    [Fact]
    public void Vendor_Capsule_SurvivesBodyTrimAtTinyCeiling()
    {
        // The ## Vendor offerings section lives in the protected salience tail,
        // so the open vendor's merchandise stays visible even at a tight request
        // ceiling that forces the trimmable body (rules preamble + ## Visible
        // nearby) to be hard-cut.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            VendorProj(Offers(1.0f, ("Slay Drudges Contract", 500u, -1))),
            new EventStream(), currentGoal: null, stack: null,
            pickerActivity: null, explorationCandidates: null,
            talkedNpcGuids: new HashSet<uint>(), promptCeiling: 10000);

        Assert.Contains("## Vendor offerings", prompt);
        Assert.Contains("Slay Drudges Contract", Section(prompt, "## Vendor offerings"));
        Assert.True(prompt.Length <= 10000,
            $"prompt length {prompt.Length} exceeds ceiling 10000");
    }
}
