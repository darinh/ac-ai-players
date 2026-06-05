// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for GameEventPayloadDecoder — decodes the body of each
// supported GameEvent (0xF7B0) subtype.

using System;
using System.Buffers.Binary;
using System.Text;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameEventPayloadDecoderTests
{
    [Fact]
    public void Decode_WeenieError_ReadsErrorCode()
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x12345678);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.WeenieError);

        Assert.NotNull(p?.WeenieError);
        Assert.Equal(0x12345678u, p!.WeenieError!.ErrorCode);
        Assert.Equal("WeenieError(0x12345678)", p.WeenieError.ToString());
    }

    [Fact]
    public void Decode_WeenieErrorWithString_ReadsCodeAndMessage()
    {
        // 4B err + 2B len + 6B "Hello!" → cursor at 12 (already 4-aligned, no pad).
        var msg = Encoding.UTF8.GetBytes("Hello!");
        var body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 0x0123);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)msg.Length);
        msg.CopyTo(body, 6);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.WeenieErrorWithString);

        Assert.NotNull(p?.WeenieErrorWithString);
        Assert.Equal(0x0123u,  p!.WeenieErrorWithString!.ErrorCode);
        Assert.Equal("Hello!", p.WeenieErrorWithString.Message);
    }

    [Fact]
    public void Decode_WeenieErrorWithString_PadsToFourBytes()
    {
        // 4B err + 2B len + 5B "Hello" + 1B pad → 12B total.
        var msg = Encoding.UTF8.GetBytes("Hello");
        var body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 0x0456);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)msg.Length);
        msg.CopyTo(body, 6);
        // body[11] is padding byte (0)

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.WeenieErrorWithString);
        Assert.NotNull(p?.WeenieErrorWithString);
        Assert.Equal("Hello", p!.WeenieErrorWithString!.Message);
    }

    [Fact]
    public void Decode_CharacterTitle_ZeroTitles()
    {
        // u32 unused=1 + u32 currentTitle + u32 count=0
        var body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8, 4), 0);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.CharacterTitle);
        Assert.NotNull(p?.CharacterTitle);
        Assert.Equal(42u, p!.CharacterTitle!.CurrentTitleId);
        Assert.Empty(p.CharacterTitle.TitleIds);
    }

    [Fact]
    public void Decode_CharacterTitle_OneTitle()
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0,  4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4,  4), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8,  4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12, 4), 100);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.CharacterTitle);
        Assert.NotNull(p?.CharacterTitle);
        Assert.Equal(7u, p!.CharacterTitle!.CurrentTitleId);
        Assert.Single(p.CharacterTitle.TitleIds);
        Assert.Equal(100u, p.CharacterTitle.TitleIds[0]);
    }

    [Fact]
    public void Decode_FriendsListUpdate_EmptyList()
    {
        // u32 friendCount=0 + u32 updateType=0 → 8B
        var body = new byte[8];
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FriendsListUpdate);

        Assert.NotNull(p?.FriendsListUpdate);
        Assert.Empty(p!.FriendsListUpdate!.Friends);
        Assert.Equal(0u, p.FriendsListUpdate.UpdateType);
    }

    [Fact]
    public void Decode_FriendsListUpdate_OneFriend()
    {
        // count=1 + (fid=0xAAAA + online=1 + offline=0 + name "ab" string16L:
        //                      2B len + 2B str + 0B pad = 4
        //              + friendsOfFriendCount=0 + inverseFriendsCount=0)
        //         + updateType=1
        var name = Encoding.UTF8.GetBytes("ab");
        var w = new System.Collections.Generic.List<byte>();
        void U32(uint v) {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            foreach (var x in b) w.Add(x);
        }
        void U16(ushort v) {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            foreach (var x in b) w.Add(x);
        }
        U32(1);              // friend count
        U32(0xAAAAu);        // friendId
        U32(1);              // isOnline
        U32(0);              // appearOffline
        U16((ushort)name.Length);
        foreach (var b in name) w.Add(b);
        // cursor at 4+4+4+4+2+2 = 20 → 20%4=0 → no pad
        U32(0);              // friendsOfFriendCount
        U32(0);              // inverseFriendsCount
        U32(1);              // updateType

        var body = w.ToArray();
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FriendsListUpdate);
        Assert.NotNull(p?.FriendsListUpdate);
        var fl = p!.FriendsListUpdate!;
        Assert.Single(fl.Friends);
        Assert.Equal(0xAAAAu, fl.Friends[0].FriendId);
        Assert.True(fl.Friends[0].IsOnline);
        Assert.False(fl.Friends[0].AppearOffline);
        Assert.Equal("ab", fl.Friends[0].FriendName);
        Assert.Equal(1u, fl.UpdateType);
    }

    [Fact]
    public void Decode_SetTurbineChatChannels_ReadsAllTenU32s()
    {
        var body = new byte[40];
        for (var i = 0; i < 10; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i * 4, 4), (uint)(0x1000 + i));

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.SetTurbineChatChannels);
        Assert.NotNull(p?.SetTurbineChatChannels);
        var c = p!.SetTurbineChatChannels!;
        Assert.Equal(0x1000u, c.Allegiance);
        Assert.Equal(0x1001u, c.General);
        Assert.Equal(0x1002u, c.Trade);
        Assert.Equal(0x1003u, c.Lfg);
        Assert.Equal(0x1004u, c.Roleplay);
        Assert.Equal(0x1005u, c.Olthoi);
        Assert.Equal(0x1006u, c.Society);
        Assert.Equal(0x1007u, c.SocietyCelestialHand);
        Assert.Equal(0x1008u, c.SocietyEldrytchWeb);
        Assert.Equal(0x1009u, c.SocietyRadiantBlood);
    }

    [Fact]
    public void Decode_UnknownEventType_ReturnsNull()
    {
        var body = new byte[16];
        // 0x9999 is not in our supported list.
        var p = GameEventPayloadDecoder.Decode(body, (GameEventType)0x9999);
        Assert.Null(p);
    }

    [Fact]
    public void Decode_TooShortBody_ReturnsNull_NoThrow()
    {
        var p1 = GameEventPayloadDecoder.Decode(new byte[2], GameEventType.WeenieError);
        var p2 = GameEventPayloadDecoder.Decode(new byte[5], GameEventType.WeenieErrorWithString);
        var p3 = GameEventPayloadDecoder.Decode(new byte[8], GameEventType.CharacterTitle);
        var p4 = GameEventPayloadDecoder.Decode(new byte[4], GameEventType.FriendsListUpdate);
        var p5 = GameEventPayloadDecoder.Decode(new byte[20], GameEventType.SetTurbineChatChannels);
        Assert.Null(p1);
        Assert.Null(p2);
        Assert.Null(p3);
        Assert.Null(p4);
        Assert.Null(p5);
    }

    [Fact]
    public void Decode_FriendsListUpdate_AbsurdCount_ReturnsNull()
    {
        // Set count to 2 billion — should reject, not allocate.
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 2_000_000_000);
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FriendsListUpdate);
        Assert.Null(p);
    }

    [Fact]
    public void Decode_EvasionAttackerNotification_ReadsDefenderName()
    {
        // string16L "Drudge Skulker" (14B) → 2B len + 14B = 16, already
        // 4-aligned only if 16%4==0 (yes), no pad.
        var name = Encoding.UTF8.GetBytes("Drudge Skulker");
        var body = new byte[2 + name.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.EvasionAttackerNotification);

        Assert.NotNull(p?.EvasionAttackerNotification);
        Assert.Equal("Drudge Skulker", p!.EvasionAttackerNotification!.DefenderName);
    }

    [Fact]
    public void Decode_EvasionAttackerNotification_PadsNameToFourBytes()
    {
        // "Cow" = 3B → 2B len + 3B = 5, pad 3 → cursor 8. Decoder must
        // not throw on the trailing padding bytes.
        var name = Encoding.UTF8.GetBytes("Cow");
        var body = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.EvasionAttackerNotification);

        Assert.NotNull(p?.EvasionAttackerNotification);
        Assert.Equal("Cow", p!.EvasionAttackerNotification!.DefenderName);
    }

    [Fact]
    public void Decode_AttackerNotification_ReadsNameAndDamage()
    {
        // string16L "Chicken" (7B) → 2B len + 7B + 3B pad = 12 (4-aligned),
        // then u32 damageType, f64 percent, u32 damage.
        var name = Encoding.UTF8.GetBytes("Chicken");
        var body = new byte[12 + 4 + 8 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);
        // bytes [9..11] are name padding (zero)
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12, 4), 0x4); // damageType
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(16, 8), 0.25); // percent
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24, 4), 17);   // damage

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.AttackerNotification);

        Assert.NotNull(p?.AttackerNotification);
        Assert.Equal("Chicken", p!.AttackerNotification!.DefenderName);
        Assert.Equal(17u, p.AttackerNotification.Damage);
    }

    [Fact]
    public void Decode_AttackerNotification_TooShort_ReturnsNull()
    {
        // Name present but truncated before the damage fields → decoder
        // throws internally and Decode returns null (graceful fallback).
        var name = Encoding.UTF8.GetBytes("Cow");
        var body = new byte[8]; // just the padded name, no damage fields
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.AttackerNotification);

        Assert.Null(p);
    }
}
