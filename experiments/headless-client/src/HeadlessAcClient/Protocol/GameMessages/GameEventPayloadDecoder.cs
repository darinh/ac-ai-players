// SPDX-License-Identifier: AGPL-3.0-or-later
// GameEventPayloadDecoder — Phase 5d, polymorphic decoder for the
// per-event payload that follows a GameEvent (0xF7B0) envelope.
//
// The envelope is decoded in GameMessageDecoder.DecodeGameEvent; this
// file decodes the body based on EventType. We only implement
// payloads we have actually seen in the firehose; everything else
// stays as raw bytes (PayloadBytes on the envelope).
//
// Authoritative server-side sources (ACE-bots fork):
//   Source/ACE.Server/Network/GameEvent/Events/GameEventWeenieError.cs
//   Source/ACE.Server/Network/GameEvent/Events/GameEventWeenieErrorWithString.cs
//   Source/ACE.Server/Network/GameEvent/Events/GameEventCharacterTitle.cs
//   Source/ACE.Server/Network/GameEvent/Events/GameEventFriendsListUpdate.cs
//   Source/ACE.Server/Network/GameEvent/Events/GameEventSetTurbineChatChannels.cs
//
// Wire layouts (after the 16B GameEvent envelope):
//
//   WeenieError (0x028A):
//     u32 errorCode                                   = 4B
//
//   UseDone (0x01C7):
//     u32 errorCode (WeenieError; 0 = None / success) = 4B
//
//   WeenieErrorWithString (0x028B):
//     u32 errorCode
//     string16L message  (u16 len + utf8 bytes + 4-byte align)
//
//   CharacterTitle (0x0029):
//     u32 unused = 1
//     u32 currentTitleId
//     u32 numTitles
//     u32[numTitles] titleIds
//
//   FriendsListUpdate (0x0021):
//     u32 friendCount
//     foreach friend:
//         u32 friendId
//         u32 isOnline      (0 or 1)
//         u32 appearOffline (always 0 in current server)
//         string16L friendName + 4-byte align
//         u32 friendsOfFriendCount   (always 0)
//         u32 inverseFriendsCount    (always 0)
//     u32 updateType (FriendsUpdateTypeFlag: 0=FullList, 1=Added, 2=Removed, 4=StatusChanged)
//
//   SetTurbineChatChannels (0x0295):
//     u32 allegiance
//     u32 general
//     u32 trade
//     u32 lfg
//     u32 roleplay
//     u32 olthoi
//     u32 society
//     u32 societyCelestialHand
//     u32 societyEldrytchWeb
//     u32 societyRadiantBlood
//     -------------------------- = 40B fixed
//
//   InventoryPutObjInContainer (0x0022):
//     u32 itemGuid
//     u32 containerGuid
//     u32 placementPosition
//     u32 containerType
//     -------------------------- = 16B fixed
//     (mirrors GameEventItemServerSaysContainId.cs)
//
//   Tell (0x02BD):
//     string16L messageText  (u16 len + cp1252 bytes + pad-to-4)
//     string16L senderName   (same encoding)
//     u32 senderId
//     u32 targetId
//     u32 chatMessageType
//     u32 padding (always 0; see GameEventTell.cs)
//
// PlayerDescription (0x0013) is deliberately NOT decoded here — it
// is a multi-section character-sheet serialization (PackableHashTable
// of 7 different property collections, attributes, skills, spells,
// enchantments, character options, shortcuts, spell bars, inventory,
// equipment) that warrants its own dedicated phase.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record WeenieErrorPayload(uint ErrorCode)
{
    public override string ToString() => $"WeenieError(0x{ErrorCode:X8})";
}

internal sealed record UseDonePayload(uint ErrorCode)
{
    public override string ToString()
        => ErrorCode == 0
            ? "UseDone(ok)"
            : $"UseDone(err=0x{ErrorCode:X8})";
}

internal sealed record WeenieErrorWithStringPayload(
    uint ErrorCode,
    string Message)
{
    public override string ToString()
    {
        var preview = Message.Length > 60 ? Message.Substring(0, 60) + "..." : Message;
        return $"WeenieErrorWithString(0x{ErrorCode:X8}: \"{preview}\")";
    }
}

