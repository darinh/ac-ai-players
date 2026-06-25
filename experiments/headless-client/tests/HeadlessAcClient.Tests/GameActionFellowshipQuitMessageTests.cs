// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the fellowship-quit GameAction:
//
//   FellowshipQuit (0x00A3) — leave (or disband) the bot's fellowship.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x00A3)
//
// then the payload the server handler reads (ACE-bots Source/ACE.Server/Network/
// GameAction/Actions/GameActionFellowshipQuit.cs):
//   u32 disbandFellowship   (0 = just leave, nonzero = disband)
// A wrong byte here silently fails the quit.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionFellowshipQuitMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void Quit_PackedSize_Is16Bytes()
    {
        Assert.Equal(16, GameActionFellowshipQuitMessage.PackedSize);
    }

    [Fact]
    public void Quit_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionFellowshipQuitMessage.PackedSize];

        var written = GameActionFellowshipQuitMessage.Pack(dest, disband: false, actionSequence: 5u);

        Assert.Equal(GameActionFellowshipQuitMessage.PackedSize, written);

        var expected = new byte[16];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 5u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x00A3u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0u);                        c += 4; // disband=false

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Quit_Pack_UsesOpcode00A3()
    {
        var dest = new byte[GameActionFellowshipQuitMessage.PackedSize];
        GameActionFellowshipQuitMessage.Pack(dest);
        Assert.Equal(0x00A3u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 1u)]
    public void Quit_Pack_DisbandFlag(bool disband, uint expected)
    {
        var dest = new byte[GameActionFellowshipQuitMessage.PackedSize];
        GameActionFellowshipQuitMessage.Pack(dest, disband: disband);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));
    }

    [Fact]
    public void Quit_DefaultsSequenceToOneAndDisbandFalse()
    {
        var dest = new byte[GameActionFellowshipQuitMessage.PackedSize];
        GameActionFellowshipQuitMessage.Pack(dest);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));   // actionSequence
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));  // disband=false
    }

    [Fact]
    public void Quit_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[15];
        Assert.Throws<ArgumentException>(() => GameActionFellowshipQuitMessage.Pack(tooSmall));
    }
}
