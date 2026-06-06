// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for AttributeRaise — the pure protocol-level helper behind the
// RaiseAttribute (0x0045) XP-spend verb. These also lock in the
// audit-safety properties: the source has NO default spend amount, makes
// NO attribute preference, and only translates a VALID LLM request into
// wire values (clamping the amount to the bot's observed unspent XP).

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class AttributeRaiseTests
{
    [Theory]
    [InlineData("strength", 1u)]
    [InlineData("endurance", 2u)]
    [InlineData("quickness", 3u)]
    [InlineData("coordination", 4u)]
    [InlineData("focus", 5u)]
    [InlineData("self", 6u)]
    [InlineData("Endurance", 2u)]          // case-insensitive
    [InlineData("  Coordination  ", 4u)]   // trims whitespace
    public void TryResolveAttributeId_KnownNames_MapToWireIds(string name, uint expected)
    {
        Assert.True(AttributeRaise.TryResolveAttributeId(name, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("health")]      // a VITAL, not an attribute (RaiseAttribute can't raise it)
    [InlineData("luck")]        // not an AC attribute
    [InlineData("str")]         // abbreviations are not accepted
    public void TryResolveAttributeId_UnknownNames_Rejected(string? name)
    {
        Assert.False(AttributeRaise.TryResolveAttributeId(name, out var id));
        Assert.Equal(0u, id);
    }

    [Fact]
    public void TryValidateAndClampAmount_WithinAvailable_PassesThrough()
    {
        Assert.True(AttributeRaise.TryValidateAndClampAmount(5000, availableExperience: 80000, out var clamped));
        Assert.Equal(5000u, clamped);
    }

    [Fact]
    public void TryValidateAndClampAmount_AboveAvailable_ClampsToAvailable()
    {
        // Mechanical safety only: the server rejects amount > AvailableExperience.
        Assert.True(AttributeRaise.TryValidateAndClampAmount(100000, availableExperience: 80000, out var clamped));
        Assert.Equal(80000u, clamped);
    }

    [Fact]
    public void TryValidateAndClampAmount_NullAmount_Rejected_NoSourceDefault()
    {
        // The LLM MUST supply the amount; a missing amount must NOT default
        // to "spend everything" (or any other value) — that is a strategic
        // choice the source may not make.
        Assert.False(AttributeRaise.TryValidateAndClampAmount(null, availableExperience: 80000, out var clamped));
        Assert.Equal(0u, clamped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50000)]
    [InlineData(4294967296)]   // uint.MaxValue + 1 — out of wire range
    public void TryValidateAndClampAmount_NonPositiveOrOverflow_Rejected(long amount)
    {
        Assert.False(AttributeRaise.TryValidateAndClampAmount(amount, availableExperience: 80000, out var clamped));
        Assert.Equal(0u, clamped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void TryValidateAndClampAmount_NoSpendableXp_Rejected(long? available)
    {
        // Cannot spend when the bot has observed no unspent XP yet.
        Assert.False(AttributeRaise.TryValidateAndClampAmount(5000, available, out var clamped));
        Assert.Equal(0u, clamped);
    }

    [Fact]
    public void TryValidateAndClampAmount_ExactlyUintMax_WithEnoughXp_Allowed()
    {
        Assert.True(AttributeRaise.TryValidateAndClampAmount(
            uint.MaxValue, availableExperience: long.MaxValue, out var clamped));
        Assert.Equal(uint.MaxValue, clamped);
    }
}
