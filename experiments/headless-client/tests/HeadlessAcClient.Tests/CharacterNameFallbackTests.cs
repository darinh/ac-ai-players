// SPDX-License-Identifier: AGPL-3.0-or-later
// CharacterNameFallback unit tests — the deterministic alternate-name
// generator used by the login handshake to recover from a NameInUse /
// NameBanned CharacterCreate rejection (smoke-charlist-quirk).
//
// These cover the pure name math (the part the giant async receive
// loop cannot exercise in a unit test): retryability classification,
// determinism, distinctness across attempts, the base-26 suffix
// rollover, length capping, and sanitization of non-letter input.

using HeadlessAcClient.Protocol;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CharacterNameFallbackTests
{
    [Fact]
    public void IsNameRetryable_OnlyForNameSpecificFailures()
    {
        // Name-specific rejections are retryable under a fresh name.
        Assert.True(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.NameInUse));
        Assert.True(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.NameBanned));

        // Everything else is transient or fatal; a rename would not help.
        Assert.False(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.Ok));
        Assert.False(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.Pending));
        Assert.False(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.Corrupt));
        Assert.False(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.DatabaseDown));
        Assert.False(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.AdminPrivilegeDenied));
        Assert.False(CharacterNameFallback.IsNameRetryable(CharacterCreateResponse.Undef));
    }

    [Theory]
    [InlineData(1, "Headlessa")]
    [InlineData(2, "Headlessb")]
    [InlineData(26, "Headlessz")]
    [InlineData(27, "Headlessaa")]
    [InlineData(52, "Headlessaz")]
    [InlineData(53, "Headlessba")]
    public void NextName_BuildsSpreadsheetStyleSuffix(int attempt, string expected)
    {
        Assert.Equal(expected, CharacterNameFallback.NextName("Headless", attempt));
    }

    [Fact]
    public void NextName_StripsNonLetterCharactersFromBase()
    {
        // "Headless01" -> root "Headless", attempt 1 -> "Headlessa".
        Assert.Equal("Headlessa", CharacterNameFallback.NextName("Headless01", 1));
        // Spaces / apostrophes / hyphens dropped too.
        Assert.Equal("OldAla", CharacterNameFallback.NextName("Old-Al' 7", 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("__--__")]
    public void NextName_FallsBackToDefaultWhenBaseHasNoLetters(string? baseName)
    {
        // Default root is "Headless"; attempt 1 suffix is "a".
        Assert.Equal("Headlessa", CharacterNameFallback.NextName(baseName, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NextName_TreatsNonPositiveAttemptAsFirst(int attempt)
    {
        Assert.Equal("Headlessa", CharacterNameFallback.NextName("Headless", attempt));
    }

    [Fact]
    public void NextName_AlwaysLettersOnlyAndWithinLengthCap()
    {
        var longBase = new string('A', 100);
        for (var attempt = 1; attempt <= 60; attempt++)
        {
            var name = CharacterNameFallback.NextName(longBase, attempt);
            Assert.NotEmpty(name);
            Assert.True(name.Length <= CharacterNameFallback.MaxNameLength,
                $"attempt {attempt} produced length {name.Length}: {name}");
            Assert.All(name, ch => Assert.True(char.IsLetter(ch), $"non-letter in {name}"));
        }
    }

    [Fact]
    public void NextName_IsDeterministicAndDistinctPerAttempt()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var a = CharacterNameFallback.NextName("Pilot", attempt);
            var b = CharacterNameFallback.NextName("Pilot", attempt);
            Assert.Equal(a, b);                 // deterministic
            Assert.True(seen.Add(a), $"duplicate name {a} at attempt {attempt}");
        }
    }
}
