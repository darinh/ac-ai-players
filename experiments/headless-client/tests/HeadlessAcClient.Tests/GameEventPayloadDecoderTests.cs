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

    [Fact]
    public void Decode_DefenderNotification_ReadsAttackerNameAndDamage_FullBody()
    {
        // GameEventDefenderNotification (0x01B2): string16L attackerName,
        // u32 damageType, f64 percent, u32 damage, u32 damageLocation,
        // u32 criticalHit, u64 attackConditions, Align. "Drudge Skulker"
        // = 14B → 2+14 = 16 (already 4-aligned, no pad). Use the FULL
        // realistic body to prove the decoder reads the damage at the
        // correct offset and ignores the trailing fields.
        var name = Encoding.UTF8.GetBytes("Drudge Skulker");
        var body = new byte[16 + 4 + 8 + 4 + 4 + 4 + 8]; // = 48
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16, 4), 0x4); // damageType
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(20, 8), 0.15); // percent
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28, 4), 9);    // damage
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32, 4), 1);    // damageLocation
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(36, 4), 0);    // criticalHit
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(40, 8), 0);    // attackConditions

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.DefenderNotification);

        Assert.NotNull(p?.DefenderNotification);
        Assert.Equal("Drudge Skulker", p!.DefenderNotification!.AttackerName);
        Assert.Equal(9u, p.DefenderNotification.Damage);
    }

    [Fact]
    public void Decode_DefenderNotification_TooShort_ReturnsNull()
    {
        // Name present but truncated before the damage fields → decoder
        // throws internally and Decode returns null (graceful fallback).
        var name = Encoding.UTF8.GetBytes("Cow");
        var body = new byte[8]; // just the padded name, no damage fields
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.DefenderNotification);

        Assert.Null(p);
    }

    [Fact]
    public void Decode_EvasionDefenderNotification_ReadsAttackerName()
    {
        // GameEventEvasionDefenderNotification (0x01B4): a single string16L
        // with the attacker's name. "Drudge Skulker" = 14B → 16, no pad.
        var name = Encoding.UTF8.GetBytes("Drudge Skulker");
        var body = new byte[2 + name.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.EvasionDefenderNotification);

        Assert.NotNull(p?.EvasionDefenderNotification);
        Assert.Equal("Drudge Skulker", p!.EvasionDefenderNotification!.AttackerName);
    }

    [Fact]
    public void Decode_EvasionDefenderNotification_PadsNameToFourBytes()
    {
        // "Cow" = 3B → 2B len + 3B = 5, pad 3 → cursor 8. Decoder must
        // tolerate the trailing padding bytes after the string.
        var name = Encoding.UTF8.GetBytes("Cow");
        var body = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
        name.CopyTo(body, 2);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.EvasionDefenderNotification);

        Assert.NotNull(p?.EvasionDefenderNotification);
        Assert.Equal("Cow", p!.EvasionDefenderNotification!.AttackerName);
    }

    [Fact]
    public void Decode_FellowshipUpdateFellow_ReadsAllFields()
    {
        // 11-u32 prefix (44B): guid, cpCached(0), lumCached(0), level,
        // hMax, sMax, mMax, hCur, sCur, mCur, shareLoot. Then string16L
        // name "ab" (2B len + 2B str, already 4-aligned), then u32 updateType.
        var name = Encoding.UTF8.GetBytes("ab");
        var body = new byte[44 + 4 + 4];
        var s = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0, 4),  0xB0B0u); // guid
        // [4..8] cpCached = 0, [8..12] lumCached = 0
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12, 4), 7u);      // level
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16, 4), 50u);     // healthMax
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(20, 4), 40u);     // staminaMax
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24, 4), 30u);     // manaMax
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28, 4), 45u);     // healthCur
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(32, 4), 35u);     // staminaCur
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(36, 4), 25u);     // manaCur
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(40, 4), 2u);      // shareLoot (bool << 1)
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(44, 2), (ushort)name.Length);
        name.CopyTo(body, 46);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(48, 4), 1u);      // updateType

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FellowshipUpdateFellow);

        Assert.NotNull(p?.FellowshipUpdateFellow);
        var f = p!.FellowshipUpdateFellow!;
        Assert.Equal(0xB0B0u, f.Guid);
        Assert.Equal(7u, f.Level);
        Assert.Equal(50u, f.HealthMax);
        Assert.Equal(40u, f.StaminaMax);
        Assert.Equal(30u, f.ManaMax);
        Assert.Equal(45u, f.HealthCurrent);
        Assert.Equal(35u, f.StaminaCurrent);
        Assert.Equal(25u, f.ManaCurrent);
        Assert.Equal(2u, f.ShareLootFlags);
        Assert.Equal("ab", f.Name);
        Assert.Equal(1u, f.UpdateType);
    }

    [Fact]
    public void Decode_FellowshipUpdateFellow_PadsNameToFourBytes()
    {
        // name "Cow" (3B): after the 44B prefix, 2B len + 3B str → cursor 49,
        // pad 3 → cursor 52, then u32 updateType. The decoder must skip the
        // padding and read updateType at the 4-aligned offset (the critical
        // alignment case — a wrong pad calc would read updateType as garbage).
        var name = Encoding.UTF8.GetBytes("Cow");
        var body = new byte[44 + 2 + 3 + 3 + 4]; // prefix + len + str + pad + updateType = 56
        var s = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0, 4),  0x00C0FFEEu); // guid
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12, 4), 11u);         // level
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(44, 2), (ushort)name.Length);
        name.CopyTo(body, 46);
        // bytes [49..52) are name padding (zero)
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(52, 4), 9u);          // updateType

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FellowshipUpdateFellow);

        Assert.NotNull(p?.FellowshipUpdateFellow);
        Assert.Equal("Cow", p!.FellowshipUpdateFellow!.Name);
        Assert.Equal(9u, p.FellowshipUpdateFellow.UpdateType);
        Assert.Equal(11u, p.FellowshipUpdateFellow.Level);
    }

    [Fact]
    public void Decode_FellowshipUpdateFellow_TooShort_ReturnsNull()
    {
        // 10 bytes — far below the 11-u32 fixed prefix. The guard throws
        // internally and Decode returns null (graceful fallback to raw bytes).
        var body = new byte[10];
        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FellowshipUpdateFellow);
        Assert.Null(p);
    }

    // ---- FellowshipFullUpdate (0x02BE) ----

    // Shared byte-builder for the FellowshipFullUpdate wire layout. w.Count is
    // body-relative (the real 16B GameEvent envelope is 4-aligned, so the
    // string padding matches). Mirrors PackableHashTable.WriteHeader (u16 count
    // + u16 numBuckets) + WriteFellow + name + leader + 4 bool-as-u32 flags.
    private static byte[] BuildFullUpdate(
        (uint guid, uint level, uint hMax, uint sMax, uint mMax, uint hCur, uint sCur, uint mCur, string name)[] fellows,
        string fellowshipName, uint leaderGuid, uint shareXp, uint evenShare, uint open, uint isLocked,
        byte[]? trailing = null)
    {
        var w = new System.Collections.Generic.List<byte>();
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void U32(uint v)   { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void Str16(string s) { var nb = Encoding.Latin1.GetBytes(s); U16((ushort)nb.Length); foreach (var x in nb) w.Add(x); while (w.Count % 4 != 0) w.Add(0); }
        U16((ushort)fellows.Length); U16(16); // PHT header: count + numBuckets
        foreach (var f in fellows)
        {
            U32(f.guid); U32(0); U32(0); U32(f.level);
            U32(f.hMax); U32(f.sMax); U32(f.mMax);
            U32(f.hCur); U32(f.sCur); U32(f.mCur);
            U32(0x10); // shareLoot stub
            Str16(f.name);
        }
        Str16(fellowshipName);
        U32(leaderGuid); U32(shareXp); U32(evenShare); U32(open); U32(isLocked);
        if (trailing != null) w.AddRange(trailing);
        return w.ToArray();
    }

    [Fact]
    public void Decode_FellowshipFullUpdate_TwoMembers()
    {
        var body = BuildFullUpdate(
            new[]
            {
                (0x1111u, 10u, 100u, 90u, 80u, 95u, 85u, 75u, "Al"),
                (0x2222u, 20u, 200u, 190u, 180u, 195u, 185u, 175u, "Bo"),
            },
            fellowshipName: "Crew", leaderGuid: 0x1111u,
            shareXp: 1, evenShare: 0, open: 1, isLocked: 0);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FellowshipFullUpdate);

        Assert.NotNull(p?.FellowshipFullUpdate);
        var f = p!.FellowshipFullUpdate!;
        Assert.Equal(2, f.Members.Count);
        Assert.Equal(0x1111u, f.Members[0].Guid);
        Assert.Equal(10u, f.Members[0].Level);
        Assert.Equal(100u, f.Members[0].HealthMax);
        Assert.Equal(95u, f.Members[0].HealthCurrent);
        Assert.Equal("Al", f.Members[0].Name);
        Assert.Equal(0x2222u, f.Members[1].Guid);
        Assert.Equal(20u, f.Members[1].Level);
        Assert.Equal(175u, f.Members[1].ManaCurrent);
        Assert.Equal("Bo", f.Members[1].Name);
        Assert.Equal("Crew", f.FellowshipName);
        Assert.Equal(0x1111u, f.LeaderGuid);
        Assert.True(f.ShareXp);
        Assert.False(f.EvenShare);
        Assert.True(f.Open);
        Assert.False(f.IsLocked);
    }

    [Fact]
    public void Decode_FellowshipFullUpdate_EmptyFellowship()
    {
        var body = BuildFullUpdate(
            Array.Empty<(uint, uint, uint, uint, uint, uint, uint, uint, string)>(),
            fellowshipName: "", leaderGuid: 0u,
            shareXp: 0, evenShare: 0, open: 0, isLocked: 0);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FellowshipFullUpdate);

        Assert.NotNull(p?.FellowshipFullUpdate);
        Assert.Empty(p!.FellowshipFullUpdate!.Members);
        Assert.Equal("", p.FellowshipFullUpdate.FellowshipName);
        Assert.False(p.FellowshipFullUpdate.IsLocked);
    }

    [Fact]
    public void Decode_FellowshipFullUpdate_IgnoresTrailingTables()
    {
        // Append DepartedMembers (1 entry: u16 count + u16 buckets + u32 guid +
        // i32 value) and an empty FellowshipLocks PHT after the flags. The
        // decoder must read members/name/leader/flags and leave these unread.
        var trailing = new System.Collections.Generic.List<byte>();
        void T16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); foreach (var x in b) trailing.Add(x); }
        void T32(uint v)   { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); foreach (var x in b) trailing.Add(x); }
        T16(1); T16(8); T32(0x9999); T32(0x7B); // DepartedMembers: 1 entry
        T16(0); T16(8);                          // FellowshipLocks: empty

        var body = BuildFullUpdate(
            new[] { (0x3333u, 5u, 50u, 40u, 30u, 45u, 35u, 25u, "X") },
            fellowshipName: "T", leaderGuid: 0x3333u,
            shareXp: 1, evenShare: 1, open: 0, isLocked: 1,
            trailing: trailing.ToArray());

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FellowshipFullUpdate);

        Assert.NotNull(p?.FellowshipFullUpdate);
        var f = p!.FellowshipFullUpdate!;
        Assert.Single(f.Members);
        Assert.Equal(0x3333u, f.Members[0].Guid);
        Assert.Equal("X", f.Members[0].Name);
        Assert.Equal("T", f.FellowshipName);
        Assert.Equal(0x3333u, f.LeaderGuid);
        Assert.True(f.IsLocked);
        Assert.False(f.Open);
    }

    [Fact]
    public void Decode_FellowshipFullUpdate_DecodesLatin1Name()
    {
        // The server writes string16L with CP1252 ('é' = single byte 0xE9),
        // not UTF-8 (which would be two bytes 0xC3 0xA9). The decoder must
        // read it as Latin-1/CP1252 so the name round-trips, not as UTF-8
        // (which would mangle 0xE9 into the replacement char). Locks the
        // shared ReadString16L codec fix.
        var body = BuildFullUpdate(
            new[] { (0x4444u, 3u, 30u, 20u, 10u, 30u, 20u, 10u, "Café") },
            fellowshipName: "Naïve", leaderGuid: 0x4444u,
            shareXp: 0, evenShare: 0, open: 0, isLocked: 0);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.FellowshipFullUpdate);

        Assert.NotNull(p?.FellowshipFullUpdate);
        Assert.Equal("Café", p!.FellowshipFullUpdate!.Members[0].Name);
        Assert.Equal("Naïve", p.FellowshipFullUpdate.FellowshipName);
    }

    // ---- FellowshipQuit (0x00A3) / FellowshipDismiss (0x00A4) / FellowshipDisband (0x02BF) ----

    private static byte[] U32Body(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    [Fact]
    public void Decode_FellowshipQuit_ReadsDepartedGuid()
    {
        // Server writes a single u32 — the guid of the player who quit.
        var p = GameEventPayloadDecoder.Decode(U32Body(0xABCD1234u), GameEventType.FellowshipQuit);
        Assert.NotNull(p?.FellowshipQuit);
        Assert.Equal(0xABCD1234u, p!.FellowshipQuit!.DepartedGuid);
        Assert.Null(p.FellowshipFullUpdate);
        Assert.Null(p.FellowshipDismiss);
    }

    [Fact]
    public void Decode_FellowshipDismiss_ReadsDismissedGuid()
    {
        // Server writes a single u32 — the guid of the dismissed player.
        var p = GameEventPayloadDecoder.Decode(U32Body(0x00112233u), GameEventType.FellowshipDismiss);
        Assert.NotNull(p?.FellowshipDismiss);
        Assert.Equal(0x00112233u, p!.FellowshipDismiss!.DismissedGuid);
        Assert.Null(p.FellowshipFullUpdate);
        Assert.Null(p.FellowshipQuit);
    }

    [Fact]
    public void Decode_FellowshipDisband_EmptyPayloadCarriesEventType()
    {
        // Disband has no body; the EventType alone signals the dissolution. The
        // decoder returns a non-null payload with no fellowship sub-record set.
        var p = GameEventPayloadDecoder.Decode(Array.Empty<byte>(), GameEventType.FellowshipDisband);
        Assert.NotNull(p);
        Assert.Equal(GameEventType.FellowshipDisband, p!.EventType);
        Assert.Null(p.FellowshipFullUpdate);
        Assert.Null(p.FellowshipQuit);
        Assert.Null(p.FellowshipDismiss);
    }

    [Fact]
    public void Decode_FellowshipQuit_ShortBody_ReturnsNull()
    {
        // A truncated quit body (< 4 bytes) is rejected by the decoder guard;
        // the outer catch returns null so the caller falls back to PayloadBytes.
        var p = GameEventPayloadDecoder.Decode(new byte[] { 0x01, 0x02 }, GameEventType.FellowshipQuit);
        Assert.Null(p);
    }

    // ---- ApproachVendor / VendorInfoEvent (0x0062) ----

    private static byte[] BuildApproachVendor(
        uint vendorGuid, uint merchandiseItemTypes, uint minValue, uint maxValue,
        uint dealMagical, float buyPrice, float sellPrice,
        uint altCurrency, uint altCurrencyAmount, string altCurrencyName,
        int numItems,
        (uint guid, string name, uint wcid, uint itemType, uint? value, int stackSize)[]? items = null,
        byte[]? trailingItems = null)
    {
        var w = new System.Collections.Generic.List<byte>();
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void U32(uint v)   { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void I32(int v)    { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void F32(float v)  { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void Str16(string s) { var nb = Encoding.Latin1.GetBytes(s); U16((ushort)nb.Length); foreach (var x in nb) w.Add(x); while (w.Count % 4 != 0) w.Add(0); }
        void Align() { while (w.Count % 4 != 0) w.Add(0); }
        // AC packed dword: a single u16 for values < 0x8000.
        void PackedDword(uint v) { if (v < 0x8000) U16((ushort)v); else { U16((ushort)(0x8000 | (v >> 16))); U16((ushort)(v & 0xFFFF)); } }
        U32(vendorGuid); U32(merchandiseItemTypes); U32(minValue); U32(maxValue);
        U32(dealMagical); F32(buyPrice); F32(sellPrice);
        U32(altCurrency); U32(altCurrencyAmount); Str16(altCurrencyName);
        I32(numItems);
        if (items != null)
        {
            // Mirror GameEventApproachVendor.cs: per item a packed u32
            // (stackSize low 24 | pwdType high 8) then SerializeGameDataOnly =
            // guid + weenie header (gamedataonly). Minimal header: flags (Value
            // bit only), name, wcid, icon=0, itemType, objDescFlags=0, Align,
            // [value], Align. Alignment is body-relative (matches the decoder).
            foreach (var it in items)
            {
                U32((uint)((it.stackSize & 0xFFFFFF) | (-1 << 24)));
                U32(it.guid);
                uint flags = it.value.HasValue ? (uint)WeenieHeaderFlag.Value : 0u;
                U32(flags);
                Str16(it.name);
                PackedDword(it.wcid);
                PackedDword(0u);          // iconId 0
                U32(it.itemType);
                U32(0u);                  // objDescFlags (no second header)
                Align();
                if (it.value.HasValue) U32(it.value.Value);
                Align();
            }
        }
        if (trailingItems != null) w.AddRange(trailingItems);
        return w.ToArray();
    }

    [Fact]
    public void Decode_ApproachVendor_ReadsTradeTermsAndItemCount()
    {
        var body = BuildApproachVendor(
            vendorGuid: 0x7A9B46A9u, merchandiseItemTypes: 0x10u, minValue: 1u, maxValue: 999u,
            dealMagical: 1u, buyPrice: 1.15f, sellPrice: 0.75f,
            altCurrency: 0u, altCurrencyAmount: 0u, altCurrencyName: "",
            numItems: 5);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.ApproachVendor);

        Assert.NotNull(p?.VendorInfo);
        var v = p!.VendorInfo!;
        Assert.Equal(0x7A9B46A9u, v.VendorGuid);
        Assert.Equal(0x10u, v.MerchandiseItemTypes);
        Assert.Equal(1u, v.MerchandiseMinValue);
        Assert.Equal(999u, v.MerchandiseMaxValue);
        Assert.True(v.DealMagicalItems);
        Assert.Equal(1.15f, v.BuyPrice);
        Assert.Equal(0.75f, v.SellPrice);
        Assert.Equal(0u, v.AlternateCurrency);
        Assert.Equal("", v.AlternateCurrencyName);
        Assert.Equal(5, v.ItemCount);
        // The decode-only primitive does not touch the other variants.
        Assert.Null(p.FellowshipFullUpdate);
    }

    [Fact]
    public void Decode_ApproachVendor_ReadsAlternateCurrencyBlock()
    {
        // With an alternate currency the server writes the player's alt-currency
        // amount and the currency's plural name; the string16L padding must keep
        // the trailing numItems word aligned.
        var body = BuildApproachVendor(
            vendorGuid: 0x12345678u, merchandiseItemTypes: 0x4u, minValue: 0u, maxValue: 0u,
            dealMagical: 0u, buyPrice: 1.0f, sellPrice: 0.5f,
            altCurrency: 0xABCDu, altCurrencyAmount: 42u, altCurrencyName: "Notes",
            numItems: 3);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.ApproachVendor);

        Assert.NotNull(p?.VendorInfo);
        var v = p!.VendorInfo!;
        Assert.Equal(0x12345678u, v.VendorGuid);
        Assert.Equal(0xABCDu, v.AlternateCurrency);
        Assert.Equal(42u, v.AlternateCurrencyAmount);
        Assert.Equal("Notes", v.AlternateCurrencyName);
        Assert.False(v.DealMagicalItems);
        Assert.Equal(3, v.ItemCount);
    }

    [Fact]
    public void Decode_ApproachVendor_ReadsForSaleItems()
    {
        // Round-trip the authoritative GameEventApproachVendor item layout: per
        // item a packed stackSize word + SerializeGameDataOnly (guid + weenie
        // header). Confirms the item list — previously left unread — now decodes
        // to name/wcid/value so the bot can perceive what a vendor sells.
        var body = BuildApproachVendor(
            vendorGuid: 0x7A9B46A9u, merchandiseItemTypes: 0u, minValue: 0u, maxValue: 0u,
            dealMagical: 0u, buyPrice: 1.0f, sellPrice: 0.5f,
            altCurrency: 0u, altCurrencyAmount: 0u, altCurrencyName: "",
            numItems: 2,
            items: new (uint, string, uint, uint, uint?, int)[]
            {
                (0x80002DEFu, "Drudge Slaying Contract", 30491u, 0x2000u, 5000u, -1),
                (0x80002BDAu, "Mite Culling Contract",   30492u, 0x2000u, 7500u, 1),
            });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.ApproachVendor);

        Assert.NotNull(p?.VendorInfo);
        var v = p!.VendorInfo!;
        Assert.Equal(2, v.ItemCount);
        Assert.Equal(2, v.Items.Count);

        Assert.Equal(0x80002DEFu, v.Items[0].Guid);
        Assert.Equal(30491u, v.Items[0].WeenieClassId);
        Assert.Equal("Drudge Slaying Contract", v.Items[0].Name);
        Assert.Equal(5000u, v.Items[0].Value);
        Assert.Equal(-1, v.Items[0].StackSize);   // unlimited supply

        Assert.Equal(0x80002BDAu, v.Items[1].Guid);
        Assert.Equal("Mite Culling Contract", v.Items[1].Name);
        Assert.Equal(30492u, v.Items[1].WeenieClassId);
        Assert.Equal(7500u, v.Items[1].Value);
        Assert.Equal(1, v.Items[1].StackSize);
    }

    [Fact]
    public void Decode_ApproachVendor_ItemWithNoValueFlag_LeavesValueNull()
    {
        var body = BuildApproachVendor(
            vendorGuid: 0x5u, merchandiseItemTypes: 0u, minValue: 0u, maxValue: 0u,
            dealMagical: 0u, buyPrice: 1.0f, sellPrice: 1.0f,
            altCurrency: 0u, altCurrencyAmount: 0u, altCurrencyName: "",
            numItems: 1,
            items: new (uint, string, uint, uint, uint?, int)[]
            {
                (0x90u, "Free Sample", 1234u, 0x1u, null, 1),
            });

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.ApproachVendor);

        Assert.NotNull(p?.VendorInfo);
        Assert.Single(p!.VendorInfo!.Items);
        Assert.Equal("Free Sample", p.VendorInfo.Items[0].Name);
        Assert.Null(p.VendorInfo.Items[0].Value);
    }

    [Fact]
    public void Decode_ApproachVendor_TruncatedItemList_KeepsHeaderAndReadsNone()
    {
        // numItems says 1 but the item blob is truncated (just a packed word +
        // guid, no weenie desc). The defensive decode keeps the header and the
        // items it could read cleanly (none here).
        var trailing = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00 };
        var body = BuildApproachVendor(
            vendorGuid: 0x1u, merchandiseItemTypes: 0u, minValue: 0u, maxValue: 0u,
            dealMagical: 0u, buyPrice: 1.0f, sellPrice: 1.0f,
            altCurrency: 0u, altCurrencyAmount: 0u, altCurrencyName: "",
            numItems: 1, trailingItems: trailing);

        var p = GameEventPayloadDecoder.Decode(body, GameEventType.ApproachVendor);

        Assert.NotNull(p?.VendorInfo);
        Assert.Equal(1, p!.VendorInfo!.ItemCount);
        Assert.Equal(0x1u, p.VendorInfo.VendorGuid);
        Assert.Empty(p.VendorInfo.Items);
    }

    [Fact]
    public void Decode_ApproachVendor_ShortBody_ReturnsNull()
    {
        // A truncated header (< the nine fixed 4-byte fields) is rejected; the
        // outer catch returns null so the caller falls back to PayloadBytes.
        var p = GameEventPayloadDecoder.Decode(new byte[20], GameEventType.ApproachVendor);
        Assert.Null(p);
    }

    // ---- SendClientContractTracker (0x0315) / Table (0x0314) ----

    private static void WriteTracker(System.Collections.Generic.List<byte> w,
        uint version, uint contractId, uint stage, double timeWhenDone, double timeWhenRepeats)
    {
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); foreach (var x in b) w.Add(x); }
        U32(version); U32(contractId); U32(stage); F64(timeWhenDone); F64(timeWhenRepeats);
    }

    [Fact]
    public void Decode_ContractTracker_ReadsEntryAndFlags()
    {
        var w = new System.Collections.Generic.List<byte>();
        WriteTracker(w, version: 3u, contractId: 12345u, stage: 2u, timeWhenDone: 100.0, timeWhenRepeats: 0.0);
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        U32(0u); // deleteContract = false
        U32(1u); // setAsDisplayContract = true

        var p = GameEventPayloadDecoder.Decode(w.ToArray(), GameEventType.SendClientContractTracker);

        Assert.NotNull(p?.ContractTracker);
        var c = p!.ContractTracker!;
        Assert.Equal(3u, c.Entry.Version);
        Assert.Equal(12345u, c.Entry.ContractId);
        Assert.Equal(2u, c.Entry.Stage);
        Assert.Equal(100.0, c.Entry.TimeWhenDone);
        Assert.False(c.DeleteContract);
        Assert.True(c.SetAsDisplayContract);
        Assert.Null(p.ContractTrackerTable);
    }

    [Fact]
    public void Decode_ContractTrackerTable_TwoEntries()
    {
        var w = new System.Collections.Generic.List<byte>();
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        U16(2); U16(32); // PackableHashTable header: count + numBuckets
        U32(111u); WriteTracker(w, 1u, 111u, 1u, 0.0, 0.0);     // key + tracker
        U32(222u); WriteTracker(w, 1u, 222u, 3u, 500.0, 600.0);

        var p = GameEventPayloadDecoder.Decode(w.ToArray(), GameEventType.SendClientContractTrackerTable);

        Assert.NotNull(p?.ContractTrackerTable);
        var t = p!.ContractTrackerTable!;
        Assert.Equal(2, t.Contracts.Count);
        Assert.Equal(111u, t.Contracts[0].ContractId);
        Assert.Equal(1u, t.Contracts[0].Stage);
        Assert.Equal(222u, t.Contracts[1].ContractId);
        Assert.Equal(3u, t.Contracts[1].Stage);
        Assert.Equal(600.0, t.Contracts[1].TimeWhenRepeats);
    }

    [Fact]
    public void Decode_ContractTrackerTable_Empty()
    {
        var w = new System.Collections.Generic.List<byte>();
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); foreach (var x in b) w.Add(x); }
        U16(0); U16(32); // empty table: count 0
        var p = GameEventPayloadDecoder.Decode(w.ToArray(), GameEventType.SendClientContractTrackerTable);
        Assert.NotNull(p?.ContractTrackerTable);
        Assert.Empty(p!.ContractTrackerTable!.Contracts);
    }

    [Fact]
    public void Decode_ContractTracker_ShortBody_ReturnsNull()
    {
        // Less than the 36B single-tracker payload; outer catch returns null so
        // the caller falls back to PayloadBytes.
        var p = GameEventPayloadDecoder.Decode(new byte[10], GameEventType.SendClientContractTracker);
        Assert.Null(p);
    }
}
