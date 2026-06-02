// SPDX-License-Identifier: AGPL-3.0-or-later
// Phase 3.3 outbound packers for the two-step world-entry handshake.
//
// Step 1: client -> server: CharacterEnterWorldRequest (0xF7C8)
//   Pure opcode-only message. Handler at
//   Source/ACE.Server/Network/Handlers/CharacterHandler.cs:184-196
//   does NOT read any payload - it just checks
//   ServerManager.ShutdownInProgress and replies with either
//   GameMessageCharacterEnterWorldServerReady (0xF7DF, empty body)
//   or GameMessageCharacterError (0xF659, LogonServerFull).
//
// Step 2: client -> server: CharacterEnterWorld (0xF657)
//   Carries the GUID of the character we want to enter the world
//   as, plus the session account name. Handler at
//   CharacterHandler.cs:198-263. Validates:
//     - session is not shutting down -> LogonServerFull
//     - clientString == session.Account -> EnterGameCharacterNotOwned
//     - character GUID exists in session.Characters -> EnterGameCharacterNotOwned
//     - character not deleted -> EnterGameCharacterNotOwned
//     - character not already in world -> EnterGameCharacterInWorld
//     - offline player record exists -> EnterGameGeneric
//     - Olthoi heritage check -> EnterGameCouldntPlaceCharacter
//   On success: session.State -> WorldConnected, WorldManager.PlayerEnterWorld.
//
// NOTE: the opcode 0xF657 is named "CharacterEnterWorld" (not
// "CharacterEnterWorldResponse" or similar). This is the C->S
// commit message, NOT the server's reply. The server's "you're in"
// confirmation arrives as world-state messages (PlayerCreate +
// PlayerDescription + landblock data), not a single ack message.

using System;
using System.Buffers.Binary;

namespace HeadlessAcClient.Protocol.GameMessages;

internal static class CharacterEnterWorldRequestMessage
{
    // CharacterEnterWorldRequest is opcode-only - 4 bytes total.
    public const int PackedSize = 4;

    public static int Pack(Span<byte> dest)
    {
        if (dest.Length < PackedSize)
            throw new ArgumentException($"buffer too small: need {PackedSize}, got {dest.Length}");

        BinaryPrimitives.WriteUInt32LittleEndian(
            dest,
            (uint)GameMessageOpcode.CharacterEnterWorldRequest);
        return PackedSize;
    }
}

internal static class CharacterEnterWorldMessage
{
    /// <summary>
    /// Pack: u32 opcode (0xF657) + u32 guid + string16L account.
    /// The String16L padding is included in the returned byte count.
    /// </summary>
    public static int Pack(Span<byte> dest, uint characterGuid, string account)
    {
        if (account is null) throw new ArgumentNullException(nameof(account));

        var cursor = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(
            dest.Slice(cursor),
            (uint)GameMessageOpcode.CharacterEnterWorld);
        cursor += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(cursor), characterGuid);
        cursor += 4;

        cursor += AcStrings.WriteString16L(dest.Slice(cursor), account);

        return cursor;
    }

    /// <summary>
    /// Compute the packed size without allocating.
    /// </summary>
    public static int MeasurePackedSize(string account)
    {
        return 4 + 4 + AcStrings.MeasureString16L(account);
    }
}
