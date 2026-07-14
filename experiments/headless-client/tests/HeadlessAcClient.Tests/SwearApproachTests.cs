// SPDX-License-Identifier: AGPL-3.0-or-later

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SwearApproachTests
{
    [Fact]
    public void RequiresWalk_UnknownDistance_PreservesImmediateDispatch()
        => Assert.False(SwearApproach.RequiresWalk(null, rangeUnits: 2f));

    [Theory]
    [InlineData(4f, false)]
    [InlineData(4.01f, true)]
    public void RequiresWalk_UsesInclusiveRangeBoundary(float distanceSquared, bool expected)
        => Assert.Equal(expected, SwearApproach.RequiresWalk(distanceSquared, rangeUnits: 2f));

    [Fact]
    public void IsConfirmedInRange_UnknownDistance_DoesNotDispatchAfterWalk()
        => Assert.False(SwearApproach.IsConfirmedInRange(null, rangeUnits: 2f));

    [Theory]
    [InlineData(4f, true)]
    [InlineData(4.01f, false)]
    public void IsConfirmedInRange_UsesInclusiveRangeBoundary(float distanceSquared, bool expected)
        => Assert.Equal(expected, SwearApproach.IsConfirmedInRange(distanceSquared, rangeUnits: 2f));
}
