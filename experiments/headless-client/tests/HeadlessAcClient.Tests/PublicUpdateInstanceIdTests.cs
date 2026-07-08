// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for PublicUpdateInstanceId (0x02DA) - the guid-valued property-update
// stream that carries a named object's InstanceId property change. The bot
// consumes the allegiance Monarch property (26) so its self allegiance state
// stays LIVE after a swear/break without waiting for a fresh ObjectCreate (the
// staleness window the projection otherwise had). Wire: u32 opcode | u8 seq |
// u32 objectGuid | u32 property | u32 value; value 0 = property removed.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class PublicUpdateInstanceIdTests
{
    private const uint Self = 0x5000005Cu;
    private const uint Monarch = 26u;   // PropertyInstanceId.Monarch
    private const uint Patron  = 25u;   // PropertyInstanceId.Patron

    private static byte[] BuildWire(byte sequence, uint objectGuid, uint property, uint value)
    {
        var buf = new byte[PublicUpdateInstanceIdMessage.PackedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)GameMessageOpcode.PublicUpdateInstanceId);
        buf[4] = sequence;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5), objectGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(9), property);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(13), value);
        return buf;
    }

    private static WorldState SelfWorld()
    {
        var ws = new WorldState();
        ws.SetSelf(Self);
        return ws;
    }

    // ---- byte-level decode ----

    [Fact]
    public void Decode_ParsesAllFields()
    {
        var wire = BuildWire(sequence: 7, objectGuid: Self, property: Monarch, value: 0x50000099u);
        var msg = GameMessageDecoder.Decode(wire) as PublicUpdateInstanceIdMessage;
        Assert.NotNull(msg);
        Assert.Equal((byte)7, msg!.Sequence);
        Assert.Equal(Self, msg.ObjectGuid);
        Assert.Equal(Monarch, msg.Property);
        Assert.Equal(0x50000099u, msg.Value);
        Assert.Equal("Monarch", msg.PropertyName);
    }

    [Fact]
    public void Decode_PackedSizeIs17()
    {
        Assert.Equal(17, PublicUpdateInstanceIdMessage.PackedSize);
    }

    [Fact]
    public void Decode_ShortPayload_ReturnsNull()
    {
        var wire = BuildWire(1, Self, Monarch, 1);
        var truncated = wire.AsSpan(0, PublicUpdateInstanceIdMessage.PackedSize - 1).ToArray();
        Assert.Null(GameMessageDecoder.Decode(truncated));
    }

    // ---- apply: Monarch -> MonarchGuid ----

    [Fact]
    public void ApplyMonarch_SetsMonarchGuid_OnTargetSnapshot()
    {
        var ws = SelfWorld();
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildWire(sequence: 1, objectGuid: Self, property: Monarch, value: 0x50000099u))!));
        Assert.Equal(0x50000099u, ws.TryGet(Self)!.MonarchGuid);
    }

    [Fact]
    public void ApplyMonarch_ValueZero_ClearsMonarchGuid()
    {
        var ws = SelfWorld();
        // Swear (monarch set), then break (monarch removed -> value 0).
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildWire(1, Self, Monarch, 0x50000099u))!));
        Assert.Equal(0x50000099u, ws.TryGet(Self)!.MonarchGuid);
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildWire(2, Self, Monarch, 0u))!));
        Assert.Null(ws.TryGet(Self)!.MonarchGuid);
    }

    [Fact]
    public void ApplyMonarch_StaleSequence_Dropped()
    {
        var ws = SelfWorld();
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildWire(sequence: 5, objectGuid: Self, property: Monarch, value: 0x50000099u))!));
        // A lower sequence for the same (guid, property) is a stale/reordered
        // resend -> dropped, MonarchGuid unchanged.
        Assert.False(ws.Apply(GameMessageDecoder.Decode(
            BuildWire(sequence: 3, objectGuid: Self, property: Monarch, value: 0x5000AAAAu))!));
        Assert.Equal(0x50000099u, ws.TryGet(Self)!.MonarchGuid);
    }

    [Fact]
    public void ApplyMonarch_OtherObject_DoesNotTouchSelf()
    {
        var ws = SelfWorld();
        // A DIFFERENT player's allegiance change lands on THAT object's snapshot,
        // not self's.
        Assert.True(ws.Apply(GameMessageDecoder.Decode(
            BuildWire(1, objectGuid: 0x500000A1u, property: Monarch, value: 0x5000BBBBu))!));
        Assert.Equal(0x5000BBBBu, ws.TryGet(0x500000A1u)!.MonarchGuid);
        Assert.Null(ws.TryGet(Self)?.MonarchGuid);
    }

    [Fact]
    public void ApplyNonMonarchProperty_DoesNotSetMonarchGuid()
    {
        var ws = SelfWorld();
        // Patron (25) is decoded and sequence-tracked but not consumed today; it
        // must NOT land on MonarchGuid.
        Assert.False(ws.Apply(GameMessageDecoder.Decode(
            BuildWire(1, Self, Patron, 0x50000099u))!));
        Assert.Null(ws.TryGet(Self)?.MonarchGuid);
    }
}
