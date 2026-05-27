# Open questions

Things we haven't decided yet. Each entry should either:

- become a research issue and get an answer, or
- become an ADR once we decide, or
- get explicitly deferred ("not for v1").

Grouped by area.

## Engine / fork

- **In-process vs. sidecar for v1.** Architecture doc says "start in-process,
  move to sidecar before M5". Is that right, or should we eat the complexity
  early and go sidecar from M1? Risk of starting in-process: every brain
  bug is now a server-crash bug.
- **BotPlayer vs. souped-up Creature.** Subclass `Player` (gets us free
  player-like behavior, drags in session assumptions) vs. extend `Creature`
  (cleaner separation, but we re-implement a lot). Answered by Q1 in
  [`../docs/research/ace-investigation.md`](../docs/research/ace-investigation.md).
- **Threading model.** One thread per landblock? One thread per bot? Async
  on a shared pool? Depends on Q2.
- **How invasive is "minimal diff"?** We say "minimize the ACE fork". Define
  it. Lines of code? Number of touched files? "Could upstream accept it as
  a PR"? The last one is the strictest and probably the right bar.

## BotDirector

- **Cross-bubble travel.** Do bots ever actually move between regions on
  their own, or is "travel" purely a spawn/despawn illusion?
  - Pro travel: feels more alive, supports persistent identity.
  - Con travel: cost (simulating bots no human can see), pathfinding edge
    cases, increased complexity.
  - Lean: illusion for v1, real travel post-M7.
- **Bubble parameters per zone.** Starting numbers in
  [`../docs/bot-director.md`](../docs/bot-director.md) are guesses. Need
  play-test data to tune. M7 territory.
- **Anti-cluster.** What stops every bubble from spawning the same archetype
  mix and making every dungeon feel identical? Probably randomization +
  per-zone weights, but worth a real design pass.
- **Despawn theater.** How elaborate should despawn be? Walk to portal?
  Logout animation in place? Just vanish if no human is looking? Probably
  all three, depending on context.

## Brain / model

- **Per-bot vs. shared context.** Each bot has its own brain state. Should
  there ever be a shared "world brain" that maintains coherent server-wide
  facts (current events, rumors)? Risk: one fact wrong everywhere.
- **Latency budget for /tell.** What's the maximum time a human will wait
  for a bot's reply to a direct tell before it feels broken? Guess: 3s.
  Need to test.
- **Cache aggressiveness.** How much can we reuse generations across bots
  of the same archetype before they start sounding interchangeable?
- **Model size per archetype.** Should a Buffbot run on a tiny model and a
  Helpful Vet on a bigger one? Probably yes, but adds routing complexity.

## Archetypes

- **How many is enough?** ~10 is the current plan
  ([`../docs/archetypes.md`](../docs/archetypes.md)). Could v1 ship with
  3–4? Which ones are load-bearing for "feels alive"?
  - Lean: Newbie, Helpful Vet, Buffbot, Trade Spammer. These four alone
    probably carry a starter town.
- **Player-typed archetypes.** Should there be an archetype that mimics
  specific real players (with consent), for an inside joke / nostalgia
  effect? Probably no, gets weird fast.
- **Negative archetypes.** PKs, scammers, drama llamas. How toxic is too
  toxic before bots ruin the server they're supposed to populate?

## Memory and identity

- **Bot name collisions with future humans.** A human wants to make a
  character named Foo, but a bot has been Foo for two weeks. Who wins?
  - Lean: humans always win, bot gets renamed at next despawn.
- **GDPR-ish concerns for human-bot interactions.** Bot memory of "this
  human said X to me" — is that fine to persist forever? Probably fine
  for a private server, but worth thinking about.
- **Memory decay.** Do bot memories fade? Probably yes; permanent grudges
  get weird.

## Operations

- **Cost budgeting.** Daily / monthly cap on hosted-model spend, with
  graceful degradation to local model and then to scripted.
- **Observability.** Per-bot journal, bubble dashboard, spend dashboard,
  "show me what this bot is thinking" debug tool.
- **Kill switch.** A single command that despawns all bots immediately,
  for when something goes wrong on a live server.
- **Admin tools.** Spawn / despawn / inspect / possess. Probably GM-only
  slash commands plus a small web UI.

## Ethics and disclosure

- **Do players know which characters are bots?** Options:
  1. Never disclose. Maximum immersion, but actively deceptive.
  2. Disclose at signup ("this server uses AI players"), bots themselves
     stay in character.
  3. `/who` flag — bots are flagged but indistinguishable in normal play.
  - Lean: option 2.
- **Do bots ever lie about being bots when asked directly?** Hard rule
  candidate: bots stay in character but never affirm humanity if directly
  challenged. ("I'd rather not talk about that, friend.")

## Process

- **When do we open the ACE fork repo?** After M0, not before.
- **When do we make the project public?** Probably after M3 (something
  visibly working) or M5 (chat works). Definitely not before M1.
- **Licensing.** Match ACE's license for anything that touches the fork.
  Decide separately for the BotBrain sidecar.
