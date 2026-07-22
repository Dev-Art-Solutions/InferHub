# CLAUDE.md

Guidance for Claude Code when working in this repository. Keep it focused on what is
non-obvious — README.md has the user-facing pitch and config reference.

## What this is

InferHub is a self-hosted, Ollama-compatible inference mesh. A **coordinator** runs on an
always-on, GPU-less host and exposes Ollama-shaped HTTP endpoints; **nodes** run on GPU
machines, reach out to the coordinator over SignalR, and execute prompts against a local
inference backend (Ollama today, pluggable). No port forwarding on the node side.

## Solution layout

```
src/
  InferHub.Shared/        Contracts + Ollama DTOs + OpenAI DTOs/translators (both ends speak it).
  InferHub.Coordinator/   ASP.NET Core web app (Sdk.Web). HTTP + SignalR hub + routing.
  InferHub.Node/          Worker service (Sdk.Worker). SignalR client + backend driver.
  InferHub.Node.WindowsService/  Windows-service host. References InferHub.Node, adds AddWindowsService + install scripts.
tests/
  InferHub.Tests/         xUnit. References all three projects.
deploy/
  docker/                 Compose stack (coordinator + node), Postgres overlay, runbook.
  postgres/               Postgres+pgvector for the gated integration tests.
  windows/                Node-as-a-Windows-service install scripts.
.github/workflows/        CI: build+test and docker image build on PRs; GHCR publish on v* tags.
plan/                     Phase build-briefs. Not shipped; lives in repo for context.
tools/
  InferHub.Eval/          Retrieval eval harness (phase 24). Standalone console, no project refs, NOT in the images.
```

- TFM: `net10.0`, `Nullable enable`, `LangVersion latest` — set in [Directory.Build.props](Directory.Build.props).
- Solution version is also in `Directory.Build.props` (`<Version>`); bump it when tagging a release.
- `InferHub.Coordinator` has `InternalsVisibleTo("InferHub.Tests")` — tests can reach internals.

## Build / test / run

```powershell
dotnet build InferHub.sln
dotnet test  tests/InferHub.Tests

# two terminals for a local end-to-end:
dotnet run --project src/InferHub.Coordinator    # http://localhost:5080
dotnet run --project src/InferHub.Node           # talks to Ollama on :11434
```

Loopback skips auth by default (`Auth:RequireAuthForLoopback=false`) — local curl just
works. Set keys via env vars or user-secrets (`dotnet user-secrets`); never commit secrets
into `appsettings.json`.

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
  toolchain. If you reach for a bundler, stop and rethink.

### Vector providers

`IVectorStore` ([Vector/IVectorStore.cs](src/InferHub.Coordinator/Vector/IVectorStore.cs)) is
the seam; two implementations sit behind it, selected by `VectorStore:Provider` and wired in
[Vector/VectorStoreServiceCollectionExtensions.cs](src/InferHub.Coordinator/Vector/VectorStoreServiceCollectionExtensions.cs)
(the single composition root — `Program.cs` and the DI-shape test both go through it):

- **`LocalVectorStore`** — raw store on disk + in-memory `FlatIndex`, replicated to nodes.
- **`PostgresVectorStore`** ([Vector/Postgres/](src/InferHub.Coordinator/Vector/Postgres/)) —
  table-per-collection over pgvector; publishes the two lifecycle events itself and returns the
  same score sign-conventions as `FlatIndex` (see `PostgresSchema.ScoreExpression`).

Hard rule: **`ReplicationCoordinator` and `HealingService` bind to `LocalVectorStore`
concretely** (they subscribe to its `CollectionCreated` / `RecordUpserted` events). Do **not**
widen them to `IVectorStore` — that would drag replication concerns into the interface. Under
`postgres` they are simply not registered; `VectorCompositionTests` fails if anyone re-couples
them. That's why `PostgresVectorStore` publishes `vector.collection.created` / `.dropped`
itself, and why `NullVectorQueryRouter` replaces the node-serving router.

## Node anatomy

- [NodeHostBuilderExtensions.cs](src/InferHub.Node/NodeHostBuilderExtensions.cs) —
  `AddInferHubNode(this IHostApplicationBuilder)` is the **shared composition root**. Both
  the console host ([InferHub.Node/Program.cs](src/InferHub.Node/Program.cs)) and the
  Windows-service host ([InferHub.Node.WindowsService/Program.cs](src/InferHub.Node.WindowsService/Program.cs))
  wire their services through it, so the two hosts can never drift. New node DI
  registrations go here, not in either `Program.cs`.
