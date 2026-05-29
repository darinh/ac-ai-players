// SPDX-License-Identifier: AGPL-3.0-or-later
//
// IndoorNavServiceTests — Phase 3.0 unit tests for the API surface
// of the headless-client side of the WorldNav integration.
//
// These tests do NOT load real DAT files; they exercise the
// disabled-mode path, the static helpers, and the early-exit
// branches that gate on cell-id shape (indoor-vs-outdoor,
// cross-landblock). The "happy path" — loading a real graph and
// returning waypoints — is covered end-to-end by AcAiPlayers.WorldNav's
// own test fixtures (PathfinderWalkableTests) and the live spike
// runs against academy 0x8602.

using System.Collections.Generic;
using System.Numerics;

using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public sealed class IndoorNavServiceTests
{
    [Fact]
    public void IsIndoorCell_Indoor()
    {
        // Academy 0x8602 cell 0x01AD — lower 16 bits 0x01AD >= 0x100.
        Assert.True(IndoorNavService.IsIndoorCell(0x860201ADu));
        // Boundary: first indoor index is 0x100.
        Assert.True(IndoorNavService.IsIndoorCell(0x86020100u));
    }

    [Fact]
    public void IsIndoorCell_Outdoor()
    {
        // Outdoor LandCell with lower bits 0x0001..0x00FF.
        Assert.False(IndoorNavService.IsIndoorCell(0xA9B40001u));
        Assert.False(IndoorNavService.IsIndoorCell(0xA9B400FFu));
        Assert.False(IndoorNavService.IsIndoorCell(0xA9B40000u));
    }

    [Fact]
    public void GetLandblockId_ReturnsUpper16Bits()
    {
        Assert.Equal((ushort)0x8602, IndoorNavService.GetLandblockId(0x860201ADu));
        Assert.Equal((ushort)0xA9B4, IndoorNavService.GetLandblockId(0xA9B40001u));
    }

    [Fact]
    public void DisabledService_AlwaysReturnsDisabled()
    {
        var svc = new IndoorNavService();
        Assert.False(svc.IsEnabled);
        var r = svc.TryFindPath(
            0x860201ADu, new Vector3(100, -50, 0),
            0x860201B0u, new Vector3(120, -50, 0),
            new HashSet<uint>());
        Assert.Equal(IndoorPathStatus.Disabled, r.Status);
        Assert.Empty(r.Waypoints);
        Assert.Equal(1, svc.Telemetry.Disabled);
    }

    [Fact]
    public void DisabledService_DoesNotShortCircuitBeforeDisabledCheck()
    {
        // Even with outdoor cell ids, the disabled service should
        // still return Disabled (and increment that counter, not
        // NotIndoor). Catches regressions where someone reorders
        // the guard chain in TryFindPath.
        var svc = new IndoorNavService();
        var r = svc.TryFindPath(
            0xA9B40001u, Vector3.Zero,
            0xA9B40002u, Vector3.Zero,
            new HashSet<uint>());
        Assert.Equal(IndoorPathStatus.Disabled, r.Status);
        Assert.Equal(0, svc.Telemetry.NotIndoor);
    }

    [Fact]
    public void TelemetrySummary_IncludesAllCategories()
    {
        var telem = new IndoorNavTelemetry();
        // Reflection-free smoke: just make sure the Summary
        // string mentions every visible category so a grep-based
        // log scraper can find them.
        var s = telem.Summary();
        Assert.Contains("success=", s);
        Assert.Contains("no-path=", s);
        Assert.Contains("not-indoor=", s);
        Assert.Contains("cross-lb=", s);
        Assert.Contains("no-graph=", s);
        Assert.Contains("disabled=", s);
        Assert.Contains("graphs=", s);
    }
}
