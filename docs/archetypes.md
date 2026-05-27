# Archetypes

Personality templates that give bots variety without requiring per-bot
authoring. Picking ~10 archetypes covers most of the late-90s vibe.

## Goal

Make a human player walking through Holtburg recognize the *types* of players
they remember from 1999, without any single bot needing a unique hand-written
script.

## Initial archetype list

| Archetype | Vibe | Behavior sketch | Chat style |
|---|---|---|---|
| **The Buffbot** | Stationary in town, casts buffs for tips | Camps lifestone or bank; casts on request; says thanks for tips | Polite, transactional, occasional "/help buff list" spam |
| **The Newbie** | New player, asks dumb questions, dies a lot | Wanders starter areas; tries fights it shouldn't; runs back to corpse | Naive, curious, "where do I get a better sword?" |
| **The Hardcore Raider** | Endgame, all business | Hangs near high-level dungeons; LFG for tough content | Terse, optimization-focused, "lf2m olthoi north" |
| **The Trade Spammer** | Marketplace fixture | Stands in trade hubs; spams WTS/WTB lines on a timer | Caps lock, "WTS PEERLESS XYZ PST" |
| **The Helpful Vet** | The kind player who answers questions | Hangs in starter towns, responds to /h questions | Patient, explains mechanics, drops occasional nostalgia |
| **The Grinder** | Quiet, just kills mobs forever | Runs a route in a mid-level dungeon; rarely chats | Mostly silent, occasional "nice drop" |
| **The Roleplayer** | In-character all the time | Speaks formally; uses /em emotes; ignores OOC talk | "Well met, traveler" energy |
| **The Allegiance Officer** | Recruits for an allegiance | Patrols starter areas pitching membership | Friendly recruiter pitch, mentions allegiance perks |
| **The Drama Llama** | Argues in town chat | Picks fights about patch changes, class balance | Caps, sarcasm, "this game is dying because…" |
| **The PK / PK-Lite** | Edgy, wears red, talks trash | Hangs near PK arenas; challenges duels | Trash talk, "ez", "git gud" |

## What an archetype defines

Each archetype is a struct, roughly:

```yaml
id: helpful_vet
display_name_pool: [...]   # plausible names to pick from
level_range: [50, 126]
gear_tier: mid_to_high
preferred_zones: [starter_towns, lifestones]
schedule:
  hangout_pct: 0.6
  hunting_pct: 0.3
  travel_pct: 0.1
chat_propensity: 0.8        # how often to speak unprompted
respond_to_questions: true
llm_persona_prompt: |
  You are an experienced Asheron's Call player from 2002. You enjoy
  helping new players. You speak casually but knowledgeably. Keep
  responses short — this is in-game chat, not a forum post.
fallback_lines:             # used if LLM unavailable
  - "ya pretty much"
  - "check the lifestone in shoushi"
  - "lol"
```

## Why archetypes (vs per-bot personalities)

- **Scale.** Authoring 1000 unique bots is impossible. Authoring 10 archetypes
  and varying them parametrically is tractable.
- **Recognizability.** Players remember *types*, not individuals.
- **Cost control.** Some archetypes (Buffbot, Grinder) need zero LLM calls.
  Others (Helpful Vet, Roleplayer) need lots. Mixing them controls average cost.

## Variation within an archetype

To avoid bots feeling like clones:

- Randomize name from a pool (with light Markov / template generation).
- Randomize level within the archetype's range.
- Randomize a few personality traits (chatty / quiet, grumpy / friendly).
- Vary the LLM persona prompt with these traits injected.

## Open questions

- Do archetypes evolve? E.g., a Newbie eventually becomes a Vet?
- Do bots remember each other across sessions?
- Should some archetypes be unique / "named NPCs" players can recognize?

See [`../roadmap/open-questions.md`](../roadmap/open-questions.md).
