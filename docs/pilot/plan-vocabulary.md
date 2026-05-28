# Plan vocabulary

**Status:** Draft — P-1 deliverable from the Pilot Track session plan v5
**Date:** 2026-05-27
**Author:** Anvil (Copilot)
**Reviews:** awaiting rubber-duck pass

## Purpose

Pilot-01 must work on any ACE server, including servers with custom
quest content the bot has never seen. It cannot ship with a quest
catalog, hardcoded NPC dialogue tables, or per-quest scripts. Instead,
it **reads NPC dialogue at runtime** and an LLM **compiles** that
dialogue into a structured `Plan` the bot's behavior tree can execute.

For that compile step to be reliable, the LLM needs a **closed schema**
to compile to. This document defines that schema (the "plan vocabulary")
and grounds it in the actual quest mechanics ACE supports.

This is a survey doc, not a design decision. The four pending ADRs
(`0010` three-layer brain, `0011` LLM as compiler, `0012` two-tier LLM,
`0013` needs engine) build on this survey but don't depend on every
detail being right. The vocabulary is expected to grow as Pilot-01
encounters quest patterns we missed; the goal of P-1 is to nail the
common 80–90% so the compiler is useful from day one.

## Method

Surveyed three sources:

1. **ACE engine source** in `darinh/ACE-bots` branch `botplayer-spike`:
   - `Source/ACE.Entity/Enum/EmoteCategory.cs` (50 trigger categories)
   - `Source/ACE.Entity/Enum/EmoteType.cs` (130+ action types)
   - `Source/ACE.Server/Managers/QuestManager.cs` (quest state model)
   - `Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs`
     (emote execution dispatcher)
2. **AC community knowledge** of canonical quest archetypes (kill task,
   fetch, escort, training quests, tinkering, allegiance) from prior
   knowledge of the live retail game and ACE community docs.
3. **Custom-content patterns** observed in popular community packs
   (e.g., DarkMajesty additions, ACEmulator-community quest packs)
   that extend or repurpose the canonical mechanics.

## ACE quest mechanics — what the engine actually supports

### Quest state model

A "quest" in ACE is identified by a `QuestName` string. For each
(player, quest) pair the engine tracks:

| Field | Meaning |
|---|---|
| `NumTimesCompleted` | Counter; also reinterpreted as a 32-bit flag set for multi-stage quests (`HasQuestBits` / `SetQuestBits`) |
| `LastTimeCompleted` | Unix timestamp; used to enforce cooldowns |
| Quest definition `MaxSolves` | -1 = unlimited; ≥0 = cap |
| Quest definition `MinDelta` | seconds between solves; 0 = no cooldown |

Implication for the Plan schema: every Plan needs a
**`repeatability`** field (`{ max_solves, min_cooldown_seconds }`) so
the economist (plan v5 §8) can rank repeatable XP fountains higher
than one-and-done quests of the same magnitude.

### Trigger categories (`EmoteCategory`)

Categories the bot will most often see as the "what just happened
that caused this NPC to talk to me?":

| Category | Bot-side meaning |
|---|---|
| `Refuse` | NPC examined an item but lets bot keep it (identify / preview / "I don't need this") |
| `Vendor` | Vendor interaction surface |
| `Give` | Bot gave NPC an item (turn-in trigger) |
| `Use` | Bot right-clicked the NPC (the primary "talk to me" trigger) |
| `QuestSuccess` / `QuestFailure` | Quest state transition reaction |
| `TestSuccess` / `TestFailure` / `TestNoQuality` | Predicate-checked branch (e.g. "you don't have enough magic skill") |
| `ReceiveTalkDirect` | NPC heard the bot say a specific keyword phrase |
| `HearChat` | NPC heard general chat |
| `NumFellowsSuccess` / `NumFellowsFailure` | Fellowship-size gate |
| `NumCharacterTitlesSuccess` / `NumCharacterTitlesFailure` | Title gate |
| `QuestNoFellow` / `TestNoFellow` | Quest forbids fellowship |
| `Activation` | Bot activated an object (door, lever, altar) |

