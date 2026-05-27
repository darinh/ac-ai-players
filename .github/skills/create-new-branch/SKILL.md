---
name: create-new-branch
description: >-
    Create a new git branch inside a worktree under the repo's
    .worktrees/ folder. Branch name is derived from a task-id slug
    (becomes `anvil/<task-id>`); worktree path is
    `.worktrees/<task-id>`. Use this for any Medium or Large task
    BEFORE making changes — it keeps work isolated, makes rollback
    cheap, prevents accidental commits to main, and survives if
    the agent session is randomized. This replaces the bare
    `git checkout -b` flow. Invoke when the user asks to start
    new work, when Anvil's branch-check step (0b) wants to create
    a branch, or when the user explicitly asks for `/create-new-branch`.
user-invocable: true
---

# create-new-branch

Create a git branch inside an isolated worktree under
`.worktrees/<task-id>/`. The branch is named `anvil/<task-id>`.

> **Hard enforcement.** This repo ships a `preToolUse` hook at
> `.github/hooks/enforce-worktree-branching.json` that intercepts
> bare `git checkout -b/-B <name>` and `git switch -c/-C <name>`
> (including `--create`, `--force-create`, attached-arg `-bfoo`,
> and `git -c x=y checkout -b foo`) and denies them with a reason
> that points back to this skill. You normally don't have to think
> about the hook — just invoke this skill when you need a new
> branch and the hook stays quiet. The hook is the safety net for
> the times the convention slips.

## When to use this skill

- At the start of any Medium or Large task (Anvil sizing).
- Whenever Anvil's step 0b ("Branch check") would have run
  `git checkout -b anvil/{task_id}` — run THIS skill instead so
  the branch lives in a worktree.
- When the user wants a clean sandbox for a spike, experiment, or
  PR-bound change.

Do NOT use this skill for:
- Small tasks (typo, one-liner, doc tweak) — commit directly on
  the current branch. Worktree overhead isn't worth it.
- Continuing work already in progress on an existing branch — `cd`
  to the existing worktree (`git worktree list`).

## Inputs

| Input | Required | Default | Notes |
|---|---|---|---|
| `task_id` | yes | — | Slug: `[a-z0-9-]+`, ≤ 40 chars. No slashes. The skill validates and rejects anything else (prevents path-traversal like `../escape`). Should match the Anvil `task_id` for the current task. |
| `base_branch` | no | repo default branch (resolved from `origin/HEAD`) | The branch to fork from. Falls back to `main` then `master` if `origin/HEAD` isn't set. |

## Pre-flight checks (the skill aborts on any failure)

1. **Inside a git repo.** `git rev-parse --show-toplevel` must
   succeed. Capture the result as `<repo-root>`.
2. **Target path is not nested under another worktree.** Compute
   the absolute target `<repo-root>/.worktrees/<task-id>`. Walk
   `git worktree list --porcelain` and reject if the target's
   parent directory chain crosses any existing worktree root other
   than the main checkout. (Being inside a linked worktree is
   FINE — e.g., `ACE-bots` itself is a worktree of `ACE`. The
   skill can still create siblings under `ACE-bots/.worktrees/`.
   What's forbidden is putting `.worktrees/<task-id>` *underneath*
   another worktree's working tree.)
3. **`task_id` is a valid slug.** Regex `^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$`.
   Reject empties, uppercase, underscores, slashes, leading/trailing
   hyphens. **PowerShell gotcha:** `-match` is case-insensitive by
   default and will incorrectly accept `MyTask`. Use `-cmatch` or
   `[regex]::IsMatch($task_id, $rx)`. In `bash`, `python`, and
   `node` the regex is case-sensitive as written.
4. **Clean working tree.** `git status --porcelain` returns no
   lines. If dirty, abort: *"Commit, stash, or revert local changes
   before branching."* (Mirrors Anvil's existing step 0b dirty-state
   check — do not try to handle stashing in this skill.)
5. **`.worktrees/` is gitignored.** Open `<repo-root>/.gitignore`.
   If no line matches `^\.worktrees/?$`, append:
   ```
   
   # Local-only: agent worktrees (see .github/skills/create-new-branch)
   .worktrees/
   ```
   Stage the change. Mention in the report that the gitignore was
   updated so the agent includes it in the current task's commit
   (do not commit it from inside the skill).
6. **Reuse check.** If `<repo-root>/.worktrees/<task-id>` already
   exists as a directory, do NOT trust `Test-Path` alone — a
   stale dir can exist without a valid worktree registration.
   Verify all four:
   - `<target>` appears in `git -C <repo-root> worktree list
     --porcelain` output as a registered worktree path (this is
     the authoritative check; `is-inside-work-tree` is unreliable
     because git walks up to the parent repo and returns `true`
     for any subdirectory).
   - `git -C <target> rev-parse --show-toplevel` (normalized to
     an absolute path) equals `<target>` itself (confirms `<target>`
     is its own worktree root, not just a subdir of the main repo).
   - `git -C <target> rev-parse --git-common-dir` (normalized)
     matches `<repo-root>`'s common-dir.
   - `git -C <target> symbolic-ref --short HEAD` returns
     `anvil/<task-id>`.
   If all four pass → enter reuse mode (see §Create the worktree).
   If any fail → abort with: *"`.worktrees/<task-id>` exists but is
   not a valid worktree on `anvil/<task-id>`. Move it aside, run
   `git worktree prune`, and retry."*

