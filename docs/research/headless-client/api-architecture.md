# API-based shared services architecture

**Status:** active design, replaces the in-process shared-kernel
sketch in [`README.md`](README.md) §Architecture.

**Why we pivoted:** the user wants bots distributed across
multiple physical servers. Many-bots-in-one-process still
matters for per-host density, but bots can no longer assume any
shared resource lives in their address space.

## Rules

1. **Every dependency a bot does not own goes through an HTTP
   API.** No in-process LLM, no in-process pathfinder, no
   in-process training writer, no in-process world-data lookup.
2. **API endpoints can be local or central.** Each service has
   its own configurable base URL. A bot host can run its own
   `ApiHost` on localhost for low latency, or point at a
   shared central host for shared state / shared LLM quota.
3. **Authenticated requests only.** Bearer token in
   `Authorization` header. Hard-coded shared secret is fine for
   now (`AC_BOTS_API_TOKEN` env var; falls back to
   `dev-insecure-token` with a startup warning).
4. **Each request identifies the bot.** `X-Bot-Id: <uuid>`
   header on every call. Server scopes per-bot state by this id.
5. **Each request has an idempotency key.** `X-Request-Id:
   <uuid>` header so retries on transient failure are safe.
6. **Schema versioning in the path.** `/v1/<service>/<op>`.
7. **MessagePack over HTTP/2 (h2c) for hot paths.** The API is
   in the bot's per-tick critical path; per-call overhead
   directly raises bot reaction latency. See §Transport choice
   and §Performance budget below. JSON over HTTP/1.1 is kept
   for `/v1/health` and any developer-debug endpoints so curl /
   browser inspection still works.
8. **Bot-local state stays bot-local.** The UDP socket, ISAAC
   keys, the immediate motor command stream (30Hz tick), and the
   per-bot perception cache for the current landblock all live
   in the bot process. Sending those over the network would be
   absurd.

## What is in vs out of the bot process

| Lives in bot process            | Lives in an API service       |
|---------------------------------|-------------------------------|
| UDP socket + ISAAC state        | LLM inference                 |
| Packet framing + sequencing     | Pathfinding compute           |
| Motor commands (30Hz tick)      | Training data ingest          |
| Current PVS / nearby objects    | World data lookups (DAT)      |
| Short-lived plan execution      | Per-bot persistent memory     |
| Immediate tactical decisions    | Shared knowledge graphs       |

The split rule: anything CPU-cheap, latency-critical, or tied to
the UDP stream stays local. Anything CPU-expensive, shareable, or
worth persisting across bot restarts crosses an API.

## Transport choice

| Concern | HTTP/1.1 + JSON | HTTP/2 (h2c) + MessagePack | Named pipes + MessagePack |
|---|---|---|---|
| Per-call wire overhead | 200-400 B headers + JSON ASCII | ~30 B HPACK + binary | ~10 B framing + binary |
| Serialization cost | high (UTF-8 + reflection unless source-gen) | low (binary, source-gen) | low |
| Connection setup | per-request TCP handshake without keep-alive; one connection per route with keep-alive | single multiplexed connection per ApiHost | single per-process channel |
| Multi-request parallelism | head-of-line blocking | full multiplex | full multiplex |
| Cross-host portability | yes | yes | local only |
| curl / browser debuggable | yes | partial (curl `--http2-prior-knowledge`) | no |

**Decision:** HTTP/2 h2c (HTTP/2 cleartext, no TLS) with
MessagePack payloads for hot paths. JSON over HTTP/1.1 stays
available for `/v1/health` because anything that takes a
human five seconds to debug with `curl` is worth keeping.

**Local-host fast path (future):** if profiling shows that
h2c is still too slow for a co-located ApiHost, fall back to
named pipes on Windows / Unix domain sockets on Linux for the
same wire format. The `ApiClient` will pick the transport
based on whether the base URL starts with `npipe://`,
`unix://`, or `http://`. Phase 1 stays on h2c only.

## Performance budget

The headless client ticks the motor layer at ~30 Hz (33 ms
per tick). Multiple API calls can occur per tick (perception
snapshot, pathfinding query, etc.). Budget:

| Operation | Target p95 | Hard ceiling | Rationale |
|---|---|---|---|
| `/v1/health` startup ping | 5 ms | 50 ms | One-time, not perf-critical |
| `/v1/path/find` (cached) | 200 µs | 2 ms | Per tactical decision, occasionally per tick |
| `/v1/world/weenie/{id}` | 200 µs | 2 ms | Lookups during item interaction |
| `/v1/memory/bot/{id}/*` GET | 500 µs | 5 ms | Per scene change |
| `/v1/memory/bot/{id}/*` POST | 1 ms | 10 ms | Per place / contact event |
| `/v1/training/log` batched | 100 µs append (fire-and-forget) | 1 ms | Per perception step |
| `/v1/llm/plan` | 100-2000 ms | 5 s | Pulls human-scale latency; only triggered at goal-shift, not per tick |
| `/v1/llm/chat` | 100-2000 ms | 5 s | Same |

LLM calls are explicitly off the tick path. Pathfinding and
world-data calls ARE on the tick path and the budget is
strict.

**Achieving sub-millisecond local calls** requires:

1. Single long-lived `HttpClient` per bot host process with
   `SocketsHttpHandler.PooledConnectionLifetime` set high.
2. `HttpVersion.Version20` + `HttpVersionPolicy.RequestVersionExact`.
3. MessagePack with source-generated formatters (no runtime
   reflection per call).
4. Pre-allocated `ArrayBufferWriter<byte>` per concurrent
   request slot (avoid per-call GC).
5. `Content-Length` always set (server can skip chunked).
6. No middleware on hot paths beyond auth and request-id.
7. `X-Request-Id` generated as a `Guid.NewGuid()` and
   formatted in-place to a stack `Span<char>` (no string
   allocation).

These optimizations are not premature — they are the
difference between "API client adds 5 ms per tick" (~15% of
the tick budget) and "API client adds 200 µs per tick" (~0.6%
of the tick budget), and the latter is what makes the
architecture viable.

## Service catalog (Phase 1 contract)

All endpoints are POST unless noted. Hot-path endpoints take
and return `application/x-msgpack` (MessagePack). Debug
endpoints (`/v1/health`) take and return `application/json`.
All endpoints (other than `/v1/health`) require the
`Authorization`, `X-Bot-Id`, and `X-Request-Id` headers.

### Health (no auth required)

- `GET /v1/health` → `{"status": "ok", "service": "ApiHost",
  "version": "..."}`. Used by bots at startup to confirm the API
  host is reachable.

### LLM service (`/v1/llm/`)

- `POST /v1/llm/plan` — given a goal + perception snapshot,
  return a structured plan in the existing plan vocabulary.
  Request:
  ```json
  {
    "goal": "complete training academy",
    "perception": { "near": [...], "self": {...}, "location": {...} },
    "vocabulary_version": "v1"
  }
  ```
  Response:
  ```json
  {
    "plan_id": "uuid",
    "vocabulary": "fetch",
    "ops": [ { "op": "Collect", "args": {...} }, ... ],
    "model": "stub-canned",
    "trace_id": "uuid"
  }
  ```
- `POST /v1/llm/chat` — single-turn chat completion. Mostly for
  Social-layer responses.

### Pathfinding service (`/v1/path/`)

- `POST /v1/path/find` — given `{start, goal}` (cell+pos),
  return a list of waypoints.
- `POST /v1/path/explore` — given a current position + "explore
  the next unknown area" semantics, return waypoints into
  unexplored space.

(Phase 1 returns canned/empty paths. Real navmesh integration is
post-Phase 2.)

### Training service (`/v1/training/`)

- `POST /v1/training/log` — write-only firehose. Body is a list
  of perception+action+outcome triples. Server appends to a
  JSONL log under a per-bot directory. No response body beyond
  202 Accepted.
- `POST /v1/training/event` — single-event variant for low-rate
  high-importance events (deaths, level-ups, quest completions).

### World data service (`/v1/world/`)

- `GET /v1/world/weenie/{wcid}` — static weenie definition
  lookup. Server caches DAT data.
- `GET /v1/world/landblock/{landblockId}` — landblock metadata
  (cell list, environment).
- `GET /v1/world/portal/{portalId}` — portal destination.
- Cache-friendly: server emits `ETag` + `Cache-Control`. Client
  can do `If-None-Match` and get 304.

### Memory service (`/v1/memory/`)

- `GET /v1/memory/bot/{botId}/places` — list places this bot has
  been (lifestone bindings, landblocks visited).