internal sealed record CharacterTitlePayload(
    uint CurrentTitleId,
    IReadOnlyList<uint> TitleIds)
{
    public override string ToString() =>
        $"CharacterTitle(current=0x{CurrentTitleId:X8} count={TitleIds.Count})";
}

internal sealed record FriendEntry(
    uint FriendId,
    bool IsOnline,
    bool AppearOffline,
    string FriendName);

internal sealed record FriendsListUpdatePayload(
    IReadOnlyList<FriendEntry> Friends,
    uint UpdateType)
{
    public override string ToString() =>
        $"FriendsListUpdate(count={Friends.Count} updateType={UpdateType})";
}

internal sealed record SetTurbineChatChannelsPayload(
    uint Allegiance,
    uint General,
    uint Trade,
    uint Lfg,
    uint Roleplay,
    uint Olthoi,
    uint Society,
    uint SocietyCelestialHand,
    uint SocietyEldrytchWeb,
    uint SocietyRadiantBlood)
{
    public override string ToString() =>
        $"SetTurbineChatChannels(allegiance={Allegiance} general={General} trade={Trade} " +
        $"lfg={Lfg} rp={Roleplay} olthoi={Olthoi} society={Society})";
}

internal sealed record InventoryPutObjInContainerPayload(
    uint ItemGuid,
    uint ContainerGuid,
    uint Placement,
    uint ContainerType)
{
    public override string ToString() =>
        $"InventoryPutObjInContainer(item=0x{ItemGuid:X8} container=0x{ContainerGuid:X8} " +
        $"placement={Placement} containerType={ContainerType})";
}

internal sealed record InventoryServerSaveFailedPayload(
    uint ItemGuid,
    uint ErrorType)
{
    public override string ToString() =>
        $"InventoryServerSaveFailed(item=0x{ItemGuid:X8} err=0x{ErrorType:X8} [{WeenieErrorLabels.Label(ErrorType)}])";
}

internal static class WeenieErrorLabels
{
    // Subset of ACE.Entity.Enum.WeenieError relevant to inventory/equip errors.
    // Source: ACE-bots/Source/ACE.Entity/Enum/WeenieError.cs
    public static string Label(uint code) => code switch
    {
        0x00000000 => "None",
        0x00000007 => "TooBusyToMove",
        0x0000004B => "InvalidInventoryLocation",
        0x0000004C => "YouHaveBeenInterrupted",
        0x0000004D => "ConflictingInventoryLocation",
        0x00000051 => "ActionCancelled",
        0x00000059 => "YouAreTooBusy",
        0x00000064 => "SkillTooLow",
        0x00000065 => "LevelTooLow",
        0x000000C9 => "Stuck",
        0x000000CA => "YoureTooBusy",
        0x00000204 => "YouDoNotOwnThatItem",
        0x00000274 => "HeritageRequiresSpecificArmor",
        0x00000275 => "ArmorRequiresSpecificHeritage",
        _ => "?",
    };
}

internal sealed record TellPayload(
    string Message,
    string SenderName,
    uint SenderId,
    uint TargetId,
    uint ChatMessageType)
{
    public override string ToString()
    {
        var preview = Message.Length > 80 ? Message.Substring(0, 80) + "..." : Message;
        return $"Tell(from='{SenderName}' (0x{SenderId:X8}) -> 0x{TargetId:X8} " +
               $"chatType={ChatMessageType}: \"{preview}\")";
    }
}

