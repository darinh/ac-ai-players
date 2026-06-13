// SPDX-License-Identifier: AGPL-3.0-or-later
// Contract-progress funnel (diagnostic only). The Pilot Track's criterion 2 is
// "complete a kill-task contract and turn it in". A live run is a long stdout
// stream; pinpointing WHERE the contract chain stalls means grepping a multi-MB
// log. This tracks the FURTHEST point the bot has reached along the
// engage-a-vendor -> hold/progress/finish-a-contract path and emits ONE concise
// `[contract-funnel]` line the first time each is reached past the run's
// starting state, so the autonomous loop can see the stall at a glance.
//
// HONESTY about scope: the CONTRACT milestones (held/in-progress/done) are the
// criterion-2 signal (a tracked contract advancing). The VENDOR milestones are
// GENERIC PRECURSORS — a vendor is a POSSIBLE contract source, but the bot
// cannot tell whether a given vendor sells contracts until it opens it, so
// "vendor seen/opened" does not assert "contract vendor". Likewise `Contracts`
// is the bot's generic tracked-objective table; completing ANY of them is the
// criterion-2 outcome. This is a proxy funnel, not a kill-task-exclusive gate.
//
// Pure observation of WorldStateProjection — NO decision-making, NO game
// knowledge, NO effect on what the bot does. It reads the wire ContractStage
// codes (source of truth: ACE-bots Source/ACE.Server/Network/Structure/
// ContractTracker.cs: 1 Available, 2 InProgress, 3 DoneOrPendingRepeat, 4+
// ProgressCounter) and the already-projected vendor/visibility facts.

using System;

namespace HeadlessAcClient.Strategy;

internal sealed class ContractProgressFunnel
{
    internal enum Milestone
    {
        None = 0,
        VendorVisible = 1,       // precursor: a vendor (possible contract source) is in view
        VendorPanelOpen = 2,     // precursor: a vendor's trade panel is open (offerings shown)
        ContractHeld = 3,        // a contract is tracked
        ContractInProgress = 4,  // a tracked contract is in progress
        ContractDone = 5,        // a tracked contract is done / ready to turn in
    }

    private Milestone _maxReached = Milestone.None;
    private DateTime? _startUtc;

    // The server delivers the bot's PRE-EXISTING state (contract tracker table
    // 0x0314, etc.) as a burst over the first seconds after login — AFTER the
    // first (contract-less) projection. Silently absorb whatever the bot already
    // had during this startup window so it becomes the baseline, not a logged
    // "advance" (otherwise a stale stage-3 DoneOrPendingRepeat contract would
    // log a false completion). The contract table arrives within the first ~2s;
    // this window is generously larger.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Observe the latest world state. Returns a one-line log string the FIRST
    /// time the bot advances to a new furthest milestone after the startup grace
    /// window, else null. State the bot already had at login (including a
    /// pre-existing contract delivered a few ticks in) is absorbed silently as
    /// the baseline, so no false "reached" is logged for it.
    /// </summary>
    public string? Observe(WorldStateProjection world) => Observe(world, DateTime.UtcNow);

    internal string? Observe(WorldStateProjection world, DateTime nowUtc)
    {
        _startUtc ??= nowUtc;
        var current = CurrentMilestone(world);

        // Startup grace: absorb (raise the baseline, never lower it), log nothing.
        if (nowUtc - _startUtc.Value < StartupGrace)
        {
            if ((int)current > (int)_maxReached) _maxReached = current;
            return null;
        }

        if ((int)current <= (int)_maxReached) return null;
        _maxReached = current;
        return $"[contract-funnel] reached {current} (furthest since startup)";
    }

    /// <summary>
    /// The highest milestone the given state currently satisfies. A held
    /// contract does not imply a vendor is visible right now (contracts can be
    /// granted by an NPC emote without a vendor), so this returns the
    /// most-advanced condition that holds, not a strict ladder. Allocation-free
    /// (index loops over IReadOnlyList) because it runs every decision tick.
    /// </summary>
    internal static Milestone CurrentMilestone(WorldStateProjection world)
    {
        var anyDone = false;
        var anyInProgress = false;
        var contracts = world.Contracts;
        for (var i = 0; i < contracts.Count; i++)
        {
            var stage = contracts[i].Stage;
            if (stage == 3u) anyDone = true;
            else if (stage == 2u || stage >= 4u) anyInProgress = true;
        }

        if (anyDone) return Milestone.ContractDone;
        if (anyInProgress) return Milestone.ContractInProgress;
        if (contracts.Count > 0) return Milestone.ContractHeld;
        if (world.Vendor is not null) return Milestone.VendorPanelOpen;

        var visible = world.Visible;
        for (var i = 0; i < visible.Count; i++)
            if (visible[i].IsVendor) return Milestone.VendorVisible;

        return Milestone.None;
    }
}
