namespace HeadlessAcClient.Tests;

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

    // ---- ShouldSurfaceInventoryFailure (cp-2386) ----

    [Fact]
    public void ShouldSurface_NonZeroError_AlwaysSurfaces()
    {
        // A specific (non-None) error always surfaces regardless of any
        // in-flight give.
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0x420u, 0x1234u, null));
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0x06u, 0x1234u, 0x9999u));
    }

    [Fact]
    public void ShouldSurface_NoneError_NoPendingGive_Suppressed()
    {
        // A None (0) error with no in-flight give is a benign teardown — stay
        // suppressed (preserves the pre-cp-2386 behavior).
        Assert.False(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0x1234u, null));
    }

    [Fact]
    public void ShouldSurface_NoneError_MatchingPendingGive_Surfaces()
    {
        // A None error that names the item the bot is currently giving is a
        // refused Give — surface it so the LLM pivots instead of re-giving.
        Assert.True(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0x80008861u, 0x80008861u));
    }

    [Fact]
    public void ShouldSurface_NoneError_NonMatchingPendingGive_Suppressed()
    {
        // A None error for a DIFFERENT item than the in-flight give stays
        // suppressed (e.g. a benign auto-equip None failure during a give).
        Assert.False(AutoEquipFailureFilter.ShouldSurfaceInventoryFailure(0u, 0xABCDu, 0x80008861u));
    }
}
