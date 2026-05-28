# Design spikes

Implementation-level design documents that bridge an ADR (which says
"we will do X") to a tracked implementation issue (which produces code).

A design spike here is denser than an ADR — it includes API shapes,
data formats, algorithms, failure modes, and a test plan — but it does
not yet write the code. The implementation PR cites the spike and
follows it.

Each spike should:

- cite its driving ADR(s) and tracking issue
- explain what the existing codebase provides today
- name the data structures, interfaces, and integration points it will
  add
- enumerate failure modes and how they're handled
- list open questions with default answers
- pass a rubber-duck critique before being merged

## Current spikes

| Spike | Tracking | Driving ADR | Status |
|---|---|---|---|
| [`m2-pathfinding-planner.md`](m2-pathfinding-planner.md) | [#16](https://github.com/darinh/ac-ai-players/issues/16) | [ADR-0005](../adr/0005-pathfinding-reuse-and-build.md) | Draft (rubber-duck pass complete) |
