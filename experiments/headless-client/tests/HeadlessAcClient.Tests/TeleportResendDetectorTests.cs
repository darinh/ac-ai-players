// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for HandshakeDriver.TeleportOccurredSinceLoginComplete —
// the pure decision the intra-landblock teleport detector uses to know
// when the server-side Teleporting flag was re-set by a teleport that
// happened AFTER our last LoginComplete (teleport-resend-loginc).
//
// The detector lives inside the giant async receive loop and cannot be
// unit-tested directly, but this predicate carries the tricky logic:
// instance-epoch awareness (a new epoch resets the per-epoch teleport
// counter to a low value), null-ack seeding, wrap-aware comparison, and
// the no-false-positive guarantee for ordinary movement.

using HeadlessAcClient.Protocol;
using Xunit;

namespace HeadlessAcClient.Tests;

public class TeleportResendDetectorTests
{
    private static bool Occurred(
        ushort? curInst, ushort? curTele, ushort? ackInst, ushort? ackTele)
        => HandshakeDriver.TeleportOccurredSinceLoginComplete(
            curInst, curTele, ackInst, ackTele);

    [Fact]
    public void NoAdvance_SameInstanceSameTeleport_DoesNotResend()
    {
        // The steady state immediately after a LoginComplete: nothing moved.
        Assert.False(Occurred(curInst: 5, curTele: 3, ackInst: 5, ackTele: 3));
    }

    [Fact]
    public void IntraEpochTeleportAdvance_Resends()
    {
        // The mini-observe freeze: same instance epoch, teleport counter
        // advances 0 -> 1 after the initial LoginComplete.
        Assert.True(Occurred(curInst: 5, curTele: 1, ackInst: 5, ackTele: 0));
    }

    [Fact]
    public void NewInstanceEpoch_WithLowerTeleportCounter_StillResends()
    {
        // The bug both reviewers flagged: a teleport that bumps the instance
        // epoch resets the teleport counter to a low value. A teleport-only
        // compare would see IsStrictlyNewer(1, 10) == false and MISS the
        // resend. Instance-awareness must catch it.
        Assert.True(Occurred(curInst: 6, curTele: 1, ackInst: 5, ackTele: 10));
    }

    [Fact]
    public void NewInstanceEpoch_WithZeroTeleportCounter_StillResends()
    {
        Assert.True(Occurred(curInst: 6, curTele: 0, ackInst: 5, ackTele: 0));
    }

    [Fact]
    public void SameInstance_OlderTeleport_DoesNotResend()
    {
        // Out-of-order / stale teleport observation within the same epoch.
        Assert.False(Occurred(curInst: 5, curTele: 2, ackInst: 5, ackTele: 7));
    }

    [Fact]
    public void StaleOlderInstance_DoesNotResend()
    {
        // An out-of-order observation from a PRIOR epoch must never resend.
        Assert.False(Occurred(curInst: 4, curTele: 99, ackInst: 5, ackTele: 0));
    }

    [Fact]
    public void NullAckInstance_ButObservedInstance_Resends()
    {
        // We sent LoginComplete before observing any self instance sequence
        // (the null-ack edge). Once the epoch is known, account for it.
        Assert.True(Occurred(curInst: 0, curTele: 0, ackInst: null, ackTele: null));
    }

    [Fact]
    public void NullAckTeleport_SameNullInstance_ButObservedTeleport_Resends()
    {
        // No instance info on either side, but a teleport sequence appeared
        // since the ack.
        Assert.True(Occurred(curInst: null, curTele: 0, ackInst: null, ackTele: null));
    }

    [Fact]
    public void AllNull_DoesNotResend()
    {
        // No sequence information at all → nothing to act on.
        Assert.False(Occurred(curInst: null, curTele: null, ackInst: null, ackTele: null));
    }

    [Fact]
    public void NoTeleportInfoYet_SameNullInstance_DoesNotResend()
    {
        // Acked a teleport value; current observation carries no teleport
        // sequence (e.g. a non-position update) → not newer.
        Assert.False(Occurred(curInst: null, curTele: null, ackInst: null, ackTele: 4));
    }

    [Fact]
    public void WrapAround_IntraEpochTeleport_Resends()
    {
        // Wrap-aware: 0 is strictly newer than 65535 (forward delta 1).
        Assert.True(Occurred(curInst: 5, curTele: 0, ackInst: 5, ackTele: ushort.MaxValue));
    }

    [Fact]
    public void WrapAround_InstanceEpoch_Resends()
    {
        Assert.True(Occurred(curInst: 0, curTele: 0, ackInst: ushort.MaxValue, ackTele: 500));
    }

    [Theory]
    // Ordinary movement advances ONLY the position sequence, which this
    // predicate never inspects. As long as instance + teleport are
    // unchanged, a moving or parked bot must never trip a resend.
    [InlineData((ushort)5, (ushort)3)]
    [InlineData((ushort)0, (ushort)0)]
    [InlineData(ushort.MaxValue, ushort.MaxValue)]
    public void UnchangedInstanceAndTeleport_NeverResends(ushort inst, ushort tele)
    {
        Assert.False(Occurred(inst, tele, inst, tele));
    }
}
