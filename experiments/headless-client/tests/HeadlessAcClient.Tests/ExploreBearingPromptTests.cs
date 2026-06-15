// SPDX-License-Identifier: AGPL-3.0-or-later
// The PURSUE UNSEEN OBJECTIVES rule is broadened so that when dialog/hint gives
// a compass DIRECTION toward an unseen named objective, the LLM copies that
// stated compass bearing into the Explore goal's `direction` field — which the
// Motor now COMMITS to (the directed-travel frontier change). A non-compass
// relative phrase is not a valid `direction` and is omitted. These tests lock
// that the bearing-copy instruction renders in the user prompt.

using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using System;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ExploreBearingPromptTests
{
    // A phrase that only appears in the new bearing-copy clause of the
    // PURSUE UNSEEN OBJECTIVES rule.
    private const string Marker = "copy that stated compass bearing into an `Explore`";

    private static WorldStateProjection MinimalWorld() => new()
    {
        Self = new SelfProjection { Guid = 0x500u, Name = "Headless", HealthFraction = 1.0f },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = Array.Empty<VisibleObjectProjection>(),
    };

    [Fact]
    public void BearingCopyInstruction_Renders_InUserPrompt()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            MinimalWorld(), new EventStream(), currentGoal: null, stack: new IntentStack());
        Assert.Contains(Marker, prompt);
    }

    [Fact]
    public void BearingCopyInstruction_RestrictsToStatedBearings_NotGuesses()
    {
        // The instruction must constrain the bearing to one the text actually
        // gives (HK-safe: copy a server/NPC bearing, never invent one). This
        // phrase is unique to the new clause.
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            MinimalWorld(), new EventStream(), currentGoal: null, stack: new IntentStack());
        Assert.Contains("never a guessed one", prompt);
    }
}
