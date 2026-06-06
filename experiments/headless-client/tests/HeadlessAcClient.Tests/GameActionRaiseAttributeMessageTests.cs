// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the XP-spend GameAction:
//
//   RaiseAttribute (0x0045) — spend accumulated experience to raise one
//                             of the six primary attributes.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0045)
//
// then two u32 payload words: attributeId then xpSpent. The server handler
// (ACE-bots Source/ACE.Server/Network/GameAction/Actions/
// GameActionRaiseAttribute.cs) reads attributeId first, then xpSpent, and
// calls session.Player.HandleActionRaiseAttribute(attribute, xpSpent).

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionRaiseAttributeMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void RaiseAttribute_PackedSize_Is20Bytes()
    {
        Assert.Equal(20, GameActionRaiseAttributeMessage.PackedSize);
    }

    [Fact]
    public void RaiseAttribute_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionRaiseAttributeMessage.PackedSize];

        var written = GameActionRaiseAttributeMessage.Pack(
            dest,
            attributeId: 2u /* Endurance */,
            xpSpent: 12500u,
            actionSequence: 7u);

        Assert.Equal(GameActionRaiseAttributeMessage.PackedSize, written);

        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 7u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0045u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 2u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 12500u);                    c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void RaiseAttribute_Pack_AttributeThenXp_NotSwapped()
    {
        // The server reads attributeId first then xpSpent; a transposition
        // would spend the attribute-id's worth of XP on a bogus attribute.
        // Distinct values let the assertion catch a swap.
        var dest = new byte[GameActionRaiseAttributeMessage.PackedSize];

        GameActionRaiseAttributeMessage.Pack(
            dest, attributeId: 0x00000006u /* Self */, xpSpent: 0x000003E8u, actionSequence: 1u);

        var attrWord = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12));
        var xpWord   = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16));

        Assert.Equal(0x00000006u, attrWord);
        Assert.Equal(0x000003E8u, xpWord);
    }

    [Fact]
    public void RaiseAttribute_DefaultActionSequence_IsOne()
    {
        var dest = new byte[GameActionRaiseAttributeMessage.PackedSize];

        GameActionRaiseAttributeMessage.Pack(dest, attributeId: 1u, xpSpent: 100u);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));
    }

    [Fact]
    public void RaiseAttribute_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[19];
        Assert.Throws<ArgumentException>(() =>
            GameActionRaiseAttributeMessage.Pack(tooSmall, 2u, 100u));
    }
}
