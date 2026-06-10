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
// PlayerDescription (0x0013) is PARTIALLY decoded here: only the
// initial Level (PropertyInt 25) and experience totals (PropertyInt64
// TotalExperience=1 / AvailableExperience=2) are extracted from the
// leading property-flags + Int32 + Int64 PackableHashTable sections.
// The remaining sections (Bool/Double/String/Did/Iid/Position, the
// attribute/skill/spell/enchantment vectors, character options,
// inventory, equipment) are intentionally NOT parsed — they warrant
// their own dedicated phase. Layout: u32 propertyFlags, u32 weenieType,
// then each present section in fixed write order Int32 → Int64 → …
// (PackableHashTable header = u16 count + u16 numBuckets, then entries).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// One fellow's stat block from a FellowshipUpdateFellow (0x02C0) GameEvent —
/// the incremental "this fellow was added / changed" update the server sends
/// to each fellowship member. <see cref="UpdateType"/> is the raw
/// FellowUpdateType enum value as written by the server. <see cref="ShareLootFlags"/>
/// is the raw field the server writes (the shareLoot bool shifted left one bit,
/// i.e. 0 or 2). The server's TODO-stub cpCached / lumCached fields are always 0
/// and are not surfaced. Pure wire-protocol projection — no game knowledge.
/// </summary>
internal sealed record FellowshipUpdateFellowPayload(
    uint Guid,
    uint Level,
    uint HealthMax,
    uint StaminaMax,
    uint ManaMax,
    uint HealthCurrent,
    uint StaminaCurrent,
    uint ManaCurrent,
    uint ShareLootFlags,
    string Name,
    uint UpdateType)
{
    public override string ToString() =>
        $"FellowshipUpdateFellow(0x{Guid:X8} \"{Name}\" L{Level} " +
        $"hp={HealthCurrent}/{HealthMax} updateType={UpdateType})";
}

/// <summary>
/// One member's stat block within a FellowshipFullUpdate (0x02BE) snapshot.
/// Same wire shape as the incremental update minus the trailing updateType:
/// the server's per-fellow shareLoot is a fixed stub here and is not surfaced,
/// and the cpCached / lumCached TODO-stubs (always 0) are skipped. Pure
/// wire-protocol projection.
/// </summary>
internal sealed record FellowMember(
    uint Guid,
    uint Level,
    uint HealthMax,
    uint StaminaMax,
    uint ManaMax,
    uint HealthCurrent,
    uint StaminaCurrent,
    uint ManaCurrent,
    string Name);

