namespace HeadlessAcClient.World;

/// <summary>
/// Pure decision for classifying a just-applied ObjectCreate as a
/// server-initiated GIVE into the player's own inventory (an NPC
/// quest reward / hand-off).
///
/// Such items arrive as a fresh ObjectCreate whose ContainerGuid is
/// the player's own guid (the server places the item directly in the
/// pack). Unlike looting an item, which the client drives via
/// PutItemInContainer and whose ack emits an InventoryItemAdded
/// StreamEvent, a server give produces no client-driven ack — so
/// without surfacing it here the EventStream never records the
/// acquisition. An Intent completion predicate that counts inventory
/// acquisitions (e.g. <c>+inv name~"...">=N</c>) would then never
/// fire for a give, leaving a quest:talk-to-X intent Active forever
/// while the bot re-Talks the same NPC and accumulates duplicates.
///
/// Encodes no game knowledge (no names / wcids / landblocks /
/// priorities): it keys only on protocol-derived facts — the world
/// accepted the message, the guid was not previously known, the
/// initial login inventory firehose has already flushed, and the new
/// object's container is the player itself.
/// </summary>
public static class InventoryGiveClassifier
{
    /// <summary>
    /// True when a just-applied ObjectCreate should be surfaced as an
    /// InventoryItemAdded acquisition (a server give into our pack).
    /// </summary>
    /// <param name="applied">
    /// Whether <c>WorldState.Apply</c> accepted the message (false for
    /// stale/dropped instance data — nothing was added).
    /// </param>
    /// <param name="preCreateKnown">
    /// Whether the object's guid already existed in WorldState BEFORE
    /// this ObjectCreate was applied. A genuinely new give is unknown;
    /// a looted item's re-broadcast ObjectCreate (which the put-ack
    /// path already surfaced) is already known, so it must not re-emit.
    /// </param>
    /// <param name="initialInventorySettled">
    /// Whether the initial login inventory firehose has flushed. The
    /// items the character already carries at login arrive as
    /// self-container ObjectCreates too; suppressing them until the
    /// firehose settles avoids spurious acquisition events. This must
    /// be a one-shot latch (never reset by a later teleport
    /// LoginComplete resend) so a give right after a teleport still
    /// counts.
    /// </param>
    /// <param name="selfGuid">The player's own object guid, if known.</param>
    /// <param name="containerGuid">
    /// The applied snapshot's ContainerGuid (the object's current
    /// container), if any.
    /// </param>
    public static bool IsServerGive(
        bool applied,
        bool preCreateKnown,
        bool initialInventorySettled,
        uint? selfGuid,
        uint? containerGuid)
        => applied
           && !preCreateKnown
           && initialInventorySettled
           && selfGuid is uint self
           && containerGuid == self;

    /// <summary>
    /// True when the initial login inventory firehose can be
    /// considered flushed, so subsequent fresh self-container
    /// ObjectCreates may be treated as server gives. Requires BOTH the
    /// established post-LoginComplete packet grace AND that
    /// self-inventory ObjectCreates have been quiet for a grace window
    /// (or none have arrived) — so a firehose that runs longer than the
    /// raw packet grace cannot leak starter items as false gives.
    /// </summary>
    /// <remarks>
    /// The caller must record <paramref name="lastSelfInventoryCreatePacketIndex"/>
    /// for the current packet BEFORE evaluating this, so that a packet
    /// which is itself a fresh self-inventory create can never flip the
    /// settle latch on its own packet (which would emit that very create
    /// — possibly a slow-firehose starter item — as a spurious give).
    /// The latch therefore flips only on a later quiet packet; the
    /// continuous position/heartbeat stream guarantees such a packet
    /// arrives long before any interaction-driven give.
    /// </remarks>
    public static bool ShouldMarkInventorySettled(
        bool loginCompleteSent,
        int packetIndex,
        int loginCompletePacketIndex,
        int lastSelfInventoryCreatePacketIndex,
        int gracePackets)
        => loginCompleteSent
           && loginCompletePacketIndex >= 0
           && (packetIndex - loginCompletePacketIndex) >= gracePackets
           && (lastSelfInventoryCreatePacketIndex < 0
               || (packetIndex - lastSelfInventoryCreatePacketIndex) >= gracePackets);
}
