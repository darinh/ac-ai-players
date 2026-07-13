// SPDX-License-Identifier: AGPL-3.0-or-later
// Phase7f4EquipDedup — per-slot de-duplication for the autonomous login/inventory
// auto-equip pass (HandshakeDriver PHASE7F.4).
//
// Why this exists: the login pass sends one GetAndWieldItem per tick for every
// owned item whose ValidLocations is non-zero. Many owned items compete for the
// SAME wield slot (a whole loot collection of weapons for the single main-hand
// slot; several shirts for the one chest slot). The server accepts one item per
// slot and rejects the rest with a silent InventoryServerSaveFailed (err=None).
//
// The existing dedup (satisfiedEquipSlots) is ACK-based: a slot is only marked
// occupied once its WieldObject ack arrives. But the pass sends faster than acks
// return, so during the pre-ack window it still fires a wield for every same-slot
// item before the ack-based dedup engages — one login produces dozens of doomed
// InventoryServerSaveFailed round-trips (plus their retries).
//
// This adds an OPTIMISTIC layer: the caller marks a slot in-flight the moment it
// SENDS a wield for it, and releases the mark on the item's ack (the slot is now
// satisfied/worn) or its InventoryServerSaveFailed (the slot is still free, so the
// next same-slot candidate may try). That serializes each slot to one in-flight
// wield, so the doomed duplicates are never sent.
//
// This is purely mechanical de-duplication: the WORN OUTCOME is unchanged — the
// first item the server accepts per slot still wins — only the wasted parallel
// sends are removed. No item preference, no game knowledge.

using System;
using System.Collections.Generic;

namespace HeadlessAcClient.Strategy;

internal static class Phase7f4EquipDedup
{
    /// <summary>
    /// Whether the optimistic in-flight serialization is enabled. Default ON;
    /// env AC_BOTS_PHASE7F4_SLOT_SERIALIZE = 0/false/off/no disables it (reverting
    /// to the ack-only <c>satisfiedEquipSlots</c> dedup, byte-identical behaviour).
    /// </summary>
    internal static bool ResolveSerializeEnabled(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue)) return true; // default ON
        var v = envValue.Trim();
        return !(string.Equals(v, "0", StringComparison.Ordinal)
              || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
              || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)
              || string.Equals(v, "no", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True iff a candidate targeting <paramref name="chosenSlot"/> should be
    /// SKIPPED this tick because the slot is already spoken for:
    ///   - it is SATISFIED (an item was successfully wielded there — ack-based), or
    ///   - (when <paramref name="serializeEnabled"/> AND the item is single-slot) a
    ///     wield for that slot is already IN FLIGHT (sent, not yet acked/rejected).
    ///
    /// The in-flight skip is gated on <paramref name="isSingleSlot"/> — an item
    /// whose ValidLocations has exactly one bit can ONLY go in <paramref name="chosenSlot"/>,
    /// so a duplicate while that slot is in flight is definitively doomed. A
    /// MULTI-slot item (a ring/bracelet with two finger/wrist bits) might still
    /// equip in another of its slots, so its lowest-bit being in flight does not
    /// make it redundant; its behaviour is left unchanged (only the pre-existing
    /// ack-based satisfied check applies). A zero slot is never blocked. Pure set
    /// membership; no side effects. The worn outcome is unchanged.
    /// </summary>
    internal static bool SlotOccupied(
        uint chosenSlot,
        bool isSingleSlot,
        ISet<uint> satisfiedSlots,
        ICollection<uint> inFlightSlots,
        bool serializeEnabled)
    {
        if (chosenSlot == 0) return false;
        if (satisfiedSlots.Contains(chosenSlot)) return true;
        if (serializeEnabled && isSingleSlot && inFlightSlots.Contains(chosenSlot)) return true;
        return false;
    }

    /// <summary>
    /// True iff <paramref name="validLocations"/> has exactly one bit set — the item
    /// can be equipped in only ONE slot (a weapon, chest, neck, ...). A multi-bit
    /// value (a ring's two finger slots) returns false. Zero returns false.
    /// </summary>
    internal static bool IsSingleSlot(uint validLocations)
        => validLocations != 0 && (validLocations & (validLocations - 1)) == 0;
}
