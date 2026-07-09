// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CharacterCreateMessage default attribute allocation. The
// server (PlayerFactory.ValidateAttributeCredits) requires each of the
// six attributes in [10,100] and their sum <= the selected heritage's
// AttributeCredits budget (the smallest heritage budget floor is >= 290).
// These pin the default Options to a safe, budget-using allocation so a
// freshly created character does not start at the all-minimum (which
// spent only 60 of the budget).

using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CharacterCreateMessageTests
{
    // The smallest heritage AttributeCredits budget the client may face.
    // The default allocation MUST stay at or under this so CharacterCreate
    // is never rejected with TooManySkillCreditsUsed regardless of heritage.
    private const uint SafeHeritageAttributeBudget = 290u;

    private static CharacterCreateMessage.Options DefaultOptions()
        => new(Account: "testaccount", Name: "Testbot");

    [Fact]
    public void DefaultAttributes_EachWithinServerRange()
    {
        var o = DefaultOptions();
        foreach (var v in new[]
        {
            o.StrengthAbility, o.EnduranceAbility, o.CoordinationAbility,
            o.QuicknessAbility, o.FocusAbility, o.SelfAbility,
        })
        {
            Assert.InRange(v, 10u, 100u);
        }
    }

    [Fact]
    public void DefaultAttributes_SumWithinSafeHeritageBudget()
    {
        var o = DefaultOptions();
        var sum = o.StrengthAbility + o.EnduranceAbility + o.CoordinationAbility +
                  o.QuicknessAbility + o.FocusAbility + o.SelfAbility;
        Assert.True(sum <= SafeHeritageAttributeBudget,
            $"attribute sum {sum} exceeds the safe heritage budget {SafeHeritageAttributeBudget}");
    }

    [Fact]
    public void DefaultAttributes_UseMoreThanBareMinimum()
    {
        // Regression guard: the old default was all-10s (sum 60), which
        // wasted the budget. The new default must spend meaningfully more.
        var o = DefaultOptions();
        var sum = o.StrengthAbility + o.EnduranceAbility + o.CoordinationAbility +
                  o.QuicknessAbility + o.FocusAbility + o.SelfAbility;
        Assert.True(sum > 60u, $"attribute sum {sum} is not above the all-10s minimum (60)");
    }

    [Fact]
    public void Pack_WithDefaultAttributes_Succeeds()
    {
        // The per-attribute [10,100] gate is enforced client-side in Pack
        // (ValidateAttribute); the new defaults must pass it and produce the
        // size MeasurePackedSize predicts.
        var o = DefaultOptions();
        var buf = new byte[CharacterCreateMessage.MeasurePackedSize(o)];
        var written = CharacterCreateMessage.Pack(buf, o);
        Assert.Equal(buf.Length, written);
    }

    [Fact]
    public void DefaultHeritage_KeepsAllocationWithinBudget()
    {
        // The default allocation (270) is only safe because the shipped default
        // heritage's real credit budget accommodates it. Pin the default
        // heritage so a future change that lowers the budget below the
        // allocation is caught here instead of only failing server-side at
        // create time.
        Assert.Equal(CharacterCreateMessage.HeritageAluvian, DefaultOptions().Heritage);
    }
}
