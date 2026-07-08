// SPDX-License-Identifier: AGPL-3.0-or-later
//
// GameAction outbound message family. GameActions are the
// general-purpose client→server command channel after world entry,
// wrapped in the 0xF7B1 game-message envelope.
//
// Wire layout (after the 4-byte 0xF7B1 envelope opcode):
//   u32  actionSequence   (server-side TODO: verify; safe to use 1)
//   u32  actionOpcode     (GameActionType, e.g. 0x00A1 LoginComplete)
//   ...  payload          (varies per action; LoginComplete is empty)
//
// References:
//   Source/ACE.Server/Network/GameAction/GameActionPacket.cs:9-17
//     [GameMessage(GameMessageOpcode.GameAction, WorldConnected)]
//     reads u32 sequence + u32 opcode, dispatches to action handler.
//   Source/ACE.Server/Network/GameAction/GameActionType.cs:54
//     LoginComplete = 0x00A1
//   Source/ACE.Server/Network/GameAction/Actions/GameActionLoginComplete.cs
//     Handler calls session.Player.OnTeleportComplete() to clear the
//     Teleporting flag (purple-portal-haze state). Without this the
//     character stays "in-portal" forever and cannot be interacted
//     with by other players or NPCs.
//
// IMPORTANT: LoginComplete MUST be sent only after the server has
// fully bound session.Player. Sending too early NPEs the handler.
// The canonical signal is PlayerCreate (0xF746) for our own guid —
// once we see ourselves in the world-state firehose, the server-
// side player object exists and OnTeleportComplete is safe.

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum GameActionType : uint
{
    LoginComplete       = 0x00A1,
    PutItemInContainer  = 0x0019,
    UseWithTarget       = 0x0035,
    Use                 = 0x0036,
    MoveToState         = 0xF61C,
    AutonomousPosition  = 0xF753,
    TargetedMeleeAttack = 0x0008,
    TargetedMissileAttack = 0x000A,
    GetAndWieldItem     = 0x001A,
    RaiseVital          = 0x0044,
    RaiseAttribute      = 0x0045,
    RaiseSkill          = 0x0046,
    ChangeCombatMode    = 0x0053,
    QueryHealth         = 0x01BF,
    GiveObjectRequest   = 0x00CD,
    SetSingleCharacterOption = 0x0005,
    TeleToLifestone     = 0x0063,
    Buy                 = 0x005F,
    Sell                = 0x0060,
    FellowshipCreate    = 0x00A2,
    FellowshipQuit      = 0x00A3,
    FellowshipRecruit   = 0x00A5,
    SwearAllegiance     = 0x001D,
    BreakAllegiance     = 0x001E,
    Talk                = 0x0015,
    ChatChannel         = 0x0147,
}

/// <summary>
/// Subset of ACE's <c>CharacterOption</c> enum
/// (Source/ACE.Entity/Enum/CharacterOption.cs) that the headless
/// client toggles over the wire. The numeric value is the option id
/// the server reads from the SetSingleCharacterOption payload — NOT
/// the underlying CharacterOptions1/2 bit (the server maps id → bit
/// internally via SetCharacterOption).
/// </summary>
internal enum CharacterOption : uint
{
    /// <summary>
    /// When enabled, the server's melee loop auto-repeats swings
    /// (Player_Melee.cs:375 gates the next swing on
    /// GetCharacterOption(AutoRepeatAttacks)). A real AC client sets
    /// this so a single TargetedMeleeAttack keeps swinging at weapon
    /// cadence until the target dies or leaves range; without it the
    /// server does ONE swing then OnAttackDone(), forcing the client
    /// to re-issue every swing.
    /// </summary>
    AutoRepeatAttacks = 0x00,
}

/// <summary>
/// Mirror of ACE's RawMotionFlags
/// (Source/ACE.Server/Network/Enum/RawMotionFlags.cs).
/// The low 11 bits of <c>RawMotionState.PackedFlags</c> encode a
/// bitmask of which optional fields follow; the high 21 bits encode
/// <c>CommandListLength</c>. Fields are serialized in strict
/// numeric flag order: 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x40, 0x80,
/// 0x100, 0x200, 0x400.
/// </summary>
[Flags]
internal enum RawMotionFlags : uint
{
    None            = 0x000,
    CurrentHoldKey  = 0x001,
    CurrentStyle    = 0x002,
    ForwardCommand  = 0x004,
    ForwardHoldKey  = 0x008,
    ForwardSpeed    = 0x010,
    SideStepCommand = 0x020,
    SideStepHoldKey = 0x040,
    SideStepSpeed   = 0x080,
    TurnCommand     = 0x100,
    TurnHoldKey     = 0x200,
    TurnSpeed       = 0x400,
}

/// <summary>
/// Subset of <c>ACE.Entity.Enum.MotionStance</c> values the spike
/// uses. NonCombat is the default standing stance for an unequipped
/// player. Full enum at
/// <c>Source/ACE.Entity/Enum/MotionStance.cs</c>.
/// </summary>
internal enum MotionStance : uint
{
    Invalid   = 0x80000000,
    NonCombat = 0x8000003D,
}

/// <summary>
/// Subset of <c>ACE.Entity.Enum.MotionCommand</c> values the spike
/// uses. Full enum at
/// <c>Source/ACE.Entity/Enum/MotionCommand.cs</c>.
/// </summary>
internal enum MotionCommand : uint
{
    Invalid     = 0x00000000,
    Ready       = 0x41000003,
    WalkForward = 0x45000005,
    RunForward  = 0x44000007,
}

/// <summary>
/// Mirror of <c>ACE.Entity.Enum.HoldKey</c>.
/// </summary>
internal enum HoldKey : uint
{
    Invalid = 0x0,
    None    = 0x1,
    Run     = 0x2,
}

internal static class GameActionMessage
{
    public const int HeaderSize = 4 + 4 + 4;  // envelope + actionSeq + actionOpcode

