# M0 checklist

The research-and-decisions milestone. Done when every box here is checked.

See [`milestones.md`](milestones.md) for the definition of M0 and its
success criterion.

## Research questions answered

Each question from
[`../docs/research/ace-investigation.md`](../docs/research/ace-investigation.md)
gets a research issue, an answer summarized on that issue, and a doc
update.

- [ ] Q1 — Player without a Session: research issue opened
- [ ] Q1 — finding written up in `docs/research/ace-investigation.md`
- [ ] Q2 — World tick and threading: research issue opened
- [ ] Q2 — finding written up
- [ ] Q3 — Movement and pathfinding: research issue opened
- [ ] Q3 — finding written up
- [ ] Q4 — Combat hooks: research issue opened
- [ ] Q4 — finding written up
- [ ] Q5 — Chat plumbing: research issue opened
- [ ] Q5 — finding written up

## ADRs accepted

Each decision the research forces gets an ADR. Likely candidates (add or
remove as the research forces):

- [x] ADR-0001 — in-process for M1–M4, sidecar before M5 (proposed)
- [ ] ADR — BotPlayer vs. Creature subclass choice
- [ ] ADR — threading model for bot ticks
- [ ] ADR — pathfinding reuse vs. replacement
- [ ] ADR — chat hook strategy
- [ ] ADR — "minimal fork" definition (lines of code? upstreamable?)

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
