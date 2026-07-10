// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for RecallEscape — the pure "did a dispatched Recall land?" decision
// that gates the synthetic recall-did-not-land rejection. The stamp must fire
// ONLY on a CONFIDENTLY observed non-land (both poses known, same cell, no
// move); a landed teleport (cell change OR position move, incl. a same-cell
// lifestone) or a null/unknown pose must NOT be recorded as refused.

using System.Numerics;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class RecallEscapeTests
{
    private static readonly Vector3 A = new(10f, 20f, 30f);

    [Fact]
    public void ReservedCode_MatchesOnlyItself()
    {
        Assert.True(RecallEscape.IsRecallDidNotLandRejection(RecallEscape.RecallDidNotLandRejectionCode));
        Assert.False(RecallEscape.IsRecallDidNotLandRejection(0x046Au));
        Assert.False(RecallEscape.IsRecallDidNotLandRejection(null));
    }

    [Fact]
    public void SameCell_NoMove_DidNotLand_True()
    {
        // Refused / no-teleport: same cell id, position essentially unchanged.
        Assert.True(RecallEscape.RecallConfidentlyDidNotLand(0x1234_0005u, A, 0x1234_0005u, A));
    }

    [Fact]
    public void CellChanged_Landed_False()
    {
        Assert.False(RecallEscape.RecallConfidentlyDidNotLand(0x1234_0005u, A, 0x9999_0000u, A));
    }

    [Fact]
    public void SameCell_MovedFar_Landed_False()
    {
        // A lifestone in the same cell: cell id unchanged but the bot moved far
        // -> a real teleport, must NOT be flagged as a non-land.
        var far = new Vector3(A.X + 50f, A.Y, A.Z);
        Assert.False(RecallEscape.RecallConfidentlyDidNotLand(0x1234_0005u, A, 0x1234_0005u, far));
    }

    [Theory]
    [InlineData(0u)]        // dispatch cell unknown
    public void UnknownDispatchCell_Inconclusive_False(uint dispatchCell)
    {
        Assert.False(RecallEscape.RecallConfidentlyDidNotLand(dispatchCell, A, 0x1234_0005u, A));
    }

    [Fact]
    public void UnknownNowCell_Inconclusive_False()
    {
        Assert.False(RecallEscape.RecallConfidentlyDidNotLand(0x1234_0005u, A, 0u, A));
    }

    [Fact]
    public void NullDispatchPos_Inconclusive_False()
    {
        Assert.False(RecallEscape.RecallConfidentlyDidNotLand(0x1234_0005u, null, 0x1234_0005u, A));
    }

    [Fact]
    public void NullNowPos_Inconclusive_False()
    {
        Assert.False(RecallEscape.RecallConfidentlyDidNotLand(0x1234_0005u, A, 0x1234_0005u, null));
    }
}
