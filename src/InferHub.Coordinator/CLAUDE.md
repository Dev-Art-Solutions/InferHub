# InferHub.Coordinator — agent context

**Scope: `src/InferHub.Coordinator/`.** The always-on host: HTTP, the SignalR hub, routing,
admission, the vector providers, the cluster lease, `/metrics` and the console.

> **Read the root `CLAUDE.md` first** — the seven design rules bind everything here. The two that
> bite most often in this project are **rule 4** (no persisted state, and its four recorded
> exceptions) and **rule 7** (no content on the coordinator, which now covers prompts and uploaded
> pictures as well as conversations).

## Related context

- The contracts and dialects this host renders: `src/InferHub.Shared/CLAUDE.md`
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

`IVectorStore` ([Vector/IVectorStore.cs](src/InferHub.Shared/Vector/IVectorStore.cs)) is
the seam; three implementations sit behind it, selected by `VectorStore:Provider` and wired in
[Vector/VectorStoreServiceCollectionExtensions.cs](src/InferHub.Coordinator/Vector/VectorStoreServiceCollectionExtensions.cs)
(the single composition root — `Program.cs` and the DI-shape test both go through it):

- **`LocalVectorStore`** — raw store on disk + in-memory `FlatIndex`, replicated to nodes.
- **`PostgresVectorStore`** ([Vector/Postgres/](src/InferHub.Coordinator/Vector/Postgres/)) —
  table-per-collection over pgvector; publishes the two lifecycle events itself and returns the
  same score sign-conventions as `FlatIndex` (see `PostgresSchema.ScoreExpression`).
- **`QdrantVectorStore`** ([Vector/Qdrant/](src/InferHub.Shared/Vector/Qdrant/), phase 33, **moved to
  `InferHub.Shared` in phase 44 D2** so a node assigned a Qdrant corpus runs the same store rather
  than a second one — only `QdrantBootstrapper` stayed behind, as a host concern) —
  Qdrant over a hand-rolled REST `QdrantClient` (no dependency); publishes its own lifecycle
  events and returns the same `FlatIndex` score conventions (Qdrant reports them with no sign flip).
  Since phase 34 it also fuses dense + sparse **server-side** (`IServerSideHybridSearch`) for
  collections created on 3.2+; collections created on 3.1 stay dense-only until migrated. Phase 35
  added collection-creation knobs (quantization, on-disk vectors, payload indexing).

Since phase 35 there is a **migration path between providers**: `tools/InferHub.Migrate` copies a
populated collection from any provider to any other, over `ScanWithVectorsAsync` → `UpsertAsync`. It
is an operator tool, outside the runtime — see "Phase 35" below before changing anything about it.

`postgres` and `qdrant` are **external providers** — `VectorStoreProviderExtensions.IsExternal` is
the one predicate every call site (`Program.cs`, `StatusEndpoint`, `VectorEndpoints`) branches on,
because what matters is external-vs-local, not which external one. Do not reintroduce per-provider
`IsPostgres` checks at those sites.

Hard rule: **`ReplicationCoordinator` and `HealingService` bind to `LocalVectorStore`
concretely** (they subscribe to its `CollectionCreated` / `RecordUpserted` events). Do **not**
widen them to `IVectorStore` — that would drag replication concerns into the interface. Under any
external provider they are simply not registered; `VectorCompositionTests` fails if anyone
re-couples them. That's why the external stores publish `vector.collection.created` / `.dropped`
themselves, and why `NullVectorQueryRouter` replaces the node-serving router.


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

**D4 — Cloud burst stores nothing.** [FallbackDispatcher](src/InferHub.Coordinator/Services/FallbackDispatcher.cs)
forwards the body in flight and streams the response straight through. It is a proxy hop, not
a cache. Rule 7 is load-bearing and this does not dent it: the model name is metered, the
prompt and the answer are not.

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

**One backend implementation, five servers.** `Backend:Type=openai` covers vLLM, llama.cpp's
server, LM Studio, TGI and every hosted provider, because they all landed on the same dialect.
[OpenAiUpstreamClient](src/InferHub.Shared/OpenAi/OpenAiUpstreamClient.cs) is the single place
that speaks it, and **both** the node's `OpenAiBackend` and the coordinator's
`FallbackDispatcher` drive it. Do not grow a second one.

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
returns `true` (pull/delete via `OllamaClient`, warm via an empty-prompt generate); `OpenAiBackend`
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

