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

See [`roadmap/milestones.md`](roadmap/milestones.md) for the plan,
[`roadmap/m0-checklist.md`](roadmap/m0-checklist.md) for what's left in
the current milestone, and
[`docs/research/ace-investigation.md`](docs/research/ace-investigation.md)
for the questions we need to answer first. The final M0 deliverable —
the one-page summary of what we change in our ACE fork — lives at
[`docs/ace-fork-plan.md`](docs/ace-fork-plan.md) (stub until Q1–Q5 are
answered).

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