    /// <summary>
    /// Pack a payload-less GameAction (e.g. LoginComplete). The
    /// returned byte count is exactly <see cref="HeaderSize"/>.
    /// </summary>
    public static int Pack(Span<byte> dest, GameActionType action, uint actionSequence = 1)
    {
        if (dest.Length < HeaderSize)
            throw new ArgumentException($"buffer too small: need {HeaderSize}, got {dest.Length}");

        var cursor = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(
            dest.Slice(cursor),
            (uint)GameMessageOpcode.GameAction);
        cursor += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), actionSequence);
        cursor += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), (uint)action);
        cursor += 4;

        return cursor;
    }

    /// <summary>
    /// Write a 32-byte Position structure (cell+xyz+quaternion).
    /// Matches the wire layout consumed by ACE's
    /// <c>Position(BinaryReader)</c> constructor: u32 cell, then
    /// three float xyz, then four float quaternion in
    /// <b>W, X, Y, Z</b> order (NOT System.Numerics's X,Y,Z,W).
    /// Returns 32 (bytes written).
    /// </summary>
    public static int WritePosition(
        Span<byte> dest, uint cellId, Vector3 pos, Quaternion rot)
    {
        if (dest.Length < 32)
            throw new ArgumentException($"buffer too small: need 32, got {dest.Length}");

        var cursor = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), cellId);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), pos.X);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), pos.Y);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), pos.Z);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.W);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.X);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.Y);
        cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), rot.Z);
        cursor += 4;
        return cursor;
    }
}

internal static class GameActionLoginCompleteMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize;  // 12 bytes

    /// <summary>
    /// Pack the LoginComplete GameAction. Server handler:
    /// Source/ACE.Server/Network/GameAction/Actions/GameActionLoginComplete.cs
    /// </summary>
    public static int Pack(Span<byte> dest)
        => GameActionMessage.Pack(dest, GameActionType.LoginComplete);
}

/// <summary>
/// TeleToLifestone (0x0063). The lifestone-recall escape verb: asks the
/// server to teleport the player to their attuned sanctuary (lifestone).
/// Server handler
/// <c>Source/ACE.Server/Network/GameAction/Actions/GameActionTeleToLifestone.cs</c>
/// calls <c>session.Player.HandleActionTeleToLifestone()</c> and reads
/// NOTHING from the message body — so the wire payload is empty (just the
/// standard 12-byte GameAction header). The server validates preconditions
/// itself (must have an attuned sanctuary; refused inside the training
/// academy, during a recent PvP timer, or while too busy) and, on success,
/// plays the LifestoneRecall animation then teleports the player. The motor
/// only sends the opcode when Strategy names a <see cref="Strategy.GoalKind.Recall"/>
/// goal; it makes NO decision about WHETHER to recall.
/// </summary>
internal static class GameActionTeleToLifestoneMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize;  // 12 bytes

    public static int Pack(Span<byte> dest, uint actionSequence = 1)
        => GameActionMessage.Pack(dest, GameActionType.TeleToLifestone, actionSequence);
}

/// <summary>
/// Use (0x0036). The single most useful interact opcode: sent by the
/// client when the user double-clicks a world object. Server handler
/// is <c>Source/ACE.Server/Network/GameAction/Actions/GameActionUseItem.cs</c>
/// which calls <c>session.Player.HandleActionUseItem(itemGuid)</c>.
/// HandleActionUseItem walks the player to the target if needed, then
/// invokes the target's ActivationResponse: items get picked up,
/// doors toggle, portals teleport, NPCs initiate dialog, etc.
///
/// Payload after the GameAction header is a single u32: the target
/// object's guid. No rotation, no position — the server resolves the
/// target from the in-world guid registry.
/// </summary>
internal static class GameActionUseMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.Use, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid);
        cursor += 4;
        return cursor;
    }
}

/// <summary>
/// UseWithTarget (0x0035). The two-object "use item on target"
/// interact opcode: sent by the real client when the user uses an
/// inventory item ON a world object (e.g. a key on a locked chest, a
/// lockpick on a lock, an ust on a salvageable item). Server handler
/// is <c>Source/ACE.Server/Network/GameAction/Actions/GameActionUseWithTarget.cs</c>:
/// <code>
///   uint sourceObjectGuid = message.Payload.ReadUInt32();
///   uint targetObjectGuid = message.Payload.ReadUInt32();
///   session.Player.HandleActionUseWithTarget(sourceObjectGuid, targetObjectGuid);
/// </code>
///
/// Payload after the 12-byte GameAction header, in strict order:
///   u32 sourceObjectGuid   (the item being applied — e.g. the key,
///                           from our inventory)
///   u32 targetObjectGuid   (the world object it is applied to —
///                           e.g. the locked chest)
/// = 20 bytes total.
///
/// NOTE: for a locked container this UNLOCKS the target (sets
/// IsLocked=false / broadcasts Locked=false) but does NOT open it; a
/// follow-up plain <see cref="GameActionUseMessage"/> on the now-unlocked
/// container is what opens it and reveals loot. The motor only
/// mechanically dispatches this opcode when the LLM emits a Use goal
/// carrying an inventory Item; it makes no decision about WHICH item or
/// target — that is the LLM's job.
/// </summary>
internal static class GameActionUseWithTargetMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 8;  // 20 bytes

    public static int Pack(Span<byte> dest, uint sourceGuid, uint targetGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.UseWithTarget, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), sourceGuid); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// Buy (0x005F). Purchases item(s) from a vendor whose trade panel is open. The
