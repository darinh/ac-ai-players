// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit coverage for the FellowshipRecruit target-resolution contract
// (SelectorResolver.ResolveUniquePlayerOtherThanActor): a player-directed invite
// must resolve to EXACTLY ONE matching PLAYER other than self, else Fail. Since the
// recruit dispatch cannot be live-validated with a single bot, this resolution path
// is the only safety net, so it is pinned here directly.

using HeadlessAcClient.Strategy;
using HeadlessAcClient.Tactics;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class FellowshipRecruitResolveTests
{
    private const uint SelfGuid    = 0x50000005u; // player band
    private const uint PlayerAGuid = 0x50000101u; // player band
    private const uint PlayerBGuid = 0x50000202u; // player band
    private const uint NpcGuid     = 0x90000010u; // NOT player band

    private static WorldState NewWorldWithSelf(string selfName = "Hipin")
    {
        var ws = new WorldState();
        // Seed self into Objects so ws.Self is non-null; cellId 0 => the resolver's
        // landblock filter is bypassed (actor has no cell), isolating the player logic.
        SnapshotSeeding.Seed(ws, SelfGuid, selfName, wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);
        ws.SetSelf(SelfGuid);
        return ws;
    }

    [Fact]
    public void ResolveUniquePlayer_SinglePlayerByName_Resolves()
    {
        var ws = NewWorldWithSelf();
        SnapshotSeeding.Seed(ws, PlayerAGuid, "Galad", wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);
        SnapshotSeeding.Seed(ws, NpcGuid, "Jonathan", wcid: 29324u, itemType: 0x10u, cellId: 0u, containerGuid: null);

        var player = SelectorResolver.ResolveUniquePlayerOtherThanActor(
            new Selector { Name = "Galad" }, ws, ws.Self!, out var n);

        Assert.NotNull(player);
        Assert.Equal(PlayerAGuid, player!.Guid);
        Assert.Equal(1, n);
    }

    [Fact]
    public void ResolveUniquePlayer_NonPlayerMatch_ReturnsNull()
    {
        var ws = NewWorldWithSelf();
        SnapshotSeeding.Seed(ws, NpcGuid, "Jonathan", wcid: 29324u, itemType: 0x10u, cellId: 0u, containerGuid: null);

        // The only object matching the name is an NPC (guid outside the player band).
        var player = SelectorResolver.ResolveUniquePlayerOtherThanActor(
            new Selector { Name = "Jonathan" }, ws, ws.Self!, out var n);

        Assert.Null(player);
        Assert.Equal(0, n);
    }

    [Fact]
    public void ResolveUniquePlayer_AmbiguousPlayers_ReturnsNullWithCount()
    {
        var ws = NewWorldWithSelf();
        SnapshotSeeding.Seed(ws, PlayerAGuid, "Adventurer", wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);
        SnapshotSeeding.Seed(ws, PlayerBGuid, "Adventurer", wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);

        // Two players share the name -> ambiguous -> Fail (never pick the nearest).
        var player = SelectorResolver.ResolveUniquePlayerOtherThanActor(
            new Selector { Name = "Adventurer" }, ws, ws.Self!, out var n);

        Assert.Null(player);
        Assert.Equal(2, n);
    }

    [Fact]
    public void ResolveUniquePlayer_SelfMatches_ExcludedReturnsOtherPlayer()
    {
        // Self is a player-band guid; a selector that ALSO matches self (e.g. shares a
        // name) must NOT resolve to self (you cannot recruit yourself) but to the OTHER
        // player. Guards the self-at-distance-0 pitfall of a nearest-pick.
        var ws = NewWorldWithSelf(selfName: "Echo");
        SnapshotSeeding.Seed(ws, PlayerAGuid, "Echo", wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);

        var player = SelectorResolver.ResolveUniquePlayerOtherThanActor(
            new Selector { Name = "Echo" }, ws, ws.Self!, out var n);

        Assert.NotNull(player);
        Assert.Equal(PlayerAGuid, player!.Guid);
        Assert.Equal(1, n);
    }

    [Fact]
    public void ResolveUniquePlayer_OnlySelfMatches_ReturnsNull()
    {
        // If the ONLY match is self, there is no one to recruit -> Fail (count 0).
        var ws = NewWorldWithSelf(selfName: "Solo");

        var player = SelectorResolver.ResolveUniquePlayerOtherThanActor(
            new Selector { Name = "Solo" }, ws, ws.Self!, out var n);

        Assert.Null(player);
        Assert.Equal(0, n);
    }

    [Fact]
    public void ResolveUniquePlayer_EmptySelector_ReturnsNull()
    {
        var ws = NewWorldWithSelf();
        SnapshotSeeding.Seed(ws, PlayerAGuid, "Galad", wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);

        var player = SelectorResolver.ResolveUniquePlayerOtherThanActor(
            new Selector(), ws, ws.Self!, out var n);

        Assert.Null(player);
        Assert.Equal(0, n);
    }

    [Fact]
    public void ResolveUniquePlayer_FuzzyAmbiguousPartial_ReturnsNullCountZero()
    {
        // Two players match a partial name only via fuzzy whole-word subsequence
        // (no exact match). Resolve INTENTIONALLY returns empty for an ambiguous
        // fuzzy partial (it never snaps to one of several), so the helper reports
        // matchCount 0 ("no match"), not 2 -- the safe outcome (Fail; the LLM
        // re-decides with a sharper name) regardless of the count label.
        var ws = NewWorldWithSelf();
        SnapshotSeeding.Seed(ws, PlayerAGuid, "Galad Stormrider", wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);
        SnapshotSeeding.Seed(ws, PlayerBGuid, "Galad Brightblade", wcid: 1u, itemType: 0x10u, cellId: 0u, containerGuid: null);

        var player = SelectorResolver.ResolveUniquePlayerOtherThanActor(
            new Selector { Name = "Galad" }, ws, ws.Self!, out var n);

        Assert.Null(player);
        Assert.Equal(0, n);
    }
}
