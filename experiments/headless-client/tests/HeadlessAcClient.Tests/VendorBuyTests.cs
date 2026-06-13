// SPDX-License-Identifier: AGPL-3.0-or-later
// Vendor-buy slice: the LLM can emit a Buy goal naming a for-sale item, and the
// prompt tells it how. These tests lock the goal-parse path (a "Buy" kind with a
// target item name) and the prompt surface (the ## Vendor offerings capsule
// names the Buy verb; the schema lists "Buy"). The motor dispatch
// (HandshakeDriver resolving the name to the open vendor's item guid and sending
// GameActionBuy) mirrors the existing Raise* self-action dispatch.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VendorBuyTests
{
    private const uint SelfGuid = 0x50000009;
    private const uint VendorGuid = 0x7A9B404E;

    // ── goal parse ───────────────────────────────────────────────────────

    [Fact]
    public void TryParseGoal_BuyKind_WithItemName_Parses()
    {
        var json = """
        { "goal_id": "g1", "kind": "Buy",
          "target": { "name": "Drudge Slaying Contract" },
          "rationale": "buy the kill contract", "priority": 5 }
        """;

        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var goal, out var error), error);
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.Buy, goal!.Kind);
        Assert.Equal("Drudge Slaying Contract", goal.Target.Name);
    }

    [Fact]
    public void TryParseGoal_BuyKind_LowercaseAndCaseInsensitive_Parses()
    {
        // JsonStringEnumConverter is case-insensitive, so the model may emit
        // "buy" in any case.
        var json = """{ "kind": "buy", "target": { "name": "Health Kit" } }""";

        Assert.True(LlmGoalPolicy.TryParseGoal(json, out var goal, out _));
        Assert.Equal(GoalKind.Buy, goal!.Kind);
    }

    [Fact]
    public void TryParseGoal_BuyKind_EmptyTarget_Rejected()
    {
        // A Buy with no item to buy is meaningless and must be rejected (like
        // every non-Recall verb, Buy requires a target).
        var json = """{ "kind": "Buy", "target": null }""";

        Assert.False(LlmGoalPolicy.TryParseGoal(json, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ── prompt surface ───────────────────────────────────────────────────

    private static WorldStateProjection VendorWorld() => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
            PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
        },
        Inventory = System.Array.Empty<InventoryItemProjection>(),
        Visible = System.Array.Empty<VisibleObjectProjection>(),
        Vendor = new VendorProjection
        {
            VendorGuid = VendorGuid,
            BuyCostMultiplier = 1.0f,
            Offers = new[]
            {
                new VendorOfferProjection { Name = "Drudge Slaying Contract", Value = 100u, StackSize = -1 },
            },
        },
    };

    [Fact]
    public void Prompt_VendorCapsule_TellsTheLlmHowToBuy()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(VendorWorld(), new EventStream(), null);

        // The schema offers the Buy verb...
        Assert.Contains("\"Buy\"", prompt);
        // ...and the ## Vendor offerings capsule instructs how to use it.
        Assert.Contains("emit a Buy goal", prompt);
        Assert.Contains("target.name", prompt);
    }
}
