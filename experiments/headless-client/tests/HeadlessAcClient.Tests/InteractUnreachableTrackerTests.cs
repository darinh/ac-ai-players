using System;
using System.Linq;
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

    [Fact]
    public void SnapshotSuppressed_Empty_WhenNothingMarked()
    {
        var t = new InteractUnreachableTracker();
        Assert.Empty(t.SnapshotSuppressed(T0));
    }

    [Fact]
    public void SnapshotSuppressed_ReturnsLiveEntries_WithUntil()
    {
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl);
        t.MarkUnreachable(DoorGuid, T0, Ttl);
        var snap = t.SnapshotSuppressed(T0.AddSeconds(10));
        Assert.Equal(2, snap.Count);
        var byGuid = snap.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(T0.Add(Ttl), byGuid[ChestGuid]);
        Assert.Equal(T0.Add(Ttl), byGuid[DoorGuid]);
    }

    [Fact]
    public void SnapshotSuppressed_OmitsAndEvictsExpired()
    {
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl);                       // expires T0+60
        t.MarkUnreachable(DoorGuid, T0.AddSeconds(40), Ttl);         // expires T0+100
        // At T0+70 the chest has expired, the door is still live.
        var snap = t.SnapshotSuppressed(T0.AddSeconds(70));
        Assert.Single(snap);
        Assert.Equal(DoorGuid, snap[0].Key);
        // The expired chest entry was lazily evicted by the snapshot.
        Assert.Equal(1, t.Count);
    }

    // ── escalating backoff (opt-in via maxBackoffMultiplier) ──────────────

    [Fact]
    public void Backoff_Default1_DoesNotEscalate()
    {
        // The default path (no/explicit 1 multiplier) keeps the fixed base ttl:
        // rapid consecutive re-marks each extend by only the base 60s.
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl, maxBackoffMultiplier: 1);
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(5), Ttl, maxBackoffMultiplier: 1);
        // Still only base ttl from the last mark (T0+5+60 = T0+65), NOT 2x.
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(64)));
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(65)));
    }

    [Fact]
    public void Backoff_ConsecutiveRefusals_ExtendCooldown()
    {
        // A second refusal within the decay window doubles the base ttl (streak 2).
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl, maxBackoffMultiplier: 5);              // streak1 -> T0+60
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(30), Ttl, maxBackoffMultiplier: 5); // streak2 -> T0+30+120 = T0+150
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(149)));
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(150)));
    }

    [Fact]
    public void Backoff_CapsAtMaxMultiplier()
    {
        // With cap 2, a third consecutive refusal stays at 2x base (not 3x).
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl, maxBackoffMultiplier: 2);               // streak1 -> T0+60
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(10), Ttl, maxBackoffMultiplier: 2); // streak2 -> T0+10+120 = T0+130
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(20), Ttl, maxBackoffMultiplier: 2); // streak3 capped@2 -> T0+20+120 = T0+140
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(139)));
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(140)));
    }

    [Fact]
    public void Backoff_StreakResets_AfterDecayWindowGap()
    {
        // A re-mark more than the 5-min decay window after the previous one resets
        // the streak to 1 -> back to the base ttl (NOT the escalated 2x), so a
        // target reachable from a new position later is not over-suppressed.
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl, maxBackoffMultiplier: 5);                  // streak1 -> T0+60 (expires)
        // 400s later (> 300s decay window): streak resets to 1 -> base 60s.
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(400), Ttl, maxBackoffMultiplier: 5);  // streak1 -> T0+460
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(459)));
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(460)));
    }

    [Fact]
    public void Backoff_DecayReset_GivesFreshBaseTtl_NotStaleLongSuppression()
    {
        // After a gap longer than the (scaled) decay window the streak resets, so
        // the new cooldown is the base ttl (a fresh chance), NOT an inherited
        // escalated one. With cap 5 the decay window is 60*(5+1)=360s.
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl, maxBackoffMultiplier: 5);                // streak1 -> T0+60
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(1), Ttl, maxBackoffMultiplier: 5);  // streak2 -> T0+121
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(2), Ttl, maxBackoffMultiplier: 5);  // streak3 -> T0+182
        // Re-mark 400s after the last mark (> 360s window) -> streak resets to 1 ->
        // base 60s from now (T0+402 -> T0+462), a fresh chance.
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(402), Ttl, maxBackoffMultiplier: 5);
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(461)));
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(462)));   // base ttl, not escalated
    }

    [Fact]
    public void Backoff_RealRetryCadence_ClimbsToCapThenHolds()
    {
        // Model the REAL driver cadence: the guid is suppressed for its current
        // cooldown, so the next refusal re-mark arrives ~cooldown later. With cap 5
        // the cooldown should climb 60 -> 120 -> 180 -> 240 -> 300 then HOLD at 300
        // (the scaled decay window 360s keeps the streak from resetting at the
        // boundary), NOT cycle back to 60.
        var t = new InteractUnreachableTracker();
        const int cap = 5;
        var mark = T0;
        var expectedTtl = new[] { 60, 120, 180, 240, 300, 300, 300 };
        foreach (var ttlSec in expectedTtl)
        {
            t.MarkUnreachable(ChestGuid, mark, Ttl, maxBackoffMultiplier: cap);
            // Still suppressed just before the expected expiry, free at it.
            Assert.True(t.IsSuppressed(ChestGuid, mark.AddSeconds(ttlSec - 1)));
            Assert.False(t.IsSuppressed(ChestGuid, mark.AddSeconds(ttlSec)));
            // Next refusal arrives right when the cooldown lapses (the natural cadence).
            mark = mark.AddSeconds(ttlSec);
        }
    }

    [Fact]
    public void Backoff_BoundaryGapEqualToWindow_Resets()
    {
        // The streak-continue test is strict (`now < StaleAfter`), so a re-mark at
        // EXACTLY the decay window (cap 5 -> 360s) resets to base ttl, not escalates.
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl, maxBackoffMultiplier: 5);                 // streak1, StaleAfter T0+360
        t.MarkUnreachable(ChestGuid, T0.AddSeconds(360), Ttl, maxBackoffMultiplier: 5); // gap==360 -> reset -> base 60s -> T0+420
        Assert.True(t.IsSuppressed(ChestGuid, T0.AddSeconds(419)));
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(420)));    // base ttl, would be T0+480 if it had escalated
    }

    [Fact]
    public void Backoff_StrikeState_SelfPrunes_OnReads_InQuiescence()
    {
        // Strike state must outlive the _until cooldown (to accumulate the streak)
        // but not leak forever if marks stop. A read after the decay window prunes it.
        var t = new InteractUnreachableTracker();
        t.MarkUnreachable(ChestGuid, T0, Ttl, maxBackoffMultiplier: 5);  // StaleAfter T0+360
        Assert.Equal(1, t.StrikeCount);
        // Within the window the strike survives even though the cooldown lapsed at T0+60.
        Assert.False(t.IsSuppressed(ChestGuid, T0.AddSeconds(100)));
        Assert.Equal(1, t.StrikeCount);
        // A read at/after the decay window prunes the stale strike (no further marks).
        t.SnapshotSuppressed(T0.AddSeconds(360));
        Assert.Equal(0, t.StrikeCount);
    }
}
