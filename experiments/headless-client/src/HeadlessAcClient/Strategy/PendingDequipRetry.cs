// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal enum DequipTransactionOwner
{
    ExplicitGoal,
    WieldSwap,
}

internal readonly record struct PendingDequipRetry(
    DateTime SentAt,
    int Attempts,
    int ExplicitRejections,
    DequipTransactionOwner Owner);

internal static class PendingDequipRetryOwnership
{
    public static bool RemoveIfSwapOwned(
        uint itemGuid,
        IDictionary<uint, PendingDequipRetry> pending)
    {
        if (!pending.TryGetValue(itemGuid, out var retry) ||
            retry.Owner != DequipTransactionOwner.WieldSwap)
        {
            return false;
        }

        return pending.Remove(itemGuid);
    }
}
