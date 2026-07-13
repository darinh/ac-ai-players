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

using HeadlessAcClient.Strategy.Intent;

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

    // cp-2360/cp-2361: break a barren TOWN-OBJECT tour. With no LLM (e.g. the
    // model is rate-limited) this fallback drives the bot, and the town-interaction
    // steps below (openable Use, lifestone Use, portal Use, NPC Talk + the Talk
    // recycle) each re-pick the nearest such object every tick; in a town the bot
    // tours them forever and never egresses to hunt. Count the town-interaction
    // goals PROPOSED in the CURRENT landblock across ALL those steps — counting
    // re-proposals, not just distinct guids, so a sparse landblock where the bot
    // re-Talks the same one or two NPCs (via the recycle) still trips it — and once
    // that crosses a threshold with no egress and no productive inventory change,
    // stop proposing town-interaction targets so the deliberation falls through to
    // the Explore step. Generic anti-churn over the bot's OWN proposals + observed
    // progress signals — object-type neutral, no game knowledge. Resets on a
    // landblock change (egress) or an inventory-add (a productive loot/quest item),
    // so genuinely productive town interaction never trips it.
    private uint? _townTourLandblock;
    private int _townTourCount;
    private long _townTourInvSeq = -1;
    private const int TownTourEgressThreshold = 6;

    // Slice 0 (Hunt) — shared reference to the IntentStack so this
    // fallback can read (NEVER mutate) the strategic context an
    // operator (or eventually the LLM) has authorised.
    //
    // Constructor injection rather than ProposeGoal-arg avoids
    // rippling IGoalPolicy. Both production policies (this one and
    // LlmGoalPolicy) receive the SAME instance from HandshakeDriver,
    // so observations stay consistent across fallover.
    private readonly IntentStack? _intentStack;

    // Runtime-LEARNED set of creature WCIDs that never answer a Talk with
    // dialog (e.g. inert "Fishing Hole" scenery). The Motor populates it from
    // the bot's OWN Talk dispatches + observed dialog source guids; here we use
    // it ONLY to skip such kinds in the civilian Talk step so the fallback
    // doesn't march the bot across the map to "Talk" non-conversational
    // scenery. Read-only from this policy; never mutated here. May be null
    // (tests / LLM-disabled minimal wiring) — treated as "nothing learned".
    private readonly SilentTalkTargetLearner? _silentTalk;

    // cp-2403: containers (chests/corpses) the Motor observed OPENED-and-EMPTY,
    // with a TTL cooldown. Used ONLY to skip recently-emptied containers in the
    // openable/chest Use steps so this fallback stops marching the bot to empty
    // chest after empty chest when the LLM throttles. Read-only here (the Motor
    // owns marking). May be null (tests / minimal wiring) — treated as "nothing
    // suppressed". Generic TTL guid-suppression; keys only on guid + expiry.
    private readonly HeadlessAcClient.World.InteractUnreachableTracker? _emptiedContainers;

    public NoQuestKnowledgePolicy() : this(null) { }

    public NoQuestKnowledgePolicy(IntentStack? intentStack)
        : this(intentStack, null) { }

    public NoQuestKnowledgePolicy(
        IntentStack? intentStack, SilentTalkTargetLearner? silentTalk)
        : this(intentStack, silentTalk, null) { }

    public NoQuestKnowledgePolicy(
        IntentStack? intentStack, SilentTalkTargetLearner? silentTalk,
        HeadlessAcClient.World.InteractUnreachableTracker? emptiedContainers)
    {
        _intentStack = intentStack;
        _silentTalk = silentTalk;
        _emptiedContainers = emptiedContainers;
    }

    // cp-2403: true when the Motor has this guid in its recently-emptied-container
    // cooldown (so the fallback's Use steps skip it). Null tracker -> never
    // suppressed.
    private bool IsEmptiedContainer(uint guid)
        => _emptiedContainers?.IsSuppressed(guid, DateTime.UtcNow) ?? false;

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

        // Is an externally-authorised hunt commitment currently on top
        // of the stack? Computed once, up front, because several steps
        // below adjust their posture while a hunt is active. Reads ONLY
        // the typed, LLM/operator-authored intent label
        // (HuntAuthorization.IsActiveHunt — the SAME predicate the Motor
        // picker uses; covers Kind "Hunt"/"hunt-excursion" and a typed
        // visible_tag:monster completion, all Status==Active). It
        // originates no strategy, names no content, and assigns no
        // object-type urgency.
        var huntActive = _intentStack?.Top is Intent.Intent huntTop
            && HuntAuthorization.IsActiveHunt(huntTop);

        // No-detour adjacency envelope for OPTIONAL Use targets while a
        // hunt is active (steps 5b/5c/5d). Same radius the Motor already
        // uses to decide it is "at" a container for post-open looting
        // (HandshakeDriver LootContainerProximityRadius). The point is
        // geometry, not value: during a hunt the fallback does not
        // autonomously WALK to a distant optional Use target (that is an
        // off-intent detour); it only Uses one it is already standing
        // next to (e.g. the corpse of a kill it just made). Corpses,
        // chests, lifestones and portals are treated IDENTICALLY — only
        // proximity matters, so this adds no object-type judgment.
        const float HuntActiveUseNoDetourRadiusUnits = 5.0f;

        // cp-2413: no-hunt optional-interaction detour bound. With NO LLM
        // (throttled) and NO active hunt, the fallback must not march the bot a
        // long way to ONE optional interaction — live (cp2412-validate.log) it
        // walked 65-121u to Use a distant Chest, Talk a distant NPC/scenery, or
        // Pickup a far item, which is the "bots are slow" symptom. An Explore
        // egress (a ~72u hop toward UNSEEN area, step 7) discovers more than
        // marathoning to one already-seen object, so beyond this radius the
        // optional-interaction steps yield and the bot Explores instead. Same
        // geometry-only posture as the hunt radius above: object-type-neutral
        // (chest, lifestone, portal, NPC, ground loot all bound identically),
        // no name/wcid/landblock, no value judgment. Looser than the 5u hunt
        // radius because off-hunt there is no mission to detour from — only the
        // tempo cost of the walk. Bound, never a hard skip: a respawn or a
        // closer instance inside the radius is still taken next tick.
        const float FallbackOptionalUseNoDetourRadiusUnits = 40.0f;

        // NOTE: this fallback deliberately does NOT gate on a
        // hardcoded self-health threshold. A "flee/rest when wounded
        // below X%" rule is a rule-of-thumb the LLM must own, not
        // source: the threshold value belongs in an LLM-authored
        // Intent predicate (HealthFractionAtMostPredicate /
        // HealthFractionAtLeastPredicate), which the Strategy layer
        // pushes and the Motor evaluates mechanically. Hardcoding a
        // magic "< 0.3" here is forbidden game knowledge (audited),
        // and the old `Wait` it returned was also a no-op fiction —
        // this fallback has no heal or flee action, so freezing a
        // wounded bot in place only stopped it defending itself while
        // a mob finished it off. Without the gate the bot falls
        // through to "fight the visible hostile", which at least has a
        // chance of surviving.

        // 2) Combat — nearest observed-hostile creature.
        var hostile = world.Visible
            .Where(v => v.IsCreature && v.ObservedHostile && !v.IsPlayer)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (hostile is not null)
            return MakeGoal(GoalKind.Attack,
                new Selector { Guid = hostile.Guid, Name = hostile.Name },
                null, priority: 7,
                rationale: $"observed-hostile {hostile.Name} at d={hostile.Distance:F1}");

        // 3) Wield: any inventory item with ValidLocations set and not yet wielded.
        //
        // Symmetric dedup with steps 4/5b/5c/5d/6/6b: filter out guids
        // already proposed in the recent window, and remember each new
        // proposal. Without this, a Wield that the driver silently
        // no-ops (Wield is NOT in the action-dispatch allowlist; the
        // pickup→equip pipeline owns the only working wield path) AND
        // that the server failed to actuate (e.g. PHASE6L
        // GetAndWieldItem race losing to the slot-filled check) gets
        // re-proposed every tick forever — starving steps 4/5b/5c/5d/6
        // of CPU and blocking all downstream behaviour. portal02 spike
        // smoking gun: Leather Cap 0x80000483 picked up at L2892,
        // PHASE6L sent L2942, InventoryServerSaveFailed err=0 at L2950,
        // NO WieldObject ever. From L3023 onward fallback re-proposed
        // Wield{Cap} on every one of 24+ ticks; step 5d (Use Portal)
        // never executed despite portal visible at d=12.6u.
        // Mechanical weapon-collision facts for the whole inventory, used
        // to skip a self-disarming auto-wield below. Mirrors the PHASE7F.4
        // auto-equip collision skip (HandshakeDriver) and the server's
        // CheckWeaponCollision precondition.
        var nqpInventoryFacts = world.Inventory
            .Select(i => new WeaponSwap.ItemFacts(i.Guid, i.ItemType, i.ValidLocations, i.WieldedAt))
            .ToList();
        // Does the bot have loaded ammo (an item wielded in the missile-ammo
        // slot)? A missile LAUNCHER (a MissileWeapon carrying an AmmoType — vs a
        // self-firing THROWN weapon, whose AmmoType is null) cannot fire without
        // loaded ammo (the server cancels the attack) and, once wielded, forces
        // Missile combat mode + blocks an unarmed-melee strike.
        var nqpHasLoadedAmmo = world.Inventory.Any(
            i => i.WieldedAt is uint aw && aw == ItemTypeMasks.MissileAmmoSlot);
        var unwielded = world.Inventory
            .Where(i => i.ValidLocations is uint vl && vl != 0 && (i.WieldedAt is null || i.WieldedAt == 0))
            .Where(i => !recentlyRejectedGuids.Contains(i.Guid))
            .Where(i => !_recentProposedGuids.Contains(i.Guid))
            // Do NOT auto-wield a missile LAUNCHER with no loaded ammo. It cannot
            // attack (the server cancels a launcher shot with no ammo) yet wielding
            // it forces Missile combat mode and displaces a usable weapon, leaving
            // the bot NOT combat-capable (the observed armed=False regression: the
            // fallback swapped a wielded melee weapon for an ammoless launcher). A
            // thrown missile weapon (AmmoType null — it is its own projectile) and
            // a launcher WITH loaded ammo are unaffected. The MAIN-WEAPON valid-slot
            // requirement is essential: bag AMMO also carries the MissileWeapon
            // ItemType bit AND a non-null AmmoType, and differs only by valid slot
            // (ammo → MissileAmmoSlot, launcher → a main-weapon slot); without this
            // the guard would wrongly skip loadable ammo. Mirrors the launcher+ammo
            // leg of IsCombatCapable; the LLM may still explicitly wield a launcher
            // (e.g. when it plans to load ammo). Pure wire-state precondition
            // (ItemType/AmmoType/valid-slot + ammo-slot occupancy); no game knowledge.
            .Where(i => !(i.ValidLocations is uint vloc && (vloc & WeaponSwap.MainWeaponSlotMask) != 0
                          && i.ItemType is uint it && (it & ItemTypeMasks.MissileWeapon) != 0
                          && i.AmmoType is not null
                          && !nqpHasLoadedAmmo))
            // Do NOT auto-wield gear whose wield would dequip a currently-
            // wielded WEAPON — a loadout DOWNGRADE the LLM owns, not the
            // mechanical fallback. The earlier guard only caught a redundant
            // second WEAPON (self-disarm: e.g. auto-wielding a second ranged
            // weapon while a melee weapon is wielded). But the dequip-before-
            // wield swap now also frees the off-hand for a two-handed weapon,
            // so auto-wielding an off-hand SHIELD would dequip the bot's
            // (possibly trained-skill) two-handed weapon. Skip any gear whose
            // blocking set includes a wielded weapon; gear that displaces only
            // non-weapons (armor) or nothing is fine. Pure server-precondition
            // mirror (WeaponSwap mirrors the server's CheckWeaponCollision) —
            // no weapon preference, no game knowledge; the first weapon into an
            // empty slot is unaffected.
            .Where(i =>
            {
                var blockers = WeaponSwap.FindBlockingWieldedItems(
                    new WeaponSwap.ItemFacts(i.Guid, i.ItemType, i.ValidLocations, i.WieldedAt),
                    nqpInventoryFacts);
                return !blockers.Any(bg =>
                    nqpInventoryFacts.Any(f => f.Guid == bg && WeaponSwap.IsWieldedWeapon(f)));
            })
            // No source-side gear-class ordering: ranking inventory by
            // "weapons/armor first" is a game-knowledge rule-of-thumb the
            // LLM owns, not the mechanical fallback. The fallback wields
            // every equippable item across successive ticks regardless of
            // order, so the end state is identical; we just take whatever
            // the server's inventory iteration yields first.
            .FirstOrDefault();
        if (unwielded is not null)
        {
            RememberProposed(unwielded.Guid);
            return MakeGoal(GoalKind.Wield,
                new Selector { Name = "self" }, // wield target is the bot
                new Selector { Guid = unwielded.Guid, Name = unwielded.Name, Wcid = unwielded.Wcid },
                priority: 6,
                rationale: $"unwielded gear in inventory: {unwielded.Name}");
        }

        // cp-2360/cp-2361/cp-2373: reset the per-landblock town-tour memory on
        // egress (landblock change) or a productive inventory-add (loot/quest
        // item) — only a barren tour with neither persists. Once the bot has
        // toured the threshold of DISTINCT town objects here (the Pickup step
        // below + the openable/lifestone/portal/NPC steps) without progress, the
        // town-interaction steps tap out (propose nothing, unless a hunt is
        // active) so this deliberation falls through to the Explore step and the
        // bot heads out. cp-2373 folds the Pickup step into the cap: a barren
        // Pickup churn — re-proposing pickup-eligible items that never enter the
        // bag because the pickup never completes (out of reach / non-committal
        // re-targeting) — must also count toward the egress cap, while a
        // productive Pickup (InventoryItemAdded) resets it. Generic anti-churn
        // over the bot's OWN proposals — no object-type judgment.
        var townAddSeq = events.RecentOfKind(EventKind.InventoryItemAdded, 1)
            .FirstOrDefault()?.Sequence ?? -1;
        if (_townTourLandblock != world.Self.Landblock || townAddSeq > _townTourInvSeq)
        {
            _townTourLandblock = world.Self.Landblock;
            _townTourCount = 0;
            _townTourInvSeq = townAddSeq;
        }
        var townTourTapped = !huntActive
            && _townTourCount >= TownTourEgressThreshold;

        // 4) Pickup: nearest pickup-eligible (by ItemType mask) on the ground.
        var pickup = townTourTapped ? null : world.Visible
            .Where(v => v.ItemType is uint it && (it & ItemTypeMasks.Pickup) != 0)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            // cp-2413: off-hunt, do not marathon to a distant ground item. A
            // null (unmeasurable) distance is treated as near, never bounded out.
            .Where(v => huntActive
                || (v.Distance ?? 0f) <= FallbackOptionalUseNoDetourRadiusUnits)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (pickup is not null)
        {
            _townTourCount++;
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
        // The per-landblock town-tour cap (_townTourCount / townTourTapped) is
        // computed once above (at the Pickup step) and shared by every town
        // step below — openable/lifestone/portal/NPC. cp-2360/cp-2361 added the
        // openable/NPC/etc steps; cp-2373 folded in the Pickup step.
        var openable = townTourTapped ? null : world.Visible
            .Where(v => v.IsOpenable && !v.IsDoor)
            // cp-2403: skip a container the Motor just opened and found EMPTY
            // (TTL'd) so the fallback stops marching to empty chest after empty
            // chest when the LLM throttles. Fallback-only; never blocks an LLM
            // Use goal. The TTL lets a respawned chest be retried later.
            .Where(v => !IsEmptiedContainer(v.Guid))
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            // While hunting, only Use an openable we are essentially
            // already standing next to (own-kill corpse), never a far
            // detour off the hunt (see HuntActiveUseNoDetourRadiusUnits).
            // cp-2413: off-hunt, still bound the detour to the looser
            // FallbackOptionalUseNoDetourRadiusUnits (no 100u chest marches);
            // a null (unmeasurable) distance is treated as near, not far.
            .Where(v => huntActive
                ? (v.Distance ?? float.MaxValue) <= HuntActiveUseNoDetourRadiusUnits
                : (v.Distance ?? 0f) <= FallbackOptionalUseNoDetourRadiusUnits)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (openable is not null)
        {
            _townTourCount++;
            RememberProposed(openable.Guid);
            return MakeGoal(GoalKind.Use,
                new Selector { Guid = openable.Guid, Name = openable.Name },
                null, priority: 4,
                rationale: $"openable visible {openable.Name} at d={openable.Distance:F1}");
        }

        // 5c) Visible lifestone: any visible object with the
        //     ObjectDescriptionFlag.LifeStone bit. Lifestones do NOT
        //     have the Openable bit (see ACE Lifestone.cs:33 — only
        //     LifeStone is set), so step 5b does not cover them, but
        //     they ARE Use-targets via the same WorldObject.ActOnUse
        //     dispatch path.
        //
        //     Same shape as step 5b — pure wire-bit predicate,
        //     priority 4 (no bump), generic action verb (Use),
        //     generic rationale string. Sits AFTER step 5b so an
        //     openable in the same view still wins one tick first;
        //     the lifestone gets its turn on the next tick because
        //     the openable will be in `_recentProposedGuids` by then.
        var lifestone = townTourTapped ? null : world.Visible
            .Where(v => v.IsLifestone)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            // No far detour to a lifestone while hunting (see step 5b).
            // cp-2413: off-hunt, bound to FallbackOptionalUseNoDetourRadiusUnits
            // (null distance treated as near).
            .Where(v => huntActive
                ? (v.Distance ?? float.MaxValue) <= HuntActiveUseNoDetourRadiusUnits
                : (v.Distance ?? 0f) <= FallbackOptionalUseNoDetourRadiusUnits)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (lifestone is not null)
        {
            _townTourCount++;
            RememberProposed(lifestone.Guid);
            return MakeGoal(GoalKind.Use,
                new Selector { Guid = lifestone.Guid, Name = lifestone.Name },
                null, priority: 4,
                rationale: $"lifestone visible {lifestone.Name} at d={lifestone.Distance:F1}");
        }

        // 5d) Visible portal: any visible object with the
        //     ObjectDescriptionFlag.Portal bit. Portals do NOT carry
        //     the Openable bit in ACE wire data (the Openable bit is
        //     reserved for chests/doors), so step 5b does not cover
        //     them, but they ARE Use-targets via the same
        //     WorldObject.ActOnUse dispatch path (PortalUseHandler
        //     server-side). Without this step a fallback-only bot
        //     stays trapped inside whichever indoor area it spawned
        //     in — the academy exit portal, dungeon-return portals,
        //     and town gateway portals all go unused.
        //
        //     Same shape as step 5b/5c — pure wire-bit predicate
        //     (no name/wcid hardcoded), priority 4 (no bump),
        //     generic action verb (Use), generic rationale string.
        //     Sits AFTER step 5b and 5c so chests + lifestones in
        //     the same view still win a tick first; the portal gets
        //     its turn on the next tick because both will be in
        //     `_recentProposedGuids` by then.
        //
        //     Risk acknowledged: portals can lead anywhere, including
        //     dangerous zones. This is symmetric to "humans walk
        //     through portals they discover". A future slice MAY
        //     gate this behind an authorised Intent (e.g.
        //     `Explore{outdoor}` or `ReturnTo{lifestone}`), but the
        //     fallback baseline matches step 5b's posture: observe,
        //     act, learn from the result.
        var portal = townTourTapped ? null : world.Visible
            .Where(v => v.IsPortal)
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            // No far detour to a portal while hunting (see step 5b).
            // cp-2413: off-hunt, bound to FallbackOptionalUseNoDetourRadiusUnits
            // (null distance treated as near).
            .Where(v => huntActive
                ? (v.Distance ?? float.MaxValue) <= HuntActiveUseNoDetourRadiusUnits
                : (v.Distance ?? 0f) <= FallbackOptionalUseNoDetourRadiusUnits)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (portal is not null)
        {
            _townTourCount++;
            RememberProposed(portal.Guid);
            return MakeGoal(GoalKind.Use,
                new Selector { Guid = portal.Guid, Name = portal.Name },
                null, priority: 4,
                rationale: $"portal visible {portal.Name} at d={portal.Distance:F1}");
        }

        // While an ACTIVE hunt commitment is on top of the stack,
        // chatting up civilian NPCs is OFF-INTENT: it does not advance
        // the hunt and, in a town full of NPCs, the Talk steps below
        // (6 / 6b) would win every deliberation and starve the
        // hunt-decompose (6c) and Explore-egress (7) steps — leaving the
        // bot milling among townsfolk instead of heading out to hunt
        // (observed live when the LLM was 429-rate-limited and the
        // fallback drove an operator AC_BOTS_INITIAL_INTENT=Hunt). So when
        // a hunt is authorised we SUPPRESS the autonomous civilian Talk
        // and fall through to the hunt-decompose / Explore-egress steps.
        // `huntActive` is computed once near the top of ProposeGoal (it
        // also gates the optional Use steps 5b/5c/5d); the same typed,
        // LLM/operator-authored hunt-intent label drives both.

        // 6) Talk to nearest NPC creature (non-hostile, non-monster
        //    creature with name) — talking emits PopupString,
        //    feeding the LLM next round. The !IsMonster filter is
        //    mechanical, not game-knowledge: monsters do not have
        //    NPC dialog trees, so a Talk dispatched against one
        //    will produce no PopupString. Filtering them out here
        //    keeps the Talk step on its actual surface (vendors,
        //    greeters, quest-givers) and avoids competing with the
        //    Hunt-intent decomposer (step 6c) for the same wire
        //    objects. Skipped entirely under an active hunt (see above).
        //    Also skips creature KINDS the bot has LEARNED never answer a
        //    Talk with dialog (SilentTalkTargetLearner) — e.g. inert
        //    "Fishing Hole" scenery — so the fallback stops marching across
        //    the map to Talk non-conversational objects of the same wcid.
        var npc = (huntActive || townTourTapped) ? null : world.Visible
            .Where(v => LlmGoalPolicy.IsDialogNpcCandidate(v) && !v.ObservedHostile)
            .Where(v => !(_silentTalk?.IsSilent(v.Wcid) ?? false))
            .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
            .Where(v => !_recentProposedGuids.Contains(v.Guid))
            // cp-2413: off-hunt, do not marathon to a distant NPC to chat
            // (null distance treated as near).
            .Where(v => (v.Distance ?? 0f) <= FallbackOptionalUseNoDetourRadiusUnits)
            .OrderBy(v => v.Distance ?? float.MaxValue)
            .FirstOrDefault();
        if (npc is not null)
        {
            _townTourCount++;
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
        //     fallback in a small room. Same !IsMonster filter as
        //     step 6.
        if (!huntActive && !townTourTapped && _recentProposedGuids.Count > 0)
        {
            _recentProposedGuids.Clear();
            var npcRetry = world.Visible
                .Where(v => LlmGoalPolicy.IsDialogNpcCandidate(v) && !v.ObservedHostile)
                .Where(v => !(_silentTalk?.IsSilent(v.Wcid) ?? false))
                .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
                // cp-2413: off-hunt detour bound applies to the recycle too.
                .Where(v => (v.Distance ?? 0f) <= FallbackOptionalUseNoDetourRadiusUnits)
                .OrderBy(v => v.Distance ?? float.MaxValue)
                .FirstOrDefault();
            if (npcRetry is not null)
            {
                _townTourCount++;
                RememberProposed(npcRetry.Guid);
                return MakeGoal(GoalKind.Talk,
                    new Selector { Guid = npcRetry.Guid, Name = npcRetry.Name },
                    null, priority: 3,
                    rationale: $"explore via dialog (recycle): {npcRetry.Name}");
            }
        }

        // 6c) Hunt-intent decomposer — only fires when an authorised
        //     `Hunt` Intent is currently on top of the stack. The
        //     intent itself is pushed by either:
        //       - the operator via the AC_BOTS_INITIAL_INTENT=Hunt
        //         env var (HandshakeDriver pushes once at startup),
        //       - the LLM via a stack_ops push in its JSON response.
        //
        //     Code does NOT originate the strategic commitment to
        //     hunt. It only materialises an existing, externally-
        //     authorised commitment into a concrete tactical Attack
        //     goal during downtime — and only when every more
        //     concrete observable opportunity is already exhausted
        //     (Wield, Pickup, inspect-newest-inv, openable Use,
        //     lifestone Use, NPC dialog, dialog recycle). Per the
        //     audit narrative for ac-ai-players#92 (slice 0), this
        //     is the difference between "proactive combat
        //     initiation" (FORBIDDEN — what the prior naive
        //     Attack-on-IsMonster step did) and "reactive
        //     decomposition of an authorised intent" (ALLOWED).
        //
        //     Placement is deliberately LAST in the opportunity
        //     ladder, immediately before bare Explore. This is the
        //     audit-driven fix for the v1 of this slice, which
        //     placed Hunt-Attack at priority 6 ahead of Pickup and
        //     was flagged as encoding the rule "combat during Hunt
        //     outranks loot". The shape now is purely "if the
        //     operator asked us to hunt and nothing else observable
        //     is worth doing, attack a monster instead of wandering
        //     aimlessly". The observed-hostile path (step 2,
        //     priority 7) still beats us if a monster becomes
        //     aggressive mid-tick — that's a fight-back response,
        //     not a Hunt commitment.
        //
        //     Wire-bit gating only:
        //       - an active hunt commitment on top (HuntAuthorization.
        //         IsActiveHunt — the same typed predicate that suppresses
        //         the civilian Talk above; covers Kind "Hunt"/"hunt-
        //         excursion" and a typed visible_tag:monster completion,
        //         all LLM/operator-authored — never English parsing)
        //       - any inventory item with WieldedAt != 0 AND the
        //         ItemTypeMasks.MeleeWeapon bit set (mechanical
        //         precondition for GameActionTargetedMeleeAttack)
        //       - any visible IsMonster (Slice H composite of
        //         Creature+Attackable+!RadarBlipColor+!Vendor+!Healer
        //         +!Corpse — corpse exclusion shipped this slice)
        if (huntActive && _intentStack?.Top is Intent.Intent topIntent)
        {
            var hasMeleeWielded = world.Inventory.Any(i =>
                i.WieldedAt is uint w && w != 0 &&
                i.ItemType is uint it && (it & ItemTypeMasks.MeleeWeapon) != 0);
            if (hasMeleeWielded)
            {
                // Prefer the nearest monster the bot's OWN combat-feel record
                // does NOT mark as a repeated loss. A "beaten" kind (recorded
                // death/near-death/ineffective and NO kill — the SAME signal the
                // prompt's combat-history rule surfaces to the LLM) is skipped so
                // autonomous fallback grinding (which runs heavily during LLM
                // 429-backoffs) does not keep feeding the bot to a kind it cannot
                // beat. No kind is dangerous in source: a kind becomes avoidable
                // ONLY after the bot itself loses to it (its own experience, not
                // hardcoded danger). If every visible monster is a beaten kind, no
                // Attack is emitted and the policy falls through to Explore (move
                // on to find winnable monsters) rather than dying in place.
                // Mirrors the cp-2405 disengage reflex (self-experience).
                var huntTarget = world.Visible
                    .Where(v => v.IsMonster && !v.IsCorpse)
                    .Where(v => !recentlyRejectedGuids.Contains(v.Guid))
                    .Where(v => !IsBeatenKind(v, world))
                    .OrderBy(v => v.Distance ?? float.MaxValue)
                    .FirstOrDefault();
                if (huntTarget is not null)
                    return MakeGoal(GoalKind.Attack,
                        new Selector { Guid = huntTarget.Guid, Name = huntTarget.Name },
                        null, priority: 2,
                        rationale: $"decompose Hunt intent [{topIntent.Id}]: winnable monster {huntTarget.Name} at d={huntTarget.Distance:F1}");
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

    // True iff the bot's OWN combat-feel record marks this monster's KIND as a
    // repeated loss the bot has NOT yet out-leveled. Delegates the full verdict
    // (identity aggregation + non-lethal level-aware re-test) to the SHARED
    // LlmGoalPolicy.IsBeatenKind so the fallback hunt-target skip and the
    // outdoor frontier mob-bias avoid the SAME kinds from one definition. Uses
    // the UNCAPPED CombatHistoryFull + the bot's own level; no hardcoded
    // kind/wcid danger.
    private static bool IsBeatenKind(VisibleObjectProjection v, WorldStateProjection world)
        => LlmGoalPolicy.IsBeatenKind(world.CombatHistoryFull, v.Wcid, v.Name, world.Self.Level);
}
