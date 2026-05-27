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
- [x] Q1 — finding written up in `docs/research/ace-investigation.md`
- [x] Q2 — World tick loop and per-entity decisions: [#2](https://github.com/darinh/ac-ai-players/issues/2)
- [x] Q2 — finding written up
- [x] Q3 — Action surface (move, attack, cast, chat): [#3](https://github.com/darinh/ac-ai-players/issues/3)
- [x] Q3 — finding written up
- [x] Q4 — Pathfinding and the world representation: [#4](https://github.com/darinh/ac-ai-players/issues/4)
- [x] Q4 — finding written up
- [x] Q5 — Persistence and characters: [#5](https://github.com/darinh/ac-ai-players/issues/5)
- [x] Q5 — finding written up

## ADRs accepted

Each decision the research forces gets an ADR. Likely candidates (add or
remove as the research forces):

- [x] ADR-0001 — in-process for M1–M4, sidecar before M5 (proposed):
      [`../docs/adr/0001-start-in-process-then-sidecar.md`](../docs/adr/0001-start-in-process-then-sidecar.md)
- [x] ADR-0003 — BotCreature, not BotPlayer:
      [`../docs/adr/0003-botcreature-not-botplayer.md`](../docs/adr/0003-botcreature-not-botplayer.md)
- [x] ADR-0004 — bot tick on `Monster_Tick`:
      [`../docs/adr/0004-bot-tick-via-monster-tick.md`](../docs/adr/0004-bot-tick-via-monster-tick.md)
- [x] ADR-0005 — pathfinding: reuse motion + build planner:
      [`../docs/adr/0005-pathfinding-reuse-and-build.md`](../docs/adr/0005-pathfinding-reuse-and-build.md)
- [x] ADR-0006 — chat via `EnqueueBroadcast`; inbound /tell shim:
      [`../docs/adr/0006-chat-via-creature-broadcast.md`](../docs/adr/0006-chat-via-creature-broadcast.md)
- [x] ADR-0002 — "minimal fork" definition (proposed):
      [`../docs/adr/0002-minimal-fork-bar.md`](../docs/adr/0002-minimal-fork-bar.md)
- [ ] ADR — `Character.is_Bot` persistence shape (deferred to pre-M6)

## Docs updated to reflect findings

- [x] `docs/architecture.md` reflects the BotCreature decision (via
      ADR-0003 cross-references)
- [x] `docs/architecture.md` reflects the threading decision (via
      ADR-0004 cross-references)
- [x] `docs/bot-director.md` reflects pathfinding constraints discovered
      during Q4 (via ADR-0005 cross-references)
- [x] `docs/brain-providers.md` reflects any chat-hook constraints
      discovered during Q3 (via ADR-0006 cross-references)
- [x] `roadmap/open-questions.md` items that were resolved are annotated
      inline with a "Resolved by …" link to the ADR

## Final deliverable

The success criterion for M0: "We can describe, on one page, exactly what
we are going to change in our ACE fork and why."

- [x] That one-pager exists at `docs/ace-fork-plan.md`
- [ ] It is reviewed and accepted in a PR
- [x] It is referenced from `docs/architecture.md` and `README.md`

Only when every box above is checked do we open the ACE fork repo and
start M1.
