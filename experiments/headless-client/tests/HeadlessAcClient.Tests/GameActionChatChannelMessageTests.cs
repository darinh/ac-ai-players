// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire + channel-resolver tests for the group-channel chat GameAction:
//
//   ChatChannel (0x0147) — say a line on a named group chat channel (fellowship,
//   monarch, vassals) rather than aloud locally.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0147)
//
// then the payload the server handler reads (ACE-bots GameActionChatChannel.Handle):
//   u32 channel      (ACE.Entity.Enum.Channel bitmask)
//   String16L message

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionChatChannelMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    // ---- ResolveChannel ----

    [Fact]
    public void Resolve_MapsFellowship()
    {
        Assert.Equal(0x00000800u, GameActionChatChannelMessage.ResolveChannel("fellowship"));
        Assert.Equal(0x00000800u, GameActionChatChannelMessage.ResolveChannel("Fellow"));
        Assert.Equal(0x00000800u, GameActionChatChannelMessage.ResolveChannel("  FELLOWSHIP  "));
    }

    [Fact]
    public void Resolve_MapsAllegianceRelationshipChannels()
    {
        // Permission-free per-relationship allegiance channels.
        Assert.Equal(0x00004000u, GameActionChatChannelMessage.ResolveChannel("monarch"));
        Assert.Equal(0x00004000u, GameActionChatChannelMessage.ResolveChannel("Monarch"));
        Assert.Equal(0x00001000u, GameActionChatChannelMessage.ResolveChannel("vassals"));
        Assert.Equal(0x00001000u, GameActionChatChannelMessage.ResolveChannel("vassal"));
    }

    [Fact]
    public void Resolve_UnknownOrBlank_ReturnsNull()
    {
        Assert.Null(GameActionChatChannelMessage.ResolveChannel(null));
        Assert.Null(GameActionChatChannelMessage.ResolveChannel(""));
        Assert.Null(GameActionChatChannelMessage.ResolveChannel("general"));
        Assert.Null(GameActionChatChannelMessage.ResolveChannel("trade"));
        // "allegiance" (whole-allegiance broadcast) stays UNMAPPED — it needs a server
        // Speaker rank, so it must NOT silently become a local say; the motor fails it.
        Assert.Null(GameActionChatChannelMessage.ResolveChannel("allegiance"));
        Assert.Null(GameActionChatChannelMessage.ResolveChannel("felloship"));   // typo
    }

    // ---- Pack (byte-exact) ----

    [Fact]
    public void Pack_WritesHeaderChannelAndString16L()
    {
        var need = GameActionChatChannelMessage.MeasureSize("hi");
        var dest = new byte[need];
        var written = GameActionChatChannelMessage.Pack(
            dest, GameActionChatChannelMessage.FellowChannel, "hi", actionSequence: 5u);
        Assert.Equal(need, written);

        // header(12) + u32 channel(4) + String16L("hi")=(2+2)->4 => 20 total
        Assert.Equal(20, written);
        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 5u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0147u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x00000800u);               c += 4; // channel
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(c), 2);                         c += 2;
        expected[c++] = (byte)'h';
        expected[c++] = (byte)'i';
        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Pack_UsesOpcode0147_AndChannelAtOffset12()
    {
        var dest = new byte[GameActionChatChannelMessage.MeasureSize("x")];
        GameActionChatChannelMessage.Pack(dest, GameActionChatChannelMessage.FellowChannel, "x");
        Assert.Equal(0x0147u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
        Assert.Equal(0x00000800u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));
    }

    [Fact]
    public void Pack_MessageRoundTripsAfterChannel()
    {
        const string msg = "forming up at the door";
        var dest = new byte[GameActionChatChannelMessage.MeasureSize(msg)];
        GameActionChatChannelMessage.Pack(dest, GameActionChatChannelMessage.FellowChannel, msg);
        var offset = GameActionMessage.HeaderSize + 4;   // skip header + channel
        var read = AcStrings.ReadString16L(dest, ref offset);
        Assert.Equal(msg, read);
        Assert.Equal(dest.Length, offset);
    }

    [Fact]
    public void Pack_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[GameActionChatChannelMessage.MeasureSize("hi") - 1];
        Assert.Throws<ArgumentException>(() =>
            GameActionChatChannelMessage.Pack(tooSmall, GameActionChatChannelMessage.FellowChannel, "hi"));
    }
}
