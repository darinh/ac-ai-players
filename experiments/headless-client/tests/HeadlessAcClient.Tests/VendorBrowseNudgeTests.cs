// SPDX-License-Identifier: AGPL-3.0-or-later
// vendor-browse-nudge: a relevance-gated rule that surfaces an unbrowsed in-view
// vendor as a possible kill-task (contract) source. It is a COMBAT-ONLY carveout:
// it fires ONLY when a monster is in view (the monster-gated task-seeking rules
// are suppressed in combat, so the bot would otherwise grind past the vendor),
// AND a `vendor`-tagged object is visible, no vendor panel is open, and no
// contract is tracked. Zero prompt budget otherwise, never nags once on a
// contract, and never duplicates/conflicts with the no-monster hunt/seek rules.
// The rule surfaces a fact + a cheap option; the LLM decides.

using System;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VendorBrowseNudgeTests
{
    private const uint SelfGuid = 0x5000000C;
    private const string NudgeMarker = "BROWSE A VENDOR FOR A KILL-TASK";

    private static WorldStateProjection World(
        bool vendorVisible, bool monsterVisible = false,
        bool vendorPanelOpen = false, bool hasContract = false)
    {
        var visible = new System.Collections.Generic.List<VisibleObjectProjection>();
        if (vendorVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9001u, Name = "Provisioner", IsVendor = true, Distance = 10f,
            });
        if (monsterVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9002u, Name = "Drudge", IsMonster = true, IsCreature = true,
                IsAttackable = true, Distance = 8f,
            });

        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
                PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            Vendor = vendorPanelOpen ? new VendorProjection { VendorGuid = 0x9001u } : null,
            Contracts = hasContract
                ? new[] { new ContractProjection { ContractId = 1u, Stage = 2u } }
                : Array.Empty<ContractProjection>(),
        };
    }

    private static string Prompt(WorldStateProjection w) =>
        LlmGoalPolicy.BuildUserPrompt(w, new EventStream(), null);

    [Fact]
    public void Nudge_Absent_WhenNoMonsterInView()
    {
        // No monster -> the !monsterInView SEEK A KILL-TASK rule already covers
        // vendors and HUNT EXCURSION would conflict, so this combat-only nudge
        // stays off.
        Assert.DoesNotContain(NudgeMarker, Prompt(World(vendorVisible: true, monsterVisible: false)));
    }

    [Fact]
    public void Nudge_Present_WhenMonsterInViewSuppressesSeekRules()
    {
        // The key gap: with a monster in view the task-seeking rules are
        // suppressed, so this nudge surfaces the vendor opportunity the bot would
        // otherwise grind past.
        Assert.Contains(NudgeMarker, Prompt(World(vendorVisible: true, monsterVisible: true)));
    }

    [Fact]
    public void Nudge_Absent_WhenNoVendorVisible()
    {
        Assert.DoesNotContain(NudgeMarker, Prompt(World(vendorVisible: false, monsterVisible: true)));
    }

    [Fact]
    public void Nudge_Absent_WhenAlreadyTrackingAContract()
    {
        // Already on a contract -> pursue it, don't nag about browsing vendors.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true, hasContract: true)));
    }

    [Fact]
    public void Nudge_Absent_WhenVendorPanelAlreadyOpen()
    {
        // The panel is open -> ## Vendor offerings already shows the wares.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true, vendorPanelOpen: true)));
    }
}
