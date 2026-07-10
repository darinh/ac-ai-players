// SPDX-License-Identifier: AGPL-3.0-or-later
// Byte-exact wire tests for the confirmation-reply GameAction:
//
//   ConfirmationResponse (0x0275) — answer a server confirmation prompt
//   (GameEventConfirmationRequest 0x0274), e.g. accept/decline a fellowship invite.
//
// Standard GameAction wire prelude:
//   u32 envelope opcode (0xF7B1 GameAction)
//   u32 actionSequence
//   u32 actionOpcode (0x0275)
//
// then the payload the server handler reads (ACE-bots GameActionConfirmationResponse):
//   i32 confirmationType
//   u32 context
//   i32 response   (Convert.ToBoolean: 0 = decline, nonzero = accept)

using System;
using System.Buffers.Binary;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionConfirmationResponseMessageTests
{
    private const uint GameActionEnvelopeOpcode = 0xF7B1;

    [Fact]
    public void Pack_WritesHeaderTypeContextAndAcceptFlag()
    {
        var dest = new byte[GameActionConfirmationResponseMessage.PackedSize];
        var written = GameActionConfirmationResponseMessage.Pack(
            dest, ConfirmationType.Fellowship, context: 7u, accept: true, actionSequence: 5u);

        Assert.Equal(24, written);
        Assert.Equal(GameActionConfirmationResponseMessage.PackedSize, written);

        var expected = new byte[24];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 5u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0275u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x04u);                     c += 4; // Fellowship
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 7u);                        c += 4; // context
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(c), 1);                          c += 4; // accept
        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Pack_UsesOpcode0275_AndFieldsAtFixedOffsets()
    {
        var dest = new byte[GameActionConfirmationResponseMessage.PackedSize];
        GameActionConfirmationResponseMessage.Pack(
            dest, ConfirmationType.Fellowship, context: 0xDEADBEEFu, accept: true);
        Assert.Equal(0x0275u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8)));
        Assert.Equal(0x04u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));   // type
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(16))); // context
    }

    [Fact]
    public void Pack_DeclineWritesZeroResponse()
    {
        var dest = new byte[GameActionConfirmationResponseMessage.PackedSize];
        GameActionConfirmationResponseMessage.Pack(
            dest, ConfirmationType.Fellowship, context: 1u, accept: false);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(dest.AsSpan(20)));
    }

    [Fact]
    public void Pack_AcceptWritesOneResponse()
    {
        var dest = new byte[GameActionConfirmationResponseMessage.PackedSize];
        GameActionConfirmationResponseMessage.Pack(
            dest, ConfirmationType.Fellowship, context: 1u, accept: true);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(dest.AsSpan(20)));
    }

    [Fact]
    public void Pack_EchoesGivenConfirmationType()
    {
        // The type is echoed verbatim so the server matches the reply to the prompt.
        var dest = new byte[GameActionConfirmationResponseMessage.PackedSize];
        GameActionConfirmationResponseMessage.Pack(
            dest, ConfirmationType.SwearAllegiance, context: 3u, accept: true);
        Assert.Equal((uint)ConfirmationType.SwearAllegiance,
            BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12)));
    }

    [Fact]
    public void Pack_ApproveSwearAllegiance_WritesType01ContextAccept1()
    {
        // The monarch's AllegianceApprove path: ConfirmationResponse(type=SwearAllegiance=1,
        // echoed context, response=1).
        var dest = new byte[GameActionConfirmationResponseMessage.PackedSize];
        var written = GameActionConfirmationResponseMessage.Pack(
            dest, ConfirmationType.SwearAllegiance, context: 88u, accept: true, actionSequence: 4u);

        Assert.Equal(24, written);
        var expected = new byte[24];
        var c = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), GameActionEnvelopeOpcode); c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 4u);                        c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x0275u);                   c += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 0x01u);                     c += 4; // SwearAllegiance
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(c), 88u);                       c += 4; // context
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(c), 1);                          c += 4; // approve
        Assert.Equal(expected, dest);
    }

    [Fact]
    public void Pack_RejectsTooSmallBuffer()
    {
        var tooSmall = new byte[GameActionConfirmationResponseMessage.PackedSize - 1];
        Assert.Throws<ArgumentException>(() =>
            GameActionConfirmationResponseMessage.Pack(
                tooSmall, ConfirmationType.Fellowship, 1u, true));
    }
}
