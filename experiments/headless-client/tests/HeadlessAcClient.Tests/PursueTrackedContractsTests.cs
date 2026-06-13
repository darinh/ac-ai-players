// SPDX-License-Identifier: AGPL-3.0-or-later
// Tracked-contract pursuit. The QUEST-DIALOG COMPILER rule is broadened to fire
// not only on freshly observed dialog (hasRecentServerDialog) but also when the
// bot HOLDS tracked contracts (## Contracts non-empty), so a contract accepted
// earlier — with no fresh dialog this run — gets the SAME compile-onto-stack /
// hunt-the-objective / turn-in mechanics instead of being ignored. These tests
// lock that the contract path of the rule renders when (and only when) the bot
// holds contracts and the strategic-stack block is active.

using System;
using System.Linq;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using Xunit;

namespace HeadlessAcClient.Tests;

public class PursueTrackedContractsTests
{
    // A phrase that only appears in the CONTRACT branch of the broadened
    // QUEST-DIALOG COMPILER rule (so it renders only when contracts are tracked
    // or dialog assigns one).
    private const string Marker = "a contract you ALREADY hold";

    private static WorldStateProjection W(int contractCount)
    {
        var contracts = Enumerable.Range(0, contractCount)
            .Select(i => new ContractProjection { ContractId = (uint)(100 + i), Stage = 2u })
            .ToArray();
        return new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x500u, Name = "Headless", HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),
            Contracts = contracts,
        };
    }

    private static string PromptWithStack(WorldStateProjection w) =>
        LlmGoalPolicy.BuildUserPrompt(w, new EventStream(), currentGoal: null, stack: new IntentStack());

    [Fact]
    public void ContractBranch_Present_WhenContractsTracked_AndStackPresent()
    {
        // Held contracts + a stack + NO recent dialog (empty EventStream): the
        // broadened QUEST-DIALOG COMPILER still fires on the tracked contract —
        // the exact gap (a contract held with no fresh dialog this run).
        Assert.Contains(Marker, PromptWithStack(W(2)));
    }

    [Fact]
    public void ContractBranch_Absent_WhenNoContractsAndNoDialog()
    {
        // No contracts + no dialog -> the rule does not fire at all.
        Assert.DoesNotContain(Marker, PromptWithStack(W(0)));
    }

    [Fact]
    public void ContractBranch_Absent_WhenNoIntentStack()
    {
        // The strategic-stack rule block (and this rule with it) is skipped when
        // no stack is present. Production always passes a stack.
        Assert.DoesNotContain(Marker,
            LlmGoalPolicy.BuildUserPrompt(W(2), new EventStream(), currentGoal: null));
    }
}
