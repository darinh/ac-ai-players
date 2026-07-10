// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CharacterCreateMessage default attribute allocation. The
// server (PlayerFactory.ValidateAttributeCredits) requires each of the
// six attributes in [10,100] and their sum <= the selected heritage's
// AttributeCredits budget. The server-side reference factory spends a
// full even 6 x 55 = 330 and treats 330 as the standard-heritage budget.
// These pin the default Options to a full, budget-using allocation so a
// freshly created character does not start at the all-minimum (which
// spent only 60 of the budget).

using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CharacterCreateMessageTests
{
    // The attribute-credit budget the server-side reference factory targets
    // (it spends exactly 6 x 55 = 330). The default allocation MUST stay at or
    // under this so CharacterCreate is never rejected with
    // TooManySkillCreditsUsed. Live-verified: the server accepts 330 on the
    // default heritage.
    private const uint SafeHeritageAttributeBudget = 330u;

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
    public void DefaultAttributes_SpendTheFullStandardHeritageBudget()
    {
        // Regression guard: the old default was all-10s (sum 60), then a
        // partial 6 x 45 = 270 that left 60 credits unspent. The new default
        // spends the full budget the server allows (6 x 55 = 330).
        var o = DefaultOptions();
        var sum = o.StrengthAbility + o.EnduranceAbility + o.CoordinationAbility +
                  o.QuicknessAbility + o.FocusAbility + o.SelfAbility;
        Assert.Equal(SafeHeritageAttributeBudget, sum);
    }

    [Fact]
    public void DefaultAttributes_AreEven_NoBuildFavoritism()
    {
        // Even split across all six: an uneven spread would bake a preference
        // into the wire packer, which the Strategy layer owns, not this packer.
        var o = DefaultOptions();
        Assert.Equal(o.StrengthAbility, o.EnduranceAbility);
        Assert.Equal(o.StrengthAbility, o.CoordinationAbility);
        Assert.Equal(o.StrengthAbility, o.QuicknessAbility);
        Assert.Equal(o.StrengthAbility, o.FocusAbility);
        Assert.Equal(o.StrengthAbility, o.SelfAbility);
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
        // The default allocation (330) is only safe because the shipped default
        // heritage's real credit budget accommodates it. Pin the default
        // heritage so a future change that lowers the budget below the
        // allocation is caught here instead of only failing server-side at
        // create time.
        Assert.Equal(CharacterCreateMessage.HeritageAluvian, DefaultOptions().Heritage);
    }
}
