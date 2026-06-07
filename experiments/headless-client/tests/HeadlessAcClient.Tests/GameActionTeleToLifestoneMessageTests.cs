// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the lifestone-recall escape verb:
//
//   TeleToLifestone (0x0063) — recall to the attuned sanctuary.
//
// Standard GameAction wire prelude, and NOTHING after it:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0063)
//
// The server handler (ACE-bots Source/ACE.Server/Network/GameAction/
// Actions/GameActionTeleToLifestone.cs) calls
// session.Player.HandleActionTeleToLifestone() and reads NOTHING from the
// message body, so the payload is empty (the 12-byte header only).

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionTeleToLifestoneMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void TeleToLifestone_PackedSize_IsHeaderOnly_12Bytes()
    {
        Assert.Equal(12, GameActionTeleToLifestoneMessage.PackedSize);
    }

    [Fact]
    public void TeleToLifestone_Pack_WritesHeaderWithOpcode0063_NoBody()
    {
        var dest = new byte[GameActionTeleToLifestoneMessage.PackedSize];

        var written = GameActionTeleToLifestoneMessage.Pack(dest, actionSequence: 9u);

        Assert.Equal(GameActionTeleToLifestoneMessage.PackedSize, written);

        var expected = new byte[12];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 9u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0063u);                   c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void TeleToLifestone_DefaultActionSequence_IsOne()
    {
        var dest = new byte[GameActionTeleToLifestoneMessage.PackedSize];

        GameActionTeleToLifestoneMessage.Pack(dest);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));
        Assert.Equal(0x0063u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void TeleToLifestone_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[11];
        Assert.Throws<ArgumentException>(() =>
            GameActionTeleToLifestoneMessage.Pack(tooSmall));
    }
}
