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
}
