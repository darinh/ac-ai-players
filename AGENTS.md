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
Strategy), with an LLM acting as a quest compiler (not a controller)
AND as the bot's in-game chat handler.

**Until the success criteria in
[`docs/pilot/improvement-loop.md`](docs/pilot/improvement-loop.md)
are met, agent sessions MUST NOT:**

- **Call `task_complete` for milestone work.** `task_complete` is
  reserved for the moment all 9 done criteria in the loop doc hold,
  or for a true blocker per the loop's "True blockers" list.
  Finishing a PR, fixing a bug, or completing one todo is NOT a
  reason to call `task_complete`.
- **Call `ask_user` for routine decisions.** Apply autopilot:
  pick the option that best matches the user's stated preferences,
  record the assumption in the checkpoint, proceed. Ask only when a
  true blocker requires user-side action (credentials, hardware,
  schema changes that need user sign-off).
- **Stop tool-calling at a "good place to break."** When a PR lands
  or a milestone closes, immediately query the todos table or open
  issues for the next item and start it.
- **Wait for the user** except for the documented true blockers.

**Agent sessions MUST:**

1. Read `docs/pilot/improvement-loop.md` first.
2. Operate autonomously per that loop.
3. Treat the earlier rule "Bots are bots, not agents — LLMs only at
   the Social layer" as **superseded**: the LLM is used for quest
   comprehension AND for in-game chat (bot↔bot and bot↔player) in a
   constrained role. See
   [`docs/pilot/plan-vocabulary.md`](docs/pilot/plan-vocabulary.md).
4. Checkpoint before context exhaustion so the next session resumes
   cleanly.

A scheduled prompt named `pilot-loop` periodically kicks a fresh
session to advance the loop. The schedule is a backstop. The
primary mechanism is the agent that is currently running NOT
stopping.

## Bot in-game chat — LLM-driven, anti-spam (MANDATORY)

The bot's in-game chat is LLM-driven in both directions: bot↔bot
AND bot↔player. Anti-spam discipline is non-negotiable.

- **Two-stage gate.** Every potential outbound chat MUST run a
  "should I speak?" decision BEFORE the "what should I say?"
  generation. Default to silence.
- **Valid reasons to speak:** addressed directly (`/tell`, fellowship
  chat directed at this bot, local `@me`-style mention), an
  in-character situational response (e.g. responding to a trade
  offer, a quest handoff cue), or an explicit roleplay beat that
  fits the bot's persona.
- **Forbidden:** ambient filler, periodic "I'm still here" messages,
  repeating recent lines, monologuing, broadcasting the bot's
  internal state, narrating the bot's plan.
- **Hard limits (enforce in code, not just policy):**
  - Per-channel cooldown: at least 30 seconds between this bot's
    own outbound messages on the same channel (general, fellowship,
    allegiance, tell-thread).
  - Max consecutive: at most 3 messages from this bot in a thread
    without a non-bot turn in between. A "thread" is a tell-thread,
    a fellowship exchange, or a local-chat back-and-forth bracketed
    by ≤ 60 second gaps.
  - Near-duplicate suppression: do not emit a message whose
    normalized text matches anything this bot said in the last 5
    minutes (case-folded, whitespace-collapsed exact match is the
    floor; semantic dedup is better).
  - These are defaults. A persona may tune them in a checked-in
    config file, but never relax them silently from the LLM call.
- **Hardcoded text matching is forbidden** (see the no-cheating
  section below). To decide if a chat line is addressed to the bot,
  pass the speaker, the channel, the line, and the bot's recent
  context to the LLM — do not pattern-match strings.

## Bot world knowledge — no cheating (MANDATORY)

The point of this project is a bot that **learns the world by
playing it**, the same way a human player does. The constructed
NavGraph and all derived knowledge are the whole architecture.
Shortcuts that bypass exploration defeat the architecture.

**Allowed sources for environmental knowledge:**

1. The headless client wire stream (what a normal AC1 client
   receives: `ObjectCreate`, `SetState`, position updates, chat,
   NPC tells, `WeenieError` responses, etc.).
2. The bot's own constructed state, **derived only from allowed
   wire/UI gameplay inputs (sources 1 and 3)**: NavGraph
   nodes/edges built from walking, journals, inventory, chat
   memory, observations. Seeding constructed state from any
   forbidden source below is itself forbidden — the constructed-
   state allowance is not a laundering channel for cheats.
3. In-game UI surfaces a human player can see at the keyboard:
   the world map, the radar, the `@` command output, item examine
   text. (Example: the canonical landmark seed in
   [#77](https://github.com/darinh/ac-ai-players/issues/77) part 2
   is allowed because every player can open the world map.)

**Forbidden sources (this is cheating; do not propose it):**

- Static client DAT extracts: `EnvCell`, `BldPortal`, wall
  geometry, cell adjacency, weenie database dumps, anything from
  `ACE.DatLoader` / `client_cell_1.dat` / `client_portal.dat`.
- ACE server-internal types not on the wire: `WeenieType`,
  `Biota` properties, anything the server keeps private from the
  client.
- **Server-side observability that no human client can see:**
  server console output, server log files
  (`C:\ACE\Logs\ACE_Log.txt`), direct database queries against
  `ace_shard` / `ace_world` / `ace_auth`, GM/admin chat
  endpoints, packet captures of other clients' sessions. The bot
  perceives only what its OWN client connection receives. (The
  human developer may read server logs for debugging; the bot's
  source code may not.)
- Pre-baked "I already know about this dungeon" lookup tables built
  from server-side data.
- Hardcoded semantic string matching on bot-perceived text **in any
  language or localization** (e.g. `Name.Contains("Door")`,
  `text.StartsWith("You see")`, equivalent patterns in French /
  German / custom-content text). Use typed wire flags for
  structured perception (`ObjectDescriptionFlag`, `ItemType`,
  `ObjectCreateFlags`); route semantic text to the LLM with full
  context.

If you find yourself wanting to ship pre-computed topology so the
bot "just knows" where things are inside a building, **stop**. The
bot must walk the building. That walk produces the NavGraph nodes
and walkability evidence that everything else builds on. Skipping
the walk skips the architecture.

The full rationale is in
[`docs/adr/0012-no-cheating-bot-world-knowledge.md`](docs/adr/0012-no-cheating-bot-world-knowledge.md).
The pilot loop's
[anti-patterns section](docs/pilot/improvement-loop.md#anti-patterns-dont-do-these)
mirrors this rule.

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
