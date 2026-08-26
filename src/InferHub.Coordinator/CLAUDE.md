# InferHub.Coordinator — agent context

**Scope: `src/InferHub.Coordinator/`.** The always-on host: HTTP, the SignalR hub, routing,
admission, the cloud providers, the cluster lease, `/metrics` and the console. **Retrieval and the
vector stores split out in phase 62** — see below.

> **Read the root `CLAUDE.md` first** — the seven design rules bind everything here. The two that
> bite most often in this project are **rule 4** (no persisted state, and its four recorded
> exceptions) and **rule 7** (no content on the coordinator, which now covers prompts and uploaded
> pictures as well as conversations).

## Related context

- The contracts and dialects this host renders: `src/InferHub.Shared/CLAUDE.md`
- The vector providers, replication, ownership and migration: `src/InferHub.Coordinator/Vector/CLAUDE.md`
- The other side of every job: `src/InferHub.Node/CLAUDE.md`
- The images this ships in, and their permissions traps: `deploy/CLAUDE.md`

## Coordinator anatomy


- [Program.cs](src/InferHub.Coordinator/Program.cs) wires DI, middleware order, and
  endpoint mapping. The middleware order is **`AdminApiKeyMiddleware` → `BearerApiKeyMiddleware`** —
  do not swap them. Admin middleware short-circuits on `/api/admin/*`; bearer middleware
  guards inference routes.
- [Endpoints/](src/InferHub.Coordinator/Endpoints/) — minimal-API handlers. Three groups:
  inference (`/api/generate`, `/api/chat`), status (`/api/status`, `/api/tags`,
  `/api/nodes`), admin (`/api/admin/*` including the SSE `/api/admin/stream`).
  [InferenceCore.cs](src/InferHub.Coordinator/Endpoints/InferenceCore.cs) holds routing +
  pre-stream failover + metrics, and is shared by **both** client dialects; the endpoint
  files only format its outcome (see D3).
- [OpenAi/](src/InferHub.Coordinator/OpenAi/) — the OpenAI-compatible edge (phase 21):
  `/v1/chat/completions`, `/v1/completions`, `/v1/embeddings`, `/v1/models`. Only the two
  ASP.NET-bound pieces live here now — `OpenAiEndpoints` and `OpenAiStreamingResult` (SSE).
  The DTOs and the shape mappers moved to
  [InferHub.Shared/OpenAi/](src/InferHub.Shared/OpenAi/) in phase 22, because the node drives
  the same dialect *upstream*.
- [Ingestion/](src/InferHub.Coordinator/Ingestion/) — document ingestion (phase 23):
  `TextExtractor` (text/Markdown/HTML/JSON, and PDF via `IPdfTextExtractor`), `Chunker`,
  `IngestionPipeline` (extract → chunk → batch → embed on the fleet → upsert), and `DocumentIndex`,
  which is the *only* place that knows how to read a set of chunks back as a document. Registered
  inside `AddInferHubVectorStore` — with no vector store there is nothing to ingest into.
- [Services/](src/InferHub.Coordinator/Services/) — `INodeRegistry` (the source of
  truth for connected nodes; raises `Changed` events the SSE stream listens to),
  `IRouter` (least-busy + sticky affinity, skips cordoned nodes), `IDispatcher` (job
  lifecycle, streams, pre-stream failover), `IConversationAffinity`, `IAuditLog`,
  `NodeReaper` (background heartbeat sweep), `INodeConnectionTracker` (for forced
  disconnect).
- [Hubs/NodeHub.cs](src/InferHub.Coordinator/Hubs/NodeHub.cs) — node-side SignalR
  surface: `Register`, `Heartbeat`, `ReportModels`, `JobResult`, `StreamChunks`.

  > **`StreamChunks` must never declare a `CancellationToken` parameter.** SignalR only
  > treats a `CancellationToken` as a synthetic (server-supplied) argument on hub methods
  > that *return* a stream. `StreamChunks` returns `Task` — it is a **client-to-server**
  > upload — so a token parameter is counted as a real argument the caller must send. The
  > client sends none (the `IAsyncEnumerable` travels as a stream, not an argument), the
  > binder throws `Invocation provides 0 argument(s) but target expects 1`, the stream never
  > binds, and **every `stream: true` request hangs forever on both the Ollama and OpenAI
  > surfaces.** This shipped broken for several releases because every test stubbed
  > `IDispatcher` and none crossed the wire. Use `Context.ConnectionAborted` instead.
  > [NodeHubStreamingTests](tests/InferHub.Tests/NodeHubStreamingTests.cs) now guards this
  > with a real Kestrel host and a real `HubConnection` — keep it that way.
- [wwwroot/](src/InferHub.Coordinator/wwwroot/) — static `status.html` (read-only) and
  `console.html` + `console.js` (admin). **Build-free**: plain HTML/CSS/JS, no Node/React
  toolchain. If you reach for a bundler, stop and rethink. Since phase 45 the console drives the
  whole tools-and-fleet track — capabilities, tools, node profiles, node retrieval, and a
  **Needs attention** strip above the fold — all of it off `/api/status` and `/api/admin/*` with no
  endpoint of its own. `ConsoleContractTests` fails when a panel reads a field the payload does not
  carry, which is the one class of console bug a unit suite can catch.

### Vector providers

Three implementations behind `IVectorStore`, selected by `VectorStore:Provider`, plus the
replication and healing that only `local` has and the migration path between them — all of it in
`src/InferHub.Coordinator/Vector/CLAUDE.md` since phase 62.

## Decisions recorded here

### Phase 21 (OpenAI surface + Docker) — also load-bearing

**D1 — OpenAI DTOs live at the edge, coordinator-only.** *Superseded by phase 22's D1 — see
below.* The DTOs now live in `InferHub.Shared/OpenAi/` because the node speaks the dialect
upstream too. What survives untouched is the part that mattered: the **node-facing job
protocol** is still Ollama-shaped job kinds (`chat`, `generate`, embed) carrying raw Ollama
JSON, and the nodes still do not know the coordinator has a second client-facing dialect.

**D2 — The auth guard is prefix-based, and `/v1` is not `/api`.** `BearerApiKeyMiddleware`
guards a list of prefixes (`/api`, `/v1`), keeps the `/api/admin` carve-out for
`AdminApiKeyMiddleware`, and shares one loopback exemption. **Adding a client-facing route
under a new prefix without adding it here ships an unauthenticated inference API.**
`OpenAiAuthTests` fails if `/v1` ever becomes reachable without a key.

**D3 — One dispatch path, two formatters.** Routing, pre-stream failover and metrics live
once, in `InferenceCore`. Both surfaces call it and format the outcome in their own dialect
(NDJSON vs SSE). Do not copy failover logic into an endpoint — two copies is how failover
quietly rots.

**D5 — In a container, host traffic is not loopback.** `Auth:RequireAuthForLoopback=false`
exempts loopback callers, but requests from the host reach a container over the bridge
network with a non-loopback source address. **API keys are mandatory in the compose stack**,
unlike the bare-metal quickstart. This is correct, and it surprises people — the runbook
says so out loud.

**D7 — In a container, `/app` is not writable, and a fresh named volume inherits its mount
point's ownership from the image.** (Found in v2.5.1, by pulling the published image on a clean
machine — the only way this class of bug is ever found. It had been shipped and broken since v2.3.)

Both images run `USER app`. `LocalVectorStore` and the node's `ReplicaStore` both call
`Directory.CreateDirectory` on a path that defaults to `./data/...` → `/app/data`, which `app`
cannot write: the coordinator **died at startup** the moment `VectorStore:Enabled=true`, and the
node would have died the moment it was assigned a replica. Pointing a volume at it did not help
either — Docker seeds a fresh named volume from the image's mount point *including its ownership*,
and a mount point that does not exist in the image is created **root-owned**. So the documented
compose stack was broken by the same root cause, and nobody noticed because
`INFERHUB_VECTORS_ENABLED` defaults to `false`.

The fix is **two lines in each Dockerfile, and both are load-bearing**:

```dockerfile
RUN mkdir -p /data && chown app:app /data      # makes the *volume* case work
ENV VectorStore__DataDirectory=/data/vectors   # makes the *bare image* case work
```

Do not "simplify" either away. And when a release touches anything that writes to disk, **pull the
published image and run it** — the unit tests and a from-source end-to-end both pass happily while
the artefact users actually install is dead on arrival.

> **The v2.5.1 fix was half of the bug, and the other half hid for five releases.** Found in
> v2.10.0, again by running the container. `FileNodeIdentity` writes the stable node id to
> `Node:DataDirectory`, defaulting to the content root — `/app` — so the **node image threw
> `UnauthorizedAccessException: /app/.inferhub-node-id` at startup on every run since v2.3.0**.
> It went unnoticed because the replica half *is* conditional on a feature being on, which made
> the conditional fix look like the whole fix. The node Dockerfile now also sets
> `ENV Node__DataDirectory=/data`, and the compose stack mounts the node's volume at **`/data`**
> rather than a subdirectory — a volume at a path the image does not contain is created
> root-owned, which is the same trap a third time. When you fix a permissions bug, grep for
> *every* write path, not the one that reported it.

