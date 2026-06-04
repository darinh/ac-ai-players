// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for OutdoorFrontierExplorer — autonomous OUTDOOR frontier
// search (the surface analogue of IndoorFrontierExplorer). Each test
// works in global meters and asserts the explorer steps toward the
// least-explored compass direction (or declines only when every
// candidate cell is on cooldown).

using System;
using System.Collections.Generic;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class OutdoorFrontierExplorerTests
{
    private static readonly IReadOnlySet<uint> NoCooldown = new HashSet<uint>();
    private static readonly DateTimeOffset Now = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);
    private const float Step = 72f;
    private const float Locality = 250f;
    private static readonly TimeSpan Recency = TimeSpan.FromMinutes(20);

    private static OutdoorFrontierExplorer.VisitedSample Sample(float gx, float gy, DateTimeOffset seen) =>
        new(gx, gy, seen);

    // Place the bot well inside a landblock so all 8 candidates resolve to
    // valid outdoor cells (away from the global origin clamp).
    private const float SelfX = 5000f;
    private const float SelfY = 5000f;

    [Fact]
    public void NoVisited_ReturnsDeterministicNonNullPick()
    {
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency);

        Assert.NotNull(result);
        // With no references the tie-break prefers the lowest sector index
        // (sector 0 = +X/east).
        Assert.Equal(0, result!.Value.Sector);
        Assert.True(result.Value.GlobalX > SelfX); // stepped east
    }

    [Fact]
    public void StepsAwayFromClusteredVisitedHistory()
    {
        // The bot has visited a tight cluster to the WEST. The least-explored
        // direction is therefore EAST (+X) — the candidate farthest from the
        // cluster.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX - 60f, SelfY, Now),
            Sample(SelfX - 70f, SelfY + 10f, Now),
            Sample(SelfX - 80f, SelfY - 10f, Now),
        };

        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency);

        Assert.NotNull(result);
        // East candidate (+X) is farthest from the western cluster.
        Assert.True(result!.Value.GlobalX > SelfX,
            $"expected eastward pick, got ({result.Value.GlobalX},{result.Value.GlobalY})");
        Assert.True(Math.Abs(result.Value.GlobalY - SelfY) < 1f);
    }

    [Fact]
    public void CooledCandidateCellIsSkipped()
    {
        // Cluster to the west -> best is east. Cool the east cell; the pick
        // must move to another (still-unexplored) bearing.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX - 60f, SelfY, Now),
            Sample(SelfX - 70f, SelfY + 10f, Now),
        };

        var eastCell = AcCoords.OutdoorCellIdFromGlobal(SelfX + Step, SelfY);
        var cooled = new HashSet<uint> { eastCell };

        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, cooled, Now, Step, Locality, Recency);

        Assert.NotNull(result);
        Assert.NotEqual(eastCell, result!.Value.DestCellId);
    }

    [Fact]
    public void AllCandidatesCooled_ReturnsNull()
    {
        // Cool every one of the 8 sector destination cells.
        var cooled = new HashSet<uint>();
        for (int k = 0; k < OutdoorFrontierExplorer.SectorCount; k++)
        {
            var angle = k * (2.0 * Math.PI / OutdoorFrontierExplorer.SectorCount);
            var gx = SelfX + (float)Math.Cos(angle) * Step;
            var gy = SelfY + (float)Math.Sin(angle) * Step;
            cooled.Add(AcCoords.OutdoorCellIdFromGlobal(gx, gy));
        }

        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            cooled, Now, Step, Locality, Recency);

        Assert.Null(result);
    }

    [Fact]
    public void StaleVisitedBeyondRecency_StillUsedWhenNoneRecent()
    {
        // The only visited sample is OLD (outside the recency window) but
        // LOCAL. With no recent references, the explorer relaxes to any-age
        // local samples and still steps away from it (east, away from the
        // western stale cluster).
        var stale = Now - TimeSpan.FromHours(3);
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX - 70f, SelfY, stale),
        };

        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency);

        Assert.NotNull(result);
        Assert.True(result!.Value.GlobalX > SelfX);
    }

    [Fact]
    public void FarAwayVisited_BeyondLocality_DoesNotInfluencePick()
    {
        // A visited sample far OUTSIDE the locality radius must be ignored,
        // so the choice degenerates to the no-reference deterministic pick
        // (sector 0). Put the far sample to the east; if it were (wrongly)
        // considered, the pick would flee west instead.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX + Locality + 500f, SelfY, Now),
        };

        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Sector); // east — far sample ignored
    }

    [Fact]
    public void DestCellMatchesChosenGlobalPoint()
    {
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency);

        Assert.NotNull(result);
        var expectedCell = AcCoords.OutdoorCellIdFromGlobal(
            result!.Value.GlobalX, result.Value.GlobalY);
        Assert.Equal(expectedCell, result.Value.DestCellId);
    }
}
