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
    public void Capsule_Stage3RepeatedTurnInAttempts_SurfacesDoneNoHandIn()
    {
        // The live criterion-2 blocker: a stage-3 contract whose turn-in NPC is
        // the located target, Talked repeatedly with no stage change. The capsule
        // must surface the bot's OWN post-completion attempt count + the "no
        // separate hand-in" fact so the LLM stops re-attempting it.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 800u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(contract), WithTalkGoals("Pathwarden Thorolf", 4, since), null),
            "## Contracts");

        Assert.Contains("DONE (stage 3, complete)", cap);
        Assert.Contains("Talked Pathwarden Thorolf 4x", cap);
        Assert.Contains("no separate hand-in", cap);
    }

    [Fact]
    public void Capsule_Stage3SingleAttempt_PreservesTurnIn()
    {
        // One (legitimate) post-completion hand-in attempt must NOT trigger the
        // done/no-hand-in note — a contract that really clears on a final Talk
        // keeps its one real attempt.
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

        Assert.DoesNotContain("DONE (stage 3, complete)", cap);
        Assert.Contains("turn-in NPC: Pathwarden Thorolf", cap);
    }

    [Fact]
    public void Capsule_Stage3_PreStage3TalksExcluded()
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

        var cap = Section(
            LlmGoalPolicy.BuildUserPrompt(
                WorldWithContracts(contract),
                WithTalkGoals("Pathwarden Thorolf", 4, since.AddMinutes(-5)), null),
            "## Contracts");

        Assert.DoesNotContain("DONE (stage 3, complete)", cap);
    }

    [Fact]
    public void Capsule_Stage3_SharedTurnInNpc_NoDoneNote()
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

        Assert.DoesNotContain("DONE (stage 3, complete)", cap);
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

        Assert.Contains("DONE (stage 3, complete)", cap);
        Assert.Contains($"Talked {oddName} 3x", cap);
    }

    [Fact]
    public void Capsule_InProgressContract_NoDoneNoteEvenWithRepeatedTalks()
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

        Assert.DoesNotContain("DONE (stage 3, complete)", cap);
    }
}
