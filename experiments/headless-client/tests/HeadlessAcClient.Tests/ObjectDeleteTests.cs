// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for ObjectDelete (0xF747) decoding and WorldState
// integration (stale-instance protection, unknown-guid noop,
// self-guid protection).

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class ObjectDeleteTests
{
    private const uint TestGuid = 0x50000005;

    private static byte[] BuildWire(uint guid, ushort instSeq)
    {
        var buf = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.ObjectDelete);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), guid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), instSeq);
        // bytes 10..11 are alignment padding — server leaves them as
        // whatever the underlying stream had (Writer.Align zero-fills,
        // but the decoder ignores them either way). Leave as 0 here.
        return buf;
    }

    [Fact]
    public void Decode_ParsesGuidAndInstanceSequence()
    {
        var wire = BuildWire(TestGuid, 0x1234);
        var msg = GameMessageDecoder.Decode(wire) as ObjectDeleteMessage;
        Assert.NotNull(msg);
        Assert.Equal(TestGuid, msg!.Guid);
        Assert.Equal((ushort)0x1234, msg.InstanceSequence);
    }

    [Fact]
    public void Decode_ShortPayload_ReturnsNull()
    {
        var wire = BuildWire(TestGuid, 0);
        var truncated = wire.AsSpan(0, 9).ToArray();
        Assert.Null(GameMessageDecoder.Decode(truncated));
    }

    [Fact]
    public void Decode_IgnoresAlignmentPadding()
    {
        var wire = BuildWire(TestGuid, 42);
        // Fill trailing pad with junk; decoder should still parse.
        wire[10] = 0xAB;
        wire[11] = 0xCD;
        var msg = GameMessageDecoder.Decode(wire) as ObjectDeleteMessage;
        Assert.NotNull(msg);
        Assert.Equal((ushort)42, msg!.InstanceSequence);
    }

    // ---- WorldState integration ----

    [Fact]
    public void Apply_RemovesKnownObject()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 5));
        Assert.Equal(1, ws.ObjectCount);

        Assert.True(ws.Apply(new ObjectDeleteMessage(TestGuid, 5)));
        Assert.Equal(0, ws.ObjectCount);
        Assert.Null(ws.TryGet(TestGuid));
    }

    [Fact]
    public void Apply_UnknownGuid_IsNoop()
    {
        var ws = new WorldState();
        Assert.False(ws.Apply(new ObjectDeleteMessage(0xDEADBEEF, 1)));
        Assert.Equal(0, ws.ObjectCount);
    }

    [Fact]
    public void Apply_StaleInstanceSequence_Dropped()
    {
        // ObjectCreate at instance=10. A late-arriving delete from
        // instance=5 (the previous epoch, before respawn) must NOT
        // wipe the live snapshot.
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 10));

        Assert.False(ws.Apply(new ObjectDeleteMessage(TestGuid, 5)));
        Assert.NotNull(ws.TryGet(TestGuid));
    }

    [Fact]
    public void Apply_EqualInstanceSequence_Removes()
    {
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 7));
        Assert.True(ws.Apply(new ObjectDeleteMessage(TestGuid, 7)));
        Assert.Null(ws.TryGet(TestGuid));
    }

    [Fact]
    public void Apply_NewerInstanceSequence_Removes()
    {
        // Server is signalling a delete from a newer epoch than we
        // know about. Accept: the alternative (drop) means leaving a
        // stale snapshot in memory forever.
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 3));
        Assert.True(ws.Apply(new ObjectDeleteMessage(TestGuid, 9)));
        Assert.Null(ws.TryGet(TestGuid));
    }

    [Fact]
    public void Apply_SelfGuid_RefusesToWipeSelf()
    {
        var ws = new WorldState();
        ws.SetSelf(TestGuid);
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 1));

        Assert.False(ws.Apply(new ObjectDeleteMessage(TestGuid, 1)));
        Assert.NotNull(ws.TryGet(TestGuid));
        Assert.Equal((uint)TestGuid, ws.SelfGuid);
    }

    [Fact]
    public void Apply_SequenceWrapsAround_RemovesUsingWrapAware()
    {
        // Current seq is near the top of the u16 space. A delete with
        // a wrap-around-zero instance is "newer" in the SequenceCompare
        // semantics and must remove cleanly.
        var ws = new WorldState();
        ws.Apply(BuildObjectCreate(TestGuid, seqInstance: 65530));
        Assert.True(ws.Apply(new ObjectDeleteMessage(TestGuid, 3))); // wrapped past 0
        Assert.Null(ws.TryGet(TestGuid));
    }

    // ---- Helpers ----
    // Minimal ObjectCreate builder. Duplicates the shape of the one
    // in WorldStateTests rather than refactor it into a shared helper
    // class — keeps this change scoped to the new test file.

    private static ObjectCreateMessage BuildObjectCreate(uint guid, ushort seqInstance)
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
            SeqObjectForcePosition: 0, SeqObjectVisualDesc: 0, SeqObjectInstance: seqInstance);

        var weenie = new ObjectWeenieHeader(
            Flags: 0, Flags2: 0,
            Name: "obj", WeenieClassId: 1, IconId: 0, ItemType: 0, DescriptionFlags: 0,
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
