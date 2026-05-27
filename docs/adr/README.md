# Architecture Decision Records

We use lightweight ADRs to record architectural decisions and why we made
them. Format inspired by Michael Nygard's original ADR proposal.

## When to write one

Write an ADR when:

- A decision will be expensive to reverse (changes the shape of the code,
  the data model, or the deployment topology).
- Reasonable people would disagree about which option to pick.
- The decision resolves an item in
  [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md).

Don't write one for:

- Cosmetic choices.
- Decisions that are obviously right and uncontroversial.
- Things that are easy to change later and don't constrain anything else.

## How to write one

1. Copy [`template.md`](template.md) to `NNNN-short-title.md` where `NNNN`
   is the next number in sequence (zero-padded to 4).
2. Fill it in. Keep it short — one screen if you can.
3. Open a PR. The PR description should link the doc(s) and open
   question(s) the decision affects.
4. Once merged:
   - Update affected doc(s) to reference the ADR.
   - Remove (or strike through) the resolved item in
     `roadmap/open-questions.md`.

## Statuses

- **Proposed** — under discussion in a PR.
- **Accepted** — merged and in effect.
- **Superseded by NNNN** — replaced by a later ADR. Don't delete superseded
  ADRs; they're history.
- **Deprecated** — no longer in effect, but not replaced.

## Index

_(Add a line per ADR as they land.)_

- [`0001-start-in-process-then-sidecar.md`](0001-start-in-process-then-sidecar.md)
  — Proposed. Run BotBrain in-process with ACE for M1–M4; split the
  Social layer into a sidecar before M5.
- [`0002-minimal-fork-bar.md`](0002-minimal-fork-bar.md) — Proposed.
  Define "minimal fork" as: every change to the ACE fork must be
  defensible as an upstreamable PR, with a written upstream
  justification in the PR description.
- [`0003-botcreature-not-botplayer.md`](0003-botcreature-not-botplayer.md)
  — **Superseded by [0007](0007-bots-as-player-not-creature.md).**
  Originally proposed subclassing `Creature` to avoid `Session`
  coupling. Reversed when the user directive forced bots to be real
  players (mob aggro, NPC interaction).
- [`0004-bot-tick-via-monster-tick.md`](0004-bot-tick-via-monster-tick.md)
  — **Superseded by [0008](0008-bot-tick-via-player-tick.md).**
  Originally proposed ticking bots via the per-landblock
  `Monster_Tick` scheduler. Superseded because ADR-0007 moves bots
  from `Creature` to `Player`, and `Player` ticks via
  `Player_Tick`, not `Monster_Tick`.
- [`0005-pathfinding-reuse-and-build.md`](0005-pathfinding-reuse-and-build.md)
  — Proposed. Reuse ACE motion and collision primitives; build our own
  LOS+waypoint planner; defer navmesh until M7 if needed.
- [`0006-chat-via-creature-broadcast.md`](0006-chat-via-creature-broadcast.md)
  — Proposed. Bots speak via `EnqueueBroadcast(GameMessageHearSpeech)`
  directly; inbound /tell handled by a guid shim in the tell handler.
- [`0007-bots-as-player-not-creature.md`](0007-bots-as-player-not-creature.md)
  — Proposed. Reverses ADR-0003. Bots subclass `Player` using the
  character-create constructor (`Player(Weenie, ObjectGuid, accountId)`)
  with a `NullSession` no-op to absorb `Session.Network.EnqueueSend(...)`
  calls. Driver: bots must aggro mobs and interact with NPCs as real
  players do.
- [`0008-bot-tick-via-player-tick.md`](0008-bot-tick-via-player-tick.md)
  — Proposed. Bots tick via a new `OnBrainTick` virtual hook at the
  end of `Player.Player_Tick`. Supersedes ADR-0004 (which assumed
  the `Monster_Tick` scheduler that no longer applies under
  ADR-0007). Brain work remains async-by-contract.