### Phase 31 (client-scoped collections) — also load-bearing

**D1 — Scoping is an authorization filter over the one vector store, not a store per tenant.**
`Auth:Clients[].Collections` (null/absent = all, exactly as before v2.13) is answered in exactly one
place, [CollectionAccessPolicy](src/InferHub.Coordinator/Auth/CollectionAccessPolicy.cs) — the same
"one place saturation is defined" discipline as `FleetSaturation`. Rule 4 survives untouched: there
is still one collection namespace and one source of truth, and a scope only decides which names a
key may say. Only a **trailing `*`** is a wildcard; a full glob dialect or a regex in a config file
is a footgun aimed at an isolation boundary, and `tenant-a-*` is what provisioning actually needs.

**D2 — Enforcement is a group filter, not a line in each handler.** `RequireCollectionScope()` hangs
off the route groups that carry `{collection}` (`/api/collections/{c}/documents`, the search route,
the `/api/vector/{c}` data plane). The ingestion group alone has five routes, and **the one that
gets forgotten is the isolation hole** — so no path may enforce this inline. Inline retrieval is the
one exception, because it names its collection in a *header*: the check lives in
`InferenceEndpoints.TryReadRetrievalHeader`, the single parser both client dialects already share.

**D3 — Out of scope is `404`, and the check runs before the store is consulted.** Phase-25 D4's
principle: a tenant is not told another tenant's collections exist. The denial is the *same
sentence* a missing collection produces (`collection 'x' does not exist`) at the same status. And
because nothing is looked up first, a name outside a client's scope reads identically whether or not
it exists — so the 404 leaks nothing, it only reflects the caller's own scope back at them.
`CollectionAccessTests` asserts the real-vs-imaginary pair are indistinguishable.

**D4 — A scoped-out `X-InferHub-Retrieve` is an error, not a passthrough.** It does **not** go
through `Retrieval:OnMissing`: answering without the context the caller asked for, silently, is the
wrong failure on a tenancy boundary. `CollectionNotVisibleException` is caught by both surfaces and
rendered as a 404 in each dialect (`collection_not_found` in the OpenAI envelope).

**D5 — Auto-provision on first ingest, for scoped clients only. This is a deliberate reversal of
half of phase 23's refusal, and only half.** Phase 23 declined to auto-create collections for two
reasons: it would guess the dimension, and it would route around the admin scope that owns
collection lifecycle. The second dissolves for a client whose config *names* its collection scope —
that list **is** the provisioning grant. The first does not dissolve, so nothing is guessed:
creation is deferred until the first batch comes back embedded and the dimension is **measured**
from the vectors (`IngestionPipeline.EmbedAndUpsertBatchAsync`). A consequence worth keeping: an
embed that never succeeds leaves **no** empty collection behind for the next caller to misread as
provisioned. An unscoped client keeps the phase-23 contract exactly — `autoProvision: false`.

**Rule 5 survived again.** Phase 31 added **zero** new dependencies.

### Phase 32 (multi-coordinator: standby hub & warm failover) — also load-bearing

**D1 — Standby and active share the *same* Postgres, so rule 4 is untouched.** There is no new
source of truth: the lease row is a mutual-exclusion token, never state anyone reads to answer a
request, and the vector store and usage ledger are the same external stores both hubs already
used. The coordinators are interchangeable readers/writers of one durable store, not two
authorities. Everything else on a hub (registry, affinity, metrics, audit) is *derived* and
rebuilds as nodes reconnect — which is exactly why a promoted standby needs no migration step.
**HA targets `postgres` only.** Under `local` the raw store is per-hub; clustering it is future
work, and `Cluster:Enabled=true` over a `local` store would be two authorities wearing one name.

**D2 — A lease row, not a PG advisory lock.** The obvious alternative was rejected on purpose: an
advisory lock is scoped to a *session*, so a pooled connection dropping silently releases
leadership with nothing to observe, and it carries no expiry and no fence a partitioned holder can
reason about **locally**. [PostgresClusterLease](src/InferHub.Coordinator/Cluster/PostgresClusterLease.cs)
is one conditional upsert — `ON CONFLICT DO UPDATE … WHERE holder = me OR expires_at <= now()`,
`RETURNING` — decided entirely by the database clock, so there is no read-then-write window two
coordinators can both walk through. The fence counter bumps only on a change of holder, never on a
renewal: a bumped fence is how an operator knows leadership actually moved.

