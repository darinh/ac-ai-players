// SPDX-License-Identifier: AGPL-3.0-or-later
// SightingRecordPolicy — decides, for a freshly-observed ObjectCreate,
// WHICH of the two per-bot sighting memories to write, based purely on
// the landblock distance between the observed object and the bot.
//
// Two memories exist and have different coordinate-frame requirements:
//
//   - RecordObservation (semantic recall) stores the entity position
//     RELATIVE to the bot's current nav node (rel = entityPos -
//     node.Position). That subtraction is only meaningful when the
//     entity shares the bot's landblock-LOCAL frame, so it is written
//     ONLY for a same-landblock sighting. Writing it for an adjacent
//     landblock would apply no 192u landblock offset and yield a garbage
//     relative vector; post-teleport it could also anchor a destination
//     object to a stale node (the original reason the exact-landblock
//     gate was introduced).
//
//   - RecordSightedLocation (FOV location memory) stores the entity's
//     ABSOLUTE cell + cell-local position (landblock-offset aware via
//     AcCoords). It is correct for a same-OR-adjacent landblock sighting
//     and is what the cross-landblock Attack/Explore resolver reads to
//     route toward a monster seen one landblock away. The observer node
//     is provenance only; for a non-same-landblock sighting it lives in
//     a different landblock frame, so no observer node is anchored.
//
// This is pure wire-coordinate geometry: it assigns no priority, knows
// no object types/names/wcids, and makes no autonomous target choice.

namespace HeadlessAcClient.World;

internal static class SightingRecordPolicy
{
    /// <summary>
    /// The recording decision for one observed object.
    /// </summary>
    /// <param name="RecordObservation">
    /// Write the node-relative semantic observation (same-landblock only).
    /// </param>
    /// <param name="RecordSightedLocation">
    /// Write the absolute FOV location memory (same-or-adjacent landblock).
    /// </param>
    /// <param name="AnchorObserverNode">
    /// Attach the bot's current node as the sighting's observer/provenance
    /// node. True only for a same-landblock sighting; for an adjacent
    /// sighting the node is in a different frame, so it is left unset.
    /// </param>
    public readonly record struct Decision(
        bool RecordObservation,
        bool RecordSightedLocation,
        bool AnchorObserverNode);

    /// <summary>
    /// Decide which sighting memories to write for an object observed in
    /// <paramref name="objectCellId"/> while the bot is in
    /// <paramref name="selfCellOrLandblock"/> (either a full cell id or an
    /// already-masked landblock — only the landblock bytes matter).
    /// </summary>
    public static Decision Decide(uint objectCellId, uint selfCellOrLandblock)
    {
        bool sameLandblock =
            (objectCellId & 0xFFFF0000u) == (selfCellOrLandblock & 0xFFFF0000u);
        bool sameOrAdjacent =
            WorldDistance.IsSameOrAdjacentLandblock(objectCellId, selfCellOrLandblock);
        return new Decision(
            RecordObservation: sameLandblock,
            RecordSightedLocation: sameOrAdjacent,
            AnchorObserverNode: sameLandblock);
    }
}
