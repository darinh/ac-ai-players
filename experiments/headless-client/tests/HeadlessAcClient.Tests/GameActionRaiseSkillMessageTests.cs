// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the XP-spend GameAction:
//
//   RaiseSkill (0x0046) — spend accumulated experience to raise one
//                         trained skill.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0046)
//
// then two u32 payload words: skillId then xpSpent. The server handler
// (ACE-bots Source/ACE.Server/Network/GameAction/Actions/
// GameActionRaiseSkill.cs) reads skillId first, then xpSpent, and calls
// session.Player.HandleActionRaiseSkill(skill, xpSpent).

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionRaiseSkillMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void RaiseSkill_PackedSize_Is20Bytes()
    {
        Assert.Equal(20, GameActionRaiseSkillMessage.PackedSize);
    }

    [Fact]
    public void RaiseSkill_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionRaiseSkillMessage.PackedSize];

        var written = GameActionRaiseSkillMessage.Pack(
            dest,
            skillId: 34u /* WarMagic */,
            xpSpent: 12500u,
            actionSequence: 7u);

        Assert.Equal(GameActionRaiseSkillMessage.PackedSize, written);

        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 7u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0046u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 34u);                       c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 12500u);                    c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void RaiseSkill_Pack_SkillThenXp_NotSwapped()
    {
        // The server reads skillId first then xpSpent; a transposition would
        // spend the skill-id's worth of XP on a bogus skill. Distinct values
        // let the assertion catch a swap.
        var dest = new byte[GameActionRaiseSkillMessage.PackedSize];

        GameActionRaiseSkillMessage.Pack(
            dest, skillId: 0x00000021u /* LifeMagic=33 */, xpSpent: 0x000003E8u, actionSequence: 1u);

        var skillWord = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12));
        var xpWord    = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16));

        Assert.Equal(0x00000021u, skillWord);
        Assert.Equal(0x000003E8u, xpWord);
    }

    [Fact]
    public void RaiseSkill_DefaultActionSequence_IsOne()
    {
        var dest = new byte[GameActionRaiseSkillMessage.PackedSize];

        GameActionRaiseSkillMessage.Pack(dest, skillId: 33u, xpSpent: 100u);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));
    }

    [Fact]
    public void RaiseSkill_OpcodeIs0x0046_NotRaiseAttributeOrVital()
    {
        // Guards against accidentally reusing the RaiseVital (0x0044) or
        // RaiseAttribute (0x0045) opcode.
        var dest = new byte[GameActionRaiseSkillMessage.PackedSize];

        GameActionRaiseSkillMessage.Pack(dest, skillId: 33u, xpSpent: 100u);

        Assert.Equal(0x0046u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void RaiseSkill_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[19];
        Assert.Throws<ArgumentException>(() =>
            GameActionRaiseSkillMessage.Pack(tooSmall, 33u, 100u));
    }
}