**D3 — The split-brain guard is local, and the trade is deliberate.** A partitioned active hub
cannot *be told* it lost the lease — by definition it cannot reach the database that knows. So
[ClusterLeaseService](src/InferHub.Coordinator/Cluster/ClusterLeaseService.cs) demotes when this
instance has not **proved** leadership within the TTL, measured on its own clock from the last
successful renewal. That is the same deadline Postgres uses to hand the lease over, so the two
windows cannot overlap with both hubs serving. The consequence — an unreachable database demotes a
healthy primary after one TTL, taking the mesh down — is correct and is not to be softened: a
request the mesh cannot attribute to a single leader is worse than a `503` a load balancer routes
elsewhere. `Cluster:RenewIntervalSeconds` is validated at ≤ TTL/3 so ordinary packet loss cannot
flap leadership. A clustered hub starts **standby** and is promoted only on a real acquisition;
starting active would give every cold boot a two-primary window.

> **The deadline is checked *before* any I/O, and the attempt is bounded by what is left of it.**
> Found by pulling the plug on Postgres under the running stack: the round-trip itself burned
> Npgsql's connect timeout, so demotion landed at **23s on a 15s TTL** — and the row frees at 15s.
> That 8s gap is a window in which the standby holds the lease and the old primary still believes
> it leads: precisely the split brain the fence exists to prevent. The loop's sleep is clamped to
> the remaining time too, so tick granularity cannot add slack either. A fence that can be
> outrun by its own health check is not a fence, and only running it found that —
> `SplitBrainTests.TheFenceDoesNotWaitForTheRoundTripToComplete` pins it.

**D4 — Node failover is enforced in the middleware, not in the hub, because a `HubException` from
`OnConnectedAsync` does not fail the client's `StartAsync`.** Found live: by the time
`OnConnectedAsync` runs the handshake has completed, so throwing (or `Context.Abort()`-ing) leaves
the node believing it connected, only to be dropped a beat later with no reason attached — it
cannot tell "standby, try the next endpoint" from "hub is broken". So `/hubs/node` is in
[ClusterRoleMiddleware](src/InferHub.Coordinator/Cluster/ClusterRoleMiddleware.cs)'s refusal set and
a standby answers the *negotiate* with the same `503` clients get. `NodeHub` keeps its own check as
defence in depth. **Do not "simplify" the middleware entry away** — the hub check alone does not
work, and `FailoverTests` crosses the real wire precisely so that cannot regress unnoticed.

**D5 — The hub does not become a load balancer; it becomes honest.** Client failover is a TCP/HTTP
LB or DNS in front of both hubs. What InferHub owes that front is signals: `X-InferHub-Role` on
every response, `role` on `/health`, and a `503` + `Retry-After` on inference against a standby, in
the caller's own dialect (OpenAI envelope on `/v1`, per phase 21/29). **`/health` stays `200` on a
standby** — a standby *is* healthy, it just is not leading, and reporting otherwise has an
orchestrator restart-loop the instance that is supposed to be waiting quietly. Drain on the role or
the inference `503`. Unlike phase-25 admission (which lives in `InferenceCore` because it needs the
model name), the role decision needs nothing from the body, so it belongs in the pipeline before
routing, deserialization or a queue wait.

**D6 — What a standby refuses is a short, explicit list, and status is not on it.** Inference,
ingestion, search, the vector data plane and the node hub. `/health`, `/api/status`, `/metrics`,
`/api/admin/*` and the status page stay served, because "why is nothing being served?" has to be
answerable *from* the instance that stopped serving. A standby that goes dark is a standby nobody
can diagnose.

**D7 — `IF NOT EXISTS` is not atomic, and this is the first phase where that is reachable.** The
existence check and the catalog insert are separate steps, so two coordinators booting at the same
instant both pass the check and one dies on a unique index in `pg_extension` / `pg_namespace` /
`pg_class`. Everywhere else in InferHub bootstrap happens once on one hub, so the race never fired;
here simultaneous startup is the *normal* case, and an HA pair that crashes half of itself on a cold
boot is not HA. [ConcurrentDdl](src/InferHub.Coordinator/Postgres/ConcurrentDdl.cs) is the one place
that retries it — the other session winning **is** success — and **all three** Postgres bootstraps
(the lease, the vector store, the usage ledger) go through it.