### Action types (`EmoteType`) relevant to bot strategy

Action types the LLM compiler needs to translate into Plan steps OR
recognize as rewards/state-changes:

**Dialogue (what the bot reads):**
- `Tell` (10), `TextDirect` (13), `Say` (8), `LocalBroadcast` (17),
  `WorldBroadcast` (16) — NPC speaks. `Tell` is the most common; the
  message text supports placeholder substitutions (`%n` for player
  name etc.) resolved server-side, so the bot sees the substituted
  text.

**State manipulation (the bot must know happened):**
- `StampQuest` (22) / `StampMyQuest` (81) — increment quest counter
- `UpdateQuest` (20) / `UpdateMyQuest` (79) — likewise
- `EraseQuest` (31) / `EraseMyQuest` (83) — clear quest
- `SetQuestCompletions` (70) / `SetMyQuestCompletions` (86) — set
  counter to specific value (often used to mark bit-flag stages)
- `SetMyQuestBitsOn` (108) / `SetMyQuestBitsOff` (109) — multi-stage
  progression
- `IncrementMyQuest` (85) / `DecrementMyQuest` (84) — for kill-task
  style accumulators

**Rewards (what the bot gains):**
- `AwardXP` (2), `AwardLevelProportionalXP` (49), `AwardNoShareXP`
  (62) — raw XP
- `AwardSkillXP` (28), `AwardSkillPoints` (29) — skill-specific XP
- `AwardTrainingCredits` (47) — skill credit refund / grant
- `AwardLuminance` (113) — luminance currency (endgame)
- `TeachSpell` (27) — adds spell to bot's spellbook
- `AddContract` (119) — adds a permanent "contract" entry
- `AddCharacterTitle` (34) — grants a title (cosmetic / gate value)
- `Give` (3) — gives an item (when called as an NPC reward action)
- `TeleportTarget` (99) — teleports the bot (a quest can grant
  passage through a "gate" NPC)
- `CastSpell` (14) — buffs the bot (or attacks)
- `RemoveVitaePenalty` (90) — clears death debt

**Cost / state removal (what the bot spends):**
- `TakeItems` (74) — consumes inventory items (turn-in cost)
- `InflictVitaePenalty` (48) — adds death debt
- `SpendLuminance` (112) — consumes luminance

**Predicates (what gates the next emote step server-side):**
- `InqQuest` (21), `InqQuestSolves` (30), `InqMyQuestBitsOn` (104),
  `InqMyQuestBitsOff` (105) — quest state checks
- `InqBoolStat` (35), `InqIntStat` (36), `InqFloatStat` (37),
  `InqStringStat` (38) — generic property checks
- `InqAttributeStat` (39), `InqRawAttributeStat` (40),
  `InqSecondaryAttributeStat` (41), `InqRawSecondaryAttributeStat`
  (42) — attribute checks
- `InqSkillStat` (43), `InqRawSkillStat` (44),
  `InqSkillTrained` (45), `InqSkillSpecialized` (46) — skill checks
- `InqOwnsItems` (76), `InqPackSpace` (89) — inventory checks
- `InqYesNo` (75) — prompt the player and branch
- `InqEvent` (51) — world event state check

**Flow control:**
- `Goto` (67), `MoveToPos` (87) — branch to another emote set
- `StartEvent` (23), `StopEvent` (24) — world event control

### Quest archetypes synthesized from these primitives

Watching these primitives compose, ACE quests collapse into a small
set of recurring patterns:

