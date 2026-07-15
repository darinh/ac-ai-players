// SPDX-License-Identifier: AGPL-3.0-or-later
// TacticsExecutor — the middle layer between Strategy (IGoalPolicy)
// and Motor (action-send branches in HandshakeDriver).
//
// Responsibilities:
//   - Owns currentGoal across ticks.
//   - On each tick, asks the policy to (re)propose a goal.
//   - Resolves the goal's target/item Selectors against live
//     WorldState (NOT the projection snapshot, because the picker
//     needs guid -> WorldObjectSnapshot resolution that the
//     projection deliberately drops).
//   - Exposes ResolvedAction { Kind, Target, Item } for the
//     Motor to dispatch on.
//
// Anti-hardcoding rule (per EPIC #67/#68): TacticsExecutor never
// matches on wcid literals, NPC name literals, or landblock ids.
// All matching goes through SelectorResolver which honors the
// runtime-only constraint.

using System;
using System.Collections.Generic;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Tactics;

internal sealed class TacticsExecutor
{
    private readonly IGoalPolicy _policy;
    private readonly IWeenieRepository _weenies;
    private readonly ITrainingDataSink? _training;

    public Goal? CurrentGoal { get; private set; }

    /// <summary>
    /// Pass-through to the underlying policy. True iff the policy
    /// has an asynchronous decision pending (LLM in flight). The
    /// Motor layer uses this to defer its schema-only fallback so
    /// it doesn't race ahead of an in-flight LLM call.
    /// </summary>
    public bool PolicyHasInflight => _policy.HasInflight;

    public TacticsExecutor(IGoalPolicy policy, IWeenieRepository weenies, ITrainingDataSink? training = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _weenies = weenies ?? throw new ArgumentNullException(nameof(weenies));
        _training = training;
    }

    /// <summary>
    /// Pump the policy: it may return the same goal, a new goal,
    /// or null (no proposal yet — in-flight or quiescent). Returns
    /// the goal that's now current (may be null).
    /// </summary>
    public Goal? Tick(WorldStateProjection projection, EventStream events)
    {
        var proposed = _policy.ProposeGoal(projection, events, CurrentGoal);
        if (!ReferenceEquals(proposed, CurrentGoal))
        {
            CurrentGoal = proposed;
            if (proposed is not null)
                events.Append(new StreamEvent
                {
                    Sequence = 0,
                    Utc = DateTimeOffset.UtcNow,
                    Kind = EventKind.GoalEmitted,
                    GoalId = proposed.Id,
                    Text = $"{proposed.Kind} target={proposed.Target} item={proposed.Item} source={proposed.Source}",
                });
        }
        return CurrentGoal;
    }

    /// <summary>
    /// Replace the current goal with a Motor-fabricated one (e.g. the config-gated
    /// auto-team approve) so <see cref="CurrentGoal"/> — which <see cref="Clear"/>,
    /// <see cref="Fail"/>, and the driver's search-continuity logic read — stays in
    /// sync with the goal the Motor is about to dispatch. Returns the goal. Emits the
    /// same GoalEmitted event Tick does so telemetry is consistent.
    /// </summary>
    public Goal OverrideCurrentGoal(Goal goal, EventStream events)
    {
        if (!ReferenceEquals(goal, CurrentGoal))
        {
            CurrentGoal = goal;
            events.Append(new StreamEvent
            {
                Sequence = 0,
                Utc = DateTimeOffset.UtcNow,
                Kind = EventKind.GoalEmitted,
                GoalId = goal.Id,
                Text = $"{goal.Kind} target={goal.Target} item={goal.Item} source={goal.Source}",
            });
        }
        return goal;
    }

