// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the fellowship-recruit GameAction:
//
//   FellowshipRecruit (0x00A5) — invite another player into the fellowship.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x00A5)
//
// then the payload the server handler reads (ACE-bots Source/ACE.Server/Network/
// GameAction/Actions/GameActionFellowshipRecruit.cs):
//   u32 newMemberGuid   (the player to recruit)
// A wrong byte here silently fails the recruit.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionFellowshipRecruitMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void Recruit_PackedSize_Is16Bytes()
    {
        Assert.Equal(16, GameActionFellowshipRecruitMessage.PackedSize);
    }

    [Fact]
    public void Recruit_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionFellowshipRecruitMessage.PackedSize];

        var written = GameActionFellowshipRecruitMessage.Pack(dest, newMemberGuid: 0x50000123u, actionSequence: 5u);

        Assert.Equal(GameActionFellowshipRecruitMessage.PackedSize, written);

        var expected = new byte[16];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 5u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x00A5u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x50000123u);               c += 4; // newMemberGuid

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Recruit_Pack_UsesOpcode00A5()
    {
        var dest = new byte[GameActionFellowshipRecruitMessage.PackedSize];
        GameActionFellowshipRecruitMessage.Pack(dest, newMemberGuid: 0x50000001u);
        Assert.Equal(0x00A5u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void Recruit_Pack_WritesMemberGuidAtOffset12()
    {
        var dest = new byte[GameActionFellowshipRecruitMessage.PackedSize];
        GameActionFellowshipRecruitMessage.Pack(dest, newMemberGuid: 0x5FABCDEFu);
        Assert.Equal(0x5FABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));
    }

    [Fact]
    public void Recruit_DefaultsSequenceToOne()
    {
        var dest = new byte[GameActionFellowshipRecruitMessage.PackedSize];
        GameActionFellowshipRecruitMessage.Pack(dest, newMemberGuid: 0x50000001u);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));   // actionSequence
    }

    [Fact]
    public void Recruit_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[15];
        Assert.Throws<ArgumentException>(() => GameActionFellowshipRecruitMessage.Pack(tooSmall, newMemberGuid: 0x50000001u));
    }
}
