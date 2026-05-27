---
name: Archetype proposal
about: Propose a new bot archetype.
title: "[archetype] "
labels: ["archetype"]
---

## Archetype name

Short display name (e.g. "Helpful Vet").

## One-line vibe

What kind of player does this remind you of?

## Why this archetype

What "feels alive" gap does it fill? Reference the existing list in
[`docs/archetypes.md`](../../docs/archetypes.md) and explain why current
archetypes don't already cover it.

## Behavior sketch

- Where do they hang out?
- What do they do (motor + tactical)?
- Do they group with others, or stay solo?
- How chatty are they?
- Do they need an LLM, or are scripted lines enough?

## Risks

- Could this archetype hurt the populated-server feel rather than help it?
  (e.g. spammy, toxic, breaks immersion easily.)
- Any moderation concerns?

## Done when

- [ ] YAML added under `archetypes/`.
- [ ] Row added to the table in `docs/archetypes.md`.
- [ ] Any new BrainRouter routing notes added to `docs/brain-providers.md`.
- [ ] Spawn rules added to the relevant zone tables.