    /// <summary>
    /// Resolve the current goal's Target Selector to a concrete
    /// nearest world object (relative to <paramref name="self"/>).
    /// Returns null if no goal or no live match.
    /// </summary>
    public WorldObjectSnapshot? ResolveTarget(
        WorldState world, WorldObjectSnapshot? self,
        IReadOnlySet<uint>? killedAttackGuids = null)
    {
        if (CurrentGoal is null) return null;
        // Self-pronoun target: the LLM names the bot ITSELF as a generic
        // pronoun — "self"/"me"/"myself"/"yourself" — when a goal acts ON the
        // bot (e.g. the prompt rule "Use a 'double-click'/'read'/'activate' item
        // on YOURSELF first, target = your own name"). No world object is named
        // any of those, so a plain nearest-name resolve MISSES and the goal
        // loops unresolved every tick. Resolve a pronoun self-reference to the
        // bot's OWN snapshot. (The bot's own NAME or own GUID need no special
        // case: the bot is in WorldState, so SelectorResolver already resolves
        // those to self at distance 0 — and does so while still honouring any
        // other selector constraints, which a blind name short-circuit would
        // wrongly bypass on a name collision.) Excludes Attack (targeting self
        // for combat is never intended). Mechanical self-reference; no game
        // knowledge.
        if (self is not null && CurrentGoal.Kind != GoalKind.Attack
            && IsSelfPronounTarget(CurrentGoal.Target))
        {
            return self;
        }
        // For an Attack goal exclude (a) corpses — a corpse keeps the slain
        // creature's name but is not attackable — and (b) recently-killed creature
        // guids: a slain creature can LINGER in the world model (no ObjectDelete,
        // health=0 known only to the combat layer, not corpse-flagged), so a
        // name-only Attack would re-resolve the dead body the bot is standing on
        // (a repeated 60s no-damage abandon) instead of the next LIVE match.
        // Use/Talk/Give deliberately keep corpses (open/loot the body). Pickup
        // EXCLUDES the corpse OBJECT (it is a container, not a pickable item —
        // see the Pickup branch below); its CONTENTS are separate guids.
        // attackableOnly: an Attack must also bind a server-Attackable,
        // non-player target — a selector can match a NON-attackable object by
        // name (an item whose name merely contains a selector word) or a
        // non-attackable NPC; dispatching a melee/missile attack at it lands
        // nothing and strands the bot in the no-damage abandon watchdog, so the
        // resolver drops it (see SelectorResolver.MatchesAttackable; mirrors the
        // out-of-view sighted-memory Attack filter).
        if (CurrentGoal.Kind == GoalKind.Attack)
        {
            var resolved = SelectorResolver.ResolveSingleNearest(
                CurrentGoal.Target, world, self, _weenies,
                excludeCorpses: true, excludeGuids: killedAttackGuids,
                attackableOnly: true);
            // Perception-bounded Attack resolution. The Strategy chose WHAT to
            // attack from the projection's visible set, which is capped at
            // WorldStateProjection.DefaultVisibleRadiusUnits. Once the nearby
            // creature the LLM named is excluded above (a corpse, or a guid that
            // died mid-deliberation during LLM latency), the NEXT live name-match
            // can be a SAME-NAME creature clear across the zone — one the LLM
            // never saw and did not choose. Committing a name-only Attack to a
            // target beyond the perception radius marches the bot to something
            // outside its own sensor window instead of re-deciding over what it
            // can actually see. Treat such an out-of-perception match as
            // unresolved so the policy re-picks from the CURRENT visible set next
            // tick. Sensor range, not game knowledge: it reuses the SAME radius
            // that built the LLM's view and encodes nothing about mob
            // danger/type/level. self==null or an unknown target distance leaves
            // the resolution intact (no basis to reject).
            if (resolved is not null && self is not null &&
                WorldDistance.TrySelectionSquaredDistance(self, resolved, out var d2) &&
                d2 > (double)WorldStateProjection.DefaultVisibleRadiusUnits
                     * WorldStateProjection.DefaultVisibleRadiusUnits)
            {
                return null;
            }
            return resolved;
        }
        if (CurrentGoal.Kind == GoalKind.Pickup)
        {
            // A corpse is a CONTAINER, not a pickable item: the server rejects
            // PUTITEMINCONTAINER on a corpse object (WeenieError 0x29 +
            // InventoryServerSaveFailed). Live (cp2388-deploy.log) the LLM
            // emitted Pickup{Corpse of Drudge Slinker} — a common conflation of
            // "loot the corpse" — and the Motor looped the doomed dispatch 3x.
            // Exclude corpses from Pickup resolution so a Pickup that names the
            // corpse object resolves to NULL and the policy re-deliberates (a
            // corpse's loot is reached by Use → the existing corpse-loot
            // extraction; its CONTENTS are separate guids that are NOT IsCorpse,
            // so they still resolve for Pickup). Wire-bit IsCorpse decode only —
            // identical to the Attack branch's excludeCorpses; no game knowledge.
            return SelectorResolver.ResolveSingleNearest(
                CurrentGoal.Target, world, self, _weenies, excludeCorpses: true);
        }
        return SelectorResolver.ResolveSingleNearest(CurrentGoal.Target, world, self, _weenies);
    }

