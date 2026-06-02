# AGENTS.md — `ac-ai-players`

Guidance for AI agents (Copilot CLI, Anvil, Claude, etc.) working
in this repo. Auto-loaded by Copilot CLI from the repo root and
cwd. Keep this file short; deeper context lives in linked docs.

## What this repo is

A repo for ACE AI Players — a project to add NPC-bot players to an
Asheron's Call Emulator (ACE) server. It holds the project's
planning material (milestones, ADRs, research notes, roadmap) and
the bot code under [`experiments/`](experiments).

**The bot runtime is a distributed headless client**, not a
server-side bot object. The bots run as headless AC network
clients that connect to the server like a real player and play the
game over the wire — no `BotPlayer`-in-`ACE.Server` objects, no
admin commands.

- [`experiments/headless-client/`](experiments/headless-client) —
  the bot runtime (headless AC client + brain). NOTE: the source
  currently lives on the spike branches under `.worktrees/`
  (`anvil/llm-deliberation-race` is the furthest along); it is
  being consolidated back onto `main`. Only `data/` is on `main`
  today.
- [`experiments/world-nav/`](experiments/world-nav) — the static
  navmesh library: waypoints extracted from the AC1 DAT files. The
  headless client consumes it for pathfinding; it also backs the
  optional server-side `ACE.Mod.Pathfinding` Harmony mod (see
  [ADR-0010](docs/adr/0010-pathfinding-as-standalone-mod.md)).

The server fork `darinh/ACE-bots` hosts the ACE server you run the
clients against, plus any server-side mods. **Bot decision-making
lives in the headless client in THIS repo — not in the server
fork.** A server-side `BotPlayer.cs` track exists in that fork from
an earlier pivot; it is being retired in favor of the headless
client.

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

## Hardcoded knowledge audit (MANDATORY for bot code changes)

Every commit by an AI agent that touches the bot's decision-making
code MUST pass an adversarial review for **hardcoded game
knowledge** before `git push`. This is binding, not advisory.

The architecture (Pilot Track) is:

- LLM (Strategy) decides WHAT to do → pushes Intents.
- IntentStack persists strategic decisions across ticks.
- Goal = tactical decomposition of the current top Intent.
- Motor (HandshakeDriver picker + action dispatch + the headless
  client's tick loop) executes Goals.

Source code MAY:
- Decode wire-protocol bits into named projection properties
  (`IsContainer`, `IsOpenable`, `IsStuck`).
- Mechanically execute a Goal/Intent (walk to coord, send
  opcode).
- Maintain bookkeeping the LLM cannot do (ack queues, revision
  counters, eviction TTLs).

Source code MAY NOT:
- Assign priorities or urgency to in-game object types
  (`if (isChest) prio = 0`).
- Hardcode lists of NPC names, quest names, wcids, landblocks.
- Decide on its own to interact with a target the LLM has not
  asked it to interact with (the picker autonomously walking to
  the nearest NPC counts).
- Encode rules of thumb the LLM should learn from the prompt.

How to comply (per commit):

1. Stage changes (`git add`), then commit locally.
2. Invoke the
   [`audit-hardcoded-knowledge`](.github/skills/audit-hardcoded-knowledge/SKILL.md)
   skill against the local commit SHA. Use the canonical prompt
   in that SKILL.md verbatim.
3. If the audit returns FORBIDDEN findings, fix in the same
   commit (revert the bump, move the rule to the prompt, refactor
   into an Intent push) OR amend to remove offending hunks.
4. Only push after a clean audit.

This applies to commits touching the headless client's
decision-making code in this repo
(`experiments/headless-client/`). Doc-only commits, test-only
commits, dependency bumps, and CI config changes are exempt.

History: this discipline was added after the Slice U incident,
where chest-loot priority bumps slipped into the picker as
`prio = 0 // chest = loot-critical`. The audit caught it on a
retrospective pass; this rule prevents future incidents.

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
