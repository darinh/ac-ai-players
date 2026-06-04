# Copilot instructions — `ac-ai-players`

Repo-wide operating rules. Auto-loaded. Read alongside
[`AGENTS.md`](../AGENTS.md) and
[`docs/pilot/improvement-loop.md`](../docs/pilot/improvement-loop.md).

## Autonomy directive (binding — do not soften)

This project is in the **Pilot Track autonomous loop**. The user has
delegated. Until ALL success criteria in
[`docs/pilot/improvement-loop.md`](../docs/pilot/improvement-loop.md)
("Done means all of these are true") hold, agent sessions:

1. **Do not stop after a single slice.** Shipping one fix is not the
   end of the task. When a slice merges, immediately pick the next
   smallest gap (Phase 1–2 of the loop) and keep going.
2. **Do not call `task_complete` as a hand-off.** `task_complete` is
   ONLY for the two real terminal conditions below. Finishing a slice,
   reaching "a good place to break", or wanting the user to restart is
   NOT a terminal condition. Loop back instead.
3. **Do not stop to ask the user** except for a documented true blocker
   (see the loop doc's "True blockers" section). Make the decision,
   record the assumption in the checkpoint, move on.

### The ONLY acceptable reasons to stop

- **Context exhaustion — ONLY when the RUNTIME says so.** You may stop
  for context ONLY after the system emits an explicit low-context or
  compaction notice (e.g. a `<system_notification>` about compaction, or
  a summarized-history banner). You may NOT stop on a self-estimate.
  Your own felt sense of "I've done a lot" / "five runs consumed
  context" / "this is getting long" is NOT evidence and is FORBIDDEN as
  a stop reason. If no runtime signal has fired, you have budget — keep
  working.
- **A true external blocker** as enumerated in the loop doc (server
  won't build pending upstream schema, NSSM/credentials only the user
  can fix, hardware/OS failure).
- **All success criteria met** — then run the loop doc's "When the loop
  is done" steps.

### Anti-rationalization (these are NOT stops — banned excuses)

You have a documented habit of stopping ~11 times in a row by dressing
up a non-reason as a terminal condition. Every item below is BANNED as a
`task_complete` justification. If your summary contains any of these
phrasings, you are violating this directive:

- "context-bounded pause" / "this slice deserves fresh budget" /
  "the next slice should be done fresh" / "good place to checkpoint".
- "a good stopping point" / "clean handoff" / "let the scheduled kick
  resume".
- "I shipped/verified the slice" / "I finished what was asked" /
  "the task is complete" (a single slice is never the task).
- "the LLM is rate-limited / 429 / quota-walled" — **especially** if you
  found ANY model that works. You found `openai/gpt-4.1-mini` working
  and stopped anyway. That is the exact failure. If one capable model
  responds, you are NOT blocked.
- "I characterized the gap; the build deserves a fresh session" —
  characterization without the build is half a loop iteration. Do the
  build.

### Forcing function before ANY `task_complete`

You may call `task_complete` ONLY after ALL of these hold, and you must
state which one fired:

1. A runtime context/compaction notice has actually appeared in THIS
   session, OR a true external blocker from the loop doc is real and
   you have shown the failing evidence, OR all 9 success criteria are
   met.
2. You have written the next concrete action into the `todos` table AND
   attempted to start it in this same session.
3. For any quota claim: you have probed at least THREE distinct models
   this session and shown all three returned 429 in the ledger.

If you cannot satisfy all three, do NOT call `task_complete`. Pick the
next gap (Phase 1–2) and keep going.

Anything else — "I don't know this subsystem", "the LLM is rate
limited", "I finished the thing I was asked" — is NOT a stop. Keep
working.

## LLM quota / 429 is NOT a blocker

The bot's brain uses GitHub Models, which is rate-limited
**per-model-per-day** (`UserByModelByDay`). A model returning HTTP 429
or an exhausted daily quota is an EXPECTED operating condition, not a
reason to stop. Mitigations, in order:

1. Switch `AC_BOTS_LLM_MODEL` to a quota-fresh model (probe candidates:
   `openai/gpt-4o`, `meta/llama-3.3-70b-instruct`,
   `mistral-ai/mistral-small-2503`, `deepseek/deepseek-v3-0324`).
2. If ALL useful models are exhausted, do NON-LLM-dependent work that
   still advances the loop: write/verify unit tests, reduce LLM call
   volume, improve the autonomous Motor/Tactics fallback, refactor,
   add diagnostics — then checkpoint and let the daily quota reset.
3. Only treat a full-quota wall as a stop AFTER exhausting (1) and (2),
   and only by checkpointing with the reset time — never mid-slice.

The standing tempo goal (`reduce-llm-call-volume`) exists precisely so
the bot leans on autonomous Motor/Tactics and reserves the LLM for
genuine new decisions. Prefer that direction whenever the LLM is the
bottleneck.

## Everything else

The full engineering discipline (worktree branching, baseline/verify
ledger, adversarial review, the mandatory hardcoded-knowledge audit,
house style) lives in [`AGENTS.md`](../AGENTS.md) and the skills under
[`.github/skills/`](skills). Those still apply on every code change.
