namespace HeadlessAcClient.Tests;

using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

public class AutoEquipFailureFilterTests
{
    [Fact]
    public void UnmarkedGuid_ConsumeReturnsFalse()
    {
        var f = new AutoEquipFailureFilter();
        Assert.False(f.TryConsumeAutonomous(0x1234u));
    }

    [Fact]
    public void MarkedGuid_ConsumesExactlyOnce()
    {
        var f = new AutoEquipFailureFilter();
        f.MarkAutonomous(0x1234u);
        Assert.True(f.TryConsumeAutonomous(0x1234u));
        // One-shot: the marker is gone after the first consume.
        Assert.False(f.TryConsumeAutonomous(0x1234u));
    }

    [Fact]
    public void DistinctGuids_TrackedIndependently()
    {
        var f = new AutoEquipFailureFilter();
        f.MarkAutonomous(0xAAu);
        f.MarkAutonomous(0xBBu);
        Assert.True(f.TryConsumeAutonomous(0xBBu));
        Assert.False(f.TryConsumeAutonomous(0xBBu));
        // Consuming BB does not affect AA.
        Assert.True(f.TryConsumeAutonomous(0xAAu));
    }

    [Fact]
    public void MarkTwiceSameGuid_StillSingleSuppression()
    {
        var f = new AutoEquipFailureFilter();
        f.MarkAutonomous(0x55u);
        f.MarkAutonomous(0x55u);
        // Set semantics: a single marker, a single suppression.
        Assert.True(f.TryConsumeAutonomous(0x55u));
        Assert.False(f.TryConsumeAutonomous(0x55u));
    }

    [Fact]
    public void ClearAutonomous_RemovesMarker_SoNextFailureSurfaces()
    {
        // Race contract: the LLM takes explicit ownership (Wield dispatch)
        // before the autonomous failure arrives. Clearing the marker means a
        // subsequent failure is NOT suppressed (it surfaces normally).
        var f = new AutoEquipFailureFilter();
        f.MarkAutonomous(0x99u);
        f.ClearAutonomous(0x99u);
        Assert.False(f.TryConsumeAutonomous(0x99u));
    }

    [Fact]
    public void ClearAutonomous_UnmarkedGuid_IsNoOp()
    {
        var f = new AutoEquipFailureFilter();
        f.ClearAutonomous(0x77u); // no throw, no effect
        Assert.False(f.TryConsumeAutonomous(0x77u));
    }

    [Fact]
    public void Remark_AfterConsume_SuppressesAgain()
    {
        // If the source autonomously re-attempts a guid (hypothetically) it
        // re-marks; a fresh marker yields a fresh single suppression.
        var f = new AutoEquipFailureFilter();
        f.MarkAutonomous(0x42u);
        Assert.True(f.TryConsumeAutonomous(0x42u));
        f.MarkAutonomous(0x42u);
        Assert.True(f.TryConsumeAutonomous(0x42u));
        Assert.False(f.TryConsumeAutonomous(0x42u));
    }

    // ---- ShouldSurfaceInventoryFailure (cp-2386 give + cp-2418 wield + pickup) ----

    private static readonly IReadOnlySet<uint> NoWields = new HashSet<uint>();
    private static readonly IReadOnlySet<uint> NoPickups = new HashSet<uint>();

    [Fact]
    public void ShouldSurface_NonZeroError_AlwaysSurfaces()
    {
        // A specific (non-None) error always surfaces regardless of any
        // in-flight give.
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0x420u, 0x1234u, null, NoWields, NoPickups));
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0x06u, 0x1234u, 0x9999u, NoWields, NoPickups));
    }

    [Fact]
    public void ShouldSurface_NoneError_NoPendingGive_Suppressed()
    {
        // A None (0) error with no in-flight give and no matching wield/pickup is a
        // benign teardown — stay suppressed (preserves the pre-cp-2386 behavior).
        Assert.False(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0x1234u, null, NoWields, NoPickups));
    }

    [Fact]
    public void ShouldSurface_NoneError_MatchingPendingGive_Surfaces()
    {
        // A None error that names the item the bot is currently giving is a
        // refused Give — surface it so the LLM pivots instead of re-giving.
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0x80008861u, 0x80008861u, NoWields, NoPickups));
    }

    [Fact]
    public void ShouldSurface_NoneError_NonMatchingPendingGive_Suppressed()
    {
        // A None error for a DIFFERENT item than the in-flight give stays
        // suppressed (e.g. a benign auto-equip None failure during a give).
        Assert.False(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0xABCDu, 0x80008861u, NoWields, NoPickups));
    }

    [Fact]
    public void ShouldSurface_NoneError_GuidInWieldSet_Surfaces()
    {
        // cp-2418: a None error for an item the bot dispatched a wield for is a
        // CheckWeaponCollision refusal (a weapon is already equipped) — surface it
        // so the LLM learns the wield failed and stops re-emitting it.
        var wields = new HashSet<uint> { 0x80008A8Eu };
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0x80008A8Eu, null, wields, NoPickups));
    }

    [Fact]
    public void ShouldSurface_NoneError_GuidNotInWieldSet_Suppressed()
    {
        // A None error for an item NOT in the wield set (and not the in-flight
        // give or a dispatched pickup) stays suppressed.
        var wields = new HashSet<uint> { 0x80008A8Eu };
        Assert.False(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0xABCDu, null, wields, NoPickups));
    }

    [Fact]
    public void ShouldSurface_NoneError_GuidInPickupSet_Surfaces()
    {
        // A None error for an item the bot dispatched a Pickup for is a refused
        // pickup of a non-takeable object — surface it so the recently-rejected
        // dedup breaks the re-emit loop (the failed pickup's queued auto-equip
        // never fires, so the guid never reaches the wield set).
        var pickups = new HashSet<uint> { 0x80003FDFu };
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0x80003FDFu, null, NoWields, pickups));
    }

    [Fact]
    public void ShouldSurface_NoneError_GuidNotInPickupSet_Suppressed()
    {
        // A None error for an item NOT in the pickup set (nor give/wield) stays
        // suppressed — a non-dispatched guid is a benign teardown.
        var pickups = new HashSet<uint> { 0x80003FDFu };
        Assert.False(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0xABCDu, null, NoWields, pickups));
    }
}
