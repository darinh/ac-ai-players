# Architecture

## Contents

- [High-level](#high-level)
- [The layers, and why each one exists](#the-layers-and-why-each-one-exists)
- [Per-bot state](#per-bot-state)
- [Process boundaries](#process-boundaries)
- [Where the ACE fork actually changes](#where-the-ace-fork-actually-changes)
- [See also](#see-also)

## High-level

```
┌─────────────────────────────────────────────────┐
│                  ACE (forked)                    │
│  ┌──────────────────────────────────────────┐   │
│  │  Bot extension hooks (minimal diff)       │   │
│  │  - Spawn/despawn "headless player"        │   │
│  │  - Subscribe to world events near bots    │   │
│  │  - Action API: move, attack, cast, chat   │   │
│  └──────────────────────────────────────────┘   │
└───────────────────┬──────────────────────────────┘
                    │ in-process or IPC
┌───────────────────▼──────────────────────────────┐
│              BotDirector (orchestrator)           │
│  - Tracks human player locations                  │
│  - Manages bot population per "bubble"            │
│  - Spawns/despawns bots, assigns archetypes       │
└───────────────────┬──────────────────────────────┘
                    │
┌───────────────────▼──────────────────────────────┐
│            BotBrain (per-bot, lightweight)        │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────┐ │
│  │  Motor      │  │  Tactical   │  │  Social  │ │
│  │  (BT/GOAP)  │  │  (Utility)  │  │  (LLM)   │ │
│  └─────────────┘  └─────────────┘  └──────────┘ │
│         ↓                ↓               ↓        │
│  ┌──────────────────────────────────────────┐   │
│  │  Per-bot state: personality, memory,      │   │
│  │  goals, relationships, journal            │   │
│  └──────────────────────────────────────────┘   │
└───────────────────┬──────────────────────────────┘
                    │
┌───────────────────▼──────────────────────────────┐
│          IBrainProvider (pluggable)               │
│  - LocalOllamaProvider                            │
│  - OpenAIProvider                                 │
│  - ScriptedProvider (no LLM, cheapest)            │
└───────────────────────────────────────────────────┘
```

## The layers, and why each one exists

The single most important architectural decision: **don't use an LLM for things
that aren't language**. Most of "playing AC" is solved by classical AI.
VirindiTank has been doing it for 20 years with hand-written rules.

### Motor layer
- **Job:** Pathfinding, combat rotations, looting, buff management,
  recall-when-low-health, target acquisition.
- **Tool:** Behavior Trees and/or GOAP, A* on a navmesh.
- **No ML.** Deterministic, debuggable, fast.

### Tactical layer
- **Job:** "Should I fight this, flee, or recall?" "What dungeon next?"
  "Group up with that bot or solo?"
- **Tool:** Utility AI scoring functions, scripted policies. Maybe a small
  classifier later if we find behaviors we can't easily script.

### Strategic layer
- **Job:** Long-term goals — leveling path, gear targets, faction allegiance,
  daily schedule (hunt vs. town vs. travel).
- **Tool:** Scripted "personality archetypes" + a simple planner driven by
  archetype config. **Refined by
  [`adr/0011-bot-brain-agent-loop.md`](adr/0011-bot-brain-agent-loop.md):**
  the goal selection inside this layer is a Deliberation LLM call
  when the agent loop's Goal Stack is empty, gated by a scripted
  fallback for cheap archetypes.

### Social layer
- **Job:** Chat in /local, /allegiance, /tell. RP in starter towns.
  Respond to questions. Trade negotiations. **Plus:** read NPC
  dialog and extract facts + candidate goals
  ([ADR-0011](adr/0011-bot-brain-agent-loop.md), Dialog mode).
  This extends the original "LLM only at Social" framing into the
  Strategic layer for goal selection, per `AGENTS.md`.
- **Tool:** LLM, with a short system prompt encoding archetype + recent memory.
  This is the *only* layer that justifies a model.
- **Cheap path:** Many archetypes (Grinder, Buffbot) need zero or near-zero
  LLM calls — they use canned lines.

## Per-bot state

Lives entirely in the BotBrain process, not in ACE. ACE only knows about the
in-world entity. This keeps the ACE fork diff small.

```
Bot {
  id
  archetype_id
  display_name
  personality_traits: { chatty: 0.7, grumpy: 0.2, ... }
  goals: [ current_goal, ...next_goals ]
  short_term_memory: [ last N events: "killed olthoi", "got buff from X" ]
  long_term_memory: { relationships, completed_content, grudges }
  journal: append-only log for debugging
}
```

Persisted to a local DB (SQLite is fine for v1). When a bot despawns due to
its bubble emptying out, state is saved; when respawned, state is restored.

## Process boundaries

Three reasonable options, in increasing complexity:

1. **In-process.** BotDirector and BotBrain run inside the ACE server
   process. Simplest. Fine for v1 with <50 bots. Risk: a crashing brain
   crashes the server.
2. **Sidecar process, same host.** ACE has a small extension that exposes a
   gRPC/named-pipe API. BotBrain runs as a separate process. Isolation,
   independent restart, can be written in any language (Python is tempting
   for the LLM bits).
3. **Distributed.** Brain service on another box / GPU host. Overkill for v1
   but the sidecar design naturally evolves into this.

**Plan:** Start in-process for M1–M3, move to sidecar before M5 (LLM social
layer) when isolation matters most.

## Where the ACE fork actually changes

Minimize the diff. Ideally we add only:

- A `BotPlayer` class (or equivalent) that subclasses / mirrors `Player`
  without a network session.
- Hooks on the world tick that the BotDirector can subscribe to.
- An action surface (move/attack/cast/chat/use) callable from C# code.

Everything else lives outside the fork.

## See also

- [`adr/0011-bot-brain-agent-loop.md`](adr/0011-bot-brain-agent-loop.md)
  — refines this layered model into an explicit six-stage agent
  loop (Perception → Blackboard → Goal Stack → Planner → Executor
  → Critic). Maps to layers as: Motor ≈ Executor, Tactical ≈
  Critic + interrupts, Strategic ≈ Goal Stack + Deliberation LLM,
  Social ≈ Dialog LLM. Read alongside this doc for current shape.
- [`design/bot-brain-agent-loop.md`](design/bot-brain-agent-loop.md)
  — the detailed design behind ADR-0011.
- [`ace-fork-plan.md`](ace-fork-plan.md) — the concrete one-pager of what
  we change in ACE (M0 final deliverable)
- [`architecture-diagrams.md`](architecture-diagrams.md) — Mermaid
  diagrams supporting the prose above
- [`bot-director.md`](bot-director.md) — bubble spawn model
- [`brain-providers.md`](brain-providers.md) — pluggable LLM backends
- [`archetypes.md`](archetypes.md) — personality templates
- [`research/ace-investigation.md`](research/ace-investigation.md) — what we
  need to learn about ACE before forking
- [`adr/0003-botcreature-not-botplayer.md`](adr/0003-botcreature-not-botplayer.md)
  — **superseded by [0007](adr/0007-bots-as-player-not-creature.md).**
  Originally proposed `Creature` subclass.
- [`adr/0004-bot-tick-via-monster-tick.md`](adr/0004-bot-tick-via-monster-tick.md)
  — **superseded by [0008](adr/0008-bot-tick-via-player-tick.md).**
  Originally proposed `Monster_Tick`.