| Archetype | Trigger → Predicate → Reward pattern | Real examples |
|---|---|---|
| **Kill task** | `Use` → `InqQuest`/no-quest → `StampQuest` (kill-counter); per-kill `HandleKillTask` → on `IsMaxSolves`, `AwardXP` + `Give` reward | Mosswart Heads, Tusker Boots, etc. |
| **Fetch / turn-in** | `Give` → `InqOwnsItems` → `TakeItems` + `AwardXP` + `Give` reward | "Bring me 5 Wisps", supply quests |
| **Talk-to-chain** | `Use` → `Tell` (story dialogue) → `StampQuest`; bot later `Use`s next NPC; chain ends with reward emote set | Aluvian heritage quest, town introduction |
| **Keyword-gated** | `ReceiveTalkDirect` (specific phrase) → `InqQuest` → `StampQuest` + `Tell` next-hint | Asheron's Castle "Ancient Powers" tier, secret-word riddles |
| **Multi-stage bit-flag** | Each stage sets a bit via `SetMyQuestBitsOn`; next stage gated by `InqMyQuestBitsOn` | Aerlinthe Recall, multi-step crafting |
| **Escort** | NPC follows player; `OnDeath` of NPC fails; arrival at landblock `StampQuest` + `AwardXP` | Caravan quests, prisoner escort |
| **Gather / collect** | `Use`-on-resource increments quest counter; on max → bot can turn in to NPC | Hide / pelt gathering, herb collection |
| **Defend** | `StartEvent` spawns waves; surviving N seconds → `StampQuest` | Town invasion events, dungeon defend rooms |
| **Spell teach** | Trade-style turn-in → `TeachSpell` instead of XP | Spell research quests |
| **Train / skill gate** | Vendor NPC `InqSkillTrained` → if not, prompt to spend credits → `AwardSkillPoints` | All trainers in towns |
| **Vendor** | `Vendor` category opens trade UI; bot buys / sells | Every shopkeeper |
| **Teleport gate** | `InqQuest` → if has quest, `TeleportTarget` to destination | Portal magus, gate guardians, raid entrances |

These archetypes drive the **Plan vocabulary** below. Anything in this
list is something the LLM must be able to compile to.

## Plan vocabulary

Closed set of plan shapes the LLM compiler emits to. The behavior
tree's generic executor (P-7 in the session plan) ships one subtree
per vocab type.

If the LLM cannot fit a dialogue into any vocab type, it returns
`null` and the bot logs the unhandled dialogue for offline review.
Vocabulary grows by adding entries here based on what shows up in
the wild.

### Schema (top-level)

```jsonc
{
  "plan_id": "uuid",
  "source": {
    "npc_id": 5012,
    "npc_name": "Town Crier of Holtburg",
    "dialogue_hash": "sha256(text)",
    "captured_at": "2026-05-27T19:00:00Z",
    "server_id": "darin-dev-box"
  },
  "vocab_type": "fetch",
  "vocab_args": { ... },
  "steps": [
    { "op": "TalkTo",  "target_npc_name": "Aldous" },
    { "op": "GoTo",    "landblock_hint": "drudge cave south of Holtburg" },
    { "op": "KillMob", "mob_name": "Drudge", "count": 5 },
    { "op": "Collect", "item_name": "Drudge Fang", "count": 5 },
    { "op": "ReturnTo","target_npc_name": "Aldous" }
  ],
  "rewards":      { /* RewardSpec */ },
  "cost_estimate":{ /* CostSpec */ },
  "prereqs":      { /* PrereqSpec */ },
  "repeatability":{ "max_solves": 1, "min_cooldown_seconds": 0 },
  "validation":   { "all_targets_exist": true, "warnings": [] }
}
```

### Step op vocabulary

The `steps[]` list is the executable plan. Each op is one of:

