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
            0, isSingleSlot: true, Slots(0), wornSlotsMask: 0x2, InFlight(0), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SatisfiedSlot_Blocked()
    {
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x100000, isSingleSlot: true, Slots(0x100000), wornSlotsMask: 0, InFlight(), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SingleSlot_WornSlot_Blocked()
    {
        // The new win: a single-slot bag item (chest 0x2) is skipped when the chest
        // slot is already occupied by worn starter gear (wornSlotsMask has 0x2).
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x2, isSingleSlot: true, Slots(), wornSlotsMask: 0x2, InFlight(), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SingleSlot_WornSlot_SerializeOff_NotBlocked()
    {
        // Serialization disabled -> the worn-slot skip is off too (byte-identical to
        // the prior ack-only dedup).
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x2, isSingleSlot: true, Slots(), wornSlotsMask: 0x2, InFlight(), serializeEnabled: false));
    }

    [Fact]
    public void SlotOccupied_MultiSlot_WornSlot_NotBlocked()
    {
        // Regression guard: a MULTI-slot item whose lowest bit is worn is NOT blocked
        // (it might equip in another of its slots) — the worn-slot skip is single-slot only.
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x40000, isSingleSlot: false, Slots(), wornSlotsMask: 0x40000, InFlight(), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SingleSlot_InFlight_SerializeOn_Blocked()
    {
        // A second single-slot item is skipped while the first is still in flight.
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x100000, isSingleSlot: true, Slots(), wornSlotsMask: 0, InFlight(0x100000), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SingleSlot_InFlight_SerializeOff_NotBlocked()
    {
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x100000, isSingleSlot: true, Slots(), wornSlotsMask: 0, InFlight(0x100000), serializeEnabled: false));
    }

    [Fact]
    public void SlotOccupied_MultiSlot_InFlight_NotBlocked()
    {
        // A MULTI-slot item (ring, lowest bit in flight) is NOT blocked by the
        // in-flight serialization — could equip in another slot.
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x40000, isSingleSlot: false, Slots(), wornSlotsMask: 0, InFlight(0x40000), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_MultiSlot_Satisfied_StillBlocked()
    {
        // The pre-existing ack-based satisfied check applies to ALL items.
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x40000, isSingleSlot: false, Slots(0x40000), wornSlotsMask: 0, InFlight(), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_SatisfiedStillBlocks_WhenSerializeOff()
    {
        Assert.True(Phase7f4EquipDedup.SlotOccupied(
            0x2, isSingleSlot: true, Slots(0x2), wornSlotsMask: 0, InFlight(), serializeEnabled: false));
    }

    [Fact]
    public void SlotOccupied_FreeSlot_NotBlocked()
    {
        // A slot not satisfied/worn/in-flight is allowed (a shield 0x400000 is not
        // blocked by a worn shirt 0x2, a wielded weapon 0x100000, or a neck in flight).
        Assert.False(Phase7f4EquipDedup.SlotOccupied(
            0x400000, isSingleSlot: true, Slots(0x100000), wornSlotsMask: 0x2, InFlight(0x8000), serializeEnabled: true));
    }

    [Fact]
    public void SlotOccupied_DistinctSlots_EachIndependent()
    {
        var satisfied = Slots(0x100000); // weapon worn (this session)
        uint worn = 0x2;                 // chest worn (starter gear)
        var inflight = InFlight(0x8000); // neck wield sent
        Assert.True(Phase7f4EquipDedup.SlotOccupied(0x100000, true, satisfied, worn, inflight, true)); // weapon blocked (satisfied)
        Assert.True(Phase7f4EquipDedup.SlotOccupied(0x2, true, satisfied, worn, inflight, true));       // chest blocked (worn)
        Assert.True(Phase7f4EquipDedup.SlotOccupied(0x8000, true, satisfied, worn, inflight, true));    // neck blocked (in flight)
        Assert.False(Phase7f4EquipDedup.SlotOccupied(0x4, true, satisfied, worn, inflight, true));      // legs free
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