/// server handler (Source/ACE.Server/Network/GameAction/Actions/
/// GameActionBuyItems.cs) reads:
/// <code>
///   u32 vendorGuid
///   u32 numItems
///   for each item: i32 amount, u32 objectID   (objectID = the for-sale guid)
///   // an optional trailing u32 altCurrencyWcid is commented out server-side
/// </code>
/// then calls <c>Player.HandleActionBuyItem(vendorGuid, items)</c>, which
/// validates the bot is at the vendor and has the funds, charges the
/// GetSellCost (in coin or the vendor's alternate currency), and creates the
/// item(s) in the bot's pack. This packer sends a SINGLE-item buy
/// (numItems = 1). The motor only dispatches this when the LLM emits a Buy goal
/// naming a vendor item; it makes NO decision about WHAT or WHETHER to buy —
/// that is the Strategy layer's job.
///
/// Payload after the 12B GameAction header (16 bytes):
///   u32 vendorGuid
///   u32 numItems (= 1)
///   i32 amount
///   u32 objectID
/// </summary>
internal static class GameActionBuyMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 16;  // 28 bytes

    public static int Pack(Span<byte> dest, uint vendorGuid, uint itemGuid, int amount = 1, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.Buy, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), vendorGuid); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), 1u); cursor += 4;   // numItems
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(cursor), amount); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), itemGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// Sell (0x0060). Sells item(s) from the bot's OWN inventory to a vendor whose
/// trade panel is open. The server handler (Source/ACE.Server/Network/GameAction/
/// Actions/GameActionSellItems.cs) reads the SAME layout as Buy:
/// <code>
///   u32 vendorGuid
///   u32 numItems
///   for each item: i32 amount, u32 objectID   (objectID = the bot's inventory item guid)
///   // an optional trailing u32 altCurrencyWcid is commented out server-side
/// </code>
/// then calls <c>Player.HandleActionSellItem(vendorGuid, items)</c>, which credits
/// the bot with the item's sell value and removes it from the bot's pack. This
/// packer sends a SINGLE-item sell (numItems = 1). The motor only dispatches this
/// when the LLM emits a Sell goal naming an inventory item; it makes NO decision
/// about WHAT or WHETHER to sell — that is the Strategy layer's job.
///
/// Payload after the 12B GameAction header (16 bytes):
///   u32 vendorGuid
///   u32 numItems (= 1)
///   i32 amount
///   u32 objectID (the bot's inventory item guid)
/// </summary>
internal static class GameActionSellMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 16;  // 28 bytes

    public static int Pack(Span<byte> dest, uint vendorGuid, uint itemGuid, int amount = 1, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.Sell, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), vendorGuid); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), 1u); cursor += 4;   // numItems
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(cursor), amount); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), itemGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// FellowshipCreate (0x00A2): ask the server to create a fellowship led by the
/// bot. Mirrors ACE-bots GameActionFellowshipCreate.Handle, which reads, after
/// the GameAction header:
///   String16L fellowshipName   (u16 length + CP1252 bytes + pad to 4-byte align)
///   u32       shareXp          (0 = no XP sharing, nonzero = share)
/// On success the server broadcasts a FellowshipFullUpdate the client already
/// decodes into <see cref="HeadlessAcClient.World.FellowshipMembership"/>. The
/// LLM chose to form the fellowship; this only packs the wire bytes.
/// </summary>
internal static class GameActionFellowshipCreateMessage
{
    /// <summary>
    /// Clamp a (possibly LLM-authored) fellowship name to printable ASCII and a
    /// non-empty mechanical default. WriteString16L casts each char to one byte,
    /// so non-ASCII LLM punctuation (curly quotes, em dashes) would otherwise pack
    /// as the wrong byte; restricting to printable ASCII keeps the name legible.
    /// </summary>
    public static string SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Fellowship";
        Span<char> buf = stackalloc char[raw.Length];
        var n = 0;
        foreach (var ch in raw)
            if (ch >= 0x20 && ch <= 0x7E) buf[n++] = ch;
        var cleaned = new string(buf[..n]).Trim();
        return cleaned.Length == 0 ? "Fellowship" : cleaned;
    }

    /// <summary>Packed size for a given name (header + String16L + u32 shareXp).</summary>
    public static int MeasureSize(ReadOnlySpan<char> fellowshipName) =>
        GameActionMessage.HeaderSize + AcStrings.MeasureString16L(fellowshipName) + 4;

    public static int Pack(Span<byte> dest, ReadOnlySpan<char> fellowshipName, bool shareXp, uint actionSequence = 1)
    {
        var need = MeasureSize(fellowshipName);
        if (dest.Length < need)
            throw new ArgumentException($"buffer too small: need {need}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.FellowshipCreate, actionSequence);
        cursor += AcStrings.WriteString16L(dest.Slice(cursor), fellowshipName);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), shareXp ? 1u : 0u); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// FellowshipQuit (0x00A3): leave (or disband) the bot's current fellowship.
/// Mirrors ACE-bots GameActionFellowshipQuit.Handle, which reads, after the
/// GameAction header:
///   u32 disbandFellowship   (0 = just leave, nonzero = disband the whole group)
/// The LLM chose to leave; this only packs the wire bytes.
/// </summary>
internal static class GameActionFellowshipQuitMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, bool disband = false, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.FellowshipQuit, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), disband ? 1u : 0u); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// FellowshipRecruit (0x00A5): invite another player into the bot's fellowship.
/// Mirrors ACE-bots GameActionFellowshipRecruit.Handle, which reads, after the
/// GameAction header:
///   u32 newMemberGuid   (the player to recruit)
/// The LLM chose WHICH player to recruit; this only packs the wire bytes.
/// </summary>
internal static class GameActionFellowshipRecruitMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint newMemberGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.FellowshipRecruit, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), newMemberGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// SwearAllegiance (0x001D): swear allegiance to a target PATRON, making the bot
/// their vassal. Mirrors ACE-bots GameActionAllegianceSwearAllegiance.Handle,
/// which reads, after the GameAction header:
///   u32 targetGuid   (the player to swear allegiance to)
/// then calls Player.HandleActionSwearAllegiance(targetGuid). The LLM chose WHICH
/// player to swear to; this only packs the wire bytes. Same shape as
/// FellowshipRecruit (a single player-guid payload).
/// </summary>
internal static class GameActionSwearAllegianceMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.SwearAllegiance, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// BreakAllegiance (0x001E): sever the allegiance link with a target player.
/// Mirrors ACE-bots GameActionAllegianceBreakAllegiance.Handle, which reads,
/// after the GameAction header:
///   u32 targetGuid   (the player on the other end of the allegiance link)
/// then calls Player.HandleActionBreakAllegiance(targetGuid). The LLM chose WHICH
/// player; this only packs the wire bytes. Same shape as SwearAllegiance (a single
/// player-guid payload).
/// </summary>
internal static class GameActionBreakAllegianceMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.BreakAllegiance, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// Talk (0x0015): the local "say" — the player speaks a line ALOUD, heard by
/// nearby players/creatures as HearSpeech. Mirrors ACE-bots GameActionTalk.Handle,
/// which reads, after the GameAction header:
///   String16L message
/// and re-broadcasts it as local chat. The message TEXT is authored by the LLM
/// (this only packs the bytes). Note: the server treats a message beginning with
/// '@' as a slash/admin COMMAND, so <see cref="SanitizeMessage"/> strips a leading
/// '@' to keep a Say strictly chat, and clamps to printable ASCII + a length cap
/// (WriteString16L casts each char to one byte, so non-ASCII would pack wrong).
/// </summary>
internal static class GameActionTalkMessage
{
    /// <summary>AC caps a local-say line well under this; clamp to keep the packet bounded.</summary>
    public const int MaxMessageChars = 256;

