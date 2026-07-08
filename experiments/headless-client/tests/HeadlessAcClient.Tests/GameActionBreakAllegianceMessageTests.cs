// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the break-allegiance GameAction:
//
//   BreakAllegiance (0x001E) — sever the allegiance bond with a target player
//   (leave your patron, or boot a vassal).
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x001E)
//
// then the payload the server handler reads (ACE-bots Source/ACE.Server/Network/
// GameAction/Actions/GameActionAllegianceBreakAllegiance.cs — message.Payload.ReadUInt32
// -> Player.HandleActionBreakAllegiance(targetGuid)):
//   u32 targetGuid   (the other party in the allegiance link to sever)
// A wrong byte here silently fails the break.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionBreakAllegianceMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void Break_PackedSize_Is16Bytes()
    {
        Assert.Equal(16, GameActionBreakAllegianceMessage.PackedSize);
    }

    [Fact]
    public void Break_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionBreakAllegianceMessage.PackedSize];

        var written = GameActionBreakAllegianceMessage.Pack(dest, targetGuid: 0x50000123u, actionSequence: 5u);

        Assert.Equal(GameActionBreakAllegianceMessage.PackedSize, written);

        var expected = new byte[16];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 5u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x001Eu);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x50000123u);               c += 4; // targetGuid

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Break_Pack_UsesOpcode001E()
    {
        var dest = new byte[GameActionBreakAllegianceMessage.PackedSize];
        GameActionBreakAllegianceMessage.Pack(dest, targetGuid: 0x50000001u);
        Assert.Equal(0x001Eu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void Break_Pack_WritesTargetGuidAtOffset12()
    {
        var dest = new byte[GameActionBreakAllegianceMessage.PackedSize];
        GameActionBreakAllegianceMessage.Pack(dest, targetGuid: 0x5FABCDEFu);
        Assert.Equal(0x5FABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));
    }

    [Fact]
    public void Break_DefaultsSequenceToOne()
    {
        var dest = new byte[GameActionBreakAllegianceMessage.PackedSize];
        GameActionBreakAllegianceMessage.Pack(dest, targetGuid: 0x50000001u);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));   // actionSequence
    }

    [Fact]
    public void Break_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[15];
        Assert.Throws<ArgumentException>(() => GameActionBreakAllegianceMessage.Pack(tooSmall, targetGuid: 0x50000001u));
    }
}
