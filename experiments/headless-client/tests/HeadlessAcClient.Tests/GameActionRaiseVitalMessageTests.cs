// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the XP-spend GameAction:
//
//   RaiseVital (0x0044) — spend accumulated experience to raise one of
//                         the three vital MAX pools (MaxHealth/MaxStamina/
//                         MaxMana).
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0044)
//
// then two u32 payload words: vitalId then xpSpent. The server handler
// (ACE-bots Source/ACE.Server/Network/GameAction/Actions/
// GameActionRaiseVital.cs) reads vitalId first, then xpSpent, and calls
// session.Player.HandleActionRaiseVital(vital, xpSpent).

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionRaiseVitalMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void RaiseVital_PackedSize_Is20Bytes()
    {
        Assert.Equal(20, GameActionRaiseVitalMessage.PackedSize);
    }

    [Fact]
    public void RaiseVital_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionRaiseVitalMessage.PackedSize];

        var written = GameActionRaiseVitalMessage.Pack(
            dest,
            vitalId: 1u /* MaxHealth */,
            xpSpent: 12500u,
            actionSequence: 7u);

        Assert.Equal(GameActionRaiseVitalMessage.PackedSize, written);

        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 7u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0044u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 1u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 12500u);                    c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void RaiseVital_Pack_VitalThenXp_NotSwapped()
    {
        // The server reads vitalId first then xpSpent; a transposition would
        // spend the vital-id's worth of XP on a bogus vital. Distinct values
        // let the assertion catch a swap.
        var dest = new byte[GameActionRaiseVitalMessage.PackedSize];

        GameActionRaiseVitalMessage.Pack(
            dest, vitalId: 0x00000005u /* MaxMana */, xpSpent: 0x000003E8u, actionSequence: 1u);

        var vitalWord = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12));
        var xpWord    = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16));

        Assert.Equal(0x00000005u, vitalWord);
        Assert.Equal(0x000003E8u, xpWord);
    }

    [Fact]
    public void RaiseVital_DefaultActionSequence_IsOne()
    {
        var dest = new byte[GameActionRaiseVitalMessage.PackedSize];

        GameActionRaiseVitalMessage.Pack(dest, vitalId: 1u, xpSpent: 100u);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));
    }

    [Fact]
    public void RaiseVital_OpcodeIs0x0044_NotRaiseAttribute0x0045()
    {
        // Guards against accidentally reusing the RaiseAttribute opcode.
        var dest = new byte[GameActionRaiseVitalMessage.PackedSize];

        GameActionRaiseVitalMessage.Pack(dest, vitalId: 1u, xpSpent: 100u);

        Assert.Equal(0x0044u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void RaiseVital_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[19];
        Assert.Throws<ArgumentException>(() =>
            GameActionRaiseVitalMessage.Pack(tooSmall, 1u, 100u));
    }
}
