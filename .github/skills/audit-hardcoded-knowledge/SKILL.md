# audit-hardcoded-knowledge

Run an adversarial review of a code diff to catch **hardcoded game
knowledge** before it ships. This is mandatory for any agent
commit that touches the headless client bot or this repo's
strategy / motor / projection code.

> Why this exists: the Pilot Track architecture says LLMs decide
> WHAT (push Intents), source code only decides HOW (decode wire
> bits, walk to coordinates, send opcodes). It is structurally
> easy to slip game-specific value judgements into picker
> priorities, urgency bumps, "always do X with Y" heuristics, or
> RULES bullets that contain spoilers. Each such slip is a future
> bug AND a regression toward "ACE bot with bolted-on LLM" instead
> of "LLM player with mechanical motor".

## When to use this skill

- **Mandatory** before every commit by an AI agent that touches
  any of:
  - `experiments/headless-client/src/HeadlessAcClient/Handshake/**`
  - `experiments/headless-client/src/HeadlessAcClient/Strategy/**`
  - `experiments/headless-client/src/HeadlessAcClient/Tactics/**`
  - `experiments/headless-client/src/HeadlessAcClient/World/**`
- Optional but recommended for any RULES / system-prompt change
  in `LlmGoalPolicy.cs` or `NoQuestKnowledgePolicy.cs` — these are
  the boundary where "prompt knowledge" can become "spoilers /
  hand-holding".

You do NOT need to invoke this for: pure docs, pure tests,
configuration files, dependency bumps, or code that has nothing to
do with the bot's decision-making.

## How to invoke

Use the Copilot CLI `task` tool with `agent_type: code-review`
and pass the canonical prompt below. The audit must run BEFORE
`git push`. If the audit returns FORBIDDEN findings, fix them in
the same commit OR roll back the offending hunks — do not push a
known-FORBIDDEN diff and "fix it later".

## Canonical prompt

> Copy this verbatim into the `prompt:` argument of the `task`
> tool. Replace `<COMMIT_SHA>` and `<WORKTREE_PATH>` per call.

```
You are auditing commit <COMMIT_SHA> in <WORKTREE_PATH> for a
SPECIFIC class of issue: hardcoded game knowledge in the bot's
source code.

## Background

This project is building autonomous NPC bots for Asheron's Call.
The architecture is:

- LLM (Strategy layer) decides WHAT to do → pushes Intents onto
  the IntentStack.
- IntentStack persists strategic decisions across ticks.
- Goal = tactical decomposition of the current top Intent.
- Motor (HandshakeDriver picker + action dispatch + the headless
  client tick) executes Goals — walks to targets, sends opcodes,
  records outcomes.

The Pilot Track directive at `docs/pilot/improvement-loop.md` and
the long-standing repo memory "Bots are bots, not agents — LLMs
only at the Social layer" (now generalised to quest comprehension)
mean: source code must contain ZERO game-specific knowledge.

Source code IS allowed to:
- Decode wire-protocol bits into named projection properties
  (e.g. ItemType.Container → IsContainer). These translate raw
  bits to stable names that the LLM can read in the prompt.
- Mechanically execute a Goal/Intent the LLM has proposed
  (walk to (x,y), send USE on guid Z, fire PUTITEMINCONTAINER).
- Maintain bookkeeping the LLM cannot do for itself (revision
  counters, ack queues, position smoothing, eviction TTLs).

Source code is NOT allowed to:
- Assign priorities or urgency to in-game object types.
  (e.g. "prio = 0 for chests", "corpses decay so bump them up",
  "NPCs are always priority 0".)
- Hardcode lists of NPC names, quest names, item wcids, or
  landblock IDs.
- Decide on its own to interact with a target the LLM has not
  asked it to interact with.
- Encode rules of thumb ("always loot before moving on", "talk
  to greeter first", "wield best armor on level up") that are
  game knowledge.

Game knowledge belongs in the LLM prompt (RULES bullets,
visibility tags) or in server-driven runtime data (weenie
properties, server messages, BookText events). Never in source.

## What to do

For EACH hunk in the diff, classify:

1. PROJECTION — translating wire bits to a named property. ALLOWED.
2. MECHANICAL — generic motor behavior (executes a Goal verb).
   ALLOWED.
3. PROMPT — text added to LlmGoalPolicy / RULES / visibility tags
   / system prompt. ALLOWED (this is what the LLM reads).
4. HARDCODED KNOWLEDGE — source code that encodes a game-specific
   preference, urgency, priority, list, or behavior decision.
   FORBIDDEN. Flag every instance.

For each FORBIDDEN item, quote:
- The exact file + line range.
- The exact code snippet.
- WHY it's game knowledge.
- The correct shape (move to prompt? require LLM to push an
  Intent first? remove entirely?).

Also examine code touched by the commit AROUND the hunks — if a
hunk extends a hardcoded list or priority ladder, the surrounding
list/ladder may already be FORBIDDEN even though it pre-dates the
commit. Call that out as a "pre-existing smell exemplified by this
commit" finding.

## Output

Bulleted list, FORBIDDEN findings first, then ALLOWED
classifications. End with a recommended cleanup scope and an
explicit "OK to push" or "DO NOT PUSH" verdict.
```

## Acting on findings

- **FORBIDDEN** → fix in the same commit (revert the bump, move
  the rule to the prompt, refactor into an Intent push) OR amend
  the commit to remove the offending hunks. Do NOT push.
- **ALLOWED** → push.
- **Borderline** (e.g. a strongly-worded RULES bullet that's
  almost a spoiler) → soften the wording, commit the softening,
  re-audit, then push.

If you push a commit with FORBIDDEN findings AGAINST the auditor's
verdict, document why in the commit message — the next session
will see the contradiction and may revert.

## Failure modes

| Symptom | Cause | Recovery |
|---|---|---|
| Auditor returns "no diff found" | passed wrong SHA or worktree | Re-run with `git -C <worktree> log -1 --pretty=%H` |
| Auditor floods FORBIDDEN findings on a refactor | refactor renamed but didn't introduce hardcoding | Check the BEFORE side too; if the before was already FORBIDDEN, file an epic instead of blocking the refactor |
| Auditor takes >60s | code-review agent doing deep analysis | Wait. Don't switch to a smaller model — the audit is the safety net. |

## Why not just a linter

A linter cannot tell that `if (isCorpse) prio = 0` is game
knowledge but `if (isContainer) IsContainerOpenable = true` is
projection. The distinction is semantic, not syntactic. The
code-review agent has the context of the architecture + the
intent of each line.

## Related

- `docs/pilot/improvement-loop.md` — the autonomous loop directive
  that this audit protects.
- `docs/pilot/plan-vocabulary.md` — the Intent / Goal / Motor
  vocabulary the auditor uses.
- `AGENTS.md` "Hardcoded knowledge audit" section — the binding
  rule that this skill implements.
