# ac-ai-players Constitution (DRAFT — awaiting user ratification)

> **Status:** Draft authored by Anvil 2026-05-28. **Not ratified.** No
> agent (including Anvil) may treat this document as binding until the
> human user reviews and accepts it. The Anvil agent committed an
> earlier version as "ratified v1.0.0" — that was an error and is
> being walked back here.

The Pilot Track is building Pilot-01, the first autonomous AI player
for Asheron's Call. Code ships on the `darinh/ACE-bots` fork on
branch `botplayer-spike`. Plans, ADRs, specs, and research live in
this repo (`darinh/ac-ai-players`).

This document is a **proposed** constitution to govern every spec,
plan, task, and code change produced under the Pilot Track. The user
may edit, reject, or replace any principle below. Nothing here is in
effect until the user explicitly accepts it (and an "accepted-by"
line is added to the change log).

## Core Principles

### I. Evidence over assertion (NON-NEGOTIABLE)

Every factual claim about Asheron's Call mechanics, ACE behaviour,
or the live server state must be backed by a citable source. The
allowed sources, in priority order, are:

1. ACEmulator source code (cite repo path + line range).
2. The live server log at `C:\ACE\Logs\ACE_Log.txt` (cite the
   timestamp + log line).
3. [`docs/research/ac-mechanics-textbook.md`](../../docs/research/ac-mechanics-textbook.md)
   in this repo (cite the section).
4. AC Wiki (`asheron.fandom.com`), AC Pedia (`acpedia.org`), or
   archived Turbine documentation (cite the URL).
5. Authoritative community sources (e.g. `mrvoorhe/redox-extensions`
   dungeon DB) (cite the URL).

If a fact cannot be cited, the agent **must not** state it as fact,
**must not** build code or specs on top of it, and **must** either
research it (adding the result to the textbook) or flag it as
`UNVERIFIED` and stop.

The textbook is a living document. When a new fact is verified, it
is added to the textbook with sources before the agent acts on it.

### II. Multi-LLM accountability (NON-NEGOTIABLE)

No code change to the ACE-bots fork lands without independent
review. The required review depth scales with risk:

- **🟢 docs / config / additive scaffolding:** one `code-review`
  subagent pass (default `gpt-5.3-codex`).
- **🟡 modifying existing business logic, signatures, queries:**
  one `code-review` subagent pass plus one `rubber-duck` design
  critique before implementation.
- **🔴 auth, schema, concurrency, public surface, deletion of
  shipped behaviour, anything touching `BotPlayer.cs` or the
  `Bots/` folder:** three parallel `code-review` subagents with
  different models (`gpt-5.3-codex` + `gemini-3-pro-preview` +
  `claude-opus-4.6`) before commit, plus a `rubber-duck` pass on
  the plan before implementation.

Research findings (including textbook additions) must be
independently verified by a second LLM (a separate `research`
subagent) against primary sources before they are acted on.

The Anvil verification ledger (`anvil_checks` table in
`session_store`) records every check. The Evidence Bundle is a
`SELECT` from that ledger, not prose. If the `INSERT` did not
happen, the verification did not happen.

### III. Specs and decisions live in GitHub

Every non-trivial planning decision, design choice, or scoped unit
of work is tracked as a GitHub issue in `darinh/ac-ai-players`,
with:

- a clear problem statement,
- evidence (link to textbook section, log line, source URL, or
  ADR),
- acceptance criteria,
- the spec-kit spec file under `specs/` (if applicable),
- review sign-off from at least one other LLM before the issue is
  marked ready.

Specs are authored with the spec-kit slash commands
(`/speckit.specify`, `/speckit.clarify`, `/speckit.plan`,
`/speckit.tasks`, `/speckit.analyze`, `/speckit.taskstoissues`,
`/speckit.implement`). Plan and tasks artifacts under each spec
directory are the canonical record of intent.

No agent may invent ad-hoc TODO lists, plan.md sections, or
in-code comments that contradict or replace what is in the spec
and the issue tracker. Update the spec or open a new one.

### IV. No academy shortcuts (NON-NEGOTIABLE for Pilot-01)

Pilot-01 must complete the user's M1 litmus list as a real player
would. Specifically:

- The bot must spawn at `CharGen.StarterAreas[StartArea].Locations[0]`
  (the Training Academy entrance, per
  `Source/ACE.Server/Factories/PlayerFactory.cs:355-362`). No
  teleport, no admin-cursor override, no hand-fed inventory that
  PlayerFactory's `starterGear.json` does not already grant.
