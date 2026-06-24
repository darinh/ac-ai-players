// SPDX-License-Identifier: AGPL-3.0-or-later
// IntentStackReclaim — pure overflow-reclaim policy for the bounded IntentStack.
//
// The stack is depth-bounded (MaxDepth). Only the TOP frame is auto-checked for
// completion/deadline each tick (IntentStack.CheckTopForCompletion), so a buried
// frame that is already terminal — or whose own deadline has elapsed — is never
// reclaimed by the per-tick path. Over a long run those accumulate until the stack
// is full and every push is refused, which blocks the caller from placing a new
// top-level intent. This policy decides which frames survive when one new frame is
// being admitted onto a full stack, dropping the FEWEST needed in three gentle-first
// tiers. Pure eviction bookkeeping over (lifecycle status, deadline); no game
// knowledge.

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Strategy.Intent;

internal static class IntentStackReclaim
{
    /// <summary>The two fields the reclaim policy reads from a frame.</summary>
    internal readonly record struct FrameView(IntentLifecycle Status, DateTime? DeadlineUtc);

    /// <summary>
    /// Given <paramref name="frames"/> (root-first; index 0 = root) and a single new
    /// frame about to be pushed, return the indices to KEEP (root-first order),
    /// dropping the fewest frames so admitting one more stays within
    /// <paramref name="maxDepth"/>. When the stack is not full this is a no-op
    /// (every index kept). When full, tiers run gentlest first:
    ///   1. Drop terminal frames (Completed/Expired) anywhere, including the root —
    ///      matches IntentStack.ReapTerminalFrames; never empties (if all are
    ///      terminal the newest is kept).
    ///   2. (eviction on, still full) Drop non-root frames that are Active AND past
    ///      their own deadline — enforcing, for a buried frame, the deadline the
    ///      per-tick check only applies to the TOP frame.
    ///   3. (eviction on, still full) Drop the oldest non-root frame, preferring an
    ///      Active frame over a durable Blocked marker.
    /// The bottom-most survivor (index 0) is preserved by tiers 2-3. With
    /// <paramref name="evictNonTerminal"/> false only tier 1 runs (the legacy
    /// reclaim-terminal-then-refuse behavior). Pure; allocates only the result list.
    /// </summary>
    public static List<int> SurvivorsForPush(
        IReadOnlyList<FrameView> frames, int maxDepth, DateTime utcNow, bool evictNonTerminal)
    {
        int n = frames.Count;

        // Not full: admitting one more stays within bounds, so reclaim nothing.
        if (n < maxDepth)
        {
            var all = new List<int>(n);
            for (int i = 0; i < n; i++) all.Add(i);
            return all;
        }

        var keep = new List<int>(n);

        // Tier 1: keep every non-terminal frame.
        for (int i = 0; i < n; i++)
            if (frames[i].Status is not (IntentLifecycle.Completed or IntentLifecycle.Expired))
                keep.Add(i);
        if (keep.Count == 0 && n > 0) keep.Add(n - 1); // all terminal — keep the newest

        if (!evictNonTerminal) return keep;
        if (keep.Count < maxDepth) return keep;

        // Tier 2: drop non-root (keep[0] preserved) Active frames past their deadline.
        var t2 = new List<int>(keep.Count);
        if (keep.Count > 0) t2.Add(keep[0]);
        for (int k = 1; k < keep.Count; k++)
        {
            var fv = frames[keep[k]];
            bool elapsed = fv.Status == IntentLifecycle.Active
                           && fv.DeadlineUtc is DateTime d && utcNow >= d;
            if (!elapsed) t2.Add(keep[k]);
        }
        keep = t2;
        if (keep.Count < maxDepth) return keep;

        // Tier 3: last resort — drop the oldest non-root frame, preferring an Active
        // frame over a durable Blocked marker (Blocked markers carry state the prompt
        // relies on, so evict one only when no non-root Active frame remains).
        if (keep.Count > 1)
        {
            int dropAt = -1;
            for (int k = 1; k < keep.Count; k++)
                if (frames[keep[k]].Status == IntentLifecycle.Active) { dropAt = k; break; }
            if (dropAt < 0) dropAt = 1; // all non-root frames are Blocked — evict the oldest
            keep.RemoveAt(dropAt);
        }
        return keep;
    }
}
