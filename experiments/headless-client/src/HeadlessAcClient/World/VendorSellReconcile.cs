// SPDX-License-Identifier: AGPL-3.0-or-later

namespace HeadlessAcClient.World;

/// <summary>
/// Pure reconcile predicate for an in-flight vendor Sell. A sell is treated as
/// settled when any of:
///   - the sold item's guid has left the bot's own pack (a full-item sale —
///     sale-specific), or
///   - coin rose above the pre-dispatch baseline (the server credits coin on a
///     completed sale; this also covers a partial-stack sale where the object's
///     guid remains), or
///   - the in-flight window elapsed (drop a stale entry so the dedup can't wedge).
///
/// Coin is NOT sale-specific (other coin gains also raise it), so the motor must
/// NOT re-dispatch a sell on the SAME tick it settles a prior one off this
/// predicate — settling only clears the one-in-flight dedup; the next decision
/// re-evaluates with fresh perception. Kept pure + side-effect-free so that
/// "did this sell settle" is unit-testable independent of the motor.
/// </summary>
internal static class VendorSellReconcile
{
    public const double DefaultTimeoutSeconds = 12.0;

    public static bool IsSettled(
        int? preCoin,
        int? coinNow,
        bool stillOwned,
        double secondsElapsed,
        double timeoutSeconds = DefaultTimeoutSeconds)
    {
        var coinRose = preCoin is int was && coinNow is int now && now > was;
        return !stillOwned || coinRose || secondsElapsed > timeoutSeconds;
    }
}