/// <summary>
/// Discriminated-union view of the decoded GameEvent payload.
/// Exactly one variant is non-null. If the GameEvent type is not
/// implemented here, all variants are null and the caller should
/// fall back to <see cref="GameEventMessage.PayloadBytes"/>.
/// </summary>
internal sealed record GameEventPayload(
    GameEventType EventType,
    WeenieErrorPayload?                  WeenieError,
    WeenieErrorWithStringPayload?        WeenieErrorWithString,
    CharacterTitlePayload?               CharacterTitle,
    FriendsListUpdatePayload?            FriendsListUpdate,
    SetTurbineChatChannelsPayload?       SetTurbineChatChannels,
    UseDonePayload?                      UseDone,
    InventoryPutObjInContainerPayload?   InventoryPutObjInContainer,
    InventoryServerSaveFailedPayload?    InventoryServerSaveFailed,
    TellPayload?                         Tell)
{
    public override string ToString() => EventType switch
    {
        GameEventType.WeenieError                  when WeenieError                is { } x => x.ToString(),
        GameEventType.WeenieErrorWithString        when WeenieErrorWithString      is { } x => x.ToString(),
        GameEventType.CharacterTitle               when CharacterTitle             is { } x => x.ToString(),
        GameEventType.FriendsListUpdate            when FriendsListUpdate          is { } x => x.ToString(),
        GameEventType.SetTurbineChatChannels       when SetTurbineChatChannels     is { } x => x.ToString(),
        GameEventType.UseDone                      when UseDone                    is { } x => x.ToString(),
        GameEventType.InventoryPutObjInContainer   when InventoryPutObjInContainer is { } x => x.ToString(),
        GameEventType.InventoryServerSaveFailed    when InventoryServerSaveFailed  is { } x => x.ToString(),
        GameEventType.Tell                         when Tell                       is { } x => x.ToString(),
        _ => $"{EventType}",
    };
}

internal static class GameEventPayloadDecoder
{
    public static GameEventPayload? Decode(
        ReadOnlySpan<byte> body,
        GameEventType eventType)
    {
        try
        {
            return eventType switch
            {
                GameEventType.WeenieError =>
                    Empty(eventType) with { WeenieError = DecodeWeenieError(body) },
                GameEventType.WeenieErrorWithString =>
                    Empty(eventType) with { WeenieErrorWithString = DecodeWeenieErrorWithString(body) },
                GameEventType.CharacterTitle =>
                    Empty(eventType) with { CharacterTitle = DecodeCharacterTitle(body) },
                GameEventType.FriendsListUpdate =>
                    Empty(eventType) with { FriendsListUpdate = DecodeFriendsListUpdate(body) },
                GameEventType.SetTurbineChatChannels =>
                    Empty(eventType) with { SetTurbineChatChannels = DecodeSetTurbineChatChannels(body) },
                GameEventType.UseDone =>
                    Empty(eventType) with { UseDone = DecodeUseDone(body) },
                GameEventType.InventoryPutObjInContainer =>
                    Empty(eventType) with { InventoryPutObjInContainer = DecodeInventoryPutObjInContainer(body) },
                GameEventType.InventoryServerSaveFailed =>
                    Empty(eventType) with { InventoryServerSaveFailed = DecodeInventoryServerSaveFailed(body) },
                GameEventType.Tell =>
                    Empty(eventType) with { Tell = DecodeTell(body) },
                _ => null,
            };
        }
        catch
        {
            // Don't let a malformed payload block decoding. Caller
            // can fall back to PayloadBytes.
            return null;
        }
    }

    private static GameEventPayload Empty(GameEventType et) =>
        new GameEventPayload(et,
            WeenieError: null,
            WeenieErrorWithString: null,
            CharacterTitle: null,
            FriendsListUpdate: null,
            SetTurbineChatChannels: null,
            UseDone: null,
            InventoryPutObjInContainer: null,
            InventoryServerSaveFailed: null,
            Tell: null);

