# ACE fork plan

A one-page summary of what we are going to change in our forked copy of
[ACEmulator/ACE](https://github.com/ACEmulator/ACE), and why. This doc
is the final M0 deliverable — see
[`../roadmap/m0-checklist.md`](../roadmap/m0-checklist.md).

It is a stub. Every section below is **TBD** until the corresponding
research issue lands a finding. When all sections are filled in, M0 is
done and we can open the ACE fork repo.

## What we are changing in ACE

**TBD.** Pending the answers to all five M0 research issues
([#1](https://github.com/darinh/ac-ai-players/issues/1)–[#5](https://github.com/darinh/ac-ai-players/issues/5)).
The short version will list, with file paths into the ACE source tree:

- The new `BotPlayer` class (or `Creature` subclass — see below).
- The tick hook our BotDirector subscribes to.
- The action-surface methods we call to drive bots in-world.
- Any database changes (likely a single `is_bot` flag).

## Why

**TBD.** Will summarize: we want a "headless player" so that bots share
the same in-world behaviors as humans without faking packets. Doing
this from outside ACE is not feasible because `Player` behavior lives
behind internal methods. See
[`architecture.md`](architecture.md) ("Where the ACE fork actually
changes").

## Minimal-diff justification

**TBD.** Governed by the bar set in
[`adr/0002-minimal-fork-bar.md`](adr/0002-minimal-fork-bar.md): every
fork change must be defensible as "would upstream take this?". This
section will list each addition we make to ACE and the one-paragraph
upstream-justification for it.

## Threading model

**TBD.** Blocked on
[#2](https://github.com/darinh/ac-ai-players/issues/2). Likely
outcome: bot ticks run on the same landblock thread as the bot they
drive. The async boundary for the future sidecar (per
[`adr/0001-start-in-process-then-sidecar.md`](adr/0001-start-in-process-then-sidecar.md))
sits at the Social layer; Motor and Tactical stay synchronous.

## BotPlayer vs. Creature decision

**TBD.** Blocked on
[#1](https://github.com/darinh/ac-ai-players/issues/1). Two candidates:
subclass `Player` (gets player-like behavior for free, drags in session
assumptions) vs. extend `Creature` (cleaner isolation, but we
re-implement a lot of player features). Decision will be recorded as
its own ADR and summarized here.

## Chat hook strategy

**TBD.** Blocked on
[#3](https://github.com/darinh/ac-ai-players/issues/3). Need to know
how `/local`, `/tell`, `/allegiance`, and emotes are routed in ACE
before we can decide whether bots send chat through the normal player
chat path or through a dedicated hook. The decision also affects how
the Social layer *receives* chat from other players, which is what
drives most BotBrain wakeups
([`brain-providers.md`](brain-providers.md)).

## Open risks

**TBD.** Will be assembled from the findings of issues
[#1](https://github.com/darinh/ac-ai-players/issues/1)–[#5](https://github.com/darinh/ac-ai-players/issues/5).
Candidates already on the table:

- Pathfinding may need a custom solution if ACE has no reusable nav
  data ([#4](https://github.com/darinh/ac-ai-players/issues/4)).
- `Player` may dereference `Session` deep enough in its lifecycle that
  a session-less `BotPlayer` requires invasive changes
  ([#1](https://github.com/darinh/ac-ai-players/issues/1)).
- Sidecar/ACE state divergence (sidecar dies, ACE keeps bot alive) may
  need a recovery strategy
  ([#5](https://github.com/darinh/ac-ai-players/issues/5)).

## See also

- [`architecture.md`](architecture.md) — overall design this fork plan
  implements
- [`research/ace-investigation.md`](research/ace-investigation.md) — Q1–Q5
- [`adr/0001-start-in-process-then-sidecar.md`](adr/0001-start-in-process-then-sidecar.md)
- [`adr/0002-minimal-fork-bar.md`](adr/0002-minimal-fork-bar.md)
- [`../roadmap/m0-checklist.md`](../roadmap/m0-checklist.md)
