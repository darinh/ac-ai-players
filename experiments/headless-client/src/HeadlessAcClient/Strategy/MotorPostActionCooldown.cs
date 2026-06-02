// SPDX-License-Identifier: AGPL-3.0-or-later
// MotorPostActionCooldown — wall-clock delay between USE dispatch
// and motion-state reset (which clears motionTarget + useSent so
// the picker can fire a new goal).
//
// Role: keep the motor "parked on target" after USE/PICKUP so the
// server-side action animation/choreography can complete before the
// picker dispatches the next AP/MS. The 2-second default works for
// most use targets (chests, lifestones, doors, NPCs) where the
// server's action handler runs synchronously and emits a single
// GameEvent reply (UseDone / InventoryPutObjInContainer / etc.).
// It does NOT work for Portals: AC's portal-activation flow is a
// multi-stage server-driven sequence that lasts ~3-5 seconds —
// (1) server sends Motion: MoveToPosition pinning the client at
// the portal's stab origin, (2) "Portal Sending..." animation
// runs server-side, (3) Teleport packet fires. Any client-side
// AP / MoveToState during those seconds cancels the windup
// (observed at files/portal03-spike.log:20227 — server sent
// MoveToPosition(origin=portal-stab, runRate=0.00) at L20227 but
// our motor's action-cycle-complete fired at L20233 (only 9 lines
// after PHASE6E/6F USE at L20224), the picker locked a new
// Explore goal at L20236, AP sent → bot walked away from the
// portal stab → portal windup cancelled → bot stayed in
// landblock 0x8602 even though UseDone(ok) arrived at L20266).
//
// AUDIT FRAMING (.github/skills/audit-hardcoded-knowledge/SKILL.md):
// This is MECHANICAL motor configuration, NOT game knowledge.
//   - The IsPortal predicate is a pure wire-bit decode on
//     ObjectDescriptionFlag.Portal (0x40000) from the server's
//     ObjectCreate header — identical predicate to the existing
//     MotorStopRadius.For() (Strategy/MotorStopRadius.cs:100). No
//     name, wcid, weenie, or landblock matching.
//   - The strategy layer (LLM or NoQuestKnowledgePolicy step 5d)
//     has ALREADY selected GoalKind.Use against this exact guid.
//     The motor is only choosing how long to hold its position
//     after dispatching that USE so the server's action handler
//     can run to completion. We are not adding a new interaction
//     the LLM did not ask for; we are letting the LLM's existing
//     Use{Portal} goal complete cleanly instead of self-cancelling
//     in tick N+1.
//   - The PortalWindupSeconds=6 constant is an empirical upper
//     bound on classic AC portal activation: ~3-5 seconds from
//     USE-ack to Teleport packet on a normal server, plus 1s
//     margin for network jitter. If a server runs faster (custom
//     content with a shorter windup) the bot is only idle for the
//     leftover seconds; if it runs slower the windup still
//     succeeds because the picker is gated on motionTarget==null
//     && useSent==false (both held high by this cooldown).
//   - This is the same shape as MotorStopRadius (sibling helper,
//     classified MECHANICAL by the audit on commit b5a78b3).
//
// If a future Use-target class needs an even longer hold (e.g. a
// summoning ritual that takes 15s) it belongs as a new branch
// here, not in autonomous picker logic.

namespace HeadlessAcClient.Strategy;

using System;
using HeadlessAcClient.Protocol.GameMessages;
using HeadlessAcClient.World;

internal static class MotorPostActionCooldown
{
    /// <summary>
    /// Default wall-clock delay between USE/PICKUP dispatch and
    /// motion-state reset. Two seconds is enough for the server's
    /// synchronous action handler to emit its reply event
    /// (UseDone, InventoryPutObjInContainer, etc.) and for any
    /// follow-up packets (private-property updates, container
    /// open) to arrive before the motor moves on to the next
    /// target. Mirrors the prior <c>PostActionCooldownSec</c>
    /// constant in HandshakeDriver.cs.
    /// </summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wall-clock delay for targets carrying the
    /// <see cref="ObjectDescriptionFlag.Portal"/> bit. Bumped from
    /// <see cref="Default"/> because AC's portal-activation flow is
    /// a multi-stage server-driven sequence: USE ack → Motion:
    /// MoveToPosition pinning client at portal stab → server-side
    /// windup → Teleport packet. Any client-side AP / MoveToState
    /// during that window cancels the windup (the server treats
    /// the new motion intent as the player aborting the portal).
    /// 6 seconds covers the typical 3-5 second windup with margin
    /// for jitter; if the teleport succeeds early the bot is
    /// briefly idle in its new landblock until this cooldown
    /// expires, which is harmless.
    /// </summary>
    public static readonly TimeSpan PortalWindup = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Per-target wall-clock cooldown for the action-cycle-reset
    /// gate. Returns <see cref="PortalWindup"/> for Portal entities
    /// (pure wire-bit predicate on
    /// <see cref="ObjectDescriptionFlag.Portal"/>) and
    /// <see cref="Default"/> for everything else. Null target
    /// returns <see cref="Default"/> as a defensive fallback (the
    /// gate should not run with a null motion target, but if it
    /// does the legacy behaviour is preserved).
    /// </summary>
    public static TimeSpan For(WorldObjectSnapshot? target)
    {
        if (target is null) return Default;
        return ForFlags(target.ObjectDescriptionFlags ?? 0u);
    }

    /// <summary>
    /// Pure-bitflag overload for direct testing without a
    /// <see cref="WorldObjectSnapshot"/> instance.
    /// </summary>
    public static TimeSpan ForFlags(uint objectDescriptionFlags)
    {
        var isPortal = (objectDescriptionFlags & (uint)ObjectDescriptionFlag.Portal) != 0;
        return isPortal ? PortalWindup : Default;
    }
}