    /// <summary>
    /// Clamp an LLM-authored say line to printable ASCII, strip a leading '@' (so it
    /// is never parsed as a server command), collapse to empty-safe, and cap length.
    /// Returns null when nothing sayable remains (caller should not send).
    /// </summary>
    public static string? SanitizeMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Strip leading whitespace and command-'@' markers FIRST, BEFORE the length
        // cap, so a line that merely starts with a run of them is not consumed by the
        // cap and wrongly dropped. The server parses a leading '@' as a slash command.
        var start = 0;
        while (start < raw.Length && (raw[start] == '@' || char.IsWhiteSpace(raw[start])))
            start++;
        if (start >= raw.Length) return null;

        var src = raw.AsSpan(start);
        Span<char> buf = stackalloc char[Math.Min(src.Length, MaxMessageChars)];
        var n = 0;
        foreach (var ch in src)
        {
            if (n >= MaxMessageChars) break;
            if (ch >= 0x20 && ch <= 0x7E) buf[n++] = ch;
        }
        var cleaned = new string(buf[..n]).Trim();
        // A '@' can still be leading if a dropped non-ASCII char preceded it above;
        // strip once more so the sanitized line is never parsed as a command.
        while (cleaned.StartsWith("@", StringComparison.Ordinal))
            cleaned = cleaned[1..].TrimStart();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Packed size for a given (already-sanitized) message: header + String16L.</summary>
    public static int MeasureSize(ReadOnlySpan<char> message) =>
        GameActionMessage.HeaderSize + AcStrings.MeasureString16L(message);

    public static int Pack(Span<byte> dest, ReadOnlySpan<char> message, uint actionSequence = 1)
    {
        var need = MeasureSize(message);
        if (dest.Length < need)
            throw new ArgumentException($"buffer too small: need {need}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.Talk, actionSequence);
        cursor += AcStrings.WriteString16L(dest.Slice(cursor), message);
        return cursor;
    }
}

/// <summary>
/// ChatChannel (0x0147): say a line on a named group chat CHANNEL (fellowship,
/// allegiance, etc.) rather than aloud locally. Mirrors ACE-bots
/// GameActionChatChannel.Handle, which reads, after the GameAction header:
///   u32 channel   (ACE.Entity.Enum.Channel bitmask value)
///   String16L message
/// and broadcasts the line to that channel's members (the server rejects it if
/// the bot is not in the relevant fellowship/allegiance or lacks permission).
/// The message TEXT is authored by the LLM; this only packs the bytes.
/// </summary>
internal static class GameActionChatChannelMessage
{
    // Group channels a bot coordinates on. Wire values are ACE.Entity.Enum.Channel
    // members (verified against ACE-bots). Only PERMISSION-FREE channels are mapped —
    // ones any member of the relevant group can use without a server-side rank:
    //   Fellow   (@f)  — any fellowship member.
    //   Monarch (/monarch) — a vassal messages its monarch (up the tree).
    //   Vassals (/vassals) — a monarch/patron messages its vassals (down the tree).
    // The whole-allegiance broadcast (AllegianceBroadcast) is deliberately NOT mapped:
    // it needs a server Speaker rank, so a plain vassal would be silently muted.
    public const uint FellowChannel  = 0x00000800;  // Channel.Fellow
    public const uint MonarchChannel = 0x00004000;  // Channel.Monarch
    public const uint VassalsChannel = 0x00001000;  // Channel.Vassals

    /// <summary>
    /// Map an LLM-supplied channel NAME to its wire Channel value, or null when the
    /// name is unknown/blank. Only the permission-free group channels are mapped; the
    /// motor invents no channel. Callers must treat a non-blank name that returns null
    /// as an INVALID request (not a local-say downgrade) so group-intended text never
    /// leaks to local chat.
    /// </summary>
    public static uint? ResolveChannel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant() switch
        {
            "fellowship" or "fellow" => FellowChannel,
            "monarch" => MonarchChannel,
            "vassals" or "vassal" => VassalsChannel,
            _ => null,
        };
    }

    /// <summary>Packed size for a given (already-sanitized) message: header + u32 channel + String16L.</summary>
    public static int MeasureSize(ReadOnlySpan<char> message) =>
        GameActionMessage.HeaderSize + 4 + AcStrings.MeasureString16L(message);

    public static int Pack(Span<byte> dest, uint channel, ReadOnlySpan<char> message, uint actionSequence = 1)
    {
        var need = MeasureSize(message);
        if (dest.Length < need)
            throw new ArgumentException($"buffer too small: need {need}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.ChatChannel, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), channel); cursor += 4;
        cursor += AcStrings.WriteString16L(dest.Slice(cursor), message);
        return cursor;
    }
}

/// <summary>
/// RaiseAttribute (0x0045). Spends accumulated experience to raise one of
/// the six primary attributes. The server handler
/// (GameActionRaiseAttribute.cs) reads u32 attributeId then u32 xpSpent and
/// calls Player.HandleActionRaiseAttribute, which accepts ANY
/// amount &lt;= AvailableExperience (it accumulates ExperienceSpent and
/// recomputes the rank — there is no exact per-rank cost), so the client may
/// spend any clamped chunk. On success the server emits a private attribute
/// update (and, for Endurance, a Health vital update — perceivable as a
/// higher max HP); on failure it sends a "Your attempt to raise X has
/// failed." chat and no update.
///
/// attributeId is the raw PropertyAttribute wire enum (Strength=1,
/// Endurance=2, Quickness=3, Coordination=4, Focus=5, Self=6). The motor
/// only maps the LLM-named attribute to this id and sends the chunk the LLM
/// asked for; it makes NO decision about WHICH attribute or HOW MUCH — that
/// is the Strategy layer's job. No game-content knowledge (e.g. an
/// attribute's in-game effect) lives here.
///
/// Payload after the 12B GameAction header:
///   u32 attributeId
///   u32 xpSpent
/// </summary>
internal static class GameActionRaiseAttributeMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 8;  // 20 bytes

