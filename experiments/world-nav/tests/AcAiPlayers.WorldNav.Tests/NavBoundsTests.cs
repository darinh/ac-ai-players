// SPDX-License-Identifier: AGPL-3.0-or-later
// Pure data-model tests that don't touch DAT files.

using System.Numerics;
using AcAiPlayers.WorldNav;
using Xunit;

namespace AcAiPlayers.WorldNav.Tests;

public class NavBoundsTests
{
    [Fact]
    public void FromPoints_ComputesAxisAlignedBoundsAcrossAllAxes()
    {
        var points = new[]
        {
            new Vector3(1, 2, 3),
            new Vector3(-4, 5, 6),
            new Vector3(7, -8, 9),
        };

        var bounds = NavBounds.FromPoints(points);

        Assert.Equal(-4f, bounds.MinX);
        Assert.Equal(7f, bounds.MaxX);
        Assert.Equal(-8f, bounds.MinY);
        Assert.Equal(5f, bounds.MaxY);
        Assert.Equal(3f, bounds.MinZ);
        Assert.Equal(9f, bounds.MaxZ);
        Assert.Equal(11f, bounds.Width);
        Assert.Equal(13f, bounds.Height);
    }

    [Fact]
    public void Union_ProducesEnclosingBox()
    {
        var a = new NavBounds(0, 0, 10, 10, 0, 5);
        var b = new NavBounds(5, -3, 20, 4, 2, 9);

        var u = a.Union(b);

        Assert.Equal(0f, u.MinX);
        Assert.Equal(20f, u.MaxX);
        Assert.Equal(-3f, u.MinY);
        Assert.Equal(10f, u.MaxY);
        Assert.Equal(0f, u.MinZ);
        Assert.Equal(9f, u.MaxZ);
    }
}
