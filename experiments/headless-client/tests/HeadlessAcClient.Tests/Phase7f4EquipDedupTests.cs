// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for Phase7f4EquipDedup — the pure per-slot de-duplication decision for the
// login auto-equip pass. The stateful wiring (mark-on-send, release-on-ack/reject)
// lives in HandshakeDriver's observe loop (entry code, not unit-tested by
// convention); the skip decision + the enable flag are what's testable here.

using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using Xunit;

namespace HeadlessAcClient.Tests;

public class Phase7f4EquipDedupTests
{
    // ---- ResolveSerializeEnabled -------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("anything-else")]
    public void ResolveSerializeEnabled_DefaultsAndTruthyValues_Enabled(string? env)
    {
        Assert.True(Phase7f4EquipDedup.ResolveSerializeEnabled(env));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("no")]
    [InlineData("  no  ")]
    public void ResolveSerializeEnabled_DisableValues_Disabled(string env)
    {
        Assert.False(Phase7f4EquipDedup.ResolveSerializeEnabled(env));
    }

    // ---- SlotOccupied ------------------------------------------------------

    private static ISet<uint> Slots(params uint[] s) => new HashSet<uint>(s);
    private static ICollection<uint> InFlight(params uint[] s) => new List<uint>(s);

    [Fact]
    public void SlotOccupied_ZeroSlot_NeverBlocked()
    {
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0, isSingleSlot: true, Slots(0), InFlight(0), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SatisfiedSlot_Blocked()
    {
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x100000, isSingleSlot: true, Slots(0x100000), InFlight(), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SingleSlot_InFlight_SerializeOn_Blocked()
    {
        // The main win: a second single-slot item is skipped while the first is
        // still in flight (before any ack), serializing the login burst.
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x100000, isSingleSlot: true, Slots(), InFlight(0x100000), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SingleSlot_InFlight_SerializeOff_NotBlocked()
    {
        // Serialization disabled -> in-flight mark ignored (byte-identical to the
        // prior ack-only dedup).
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x100000, isSingleSlot: true, Slots(), InFlight(0x100000), serializeEnabled: false));
    }

    [Fact]
    public void SlotOccupied_MultiSlot_InFlight_NotBlocked()
    {
        // Regression guard: a MULTI-slot item (e.g. a ring, whose lowest bit is in
        // flight) is NOT blocked by the in-flight serialization — it could still
        // equip in another of its slots, so its behaviour is unchanged.
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x40000, isSingleSlot: false, Slots(), InFlight(0x40000), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_MultiSlot_Satisfied_StillBlocked()
    {
        // The pre-existing ack-based satisfied check applies to ALL items (single
        // and multi-slot) — unchanged by this slice.
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x40000, isSingleSlot: false, Slots(0x40000), InFlight(), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SatisfiedStillBlocks_WhenSerializeOff()
    {
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x2, isSingleSlot: true, Slots(0x2), InFlight(), serializeEnabled: false));
    }

    [Fact]
    public void SlotOccupied_FreeSlot_NotBlocked()
    {
        // A different slot than the occupied/in-flight ones is allowed (e.g. a
        // shield 0x400000 is not blocked by a wielded weapon 0x100000).
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x400000, isSingleSlot: true, Slots(0x100000), InFlight(0x8000), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_DistinctSlots_EachIndependent()
    {
        var satisfied = Slots(0x100000); // weapon worn
        var inflight = InFlight(0x2);    // chest wield sent
        Assert.True(Phase7f4EquipDedup.SlotOccupied(0x100000, true, satisfied, inflight, true));  // weapon blocked (worn)
        Assert.True(Phase7f4EquipDedup.SlotOccupied(0x2, true, satisfied, inflight, true));       // chest blocked (in flight)
        Assert.False(Phase7f4EquipDedup.SlotOccupied(0x8000, true, satisfied, inflight, true));   // neck free
    }

    // ---- IsSingleSlot ------------------------------------------------------

    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x2u)]
    [InlineData(0x8000u)]
    [InlineData(0x100000u)]
    [InlineData(0x80000000u)]
    public void IsSingleSlot_SingleBit_True(uint validLocations)
    {
        Assert.True(Phase7f4EquipDedup.IsSingleSlot(validLocations));
    }

    [Theory]
    [InlineData(0u)]            // no slot at all
    [InlineData(0x3u)]          // two low bits
    [InlineData(0xC0000u)]      // two finger bits (a ring)
    [InlineData(0x30000u)]      // two wrist bits (a bracelet)
    [InlineData(0xFFFFFFFFu)]   // everything
    public void IsSingleSlot_ZeroOrMultiBit_False(uint validLocations)
    {
        Assert.False(Phase7f4EquipDedup.IsSingleSlot(validLocations));
    }
}