**D6 — `ASPNETCORE_URLS` does not work here; set `Urls`.** `appsettings.json` pins
`"Urls": "http://localhost:5080"`, and that layer *overrides* the `ASPNETCORE_`-prefixed
provider (which loads into host config first). A container honouring `ASPNETCORE_URLS` would
bind loopback and answer nobody. The images set the config key directly
(`ENV Urls=http://+:8080`), which is layered after `appsettings.json` and actually wins.
Verified at runtime, not assumed.

**Rule 5 survived.** Phase 21 added **zero** new dependencies: `System.Text.Json` does the
translation and the SSE framing is written by hand, exactly as the NDJSON framing is.

### Phase 22 (OpenAI node backend + cloud burst) — also load-bearing

**D1 — The OpenAI DTOs live in `InferHub.Shared/OpenAi/`.** They are pure records over
`System.Text.Json` with no ASP.NET types, so rule 2 holds. Both ends need them now: the
coordinator to speak OpenAI to *clients*, the node to speak it *upstream*. Duplicating a wire
format into two projects is how two copies drift, silently. Only the ASP.NET-bound pieces
(`OpenAiEndpoints`, `OpenAiStreamingResult`) stayed in the coordinator.

**D2 — Rule 6 was reworded, not weakened.** See rule 6 above. The node-facing job protocol is
still Ollama-shaped, always.

**D3 — Yes, an OpenAI request can be translated twice, and that is deliberate.**
`/v1/chat/completions` → Ollama body → node → OpenAI body → vLLM, and back. It looks silly
written down. The alternative is a polymorphic job payload with a dialect tag, which infects
the dispatcher, the router, the affinity-key derivation, the retrieval pipeline and every test
that touches them — to save two `JsonSerializer` round-trips on a request that is about to
spend seconds on a GPU. Take the round-trips. The reason is written in
[UpstreamTranslator](src/InferHub.Shared/OpenAi/UpstreamTranslator.cs) so nobody "fixes" it.

**D4 — Cloud burst stores nothing.** [ProviderDispatcher](src/InferHub.Coordinator/Services/ProviderDispatcher.cs)
(`FallbackDispatcher` until 61) forwards the body in flight and streams the response straight through
— a proxy hop, not a cache. The model name is metered; the prompt and the answer are not.

**D5 — Cloud burst is off by default and loud when on.** Silently shipping a user's prompts to
a third party because their GPU was asleep is a betrayal, not a feature. So: `Fallback:Enabled`
defaults to `false`; only models named in `Fallback:ModelMap` are eligible (**the map is the
consent**); every fallback response carries `X-InferHub-Served-By: fallback` (node-served ones
say `node`); `/api/status` and the status page report the feature and its counter *even when it
is off*; and each burst logs at Information with the model. **`FallbackTests` is mostly a suite
about when it must not fire** — keep it that way.

*Deviation from the phase brief, recorded on purpose:* the brief said "audit-log entry", but
`IAuditLog` is a per-node *last admin action* store keyed by `nodeId` (cordon/uncordon), not an
event stream. Writing bursts into it would overwrite a node's cordon history and key events by
a node that by definition did not serve them. The visibility requirement is met by the log
line, the metric, the header and the status block instead.

**One implementation, five servers** — `Backend:Type=openai` covers vLLM, llama.cpp's server, LM
Studio, TGI and every hosted provider, and
[OpenAiUpstreamClient](src/InferHub.Shared/OpenAi/OpenAiUpstreamClient.cs) is the single place that
speaks that dialect. *"Do not grow a second one" was the right rule for one dialect; 61 D3 replaced
it with the shape that survives four — a second dialect is a second `IUpstreamDialect`.*

**Rule 5 survived again.** Phase 22 added **zero** new dependencies: `HttpClient` and
`System.Net.Http.Json` ship in the shared framework, and the SSE *parser* is written by hand
just as the SSE *writer* was in phase 21.

### Phase 23 (document ingestion) — also load-bearing

**D1 — Ingestion writes to the vector store and nowhere else.** There is no documents table, no
blob directory, no second lifecycle. **A document *is* the set of chunks sharing a `documentId` in
their metadata**, and [DocumentIndex](src/InferHub.Shared/Ingestion/DocumentIndex.cs) is the
only thing that knows how to read that set back as a document. Rule 4 survives untouched. This is
what phase 23's two additions to `IVectorStore` are *for*:

- `ScanAsync(collection, filter, limit, afterId)` — metadata scan, ordered by id, **without the
  embeddings** (hence `VectorEntry`, a record minus its vector: "not fetched" must not be
  confusable with "not there").
- `DeleteByFilterAsync(collection, filter)` — bulk delete by metadata; the filter must be
  non-empty, because an empty one means `DropCollectionAsync` and nobody should reach that by
  accident.

Both providers implement both, and `VectorProviderParityTests` proves they agree — if the two
engines disagreed about what a scan or a filtered delete matches, they would have two different
ideas of what a document *is*.

> **`LocalVectorStore.DeleteByFilterAsync` deliberately loops over the ordinary per-id delete**
> instead of doing a bulk removal under the lock. The per-id path is what appends to the raw store
> and raises `RecordDeleted`, and `RecordDeleted` is the *only* way the deletion reaches the node
> replicas. A faster bulk delete would leave every node in the fleet still serving the chunks of a
> document the hub thinks is gone — and a node replica answers reads *before* the hub does.

**D2 — The original document is not retained.** Chunk text, a content hash, and metadata. Not the
file. A retrieval system that quietly becomes a document store has two sources of truth and a
data-retention question its owner never agreed to answer.

**D3 — PDF costs one dependency, scoped and recorded.** See rule 5.

**D4 — No OCR, ever. Fail loudly instead.** A PDF whose text layer yields under ~50 characters per
page is rejected with an error that says it looks like a scan. Bolting on OCR would produce
something that *usually* works — and a bad extraction does not fail, it succeeds quietly and fills
the corpus with near-gibberish that retrieves plausible nonsense, surfacing months later as a model
that is subtly, unaccountably wrong. `PdfExtractionTests` builds **real** PDFs and parses them back;
keep it that way, because a stub cannot reach the part this dependency was spent on.

**D5 — Chunk ids are deterministic:** `sha256(documentId + ":" + chunkIndex)`. Re-ingesting replaces
chunks in place rather than layering a second copy underneath the first, and a citation minted last
month still points at the same chunk.

> **Deterministic ids make re-ingest idempotent; they do not make a document *shrink*.** A revision
> that chunks into fewer pieces leaves the old tail chunks behind — their indices no longer exist,
> so nothing overwrote them, and a stale chunk retrieves as confidently as a live one.
> `IngestionPipeline.DeleteStaleChunksAsync` is what sweeps them, *after* the new chunks land, so
> there is no window in which the document is absent.

**D6 — Embedding runs through the fleet.** `IEmbeddingDispatcher`, the same one that serves
`/api/embed`. The coordinator grows no embedding path of its own. Batches are bounded by
`Ingestion:EmbeddingBatchSize`, so a 300-page PDF queues behind itself instead of filling the fleet's
job queues and starving interactive chat.

**A partial ingest is a failure, and says so.** A run that embeds some chunks and then loses the
fleet returns **HTTP 500** with `status: "partial"` and the chunk counts. The chunks that landed are
real and visible, and re-posting the same bytes *resumes* rather than no-ops — the content-hash
short-circuit deliberately does not fire on a `partial` document, because "you already have this"
would be a lie about a document that is half-missing. A half-ingested document that claims success
is worse than a failure.

*Deviations from the phase brief, recorded on purpose:*
- **There is no `status` key in chunk metadata.** A document is `partial` when the chunks actually
  in the store are fewer than the `chunkCount` its chunks claim — derived at read time. Writing a
  status onto the chunks would mean rewriting all of them when the verdict changed, and would let
  the stored status drift from the stored chunks: the one thing a partial marker exists to prevent.
- **`/api/status`'s per-collection ingestion block reports `documentsIngested` / `chunksEmbedded`,
  not "document count" / "chunk count".** They are since-start counters that a restart zeroes, like
  everything else in `Metrics`, and naming them as a census would be a quiet lie. The real chunk
  count is `recordCount`; the real document count is what `GET /api/collections/{c}/documents` reads
  back out of the store.

**The ingest endpoints are client-scoped, not admin.** `/api/collections/{c}/documents` sits under
the `Auth:ApiKeys` bearer guard. Ingesting is a client action; forcing an admin key on it would push
people toward using one key for everything, which is worse for them than the split it was meant to
protect. The console's documents panel therefore holds its **own** client key — the admin key the
rest of the console uses will not open it.

**`X-InferHub-Sources` changed shape in v2.5.0** — from `["chunkId", ...]` to
`[{"id":..., "documentId":..., "page":...}, ...]`. A chunk id alone tells the reader nothing about
where the answer came from, and a citation that cannot name a document and a page is not a citation.
`documentId` and `page` are omitted (not null) for records written straight through `/api/vector`,
which never had a document.

### Phase 25 (clients, quotas & usage) — also load-bearing

