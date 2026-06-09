// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for InventoryRemoveObject (0x0024) decoding and WorldState
// integration (known-guid removal, unknown-guid noop, self-guid
// protection). cp-2388: the server sends this when an item leaves the
// player inventory (give/drop/use-consume); the client previously had
// no decoder, leaving a phantom inventory item that broke later gives.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class InventoryRemoveObjectTests
{
    private const uint TestGuid = 0x80008861;

    private static byte[] BuildWire(uint guid)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.InventoryRemoveObject);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), guid);
        return buf;
    }

    [Fact]
    public void Decode_ParsesGuid()
    {
        var msg = GameMessageDecoder.Decode(BuildWire(TestGuid)) as InventoryRemoveObjectMessage;
        Assert.NotNull(msg);
        Assert.Equal(TestGuid, msg!.Guid);
    }

    [Fact]
    public void Decode_ShortPayload_ReturnsNull()
    {
        var truncated = BuildWire(TestGuid).AsSpan(0, 7).ToArray();
        Assert.Null(GameMessageDecoder.Decode(truncated));
    }

    [Fact]
    public void Apply_RemovesKnownInventoryItem()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid));
        Assert.Equal(1, ws.ObjectCount);

        Assert.True(ws.Apply(new InventoryRemoveObjectMessage(TestGuid)));
        Assert.Equal(0, ws.ObjectCount);
        Assert.Null(ws.TryGet(TestGuid));
    }

    [Fact]
    public void Apply_UnknownGuid_IsNoop()
    {
        var ws = new WorldState();
        Assert.False(ws.Apply(new InventoryRemoveObjectMessage(0xDEADBEEF)));
        Assert.Equal(0, ws.ObjectCount);
    }

    [Fact]
    public void Apply_SelfGuid_RefusesToWipeSelf()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(BuildObjectCreate(TestGuid));

        Assert.False(ws.Apply(new InventoryRemoveObjectMessage(TestGuid)));
        Assert.NotNull(ws.TryGet(TestGuid));
        Assert.Equal((uint)TestGuid, ws.SelfGuid);
    }

    [Fact]
    public void Decode_RealWireBytes_FromAcademyUseRemoval()
    {
        // The exact 8 bytes observed live after a Calling Stone USE:
        // 24 00 00 00 (opcode) 61 88 00 80 (guid 0x80008861).
        var wire = new byte[] { 0x24, 0x00, 0x00, 0x00, 0x61, 0x88, 0x00, 0x80 };
        var msg = GameMessageDecoder.Decode(wire) as InventoryRemoveObjectMessage;
        Assert.NotNull(msg);
        Assert.Equal(0x80008861u, msg!.Guid);
    }

    private static ObjectCreateMessage BuildObjectCreate(uint guid)
    {
        var model = new ObjectModelData(
            PaletteId: null,
            SubPalettes: Array.Empty<SubPaletteEntry>(),
            TextureChanges: Array.Empty<TextureChangeEntry>(),
            AnimPartChanges: Array.Empty<AnimPartChangeEntry>());

        var physics = new ObjectPhysicsData(
            DescriptionFlags: 0,
            PhysicsState: 0,
            MovementBody: null, MovementIsAutonomous: null, AnimationFramePlacement: null,
            Position: null,
            MotionTableId: null, SoundTableId: null, PhysicsTableId: null, SetupTableId: null,
            ParentWielderId: null, ParentLocation: null, Children: null,
            ObjScale: null, Friction: null, Elasticity: null, Translucency: null,
            Velocity: null, Acceleration: null, Omega: null,
            DefaultScriptId: null, DefaultScriptIntensity: null,
            SeqObjectPosition: 0, SeqObjectMovement: 0, SeqObjectState: 0,
            SeqObjectVector: 0, SeqObjectTeleport: 0, SeqObjectServerControl: 0,
            SeqObjectForcePosition: 0, SeqObjectVisualDesc: 0, SeqObjectInstance: 0);

        var weenie = new ObjectWeenieHeader(
            Flags: 0, Flags2: 0,
            Name: "Calling Stone", WeenieClassId: 5084, IconId: 0, ItemType: 0, DescriptionFlags: 0,
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
