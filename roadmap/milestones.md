# Milestones

A sequence of milestones from "nothing" to "populated server". Each one is
small enough to ship and demo. Each one has a clear success criterion.

We do not start the next milestone until the previous one's success
criterion is met on a real server build.

---

## M0 — Research and decisions

**Goal:** Answer the open questions in
[`../docs/research/ace-investigation.md`](../docs/research/ace-investigation.md)
well enough to design the fork.

**Deliverables:**
- One closed research issue per question (Q1–Q5).
- A short ADR per architectural decision the answers force.
- This repo's docs updated to reflect what we learned.

**Success criterion:** We can describe, on one page, exactly what we are
going to change in our ACE fork and why.

---

## M1 — Headless player, one bot, no brain

**Goal:** A single bot exists in the world. It does nothing. It just stands
there and other players can see it.

**Deliverables:**
- ACE fork with the minimum hook needed to instantiate a `BotPlayer`.
- A console command `/spawnbot` (GM-only) that creates one at the caller's
  location.
- Bot appears in `/who`, in the world, with a name.

**Success criterion:** A human player logs in, runs `/spawnbot`, and sees
another character standing there with no client connection backing it.

---

## M2 — Motor layer: movement and combat

**Goal:** The bot can walk and fight. No personality yet, just a scripted
loop.

**Deliverables:**
- Movement: bot walks to a target point.
- Pathfinding good enough for open terrain and simple dungeon rooms.
- Combat: bot attacks a designated monster, uses one weapon skill, dies
  gracefully and respawns at a lifestone.
- Health/stamina/mana awareness; recall when low.

**Success criterion:** A spawned bot can be told "go to X, kill Y, come
back" and does it without help.

---

## M3 — BotDirector with bubbles

**Goal:** Bots spawn and despawn around human players according to the
bubble model.

**Deliverables:**
- BotDirector tracks logged-in humans.
- Bubble math: target population, hysteresis TTL, spawn cooldown.
- Plausible spawn rules: out-of-sight, zone-appropriate.
- Per-landblock and global caps.

**Success criterion:** A human walks from a starter town into a low-level
dungeon and back. Bot population follows them — appears around them in
both places, fades out behind them with a delay.

---

## M4 — Archetypes and scripted personalities

**Goal:** Bots feel different from each other without any LLM yet.

**Deliverables:**
- Archetype definitions loaded from config (see
  [`../docs/archetypes.md`](../docs/archetypes.md)).
- Per-archetype Motor/Tactical config: where they go, what they fight,
  whether they group, whether they buff strangers.
- Scripted chat lines per archetype (greetings, idle chatter, trade calls).
- Spawn tables per zone.

**Success criterion:** A human in a starter town sees a believable mix
without anyone talking like a real player yet — the right *kinds* of
characters in the right places, doing the right things.

---

## M5 — Social layer: LLM chat

**Goal:** Bots can hold short, in-character conversations.

**Deliverables:**
- BrainProvider interface and Ollama implementation.
- Per-bot context assembly (persona, short-term memory, recent chat).
- Routing rules (when to call the model vs. use scripted lines).
- Cost / rate-limit controls.
- BrainRouter fallback chain to scripted on failure.

**Success criterion:** A human can `/tell` a bot a simple question
("where is the trade district?") and get a plausible, in-character
answer within a couple of seconds, without the bot saying anything that
breaks immersion.

---

## M6 — Memory and persistence

**Goal:** Bots remember things across despawn/respawn cycles.

**Deliverables:**
- Sidecar DB for brain state (SQLite for v1).
- Persisted bot identity: name, archetype, level, inventory snapshot.
- Persisted relationships ("this human helped me", "this human killed me").
- Bot rehydration: when the same archetype name spawns again, restore
  state.

**Success criterion:** A human meets a Helpful Vet bot named Foo today.
Tomorrow, after a server restart and a fresh bubble spawn, Foo greets
them by name and references something from yesterday.

---

## M7 — Tuning and "feels alive"

**Goal:** It actually feels like a populated server.

**Deliverables:**
- Tuned bubble parameters per zone.
- Tuned archetype mix per zone.
- Tuned chat propensity so bots aren't silent and aren't spammy.
- Dashboards: bot count, model spend, despawn reasons, chat volume.
- A short play-test report.

**Success criterion:** A blind play-tester logs in, plays for 30 minutes
in a starter town and a low-level dungeon, and either doesn't notice the
bots are bots, or notices but says "this still feels alive in a way the
empty server didn't."

---

## After M7

Out of scope for the initial plan; revisit when we get there:

- Bots forming durable groups / fellowships.
- Bots running content (quests, escorts).
- Cross-bubble travel (bots that "live" somewhere and commute).
- A web dashboard for tuning archetype mixes live.
- A "show me what this bot is thinking" debug overlay.