    public static int Pack(Span<byte> dest, uint attributeId, uint xpSpent, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.RaiseAttribute, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), attributeId); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), xpSpent); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// RaiseVital (0x0044). Spends accumulated experience to raise one of the
/// three secondary-attribute (vital) maximums. The server handler
/// (GameActionRaiseVital.cs) reads u32 vitalId then u32 xpSpent and calls
/// Player.HandleActionRaiseVital, which accepts ANY amount &lt;=
/// AvailableExperience (it accumulates the spend and recomputes the rank —
/// there is no exact per-rank cost), so the client may spend any clamped
/// chunk. On success the server emits a private vital update (perceivable as
/// a higher max Health/Stamina/Mana pool); on failure it sends a "Your
/// attempt to raise X has failed." chat and no update.
///
/// vitalId is the raw PropertyAttribute2nd wire enum for the raisable MAX
/// pools (MaxHealth=1, MaxStamina=3, MaxMana=5). The motor only maps the
/// LLM-named vital to this id and sends the chunk the LLM asked for; it makes
/// NO decision about WHICH vital or HOW MUCH — that is the Strategy layer's
/// job. No game-content knowledge (e.g. a vital's in-game effect) lives here.
///
/// Payload after the 12B GameAction header:
///   u32 vitalId
///   u32 xpSpent
/// </summary>
internal static class GameActionRaiseVitalMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 8;  // 20 bytes

    public static int Pack(Span<byte> dest, uint vitalId, uint xpSpent, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.RaiseVital, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), vitalId); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), xpSpent); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// RaiseSkill (0x0046). Spends accumulated experience to raise one trained
/// skill. The server handler (GameActionRaiseSkill.cs) reads u32 skillId then
/// u32 xpSpent and calls Player.HandleActionRaiseSkill, which accepts a spend
/// against a trained/specialized skill (it accumulates the spend and
/// recomputes the rank — there is no exact per-rank cost), so the client may
/// spend any clamped chunk. On success the server emits a private skill
/// update; on failure (untrained / retired / unimplemented skill, or amount
/// &gt; AvailableExperience) it sends a "Your attempt to raise X has failed."
/// chat and no update.
///
/// skillId is the raw <see cref="Skill"/> wire ordinal. The motor only maps
/// the LLM-named skill to this id and sends the chunk the LLM asked for; it
/// makes NO decision about WHICH skill or HOW MUCH, and does NOT pre-judge
/// whether the skill is trained — that is the Strategy layer's job and the
/// server's validation. No game-content knowledge (e.g. a skill's effect or
/// which skills are "good") lives here.
///
/// Payload after the 12B GameAction header:
///   u32 skillId
///   u32 xpSpent
/// </summary>
internal static class GameActionRaiseSkillMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 8;  // 20 bytes

    public static int Pack(Span<byte> dest, uint skillId, uint xpSpent, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.RaiseSkill, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), skillId); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), xpSpent); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// PutItemInContainer (0x0019). The pickup / move-between-containers
/// network event. Real clients emit this when the user drags an item
/// from the world (or another container) into a container; passing
/// the player's own guid as the container picks it up into the main
/// inventory.
///
/// Server handler at
/// <c>Source/ACE.Server/Network/GameAction/Actions/GameActionPutItemInContainer.cs</c>
/// calls <c>session.Player.HandleActionPutItemInContainer(itemGuid,
/// containerGuid, placement)</c>. That method enqueues a server-side
/// MoveTo chain to walk the player into UseRadius of the item, then
/// performs the pickup callback (broadcasts ObjectDelete on the item,
/// adds it to the player's inventory, emits Sound.PickUpItem).
///
/// Payload after the 12B GameAction header:
///   u32 itemGuid
///   u32 containerGuid
///   i32 placement       (0 = first available slot)
/// = 24 bytes total.
/// </summary>
internal static class GameActionPutItemInContainerMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 12;  // 24 bytes

    public static int Pack(Span<byte> dest, uint itemGuid, uint containerGuid, int placement = 0, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.PutItemInContainer, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), itemGuid);     cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), containerGuid); cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian (dest.Slice(cursor), placement);     cursor += 4;
        return cursor;
    }
}

/// <summary>
/// GiveObjectRequest (0x00CD). Sent client→server to hand an
/// inventory item to a target Container (NPC or another player).
/// The server validates ownership, runs CreateMoveToChain to walk
/// the player to the target, then either dispatches GiveObjectToNPC
/// (which fires the NPC's category-6 Give emote chain for the item's
/// wcid) or GiveObjectToPlayer.
///
/// Wire payload (after the 12-byte GameAction header):
///   u32 targetGuid       (recipient — NPC's guid)
///   u32 objectGuid       (item being given — from our inventory)
///   i32 amount           (stack count, normally 1)
///
/// Server handler:
///   Source/ACE.Server/Network/GameAction/Actions/GameActionGiveObjectRequest.cs
///   Source/ACE.Server/WorldObjects/Player_Inventory.cs:3190
///     HandleActionGiveObjectRequest → CreateMoveToChain → GiveObjectToNPC
///
/// Key academy use-case: GIVE Academy Exit Token (wcid 29335) to
/// Jonathan (wcid 29324) triggers his emote chain:
///   cat=6 Give wcid=29335 → Goto pick_coat_color (auto-picks via
///   weighted GotoSet) → Goto finalize_exit → InqBoolStat
///   RecallsDisabled → TestSuccess RecallsDisabled → CastSpellInstant
///   spell_Id=3815 (recall to fresh sanctuary at landblock 0xA9B4
///   cell 0x0019 coord (84, 7.1, 94) = outdoor Holtburg).
/// </summary>
internal static class GameActionGiveObjectRequestMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 12;  // 24 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint itemGuid, int amount = 1, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.GiveObjectRequest, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), itemGuid);   cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian (dest.Slice(cursor), amount);     cursor += 4;
        return cursor;
    }
}

