# Prior art

Short notes on projects and research we should look at before designing our
own bot system. The goal is to learn what worked, what didn't, and what's
worth borrowing.

## MMO server-side bot systems

There is a long history of community MMO server projects that include
"player bot" features — autonomous in-world entities that behave like
players for the benefit of low-population servers. We want to study the
public design discussions and architecture docs from those projects:

- How they hook bots into the server's world tick.
- How they share or duplicate the `Player` class.
- How they persist bot state.
- How they handle pathfinding, combat, and grouping.
- What they ended up regretting in hindsight.

**Action item:** open a research issue per project we want to study, link
the relevant source files, and write a one-page summary of the lessons.

## Game AI techniques to borrow

The non-LLM parts of our bots are well-trodden ground in game AI:

- **Behavior Trees.** Standard for NPC/AI in modern engines. Good fit for
  Motor layer (combat rotations, looting sequences, recall-on-low-hp).
- **GOAP (Goal-Oriented Action Planning).** Useful when goals shift often
  and we want the planner to choose action sequences. Probably overkill
  for v1.
- **Utility AI.** Score-based decision making. Good fit for Tactical
  layer ("fight or flee?", "which dungeon next?").
- **Navmesh + A\*.** Standard pathfinding. Whether we can reuse ACE's
  existing nav data is one of the open research questions (see
  [`ace-investigation.md`](ace-investigation.md) Q4).

Recommended reading: the *Game AI Pro* book series and the AI sections of
*Game Programming Gems*. Free chapters from GDC talks are also a good
starting point.

## LLM-driven NPCs / agents

Recent work on giving game characters generative behavior:

- **Generative Agents (Park et al., 2023).** "Smallville" paper showing a
  small town of LLM-driven characters with memory, reflection, and daily
  schedules. Many of the ideas (short-term memory window, importance-scored
  long-term memory, daily planning) map directly onto our archetype design.
- **Voyager (Wang et al., 2023).** LLM agent in Minecraft that builds a
  skill library over time. Probably more than we need, but the skill-
  caching idea is interesting for the Tactical layer.
- **Inworld AI, Convai, NVIDIA ACE (the NVIDIA one, not the emulator).**
  Commercial NPC-dialogue products. Worth reading their public design
  notes for prompt structure and cost-control patterns.

## What we want to *not* copy

A pattern to avoid: bots that try to be "general agents" driven end-to-end
by an LLM. They are slow, expensive, brittle, and break character
constantly. Our design uses the LLM only for chat, and uses classical AI
for everything else, specifically to avoid this failure mode.

## How this list grows

Each entry that turns into "we should actually read the source / paper"
becomes a research issue. Findings get summarized back into this file with
a one-line takeaway and a link to the issue.