**D1 — Named clients are backwards compatible.** `Auth:Clients` is a list of
`{ Id, Key, Limits }`; the flat `Auth:ApiKeys` list keeps working and its entries resolve to
one shared anonymous unlimited identity (`ResolvedClient.Anonymous`). A key that appears in
both lists, or in two clients, **fails startup** (`ApiKeyOptionsValidator`) — attribution must
never depend on list order. `BearerApiKeyMiddleware` resolves the client and stashes it on
`HttpContext.Items`; it stayed a middleware and did not grow a policy engine. Loopback is
exempt from *rejection*, not from identity: a valid named key still resolves on loopback, so
quotas can be exercised locally.

**D2 — Usage persistence is the second exception to rule 4.** See rule 4 above for the full
reasoning; the short form: append-only facts, own connection string, `none` by default.

**D3 — Count tokens, never text.** A `UsageRecord` is a client id, a model, a kind, two
integers, a fallback flag and a timestamp. Not the prompt, not the completion, not a hash, not
a "sample" — and there is deliberately **no flag** to add one, because a flag is an invitation.
`UsageLedgerTests.NoPromptOrCompletionTextExistsAnywhereInTheUsagePath` pins the record's
shape. The meter parses exactly three JSON fields (`prompt_eval_count`, `eval_count`, `model`)
and reads counts from the **terminal** chunk of a stream; a stream that never delivers its
terminal chunk (mid-stream disconnect) records **nothing** — the counts only exist in that
chunk, and a meter that invents numbers is worse than one that under-counts an aborted request.
Embeds are metered once, at the funnel (`EmbeddingDispatcher`), so ingestion and retrieval
count as usage without threading a client id through four call chains.

**D4 — Rejection is honest and standard.** Over a rate limit → `429` + `Retry-After` (window-
accurate); over the daily budget → `402` + `Retry-After` pointing at UTC midnight, checked
*before* the rate limits because "waiting a minute will help" would be a lie. A model outside
`AllowedModels` → `404` **byte-identical to a model that does not exist** — a client is not
told what exists but is not for them. On `/v1` these map to the OpenAI envelope
(`rate_limit_error` / `rate_limit_exceeded`, `insufficient_quota`). Admission runs once, in
`InferenceCore`, before routing — not in middleware, because it needs the model name. Budgets
can be overshot by one in-flight request: counts are fed back post-completion, and a meter
that guesses up front would be worse.

