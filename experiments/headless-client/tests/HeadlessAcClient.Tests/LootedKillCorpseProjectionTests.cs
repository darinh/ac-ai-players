// SPDX-License-Identifier: AGPL-3.0-or-later
// LootedKillCorpseProjection unit tests (cp-2358). The projection surfaces the
// bot's OWN recently-emptied kill corpses (guid -> name + time) so the prompt
// can stop presenting an already-looted corpse as an actionable loot target.
// Synthetic opaque names are used deliberately (no game proper nouns in source).

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class LootedKillCorpseProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    private static IReadOnlyList<LootedCorpse> Run(
        Dictionary<uint, (string Name, DateTimeOffset At)> emptied, int max = 3) =>
        LootedKillCorpseProjection.Compute(emptied, Now, Window, max);

    [Fact]
    public void Surfaces_RecentlyEmptiedCorpse()
    {
        var r = Run(new() { [0x900] = ("husk-alpha", Now) });
        Assert.Single(r);
        Assert.Equal(0x900u, r[0].Guid);
        Assert.Equal("husk-alpha", r[0].Name);
    }

    [Fact]
    public void Excludes_StaleEmptiedCorpse_PastWindow()
    {
        var r = Run(new() { [0x900] = ("husk-alpha", Now - TimeSpan.FromSeconds(120)) });
        Assert.Empty(r);
    }

    [Fact]
    public void Excludes_EntryWithEmptyName()
    {
        var r = Run(new() { [0x900] = ("", Now) });
        Assert.Empty(r);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Run(new()));
    }

    [Fact]
    public void OrdersNewestFirst_AndCapsToMaxResults()
    {
        var r = Run(new()
        {
            [0x901] = ("husk-old", Now - TimeSpan.FromSeconds(40)),
            [0x902] = ("husk-new", Now - TimeSpan.FromSeconds(1)),
            [0x903] = ("husk-mid", Now - TimeSpan.FromSeconds(20)),
        }, max: 2);
        Assert.Equal(2, r.Count);
        Assert.Equal(0x902u, r[0].Guid); // newest
        Assert.Equal(0x903u, r[1].Guid);
    }

    [Fact]
    public void KeepsDistinctGuids_SharingAName()
    {
        // Two distinct corpses that happen to share a name are both kept (the
        // suppression set needs every guid; the render dedupes by name).
        var r = Run(new()
        {
            [0x901] = ("husk-twin", Now),
            [0x902] = ("husk-twin", Now - TimeSpan.FromSeconds(5)),
        });
        Assert.Equal(2, r.Count);
        Assert.Equal(2, r.Select(c => c.Guid).Distinct().Count());
    }
}