| Op | Args | Bot does |
|---|---|---|
| `TalkTo` | `target_npc_name`, optional `dialogue_choice_hint` | Use NPC; if dialogue choice prompt appears, pick matching option |
| `SayKeyword` | `target_npc_name`, `keyword_phrase` | Say specific text in `ReceiveTalkDirect` range of NPC |
| `GoTo` | `landblock_hint` OR `position` | Navigate to (uses world knowledge) |
| `KillMob` | `mob_name`, `count`, optional `location_hint` | Engage and kill N instances |
| `Collect` | `item_name`, `count`, optional `source_mob_or_container` | Pick up matching items from kills / containers / ground |
| `UseObject` | `object_name`, optional `location_hint` | Right-click world object (lever, altar, statue, sign) |
| `Equip` | `item_in_inventory`, `slot` | Equip an item |
| `GiveItem` | `target_npc_name`, `item_name`, `count` | Drag-give item to NPC |
| `ReturnTo` | `target_npc_name` | TalkTo the originating NPC for turn-in |
| `UsePortal` | `portal_name_or_location` | Walk through a portal |
| `Wait` | `condition` or `seconds` | Pause until predicate true / time elapsed |
| `ChainTo` | `plan_id` | Move to the next plan in a chained quest |

### Vocab types

```
fetch         { target_mob?, target_item, count, deliver_to_npc }
kill          { target, count, location_hint? }
escort        { protect_npc, destination, hostiles_expected? }
deliver       { item_in_inventory, deliver_to_npc, source_npc? }
talk          { sequence_of_npcs[], dialogue_choices_hint? }
keyword       { target_npc, keyword_phrase, expected_state_change }
explore       { destination_landblock_or_pos, expected_event? }
gather        { resource_object_name, count, location_hint }
defend        { area, duration_or_waves }
train         { skill, target_level, trainer_npc }
craft         { recipe, count, station_object? }
teleport_gate { gate_npc, destination_zone, requires_quest? }
chained       { sub_plans[] }
```

If during execution the bot encounters a mid-stream choice ACE
threw at it (e.g., `InqYesNo` prompt) the plan didn't anticipate,
the executor returns to the strategy layer to recompile the
augmented context. This is the only LLM call inside an active
execution and is heavily rate-limited.

## RewardSpec

```jsonc
{
  "xp_estimate":         12000,
  "skill_xp_estimate":   { "war_magic": 5000 },
  "training_credits":    0,
  "luminance":           null,
  "items":               [{ "name": "Bronze Sword", "tier_hint": 1 }],
  "currency":            100,
  "taught_spells":       [],
  "titles":              [],
  "contracts":           [],
  "teleport_destination":null,
  "removes_vitae":       false,
  "confidence":          0.6
}
```

After a quest completes, the executor records the actual delta
(`TotalExperience` change, items added) and writes a
`reward_ground_truth` entry into the shared cache. Cache self-corrects.

## CostSpec

```jsonc
{
  "expected_time_min":  8,
  "expected_deaths":    0.5,
  "consumables":        [{ "name": "Healing Kit", "estimated_count": 1 }],
  "currency_cost":      0,
  "confidence":         0.5
}
```

## PrereqSpec

```jsonc
{
  "min_level":         3,
  "required_skills":   [{ "skill": "War Magic", "trained": true }],
  "required_items":    [{ "name": "Key of Bobo", "count": 1 }],
  "required_quests":   ["preceding-quest-name@completed"],
  "preceding_plan_id": null,
  "forbidden_fellowship": false,
  "required_title":    null,
  "confidence":        0.7
}
```

## Repeatability

```jsonc
{ "max_solves": 1, "min_cooldown_seconds": 0 }
```

- `max_solves: -1` = unlimited (kill tasks, daily quests)
- `max_solves: 1` = one-and-done (most story quests)
- `min_cooldown_seconds > 0` = "you may complete this quest again in X"

The Economist values a +1000-XP daily much higher than a +1000-XP
one-shot at the same level. Over a month, the daily is worth ~30× as much.

## Sample dialogue → compiled Plan transformations

These seed the LLM prompt as few-shot examples.

### Example 1 — canonical fetch (Mosswart Tongues)

**Dialogue:** "I need eight mosswart tongues from the foul creatures in the swamps near the Mosswart village. Bring them to me and you will be rewarded."