## Resolve `base_branch` (if not provided)

Order of precedence:

1. Explicit `base_branch` argument (always wins).
2. `<repo-root>/.github/skills/create-new-branch/default-base`
   (plain text file, one branch name, no other content). Use this
   for repos where the integration branch is NOT the one
   `origin/HEAD` points at. Example: `ACE-bots` ships a
   `default-base` file containing `botplayer-spike`.
3. `origin/HEAD` (`git symbolic-ref --short refs/remotes/origin/HEAD`
   minus the `origin/` prefix).
4. First of `main`, `master` that exists locally.
5. Abort with: *"Could not resolve default branch. Pass
   `base_branch` explicitly or create `.github/skills/create-new-branch/default-base`."*

```powershell
$cfgFile = Join-Path <repo-root> '.github\skills\create-new-branch\default-base'
$base = $explicitBase
if (-not $base -and (Test-Path $cfgFile)) {
    $base = (Get-Content $cfgFile -Raw).Trim()
}
if (-not $base) {
    $base = (git -C <repo-root> symbolic-ref --short refs/remotes/origin/HEAD 2>$null) -replace '^origin/',''
}
if (-not $base) {
    foreach ($n in 'main','master') {
        if (git -C <repo-root> rev-parse --verify "refs/heads/$n" 2>$null) { $base = $n; break }
    }
}
if (-not $base) { abort "Could not resolve default branch" }
```

## Create the worktree

The branch may or may not exist. Handle all three states:

```powershell
$branch  = "anvil/$task_id"
$wtpath  = ".worktrees/$task_id"
$branchExists = [bool](git -C <repo-root> rev-parse --verify "refs/heads/$branch" 2>$null)
$reuseOk = <result of pre-flight check 6>  # true only if all three
                                            # reuse checks passed

if ($reuseOk) {
    # Reuse mode: existing worktree is valid, on the right branch,
    # and in this repo. cd into it. Do not modify anything.
} elseif ($branchExists) {
    # Branch exists, no worktree yet: attach.
    git -C <repo-root> worktree add $wtpath $branch
} else {
    # Fresh: create both.
    git -C <repo-root> worktree add -b $branch $wtpath $base
}
```

## Hand-off

Final steps before returning control:

1. Resolve the absolute worktree path: `<repo-root>/.worktrees/<task-id>`.
2. Use the Copilot CLI `/cwd` slash command (or the equivalent
   `report_intent`-aware mechanism) to switch the agent's working
   directory to the worktree. The agent's subsequent `git status`,
   builds, and edits should all happen in the worktree.
3. Print the report (see next section).

## Report (always print)

```
✅ Worktree ready
   path:   <abs-path-to-worktree>
   branch: anvil/<task-id>
   base:   <base-branch>@<short-sha>
   mode:   created | branch-attached | reused

Working directory switched to the worktree.

When this branch merges:
   git -C <repo-root> worktree remove .worktrees/<task-id>
   git -C <repo-root> branch -d anvil/<task-id>
(Use `-d` lowercase; it refuses to delete unmerged branches and
protects against accidental loss. Only escalate to `-D` if you
have confirmed the branch is no longer needed.)
(The companion `cleanup-merged-worktree` skill, when it exists,
will do this automatically.)
```

If `.gitignore` was modified, add a line:
```
⚠️ Appended `.worktrees/` to .gitignore in the MAIN checkout.
   Include this change in your current task's commit.
```

## Failure modes and how to recover

| Symptom | Cause | Recovery |
|---|---|---|
| `fatal: '<wt>' already exists` | stale worktree dir without a worktree registration | `git worktree prune` then retry. If the dir holds work, move it aside first. |
| `fatal: <branch> is already checked out at ...` | branch is checked out elsewhere | Use that worktree (`git worktree list`) or pick a different `task_id`. |
| Worktree created but `cd` failed | `/cwd` not available in this CLI version | Report the path and ask the user to `cd` manually. Do not retry silently. |
| Pre-flight `.gitignore` write failed | read-only filesystem, permissions | Abort. The worktree is fine; the user must fix permissions and rerun. |

## Why this exists (don't skip)

- Worktrees keep main checkout untouched. Build/test artifacts
  stay isolated. Switching tasks doesn't disturb in-flight work.
- The `.worktrees/` path is gitignored, so worktree contents never
  pollute commits in the main checkout.
- A SKILL.md is harder to forget than a prose rule in an
  instructions file. If the agent gets randomized mid-task, the
  next agent picks up the worktree by listing `.worktrees/` and
  reads this skill to understand the convention.

## What this skill does NOT do

- Does not push the branch upstream. Push at commit time (Anvil
  step 8) or when the user asks.
- Does not open a GitHub issue. That's the companion
  `log-work-as-issue` skill (TBD).
- Does not delete worktrees. That's the companion
  `cleanup-merged-worktree` skill (TBD).
- Does not handle submodules, LFS, or detached HEAD setups.
- Does not modify Anvil's agent file or any plugin file.
