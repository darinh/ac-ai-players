// SPDX-License-Identifier: AGPL-3.0-or-later
// RecallEscape — pure helper for deduping a REFUSED/failed self-action Recall.
//
// A Recall (TeleToLifestone, 0x0063) is a targetless self-action: the Motor
// dispatches it, then holds an in-flight window (up to ~20s) for the teleport
// to land. If the window closes with NO landblock change, the recall did NOT
// land — the server refused it (no attuned lifestone, blocked in a training
// area / just after PvP) or it was otherwise interrupted.
//
// IsGoalRecentlyRejected keys the generic dedup on a goal's TARGET/ITEM name,
// so a targetless Recall can never be matched there and a refused Recall would
// re-emit in a loop (one full LLM call + a ~20s in-flight hold per cycle). To
// close that loop the Motor stamps a SYNTHETIC targetless ActionRejected with
// the reserved code below when a recall fails to land, and the policy treats a
// fresh one as a recent rejection of a Recall goal — mirroring the existing
// synthetic transport-failure rejections (0xFFFC-0xFFFE) and the surfaced
// swing-loop cancel (0xFFFA). Pure bookkeeping on the bot's OWN recall outcome;
// no game knowledge, no per-target/type decision.
internal static class RecallEscape
{
    // Motor-reserved ActionRejected code for "a dispatched Recall did not land
    // (window closed with no teleport)". Distinct from the transport codes
    // 0xFFFC-0xFFFE and the surfaced swing-loop cancel 0xFFFA.
    public const uint RecallDidNotLandRejectionCode = 0xFFF9u;

    // Squared position delta (game units^2) above which the bot is judged to
    // have MOVED between recall dispatch and window-close, i.e. the teleport
    // landed even if it stayed within the same cell id. ~2u of drift is normal
    // idle jitter; a real teleport moves far more.
    public const float LandedMoveThresholdSq = 4.0f;

    /// <summary>True iff this rejection code is the synthetic recall-did-not-land marker.</summary>
    public static bool IsRecallDidNotLandRejection(uint? errorCode) =>
        errorCode == RecallDidNotLandRejectionCode;

    /// <summary>
    /// Whether a dispatched Recall is CONFIDENTLY observed to have NOT landed —
    /// the only case that should stamp the synthetic rejection. Returns true
    /// ONLY when BOTH the dispatch and current cell ids are known (non-zero),
    /// the cell id is UNCHANGED, AND the bot has not moved beyond
    /// <see cref="LandedMoveThresholdSq"/>. A landed teleport (cell id changed
    /// OR moved significantly — e.g. to a lifestone in the same landblock/cell)
    /// returns false; a null/unknown position or cell at either end is
    /// INCONCLUSIVE and returns false, so a real recall is never mis-recorded as
    /// refused. Pure geometry over the bot's own dispatch vs current pose.
    /// </summary>
    public static bool RecallConfidentlyDidNotLand(
        uint dispatchCellId, System.Numerics.Vector3? dispatchPos,
        uint nowCellId, System.Numerics.Vector3? nowPos,
        float landedMoveThresholdSq = LandedMoveThresholdSq)
    {
        if (dispatchCellId == 0u || nowCellId == 0u) return false;            // inconclusive: unknown cell
        if (dispatchPos is not { } dp || nowPos is not { } np) return false;  // inconclusive: unknown pose
        if (nowCellId != dispatchCellId) return false;                        // landed: cell changed
        if (System.Numerics.Vector3.DistanceSquared(dp, np) > landedMoveThresholdSq)
            return false;                                                     // landed: moved far (same-cell lifestone)
        return true;                                                          // same cell, no move => did not land
    }
}
