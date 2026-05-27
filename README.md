# ac-ai-players

AI players for a private Asheron's Call server.

## Vision

Make a private AC server *feel* like it did at peak popularity in the late
1990s — populated, social, alive — by spawning autonomous AI players around
the human players who are actually online.

Bots are not meant to replace human players. They are meant to make the world
feel inhabited when only a handful of humans are logged in.

## Core ideas

- **Population follows humans.** Bots spawn in "bubbles" around active human
  players and despawn when no one is around. No simulating an empty Dereth.
- **Archetypes, not unique personalities.** ~10 archetypes (Buffbot, Newbie,
  Hardcore Raider, Helpful Vet, etc.) cover the late-90s vibe. See
  [`docs/archetypes.md`](docs/archetypes.md).
- **Layered brain.** Motor / Tactical / Social. Use the right tool at each
  layer — behavior trees for combat and movement, an LLM only for chat.
  See [`docs/architecture.md`](docs/architecture.md).
- **Pluggable model backend.** Local (Ollama) for dev, hosted API optional,
  scripted no-LLM mode for cheap bots. See [`docs/brain-providers.md`](docs/brain-providers.md).

## Status

**M0:** complete. Q1–Q5 research issues ([#1](https://github.com/darinh/ac-ai-players/issues/1)–[#5](https://github.com/darinh/ac-ai-players/issues/5)) closed; 6 ADRs landed at [`docs/adr/`](docs/adr/); one-page fork plan at [`docs/ace-fork-plan.md`](docs/ace-fork-plan.md).

**M1:** spike shipped on the personal fork branch (`botplayer-spike`), not yet merged upstream and not yet public. Live on a private Windows-service deployment. Shipped capabilities tracked in issues [#6](https://github.com/darinh/ac-ai-players/issues/6)–[#15](https://github.com/darinh/ac-ai-players/issues/15): bot spawning + `/spawnbot`, `/botdirector` command surface, per-archetype chat/greetings/tells/emotes, persistence with auto-save, hot-reload of bot data, auto-spawn-from-roster on world-open, heritage-based name generator, `/botdirector follow`, greeter mute, Windows-service deployment via NSSM.

**Open architectural reversal in progress:** the M1 spike implemented bots as `BotCreature : Creature` per [ADR-0003](docs/adr/0003-botcreature-not-botplayer.md). That ADR is being superseded by [ADR-0007](docs/adr/0007-bots-as-player-not-creature.md) — bots become `BotPlayer : Player` so they aggro mobs and interact with NPCs as real players do. Migration epic tracked in the GitHub issues.

**M2 backlog:** issues [#16](https://github.com/darinh/ac-ai-players/issues/16)–[#20](https://github.com/darinh/ac-ai-players/issues/20) — pathfinding planner (blocking), walk-to-point, attack-monster, HP/stam/mana awareness, death + lifestone respawn.

See [`roadmap/milestones.md`](roadmap/milestones.md) for the milestone plan, [`roadmap/m0-checklist.md`](roadmap/m0-checklist.md) for M0 closeout state, and [`docs/research/ace-investigation.md`](docs/research/ace-investigation.md) for the M0 research findings.

Want to run ACE locally to follow along? See [`docs/local-install.md`](docs/local-install.md) for a verified Windows 11 setup procedure.

## Repository map

```
PLAN.md                                  # Progress tracker for repo population
README.md                                # This file
CONTRIBUTING.md                          # How to propose docs/ADRs/archetypes
docs/
  README.md                              # Index of all design docs
  architecture.md                        # Layered brain design
  architecture-diagrams.md               # Mermaid diagrams for the architecture
  bot-director.md                        # Spawn/despawn bubble model
  archetypes.md                          # Personality templates
  brain-providers.md                     # Local/API/scripted pluggability
  ace-fork-plan.md                       # M0 final deliverable: what we fork and why
  local-install.md                       # How to run upstream ACE locally on Windows 11
  glossary.md                            # Project-specific terms
  adr/
    README.md                            # How and when to write an ADR
    template.md                          # ADR template
    0001-start-in-process-then-sidecar.md
    0002-minimal-fork-bar.md
  research/
    ace-investigation.md                 # Open questions about ACE codebase
    related-work.md                      # WoW playerbots, other MMO precedents
roadmap/
  milestones.md                          # M0 through M7
  m0-checklist.md                        # M0 work items
  open-questions.md                      # Things still TBD
archetypes/
  README.md                              # How to add an archetype (YAML schema is illustrative for now)
  *.yaml                                 # Per-archetype stubs (buffbot, helpful_vet, newbie, trade_spammer)
.github/
  CODEOWNERS
  PULL_REQUEST_TEMPLATE.md
  labels.yml                             # Labels we use (synced by .github/workflows/labels-sync.yml)
  workflows/
    labels-sync.yml                      # Applies labels.yml to the repo
  ISSUE_TEMPLATE/
    research-question.md
    adr-proposal.md
    archetype-proposal.md
    bug.md
    config.yml
```