> **This shipped broken in v3.0.0, in the two paths that were noted-but-not-fixed.** The lease was
> hardened during the phase; the note said "if the vector store or the usage ledger ever bootstrap
> concurrently, they need the same treatment" — and then v3.0.0 tagged without doing it. Pulling the
> published images and cold-booting two hubs against an empty database, `hub-a` exited 139 on
> `pg_extension_name_index` while `hub-b` came up fine, and the error text blamed a missing
> privilege, sending the operator after a DBA for a problem that was a race. Fixed in v3.0.1.
> `ConcurrentBootstrapTests` races eight of each against a real Postgres and fails without the
> retry. **A hazard you have written down but not fixed is still shipped** — and D7 exists because
> that class of thing is only ever found by running the artefact.

**Rule 5 survived again.** Phase 32 added **zero** new dependencies: the lease is `Npgsql`, already
recorded for the `postgres` vector provider, and the standby refusal is `System.Text.Json`.

### Phase 35 (Qdrant in production + cross-provider migration) — also load-bearing

**D1 — The migration tool is an operator action *outside* the runtime, so rule 4 is untouched.**
[tools/InferHub.Migrate](tools/InferHub.Migrate) is a standalone console (Eval discipline: **not**
built into either image). The coordinator never migrates itself and no second store appears at
runtime — a hub that copied itself into another engine would be a second write path and, for as long
as the copy ran, a second truth. Rule 7 holds: the tool moves chunk text and vectors that are already
in a store and retains nothing beyond the copy it is making.

Unlike Eval it **does** reference `InferHub.Coordinator`, and that is deliberate: it stands up real
stores through `AddInferHubVectorStore` — the one composition root — rather than reimplementing three
connectors. A tool with its own copy of "how a provider is built" is the copy that silently rots. It
starts the provider's `IHostedService`s by hand, which is what creates the schema on an empty
Postgres, warms the Qdrant cache, and fails fast with the coordinator's own message when a store is
unreachable. The reference points one way only; nothing ships inward.

**D2 — `ScanWithVectorsAsync` joined `IVectorStore`, and only because a per-id fetch is not a tool.**
`ScanAsync` deliberately omits the embeddings (phase 23 D1), which is right for finding a document's
chunks and useless for copying them. The alternative was `GetAsync` per record — a round trip per
chunk against stores that answer a page in one, which nobody would run on a million chunks. So the
seam grew a twelfth method, implemented by all three providers with **identical filter, ordering and
`afterId` semantics** to `ScanAsync`, and pinned in `VectorProviderParityTests` /
`QdrantVectorStoreTests`: two providers that disagreed here would give a migration between them a
different corpus on the far side. `ScanAsync` stays the default for everything else.

Qdrant keeps phase-33 D4's discipline here — it scrolls by its own UUID point id, so the filtered set
is materialised and sorted by real id before the window is taken — and unpacks the dense floats from
the **named** vector map a hybrid collection returns. A migration that copied an empty `float[]`
because the wire shape changed would be the quietest possible data loss.

> **Qdrant stores the unit-normalised vector under `Cosine`, and the one you sent under `Dot` and
> `Euclid`.** Found by running the new parity arm against a real Qdrant 1.12.4 — `[0.1, 0.9, 0]` came
> back as `[0.1104…, 0.9938…, 0]`. A stub would have echoed what it was handed and this would have
> shipped unnoticed. It is **safe**: cosine is scale-invariant, so rankings and scores are identical
> either way, and `ACosineRoundTripThroughQdrantKeepsTheRankingAndTheScores` pins exactly that.
> But it is real — a cosine collection migrated *out of* Qdrant carries normalised vectors, so
> anyone diffing raw floats across a migration will see different numbers and must not conclude the
> copy is broken. `ScanWithVectorsMatchesTheLocalStore` therefore asserts the honest thing per
> metric: verbatim under `dot`/`l2`, same direction and unit length under `cosine`. If a future
> provider is added, ask what *it* does to a vector on the way in before assuming a byte-for-byte
> round trip.

