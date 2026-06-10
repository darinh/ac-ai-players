// SPDX-License-Identifier: AGPL-3.0-or-later
// MotorPostActionCooldown unit tests — wall-clock delay between
// USE dispatch and motion-state reset. Audit-safe INVARIANT shape
// (mirrors MotorStopRadiusTests): we assert the bit-decode and the
// arithmetic relationship between Default and PortalWindup, NOT
// any particular game-knowledge claim about portal weenies.
//
// What we test:
//   - For(null) returns Default (defensive fallback for callers
//     that hit the cooldown gate with no motionTarget — the gate
//     should not run in that state, but if it does the legacy 2s
//     behaviour is preserved).
//   - A snapshot with no ObjectDescriptionFlags returns Default.
//   - A snapshot whose ObjectDescriptionFlags has the Portal bit
//     set returns PortalWindup (regardless of other bits).
//   - A snapshot whose flags carry any non-Portal mix (Door,
//     Corpse, LifeStone, Openable) returns Default. This is
//     critical: doors USE quickly, lifestones return UseDone
//     synchronously, chests/coffers spawn loot items in the same
//     packet train — none of them need the extended hold.
//   - ForFlags is the same predicate as For (bit-level equivalence)
//     for both Portal-set and Portal-clear inputs.
//   - PortalWindup is strictly greater than Default (relationship
//     invariant — the helper exists precisely because portal USE
//     needs a wider hold than the default).
//   - Default is exactly 2 seconds (matches the legacy
//     PostActionCooldownSec int the helper replaced; if it drifts
//     we want a test failure to make the change deliberate).

using System;
using System.Numerics;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;
using Xunit;

namespace HeadlessAcClient.Tests;

public class MotorPostActionCooldownTests
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
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.For(null));
    }

    [Fact]
    public void For_SnapshotWithNullFlags_ReturnsDefault()
    {
        var snap = Snap(0x80000001u, descFlags: null);
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.For(snap));
    }

    [Fact]
    public void For_PortalBitSet_ReturnsPortalWindup()
    {
        var snap = Snap(0x80000002u, descFlags: FlagPortal);
        Assert.Equal(MotorPostActionCooldown.PortalWindup, MotorPostActionCooldown.For(snap));
    }

    [Fact]
    public void For_PortalBitWithOtherBits_ReturnsPortalWindup()
    {
        // Defensive: in real ACE data a Portal can carry other
        // descriptor bits (Attackable, Visible, etc.). The bit-mask
        // test must not require Portal to be the only bit set.
        var snap = Snap(0x80000003u, descFlags: FlagPortal | 0x1u | 0x10u | 0x100u);
        Assert.Equal(MotorPostActionCooldown.PortalWindup, MotorPostActionCooldown.For(snap));
    }

    [Fact]
    public void For_DoorBitOnly_ReturnsDefault()
    {
        // Doors USE quickly (single Open animation, no server-driven
        // windup); they do NOT need the portal hold. If they did
        // the existing door-USE loop (cd8ac21) would already be
        // broken.
        var snap = Snap(0x80000004u, descFlags: FlagDoor);
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.For(snap));
    }

    [Fact]
    public void For_CorpseBitOnly_ReturnsDefault()
    {
        // Corpses USE synchronously (open container event arrives
        // immediately); loot extraction (Slice Q) needs the 2s
        // window to discover spawned items.
        var snap = Snap(0x80000005u, descFlags: FlagCorpse);
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.For(snap));
    }

    [Fact]
    public void For_LifeStoneBitOnly_ReturnsDefault()
    {
        // Lifestone USE returns UseDone synchronously — no windup.
        var snap = Snap(0x80000006u, descFlags: FlagLifeStone);
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.For(snap));
    }

    [Fact]
    public void For_OpenableBitOnly_ReturnsDefault()
    {
        // Chests / coffers — step 5b — synchronous open + loot
        // spawn.
        var snap = Snap(0x80000007u, descFlags: FlagOpenable);
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.For(snap));
    }

    [Fact]
    public void ForFlags_PortalSet_ReturnsPortalWindup()
    {
        Assert.Equal(MotorPostActionCooldown.PortalWindup, MotorPostActionCooldown.ForFlags(FlagPortal));
    }

    [Fact]
    public void ForFlags_PortalClear_ReturnsDefault()
    {
        // Every non-Portal bit combo we care about should fall to
        // the default arm.
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.ForFlags(0u));
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.ForFlags(FlagDoor));
        Assert.Equal(MotorPostActionCooldown.Default, MotorPostActionCooldown.ForFlags(FlagDoor | FlagCorpse | FlagLifeStone | FlagOpenable));
    }

    [Fact]
    public void PortalWindup_IsStrictlyGreaterThanDefault()
    {
        // Relationship invariant — the helper exists precisely
        // because portal targets need a wider post-USE hold than
        // the default. If a future refactor accidentally inverts
        // these, the walk-tick regresses to the
        // portal03-spike.log:20227..20236 windup-cancellation
        // failure.
        Assert.True(MotorPostActionCooldown.PortalWindup > MotorPostActionCooldown.Default,
            $"Expected PortalWindup ({MotorPostActionCooldown.PortalWindup}) > Default ({MotorPostActionCooldown.Default})");
    }

    [Fact]
    public void Default_IsExactlyTwoSeconds()
    {
        // Pin the default to the prior PostActionCooldownSec int.
        // If a future change wants to move the default, force the
        // change to be deliberate by failing this test.
        Assert.Equal(TimeSpan.FromSeconds(2), MotorPostActionCooldown.Default);
    }

    [Fact]
    public void PortalWindup_IsAtLeastFiveSeconds()
    {
        // AC portal windup is empirically ~3-5s (USE ack → server
        // MoveToPosition pin → animation → Teleport packet). The
        // helper exists to cover the upper end of that range with
        // a small margin for jitter; anything shorter than 5s
        // risks cancelling slow windups. Pin the lower bound so a
        // future refactor cannot silently shrink this back toward
        // the broken 2s default.
        Assert.True(MotorPostActionCooldown.PortalWindup >= TimeSpan.FromSeconds(5),
            $"Expected PortalWindup ({MotorPostActionCooldown.PortalWindup}) >= 5 seconds");
    }

    [Fact]
    public void NonInteractArrival_IsZero()
    {
        // An Explore arrival dispatches NO opcode — pure movement that
        // reached its waypoint — so there is no server reply/animation to
        // await. The hold is zero so the motor resets and picks the next goal
        // immediately, removing the ~2s idle the Explore short-circuit
        // inherited from reusing the useSent-gated reset cascade.
        Assert.Equal(TimeSpan.Zero, MotorPostActionCooldown.NonInteractArrival);
    }

    [Fact]
    public void NonInteractArrival_IsLessThanDefault()
    {
        // Relationship invariant: a non-interact (Explore) arrival must NOT
        // wait as long as an interact (USE/PICKUP/Talk/Give) cycle, which holds
        // for the server's action reply. If a refactor raised NonInteractArrival
        // to >= Default the Explore tempo win would silently regress.
        Assert.True(MotorPostActionCooldown.NonInteractArrival < MotorPostActionCooldown.Default,
            $"Expected NonInteractArrival ({MotorPostActionCooldown.NonInteractArrival}) < Default ({MotorPostActionCooldown.Default})");
    }
}
