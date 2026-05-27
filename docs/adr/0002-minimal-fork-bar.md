# 0002. Define "minimal fork" as the upstreamable-PR bar

- **Status:** Proposed
- **Date:** 2026-05-26
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

[`docs/architecture.md`](../architecture.md) commits to "minimize the
diff" of our ACE fork: ideally we only add a `BotPlayer` class, a tick
hook, and an action surface. ADR-0001
([`0001-start-in-process-then-sidecar.md`](0001-start-in-process-then-sidecar.md))
commits to running BotBrain in-process through M4 and splitting it out
before M5 — which only stays cheap if the in-process surface inside
ACE is small.

What "minimize" actually means is currently undefined.
[`../../roadmap/open-questions.md`](../../roadmap/open-questions.md)
flags this: *"How invasive is 'minimal diff'? We say 'minimize the ACE
fork'. Define it. Lines of code? Number of touched files? 'Could
upstream accept it as a PR'? The last one is the strictest and probably
the right bar."*

Why decide now: every PR to our fork from M1 onward will need a rubric
to know whether it's pulling its weight. Without one, the fork's diff
grows by accretion and the M5 sidecar split gets harder, not easier.
This ADR also gates the "Minimal-diff justification" section of
[`../ace-fork-plan.md`](../ace-fork-plan.md), which is the final M0
deliverable.

## Decision

The bar for any change to our ACE fork is **"could this be opened as a
PR against upstream ACE without being laughed out of the room?"** —
i.e., a maintainer's first reaction would not be "this only exists to
support your bot project, take it elsewhere."

Operationally:

- Every PR to the fork records, in its description, a one-paragraph
  upstream-justification explaining what *general* capability the
  change exposes (not "needed for bots", but "exposes a tick hook
  other server-side automation could reuse").
- If we cannot write that paragraph honestly, the change belongs in
  the BotBrain sidecar, not in the fork.
- We do not enforce a quantitative line-count or file-count budget.
  The qualitative bar above is stricter in practice and explains
  *why* we're saying no, which a line-count rule does not.

## Options considered

### Option A — Line-count budget ("≤ N lines added to ACE source")

- Pros:
  - Mechanically checkable in CI.
  - Easy to talk about ("we're at 600/1000 lines").
- Cons:
  - Optimizes the wrong thing. A 50-line invasive change that breaks
    an upstream invariant is worse than a 500-line additive change
    that just adds a hook.
  - Encourages dense, unreadable diffs to stay under budget.
  - Doesn't tell a contributor *why* a change is or isn't acceptable.

### Option B — File-count budget ("touch ≤ N files in ACE.Server")

- Pros:
  - Forces us to localize changes.
  - Easy to measure.
- Cons:
  - Same fundamental problem as Option A: counts the wrong thing.
  - A single file rewritten is worse than five files each gaining one
    extension point.

### Option C — Upstreamable-PR bar ("could be PR'd upstream without being laughed out")

- Pros:
  - Forces every change to justify itself in terms of a general
    capability, not "needed for our bots".
  - Naturally pushes bot-specific behavior into the sidecar, which is
    exactly where we want it post-M5 (ADR-0001).
  - Aligns the fork with potential future upstream contribution. If we
    ever want to upstream the hooks, they're already shaped for it.
- Cons:
  - Subjective. Two reviewers may disagree on whether a change clears
    the bar.
  - Slows velocity on changes we'd otherwise wave through.
  - The "without being laughed out" framing is intentionally
    hyperbolic — it's a culture marker, not a rubric. Reviewers have
    to internalize it.

### Option D — Compile-flag isolation ("all bot code behind `#if AC_AI_PLAYERS`")

- Pros:
  - Mechanically separates our code from upstream's.
  - Trivial to compute the diff that would survive a flag-off build.
- Cons:
  - C# `#if` is unergonomic for anything beyond trivial guards.
  - Hooks that callers reach through (a virtual method, an event)
    can't really be `#if`-guarded — the call site needs to live in
    upstream code or it doesn't fire.
  - Doesn't actually answer the "is this change acceptable in
    principle?" question — only "is it isolated in code?".

## Consequences

- **Easier:**
  - Every fork PR has a clear rubric: write the
    upstream-justification paragraph or move the change to the
    sidecar.
  - When we eventually decide whether to upstream any of our hooks
    (or fork less), the diff is already shaped for that decision.
  - Bot-specific behavior naturally drains out of the fork and into
    the sidecar, supporting ADR-0001's M5 split.
- **Harder:**
  - Some changes that would be a one-line fix in the fork become a
    larger refactor to expose an upstreamable hook. We accept that.
  - Reviews are more subjective; we will occasionally disagree on
    whether a change clears the bar.
  - The bar tightens over time as we get better at it; early M1/M2
    fork PRs may pass things later M3+ PRs would not.
- **Follow-ups:**
  - Update the ACE fork repo's `PULL_REQUEST_TEMPLATE.md` (when that
    repo exists) to require the upstream-justification paragraph.
  - Revisit this ADR after M3: if "could it be upstreamed?" is
    producing useful pushback, keep it; if it's pure ceremony,
    consider Option A as a coarse fallback.

## References

- Related doc(s):
  - [`../architecture.md`](../architecture.md) ("Where the ACE fork actually changes")
  - [`../ace-fork-plan.md`](../ace-fork-plan.md) (the document this bar is meant to enforce)
  - [`0001-start-in-process-then-sidecar.md`](0001-start-in-process-then-sidecar.md) (the in-process/sidecar split this bar reinforces)
- Related open question(s):
  - "How invasive is 'minimal diff'?" in
    [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md)
- Related issue(s) / PR(s):
  - This ADR PR
