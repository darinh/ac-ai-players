// SPDX-License-Identifier: AGPL-3.0-or-later
// NoQuestKnowledgePolicy — Strategy impl that knows ZERO game
// content. It picks goals using ONLY:
//   - ItemType bitmask (a schema constant: Creature/Portal/Door)
//   - Pickup mask (the existing 0xD96F schema constant)
//   - "Do I have a fresh inventory item I haven't acted on yet?"
//
// What it WILL do (illustrating the schema-only behavior):
//   - If a nearby creature is observed-hostile -> Attack goal
//     (priority high)
//   - If inventory contains a wearable not yet wielded -> Wield
//   - If a nearby pickup-eligible item is on the ground -> Pickup
//   - If inventory contains a "newly-acquired" item (added since
//     last goal) -> Use goal targeting the item itself (so the
//     LLM gets to see the popup that follows, which often reveals
//     quest text)
//   - If nothing else, propose an Explore goal (no target)
//
// What it CANNOT do (by design):
//   - Give an item to a specific NPC (requires knowing WHICH item
//     pairs with WHICH NPC — that's quest content)
//   - Use a specific portal/door to make academy progress
//
// Therefore: with this policy alone the bot will explore, pick up
// loot, equip armor, attack hostiles — but it will NOT exit the
// academy on its own. That's the LLM's job (LlmGoalPolicy).
//
// We KEEP this policy in production because:
//   1) Pure-LLM mode would stall if the LLM endpoint is offline
//      / rate-limited / returns garbage. The fallback lets the
//      bot keep moving while operator fixes the LLM path.
//   2) Its decisions are deterministic and form the baseline for
//      training-data: every LLM decision can be diffed against
//      "what the dumb policy would have done".

using System;
using System.Collections.Generic;
using System.Linq;

namespace HeadlessAcClient.Strategy;

internal sealed class NoQuestKnowledgePolicy : IGoalPolicy
{
    public string Source => "fallback:no-quest-knowledge";

    // Track the latest inventory event we considered so we don't
    // propose the same Use-newly-added-item goal twice.
    private long _lastInventoryEventSeen = -1;

    // Slice K — rotate fallback Talk/Pickup targets so we don't pick
    // the same nearest-named object every deliberation. Successful
    // actions don't produce ActionRejected events (which Slice J
    // dedupes on), so without a separate "what did I just propose"
    // memory the fallback gets locked into a one-NPC loop the moment
    // the LLM stops driving (e.g. when LLM picks Explore — a goal
    // kind the driver doesn't yet pre-empt).
    //
    // Window is intentionally short (8). We want to come back to the
    // same NPC after a few cycles in case it has new dialog after
    // a state change, but never twice in a row.
    private const int RecentProposedWindow = 8;
    private readonly Queue<uint> _recentProposedGuids = new();

    private void RememberProposed(uint guid)
    {
        if (_recentProposedGuids.Contains(guid)) return;
        _recentProposedGuids.Enqueue(guid);
        while (_recentProposedGuids.Count > RecentProposedWindow)
            _recentProposedGuids.Dequeue();
    }