/// <summary>
/// ChangeCombatMode (0x0053). Switches the player between non-combat
/// idle stance and an active combat stance (Melee / Missile / Magic).
/// Required before any TargetedMeleeAttack / TargetedMissileAttack /
/// CreateSpell game action — the server's HandleAction* methods all
/// validate that <c>Player.CombatMode</c> matches the action.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionChangeCombatMode.cs</c>
///   reads u32 newMode and calls
///   <c>session.Player.HandleActionChangeCombatMode((CombatMode)newMode)</c>.
///
/// Payload after the 12B GameAction header:
///   u32 newCombatMode  (CombatMode enum:
///                       NonCombat=1, Melee=2, Missile=4, Magic=8)
/// = 16 bytes total.
///
/// On success the server broadcasts a Motion update flipping the
/// player into the new stance and emits SetState for nearby
/// observers. Subsequent attack actions become legal.
/// </summary>
internal static class GameActionChangeCombatModeMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint newCombatMode, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.ChangeCombatMode, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), newCombatMode); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// SetSingleCharacterOption (0x0005). Toggles one boolean character
/// option. A real AC client sends this when the user ticks an option
/// checkbox (e.g. "Auto Repeat Attacks").
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionSetSingleCharacterOption.cs</c>
///   reads u32 option + u32 value and (default case) calls
///   <c>session.Player.SetCharacterOption((CharacterOption)option, value != 0)</c>,
///   which maps the option id to its CharacterOptions1/2 bit.
///
/// Payload after the 12B GameAction header:
///   u32 option   (<see cref="CharacterOption"/>)
///   u32 value    (0 = off, non-zero = on)
/// = 20 bytes total.
/// </summary>
internal static class GameActionSetSingleCharacterOptionMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 8;  // 20 bytes

    public static int Pack(Span<byte> dest, CharacterOption option, bool value, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.SetSingleCharacterOption, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), (uint)option); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), value ? 1u : 0u); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// TargetedMeleeAttack (0x0008). Issues a melee strike against a
/// specific creature guid using the currently wielded weapon (or
/// unarmed strike). Requires the player to already be in
/// CombatMode.Melee — see <see cref="GameActionChangeCombatModeMessage"/>.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionTargetedMeleeAttack.cs</c>
///   reads u32 targetGuid + u32 attackHeight + f32 powerLevel and
///   calls <c>session.Player.HandleActionTargetedMeleeAttack(
///       targetGuid, attackHeight, powerLevel)</c>.
///
/// Payload after the 12B GameAction header:
///   u32 targetGuid
///   u32 attackHeight   (AttackHeight enum: High=1, Medium=2, Low=3)
///   f32 powerLevel     ([0.0, 1.0] — 0.5 is the "half power" default
///                       a real client sends for an unmodified click)
/// = 24 bytes total.
///
/// The server walks the player into UseRadius, plays the swing
/// animation, rolls hit/miss/damage based on the wielded weapon and
/// the target's defenses, and broadcasts UpdateHealth (the target's
/// PrivateUpdatePropertyInt for Health) plus various combat events.
/// </summary>
internal static class GameActionTargetedMeleeAttackMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 12;  // 24 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint attackHeight, float powerLevel, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.TargetedMeleeAttack, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid);    cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), attackHeight);  cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), powerLevel);    cursor += 4;
        return cursor;
    }
}

/// <summary>
/// TargetedMissileAttack (0x000A). Issues a missile strike (bow,
/// crossbow, atlatl) against a specific creature guid using the
/// currently wielded missile weapon. Requires the player to already be
/// in CombatMode.Missile — see <see cref="GameActionChangeCombatModeMessage"/>
/// — AND, for ammo launchers, to have ammo wielded in the MissileAmmo
/// slot (the server silently no-ops the attack otherwise; thrown
/// weapons need no ammo).
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionTargetedMissileAttack.cs</c>
///   reads u32 targetGuid + u32 attackHeight + f32 accuracyLevel and
///   calls <c>session.Player.HandleActionTargetedMissileAttack(
///       targetGuid, attackHeight, accuracyLevel)</c>. That method bails
///   if <c>CombatMode != CombatMode.Missile</c> or
///   <c>weapon == null || (weapon.IsAmmoLauncher &amp;&amp; ammo == null)</c>.
///
/// Payload after the 12B GameAction header (identical shape to the
/// melee action, only the opcode + the float's semantic differ):
///   u32 targetGuid
///   u32 attackHeight   (AttackHeight enum: High=1, Medium=2, Low=3)
///   f32 accuracyLevel  ([0.0, 1.0] — clamped server-side)
/// = 24 bytes total.
/// </summary>
internal static class GameActionTargetedMissileAttackMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 12;  // 24 bytes

    public static int Pack(Span<byte> dest, uint targetGuid, uint attackHeight, float accuracyLevel, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.TargetedMissileAttack, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), targetGuid);     cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), attackHeight);   cursor += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(cursor), accuracyLevel);  cursor += 4;
        return cursor;
    }
}

/// <summary>
/// GetAndWieldItem (0x001A). Equips an item that is currently in the
/// player's inventory (or picks it up from the world and equips it in
/// one combined action) into the specified equipment slot.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionGetAndWieldItem.cs</c>
///   reads u32 itemGuid + i32 location and calls
///   <c>session.Player.HandleActionGetAndWieldItem(itemGuid, location)</c>.
///
/// Payload after the 12B GameAction header:
///   u32 itemGuid
///   i32 equipLocation  (EquipMask enum, e.g. HeadWear=0x01,
///                       HandWear=0x20, ChestArmor=0x200,
///                       MeleeWeapon=0x100000, etc.)
/// = 20 bytes total.
///
/// The chosen location must be compatible with the item's
/// ValidLocations property OR the server rejects with a
/// WeenieErrorWithString. For unambiguously-slotted gear (gauntlets,
/// helmet, etc.) the location is typically a single bit; for
/// multi-slot items (rings, bracelets) the caller must pick one.
/// </summary>
internal static class GameActionGetAndWieldItemMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 8;  // 20 bytes

    public static int Pack(Span<byte> dest, uint itemGuid, int equipLocation, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.GetAndWieldItem, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), itemGuid);       cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian (dest.Slice(cursor), equipLocation);  cursor += 4;
        return cursor;
    }
}

