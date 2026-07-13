# InferHub v2.3.0 — OpenAI-compatible API + Docker distribution

Phase 21. Two headline features, and two bugs found by actually driving the thing.

## OpenAI-compatible API

The coordinator now serves `/v1` alongside the Ollama endpoints it always had:

- `POST /v1/chat/completions` — blocking + SSE streaming
- `POST /v1/completions` — legacy text completion (→ `generate`)
- `POST /v1/embeddings` — `float` and `base64` encodings
- `GET /v1/models`, `GET /v1/models/{id}`

Point any OpenAI SDK, LangChain app, or IDE plugin at the coordinator's base URL and it
works. The same least-busy router, sticky conversation affinity, pre-stream failover, and
inline retrieval run underneath — `X-InferHub-Retrieve` is honoured on `/v1`, so an OpenAI
client gets RAG through `default_headers`.

Verified against the real OpenAI Python SDK (2.45.0), not just unit tests.

**Honest about the gaps.** `n > 1` is rejected with a `400` rather than quietly served once.
Tool calls are mapped in blocking mode only; streaming `tool_calls` deltas are deferred to
v2.4 and not faked. `logprobs` / `logit_bias` / `user` are accepted and ignored. Multimodal
content parts are rejected rather than silently dropped.

**Zero new dependencies.** `System.Text.Json` does the translation; the SSE framing is
written by hand, exactly as the NDJSON framing already was. Design rule 5 holds.

### Design decisions (recorded in CLAUDE.md)

- **D1** — OpenAI DTOs are coordinator-only (`OpenAi/`). The node protocol stays Ollama-shaped;
  the nodes never learn the second dialect exists.
- **D2** — The bearer guard is prefix-based over `/api` *and* `/v1`. Mapping `/v1` without
  widening it would have shipped an unauthenticated inference API. Regression-tested.
- **D3** — One dispatch path, two formatters: routing + failover + metrics live once in
  `InferenceCore`; each surface formats the outcome in its own dialect.
- **D5** — In a container, host traffic is not loopback, so **API keys are mandatory** in the
  compose stack (unlike bare metal).
- **D6** — `ASPNETCORE_URLS` **does not work**: `appsettings.json` pins `Urls` and overrides
  it, so the container would bind loopback and answer nobody. The image sets `Urls` directly.
  Verified at runtime.

## Docker distribution

`docker compose up` now gets you a coordinator. Multi-stage Dockerfiles for coordinator and
node (non-root, healthchecked), a compose stack, a Postgres overlay, `.env.example`, and a
runbook. Plus the repo's first CI: build+test and a Docker build on PRs, and a GHCR publish
on `v*` tags for `linux/amd64` and `linux/arm64`.

## Bug fixes — all pre-existing, all found by end-to-end verification

### Streaming was broken over SignalR (all releases since streaming shipped)

**Every `stream: true` request hung and returned nothing** — on the existing `/api/chat`
surface as much as the new `/v1/chat/completions`.

`NodeHub.StreamChunks` declared a `CancellationToken` parameter. SignalR only treats a
`CancellationToken` as a synthetic, server-supplied argument on hub methods that *return* a
stream. `StreamChunks` returns `Task` — it is a **client-to-server** upload — so the token was
counted as a real argument the caller must send. The node sends none (the `IAsyncEnumerable`
travels as a stream, not an argument), so the binder threw

```
InvalidDataException: Invocation provides 0 argument(s) but target expects 1.
Stream '4' closed with error 'Failed to bind Stream message.'
```

the stream never bound, and the caller waited forever.

It survived this long because **every test stubbed `IDispatcher` and none crossed the wire**.
`NodeHubStreamingTests` now spins up a real Kestrel host and a real `HubConnection` and
streams through the actual hub — it fails if the parameter ever comes back.

### `prompt_eval_duration` overflowed `Int32` (OllamaClient 2.0.0)

Blocking chat returned `502` whenever prompt evaluation took longer than ~2.1 seconds, which
a cold model always does. Ollama reports durations in nanoseconds; the field was typed `int?`
while every other duration on the same type was `long?`. Fixed in **OllamaClient 2.0.1**
(separate repo) — InferHub's package reference must be bumped to `2.0.1` before this tag
goes out, or v2.3.0 still carries the bug.

### The node gave up on slow models 3 minutes before the coordinator did

`Dispatcher:TimeoutSeconds` defaults to **300**, so the coordinator waits five minutes. But
the node's Ollama calls inherited `HttpClient`'s default timeout of **100 seconds**, because
nothing ever set one. Any model whose cold load ran past that — routine for a large model on
a cold GPU box, which is precisely InferHub's reason to exist — was cancelled node-side and
surfaced as a `502` that looked like the node had failed.

New `Ollama:RequestTimeout` (default `00:05:00`, matching the coordinator's patience;
validated as positive at startup). Raise it for very large models.

## Upgrading

No API changes. The Ollama surface is byte-for-byte identical — same NDJSON, same error
envelope, same headers. Existing tests pass unedited; that was the acceptance bar for the
`InferenceCore` refactor.

Two things that used to fail will now simply work: streaming clients that appeared to hang,
and large/cold models that returned a `502` after 100 seconds.

Requires **OllamaClient ≥ 2.0.1**.
