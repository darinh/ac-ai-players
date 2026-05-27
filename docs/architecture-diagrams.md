# Architecture diagrams

Lightweight Mermaid diagrams to support [`architecture.md`](architecture.md).
Kept in a separate file so the prose doc stays readable and the diagrams
are easy to find when something changes.

## Component overview

```mermaid
flowchart LR
    subgraph ACE ["ACE server (forked)"]
      World[World simulation]
      Director[BotDirector]
      BotPlayer[BotPlayer instances]
    end

    subgraph Sidecar ["BotBrain sidecar (post-M5)"]
      Router[BrainRouter]
      Memory[(Memory store)]
      Personas[Archetype configs]
    end

    subgraph Providers ["Brain providers"]
      Hosted[Hosted model API]
      Local[Local model - Ollama]
      Scripted[Scripted lines]
    end

    Humans((Human players)) -- play --> World
    World -- ticks --> BotPlayer
    Director -- spawns/despawns --> BotPlayer
    Director -- watches --> Humans
    BotPlayer -- social events --> Router
    Router -- reads/writes --> Memory
    Router -- loads --> Personas
    Router --> Hosted
    Router --> Local
    Router --> Scripted
```

## Per-bot tick layering

```mermaid
flowchart TD
    Tick[Server tick] --> Motor
    Motor[Motor layer<br/>movement, combat, world I/O] --> Tactical
    Tactical[Tactical layer<br/>where to go, what to fight] --> Social?{Social event?}
    Social? -- no --> Done[Done for this tick]
    Social? -- yes --> Social[Social layer<br/>chat, identity, memory]
    Social --> Router2[BrainRouter chooses provider]
    Router2 --> Reply[Reply or emote in-world]
```

## Bubble model

```mermaid
flowchart LR
    Human((Human player))
    Human -- defines --> Bubble
    subgraph Bubble [Bubble around human]
      Inner[Inner radius<br/>target population dense]
      Outer[Outer radius<br/>despawn boundary]
    end
    Bubble -- governed by --> Params[Per-zone params:<br/>target count,<br/>hysteresis TTL,<br/>spawn cooldown]
    Bubble -- requests --> Director2[BotDirector]
    Director2 -- spawns --> NewBots[New bots near inner edge,<br/>out of sight]
    Director2 -- despawns --> OldBots[Bots past outer + TTL,<br/>off-camera]
```

## Brain routing

```mermaid
flowchart LR
    Event[Social event<br/>e.g. /tell from human] --> Decide{Need a model?}
    Decide -- no --> ScriptedOut[Pick scripted line]
    Decide -- yes --> Pref[Archetype's brain_preference]
    Pref --> Try1[Try preferred provider]
    Try1 -- ok --> Reply2[Reply]
    Try1 -- fail/timeout --> Try2[Try next provider in fallback chain]
    Try2 -- ok --> Reply2
    Try2 -- fail --> ScriptedOut
```

These diagrams are illustrative, not normative. When the docs and the
diagrams disagree, the docs win and the diagram is wrong.