/// <summary>
/// QueryHealth (0x01BF). Sent by a real client when the player
/// selects (left-clicks) a creature in the world — registers that
/// creature as the player's "appraisal / health-query target" so
/// the server emits ongoing UpdateHealth broadcasts as that
/// target's health changes.
///
/// Server handler:
///   <c>Source/ACE.Server/Network/GameAction/Actions/GameActionQueryHealth.cs</c>
///   reads u32 objectGuid and calls
///   <c>session.Player.HandleActionQueryHealth(objectGuid)</c>.
///   That method sets <c>selectedTarget</c> + <c>HealthQueryTarget</c>
///   on the Player AND immediately invokes
///   <c>creature.QueryHealth(session)</c> which emits one UpdateHealth
///   right away. Subsequent UpdateHealth broadcasts come from
///   <c>Player_Vitals.HandleTargetVitals()</c> (called every
///   Player_Tick) — those only fire while <c>selectedTarget != null</c>.
///
/// Payload after the 12B GameAction header:
///   u32 objectGuid
/// = 16 bytes total.
///
/// Sending QueryHealth before/with TargetedMeleeAttack is what makes
/// damage visible to a headless client. Without it the swings still
/// land (server-side) but the client sees zero UpdateHealth events
/// and can't tell when the target died.
/// </summary>
internal static class GameActionQueryHealthMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 4;  // 16 bytes

    public static int Pack(Span<byte> dest, uint objectGuid, uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(dest, GameActionType.QueryHealth, actionSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), objectGuid); cursor += 4;
        return cursor;
    }
}

/// <summary>
/// AutonomousPosition (0xF753). Sent by a real client roughly once
/// per second WHILE moving to report its current authoritative
/// position to the server. Server handler:
/// Source/ACE.Server/Network/GameAction/Actions/GameActionAutonomousPosition.cs
///
/// Server-side behavior (read carefully — drives our gating):
///   - Reads Position (32B), four ushort sequences, one byte
///     ContactLongJump, then aligns to dword.
///   - Calls session.Player.SetRequestedLocation(position) IFF
///     <c>!session.Player.Teleporting</c>. So we MUST wait until
///     the server has processed our prior LoginComplete (which
///     clears Teleporting) before this is observable. Sending
///     while Teleporting=true is a silent no-op.
///   - SetRequestedLocation defaults broadcast=true, so a
///     successful AP eventually emits a server-originated
///     UpdatePosition broadcast back to us. That's how we
///     confirm acceptance — observable inbound UpdatePosition
///     with our own guid.
///
/// Wire layout (after GameAction 12B header):
///   Position    32B    (u32 cell, 3 floats xyz, 4 floats wxyz)
///   InstanceSequence       u16 (2B)
///   ServerControlSequence  u16 (2B)
///   TeleportSequence       u16 (2B)
///   ForcePositionSequence  u16 (2B)
///   ContactLongJump        u8  (1B)
///   align to 4B            +3B padding
/// Total payload: 12 + 32 + 8 + 4 = 56 bytes.
///
/// IMPORTANT: the four ushort sequences are echoes of the latest
/// inbound values the server sent us for our own guid. Do not
/// advance them client-side; the server-side handler does not
/// validate but ACE's own server-side autonomous position
/// constructor mirrors the current ObjectInstance / ServerControl
/// / Teleport / ForcePosition counters.
/// </summary>
internal static class GameActionAutonomousPositionMessage
{
    public const int PackedSize = GameActionMessage.HeaderSize + 32 + 8 + 4;  // 56

    public static int Pack(
        Span<byte> dest,
        uint cellId, Vector3 pos, Quaternion rot,
        ushort instanceSequence,
        ushort serverControlSequence,
        ushort teleportSequence,
        ushort forcePositionSequence,
        bool contact,
        uint actionSequence = 1)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(
            dest, GameActionType.AutonomousPosition, actionSequence);

        cursor += GameActionMessage.WritePosition(dest.Slice(cursor), cellId, pos, rot);

        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), instanceSequence);
        cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), serverControlSequence);
        cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), teleportSequence);
        cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), forcePositionSequence);
        cursor += 2;

        dest[cursor++] = contact ? (byte)1 : (byte)0;

        // Align to 4-byte boundary. Cursor is currently at PackedSize - 3.
        dest[cursor++] = 0;
        dest[cursor++] = 0;
        dest[cursor++] = 0;

        return cursor;
    }
}

/// <summary>
/// Bag of optional <c>RawMotionState</c> fields. Each <c>(Flag, Value)</c>
/// pair is emitted IFF the flag bit is set; emitted in strict
/// numeric flag order regardless of how this struct is populated.
/// Speed fields are floats; all others are u32s.
/// </summary>
internal readonly record struct RawMotionStatePayload(
    RawMotionFlags Flags,
    HoldKey        CurrentHoldKey,
    MotionStance   CurrentStyle,
    MotionCommand  ForwardCommand,
    HoldKey        ForwardHoldKey,
    float          ForwardSpeed,
    MotionCommand  SidestepCommand,
    HoldKey        SidestepHoldKey,
    float          SidestepSpeed,
    MotionCommand  TurnCommand,
    HoldKey        TurnHoldKey,
    float          TurnSpeed,
    ushort         CommandListLength = 0)
{
    /// <summary>
    /// Convenience constructor for the most common case: forward
    /// motion with stance + holdkey + command + speed.
    /// </summary>
    public static RawMotionStatePayload ForwardMotion(
        HoldKey holdKey, MotionStance stance, MotionCommand command, float speed)
        => new(
            RawMotionFlags.CurrentHoldKey | RawMotionFlags.CurrentStyle |
            RawMotionFlags.ForwardCommand | RawMotionFlags.ForwardSpeed,
            CurrentHoldKey:  holdKey,
            CurrentStyle:    stance,
            ForwardCommand:  command,
            ForwardHoldKey:  default,
            ForwardSpeed:    speed,
            SidestepCommand: default,
            SidestepHoldKey: default,
            SidestepSpeed:   0f,
            TurnCommand:     default,
            TurnHoldKey:     default,
            TurnSpeed:       0f);

    /// <summary>
    /// "Stop" intent — explicitly cancel forward motion by sending
    /// the stance + a None holdkey + an Invalid forward command. A
    /// real client sends something close to this on key-release.
    /// </summary>
    public static RawMotionStatePayload Stop(MotionStance stance)
        => new(
            RawMotionFlags.CurrentHoldKey | RawMotionFlags.CurrentStyle |
            RawMotionFlags.ForwardCommand,
            CurrentHoldKey:  HoldKey.None,
            CurrentStyle:    stance,
            ForwardCommand:  MotionCommand.Invalid,
            ForwardHoldKey:  default,
            ForwardSpeed:    0f,
            SidestepCommand: default,
            SidestepHoldKey: default,
            SidestepSpeed:   0f,
            TurnCommand:     default,
            TurnHoldKey:     default,
            TurnSpeed:       0f);
}

