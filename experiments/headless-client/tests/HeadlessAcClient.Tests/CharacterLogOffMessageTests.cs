// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CharacterLogOffMessage — the opcode-only (0xF653) clean-logoff
// message the client sends on a graceful exit so the server frees the
// in-world character immediately (CharacterHandler.CharacterLogOff reads no
// payload, just calls session.LogOffPlayer()).

using System;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CharacterLogOffMessageTests
{
    [Fact]
    public void Pack_IsOpcodeOnly_FourBytes()
    {
        var buf = new byte[CharacterLogOffMessage.PackedSize];
        var written = CharacterLogOffMessage.Pack(buf);
        Assert.Equal(4, CharacterLogOffMessage.PackedSize);
        Assert.Equal(4, written);
    }

    [Fact]
    public void Pack_WritesCharacterLogOffOpcodeLittleEndian()
    {
        // 0xF653 as u32 little-endian = 53 F6 00 00.
        var buf = new byte[CharacterLogOffMessage.PackedSize];
        CharacterLogOffMessage.Pack(buf);
        Assert.Equal(0x53, buf[0]);
        Assert.Equal(0xF6, buf[1]);
        Assert.Equal(0x00, buf[2]);
        Assert.Equal(0x00, buf[3]);
    }

    [Fact]
    public void Pack_OpcodeMatchesEnum()
    {
        Assert.Equal(0xF653u, (uint)GameMessageOpcode.CharacterLogOff);
    }

    [Fact]
    public void Pack_BufferTooSmall_Throws()
        => Assert.Throws<ArgumentException>(() => CharacterLogOffMessage.Pack(new byte[3]));
}
