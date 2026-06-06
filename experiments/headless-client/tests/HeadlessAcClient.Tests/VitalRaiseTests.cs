// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for VitalRaise — the pure protocol-level helper behind the
// RaiseVital (0x0044) XP-spend verb. The source maps the LLM-named vital to
// its raw PropertyAttribute2nd wire id for the raisable MAX pools
// (MaxHealth=1, MaxStamina=3, MaxMana=5) and makes NO vital preference and NO
// default amount (amount validation is shared with AttributeRaise and tested
// there).

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class VitalRaiseTests
{
    [Theory]
    [InlineData("health", 1u)]
    [InlineData("maxhealth", 1u)]
    [InlineData("max health", 1u)]
    [InlineData("stamina", 3u)]
    [InlineData("maxstamina", 3u)]
    [InlineData("max stamina", 3u)]
    [InlineData("mana", 5u)]
    [InlineData("maxmana", 5u)]
    [InlineData("max mana", 5u)]
    [InlineData("Health", 1u)]            // case-insensitive
    [InlineData("  Mana  ", 5u)]          // trims whitespace
    public void TryResolveVitalId_KnownNames_MapToWireIds(string name, uint expected)
    {
        Assert.True(VitalRaise.TryResolveVitalId(name, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("endurance")]   // an ATTRIBUTE, not a vital (RaiseVital can't raise it)
    [InlineData("strength")]    // an attribute
    [InlineData("hp")]          // abbreviations are not accepted
    [InlineData("life")]        // not an AC vital name
    public void TryResolveVitalId_UnknownNames_Rejected(string? name)
    {
        Assert.False(VitalRaise.TryResolveVitalId(name, out var id));
        Assert.Equal(0u, id);
    }

    [Fact]
    public void TryResolveVitalId_OnlyMaxPools_NeverCurrentPoolIds()
    {
        // The raisable wire ids are the MAX pools (1/3/5). The current-pool
        // ids (Health=2, Stamina=4, Mana=6) must never be produced — raising a
        // current pool is not a thing the server accepts.
        VitalRaise.TryResolveVitalId("health", out var h);
        VitalRaise.TryResolveVitalId("stamina", out var s);
        VitalRaise.TryResolveVitalId("mana", out var m);
        Assert.Equal(1u, h);
        Assert.Equal(3u, s);
        Assert.Equal(5u, m);
    }
}
