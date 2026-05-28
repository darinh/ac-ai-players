// SPDX-License-Identifier: AGPL-3.0-or-later
// GameMessageDecoder — peek opcode then dispatch to per-message
// parser. Decoders return `null` on a malformed payload rather
// than throwing; the caller logs the raw bytes for analysis.
//
// All wire fields are little-endian. Strings use AC's String16L
// encoding (see Protocol/AcStrings.cs). The opcode is the first
// 4 bytes of the fragment payload (u32 little-endian) — see
// GameMessage.cs:25-27.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace HeadlessAcClient.Protocol.GameMessages;

internal static class GameMessageDecoder
{
    /// <summary>
    /// Peek the first 4 bytes of the fragment payload as the
    /// game-message opcode. Returns <c>null</c> on short payloads.
    /// </summary>
    public static GameMessageOpcode? PeekOpcode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(uint)) return null;
        return (GameMessageOpcode)BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    /// <summary>
    /// Decode an opcode-bearing fragment payload. Returns one of
    /// the per-opcode records, or <c>null</c> for opcodes the
    /// spike does not yet handle.
    /// </summary>
    public static object? Decode(ReadOnlySpan<byte> payload)
    {
        var op = PeekOpcode(payload);
        if (op is null) return null;
        return op switch
        {
            GameMessageOpcode.CharacterList    => DecodeCharacterList(payload),
            GameMessageOpcode.ServerName       => DecodeServerName(payload),
            GameMessageOpcode.DDDInterrogation => DecodeDDDInterrogation(payload),
            GameMessageOpcode.CharacterCreateResponse => DecodeCharacterCreateResponse(payload),
            GameMessageOpcode.CharacterEnterWorldServerReady => new CharacterEnterWorldServerReadyMessage(),
            GameMessageOpcode.CharacterError => DecodeCharacterError(payload),
            GameMessageOpcode.PlayerCreate => DecodePlayerCreate(payload),
            GameMessageOpcode.ServerMessage => DecodeServerMessage(payload),
            GameMessageOpcode.ObjectCreate => ObjectCreateDecoder.Decode(payload),
            _ => null,
        };
    }

    private static PlayerCreateMessage? DecodePlayerCreate(ReadOnlySpan<byte> p)
    {
        try
        {
            // u32 opcode + u32 guid - 8 bytes total
            var guid = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(sizeof(uint)));
            return new PlayerCreateMessage(guid);
        }
        catch
        {
            return null;
        }
    }

    private static ServerMessageMessage? DecodeServerMessage(ReadOnlySpan<byte> p)
    {
        try
        {
            var cursor = sizeof(uint); // skip opcode
            var text = AcStrings.ReadString16L(p, ref cursor);
            var chatType = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(cursor));
            return new ServerMessageMessage(text, chatType);
        }
        catch
        {
            return null;
        }
    }

    private static CharacterErrorMessage? DecodeCharacterError(ReadOnlySpan<byte> p)
    {
        try
        {
            var errorCode = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(sizeof(uint)));
            return new CharacterErrorMessage(errorCode);
        }
        catch
        {
            return null;
        }
    }

    private static CharacterCreateResponseMessage? DecodeCharacterCreateResponse(ReadOnlySpan<byte> p)
    {
        try
        {
            var cursor = sizeof(uint); // skip opcode
            var response = (CharacterCreateResponse)BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor));
            cursor += 4;

            // Server only writes guid+name+trailing on Ok - see
            // GameMessageCharacterCreateResponse.cs:13-18. Eagerly
            // reading them on a failure would consume bytes that
            // belong to the next message in a batched fragment.
            if (response != CharacterCreateResponse.Ok)
                return new CharacterCreateResponseMessage(response, 0, "", 0);

            var guid = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var name = AcStrings.ReadString16L(p, ref cursor);
            var trailing = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            return new CharacterCreateResponseMessage(response, guid, name, trailing);
        }
        catch
        {
            return null;
        }
    }

    private static CharacterListMessage? DecodeCharacterList(ReadOnlySpan<byte> p)
    {
        try
        {
            var cursor = sizeof(uint); // skip opcode
            var unknown1 = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var count    = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;

            var chars = new List<CharacterEntry>((int)count);
            for (var i = 0; i < count; i++)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
                var name = AcStrings.ReadString16L(p, ref cursor);
                var deleteIn = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
                chars.Add(new CharacterEntry(id, name, deleteIn));
            }

            var unknown2  = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var slotCount = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var account   = AcStrings.ReadString16L(p, ref cursor);
            var turbine   = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var tod       = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;

            return new CharacterListMessage(unknown1, chars, unknown2, slotCount, account, turbine, tod);
        }
        catch
        {
            return null;
        }
    }

    private static ServerNameMessage? DecodeServerName(ReadOnlySpan<byte> p)
    {
        try
        {
            var cursor = sizeof(uint); // skip opcode
            var current = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var max     = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var name    = AcStrings.ReadString16L(p, ref cursor);
            return new ServerNameMessage(current, max, name);
        }
        catch
        {
            return null;
        }
    }

    private static DDDInterrogationMessage? DecodeDDDInterrogation(ReadOnlySpan<byte> p)
    {
        try
        {
            var cursor = sizeof(uint); // skip opcode
            var region = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var lang   = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var prod   = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            var langN  = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;

            // Sanity-cap the array length so a malformed payload can't
            // be coerced into a multi-GB allocation.
            if (langN > 64) return null;
            if (p.Length - cursor < langN * sizeof(uint)) return null;

            var langs = new uint[langN];
            for (var i = 0; i < langN; i++)
            {
                langs[i] = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(cursor)); cursor += 4;
            }
            return new DDDInterrogationMessage(region, lang, prod, langs);
        }
        catch
        {
            return null;
        }
    }
}
