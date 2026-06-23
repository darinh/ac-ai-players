// SPDX-License-Identifier: AGPL-3.0-or-later
// Contract-completion meter (diagnostic only). The Pilot Track's criterion 2 is
// "complete a kill-task contract and turn it in". The companion [contract-funnel]
// logs the FURTHEST milestone reached ONCE, so a run that completes several
// contracts (or finishes a batch, refreshes, and completes more) looks identical
// to one that reached "done" a single time. This meter counts each time a tracked
// contract TRANSITIONS into stage 3 (done) and logs a one-line [contract-complete]
// with the running total, so criterion-2 THROUGHPUT (how many contract objectives
// the bot actually finished this run) is visible at a glance.
//
// Pure observation of WorldStateProjection — NO decision-making, NO game knowledge.
// It reads the wire ContractStage codes keyed by ContractId (source ACE-bots
// Source/ACE.Server/Network/Structure/ContractTracker.cs: 1 Available,
// 2 InProgress, 3 DoneOrPendingRepeat, 4+ ProgressCounter). Counting TRANSITIONS
// (a previously-seen non-3 stage -> 3) rather than "first seen at 3" both ignores
// contracts already done at login and counts a contract that is re-acquired and
// completed again.
//
// Startup grace mirrors ContractProgressFunnel: the server delivers the bot's
// pre-existing contracts as a burst over the first seconds after login, so a
// contract observed reaching stage 3 within the grace is absorbed as the baseline
// (its stage is recorded, but it is not counted as a completion earned this run).

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal sealed class ContractCompletionMeter
{
    // Last-seen stage per ContractId, so a transition INTO stage 3 can be detected.
    private readonly Dictionary<uint, uint> _lastStageById = new();
    private int _completedCount;
    private DateTime? _startUtc;

    private const uint DoneStage = 3u;

    // Contracts already at stage 3 at login arrive a few ticks in; absorb them as
    // baseline rather than counting them as completions earned this run.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    // Total contract completions observed this run (post-grace transitions to done).
    internal int CompletedCount => _completedCount;

    /// <summary>
    /// Observe the latest world state. Returns a one-line log string when one or
    /// more tracked contracts transitioned into stage 3 (done) since the previous
    /// observation, after the startup grace; else null.
    /// </summary>
    public string? Observe(WorldStateProjection world) => Observe(world, DateTime.UtcNow);

    internal string? Observe(WorldStateProjection world, DateTime nowUtc)
    {
        _startUtc ??= nowUtc;
        var inGrace = nowUtc - _startUtc.Value < StartupGrace;
        var contracts = world.Contracts;
        var newlyDone = 0;
        for (var i = 0; i < contracts.Count; i++)
        {
            var id = contracts[i].ContractId;
            var stage = contracts[i].Stage;
            var hadPrev = _lastStageById.TryGetValue(id, out var prevStage);
            _lastStageById[id] = stage;
            // A completion is a transition INTO stage 3 from a previously-seen
            // non-3 stage. The startup grace absorbs the initial burst (records
            // stages without counting), so a contract already done at login — or
            // one first seen mid-progress during the grace — is not miscounted.
            if (stage == DoneStage && hadPrev && prevStage != DoneStage && !inGrace)
            {
                _completedCount++;
                newlyDone++;
            }
        }
        if (newlyDone == 0) return null;
        var noun = newlyDone == 1 ? "contract" : "contracts";
        return $"[contract-complete] {newlyDone} {noun} reached done "
            + $"({_completedCount} completed this run)";
    }
}
