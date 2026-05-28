// SPDX-License-Identifier: AGPL-3.0-or-later
// Wrap-aware sequence compare tests. The forward-distance trick
// must accept wrap-forward (e.g. 0 after 65535) and reject
// wrap-backward (e.g. 65535 after 0). Equal is always accepted
// because the server may redundantly re-send the same sequence.

using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SequenceCompareTests
{
    // ---- ushort ----

    [Theory]
    [InlineData(0, 0)]            // equal accepted
    [InlineData(1, 0)]            // forward by 1
    [InlineData(32767, 0)]        // just inside window
    [InlineData(0, 65535)]        // wrap forward by 1
    [InlineData(1, 65535)]        // wrap forward by 2
    [InlineData(100, 65500)]      // wrap forward
    public void IsCurrentOrNewer_AcceptsCurrentAndForward(int incoming, int current)
        => Assert.True(SequenceCompare.IsCurrentOrNewer((ushort)incoming, (ushort)current));

    [Theory]
    [InlineData(0, 1)]            // backward by 1
    [InlineData(0, 32768)]        // at-window-boundary backward
    [InlineData(65535, 0)]        // wrap backward
    [InlineData(32768, 0)]        // exactly at the wrap window (NOT accepted: < window)
    public void IsCurrentOrNewer_RejectsBackward(int incoming, int current)
        => Assert.False(SequenceCompare.IsCurrentOrNewer((ushort)incoming, (ushort)current));

    [Fact]
    public void IsCurrentOrNewer_NullCurrent_AcceptsAnyIncoming()
    {
        Assert.True(SequenceCompare.IsCurrentOrNewer((ushort)0, (ushort?)null));
        Assert.True(SequenceCompare.IsCurrentOrNewer((ushort)42, (ushort?)null));
        Assert.True(SequenceCompare.IsCurrentOrNewer(ushort.MaxValue, (ushort?)null));
    }

    [Fact]
    public void IsStrictlyNewer_EqualRejected()
        => Assert.False(SequenceCompare.IsStrictlyNewer(7, 7));

    [Fact]
    public void IsStrictlyNewer_ForwardAccepted()
    {
        Assert.True(SequenceCompare.IsStrictlyNewer(8, 7));
        Assert.True(SequenceCompare.IsStrictlyNewer(0, 65535));
    }

    [Fact]
    public void IsStrictlyNewer_BackwardRejected()
    {
        Assert.False(SequenceCompare.IsStrictlyNewer(6, 7));
        Assert.False(SequenceCompare.IsStrictlyNewer(65535, 0));
    }

    // ---- byte ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(127, 0)]        // just inside byte window
    [InlineData(0, 255)]        // wrap forward
    public void Byte_IsCurrentOrNewer_AcceptsCurrentAndForward(int incoming, int current)
        => Assert.True(SequenceCompare.IsCurrentOrNewer((byte)incoming, (byte)current));

    [Theory]
    [InlineData(255, 0)]        // wrap backward
    [InlineData(128, 0)]        // exactly at the byte wrap window
    public void Byte_IsCurrentOrNewer_RejectsBackward(int incoming, int current)
        => Assert.False(SequenceCompare.IsCurrentOrNewer((byte)incoming, (byte)current));

    [Fact]
    public void Byte_IsCurrentOrNewer_NullCurrent_AcceptsAny()
    {
        Assert.True(SequenceCompare.IsCurrentOrNewer((byte)0, (byte?)null));
        Assert.True(SequenceCompare.IsCurrentOrNewer((byte)255, (byte?)null));
    }
}