**D5 — Saturation queues, briefly, then fails.** `RequestQueue`: when every node holding a
model is at its **declared** `MaxConcurrency`, a request waits up to `Queue:MaxWaitSeconds`
(default 30), bounded by `Queue:MaxDepth` (default 64); past either → `503` + `Retry-After`.
Saturation is *defined once*, in `FleetSaturation`, shared with cloud burst — and it has two
questions on purpose: `IsSaturated` (zero nodes count as saturated; cloud burst's question)
vs `HasSaturatedFleet` (zero nodes = false; the queue's question — waiting for a slot only
makes sense when nodes exist to free one). A node with no declared cap never queues.
**Precedence with cloud burst is explicit and tested:** `Trigger=no-node-or-saturated`
overflows to the upstream *instead of* queueing — a client who opted into burst asked for an
answer in seconds, not a place in line. Queue depth and median wait are on `/api/status`
(reported even when zero) and the status page.

### Phase 26 (fleet operations: model management & measured routing) — also load-bearing

**D1 — Model management is a hub → node command, not a new API on the node.** The **mesh protocol is
outbound-only**: the coordinator never dials a node, and no deployment ever requires an inbound rule
on a GPU box for the fleet to work — that is the whole point of the outbound SignalR design. Model
commands travel down the existing connection: the coordinator sends `ExecuteModelCommand` to a node,
and the node streams `ModelCommandProgress` back via `StreamModelCommandProgress` — a client-to-server
stream, so like `StreamChunks` **it must never declare a `CancellationToken` parameter** (same binder
trap; use `Context.ConnectionAborted`). Nothing about the NAT story changes.

> **Amended in phase 37.** This paragraph used to read "the node has no inbound surface and never
> will". Solo mode gives the node a **client-facing** listener, and the distinction is the whole
> reason that is not a reversal: the hub still never connects to a node, and solo mode exists
> precisely so a deployment with *no coordinator at all* is possible. No job, model command or
> replica op ever arrives over it. See phase 37 D1 — and note that model management deliberately
> stayed hub-driven rather than being exposed on the local API "since we're here".

**D2 — Progress streams on the existing SSE channel.** A pull takes minutes, so it is not
request/response. [ModelCommandCoordinator](src/InferHub.Coordinator/Services/ModelCommandCoordinator.cs)
relays each frame as a `model-progress` event on the existing `/api/admin/stream`. No new transport.
It also **coalesces** a duplicate command for the same node+kind+model onto the one already running
(returns the existing command id, `reused: true`), and it holds no persistent state — a restart forgets
in-flight commands like everything else on the hub.

**D3 — Not every backend can manage models, and it declares so rather than throwing.**
`IInferenceBackend.SupportsModelManagement` is reported at registration (on `NodeRegistration`), so the
coordinator gates the endpoints and the console greys out controls a node cannot honour. `OllamaBackend`
returns `true` (pull/delete via `OllamaClient`, warm via an empty-prompt generate); `UpstreamBackend`
returns `false` — a vLLM/hosted upstream's model is fixed at launch. A backend asked to do the
impossible **refuses with a clean terminal error frame, never a 500** — `ModelCommandExecutor` turns an
unsupported backend or a thrown backend call into a `Done` frame with `Error` set. `ModelCommandTests`
pins this.

**Placement reuses phase-15, it does not reinvent it.** `POST /api/admin/models/{model}/ensure?replicas=N`
pulls the model onto the most suitable capable-and-manageable nodes that don't already have it, skipping
cordoned ones, and **reports what it decided and why** (already-present, pulling, shortfall, eligible
candidates). The pure decision lives in
[ModelPlacement.Choose](src/InferHub.Coordinator/Services/ModelPlacement.cs) over
`ReplicaPlacement.ComputeTarget` — a non-manageable holder (e.g. a vLLM node serving the model) still
counts toward N, but only manageable nodes are ever pulled onto. `PlacementTests` covers the skips and
the "not enough candidates, and says so" case. `GET /api/admin/models` is the fleet-wide model × node
matrix.

**D4 — Measured throughput is decayed and never a cold-start penalty.**
[ThroughputTracker](src/InferHub.Coordinator/Services/ThroughputTracker.cs) keeps an EWMA
(`alpha=0.3`) of tokens/second per (node, model), fed from the `eval_count`/`eval_duration` every
completed response already carries — the `Dispatcher` records it at completion, blocking and streaming,
and reads no message content. **A node with no measurement is treated as *average* (the mean measured
rate for that model), never as slow** — a pessimistic default would starve a fresh node of the requests
it needs to earn a measurement, which is a load balancer that has quietly stopped balancing. Measured
tokens/sec is on `/api/status` per node and on the status page.

**D5 — Measured routing is opt-in for one release.** `Router:Strategy` = `least-busy` (default,
**bit-for-bit** the pre-v2.8 behaviour) | `throughput` (best expected completion time = `(load+1)/rate`).
**Sticky conversation affinity still wins where it applies** — throughput is a tiebreak among
candidates, not a replacement for affinity, because a warm model on a slower node usually beats a cold
one on a faster node. `ThroughputRoutingTests` asserts the fast node wins, the unmeasured node is not
starved, affinity still wins, and `least-busy` is unchanged. Default moves to `throughput` in a later
release once there is evidence, not an argument.

**Rule 5 survived again.** Phase 26 added **zero** new dependencies.

### Phase 28 (Prometheus `/metrics`) — also load-bearing

**D1 — The exposition format is hand-written, and `prometheus-net` stays out.**
[PrometheusFormatter](src/InferHub.Coordinator/Observability/PrometheusFormatter.cs) is a pure
function from a gathered `PrometheusScrape` to a string. The format is `# HELP` / `# TYPE` /
`name{labels} value` — the same "three lines of string formatting" reasoning that kept the NDJSON
(phase 9) and SSE (phase 21) framing dependency-free. Rule 5 survived again: **zero new
dependencies**. An OTLP *push* exporter would genuinely need a package and is deferred, opt-in,
only if demand appears.

**D2 — This phase exposes numbers; it measures none.** Every series comes from `Metrics`,
`ThroughputTracker`, `RequestQueue` and `AdmissionControl`, all of which already computed it.
Nothing was added to the request path, and `/api/status` is **unchanged** — this adds a surface,
it does not migrate one. If a future change starts *measuring* in the formatter, it has drifted.

**D3 — `/metrics` is admin-guarded by default, and is not under the bearer guard.** It is
operational like `/health` (which is open), but unlike `/health` it exposes node names, model
names, client ids and traffic shape. So `AdminApiKeyMiddleware` now guards a small **prefix set**
(`/api/admin`, plus `/metrics` unless `Metrics:OpenScrape`) rather than one constant.
`OpenScrape=true` opens **only** the scrape endpoint — `PrometheusMetricsTests` fails if it ever
unlocks `/api/admin/*`, which would be a config flag that quietly grants cordon and model-pull to
anyone who can reach the port. It is deliberately not under `BearerApiKeyMiddleware`: a scraper is
not an inference client and must not hold a token that can spend GPU time.

**D4 — Client series come from `AdmissionControl`, never from the usage ledger.** The ledger is
append-only history and is never *read* to drive anything (rule 4 / phase-25 D2) — a metrics
endpoint reading it would have quietly ended that reasoning. Counts only; there is no content
anywhere in the usage path (rule 7).

**D5 — Absence is a fact, so absence is what is emitted.** An unmeasured `(node, model)` has **no**
`inferhub_node_tokens_per_second` series rather than a `0`: the router treats an unmeasured node as
*average*, never as slow (phase 26, D4), and a zero on a dashboard is a lie that pages someone about
a node nobody has asked anything yet. Same for an unset client limit (unlimited is no series — not
`0`, and not a `-1` sentinel a dashboard would happily plot) and for the queue's median before
anything has queued. The **fleet** counters are the opposite and always present at zero, where a
zero is a statement rather than an absence.

> `PrometheusMetricsTests` **parses the output back** with a minimal in-test exposition reader
> rather than string-matching it. Substring assertions pass happily on output no Prometheus can
> read, which is the exact failure this endpoint exists to avoid. It also asserts an invariant
> decimal separator on every value line — a decimal comma is a locale bug that only appears on a
> Bulgarian or German host and sinks the whole scrape.

### Phase 30 (stable-node affinity + optional persistence) — also load-bearing

**D1 — Affinity keys on the stable `nodeId`, and a disconnect no longer forgets.** The map was
keyed to a SignalR `connectionId`, which is **not stable across a node's own reconnect** — so a
node bouncing its connection dropped its warm conversations even while it stayed up.
[ConversationAffinity](src/InferHub.Coordinator/Services/ConversationAffinity.cs) now keys on
`nodeId`; the [Router](src/InferHub.Coordinator/Services/Router.cs) resolves it to a live candidate
at dispatch time (a hint for a disconnected/cordoned/model-less node is simply absent from the
candidate set — a clean miss). The consequence that matters: **`NodeHub.OnDisconnectedAsync` and
`NodeReaper` deliberately do *not* forget affinity anymore.** A disconnect is often a reconnect in
progress, and an evicted node that re-registers with the same id should resume its conversations;
the sliding window bounds the map for one that never returns. **Only an explicit admin deregister**
(`ForgetNode`) forgets — the operator saying a node is gone for good. *Recorded deviation from the
phase brief:* the brief kept `ForgetConnection`; it is replaced by `ForgetNode(nodeId)`, because a
connection-keyed forget on disconnect is the exact bug the re-key fixes.

**D2 — Persistence is opt-in, off by default, and a derived cache — never a source of truth.**
`Affinity:Persistence` = `none` (default, byte-identical to v2.11) | `file`.
[FileAffinityStore](src/InferHub.Coordinator/Services/FileAffinityStore.cs) reuses the local vector
raw-store discipline (append-only `ops.jsonl` + periodic compacted `snapshot.jsonl`), loaded on
startup with entries past their sliding expiry dropped on load. Rule 4 survives because a lost or
stale entry costs **one cold model load, never a wrong answer** — so it is not a third authority
alongside the vector store and the usage ledger. It is flushed but not fsynced on the hot path, and
a torn last line from a crash mid-append is skipped on load, not treated as corruption. The seam is
[IAffinityStore](src/InferHub.Coordinator/Services/IAffinityStore.cs); `NoAffinityStore` is the
default no-op. Rule 7 holds: the persisted record is `(conversationKey, nodeId, lastUsed)` — the key
is still a header value or a hash of the opening message, never content.

**D3 — Same Docker permissions trap as the vector store (D7), headed off in the same place.** The
`file` store's default `./data/affinity` resolves to `/app/data` under `USER app`, which cannot
write it. The coordinator image sets `ENV Affinity__DataDirectory=/data/affinity`, under the
existing `chown app:app /data` mount. Inert unless persistence is turned on — but when a release
touches a disk-writing path, pull the image and run it (D7), don't trust the unit tests.

**Rule 5 survived again.** Phase 30 added **zero** new dependencies.

### The multi-coordinator decisions moved out in phase 69

**Phase 32 (standby hub, the lease, the split-brain fence and the standby refusal set) is in
`src/InferHub.Coordinator/Cluster/CLAUDE.md`**, whole and unchanged. It sits over the code it
constrains, and this file needed the room for phase 69 without shortening a record to get it
(62 D6, 67 D6).

### The retrieval and vector-store decisions moved out in phase 62

**Phases 31 (client-scoped collections), 35 (Qdrant in production + cross-provider migration) and
44 (hub-assigned retrieval) are in `src/InferHub.Coordinator/Vector/CLAUDE.md`**, whole and
unchanged, along with the three providers' anatomy. They sit over the code they constrain, and this
file needed the room for the provider track without shortening a record to get it (62 D6).

### Phase 45 (the console, the metrics and the docs) — also load-bearing

**D1 — The console shows *desired* beside *effective*, and every refusal is above the fold.** The
single most confusing state phases 40–44 can produce is "I turned it on and nothing happened", and
the answer is almost always a refusal: a profile the node clamped (43 D1), a manifest
`Tools:Allowed` does not name (41 D2), a corpus that would not start (44 D3). A console showing only
*effective* state turns every one of those into a support conversation, because the operator's
evidence is a box behaving exactly as it did before. So the **Needs attention** strip sits directly
under the auth bar, aggregates all three kinds, and carries the *reason* rather than a status word.
It is fed from `/api/status` alone — `ConsoleContractTests.ARefusalIsVisibleFromTheStatusPayloadAlone`
pins that, because a refusal that needed a second request would stop being visible on the first
paint.

**A pool inside its restart budget is `running`, and a green pill for it is a lie.** Found by running
it: a manifest whose command does not exist reports `state: running` (it has not exhausted the budget,
so it has not given up) with zero workers and a `lastError`. It is declared for work it cannot do.
The console renders that as `running · no worker` in amber and puts it on the strip;
`ToolWorkerPool` **clears `lastError` on a successful start**, so the field means "the most recent
thing that happened to this pool was a failure" rather than "something once went wrong here" — a
permanent warning is a column operators learn to ignore.

**D2 — Absence stays absence, and the new series obey it.** Phase-28 D5 for the fourth time. A
capability nobody serves, a tool nobody loaded, a profile nobody wrote and a corpus nobody assigned
each produce **no** series rather than a zero, and `AbsenceStaysAbsenceForEveryPhase45Series` fails
if any of them starts emitting one. `inferhub_capability_nodes{capability}`,
`inferhub_tool_requests_total{node,tool,outcome}`, `inferhub_tool_workers{node,tool,state}`,
`inferhub_tool_pool{node,tool,state}`, `inferhub_audio_seconds_total{kind,model}`,
`inferhub_audio_characters_total{kind,model}`, `inferhub_profile_state{profile,state}`,
`inferhub_node_corpus_records{node,collection}`.

*Three recorded deviations from the brief's label sets, each with a reason:*
- **The tool series carry `node` as well as `tool`.** A per-node counter resets when that node
  restarts, which Prometheus detects **per series**; summing the fleet into one counter would make
  every node bounce read as a fleet-wide rate spike.
- **`inferhub_tool_pool` is a series the brief did not name.** A pool that gave up holds zero
  workers. So does a pool nobody has called. Without it a dashboard cannot tell them apart — which
  is D2's own complaint about zeros, pointed at the thing D2 asked for.
- **Audio is two series, not one.** A transcription meters seconds and a synthesis meters characters
  (phase-42 D7), so one `units` series would add seconds to characters and produce a number wrong in
  a way no reader can detect — the same reasoning `UsageAggregate` already applies to the ledger.

**The formatter still measures nothing** (phase-28 D2). `Metrics.RecordToolUnits` (renamed from `RecordAudioUnits` in phase 46, when images joined it) is called from
`AudioEndpoints.Meter`, the one place that already decides a job succeeded — so the number on a
dashboard and the number on a bill cannot come from two definitions of "done".

> **v3.13.0 broke D2 in the one place D2 is hardest to see, and the scrape of a published image
> found it.** A manifest `Tools:Allowed` does not name has **no `ToolWorkerPool` at all** — its
> worker and request numbers are synthesised in `ProcessToolRuntime.State` to fill the record — so
> `tool_workers{tool="echo",state="idle"} 0` and two `tool_requests_total` zeros sat on the scrape
> for as long as the file was on the box, describing a pool that does not exist. The `tool_pool`
> series already carried the whole of what is true about it. Fixed in **v3.13.1**: the worker and
> request series skip `not-allowed` rows only — a **suspended** or **stopped** pool keeps its
> counters, because those are real history. The lesson is the narrow one: *a zero you constructed to
> fill a field is not a measurement*, and it is easiest to ship in exactly the code that argues
> against zeros.

**D3 — Phase 41 left a gap at the hub, and it is filled *there*, not invented in the console.**
Until v3.13 the only thing a coordinator learned about a node's tools was the capability declaration
folded into its model report. A manifest present but not allowed, a pool a profile had suspended,
and a pool that had given up were **all the same thing at the hub: nothing** — and each has a
different fix. `NodeToolState` / `NodeToolInfo` is the phase-44 D6 mailbox verbatim: the node reports
on the model-refresh loop and immediately after a profile touches it, the hub records it in
`NodeToolRegistry`, and **the hub never asks**. A console that dialled the fleet could not show you
the node that stopped answering. A hub older than v3.13 has no `ReportToolState`, which is a debug
line and a node that carries on — phase-40 D1's mixed-fleet rule for the fourth time.

**D4 — Four images now, so the docs get a chooser and one end-to-end walkthrough.** `coordinator`,
`node`, `node:ollama`, `node:tools`, with sizes and what runs inside each: four artifacts with no
decision table is how somebody pulls 6 GB to run a 340 MB workload, or pulls the small one and
wonders where the audio went. *(**Five** since phase 46's `node:diffusion`, which is the first one
that does not stack — see 46 D9.)* And the track's story is one narrative — one box, one container, chat
+ RAG + speech, configured from a coordinator — so it is written once as a walkthrough somebody can
follow top to bottom, with the per-feature sections left as reference.

**Rule 3 survived, and rule 5 survived again.** Build-free UI: the panels are plain HTML/CSS/JS
reusing the existing CSS variables, with no bundler, no framework and no build step. **Zero** new
dependencies, and `InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.

### Phase 51 (the console, the metrics and the docs for the image track) — also load-bearing

**D1 — The console shows the *job*, not just the node, and a recipe's refusal has a reason.**
Phase-45 D1 is desired-vs-effective for *configuration*; the confusing state this track produces is
different — "it is running and I cannot tell how far along". So the Images panel is job-centric:
queue position, step *n* of *m*, elapsed, which node, and a cancel button, with the card's
arithmetic underneath.

**The gap that needed filling was phase 48's, not the console's.** A recipe whose licence nobody
accepted, or one too big for the declared budget, is simply **not declared** (48 D2/D5) — correct
routing, and the worst possible diagnostic: at the hub it is indistinguishable from a recipe that
does not exist, from one whose weights are still downloading, and from a typo. So the node reports
`NodeToolState.Images` — every recipe in the catalogue with an `ok | unlicensed | over-budget |
narrowed | not-ready` reason. **The order of those checks is the order of the fixes**: a recipe that
is both unlicensed and oversized reports `unlicensed`, because telling somebody to buy a bigger card
for a model they may not be allowed to run is the wrong advice in the wrong order.

`not-ready` is a deliberate **catch-all** — weights fetching, a fetch that failed, a recipe not
`cpuViable` on a CPU-only box, a pool that is not running — and it is deliberately **not on the
refusals strip**: weights that are still downloading are a fleet working correctly, and a strip that
cried about every cold start is a strip people learn to close.

**D2 — Absence stays absence, and the pair that shows the line is here.** Phase-28 D5 for the sixth
time, and this phase has both halves side by side: `inferhub_image_queue_depth` /
`_jobs_active` / `_retained_bytes` are **fleet gauges, present at zero** — a hub with an image queue
and nothing in it is saying something. `inferhub_image_jobs_total`, `_job_seconds` (a hand-written
histogram), `inferhub_image_recipe` and every `inferhub_node_vram_*` series emit **nothing at all**
for a recipe nobody has rendered with or a node with no declared budget, because
`budget_mib{node}=0` reads as "this box has no VRAM", which is a different and false statement from
"nobody declared a budget on this box".

The `+Inf` bucket is not decoration: without it the series is not a histogram and
`histogram_quantile` returns nothing rather than an obviously wrong number. The formatter still
**measures nothing** (phase-28 D2) — `Metrics.RecordImageJob` is called from `ImageJobRegistry`, the
one place a job ends, and the duration is from **submission** rather than dispatch, because a job
that spent four minutes queued behind somebody else's batch was four minutes slow.

**D3 — The console's gallery is the browser's, and that is a refusal rather than a limitation.**
Images fetched into the panel are object URLs in that tab, revoked as the list rolls over, gone on
reload. There is **no server-side gallery, no history endpoint and no thumbnail cache**, and the
panel says so in one line of UI text.

This is 46 D5 and 47 D6 arriving at their conclusion. **A console gallery is exactly the pressure
that turns a bounded in-memory job store into an image archive** — it is the feature request that
sounds harmless and ends with a retention policy, a deletion endpoint and a question about whose
pictures those are. The place to refuse it is here, in writing, before somebody adds a cache "just
for the console".

**D4 — `GET /api/images/jobs` is the one route this phase adds, and phase 49 deferred it here by
name.** A panel that shows "what is this fleet rendering right now" cannot be built from five routes
that all require a job id you already have. It is **client-scoped** like every other route in the
group (`ImageJobStore.ForClient`), never a fleet-wide listing: holding a job id is how you fetch the
picture, so an admin console listing every tenant's ids would undo phase-25 D4 with a UI. The
console therefore holds its own **client** key for this panel, exactly as the documents and viewer
panels do — the admin key the rest of it uses will not open one.

**It lists work, not results.** A finished job's bytes are gone in five minutes and on first read
(47 D6), so a delivered job is still in the list, still says so, and has nothing to fetch.

*Recorded deviations, on purpose:*
- **The brief said "no new API".** It also inherited phase 49's note that the listing "would be
  phase 51's job done in the wrong phase". The listing is therefore not a gap in an earlier phase —
  it was deliberately deferred to this one, and it is the only route added.
- **`ConsoleContractTests` does not require `jobs[].queuePosition`.** It is present only while a job
  is queued, and a read set that demanded it would either fail on a healthy fleet or force the
  payload to carry a meaningless zero. It has its own test, against a job genuinely waiting behind
  another on a worker given real per-step work.
- **The Images panel polls on its own timer rather than on the status poll**, and only while
  something is in flight. The status poll runs whether or not anybody has given the console a client
  key, and a background 401 prompt every few seconds would be unusable.

**Rule 3 survived, and rule 5 survived again.** The panel is plain HTML/CSS/JS reusing the existing
CSS variables — the progress bar is two divs and a width percentage. **Zero** new dependencies, no
bundler, no framework, no CDN script, and `InferHub.Shared.csproj` is still empty.


### Phase 53 (a large upload streams through the hub) — also load-bearing

**D1 — The node *pulls* the bytes; the hub is a pipe with one window.** `NodeHub.StreamAttachments`
returns an `IAsyncEnumerable<AttachmentChunk>` read straight off the client's live request body by
[StreamedUpload](src/InferHub.Coordinator/Endpoints/StreamedUpload.cs), 64 KB at a time. Memory here
is `chunk × SignalR's stream buffer` — a fixed number that does not grow with the upload — and
backpressure reaches the client's TCP window for free. **The node pulls rather than the hub pushing**
so phase-26 D1 is untouched (the stream is established by the node's own invocation, exactly as
`RequestNodeProfile` is), and so the node asks for bytes only once it is ready to write them.

> **This is the first hub method where a `CancellationToken` parameter is correct.** The binder trap
> written out three times in `NodeHub` applies to *client-to-server* streams — methods returning
> `Task` that take an `IAsyncEnumerable` argument. A method that **returns** a stream is the shape
> SignalR supplies the token for synthetically. Do not delete it on the strength of the other three
> comments.

**D2 — Size chooses the path, and the buffered one is byte-identical to v3.20.** At or under
`Tools:MaxAttachmentBytes` a request is read exactly as it was. `Tools:MaxStreamedBytes` defaults to
**0 — off**, so a deployment that changes no config has no second path at all and gets the same 413
from the same key in the same words. *Rejected: streaming everything*, which would make every
ordinary 25 MB request pay D3's ordering, D4's lost failover and D5's node requirement to fix a
problem it does not have.

**D3 — On the streamed path the routing fields must precede the file.** The model name decides where
the job goes, a body can be read once and only forwards, so a file section reached before `model` is
a **400 naming the ordering** — not a buffer-until-the-fields-arrive, which is the phase undone. A
field *after* the file is also refused rather than dropped: a transcription that ignored `language=bg`
and answered in English is the phase-42 failure with no error in it.

**D4 — A streamed job cannot fail over, and the 502 says so.** The body is consumed, past the hub and
into a node that just died, and a client's socket cannot be rewound. *Recorded correction to the
brief: there was nothing to exclude* — tool dispatch never had failover (only `InferenceCore` retries).
What the phase actually added is the honest 502 with `node_lost`, on the streamed handlers only, so
the buffered path's behaviour is untouched.

**D5 — Streamed-attachment support is declared, and absence means "no".**
`NodeRegistration.SupportsStreamedAttachments` / `NodeModels`, null read as false — phase-40 D1's
mixed-fleet rule for the fifth time, because a v3.20 node has no `StreamAttachments` to call.
`FindNodesWithModel(..., requireStreamedAttachments: true)` narrows **only** streamed jobs, so
buffered traffic keeps routing to the whole fleet, and a fleet with no capable node answers **503
naming the reason** — never a silent fall back to buffering, which would work right up to the 25 MB
it cannot do.

**D6 — One key moves three ceilings, and two of them are ASP.NET's.** Measured, not assumed: Kestrel's
`MaxRequestBodySize` is **30 000 000 bytes** (today's 25 MiB cap clears it by ~3.7 MB) and
`FormOptions.MultipartBodyLengthLimit` is 134 217 728. [UploadLimits](src/InferHub.Coordinator/Endpoints/UploadLimits.cs)
derives both from the keys, `NodeHubLimits`' argument in its own words. **Applied per route, never
globally** — a global raise would un-bound `/api/chat` and the vector data plane too.

**D9 — The streamed path serves *blocking dispatch only*, so the image routes stay buffered.** A body
can stream only while somebody is waiting for it. `POST /api/images/jobs` answers **202 before the job
runs** (47 D1), so its bytes would have to outlive the response — the archive 46 D5 and 51 D3 refused,
reached from a new direction; and `/v1/images/edits` looks streamable until `SyncMaxWaitSeconds`
expires, where 47 D1 is explicit that **the job keeps running** and only the waiting stops. Lifting the
ceiling for images needs the result direction and a place for bytes to live, which is not this phase.

**Rule 5 survived again.** Zero new `PackageReference`, and `InferHub.Shared.csproj` is still empty.

### Phase 56 (durable image jobs) — the pointer, and the two host-side facts

The decisions are in `src/InferHub.Shared/CLAUDE.md` (phase 56, D1–D5) and the exception itself is
argued in **rule 4** in the root file, because the store is shared with solo mode and a second copy
of "when do the bytes go away" is one answer too many (38 D2).

What is *this* host's: `Program.cs` builds the archive from `Images:Jobs:Persistence` through
`ImageJobArchives.Create` — the same factory the node uses — and
[ImageEdgeOptionsValidator](src/InferHub.Coordinator/Services/ImageEdgeOptionsValidator.cs) fails
startup on an unrecognised value rather than falling back to `none`, which would silently drop every
job on the next restart. And `GET /api/images/jobs` now reports `persistence` beside
`retainedBytes`/`retentionSeconds`, because it changes what those numbers mean: the console's
"held in memory, dropped on delivery" line is chosen from it, and a panel that kept saying that over
a hub configured to keep them would be a caveat that had quietly become false.

### Phase 57 (the video seam) — the pointer, and the two host-side facts

The decisions are in `src/InferHub.Shared/CLAUDE.md` (57 D1–D4): the dialect, the job-model reuse,
the grids and the two units.

What is *this* host's: `VideoEndpoints` maps four routes under `/v1/videos` plus two `501`s, and
they are guarded by nothing new — `/v1` is already in `BearerApiKeyMiddleware.OpenAiPathPrefix`
(21 D2), which `OpenAiAuthTests` now **checks** for each of them rather than assuming. And
`ImageJobRegistry` grew exactly one fork: a `VideoGenerationRequest` renders through `VideoRenderer`
and meters a second unit. The queue, the pump, the busy-node map, the `node_lost` refusal to retry
and phase 56's archive are untouched — which is track D3 paying off, since 56 was sequenced first
precisely so video would inherit a job model that already survives a restart.

### Phase 59 (console, metrics and docs for the video track) — load-bearing

**D1 — Video recipes ride in the mailbox that already exists, and the *console* splits them, not the
payload.** `NodeImageRecipeState` grew `media` and the node stopped filtering video out of it
(the filter and its comment were phase 57's, naming this phase). The four reasons in
`ImageRecipeReasons` are already the right four for a clip — a licence, the card, a profile, weights
that are not there yet — and each has the fix it had for a picture. **Considered and rejected: a
second `videos` array on `NodeToolState`** — two mailboxes to keep in step and a second copy of the
reason list. What genuinely differs is *rendering*, so `console.js` filters on `media` for the
Images table and the new Video one, and the needs-attention strip labels the row with it.

**D2 — The `image` metric names keep their names and gain a `media` label; the `MediaJob*` rename is
refused for good.** `inferhub_image_recipe`, `inferhub_image_jobs_total` and
`inferhub_image_job_seconds` carry `media="image"|"video"`. The counters have included video since
v3.25 with nothing to separate it, and a four-minute clip in a picture histogram makes both
unreadable. **Considered and rejected: `inferhub_video_*` as its own family** — 45's audio precedent
is two series for two *questions*, and "why is this model not offered" is one question with one
answer shape; two families means every fleet-refusal query written twice and one of them forgotten.
57 D10 deferred the type rename here: **no**, permanently. These names are in other people's
dashboards, and a label delivers the split for free.

**D3 — `VideoSecondsPerDay`, and `TryAdmit` checks the request's secondary unit too.** `ImageOutcome`
has carried two units since 57 and the gate looked at one, so a client whose only limit was a picture
budget rendered clips against a figure nobody sizes in megapixel-steps. The primary unit is still
checked first — a caller out of both hears about the one the request is principally measured in — and
the 402 names the unit that ran out, because "megapixel-step" would send an operator to the wrong
knob. **Considered and rejected: leaving video on the megapixel-step budget alone**, which is 42 D7
failing in a new unit. No per-minute companion: a clip's seconds arrive in one lump minutes after
admission, so a sliding window would refuse the wrong request; `MaxConcurrent` is the burst control.

**D4/D5 — One new route, and the `501` that becomes false is rewritten in the same commit.**
`GET /api/videos/jobs` ([VideoJobEndpoints](src/InferHub.Coordinator/Endpoints/VideoJobEndpoints.cs))
is client-scoped and capability-scoped; everything else the panel does goes over `/v1/videos`,
because that dialect *is* asynchronous and a console driving the real surface is worth more than an
admin shortcut. **Considered and rejected: `/api/images/jobs?media=video`** — a query parameter
standing in for a scope, over two jobs whose bytes come from two different routes. And
`GET /v1/videos` stays a 501, but no longer on the ground that "this coordinator holds no
client-scoped index of jobs": it holds one now. The reason it keeps is the one that was always
load-bearing — an id **is** the capability to fetch the bytes.

### Phase 61 (named cloud providers) — load-bearing; the dialect seam itself is 61 D3 in `src/InferHub.Shared/CLAUDE.md`

**D1 — One model is claimed by exactly one enabled provider, and a second claim fails startup.**
[ProviderOptionsValidator](src/InferHub.Coordinator/Services/ProviderOptionsValidator.cs) names the
model and both providers, counting the projected `Fallback:` section as a claimant so a collision is
caught on the *upgrade*. **Rejected: first match in declaration order** — what every gateway does,
and it makes the most consequential choice here (whose servers see a prompt) depend on JSON key
ordering surviving three layers of binding. **Rejected: a duplicate as a failover pair.**

**D2 — `Fallback:` is projected onto a provider named `fallback`, never read twice.** One dispatch
path, so "changes no config ⇒ behaves identically" is asserted against the *new* code, which is what `FallbackTests` now is.

**D4/D5/D6 — the wire is what it was.** `X-InferHub-Served-By` says `provider:<id>` for a named one
and still `fallback` for the legacy section; `inferhub_fallback_dispatched_total` keeps counting
*every* provider dispatch (it always meant "requests the fleet did not serve") with
`inferhub_provider_dispatched_total{provider}` beside it. The trigger moved onto the provider — it
was never a property of the hub. Internally the types say `Provider`; the config section, header
value, status key and metric names did not move. `/api/status` gains a `providers` array **omitted
when none is configured**, whose `credential` reads `configured`/`absent`, never a prefix of a key.

### Phase 62 (OpenRouter) — the type that buys configuration and not a dialect

**D1 — `openrouter` is a provider *type* over the same `IUpstreamDialect`.**
`ProviderDispatcher.Dialect` hands both it and `openai-compatible` to `OpenAiUpstreamClient`, and
that identity is the phase's claim. The type buys three things a hand-typed `BaseUrl` cannot: a
default base URL (still overridable — a proxy in front of a vendor is a deployment somebody has),
the attribution headers, and a `ModelMap` checked for OpenRouter's id shape at startup.
**Rejected: a generic `Headers:` map on every provider** — the smaller diff, a place for an operator
to put a second `Authorization` beside the one this code sets, and it leaves "which vendor is this"
unanswerable in the `type` field `/api/status` already reports.

**D2 — Attribution is opt-in and this hub never picks a value for it.** `Referer` → `HTTP-Referer`
and `Title` → `X-OpenRouter-Title`, sent **only** when the operator wrote one down and **only** to an
`openrouter` provider. They put an app on OpenRouter's *public* rankings, so defaulting them to this
product's own name and URL would be free marketing paid for with somebody else's deployment
appearing on a vendor's public page because they configured a model. Not the caller's content — a
fact about the caller's infrastructure, published, which is rule 7's spirit rather than its letter.

**D5 — The id shape is checked at startup, against nothing but itself.**
`ProviderOptionsValidator.OpenRouterModelId` — `vendor/model`, optional `~` alias prefix, optional
`:variant`. `gpt-4o-mini` is a real OpenAI id, has never been an OpenRouter one, and left to run it
is a 400 discovered weeks later on the one request the fleet could not serve. **Rejected: validating
against the live `/models` listing** — that makes booting depend on a vendor being up, and the track's
D4 says a listing may never create a route. **Rejected: a checked-in vendor list** (48 D1). *The risk
is stated rather than mitigated:* an unnamespaced id shipping there would refuse a valid config, and
that is a one-line fix behind a message that says what it wanted.

**D6 — This file's budget is what split `Vector/` out**, above. The two dialect fixes (a numeric
`error.code`, a mid-stream error frame) are the shared library's and are recorded in
`src/InferHub.Shared/CLAUDE.md`.

**Rule 5 survived again.** Phase 62 added **zero** new dependencies.

### Phase 60 — `/api/status` accepts an admin key, and the console actually sends one

The fleet view was guarded by the **client** scope alone, and `console.js` sent **no credential at
all** on it. Both halves had to be wrong for the console to work anywhere, and it did — on a
`dotnet run` hub, where the loopback exemption covers it. Inside a container the hub sees the bridge
gateway, so every containerised deployment got `401` on the poll every panel hangs off.
`BearerApiKeyMiddleware` now also accepts an admin key **on that one read-only path**, which grants
nothing new (an admin key already reads `/api/admin/nodes`, which carries more), and
`AnAdminKeyStillCannotRunInference` is what keeps the widening from spreading.

### Phase 63 (Anthropic) — the config half; the dialect itself is 63 D1/D3–D7 in `src/InferHub.Shared/CLAUDE.md`

**D2 — `MaxTokens` is declared per provider, because the vendor requires a field Ollama has none
for.** Anthropic 400s a request without `max_tokens`; an Ollama client sends `num_predict` only when
somebody set it. So the ceiling is config (default **4096**), a caller's `num_predict` always wins,
and `stop_reason: max_tokens` comes back as Ollama's `done_reason: length` — visible, not silent.
**Rejected: a constant in the code** — the per-model ceiling differs, and a number an operator
cannot see is a number they cannot raise when a long answer arrives truncated. **Rejected: refusing
a request that names none**, which is every Ollama client. This is 48 D1 the other way round: the
value is *declared* rather than detected, because absent is not an option the API allows.

**D8 — There is deliberately no id-shape check for `anthropic`, and that is not an inconsistency
with 62 D5.** OpenRouter's check is one namespace with 419-of-419 evidence. The same Anthropic model
is `claude-opus-5` first-party, `anthropic.claude-opus-5` on Bedrock and `claude-…@2025…` on
Vertex — and a `BaseUrl` override is exactly how somebody reaches the latter two, which this project
encourages. A `claude-` prefix check would refuse a valid configuration, which is 48 D1's "usually
right" wearing 62's clothes. What still runs is 61's: the type is known, the URL is absolute, the
timeout is positive, `MaxTokens` is positive, and no model is claimed twice.

*The credential is part of the dialect*, so `CreateHttpClient` asks the dialect to configure itself
rather than setting a Bearer token for everyone: `x-api-key` plus a required `anthropic-version`,
and **never an `Authorization` header** — a Bearer token here is a 401 that reads like a bad key.
`AnUnknownTypeFailsStartupNamingTheTypesThatExist` used `anthropic` as its example of an unknown
type until this phase made it real; the example moved to `bedrock` and the assertion now checks all
three names.

**Rule 5 survived again.** Phase 63 added **zero** new dependencies.

### Phase 64 (Gemini) — the config half; the dialect itself is 64 D1–D5, D7–D9 in `src/InferHub.Shared/CLAUDE.md`

**D6 — `ThinkingBudget` is declared per provider, optional, and is the lever that makes the bill
legible rather than an arithmetic that hides it.** Gemini models think by default; the thinking
tokens are **billed as output** and reported separately from the answer's, so `eval_count` (which
carries `candidatesTokenCount` alone, because a client reading that field means "tokens in the
answer I received") is smaller than the output on the invoice. The knob is the honest fix: absent
leaves the vendor's dynamic default, `0` disables thinking on the models that allow it, and the
validator refuses a negative one at boot rather than on the first prompt. **Rejected: defaulting it
to `0`** — turning off a model's reasoning is a quality decision, and making it silently for an
operator who never asked is a worse surprise than the bill. **Rejected: folding the thinking count
into `eval_count`** to make the two agree, which is 64 D6 in the shared file.

*No `MaxTokens` for `gemini`, deliberately.* 63 D2 exists because Anthropic **requires**
`max_tokens` and Ollama has no equivalent; Gemini requires nothing, so imposing a declared ceiling
would truncate answers that would otherwise finish. `maxOutputTokens` travels only when a caller
sends `options.num_predict`. Two vendors, one field, opposite answers — which is the argument for
per-provider config rather than a hub-wide one.

**D2 (the config side) — there is no id-shape check here either, and for a third distinct reason.**
62 D5 validates a shape, 63 D8 validates nothing, and **64 normalizes in the translator**: the
Gemini model id is a URL path segment, so `gemini-3-pro`, `models/gemini-3-pro` and a Vertex
`publishers/google/models/…` are all legal and the difference is structural rather than a matter of
taste. The rule the three phases add up to: *check where the vendor's namespace is evidence,
normalize where the id is structural, and never check where a `BaseUrl` override can legitimately
change it.* What still runs is 61's — the type is known, the URL is absolute, the timeout is
positive, and no model is claimed twice.

*The credential is part of the dialect* for the third time: `CreateHttpClient` hands a `gemini`
route to `GeminiUpstreamClient.Configure`, which sets `x-goog-api-key` and **never an
`Authorization` header** — a Bearer token here is the same 401-that-reads-like-a-bad-key 63 D1
named at Anthropic. `AnUnknownTypeFailsStartupNamingTheTypesThatExist` now checks all four names.
Gemini is also the first provider whose request body carries **no `model` field at all**, which the
dispatch test asserts: `RewriteModel` rewrites nothing on the way out, and the answer is still
relabelled with the name the caller used.

**Rule 5 survived again.** Phase 64 added **zero** new dependencies.

### Phase 65 (providers become routable) — load-bearing; this is where "fallback" stops being the mechanism

**D1 — `Policy` is the one word for *when* a provider serves, and it subsumes `Trigger`.**
`no-node` (default), `no-node-or-saturated`, `prefer` (asked first, fleet as backstop) and `only`
(asked always; a node holding that name never serves it). `Trigger:` keeps binding and is read as the
policy when `Policy:` is absent, so v3.29–v3.32 config is untouched — and `ProviderDefinition.Trigger`
became **nullable** so the validator can tell a value somebody wrote from a default nobody chose.
**Both present and disagreeing fails startup naming both** (61 D1's posture). **Rejected: a
`Preferred: true` boolean beside `Trigger`** — four combinations of two knobs, two of them nonsense.
**Rejected: new values under the name `Trigger`** — a field called *trigger* whose value is "always,
first" is a lie in a config file.

**D2 — `ModelPolicy` overrides it per model, and a policy for an unmapped model fails startup.** One
credential serves models an operator feels differently about; the alternative is declaring the vendor
twice and copying the key, and a credential written down twice is a credential rotated once.

**D3 — The backstop is the policy's answer, not the error's.** `prefer` and a saturation burst may
fall back to a node when the upstream fails (falling back to the local fleet is not a second
disclosure); `only` and a steered request may not, and get a **502** naming the situation. Answering
from different weights than the caller asked for, silently, is the one failure that looks like a
success.

**D4 — `X-InferHub-Provider` steers one request and can only narrow.** `<id>` serves from that
provider **iff it already claims the model**, else `400` before anything leaves the hub — a steer can
never create a route the config does not contain (track D4). `node` refuses every provider for this
request, including an `only` one, and is the direction that matters: one prompt kept off somebody's
servers without an operator editing config. **The refusal is one sentence for an unknown id, a
disabled provider and a real-but-wrong one**, so a client with a key cannot enumerate the operator's
vendors by probing; `/api/status` answers that and is admin-gated. `fallback` is a header *value*
(61 D4), not a steerable id. **Rejected: a body field** — the body is forwarded to the upstream, and
a routing directive inside a payload is a field a vendor will one day interpret.

**D5 — `/api/tags` and `/v1/models` list what a client may call, and never who will serve it.**
[ModelDiscovery](src/InferHub.Coordinator/Endpoints/ModelDiscovery.cs) merges the fleet's models with
`IProviderRegistry.ClaimedModels`; a name both hold appears once and **the node's entry wins**,
because `digest` and `size` are facts about a file on a box. A provider-only entry carries **null**
for both (v3.13.1's lesson) and `["chat"]` for capabilities — `EmbeddingDispatcher` has no provider
arm, so listing `embed` would be a promise answered with a 404. `owned_by` stays `inferhub`.
**The projected `Fallback:` provider is deliberately absent**, exactly as it is from `/api/status`'s
array, which is what keeps a v3.28-configured hub byte-identical. **Rejected: a `Discoverable` flag**
— a model a client can call and cannot see is the defect this phase removes.

**D7 — `/api/status` reports `policy` and no longer reports `trigger`.** Two spellings of one thing
on a status payload is how a dashboard ends up believing whichever key it read first; the array is
`null` for every deployment that never wrote the block and has no console panel until 66, so this is
the cheapest the rename will ever be. `modelPolicies` appears only where there are overrides.

*Threaded through `InferenceCore` as one `ProviderDecision` rather than three booleans*, so both
client dialects and both failure paths read the same answer — `ShouldServe` is gone.

**Rule 5 survived again.** Phase 65 added **zero** new dependencies.

### Phase 66 (console, metrics and docs for the provider track) — load-bearing

**D1 — The Cloud providers panel is fed by `/api/status` alone, and it is the one panel that stays on
the page when it is empty.** Every other panel hides; this one renders *No cloud provider is
configured — nothing leaves your machines.* That sentence is the feature (22 D5's question, answered
where somebody is already looking), and a panel that vanishes when the answer is the reassuring one
teaches an operator to read absence as "I could not tell". **Rejected: a new admin route** — the data
is in the poll already, and a second surface is a second thing to keep in step.

**D2 — The console draws the projected `Fallback:` upstream as a row; the payload still does not
carry it.** 61 D2 and 65 D5 keep it out of `providers[]` so a v3.28-configured hub is byte-identical,
and that is unchanged — `console.js` synthesizes the row from the `fallback` block it already reads
and marks it `legacy`. Its `credential` cell is a dash on purpose: **the legacy block gains no key
for this panel**, because a new field there would land in the payload of every deployment that
changed nothing. `TheLegacyUpstreamIsNotAProviderInThePayloadEvenThoughTheConsoleDrawsItAsARow` is
the guard against somebody tidying the projection into the array.

**D3 — A failed dispatch is counted per provider and the vendor's own sentence is kept — one, in
memory, admin-gated.** `inferhub_provider_failed_total{provider}` plus `failed` / `lastError` /
`lastErrorAtUtc` on the status block. **`inferhub_requests_failed_total` is deliberately not
incremented**: a `prefer` provider that fails is usually followed by a node answering successfully,
and one request must not fail twice in one number. **Rule 7, argued rather than assumed:** an error
message is a vendor's sentence *about* a request, but nothing stops a vendor quoting a prompt inside
one — so it is treated as content, held once per provider, never persisted, never a metric label, and
reachable only through the admin-gated payload. **Rejected: a ring of recent errors**, which is a log.

**D4 — A provider with no credential is a needs-attention row, not a startup refusal.** The validator
has never demanded an `ApiKey` and must not: an `openai-compatible` endpoint on your own network
legitimately has none. Enabled, mapping models and keyless against a vendor is the purest "I turned
it on and nothing happened" (45 D1), so the strip carries it, alongside a failing provider and — named
separately — a failing `only` one, whose models have no backstop by construction. The strip's second
column is **Where** rather than Node since this phase, because half its rows now name a vendor.

**D5/D6 — `inferhub_provider_info` describes; `inferhub_provider_refused_total` counts and carries no
label.** An info series with a constant 1 measures nothing, so 28 D5 does not reach it — and it is
what makes the absence of `inferhub_provider_dispatched_total` legible, since without it *no vendor
configured* and *a vendor that has served nothing* are the same silence. No key, no base URL (they
carry tokens in query strings in the wild) and no model names (cardinality) in its labels. The
refusal counter has **no label at all**: the id a caller steers at is text they chose, so labelling it
lets anyone with an inference key mint unbounded series, and labelling it with the provider that
*does* claim the model rebuilds by scrape the enumeration 65 D4 refused to expose by probing. It is
emitted at zero like the other hub-wide counters — a hub with no provider can still refuse a steer.

> **A `# HELP` belongs to the metric *family*, not to the row — and the second one rejects the whole
> scrape.** `Info` writes its own header, so calling it in a loop emitted a duplicate header for
> `inferhub_provider_info` (since v3.34.0) and `inferhub_provider_last_model` (since **v3.29.0**);
> Prometheus refuses the entire endpoint, so every InferHub series left the dashboard the moment an
> operator configured a **second** provider — the configuration this whole track exists to make
> possible. Fixed in **v3.35.1**: one `Header` per family, `Sample` per row, as the
> `inferhub_node_vram_*` families have done since 48. Every provider test declared one provider, and
> the in-test reader overwrote a duplicate silently; **`Exposition.Parse` now fails on a repeated
> header for any name**, which is the half that guards the families nobody has written yet. Found by
> scraping a published image with two providers on it (phase 68), not by a suite.

### Phase 69 (the hub routes on backend health) — load-bearing; the node's half is 69 D4 in `src/InferHub.Node/CLAUDE.md`

**D1 — `Heartbeat` carries a typed `BackendHealth?` and an unhealthy node stops being a candidate.**
The heartbeat *is* the liveness message; health is what liveness had been pretending to be since
phase 9. **Rejected: the hub probing the node** — that is 26 D1 undone and the whole NAT story with
it. **Rejected: a second `ReportBackendHealth` mailbox** — deliverable independently of the signal it
describes, so the two can disagree. Three states, not a boolean: `unreachable` is a server to start
and `wedged` is one to stop first, and that difference is the only part an operator acts on.

**D2 — "Who holds this model" and "who can serve it" became two questions.**
`FindNodesWithModel` filters unhealthy nodes by default; three callers ask about **possession** and
pass `includeUnserviceable: true`. Each has its own reason: **placement** must not pull twenty
gigabytes onto a second box to replace a model that is already on a sick one; **`ModelDiscovery`**
must not let a model vanish from `/api/tags` and reappear as a provider's, because `digest` and
`size` are facts about a file on a box (65 D5) and stay true while it is down; **`KnownToTheFleet`**
is what separates "no such model" from "cannot serve it right now". `RequestQueue` keeps the default
— waiting for a slot on a node that cannot answer is a queue that has stopped meaning anything.

**D3 — Every holder unhealthy is a `503` naming the backend, and never the `404` that means "no such
model".** 40 D5's line, extended, in the order the fixes go in: nobody holds it → `404`; holders
exist and all are unhealthy → `503` + `Retry-After`; otherwise the capability `503`. **This is the
defect the phase exists to remove and it was live**: 36 D7 unroutes a broken node by *emptying its
model report*, which at the hub is indistinguishable from an empty box — so a fleet whose only
`llama3` node had a dead Ollama told the client the model did not exist, sending an operator to pull
weights that were already on the disk.

**D5 — `null` is "no opinion" and is never read as unhealthy.** 40 D1's mixed-fleet rule for the
seventh time, and here it is the one that would make a release notorious: a v3.35 node, a node with
`Watch: false` and a vendor-typed node all send nothing, and an upgrade that read that as sick would
empty the fleet. Pinned across the wire, with the real three-field payload, in `Tests.Mesh`.

**D6 — A heartbeat still does not wake the console; a transition does.** `Touch` has never raised
`Changed` and must not start: it runs every few seconds and would re-render every panel per node per
interval to deliver a value that changes twice a week. It now raises only when the recorded state
actually differs.

**D7 — `inferhub_node_backend_health{node,state}` at a constant 1, and a node with no opinion emits
nothing.** 45 D2's `inferhub_profile_state` shape and 28 D5 for the eighth time —
`backend_health{state="healthy"} 0` reads as a measurement that came back bad, which is exactly what
a node that said nothing has not made. The console shows it beside `online` rather than instead of
it (the connection genuinely *is* up) and puts the node on the **Needs attention** strip with the
sentence, not the status word.

> **The empty model report was still arriving, and it undid D3 sixty seconds later — found by
> running the published 3.36.0 image, not by any test.** A node whose backend is down reports zero
> models (36 D7), which used to be the only thing unrouting it; that report kept landing, so the
> model left the registry one refresh interval after the backend died and the refusal reverted to
> the `404 model not found` this phase exists to remove. **Measured: the 503 held for six
> seconds.** Fixed in **v3.36.1** — `ReportModels` **holds** an empty list from a node that has
> declared an unhealthy backend, keeping the inventory the refusal needs in order to name the model
> and the reason, while the health field goes on doing the unrouting. The decision is entirely
> hub-side, so a node older than v3.36 never reaches it and behaves exactly as it always did. The
> stale list is replaced wholesale on recovery, never merged.

**D8 — This file was at 1076 of 1100, so phase 32 moved whole into
`src/InferHub.Coordinator/Cluster/CLAUDE.md`.** 62 D6 and 67 D6, a third time, with the same
arithmetic: a phase cannot land its decisions in a file with twenty-four lines of headroom. The
cluster phase is the largest coherent subtree backend health has nothing to do with, and it moved
**unedited**. **Rejected: raising the budget** — a limit raised on first contact is not a limit
(52 D5) — and **rejected: compressing phase 32**, which is a record and not a draft.

**Rule 5 survived again.** Phase 69 added **zero** new dependencies, and one plain enum moved into
`InferHub.Shared`, which is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.