- [Program.cs](src/InferHub.Node/Program.cs) is a two-liner over `AddInferHubNode`. The
  extension binds three typed options sections (`Coordinator`, `Node`, `Ollama`) with
  `ValidateOnStart`; a bad value fails the host with the offending key name. Backend
  selection is a `switch` on `Backend:Type`.
- [Configuration/](src/InferHub.Node/Configuration/) holds the options classes,
  validators, and the model-filter helper.
- [Backends/](src/InferHub.Node/Backends/) — `IInferenceBackend` abstraction, two
  implementations. `OllamaBackend` drives the
  [OllamaClient](https://github.com/Dev-Art-Solutions/OllamaClient) NuGet package.
  `OpenAiBackend` (phase 22, `Backend:Type=openai`) drives anything speaking the OpenAI wire
  format — vLLM, llama.cpp's server, LM Studio, TGI, a hosted provider — via the shared
  `OpenAiUpstreamClient`. `IInferenceBackend.Endpoint` is what the node reports at
  registration: before phase 22 it hard-coded `Ollama:Endpoint`, so an OpenAI-backed node would
  have advertised `localhost:11434` while talking to something else entirely.
- [CoordinatorConnection.cs](src/InferHub.Node/CoordinatorConnection.cs) owns the
  SignalR client, heartbeat loop, model-refresh loop, and reconnect delay — all driven
  by `CoordinatorOptions`, not constants.

## Design rules to preserve

These come from `plan/00-overview.md` and the way the code is shaped today. Treat them
as load-bearing:

1. **Pluggable backends stay backend-agnostic.** Anything Ollama-specific belongs in
   `Backends/OllamaBackend.cs` or behind `IInferenceBackend`. Do not let
   `OllamaClient` types leak into `Worker`, `CoordinatorConnection`, or the coordinator.
2. **Core stays host-agnostic.** No ASP.NET types in node config or shared contracts.
   `InferHub.Shared` is a plain class library.
3. **Build-free UI.** Static assets only. Reusing CSS variables across `status.html` and
   `console.html` is intentional.
4. **No persisted state, *except* the vector store.** Registry, affinity, audit log, and
   metrics are all in-memory; a coordinator restart still resets the fleet view. The
   one exception is the vector store, which now has **two providers** selected by
   `VectorStore:Provider` (`local` default, `postgres` since phase 20):
   - **`local`** — the phase-13 shape. Vector records persist to `VectorStore:DataDirectory`
     as a plain raw store (append-only ops log + periodic compacted snapshots), the in-memory
     index is rebuilt from it on startup, and (phase 15) assigned node replicas persist under
     `Vector:ReplicaDirectory`. The hub's raw store is authoritative; node replicas are derived.
   - **`postgres`** — an **external** durable store (PostgreSQL + pgvector). The coordinator
     holds **no** vector state on disk, and **node replication / self-healing are deliberately
     off** because Postgres is already the source of truth. Pushing a second derived copy onto
     the fleet would be a second write path and a second truth.

   The invariant that survives both: **one source of truth per deployment, and node replicas
   are only ever derived from it — never a second authority.** Everything else stays in-memory;
   if you find yourself adding a database or on-disk format outside those directories/providers,
   stop and rethink. The default is `Enabled=false`, so deployments that don't opt in keep the
   original no-persistence contract unchanged.

   **Second recorded exception (phase 25): the usage ledger**, when `Usage:Persistence=postgres`.
   Deliberately its **own** connection string, not coupled to `VectorStore:Postgres` — a
   deployment may want durable usage without a Postgres vector store, or the reverse. The "one
   source of truth" rule is not violated because usage records are **append-only facts about work
   already done**, not a second copy of any live state. If the ledger ever starts being *read* to
   drive behaviour (routing, admission, anything), that reasoning has stopped being true and the
   design has drifted — stop. (Admission windows are fed in-memory by `UsageMeter`, never from
   the ledger.) Default is `none`: in-memory, reset on restart, like every other counter.
5. **No new heavy dependencies.** The dependency surface is deliberately minimal (ASP.NET Core,
   SignalR, OllamaClient on the node, xunit for tests). There are exactly **two** recorded
   exceptions, both coordinator-only, both feature-scoped, both inert unless the feature is on:
   - **`Npgsql` + `Pgvector`** (phase 20) back the `postgres` vector provider. No connection is
     opened unless `VectorStore:Enabled=true` **and** `VectorStore:Provider=postgres`.
   - **`PdfPig`** (phase 23) backs PDF text extraction. It lives behind `IPdfTextExtractor`, is
     referenced by exactly one file
     ([PdfTextExtractor.cs](src/InferHub.Coordinator/Ingestion/PdfTextExtractor.cs)), and no code
     path reaches it unless a PDF is actually uploaded. Hand-rolling a PDF text-layer parser is a
     bad use of a week; taking a second-rate dependency into `InferHub.Shared` would be worse.

   Neither is in `InferHub.Shared` or `InferHub.Node`, and the rule still holds for everything
   else. Add packages reluctantly, and record them here when you do.
6. **The node-facing job protocol is Ollama-shaped. Client-facing and upstream-facing
   dialects are translations at the boundary.** `InferenceJob.RawJson` crossing SignalR and
   `InferenceChunk.ResponseJson` coming back are both Ollama JSON, always — that is the one
   shape the mesh's internals (dispatcher, router, affinity, retrieval) know. Request/response
   DTOs in `InferHub.Shared/Ollama/` track what real Ollama clients send; do not invent custom
   fields when Ollama already has one. Phase 21 added a second *client-facing* dialect and
   phase 22 a second *upstream-facing* one; both are translations at the edges, and neither
   changes what crosses the wire between coordinator and node.
7. **Conversations carry no content on the coordinator.** Clients re-send full history
   each turn; the coordinator stores only routing affinity keyed by either the
   `X-InferHub-Conversation` header or a hash of the opening message. **Phase 18
   inline retrieval preserves this**: the augmented request body is assembled
   in-flight inside the retrieval pipeline and forwarded to the node — nothing about
   the message or the retrieved context is retained on the coordinator.

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
their metadata**, and [DocumentIndex](src/InferHub.Coordinator/Ingestion/DocumentIndex.cs) is the
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

### Phase 24 (hybrid search, reranking, eval harness) — also load-bearing

**D1 — Keyword search is provider-native; zero new dependencies.** Under `postgres` it is a
`tsvector` generated column (`to_tsvector('english', payload text)`) with a GIN index and
`ts_rank_cd` — Postgres full-text search, which `Npgsql` already reaches. Under `local` it is
[InvertedIndex](src/InferHub.Coordinator/Vector/InvertedIndex.cs), a BM25 (`k1=1.2, b=0.75`)
dictionary that sits beside the `FlatIndex` in every collection. **`InvertedIndex` is derived, never
authoritative** — rebuilt from the raw store on startup and updated on every upsert/delete under the
same collection write-lock as the `FlatIndex`, exactly so the two can never diverge. Pulling in
Lucene to rank a few thousand chunks would be the wrong trade. Rule 5 survived again: `SearchKeywordAsync`
is the seam, both providers implement it, and nothing new was added.

**D2 — Fusion is Reciprocal Rank Fusion, not score blending.**
[HybridSearch](src/InferHub.Coordinator/Vector/HybridSearch.cs) fuses the two result lists by *rank*
(`Σ 1/(60+rank)`). Vector distances and BM25 scores live on different scales that no fixed constant
reconciles across corpora; normalising them is a corpus-specific guess dressed up as sophistication.
The fused RRF score replaces the branch's native score on the returned `VectorMatch`.

**D3 — The keyword branch is always hub-local, and that is recorded, not silent.** Node replicas
(phase 15) serve *vector* reads only. So in `hybrid` mode the vector branch may be served from a node
replica while the keyword branch runs against the hub's `InvertedIndex` — the pipeline logs this
rather than quietly dropping to vector-only. It never returns vector-only results while claiming to be
hybrid.

**D4 — Reranking reuses a fleet model; every failure keeps the original order.**
[LlmReranker](src/InferHub.Coordinator/Vector/LlmReranker.cs) (behind
[IReranker](src/InferHub.Coordinator/Vector/IReranker.cs), the one implementation) hands the top
candidates to a chat model already on the fleet with a scoring prompt and reorders by the parsed
scores. No node, a timeout (`RerankTimeoutSeconds`), an unparseable answer, a wrong-length score
array — **all return the candidates untouched**. A reranker that can break retrieval is worse than
none. Rule 7 holds: the query and candidate text pass through in flight, nothing is retained. A
dedicated cross-encoder (Cohere/Jina/TEI) fits behind `IReranker` later — the seam is built, with one
implementation.

**D5 — Retrieval mode and rerank are per-request, defaulting to pre-v2.6 behaviour.**
`X-InferHub-Retrieve-Mode: vector|keyword|hybrid` and `X-InferHub-Rerank: true`; unknown values are a
`400`, not a silent fallback. `Retrieval:Mode` defaults to `vector` and `Retrieval:Rerank` to `none`,
so a deployment that sends no headers and changes no config behaves **byte-identically to v2.5** — a
feature that silently changes existing results is a regression wearing a feature's clothes.
`RetrievalPipelineTests` asserts the default equals vector-only, and the exact-term case (an error
code) that vector search misses and hybrid recovers.

**The eval harness ships with the feature.** [tools/InferHub.Eval](tools/InferHub.Eval) is a standalone
console tool (no project references, **not** built into the images) that runs a golden set against a
live coordinator in every mode and reports Recall@k / MRR / nDCG@k / latency via the phase-24 search
endpoint `POST /api/collections/{c}/search`. "Hybrid improved retrieval" is an empirical claim; this is
how it is measured. Its README carries the load-bearing warning: a golden set generated by the model
you are about to evaluate is a mirror, not evidence.

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

**D1 — Model management is a hub → node command, not a new API on the node.** The node has no
inbound surface and never will — that is the whole point of the outbound SignalR design. Model
commands travel down the existing connection: the coordinator sends `ExecuteModelCommand` to a node,
and the node streams `ModelCommandProgress` back via `StreamModelCommandProgress` — a client-to-server
stream, so like `StreamChunks` **it must never declare a `CancellationToken` parameter** (same binder
trap; use `Context.ConnectionAborted`). Nothing about the NAT story changes.

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

### Phase 29 (vision passthrough) — also load-bearing

**D1 — Text and images are one array on one side and two fields on the other, so the
translator splits rather than joins.** `RequestTranslator.ExtractContent` returns a
`MessageContent(Text, Images)` pair instead of a joined string; `image_url` parts become
Ollama's base64 `images` array. `UpstreamTranslator` mirrors it for `Backend:Type=openai`
nodes. A message with no images emits **no** `images` key and goes upstream as a plain
`content` string — a stray empty array on every ordinary message would be a wire change for
requests that have nothing to do with vision.

**D2 — `data:` URLs only; the coordinator does not fetch remote images.** An `http(s)`
`image_url` is a `400` that says why. Fetching a caller-supplied URL makes the hub an SSRF
proxy and pulls third-party bytes through a hop designed to retain nothing (rule 7). Inlining
is one line in every OpenAI SDK, and it is the caller's job. Do not "helpfully" add a fetcher.

**D3 — Media type is sniffed from the magic bytes, never defaulted.** Ollama's `images` are
bare base64 with no type; an OpenAI data URL needs one. PNG/JPEG/GIF/WebP are recognised and
anything else is a clean `400`. Defaulting to `image/png` for bytes that are not a PNG turns a
detectable error into a bad model answer. The signatures are **not** `u8` literals — `0x89` and
`0xFF` are not ASCII, and a UTF-8 literal encodes them as two bytes each, a signature that
matches nothing. That bug was live for one test run; keep the explicit byte arrays.

**D4 — Base64 is validated at the edge.** A node rejecting malformed base64 seconds later, from
behind routing and a queue wait, is a much worse error than a `400` in the translator.

**D5 — No capability registry, deliberately.** A text-only model handed an image errors at the
node, and that refusal is forwarded as-is — a clean `502` carrying the model's own message,
never a `500`. Ollama is the source of truth for what a model accepts; a second list here of
"which models see images" would drift and start lying. *This is a recorded deviation from the
phase brief, which asked for a `400`/`404` shape:* the request was well-formed and it was the
upstream that refused, so `502` is the honest status. What the brief actually cared about — no
`500`s — holds.

**D6 — Node errors are unwrapped before they reach a client, and that is presentation, not
interpretation.** Found live in phase 29: Ollama encodes *its* backend's JSON error as a
**string** inside its own `error` field, so a llama.cpp refusal arrives double-encoded and our
envelope made three layers. A client read `error.message` and got
`{"error":"{\"error\":{\"code\":400,\"message\":\"…\"}}"}` — a wall of backslashes instead of
the one sentence saying what to fix, which is precisely the "useless unknown error" the OpenAI
envelope exists to prevent. `InferenceCore.ReadableNodeError` drills to the innermost message,
bounded at four levels, and lives in the **one** dispatch path so both dialects get it.

It unwraps; it never infers. Nothing is decided from the error *text* and the status code is
untouched — the moment this function starts deciding what an upstream error *means* (mapping it
to a 4xx, sniffing for "unsupported"), it has become the capability registry D5 refused, by the
back door. `NodeErrorReadabilityTests` pins the real captured Ollama payload.

**Rule 5 survived again.** Phase 29 added **zero** new dependencies.

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

**D7 — `CREATE SCHEMA IF NOT EXISTS` is not atomic, and this is the first phase where that is
reachable.** Two coordinators booting at the same instant both pass the existence check and one
dies on `pg_namespace`'s unique index (`23505`; `42P07` for the table). Everywhere else in InferHub
bootstrap happens once on one hub, so the race never fired; here simultaneous startup is the
*normal* case, and an HA pair that crashes half of itself on a cold boot is not HA. The lease
bootstrap retries those two SQL states — the other session winning is success. Found by running
eight contenders against a real Postgres; a single-connection test could not have surfaced it. If
the vector store or the usage ledger ever bootstrap concurrently, they need the same treatment.

**Rule 5 survived again.** Phase 32 added **zero** new dependencies: the lease is `Npgsql`, already
recorded for the `postgres` vector provider, and the standby refusal is `System.Text.Json`.

## Auth model (three independent token sets)

| Scope | Config key | Guards |
|---|---|---|
| Inference clients | `Auth:ApiKeys` (anonymous) / `Auth:Clients` (named, with limits and an optional `Collections` scope) | `/api/generate`, `/api/chat`, `/api/tags`, etc. |
| Admins | `Auth:AdminApiKeys` | Everything under `/api/admin/*` (incl. `/usage`, `/clients`), **and `/metrics`** unless `Metrics:OpenScrape=true`. |
| Node enrollment | `Auth:NodeEnrollmentSecret` | The SignalR hub handshake. |

Tokens are hashed and compared with `CryptographicOperations.FixedTimeEquals` — keep that
pattern for any new key-checking middleware. The loopback exemption is shared (one
`RequireAuthForLoopback` flag covers both `Auth:ApiKeys` and `Auth:AdminApiKeys`).

`/health` is intentionally open so monitoring systems can poll it.

## Phase plans & release cadence

`plan/00-overview.md` indexes the per-phase briefs (`phase-09…12`). Each phase is one
mini-release with a strict shape:

1. Implement scope; keep tests green (`dotnet test`).
2. Bump `<Version>` in `Directory.Build.props` to match the phase's version.
3. Tag `vX.Y.Z` and write release notes (`.claude/release-notes-vX.Y.Z.md` is the local
   convention).
4. Flip the `Status:` line at the top of the phase file from `TODO` to
   `DONE ✓ (vX.Y.Z, YYYY-MM-DD)` and mirror the change in the overview table.

When asked to start a phase, read its plan file first — the scope, file list, and
acceptance criteria are already written.

## Testing notes

- xUnit, `Using Include="Xunit"` is set globally for the test project.
- Tests rely on `InternalsVisibleTo`, so prefer `internal` over `public` for new helper
  types unless a node needs them via the shared contracts.
- `SmokeTests` exercises the wire-up; if you add a new endpoint or DI registration, this
  is the first place a regression shows up.

## Code style

- Records for DTOs; minimal-API delegates over controllers; primary constructors are
  used (e.g., [NodeHub.cs](src/InferHub.Coordinator/Hubs/NodeHub.cs)).
- File-scoped namespaces. No `using` statements at file top inside endpoint mapping
  extensions — collocate them.
- Comments are rare and explain *why*, not *what*. Match the existing tone.
