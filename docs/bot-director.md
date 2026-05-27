# BotDirector — spawn/despawn bubble model

## Problem

A "fully populated Dereth" simulation is wasteful: most landblocks are empty
of humans most of the time. We want bots where humans are, and nowhere else.

## Mental model: bubbles around humans

Every active human player carries an invisible **bubble** with them. Inside
the bubble, the BotDirector maintains a target population of AI players. When
bubbles overlap, they merge — you don't get double-population at the seam.

```
       (   bubble of player A   )
                   ∩
       (   bubble of player B   )
                   →
       (    merged bubble       )    target = max(targetA, targetB), not sum
```

When a player logs out / moves away / dies and bubble shrinks below a bot's
position, that bot becomes a *despawn candidate*. We don't despawn immediately
— see "hysteresis" below.

## Bubble parameters

Per archetype mix and zone, configurable:

| Param | Typical | Notes |
|---|---|---|
| `radius_close` | ~80m | "Same area" — town square, dungeon room |
| `radius_far`   | one landblock | "Same region" — broader population |
| `target_close` | 3–8 bots | Bots visible to the player |
| `target_far`   | 5–15 bots | Bots in the wider region |
| `hysteresis_ttl` | 60–180s | How long a bot lingers after bubble shrinks |
| `spawn_cooldown` | 10–30s | Don't spawn faster than this |

Numbers above are starting guesses. Tune empirically against "feels alive
without feeling fake."

## Zone-aware spawn tables

Each landblock / zone type has a spawn table — what archetypes are
appropriate where:

| Zone | Likely archetypes |
|---|---|
| Starter town (Holtburg, Shoushi, Yaraq) | Newbie, Helpful Vet, Buffbot, Trade Spammer, Allegiance Officer |
| Marketplace | Trade Spammer, Buffbot, Roleplayer, Drama Llama |
| Low-level dungeon | Newbie, Helpful Vet, Grinder |
| Mid-level dungeon | Grinder, Hardcore Raider (LFG), Helpful Vet |
| High-level dungeon | Hardcore Raider, Grinder |
| PK arena / hotspot | PK, Drama Llama |
| Wilderness | (very low density) lone Grinder, occasional Newbie lost |

## Spawn algorithm (sketch)

```
every 5s for each human player H:
    bubble = compute_bubble(H)
    current_bots = bots_within(bubble)

    if len(current_bots) < target_for(bubble):
        archetype = sample(spawn_table(bubble.zone))
        spawn_location = pick_plausible_spawn(bubble, archetype)
        bot = BotDirector.spawn(archetype, spawn_location)

    for bot in current_bots:
        if bot outside any active bubble:
            mark_despawn_candidate(bot, ttl=hysteresis_ttl)
        else:
            clear_despawn_candidate(bot)
```

## Plausible spawning

Bots must not pop into existence in front of the player. Spawn rules:

- **Out of sight.** Around a corner, in another room, behind terrain.
- **Plausible context.** Don't spawn a Hardcore Raider alone in a starter
  town. Don't spawn a Newbie deep in an endgame dungeon.
- **Travel illusion.** Sometimes "spawn" by having a bot walk in from a
  portal or zone edge instead of materializing.
- **Despawn similarly.** Bots ideally walk to a portal / log off lifestone
  before disappearing. If they're far enough from any human, they can just
  vanish silently.

## Hysteresis (the "feels alive" trick)

If we despawn the instant a human walks away, the world feels reactive in a
bad way — "all those people disappeared the moment I turned the corner."

Solution: when a bot exits all bubbles, it doesn't despawn immediately. It
gets a TTL (60–180s). During that TTL:

- It keeps doing its archetype behavior (still grinding, still chatting).
- If any human re-enters its bubble, it's "saved" and continues.
- If TTL expires with no humans nearby, it despawns gracefully (logout
  animation, walk out, etc).

This means bots have a small, self-consistent existence even between human
encounters.

## Persistence across despawn

When a bot despawns, its state (level, inventory, memory, relationships) is
written to disk. When the same archetype + name spawns again in a similar
zone, the director *may* rehydrate that bot rather than create a fresh one.

This produces the "oh, that guy again" effect — the Helpful Vet a player met
in Holtburg yesterday is the same Helpful Vet today.

## Population pressure & global caps

- Hard cap on total bots across the server (`max_global_bots`).
- Hard cap per landblock (avoid 50 bots in one dungeon room).
- If global cap is hit, prefer to skip spawning in less-populated bubbles
  (single human) and prioritize active group bubbles.

## Open questions

- Should bots ever travel *between* bubbles on their own (i.e., simulate
  cross-world movement) or only spawn/despawn?
- What's the right "feels alive" target population for, say, Holtburg with
  3 humans online vs. 20?
- Anti-griefing: should bots avoid known PK hotspots if their archetype
  doesn't expect PK?

See [`../roadmap/open-questions.md`](../roadmap/open-questions.md).
