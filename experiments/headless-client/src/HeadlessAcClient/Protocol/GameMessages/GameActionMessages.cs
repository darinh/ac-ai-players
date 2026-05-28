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
    LoginComplete      = 0x00A1,
    AutonomousPosition = 0xF753,
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
