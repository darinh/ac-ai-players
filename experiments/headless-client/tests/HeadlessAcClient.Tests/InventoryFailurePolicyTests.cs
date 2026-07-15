namespace HeadlessAcClient.Tests;

using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

public class InventoryFailurePolicyTests
{
    private static readonly IReadOnlySet<uint> NoWieldTransactions = new HashSet<uint>();
    private static readonly IReadOnlySet<uint> NoPickups = new HashSet<uint>();

    [Fact]
    public void NonZeroError_AlwaysSurfaces()
    {
        Assert.True(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0x420u, 0x1234u, null, NoWieldTransactions, NoPickups));
        Assert.True(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0x06u, 0x1234u, 0x9999u, NoWieldTransactions, NoPickups));
    }

    [Fact]
    public void NoneError_WithoutMatchingAction_IsSuppressed()
    {
        Assert.False(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0u, 0x1234u, null, NoWieldTransactions, NoPickups));
    }

    [Fact]
    public void NoneError_ForPendingGive_Surfaces()
    {
        Assert.True(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0u, 0x80008861u, 0x80008861u, NoWieldTransactions, NoPickups));
    }

    [Fact]
    public void NoneError_ForDifferentPendingGive_IsSuppressed()
    {
        Assert.False(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0u, 0xABCDu, 0x80008861u, NoWieldTransactions, NoPickups));
    }

    [Fact]
    public void NoneError_ForWieldTransactionItem_Surfaces()
    {
        var transactions = new HashSet<uint> { 0x80008A8Eu };

        Assert.True(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0u, 0x80008A8Eu, null, transactions, NoPickups));
    }

    [Fact]
    public void NoneError_ForDifferentWieldTransactionItem_IsSuppressed()
    {
        var transactions = new HashSet<uint> { 0x80008A8Eu };

        Assert.False(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0u, 0xABCDu, null, transactions, NoPickups));
    }

    [Fact]
    public void NoneError_ForDispatchedPickup_Surfaces()
    {
        var pickups = new HashSet<uint> { 0x80003FDFu };

        Assert.True(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0u, 0x80003FDFu, null, NoWieldTransactions, pickups));
    }

    [Fact]
    public void NoneError_ForDifferentPickup_IsSuppressed()
    {
        var pickups = new HashSet<uint> { 0x80003FDFu };

        Assert.False(InventoryFailurePolicy.ShouldSurfaceInventoryFailure(
            0u, 0xABCDu, null, NoWieldTransactions, pickups));
    }
}
