// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CorpseRecovery: the env TTL resolver and the pure render-gate
// predicate / reach test that decide whether the last death location is
// surfaced as a return bearing. Knowledge-free: coordinates are placeholders.

using System;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CorpseRecoveryTests
{
    [Theory]
    [InlineData(null, 600)]    // unset -> default 10 min
    [InlineData("", 600)]      // blank -> default
    [InlineData("xyz", 600)]   // unparseable -> default
    [InlineData("30", 600)]    // below min (60) -> default
    [InlineData("-5", 600)]    // negative -> default
    [InlineData("60", 60)]     // min accepted
    [InlineData("900", 900)]   // a custom value
    [InlineData("3600", 3600)] // max accepted
    [InlineData("99999", 3600)]// above max -> clamped
    public void ResolveCorpseTtl_DefaultsAndClamps(string? env, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CorpseRecovery.ResolveCorpseTtl(env));
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(600);
    private const float Radius = 120f;
    // A death far from any "current" test position below.
    private static DeathLocation FreshDeath() => new(100f, 200f, 0x00A9u, DateTimeOffset.UtcNow);

    [Fact]
    public void ShouldSurfaceCorpse_NoDeath_False()
    {
        Assert.False(CorpseRecovery.ShouldSurfaceCorpse(null, (0f, 0f), TimeSpan.FromSeconds(10), Ttl, Radius));
    }

    [Fact]
    public void ShouldSurfaceCorpse_FreshAndFarFromCorpse_True()
    {
        // Bot is well outside the visible radius of the corpse and the death is
        // fresh -> surface the return bearing.
        Assert.True(CorpseRecovery.ShouldSurfaceCorpse(FreshDeath(), (5000f, 5000f), TimeSpan.FromSeconds(30), Ttl, Radius));
    }

    [Fact]
    public void ShouldSurfaceCorpse_WithinVisibleRadius_False()
    {
        // Bot is within the visible radius of the corpse -> it is a normal
        // Visible-nearby object, so the bearing is suppressed.
        Assert.False(CorpseRecovery.ShouldSurfaceCorpse(FreshDeath(), (110f, 210f), TimeSpan.FromSeconds(30), Ttl, Radius));
    }

    [Fact]
    public void CorpseReachedRadius_IsTighterThanPerception()
    {
        // The reached radius gates the bearing-suppression + the death-location
        // clear; it must be much tighter than the 120u perception radius so the
        // bearing keeps directing the bot toward an out-of-view corpse.
        Assert.True(CorpseRecovery.CorpseReachedRadiusUnits > 0f);
        Assert.True(CorpseRecovery.CorpseReachedRadiusUnits
            < WorldStateProjection.DefaultVisibleRadiusUnits);
    }

    [Fact]
    public void ShouldSurfaceCorpse_TightReachedRadius_SurfacesWithinPerceptionButBeyondReach()
    {
        // Production passes the TIGHT reached radius (not 120u). A bot 50u from the
        // death loc is within the old perception radius but well beyond the reached
        // radius -> the bearing SURFACES (previously suppressed -> the handoff gap
        // where the bot lost the bearing while the corpse was not yet in view).
        var reached = CorpseRecovery.CorpseReachedRadiusUnits;
        Assert.True(CorpseRecovery.ShouldSurfaceCorpse(
            FreshDeath(), (150f, 200f), TimeSpan.FromSeconds(30), Ttl, reached));
        // At the corpse (within the reached radius) -> suppressed.
        Assert.False(CorpseRecovery.ShouldSurfaceCorpse(
            FreshDeath(), (104f, 200f), TimeSpan.FromSeconds(30), Ttl, reached));
    }

    [Fact]
    public void ShouldSurfaceCorpse_AgedPastTtl_False()
    {
        Assert.False(CorpseRecovery.ShouldSurfaceCorpse(FreshDeath(), (5000f, 5000f), TimeSpan.FromSeconds(601), Ttl, Radius));
    }

    [Fact]
    public void ShouldSurfaceCorpse_NegativeAge_False()
    {
        // Clock skew (age < 0) is treated as not-surfaceable.
        Assert.False(CorpseRecovery.ShouldSurfaceCorpse(FreshDeath(), (5000f, 5000f), TimeSpan.FromSeconds(-1), Ttl, Radius));
    }

    [Fact]
    public void ShouldSurfaceCorpse_UnknownCurrentPosition_True()
    {
        // Current position unknown -> cannot prove the bot is near, so surface.
        Assert.True(CorpseRecovery.ShouldSurfaceCorpse(FreshDeath(), null, TimeSpan.FromSeconds(30), Ttl, Radius));
    }

    [Theory]
    [InlineData(100f, 200f, true)]    // exactly on the corpse
    [InlineData(210f, 200f, true)]    // 110u away (<= 120 radius)
    [InlineData(100f, 320f, true)]    // exactly at the radius (120u)
    [InlineData(300f, 200f, false)]   // 200u away (> radius)
    public void WithinReach_UsesSquaredDistanceVsRadius(float cx, float cy, bool expected)
    {
        var death = new DeathLocation(100f, 200f, 0x00A9u, DateTimeOffset.UtcNow);
        Assert.Equal(expected, CorpseRecovery.WithinReach((cx, cy), death, Radius));
    }
}
