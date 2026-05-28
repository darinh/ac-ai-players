# Build templates and progression strategy

Status: spec sketch. Tracked as iter H in [improvement-loop.md](improvement-loop.md). Depends on perception (iter D) and the Plan executor (iter F).

## Why this exists

A bot at level 1 with default attributes will not become a raider by accident. Real AC progression requires a build: a coherent set of attribute bumps, skill specializations, equipment, and combat habits that compound over levels. A bot that picks XP allocations randomly will plateau hard and never beat the game.

Source requirement (user, this session):

> bots should also access entire decision trees based on the race / class / skills they have and know how to level up stats and skills appropriately to beat suit their class / template.

## What a build template is

A `BuildTemplate` is a static document the bot loads at startup that says, for a chosen archetype:

- Race (heritage) prerequisites and bonuses.
- Starting attribute targets at character creation (the 100-point spread).
- Per-level attribute bumps (which of Strength / Endurance / Coordination / Quickness / Focus / Self to push, and in what order, up to the 100 / 200 / 290 plateaus).
- Skill training plan (which skills to spec at creation, which to train, in which order; specialization thresholds and the credit costs).
- Combat stance (melee / missile / war magic / life magic / dual-school) and which keys / spells / weapons to favor.
- Recommended equipment tiers per level band (starter, 30s, 60s, 100s, 126+).
- Recall + buff posture (which town / lifestone to bind, which buffs to maintain).
- Endgame role (DPS / tank / support / debuffer / puller).

Templates are versioned, named (e.g. `Aluvian.SwordSpec.v1`, `Sho.Bowyer.v1`, `Gharundim.WarMage.v1`, `Viamontian.HeavyMace.v1`), and stored as JSON under `Bots/data/build-templates/`.

## What the bot does with one

At every meaningful decision point, the bot consults the template:

1. **Character creation** (one-shot, at bot spawn): apply starting attributes + skill picks. (Most bots today rehydrate existing characters, so this only runs for newly-spawned bots.)
2. **Level-up** (event `OnLevelUp`): the `ProgressionPlanner` reads the template's per-level plan, computes the next attribute bump or skill train/spec action, and emits one or more `AllocateXp` / `TrainSkill` / `SpecSkill` actions for the Plan executor.
3. **Action selection** (every tactical tick): the template's `Stance` biases the action picker — a melee build prefers `Attack`, a war-mage build prefers `CastWarSpell`, a missile build prefers `RangedAttack` with kiting.
4. **Inventory / equipment** (event `OnLootDecision`): the template's equipment tier filters what loot is worth keeping vs vendoring.
5. **Recall / posture** (when health or stam crosses thresholds): the template says which recall to use and where to rest.

## Schema (top-level)

```jsonc
{
  "id": "Aluvian.SwordSpec.v1",
  "displayName": "Aluvian Sword Specialist",
  "heritage": "Aluvian",
  "endgameRole": "DPS",
  "starting": {
    "attributes": { "Strength": 100, "Endurance": 100, "Coordination": 60, "Quickness": 30, "Focus": 10, "Self": 10 },
    "skillsSpec":  ["HeavyWeapons"],
    "skillsTrain": ["MeleeDefense", "MissileDefense", "MagicDefense", "Healing"]
  },
  "progression": [
    { "level": 2,  "actions": [{ "type": "AllocateXp", "attribute": "Strength" }] },
    { "level": 5,  "actions": [{ "type": "TrainSkill", "skill": "Run" }] },
    { "level": 10, "actions": [{ "type": "SpecSkill", "skill": "MeleeDefense" }] }
    // ...
  ],
  "stance":     { "primary": "Melee", "preferredAttackHeight": "Medium" },
  "equipment": { "tiers": [{ "minLevel": 1,  "weapon": "Sword", "armor": "Leather" },
                            { "minLevel": 30, "weapon": "Olthoi Sword", "armor": "Studded" }] },
  "recall":     { "primary": "Holtburg", "lifestoneBindLandblock": "0x8602" }
}
```

The Plan executor (iter F) already understands a small action grammar; `AllocateXp`, `TrainSkill`, `SpecSkill`, `EquipBest` are new verbs the executor needs to learn for this iteration.

## What the LLM is for vs not

- **Not for progression math.** Allocating XP, computing skill costs, checking credit availability — all deterministic. Pure code, no LLM.
- **Maybe for template selection.** Given the bot's heritage and a high-level goal ("I want a tank"), the LLM can pick the right template name from the catalog. This is one prompt at bot creation, not a per-tick concern.
- **Definitely not for runtime stance.** The template's `Stance` is read directly by the tactics layer. The LLM is not in the combat loop.

This keeps LLM cost bounded and progression behavior reproducible.

## First milestone (iter H)

Smallest end-to-end demonstration:

1. Hardcode `Aluvian.SwordSpec.v1` as a C# `BuildTemplate` instance (no JSON loader yet).
2. Implement `ProgressionPlanner.OnLevelUp(BotPlayer, BuildTemplate) -> List<Action>`.
3. Wire to the bot's level-up event so attribute bumps fire automatically as the bot grinds XP from iter G content.
4. Log every progression action at INFO (`[progression]` tag) so the autonomous loop can verify the bumps land.

Subsequent milestones layer in skill training/spec, equipment tier swaps, and the JSON loader for additional templates.

## Cross-links

- [improvement-loop.md](improvement-loop.md) — overall autonomous loop and how iter H fits.
- [plan-vocabulary.md](plan-vocabulary.md) — Plan grammar that progression actions extend.
- ADR-0013 (Needs Engine + Preemption Matrix) — once landed, "level up" becomes a Need the arbiter can satisfy.
