// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class PendingDequipRetryOwnershipTests
{
    private static readonly DateTime SentAt =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RemoveIfSwapOwned_SwapPrerequisite_RemovesRetry()
    {
        var pending = new Dictionary<uint, PendingDequipRetry>
        {
            [0x100u] = new(
                SentAt,
                Attempts: 1,
                ExplicitRejections: 0,
                Owner: DequipTransactionOwner.WieldSwap),
        };

        Assert.True(PendingDequipRetryOwnership.RemoveIfSwapOwned(0x100u, pending));
        Assert.Empty(pending);
    }

    [Fact]
    public void RemoveIfSwapOwned_ExplicitGoal_PreservesRetry()
    {
        var pending = new Dictionary<uint, PendingDequipRetry>
        {
            [0x100u] = new(
                SentAt,
                Attempts: 1,
                ExplicitRejections: 0,
                Owner: DequipTransactionOwner.ExplicitGoal),
        };

        Assert.False(PendingDequipRetryOwnership.RemoveIfSwapOwned(0x100u, pending));
        Assert.Contains(0x100u, pending.Keys);
    }

    [Fact]
    public void RemoveIfSwapOwned_UnknownItem_IsNoOp()
    {
        var pending = new Dictionary<uint, PendingDequipRetry>();

        Assert.False(PendingDequipRetryOwnership.RemoveIfSwapOwned(0x100u, pending));
    }
}
