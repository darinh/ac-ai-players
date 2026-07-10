// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for SetSingleCharacterOption (0x0005),
// which the headless client sends once after LoginComplete to enable
// AutoRepeatAttacks so the server runs its native continuous melee
// swing loop (Player_Melee.cs:375).
//
// Wire prelude (standard GameAction):
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0005)
// then payload:
//   u32 option (CharacterOption id)
//   u32 value  (0 = off, non-zero = on)

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionSetSingleCharacterOptionMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void SetSingleCharacterOption_PackedSize_Is20Bytes()
    {
        Assert.Equal(20, GameActionSetSingleCharacterOptionMessage.PackedSize);
    }

    [Fact]
    public void AutoRepeatAttacks_OptionId_IsZero()
    {
        // The server reads the option id straight off the wire; if this
        // drifts from ACE's CharacterOption.AutoRepeatAttacks the bot would
        // toggle the wrong option.
        Assert.Equal(0u, (uint)CharacterOption.AutoRepeatAttacks);
    }

    [Fact]
    public void Pack_EnableAutoRepeatAttacks_WritesExpectedBytes()
    {
        var dest = new byte[GameActionSetSingleCharacterOptionMessage.PackedSize];

        var written = GameActionSetSingleCharacterOptionMessage.Pack(
            dest, CharacterOption.AutoRepeatAttacks, value: true, actionSequence: 3u);

        Assert.Equal(GameActionSetSingleCharacterOptionMessage.PackedSize, written);

        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 3u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0005u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0u /* AutoRepeatAttacks */); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 1u /* on */);               c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Pack_DisableOption_WritesZeroValue()
    {
        var dest = new byte[GameActionSetSingleCharacterOptionMessage.PackedSize];

        GameActionSetSingleCharacterOptionMessage.Pack(
            dest, CharacterOption.AutoRepeatAttacks, value: false, actionSequence: 1u);

        // value field is the last 4 bytes.
        var value = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16));
        Assert.Equal(0u, value);
    }

    [Fact]
    public void Pack_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[19];
        Assert.Throws<ArgumentException>(() =>
            GameActionSetSingleCharacterOptionMessage.Pack(
                tooSmall, CharacterOption.AutoRepeatAttacks, true));
    }

    [Fact]
    public void IgnoreFellowshipRequests_OptionId_Is0x02()
    {
        // Must match ACE.Entity CharacterOption.IgnoreFellowshipRequests (0x02); if it
        // drifts the bot would toggle the wrong option and stay un-recruitable.
        Assert.Equal(0x02u, (uint)CharacterOption.IgnoreFellowshipRequests);
    }

    [Fact]
    public void Pack_ClearIgnoreFellowshipRequests_WritesOption02Value0()
    {
        // The login-time clear that makes the bot recruitable: option 0x02, value 0.
        var dest = new byte[GameActionSetSingleCharacterOptionMessage.PackedSize];

        var written = GameActionSetSingleCharacterOptionMessage.Pack(
            dest, CharacterOption.IgnoreFellowshipRequests, value: false, actionSequence: 2u);

        Assert.Equal(GameActionSetSingleCharacterOptionMessage.PackedSize, written);

        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 2u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0005u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x02u /* IgnoreFellowshipRequests */); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0u /* off */);              c += 4;

        Assert.Equal(expected, dest);
    }
}
