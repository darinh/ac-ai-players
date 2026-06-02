// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for WorldDistance (cell-aware squared-distance math) and
// the WorldState spatial query API (EnumerateNearby, WithinRadius,
// NearestN, OfType).
//
// Coverage strategy:
//   - WorldDistance: pure math. Same-cell fast path, cross-landblock
//     scaling, signed (west/south) deltas, boundary crossing,
//     Z passthrough, TrySquaredDistance null-CellId failure mode.
//   - WorldState spatial: integration via real ObjectCreate
//     messages so the snapshots populate exactly like production.

using System;
using System.Linq;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class WorldSpatialTests
{
    private const uint SelfGuid = 0x50000005;
    private const uint TargetA  = 0x50000010;
    private const uint TargetB  = 0x50000011;
    private const uint TargetC  = 0x50000012;
    private const uint TargetD  = 0x50000013;

    // Cell ids — top byte is landblock X, next byte landblock Y,
    // low 16 bits are the cell index within the landblock.
    // 0x12 = landblock X, 0x34 = landblock Y. 0x12340001 means
    // outdoor surface cell 1 of landblock (0x12, 0x34).
    private const uint CellLB1234_Cell1 = 0x12340001;
    private const uint CellLB1234_Cell2 = 0x12340002;
    private const uint CellLB1334_Cell1 = 0x13340001; // one east  of LB1234
    private const uint CellLB1134_Cell1 = 0x11340001; // one west  of LB1234
    private const uint CellLB1235_Cell1 = 0x12350001; // one north of LB1234
    private const uint CellLB1233_Cell1 = 0x12330001; // one south of LB1234
    private const uint CellLB1335_Cell1 = 0x13350001; // NE

    // ---- WorldDistance.SquaredDistanceBetween ----

    [Fact]
    public void SameCell_UsesLocalDelta()
    {
        var a = new Vector3(10, 20, 30);
        var b = new Vector3(13, 24, 30);
        // dx=3, dy=4, dz=0 -> 9+16+0 = 25
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1234_Cell1, a, CellLB1234_Cell1, b);
        Assert.Equal(25f, d2);
    }

    [Fact]
    public void SameLandblockDifferentCell_UsesLocalDelta()
    {
        // Same LX/LY, different cell index -> cross-landblock branch
        // fires with zero LX/LY deltas, reducing to local delta.
        var a = new Vector3(10, 20, 30);
        var b = new Vector3(13, 24, 30);
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1234_Cell1, a, CellLB1234_Cell2, b);
        Assert.Equal(25f, d2);
    }

    [Fact]
    public void OneLandblockEast_AddsScaleFactor()
    {
        var a = new Vector3(100, 0, 0);
        var b = new Vector3(50, 0, 0);
        // origin in LB1234, target in LB1334 (one east).
        // dx = (0x12 - 0x13) * 192 + 100 - 50 = -192 + 50 = -142
        // d2 = 142^2 = 20164
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1234_Cell1, a, CellLB1334_Cell1, b);
        Assert.Equal(20164f, d2);
    }

    [Fact]
    public void OneLandblockWest_NegativeDeltaDoesNotUnderflow()
    {
        // Regression: if landblock components are subtracted as uint,
        // underflow makes the delta ~4 billion, distance ~10^19.
        // Must remain in single-precision-finite range.
        var a = new Vector3(10, 0, 0);
        var b = new Vector3(190, 0, 0);
        // origin in LB1234, target in LB1134 (one west).
        // dx = (0x12 - 0x11) * 192 + 10 - 190 = 192 - 180 = 12
        // d2 = 144
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1234_Cell1, a, CellLB1134_Cell1, b);
        Assert.Equal(144f, d2);
        Assert.True(float.IsFinite(d2));
    }

    [Fact]
    public void OneLandblockSouth_NegativeYDeltaDoesNotUnderflow()
    {
        var a = new Vector3(0, 10, 0);
        var b = new Vector3(0, 190, 0);
        // origin in LB1234, target in LB1233 (one south).
        // dy = (0x34 - 0x33) * 192 + 10 - 190 = 192 - 180 = 12
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1234_Cell1, a, CellLB1233_Cell1, b);
        Assert.Equal(144f, d2);
    }

    [Fact]
    public void BoundaryCrossing_SmallActualDistance()
    {
        // Object at X=191 in west landblock, object at X=1 in east
        // landblock. Actual distance is 2, not 190 or 382.
        var west  = new Vector3(191, 50, 0);
        var east  = new Vector3(1,   50, 0);
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1134_Cell1, west, CellLB1234_Cell1, east);
        // dx = (0x11 - 0x12) * 192 + 191 - 1 = -192 + 190 = -2
        // d2 = 4
        Assert.Equal(4f, d2);
    }

    [Fact]
    public void DiagonalNorthEast_BothDeltasContribute()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(0, 0, 0);
        // origin LB1234, target LB1335 (NE one).
        // dx = (0x12 - 0x13) * 192 + 0 - 0 = -192
        // dy = (0x34 - 0x35) * 192 + 0 - 0 = -192
        // d2 = 192^2 + 192^2 = 73728
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1234_Cell1, a, CellLB1335_Cell1, b);
        Assert.Equal(73728f, d2);
    }

    [Fact]
    public void ZAxis_PassesThroughAsLocalDelta()
    {
        var a = new Vector3(0, 0, 100);
        var b = new Vector3(0, 0, 90);
        // Same XY landblock-equivalent setup, just Z differs by 10.
        var d2 = WorldDistance.SquaredDistanceBetween(
            CellLB1234_Cell1, a, CellLB1234_Cell2, b);
        // dx=0, dy=0, dz=10 -> 100
        Assert.Equal(100f, d2);
    }

    [Fact]
    public void DistanceToSelf_IsZero()
    {
        var pos = new Vector3(42, 99, 12);
        var d2 = WorldDistance.SquaredDistanceBetween(CellLB1234_Cell1, pos, CellLB1234_Cell1, pos);
        Assert.Equal(0f, d2);
    }

    // ---- WorldDistance.TrySquaredDistance ----

    [Fact]
    public void TrySquaredDistance_BothHavePositions_ReturnsTrue()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, new Vector3(0, 0, 0)));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(3, 4, 0)));
        Assert.True(WorldDistance.TrySquaredDistance(
            ws.TryGet(SelfGuid)!, ws.TryGet(TargetA)!, out var d2));
        Assert.Equal(25f, d2);
    }

    [Fact]
    public void TrySquaredDistance_FirstHasNoCellId_ReturnsFalse()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid); // SelfGuid snapshot has no CellId yet
        ws.Apply(BuildOC(TargetA, CellLB1234_Cell1, Vector3.Zero));
        Assert.False(WorldDistance.TrySquaredDistance(
            ws.TryGet(SelfGuid)!, ws.TryGet(TargetA)!, out var d2));
        Assert.True(float.IsNaN(d2));
    }

    [Fact]
    public void TrySquaredDistance_SecondHasNoCellId_ReturnsFalse()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.SetSelf(TargetA); // TargetA snapshot has no CellId
        Assert.False(WorldDistance.TrySquaredDistance(
            ws.TryGet(SelfGuid)!, ws.TryGet(TargetA)!, out _));
    }

    [Fact]
    public void TrySquaredDistance_NullArg_Throws()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        Assert.Throws<ArgumentNullException>(
            () => WorldDistance.TrySquaredDistance(null!, ws.TryGet(SelfGuid)!, out _));
        Assert.Throws<ArgumentNullException>(
            () => WorldDistance.TrySquaredDistance(ws.TryGet(SelfGuid)!, null!, out _));
    }

    // ---- WorldState.EnumerateNearby ----

    [Fact]
    public void EnumerateNearby_ExcludesOriginAndPositionlessSnapshots()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(5, 0, 0)));
        // PrivateUpdatePropertyInt routes to SelfGuid, but it also
        // materializes a SECOND snapshot for SelfGuid only — to
        // create a positionless snapshot for a different guid we
        // can't go through normal Apply paths. Instead, create a
        // positionless target directly and add it via reflection on
        // the dictionary. Cleaner alternative: ensure the rule by
        // proving EnumerateNearby's loop body actually checks CellId
        // (covered by unit tests on the helper above). Here we just
        // verify the origin-exclusion + happy path.
        var origin = ws.TryGet(SelfGuid)!;
        var nearby = ws.EnumerateNearby(origin);

        Assert.Single(nearby);
        Assert.Equal(TargetA, nearby[0].Object.Guid);
        Assert.Equal(25f, nearby[0].SquaredDistance);
    }

    [Fact]
    public void EnumerateNearby_SkipsTargetWithoutCellId()
    {
        // To create a positionless target, exploit the fact that
        // ApplyPrivatePropertyInt creates a snapshot for SelfGuid
        // with no CellId. Set SelfGuid to TargetB, fire a property
        // update (which creates an empty TargetB snapshot), then
        // restore SelfGuid back to the real self via re-SetSelf.
        // The stderr "mismatch" line is benign in test output.
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(5, 0, 0)));

        ws.SetSelf(TargetB);                                                                   // creates positionless TargetB snapshot
        ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25, Value: 99));   // populates TargetB props
        ws.SetSelf(SelfGuid);                                                                  // restore self

        Assert.NotNull(ws.TryGet(TargetB));
        Assert.Null(ws.TryGet(TargetB)!.CellId);

        var nearby = ws.EnumerateNearby(ws.TryGet(SelfGuid)!);
        Assert.Single(nearby);
        Assert.Equal(TargetA, nearby[0].Object.Guid);
    }

    [Fact]
    public void EnumerateNearby_OriginWithoutCellId_ReturnsEmpty()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid); // no CellId
        ws.Apply(BuildOC(TargetA, CellLB1234_Cell1, Vector3.Zero));
        Assert.Empty(ws.EnumerateNearby(ws.TryGet(SelfGuid)!));
    }

    [Fact]
    public void EnumerateNearby_OriginMatchedByGuid_NotByReference()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(5, 0, 0)));

        // Build a separate snapshot instance with the same Guid +
        // a non-zero offset position. EnumerateNearby must still
        // exclude SelfGuid from results, even though we pass a
        // different reference.
        var fakeOrigin = new WorldObjectSnapshot(SelfGuid)
        {
            CellId = CellLB1234_Cell1,
            Position = new Vector3(0, 0, 0),
        };
        var nearby = ws.EnumerateNearby(fakeOrigin);
        Assert.Single(nearby);
        Assert.Equal(TargetA, nearby[0].Object.Guid);
    }

    // ---- WorldState.WithinRadius ----

    [Fact]
    public void WithinRadius_FiltersByRadius()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(3, 0, 0)));   // d=3
        ws.Apply(BuildOC(TargetB,  CellLB1234_Cell1, new Vector3(10, 0, 0)));  // d=10
        ws.Apply(BuildOC(TargetC,  CellLB1234_Cell1, new Vector3(50, 0, 0)));  // d=50

        var origin = ws.TryGet(SelfGuid)!;
        var within = ws.WithinRadius(origin, 10f);

        var guids = within.Select(s => s.Guid).OrderBy(g => g).ToArray();
        Assert.Equal(new uint[] { TargetA, TargetB }, guids);
    }

    [Fact]
    public void WithinRadius_RadiusZero_ReturnsOnlyCoincident()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, Vector3.Zero));            // d=0
        ws.Apply(BuildOC(TargetB,  CellLB1234_Cell1, new Vector3(1, 0, 0)));    // d=1

        var within = ws.WithinRadius(ws.TryGet(SelfGuid)!, 0f);
        Assert.Single(within);
        Assert.Equal(TargetA, within[0].Guid);
    }

    [Fact]
    public void WithinRadius_NegativeRadius_Throws()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ws.WithinRadius(ws.TryGet(SelfGuid)!, -1f));
    }

    [Fact]
    public void WithinRadius_NaNRadius_Throws()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ws.WithinRadius(ws.TryGet(SelfGuid)!, float.NaN));
    }

    // ---- WorldState.NearestN ----

    [Fact]
    public void NearestN_SortsAscending()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(50, 0, 0)));  // d=50
        ws.Apply(BuildOC(TargetB,  CellLB1234_Cell1, new Vector3(3, 0, 0)));   // d=3
        ws.Apply(BuildOC(TargetC,  CellLB1234_Cell1, new Vector3(10, 0, 0)));  // d=10

        var top3 = ws.NearestN(ws.TryGet(SelfGuid)!, 3);
        Assert.Equal(new uint[] { TargetB, TargetC, TargetA },
            top3.Select(s => s.Guid).ToArray());
    }

    [Fact]
    public void NearestN_LimitsResults()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(50, 0, 0)));
        ws.Apply(BuildOC(TargetB,  CellLB1234_Cell1, new Vector3(3, 0, 0)));
        ws.Apply(BuildOC(TargetC,  CellLB1234_Cell1, new Vector3(10, 0, 0)));

        var top1 = ws.NearestN(ws.TryGet(SelfGuid)!, 1);
        Assert.Single(top1);
        Assert.Equal(TargetB, top1[0].Guid);
    }

    [Fact]
    public void NearestN_CountZero_ReturnsEmpty()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(3, 0, 0)));
        Assert.Empty(ws.NearestN(ws.TryGet(SelfGuid)!, 0));
    }

    [Fact]
    public void NearestN_CountExceedsAvailable_ReturnsAll()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(3, 0, 0)));
        ws.Apply(BuildOC(TargetB,  CellLB1234_Cell1, new Vector3(10, 0, 0)));

        var result = ws.NearestN(ws.TryGet(SelfGuid)!, 100);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NearestN_NegativeCount_Throws()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ws.NearestN(ws.TryGet(SelfGuid)!, -1));
    }

    [Fact]
    public void NearestN_TieBreaksByGuidAscending()
    {
        // Two targets at the exact same distance — tie-break by guid.
        var ws = new WorldState();
        ws.Apply(BuildOC(SelfGuid, CellLB1234_Cell1, Vector3.Zero));
        ws.Apply(BuildOC(TargetD,  CellLB1234_Cell1, new Vector3(5, 0, 0))); // d=5
        ws.Apply(BuildOC(TargetA,  CellLB1234_Cell1, new Vector3(5, 0, 0))); // d=5
        ws.Apply(BuildOC(TargetB,  CellLB1234_Cell1, new Vector3(5, 0, 0))); // d=5

        var sorted = ws.NearestN(ws.TryGet(SelfGuid)!, 3);
        Assert.Equal(new uint[] { TargetA, TargetB, TargetD },
            sorted.Select(s => s.Guid).ToArray());
    }

    // ---- WorldState.OfType ----

    [Fact]
    public void OfType_MatchesBitfieldIntersection()
    {
        var ws = new WorldState();
        // AC ItemType bitfield (sampling):
        //   0x00000010 Creature
        //   0x00000020 Portal
        //   0x00000080 Container
        ws.Apply(BuildOC(TargetA, CellLB1234_Cell1, Vector3.Zero, itemType: 0x00000010)); // Creature
        ws.Apply(BuildOC(TargetB, CellLB1234_Cell1, Vector3.Zero, itemType: 0x00000020)); // Portal
        ws.Apply(BuildOC(TargetC, CellLB1234_Cell1, Vector3.Zero, itemType: 0x00000080)); // Container

        // Match creatures | portals
        var matched = ws.OfType(0x00000030);
        var guids = matched.Select(s => s.Guid).OrderBy(g => g).ToArray();
        Assert.Equal(new uint[] { TargetA, TargetB }, guids);
    }

    [Fact]
    public void OfType_ZeroMask_ReturnsEmpty()
    {
        var ws = new WorldState();
        ws.Apply(BuildOC(TargetA, CellLB1234_Cell1, Vector3.Zero, itemType: 0x10));
        Assert.Empty(ws.OfType(0));
    }

    [Fact]
    public void OfType_ExcludesSnapshotsWithoutItemType()
    {
        var ws = new WorldState();
        ws.SetSelf(SelfGuid); // no ObjectCreate → no ItemType
        ws.Apply(BuildOC(TargetA, CellLB1234_Cell1, Vector3.Zero, itemType: 0x10));

        var matched = ws.OfType(0xFFFFFFFF);
        Assert.Single(matched);
        Assert.Equal(TargetA, matched[0].Guid);
    }

    // ---- Helpers ----
    // Local builder; extends WorldStateTests.BuildObjectCreate with
    // ItemType + Position-from-Vector3 conveniences. Inlined rather
    // than refactored into a shared helper class to keep this change
    // scoped to the new test file.

    private static ObjectCreateMessage BuildOC(
        uint guid,
        uint cellId,
        Vector3 position,
        uint itemType = 0)
    {
        var op = new ObjectPosition(
            cellId,
            position.X, position.Y, position.Z,
            1f, 0f, 0f, 0f);

        var model = new ObjectModelData(
            PaletteId: null,
            SubPalettes: Array.Empty<SubPaletteEntry>(),
            TextureChanges: Array.Empty<TextureChangeEntry>(),
            AnimPartChanges: Array.Empty<AnimPartChangeEntry>());

        var physics = new ObjectPhysicsData(
            DescriptionFlags: PhysicsDescriptionFlag.Position,
            PhysicsState: 0,
            MovementBody: null, MovementIsAutonomous: null, AnimationFramePlacement: null,
            Position: op,
            MotionTableId: null, SoundTableId: null, PhysicsTableId: null, SetupTableId: null,
            ParentWielderId: null, ParentLocation: null, Children: null,
            ObjScale: null, Friction: null, Elasticity: null, Translucency: null,
            Velocity: null, Acceleration: null, Omega: null,
            DefaultScriptId: null, DefaultScriptIntensity: null,
            SeqObjectPosition: 1, SeqObjectMovement: 0, SeqObjectState: 0,
            SeqObjectVector: 0, SeqObjectTeleport: 0, SeqObjectServerControl: 0,
            SeqObjectForcePosition: 0, SeqObjectVisualDesc: 0, SeqObjectInstance: 1);

        var weenie = new ObjectWeenieHeader(
            Flags: 0, Flags2: 0,
            Name: "obj", WeenieClassId: 1, IconId: 0, ItemType: itemType, DescriptionFlags: 0,
            PluralName: null, ItemsCapacity: null, ContainersCapacity: null, AmmoType: null,
            Value: null, Usable: null, UseRadius: null, TargetType: null, UiEffects: null,
            CombatUse: null, Structure: null, MaxStructure: null, StackSize: null, MaxStackSize: null,
            ContainerGuid: null, WielderGuid: null, ValidLocations: null, CurrentlyWieldedLocation: null,
            Priority: null, RadarBlipColor: null, RadarBehavior: null, PScript: null, Workmanship: null,
            Burden: null, Spell: null, HouseOwner: null, HookItemTypes: null, MonarchGuid: null,
            HookType: null, IconOverlay: null, IconUnderlay: null, MaterialType: null,
            CooldownId: null, CooldownDuration: null, PetOwner: null);

        return new ObjectCreateMessage(guid, model, physics, weenie);
    }
}
