namespace HeadlessAcClient.Strategy;

using System.Collections.Generic;

/// <summary>
/// Tracks item guids that the SOURCE autonomously chose to auto-equip
/// (the PHASE7F.4 startup equip-from-inventory pass), so that a server
/// <c>InventoryServerSaveFailed</c> for one of those guids is NOT
/// surfaced to the Strategy layer as a plan-invalidating
/// <c>ActionRejected</c>.
///
/// Why this is needed: PHASE7F.4 is source-autonomous — the motor, not
/// the LLM, decided to wield the item. When the server rejects that
/// autonomous wield (e.g. a level-gated starter cloak →
/// <c>0x420 LevelTooLow</c>), the LLM never requested it, so the failure
/// must not invalidate the LLM's plan and must not pollute the rejection
/// surface (the LLM has been observed mis-attributing such a rejection to
/// its OWN current goal and abandoning it).
///
/// One-shot consume: PHASE7F.4 attempts each guid exactly once (the
/// caller's own dedup set prevents re-attempt), so there is exactly one
/// autonomous failure per marked guid. <see cref="TryConsumeAutonomous"/>
/// returns true exactly once per <see cref="MarkAutonomous"/> and removes
/// the marker, so if the LLM LATER explicitly wields the same item and it
/// fails, that failure surfaces normally (the LLM asked for it that time
/// and should learn). The caller also clears the marker the moment the LLM
/// explicitly takes ownership of the guid via its Wield dispatch (see
/// <see cref="ClearAutonomous"/>) to close the race where the LLM emits a
/// Wield for the same item before the autonomous failure arrives.
///
/// This is pure mechanical bookkeeping that distinguishes a
/// source-autonomous wield from an LLM-requested one purely by which code
/// path issued the request — it encodes no item type, name, wcid, slot, or
/// level value, so it carries no game knowledge.
/// </summary>
internal sealed class AutoEquipFailureFilter
{
    private readonly HashSet<uint> _autonomous = new();

    /// <summary>Record that the source autonomously auto-equipped this guid.</summary>
    public void MarkAutonomous(uint itemGuid) => _autonomous.Add(itemGuid);

    /// <summary>
    /// Clear any autonomous marker for this guid without consuming a
    /// suppression. Call this when the LLM explicitly takes ownership of the
    /// guid (its own Wield dispatch) so a subsequent failure surfaces.
    /// </summary>
    public void ClearAutonomous(uint itemGuid) => _autonomous.Remove(itemGuid);

    /// <summary>
    /// If <paramref name="itemGuid"/> was marked as a source-autonomous
    /// auto-equip, remove the marker and return true (the caller should
    /// suppress the rejection). Otherwise return false (surface normally).
    /// Returns true at most once per <see cref="MarkAutonomous"/>.
    /// </summary>
    public bool TryConsumeAutonomous(uint itemGuid) => _autonomous.Remove(itemGuid);

    /// <summary>
    /// Decide whether an <c>InventoryServerSaveFailed</c> game event should be
    /// surfaced to the Strategy layer as an <c>ActionRejected</c> learning
    /// signal. A non-zero (specific) <paramref name="errorType"/> always
    /// surfaces. A <c>None</c> (0) error surfaces ONLY when it names the
    /// item the bot is currently giving (<paramref name="pendingGiveItemGuid"/>),
    /// an item the bot has dispatched a wield/equip for
    /// (<paramref name="wieldDispatchedGuids"/>), or an item the bot has dispatched
    /// a <c>Pickup</c> for (<paramref name="pickupDispatchedGuids"/>):
    /// <list type="bullet">
    /// <item>Give: the server refuses a Give it cannot complete (a held quest
    /// item the bot believes it holds is not in the server's inventory) with a
    /// transient string + err=None; dropping it left a failing Give with no
    /// signal so the bot silently re-dispatched the same give.</item>
    /// <item>Wield: the server's CheckWeaponCollision refuses to wield a weapon
    /// while another is equipped with a silent err=None; without a signal the bot
    /// re-emitted the same wield many times.</item>
    /// <item>Pickup: the server refuses a Pickup of a non-takeable item (a fixed
    /// in-world object) with a silent err=None; the failed pickup's queued
    /// auto-equip never fires (the pickup-ack never arrives) so the guid never
    /// enters <paramref name="wieldDispatchedGuids"/>, and without this the bot
    /// re-emitted the SAME Pickup every cycle with no learning signal.</item>
    /// </list>
    /// Other benign None failures (e.g. a source-autonomous auto-equip teardown)
    /// match none of the sets and stay suppressed; an autonomous auto-equip whose
    /// guid IS in the wield set still gets dropped one-shot by
    /// <see cref="TryConsumeAutonomous"/> downstream. Pure: keyed on the wire
    /// error code + the in-flight give/wield/pickup guids; carries no item type,
    /// name, wcid, or game knowledge.
    /// </summary>
    public static bool ShouldSurfaceInventoryFailure(
        uint errorType, uint itemGuid, uint? pendingGiveItemGuid,
        IReadOnlySet<uint> wieldDispatchedGuids,
        IReadOnlySet<uint> pickupDispatchedGuids)
        => errorType != 0
           || (pendingGiveItemGuid is uint pg && pg == itemGuid)
           || wieldDispatchedGuids.Contains(itemGuid)
           || pickupDispatchedGuids.Contains(itemGuid);
}
