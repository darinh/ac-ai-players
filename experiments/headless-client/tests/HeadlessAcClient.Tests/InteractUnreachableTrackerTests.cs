using System;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

/// <summary>
/// Tests for <see cref="InteractUnreachableTracker"/>: the TTL'd guid
/// cooldown that stops the Motor's LLM-goal resolver from re-locking a
/// target the SERVER refused as out-of-reach. Live repro: a chest on a
/// ledge (XY-arrivable, 3D-unreachable) re-resolved 5 cycles in a row
/// because tactics.ResolveTarget bypasses the picker's visitedTargetGuids
/// filter. Marking the guid suppresses re-resolution for a cooldown;
/// the TTL lets a later approach from a different cell retry.
/// </summary>
public class InteractUnreachableTrackerTests
{
    private const uint ChestGuid = 0x7A9B400Eu;
    private const uint DoorGuid = 0x7A9B400Cu;
    private static readonly DateTime T0 = new(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    [Fact]
    public void Unmarked_Guid_IsNotSuppressed()
    {
        var t = new InteractUnreachableTracker();
        Assert.False(t.IsSuppressed(ChestGuid, T0));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Marked_Guid_IsSuppressedWithinCooldown()
    {
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl);
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(1)));
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(59)));
    }

    [Fact]
    public void Marked_Guid_IsNotSuppressedAtOrAfterExpiry_AndIsEvicted()
    {
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl);
        Assert.Equal(1, t.Count);
        // At exactly now+ttl the cooldown has elapsed (strict `now < until`).
        Assert.False(t.IsSuppressed(ChestGuid, T0.Add(Ttl)));
        // The expired entry is lazily evicted on the failing check.
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Remark_RefreshesCooldown()
    {
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl);
        // Re-mark 50s later: cooldown now extends to T0+50s+60s = T0+110s.
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(50), Ttl);
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(100)));
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(110)));
    }

    [Fact]
    public void DistinctGuids_TrackedIndependently()
    {
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl);
        // The door was never refused — it stays resolvable even though the
        // chest is suppressed (the regression guard: legitimate re-Use of a
        // reachable visited object must not be blocked).
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(5)));
        Assert.False(t.IsSuppressed(DoorGuid, T0.AddSeconds(5)));
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void ReSuppression_AfterExpiry_Works()
    {
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl);
        Assert.False(t.IsSuppressed(ChestGuid, T0.Add(Ttl)));        // expired + evicted
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(120), Ttl);       // refused again later
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(150)));
    }
}
