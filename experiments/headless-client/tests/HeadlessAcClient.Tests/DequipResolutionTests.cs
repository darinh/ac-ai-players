// SPDX-License-Identifier: AGPL-3.0-or-later

using HeadlessAcClient.Strategy;
using HeadlessAcClient.Tactics;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class DequipResolutionTests
{
    private const uint SelfGuid = 0x50000005u;

    private static WorldState NewWorld()
    {
        var world = new WorldState();
        SnapshotSeeding.Seed(
            world,
            SelfGuid,
            "Self",
            wcid: 1u,
            itemType: 0x10u,
            cellId: 0u,
            containerGuid: null);
        world.SetSelf(SelfGuid);
        return world;
    }

    private static WorldObjectSnapshot SeedItem(
        WorldState world,
        uint guid,
        string name,
        bool wielded)
    {
        SnapshotSeeding.Seed(
            world,
            guid,
            name,
            wcid: guid,
            itemType: 0x100u,
            cellId: 0u,
            containerGuid: wielded ? null : SelfGuid);
        var item = world.TryGet(guid)!;
        if (wielded)
        {
            item.WielderGuid = SelfGuid;
            item.CurrentWieldedLocation = 0x400000u;
        }
        return item;
    }

    [Fact]
    public void ExactEquippedMatch_ResolvesEvenWhenBagHasSameName()
    {
        var world = NewWorld();
        var equipped = SeedItem(world, 0x80000101u, "Fixture Launcher", wielded: true);
        SeedItem(world, 0x80000102u, "Fixture Launcher", wielded: false);

        var result = SelectorResolver.ResolveUniqueWieldedByActor(
            new Selector { Name = "Fixture Launcher" },
            world,
            SelfGuid,
            weenies: null,
            out var count);

        Assert.Same(equipped, result);
        Assert.Equal(1, count);
    }

    [Fact]
    public void BagOnlyMatch_DoesNotResolve()
    {
        var world = NewWorld();
        SeedItem(world, 0x80000101u, "Fixture Launcher", wielded: false);

        var result = SelectorResolver.ResolveUniqueWieldedByActor(
            new Selector { Name = "Fixture Launcher" },
            world,
            SelfGuid,
            weenies: null,
            out var count);

        Assert.Null(result);
        Assert.Equal(0, count);
    }

    [Fact]
    public void MultipleEquippedMatches_AreRejectedAsAmbiguous()
    {
        var world = NewWorld();
        SeedItem(world, 0x80000101u, "Fixture Launcher", wielded: true);
        SeedItem(world, 0x80000102u, "Fixture Launcher", wielded: true);

        var result = SelectorResolver.ResolveUniqueWieldedByActor(
            new Selector { Name = "Fixture Launcher" },
            world,
            SelfGuid,
            weenies: null,
            out var count);

        Assert.Null(result);
        Assert.Equal(2, count);
    }
}
