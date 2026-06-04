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

- **Context exhaustion**: remaining context is low (< ~25%). Checkpoint
  thoroughly so the next session resumes, then stop. The scheduled
  `pilot-loop` kick (or the user) starts a fresh session.
- **A true external blocker** as enumerated in the loop doc (server
  won't build pending upstream schema, NSSM/credentials only the user
  can fix, hardware/OS failure).
- **All success criteria met** — then run the loop doc's "When the loop
  is done" steps.

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