**D3 — Quantization / on-disk / payload indexing are collection-*creation* options, and are honest
about the trade.** `Quantization` (`none|scalar|binary`), `OnDisk` and `PayloadIndexKeys` are applied
by `CreateCollectionAsync` and **do not touch existing collections** — which is why migrating is also
how a collection adopts them. Quantization is a *memory-for-recall* trade, not a free win, and the
docs say so with a pointer at the eval harness rather than an adjective: a store that ranks
approximately and is described as "faster" is a store that lies about relevance. `PayloadIndexKeys`
indexes `__meta.<key>` — the same path `BuildFilter` writes; an index on any other path would be
built, reported healthy and never used. A refused index is logged and skipped, not fatal: the
collection works without it, just slower, and losing a collection over an optimisation is the wrong
trade.

**D4 — A remote Qdrant with no API key warns; it does not refuse.** Qdrant ships unauthenticated —
fine on localhost, a data leak anywhere else, since the payload holds the chunk *text*. So
`QdrantBootstrapper` warns at startup when the `Url` is non-loopback and `ApiKey` is empty. Not a
hard failure on purpose: a private network with its own controls is a legitimate deployment, and
refusing to boot would be us overruling an operator about their own network. Compare phase-32 D3,
where demoting *was* correct — there the alternative was two hubs both serving, which is a
correctness failure; here the alternative is someone else's risk assessment.

**D5 — The tool refuses a shape mismatch and never deletes.** A target collection that already exists
with a different dimension or distance is **skipped with a reason**, not half-filled: the first fails
per record, and the second would *succeed* and silently rank differently. Records in the target that
are not in the source are left alone — a migration tool that removes data nobody asked it to remove
is a worse failure than one that leaves a stale record behind. Re-running converges rather than
duplicating (deterministic ids, phase 23 D5), so an interrupted run is resumed by running it again.
And the summary reports the **target's own count**, exiting non-zero when it is short: "the upserts
returned" is not the same claim as "the data is there."

**The "no migration path between providers" caveat is deleted, everywhere.** It was true from v2.2 to
v3.2 and was repeated in the README twice, the site and the release notes. A caveat that is no longer
true is as bad as a missing one — if a future change makes it true again, put it back in all of those
places.

**Rule 5 survived again.** Phase 35 added **zero** new dependencies to the shipped projects; the
tool references, and ships nothing inward.

### Phase 44 (hub-assigned retrieval) — also load-bearing

**D1 — Phase-38 D1 is amended precisely, and the invariant it protected is intact. Read this
twice.** The sentence to check any future change against is: **one authority per collection name, and
the hub knows who it is.**

Phase-38 D1 refused to boot a node that was both meshed and holding its own corpus, because such a
node holds hub-derived replicas *and* an authority under the same names, and `ReplicationCoordinator`
would eventually overwrite a collection its operator believed they owned. **That reasoning is not
reversed.** What changed is that the hub can now be the one who *assigns* the corpus, and can
therefore be the one who *knows*:

- [CollectionOwnership](src/InferHub.Coordinator/Vector/CollectionOwnership.cs) records an owner per
  name — `hub` or `node:{id}` — **one place**, the way `CollectionAccessPolicy` and `FleetSaturation`
  are one place. It is re-derived from the profile book on every re-assert and on every node's
  registration pull, so it follows the documents rather than accumulating beside them.
- **A hub-side create of a node-owned name is a `409` naming the owner.** Disjointness is structural,
  not a convention somebody has to remember.
- **Replication and healing bind to it.** `ReplicationCoordinator` skips placement and fan-out for a
  node-owned name; `HealingService` skips it in the sweep and refuses a manual rebuild with the
  owner's name. `VectorCompositionTests` asserts both constructors still take the record — a refactor
  that dropped the parameter would leave every behavioural test passing against a default of
  "everything is the hub's", which is exactly the silent failure this decision is about.
- **A self-configured corpus on a meshed node is still a startup failure.** `LocalRetrievalOptions`
  set by hand plus `Coordinator:Enabled=true` fails exactly as it did in v3.6. The *only* way a meshed
  node holds an authority is that the hub granted it, recorded it, and excluded it from replication.

