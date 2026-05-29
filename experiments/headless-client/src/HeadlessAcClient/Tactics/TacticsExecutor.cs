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
using HeadlessAcClient.Strategy;
using HeadlessAcClient.World;

namespace HeadlessAcClient.Tactics;

internal sealed class TacticsExecutor
{
    private readonly IGoalPolicy _policy;
    private readonly IWeenieRepository _weenies;
    private readonly ITrainingDataSink? _training;

    public Goal? CurrentGoal { get; private set; }

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
    /// Resolve the current goal's Target Selector to a concrete
    /// nearest world object (relative to <paramref name="self"/>).
    /// Returns null if no goal or no live match.
    /// </summary>
    public WorldObjectSnapshot? ResolveTarget(WorldState world, WorldObjectSnapshot? self)
    {
        if (CurrentGoal is null) return null;
        return SelectorResolver.ResolveSingleNearest(CurrentGoal.Target, world, self, _weenies);
    }

    /// <summary>
    /// Resolve the current goal's Item Selector (only meaningful for
    /// Give goals). Returns null if no item selector or no match.
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
            Text = $"{CurrentGoal.Kind}: {reason}",
        });
        _training?.RecordOutcome(id, "failed", reason);
        CurrentGoal = null;
    }
}
