# M2 checklist

The motor-layer milestone. Done when every box here is checked.

See [`milestones.md`](milestones.md) for the definition of M2 and its
success criterion.

## Prerequisite

- [ ] [#16](https://github.com/darinh/ac-ai-players/issues/16) — LOS+waypoint
      pathfinding planner. M2 blocker. ADR-0005 commits to "reuse
      motion + build planner"; the planner does not exist yet. Needs a
      design spike before implementation.

## Deliverables

- [ ] [#17](https://github.com/darinh/ac-ai-players/issues/17) — bot walks
      to a target point
- [ ] [#18](https://github.com/darinh/ac-ai-players/issues/18) — bot attacks
      a designated monster with one weapon skill
- [ ] [#19](https://github.com/darinh/ac-ai-players/issues/19) — bot HP /
      stamina / mana awareness; recall when low
- [ ] [#20](https://github.com/darinh/ac-ai-players/issues/20) — bot dies
      gracefully and respawns at lifestone

## ADRs likely needed

- [ ] ADR for the pathfinding planner shape (probably grid-based +
      jump-point search over the landblock cell grid; ADR-0005 picks the
      direction but does not commit to a specific algorithm)
- [ ] ADR for the combat-loop tick cadence on `BotPlayer` (whether to
      reuse the `OnBrainTick` 250ms cadence from ADR-0008 or run combat
      on a tighter 100ms loop closer to weapon attack windows)

## Success criterion

"A spawned bot can be told 'go to X, kill Y, come back' and does it
without help."

## Does-not-block

These are NOT M2 deliverables:

- Spawn rules / bubble model — that is M3 ([`bot-director.md`](../docs/bot-director.md))
- Multiple weapon skills / weapon swap — single skill is enough for M2
- Healing items, buffs, debuffs — out of scope; bot recalls when low
- Group combat / aggro management — single bot, single mob
- Personality chat during combat — that is M4 (scripted) / M5 (LLM)

## Files / code areas expected to change

- `Source/ACE.Server/WorldObjects/BotPlayer.cs` — motor + combat hooks
- `Source/ACE.Server/Bots/` — new `Motor/` and `Tactical/` subdirs
  expected
- `Source/ACE.Server/Command/Handlers/BotCommands.cs` — new `goto`,
  `attack`, `recall` subcommands under `/botdirector`
- Possibly upstream-equivalent code: `Player_Combat.cs`,
  `Monster_Navigation.cs` (cited in ADR-0005 + ADR-0008 as the patterns
  to mirror)

## After M2

M3 picks up the bubble model and BotDirector spawn rules — see
[`milestones.md`](milestones.md) M3 section.
