# Skills index

Reusable agent skills used in this repo. Each lives in its own
folder with a `SKILL.md` that describes when to invoke it and what
it does. Most are vendored from
[obra/superpowers](https://github.com/obra/superpowers) under MIT —
see [`NOTICE.md`](NOTICE.md).

## Index

| Skill | When to invoke |
|---|---|
| [`systematic-debugging/`](systematic-debugging/SKILL.md) | Any bug, test failure, or unexpected behavior. **Iron Law: no fixes without root-cause investigation first.** |
| [`verification-before-completion/`](verification-before-completion/SKILL.md) | Before claiming a task is done. Forces evidence gathering. |
| [`test-driven-development/`](test-driven-development/SKILL.md) | Writing tests for new functionality. RED-GREEN-REFACTOR loop. |
| [`subagent-driven-development/`](subagent-driven-development/SKILL.md) | Delegating implementation to sub-agents with strong specs. |
| [`requesting-code-review/`](requesting-code-review/SKILL.md) | Before merging a non-trivial change. |
| [`receiving-code-review/`](receiving-code-review/SKILL.md) | When acting on review feedback. |
| [`dispatching-parallel-agents/`](dispatching-parallel-agents/SKILL.md) | Multi-thread investigations that benefit from parallel exploration. |
| [`brainstorming/`](brainstorming/SKILL.md) | Open-ended design problems with multiple paths. |
| [`writing-plans/`](writing-plans/SKILL.md) | Producing a plan.md that another agent can pick up. |
| [`executing-plans/`](executing-plans/SKILL.md) | Resuming work from a plan.md. |
| [`finishing-a-development-branch/`](finishing-a-development-branch/SKILL.md) | Wrapping up a branch for merge. |
| [`writing-skills/`](writing-skills/SKILL.md) | Authoring new skills. |
| [`create-new-branch/`](create-new-branch/SKILL.md) | Mandatory for Medium/Large tasks. Repo-local; creates worktree under `.worktrees/`. |

## Discovery rule for agents

When you encounter a task category, check this index FIRST before
ad-hoc reasoning. A skill that already encodes a tested discipline
beats freshly-improvised process every time.

Frequent triggers:

- **A bug, test failure, or unexpected output** → invoke
  `systematic-debugging`. Do NOT propose fixes until Phase 1 is
  complete.
- **About to claim "done"** → invoke
  `verification-before-completion`.
- **About to delegate work to a sub-agent** → invoke
  `subagent-driven-development` to write the spec.
- **Starting a Medium/Large task** → invoke `create-new-branch`.
