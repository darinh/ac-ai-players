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

    // ---- Hunt-bias post-pass (remembered Mob sightings break a near-tie) ----

    private const float MobWindow = 36f;   // half an outdoor step
    private const float MobMinDist = 72f;  // ignore sightings closer than one step

    private static OutdoorFrontierExplorer.MonsterSighting Mob(float gx, float gy) =>
        new(gx, gy);

    [Fact]
    public void EmptyMobList_LeavesGeometryResultUnchanged()
    {
        // No visited, no mobs -> deterministic sector-0 pick, exactly as the
        // no-bias overload. Audit-safety: zero monsters => zero influence.
        var baseline = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency);
        var withEmpty = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency,
            Array.Empty<OutdoorFrontierExplorer.MonsterSighting>(), MobWindow, MobMinDist);

        Assert.Equal(baseline!.Value.Sector, withEmpty!.Value.Sector);
        Assert.Equal(baseline.Value.DestCellId, withEmpty.Value.DestCellId);
    }

    [Fact]
    public void ZeroWindow_DisablesMobBias()
    {
        // Even WITH a north sighting, a zero tie-window (the value the caller
        // passes when the Explore is NOT hunt-authorized) means no bias: the
        // default sector-0 (east) pick stands.
        var mobs = new List<OutdoorFrontierExplorer.MonsterSighting> { Mob(SelfX, SelfY + 200f) };
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency, mobs, 0f, MobMinDist);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Sector); // east — bias disabled
    }

    [Fact]
    public void MobSightingBreaksExactTie_TowardItsBearing()
    {
        // No visited -> all 8 sectors tie (geometry can't choose); default is
        // sector 0 (east). A remembered Mob due NORTH (sector 2) must win.
        var mobs = new List<OutdoorFrontierExplorer.MonsterSighting> { Mob(SelfX, SelfY + 200f) };
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency, mobs, MobWindow, MobMinDist);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Sector);       // north sector
        Assert.True(result.Value.GlobalY > SelfY);
        Assert.True(Math.Abs(result.Value.GlobalX - SelfX) < 1f);
    }

    [Fact]
    public void MobWithinWindow_WinsOverSlightlyBetterGeometry()
    {
        // Visited just WEST: EAST is the best geometric pick, but NORTH scores
        // within the tie window of it. A Mob due NORTH must pull the choice
        // north even though east scored marginally higher.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample> { Sample(SelfX - 30f, SelfY, Now) };

        var noMob = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency);
        Assert.Equal(0, noMob!.Value.Sector); // east is best without the bias

        var mobs = new List<OutdoorFrontierExplorer.MonsterSighting> { Mob(SelfX, SelfY + 200f) };
        var withMob = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency,
            mobs, MobWindow, MobMinDist);

        Assert.Equal(2, withMob!.Value.Sector); // north wins the near-tie
        Assert.True(withMob.Value.GlobalY > SelfY);
    }

    [Fact]
    public void MobInFarWorseSector_CannotOverrideGeometry()
    {
        // Visited cluster to the WEST -> EAST clearly best (well beyond the
        // tie window). A Mob to the WEST sits in a far-worse sector and must
        // NOT drag the bot back into explored ground.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX - 60f, SelfY, Now),
            Sample(SelfX - 70f, SelfY + 10f, Now),
            Sample(SelfX - 80f, SelfY - 10f, Now),
        };
        var mobs = new List<OutdoorFrontierExplorer.MonsterSighting> { Mob(SelfX - 200f, SelfY) };

        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency,
            mobs, MobWindow, MobMinDist);

        Assert.NotNull(result);
        Assert.True(result!.Value.GlobalX > SelfX, // still east, not west
            $"expected eastward pick, got ({result.Value.GlobalX},{result.Value.GlobalY})");
    }

    [Fact]
    public void CooledMobSector_IsStillSkipped()
    {
        // A Mob due EAST would bias east, but the east cell is cooled. The
        // cooldown wins (a Mob never resurrects a suppressed cell).
        var eastCell = AcCoords.OutdoorCellIdFromGlobal(SelfX + Step, SelfY);
        var cooled = new HashSet<uint> { eastCell };
        var mobs = new List<OutdoorFrontierExplorer.MonsterSighting> { Mob(SelfX + 200f, SelfY) };

        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            cooled, Now, Step, Locality, Recency, mobs, MobWindow, MobMinDist);

        Assert.NotNull(result);
        Assert.NotEqual(eastCell, result!.Value.DestCellId);
    }

    [Fact]
    public void MobCloserThanMinDistance_IsIgnored()
    {
        // A Mob within the min-distance (the bot is essentially there) does
        // not bias -> default sector-0 pick stands (no orbit around a stale,
        // already-reached coord).
        var mobs = new List<OutdoorFrontierExplorer.MonsterSighting> { Mob(SelfX, SelfY + 30f) };
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency, mobs, MobWindow, MobMinDist);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Sector); // east — too-close mob ignored
    }

    // ---- directional Explore heading-bias -----------------------------

    [Fact]
    public void Heading_BreaksTie_TowardChosenBearing()
    {
        // No visited history => all 8 sectors tie on geometric score, so the
        // LLM-chosen heading decides. "north" must pick the north sector (+Y).
        var n = OutdoorFrontierExplorer.TryHeadingVector("north")!.Value;
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, n.X, n.Y, 36f);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Sector); // sector 2 = +Y = north
        Assert.True(result.Value.GlobalY > SelfY);
        Assert.True(Math.Abs(result.Value.GlobalX - SelfX) < 1f);
    }

    [Fact]
    public void Heading_CannotOverrideAClearlyBetterDirection()
    {
        // A tight western cluster makes EAST clearly the least-explored bearing
        // (score far beyond the near-tie window). A heading pointing WEST (back
        // at the explored cluster) must NOT drag the bot there — the steer is a
        // near-tie bias only, so the geometric east pick stands.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX - 60f, SelfY, Now),
            Sample(SelfX - 70f, SelfY + 10f, Now),
            Sample(SelfX - 80f, SelfY - 10f, Now),
        };
        var w = OutdoorFrontierExplorer.TryHeadingVector("west")!.Value;
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, w.X, w.Y, 36f);

        Assert.NotNull(result);
        Assert.True(result!.Value.GlobalX > SelfX,
            $"heading should not override the clearly-better east pick, got ({result.Value.GlobalX},{result.Value.GlobalY})");
    }

    [Fact]
    public void Heading_Dominant_CommitsTowardCommandedBearing_OverClearlyBetterGeometry()
    {
        // Same setup as the near-tie test above: a tight western cluster makes
        // EAST clearly the least-explored bearing. With headingDominant, a WEST
        // command must COMMIT west (sustained directed travel toward a named
        // distant target) even though east scores far better geometrically —
        // while the same call WITHOUT dominance still yields the east pick.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX - 60f, SelfY, Now),
            Sample(SelfX - 70f, SelfY + 10f, Now),
            Sample(SelfX - 80f, SelfY - 10f, Now),
        };
        var w = OutdoorFrontierExplorer.TryHeadingVector("west")!.Value;

        var nearTie = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, w.X, w.Y, 36f);
        var dominant = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, w.X, w.Y, 36f, headingDominant: true);

        Assert.NotNull(nearTie);
        Assert.True(nearTie!.Value.GlobalX > SelfX,
            "near-tie heading must NOT override the clearly-better east pick");
        Assert.NotNull(dominant);
        Assert.Equal(4, dominant!.Value.Sector); // sector 4 = -X = west
        Assert.True(dominant.Value.GlobalX < SelfX,
            $"dominant heading must commit WEST despite east being least-explored, got ({dominant.Value.GlobalX},{dominant.Value.GlobalY})");
    }

    [Fact]
    public void Heading_Dominant_SkipsCooledCommandedCell_RoutesToNextBestForwardSector()
    {
        // A southern visited cluster makes NORTH the least-explored bearing, so
        // a NEAR-TIE south heading steers NORTH (the wrong way) — only DOMINANT
        // mode commits south. With the due-south step on cooldown the dominant
        // pick must ROTATE to the next-best forward sector (SW/SE), still
        // heading generally south (obstacle routing), where the near-tie pick
        // does the opposite. The opposed near-tie result is what makes this test
        // non-vacuous: it would fail if headingDominant were ignored.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX, SelfY - 100f, Now),
            Sample(SelfX - 10f, SelfY - 90f, Now),
            Sample(SelfX + 10f, SelfY - 110f, Now),
        };
        var south = OutdoorFrontierExplorer.TryHeadingVector("south")!.Value;

        // Locate (and confirm) the due-south step under dominance, then cool it.
        var open = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, south.X, south.Y, 36f, headingDominant: true);
        Assert.NotNull(open);
        Assert.Equal(6, open!.Value.Sector); // sector 6 = -Y = due south

        var cooled = new HashSet<uint> { open.Value.DestCellId };
        var routed = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, cooled, Now, Step, Locality, Recency,
            null, 0f, 0f, south.X, south.Y, 36f, headingDominant: true);
        var nearTieRouted = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, cooled, Now, Step, Locality, Recency,
            null, 0f, 0f, south.X, south.Y, 36f); // headingDominant defaults false

        Assert.NotNull(routed);
        // Deterministic: SW(5) and SE(7) tie on alignment to SOUTH, then the
        // unexplored-score tie-break decides. The cluster is offset so its
        // nearest sample sits closer to SW, giving SE the higher score — so the
        // alignment->score ordering must resolve to SE(7), not SW(5).
        Assert.Equal(7, routed!.Value.Sector);
        Assert.True(routed.Value.GlobalY < SelfY,
            "the rotated dominant pick must still head generally south");
        Assert.NotNull(nearTieRouted);
        // Without dominance the heading only resolves near-ties: the eligible
        // set is the unexplored NORTH arc, and alignment->score lands on NE(1)
        // — the opposite of the dominant southward rotation.
        Assert.Equal(1, nearTieRouted!.Value.Sector);
        Assert.True(nearTieRouted.Value.GlobalY > SelfY,
            "without dominance the near-tie heading steers toward the unexplored NORTH (so dominance is required)");
    }

    [Fact]
    public void Heading_Dominant_ForwardArcAllCooled_DefersToGeometry()
    {
        // A southern visited cluster makes NORTH (sector 2) the least-explored
        // bearing among the non-cooled sectors. With the ENTIRE forward (south)
        // hemisphere cooled, the dominant steer must DECLINE and let the
        // geometric best (due north) stand — NOT force the least-backward
        // aligned pick (NE, sector 1), which is what a missing forward-arc guard
        // would do. Asserting the exact geometric sector makes this non-vacuous.
        var visited = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX, SelfY - 100f, Now),
            Sample(SelfX - 10f, SelfY - 90f, Now),
            Sample(SelfX + 10f, SelfY - 110f, Now),
        };
        var cooled = new HashSet<uint>();
        foreach (var dir in new[] { "south", "southeast", "southwest", "east", "west" })
        {
            var v = OutdoorFrontierExplorer.TryHeadingVector(dir)!.Value;
            var pick = OutdoorFrontierExplorer.ChooseFrontier(
                SelfX, SelfY, visited, NoCooldown, Now, Step, Locality, Recency,
                null, 0f, 0f, v.X, v.Y, 36f, headingDominant: true);
            Assert.NotNull(pick);
            cooled.Add(pick!.Value.DestCellId);
        }

        var south = OutdoorFrontierExplorer.TryHeadingVector("south")!.Value;
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, visited, cooled, Now, Step, Locality, Recency,
            null, 0f, 0f, south.X, south.Y, 36f, headingDominant: true);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Sector); // due north — the geometric best, proving deferral (not NE forced-align)
        Assert.True(result.Value.GlobalY > SelfY,
            "deferred geometric pick must not be a forced southward step");
    }

    [Fact]
    public void Sweep_RedirectsTheTunnelingTrailGeometry_ViaFallbackChannel()
    {
        // Reproduce the cp-2363 tunnel: the bot has walked EAST, leaving a trail
        // of visited samples behind it to the west (including its current spot).
        // Every 72 m candidate's nearest reference is the bot's own current node,
        // so east/north/south all tie on geometric score; the undirected
        // tie-break ("away from the visited centroid") picks EAST — the travel
        // direction — and tunnels. The mechanical sweep is wired as the
        // LOW-precedence FALLBACK heading; supplying it must redirect the SAME
        // geometry to a different bearing (the anti-tunnel core).
        var trail = new List<OutdoorFrontierExplorer.VisitedSample>
        {
            Sample(SelfX,         SelfY, Now),
            Sample(SelfX - 72f,   SelfY, Now),
            Sample(SelfX - 144f,  SelfY, Now),
            Sample(SelfX - 216f,  SelfY, Now),
        };

        // Undirected (the bug): the away-from-centroid tie-break tunnels east.
        var tunneled = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, trail, NoCooldown, Now, Step, Locality, Recency);
        Assert.NotNull(tunneled);
        Assert.Equal(0, tunneled!.Value.Sector); // east = the tunnel bearing

        // The fallback sweep heading steers the SAME geometry to new bearings.
        var north = OutdoorFrontierExplorer.TryHeadingVector("north")!.Value;
        var withNorth = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, trail, NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, 0f, 0f, 0f, north.X, north.Y, 36f);
        Assert.Equal(2, withNorth!.Value.Sector); // north, not east

        var south = OutdoorFrontierExplorer.TryHeadingVector("south")!.Value;
        var withSouth = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, trail, NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, 0f, 0f, 0f, south.X, south.Y, 36f);
        Assert.Equal(6, withSouth!.Value.Sector); // south, not east
    }

    [Fact]
    public void Fallback_DoesNotOverride_ExplicitHeading()
    {
        // When BOTH an explicit LLM heading and a mechanical fallback heading are
        // supplied, the explicit heading (higher precedence) must win.
        var east = OutdoorFrontierExplorer.TryHeadingVector("east")!.Value;
        var north = OutdoorFrontierExplorer.TryHeadingVector("north")!.Value;
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f,
            east.X, east.Y, 36f,      // explicit heading => east
            north.X, north.Y, 36f);   // fallback => north (must lose)

        Assert.Equal(0, result!.Value.Sector); // east, the explicit heading
    }

    [Fact]
    public void Fallback_DoesNotOverride_MobBias()
    {
        // When a remembered monster sighting and a fallback sweep heading both
        // apply, mob-bias (higher precedence) must win — the sweep never drags a
        // hunting bot off a remembered monster.
        var sightings = new List<OutdoorFrontierExplorer.MonsterSighting>
        {
            new(SelfX, SelfY + 100f), // a monster to the NORTH
        };
        var south = OutdoorFrontierExplorer.TryHeadingVector("south")!.Value;
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency,
            sightings, 36f, 1f,        // mob-bias active => north (the sighting)
            0f, 0f, 0f,                // no explicit heading
            south.X, south.Y, 36f);    // fallback => south (must lose)

        Assert.Equal(2, result!.Value.Sector); // north, toward the monster
    }

    [Fact]
    public void Fallback_ZeroWindow_IsNoOp()
    {
        // A fallback heading with a zero tie window is inactive => identical to
        // the undirected pick (sector 0 = east with no references).
        var n = OutdoorFrontierExplorer.TryHeadingVector("north")!.Value;
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, 0f, 0f, 0f, n.X, n.Y, 0f);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Sector); // east — unchanged default
    }

    [Fact]
    public void Heading_ZeroWindow_IsNoOp()
    {
        // A heading vector with a zero tie window is inactive => identical to
        // the undirected pick (sector 0 = east with no references).
        var n = OutdoorFrontierExplorer.TryHeadingVector("north")!.Value;
        var result = OutdoorFrontierExplorer.ChooseFrontier(
            SelfX, SelfY, Array.Empty<OutdoorFrontierExplorer.VisitedSample>(),
            NoCooldown, Now, Step, Locality, Recency,
            null, 0f, 0f, n.X, n.Y, 0f);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Sector); // east — unchanged default
    }

    [Theory]
    [InlineData("north", 0f, 1f)]
    [InlineData("east", 1f, 0f)]
    [InlineData("south", 0f, -1f)]
    [InlineData("west", -1f, 0f)]
    public void TryHeadingVector_ParsesCardinalPoints(string heading, float ex, float ey)
    {
        var v = OutdoorFrontierExplorer.TryHeadingVector(heading);
        Assert.NotNull(v);
        Assert.Equal(ex, v!.Value.X, 3);
        Assert.Equal(ey, v.Value.Y, 3);
    }

    [Theory]
    [InlineData("ne")]
    [InlineData("NorthEast")]
    [InlineData("  northeast  ")]
    public void TryHeadingVector_AcceptsAbbreviationsAndCaseAndWhitespace(string heading)
    {
        var v = OutdoorFrontierExplorer.TryHeadingVector(heading);
        Assert.NotNull(v);
        // northeast = (+,+), equal magnitude on both axes.
        Assert.True(v!.Value.X > 0f && v.Value.Y > 0f);
        Assert.Equal(v.Value.X, v.Value.Y, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("up")]
    [InlineData("toward the trees")]
    public void TryHeadingVector_NullEmptyOrUnknown_ReturnsNull(string? heading)
    {
        Assert.Null(OutdoorFrontierExplorer.TryHeadingVector(heading));
    }
}
