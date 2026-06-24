// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the vendor-sell GameAction:
//
//   Sell (0x0060) — sell item(s) from the bot's OWN inventory to an open vendor.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0060)
//
// then the payload the server handler reads (ACE-bots Source/ACE.Server/Network/
// GameAction/Actions/GameActionSellItems.cs):
//   u32 vendorGuid
//   u32 numItems
//   for each item: i32 amount, u32 objectID   (objectID = the bot's inventory guid)
// This packer sends a single-item sell (numItems = 1). The layout is identical to
// Buy except for the opcode, so a wrong byte here silently fails the sell.

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionSellMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void Sell_PackedSize_Is28Bytes()
    {
        Assert.Equal(28, GameActionSellMessage.PackedSize);
    }

    [Fact]
    public void Sell_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionSellMessage.PackedSize];

        var written = GameActionSellMessage.Pack(
            dest,
            vendorGuid: 0x7A9B404Eu,
            itemGuid: 0x50001234u,
            amount: 1,
            actionSequence: 3u);

        Assert.Equal(GameActionSellMessage.PackedSize, written);

        var expected = new byte[28];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 3u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0060u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x7A9B404Eu);               c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 1u);                        c += 4; // numItems
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(c), 1);                          c += 4; // amount
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x50001234u);               c += 4; // objectID

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Sell_Pack_UsesOpcode0060_NotBuy005F()
    {
        // The ONLY field that distinguishes Sell from Buy on the wire is the
        // action opcode at offset 8. A regression that reused 0x005F would credit
        // nothing and silently fail every sell.
        var dest = new byte[GameActionSellMessage.PackedSize];

        GameActionSellMessage.Pack(dest, vendorGuid: 1u, itemGuid: 2u);

        Assert.Equal(0x0060u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
    }

    [Fact]
    public void Sell_Pack_FieldOrder_NotSwapped()
    {
        // The server reads vendorGuid, numItems, then per item amount, objectID.
        // Distinct values catch any transposition (e.g. selling the wrong guid).
        var dest = new byte[GameActionSellMessage.PackedSize];

        GameActionSellMessage.Pack(
            dest, vendorGuid: 0xAABBCCDDu, itemGuid: 0x11223344u, amount: 5, actionSequence: 1u);

        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12))); // vendorGuid
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16)));          // numItems
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(dest.AsSpan(20)));            // amount
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(24))); // objectID
    }

    [Fact]
    public void Sell_DefaultsAmountToOneAndSequenceToOne()
    {
        var dest = new byte[GameActionSellMessage.PackedSize];

        GameActionSellMessage.Pack(dest, vendorGuid: 1u, itemGuid: 2u);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));  // actionSequence
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(dest.AsSpan(20)));   // amount
    }

    [Fact]
    public void Sell_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[27];
        Assert.Throws<ArgumentException>(() =>
            GameActionSellMessage.Pack(tooSmall, 1u, 2u));
    }
}
