#!/usr/bin/env bash
# preToolUse hook: intercept in-place branch creation and redirect to the
# create-new-branch skill (which creates a worktree under .worktrees/).
#
# Mirrors intercept-git-branch.ps1 for bash environments (Linux, macOS,
# WSL, git-bash on Windows). See the PowerShell version for full design
# notes, including the documented known limitations (nested-shell
# wrappers like `bash -c 'git ...'` evade the regex by design — full
# shell-quote parsing is intractable).
#
# Fail-open on every error path: emits `{}` so normal tool execution
# proceeds. Per the hooks reference, non-zero exits are logged and
# skipped — they never block agent execution.

set +e

allow() {
    echo '{}'
    exit 0
}

INPUT=$(cat 2>/dev/null)
[ -z "$INPUT" ] && allow

# jq is required for safe JSON parse/build. Fail-open if missing but
# emit a one-line warning to stderr so the user knows enforcement is
# degraded. On Linux/macOS install with `apt-get install jq` / `brew
# install jq`. On Windows the powershell branch of the manifest runs
# instead, so this script only loads under WSL or git-bash there.
if ! command -v jq >/dev/null 2>&1; then
    echo "intercept-git-branch.sh: jq not found — hook degraded to allow-all. Install jq to enforce." >&2
    allow
fi

# toolArgs is `unknown` in the spec; for bash/powershell tools it's
# { "command": "..." }. Older payloads may stringify it.
TARGS_TYPE=$(echo "$INPUT" | jq -r '.toolArgs | type' 2>/dev/null)
if [ "$TARGS_TYPE" = "string" ]; then
    CMD=$(echo "$INPUT" | jq -r '.toolArgs' | jq -r '.command // empty' 2>/dev/null)
else
    CMD=$(echo "$INPUT" | jq -r '.toolArgs.command // empty' 2>/dev/null)
fi
[ -z "$CMD" ] && allow

# Pattern construction (kept in sync with intercept-git-branch.ps1):
#
#   SEP      — command-position separator: start-of-string OR a real
#              shell separator (;, &&, ||, |, backtick, newline).
#              NOT plain whitespace (avoids false-deny on
#              `echo git checkout -b demo`).
#   GLOBALS  — optional git global options between `git` and the
#              subcommand: `-c key=val`, `--no-pager`, `-C /path`,
#              `--git-dir=...`, etc.
#   SUBCMD   — checkout or switch with its branch-creating flag,
#              covering short separated (`-b foo`), short attached
#              (`-bfoo`), and long (`--create foo`,
#              `--force-create foo`) forms.
#
# grep -E is POSIX ERE; backreferences and lookarounds aren't
# available so we keep the structure simple and post-process to
# pull out flag + branch.
SEP='(^|[;&|\`]|&&|\|\|)'
GLOBALS='([[:space:]]+(-[cC][[:space:]]+[^[:space:]]+|--git-dir(=[^[:space:]]+)?|--no-pager|--bare|--exec-path(=[^[:space:]]+)?|-C[[:space:]]+[^[:space:]]+|--[a-zA-Z-]+(=[^[:space:]]+)?|-[a-zA-Z][^[:space:]]*))*'
SUBCMD='(checkout[[:space:]]+-[bB][[:space:]]*[^[:space:]]+|switch[[:space:]]+(-[cC][[:space:]]*[^[:space:]]+|(--create|--force-create)[[:space:]]+[^[:space:]]+))'
PATTERN="${SEP}[[:space:]]*git${GLOBALS}[[:space:]]+${SUBCMD}"

if ! echo "$CMD" | grep -qE "$PATTERN"; then
    allow
fi

# Pull the flag and branch out for the reason text. Handle short
# separated, short attached, and long forms.
MATCH=$(echo "$CMD" | grep -oE "$SUBCMD" | head -1)

if echo "$MATCH" | grep -qE '^checkout[[:space:]]+-[bB][[:space:]]+'; then
    FLAG=$(echo "$MATCH" | sed -E 's/^(checkout[[:space:]]+-[bB]).*/\1/')
    BRANCH=$(echo "$MATCH" | sed -E 's/^checkout[[:space:]]+-[bB][[:space:]]+([^[:space:]]+).*/\1/')
elif echo "$MATCH" | grep -qE '^checkout[[:space:]]+-[bB]'; then
    # attached: -bfoo
    FLAG=$(echo "$MATCH" | sed -E 's/^(checkout[[:space:]]+-[bB]).*/\1/')
    BRANCH=$(echo "$MATCH" | sed -E 's/^checkout[[:space:]]+(-[bB])([^[:space:]]+).*/\2/')
elif echo "$MATCH" | grep -qE '^switch[[:space:]]+-[cC][[:space:]]+'; then
    FLAG=$(echo "$MATCH" | sed -E 's/^(switch[[:space:]]+-[cC]).*/\1/')
    BRANCH=$(echo "$MATCH" | sed -E 's/^switch[[:space:]]+-[cC][[:space:]]+([^[:space:]]+).*/\1/')
elif echo "$MATCH" | grep -qE '^switch[[:space:]]+-[cC]'; then
    FLAG=$(echo "$MATCH" | sed -E 's/^(switch[[:space:]]+-[cC]).*/\1/')
    BRANCH=$(echo "$MATCH" | sed -E 's/^switch[[:space:]]+(-[cC])([^[:space:]]+).*/\2/')
else
    FLAG=$(echo "$MATCH" | sed -E 's/^(switch[[:space:]]+(--create|--force-create)).*/\1/')
    BRANCH=$(echo "$MATCH" | sed -E 's/^switch[[:space:]]+(--create|--force-create)[[:space:]]+([^[:space:]]+).*/\2/')
fi
TASK_ID="${BRANCH#anvil/}"

REASON=$(cat <<EOF
Direct \`git ${FLAG} ${BRANCH}\` is not allowed in this repository.

This repo uses an isolated-worktree branch workflow. Branches are created
inside .worktrees/<task-id>/ so the main checkout stays clean and parallel
work doesn't collide. Two ways forward:

1. Invoke the create-new-branch skill (preferred):
   Follow the runbook at .github/skills/create-new-branch/SKILL.md
   with task_id='${TASK_ID}'. It creates .worktrees/${TASK_ID}/, sets the
   branch to anvil/${TASK_ID}, and moves the working directory into it.

2. Or run the equivalent manually from the repo root:
   git worktree add -b anvil/${TASK_ID} .worktrees/${TASK_ID} <base-branch>

See AGENTS.md > Branch workflow for the rule. This hook lives at
.github/hooks/enforce-worktree-branching.json and intercepts
'git checkout -b/-B' and 'git switch -c/-C' across all sessions.
EOF
)

jq -n --arg reason "$REASON" \
    '{permissionDecision: "deny", permissionDecisionReason: $reason}'
exit 0