**Compiled:**
```jsonc
{
  "vocab_type": "fetch",
  "vocab_args": { "target_mob": "Mosswart", "target_item": "Mosswart Tongue", "count": 8, "deliver_to_npc": "<source_npc>" },
  "steps": [
    { "op": "GoTo",      "landblock_hint": "Mosswart village swamps" },
    { "op": "KillMob",   "mob_name": "Mosswart", "count": 8 },
    { "op": "Collect",   "item_name": "Mosswart Tongue", "count": 8 },
    { "op": "ReturnTo",  "target_npc_name": "<source_npc>" },
    { "op": "GiveItem",  "target_npc_name": "<source_npc>", "item_name": "Mosswart Tongue", "count": 8 }
  ],
  "rewards":      { "confidence": 0.2 },
  "cost_estimate":{ "expected_time_min": 10, "expected_deaths": 0.1, "confidence": 0.4 },
  "prereqs":      { "min_level": 5, "confidence": 0.4 },
  "repeatability":{ "max_solves": -1, "min_cooldown_seconds": 86400 }
}
```

### Example 2 — canonical fetch with named reward (Tusker Boots)

**Dialogue:** "Bring me twenty tusker hide and I will craft you boots that increase your speed. Be warned: tuskers grow more dangerous in numbers."

**Compiled:**
```jsonc
{
  "vocab_type": "fetch",
  "vocab_args": { "target_mob": "Tusker", "target_item": "Tusker Hide", "count": 20, "deliver_to_npc": "<source_npc>" },
  "rewards":      { "items": [{ "name": "Tusker Boots", "tier_hint": 2 }], "confidence": 0.85 },
  "cost_estimate":{ "expected_time_min": 30, "expected_deaths": 1.0, "confidence": 0.6 },
  "prereqs":      { "min_level": 15, "confidence": 0.6 }
}
```

### Example 3 — talk-chain (heritage intro)

**Dialogue:** "Welcome to Holtburg, traveler. If you wish to learn the ways of our people, speak to Daralet on the west side of town."

**Compiled:**
```jsonc
{
  "vocab_type": "talk",
  "vocab_args": { "sequence_of_npcs": ["Daralet"] },
  "steps": [
    { "op": "GoTo",   "landblock_hint": "west side of Holtburg" },
    { "op": "TalkTo", "target_npc_name": "Daralet" }
  ],
  "rewards":      { "xp_estimate": 100, "confidence": 0.3 },
  "cost_estimate":{ "expected_time_min": 3, "expected_deaths": 0.0, "confidence": 0.8 }
}
```

### Example 4 — keyword-gated (riddle)

**Dialogue:** "I am bound by oath to remain silent, but the wise know the phrase that unlocks my counsel. Speak the words of the river."

**Compiled:**
```jsonc
{
  "vocab_type": "keyword",
  "vocab_args": { "target_npc": "<source_npc>", "keyword_phrase": "<UNKNOWN>", "expected_state_change": "quest_stamp" },
  "validation": { "warnings": ["keyword unknown — bot must research before attempting"] }
}
```

