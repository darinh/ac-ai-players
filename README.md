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

Planning. No code yet. We're answering open questions about how to extend
[ACEmulator/ACE](https://github.com/ACEmulator/ACE) before forking anything.

See [`roadmap/milestones.md`](roadmap/milestones.md) for the plan and
[`docs/research/ace-investigation.md`](docs/research/ace-investigation.md)
for the questions we need to answer first.

## Repository map

```
PLAN.md                                  # Progress tracker for repo population
README.md                                # This file
docs/
  architecture.md                        # Layered brain design
  bot-director.md                        # Spawn/despawn bubble model
  archetypes.md                          # Personality templates
  brain-providers.md                     # Local/API/scripted pluggability
  research/
    ace-investigation.md                 # Open questions about ACE codebase
    related-work.md                      # WoW playerbots, other MMO precedents
roadmap/
  milestones.md                          # M0 through M7
  open-questions.md                      # Things still TBD
.github/
  ISSUE_TEMPLATE/
    milestone.md
    research-question.md
```