/// <summary>
/// MoveToState (0xF61C). Sent by a real client on every movement
/// key press AND release to update the server's view of player
/// motion intent. Server handler:
/// Source/ACE.Server/Network/GameAction/Actions/GameActionMoveToState.cs
///
/// Important server-side caveat (per rubber-duck on Phase 5b):
///   - <c>Player.OnMoveToState</c> is gated by <c>Player.FastTick</c>,
///     which is FALSE for normal NPK players (returns true only
///     when <c>IsPKType</c>). So the server-side physics
///     integration that would actually MOVE a normal char in
///     response to one of these messages is short-circuited.
///   - <c>BroadcastMovement(moveToState)</c> is called
///     unconditionally though, so we DO see a Motion broadcast
///     for our own guid back at us proving wire-format acceptance.
///   - For continuous server-driven physics movement we would need
///     a PK char OR a different code path. Phase 5b's success
///     criterion is therefore "Motion broadcast observed", not
///     "UpdatePosition stream observed".
///
/// Wire layout (after the 12B GameAction header):
///   u32 packedFlags        (low 11 bits = RawMotionFlags,
///                           high 21 bits = CommandListLength)
///   ... flagged fields ... (u32 or float, in strict numeric flag
///                           order: 0x1 → 0x2 → 0x4 → ... → 0x400)
///   32B Position           (full cell + xyz + W,X,Y,Z quaternion)
///   8B sequences           (instance, serverControl, teleport, forcePos)
///   1B ContactLongJump     (bit 0 = Contact, bit 1 = StandingLongJump)
///   align to 4B boundary
/// </summary>
internal static class GameActionMoveToStateMessage
{
    /// <summary>
    /// Size of the fixed (non-RawMotionState) suffix: Position +
    /// sequences + ContactLongJump byte + 3B align.
    /// </summary>
    private const int FixedSuffixSize = 32 + 8 + 4;  // 44

    /// <summary>
    /// Total packed bytes for a given motion state. Does NOT include
    /// any MotionItems (CommandListLength is always 0 for the
    /// spike's needs).
    /// </summary>
    public static int CalcPackedSize(RawMotionFlags flags)
        => GameActionMessage.HeaderSize  // 12
         + 4                              // packedFlags
         + CountFlaggedFieldBytes(flags)
         + FixedSuffixSize;

    public static int Pack(
        Span<byte> dest,
        RawMotionStatePayload motion,
        uint cellId, Vector3 pos, Quaternion rot,
        ushort instanceSequence,
        ushort serverControlSequence,
        ushort teleportSequence,
        ushort forcePositionSequence,
        bool contact,
        bool standingLongJump = false,
        uint actionSequence = 1)
    {
        var needed = CalcPackedSize(motion.Flags);
        if (dest.Length < needed)
            throw new ArgumentException($"buffer too small: need {needed}, got {dest.Length}");

        var cursor = GameActionMessage.Pack(
            dest, GameActionType.MoveToState, actionSequence);

        // packedFlags: bottom 11 bits = flags, top 21 bits = commandListLength.
        // Per rubber-duck: mask to 0x7FF defensively even though the
        // RawMotionFlags enum's highest value is 0x400 — guards
        // against accidentally setting reserved bits.
        var packedFlags = ((uint)motion.Flags & 0x7FF) | ((uint)motion.CommandListLength << 11);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), packedFlags);
        cursor += 4;

        // Emit fields in strict numeric flag order. The server's
        // RawMotionState(BinaryReader) parser reads them in this
        // exact order; any other order will misparse.
        if ((motion.Flags & RawMotionFlags.CurrentHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.CurrentHoldKey);
        if ((motion.Flags & RawMotionFlags.CurrentStyle) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.CurrentStyle);
        if ((motion.Flags & RawMotionFlags.ForwardCommand) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.ForwardCommand);
        if ((motion.Flags & RawMotionFlags.ForwardHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.ForwardHoldKey);
        if ((motion.Flags & RawMotionFlags.ForwardSpeed) != 0)
            cursor += WriteF32(dest.Slice(cursor), motion.ForwardSpeed);
        if ((motion.Flags & RawMotionFlags.SideStepCommand) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.SidestepCommand);
        if ((motion.Flags & RawMotionFlags.SideStepHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.SidestepHoldKey);
        if ((motion.Flags & RawMotionFlags.SideStepSpeed) != 0)
            cursor += WriteF32(dest.Slice(cursor), motion.SidestepSpeed);
        if ((motion.Flags & RawMotionFlags.TurnCommand) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.TurnCommand);
        if ((motion.Flags & RawMotionFlags.TurnHoldKey) != 0)
            cursor += WriteU32(dest.Slice(cursor), (uint)motion.TurnHoldKey);
        if ((motion.Flags & RawMotionFlags.TurnSpeed) != 0)
            cursor += WriteF32(dest.Slice(cursor), motion.TurnSpeed);

        cursor += GameActionMessage.WritePosition(dest.Slice(cursor), cellId, pos, rot);

        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), instanceSequence);      cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), serverControlSequence); cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), teleportSequence);      cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(cursor), forcePositionSequence); cursor += 2;

        byte contactLongJump = 0;
        if (contact)           contactLongJump |= 0x1;
        if (standingLongJump)  contactLongJump |= 0x2;
        dest[cursor++] = contactLongJump;

        // Align to 4-byte boundary. After ContactLongJump cursor sits
        // 1 byte into the trailing dword, so 3 bytes of zero padding.
        dest[cursor++] = 0;
        dest[cursor++] = 0;
        dest[cursor++] = 0;

        return cursor;
    }

    private static int WriteU32(Span<byte> dest, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest, value);
        return 4;
    }

    private static int WriteF32(Span<byte> dest, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest, value);
        return 4;
    }

    private static int CountFlaggedFieldBytes(RawMotionFlags flags)
    {
        var count = 0;
        // Every flag emits 4 bytes (either u32 or float). 11 possible flags.
        for (var bit = 0; bit < 11; bit++)
        {
            if (((uint)flags & (1u << bit)) != 0)
                count += 4;
        }
        return count;
    }
}
