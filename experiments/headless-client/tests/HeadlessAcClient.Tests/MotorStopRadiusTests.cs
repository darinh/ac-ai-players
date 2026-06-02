// SPDX-License-Identifier: AGPL-3.0-or-later
// MotorStopRadius unit tests — terminal arrival radius for the
// walk-tick. Audit-safe INVARIANT shape (mirrors PickerSelection /
// FoundationTests style): we assert the bit-decode and the
// arithmetic relationship between Default and Portal, NOT any
// particular game-knowledge claim about portal weenies.
//
// What we test:
//   - For(null) returns DefaultUnits (defensive fallback).
//   - A snapshot with no ObjectDescriptionFlags returns DefaultUnits.
//   - A snapshot whose ObjectDescriptionFlags has the Portal bit
//     set returns PortalUnits (regardless of other bits).
//   - A snapshot whose flags carry any non-Portal mix (Door,
//     Corpse, LifeStone, Openable) returns DefaultUnits.
//   - ForFlags is the same predicate as For (bit-level equivalence)
//     for both Portal-set and Portal-clear inputs.
//   - PortalUnits is strictly greater than DefaultUnits (relationship
//     invariant — the helper has a reason to exist).

using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class MotorStopRadiusTests
{
    private const uint FlagPortal    = (uint)ObjectDescriptionFlag.Portal;
    private const uint FlagDoor      = (uint)ObjectDescriptionFlag.Door;
    private const uint FlagCorpse    = (uint)ObjectDescriptionFlag.Corpse;
    private const uint FlagLifeStone = (uint)ObjectDescriptionFlag.LifeStone;
    private const uint FlagOpenable  = (uint)ObjectDescriptionFlag.Openable;

    private static WorldObjectSnapshot Snap(uint guid, uint? descFlags) =>
        new(guid)
        {
            Name = "test",
            CellId = 0x12340001u,
            Position = Vector3.Zero,
            ObjectDescriptionFlags = descFlags,
        };

    [Fact]
    public void For_NullTarget_ReturnsDefault()
    {
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.For(null));
    }

    [Fact]
    public void For_SnapshotWithNullFlags_ReturnsDefault()
    {
        var snap = Snap(0x80000001u, descFlags: null);
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.For(snap));
    }

    [Fact]
    public void For_PortalBitSet_ReturnsPortal()
    {
        var snap = Snap(0x80000002u, descFlags: FlagPortal);
        Assert.Equal(MotorStopRadius.PortalUnits, MotorStopRadius.For(snap));
    }

    [Fact]
    public void For_PortalBitWithOtherBits_ReturnsPortal()
    {
        // Defensive: in real ACE data a Portal can carry other
        // descriptor bits (Attackable, Visible, etc.). The bit-mask
        // test must not require Portal to be the only bit set.
        var snap = Snap(0x80000003u, descFlags: FlagPortal | 0x1u | 0x10u | 0x100u);
        Assert.Equal(MotorStopRadius.PortalUnits, MotorStopRadius.For(snap));
    }

    [Fact]
    public void For_DoorBitOnly_ReturnsDefault()
    {
        var snap = Snap(0x80000004u, descFlags: FlagDoor);
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.For(snap));
    }

    [Fact]
    public void For_CorpseBitOnly_ReturnsDefault()
    {
        var snap = Snap(0x80000005u, descFlags: FlagCorpse);
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.For(snap));
    }

    [Fact]
    public void For_LifeStoneBitOnly_ReturnsDefault()
    {
        // LifeStone is a Use-target (step 5c) but reaches the
        // default radius fine — no stab-obstacle footprint
        // observed in academy data.
        var snap = Snap(0x80000006u, descFlags: FlagLifeStone);
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.For(snap));
    }

    [Fact]
    public void For_OpenableBitOnly_ReturnsDefault()
    {
        // Chest / coffer — step 5b — default radius reaches them.
        var snap = Snap(0x80000007u, descFlags: FlagOpenable);
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.For(snap));
    }

    [Fact]
    public void ForFlags_PortalSet_ReturnsPortal()
    {
        Assert.Equal(MotorStopRadius.PortalUnits, MotorStopRadius.ForFlags(FlagPortal));
    }

    [Fact]
    public void ForFlags_PortalClear_ReturnsDefault()
    {
        // Every non-Portal bit combo we care about should fall to
        // the default arm.
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.ForFlags(0u));
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.ForFlags(FlagDoor));
        Assert.Equal(MotorStopRadius.DefaultUnits, MotorStopRadius.ForFlags(FlagDoor | FlagCorpse | FlagLifeStone | FlagOpenable));
    }

    [Fact]
    public void PortalUnits_IsStrictlyGreaterThanDefaultUnits()
    {
        // Relationship invariant — the helper exists precisely
        // because portal targets need a wider stop envelope than
        // the default. If a future refactor accidentally inverts
        // these, the walk-tick regresses to the
        // portal01-spike.log:9655 BLOCKED-at-3u failure.
        Assert.True(MotorStopRadius.PortalUnits > MotorStopRadius.DefaultUnits,
            $"Expected PortalUnits ({MotorStopRadius.PortalUnits}) > DefaultUnits ({MotorStopRadius.DefaultUnits})");
    }
}
