# AGENTS.md — `ac-ai-players`

Guidance for AI agents (Copilot CLI, Anvil, Claude, etc.) working
in this repo. Auto-loaded by Copilot CLI from the repo root and
cwd. Keep this file short; deeper context lives in linked docs.

## What this repo is

A docs-only planning repo for ACE AI Players — a project to add
NPC-bot players to an Asheron's Call Emulator (ACE) server. The
shipped code lives in the fork repo `darinh/ACE-bots`. Until M0
fully closes, this repo holds milestones, ADRs, research notes,
and the public roadmap.

See [`README.md`](README.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md)
for the project overview, status, and contributor ground rules.

## ACTIVE DIRECTIVE — Pilot Track autonomous loop

The project is in **Pilot Track v5** (see
`~/.copilot/session-state/<id>/plan.md`). The bot is being driven to
play AC end-to-end via a three-layer brain (Motor / Tactics /
Strategy), with an LLM acting as a quest compiler (not a controller).

**Until the success criteria in
[`docs/pilot/improvement-loop.md`](docs/pilot/improvement-loop.md)
are met, agent sessions must:**

1. Read `docs/pilot/improvement-loop.md` first.
2. Operate autonomously per that loop — do not stop and wait for the
   user except for the documented true blockers.
3. Treat the earlier rule "Bots are bots, not agents — LLMs only at
   the Social layer" as **superseded**: the LLM is now used for quest
   comprehension as well, in a constrained compiler role. See
   [`docs/pilot/plan-vocabulary.md`](docs/pilot/plan-vocabulary.md).
4. Checkpoint before context exhaustion so the next session resumes
   cleanly.

A scheduled prompt named `pilot-loop` periodically kicks a fresh
session to advance the loop.

## House style (match this or docs will feel off)

- Plain, direct prose. No marketing voice. No hype. No emoji.
- Short sentences. Cross-link related docs via relative paths.
- (Historical: "Bots are bots, not agents — LLMs only at the Social
  layer." Superseded by the Pilot Track directive above; the loop doc
  governs current architecture.)

## Branch workflow (MANDATORY for Medium and Large tasks)

**Rule:** For any Medium or Large task in this repo, you MUST
invoke the `create-new-branch` skill before making any code
changes. Do NOT run a bare `git checkout -b` — even if your agent
framework's branch-check step (e.g. Anvil step 0b) suggests it.
The skill replaces that step.

Why this is mandatory, not advisory:
- Worktrees keep the main checkout untouched between tasks.
- Branch naming and `.gitignore` hygiene are handled in one place.
- The agent's working directory is moved to the worktree, so a
  session interruption leaves recoverable state on disk.

How:

1. The skill lives at
   [`.github/skills/create-new-branch/SKILL.md`](.github/skills/create-new-branch/SKILL.md).
2. Inputs: `task_id` (required, slugified Anvil task-id);
   `base_branch` (optional — see below).
3. The skill creates `.worktrees/<task-id>/` on branch
   `anvil/<task-id>`, then `/cwd`s the agent into it.

When the branch is merged, delete the worktree (use `-d`, not
`-D`, to protect against accidental loss of unmerged work):

```sh
git worktree remove .worktrees/<task-id>
git branch -d anvil/<task-id>
```

A `cleanup-merged-worktree` skill will automate this once it
exists.

Small tasks (typo, one-liner, doc tweak) may commit directly on
the current branch — the worktree overhead is not worth it. Use
your judgment but err on the side of using the skill.

## Skills (consult BEFORE ad-hoc reasoning)

This repo vendors a set of reusable agent skills under
[`.github/skills/`](.github/skills/INDEX.md). Read the index first
when a task fits a known category. Highest-value triggers:

- **Any bug, test failure, unexpected output** → invoke
  [`systematic-debugging`](.github/skills/systematic-debugging/SKILL.md).
  Iron Law: no fixes without root-cause investigation first. If
  you've tried 3+ fixes without success, STOP and question the
  architecture.
- **About to claim "done"** → invoke
  [`verification-before-completion`](.github/skills/verification-before-completion/SKILL.md).
- **About to delegate to a sub-agent** → invoke
  [`subagent-driven-development`](.github/skills/subagent-driven-development/SKILL.md)
  to write the spec.
- **Starting a Medium/Large task** → invoke
  [`create-new-branch`](.github/skills/create-new-branch/SKILL.md)
  (see "Branch workflow" below).
- **About to merge a non-trivial change** → invoke
  [`requesting-code-review`](.github/skills/requesting-code-review/SKILL.md).

See [`INDEX.md`](.github/skills/INDEX.md) for the full list.

## GitHub Actions hygiene

- Pin third-party actions to full 40-char commit SHAs (not
  floating tags like `@v4`); add an inline `# v4` comment.
- When a workflow declares an explicit `permissions:` block AND
  uses `actions/checkout`, it must grant `contents: read` —
  otherwise the checkout fails.

## Related instruction files

- This file (`AGENTS.md`) — primary, auto-loaded.
- [`.github/copilot-instructions.md`](.github/copilot-instructions.md)
  — repo-wide Copilot instructions (currently absent; add if
  needed).
- [`.github/instructions/`](.github/instructions) — path-scoped
  instructions (currently absent).
