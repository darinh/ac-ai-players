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
}