    /// <summary>
    /// True when <paramref name="target"/> is a generic self-PRONOUN
    /// ("self"/"me"/"myself"/"yourself") the LLM uses to mean the bot itself.
    /// No world object is named any of these, so without this such a target
    /// resolves to nothing and an on-self goal loops unresolved. The bot's own
    /// NAME and own GUID are deliberately NOT matched here — the bot is in
    /// WorldState, so SelectorResolver resolves those to self natively while
    /// still honouring other selector constraints (a blind name/guid
    /// short-circuit would wrongly bypass those on a collision). Pure mechanical
    /// self-reference; no game knowledge.
    /// </summary>
    private static bool IsSelfPronounTarget(Selector target)
    {
        var n = target.Name;
        if (string.IsNullOrWhiteSpace(n)) return false;
        return string.Equals(n, "self", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "me", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "myself", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "yourself", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve the current goal's Item Selector. Returns null if no item
    /// selector or no match; the motor applies action-specific ownership
    /// checks after resolution.
    /// </summary>
    public WorldObjectSnapshot? ResolveItem(WorldState world)
    {
        if (CurrentGoal?.Item is null || CurrentGoal.Item.IsEmpty) return null;
        // Items in inventory have ContainerGuid == self.Guid; the
        // resolver doesn't filter on container so it'll match either
        // an inventory item by name or a world item — both are valid
        // sources for a Give (NPC may have the item in their pack).
        // For our use, the LLM only emits Give when the item is in
        // inventory, so the nearest match (or only match) suffices.
        return SelectorResolver.ResolveSingleNearest(CurrentGoal.Item, world, null, _weenies);
    }

    /// <summary>
    /// Clear the current goal. Called by the Motor when a goal
    /// completes (e.g., GIVE acked, target killed, portal used).
    /// </summary>
    public void Clear(string reason, EventStream events)
    {
        if (CurrentGoal is null) return;
        var id = CurrentGoal.Id;
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalCompleted,
            GoalId = id,
            Text = $"{CurrentGoal.Kind}: {reason}",
        });
        _training?.RecordOutcome(id, "completed", reason);
        CurrentGoal = null;
    }

    /// <summary>
    /// Mark the current goal as failed (target gone, action rejected,
    /// stuck). Surfaces as a GoalFailed event so the policy will
    /// re-deliberate on the next ProposeGoal.
    /// </summary>
    public void Fail(string reason, EventStream events)
    {
        if (CurrentGoal is null) return;
        var id = CurrentGoal.Id;
        events.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = DateTimeOffset.UtcNow,
            Kind = EventKind.GoalFailed,
            GoalId = id,
            // Carry the failed goal's target selector name so the policy
            // can correlate a repeated terminal failure to a specific
            // selector (e.g. an out-of-PVS Attack target that keeps
            // failing to resolve). Our OWN selector name, not server text.
            Name = CurrentGoal.Target?.Name,
            Text = $"{CurrentGoal.Kind}: {reason}",
        });
        _training?.RecordOutcome(id, "failed", reason);
        CurrentGoal = null;
    }
}