When `keyword_phrase` is `<UNKNOWN>`, executor blocks (Economist
won't pick it). Bot remembers the NPC; re-evaluates if it later picks
up the keyword from another NPC, a book item, or a player chat hint.

### Example 5 — custom-content gather (hypothetical)

**Dialogue:** "The shrine of starlight needs five offerings of moonpetal blossom to bloom. Find them in the glades west of here. I will reward your devotion with a moonsilver charm."

**Compiled:**
```jsonc
{
  "vocab_type": "gather",
  "vocab_args": { "resource_object_name": "moonpetal blossom", "count": 5, "location_hint": "glades west of here" },
  "rewards":      { "items": [{ "name": "Moonsilver Charm" }], "confidence": 0.7 },
  "cost_estimate":{ "expected_time_min": 15, "expected_deaths": 0.2, "confidence": 0.3 }
}
```

Demonstrates: vocabulary handles a custom mechanic the bot has
never seen (moonpetal). Confidence varies by extractable detail.

## What this vocabulary does NOT cover (yet)

Listed so the gaps are explicit. Each is a candidate for a v2
addition once Pilot-01 hits them in P-8 (custom-content validation):

- **Time-gated** ("come back at dawn") — `Wait` op exists but
  compiler doesn't yet extract time-of-day predicates
- **PvP-gated** quests — out of scope (plan §16)
- **Allegiance / monarch** quests — out of scope (plan §16)
- **Random-outcome** quests ("flip a coin") — could fit inside
  `chained` with probabilistic sub-plans, but not modeled
- **Quest cancellation** flow ("nevermind") — Plan field needed
- **Multi-target turn-in** ("bring 5 of each: A, B, C") — current
  `fetch` takes a single item; would compile as `chained` of three
  `fetch` plans
- **Fellowship-required** quests — `PrereqSpec.forbidden_fellowship`
  exists but `required_fellowship_size` does not

## Validation criteria for P-5 (LLM compiler smoke test)

When P-5 ships, it's evaluated on a hand-curated set of 20–30 real
ACE dialogues. Pass criteria:

- **Plan type recall** ≥ 80%: of dialogues classified by a human as
  belonging to one of the §"Vocab types" entries, the compiler picks
  the same one ≥80% of the time
- **Target extraction precision** ≥ 90%: when the compiler picks a
  `target_mob` / `target_item` / `target_npc`, it should match an
  entity that exists in the world data
- **Reward calibration**: when the compiler emits `confidence ≥ 0.7`,
  actual reward (measured post-execution) should be within ±50% of
  the estimate at least 80% of the time
- **Null discipline**: pure-flavor dialogue (greetings, ambient lore,
  vendor pricing chatter) compiles to `null` ≥ 95% of the time

These thresholds are starting points; tune after the first 100 real
dialogues are seen.

## Open questions

Defaults in italics. None block ADRs 0010–0013. All are revisited
in P-5 / P-8.

1. **How does the bot know where "the drudge cave south of Holtburg"
   is?** _Default: world knowledge file in `Source/ACE.Server/Bots/data/world/`
   maintains a NPC + zone name → landblock id map. Bot extends it
   as it explores. Cross-bot shared._
2. **What about dialogues offering multiple sub-quests in one
   message?** _Default: compiler returns a `chained` plan with sub-plans
   for each branch; Economist picks among them._
3. **What if the bot mis-classifies a flavor message as a quest?**
   _Default: validator rejects plans whose `steps[0]` is `GoTo` to a
   nonexistent landblock or whose `KillMob` target doesn't exist.
   Anything that gets through and produces real execution failure
   increments `fail_count`; after 3 fails the cache invalidates._
4. **How does the bot get the raw NPC text without bot-side markup
   pollution?** _Default: P-3 Perception API exposes raw `Tell.message`
   strings as they arrive from `EmoteManager.Tell` (with the
   `Replace()` substitutions already applied server-side, so the bot
   sees its own name in `%n`)._
5. **Per-archetype variations in compile prompt?** _Default: no for
   v1. The compiler emits a neutral plan; the Economist's
   archetype-specific weights determine whether the plan is worth
   doing. Future: pass archetype as compile-time context._
6. **What's the cache key for "same dialogue across servers"?**
   _Default: `(server_id, npc_id, dialogue_hash)`. Two servers could
   legitimately have different content for the same NPC name._

## References

- Driving plan: session plan v5 (Pilot Track)
- ACE source: `Source/ACE.Entity/Enum/EmoteCategory.cs`,
  `Source/ACE.Entity/Enum/EmoteType.cs`,
  `Source/ACE.Server/Managers/QuestManager.cs`,
  `Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs`
- Adjacent design: [`../brain-providers.md`](../brain-providers.md),
  [`../bot-director.md`](../bot-director.md),
  [`../archetypes.md`](../archetypes.md)
- Pending ADRs: `0010`, `0011`, `0012`, `0013`
