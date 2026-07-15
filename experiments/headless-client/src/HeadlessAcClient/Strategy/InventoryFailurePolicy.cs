namespace HeadlessAcClient.Strategy;

using System.Collections.Generic;

/// <summary>
/// Decides whether an inventory failure is tied to an explicit action and
/// should be surfaced to Strategy as an <c>ActionRejected</c> event.
/// </summary>
internal static class InventoryFailurePolicy
{
    /// <summary>
    /// A specific error always surfaces. A <c>None</c> error surfaces only
    /// when it names the item currently being given, an item participating
    /// in an explicit Wield transaction, or an item explicitly picked up.
    /// </summary>
    internal static bool ShouldSurfaceInventoryFailure(
        uint errorType,
        uint itemGuid,
        uint? pendingGiveItemGuid,
        IReadOnlySet<uint> wieldTransactionGuids,
        IReadOnlySet<uint> pickupDispatchedGuids)
        => errorType != 0
           || (pendingGiveItemGuid is uint pg && pg == itemGuid)
           || wieldTransactionGuids.Contains(itemGuid)
           || pickupDispatchedGuids.Contains(itemGuid);
}