    private static WeenieErrorPayload DecodeWeenieError(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            throw new InvalidOperationException("body too short for WeenieError");
        return new WeenieErrorPayload(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
    }

    private static UseDonePayload DecodeUseDone(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            throw new InvalidOperationException("body too short for UseDone");
        return new UseDonePayload(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
    }

    private static WeenieErrorWithStringPayload DecodeWeenieErrorWithString(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6) // 4 errCode + at least u16 len
            throw new InvalidOperationException("body too short for WeenieErrorWithString");
        var errCode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        var cursor  = 4;
        var msg     = ReadString16L(body, ref cursor);
        return new WeenieErrorWithStringPayload(errCode, msg);
    }

    private static CharacterTitlePayload DecodeCharacterTitle(ReadOnlySpan<byte> body)
    {
        if (body.Length < 12) // u32 unused + u32 current + u32 count
            throw new InvalidOperationException("body too short for CharacterTitle");
        // Skip the leading constant `1u`.
        var cursor = 4;
        var current = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var count   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        if (count > int.MaxValue || body.Length - cursor < count * 4)
            throw new InvalidOperationException("CharacterTitle count overruns buffer");
        var titles = new List<uint>((int)count);
        for (var i = 0; i < count; i++)
        {
            titles.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
            cursor += 4;
        }
        return new CharacterTitlePayload(current, titles);
    }

    private static FriendsListUpdatePayload DecodeFriendsListUpdate(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) // u32 count + u32 updateType (when count==0)
            throw new InvalidOperationException("body too short for FriendsListUpdate");
        var cursor = 0;
        var count  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        if (count > 1_000_000)
            throw new InvalidOperationException($"FriendsListUpdate count absurd: {count}");
        var friends = new List<FriendEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var fid       = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            var online    = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)) != 0; cursor += 4;
            var offline   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)) != 0; cursor += 4;
            var name      = ReadString16L(body, ref cursor);
            // friends-of-friend count (always 0 in current server) and inverse (always 0)
            cursor += 4;
            cursor += 4;
            friends.Add(new FriendEntry(fid, online, offline, name));
        }
        var updateType = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        return new FriendsListUpdatePayload(friends, updateType);
    }

    private static SetTurbineChatChannelsPayload DecodeSetTurbineChatChannels(ReadOnlySpan<byte> body)
    {
        const int FixedSize = 40;
        if (body.Length < FixedSize)
            throw new InvalidOperationException($"body too short for SetTurbineChatChannels: need {FixedSize}, got {body.Length}");
        return new SetTurbineChatChannelsPayload(
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0,  4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4,  4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8,  4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(12, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(16, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(24, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(28, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(32, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(36, 4)));
    }

    private static InventoryPutObjInContainerPayload DecodeInventoryPutObjInContainer(ReadOnlySpan<byte> body)
    {
        const int FixedSize = 16;
        if (body.Length < FixedSize)
            throw new InvalidOperationException($"body too short for InventoryPutObjInContainer: need {FixedSize}, got {body.Length}");
        return new InventoryPutObjInContainerPayload(
            ItemGuid:      BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0,  4)),
            ContainerGuid: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4,  4)),
            Placement:     BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8,  4)),
            ContainerType: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(12, 4)));
    }

    private static InventoryServerSaveFailedPayload DecodeInventoryServerSaveFailed(ReadOnlySpan<byte> body)
    {
        const int FixedSize = 8;
        if (body.Length < FixedSize)
            throw new InvalidOperationException($"body too short for InventoryServerSaveFailed: need {FixedSize}, got {body.Length}");
        return new InventoryServerSaveFailedPayload(
            ItemGuid:  BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            ErrorType: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)));
    }

    private static TellPayload DecodeTell(ReadOnlySpan<byte> body)
    {
        // Mirrors GameEventTell.cs (single overload used for NPC dialogue):
        //   string16L messageText
        //   string16L senderName
        //   u32 senderId
        //   u32 targetId
        //   u32 chatMessageType
        //   u32 padding (always 0 from server side)
        if (body.Length < 2)
            throw new InvalidOperationException("body too short for Tell");
        var cursor = 0;
        var message = ReadString16L(body, ref cursor);
        var senderName = ReadString16L(body, ref cursor);
        if (body.Length - cursor < 16)
            throw new InvalidOperationException("Tell: not enough bytes for trailing 4xu32");
        var senderId  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var targetId  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var chatType  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        // u32 padding — ignored.
        return new TellPayload(message, senderName, senderId, targetId, chatType);
    }

    /// <summary>
    /// Reads a string16L: u16 length, then `length` bytes UTF-8,
    /// then padding so the cursor (relative to body start) lands
    /// on the next 4-byte boundary. The GameEvent body itself
    /// begins at a 4-byte-aligned offset in the stream (the 16B
    /// envelope is itself 4-aligned), so body-relative mod-4
    /// alignment is equivalent to BinaryWriter.Align()'s
    /// stream-relative behaviour.
    /// </summary>
    private static string ReadString16L(ReadOnlySpan<byte> body, ref int cursor)
    {
        if (body.Length - cursor < 2)
            throw new InvalidOperationException("string16L: not enough bytes for length");
        var len = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 2;
        if (body.Length - cursor < len)
            throw new InvalidOperationException($"string16L: declared {len} bytes, only {body.Length - cursor} remaining");
        var str = Encoding.UTF8.GetString(body.Slice(cursor, len));
        cursor += len;
        var pad = (4 - (cursor % 4)) % 4;
        cursor += pad;
        return str;
    }
}