/// <summary>
/// The whole-fellowship snapshot from a FellowshipFullUpdate (0x02BE) GameEvent —
/// the membership view the bot needs to know it is in a fellowship, who is in it,
/// and who leads. <see cref="ShareXp"/> / <see cref="EvenShare"/> /
/// <see cref="Open"/> / <see cref="IsLocked"/> are the fellowship bool flags the
/// server writes (each as a u32). The trailing DepartedMembers / FellowshipLocks
/// bookkeeping tables the server appends are not surfaced (the bot does not need
/// them yet) and are left unread — every field here precedes them on the wire.
/// </summary>
internal sealed record FellowshipFullUpdatePayload(
    IReadOnlyList<FellowMember> Members,
    string FellowshipName,
    uint LeaderGuid,
    bool ShareXp,
    bool EvenShare,
    bool Open,
    bool IsLocked)
{
    public override string ToString() =>
        $"FellowshipFullUpdate(\"{FellowshipName}\" members={Members.Count} " +
        $"leader=0x{LeaderGuid:X8} shareXp={ShareXp} even={EvenShare} " +
        $"open={Open} locked={IsLocked})";
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

internal sealed record WieldObjectPayload(
    uint ItemGuid,
    uint NewLocation)
{
    public override string ToString() =>
        $"WieldObject(item=0x{ItemGuid:X8} loc=0x{NewLocation:X8} [{EquipMaskLabels.Label(NewLocation)}])";
}

internal sealed record PopupStringPayload(string Message)
{
    public override string ToString()
    {
        var preview = Message.Length > 240 ? Message[..240] + "..." : Message;
        // Keep multi-line popups on a single log line.
        var sanitized = preview.Replace("\r", "\\r").Replace("\n", "\\n");
        return $"PopupString(\"{sanitized}\")";
    }
}

internal sealed record BookPageSummary(
    uint AuthorId,
    string AuthorName,
    string AuthorAccount,
    uint Flags,
    bool TextIncluded,
    bool IgnoreAuthor,
    string? PageText);

internal sealed record BookDataResponsePayload(
    uint BookId,
    int MaxNumPages,
    int NumPages,
    int MaxNumCharsPerPage,
    IReadOnlyList<BookPageSummary> Pages,
    string Inscription,
    uint AuthorId,
    string AuthorName)
{
    public override string ToString()
    {
        var pageNote = Pages.Count > 0 && Pages[0].PageText is not null
            ? $" pg0=\"{Sanitize(Pages[0].PageText!)}\""
            : "";
        return $"BookDataResponse(book=0x{BookId:X8} pages={NumPages}/{MaxNumPages} maxChars={MaxNumCharsPerPage} insc=\"{Sanitize(Inscription)}\" author=\"{AuthorName}\" hasText={Pages.Count(p => p.TextIncluded)}/{Pages.Count}{pageNote})";
    }

    private static string Sanitize(string s)
    {
        var preview = s.Length > 160 ? s[..160] + "..." : s;
        return preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
    }
}

internal sealed record BookPageDataResponsePayload(
    uint BookId,
    int PageIndex,
    uint AuthorId,
    string AuthorName,
    string AuthorAccount,
    uint Flags,
    bool TextIncluded,
    bool IgnoreAuthor,
    string PageText)
{
    public override string ToString()
    {
        var preview = PageText.Length > 240 ? PageText[..240] + "..." : PageText;
        var sanitized = preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
        return $"BookPageDataResponse(book=0x{BookId:X8} page={PageIndex} author=\"{AuthorName}\" text=\"{sanitized}\")";
    }
}

internal static class EquipMaskLabels
{
    // Verified against ACE-bots/Source/ACE.Entity/Enum/EquipMask.cs.
    // The value can be a bitmask combining adjacent slots (e.g. pants
    // wield to UpperLegArmor|LowerLegArmor = 0x6000) so we list every
    // set bit.
    private static readonly (uint Bit, string Name)[] Bits =
    {
        (0x00000001, "HeadWear"),
        (0x00000002, "ChestWear"),
        (0x00000004, "AbdomenWear"),
        (0x00000008, "UpperArmWear"),
        (0x00000010, "LowerArmWear"),
        (0x00000020, "HandWear"),
        (0x00000040, "UpperLegWear"),
        (0x00000080, "LowerLegWear"),
        (0x00000100, "FootWear"),
        (0x00000200, "ChestArmor"),
        (0x00000400, "AbdomenArmor"),
        (0x00000800, "UpperArmArmor"),
        (0x00001000, "LowerArmArmor"),
        (0x00002000, "UpperLegArmor"),
        (0x00004000, "LowerLegArmor"),
        (0x00008000, "NeckWear"),
        (0x00010000, "WristWearLeft"),
        (0x00020000, "WristWearRight"),
        (0x00040000, "FingerWearLeft"),
        (0x00080000, "FingerWearRight"),
        (0x00100000, "MeleeWeapon"),
        (0x00200000, "Shield"),
        (0x00400000, "MissileWeapon"),
        (0x00800000, "MissileAmmo"),
        (0x01000000, "Held"),
        (0x02000000, "TwoHanded"),
        (0x04000000, "TrinketOne"),
        (0x08000000, "Cloak"),
        (0x10000000, "SigilOne"),
        (0x20000000, "SigilTwo"),
        (0x40000000, "SigilThree"),
    };

    public static string Label(uint mask)
    {
        if (mask == 0) return "None";
        var names = new List<string>();
        foreach (var (bit, name) in Bits)
        {
            if ((mask & bit) != 0) names.Add(name);
        }
        return names.Count == 0 ? $"0x{mask:X8}" : string.Join("|", names);
    }
}

internal static class WeenieErrorLabels
{
    // Subset of ACE.Entity.Enum.WeenieError. Codes VERIFIED against
    // ACE-bots/Source/ACE.Entity/Enum/WeenieError.cs as of 2025-11.
    // Prior table had many wrong values (decimal/hex confusion).
    public static string Label(uint code) => code switch
    {
        0x0000 => "None",
        0x001D => "YoureTooBusy",
        0x0029 => "Stuck",
        0x0036 => "ActionCancelled",
        0x03F0 => "InvalidInventoryLocation",
        0x03F3 => "ConflictingInventoryLocation",
        0x0406 => "MagicTargetOutOfRange",
        0x041D => "YouMustBeLeaderOfFellowship",
        0x0420 => "LevelTooLow",
        0x0429 => "YouMustControlAtLeastOneStack",
        0x0437 => "YouDoNotPassCraftingRequirements",
        0x043A => "YouMustBeInPeaceModeToTrade",
        0x043F => "YouHaveSolvedThisQuestTooManyTimes",
        0x0445 => "ItemRequiresQuestToBePickedUp",
        0x0466 => "YouMustHaveDarkMajestyToUsePortal",
        0x0468 => "SkillTooLow",
        0x046A => "TradeAiDoesntWant",
        0x0474 => "YouMustCompleteQuestToUsePortal",
        0x049E => "YouMustLinkToLifestoneToRecall",
        0x04A3 => "YouMustLinkToPortalToRecall",
        0x04BE => "YouDoNotOwnThatItem",
        0x04CD => "TradeAiRefuseEmote",
        0x0550 => "MissileOutOfRange",
        0x0585 => "HeritageRequiresSpecificArmor",
        0x0586 => "ArmorRequiresSpecificHeritage",
        0x058D => "YouCannotUseThatItem",
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

internal sealed record UpdateHealthPayload(uint ObjectId, float HealthFraction)
{
    public override string ToString() =>
        $"UpdateHealth(objectId=0x{ObjectId:X8} healthFraction={HealthFraction:F3} ({(int)(HealthFraction * 100)}%))";
}

internal sealed record AttackDonePayload(uint ErrorCode)
{
    public override string ToString() =>
        $"AttackDone(errCode=0x{ErrorCode:X4} {WeenieErrorLabels.Label(ErrorCode)})";
}

/// <summary>
/// GameEventAttackerNotification (0x01B1) — sent to the ATTACKER when
/// one of their swings LANDS on a defender. Carries the defender's
/// display name plus the damage dealt. Decoded so the Motor can count
/// landing hits and surface combat outcomes to the LLM (raw perception:
/// "you hit X for N"). The bot deals 0 damage when it NEVER receives
/// this event (every swing instead produces an EvasionAttackerNotification).
/// </summary>
internal sealed record AttackerNotificationPayload(string DefenderName, uint Damage)
{
    public override string ToString() =>
        $"AttackerNotification(defender=\"{DefenderName}\" damage={Damage})";
}

/// <summary>
/// GameEventEvasionAttackerNotification (0x01B3) — sent to the ATTACKER
/// when their swing is EVADED by the defender (a miss). Carries only the
/// defender's display name. Decoded so the Motor can count evaded swings
/// and surface the raw outcome to the LLM (the LLM owns the disengage /
/// target-choice decision — source never auto-disengages on evasion).
/// </summary>
internal sealed record EvasionAttackerNotificationPayload(string DefenderName)
{
    public override string ToString() =>
        $"EvasionAttackerNotification(defender=\"{DefenderName}\")";
}

/// <summary>
/// GameEventDefenderNotification (0x01B2) — sent to the DEFENDER (the
/// bot) when a creature's swing LANDS on it. Carries the attacker's
/// display name plus the damage taken. Decoded so the Motor can surface
/// RAW perception that a named creature is attacking the bot (populates
/// ObservedHostile); the LLM owns the fight-vs-flee decision.
/// </summary>
internal sealed record DefenderNotificationPayload(string AttackerName, uint Damage)
{
    public override string ToString() =>
        $"DefenderNotification(attacker=\"{AttackerName}\" damage={Damage})";
}

/// <summary>
/// GameEventEvasionDefenderNotification (0x01B4) — sent to the DEFENDER
/// (the bot) when it EVADES a creature's swing (a miss against the bot).
/// Carries only the attacker's display name. Decoded for the same RAW
/// "this creature is attacking me" perception — an evaded incoming swing
/// is still evidence of hostility.
/// </summary>
internal sealed record EvasionDefenderNotificationPayload(string AttackerName)
{
    public override string ToString() =>
        $"EvasionDefenderNotification(attacker=\"{AttackerName}\")";
}

/// <summary>
/// Partial decode of the PlayerDescription (0x0013) login bundle.
/// Extracts the initial Level (PropertyInt 25), experience totals
/// (PropertyInt64 TotalExperience=1, AvailableExperience=2), and the
/// character sheet's attribute ranks (<see cref="PdAttribute"/>) and
/// skills (<see cref="PdSkill"/>). The later spell/enchantment/option/
/// inventory sections are not parsed. Any field is null when its
/// property/section is absent (or the bundle was truncated before it).
/// </summary>
internal sealed record PlayerDescriptionPayload(
    int? Level,
    long? TotalExperience,
    long? AvailableExperience,
    IReadOnlyList<PdAttribute>? Attributes = null,
    IReadOnlyList<PdSkill>? Skills = null)
{
    public override string ToString()
    {
        var attrPart = Attributes is null ? "" : $" attrs={Attributes.Count}";
        var skillPart = Skills is null ? "" : $" skills={Skills.Count}";
        return $"PlayerDescription(level={Level?.ToString() ?? "?"} " +
               $"totalXp={TotalExperience?.ToString() ?? "?"} " +
               $"unspentXp={AvailableExperience?.ToString() ?? "?"}{attrPart}{skillPart})";
    }
}

/// <summary>
/// One primary attribute or vital from the login character sheet.
/// <see cref="Base"/> is the unbuffed base value (StartingValue + raised
/// <see cref="Ranks"/>); it excludes equipment/spell buffs and, for vitals,
/// the Endurance/Self-derived max formula. <see cref="ExperienceSpent"/> is
/// the XP already invested in this attribute.
/// </summary>
internal sealed record PdAttribute(string Name, uint Base, uint Ranks, uint ExperienceSpent);

/// <summary>
/// One skill entry from the login character sheet.
/// <see cref="AdvancementClass"/> is the wire SkillAdvancementClass enum:
/// 0=Inactive, 1=Untrained, 2=Trained, 3=Specialized (only Trained and
/// Specialized are raisable). <see cref="Ranks"/> is the RAISED ranks only
/// (it excludes the <see cref="InitLevel"/> training/creation bonus and the
/// attribute contribution to the displayed skill value).
/// </summary>
internal sealed record PdSkill(
    string Name, uint Id, uint AdvancementClass, uint Ranks, uint InitLevel, uint ExperienceSpent);

/// <summary>
/// Discriminated-union view of the decoded GameEvent payload.
/// Exactly one variant is non-null. If the GameEvent type is not
/// implemented here, all variants are null and the caller should
/// fall back to <see cref="GameEventMessage.PayloadBytes"/>.
/// </summary>
internal sealed record GameEventPayload(
    GameEventType EventType,
    PlayerDescriptionPayload?            PlayerDescription,
    WeenieErrorPayload?                  WeenieError,
    WeenieErrorWithStringPayload?        WeenieErrorWithString,
    CharacterTitlePayload?               CharacterTitle,
    FriendsListUpdatePayload?            FriendsListUpdate,
    SetTurbineChatChannelsPayload?       SetTurbineChatChannels,
    UseDonePayload?                      UseDone,
    InventoryPutObjInContainerPayload?   InventoryPutObjInContainer,
    InventoryServerSaveFailedPayload?    InventoryServerSaveFailed,
    WieldObjectPayload?                  WieldObject,
    TellPayload?                         Tell,
    PopupStringPayload?                  PopupString,
    BookDataResponsePayload?             BookDataResponse,
    BookPageDataResponsePayload?         BookPageDataResponse,
    UpdateHealthPayload?                 UpdateHealth,
    AttackDonePayload?                   AttackDone,
    AttackerNotificationPayload?         AttackerNotification,
    EvasionAttackerNotificationPayload?  EvasionAttackerNotification,
    DefenderNotificationPayload?         DefenderNotification,
    EvasionDefenderNotificationPayload?  EvasionDefenderNotification,
    FellowshipUpdateFellowPayload?       FellowshipUpdateFellow,
    FellowshipFullUpdatePayload?         FellowshipFullUpdate)
{
    public override string ToString() => EventType switch
    {
        GameEventType.PlayerDescription            when PlayerDescription          is { } x => x.ToString(),
        GameEventType.WeenieError                  when WeenieError                is { } x => x.ToString(),
        GameEventType.WeenieErrorWithString        when WeenieErrorWithString      is { } x => x.ToString(),
        GameEventType.CharacterTitle               when CharacterTitle             is { } x => x.ToString(),
        GameEventType.FriendsListUpdate            when FriendsListUpdate          is { } x => x.ToString(),
        GameEventType.SetTurbineChatChannels       when SetTurbineChatChannels     is { } x => x.ToString(),
        GameEventType.UseDone                      when UseDone                    is { } x => x.ToString(),
        GameEventType.InventoryPutObjInContainer   when InventoryPutObjInContainer is { } x => x.ToString(),
        GameEventType.InventoryServerSaveFailed    when InventoryServerSaveFailed  is { } x => x.ToString(),
        GameEventType.WieldObject                  when WieldObject                is { } x => x.ToString(),
        GameEventType.Tell                         when Tell                       is { } x => x.ToString(),
        GameEventType.PopupString                  when PopupString                is { } x => x.ToString(),
        GameEventType.BookDataResponse             when BookDataResponse           is { } x => x.ToString(),
        GameEventType.BookPageDataResponse         when BookPageDataResponse       is { } x => x.ToString(),
        GameEventType.UpdateHealth                 when UpdateHealth               is { } x => x.ToString(),
        GameEventType.AttackDone                   when AttackDone                 is { } x => x.ToString(),
        GameEventType.AttackerNotification         when AttackerNotification       is { } x => x.ToString(),
        GameEventType.EvasionAttackerNotification  when EvasionAttackerNotification is { } x => x.ToString(),
        GameEventType.DefenderNotification         when DefenderNotification       is { } x => x.ToString(),
        GameEventType.EvasionDefenderNotification  when EvasionDefenderNotification is { } x => x.ToString(),
        GameEventType.FellowshipUpdateFellow       when FellowshipUpdateFellow      is { } x => x.ToString(),
        GameEventType.FellowshipFullUpdate         when FellowshipFullUpdate        is { } x => x.ToString(),
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
                GameEventType.PlayerDescription =>
                    Empty(eventType) with { PlayerDescription = DecodePlayerDescription(body) },
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
                GameEventType.WieldObject =>
                    Empty(eventType) with { WieldObject = DecodeWieldObject(body) },
                GameEventType.Tell =>
                    Empty(eventType) with { Tell = DecodeTell(body) },
                GameEventType.PopupString =>
                    Empty(eventType) with { PopupString = DecodePopupString(body) },
                GameEventType.BookDataResponse =>
                    Empty(eventType) with { BookDataResponse = DecodeBookDataResponse(body) },
                GameEventType.BookPageDataResponse =>
                    Empty(eventType) with { BookPageDataResponse = DecodeBookPageDataResponse(body) },
                GameEventType.UpdateHealth =>
                    Empty(eventType) with { UpdateHealth = DecodeUpdateHealth(body) },
                GameEventType.AttackDone =>
                    Empty(eventType) with { AttackDone = DecodeAttackDone(body) },
                GameEventType.AttackerNotification =>
                    Empty(eventType) with { AttackerNotification = DecodeAttackerNotification(body) },
                GameEventType.EvasionAttackerNotification =>
                    Empty(eventType) with { EvasionAttackerNotification = DecodeEvasionAttackerNotification(body) },
                GameEventType.DefenderNotification =>
                    Empty(eventType) with { DefenderNotification = DecodeDefenderNotification(body) },
                GameEventType.EvasionDefenderNotification =>
                    Empty(eventType) with { EvasionDefenderNotification = DecodeEvasionDefenderNotification(body) },
                GameEventType.FellowshipUpdateFellow =>
                    Empty(eventType) with { FellowshipUpdateFellow = DecodeFellowshipUpdateFellow(body) },
                GameEventType.FellowshipFullUpdate =>
                    Empty(eventType) with { FellowshipFullUpdate = DecodeFellowshipFullUpdate(body) },
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
            PlayerDescription: null,
            WeenieError: null,
            WeenieErrorWithString: null,
            CharacterTitle: null,
            FriendsListUpdate: null,
            SetTurbineChatChannels: null,
            UseDone: null,
            InventoryPutObjInContainer: null,
            InventoryServerSaveFailed: null,
            WieldObject: null,
            Tell: null,
            PopupString: null,
            BookDataResponse: null,
            BookPageDataResponse: null,
            UpdateHealth: null,
            AttackDone: null,
            AttackerNotification: null,
            EvasionAttackerNotification: null,
            DefenderNotification: null,
            EvasionDefenderNotification: null,
            FellowshipUpdateFellow: null,
            FellowshipFullUpdate: null);

    // PlayerDescription (0x0013) — wire layout from the ACE-bots server
    // serializer GameEventPlayerDescription.cs. We extract the initial
    // Level (PropertyInt 25) + experience totals (PropertyInt64
    // TotalExperience=1 / AvailableExperience=2) from the leading property
    // hashtables, then traverse the remaining property sections to reach
    // the attribute + skill vectors that carry the character sheet.
    //
    // The body begins at the back-patched property-flags dword. Property
    // sections are written in a FIXED order that is NOT the flag-bit order:
    //   Int32(0x0001), Int64(0x0080), Bool(0x0002), Double(0x0004),
    //   String(0x0010), Did(0x0008), Iid(0x0040), Position(0x0020).
    // Each is a PackableHashTable (u16 count + u16 numBuckets, then
    // entries). After them comes the (non-flag-gated) vector section:
    //   u32 vectorFlags, u32 healthPresent, [attribute block], [skill PHT].
    private const uint DescFlagPropertyInt32  = 0x0001;
    private const uint DescFlagPropertyBool   = 0x0002;
    private const uint DescFlagPropertyDouble = 0x0004;
    private const uint DescFlagPropertyDid    = 0x0008;
    private const uint DescFlagPropertyString = 0x0010;
    private const uint DescFlagPosition       = 0x0020;
    private const uint DescFlagPropertyIid    = 0x0040;
    private const uint DescFlagPropertyInt64  = 0x0080;
    private const uint LevelPropertyIntId = 25;
    private const uint TotalExperienceInt64Id = 1;
    private const uint AvailableExperienceInt64Id = 2;

    // DescriptionVectorFlag bits (GameEventPlayerDescription.cs).
    private const uint VectorFlagAttribute = 0x0001;
    private const uint VectorFlagSkill     = 0x0002;

    // AttributeCache bits in the server's WRITE order. The 6 primary
    // attributes write 3 u32s (Ranks, StartingValue, ExperienceSpent); the
    // 3 vitals append a 4th u32 (Current) we do not surface. Names match the
    // RaiseAttribute / RaiseVital resolver vocabulary.
    private static readonly (uint Bit, string Name, bool IsVital)[] AttributeOrder =
    {
        (0x0001, "strength",     false),
        (0x0002, "endurance",    false),
        (0x0004, "quickness",    false),
        (0x0008, "coordination", false),
        (0x0010, "focus",        false),
        (0x0020, "self",         false),
        (0x0040, "health",       true),
        (0x0080, "stamina",      true),
        (0x0100, "mana",         true),
    };

    // Per-skill wire entry: u32 id + u16 Ranks + u16(=1) + u32
    // AdvancementClass + u32 ExperienceSpent + u32 InitLevel + u32(=0
    // resistance) + f64(=0 last-used) = 32B. NOTE: CreatureSkill.Ranks is
    // a ushort on the server (CreatureSkill.cs), so Ranks is 2 bytes —
    // unlike attributes/vitals whose Ranks are u32.
    private const int SkillEntrySize = 32;

    private static PlayerDescriptionPayload DecodePlayerDescription(ReadOnlySpan<byte> body)
    {
        int? level = null;
        long? totalXp = null;
        long? availXp = null;
        List<PdAttribute>? attributes = null;
        List<PdSkill>? skills = null;

        PlayerDescriptionPayload Result() =>
            new(level, totalXp, availXp, attributes, skills);

        // u32 propertyFlags + u32 weenieType
        if (body.Length < 8)
        {
            WarnPlayerDescOverrun("header", 8, body.Length, cursor: 0);
            return Result();
        }
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        var cursor = 8; // skip flags + weenieType

        // Property sections in WRITE order. We read the two we care about
        // (Int32 Level, Int64 XP) and SKIP the rest. Unlike the old
        // (stop-after-Int64) decoder these fail CLOSED: any malformed or
        // overrunning section returns the fields gathered so far with
        // attrs/skills null, so a corrupt count can never desync the cursor
        // and make us decode garbage as attributes/skills.
        if ((flags & DescFlagPropertyInt32) != 0 &&
            !TryReadInt32Section(body, ref cursor, ref level))
            return Result();
        if ((flags & DescFlagPropertyInt64) != 0 &&
            !TryReadInt64Section(body, ref cursor, ref totalXp, ref availXp))
            return Result();
        if ((flags & DescFlagPropertyBool) != 0 &&
            !TrySkipFixedPht(body, ref cursor, entrySize: 8, "bool"))
            return Result();
        if ((flags & DescFlagPropertyDouble) != 0 &&
            !TrySkipFixedPht(body, ref cursor, entrySize: 12, "double"))
            return Result();
        if ((flags & DescFlagPropertyString) != 0 &&
            !TrySkipStringSection(body, ref cursor))
            return Result();
        if ((flags & DescFlagPropertyDid) != 0 &&
            !TrySkipFixedPht(body, ref cursor, entrySize: 8, "did"))
            return Result();
        if ((flags & DescFlagPropertyIid) != 0 &&
            !TrySkipFixedPht(body, ref cursor, entrySize: 8, "iid"))
            return Result();
        // Position entry = u32 PositionType key + (u32 landblock + 3 floats
        // + 4 floats) = 4 + 32 = 36B.
        if ((flags & DescFlagPosition) != 0 &&
            !TrySkipFixedPht(body, ref cursor, entrySize: 36, "position"))
            return Result();

        // Vector section — NOT propertyFlags-gated; always written.
        if (cursor + 8 > body.Length)
        {
            WarnPlayerDescOverrun("vector-header", 8, body.Length, cursor);
            return Result();
        }
        var vectorFlags = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        cursor += 8; // vectorFlags + healthPresent (u32 bool)

        if ((vectorFlags & VectorFlagAttribute) != 0 &&
            !TryReadAttributeVector(body, ref cursor, out attributes))
            return Result();
        if ((vectorFlags & VectorFlagSkill) != 0 &&
            !TryReadSkillVector(body, ref cursor, out skills))
            return Result();

        return Result();
    }

    // Reads the PropertyInt32 PackableHashTable, capturing Level (id 25).
    private static bool TryReadInt32Section(ReadOnlySpan<byte> body, ref int cursor, ref int? level)
    {
        if (cursor + 4 > body.Length)
        {
            WarnPlayerDescOverrun("int32-header", 4, body.Length, cursor);
            return false;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 4; // count + numBuckets
        if (count > (body.Length - cursor) / 8)
        {
            WarnPlayerDescOverrun("int32-entries", count * 8, body.Length, cursor);
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            var key = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            var val = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(cursor + 4, 4));
            cursor += 8;
            if (key == LevelPropertyIntId)
                level = val;
        }
        return true;
    }

    // Reads the PropertyInt64 PackableHashTable, capturing Total/Available XP.
    private static bool TryReadInt64Section(ReadOnlySpan<byte> body, ref int cursor, ref long? totalXp, ref long? availXp)
    {
        if (cursor + 4 > body.Length)
        {
            WarnPlayerDescOverrun("int64-header", 4, body.Length, cursor);
            return false;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 4;
        if (count > (body.Length - cursor) / 12)
        {
            WarnPlayerDescOverrun("int64-entries", count * 12, body.Length, cursor);
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            var key = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            var val = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(cursor + 4, 8));
            cursor += 12;
            if (key == TotalExperienceInt64Id)
                totalXp = val;
            else if (key == AvailableExperienceInt64Id)
                availXp = val;
        }
        return true;
    }

    // Skips a PackableHashTable whose entries are a fixed byte size.
    private static bool TrySkipFixedPht(ReadOnlySpan<byte> body, ref int cursor, int entrySize, string section)
    {
        if (cursor + 4 > body.Length)
        {
            WarnPlayerDescOverrun(section + "-header", 4, body.Length, cursor);
            return false;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 4;
        long needed = (long)count * entrySize;
        if (needed > body.Length - cursor)
        {
            WarnPlayerDescOverrun(section + "-entries", (int)Math.Min(int.MaxValue, needed), body.Length, cursor);
            return false;
        }
        cursor += (int)needed;
        return true;
    }

    // Skips the PropertyString PackableHashTable. Each entry is a u32 key
    // followed by WriteString16L { u16 len + len CP1252 bytes + pad so
    // (2 + len) is a multiple of 4 }.
    private static bool TrySkipStringSection(ReadOnlySpan<byte> body, ref int cursor)
    {
        if (cursor + 4 > body.Length)
        {
            WarnPlayerDescOverrun("string-header", 4, body.Length, cursor);
            return false;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 4;
        for (int i = 0; i < count; i++)
        {
            if (cursor + 6 > body.Length) // u32 key + u16 len
            {
                WarnPlayerDescOverrun("string-entry", 6, body.Length, cursor);
                return false;
            }
            cursor += 4; // key
            int len = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            int field = 2 + len;
            int advance = field + ((4 - (field % 4)) % 4);
            if (cursor + advance > body.Length)
            {
                WarnPlayerDescOverrun("string-data", advance, body.Length, cursor);
                return false;
            }
            cursor += advance;
        }
        return true;
    }

    // Reads the attribute block: u32 attributeFlags then, per set bit (in
    // AttributeOrder), the 3 (or 4 for vitals) u32 fields. Base = StartingValue
    // + Ranks. On overrun, surfaces the entries read so far and stops.
    private static bool TryReadAttributeVector(ReadOnlySpan<byte> body, ref int cursor, out List<PdAttribute>? attributes)
    {
        attributes = null;
        if (cursor + 4 > body.Length)
        {
            WarnPlayerDescOverrun("attr-flags", 4, body.Length, cursor);
            return false;
        }
        var attrFlags = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        cursor += 4;
        var list = new List<PdAttribute>(AttributeOrder.Length);
        attributes = list;
        foreach (var (bit, name, isVital) in AttributeOrder)
        {
            if ((attrFlags & bit) == 0)
                continue;
            int need = isVital ? 16 : 12;
            if (cursor + need > body.Length)
            {
                WarnPlayerDescOverrun("attr-" + name, need, body.Length, cursor);
                return false;
            }
            var ranks         = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            var startingValue = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 4, 4));
            var xpSpent       = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 8, 4));
            cursor += need; // vitals append a u32 Current we don't surface
            list.Add(new PdAttribute(name, startingValue + ranks, ranks, xpSpent));
        }
        return true;
    }

    // Reads the skill PackableHashTable into PdSkill entries (all skills,
    // any AdvancementClass — the render layer filters to raisable ones).
    private static bool TryReadSkillVector(ReadOnlySpan<byte> body, ref int cursor, out List<PdSkill>? skills)
    {
        skills = null;
        if (cursor + 4 > body.Length)
        {
            WarnPlayerDescOverrun("skill-header", 4, body.Length, cursor);
            return false;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 4;
        if ((long)count * SkillEntrySize > body.Length - cursor)
        {
            WarnPlayerDescOverrun("skill-entries", count * SkillEntrySize, body.Length, cursor);
            return false;
        }
        var list = new List<PdSkill>(count);
        skills = list;
        for (int i = 0; i < count; i++)
        {
            var id    = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            var ranks = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor + 4, 2));
            // cursor + 6: u16 const 1
            var sac   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 8, 4));
            var xp    = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 12, 4));
            var init  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 16, 4));
            // cursor + 20: u32 resistance(0), cursor + 24: f64 last-used(0)
            cursor += SkillEntrySize;
            list.Add(new PdSkill(SkillName(id), id, sac, ranks, init, xp));
        }
        return true;
    }

    // Wire skill-id -> name via the local Protocol.Skill mirror. Unknown
    // ids (future/unmapped) fall back to a stable synthetic name. Internal
    // so the discrete PrivateUpdateSkill (0x02DD) apply resolves names the
    // same way the login bundle does.
    internal static string SkillName(uint id)
    {
        var e = (Skill)id;
        return Enum.IsDefined(typeof(Skill), e) ? e.ToString() : $"Skill{id}";
    }

    // Wire primary-attribute-id -> the SAME canonical lowercase name the
    // login bundle seeds (so discrete 0x02E3 updates upsert by name). Only
    // the six primaries (PropertyAttribute 1..6); returns null otherwise
    // (vitals ride separate opcodes).
    internal static string? PrimaryAttributeName(uint id) => id switch
    {
        1 => "strength",
        2 => "endurance",
        3 => "quickness",
        4 => "coordination",
        5 => "focus",
        6 => "self",
        _ => null,
    };


    private static void WarnPlayerDescOverrun(string section, int needed, int bodyLen, int cursor)
        => Console.Error.WriteLine(
            $"[playerdesc] decode overrun in {section}: needed {needed}B at cursor {cursor} " +
            $"but body is {bodyLen}B — returning partial (extracted fields may be null)");

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

    // FellowshipUpdateFellow (0x02C0) — incremental single-fellow update. Wire
    // layout mirrors the authoritative server writer
    // GameEventFellowshipUpdateFellow.cs (after the 16B GameEvent envelope):
    //   u32 guid
    //   u32 cpCached   (TODO-stub, always 0 server-side; skipped)
    //   u32 lumCached  (TODO-stub, always 0 server-side; skipped)
    //   u32 level
    //   u32 healthMax  u32 staminaMax  u32 manaMax
    //   u32 healthCur  u32 staminaCur  u32 manaCur
    //   u32 shareLoot  (server writes Convert.ToUInt32(bool) << 1 → 0 or 2)
    //   string16L name (u16 len + utf8 + pad to 4)
    //   u32 fellowUpdateType
    // Fixed prefix is 11 u32 = 44B, then the name, then a trailing 4B updateType.
    private static FellowshipUpdateFellowPayload DecodeFellowshipUpdateFellow(ReadOnlySpan<byte> body)
    {
        if (body.Length < 44 + 2 + 4) // 11 u32 prefix + min string16L len + updateType
            throw new InvalidOperationException("body too short for FellowshipUpdateFellow");
        var cursor = 0;
        var guid        = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        cursor += 4; // cpCached  (always 0)
        cursor += 4; // lumCached (always 0)
        var level       = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var healthMax   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var staminaMax  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var manaMax     = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var healthCur   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var staminaCur  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var manaCur     = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var shareLoot   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var name        = ReadString16L(body, ref cursor);
        var updateType  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        return new FellowshipUpdateFellowPayload(
            guid, level, healthMax, staminaMax, manaMax,
            healthCur, staminaCur, manaCur, shareLoot, name, updateType);
    }

    // FellowshipFullUpdate (0x02BE) — whole-fellowship snapshot. Wire layout from
    // the authoritative server writer GameEventFellowshipFullUpdate.cs (after the
    // 16B GameEvent envelope):
    //   PackableHashTable header: u16 count + u16 numBuckets
    //   foreach fellow (WriteFellow): u32 guid, u32 cpCached(0), u32 lumCached(0),
    //     u32 level, u32 health/stamina/mana max, u32 health/stamina/mana cur,
    //     u32 shareLoot (fixed 0x10 stub), string16L name
    //     — note: NO trailing updateType, unlike the incremental UpdateFellow.
    //   string16L fellowshipName
    //   u32 leaderGuid
    //   u32 shareXp  u32 evenShare  u32 open  u32 isLocked   (each a bool as u32)
    //   [trailing: DepartedMembers PHT + FellowshipLocks PHT]
    // The trailing departed/lock tables are intentionally left unread — every
    // field the bot needs (members, name, leader, flags) precedes them on the
    // wire, so decoding stops after the four flags. PackableHashTable.WriteHeader
    // writes count first, numBuckets second (both u16); we read count and skip
    // the bucket count, matching the PropertyInt32/Int64 readers above.
    private static FellowshipFullUpdatePayload DecodeFellowshipFullUpdate(ReadOnlySpan<byte> body)
    {
        var cursor = 0;
        if (body.Length - cursor < 4)
            throw new InvalidOperationException("body too short for FellowshipFullUpdate header");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 4; // u16 count + u16 numBuckets
        // Each fellow is >= 48B (11 u32 = 44B + a >=4B string16L), so a count
        // larger than the remaining body can hold is corrupt — reject before
        // allocating. Body-length-derived bound; no game knowledge about caps.
        if (count > (body.Length - cursor) / 48)
            throw new InvalidOperationException($"FellowshipFullUpdate fellow count absurd: {count}");
        var members = new List<FellowMember>(count);
        for (var i = 0; i < count; i++)
        {
            var fGuid       = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            cursor += 4; // cpCached  (always 0)
            cursor += 4; // lumCached (always 0)
            var fLevel      = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            var fHealthMax  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            var fStaminaMax = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            var fManaMax    = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            var fHealthCur  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            var fStaminaCur = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            var fManaCur    = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
            cursor += 4; // shareLoot (fixed 0x10 server stub)
            var fName       = ReadString16L(body, ref cursor);
            members.Add(new FellowMember(fGuid, fLevel, fHealthMax, fStaminaMax, fManaMax,
                fHealthCur, fStaminaCur, fManaCur, fName));
        }
        var fellowshipName = ReadString16L(body, ref cursor);
        var leaderGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)); cursor += 4;
        var shareXp    = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)) != 0; cursor += 4;
        var evenShare  = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)) != 0; cursor += 4;
        var open       = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)) != 0; cursor += 4;
        var isLocked   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)) != 0;
        return new FellowshipFullUpdatePayload(
            members, fellowshipName, leaderGuid, shareXp, evenShare, open, isLocked);
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

    private static WieldObjectPayload DecodeWieldObject(ReadOnlySpan<byte> body)
    {
        const int FixedSize = 8;
        if (body.Length < FixedSize)
            throw new InvalidOperationException($"body too short for WieldObject: need {FixedSize}, got {body.Length}");
        return new WieldObjectPayload(
            ItemGuid:    BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            NewLocation: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)));
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

    private static PopupStringPayload DecodePopupString(ReadOnlySpan<byte> body)
    {
        // Mirrors GameEventPopupString.cs: single string16L message.
        // Used by NPCs to display popup dialog windows (quest text,
        // tutorial instructions, etc.) — the academy training NPCs
        // emit these on USE to teach the player about gear, combat,
        // and the next training station to visit.
        if (body.Length < 2)
            throw new InvalidOperationException("body too short for PopupString");
        var cursor = 0;
        var message = ReadString16L(body, ref cursor);
        return new PopupStringPayload(message);
    }

    private static BookDataResponsePayload DecodeBookDataResponse(ReadOnlySpan<byte> body)
    {
        // Mirrors GameEventBookDataResponse.cs (Book.ActOnUse and
        // Player_Book.ReadBook are the two server-side senders).
        // Wire layout:
        //   u32  bookId
        //   i32  maxNumPages
        //   i32  numPages
        //   i32  maxNumCharsPerPage
        //   i32  pageCount
        //   pageCount * {
        //       u32   authorId
        //       str16 authorName
        //       str16 authorAccount   (always "beer good" for non-admin)
        //       u32   flags           (always 0xFFFF0002 per server)
        //       i32   textIncluded    (1 = page text follows)
        //       i32   ignoreAuthor
        //       (if textIncluded) str16 pageText
        //   }
        //   str16 inscription
        //   u32   authorId (final; 0 if 0xFFFFFFFF on server side)
        //   str16 authorName (final)
        //
        // Note: the initial BookDataResponse sent on ActOnUse has
        // PageText == null for every page so textIncluded will be 0
        // and the client must request each page via GameActionBook-
        // PageData. The decoder handles both cases.
        if (body.Length < 20) // 4 + 4 + 4 + 4 + 4
            throw new InvalidOperationException("body too short for BookDataResponse");
        var cursor = 0;
        var bookId = ReadU32(body, ref cursor);
        var maxNumPages = (int)ReadU32(body, ref cursor);
        var numPages = (int)ReadU32(body, ref cursor);
        var maxNumChars = (int)ReadU32(body, ref cursor);
        var pageCount = (int)ReadU32(body, ref cursor);
        var pages = new List<BookPageSummary>(Math.Max(0, pageCount));
        for (int i = 0; i < pageCount; i++)
        {
            var authorId = ReadU32(body, ref cursor);
            var authorName = ReadString16L(body, ref cursor);
            var authorAccount = ReadString16L(body, ref cursor);
            var flags = ReadU32(body, ref cursor);
            var textIncluded = ReadU32(body, ref cursor) != 0;
            var ignoreAuthor = ReadU32(body, ref cursor) != 0;
            string? pageText = null;
            if (textIncluded)
                pageText = ReadString16L(body, ref cursor);
            pages.Add(new BookPageSummary(authorId, authorName, authorAccount, flags, textIncluded, ignoreAuthor, pageText));
        }
        var inscription = ReadString16L(body, ref cursor);
        var finalAuthorId = ReadU32(body, ref cursor);
        var finalAuthorName = ReadString16L(body, ref cursor);
        return new BookDataResponsePayload(bookId, maxNumPages, numPages, maxNumChars, pages, inscription, finalAuthorId, finalAuthorName);
    }

    private static BookPageDataResponsePayload DecodeBookPageDataResponse(ReadOnlySpan<byte> body)
    {
        // Mirrors GameEventBookPageDataResponse.cs. Wire layout:
        //   u32  bookId
        //   i32  pageIndex
        //   u32  authorId
        //   str16 authorName
        //   str16 authorAccount  ("Password is cheese" for non-admin)
        //   u32  flags           (always 0xFFFF0002)
        //   i32  textIncluded    (always 1 per server)
        //   i32  ignoreAuthor
        //   str16 pageText
        if (body.Length < 16)
            throw new InvalidOperationException("body too short for BookPageDataResponse");
        var cursor = 0;
        var bookId = ReadU32(body, ref cursor);
        var pageIndex = (int)ReadU32(body, ref cursor);
        var authorId = ReadU32(body, ref cursor);
        var authorName = ReadString16L(body, ref cursor);
        var authorAccount = ReadString16L(body, ref cursor);
        var flags = ReadU32(body, ref cursor);
        var textIncluded = ReadU32(body, ref cursor) != 0;
        var ignoreAuthor = ReadU32(body, ref cursor) != 0;
        var pageText = ReadString16L(body, ref cursor);
        return new BookPageDataResponsePayload(bookId, pageIndex, authorId, authorName, authorAccount, flags, textIncluded, ignoreAuthor, pageText);
    }

    private static uint ReadU32(ReadOnlySpan<byte> body, ref int cursor)
    {
        if (cursor + 4 > body.Length)
            throw new InvalidOperationException($"u32 read OOB at cursor={cursor} len={body.Length}");
        var v = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        cursor += 4;
        return v;
    }

    private static UpdateHealthPayload DecodeUpdateHealth(ReadOnlySpan<byte> body)
    {
        // GameEventUpdateHealth (0x01C0):
        //   u32 objectId
        //   f32 healthFraction  ([0.0, 1.0])
        if (body.Length < 8)
            throw new InvalidOperationException("body too short for UpdateHealth");
        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        var health   = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(4, 4));
        return new UpdateHealthPayload(objectId, health);
    }

    private static AttackDonePayload DecodeAttackDone(ReadOnlySpan<byte> body)
    {
        // GameEventAttackDone (0x01A7):
        //   u32 errorCode  (WeenieError; 0 = success)
        if (body.Length < 4)
            throw new InvalidOperationException("body too short for AttackDone");
        return new AttackDonePayload(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
    }

    private static AttackerNotificationPayload DecodeAttackerNotification(ReadOnlySpan<byte> body)
    {
        // GameEventAttackerNotification (0x01B1) — sent to the attacker
        // when a swing LANDS. Mirrors GameEventAttackerNotification.cs:
        //   string16L defenderName  (DWORD-aligned)
        //   u32       damageType
        //   f64       percent
        //   u32       damage
        //   u32       criticalHit
        //   u64       attackConditions
        // We only need the name + damage; the rest is read past for
        // bounds safety but otherwise ignored.
        var cursor = 0;
        var defenderName = ReadString16L(body, ref cursor);
        if (body.Length - cursor < 4 + 8 + 4) // damageType + percent + damage
            throw new InvalidOperationException("body too short for AttackerNotification");
        cursor += 4;                                  // skip damageType
        cursor += 8;                                  // skip percent (f64)
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        return new AttackerNotificationPayload(defenderName, damage);
    }

    private static EvasionAttackerNotificationPayload DecodeEvasionAttackerNotification(ReadOnlySpan<byte> body)
    {
        // GameEventEvasionAttackerNotification (0x01B3) — sent to the
        // attacker when a swing is EVADED. Mirrors
        // GameEventEvasionAttackerNotification.cs: a single string16L
        // with the defender's name.
        var cursor = 0;
        var defenderName = ReadString16L(body, ref cursor);
        return new EvasionAttackerNotificationPayload(defenderName);
    }

    private static DefenderNotificationPayload DecodeDefenderNotification(ReadOnlySpan<byte> body)
    {
        // GameEventDefenderNotification (0x01B2) — sent to the DEFENDER
        // (the bot) when an attacker's swing LANDS. Mirrors
        // GameEventDefenderNotification.cs:
        //   string16L attackerName  (DWORD-aligned)
        //   u32       damageType
        //   f64       percent
        //   u32       damage
        //   u32       damageLocation
        //   u32       criticalHit
        //   u64       attackConditions
        // We only need the attacker name + damage; the remaining fields
        // are read past for bounds safety but otherwise ignored.
        var cursor = 0;
        var attackerName = ReadString16L(body, ref cursor);
        if (body.Length - cursor < 4 + 8 + 4) // damageType + percent + damage
            throw new InvalidOperationException("body too short for DefenderNotification");
        cursor += 4;                                  // skip damageType
        cursor += 8;                                  // skip percent (f64)
        var damage = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
        return new DefenderNotificationPayload(attackerName, damage);
    }

    private static EvasionDefenderNotificationPayload DecodeEvasionDefenderNotification(ReadOnlySpan<byte> body)
    {
        // GameEventEvasionDefenderNotification (0x01B4) — sent to the
        // DEFENDER (the bot) when it EVADES an attacker's swing. Mirrors
        // GameEventEvasionDefenderNotification.cs: a single string16L
        // with the attacker's name.
        var cursor = 0;
        var attackerName = ReadString16L(body, ref cursor);
        return new EvasionDefenderNotificationPayload(attackerName);
    }

    /// <summary>
    /// Reads a string16L: u16 length, then `length` bytes decoded as
    /// CP1252/Latin-1 (one byte per char), then padding so the cursor
    /// (relative to body start) lands on the next 4-byte boundary. The
    /// server writes these bytes with Encoding.GetEncoding(1252)
    /// (ACE.Server Extensions.WriteString16L); we decode with
    /// <see cref="Encoding.Latin1"/> — built-in (no CodePages provider
    /// needed, so it works in test hosts) and byte-identical to CP1252
    /// for the printable range AC names/messages use. UTF-8 would mangle
    /// any byte >= 0x80 (e.g. 0xE9 'é' -> replacement char). Matches the
    /// sibling reader AcStrings.ReadString16L. The GameEvent body begins
    /// at a 4-byte-aligned offset in the stream (the 16B envelope is
    /// itself 4-aligned), so body-relative mod-4 alignment is equivalent
    /// to BinaryWriter.Align()'s stream-relative behaviour.
    /// </summary>
    private static string ReadString16L(ReadOnlySpan<byte> body, ref int cursor)
    {
        if (body.Length - cursor < 2)
            throw new InvalidOperationException("string16L: not enough bytes for length");
        var len = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
        cursor += 2;
        if (body.Length - cursor < len)
            throw new InvalidOperationException($"string16L: declared {len} bytes, only {body.Length - cursor} remaining");
        var str = Encoding.Latin1.GetString(body.Slice(cursor, len));
        cursor += len;
        var pad = (4 - (cursor % 4)) % 4;
        cursor += pad;
        return str;
    }
}
