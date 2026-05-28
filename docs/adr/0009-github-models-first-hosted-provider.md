# 0009. GitHub Models is the first hosted `IBrainProvider` implementation

- **Status:** Proposed
- **Date:** 2026-05-27
- **Deciders:** @darinh
- **Supersedes:** _(none)_
- **Superseded by:** _(none)_

## Context

[`brain-providers.md`](../brain-providers.md) specifies the
`IBrainProvider` interface, the fallback chain
(`hosted → local → scripted → null`), the routing policy, and the
prompt structure. It names three concrete provider variants
(LocalOllamaProvider, OpenAIProvider/OpenAI-compatible,
ScriptedProvider, NullProvider) but does not pick which hosted
provider ships first.

The E7 brain in
[`darinh/ACE-bots@botplayer-spike`](https://github.com/darinh/ACE-bots/tree/botplayer-spike)
currently replies to `/tell` by sampling a random template from
[`BotArchetypes.GetTellReplies`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Bots/BotArchetype.cs)
— a `ScriptedProvider` in everything but name. The user-observable
result is the Newbie bot replying with one of four canned lines per
inbound tell, which fails the "feels alive" bar within ~3 tells.

To cross that bar without first standing up a GPU-equipped Ollama
host, the bot system needs **one** hosted `IBrainProvider`
implementation that:

1. Is reachable from a Windows-hosted ACE server with zero new
   infrastructure beyond an HTTPS client and a bearer token.
2. Has an OpenAI-compatible chat-completions surface so the same
   client code can later be repointed at Ollama, vLLM, Groq, etc.
   without rewriting the provider.
3. Uses an authentication credential the user already has on the
   dev box.
4. Offers a free / low-cost tier sufficient for the dev loop (a few
   dozen tells per minute, single bot).
5. Has a published, stable HTTP contract — not a CLI shim or
   IDE-only protocol.

Why decide now: blocking E9 (LLM-driven social chat) on standing up
local Ollama infra delays the first "real conversation with a bot"
demo. ScriptedProvider on its own won't sell the milestone. We need
to pick the first hosted provider and start shipping.

## Decision

**GitHub Models** (`https://models.github.ai/inference/chat/completions`)
is the first hosted `IBrainProvider` implementation. It ships as
`GitHubModelsProvider` under `ACE.Server.Bots.Llm` and is
selectable via env-var config. `ScriptedProvider` (wrapping
`BotArchetypes.GetTellReplies`) is the always-on fallback and is
the only provider that runs when no token is configured. The
chain in `BrainRouter` is:

```
GitHubModelsProvider (if ACE_LLM_PROVIDER_TOKEN set and request succeeds)
  → ScriptedProvider (always)
```

`LocalOllamaProvider` is **not** built in E9. It can be added later
as a third entry above ScriptedProvider — the
[brain-providers.md](../brain-providers.md) fallback chain already
specifies the ordering — without touching `BotPlayer` or
`BrainRouter`'s contract.

### Configuration

All provider knobs are sourced from environment variables, not from
ACE's `Config.js`. This keeps the GitHub PAT out of any
repo-tracked file (per the project rule that secrets never land
in source) and avoids touching `ACE.Common.Config` for the first
cut. The strongly-typed config can adopt these later once the API
surface is stable.

| Variable                     | Purpose                                        | Default                                                  |
|------------------------------|------------------------------------------------|----------------------------------------------------------|
| `ACE_LLM_PROVIDER_TOKEN`     | Bearer token (GitHub PAT or `gh auth token`)   | _(unset → provider disabled, ScriptedProvider only)_     |
| `ACE_LLM_PROVIDER_ENDPOINT`  | Chat-completions endpoint                      | `https://models.github.ai/inference/chat/completions`    |
| `ACE_LLM_PROVIDER_MODEL`     | Model id passed to the provider                | `openai/gpt-4o-mini`                                     |
| `ACE_LLM_PROVIDER_TIMEOUT_MS`| Per-request HTTP timeout                       | `8000`                                                   |

For the dev box, `ACE_LLM_PROVIDER_TOKEN` is set on the `ACEServer`
NSSM service to the user's `gh auth token` value. For production
this should be replaced with a fine-grained PAT carrying
`models:read` permission only.

### Token rotation and degradation

If a request returns 401/403, the provider logs the failure once
per minute (rate-limited to avoid log spam) and the router falls
through to ScriptedProvider. The hosted provider does not
self-disable on auth failure — the next request retries — so
rotating the env var on the service and restarting picks the new
token up without a code change.

## Options considered

### Option A — GitHub Models (chosen)

- Pros:
  - OpenAI-compatible chat-completions surface; same client code
    works for the future Ollama / OpenAI / Groq providers when they
    arrive.
  - The user's `gh auth token` already grants access on this box
    (verified 2026-05-27 against `gpt-4o-mini-2024-07-18`).
  - Free tier rate-limits are well above the dev loop budget
    (~150 req/min for a single bot doing tells); no billing setup
    required to ship E9.
  - GitHub-hosted credential matches the rest of the project's
    GitHub-native posture (issues, PRs, ADRs all on github.com).
  - The model catalog includes both small/fast (`gpt-4o-mini`,
    `Phi-3.5-mini`) and big/quality (`gpt-4o`, `Llama-3.3-70B`)
    models from a single endpoint, satisfying the "model size per
    archetype" open question in
    [`roadmap/open-questions.md`](../../roadmap/open-questions.md#brain--model)
    without provider sprawl.
- Cons:
  - Outbound HTTPS from the game server adds an external dependency
    on GitHub's availability. Mitigated by the always-on
    ScriptedProvider fallback — bot chat degrades to canned lines,
    not silence.
  - Free-tier rate limits are a hard ceiling. For a populated
    server this would be exhausted quickly; the budgeted hosted →
    local → scripted degradation from
    [`brain-providers.md`](../brain-providers.md#selection-policy)
    will need to land before public exposure.
  - GitHub Models is in public preview at decision time; the URL
    and auth shape could shift. Mitigated by the
    `ACE_LLM_PROVIDER_ENDPOINT` env var (repoint without rebuild)
    and by the OpenAI-compatible request body (other providers
    accept the same shape).

### Option B — OpenAI API direct (api.openai.com)

- Pros:
  - The canonical OpenAI-compatible endpoint; widest provider
    fluency.
  - Production-tier SLAs, no preview-product risk.
- Cons:
  - Requires the user to set up OpenAI billing, generate an API
    key, manage spend manually — a setup step that blocks the
    dev loop today.
  - Auth is a separate credential the user does not already have
    on the dev box.
  - Same shape as Option A (request/response identical) — adding
    it later as a parallel provider is trivial.

### Option C — Anthropic API direct (api.anthropic.com)

- Pros:
  - Strong models for in-character roleplay; Claude is widely
    cited as good at sustained persona.
- Cons:
  - **Not** OpenAI-compatible at the wire level (different request
    shape, different auth header). Would force the provider abstraction
    to surface a wider lowest-common-denominator interface or carry
    two parallel HTTP clients. Either choice complicates the
    "swap providers by config" goal stated in
    [`brain-providers.md`](../brain-providers.md#providers).
  - Same billing/key-setup blocker as Option B.

### Option D — Wait for `LocalOllamaProvider`

- Pros:
  - Per [`brain-providers.md`](../brain-providers.md#localollamaprovider)
    the long-term self-hosted answer; zero per-token cost.
- Cons:
  - Requires installing Ollama, choosing and pulling a model
    (`llama3.1:8b-instruct` is the doc's first suggestion, ~5 GB),
    and confirming GPU acceleration works on the dev box. Each
    of those is a hands-on setup step that blocks the demo loop.
  - The first "bot has a real conversation" demo gates on this
    work landing first, which inverts the cost/value ordering:
    we'd be paying setup cost before knowing whether the demo
    even moves the needle.
  - Once GitHub Models is shipped, adding Ollama is a parallel
    `IBrainProvider` impl and a config switch — not a re-architecture.

### Option E — Ship ScriptedProvider only for E9, defer all hosted to E10

- Pros:
  - Smallest change: refactor the existing
    [`BotArchetypes.GetTellReplies`](https://github.com/darinh/ACE-bots/blob/botplayer-spike/Source/ACE.Server/Bots/BotArchetype.cs)
    sampling into an `IBrainProvider` shape.
  - Zero new infrastructure, zero new dependencies, zero new
    risk surface.
- Cons:
  - Does not move the "feels alive" needle. The whole user-visible
    point of E9 is bots that respond *meaningfully*, not bots that
    respond *consistently*. Without a real model the milestone is
    invisible to the player.
  - The IBrainProvider abstraction itself adds no value if there's
    only one impl behind it. The right time to ship the abstraction
    is when the second impl needs it.

## Consequences

- **Easier:** Bots can hold a short conversation today using a
  credential the user already has. No GPU, no Ollama install, no
  billing setup. The fallback to ScriptedProvider means the worst
  case is "bots still talk, just blandly" — the same behavior they
  have today.
- **Easier:** Future providers (Ollama, OpenAI-direct, Anthropic-via-adapter,
  Azure OpenAI) plug into the same `IBrainProvider` interface
  without changing `BotPlayer` or `BrainRouter`.
- **Harder:** First time the game server makes outbound HTTPS calls
  in the brain path. New failure modes: TLS, DNS, timeouts, partial
  reads, 429s. These are all caught by the existing
  `EnqueueBrainInput` action queue (LLM calls happen on a thread
  pool task; results post back into the bot's brain tick) plus the
  per-call timeout, but the operational surface area grew.
- **Harder:** Token lives in a service env var. Rotating it requires
  a service restart. Acceptable for dev; replaced by a proper
  secret store (probably ACE's existing `appsettings.json` pattern
  with a strongly-typed `LlmConfig` section) when the API stabilizes.
- **Open question for next ADR:** Persistent per-bot chat memory
  (the `BotContext.short_term_memory_summary` field in
  [`brain-providers.md`](../brain-providers.md#prompt-structure-sketch))
  ships in E9 as an in-process rolling window only. The schema +
  migration for SQLite-backed persistence — needed for bots to
  "remember things across reboots" per the user's request — is
  deferred to ADR-0010 and a follow-up milestone, because adding
  a schema migration on top of the LLM transport is too much
  surface area for one merge. The in-process window is sufficient
  to demonstrate the conversation loop end-to-end first.
- **Open question for next ADR:** Cost / budget enforcement
  (the `budget_per_hour_usd` knob in
  [`brain-providers.md`](../brain-providers.md#provider-config-example))
  is also deferred. The GitHub Models free tier is the de-facto
  cap for E9.

## References

- Spec being implemented:
  [`../brain-providers.md`](../brain-providers.md)
- Resolves none of [`../../roadmap/open-questions.md`](../../roadmap/open-questions.md)
  outright, but unblocks experimentation on:
  - "Latency budget for /tell" — once shipped we can measure
  - "Model size per archetype" — GitHub Models offers small and
    large models from one endpoint, ready for A/B testing
- Related ADRs:
  - [`0007-bots-as-player-not-creature.md`](0007-bots-as-player-not-creature.md)
    — bots ARE Players, so `BotPlayer.OnReceivedTell` is the hook
    point
  - [`0008-bot-tick-via-player-tick.md`](0008-bot-tick-via-player-tick.md)
    — the brain-tick contract LLM results land back on
- Implementation:
  [`darinh/ACE-bots@botplayer-spike`](https://github.com/darinh/ACE-bots/tree/botplayer-spike)
  under `Source/ACE.Server/Bots/Llm/`
