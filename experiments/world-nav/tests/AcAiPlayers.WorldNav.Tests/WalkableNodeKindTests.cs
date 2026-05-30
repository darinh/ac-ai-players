// SPDX-License-Identifier: AGPL-3.0-or-later
// WalkableNodeKindTests — pin the wire-numeric values + the
// existence of the kind variants the walk-tick contract depends
// on. The enum is consumed by both the static LandblockNavLoader
// (Floor + Doorway) and (future) the runtime nav-graph
// augmentation slice (Portal). Stable numeric values matter
// because reference-svg / future-debug-dumps key on them.

using AcAiPlayers.WorldNav;
using Xunit;

namespace AcAiPlayers.WorldNav.Tests;

public class WalkableNodeKindTests
{
    [Fact]
    public void Floor_IsZero()
    {
        Assert.Equal(0, (int)WalkableNodeKind.Floor);
    }

    [Fact]
    public void Doorway_IsOne()
    {
        Assert.Equal(1, (int)WalkableNodeKind.Doorway);
    }

    [Fact]
    public void Portal_IsTwo()
    {
        // Reserved for the runtime nav-graph augmentation slice.
        // LandblockNavLoader never emits Portal kinds today —
        // portals live in the ACE world DB, not the client DAT
        // files — but the consumer-side walk-tick must be able
        // to round-trip the enum value without re-numbering when
        // the runtime injector lands.
        Assert.Equal(2, (int)WalkableNodeKind.Portal);
    }

    [Fact]
    public void Portal_IsNotDoorwayOrFloor()
    {
        Assert.NotEqual(WalkableNodeKind.Floor, WalkableNodeKind.Portal);
        Assert.NotEqual(WalkableNodeKind.Doorway, WalkableNodeKind.Portal);
    }
}
