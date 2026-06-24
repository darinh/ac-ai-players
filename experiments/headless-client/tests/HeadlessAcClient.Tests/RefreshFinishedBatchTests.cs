// SPDX-License-Identifier: AGPL-3.0-or-later
// refresh-finished-batch: the already-engaged-source case the FIND-A-KILL-TASK-
// SOURCE rule's un-talked / unbrowsed disjuncts MISS. When every tracked contract
// is DONE (stage 3, a finished batch) and the npc/vendor that ISSUED it (a done
// contract's start/turn-in NPC NAME) is in view but has already been talked/
// browsed, re-engaging THAT source is how a FRESH batch is obtained -- yet FIND
// would not re-nudge it. DoneBatchSourceInViewToRefresh recognizes that source by
// name-matching a done contract's NpcStart/NpcEnd against a visible creature/
// vendor, BOUNDED by the bot's own recent re-engage count since the batch
// completed so a tapped source is not re-engaged forever.

using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RefreshFinishedBatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);
    private const string Issuer = "Broker";

    private static StreamEvent EngageGoal(string verb, string name, DateTimeOffset utc) => new()
    {
        Sequence = 0,
        Utc = utc,
        Kind = EventKind.GoalEmitted,
        Text = $"{verb} target=name=\"{name}\" item= source=llm",
    };

    private static WorldStateProjection World(
        uint[] stages,
        string? npcEnd = Issuer, string? npcStart = null,
        bool issuerVisible = true, bool issuerIsVendor = false,
        bool issuerAsPlainObject = false, DateTimeOffset? stage3Since = null,
        bool armed = true)
    {
        var visible = new List<VisibleObjectProjection>();
        if (issuerVisible)
            visible.Add(new VisibleObjectProjection
            {
                Guid = 0x9001u,
                Name = Issuer,
                // A dialog task-giver is a creature; a vendor sets IsVendor. A plain
                // object (container/sign) is neither -> must NOT match as a source.
                IsCreature = !issuerAsPlainObject && !issuerIsVendor,
                IsVendor = issuerIsVendor,
                Distance = 9f,
            });

        var contracts = new ContractProjection[stages.Length];
        for (int i = 0; i < contracts.Length; i++)
            contracts[i] = new ContractProjection
            {
                ContractId = (uint)(i + 1),
                Stage = stages[i],
                NpcStart = npcStart,
                NpcEnd = npcEnd,
                Stage3SinceUtc = stages[i] == 3u ? stage3Since : null,
            };

        return new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = 0x5000000Eu, Name = "Headless", Landblock = 0xAAB5u, CellId = 0xAAB50003u,
                PositionX = 1f, PositionY = 2f, PositionZ = 3f, HealthFraction = 1.0f,
            },
            Inventory = armed
                ? new[] { new InventoryItemProjection { Guid = 0x7E1u, Name = "Spadone", Wcid = 1u, ItemType = 0x1u, WieldedAt = 0x02000000u } }
                : Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            Contracts = contracts,
        };
    }

    [Fact]
    public void Present_WhenAllDoneBatch_TurnInNpcInView()
    {
        Assert.True(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u, 3u }), new EventStream()));
    }

    [Fact]
    public void Present_ViaStartNpc_WhenVendorInView()
    {
        // The issuing (start) NPC can be a vendor; match it too.
        Assert.True(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, npcEnd: null, npcStart: Issuer, issuerIsVendor: true),
            new EventStream()));
    }

    [Fact]
    public void Absent_WhenBatchNotAllDone()
    {
        // One contract still in progress (stage 2) -> not a finished batch.
        Assert.False(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u, 2u }), new EventStream()));
    }

    [Fact]
    public void Absent_WhenNoContracts()
    {
        Assert.False(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(Array.Empty<uint>()), new EventStream()));
    }

    [Fact]
    public void Absent_WhenIssuerNotInView()
    {
        Assert.False(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, issuerVisible: false), new EventStream()));
    }

    [Fact]
    public void Absent_WhenSourceNameMatchesNonCreatureObject()
    {
        // A visible object NAMED like the source but that is neither a creature nor
        // a vendor (a container/sign) is not a task source -> no match.
        Assert.False(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, issuerAsPlainObject: true), new EventStream()));
    }

    [Fact]
    public void Absent_WhenContractCarriesNoSourceName()
    {
        // A done batch whose contracts name no start/turn-in NPC has no anchor.
        Assert.False(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, npcEnd: null, npcStart: null), new EventStream()));
    }

    [Fact]
    public void Absent_WhenReEngagedAtThreshold()
    {
        // Two re-engages (Talk + Talk) since the batch completed -> tapped -> off.
        var es = new EventStream();
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(1)));
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(2)));
        Assert.False(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, stage3Since: T0), es));
    }

    [Fact]
    public void Present_WhenReEngagedBelowThreshold()
    {
        var es = new EventStream();
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(1)));
        Assert.True(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, stage3Since: T0), es));
    }

    [Fact]
    public void Absent_WhenReEngagedAtThreshold_MixedTalkAndUse()
    {
        // The bound counts BOTH verbs: a vendor source is re-engaged via Use, a
        // dialog source via Talk -> one of each still totals the threshold.
        var es = new EventStream();
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(1)));
        es.Append(EngageGoal("Use", Issuer, T0.AddSeconds(2)));
        Assert.False(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, stage3Since: T0), es));
    }

    [Fact]
    public void Present_WhenReEngagesPredateBatchCompletion()
    {
        // Re-engages BEFORE the batch became all-done (the stage-3 time) do not
        // count against the bound -- those were the original accept/hand-in, not
        // refresh attempts.
        var es = new EventStream();
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(-5)));
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(-2)));
        Assert.True(LlmGoalPolicy.DoneBatchSourceInViewToRefresh(
            World(new uint[] { 3u }, stage3Since: T0), es));
    }

    [Fact]
    public void Nudge_Present_WhenAlreadyTalkedIssuerInView()
    {
        // Prompt-level: the issuer is the ONLY source and is already-talked (so
        // untalkedNpcInView excludes it) with no vendor panel -> FIND fires SOLELY
        // via the refresh disjunct, and the REFRESH A FINISHED BATCH exception text
        // renders. The contradictory RETURN-TO-A-CONTRACT-SOURCE travel-back nudge
        // must NOT also fire: the issuer IS a source in view, so do not say "no
        // source in view" / "do not re-Talk the turn-in NPC" alongside the refresh.
        var world = World(new uint[] { 3u });
        var talked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Issuer };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), currentGoal: null, stack: null, pickerActivity: null,
            explorationCandidates: null, talkedNpcNames: talked);
        Assert.Contains("FIND A KILL-TASK SOURCE", prompt);
        Assert.Contains("REFRESH A FINISHED BATCH", prompt);
        Assert.DoesNotContain("RETURN TO A CONTRACT SOURCE", prompt);
    }

    [Fact]
    public void Nudge_FindAndRefresh_Absent_WhenUnarmed()
    {
        // Combat-readiness gate: the SAME finished-batch/issuer-in-view scene that
        // renders FIND + the REFRESH exception when armed is suppressed when UNARMED
        // (the bot cannot complete a kill-task; arming via SELF-ARM comes first).
        var world = World(new uint[] { 3u }, armed: false);
        var talked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Issuer };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), currentGoal: null, stack: null, pickerActivity: null,
            explorationCandidates: null, talkedNpcNames: talked);
        Assert.DoesNotContain("FIND A KILL-TASK SOURCE", prompt);
    }

    [Fact]
    public void Nudge_ReturnToSource_Absent_WhenTappedIssuerStillInView()
    {
        // Post-bound: the issuer is in view but its refresh bound is spent (>=2
        // re-engages), so the FIND refresh disjunct is OFF. RETURN-TO must STILL stay
        // off -- the source is physically in view (tapped, but present), so the bot
        // must not be told "no source in view, travel back". This is why the RETURN
        // gate uses the UNBOUNDED issuer-in-view check, not the bounded refresh one.
        var es = new EventStream();
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(1)));
        es.Append(EngageGoal("Talk", Issuer, T0.AddSeconds(2)));
        var world = World(new uint[] { 3u }, stage3Since: T0);
        var talked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Issuer };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, es, currentGoal: null, stack: null, pickerActivity: null,
            explorationCandidates: null, talkedNpcNames: talked);
        Assert.DoesNotContain("RETURN TO A CONTRACT SOURCE", prompt);
    }

    [Fact]
    public void Nudge_RefreshText_Absent_WhenNoFinishedBatch()
    {
        // No finished batch (a stage-2 contract in progress) -> the refresh
        // exception path is off (and FIND itself stays off: an actionable contract
        // is held).
        var world = World(new uint[] { 2u });
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, new EventStream(), currentGoal: null, stack: null, pickerActivity: null,
            explorationCandidates: null);
        Assert.DoesNotContain("REFRESH A FINISHED BATCH", prompt);
    }
}
