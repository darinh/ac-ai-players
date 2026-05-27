# preToolUse hook: intercept in-place branch creation and redirect to the
# create-new-branch skill (which creates a worktree under .worktrees/).
#
# Triggers a deny verdict for any of these branch-creation forms,
# including the attached-arg variant (-bfoo) and git's global-option
# prefix (git -c key=val checkout -b foo):
#   - git checkout -b/-B <branch>        (short, separated)
#   - git checkout -b<branch>            (short, attached)
#   - git switch   -c/-C <branch>        (short, separated)
#   - git switch   -c<branch>            (short, attached)
#   - git switch   --create <branch>     (long)
#   - git switch   --force-create <branch>
#
# `git` must be in command position — at start-of-string or after a
# real shell command separator (;, &&, ||, |, backtick, newline).
# Plain whitespace is NOT a separator, so harmless text like
# `echo git checkout -b demo` (a literal string passed to echo) is
# correctly allowed.
#
# All other inputs (including bare `git checkout <branch>`, file
# reverts, `git worktree add`, malformed JSON, empty stdin, missing
# fields) emit `{}` so normal tool execution proceeds.
#
# Known limitations (documented, not bugs):
#   - Deliberate nested-shell wrappers like `bash -lc 'git checkout
#     -b foo'` or `cmd /c "git checkout -b foo"` evade the regex
#     because `git` is preceded by a quote, not by a command-position
#     separator. Defending against this would require full shell-quote
#     parsing, which is intractable in a hook regex and pulls in
#     false-positives on innocent quoted text. The expectation is
#     that agents follow AGENTS.md; deliberate evasion is a different
#     trust problem and is partially backstopped by git-side hooks
#     (a future companion piece).
#   - `git branch <newname>` (creates a ref without checkout) is not
#     intercepted because it doesn't put the user "on" the branch.
#     If you `git branch foo && git switch foo`, the second command
#     is allowed.
#
# Why fail-open on errors: per the hooks reference, non-zero exits
# are logged and skipped — they never block agent execution.
# Emitting `{}` explicitly is the safe documented allow path.
#
# Schema (deny verdict, flat):
#   { "permissionDecision": "deny", "permissionDecisionReason": "..." }
# Schema (allow): {} or empty stdout.

$ErrorActionPreference = 'Continue'

function Write-Allow {
    Write-Output '{}'
    exit 0
}

try {
    $raw = [Console]::In.ReadToEnd()
} catch {
    Write-Allow
}
if (-not $raw) { Write-Allow }

try {
    $payload = $raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Allow
}

$toolArgs = $payload.toolArgs
if ($toolArgs -is [string]) {
    try { $toolArgs = $toolArgs | ConvertFrom-Json -ErrorAction Stop } catch { Write-Allow }
}
$cmd = $null
if ($toolArgs) { $cmd = $toolArgs.command }
if (-not $cmd) { Write-Allow }

# Pattern construction (built piece by piece for readability):
#
#   $sep      — command-position separator: start-of-string OR a real
#               shell separator. NOT plain whitespace (which would
#               cause false-denies on `echo git checkout -b demo`).
#   $globals  — optional git global options between `git` and the
#               subcommand (e.g. `git -c core.autocrlf=false ...`,
#               `git --no-pager ...`, `git -C /path ...`).
#   $subcmd   — checkout or switch with its branch-creating flag,
#               supporting short separated (`-b foo`), short attached
#               (`-bfoo`), and long (`--create foo`, `--force-create
#               foo`) forms. Branch name captured.
$sep = '(?:^|[;&|`\n]|&&|\|\|)\s*'
$globals = '(?:-[cC]\s+\S+|--git-dir(?:=\S+)?|--no-pager|--bare|--exec-path(?:=\S+)?|-C\s+\S+|--[a-zA-Z-]+(?:=\S+)?|-[a-zA-Z]\S*)'
$subcmd = '(checkout\s+(-[bB])\s*(\S+)|switch\s+(?:(-[cC])\s*(\S+)|(--create|--force-create)\s+(\S+)))'
$pattern = "${sep}git\b(?:\s+${globals})*\s+${subcmd}"
$m = [regex]::Match($cmd, $pattern)
if (-not $m.Success) { Write-Allow }

# The subcmd alternation has three branches; only one set of capture
# groups will be populated. Coalesce to extract flag + branch.
if ($m.Groups[2].Success) {
    # checkout -b/-B (sep or attached)
    $flag = "checkout $($m.Groups[2].Value)"
    $branch = $m.Groups[3].Value
} elseif ($m.Groups[4].Success) {
    # switch -c/-C (sep or attached)
    $flag = "switch $($m.Groups[4].Value)"
    $branch = $m.Groups[5].Value
} else {
    # switch --create / --force-create
    $flag = "switch $($m.Groups[6].Value)"
    $branch = $m.Groups[7].Value
}
$taskId = $branch -replace '^anvil/', ''

$reason = @"
Direct ``git $flag $branch`` is not allowed in this repository.

This repo uses an isolated-worktree branch workflow. Branches are created
inside .worktrees/<task-id>/ so the main checkout stays clean and parallel
work doesn't collide. Two ways forward:

1. Invoke the create-new-branch skill (preferred):
   Follow the runbook at .github/skills/create-new-branch/SKILL.md
   with task_id='$taskId'. It creates .worktrees/$taskId/, sets the
   branch to anvil/$taskId, and moves the working directory into it.

2. Or run the equivalent manually from the repo root:
   git worktree add -b anvil/$taskId .worktrees/$taskId <base-branch>

See AGENTS.md > Branch workflow for the rule. This hook lives at
.github/hooks/enforce-worktree-branching.json and intercepts
'git checkout -b/-B' and 'git switch -c/-C' across all sessions.
"@

$verdict = [ordered]@{
    permissionDecision = 'deny'
    permissionDecisionReason = $reason
}
$verdict | ConvertTo-Json -Compress -Depth 4
exit 0
