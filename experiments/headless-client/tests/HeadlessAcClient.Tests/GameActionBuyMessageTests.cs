// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire-layout tests for the vendor-purchase GameAction:
//
//   Buy (0x005F) — purchase item(s) from an open vendor.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x005F)
//
// then the payload the server handler reads (ACE-bots Source/ACE.Server/Network/
// GameAction/Actions/GameActionBuyItems.cs):
//   u32 vendorGuid
//   u32 numItems
//   for each item: i32 amount, u32 objectID
// This packer sends a single-item buy (numItems = 1).

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionBuyMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void Buy_PackedSize_Is28Bytes()
    {
        Assert.Equal(28, GameActionBuyMessage.PackedSize);
    }

    [Fact]
    public void Buy_Pack_WritesExpectedBytes()
    {
        var dest = new byte[GameActionBuyMessage.PackedSize];

        var written = GameActionBuyMessage.Pack(
            dest,
            vendorGuid: 0x7A9B404Eu,
            itemGuid: 0x50001234u,
            amount: 1,
            actionSequence: 3u);

        Assert.Equal(GameActionBuyMessage.PackedSize, written);

        var expected = new byte[28];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 3u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x005Fu);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x7A9B404Eu);               c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 1u);                        c += 4; // numItems
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(c), 1);                          c += 4; // amount
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x50001234u);               c += 4; // objectID

        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Buy_Pack_FieldOrder_NotSwapped()
    {
        // The server reads vendorGuid, numItems, then per item amount, objectID.
        // Distinct values catch any transposition (e.g. buying the wrong guid).
        var dest = new byte[GameActionBuyMessage.PackedSize];

        GameActionBuyMessage.Pack(
            dest, vendorGuid: 0xAABBCCDDu, itemGuid: 0x11223344u, amount: 5, actionSequence: 1u);

        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12))); // vendorGuid
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16)));          // numItems
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(dest.AsSpan(20)));            // amount
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(24))); // objectID
    }

    [Fact]
    public void Buy_DefaultsAmountToOneAndSequenceToOne()
    {
        var dest = new byte[GameActionBuyMessage.PackedSize];

        GameActionBuyMessage.Pack(dest, vendorGuid: 1u, itemGuid: 2u);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4)));  // actionSequence
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(dest.AsSpan(20)));   // amount
    }

    [Fact]
    public void Buy_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[27];
        Assert.Throws<ArgumentException>(() =>
            GameActionBuyMessage.Pack(tooSmall, 1u, 2u));
    }
}