    public Goal? ProposeGoal(
        WorldStateProjection world,
        EventStream events,
        Goal? currentGoal)
    {
        // Slice J — collect the set of recently-rejected target
        // guids so we skip them in candidate selection. Avoids
        // looping on a Bruised Apple the bot can't physically reach
        // (geometry blocked or out of pickup range).
        //
        // Window: last 32 events of kind ActionRejected. Each entry
        // carries the ItemGuid that the server (or our walk-timeout)
        // reported. Guids age out of the window naturally as new
        // events accumulate, so a target the bot revisits later (new
        // landblock, different geometry) is fair game again.
        var recentlyRejectedGuids = events
            .RecentOfKind(EventKind.ActionRejected, 32)
            .Where(e => e.ItemGuid is uint)
            .Select(e => e.ItemGuid!.Value)
            .ToHashSet();

        // 1) Health-critical: drop everything if frac < 0.3.
        if (world.Self.HealthFraction is float hf && hf < 0.3f)
            return MakeGoal(GoalKind.Wait, new Selector { Name = "self" }, null,
                priority: 9, rationale: "low health (<30%) - wait/heal");

        // 2) Combat — nearest observed-hostile creature.
        var hostile = world.Visible
            .Where(v => v.IsCreature && v.ObservedHostile)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (hostile is not null)
            return MakeGoal(GoalKind.Attack,
                new Selector { Guid = hostile.Guid, Name = hostile.Name },
                null, priority: 7,
                rationale: $"observed-hostile {hostile.Name} at d={hostile.Distance:F1}");

        // 3) Wield: any inventory item with ValidLocations set and not yet wielded.
        var unwielded = world.Inventory
            .Where(i => i.ValidLocations is uint vl && vl != 0 && (i.WieldedAt is null || i.WieldedAt == 0))
            .Where(i => !recentlyRejectedGuids.Contains(i.Guid))
            .OrderByDescending(i => i.ValidLocations) // weapons/armor first roughly
            .FirstOrDefault();
        if (unwielded is not null)
            return MakeGoal(GoalKind.Wield,
                new Selector { Name = "self" }, // wield target is the bot
                new Selector { Guid = unwielded.Guid, Name = unwielded.Name, Wcid = unwielded.Wcid },
                priority: 6,
                rationale: $"unwielded gear in inventory: {unwielded.Name}");

        // 4) Pickup: nearest pickup-eligible (by ItemType mask) on the ground.
        var pickup = world.Visible
            .Where(v => v.ItemType is uint it && (it & ItemTypeMasks.Pickup) != 0)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (pickup is not null)
        {
            RememberProposed(pickup.Guid);
            return MakeGoal(GoalKind.Pickup,
                new Selector { Guid = pickup.Guid, Name = pickup.Name },
                null, priority: 5,
                rationale: $"pickup-eligible {pickup.Name} at d={pickup.Distance:F1}");
        }

        // 5) "Newly acquired inventory" -> Use the item itself. This is the
        //    cheap version of "look at it and emit a popup" — exposes quest
        //    text via PopupString back to the LLM next deliberation.
        //
        //    NOTE: we deliberately do NOT match this on a stable wcid. We
        //    use "the newest InventoryItemAdded event since last seen" so
        //    the trigger is observation-derived, not content-keyed.
        var newest = events.RecentOfKind(EventKind.InventoryItemAdded, 1).FirstOrDefault();
        if (newest is { Sequence: var seq } && seq > _lastInventoryEventSeen && newest.ItemGuid is uint g
            && !recentlyRejectedGuids.Contains(g))
        {
            _lastInventoryEventSeen = seq;
            var matchingInv = world.Inventory.FirstOrDefault(i => i.Guid == g);
            if (matchingInv is not null)
                return MakeGoal(GoalKind.Use,
                    new Selector { Guid = g, Name = matchingInv.Name, Wcid = matchingInv.Wcid },
                    null, priority: 4,
                    rationale: $"inspect newly-acquired {matchingInv.Name} (no quest knowledge)");
        }

        // 5b) Visible openable: any visible object the world says you can
        //     OPEN. Wire-derived from ObjectDescriptionFlag.Openable.
        //     Excludes doors (Door bit gets its own dispatch path via
        //     the walk-tick door-USE handler) and items already in our
        //     bag (visible-list filter at WorldStateProjection drops
        //     them anyway).
        //
        //     Action verb is Use — opening a container exposes its
        //     contents (server-driven). This fallback fires the same
        //     way regardless of the container's role in the game
        //     (corpse, chest, bookshelf, lockbox, coffer). It does NOT
        //     bump openables above other steps — it sits at the same
        //     priority bucket as inv-Use (step 5) and runs AFTER it so
        //     freshly-acquired inventory still gets its inspect-pass
        //     before any persistent visible openable competes for the
        //     tick.
        //
        //     Schema-only framing: mirrors step 4 (Pickup) in shape —
        //     observation drives behavior, no game-knowledge value
        //     judgment about whether openable things are worth opening.
        var openable = world.Visible
            .Where(v => v.IsOpenable && !v.IsDoor)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (openable is not null)
        {
            RememberProposed(openable.Guid);
            return MakeGoal(GoalKind.Use,
                new Selector { Guid = openable.Guid, Name = openable.Name },
                null, priority: 4,
                rationale: $"openable visible {openable.Name} at d={openable.Distance:F1}");
        }

        // 6) Talk to nearest NPC creature (non-hostile creature with name) —
        //    talking emits PopupString, feeding the LLM next round.
        var npc = world.Visible
            .Where(v => v.IsCreature && !v.ObservedHostile)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (npc is not null)
        {
            RememberProposed(npc.Guid);
            return MakeGoal(GoalKind.Talk,
                new Selector { Guid = npc.Guid, Name = npc.Name },
                null, priority: 3,
                rationale: $"explore via dialog: {npc.Name}");
        }

        // 6b) Talk recycle — if every visible NPC is in the recent
        //     proposed-set (rare; happens in a sparse landblock where
        //     we've already cycled through everyone), forget the
        //     window and try again. Without this we'd starve the
        //     fallback in a small room.
        if (_recentProposedGuids.Count > 0)
        {
            _recentProposedGuids.Clear();
            var npcRetry = world.Visible
                .Where(v => v.IsCreature && !v.ObservedHostile)
                .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
                .OrderBy(v => v.Distance ?? float.MaxValue)
                .FirstOrDefault();
            if (npcRetry is not null)
            {
                RememberProposed(npcRetry.Guid);
                return MakeGoal(GoalKind.Talk,
                    new Selector { Guid = npcRetry.Guid, Name = npcRetry.Name },
                    null, priority: 3,
                    rationale: $"explore via dialog (recycle): {npcRetry.Name}");
            }
        }

        // 7) Default — explore.
        return MakeGoal(GoalKind.Explore, new Selector { Name = "anywhere" }, null,
            priority: 1, rationale: "no immediate goal, wander");
    }

    private Goal MakeGoal(GoalKind kind, Selector target, Selector? item, int priority, string rationale) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Target = target,
            Item = item,
            Priority = priority,
            Rationale = rationale,
            Source = Source,
        };
}
