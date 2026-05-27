# 0001. Start in-process, plan to split BotBrain into a sidecar before M5

- **Status:** Proposed
- **Date:** 2026-05-27
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

[`docs/architecture.md`](../architecture.md) splits a bot's runtime into
three layers (Motor / Tactical / Social) and notes that the Social layer
in particular may want to run in a separate process — the "BotBrain
sidecar" — to keep model calls out of the server's tick path.

[`roadmap/open-questions.md`](../../roadmap/open-questions.md) flags
the in-process-vs-sidecar choice as an open decision, with the lean
being "start in-process, move to sidecar before M5." This ADR records
that as a decision so we can stop re-litigating it.

The pressure to decide now: every milestone before M5 is easier if the
BotBrain runs in the same process as ACE. Every milestone from M5
onward (LLM chat) is easier if it doesn't. Picking the wrong starting
point costs either early velocity or a painful mid-project rewrite.

## Decision

For milestones M1–M4 we run the BotBrain in-process with ACE. Before
starting M5 (LLM chat) we split the Social layer out into a sidecar
process that talks to ACE over a local RPC channel. Motor and Tactical
stay in-process indefinitely.

## Options considered

### Option A — In-process for v1, sidecar later

- Pros:
  - Fastest path to M1–M4. No RPC, no serialization boundary, no second
    deployment artifact.
  - Motor and Tactical genuinely belong next to the world simulation
    — they read world state every tick.
  - We learn the right sidecar API by first writing a non-sidecar
    version. The split is much easier to design once we know the
    actual call patterns.
- Cons:
  - A future split is real work. Some BotBrain code written assuming
    in-process access will need to be reshaped.
  - Risk that we never do the split and the server slowly fills up
    with model-call latency.

### Option B — Sidecar from M1

- Pros:
  - We never write code that assumes in-process access, so no rewrite
    later.
  - Crashes in BotBrain code can't take the server down.
- Cons:
  - Adds an RPC boundary, a serialization format, and a second
    deployment target before there's anything interesting to put in
    it.
  - Forces us to design the sidecar API up front, without knowing
    what the call patterns actually look like. Almost certainly
    designed wrong on the first pass.
  - Slows M1–M4 noticeably for benefits that don't matter until M5.

### Option C — In-process forever

- Pros:
  - Simplest deployment story.
- Cons:
  - Model latency lives on the server tick path. Even with async
    handling, the failure modes (hosted API stalls, local model OOMs)
    can affect world simulation.
  - Means we can't run the BotBrain on different hardware from ACE,
    which we probably want for cost reasons (cheap server + GPU box).
  - Brain bugs become server-crash bugs.

## Consequences

- **Easier:**
  - M1–M4 ship faster because there's no RPC boundary to design.
  - We learn the real BotBrain call patterns before designing the
    sidecar API.
  - Motor/Tactical code can read world state directly without going
    through an RPC.
- **Harder:**
  - We owe ourselves a real split before M5 starts. "Before M5" is
    the deadline; if we slip past it, we will pay for it.
  - Any code in the in-process BotBrain that reaches into ACE
    internals casually needs to be refactored when the split happens.
  - We need a discipline of marking which calls cross the future
    process boundary so the M5 split is mechanical, not
    archaeological.
- **Follow-ups:**
  - Open an issue: "Mark BotBrain ↔ ACE call sites for the M5 split."
  - Add a check to the M4 success criterion: list the call sites that
    will cross the process boundary, with rough call frequencies.

## References

- [`../architecture.md`](../architecture.md)
- [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md)
- [`../../roadmap/milestones.md`](../../roadmap/milestones.md) (M5)
