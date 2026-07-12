// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for LlmGoalPolicy.TryRememberedTargetDistanceUnits: the pure helper that
// measures the bot→remembered-target straight-line distance so the goal-progress
// trend can keep sampling during a walk toward an object-pursuit target that is
// not currently in view but IS in the bot's own sighting memory. It returns null
// when memory holds no single unambiguous location for the selector (no match, or
// matches spanning more than one landblock). Names/coords are placeholders.
// reduce-llm-call-volume.

using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RememberedApproachDistanceTests
{
    private static SightedRecallProjection Sight(
        string name, float x, float y, uint landblock = 0x00A9,
        double age = 5.0, uint? wcid = null) =>
        new()
        {
            Name = name,
            Wcid = wcid,
            Kind = EntityKind.Mob,
            Landblock = landblock,
            WorldX = x,
            WorldY = y,
            AgeSeconds = age,
        };

    [Fact]
    public void NullSightings_Null() =>
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, null, 0, 0));

    [Fact]
    public void EmptySightings_Null() =>
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, new List<SightedRecallProjection>(), 0, 0));

    [Fact]
    public void NoMatchingName_Null()
    {
        var sights = new List<SightedRecallProjection> { Sight("Other Beast", 3, 4) };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 0, 0));
    }

    [Fact]
    public void SingleMatch_ReturnsDistanceAndLabel()
    {
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 3, 4) };
        var r = LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 0, 0);
        Assert.NotNull(r);
        Assert.Equal(5.0f, r!.Value.DistanceUnits, 3); // 3-4-5 triangle
        Assert.Equal("Quarry Beast", r.Value.Label);
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 6, 8) };
        var r = LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "quarry beast" }, sights, 0, 0);
        Assert.NotNull(r);
        Assert.Equal(10.0f, r!.Value.DistanceUnits, 3);
    }

    [Fact]
    public void SelfOffsetSubtracted()
    {
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 13, 14) };
        // self at (10,10) -> delta (3,4) -> 5
        var r = LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 10, 10);
        Assert.NotNull(r);
        Assert.Equal(5.0f, r!.Value.DistanceUnits, 3);
    }

    [Fact]
    public void SameLandblockMultipleMatches_DeclinesNull()
    {
        // Two distinct remembered entities match the selector in one landblock:
        // the bot can walk to only one, so the distance is ambiguous -> decline.
        var sights = new List<SightedRecallProjection>
        {
            Sight("Quarry Beast", 30, 40, landblock: 0x00A9, age: 50.0),
            Sight("Quarry Beast", 3, 4,   landblock: 0x00A9, age: 2.0),
        };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 0, 0));
    }

    [Fact]
    public void StaleSighting_BeyondTtl_Null()
    {
        // A single match older than the recall TTL (180s) is not trustworthy
        // enough to suppress on — the LLM's prompt has already dropped it.
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 3, 4, age: 200.0) };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 0, 0));
    }

    [Fact]
    public void StaleDuplicateFiltered_LeavesOneFreshMatch()
    {
        // A stale same-name row (past TTL) is filtered BEFORE ambiguity counting,
        // so a single fresh match still resolves (the stale row does not force a
        // spurious multi-match decline).
        var sights = new List<SightedRecallProjection>
        {
            Sight("Quarry Beast", 3, 4,   landblock: 0x00A9, age: 5.0),   // fresh
            Sight("Quarry Beast", 300, 0, landblock: 0x00B1, age: 300.0), // stale, filtered
        };
        var r = LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 0, 0);
        Assert.NotNull(r);
        Assert.Equal(5.0f, r!.Value.DistanceUnits, 3);
    }

    [Fact]
    public void MultiLandblockMatches_DeclinesNull()
    {
        var sights = new List<SightedRecallProjection>
        {
            Sight("Quarry Beast", 3, 4,  landblock: 0x00A9),
            Sight("Quarry Beast", 30, 40, landblock: 0x00B1), // different landblock -> ambiguous
        };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 0, 0));
    }

    [Fact]
    public void MultiLandblockMatches_DeclinesRegardlessOfOrder()
    {
        var sights = new List<SightedRecallProjection>
        {
            Sight("Quarry Beast", 30, 40, landblock: 0x00B1),
            Sight("Quarry Beast", 3, 4,  landblock: 0x00A9),
        };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Quarry Beast" }, sights, 0, 0));
    }

    [Fact]
    public void GuidSelector_Null()
    {
        // A recall row carries no guid, so a guid-qualified selector cannot be
        // confirmed against memory (the visible-object path owns guid matching).
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 3, 4) };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Guid = 0x50000123, Name = "Quarry Beast" }, sights, 0, 0));
    }

    [Fact]
    public void EmptySelector_Null()
    {
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 3, 4) };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector(), sights, 0, 0));
    }

    [Fact]
    public void NameContainsMatch()
    {
        var sights = new List<SightedRecallProjection> { Sight("Great Quarry Beast", 3, 4) };
        var r = LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { NameContains = "quarry" }, sights, 0, 0);
        Assert.NotNull(r);
        Assert.Equal(5.0f, r!.Value.DistanceUnits, 3);
    }

    [Fact]
    public void WcidMatch()
    {
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 3, 4, wcid: 4242) };
        var r = LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Wcid = 4242 }, sights, 0, 0);
        Assert.NotNull(r);
        Assert.Equal(5.0f, r!.Value.DistanceUnits, 3);
    }

    [Fact]
    public void WcidMismatch_Null()
    {
        var sights = new List<SightedRecallProjection> { Sight("Quarry Beast", 3, 4, wcid: 4242) };
        Assert.Null(LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Wcid = 9999 }, sights, 0, 0));
    }

    [Fact]
    public void TrailingQuotedRoleSuffix_Tolerated()
    {
        // The model often copies the whole `Name "role"` prompt label into a
        // selector; the bare name must still match the recall row.
        var sights = new List<SightedRecallProjection> { Sight("Town Crier", 3, 4) };
        var r = LlmGoalPolicy.TryRememberedTargetDistanceUnits(
            new Selector { Name = "Town Crier \"the herald\"" }, sights, 0, 0);
        Assert.NotNull(r);
        Assert.Equal(5.0f, r!.Value.DistanceUnits, 3);
    }
}
