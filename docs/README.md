# Documentation index

The planning docs for the project, grouped by topic. Start with
[`architecture.md`](architecture.md) for the big picture.

## Core design

- [`architecture.md`](architecture.md) — the three-layer model (Motor /
  Tactical / Social), the BotDirector, and how the pieces fit together.
- [`architecture-diagrams.md`](architecture-diagrams.md) — Mermaid
  diagrams supporting `architecture.md`.
- [`bot-director.md`](bot-director.md) — the bubble model for spawning
  and despawning bots around human players.
- [`brain-providers.md`](brain-providers.md) — how we talk to LLMs
  (hosted, local, scripted) and route between them.
- [`archetypes.md`](archetypes.md) — the recipe-card model for bot
  variety and the starter archetype list.

## Plans

- [`ace-fork-plan.md`](ace-fork-plan.md) — the one-page summary of
  exactly what we change in our ACE fork (M0 final deliverable; stub
  until Q1–Q5 are answered).
- [`design/README.md`](design/README.md) — implementation-level design
  spikes (denser than ADRs; bridge an ADR to a tracked implementation
  issue).

## How-to

- [`local-install.md`](local-install.md) — get an upstream ACE server
  running on a Windows 11 developer box so you can investigate the
  codebase against a live server.

## Research

- [`research/ace-investigation.md`](research/ace-investigation.md) — the
  open questions we need to answer about ACE before forking it.
- [`research/related-work.md`](research/related-work.md) — prior art
  worth studying (MMO bot systems, game AI techniques, LLM-driven NPCs).

## Decisions

- [`adr/README.md`](adr/README.md) — how we record architectural
  decisions, plus the index of ADRs we've accepted.
- [`adr/template.md`](adr/template.md) — the ADR template to copy.

## Reference

- [`glossary.md`](glossary.md) — terms specific to this project and how
  we use them.

## See also

- [`../roadmap/milestones.md`](../roadmap/milestones.md) — what gets
  built when.
- [`../roadmap/open-questions.md`](../roadmap/open-questions.md) —
  what we haven't decided yet.
- [`../PLAN.md`](../PLAN.md) — the original one-page plan.
