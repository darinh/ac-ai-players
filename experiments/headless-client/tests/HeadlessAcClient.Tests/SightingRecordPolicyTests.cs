// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for SightingRecordPolicy — the per-memory recording decision
// for an observed ObjectCreate, keyed on landblock distance.
//
// Three cases (the regression-prone gate-split surface):
//   - same landblock        => RecordObservation + RecordSightedLocation,
//                              observer node anchored
//   - adjacent landblock    => RecordSightedLocation only, NO observer node
//                              (and NO node-relative observation)
//   - two-or-more away / far => neither (covers the post-teleport stale-node
//                              case: a far destination object is dropped)

using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SightingRecordPolicyTests
{
    private const uint SelfCell = 0x12340001; // landblock (0x12, 0x34)

    [Fact]
    public void SameLandblock_RecordsBoth_WithObserverNode()
    {
        // Different cell index, same landblock.
        var d = SightingRecordPolicy.Decide(0x12340040u, SelfCell);
        Assert.True(d.RecordObservation);
        Assert.True(d.RecordSightedLocation);
        Assert.True(d.AnchorObserverNode);
    }

    [Theory]
    [InlineData(0x13340001u)] // E
    [InlineData(0x11340001u)] // W
    [InlineData(0x12350001u)] // N
    [InlineData(0x12330001u)] // S
    [InlineData(0x13350001u)] // NE
    [InlineData(0x11350001u)] // NW
    [InlineData(0x13330001u)] // SE
    [InlineData(0x11330001u)] // SW
    public void AdjacentLandblock_RecordsSightingOnly_NoObserverNode(uint objectCell)
    {
        var d = SightingRecordPolicy.Decide(objectCell, SelfCell);
        Assert.False(d.RecordObservation);     // node-relative frame would be wrong
        Assert.True(d.RecordSightedLocation);  // absolute memory the resolver needs
        Assert.False(d.AnchorObserverNode);    // different landblock frame
    }

    [Theory]
    [InlineData(0x14340001u)] // two east
    [InlineData(0x12360001u)] // two north
    [InlineData(0x14360001u)] // two diagonal
    [InlineData(0xA8B40001u)] // far/unrelated (e.g. stale post-teleport object)
    public void FarLandblock_RecordsNeither(uint objectCell)
    {
        var d = SightingRecordPolicy.Decide(objectCell, SelfCell);
        Assert.False(d.RecordObservation);
        Assert.False(d.RecordSightedLocation);
        Assert.False(d.AnchorObserverNode);
    }

    [Fact]
    public void Decide_IgnoresLowCellIndexOfSelf()
    {
        // Passing a full self cell id vs the masked landblock must agree.
        var fromCell = SightingRecordPolicy.Decide(0x12350001u, 0x1234003Fu);
        var fromMask = SightingRecordPolicy.Decide(0x12350001u, 0x12340000u);
        Assert.Equal(fromMask, fromCell);
    }
}
