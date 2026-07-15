// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal enum InventoryWieldDispatchDecision
{
    Dispatch,
    AlreadyWielded,
    SameTargetPending,
    DifferentTargetPending,
}

internal static class InventoryWieldDispatchGate
{
    public static InventoryWieldDispatchDecision Evaluate(
        uint requestedItemGuid,
        bool targetAlreadyWielded,
        IEnumerable<uint> pendingItemGuids)
    {
        if (targetAlreadyWielded)
            return InventoryWieldDispatchDecision.AlreadyWielded;

        var sameTargetPending = false;
        foreach (var pendingItemGuid in pendingItemGuids)
        {
            if (pendingItemGuid != requestedItemGuid)
                return InventoryWieldDispatchDecision.DifferentTargetPending;

            sameTargetPending = true;
        }

        return sameTargetPending
            ? InventoryWieldDispatchDecision.SameTargetPending
            : InventoryWieldDispatchDecision.Dispatch;
    }
}
