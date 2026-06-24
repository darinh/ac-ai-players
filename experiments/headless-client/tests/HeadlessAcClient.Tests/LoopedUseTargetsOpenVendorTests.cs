// SPDX-License-Identifier: AGPL-3.0-or-later
// vendor-use-no-flee: a bare Use of the currently-OPEN vendor is a transactional
// no-op (the panel stays open), not a dead interior-door tour — so the world-Use
// churn egress must NOT flee it (a committed Explore-away abandons the open trade
// panel before the bot can Buy/Sell). LoopedUseTargetsOpenVendor identifies that
// case; these tests lock its match semantics (guid + name selector) and its
// exclusions (no open panel, non-Use, item-Use, a different target).

using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class LoopedUseTargetsOpenVendorTests
{
    private const uint SelfGuid = 0x5000000D;
    private const uint VendorGuid = 0x9001u;
    private const uint OtherGuid = 0x9002u;

    private static WorldStateProjection World(bool panelOpen)
    {
        var visible = new List<VisibleObjectProjection>
        {
            new() { Guid = VendorGuid, Name = "Provisioner", IsVendor = true, IsCreature = true, Distance = 2f },
            new() { Guid = OtherGuid, Name = "Drudge", IsMonster = true, IsCreature = true, Distance = 8f },
        };
        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
                PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            Vendor = panelOpen ? new VendorProjection { VendorGuid = VendorGuid } : null,
            Contracts = Array.Empty<ContractProjection>(),
        };
    }

    private static Goal Use(Selector target, Selector? item = null) =>
        new() { Kind = GoalKind.Use, Target = target, Item = item };

    [Fact]
    public void True_WhenUseGuidMatchesOpenVendor()
    {
        Assert.True(LlmGoalPolicy.LoopedUseTargetsOpenVendor(
            Use(new Selector { Guid = VendorGuid }), World(panelOpen: true)));
    }

    [Fact]
    public void True_WhenUseNameMatchesVisibleOpenVendor()
    {
        Assert.True(LlmGoalPolicy.LoopedUseTargetsOpenVendor(
            Use(new Selector { Name = "Provisioner" }), World(panelOpen: true)));
    }

    [Fact]
    public void False_WhenNoPanelOpen()
    {
        Assert.False(LlmGoalPolicy.LoopedUseTargetsOpenVendor(
            Use(new Selector { Name = "Provisioner" }), World(panelOpen: false)));
        Assert.False(LlmGoalPolicy.LoopedUseTargetsOpenVendor(
            Use(new Selector { Guid = VendorGuid }), World(panelOpen: false)));
    }

    [Fact]
    public void False_WhenNotUseVerb()
    {
        var talk = new Goal { Kind = GoalKind.Talk, Target = new Selector { Name = "Provisioner" } };
        Assert.False(LlmGoalPolicy.LoopedUseTargetsOpenVendor(talk, World(panelOpen: true)));
    }

    [Fact]
    public void False_WhenItemUse()
    {
        // An item-Use (goal.Item set) is owned by the inventory-Use dedup, not the
        // world-object churn guards, so it is never this case.
        var itemUse = Use(new Selector { Name = "Provisioner" }, item: new Selector { Name = "Healing Kit" });
        Assert.False(LlmGoalPolicy.LoopedUseTargetsOpenVendor(itemUse, World(panelOpen: true)));
    }

    [Fact]
    public void False_WhenTargetsADifferentObject()
    {
        // Use of a non-vendor object (a monster) while a vendor panel is open must
        // NOT be treated as an open-vendor Use — that door/object churn should
        // still egress normally.
        Assert.False(LlmGoalPolicy.LoopedUseTargetsOpenVendor(
            Use(new Selector { Name = "Drudge" }), World(panelOpen: true)));
        Assert.False(LlmGoalPolicy.LoopedUseTargetsOpenVendor(
            Use(new Selector { Guid = OtherGuid }), World(panelOpen: true)));
    }
}
