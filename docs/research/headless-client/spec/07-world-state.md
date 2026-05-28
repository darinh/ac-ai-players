# 07 — World state

🚧 **Status: stub.** This document will describe how the
client builds and maintains a model of the game world from
the stream of game messages.

## Scope

A real AC1 client maintains:

- **Local player state**: position, heading, vital stats
  (health/stamina/mana), inventory, equipped items, skills,
  spells, attributes.
- **Nearby objects**: items, creatures, players, doors,
  portals, chests, signs.
- **Per-object state**: position, velocity, animation,
  appearance, name, GUID, attackable/dead/animating flags.
- **World data**: landblocks, terrain, cell visibility.

A bot-driven client maintains exactly the same data, plus a
serialized snapshot that the AI brain queries.

## Update sources

| Source | Frequency | Update kind |
|---|---|---|
| `Movement` broadcasts | ~10 Hz per moving object | Position delta |
| `UpdateObject` messages | event-driven | Full or partial object state |
| `CreateObject` messages | on visibility enter | New object appears |
| `DestroyObject` / object leaves visibility | event-driven | Object vanishes |
| `UpdateHealth` / `UpdateStamina` / `UpdateMana` | event-driven | Vital changes |
| `UpdateAttribute` / `UpdateSkill` | event-driven | Character stat changes |
| `Talk` / `LocalChat` / `EmoteText` | event-driven | Chat into bot's text channel |

## What we won't store

We will deliberately NOT replicate:

- 3D mesh geometry — bots navigate via the server's pathing
  hints, not local geometry queries.
- Animations and visual effects — they're irrelevant for AI
  decision-making.
- Sound.

This is the headless contract: the client maintains the
minimal data needed to (1) make decisions, (2) issue
mechanically-valid commands, (3) train a model from
replayable traces.

## To be filled in (Phase 3+)

- Object schema (the union of all `UpdateObject` payload
  variants we care about)
- Landblock / cell coordinate system and how movement
  broadcasts encode position
- Inventory data structure (containers, slots, equipment
  slots)
- Spell-component requirements (drives training-data labels
  for "do I have what I need to cast X?")
- Snapshot format (the wire shape sent to the AI brain via
  the API)
