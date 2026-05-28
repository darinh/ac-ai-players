// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldState integration tests covering the rules locked in by
// the rubber-duck design pass:
//   1. ObjectCreate seeds a snapshot.
//   2. ObjectCreate for an existing guid MERGES (preserves
//      FirstSeen + PropertyInts, advances sequences only).
//   3. UpdatePosition with stale SeqPosition is dropped.
//   4. UpdatePosition with newer SeqInstance accepts even if
//      SeqPosition would otherwise look stale (new instance
//      epoch implies the position counter restarted on the wire).
//   5. UpdatePosition arriving BEFORE ObjectCreate creates a
//      partial snapshot covering late-join.
//   6. PrivateUpdatePropertyInt arriving BEFORE SelfGuid is
//      known is dropped.
//   7. SetSelf pre-seed pattern: PropertyInt that arrives
//      between SetSelf and PlayerCreate is correctly routed.
//   8. PrivateUpdatePropertyInt with stale byte-sequence is
//      dropped (and does NOT overwrite the existing value).
//   9. PrivateUpdatePropertyInt survives ObjectCreate for the
//      same guid (merge preserves PropertyInts).

using System;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class WorldStateTests
{
    private const uint TestGuid = 0x50000005;
    private const uint OtherGuid = 0x50000099;

    // ---- ObjectCreate ----

    [Fact]
    public void ObjectCreate_SeedsSnapshot()
    {
        var ws = new WorldState();
        var msg = BuildObjectCreate(
            guid: TestGuid,
            name: "Headless01",
            wcid: 1,
            seqInstance: 5,
            seqPosition: 10,
            position: new ObjectPosition(0x12340001, 100f, 200f, 50f, 1f, 0, 0, 0));

        Assert.True(ws.Apply(msg));

        var snap = ws.TryGet(TestGuid);
        Assert.NotNull(snap);
        Assert.Equal("Headless01", snap!.Name);
        Assert.Equal((uint)1, snap.WeenieClassId);
        Assert.Equal((ushort)5, snap.SeqInstance);
        Assert.Equal((ushort)10, snap.SeqPosition);
        Assert.Equal((uint)0x12340001, snap.CellId);
        Assert.Equal(100f, snap.Position.X);
    }

    [Fact]
    public void ObjectCreate_ForExistingGuid_PreservesFirstSeenAndPropertyInts()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25 /*Level*/, Value: 7));
        var snap0 = ws.TryGet(TestGuid)!;
        var firstSeen = snap0.FirstSeen;
        var propsBefore = snap0.PropertyInts!.Count;

        // Small artificial wait to let "FirstSeen" become observably
        // distinct from a fresh FirstSeen captured during merge.
        System.Threading.Thread.Sleep(5);

        ws.Apply(BuildObjectCreate(TestGuid, name: "Headless01", wcid: 1,
            seqInstance: 3, seqPosition: 5));

        var snap1 = ws.TryGet(TestGuid)!;
        Assert.Same(snap0, snap1);               // same instance (merge, not replace)
        Assert.Equal(firstSeen, snap1.FirstSeen);
        Assert.Equal(propsBefore, snap1.PropertyInts!.Count);
        Assert.Equal(7, snap1.PropertyInts[25]);
        Assert.Equal("Headless01", snap1.Name);  // weenie field refreshed
    }

    [Fact]
    public void ObjectCreate_SequenceHighWaterMark_NeverLowers()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 100, seqPosition: 200));
        var snap = ws.TryGet(TestGuid)!;
        Assert.Equal((ushort)100, snap.SeqInstance);
        Assert.Equal((ushort)200, snap.SeqPosition);

        // Server re-creates the object with a STALE sequence value
        // — should be ignored by the advance helper (high-water).
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 50, seqPosition: 150));
        Assert.Equal((ushort)100, snap.SeqInstance);
        Assert.Equal((ushort)200, snap.SeqPosition);
    }

    [Fact]
    public void ObjectCreate_StaleInstanceSequence_DroppedDoesNotClobberFields()
    {
        // Regression: an out-of-order ObjectCreate from an OLDER
        // instance epoch (e.g., late-arriving UDP packet from the
        // pre-respawn state) must NOT overwrite the newer snapshot's
        // identity or spatial fields.
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, name: "NewName", wcid: 99,
            seqInstance: 10, seqPosition: 50,
            position: new ObjectPosition(0x12340099, 999f, 888f, 777f, 1, 0, 0, 0)));

        var stale = BuildObjectCreate(TestGuid, name: "OldName", wcid: 1,
            seqInstance: 5, seqPosition: 999,
            position: new ObjectPosition(0x12340001, 1f, 2f, 3f, 1, 0, 0, 0));

        Assert.False(ws.Apply(stale));
        var snap = ws.TryGet(TestGuid)!;
        Assert.Equal("NewName", snap.Name);
        Assert.Equal((uint)99, snap.WeenieClassId);
        Assert.Equal((uint)0x12340099, snap.CellId);
        Assert.Equal(999f, snap.Position.X);
        Assert.Equal((ushort)10, snap.SeqInstance);
        Assert.Equal((ushort)50, snap.SeqPosition);
    }

    [Fact]
    public void ObjectCreate_SameInstance_StalePositionSeq_KeepsNewerPosition()
    {
        // Within the same instance epoch, an out-of-order ObjectCreate
        // with an older SeqObjectPosition must not overwrite the
        // newer spatial state (it CAN refresh identity fields — those
        // are intrinsic to the weenie and don't change).
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, name: "Headless01", wcid: 1,
            seqInstance: 5, seqPosition: 100,
            position: new ObjectPosition(0x12340099, 999f, 888f, 777f, 1, 0, 0, 0)));

        var staleSpatial = BuildObjectCreate(TestGuid, name: "Headless01", wcid: 1,
            seqInstance: 5, seqPosition: 50,
            position: new ObjectPosition(0x12340001, 1f, 2f, 3f, 1, 0, 0, 0));

        // Apply returns true (sequence advance occurred for some
        // counters, identity refreshed), but position must be unchanged.
        ws.Apply(staleSpatial);
        var snap = ws.TryGet(TestGuid)!;
        Assert.Equal((uint)0x12340099, snap.CellId);
        Assert.Equal(999f, snap.Position.X);
        Assert.Equal((ushort)100, snap.SeqPosition);   // high-water held
    }

    // ---- UpdatePosition gating ----

    [Fact]
    public void UpdatePosition_StalePositionSequence_Dropped()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 5, seqPosition: 10,
            position: new ObjectPosition(0x12340001, 100f, 0, 0, 1, 0, 0, 0)));

        // Same instance epoch, older position seq → drop.
        var stale = new UpdatePositionMessage(
            Guid: TestGuid, Flags: 0, CellId: 0x12340002,
            Position: new Vector3(999f, 0, 0),
            Rotation: Quaternion.Identity, Velocity: null, PlacementId: null,
            InstanceSequence: 5, PositionSequence: 8,
            TeleportSequence: 0, ForcePositionSequence: 0);

        Assert.False(ws.Apply(stale));
        var snap = ws.TryGet(TestGuid)!;
        Assert.Equal((uint)0x12340001, snap.CellId);
        Assert.Equal(100f, snap.Position.X);
    }

    [Fact]
    public void UpdatePosition_NewerInstance_AcceptedEvenWithLowerPositionSeq()
    {
        // New instance epoch implies the position-sequence counter
        // restarted on the server. Snapshot must accept the update.
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 5, seqPosition: 10,
            position: new ObjectPosition(0x12340001, 100f, 0, 0, 1, 0, 0, 0)));

        var newInstance = new UpdatePositionMessage(
            Guid: TestGuid, Flags: 0, CellId: 0x12340099,
            Position: new Vector3(999f, 0, 0),
            Rotation: Quaternion.Identity, Velocity: null, PlacementId: null,
            InstanceSequence: 6, PositionSequence: 1,
            TeleportSequence: 0, ForcePositionSequence: 0);

        Assert.True(ws.Apply(newInstance));
        var snap = ws.TryGet(TestGuid)!;
        Assert.Equal((uint)0x12340099, snap.CellId);
        Assert.Equal(999f, snap.Position.X);
        Assert.Equal((ushort)6, snap.SeqInstance);
        Assert.Equal((ushort)1, snap.SeqPosition);
    }

    [Fact]
    public void UpdatePosition_BeforeObjectCreate_CreatesPartialSnapshot()
    {
        var ws = new WorldState();
        var up = new UpdatePositionMessage(
            Guid: OtherGuid, Flags: 0, CellId: 0x12340001,
            Position: new Vector3(50f, 60f, 70f),
            Rotation: Quaternion.Identity, Velocity: null, PlacementId: null,
            InstanceSequence: 0, PositionSequence: 0,
            TeleportSequence: 0, ForcePositionSequence: 0);

        Assert.True(ws.Apply(up));
        var snap = ws.TryGet(OtherGuid);
        Assert.NotNull(snap);
        Assert.Equal(50f, snap!.Position.X);
        Assert.Null(snap.Name);                   // weenie not yet known
        Assert.Equal((uint)0x12340001, snap.CellId);
    }

    [Fact]
    public void UpdatePosition_StaleInstanceSequence_Dropped()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 5, seqPosition: 10,
            position: new ObjectPosition(0x12340001, 100f, 0, 0, 1, 0, 0, 0)));

        var stale = new UpdatePositionMessage(
            Guid: TestGuid, Flags: 0, CellId: 0x12340099,
            Position: new Vector3(999f, 0, 0),
            Rotation: Quaternion.Identity, Velocity: null, PlacementId: null,
            InstanceSequence: 3, PositionSequence: 999,
            TeleportSequence: 0, ForcePositionSequence: 0);

        Assert.False(ws.Apply(stale));
        var snap = ws.TryGet(TestGuid)!;
        Assert.Equal((uint)0x12340001, snap.CellId);
    }

    // ---- PrivateUpdatePropertyInt routing ----

    [Fact]
    public void PrivatePropertyInt_BeforeSelfGuid_Dropped()
    {
        var ws = new WorldState();
        var msg = new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25, Value: 42);
        Assert.False(ws.Apply(msg));
        Assert.Null(ws.Self);
    }

    [Fact]
    public void PrivatePropertyInt_AfterSetSelf_RoutesToSelf()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        var msg = new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25, Value: 42);
        Assert.True(ws.Apply(msg));
        Assert.Equal(42, ws.Self!.PropertyInts![25]);
    }

    [Fact]
    public void PrivatePropertyInt_StaleByteSequence_Dropped()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        // Advance the family-wide byte sequence to 100.
        Assert.True(ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 100, Property: 25, Value: 10)));
        Assert.Equal(10, ws.Self!.PropertyInts![25]);

        // A subsequent property update with byte-seq 50 is stale
        // (NOT a wrap — 50 - 100 = -50, mod 256 = 206 which is
        // beyond the 128 wrap window, so it's treated as backward).
        Assert.False(ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 50, Property: 25, Value: 999)));
        Assert.Equal(10, ws.Self.PropertyInts[25]);   // unchanged
    }

    [Fact]
    public void PrivatePropertyInt_ByteSequenceWrap_Accepted()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 255, Property: 25, Value: 1));
        // 0 after 255 is a forward wrap.
        Assert.True(ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 0, Property: 25, Value: 2)));
        Assert.Equal(2, ws.Self!.PropertyInts![25]);
    }

    // ---- PlayerCreate ----

    [Fact]
    public void PlayerCreate_SetsSelfGuid_AndConfirmsPreSeededValue()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);

        // PlayerCreate confirms — no warning, SelfGuid unchanged.
        Assert.True(ws.Apply(new PlayerCreateMessage(TestGuid)));
        Assert.Equal(TestGuid, ws.SelfGuid);
    }

    [Fact]
    public void PlayerCreate_WithoutPreSeed_StillSetsSelfGuid()
    {
        var ws = new WorldState();
        ws.Apply(new PlayerCreateMessage(TestGuid));
        Assert.Equal(TestGuid, ws.SelfGuid);
        Assert.NotNull(ws.Self);
    }

    // ---- Real-burst-order regression ----

    [Fact]
    public void RealBurstOrder_SetSelf_Then_PrivateProperty_Then_PlayerCreate_Then_ObjectCreate()
    {
        var ws = new WorldState();

        // 1) Driver pre-seeds SelfGuid at EnterWorld request time.
        ws.SetSelf(TestGuid);

        // 2) Server starts the initial property dump BEFORE
        //    PlayerCreate fires for our character.
        Assert.True(ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 1, Property: 25, Value: 50)));
        Assert.True(ws.Apply(new PrivateUpdatePropertyIntMessage(Sequence: 2, Property: 20, Value: 12345)));

        // 3) Server emits PlayerCreate.
        Assert.True(ws.Apply(new PlayerCreateMessage(TestGuid)));

        // 4) Then ObjectCreate for our character.
        ws.Apply(BuildObjectCreate(TestGuid, name: "Headless01", wcid: 1,
            seqInstance: 1, seqPosition: 1,
            position: new ObjectPosition(0x12340001, 100f, 200f, 50f, 1f, 0, 0, 0)));

        var snap = ws.Self!;
        Assert.Equal("Headless01", snap.Name);
        Assert.Equal(50, snap.PropertyInts![25]);         // preserved
        Assert.Equal(12345, snap.PropertyInts[20]);       // preserved
        Assert.Equal(100f, snap.Position.X);
    }

    // ---- Motion ----

    [Fact]
    public void Motion_NewerInstance_UpdatesMotionFields()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 5, seqPosition: 10));

        var motion = new MotionMessage(
            Guid: TestGuid, InstanceSequence: 6, MovementSequence: 1,
            ServerControlSequence: 1, IsAutonomous: false,
            MovementType: MovementType.MoveToPosition,
            MotionFlags: MotionFlags.StickToObject,
            CurrentStyle: 0x1234, BodyBytes: Array.Empty<byte>());

        Assert.True(ws.Apply(motion));
        var snap = ws.TryGet(TestGuid)!;
        Assert.Equal(MovementType.MoveToPosition, snap.LastMovementType);
        Assert.Equal(MotionFlags.StickToObject, snap.LastMotionFlags);
        Assert.Equal((ushort)0x1234, snap.LastMotionStyle);
    }

    // ---- SetState ----

    [Fact]
    public void SetState_NewerSequence_UpdatesPhysicsState()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 5, seqPosition: 10));

        var ss = new SetStateMessage(Guid: TestGuid, State: 0xDEADBEEF,
            InstanceSequence: 5, StateSequence: 1);

        Assert.True(ws.Apply(ss));
        Assert.Equal((uint)0xDEADBEEF, ws.TryGet(TestGuid)!.PhysicsState);
    }

    [Fact]
    public void SetState_StaleStateSeq_SameInstance_Dropped()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 5, seqPosition: 10));
        ws.Apply(new SetStateMessage(TestGuid, 0xAAAA, 5, 10));
        Assert.False(ws.Apply(new SetStateMessage(TestGuid, 0xBBBB, 5, 5)));
        Assert.Equal((uint)0xAAAA, ws.TryGet(TestGuid)!.PhysicsState);
    }

    // ---- Apply on unknown types ----

    [Fact]
    public void Apply_OnUnknownType_ReturnsFalseAndDoesNothing()
    {
        var ws = new WorldState();
        Assert.False(ws.Apply(new object()));
        Assert.False(ws.Apply(null));
        Assert.Equal(0, ws.ObjectCount);
    }

    // ---- Helpers ----

    private static ObjectCreateMessage BuildObjectCreate(
        uint guid,
        string name = "obj",
        uint wcid = 1,
        ushort seqInstance = 0,
        ushort seqPosition = 0,
        ObjectPosition? position = null)
    {
        var model = new ObjectModelData(
            PaletteId: null,
            SubPalettes: Array.Empty<SubPaletteEntry>(),
            TextureChanges: Array.Empty<TextureChangeEntry>(),
            AnimPartChanges: Array.Empty<AnimPartChangeEntry>());

        var physics = new ObjectPhysicsData(
            DescriptionFlags: position is null ? 0 : PhysicsDescriptionFlag.Position,
            PhysicsState: 0,
            MovementBody: null, MovementIsAutonomous: null, AnimationFramePlacement: null,
            Position: position,
            MotionTableId: null, SoundTableId: null, PhysicsTableId: null, SetupTableId: null,
            ParentWielderId: null, ParentLocation: null, Children: null,
            ObjScale: null, Friction: null, Elasticity: null, Translucency: null,
            Velocity: null, Acceleration: null, Omega: null,
            DefaultScriptId: null, DefaultScriptIntensity: null,
            SeqObjectPosition: seqPosition, SeqObjectMovement: 0, SeqObjectState: 0,
            SeqObjectVector: 0, SeqObjectTeleport: 0, SeqObjectServerControl: 0,
            SeqObjectForcePosition: 0, SeqObjectVisualDesc: 0, SeqObjectInstance: seqInstance);

        var weenie = new ObjectWeenieHeader(
            Flags: 0, Flags2: 0,
            Name: name, WeenieClassId: wcid, IconId: 0, ItemType: 0, DescriptionFlags: 0,
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
