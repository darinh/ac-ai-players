// SPDX-License-Identifier: AGPL-3.0-or-later
// ContractProgressFunnel diagnostic tests. Lock the milestone classification
// (which contract-cycle stage a world state satisfies), the allocation-free
// computation, and the baseline + monotonic emit-once-per-advance Observe
// contract (no false "reached this run" from a pre-existing contract). Pure
// observation; no behavior.

using System;
using System.Linq;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ContractProgressFunnelTests
{
    private static WorldStateProjection W(
        bool vendorVisible = false, bool vendorOpen = false, params uint[] contractStages)
    {
        var visible = vendorVisible
            ? new[] { new VisibleObjectProjection { Guid = 0x1u, Name = "Vendor", IsVendor = true } }
            : Array.Empty<VisibleObjectProjection>();
        return new WorldStateProjection
        {
            Self = new SelfProjection { Guid = 0x500u, Name = "Headless", HealthFraction = 1.0f },
            Inventory = Array.Empty<InventoryItemProjection>(),
            Visible = visible,
            Vendor = vendorOpen ? new VendorProjection { VendorGuid = 0x2u } : null,
            Contracts = contractStages
                .Select(s => new ContractProjection { ContractId = 0x10u, Stage = s })
                .ToArray(),
        };
    }

    [Fact]
    public void CurrentMilestone_None_WhenNothingRelevant()
        => Assert.Equal(ContractProgressFunnel.Milestone.None,
            ContractProgressFunnel.CurrentMilestone(W()));

    [Fact]
    public void CurrentMilestone_VendorVisible_WhenVendorInView()
        => Assert.Equal(ContractProgressFunnel.Milestone.VendorVisible,
            ContractProgressFunnel.CurrentMilestone(W(vendorVisible: true)));

    [Fact]
    public void CurrentMilestone_VendorPanelOpen_OutranksMereVisibility()
        => Assert.Equal(ContractProgressFunnel.Milestone.VendorPanelOpen,
            ContractProgressFunnel.CurrentMilestone(W(vendorVisible: true, vendorOpen: true)));

    [Fact]
    public void CurrentMilestone_ContractHeld_WhenAvailableStageTracked()
        => Assert.Equal(ContractProgressFunnel.Milestone.ContractHeld,
            ContractProgressFunnel.CurrentMilestone(W(contractStages: 1u)));

    [Theory]
    [InlineData(2u)] // InProgress
    [InlineData(4u)] // ProgressCounter
    [InlineData(7u)] // higher progress-counter
    public void CurrentMilestone_ContractInProgress_ForStage2OrCounter(uint stage)
        => Assert.Equal(ContractProgressFunnel.Milestone.ContractInProgress,
            ContractProgressFunnel.CurrentMilestone(W(contractStages: stage)));

    [Fact]
    public void CurrentMilestone_ContractDone_ForStage3()
        => Assert.Equal(ContractProgressFunnel.Milestone.ContractDone,
            ContractProgressFunnel.CurrentMilestone(W(contractStages: 3u)));

    [Fact]
    public void CurrentMilestone_PrefersMostAdvancedContract()
    {
        Assert.Equal(ContractProgressFunnel.Milestone.ContractDone,
            ContractProgressFunnel.CurrentMilestone(W(contractStages: new[] { 1u, 3u })));
        Assert.Equal(ContractProgressFunnel.Milestone.ContractInProgress,
            ContractProgressFunnel.CurrentMilestone(W(contractStages: new[] { 1u, 2u })));
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime AfterGrace => T0 + TimeSpan.FromSeconds(30); // past the 20s grace

    [Fact]
    public void Observe_AbsorbsStartupStateSilently_NoFalseReached()
    {
        // The server delivers a pre-existing stage-3 (done/cooldown) contract a
        // few ticks AFTER login. That must be absorbed during the startup grace,
        // never logged as a false "reached".
        var f = new ContractProgressFunnel();
        Assert.Null(f.Observe(W(), T0));                                       // t0: None
        Assert.Null(f.Observe(W(contractStages: 3u), T0 + TimeSpan.FromSeconds(2)));  // table loads in-grace -> absorbed
        Assert.Null(f.Observe(W(contractStages: 3u), AfterGrace));            // post-grace, still baseline -> null
    }

    [Fact]
    public void Observe_EmitsAdvancesAfterGrace_AndIgnoresRegress()
    {
        var f = new ContractProgressFunnel();

        Assert.Null(f.Observe(W(), T0));                                                 // startup baseline None
        Assert.NotNull(f.Observe(W(vendorVisible: true), AfterGrace));                   // -> VendorVisible
        Assert.Null(f.Observe(W(vendorVisible: true), AfterGrace.AddSeconds(1)));        // same -> no re-emit
        Assert.NotNull(f.Observe(W(vendorOpen: true), AfterGrace.AddSeconds(2)));        // -> VendorPanelOpen
        Assert.Null(f.Observe(W(vendorVisible: true), AfterGrace.AddSeconds(3)));        // regress -> max retained
        Assert.NotNull(f.Observe(W(contractStages: 2u), AfterGrace.AddSeconds(4)));      // -> ContractInProgress
        Assert.NotNull(f.Observe(W(contractStages: 3u), AfterGrace.AddSeconds(5)));      // -> ContractDone
        Assert.Null(f.Observe(W(contractStages: 3u), AfterGrace.AddSeconds(6)));         // furthest -> null
    }

    [Fact]
    public void Observe_GraceAbsorbsPrecursor_ThenLogsGenuinePostGraceAdvance()
    {
        // Bot is already at a vendor during the grace (absorbed); a contract
        // acquired + advanced after the grace still logs.
        var f = new ContractProgressFunnel();
        Assert.Null(f.Observe(W(vendorOpen: true), T0));                  // absorbed at VendorPanelOpen
        Assert.NotNull(f.Observe(W(contractStages: 1u), AfterGrace));     // -> ContractHeld (advance past it)
    }

    [Fact]
    public void Observe_LogLine_NamesTheMilestone()
    {
        var f = new ContractProgressFunnel();
        Assert.Null(f.Observe(W(), T0));                                  // baseline
        var line = f.Observe(W(contractStages: 2u), AfterGrace);
        Assert.NotNull(line);
        Assert.Contains("[contract-funnel]", line);
        Assert.Contains("ContractInProgress", line);
    }
}
