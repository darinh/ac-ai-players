// SPDX-License-Identifier: AGPL-3.0-or-later
// return-to-contract-source (cp035): a relevance-gated nudge that, when the bot
// holds a FINISHED contract batch (every tracked contract DONE, stage 3), NO
// contract source (vendor / un-talked npc) is in Visible nearby, AND a contract
// carries a dat location (a bearing in ## Contracts), surfaces the option to
// TRAVEL back toward that populated area to find a fresh source — instead of
// grinding monsters that carry the bot further from any source. Navigation only
// (Explore toward the bearing's compass), never a re-Talk of a settled turn-in
// NPC. The FIND-A-KILL-TASK-SOURCE rule (source IN VIEW) is the complement.

using System;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ReturnToContractSourceTests
{
    private const uint SelfGuid = 0x5000000D;
    private const string Marker = "RETURN TO A CONTRACT SOURCE";

    private static WorldStateProjection World(
        uint[] contractStages, bool withBearing,
        bool npcVisible = false, bool vendorVisible = false,
        bool vendorPanelOpen = false, bool monsterVisible = false,
        bool selfCellKnown = true, bool coordsOnFirstContract = true)
    {
        var visible = new System.Collections.Generic.List<VisibleObjectProjection>();
        if (npcVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9003u, Name = "Townsperson", IsCreature = true, IsMonster = false,
                Distance = 9f,
            });
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

        var contracts = new ContractProjection[contractStages.Length];
        for (int i = 0; i < contracts.Length; i++)
        {
            // Coords present when withBearing; coordsOnFirstContract lets a test
            // give coords only to a LATER contract (first row lacks them) to
            // exercise the conservative first-row gate.
            var hasCoords = withBearing && (i == 0 ? coordsOnFirstContract : true);
            contracts[i] = new ContractProjection
            {
                ContractId = (uint)(i + 1),
                Stage = contractStages[i],
                NpcEnd = "Buckminster",
                TurnInWorldX = hasCoords ? 2000f : (float?)null,
                TurnInWorldY = hasCoords ? 3000f : (float?)null,
            };
        }

        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u,
                CellId = selfCellKnown ? 0xAAB50003u : (uint?)null,
                PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            Vendor = vendorPanelOpen ? new VendorProjection { VendorGuid = 0x9001u } : null,
            Contracts = contracts,
        };
    }

    private static string Prompt(WorldStateProjection w) =>
        LlmGoalPolicy.BuildUserPrompt(w, new EventStream(), null);

    [Fact]
    public void Present_WhenDoneBatch_NoSourceInView_WithBearing()
    {
        // Finished batch, nothing in view, a contract has a dat bearing -> nudge.
        Assert.Contains(Marker, Prompt(World(new uint[] { 3u, 3u }, withBearing: true)));
    }

    [Fact]
    public void Present_EvenWithMonsterInView()
    {
        // A monster in view must NOT suppress the nudge: the whole point is to
        // pull the bot back toward a source rather than grind it further away.
        Assert.Contains(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, monsterVisible: true)));
    }

    [Fact]
    public void Absent_WhenUntalkedNpcInView()
    {
        // A source IS in view -> the FIND-A-KILL-TASK-SOURCE rule owns it; the
        // return-travel nudge stays off (no point navigating away to find a
        // source that is right here).
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, npcVisible: true)));
    }

    [Fact]
    public void Absent_WhenVendorInView_PanelClosed()
    {
        // An unbrowsed vendor in view is also a source in view -> nudge off.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, vendorVisible: true)));
    }

    [Fact]
    public void Absent_WhenVendorInView_PanelOpen()
    {
        // A vendor in view is a reachable source (even mid-browse with the panel
        // open) -> do NOT travel away from it; the nudge stays off and the bot
        // engages/reads it via ## Vendor offerings.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true,
                vendorVisible: true, vendorPanelOpen: true)));
    }

    [Fact]
    public void Absent_WhenBatchNotAllDone()
    {
        // One contract still in progress (stage 2) -> not a finished batch -> off.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 2u }, withBearing: true)));
    }

    [Fact]
    public void Absent_WhenNoContracts()
    {
        Assert.DoesNotContain(Marker, Prompt(World(Array.Empty<uint>(), withBearing: true)));
    }

    [Fact]
    public void Absent_WhenNoBearing()
    {
        // Done batch, no source in view, but no contract carries a dat location ->
        // there is nowhere to head, so the nudge stays off.
        Assert.DoesNotContain(Marker, Prompt(World(new uint[] { 3u, 3u }, withBearing: false)));
    }

    [Fact]
    public void Absent_WhenSelfPositionUnknown()
    {
        // Coords exist but the bot's own position is unknown, so ## Contracts can
        // render NO bearing to copy -> gate on the RENDERED bearing, not raw
        // coords: the nudge must stay off rather than point at an unshown bearing.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, selfCellKnown: false)));
    }

    [Fact]
    public void Absent_WhenOnlyLaterContractHasBearing()
    {
        // Conservative gate: only the FIRST contract row is GUARANTEED rendered
        // (a later row can be dropped by the contracts char budget). If only a
        // later contract carries coords, the nudge stays OFF rather than risk
        // pointing at a bearing the budget could drop.
        Assert.DoesNotContain(Marker,
            Prompt(World(new uint[] { 3u, 3u }, withBearing: true, coordsOnFirstContract: false)));
    }
}
