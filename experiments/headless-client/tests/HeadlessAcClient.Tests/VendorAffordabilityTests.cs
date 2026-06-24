// SPDX-License-Identifier: AGPL-3.0-or-later
// vendor-affordability-marker: the ## Vendor offerings cost line now marks an
// item the bot CANNOT AFFORD (cost > the bot's server-tracked CoinValue) for a
// COIN vendor, so the LLM does not waste a Buy that can never arrive (an
// unaffordable Buy times out). Coin vendors only; only when CoinValue is known.
// The LLM still decides whether to buy or to raise coin (e.g. by Selling).

using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VendorAffordabilityTests
{
    private const uint SelfGuid = 0x5000000E;
    private const uint VendorGuid = 0x9100u;

    private static WorldStateProjection World(
        int? coin, uint altCurrency, params (string name, uint value)[] offers) =>
        World(coin, altCurrency, 2.0f, offers);

    private static WorldStateProjection World(
        int? coin, uint altCurrency, float buyCostMultiplier, params (string name, uint value)[] offers)
    {
        var offerList = new List<VendorOfferProjection>();
        foreach (var (name, value) in offers)
            offerList.Add(new VendorOfferProjection
            { Name = name, Wcid = 0u, ItemType = 0u, Value = value, StackSize = 1 });

        var vendor = new VendorProjection
        {
            VendorGuid = VendorGuid,
            BuyCostMultiplier = buyCostMultiplier,   // cost = max(1, ceil(mult*value - 0.1))
            AlternateCurrency = altCurrency,
            AlternateCurrencyName = altCurrency == 0u ? "" : "Tokens",
            Offers = offerList,
        };
        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
                PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
                CoinValue = coin,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),
            Vendor = vendor,
        };
    }

    private static string Cap(WorldStateProjection w)
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(w, new EventStream(), null);
        int start = prompt.IndexOf("## Vendor offerings", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int next = prompt.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return next < 0 ? prompt.Substring(start) : prompt.Substring(start, next - start);
    }

    [Fact]
    public void CannotAfford_Marked_WhenCoinBelowCost()
    {
        // value 100 * 2.0 = 200 cost; bot has 50 coin -> cannot afford.
        var cap = Cap(World(coin: 50, altCurrency: 0u, ("Pricey Tome", 100u)));
        Assert.Contains("200 coin to buy (value 100)", cap);
        Assert.Contains("you have 50 coin, CANNOT AFFORD", cap);
    }

    [Fact]
    public void NoMarker_WhenAffordable()
    {
        var cap = Cap(World(coin: 500, altCurrency: 0u, ("Pricey Tome", 100u)));
        Assert.Contains("200 coin to buy (value 100)", cap);
        Assert.DoesNotContain("CANNOT AFFORD", cap);
    }

    [Fact]
    public void NoMarker_WhenCoinUnknown()
    {
        var cap = Cap(World(coin: null, altCurrency: 0u, ("Pricey Tome", 100u)));
        Assert.Contains("200 coin to buy (value 100)", cap);
        Assert.DoesNotContain("CANNOT AFFORD", cap);
    }

    [Fact]
    public void NoMarker_ForAlternateCurrencyVendor()
    {
        // An alt-currency vendor charges its own currency, not coin, so the coin
        // comparison does not apply -> never mark there (even with 0 coin).
        var cap = Cap(World(coin: 0, altCurrency: 0x99u, ("Token Item", 100u)));
        Assert.DoesNotContain("CANNOT AFFORD", cap);
    }

    [Fact]
    public void NoMarker_WhenExactlyAffordable()
    {
        // cost == coin -> affordable (cost > coin is the gate).
        var cap = Cap(World(coin: 200, altCurrency: 0u, ("Pricey Tome", 100u)));
        Assert.DoesNotContain("CANNOT AFFORD", cap);
    }

    [Fact]
    public void NonIntegerMultiplier_AffordabilityRidesTheComputedCost()
    {
        // A non-integer buy rate exercises the float-multiply + ceil cost path
        // (the same one used for promissory notes / fractional SellPrice). The
        // affordability marker must ride the SAME computed cost the row displays,
        // so it stays aligned at the exact boundary. 1.15 * 100 = 115; ceil(115 -
        // 0.1) = 115.
        var costLine = "115 coin to buy (value 100)";

        // coin == cost - 1 -> cannot afford.
        var below = Cap(World(coin: 114, altCurrency: 0u, 1.15f, ("Note", 100u)));
        Assert.Contains(costLine, below);
        Assert.Contains("you have 114 coin, CANNOT AFFORD", below);

        // coin == cost -> affordable (no marker).
        var exact = Cap(World(coin: 115, altCurrency: 0u, 1.15f, ("Note", 100u)));
        Assert.Contains(costLine, exact);
        Assert.DoesNotContain("CANNOT AFFORD", exact);
    }
}
