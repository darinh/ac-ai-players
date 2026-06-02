// SPDX-License-Identifier: AGPL-3.0-or-later
// MotorStopRadius — terminal arrival radius for the walk-tick.
//
// Role: pick the XY distance at which the motor declares "we have
// arrived at the motion target" so PHASE5B STOP and PHASE6E/6F USE
// can fire. Per-target because different target classes have
// different physical footprints and different server-side use-
// radii — the 1.0u default works for items, NPCs, and signs (where
// the entity's authoritative position is reachable and ACE's
// default UseRadius=0.6u plus the cylinder-distance fudge is the
// real bound) but does NOT work for Portals (where LandblockNavLoader
// projects the portal's stab SetupModel into a ~3u CylSphere static
// obstacle in the cell's StaticObstacles list; every walkable-node
// sample within that footprint is dropped, so the bot's IndoorNav
// path ends ~3u short and the final-approach AP gets clamped by
// the server against the portal's own collision — the failure
// captured at L9655 of files/portal01-spike.log on 2026-05-30 as
// `[motion] walk-tick: BLOCKED for 3 consecutive ticks — target
// 0x78602052 'Central Courtyard' is unreachable from current
// position; stopping motion`).
//
// AUDIT FRAMING (.github/skills/audit-hardcoded-knowledge/SKILL.md):
// This is MECHANICAL motor configuration, NOT game knowledge.
//   - The IsPortal predicate is a pure wire-bit decode on
//     ObjectDescriptionFlag.Portal (0x40000) from the server's
//     ObjectCreate header. No name, wcid, weenie, or landblock
//     matching. Symmetric to the existing IsDoor / IsCorpse /
//     IsOpenable / IsLifeStone bit decodes already used by both
//     the picker and the door-USE pre-emptor.
//   - The strategy layer (LLM or NoQuestKnowledgePolicy step 5d)
//     has ALREADY selected GoalKind.Use against this exact guid.
//     The motor is only choosing the physical dispatch point
//     because collision prevents reaching the default 1.0u stop
//     radius. We are not adding a new interaction the LLM did not
//     ask for; we are letting the LLM's existing Use{Portal} goal
//     complete.
//   - The PortalRadiusUnits=4.0f constant is an empirical match
//     to the portal stab obstacle radius observed in academy
//     landblock 0x8602 (the only obstacle class where every
//     walkable sample around the entity's position is rejected by
//     PointInsideAnyObstacleXY). It provides sufficient margin for
//     the server-side IsWithinUseRadiusOf cylinder check while
//     remaining outside the stab obstacle, so PHASE6E/6F USE
//     dispatches cleanly. If a future portal class has a smaller
//     server-side UseRadius the dispatch will get rejected and the
//     existing ActionRejected dedup catches it (same recovery
//     path as any other USE rejection).
//   - This is the same shape as the door-USE pre-emptor's
//     DoorMatchRadiusUnits=3.0u + DoorUseCooldownSeconds=30.0 in
//     HandshakeDriver.cs:3877-3878, both classified MECHANICAL by
//     the audit on commit ec8f0d2.
//
// If a future Use-target class has an even larger collision
// footprint (e.g. a giant portcullis or a multi-tile chest) it
// belongs as a new branch here, not in autonomous picker logic.

namespace HeadlessAcClient.Strategy;

using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;

internal static class MotorStopRadius
{
    /// <summary>
    /// Default XY radius from the target's authoritative position at
    /// which the walk-tick declares arrival. Tuned to put the bot
    /// inside ACE's default <c>WorldObject.UseRadius=0.6u</c> plus
    /// the cylinder-distance fudge.
    /// </summary>
    public const float DefaultUnits = 1.0f;

    /// <summary>
    /// XY radius for targets carrying the
    /// <see cref="ObjectDescriptionFlag.Portal"/> bit. Bumped from
    /// <see cref="DefaultUnits"/> because the portal's stab
    /// SetupModel produces a ~3u CylSphere static obstacle in the
    /// cell mesh — walkable-node sampling rejects every point inside
    /// that footprint, so the IndoorNav path ends ~3u short of the
    /// portal's authoritative position and the final-approach step
    /// gets clamped by server physics. Stopping at 4u lets PHASE5B
    /// STOP fire at the closest reachable position and PHASE6E/6F
    /// USE dispatch <see cref="GameActionUseMessage"/> with the
    /// portal's guid; server-side
    /// <c>WorldObject.IsWithinUseRadiusOf</c> uses cylinder-distance
    /// (XY-only with bounding-cylinder radii) and 4u provides
    /// sufficient margin for that check while staying outside the
    /// stab obstacle.
    /// </summary>
    public const float PortalUnits = 4.0f;

    /// <summary>
    /// Per-target stop radius for the walk-tick. Returns
    /// <see cref="PortalUnits"/> for Portal entities (pure wire-bit
    /// predicate on <see cref="ObjectDescriptionFlag.Portal"/>) and
    /// <see cref="DefaultUnits"/> for everything else. Null target
    /// returns <see cref="DefaultUnits"/> as a defensive fallback
    /// (the walk-tick guard chain should never reach here with a
    /// null motion target).
    /// </summary>
    public static float For(WorldObjectSnapshot? target)
    {
        if (target is null) return DefaultUnits;
        return ForFlags(target.ObjectDescriptionFlags ?? 0u);
    }

    /// <summary>
    /// Pure-bitflag overload for direct testing without a
    /// <see cref="WorldObjectSnapshot"/> instance.
    /// </summary>
    public static float ForFlags(uint objectDescriptionFlags)
    {
        var isPortal = (objectDescriptionFlags & (uint)ObjectDescriptionFlag.Portal) != 0;
        return isPortal ? PortalUnits : DefaultUnits;
    }
}
