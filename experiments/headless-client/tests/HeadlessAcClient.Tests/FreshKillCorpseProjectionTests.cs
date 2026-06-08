// SPDX-License-Identifier: AGPL-3.0-or-later
// FreshKillCorpseProjection unit tests (cp-2357). Audit-safe invariants: the
// projection surfaces ONLY the bot's OWN fresh, unlooted kill corpse — a
// visible Corpse-flagged object at a recent kill SITE (proximity correlation,
// not opened, within range). No name parsing, no priority.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class FreshKillCorpseProjectionTests
{
    private const uint SelfGuid = 0x50000005;
    private const uint CellId = 0x12340001;
    private const uint FlagCorpse = (uint)ObjectDescriptionFlag.Corpse;

    private static WorldObjectSnapshot Self() =>
        new(SelfGuid) { CellId = CellId, Position = Vector3.Zero, Name = "Bot" };

    private static WorldObjectSnapshot Corpse(uint guid, string name, float x, uint? descFlags = FlagCorpse) =>
        new(guid) { Name = name, CellId = CellId, Position = new Vector3(x, 0, 0), ObjectDescriptionFlags = descFlags };

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    // A kill at a given in-cell position (the kill site = where the corpse spawns).
    private static RecentKill KillAt(float x, DateTimeOffset? at = null)
    {
        var (gx, gy) = AcCoords.ToGlobalXY(CellId, new Vector3(x, 0, 0));
        return new RecentKill(gx, gy, at ?? Now);
    }

    private static IReadOnlyList<FreshKillCorpse> Run(
        IEnumerable<WorldObjectSnapshot> visible,
        IReadOnlyCollection<RecentKill> kills,
        Func<uint, bool>? opened = null, float killRadius = 8f, float maxDist = 60f, int max = 3) =>
        FreshKillCorpseProjection.Compute(
            visible, Self(), kills, opened ?? (_ => false), Now, Window, killRadius, maxDist, max);

    [Fact]
    public void Matches_CorpseAtOwnKillSite()
    {
        var corpse = Corpse(0x800, "Corpse of Cow", x: 5f);
        var r = Run(new[] { corpse }, new[] { KillAt(5f) });
        Assert.Single(r);
        Assert.Equal(0x800u, r[0].Guid);
        Assert.Equal("Corpse of Cow", r[0].Name);
    }

    [Fact]
    public void ExcludesCorpse_FarFromAnyKillSite()
    {
        // Corpse at x=5 (within self range) but the only kill site is at x=50 —
        // 45u away, beyond the kill-match radius, so it is NOT the bot's own kill.
        var corpse = Corpse(0x800, "Corpse of Cow", x: 5f);
        Assert.Empty(Run(new[] { corpse }, new[] { KillAt(50f) }, killRadius: 8f));
    }

    [Fact]
    public void ExcludesOpenedCorpse()
    {
        var corpse = Corpse(0x800, "Corpse of Cow", x: 5f);
        Assert.Empty(Run(new[] { corpse }, new[] { KillAt(5f) }, opened: g => g == 0x800u));
    }

    [Fact]
    public void ExcludesStaleKill_PastRecencyWindow()
    {
        var corpse = Corpse(0x800, "Corpse of Cow", x: 5f);
        var stale = KillAt(5f, Now - TimeSpan.FromSeconds(120)); // > 90s
        Assert.Empty(Run(new[] { corpse }, new[] { stale }));
    }

    [Fact]
    public void ExcludesNonCorpseObject()
    {
        var notCorpse = Corpse(0x800, "Corpse of Cow", x: 5f, descFlags: null); // no Corpse flag
        Assert.Empty(Run(new[] { notCorpse }, new[] { KillAt(5f) }));
    }

    [Fact]
    public void ExcludesFarCorpse_BeyondMaxDistanceFromSelf()
    {
        // Corpse 100u from self (and there is a kill at that site) — too far to
        // be actionable, so omitted.
        var corpse = Corpse(0x800, "Corpse of Cow", x: 100f);
        Assert.Empty(Run(new[] { corpse }, new[] { KillAt(100f) }, maxDist: 60f));
    }

    [Fact]
    public void NoRecentKills_ReturnsEmpty()
    {
        var corpse = Corpse(0x800, "Corpse of Cow", x: 5f);
        Assert.Empty(Run(new[] { corpse }, Array.Empty<RecentKill>()));
    }

    [Fact]
    public void OrdersNearestFirst_AndCapsToMaxResults()
    {
        var far = Corpse(0x801, "Corpse of Cow", x: 30f);
        var near = Corpse(0x802, "Corpse of Cow", x: 3f);
        var mid = Corpse(0x803, "Corpse of Cow", x: 10f);
        var kills = new[] { KillAt(30f), KillAt(3f), KillAt(10f) };
        var r = Run(new[] { far, near, mid }, kills, max: 2);
        Assert.Equal(2, r.Count);
        Assert.Equal(0x802u, r[0].Guid); // nearest
        Assert.Equal(0x803u, r[1].Guid);
    }
}
