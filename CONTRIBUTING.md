# Contributing

This repo is the planning home for the AI-players project. Right now it's
mostly docs. Code lives in two future places:

- A fork of [ACEmulator/ACE](https://github.com/ACEmulator/ACE) (not yet
  created — see [`roadmap/milestones.md`](roadmap/milestones.md), M0).
- A separate BotBrain sidecar repo (also not yet created).

Until those exist, "contributing" means improving the plan: docs, ADRs,
research findings, and answers to open questions.

## Ground rules

- **Prefer small PRs.** One doc change, one ADR, one research finding per PR.
- **Link to context.** If you change a doc, reference any related ADR, issue,
  or other doc the change interacts with.
- **No marketing voice.** Plain, direct prose. No hype. No emoji.
- **Stay in scope.** This project is about making a single private server
  feel populated. It is not a general agent framework, not a chatbot SaaS,
  and not an effort to revive any specific commercial product.

## How to propose a change

### A new doc or doc change

1. Open a PR with the change.
2. In the PR description, say what problem it solves and what it does not
   solve.
3. If the change resolves an item in
   [`roadmap/open-questions.md`](roadmap/open-questions.md), remove that
   item in the same PR (or move it to an ADR).

### A new architectural decision

Use an ADR. See [`docs/adr/README.md`](docs/adr/README.md).

### A new archetype

1. Add a YAML file under `archetypes/` (the directory will be created when
   the first one lands).
2. Add a row to the archetype table in
   [`docs/archetypes.md`](docs/archetypes.md).
3. Note any new BrainRouter routing rules in
   [`docs/brain-providers.md`](docs/brain-providers.md).

### Research findings

Open an issue using the **Research question** template. When you have an
answer, summarize it in the issue, then update the relevant doc(s) and
close the issue.

## What we are not ready for

Until M0 is done and the ACE fork exists, please don't open PRs that:

- Add code in this repo (this repo stays docs-only).
- Propose a specific code structure for the BotBrain sidecar (too early —
  the ACE investigation will reshape it).
- Add tooling (CI, linters, formatters) beyond what the docs need.

Once code lands, this file gets a real "how to build and run" section.

## Issue and PR etiquette

- Use the provided issue templates.
- Reference the relevant doc(s) and open question(s) in every issue.
- Keep PR titles short and imperative: "Add ADR for sidecar split", not
  "Adding an ADR about how we decided to split things".

## License

Not decided yet. See the licensing item in
[`roadmap/open-questions.md`](roadmap/open-questions.md).
