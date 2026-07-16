// SPDX-License-Identifier: AGPL-3.0-or-later
// Contract turn-in / objective-area LOCATION perception. The dat ContractTable
// carries each contract's NPCEnd and quest-area positions; this surfaces them in
// the ## Contracts capsule as a bearing+distance from the bot (actionable via an
// Explore `direction`) so it can travel to complete/turn in a contract. Raw dat
// facts; the LLM decides whether to travel. These tests lock the projection
// carry and the capsule bearing rendering.

using System;
using System.Collections.Generic;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ContractLocationTests
{
    private const uint SelfGuid = 0x5000000B;
    private const uint SelfCell = 0xAAB50003u;
    private static readonly Vector3 SelfPos = new(5f, 96f, 0f);

    private static WorldStateProjection WorldWith(ContractProjection contract) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = SelfCell,
            PositionX = SelfPos.X, PositionY = SelfPos.Y, PositionZ = SelfPos.Z, HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = Array.Empty<VisibleObjectProjection>(),
        Contracts = new[] { contract },
    };

    private static string Section(string prompt, string header)
    {
        int start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int next = prompt.IndexOf("\n## ", start + header.Length, StringComparison.Ordinal);
        return next < 0 ? prompt.Substring(start) : prompt.Substring(start, next - start);
    }

    [Fact]
    public void Capsule_RendersTurnInBearing_DueEast()
    {
        var (sgx, sgy) = AcCoords.ToGlobalXY(SelfCell, SelfPos);
        var contract = new ContractProjection
        {
            ContractId = 700u, Stage = 3u, Name = "Slay the Drudges", NpcEnd = "Sergeant",
            TurnInWorldX = sgx + 100f, TurnInWorldY = sgy, // ~100u due east (+X)
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWith(contract), new EventStream(), null),
            "## Contracts");

        Assert.Contains("turn-in NPC: Sergeant", cap);
        Assert.Contains("turn-in location: ~100u E from you", cap);
    }

    [Fact]
    public void Capsule_RendersObjectiveAreaBearing_DueNorth()
    {
        var (sgx, sgy) = AcCoords.ToGlobalXY(SelfCell, SelfPos);
        var contract = new ContractProjection
        {
            ContractId = 701u, Stage = 2u, Name = "Hunt",
            QuestAreaWorldX = sgx, QuestAreaWorldY = sgy + 50f, // ~50u due north (+Y)
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWith(contract), new EventStream(), null),
            "## Contracts");

        Assert.Contains("objective area: ~50u N from you", cap);
    }

    private static WorldStateProjection WorldWithContractAndVisible(
        ContractProjection contract, params VisibleObjectProjection[] visible)
        => WorldWithContractAndVisibleArmed(contract, true, visible);

    private static WorldStateProjection WorldWithContractAndVisibleArmed(
        ContractProjection contract, bool armed, params VisibleObjectProjection[] visible) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = SelfCell,
            PositionX = SelfPos.X, PositionY = SelfPos.Y, PositionZ = SelfPos.Z, HealthFraction = 1.0f,
        },
        Inventory = armed
            ? new[] { new InventoryItemProjection { Guid = 0x7E1u, Name = "Spadone", Wcid = 1u, ItemType = 0x1u, WieldedAt = 0x02000000u } }
            : Array.Empty<InventoryItemProjection>(),
        Visible = visible,
        Contracts = new[] { contract },
    };

    private static VisibleObjectProjection ShopVendor() => new()
    { Guid = 0x900u, Name = "Shopkeeper", IsVendor = true, Distance = 5f, IsMonster = false, IsCorpse = false };

    private const string RefreshCueMarker = "fresh contract to keep earning is BOUGHT at a `vendor`";

    [Fact]
    public void Capsule_AllDoneBatch_WithVendorInView_SurfacesRefreshBuyCue()
    {
        var done = new ContractProjection { ContractId = 1u, Stage = 3u, Name = "Done" };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContractAndVisible(done, ShopVendor()), new EventStream(), null);
        Assert.Contains(RefreshCueMarker, prompt);
        // Lock the SALIENCE placement: the cue must live in the real ## Contracts
        // capsule (which opens with "- tracked objectives"), not only somewhere in the
        // prompt body — so a future refactor that moves it elsewhere fails this test.
        var capsuleStart = prompt.IndexOf("- tracked objectives (stage code", StringComparison.Ordinal);
        Assert.True(capsuleStart >= 0, "## Contracts capsule header not found");
        Assert.True(prompt.IndexOf(RefreshCueMarker, capsuleStart, StringComparison.Ordinal) >= 0,
            "refresh cue must appear inside the ## Contracts capsule");
    }

    [Fact]
    public void Capsule_AllDoneBatch_NoVendorInView_OmitsRefreshBuyCue()
    {
        var done = new ContractProjection { ContractId = 1u, Stage = 3u, Name = "Done" };
        var prompt = LlmGoalPolicy.BuildUserPrompt(WorldWith(done), new EventStream(), null);
        Assert.DoesNotContain(RefreshCueMarker, prompt);
    }

    [Fact]
    public void Capsule_AllDoneBatch_WithVendorInView_Unarmed_OmitsRefreshBuyCue()
    {
        // Combat-readiness gate: the refresh-BUY cue is suppressed when UNARMED — a
        // fresh contract is useless until the bot can fight; arming comes first.
        var done = new ContractProjection { ContractId = 1u, Stage = 3u, Name = "Done" };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContractAndVisibleArmed(done, armed: false, ShopVendor()), new EventStream(), null);
        Assert.DoesNotContain(RefreshCueMarker, prompt);
    }

    [Fact]
    public void Capsule_UnfinishedContract_WithVendorInView_OmitsRefreshBuyCue()
    {
        // Stage 2 (in progress) -> batch is NOT finished -> no refresh cue.
        var inProgress = new ContractProjection { ContractId = 1u, Stage = 2u, Name = "Active" };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContractAndVisible(inProgress, ShopVendor()), new EventStream(), null);
        Assert.DoesNotContain(RefreshCueMarker, prompt);
    }

    [Fact]
    public void Capsule_OmitsBearing_WhenDatHasNoLocation()
    {
        var contract = new ContractProjection
        {
            ContractId = 702u, Stage = 1u, Name = "No Location", NpcEnd = "Giver",
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWith(contract), new EventStream(), null),
            "## Contracts");

        Assert.Contains("turn-in NPC: Giver", cap);
        Assert.DoesNotContain("turn-in location:", cap);
        Assert.DoesNotContain("objective area:", cap);
    }

    [Fact]
    public void FromWorldState_CarriesContractLocationsFromCatalog()
    {
        var catalog = new ContractCatalog(new Dictionary<uint, ContractInfo>
        {
            [555u] = new ContractInfo(555u, "Kill Task", "Slay 5 drudges", "", "Giver", "Taker",
                TurnInWorldX: 12345f, TurnInWorldY: 67890f,
                QuestAreaWorldX: 100f, QuestAreaWorldY: 200f),
        });
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        ws.ApplyContractTable(new ContractTrackerTablePayload(
            new[] { new ContractTrackerEntry(1u, 555u, 3u, 0.0, 0.0) }));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null, contractCatalog: catalog);

        Assert.NotNull(proj);
        var c = Assert.Single(proj!.Contracts);
        Assert.Equal(12345f, c.TurnInWorldX);
        Assert.Equal(67890f, c.TurnInWorldY);
        Assert.Equal(100f, c.QuestAreaWorldX);
        Assert.Equal(200f, c.QuestAreaWorldY);
    }

    [Fact]
    public void FromWorldState_NoCatalog_LeavesLocationsNull()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid);
        ws.ApplyContractTable(new ContractTrackerTablePayload(
            new[] { new ContractTrackerEntry(1u, 999u, 2u, 0.0, 0.0) }));

        var proj = WorldStateProjection.FromWorldState(ws, weenies: null);

        var c = Assert.Single(proj!.Contracts);
        Assert.Null(c.TurnInWorldX);
        Assert.Null(c.QuestAreaWorldX);
    }

    [Fact]
    public void QuestCompiler_Stage3Contract_TeachesHandInOnceThenStop()
    {
        // Regression guard for the live turn-in loop: with a stage-3 (done)
        // contract held, the QUEST-DIALOG COMPILER rule must teach the bot to
        // attempt the hand-in ONCE and then, if the contract stays stage 3,
        // treat it as finished and stop re-Talking the turn-in NPC (some
        // contracts have no separate hand-in; re-attempting is fixation). The
        // rule is gated on a non-null stack, so pass an (empty) IntentStack.
        var contract = new ContractProjection
        {
            ContractId = 700u, Stage = 3u, Name = "Locate the Sergeant", NpcEnd = "Sergeant",
        };

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWith(contract), new EventStream(), null, new IntentStack());

        Assert.Contains("Attempt that hand-in ONCE", prompt);
        // The hand-in attempt is conditioned: only a turn-in made AFTER the
        // contract reached stage 3 counts (an earlier acceptance/objective Talk
        // with the same NPC must NOT trigger the egress), so a contract that
        // really clears on a final hand-in keeps its one real attempt.
        Assert.Contains("AFTER the contract reached", prompt);
        // Persistence: the bot marks the turn-in intent blocked (a durable stack
        // marker) instead of popping, so the stateless rule cannot re-compile it
        // once the Talk ages out of history (the macro "amnesia" loop).
        Assert.Contains("MARK_TOP_BLOCKED", prompt);
        Assert.Contains("fixation, not progress", prompt);
    }

    [Fact]
    public void QuestCompiler_Stage3TalkFailure_DefersToFinalRetryException()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var contract = new ContractProjection
        {
            ContractId = 701u, Stage = 3u, Name = "Locate the Sergeant",
            NpcEnd = "Sergeant", Stage3SinceUtc = since,
        };
        var events = WithTalkGoals("Sergeant", 1, since.AddSeconds(1));
        events.Append(new StreamEvent
        {
            Sequence = -1,
            Utc = since.AddSeconds(2),
            Kind = EventKind.GoalFailed,
            Name = "Sergeant",
            Text = "Talk: interaction target out of reach",
        });

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWith(contract), events, null, new IntentStack());

        Assert.Contains("Attempt that hand-in ONCE", prompt);
        Assert.Contains("## Recent goal outcomes", prompt);
        Assert.Contains("Talk: interaction target out of reach", prompt);
        Assert.Contains(
            "this FINAL check is authoritative over earlier generic", prompt);
        Assert.Contains(
            "A rejected or out-of-reach emission did not consume the successful attempt", prompt);
        Assert.Contains(
            "proves the earlier Talk never dispatched/reached", prompt);
    }

    // Append N Talk goals aimed at npcName (stamped at `at`) to an EventStream,
    // in the exact "Talk target=name=\"X\" item= source=..." shape the executor
    // records, so the ## Contracts done-detector counts them.
    private static EventStream WithTalkGoals(string npcName, int times, DateTimeOffset at)
    {
        var es = new EventStream();
        for (int i = 0; i < times; i++)
            es.Append(new StreamEvent
            {
                Sequence = -1,
                Utc = at,
                Kind = EventKind.GoalEmitted,
                Text = $"Talk target=name=\"{npcName}\" item= source=llm:test",
            });
        return es;
    }

    private static EventStream WithExploreGoals(string targetName, int times, DateTimeOffset at)
    {
        var es = new EventStream();
        for (int i = 0; i < times; i++)
            es.Append(new StreamEvent
            {
                Sequence = -1,
                Utc = at,
                Kind = EventKind.GoalEmitted,
                Text = $"Explore target=name=\"{targetName}\" item= source=llm:test",
            });
        return es;
    }

    private static WorldStateProjection WorldWithContracts(params ContractProjection[] contracts) => new()
    {
        Self = new SelfProjection
        {
            Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = SelfCell,
            PositionX = SelfPos.X, PositionY = SelfPos.Y, PositionZ = SelfPos.Z, HealthFraction = 1.0f,
        },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = Array.Empty<VisibleObjectProjection>(),
        Contracts = contracts,
    };

    [Fact]
    public void Capsule_Stage3History_SurfacesRawTalkCount()
    {
        // Keep the bot's own post-transition emissions available without deriving
        // a source-owned disposition from them.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 800u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithTalkGoals("Pathwarden Thorolf", 4, since), null);
        var cap = Section(prompt, "## Contracts");

        Assert.Contains(
            "post-stage-3 goal history for Pathwarden Thorolf: Talk=4, Explore=0", cap);
        Assert.Contains("evidence only", cap);
        Assert.Contains("## FINAL STAGE-3 VERB CHECK", prompt);
        Assert.Contains("If that row says `Talk=1` or more, another Talk to that same NPC is INVALID", prompt);
        Assert.Contains("goal.kind MUST NOT be `Talk`, `Use`, or `Give` for that NPC", prompt);
        Assert.Contains("the rationale MUST quote the observed `Talk=N`", prompt);
        Assert.Contains("Source does not enforce this check or veto your goal", prompt);
        Assert.DoesNotContain("no separate hand-in", cap);
    }

    [Fact]
    public void Capsule_MultipleStage3Histories_FinalAuditChecksEveryTargetAndGiveItem()
    {
        var since = DateTimeOffset.UtcNow;
        var first = new ContractProjection
        {
            ContractId = 810u, Stage = 3u, Name = "First",
            NpcEnd = "First Contact", Stage3SinceUtc = since,
        };
        var second = new ContractProjection
        {
            ContractId = 811u, Stage = 3u, Name = "Second",
            NpcEnd = "Second Contact", Stage3SinceUtc = since,
        };
        var events = new EventStream();
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = since.AddSeconds(1), Kind = EventKind.GoalEmitted,
            Text = "Talk target=name=\"First Contact\" item= source=llm:test",
        });
        events.Append(new StreamEvent
        {
            Sequence = -1, Utc = since.AddSeconds(2), Kind = EventKind.GoalEmitted,
            Text = "Talk target=name=\"Second Contact\" item= source=llm:test",
        });

        var world = WorldWithContracts(first, second);
        Assert.Empty(world.Inventory);
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            world, events, null, new IntentStack());
        var audit = Section(prompt, "## FINAL STAGE-3 VERB CHECK");

        Assert.Contains("post-stage-3 goal history for First Contact: Talk=1", prompt);
        Assert.Contains("post-stage-3 goal history for Second Contact: Talk=1", prompt);
        Assert.Contains("FINAL RESPONSE AUDIT", audit);
        Assert.Contains("compare its target name against EVERY", audit);
        Assert.Contains("A contract tracker id/name/objective is NOT held-item evidence", audit);
        Assert.Contains("DO NOT emit that candidate or invent an unobserved prerequisite intent", audit);
    }

    [Fact]
    public void Capsule_Stage3WithoutTurnInNpc_StillRendersTransitionTime()
    {
        var since = new DateTimeOffset(2026, 7, 16, 1, 2, 3, TimeSpan.Zero);
        var contract = new ContractProjection
        {
            ContractId = 849u, Stage = 3u, Name = "Unattributed",
            NpcEnd = null, Stage3SinceUtc = since,
        };

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), new EventStream(), null);
        var cap = Section(prompt, "## Contracts");

        Assert.Contains($"wire stage 3 first observed at {since:O}", cap);
        Assert.DoesNotContain("post-stage-3 goal history", cap);
        Assert.DoesNotContain("## FINAL STAGE-3 VERB CHECK", prompt);
    }

    [Fact]
    public void Stage3History_TalksRenderRawEvidenceWithoutSettledCue()
    {
        // Repeated Talk emissions remain visible without a source-owned disposition.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 850u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = since,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithTalkGoals("Buckminster", 3, since), null);
        Assert.DoesNotContain("## Settled contract — no turn-in", prompt);
        Assert.Contains("post-stage-3 goal history for Buckminster: Talk=3, Explore=0", prompt);
    }

    [Fact]
    public void Stage3History_SingleTalkAlsoRendersRawEvidenceOnly()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 851u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = since,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithTalkGoals("Buckminster", 1, since), null);
        Assert.DoesNotContain("## Settled contract — no turn-in", prompt);
    }

    [Fact]
    public void Stage3History_OmittedWhenNoContracts()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(), new EventStream(), null);
        Assert.DoesNotContain("## Settled contract — no turn-in", prompt);
    }

    [Fact]
    public void Stage3History_ExploresRenderRawEvidenceWithoutSettledCue()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        { ContractId = 863u, Stage = 3u, Name = "Find the Barkeeper", NpcEnd = "Buckminster", Stage3SinceUtc = since };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithExploreGoals("Buckminster", 3, since), null);
        Assert.DoesNotContain("## Settled contract — no turn-in", prompt);
        Assert.Contains("post-stage-3 goal history for Buckminster: Talk=0, Explore=3", prompt);
    }

    [Fact]
    public void Stage3History_MultipleContractsEachRenderRawEvidence()
    {
        var since = DateTimeOffset.UtcNow;
        var c1 = new ContractProjection
        { ContractId = 860u, Stage = 3u, Name = "Find the Pathwarden", NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since };
        var c2 = new ContractProjection
        { ContractId = 861u, Stage = 3u, Name = "Find the Barkeeper", NpcEnd = "Buckminster", Stage3SinceUtc = since };
        var es = new EventStream();
        AppendTalkGoals(es, "Pathwarden Thorolf", 3, since);
        AppendTalkGoals(es, "Buckminster", 3, since.AddSeconds(1)); // newer -> the live target

        var prompt = LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(c1, c2), es, null);

        Assert.DoesNotContain("## Settled contract — no turn-in", prompt);
        Assert.Contains(
            "post-stage-3 goal history for Pathwarden Thorolf: Talk=3, Explore=0", prompt);
        Assert.Contains("post-stage-3 goal history for Buckminster: Talk=3, Explore=0", prompt);
    }

    private static void AppendTalkGoals(EventStream es, string npcName, int times, DateTimeOffset at)
    {
        for (int i = 0; i < times; i++)
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = at, Kind = EventKind.GoalEmitted,
                Text = $"Talk target=name=\"{npcName}\" item= source=llm:test",
            });
    }

    [Fact]
    public void Stage3History_RemainsRawAfterNewerNonInteractionGoal()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        { ContractId = 862u, Stage = 3u, Name = "Find the Barkeeper", NpcEnd = "Buckminster", Stage3SinceUtc = since };
        var es = new EventStream();
        AppendTalkGoals(es, "Buckminster", 3, since);
        es.Append(new StreamEvent
        {
            Sequence = -1, Utc = since.AddSeconds(1), Kind = EventKind.GoalEmitted,
            Text = "Attack target=name=\"Drudge\" item= source=llm:test", // newer, non-interaction
        });

        var prompt = LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), es, null);

        Assert.DoesNotContain("## Settled contract — no turn-in", prompt);
        Assert.Contains("post-stage-3 goal history for Buckminster: Talk=3, Explore=0", prompt);
    }

    [Fact]
    public void Capsule_Stage3ExploreHistory_SurfacesRawCount()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 807u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = since,
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(contract), WithExploreGoals("Buckminster", 3, since), null),
            "## Contracts");

        Assert.Contains("post-stage-3 goal history for Buckminster: Talk=0, Explore=3", cap);
    }

    [Fact]
    public void Capsule_Stage3TwoExplores_SurfacesExactRawCount()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 809u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(contract), WithExploreGoals("Pathwarden Thorolf", 2, since), null),
            "## Contracts");

        Assert.Contains("post-stage-3 goal history for Pathwarden Thorolf: Talk=0, Explore=2", cap);
    }

    [Fact]
    public void Capsule_Stage3SingleTalk_SurfacesExactRawCount()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 801u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(contract), WithTalkGoals("Pathwarden Thorolf", 1, since), null),
            "## Contracts");

        Assert.Contains("post-stage-3 goal history for Pathwarden Thorolf: Talk=1, Explore=0", cap);
        Assert.Contains("turn-in NPC: Pathwarden Thorolf", cap);
    }

    [Fact]
    public void Capsule_Stage3TalkAndExplore_SurfacesSeparateRawCounts()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 808u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };
        var es = new EventStream();
        es.Append(new StreamEvent
        { Sequence = -1, Utc = since, Kind = EventKind.GoalEmitted,
          Text = "Explore target=name=\"Pathwarden Thorolf\" item= source=llm:test" });
        es.Append(new StreamEvent
        { Sequence = -1, Utc = since, Kind = EventKind.GoalEmitted,
          Text = "Talk target=name=\"Pathwarden Thorolf\" item= source=llm:test" });

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), es, null),
            "## Contracts");

        Assert.Contains("post-stage-3 goal history for Pathwarden Thorolf: Talk=1, Explore=1", cap);
    }

    [Fact]
    public void Capsule_Stage3History_ExcludesGoalsBeforeTransition()
    {
        // Talks made BEFORE the contract became stage 3 (acceptance/locating the
        // NPC) must NOT count toward hand-in attempts — only post-completion
        // Talks do, so a contract still gets its one real hand-in attempt.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 803u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract),
            WithTalkGoals("Pathwarden Thorolf", 4, since.AddMinutes(-5)), null);
        var cap = Section(prompt, "## Contracts");

        Assert.Contains("post-stage-3 goal history for Pathwarden Thorolf: Talk=0, Explore=0", cap);
        Assert.Contains("If that row says `Talk=0`, one post-transition hand-in Talk may be tried", prompt);
    }

    [Fact]
    public void Capsule_Stage3SharedTurnIn_RendersRawHistoryForEachRow()
    {
        // Two stage-3 contracts sharing one turn-in NPC make per-contract Talk
        // attribution ambiguous, so the done note must not fire for either.
        var since = DateTimeOffset.UtcNow;
        var a = new ContractProjection
        {
            ContractId = 804u, Stage = 3u, Name = "Task A", NpcEnd = "Hub Giver", Stage3SinceUtc = since,
        };
        var b = new ContractProjection
        {
            ContractId = 805u, Stage = 3u, Name = "Task B", NpcEnd = "Hub Giver", Stage3SinceUtc = since,
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(a, b), WithTalkGoals("Hub Giver", 5, since), null),
            "## Contracts");

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            cap, "post-stage-3 goal history for Hub Giver: Talk=5, Explore=0").Count);
    }

    [Fact]
    public void Capsule_Stage3_NpcNameContainingItemToken_StillCounts()
    {
        // Regex robustness: a turn-in NPC name that itself contains " item=" must
        // not break the Talk-goal parse (a bounded target=(.*?) item= regex would
        // truncate it). Unusual but a correctness guard.
        var since = DateTimeOffset.UtcNow;
        const string oddName = "Quartermaster item= Depot";
        var contract = new ContractProjection
        {
            ContractId = 806u, Stage = 3u, Name = "Odd", NpcEnd = oddName, Stage3SinceUtc = since,
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(contract), WithTalkGoals(oddName, 3, since), null),
            "## Contracts");

        Assert.Contains($"post-stage-3 goal history for {oddName}: Talk=3, Explore=0", cap);
    }

    [Fact]
    public void Capsule_InProgressContract_OmitsPostStage3History()
    {
        // The done-note is for stage-3 contracts ONLY: an in-progress (stage 2)
        // contract must never be marked done regardless of Talk history.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 802u, Stage = 2u, Name = "Hunt", NpcEnd = "Sergeant",
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(contract), WithTalkGoals("Sergeant", 5, since), null),
            "## Contracts");

        Assert.DoesNotContain("post-stage-3 goal history", cap);
    }

    // ---- raw stage-3 contract evidence ----

    [Fact]
    public void Stage3History_TwoTalksRenderRawCount()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 900u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithTalkGoals("Pathwarden Thorolf", 2, since), null);
        Assert.Contains(
            "post-stage-3 goal history for Pathwarden Thorolf: Talk=2, Explore=0", prompt);
    }

    [Fact]
    public void Stage3History_OneTalkRendersRawCount()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 901u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithTalkGoals("Pathwarden Thorolf", 1, since), null);
        Assert.Contains(
            "post-stage-3 goal history for Pathwarden Thorolf: Talk=1, Explore=0", prompt);
    }

    [Fact]
    public void Stage3History_RovingTargetsRenderBothRawCounts()
    {
        var since = DateTimeOffset.UtcNow;
        var pathwarden = new ContractProjection
        {
            ContractId = 902u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };
        var barkeeper = new ContractProjection
        {
            ContractId = 903u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = since,
        };
        var es = new EventStream();
        foreach (var n in new[] { "Pathwarden Thorolf", "Buckminster", "Pathwarden Thorolf", "Buckminster" })
            es.Append(new StreamEvent
            {
                Sequence = -1, Utc = since, Kind = EventKind.GoalEmitted,
                Text = $"Talk target=name=\"{n}\" item= source=llm:test",
            });
        var world = WorldWithContracts(pathwarden, barkeeper);
        var prompt = LlmGoalPolicy.BuildUserPrompt(world, es, null);
        Assert.Contains(
            "post-stage-3 goal history for Pathwarden Thorolf: Talk=2, Explore=0", prompt);
        Assert.Contains("post-stage-3 goal history for Buckminster: Talk=2, Explore=0", prompt);
    }

    [Fact]
    public void Stage3History_RendersAlongsideLiveContract()
    {
        var since = DateTimeOffset.UtcNow;
        var settled = new ContractProjection
        {
            ContractId = 904u, Stage = 3u, Name = "Old", NpcEnd = "Broker", Stage3SinceUtc = since,
        };
        var fresh = new ContractProjection
        {
            ContractId = 905u, Stage = 1u, Name = "New", NpcStart = "Broker",
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(settled, fresh), WithTalkGoals("Broker", 5, since), null);
        Assert.Contains("post-stage-3 goal history for Broker: Talk=5, Explore=0", prompt);
    }

    [Fact]
    public void InProgressContract_DoesNotRenderStage3History()
    {
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 906u, Stage = 2u, Name = "Hunt", NpcEnd = "Sergeant",
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithTalkGoals("Sergeant", 5, since), null);
        Assert.DoesNotContain("post-stage-3 goal history", prompt);
    }

    [Fact]
    public void Stage3History_SharedTurnInRendersEachRawRow()
    {
        var since = DateTimeOffset.UtcNow;
        var a = new ContractProjection
        {
            ContractId = 907u, Stage = 3u, Name = "A", NpcEnd = "Hub Giver", Stage3SinceUtc = since,
        };
        var b = new ContractProjection
        {
            ContractId = 908u, Stage = 3u, Name = "B", NpcEnd = "Hub Giver", Stage3SinceUtc = since,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(a, b), WithTalkGoals("Hub Giver", 5, since), null);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            prompt, "post-stage-3 goal history for Hub Giver: Talk=5, Explore=0").Count);
    }

    [Fact]
    public void Stage3History_UsesEachContractTransitionTime()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t1 = DateTimeOffset.UtcNow;
        var a = new ContractProjection
        {
            ContractId = 909u, Stage = 3u, Name = "Locate", NpcEnd = "Broker", Stage3SinceUtc = t0,
        };
        var b = new ContractProjection
        {
            ContractId = 910u, Stage = 3u, Name = "Kill", NpcStart = "Broker", NpcEnd = "Sergeant",
            Stage3SinceUtc = t1,
        };
        // Two Talks to Broker made WHILE b was still live business (between t0 and t1).
        var es = WithTalkGoals("Broker", 2, t0.AddMinutes(1));
        var prompt = LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(a, b), es, null);
        Assert.Contains("post-stage-3 goal history for Broker: Talk=2, Explore=0", prompt);
    }

    [Fact]
    public void Stage3History_CountsGoalsAfterTransition()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var a = new ContractProjection
        {
            ContractId = 911u, Stage = 3u, Name = "Locate", NpcEnd = "Broker", Stage3SinceUtc = t0,
        };
        var b = new ContractProjection
        {
            ContractId = 912u, Stage = 3u, Name = "Kill", NpcStart = "Broker", NpcEnd = "Sergeant",
            Stage3SinceUtc = t1,
        };
        var es = WithTalkGoals("Broker", 2, t1.AddMinutes(1)); // 2 Talks AFTER full settle
        var prompt = LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(a, b), es, null);
        Assert.Contains("post-stage-3 goal history for Broker: Talk=2, Explore=0", prompt);
    }

    // ── stage-3 objective remains raw evidence ─────────────────────────────

    [Fact]
    public void Capsule_Stage3Objective_RendersWithoutSourceDisposition()
    {
        // A stage-3 (DoneOrPendingRepeat) contract's objective is complete the
        // moment it reaches stage 3 — the qualifier must appear even in a fresh
        // world with NO prior turn-in/locate pursuit (the separate DONE note is
        // gated on over-pursuit; this one is not).
        var contract = new ContractProjection
        {
            ContractId = 800u, Stage = 3u, Name = "Locate", Description = "Locate the contact in the tavern.",
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWith(contract), new EventStream(), null),
            "## Contracts");

        Assert.Contains("objective: Locate the contact in the tavern.", cap);
        Assert.DoesNotContain("objective already satisfied", cap);
        Assert.DoesNotContain("do not pursue it", cap);
    }

    [Fact]
    public void Capsule_InProgressObjective_NotMarkedSatisfied()
    {
        // A non-stage-3 (still active) contract's objective must NOT carry the
        // satisfied qualifier — the bot should still pursue it.
        var contract = new ContractProjection
        {
            ContractId = 801u, Stage = 2u, Name = "Locate", Description = "Locate the contact in the tavern.",
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWith(contract), new EventStream(), null),
            "## Contracts");

        Assert.Contains("objective: Locate the contact in the tavern.", cap);
        Assert.DoesNotContain("already satisfied", cap);
    }

    [Fact]
    public void Capsule_MixedBatch_ActiveRowSurvivesAlongsideStage3Markers()
    {
        // Budget guard: a couple of stage-3 rows (each gaining the satisfied
        // qualifier) followed by a still-active row — the active objective must
        // still render (the short marker must not crowd it out of the capsule's
        // char budget for a small everyday batch).
        var done1 = new ContractProjection
        { ContractId = 810u, Stage = 3u, Name = "Locate", Description = "Locate the contact in the tavern." };
        var done2 = new ContractProjection
        { ContractId = 811u, Stage = 3u, Name = "Patrol", Description = "Patrol the eastern road for raiders." };
        var active = new ContractProjection
        { ContractId = 812u, Stage = 2u, Name = "Slay", Description = "Slay six marauders in the lowlands." };

        var world = new WorldStateProjection
        {
            Self = new SelfProjection
            {
                Guid = SelfGuid, Name = "Headless", Landblock = 0xAAB5u, CellId = SelfCell,
                PositionX = SelfPos.X, PositionY = SelfPos.Y, PositionZ = SelfPos.Z, HealthFraction = 1.0f,
            },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),
            Contracts = new[] { done1, done2, active },
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null),
            "## Contracts");

        Assert.Contains("objective: Locate the contact in the tavern.", cap);
        Assert.DoesNotContain("stage 3 done", cap);
        Assert.Contains("objective: Slay six marauders in the lowlands.", cap);
    }

    // ── ## Persistent objectives: raw contract associations ────────────────

    private static IntentStack StackWithFrame(string kind, string targetName, IntentLifecycle status)
    {
        var baseline = IntentBaseline.Capture(WorldWithContracts(), new EventStream(), DateTime.UtcNow);
        var stack = new IntentStack();
        stack.TryPush(new Intent
        {
            Id = "i-001", Kind = kind, Rationale = $"test:{kind}", TargetName = targetName,
            Status = status, Completion = new AlwaysFalsePredicate(), Baseline = baseline,
        });
        return stack;
    }

    [Fact]
    public void PersistentObjectives_Stage3ContractIntent_ReportsRawAssociation()
    {
        // The intent remains active. Its matching contract row is shown without a
        // source-owned instruction to block or replace it.
        var contract = new ContractProjection
        {
            ContractId = 880u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow,
        };
        var stack = StackWithFrame("quest:find-barkeeper", "Buckminster", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("raw contract match: id=880 wire-stage=3 relation=turn-in", cap);
        Assert.DoesNotContain("MARK_TOP_BLOCKED", cap);
    }

    [Fact]
    public void PersistentObjectives_ActiveStage3Match_RemainsActionableForStrategy()
    {
        var contract = new ContractProjection
        {
            ContractId = 887u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow,
        };
        var stack = StackWithFrame("quest:find-barkeeper", "Buckminster", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("raw contract match: id=887 wire-stage=3 relation=turn-in", cap);
        Assert.Contains("these are your OWN persistent objectives", cap);
        Assert.DoesNotContain("every objective above has reached a terminal state", cap);
    }

    [Fact]
    public void PersistentObjectives_FreshWorkIntentSameNpc_NotForbidden()
    {
        // Both raw NPC relations are preserved without inferring the intent's purpose.
        var contract = new ContractProjection
        {
            ContractId = 888u, Stage = 3u, Name = "Find the Barkeeper",
            NpcStart = "Buckminster", NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow,
        };
        var stack = StackWithFrame("get-fresh-contract", "Buckminster", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("raw contract match: id=888 wire-stage=3 relation=start+turn-in", cap);
        Assert.DoesNotContain("MARK_TOP_BLOCKED", cap);
    }

    [Fact]
    public void PersistentObjectives_AncestorAndTopEachReportRawContractMatch()
    {
        var contract = new ContractProjection
        { ContractId = 893u, Stage = 3u, Name = "Locate", NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow };
        var baseline = IntentBaseline.Capture(WorldWithContracts(), new EventStream(), DateTime.UtcNow);
        var stack = new IntentStack();
        stack.TryPush(new Intent
        {
            Id = "i-001", Kind = "quest:locate", TargetName = "Buckminster",
            Status = IntentLifecycle.Active, Completion = new AlwaysFalsePredicate(), Baseline = baseline,
        });
        stack.TryPush(new Intent
        {
            Id = "i-002", Kind = "quest:find-barkeeper", TargetName = "Buckminster",
            Status = IntentLifecycle.Active, Completion = new AlwaysFalsePredicate(), Baseline = baseline,
        });
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.DoesNotContain("MARK_TOP_BLOCKED", cap);
        Assert.DoesNotContain("ANCESTOR objective above", cap);
        var rawTags = System.Text.RegularExpressions.Regex.Matches(
            cap, "raw contract match: id=893 wire-stage=3 relation=turn-in").Count;
        Assert.Equal(2, rawTags);
    }

    [Fact]
    public void PersistentObjectives_ContractMatchedAncestor_DoesNotPrescribeTopMutation()
    {
        var contract = new ContractProjection
        { ContractId = 892u, Stage = 3u, Name = "Locate", NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow };
        var baseline = IntentBaseline.Capture(WorldWithContracts(), new EventStream(), DateTime.UtcNow);
        var stack = new IntentStack();
        stack.TryPush(new Intent
        {
            Id = "i-001", Kind = "quest:find-barkeeper", TargetName = "Buckminster",
            Status = IntentLifecycle.Active, Completion = new AlwaysFalsePredicate(), Baseline = baseline,
        });
        stack.TryPush(new Intent
        {
            Id = "i-002", Kind = "hunt", TargetName = "Drudge",
            Status = IntentLifecycle.Active, Completion = new AlwaysFalsePredicate(), Baseline = baseline,
        });
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("raw contract match: id=892 wire-stage=3 relation=turn-in", cap);
        Assert.Contains("these are your OWN persistent objectives", cap);
        Assert.DoesNotContain("MARK_TOP_BLOCKED", cap);
        Assert.DoesNotContain("ANCESTOR objective above", cap);
    }

    [Fact]
    public void PersistentObjectives_InProgressContractIntent_NotFlagged()
    {
        // An intent toward an NPC with a still-live (non-stage-3) contract must NOT be flagged
        // — that objective is genuinely unfinished and should still be pursued.
        var contract = new ContractProjection
        {
            ContractId = 881u, Stage = 2u, Name = "Find the Barkeeper", NpcEnd = "Buckminster",
        };
        var stack = StackWithFrame("quest:find-barkeeper", "Buckminster", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("raw contract match: id=881 wire-stage=2 relation=turn-in", cap);
        Assert.Contains("these are your OWN persistent objectives", cap);
    }

    [Fact]
    public void PersistentObjectives_NonContractIntentTarget_NotFlagged()
    {
        // A frame whose target is not any contract NPC is not flagged (no false positive).
        var contract = new ContractProjection
        {
            ContractId = 882u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow,
        };
        var stack = StackWithFrame("quest:explore", "Some Cave", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(contract), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.DoesNotContain("raw contract match:", cap);
    }

    [Fact]
    public void PersistentObjectives_Stage3MatchReportsRawWireStage()
    {
        // Raw association does not depend on prior goal emissions.
        var contract = new ContractProjection
        { ContractId = 883u, Stage = 3u, Name = "Locate", NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow };
        var world = WorldWithContracts(contract);
        var stack = StackWithFrame("quest:locate", "Buckminster", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(world, new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("raw contract match: id=883 wire-stage=3 relation=turn-in", cap);
    }

    [Fact]
    public void PersistentObjectives_MultipleContractMatchesReportEachRawStage()
    {
        // Every matching row is reported independently.
        var done = new ContractProjection
        { ContractId = 884u, Stage = 3u, Name = "Locate", NpcEnd = "Broker", Stage3SinceUtc = DateTimeOffset.UtcNow };
        var live = new ContractProjection
        { ContractId = 885u, Stage = 2u, Name = "Kill", NpcStart = "Broker", NpcEnd = "Sergeant" };
        var brokerStack = StackWithFrame("quest:broker", "Broker", IntentLifecycle.Active);
        var brokerCap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(done, live), new EventStream(), null, brokerStack),
            "## Persistent objectives");
        Assert.Contains("id=884 wire-stage=3 relation=turn-in", brokerCap);
        Assert.Contains("id=885 wire-stage=2 relation=start", brokerCap);

        var liveOnly = new ContractProjection
        { ContractId = 886u, Stage = 2u, Name = "Kill", NpcEnd = "Sergeant" };
        var sergeantStack = StackWithFrame("quest:kill", "Sergeant", IntentLifecycle.Active);
        var sergeantCap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(liveOnly), new EventStream(), null, sergeantStack),
            "## Persistent objectives");
        Assert.Contains("id=886 wire-stage=2 relation=turn-in", sergeantCap);
    }

    [Fact]
    public void PersistentObjectives_RawStageDoesNotDependOnTransitionTime()
    {
        // The wire stage is independent of whether a local transition time was recorded.
        var c = new ContractProjection
        { ContractId = 889u, Stage = 3u, Name = "Locate", NpcEnd = "Buckminster", Stage3SinceUtc = null };
        var stack = StackWithFrame("quest:locate", "Buckminster", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(c), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("id=889 wire-stage=3 relation=turn-in", cap);
    }

    [Fact]
    public void PersistentObjectives_SharedTurnInReportsBothRawRows()
    {
        // Two done contracts share the same NpcEnd -> attribution ambiguous -> not flagged.
        var a = new ContractProjection
        { ContractId = 890u, Stage = 3u, Name = "Locate", NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow };
        var b = new ContractProjection
        { ContractId = 891u, Stage = 3u, Name = "Deliver", NpcEnd = "Buckminster", Stage3SinceUtc = DateTimeOffset.UtcNow };
        var stack = StackWithFrame("quest:locate", "Buckminster", IntentLifecycle.Active);
        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(a, b), new EventStream(), null, stack),
            "## Persistent objectives");
        Assert.Contains("id=890 wire-stage=3 relation=turn-in", cap);
        Assert.Contains("id=891 wire-stage=3 relation=turn-in", cap);
    }
}
