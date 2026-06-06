// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for SkillRaise — the pure protocol-level helper behind the RaiseSkill
// (0x0046) XP-spend verb. The source maps the LLM-named skill to its raw wire
// ordinal (HeadlessAcClient.Protocol.Skill) and makes NO skill preference, NO
// curated list, and NO default amount. It does NOT pre-judge whether a skill
// is trained/retired — it resolves any real Skill member (except None) and
// lets the server validate. Amount validation is shared with AttributeRaise
// and tested there.

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SkillRaiseTests
{
    [Theory]
    // Wire ordinals are positional in the Skill enum (verbatim from the
    // server). These pin a representative spread.
    [InlineData("MeleeDefense", 6u)]
    [InlineData("MissileDefense", 7u)]
    [InlineData("ArcaneLore", 14u)]
    [InlineData("Healing", 21u)]
    [InlineData("Run", 24u)]
    [InlineData("LifeMagic", 33u)]
    [InlineData("WarMagic", 34u)]
    [InlineData("Alchemy", 38u)]
    [InlineData("Cooking", 39u)]
    [InlineData("TwoHandedCombat", 41u)]
    [InlineData("VoidMagic", 43u)]
    [InlineData("Summoning", 54u)]
    public void TryResolveSkillId_CamelCaseNames_MapToWireOrdinals(string name, uint expected)
    {
        Assert.True(SkillRaise.TryResolveSkillId(name, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    // The LLM writes natural names with spaces / hyphens / underscores and
    // arbitrary case; all must normalize to the same wire ordinal.
    [InlineData("war magic", 34u)]
    [InlineData("War Magic", 34u)]
    [InlineData("WAR MAGIC", 34u)]
    [InlineData("war-magic", 34u)]
    [InlineData("war_magic", 34u)]
    [InlineData("  melee defense  ", 6u)]
    [InlineData("two handed combat", 41u)]
    [InlineData("mana conversion", 16u)]
    public void TryResolveSkillId_NaturalNames_Normalized(string name, uint expected)
    {
        Assert.True(SkillRaise.TryResolveSkillId(name, out var id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void TryResolveSkillId_RetiredSkill_ResolvesButServerWillReject()
    {
        // Source does NOT pre-judge trainability: a retired skill is still a
        // real Skill enum member, so it resolves to its ordinal. The SERVER
        // rejects it. (Axe = ordinal 1.)
        Assert.True(SkillRaise.TryResolveSkillId("axe", out var id));
        Assert.Equal(1u, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none")]            // None (ordinal 0) is not a real skill
    [InlineData("None")]
    [InlineData("endurance")]       // an attribute, not a skill
    [InlineData("health")]          // a vital, not a skill
    [InlineData("notaskill")]       // not an AC skill
    [InlineData("34")]              // numeric strings must NOT parse to an ordinal
    [InlineData("999")]             // out-of-range numeric
    [InlineData("warmagic,healing")] // comma-list flags must NOT combine
    public void TryResolveSkillId_UnknownOrMalformed_Rejected(string? name)
    {
        Assert.False(SkillRaise.TryResolveSkillId(name, out var id));
        Assert.Equal(0u, id);
    }
}
