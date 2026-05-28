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

namespace HeadlessAcClient.Protocol.GameMessages;

internal enum GameActionType : uint
{
    LoginComplete = 0x00A1,
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
