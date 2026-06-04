// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the two-object "use item on target"
// GameAction:
//
//   UseWithTarget (0x0035) — apply a held item (source) to a world
//                            target, e.g. a key on a locked chest.
//
// It follows the standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0035)
//
// then two u32 payload words: sourceObjectGuid then targetObjectGuid.
// The server handler (ACE-bots
// Source/ACE.Server/Network/GameAction/Actions/GameActionUseWithTarget.cs)
// reads sourceObjectGuid first, then targetObjectGuid, and calls
// session.Player.HandleActionUseWithTarget(source, target).

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionUseWithTargetMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void UseWithTarget_PackedSize_Is20Bytes()
    {
        Assert.Equal(20, GameActionUseWithTargetMessage.PackedSize);
    }

    [Fact]
    public void UseWithTarget_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionUseWithTargetMessage.PackedSize];

        var written = GameActionUseWithTargetMessage.Pack(
            dest,
            sourceGuid: 0x800003C1u /* key */,
            targetGuid: 0x80000ABCu /* chest */,
            actionSequence: 9u);

        Assert.Equal(GameActionUseWithTargetMessage.PackedSize, written);

        var expected = new byte[20];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 9u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0035u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x800003C1u);               c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x80000ABCu);               c += 4;

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void UseWithTarget_Pack_SourceThenTarget_NotSwapped()
    {
        // Guard against a source/target transposition regression: the
        // server unlocks the TARGET using the SOURCE item, so the order
        // is load-bearing. Distinct guids let the assertion catch a swap.
        var dest = new byte[GameActionUseWithTargetMessage.PackedSize];

        GameActionUseWithTargetMessage.Pack(
            dest, sourceGuid: 0x11111111u, targetGuid: 0x22222222u, actionSequence: 1u);

        var sourceWord = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12));
        var targetWord = BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16));

        Assert.Equal(0x11111111u, sourceWord);
        Assert.Equal(0x22222222u, targetWord);
    }

    [Fact]
    public void UseWithTarget_DefaultActionSequence_IsOne()
    {
        var dest = new byte[GameActionUseWithTargetMessage.PackedSize];

        GameActionUseWithTargetMessage.Pack(
            dest, sourceGuid: 0xAAu, targetGuid: 0xBBu);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));
    }

    [Fact]
    public void UseWithTarget_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[19];
        Assert.Throws<ArgumentException>(() =>
            GameActionUseWithTargetMessage.Pack(tooSmall, 0u, 0u));
    }
}
