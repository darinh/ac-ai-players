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
        Assert.Contains("gone to Pathwarden Thorolf 4x (Talk/Explore)", cap);
        Assert.Contains("no separate hand-in", cap);
    }

    [Fact]
    public void SettledContractCue_FiresWhenReTargetingSettledTurnInNpc()
    {
        // The salience extraction: when the bot has re-targeted a settled stage-3
        // turn-in NPC past the recognition threshold, the protected-tail cue fires
        // and names that NPC, telling the LLM there is no turn-in and to do else.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 850u, Stage = 3u, Name = "Find the Barkeeper",
            NpcEnd = "Buckminster", Stage3SinceUtc = since,
        };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithTalkGoals("Buckminster", 3, since), null);
        Assert.Contains("## Settled contract — no turn-in", prompt);
        Assert.Contains("Buckminster", prompt);
        Assert.Contains("NO separate turn-in step", prompt);
    }

    [Fact]
    public void SettledContractCue_OmittedForSingleAttempt()
    {
        // One (legitimate) post-completion attempt is below the recognition threshold,
        // so the NPC is not yet "settled" -> the cue must not fire.
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
    public void SettledContractCue_OmittedWhenNoContracts()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(), new EventStream(), null);
        Assert.DoesNotContain("## Settled contract — no turn-in", prompt);
    }

    [Fact]
    public void SettledContractCue_FiresOnExploreReTargeting()
    {
        // The Explore branch: a LOCATE/REACH contract is pursued via navigate-only
        // Explore (not Talk). The newest emission is Explore toward the settled NPC.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        { ContractId = 863u, Stage = 3u, Name = "Find the Barkeeper", NpcEnd = "Buckminster", Stage3SinceUtc = since };
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            WorldWithContracts(contract), WithExploreGoals("Buckminster", 3, since), null);
        Assert.Contains("## Settled contract — no turn-in", prompt);
        Assert.Contains("Buckminster", prompt);
    }

    [Fact]
    public void SettledContractCue_RovingTwoSettled_NamesTheCurrentlyTargetedNpc()
    {
        // Roving multi-contract case: TWO settled turn-in NPCs both qualify. The cue
        // must name the one the bot is CURRENTLY re-targeting (its LATEST emission),
        // not just the first settled contract.
        var since = DateTimeOffset.UtcNow;
        var c1 = new ContractProjection
        { ContractId = 860u, Stage = 3u, Name = "Find the Pathwarden", NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since };
        var c2 = new ContractProjection
        { ContractId = 861u, Stage = 3u, Name = "Find the Barkeeper", NpcEnd = "Buckminster", Stage3SinceUtc = since };
        var es = new EventStream();
        AppendTalkGoals(es, "Pathwarden Thorolf", 3, since);
        AppendTalkGoals(es, "Buckminster", 3, since.AddSeconds(1)); // newer -> the live target

        var prompt = LlmGoalPolicy.BuildUserPrompt(WorldWithContracts(c1, c2), es, null);

        Assert.Contains("## Settled contract — no turn-in", prompt);
        var cueLine = prompt.Split('\n').FirstOrDefault(l => l.Contains("re-targeted") && l.Contains("DONE contract"));
        Assert.NotNull(cueLine);
        Assert.Contains("Buckminster", cueLine!);          // the live (latest) target
        Assert.DoesNotContain("Pathwarden", cueLine!);     // not the other settled NPC
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
    public void SettledContractCue_OmittedWhenNewerGoalIsNotInteraction()
    {
        // The cue is about the bot's CURRENT pursuit: a newer Attack (or any non-
        // Talk/Explore goal) AFTER the settled Talk/Explore means the bot has MOVED ON,
        // so the cue must not fire stale during combat.
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
    }

    [Fact]
    public void Capsule_Stage3RepeatedExplorePursuit_SurfacesDoneNoHandIn()
    {
        // cp031: a stage-3 contract whose objective is a LOCATE/REACH task is
        // pursued via navigate-only Explore (not Talk), so the Talk-only hand-in
        // count never reached the threshold and the bot roved between two done
        // contracts forever (live cp029/cp030). Explore-pursuits toward the turn-in
        // NPC since stage-3 now count too — at a HIGHER threshold (3) than Talk (2),
        // since the first Explore is ordinary travel-to-reach, not an attempt.
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

        Assert.Contains("DONE (stage 3, complete)", cap);
        Assert.Contains("gone to Buckminster 3x (Talk/Explore)", cap);
    }

    [Fact]
    public void Capsule_Stage3_TwoExploresZeroTalk_DoesNotFire()
    {
        // cp031 safety (gpt-5.4 review): ordinary travel to a far turn-in NPC can
        // take a couple of Explore goals BEFORE the first hand-in Talk. Two
        // Explores with zero Talk must NOT prematurely declare a turn-in contract
        // "finished" — the Explore threshold is 3 (1 travel + 2 redundant
        // re-navigations), above ordinary travel, so the contract keeps its real
        // hand-in attempt.
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

        Assert.DoesNotContain("DONE (stage 3, complete)", cap);
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
    public void Capsule_Stage3_OneTalkPlusOneExplore_DoesNotFire()
    {
        // cp031 safety: the gate fires on the Talk threshold (2) OR the (higher)
        // Explore threshold (3) independently, NOT on their SUM — a single real
        // hand-in Talk plus a single navigate-toward Explore (1+1) reaches NEITHER
        // threshold, so a contract that genuinely clears on a final hand-in keeps
        // its one real attempt. Otherwise a turn-in contract would be declared done
        // after just one Talk (plus the Explore to reach the NPC).
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

        Assert.DoesNotContain("DONE (stage 3, complete)", cap);
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
        Assert.Contains($"gone to {oddName} 3x (Talk/Explore)", cap);
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

    // ---- cp050: Motor settled-stage-3-turn-in Talk backstop ----
    // The prompt already SURFACES a settled stage-3 turn-in ("DONE (stage 3)"); these
    // cover the MECHANICAL backstop (IsSettledStage3TurnInNpc) that DROPS a further
    // Talk to such an NPC when a weak model ignores the note, sharing the exact
    // recognition (IsSettledStage3TurnIn) with the render so the two never drift.

    [Fact]
    public void SettledStage3TurnInNpc_PastThreshold_Suppresses()
    {
        // A stage-3 (done) contract's turn-in NPC Talked past the post-completion
        // threshold (2) is a settled turn-in with no hand-in — drop a further Talk.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 900u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };
        Assert.True(LlmGoalPolicy.IsSettledStage3TurnInNpc(
            WorldWithContracts(contract), WithTalkGoals("Pathwarden Thorolf", 2, since),
            "Pathwarden Thorolf"));
    }

    [Fact]
    public void SettledStage3TurnInNpc_SingleAttempt_DoesNotSuppress()
    {
        // One legitimate post-completion hand-in attempt is preserved — a contract
        // that really clears on a final Talk gets its one real attempt.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 901u, Stage = 3u, Name = "Find the Pathwarden",
            NpcEnd = "Pathwarden Thorolf", Stage3SinceUtc = since,
        };
        Assert.False(LlmGoalPolicy.IsSettledStage3TurnInNpc(
            WorldWithContracts(contract), WithTalkGoals("Pathwarden Thorolf", 1, since),
            "Pathwarden Thorolf"));
    }

    [Fact]
    public void SettledStage3TurnInNpc_RovingTwoSettledNpcs_BothSuppressed()
    {
        // The exact observed loop: two stage-3 contracts whose turn-in NPCs the bot
        // ROVES between, each Talked past the threshold. The stationary/novelty Talk
        // guards MISS this (movement + fresh flavor dialog reset them); this contract-
        // stage backstop is position- AND novelty-independent, so BOTH settled NPCs
        // are suppressed.
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
        Assert.True(LlmGoalPolicy.IsSettledStage3TurnInNpc(world, es, "Pathwarden Thorolf"));
        Assert.True(LlmGoalPolicy.IsSettledStage3TurnInNpc(world, es, "Buckminster"));
    }

    [Fact]
    public void SettledStage3TurnInNpc_NpcWithLiveBusiness_NotSuppressed()
    {
        // An NPC that is ALSO the start/turn-in of a NON-terminal contract has live
        // business (e.g. a fresh batch just obtained from this same source), so its
        // Talk must NOT be suppressed even though it settled an earlier contract.
        var since = DateTimeOffset.UtcNow;
        var settled = new ContractProjection
        {
            ContractId = 904u, Stage = 3u, Name = "Old", NpcEnd = "Broker", Stage3SinceUtc = since,
        };
        var fresh = new ContractProjection
        {
            ContractId = 905u, Stage = 1u, Name = "New", NpcStart = "Broker",
        };
        Assert.False(LlmGoalPolicy.IsSettledStage3TurnInNpc(
            WorldWithContracts(settled, fresh), WithTalkGoals("Broker", 5, since), "Broker"));
    }

    [Fact]
    public void SettledStage3TurnInNpc_InProgressContract_NotSuppressed()
    {
        // A stage-2 (in-progress) contract is never settled — Talking its NPC is
        // legitimate progress, never suppressed regardless of Talk count.
        var since = DateTimeOffset.UtcNow;
        var contract = new ContractProjection
        {
            ContractId = 906u, Stage = 2u, Name = "Hunt", NpcEnd = "Sergeant",
        };
        Assert.False(LlmGoalPolicy.IsSettledStage3TurnInNpc(
            WorldWithContracts(contract), WithTalkGoals("Sergeant", 5, since), "Sergeant"));
    }

    [Fact]
    public void SettledStage3TurnInNpc_SharedTurnInNpc_NotRecognized()
    {
        // Mirror of the render's shared-turn-in ambiguity guard at the predicate
        // level: two stage-3 contracts sharing one turn-in NPC make per-contract
        // attribution ambiguous, so neither is recognized as a settled turn-in.
        var since = DateTimeOffset.UtcNow;
        var a = new ContractProjection
        {
            ContractId = 907u, Stage = 3u, Name = "A", NpcEnd = "Hub Giver", Stage3SinceUtc = since,
        };
        var b = new ContractProjection
        {
            ContractId = 908u, Stage = 3u, Name = "B", NpcEnd = "Hub Giver", Stage3SinceUtc = since,
        };
        Assert.False(LlmGoalPolicy.IsSettledStage3TurnInNpc(
            WorldWithContracts(a, b), WithTalkGoals("Hub Giver", 5, since), "Hub Giver"));
    }

    [Fact]
    public void SettledStage3TurnInNpc_SequentialBatchSettlement_NoTalkLeak()
    {
        // claude review: an NPC that is the turn-in (NpcEnd) of one contract AND the
        // task-giver (NpcStart) of ANOTHER must not have the Talks it received while
        // the SECOND contract was still live business leak into the first's settled
        // count once the second also settles. The backstop's count window starts at
        // the NPC's FULLY-SETTLED time (max Stage3SinceUtc over the NPC's contracts),
        // not the earlier per-contract time, so those pre-full-settle Talks are
        // excluded and a legitimate batch-refresh Talk is not wrongly suppressed.
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
        Assert.False(LlmGoalPolicy.IsSettledStage3TurnInNpc(WorldWithContracts(a, b), es, "Broker"));
    }

    [Fact]
    public void SettledStage3TurnInNpc_SequentialBatchSettlement_SuppressesAfterFullSettle()
    {
        // The companion to the no-leak case: once the NPC is FULLY settled (its last
        // contract done at t1), re-Talks made AFTER t1 past the threshold ARE the
        // fixation the backstop suppresses.
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
        Assert.True(LlmGoalPolicy.IsSettledStage3TurnInNpc(WorldWithContracts(a, b), es, "Broker"));
    }

    // ── stage-3 objective is rendered ALREADY-SATISFIED immediately ───────

    [Fact]
    public void Capsule_Stage3Objective_MarkedAlreadySatisfied_WithoutPriorPursuit()
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
        Assert.Contains("objective already satisfied", cap);
        Assert.Contains("do not pursue it", cap);
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

        // Both done rows carry the qualifier; the active row survives and is unmarked.
        Assert.Contains("objective: Locate the contact in the tavern.  (stage 3 done", cap);
        Assert.Contains("objective: Slay six marauders in the lowlands.", cap);
        Assert.DoesNotContain("Slay six marauders in the lowlands.  (stage 3 done", cap);
    }
}
