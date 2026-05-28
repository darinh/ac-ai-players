// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the GameActionAutonomousPosition packer (0xF753).
// Verifies byte-exact wire layout against a hand-rolled reference.
//
// Why these tests matter:
//   The packer is the very first outbound movement-adjacent
//   GameAction in the spike. A bug here that produces a malformed
//   payload would be ROUTED to the server's handler (the envelope
//   checksum is correct), be silently misparsed by the wrong
//   constructor, and the server might react to a position we did
//   not actually intend to send. So unit-level verification of the
//   exact byte layout is mandatory.
//
// Cross-reference:
//   Source/ACE.Server/Network/GameAction/Actions/GameActionAutonomousPosition.cs
//   Source/ACE.Entity/Position.cs:251-264 (W,X,Y,Z quaternion order!)

using System;
using System.Buffers.Binary;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GameActionAutonomousPositionMessageTests
{
    [Fact]
    public void PackedSize_IsExactly56Bytes()
    {
        Assert.Equal(56, GameActionAutonomousPositionMessage.PackedSize);
    }

    [Fact]
    public void Pack_WritesExpectedByteLayout()
    {
        var dest = new byte[GameActionAutonomousPositionMessage.PackedSize];
        var cellId = 0x860201ADu;
        var pos    = new Vector3(12.5f, -28.25f, 0.125f);
        // Pick a quaternion where W,X,Y,Z all differ so any
        // ordering bug shows up immediately. Real ACE quaternions
        // are unit-length but the packer doesn't normalize, so
        // arbitrary values are fine here.
        var rot    = new Quaternion(x: 0.1f, y: 0.2f, z: 0.3f, w: 0.4f);

        var written = GameActionAutonomousPositionMessage.Pack(
            dest,
            cellId, pos, rot,
            instanceSequence:      0x1234,
            serverControlSequence: 0x5678,
            teleportSequence:      0x9ABC,
            forcePositionSequence: 0xDEF0,
            contact: true,
            actionSequence: 1);

        Assert.Equal(GameActionAutonomousPositionMessage.PackedSize, written);

        // GameAction envelope: 4B opcode (0xF7B1) + 4B actionSeq + 4B actionOpcode
        Assert.Equal(0xF7B1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(4, 4)));
        Assert.Equal(0xF753u, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(8, 4)));

        // Position: cell (4B)
        Assert.Equal(cellId, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12, 4)));

        // Position: xyz (3 * 4B)
        Assert.Equal(pos.X, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(16, 4)));
        Assert.Equal(pos.Y, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(20, 4)));
        Assert.Equal(pos.Z, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(24, 4)));

        // Quaternion: W, X, Y, Z order — NOT C# default X,Y,Z,W.
        // This is the BLOCKING fix from rubber-duck pass.
        Assert.Equal(rot.W, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(28, 4)));
        Assert.Equal(rot.X, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(32, 4)));
        Assert.Equal(rot.Y, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(36, 4)));
        Assert.Equal(rot.Z, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(40, 4)));

        // 4 ushort sequences
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(44, 2)));
        Assert.Equal((ushort)0x5678, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(46, 2)));
        Assert.Equal((ushort)0x9ABC, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(48, 2)));
        Assert.Equal((ushort)0xDEF0, BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(50, 2)));

        // Contact byte + 3B align padding (zero).
        Assert.Equal(1, dest[52]);
        Assert.Equal(0, dest[53]);
        Assert.Equal(0, dest[54]);
        Assert.Equal(0, dest[55]);
    }

    [Fact]
    public void Pack_ContactFalse_WritesZeroByte()
    {
        var dest = new byte[GameActionAutonomousPositionMessage.PackedSize];
        GameActionAutonomousPositionMessage.Pack(
            dest,
            cellId: 0x86020000,
            pos: Vector3.Zero,
            rot: Quaternion.Identity,
            instanceSequence: 0, serverControlSequence: 0,
            teleportSequence: 0, forcePositionSequence: 0,
            contact: false);

        Assert.Equal(0, dest[52]);
    }

    [Fact]
    public void Pack_BufferTooSmall_Throws()
    {
        var tooSmall = new byte[GameActionAutonomousPositionMessage.PackedSize - 1];
        Assert.Throws<ArgumentException>(() =>
            GameActionAutonomousPositionMessage.Pack(
                tooSmall,
                cellId: 0x86020000,
                pos: Vector3.Zero,
                rot: Quaternion.Identity,
                instanceSequence: 0, serverControlSequence: 0,
                teleportSequence: 0, forcePositionSequence: 0,
                contact: false));
    }

    [Fact]
    public void WritePosition_QuaternionIdentity_HasWFirst()
    {
        // Identity quaternion is (X=0, Y=0, Z=0, W=1). The first
        // 4 bytes of the rotation block in the packed output MUST
        // decode to 1.0f (the W component), not 0.0f. This is a
        // tight regression guard against accidentally reverting
        // the W,X,Y,Z ordering back to System.Numerics's default.
        var dest = new byte[32];
        var written = GameActionMessage.WritePosition(
            dest, cellId: 0x86020000, pos: Vector3.Zero, rot: Quaternion.Identity);

        Assert.Equal(32, written);
        // Bytes 16-19 = first float of the quaternion block.
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(16, 4)));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(20, 4)));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(24, 4)));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(dest.AsSpan(28, 4)));
    }

    [Fact]
    public void Pack_PreservesFullBlockcellCellId()
    {
        // Regression: rubber-duck flagged a likely bug where we
        // might pack only the landblock high half (0x8602) instead
        // of the full blockcell uint (0x860201AD). The cell byte
        // must be the EXACT uint we passed in.
        var dest = new byte[GameActionAutonomousPositionMessage.PackedSize];
        GameActionAutonomousPositionMessage.Pack(
            dest,
            cellId: 0x860201AD,
            pos: Vector3.Zero,
            rot: Quaternion.Identity,
            instanceSequence: 0, serverControlSequence: 0,
            teleportSequence: 0, forcePositionSequence: 0,
            contact: false);

        Assert.Equal(0x860201ADu, BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(12, 4)));
    }
}
