# M0 checklist

The research-and-decisions milestone. Done when every box here is checked.

See [`milestones.md`](milestones.md) for the definition of M0 and its
success criterion.

## Research questions answered

Each question from
[`../docs/research/ace-investigation.md`](../docs/research/ace-investigation.md)
gets a research issue, an answer summarized on that issue, and a doc
update.

- [x] Q1 — Player without a Session: [#1](https://github.com/darinh/ac-ai-players/issues/1)
- [ ] Q1 — finding written up in `docs/research/ace-investigation.md`
- [x] Q2 — World tick loop and per-entity decisions: [#2](https://github.com/darinh/ac-ai-players/issues/2)
- [ ] Q2 — finding written up
- [x] Q3 — Action surface (move, attack, cast, chat): [#3](https://github.com/darinh/ac-ai-players/issues/3)
- [ ] Q3 — finding written up
- [x] Q4 — Pathfinding and the world representation: [#4](https://github.com/darinh/ac-ai-players/issues/4)
- [ ] Q4 — finding written up
- [x] Q5 — Persistence and characters: [#5](https://github.com/darinh/ac-ai-players/issues/5)
- [ ] Q5 — finding written up

## ADRs accepted

Each decision the research forces gets an ADR. Likely candidates (add or
remove as the research forces):

- [x] ADR-0001 — in-process for M1–M4, sidecar before M5 (proposed):
      [`../docs/adr/0001-start-in-process-then-sidecar.md`](../docs/adr/0001-start-in-process-then-sidecar.md)
- [ ] ADR — BotPlayer vs. Creature subclass choice
- [ ] ADR — threading model for bot ticks
- [ ] ADR — pathfinding reuse vs. replacement
- [ ] ADR — chat hook strategy
- [x] ADR-0002 — "minimal fork" definition (proposed):
      [`../docs/adr/0002-minimal-fork-bar.md`](../docs/adr/0002-minimal-fork-bar.md)

## Docs updated to reflect findings

- [ ] `docs/architecture.md` reflects the BotPlayer/Creature decision
- [ ] `docs/architecture.md` reflects the threading decision
- [ ] `docs/bot-director.md` reflects pathfinding constraints discovered
      during Q3
- [ ] `docs/brain-providers.md` reflects any chat-hook constraints
      discovered during Q5
- [ ] `roadmap/open-questions.md` items that were resolved are removed
      or struck through with a link to the ADR

## Final deliverable

The success criterion for M0: "We can describe, on one page, exactly what
we are going to change in our ACE fork and why."

- [ ] That one-pager exists at `docs/ace-fork-plan.md`
- [ ] It is reviewed and accepted in a PR
- [ ] It is referenced from `docs/architecture.md` and `README.md`

Only when every box above is checked do we open the ACE fork repo and
start M1.
