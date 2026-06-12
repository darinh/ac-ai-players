// SPDX-License-Identifier: AGPL-3.0-or-later
// EntityClassifier — single source of truth for the wire-derived
// monster composite and the coarse sighting kind.
//
// Why this exists: the IsMonster composite was computed inline in
// WorldStateProjection (for the live "## Visible nearby" / combat
// projection) and the sighting-record path in HandshakeDriver left
// the kind Unknown. Surfacing remembered out-of-view monsters to the
// LLM needs the SAME classification at record time, so this helper
// centralizes it — both call sites reduce to identical wire bits and
// cannot drift.
//
// Audit note: this is PERCEPTION/PROJECTION, not strategy. It decodes
// wire bits (ItemType creature mask, ObjectDescriptionFlag, the
// RadarBlipColor weenie-header flag) into a named category, exactly
// like IsContainer / IsDoor. It assigns NO priority or urgency to any
// category — the LLM owns all targeting decisions.

using HeadlessAcClient.Protocol.GameMessages;

namespace HeadlessAcClient.Strategy;

internal static class EntityClassifier
{
    /// <summary>
    /// Wire-derived monster composite: a creature that is attackable,
    /// carries no friendly radar-blip color, and is not a vendor,
    /// healer, or corpse. Matches the inline predicate previously in
    /// WorldStateProjection so the visible projection and sighting
    /// memory share one definition.
    /// </summary>
    public static bool IsMonster(uint itemType, uint descFlags, uint weenieFlags)
    {
        var isCreature       = (itemType    & ItemTypeMasks.Creature)                  != 0;
        var isAttackable     = (descFlags   & (uint)ObjectDescriptionFlag.Attackable)  != 0;
        var isVendor         = (descFlags   & (uint)ObjectDescriptionFlag.Vendor)      != 0;
        var isHealer         = (descFlags   & (uint)ObjectDescriptionFlag.Healer)      != 0;
        var isCorpse         = (descFlags   & (uint)ObjectDescriptionFlag.Corpse)      != 0;
        var hasRadarBlipColor = (weenieFlags & (uint)WeenieHeaderFlag.RadarBlipColor)  != 0;
        return isCreature && isAttackable && !hasRadarBlipColor && !isVendor && !isHealer && !isCorpse;
    }

    /// <summary>
    /// Coarse wire-derived kind for sighting memory. Only distinguishes
    /// the categories the out-of-view recall prompt section surfaces:
    /// <see cref="EntityKind.Mob"/> (the IsMonster composite holds),
    /// <see cref="EntityKind.NPC"/> (a creature that is not a monster —
    /// vendor / healer / quest-giver), <see cref="EntityKind.Portal"/> (a
    /// non-creature carrying the wire Portal flag — an area transition the
    /// recall surfaces so the LLM can return to it by name), else
    /// <see cref="EntityKind.Unknown"/> for other non-creatures (items,
    /// doors) the recall section does not list. This is a perception label,
    /// not a priority.
    /// </summary>
    public static EntityKind ClassifySighting(uint itemType, uint descFlags, uint weenieFlags)
    {
        // A portal is a non-creature wire category (ObjectDescriptionFlag.Portal —
        // the same bit WorldStateProjection and the Motor stop-radius decode). The
        // out-of-view recall surfaces it so the LLM can return to a remembered area
        // transition by name. Decoded bit, not game knowledge — checked before the
        // creature gate because a portal is never a creature.
        if ((descFlags & (uint)ObjectDescriptionFlag.Portal) != 0)
            return EntityKind.Portal;
        var isCreature = (itemType & ItemTypeMasks.Creature) != 0;
        if (!isCreature) return EntityKind.Unknown;
        return IsMonster(itemType, descFlags, weenieFlags) ? EntityKind.Mob : EntityKind.NPC;
    }
}
