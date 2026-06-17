// SPDX-License-Identifier: AGPL-3.0-or-later
// SwapRejectionAttribution — re-attribute a deferred-wield prerequisite
// rejection from the BLOCKING item to the TARGET item the LLM asked to wield.
//
// Why this exists: to wield an item that needs an occupied slot freed, the Motor
// first dequips the currently-occupying item (the "blocker") via
// PutItemInContainer and defers the wield to the dequip's put-ack. If the server
// rejects that dequip, the put-ack never arrives and the deferred wield can
// never complete. The rejection arrives keyed on the BLOCKING item, but the
// policy's recently-rejected dedup keys a wield goal by its TARGET name/wcid — so
// a blocker-keyed rejection never dedups the repeated wield goal and it is
// re-emitted every cycle.
//
// This pure helper decides, from the in-flight deferred-wield map, whether a
// rejected item guid is such a blocker and, if so, how to re-attribute the
// rejection to the TARGET item (its name/wcid + a human-readable reason) so the
// dedup keys align and the policy re-deliberates. It is pure mechanical
// bookkeeping keyed on the wire rejection guid + the map; it encodes no item
// type, name, wcid, slot, or game knowledge — the target name/wcid are looked up
// at runtime by the caller-supplied resolver, and the caller is responsible for
// passing only FRESH in-flight entries (a stale/abandoned swap must not be
// re-attributed).

using System;

namespace HeadlessAcClient.Strategy;

internal static class SwapRejectionAttribution
{
    /// <summary>
    /// How to re-attribute a swap-blocker dequip rejection to the TARGET weapon
    /// whose wield it blocks: the target guid, its display name and wcid (for
    /// the policy's name/wcid-keyed dedup), and a human-readable reason text.
    /// </summary>
    public readonly record struct Attribution(
        uint TargetGuid, string Name, uint? Wcid, string Text);

    /// <summary>
    /// If <paramref name="rejectedItemGuid"/> is a pending dequip-for-swap
    /// blocker, return how to re-attribute its rejection to the TARGET weapon
    /// whose wield it blocks; otherwise return <c>null</c> (the caller surfaces
    /// the rejection normally, attributed to the rejected item itself).
    /// </summary>
    /// <param name="rejectedItemGuid">The guid the server rejected.</param>
    /// <param name="swapTargetForBlocker">Maps a blocker guid to the guid of the
    /// weapon whose wield it blocks, or null when the guid is not a pending swap
    /// blocker (typically a lookup into the in-flight dequip-for-swap map).</param>
    /// <param name="resolveTarget">Resolves a target guid to its current display
    /// name and wcid from the live world projection.</param>
    public static Attribution? ForRejectedBlocker(
        uint rejectedItemGuid,
        Func<uint, uint?> swapTargetForBlocker,
        Func<uint, (string? Name, uint? Wcid)> resolveTarget)
    {
        if (swapTargetForBlocker(rejectedItemGuid) is not uint targetGuid)
            return null;

        var (name, wcid) = resolveTarget(targetGuid);
        var targetName = string.IsNullOrWhiteSpace(name) ? "(unknown)" : name!;
        // The reason text CONTAINS the target name so the policy's substring
        // fallback match also resolves to the target; the Name/Wcid fields are
        // the primary, precise dedup keys.
        var text =
            $"Could not wield '{targetName}': the currently-equipped weapon could " +
            "not be unequipped to free the slot.";
        return new Attribution(targetGuid, targetName, wcid, text);
    }
}
