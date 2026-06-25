// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the fellowship-create GameAction:
//
//   FellowshipCreate (0x00A2) — form a fellowship led by the bot.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x00A2)
//
// then the payload the server handler reads (ACE-bots Source/ACE.Server/Network/
// GameAction/Actions/GameActionFellowshipCreate.cs):
//   String16L fellowshipName   (u16 length + CP1252 bytes + pad to 4-byte align)
//   u32       shareXp          (0 = no sharing, nonzero = share)
// A wrong byte here silently fails the create.

using System;
using System.Buffers.Binary;
using System.Text;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionFellowshipCreateMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    // Build the expected String16L segment INDEPENDENTLY of the production
    // writer: u16 length (LE) + ASCII bytes + zero pad to a 4-byte boundary.
    private static byte[] ExpectedString16L(string s)
    {
        var raw = 2 + s.Length;
        var padded = (raw + 3) & ~3;
        var buf = new byte[padded];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)s.Length);
        Encoding.ASCII.GetBytes(s).CopyTo(buf, 2);
        return buf;
    }

    [Fact]
    public void MeasureSize_HeaderPlusString16LPlusShareXp()
    {
        // "TestFellows" (11): String16L raw 2+11=13 -> aligned 16; total 12+16+4 = 32.
        var size = GameActionFellowshipCreateMessage.MeasureSize("TestFellows");
        Assert.Equal(12 + 16 + 4, size);
    }

    [Fact]
    public void Pack_WritesExpectedBytes()
    {
        const string name = "TestFellows";
        var dest = new byte[GameActionFellowshipCreateMessage.MeasureSize(name)];

        var written = GameActionFellowshipCreateMessage.Pack(
            dest, name, shareXp: true, actionSequence: 7u);

        Assert.Equal(dest.Length, written);

        var str16 = ExpectedString16L(name);
        var expected = new byte[12 + str16.Length + 4];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 7u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x00A2u);                   c += 4;
        str16.CopyTo(expected, c);                                                               c += str16.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 1u);                        c += 4; // shareXp=true

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Pack_UsesOpcode00A2()
    {
        var dest = new byte[GameActionFellowshipCreateMessage.MeasureSize("F")];
        GameActionFellowshipCreateMessage.Pack(dest, "F", shareXp: true);
        Assert.Equal(0x00A2u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void Pack_ShareXpFalse_WritesZero()
    {
        const string name = "Crew";
        var dest = new byte[GameActionFellowshipCreateMessage.MeasureSize(name)];
        GameActionFellowshipCreateMessage.Pack(dest, name, shareXp: false);
        // shareXp is the u32 right after the String16L name segment.
        var shareXpOffset = 12 + ExpectedString16L(name).Length;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(shareXpOffset)));
    }

    [Fact]
    public void Pack_NameLengthPrefix_MatchesName()
    {
        const string name = "Alpha Squad";
        var dest = new byte[GameActionFellowshipCreateMessage.MeasureSize(name)];
        GameActionFellowshipCreateMessage.Pack(dest, name, shareXp: true);
        // u16 length prefix at offset 12 (right after the 12-byte header).
        Assert.Equal((ushort)name.Length, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(12)));
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Assert.Equal(nameBytes, dest.AsSpan(14, name.Length).ToArray());
    }

    [Fact]
    public void Pack_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[GameActionFellowshipCreateMessage.MeasureSize("Crew") - 1];
        Assert.Throws<ArgumentException>(() =>
            GameActionFellowshipCreateMessage.Pack(tooSmall, "Crew", shareXp: true));
    }

    [Theory]
    [InlineData(0, 4)]   // raw 2 -> aligned 4 (pad 2)
    [InlineData(1, 4)]   // raw 3 -> aligned 4 (pad 1)
    [InlineData(2, 4)]   // raw 4 -> aligned 4 (pad 0)
    [InlineData(3, 8)]   // raw 5 -> aligned 8 (pad 3)
    [InlineData(11, 16)] // raw 13 -> aligned 16 (pad 3)
    public void MeasureSize_CoversAllAlignmentResidues(int nameLen, int expectedString16LSize)
    {
        var name = new string('x', nameLen);
        Assert.Equal(12 + expectedString16LSize + 4, GameActionFellowshipCreateMessage.MeasureSize(name));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Pack_PadBytesAreZero_ForEachResidue(int nameLen)
    {
        var name = new string('x', nameLen);
        var dest = new byte[GameActionFellowshipCreateMessage.MeasureSize(name)];
        GameActionFellowshipCreateMessage.Pack(dest, name, shareXp: true);
        // The String16L segment starts at offset 12: u16 len + nameLen bytes + pad.
        var rawEnd = 12 + 2 + nameLen;
        var alignedEnd = 12 + (((2 + nameLen) + 3) & ~3);
        for (var i = rawEnd; i < alignedEnd; i++)
            Assert.Equal(0, dest[i]);   // pad bytes must be zero
    }

    [Fact]
    public void Pack_EmptyName_PacksLenZeroThenAlignedShareXp()
    {
        var dest = new byte[GameActionFellowshipCreateMessage.MeasureSize("")];
        GameActionFellowshipCreateMessage.Pack(dest, "", shareXp: true);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(12)));   // len = 0
        // String16L raw 2 -> aligned 4; shareXp u32 at 12+4 = 16.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16)));
    }

    [Theory]
    [InlineData(null, "Fellowship")]
    [InlineData("", "Fellowship")]
    [InlineData("   ", "Fellowship")]
    [InlineData("Alpha Squad", "Alpha Squad")]
    [InlineData("Cre\u2019w", "Crew")]            // curly apostrophe stripped
    [InlineData("\u00A1Hola\u2014!", "Hola!")]    // inverted-! + em dash stripped
    public void SanitizeName_ClampsToPrintableAsciiWithDefault(string? raw, string expected)
    {
        Assert.Equal(expected, GameActionFellowshipCreateMessage.SanitizeName(raw));
    }
}
