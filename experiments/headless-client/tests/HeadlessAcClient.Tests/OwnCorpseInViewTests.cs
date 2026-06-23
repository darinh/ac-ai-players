// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for LlmGoalPolicy.HasOwnCorpseInView: the pure detector that decides
// whether a `## Visible nearby` entry is the bot's OWN corpse (wire name
// "Corpse of <selfName>"). It is the in-view complement to the out-of-view
// corpse bearing; it gates the in-view `## Corpse` retrieval cue. The detector
// deliberately does NOT consult opened-corpse bookkeeping (that set is
// Use-dispatch TTL telemetry, not an "emptied" signal). Names are placeholders.

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class OwnCorpseInViewTests
{
    private const string Self = "Botname";

    private static VisibleObjectProjection Vis(uint guid, string name, bool isCorpse) =>
        new() { Guid = guid, Name = name, IsCorpse = isCorpse };

    [Fact]
    public void OwnCorpse_InView_True()
    {
        var visible = new[] { Vis(0x10u, "Corpse of Botname", isCorpse: true) };
        Assert.True(LlmGoalPolicy.HasOwnCorpseInView(visible, Self));
    }

    [Fact]
    public void MonsterCorpse_DifferentName_False()
    {
        var visible = new[] { Vis(0x20u, "Corpse of Drudge", isCorpse: true) };
        Assert.False(LlmGoalPolicy.HasOwnCorpseInView(visible, Self));
    }

    [Fact]
    public void OwnName_ButNotFlaggedCorpse_False()
    {
        // A non-corpse object that happens to share the label is not a corpse.
        var visible = new[] { Vis(0x30u, "Corpse of Botname", isCorpse: false) };
        Assert.False(LlmGoalPolicy.HasOwnCorpseInView(visible, Self));
    }

    [Fact]
    public void EmptyVisible_False()
    {
        Assert.False(LlmGoalPolicy.HasOwnCorpseInView(new VisibleObjectProjection[0], Self));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSelfName_False(string? selfName)
    {
        var visible = new[] { Vis(0x10u, "Corpse of Botname", isCorpse: true) };
        Assert.False(LlmGoalPolicy.HasOwnCorpseInView(visible, selfName));
    }

    [Fact]
    public void NameMatch_IsCaseInsensitive_True()
    {
        var visible = new[] { Vis(0x10u, "corpse of BOTNAME", isCorpse: true) };
        Assert.True(LlmGoalPolicy.HasOwnCorpseInView(visible, Self));
    }

    [Fact]
    public void OwnCorpseAmongMonsterCorpses_True()
    {
        var visible = new[]
        {
            Vis(0x20u, "Corpse of Drudge", isCorpse: true),
            Vis(0x21u, "Corpse of Mosswart", isCorpse: true),
            Vis(0x22u, "Corpse of Botname", isCorpse: true),
        };
        Assert.True(LlmGoalPolicy.HasOwnCorpseInView(visible, Self));
    }

    [Fact]
    public void DifferentPlayerCorpse_False()
    {
        // Another player's corpse (different name) is not the bot's own.
        var visible = new[] { Vis(0x40u, "Corpse of Otherbot", isCorpse: true) };
        Assert.False(LlmGoalPolicy.HasOwnCorpseInView(visible, Self));
    }
}
