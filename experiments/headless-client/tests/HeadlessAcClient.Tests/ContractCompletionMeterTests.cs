// SPDX-License-Identifier: AGPL-3.0-or-later
// ContractCompletionMeter diagnostic tests. Lock the transition-into-done counting
// (a previously-seen non-3 stage -> 3 is one completion), the startup-grace
// baseline (contracts already done at login are not counted as earned this run),
// and the re-acquire-and-complete-again case. Pure observation; no behavior.

using System;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ContractCompletionMeterTests
{
    // Build a world whose Contracts carry the given (id, stage) pairs.
    private static WorldStateProjection W(params (uint id, uint stage)[] contracts)
        => new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x500u, Name = "Headless", HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = Array.Empty<VisibleObjectProjection>(),
            Contracts = contracts
                .Select(c => new ContractProjection { ContractId = c.id, Stage = c.stage })
                .ToArray(),
        };

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime AfterGrace => T0 + TimeSpan.FromSeconds(30); // past the 20s grace

    [Fact]
    public void Observe_CountsTransitionIntoDone_AfterGrace()
    {
        var m = new ContractCompletionMeter();
        Assert.Null(m.Observe(W(), T0));                                   // baseline, no contracts
        Assert.Null(m.Observe(W((1u, 2u)), AfterGrace));                   // in-progress, no completion yet
        var line = m.Observe(W((1u, 3u)), AfterGrace.AddSeconds(1));       // 2 -> 3 = one completion
        Assert.NotNull(line);
        Assert.Contains("[contract-complete]", line);
        Assert.Contains("1 contract reached done", line);   // singular
        Assert.Contains("1 completed this run", line);
        Assert.Equal(1, m.CompletedCount);
    }

    [Fact]
    public void Observe_StaysDone_NoDoubleCount()
    {
        var m = new ContractCompletionMeter();
        Assert.Null(m.Observe(W(), T0));                                   // login baseline starts the grace clock
        Assert.Null(m.Observe(W((1u, 2u)), AfterGrace));
        Assert.NotNull(m.Observe(W((1u, 3u)), AfterGrace.AddSeconds(1)));  // counted once
        Assert.Null(m.Observe(W((1u, 3u)), AfterGrace.AddSeconds(2)));     // still done -> no re-count
        Assert.Equal(1, m.CompletedCount);
    }

    [Fact]
    public void Observe_PreExistingDoneAtLogin_AbsorbedAsBaseline()
    {
        var m = new ContractCompletionMeter();
        // A contract already at stage 3 arrives during the startup grace -> baseline,
        // not a completion earned this run; it must NOT count even once seen post-grace.
        Assert.Null(m.Observe(W((1u, 3u)), T0 + TimeSpan.FromSeconds(2)));
        Assert.Null(m.Observe(W((1u, 3u)), AfterGrace));
        Assert.Equal(0, m.CompletedCount);
    }

    [Fact]
    public void Observe_ReacquireAndCompleteAgain_CountsTwice()
    {
        var m = new ContractCompletionMeter();
        Assert.Null(m.Observe(W(), T0));                                   // login baseline starts the grace clock
        Assert.Null(m.Observe(W((1u, 2u)), AfterGrace));
        Assert.NotNull(m.Observe(W((1u, 3u)), AfterGrace.AddSeconds(1)));  // first completion
        // Batch refresh: the same contract id re-acquired (back to in-progress)...
        Assert.Null(m.Observe(W((1u, 2u)), AfterGrace.AddSeconds(2)));
        Assert.NotNull(m.Observe(W((1u, 3u)), AfterGrace.AddSeconds(3)));  // completed again
        Assert.Equal(2, m.CompletedCount);
    }

    [Fact]
    public void Observe_MultipleDistinctContracts_CountEachCompletion()
    {
        var m = new ContractCompletionMeter();
        Assert.Null(m.Observe(W(), T0));                                   // login baseline starts the grace clock
        Assert.Null(m.Observe(W((1u, 2u), (2u, 2u), (3u, 2u)), AfterGrace));
        // Two of the three reach done on the same tick -> +2.
        var line = m.Observe(W((1u, 3u), (2u, 3u), (3u, 2u)), AfterGrace.AddSeconds(1));
        Assert.NotNull(line);
        Assert.Contains("2 contracts reached done", line);   // plural, this-tick count
        Assert.Contains("2 completed this run", line);        // cumulative
        Assert.Equal(2, m.CompletedCount);
        // The third completes later -> +1.
        Assert.NotNull(m.Observe(W((1u, 3u), (2u, 3u), (3u, 3u)), AfterGrace.AddSeconds(2)));
        Assert.Equal(3, m.CompletedCount);
    }

    [Fact]
    public void Observe_NoContracts_ReturnsNull()
    {
        var m = new ContractCompletionMeter();
        Assert.Null(m.Observe(W(), AfterGrace));
        Assert.Equal(0, m.CompletedCount);
    }

    [Fact]
    public void Observe_GraceBoundary_At20sIsPostGrace()
    {
        var m = new ContractCompletionMeter();
        Assert.Null(m.Observe(W((1u, 2u)), T0));                              // first call starts the clock; records stage 2
        // Exactly 20s after the first Observe is POST-grace (code uses `< StartupGrace`).
        Assert.NotNull(m.Observe(W((1u, 3u)), T0 + TimeSpan.FromSeconds(20)));
        Assert.Equal(1, m.CompletedCount);
    }

    [Fact]
    public void Observe_FirstCallStartsTheGraceClock_NotConstruction()
    {
        var m = new ContractCompletionMeter();
        // The grace clock starts on the FIRST Observe, not at construction: a first
        // Observe at an arbitrary "now" plus a transition 1s later is still in-grace.
        Assert.Null(m.Observe(W((1u, 2u)), AfterGrace));                      // first call -> grace starts here
        Assert.Null(m.Observe(W((1u, 3u)), AfterGrace.AddSeconds(1)));        // 1s later still in-grace -> absorbed
        Assert.Equal(0, m.CompletedCount);
    }
}
