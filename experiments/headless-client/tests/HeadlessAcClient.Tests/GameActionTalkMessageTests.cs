// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout + sanitizer tests for the local-say GameAction:
//
//   Talk (0x0015) — the player speaks a line ALOUD (local chat), heard by
//   nearby players/creatures as HearSpeech.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0015)
//
// then the payload the server handler reads (ACE-bots GameActionTalk.Handle —
// clientMessage.Payload.ReadString16L()):
//   String16L message   (u16 length, ASCII bytes, padded so (2+len) reaches a
//                         multiple of 4)

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionTalkMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    // ---- SanitizeMessage ----

    [Fact]
    public void Sanitize_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(GameActionTalkMessage.SanitizeMessage(null));
        Assert.Null(GameActionTalkMessage.SanitizeMessage(""));
        Assert.Null(GameActionTalkMessage.SanitizeMessage("   "));
    }

    [Fact]
    public void Sanitize_StripsLeadingCommandAt()
    {
        // A leading '@' would make the server parse the line as a slash command.
        Assert.Equal("teleport me", GameActionTalkMessage.SanitizeMessage("@teleport me"));
        Assert.Equal("hi", GameActionTalkMessage.SanitizeMessage("@@@hi"));
    }

    [Fact]
    public void Sanitize_AllAt_ReturnsNull()
    {
        Assert.Null(GameActionTalkMessage.SanitizeMessage("@@@"));
    }

    [Fact]
    public void Sanitize_ManyLeadingAt_ThenText_KeepsText()
    {
        // Leading '@'/whitespace are stripped BEFORE the length cap, so a long run of
        // them does not consume the cap and drop the real line.
        var raw = new string('@', GameActionTalkMessage.MaxMessageChars) + "hi";
        Assert.Equal("hi", GameActionTalkMessage.SanitizeMessage(raw));
        Assert.Equal("hi", GameActionTalkMessage.SanitizeMessage("     hi"));
    }

    [Fact]
    public void Sanitize_DropsNonPrintableAscii_KeepsInnerAt()
    {
        // Curly quotes / non-ASCII dropped; an '@' that is NOT leading is kept.
        var got = GameActionTalkMessage.SanitizeMessage("hi \u201cthere\u201d me@host");
        Assert.Equal("hi there me@host", got);
    }

    [Fact]
    public void Sanitize_ClampsLength()
    {
        var longLine = new string('x', 500);
        var got = GameActionTalkMessage.SanitizeMessage(longLine);
        Assert.NotNull(got);
        Assert.True(got!.Length <= GameActionTalkMessage.MaxMessageChars);
    }

    // ---- Pack (byte-exact) ----

    [Fact]
    public void Pack_WritesHeaderAndString16L()
    {
        var need = GameActionTalkMessage.MeasureSize("hi");
        var dest = new byte[need];
        var written = GameActionTalkMessage.Pack(dest, "hi", actionSequence: 5u);
        Assert.Equal(need, written);

        // header(12) + String16L("hi") => u16 len(2) + 'h','i' => (2+2)=4, already /4 => 16 total
        Assert.Equal(16, written);
        var expected = new byte[16];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 5u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0015u);                   c += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(c), 2);                         c += 2;
        expected[c++] = (byte)'h';
        expected[c++] = (byte)'i';
        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Pack_UsesOpcode0015()
    {
        var dest = new byte[GameActionTalkMessage.MeasureSize("x")];
        GameActionTalkMessage.Pack(dest, "x");
        Assert.Equal(0x0015u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void Pack_MessageRoundTripsThroughReadString16L()
    {
        const string msg = "hello fellow bot";
        var dest = new byte[GameActionTalkMessage.MeasureSize(msg)];
        GameActionTalkMessage.Pack(dest, msg);
        var offset = GameActionMessage.HeaderSize;
        var read = AcStrings.ReadString16L(dest, ref offset);
        Assert.Equal(msg, read);
        Assert.Equal(dest.Length, offset);   // consumed exactly (incl. padding)
    }

    [Fact]
    public void Pack_DefaultsSequenceToOne()
    {
        var dest = new byte[GameActionTalkMessage.MeasureSize("x")];
        GameActionTalkMessage.Pack(dest, "x");
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));
    }

    [Fact]
    public void Pack_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[GameActionTalkMessage.MeasureSize("hi") - 1];
        Assert.Throws<ArgumentException>(() => GameActionTalkMessage.Pack(tooSmall, "hi"));
    }
}
