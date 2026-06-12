# Pilot Track

Docs for the **Pilot Track** initiative — building Pilot-01, the first
autonomous AI player. Pilot-01 plays Asheron's Call competently
end-to-end on any server, including custom-content servers, and
becomes the reference brain that all other archetypes inherit.

Driving session plan: `~/.copilot/session-state/.../plan.md` (v5).

## Architectural ADRs (referenced by docs in this folder)

- [`adr/0009-github-models-first-hosted-provider.md`](../adr/0009-github-models-first-hosted-provider.md)
  — Tier-2 hosted LLM provider for the chat-replier job
- `adr/0010-three-layer-brain-architecture.md`
  — Motor / Tactics / Strategy layer boundaries _(pending)_
- `adr/0011-llm-as-compiler-not-controller.md`
  — LLM emits structured artifacts; BT executes deterministically;
  supersedes prior "LLMs only for social layer" rule _(pending)_
- `adr/0012-two-tier-llm-provider.md`
  — Local Ollama (bulk) + hosted (quality) + scripted (fallback)
  _(pending)_
- `adr/0013-needs-engine-and-preemption-matrix.md`
  — Catalog of bot needs in priority bands with eligibility
  predicates and a preemption matrix _(pending)_

## Docs in this folder

| Doc | Status | Purpose |
|---|---|---|
| [`plan-vocabulary.md`](plan-vocabulary.md) | Draft (P-1 deliverable) | Closed plan vocabulary the LLM compiler emits to; reward / cost / prereq schema; sample NPC dialogue → compiled Plan transformations; the LLM prompt seed corpus |

## Milestones (from plan.md v5)

| ID | Title | Status |
|---|---|---|
| P-1 | Plan Vocabulary survey | **Draft (this doc)** |
| ADRs | 0009 commit + 0010, 0011, 0012, 0013 | Pending |
| P-2 | Action API (`IBotActions`) | Pending |
| P-3 | Perception API (`IBotPerception`) | Pending |
| P-4 | BT runtime | Pending |
| P-5 | LLM Quest Compiler | Pending |
| P-6 | Needs Engine + Goal Arbiter + Preemption Matrix | Pending |
| P-7 | Generic quest executor BT | Pending |
| P-8 | Pilot-01 on AC canonical tutorial | **Working (2026-06)** — exits the Academy (via the Exit Token) into the Holtburg open world, turns in quests, buys/completes Contract Broker contracts, fights open-world mobs, and self-levels to ~L7. Slices d39965b (intent-push dedup), 7c93ce1 (beaten-kind veto), 6187654 (portal recall), c56c5f2 (directive-over-grind). |
| P-9 | Grind discovery loop | Pending |
| P-10 | Custom-content validation | Pending |
| P-11 | Generalize for archetypes | Pending |

## Live observation loop (continuous-improvement track)

In addition to the milestone work above, an autonomous build-test-fix
loop runs in parallel: spawn a bot, watch what it tries to do, fix
the smallest blocker, redeploy, observe again. Progress notes live
in session checkpoints; landmark capability additions get reflected
into milestone status here.
