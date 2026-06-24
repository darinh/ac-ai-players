// SPDX-License-Identifier: AGPL-3.0-or-later
// kill-task-source-nudge: a relevance-gated rule that surfaces an un-engaged
// in-view kill-task (contract) SOURCE — a `vendor`-tagged object (browse with
// Use) OR an un-talked dialog `npc` (reached by Talk; a source need not carry
// the wire ObjectDescriptionFlag.Vendor bit). It fires when a vendor (panel not
// open) OR an un-talked npc is visible, no ACTIONABLE contract is tracked, AND
// EITHER a monster is in view (a combat scene SUPPRESSES the !monsterInView
// task-seeking rules — SEEK A KILL-TASK / LOOP-BREAK — so the bot would
// otherwise grind past the source) OR the bot holds a FINISHED batch (every
// tracked contract DONE, stage 3) so a fresh source is the only way to keep
// earning even with no monster present (the no-monster arm requires a held
// finished batch, NOT merely zero contracts, so a fresh character is not
// canvassed). Zero prompt budget otherwise, never nags while on an actionable
// contract. The rule surfaces a fact + a cheap option; the LLM decides.

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
        uint[]? contractStages = null, bool npcVisible = false, bool vendorIsCreature = false,
        bool armed = true)
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
            Inventory = armed
                ? new[] { new InventoryItemProjection { Guid = 0x7E1u, Name = "Spadone", Wcid = 1u, ItemType = 0x1u, WieldedAt = 0x02000000u } }
                : Array.Empty<InventoryItemProjection>(),
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
        // No monster AND no held contract batch -> the !monsterInView SEEK A
        // KILL-TASK / LOOP-BREAK rules already cover finding a first source, so
        // this nudge stays off (the no-monster arm requires a HELD finished
        // batch, not a fresh character with zero contracts).
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
    public void Nudge_Absent_WhenUnarmed()
    {
        // Combat-readiness gate: an UNARMED bot cannot complete a kill-task, so the
        // contract-source nudge is suppressed (arming via the SELF-ARM loot-to-arm
        // hunt is the prompt's TOP priority when unarmed). Same scene that fires the
        // nudge when armed (monster in view, finished batch) stays silent unarmed.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: true, armed: false)));
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, hasContract: true, contractStage: 3u, armed: false)));
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
        // No monster AND no held contract batch -> the no-monster LOOP-BREAK rule
        // already drives Talking un-talked npcs in a town to find a first source;
        // this nudge stays off to avoid duplicating it (the no-monster arm
        // requires a HELD finished batch, not a fresh character).
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: false, npcVisible: true)));
    }

    [Fact]
    public void Nudge_Present_WhenNoMonsterButHeldBatchAllDone_Vendor()
    {
        // The common town-refresh case: NO monster, but the bot holds a FINISHED
        // batch (every tracked contract stage 3) and an unbrowsed vendor source
        // is in view. The nudge fires so the bot re-engages the source for a
        // fresh batch WITHOUT waiting for the >5min LOOP-BREAK town-dwell timer.
        Assert.Contains(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: false,
                contractStages: new uint[] { 3u, 3u })));
    }

    [Fact]
    public void Nudge_Present_WhenNoMonsterButHeldBatchAllDone_Npc()
    {
        // Same refresh case via a dialog-NPC source: NO monster, a FINISHED batch,
        // and an un-talked npc in view -> fire so the bot Talks it to seek a fresh
        // task instead of idling/leaving before the town-dwell timer elapses.
        Assert.Contains(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: false,
                npcVisible: true, contractStages: new uint[] { 3u, 3u })));
    }

    [Fact]
    public void Nudge_Absent_WhenNoMonsterAndActiveContract()
    {
        // NO monster + a source in view, but an ACTIONABLE contract (stage 2)
        // remains -> pursue it; the no-monster arm needs a fully DONE batch, so
        // it must NOT nag here.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: true, monsterVisible: false, hasContract: true)));
    }

    [Fact]
    public void Nudge_Absent_WhenNoMonsterAndContractsMixedDoneAndInProgress()
    {
        // NO monster + an un-talked npc, but one contract is still in progress
        // (stage 2) alongside a done one -> an actionable contract remains, so the
        // no-monster refresh arm stays off.
        Assert.DoesNotContain(NudgeMarker,
            Prompt(World(vendorVisible: false, monsterVisible: false,
                npcVisible: true, contractStages: new uint[] { 3u, 2u })));
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

    // ---- BuildContractSourceDiagKey (cp034 diagnostic, behavior-preserving) ----
    // The key records, for a finished batch, whether a contract SOURCE is
    // actionable in view (ruleFires) and the nearest task-giver proxy npc.

    [Fact]
    public void DiagKey_NpcInView_RuleFires_ReportsNearestNpc()
    {
        var w = World(vendorVisible: false, npcVisible: true,
            contractStages: new uint[] { 3u, 3u });
        var key = LlmGoalPolicy.BuildContractSourceDiagKey(
            w, untalkedNpcInView: true, vendorInView: false);
        Assert.Contains("doneBatch=2", key);
        Assert.Contains("ruleFires=True", key);
        Assert.Contains("nearestNpc=\"Townsperson\"@9.0", key);
    }

    [Fact]
    public void DiagKey_NoSourceInView_RuleDoesNotFire_NearestNone()
    {
        var w = World(vendorVisible: false, npcVisible: false,
            contractStages: new uint[] { 3u, 3u });
        var key = LlmGoalPolicy.BuildContractSourceDiagKey(
            w, untalkedNpcInView: false, vendorInView: false);
        Assert.Contains("ruleFires=False", key);
        Assert.Contains("nearestNpc=none", key);
    }

    [Fact]
    public void DiagKey_VendorPanelOpen_RuleDoesNotFire()
    {
        // An OPEN vendor panel means the wares are already shown -> the vendor arm
        // (`vendorInView && Vendor is null`) is false, so the rule does not fire.
        var w = World(vendorVisible: true, vendorPanelOpen: true,
            contractStages: new uint[] { 3u, 3u });
        var key = LlmGoalPolicy.BuildContractSourceDiagKey(
            w, untalkedNpcInView: false, vendorInView: true);
        Assert.Contains("vendorPanelOpen=True", key);
        Assert.Contains("ruleFires=False", key);
    }

    [Fact]
    public void DiagKey_VendorPanelClosed_RuleFires()
    {
        var w = World(vendorVisible: true, vendorPanelOpen: false,
            contractStages: new uint[] { 3u, 3u });
        var key = LlmGoalPolicy.BuildContractSourceDiagKey(
            w, untalkedNpcInView: false, vendorInView: true);
        Assert.Contains("vendorPanelOpen=False", key);
        Assert.Contains("ruleFires=True", key);
    }

    [Fact]
    public void DiagKey_NearestNpcProxy_ExcludesMonsters()
    {
        // A monster (8f) is closer than the npc (9f); the task-giver proxy must
        // skip the monster and report the npc.
        var w = World(vendorVisible: false, npcVisible: true, monsterVisible: true,
            contractStages: new uint[] { 3u });
        var key = LlmGoalPolicy.BuildContractSourceDiagKey(
            w, untalkedNpcInView: true, vendorInView: false);
        Assert.Contains("nearestNpc=\"Townsperson\"@9.0", key);
    }
}