- `POST /v1/memory/bot/{botId}/places` — record a new place.
- `GET /v1/memory/bot/{botId}/contacts` — NPCs and players this
  bot has interacted with.
- `POST /v1/memory/bot/{botId}/contacts` — record an
  interaction.
- `GET/POST /v1/memory/bot/{botId}/quest/{questId}` — per-quest
  scratch state (where I left off, what I've tried).
- Server backs this with a per-bot SQLite file under
  `data/bots/<botId>.sqlite`. Migrations baked in.

## Error format

`application/problem+json` per RFC 7807:

```json
{
  "type": "https://ac-bots.local/errors/auth-required",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Authorization header missing or invalid",
  "instance": "/v1/llm/plan"
}
```

Standard codes:
- 401 — missing/bad bearer token
- 403 — bot id not authorized for this resource
- 404 — resource not found
- 409 — concurrent modification (memory writes)
- 429 — rate limit (Phase 2+)
- 503 — upstream LLM / pathfinder unavailable

## Deployment topologies

The bots can run in any of three topologies:

### Topology A — single host, single process per bot

```
+--------------------------------------+
| host: dev-laptop                     |
| +--------+  +--------+  +--------+   |
| | bot 1  |  | bot 2  |  | bot N  |   |  --> 127.0.0.1:9000 (ACE)
| +---+----+  +---+----+  +---+----+   |
|     |           |           |        |
|     +-----------+-----------+        |
|     |                                |
|     v                                |
| +---+-------------+                  |
| | ApiHost :9100   |  --> local LLM, |
| +-----------------+      navmesh,    |
|                          sqlite      |
+--------------------------------------+
```

Use case: dev. Everything on one machine, lowest latency.

### Topology B — bots co-located with ACE; central API

```
+--------------------+    +----------------------+
| host: acserver-1   |    | host: api-central    |
| ACE server         |    | ApiHost :9100        |
| bots[1..50]    --------->  (LLM, pathing,      |
+--------------------+    |   training, memory)  |
| host: acserver-2   |    +----------------------+
| ACE server         |             ^
| bots[51..100]  ---------------+
+--------------------+
```

Use case: scaling. Many ACE shards, shared LLM/memory state.

### Topology C — bots elsewhere, ACE central

```
+--------------------+    +----------------------+
| host: bots-1       |    | host: ace-prod       |
| bots[1..100]   --------->  ACE :9000           |
+--------------------+    +----------------------+
| host: bots-2       |             ^
| bots[101..200] ---------------+
+--------------------+
         |                +----------------------+
         +--------------->| host: api-central    |
                          | ApiHost              |
                          +----------------------+
```

Use case: load test. Bot processes scale horizontally
independently of ACE.

## Resource budget impact

The 5 MB per-bot working set rule still holds — the API client
itself is one `HttpClient` per bot host (shared across all bots
in that process), not per-bot. JSON serialization uses
`System.Text.Json` source generators to avoid runtime reflection
allocations. Each per-bot DI scope owns:

- One pooled `HttpRequestMessage` for hot endpoints
- One pre-serialized headers dictionary (token + bot-id)
- A `Channel<TrainingEvent>` for batched training writes
- No long-lived `HttpClient` (shared)

## What this does NOT include yet

- Service discovery (Consul/etcd) — config files only
- Mutual TLS — Phase 2+, deferred per user direction
- Named-pipe / UDS local fast path — Phase 2+ (HTTP/2 h2c first)
- Rate limiting — server returns 503 on overload but no 429s
- OpenTelemetry tracing — log-only for Phase 1
- Per-bot quotas — global token only for Phase 1

## Phase 1 deliverable

1. `experiments/api-host/` — ASP.NET Core Minimal API on
   `:9100` with bearer auth, HTTP/2 h2c, `/v1/health` (JSON),
   `/v1/llm/plan` stub (MessagePack).
2. `experiments/services-common/` —
   `AcAiPlayers.ServicesClient` library: `ApiClient`,
   `ApiClientOptions`, `AuthHandler`, `BotId` value type,
   MessagePack source-gen formatters.
3. `experiments/headless-client/` — calls
   `ApiClient.GetHealthAsync()` at startup; if it fails,
   abort before connecting to ACE.
4. Manual `curl --http2-prior-knowledge` smoke test of every
   endpoint with + without the bearer token.
