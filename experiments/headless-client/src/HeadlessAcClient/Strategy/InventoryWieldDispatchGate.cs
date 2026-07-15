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
    public static bool IsOwnedByActor(
        uint actorGuid,
        uint? containerGuid,
        uint? wielderGuid) =>
        containerGuid == actorGuid || wielderGuid == actorGuid;

    public static InventoryWieldDispatchDecision Evaluate(
        uint requestedItemGuid,
        bool targetAlreadyWielded,
        IEnumerable<uint> pendingItemGuids)
    {
        var sameTargetPending = false;
        foreach (var pendingItemGuid in pendingItemGuids)
        {
            if (pendingItemGuid != requestedItemGuid)
                return InventoryWieldDispatchDecision.DifferentTargetPending;

            sameTargetPending = true;
        }

        if (targetAlreadyWielded)
            return InventoryWieldDispatchDecision.AlreadyWielded;

        return sameTargetPending
            ? InventoryWieldDispatchDecision.SameTargetPending
            : InventoryWieldDispatchDecision.Dispatch;
    }
}
