# Glossary

Terms specific to this project, with the meanings we use them with.
When a doc uses one of these terms, it means *exactly* what's here.

## Project terms

**Bot.** An in-world entity that behaves like a player but isn't backed
by a client connection. The thing this whole project exists to build.

**Headless player.** Implementation detail: a `Player`-like server-side
object that doesn't have a `Session`. The mechanism by which a bot
exists in the world. See
[`research/ace-investigation.md`](research/ace-investigation.md) Q1.

**BotPlayer.** Our subclass / variant of ACE's player class that
represents a bot in-world. Exact relationship to `Player` is TBD
(again, Q1).

**BotBrain.** The per-bot decision-making process. Holds the bot's
state (Motor / Tactical / Social), runs the tick, talks to providers.

**Sidecar.** A separate process that hosts the BotBrains and talks to
the ACE server over a local RPC channel. Optional for early
milestones, planned for M5+. See
[`architecture.md`](architecture.md).

**BotDirector.** The server-side component that decides when and where
bots spawn and despawn. Owns the bubble model.
[`bot-director.md`](bot-director.md).

**BrainRouter.** The component inside the BotBrain that picks which
BrainProvider to use for a given request, and falls back when one
fails. [`brain-providers.md`](brain-providers.md).

**BrainProvider.** A backend that can generate text for a bot. Three
flavors: hosted (paid API), local (Ollama or similar), and scripted
(canned lines, no model).
[`brain-providers.md`](brain-providers.md).

**Archetype.** A recipe for a kind of bot: how it behaves, where it
goes, how it talks, what model it uses. A spawned bot is an *instance*
of an archetype. [`archetypes.md`](archetypes.md).

## Layer terms

**Motor layer.** Movement, combat mechanics, low-level world
interaction. The "body" of a bot. Runs every tick.

**Tactical layer.** Short-horizon decisions: where to go, what to
fight, when to recall, whether to group. The "reflexes" of a bot.
Runs every few ticks.

**Social layer.** Chat, identity, memory of other characters. The
"voice" of a bot. Runs only on social events (chat, /tell, emote
from another character).

## Spawning / population terms

**Bubble.** The active region around each logged-in human player
where bots may exist. Defined by an inner radius (target population
dense), outer radius (despawn boundary), and per-zone parameters.

**Bubble model.** The full set of rules around bubbles: target
population, hysteresis TTL, spawn cooldown, anti-cluster, etc.
See [`bot-director.md`](bot-director.md).

**Hysteresis TTL.** The grace period before despawning a bot that
left a bubble. Prevents bots from popping in and out as a human
moves around the boundary.

**Spawn cooldown.** Minimum time between spawn events in a given
bubble, regardless of population gap. Prevents thrashing.

## World terms (from AC, for reference)

**Landblock.** Asheron's Call's unit of world subdivision. Roughly a
192x192 meter region. The server typically processes one landblock
on one thread.

**Allegiance.** AC's pyramid-shaped social structure where players
swear loyalty to a patron in exchange for experience-pass-up.
Relevant because several archetypes are organized around it.

**Lifestone.** AC's respawn point. A bot dying without a tied
lifestone is a Motor-layer bug, not a design feature.

**Dereth.** The setting / world of Asheron's Call. Used as shorthand
for "the game world" in our docs.
