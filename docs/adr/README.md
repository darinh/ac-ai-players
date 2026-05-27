# Architecture Decision Records

We use lightweight ADRs to record architectural decisions and why we made
them. Format inspired by Michael Nygard's original ADR proposal.

## When to write one

Write an ADR when:

- A decision will be expensive to reverse (changes the shape of the code,
  the data model, or the deployment topology).
- Reasonable people would disagree about which option to pick.
- The decision resolves an item in
  [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md).

Don't write one for:

- Cosmetic choices.
- Decisions that are obviously right and uncontroversial.
- Things that are easy to change later and don't constrain anything else.

## How to write one

1. Copy [`template.md`](template.md) to `NNNN-short-title.md` where `NNNN`
   is the next number in sequence (zero-padded to 4).
2. Fill it in. Keep it short — one screen if you can.
3. Open a PR. The PR description should link the doc(s) and open
   question(s) the decision affects.
4. Once merged:
   - Update affected doc(s) to reference the ADR.
   - Remove (or strike through) the resolved item in
     `roadmap/open-questions.md`.

## Statuses

- **Proposed** — under discussion in a PR.
- **Accepted** — merged and in effect.
- **Superseded by NNNN** — replaced by a later ADR. Don't delete superseded
  ADRs; they're history.
- **Deprecated** — no longer in effect, but not replaced.

## Index

_(Add a line per ADR as they land.)_

- _none yet_