**D2 — A node runs `local` or `qdrant`, never `postgres` — and phase-33 D2 is why this cost nothing.**
The Qdrant connector was hand-rolled over `HttpClient` with zero dependencies because the official
client is gRPC. That decision, made to protect rule 5 two releases earlier, is what let
`QdrantVectorStore`, `QdrantClient`, `QdrantIdMap` and `SparseVector` **move into `InferHub.Shared`**
— so a node assigned a Qdrant corpus runs the *same* store rather than a second one. The two host
couplings became phase-38 D3's seams (`IVectorLog`, plain options) plus two plain events the
coordinator forwards to `VectorEvents`; `QdrantBootstrapper` stayed behind as a host concern, which
is also why `using InferHub.Coordinator.Vector.Qdrant;` still resolves everywhere it was written.
`PostgresVectorStore` stays on the coordinator, because `Npgsql` is a package and rule 5 names it as
coordinator-scoped — and the node's validator refuses `postgres` **by name**, with that reason,
rather than reporting an unknown value.

**A dependency declined two releases ago is the reason a feature was free today.** `InferHub.Shared.csproj`
is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.

**D3 — The corpus starts and stops at runtime; the routes are always mapped.**
[RetrievalHost](src/InferHub.Node/Retrieval/RetrievalHost.cs) is the **only** thing on a node that
constructs a vector store. DI holds the seams around it and builds nothing until
`StartCorpusAsync` is called, because ASP.NET cannot map an endpoint after the application has
started and a node that restarts itself on a hub instruction is refused by phase-43 D6.

- The retrieval routes are **mapped unconditionally** and answer **501** with the phase-37 D8
  sentence while no corpus runs. `SoloRetrievalTests` records that amendment: they were 404s through
  v3.11. Same shape as tools (41) and audio (42).
- Stopping **drains**: a request already retrieving holds a `CorpusLease` and finishes against the
  store it started on, bounded at 30s.
- **A start that fails leaves no corpus at all** — never a half-started one that answers some
  queries. An `HttpClient` timeout arrives as a `TaskCanceledException`, so the catch distinguishes
  the caller's token from the operation's own timeout; getting that wrong would have turned the most
  ordinary failure (an engine that is slow to refuse) into an exception nobody converts to a refusal.

**D4 — The hub names a credential; the node resolves it. The hub never carries the secret.**
`credentialRef` resolves against `LocalApi:Retrieval:Credentials:{ref}` on the box. An unresolvable
ref is a **refusal naming the key**, never a fall back to an unauthenticated connection. The
alternative — the hub pushing a key down the link — makes the coordinator a secret distributor, puts
credentials in profile persistence and in an admin API response, and hands every node in the selector
a secret it may not need.

**Phase-35 D4 still applies to the node**: a non-loopback engine with no credential **warns and
proceeds**, because that is the operator's own network. The asymmetry with D1's hard refusals is the
line the whole track draws — refuse when the risk is somebody else's, warn when it is theirs.

**D5 — Ingestion and search for a node-owned collection go through the hub, dispatched to the owner.**
The client-facing API stays the hub's; `NodeCorpusDispatcher` sends a `corpus-ingest` /
`corpus-search` job down the connection the node already opened (phase-26 D1 — the hub still never
dials a node), and the node runs the **shared** pipelines against its own store. One API for clients,
no new surface, no second ingestion path, and the hub keeps enforcing phase-31 D2's client scoping
over data it does not hold. An owner that is not connected is a **503 naming the node**, never a
silent fall back to the hub's own store — that would be phase-31 D4's failure, a confident answer
from the wrong data. **PDF is a 415 on a node-owned collection** (phase-38 D5, unchanged): a hub that
extracted the text and shipped chunks would be a second ingestion path with different behaviour.

**D6 — What the hub knows about a node corpus is what the node reported, not what the hub queried.**
`ReportCorpusState` rides the existing model-refresh loop and fires again right after a profile
touches the corpus; `NodeCorpusRegistry` is a mailbox, and `/api/status` reads it. The hub does
**not** query a node's corpus, because that is a synchronous dependency on a box that may be asleep
and `/api/status` has to answer when the fleet does not. A stale block is the honest failure mode,
and the timestamp says so.

**Rule 5 survived again.** Phase 44 added **zero** new dependencies.

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
