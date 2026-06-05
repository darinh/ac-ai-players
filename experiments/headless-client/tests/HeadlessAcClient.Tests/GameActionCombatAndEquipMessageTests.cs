// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the three new GameActions that
// unlock combat and equipment:
//
//   ChangeCombatMode    (0x0053) — switches into Melee/Missile/Magic
//   TargetedMeleeAttack (0x0008) — single melee strike against a guid
//   GetAndWieldItem     (0x001A) — equip an item from inventory
//
// All three follow the standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode
//
// then a fixed-size payload. The tests below assert each payload's
// byte-for-byte layout against a hand-rolled reference derived from
// the corresponding server-side handler in ACE-bots.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionCombatAndEquipMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void ChangeCombatMode_PackedSize_Is16Bytes()
    {
        Assert.Equal(16, GameActionChangeCombatModeMessage.PackedSize);
    }

    [Fact]
    public void ChangeCombatMode_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionChangeCombatModeMessage.PackedSize];

        var written = GameActionChangeCombatModeMessage.Pack(
            dest, newCombatMode: 2u /* Melee */, actionSequence: 42u);

        Assert.Equal(GameActionChangeCombatModeMessage.PackedSize, written);

        var expected = new byte[16];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 42u);                       c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0053u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 2u);                        c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void TargetedMeleeAttack_PackedSize_Is24Bytes()
    {
        Assert.Equal(24, GameActionTargetedMeleeAttackMessage.PackedSize);
    }

    [Fact]
    public void TargetedMeleeAttack_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionTargetedMeleeAttackMessage.PackedSize];

        var written = GameActionTargetedMeleeAttackMessage.Pack(
            dest,
            targetGuid:   0x800001ABu,
            attackHeight: 2u /* Medium */,
            powerLevel:   0.5f,
            actionSequence: 7u);

        Assert.Equal(GameActionTargetedMeleeAttackMessage.PackedSize, written);

        var expected = new byte[24];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 7u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0008u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x800001ABu);               c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 2u);                        c += 4;
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(c), 0.5f);                      c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void GetAndWieldItem_PackedSize_Is20Bytes()
    {
        Assert.Equal(20, GameActionGetAndWieldItemMessage.PackedSize);
    }

    [Fact]
    public void GetAndWieldItem_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionGetAndWieldItemMessage.PackedSize];

        var written = GameActionGetAndWieldItemMessage.Pack(
            dest,
            itemGuid:      0x800002E8u,
            equipLocation: 0x20 /* HandWear */,
            actionSequence: 11u);

        Assert.Equal(GameActionGetAndWieldItemMessage.PackedSize, written);

        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 11u);                       c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x001Au);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x800002E8u);               c += 4;
        BinaryPrimitives.WriteInt32LittleEndian (expected.AsSpan(c), 0x20);                      c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void ChangeCombatMode_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[15];
        Assert.Throws<ArgumentException>(() =>
            GameActionChangeCombatModeMessage.Pack(tooSmall, 2u));
    }

    [Fact]
    public void TargetedMeleeAttack_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[23];
        Assert.Throws<ArgumentException>(() =>
            GameActionTargetedMeleeAttackMessage.Pack(tooSmall, 0u, 2u, 0.5f));
    }

    [Fact]
    public void TargetedMissileAttack_PackedSize_Is24Bytes()
    {
        Assert.Equal(24, GameActionTargetedMissileAttackMessage.PackedSize);
    }

    [Fact]
    public void TargetedMissileAttack_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionTargetedMissileAttackMessage.PackedSize];

        var written = GameActionTargetedMissileAttackMessage.Pack(
            dest,
            targetGuid:    0x800001ABu,
            attackHeight:  2u /* Medium */,
            accuracyLevel: 0.5f,
            actionSequence: 7u);

        Assert.Equal(GameActionTargetedMissileAttackMessage.PackedSize, written);

        var expected = new byte[24];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 7u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x000Au);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x800001ABu);               c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 2u);                        c += 4;
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(c), 0.5f);                      c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void TargetedMissileAttack_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[23];
        Assert.Throws<ArgumentException>(() =>
            GameActionTargetedMissileAttackMessage.Pack(tooSmall, 0u, 2u, 0.5f));
    }
}