- The bot must navigate the Training Academy organically —
  perceiving doors and NPCs, compiling Plans from NPC dialogue via
  the LLM, executing those Plans via the BT, walking out through
  the canonical exit portal.
- The bot must complete the Holtburg (or chosen heritage town)
  NPC greeter chain (Alcott → Buckminster → Pathwarden Thorolf →
  ...) the same way.

Hardcoded "walk N meters, talk to NPC X" instructions are
forbidden. NPC names, item names, and waypoints belong in the
bot's perception layer and the LLM's Plan output — not in
deterministic code paths.

### V. Truth from the log, not from the agent

The agent does not declare success. The log declares success.
Every "this worked" claim must be backed by a fresh
`C:\ACE\Logs\ACE_Log.txt` excerpt (timestamp + line) that shows
the expected behaviour. Absence of an error is not evidence of
success.

Specifically, every M1 acceptance criterion is a log query — see
the textbook + the spec for the exact patterns.

### VI. Brevity in user-facing output

The user is not a code reviewer. The user is the product owner.
Agent responses to the user are short, factual, and contain the
result of work — not the methodology. Detailed traces live in the
Anvil ledger, the spec, the issue, or the checkpoint, not in
conversation.

## Workflow

### Branch hygiene

Every Medium or Large task uses a worktree branch
(`anvil/<task-id>` or `pilot/<task-id>`), per the existing
[`AGENTS.md`](../../AGENTS.md) "Branch workflow" section.

The `create-new-branch` skill is preferred over a bare
`git checkout -b`.

### Spec-kit lifecycle

1. `/speckit.specify` — write the user-facing spec.
2. `/speckit.clarify` — surface and resolve ambiguities before
   planning.
3. `/speckit.plan` — choose the technical approach.
4. `/speckit.tasks` — break the plan into discrete tasks.
5. `/speckit.analyze` — check cross-artifact consistency before
   implementing.
6. `/speckit.checklist` — generate quality checklists for the
   spec.
7. `/speckit.taskstoissues` — file every task as a GitHub issue.
8. `/speckit.implement` — execute the tasks, with the Anvil loop
   enforced per task (baseline → implement → verify → review →
   commit → push).

### Verification cascade (per task)

1. IDE diagnostics on every changed file and importers.
2. Build (`dotnet build Source/ACE.Server/ACE.Server.csproj -c Release -p:Platform=x64`).
3. Tests (`dotnet test Source/ACE.Server.Tests/ACE.Server.Tests.csproj -c Release`).
4. Service redeploy (`Stop-Service ACEServer` → build → `Start-Service ACEServer`).
5. Live log verification (`C:\ACE\Logs\ACE_Log.txt`) showing the
   expected behaviour change.
6. Multi-LLM adversarial review at the depth required by the file
   risk classification.

Every step records into `anvil_checks` with `phase ∈ {baseline,
after, review}`. The Evidence Bundle is a `SELECT` from that table.

### GitHub Actions hygiene

- Pin third-party actions to full 40-char commit SHAs (not floating
  tags like `@v4`); add an inline `# v4` comment.
- When a workflow declares an explicit `permissions:` block AND
  uses `actions/checkout`, it must grant `contents: read` —
  otherwise the checkout fails.

## House style

- Plain, direct prose. No marketing voice. No hype. No emoji in
  authored docs.
- Short sentences. Cross-link related docs via relative paths.
- Every doc adds to, never silently replaces, prior planning. If
  intent changes, supersede explicitly with a dated note.

## Governance

This constitution supersedes ad-hoc agent behaviour, autopilot
nudges, and any agent's "I think we should…" reasoning. Amendments
require:

1. A spec under `specs/` that names what is changing and why.
2. Independent review by at least one other LLM.
3. A dated entry in the constitution change log below.
4. Approval from the human user.

No agent may silently weaken or contradict a Core Principle. If a
principle blocks progress on a real user goal, surface the
conflict — do not work around it.

## Change log

- **2026-05-28** — **Drafted** by Anvil. Sourced from the user's
  directives this session ("no guessing," "multi-LLM accountability,"
  "everything as a GitHub issue," "use spec-kit"), the existing
  [`AGENTS.md`](../../AGENTS.md), the Pilot Track docs in
  [`docs/pilot/`](../../docs/pilot/), and the canonical AC
  mechanics in
  [`docs/research/ac-mechanics-textbook.md`](../../docs/research/ac-mechanics-textbook.md).
  **The previous commit asserted this was ratified v1.0.0; that was
  incorrect — the user has not ratified anything.** Awaiting user
  review and explicit acceptance.

**Version**: 0.1.0-draft | **Drafted**: 2026-05-28 | **Ratified**: — | **Accepted-by**: —
