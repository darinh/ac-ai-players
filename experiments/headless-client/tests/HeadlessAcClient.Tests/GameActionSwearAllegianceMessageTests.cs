// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the swear-allegiance GameAction:
//
//   SwearAllegiance (0x001D) — swear allegiance to a patron, becoming their vassal.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x001D)
//
// then the payload the server handler reads (ACE-bots Source/ACE.Server/Network/
// GameAction/Actions/GameActionAllegianceSwearAllegiance.cs — message.Payload.ReadUInt32
// -> Player.HandleActionSwearAllegiance(targetGuid)):
//   u32 targetGuid   (the player to swear allegiance to)
// A wrong byte here silently fails the swear.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionSwearAllegianceMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void Swear_PackedSize_Is16Bytes()
    {
        Assert.Equal(16, GameActionSwearAllegianceMessage.PackedSize);
    }

    [Fact]
    public void Swear_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionSwearAllegianceMessage.PackedSize];

        var written = GameActionSwearAllegianceMessage.Pack(dest, targetGuid: 0x50000123u, actionSequence: 5u);

        Assert.Equal(GameActionSwearAllegianceMessage.PackedSize, written);

        var expected = new byte[16];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 5u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x001Du);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x50000123u);               c += 4; // targetGuid

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Swear_Pack_UsesOpcode001D()
    {
        var dest = new byte[GameActionSwearAllegianceMessage.PackedSize];
        GameActionSwearAllegianceMessage.Pack(dest, targetGuid: 0x50000001u);
        Assert.Equal(0x001Du, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void Swear_Pack_WritesTargetGuidAtOffset12()
    {
        var dest = new byte[GameActionSwearAllegianceMessage.PackedSize];
        GameActionSwearAllegianceMessage.Pack(dest, targetGuid: 0x5FABCDEFu);
        Assert.Equal(0x5FABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));
    }

    [Fact]
    public void Swear_DefaultsSequenceToOne()
    {
        var dest = new byte[GameActionSwearAllegianceMessage.PackedSize];
        GameActionSwearAllegianceMessage.Pack(dest, targetGuid: 0x50000001u);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));   // actionSequence
    }

    [Fact]
    public void Swear_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[15];
        Assert.Throws<ArgumentException>(() => GameActionSwearAllegianceMessage.Pack(tooSmall, targetGuid: 0x50000001u));
    }
}
