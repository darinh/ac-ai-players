// SPDX-License-Identifier: AGPL-3.0-or-later
// kill-task-source-nudge: a relevance-gated rule that surfaces an un-engaged
// in-view kill-task (contract) SOURCE — a `vendor`-tagged object (browse with
// Use) OR an un-talked dialog `npc` (reached by Talk; a source need not carry
// the wire ObjectDescriptionFlag.Vendor bit). It is a COMBAT-ONLY carveout: it
// fires ONLY when a monster is in view (the no-monster task-seeking rules — SEEK
// A KILL-TASK / LOOP-BREAK — already cover the source, and HUNT EXCURSION would
// conflict), AND a vendor (panel not open) OR an un-talked npc is visible, and
// no ACTIONABLE contract is tracked. Zero prompt budget otherwise, never nags
// while on a contract, never duplicates/conflicts with the no-monster
// hunt/seek rules. The rule surfaces a fact + a cheap option; the LLM decides.

using System;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VendorBrowseNudgeTests
{
    private const uint SelfGuid = 0x5000000C;
    private const string NudgeMarker = "FIND A KILL-TASK SOURCE";

    private static WorldStateProjection World(
        bool vendorVisible, bool monsterVisible = false,
        bool vendorPanelOpen = false, bool hasContract = false, uint contractStage = 2u,
        uint[]? contractStages = null, bool npcVisible = false, bool vendorIsCreature = false)
    {
        var visible = new System.Collections.Generic.List<VisibleObjectProjection>();
        if (vendorVisible)
            // IsCreature and IsVendor are independent wire bits; a live vendor is
            // BOTH. vendorIsCreature models that overlap so the npc-arm exclusion
            // can be tested.
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9001u, Name = "Provisioner", IsVendor = true,
                IsCreature = vendorIsCreature, Distance = 10f,
            });
        if (npcVisible)
            // An un-talked dialog NPC: a creature that is not a monster, with no
            // ObjectDescriptionFlag.Vendor bit (reached by Talk, not a trade panel).
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9003u, Name = "Townsperson", IsCreature = true, IsMonster = false,
                Distance = 9f,
            });
        if (monsterVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9002u, Name = "Drudge", IsMonster = true, IsCreature = true,
                IsAttackable = true, Distance = 8f,
            });

        ContractProjection[] contracts;
        if (contractStages is not null)
        {
            contracts = new ContractProjection[contractStages.Length];
            for (int i = 0; i < contracts.Length; i++)
                contracts[i] = new ContractProjection { ContractId = (uint)(i + 1), Stage = contractStages[i] };
        }
        else
        {
            contracts = hasContract
                ? new[] { new ContractProjection { ContractId = 1u, Stage = contractStage } }
                : Array.Empty<ContractProjection>();
        }

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
            Contracts = contracts,
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
        // Already on an ACTIONABLE contract (stage 2 in progress) -> pursue it,
        // don't nag about browsing vendors.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true, hasContract: true)));
    }

    [Fact]
    public void Nudge_Present_WhenAllTrackedContractsAreDoneStage3()
    {
        // The batch is finished: every tracked contract is stage 3 (done /
        // pending repeat), so the bot needs a FRESH contract to keep earning a
        // reward — the nudge must surface the vendor again even though a contract
        // is still in the tracker.
        Assert.Contains(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true,
                hasContract: true, contractStage: 3u)));
    }

    [Fact]
    public void Nudge_Present_WhenMultipleContractsAllStage3()
    {
        // The live shape: TWO tracked contracts, both stage 3 (done). The whole
        // batch is finished, so the nudge fires to seek a fresh one.
        Assert.Contains(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true,
                contractStages: new uint[] { 3u, 3u })));
    }

    [Fact]
    public void Nudge_Absent_WhenContractsMixedDoneAndInProgress()
    {
        // One done (stage 3) + one still in progress (stage 2): an actionable
        // contract remains, so pursue it — do NOT nag about a fresh batch.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true,
                contractStages: new uint[] { 3u, 2u })));
    }

    [Fact]
    public void Nudge_Absent_WhenVendorPanelAlreadyOpen()
    {
        // The panel is open -> ## Vendor offerings already shows the wares.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true, vendorPanelOpen: true)));
    }

    [Fact]
    public void Nudge_Present_WhenUntalkedNpcInView_NoVendor()
    {
        // A contract SOURCE need not carry the wire Vendor bit: an un-talked
        // dialog NPC is reached by Talk, not a vendor trade panel. With a monster
        // in view + an un-talked npc + the batch DONE, the nudge must fire even
        // with NO vendor present, so the bot Talks the npc to seek a fresh task
        // instead of grinding past it.
        Assert.Contains(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: true,
                npcVisible: true, contractStages: new uint[] { 3u, 3u })));
    }

    [Fact]
    public void Nudge_Absent_WhenUntalkedNpcButActionableContract()
    {
        // An un-talked npc is in view but an ACTIONABLE contract (stage 2)
        // remains -> pursue it; do NOT nudge seeking a fresh source.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: true,
                npcVisible: true, hasContract: true)));
    }

    [Fact]
    public void Nudge_Absent_WhenNpcInViewButNoMonster()
    {
        // No monster -> the no-monster LOOP-BREAK rule already drives Talking
        // un-talked npcs in a town; this combat-only nudge stays off to avoid
        // duplicating it.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: false, npcVisible: true)));
    }

    [Fact]
    public void Nudge_Present_WhenUntalkedNpcDespiteOpenVendorPanel()
    {
        // The npc arm does NOT depend on `world.Vendor is null`: an un-talked
        // task-giver is worth a Talk even while some other vendor's panel is open
        // (the open panel only makes the VENDOR arm redundant, not the npc arm).
        Assert.Contains(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: true, npcVisible: true,
                vendorPanelOpen: true, contractStages: new uint[] { 3u, 3u })));
    }

    [Fact]
    public void Nudge_Absent_WhenOnlySourceIsAnOpenPanelVendorCreature()
    {
        // A live vendor is BOTH IsVendor and IsCreature. With its panel OPEN and
        // no other source, the nudge must stay OFF: the vendor arm is suppressed
        // by the open panel, and the npc arm must NOT re-trigger on the same
        // vendor-creature (CountUntalkedNpcsInView excludeVendors:true).
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, vendorIsCreature: true, monsterVisible: true,
                vendorPanelOpen: true, contractStages: new uint[] { 3u, 3u })));
    }

    [Fact]
    public void Nudge_Absent_WhenNeitherVendorNorNpcVisible()
    {
        // Only a monster in view, no kill-task source of either kind -> absent.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: true)));
    }
}
