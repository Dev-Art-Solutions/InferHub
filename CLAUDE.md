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
  InferHub.Shared/        Contracts + Ollama DTOs + OpenAI DTOs/translators (both ends speak it),
                          and since phase 38 the whole *pure* retrieval core: Vector/ (IVectorStore,
                          LocalVectorStore, InvertedIndex, HybridSearch, RetrievalPipeline) and
                          Ingestion/ (TextExtractor, Chunker, DocumentIndex, IngestionPipeline).
                          Phase 44 added Vector/Qdrant/ (QdrantVectorStore, QdrantClient, QdrantIdMap,
                          SparseVector) — free only because phase-33 D2 hand-rolled the connector
                          instead of taking the gRPC client.
                          Still a plain class library with ZERO package references — see rule 2.
  InferHub.Coordinator/   ASP.NET Core web app (Sdk.Web). HTTP + SignalR hub + routing. Keeps the
                          external vector providers, replication/healing, endpoints, Metrics and
                          PdfTextExtractor.
  InferHub.Node/          Worker service (Sdk.Worker). SignalR client + backend driver. LocalApi/
                          is solo mode (phase 37); Retrieval/ is solo RAG (phase 38). Two
                          Dockerfiles: the plain one (multi-arch, ~340 MB) and Dockerfile.ollama
                          (phase 39 — amd64, ~4 GB, Ollama inside the container).
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
  InferHub.Migrate/       Cross-provider vector migration (phase 35). Standalone console; references the
                          Coordinator to compose real stores through the one composition root. NOT in the images.
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
- [LocalApi/](src/InferHub.Node/LocalApi/) — solo mode (phase 37): the coordinator's client-facing
  surface, served by the node itself. **The only place ASP.NET appears on the node** (rule 2 as
  amended). Since phase 38 it also maps the RAG routes, but only when a corpus is configured.
- [Profiles/](src/InferHub.Node/Profiles/) — node profiles (phase 43). `NodeProfileClamp` is the
  **pure** ceiling — what a coordinator may and may not ask of this box — and `NodeProfileApplier`
  carries out only what survived it. The clamp runs here, on the node, and that is the whole point:
  see phase-43 D1.
- [Retrieval/](src/InferHub.Node/Retrieval/) — solo RAG (phase 38): the three node-side seams the
  shared pipelines need — `LocalEmbeddingDispatcher`, `LocalReranker`, `NodeVectorLog`. The store,
  the pipelines and the extractors are `InferHub.Shared`'s; nothing is reimplemented here.

## Design rules to preserve

These come from `plan/00-overview.md` and the way the code is shaped today. Treat them
as load-bearing:

1. **Pluggable backends stay backend-agnostic.** Anything Ollama-specific belongs in
   `Backends/OllamaBackend.cs` or behind `IInferenceBackend`. Do not let
   `OllamaClient` types leak into `Worker`, `CoordinatorConnection`, or the coordinator.
2. **Core stays host-agnostic.** `InferHub.Shared` is a plain class library and no ASP.NET types
   go in it, or in node *configuration*. **Amended in phase 37:** ASP.NET now appears in
   `InferHub.Node`, confined to `LocalApi/` and only when solo mode is on. What the rule was
   protecting survives — the shared contracts and the options classes are still plain — and rule 5
   survives with it, because `Microsoft.AspNetCore.App` is a `FrameworkReference`, not a package.
   If you find yourself putting an `IResult` or an `HttpContext` anywhere else on the node, stop.

   **Phase 38 tested the rule properly and it held.** The retrieval core moved into
   `InferHub.Shared` so a solo node runs the same code rather than a second copy — and a plain
   class library cannot see `ILogger` or `IOptions<T>` without taking two packages. It did not take
   them: [IVectorLog](src/InferHub.Shared/Vector/IVectorLog.cs) is the two-method logging seam each
   host adapts in three lines, and the pipelines take **plain options objects** while the
   `IValidateOptions<T>` validators stay per-host. `InferHub.Shared.csproj` is still an empty
   `<Project Sdk="Microsoft.NET.Sdk">`, and it must stay that way: the day it grows a
   `PackageReference` is the day "the shared library is free" stops being true.
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
   are only ever derived from it — never a second authority.** This is what phase 38 refuses to
   boot past: a node cannot run its own authoritative corpus (`LocalApi:Retrieval:Enabled`) while it
   is also meshed, because it would then hold a derived copy *and* an authority under the same
   collection names. See phase-38 D1 — that startup failure is this sentence, enforced.

   > **Phase 44 sharpened that sentence, and the sharper one is the invariant now: *one authority
   > per collection name, and the hub knows who it is.*** A node may hold an authoritative corpus
   > **only** where the hub assigned it and recorded the ownership
   > ([CollectionOwnership](src/InferHub.Coordinator/Vector/CollectionOwnership.cs)), which is what
   > lets replication and healing skip those names and a hub-side create of one be a `409`. The
   > phase-38 startup failure is unchanged — a node that sets `LocalApi:Retrieval:*` **by hand**
   > while meshed still refuses to boot. What changed is that "who owns this name" became a thing
   > the hub records rather than a thing it assumes. See phase-44 D1.
   Everything else stays in-memory;
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

   > **Phase 47's image job registry is deliberately *not* a fourth exception, and the reason is
   > worth keeping.** It holds a job's record and its image bytes in memory, bounded by
   > `Images:Jobs:MaxRetainedBytes` and expiring after `Images:Jobs:RetentionSeconds` (five minutes),
   > and **nothing about it touches disk — no temp file, no spill under memory pressure, no cache
   > directory**. A restart forgets in-flight *and* completed jobs, like every other counter here,
   > and the docs say so unhedged. That is the whole test: the rule is about state that survives a
   > process, and this does not. If a future phase wants *durable* jobs it is a fourth exception and
   > must be argued **in this rule**, not added in the endpoint — because the moment a result
   > survives a restart, "where are my pictures kept" stops having the answer "nowhere, for five
   > minutes" and becomes a data-retention question somebody has to own.

   **Third recorded exception (phase 43): node profiles**, when `Fleet:Profiles:Persistence` is
   `file` or `postgres`. A profile that evaporates on hub restart is useless for the thing it was
   asked to do. The rule survives for phase-30 D2's reason: **a lost profile costs the fleet
   reverting to the operator-configured default on each box — never a wrong answer, and never a
   capability nobody granted**, because the node's own config remains the authority for what is
   *possible* and a profile is only a preference over it. See phase-43 D1/D3. Default is `none`.
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

   **The `qdrant` vector provider (phase 33) added *no* dependency, on purpose.** Qdrant's official
   client is gRPC and would drag `Grpc.Net.Client` + protobuf into the coordinator; its REST API is
   plain JSON, and the house already speaks HTTP-to-a-server by hand
   ([OpenAiUpstreamClient](src/InferHub.Shared/OpenAi/OpenAiUpstreamClient.cs)). So
   [QdrantClient](src/InferHub.Coordinator/Vector/Qdrant/QdrantClient.cs) is a hand-rolled
   `HttpClient` connector. A third vector backend, still zero new packages — considered the
   dependency and declined it. Do not "upgrade" it to the gRPC client.

   > **PyTorch is not an exception to this rule, and the reason is the whole of phase-41 D1.**
   > The v3.14–3.19 image track put `torch`, `diffusers`, `transformers` and `bitsandbytes` into a
   > container — several gigabytes of native code, the heaviest thing this project has ever shipped
   > — and **zero of it is a `PackageReference`**. It is a `pip install` in one Dockerfile, in the
   > same category as phase-39's `curl` of an Ollama tarball, and it is reachable only as a **child
   > process over a line protocol**. Nothing in any `.csproj` compiles against it, nothing in C#
   > imports it, and a node that never starts the diffusion tool never loads a byte of it.
   > `BundledNodeTests.NoProjectReferencesPythonAndTheSharedProjectIsStillEmpty` is what keeps that
   > true. **If a future phase wants an image library in C# — to decode a mask, to check a raster,
   > to make a thumbnail — that is a new dependency and it needs an argument here**, not a
   > convenience in an endpoint. Phase 50 wanted exactly that and did not take it (50 D2).

   **Six phases, five new container images' worth of capability, and still two packages.** Phases
   46–51 added text-to-image, an async job model, a seven-model catalogue with quantization, 360°
   panoramas, editing and a console for all of it, at **zero** new `PackageReference`s.
   `InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.
6. **The node-facing *inference* job protocol is Ollama-shaped. Client-facing and upstream-facing
   dialects are translations at the boundary.**
   > **The word "inference" was added in phase 40, and it is a bounding, not a weakening.**
   > `InferenceJob` still carries Ollama JSON, always. What phase 40 settled is that work with no
   > Ollama shape to be — a transcription, a synthesis — travels as its own contract rather than as
   > an `InferenceJob.Kind` with a foreign body, which would have made this rule true of *some*
   > jobs and left every later reader to discover which. See phase-40 D3. `InferenceJob.RawJson` crossing SignalR and
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

   > **The rule has met three more kinds of content since, and each widened what "content" means
   > rather than what the rule permits.** Recorded here because a rule whose amendments live only
   > in their own phase sections is a rule the next reader will apply to the case it was written
   > for and no other.
   >
   > - **Audio is content** (phase-42 D5). A transcription request is a recording of somebody's
   >   voice and the answer is what they said. Nothing containing audio bytes or transcript text is
   >   logged at any level, and the line that *is* written carries the model, the duration and the
   >   outcome — **not the filename the caller chose**, which is metadata about somebody's day.
   > - **A prompt is content** (phase-46). A transcript is content because it is what somebody
   >   *said*; a prompt is content because it is what somebody *wanted*, and the picture is the
   >   answer — which makes an image request the most revealing thing a caller sends a fleet.
   >   Nothing logs one, on either host, at any level. What may be logged is the **recipe's trigger
   >   phrase** (phase-49 D2), because that is a constant of the model rather than the caller's
   >   words, and "why does this not look like a panorama" is otherwise undiagnosable.
   > - **An uploaded picture is content, and so is a mask** (phase-50). Both are held for the
   >   dispatch and dropped; both travel as `image` and `mask` rather than under the caller's own
   >   filename, for the phase-42 reason above.
   >
   > The mechanism is the same each time and is the thing to preserve: **count, never content**
   > (phase-25 D3). The usage path has gained four units — tokens, audio seconds, characters,
   > megapixel-steps — and **no field that could hold a sample**, deliberately, because a field is
   > an invitation. `ImagePrivacyTests` and `AudioPrivacyTests` both run a real request through a
   > real mesh with a capturing logger at `Trace` and fail if a known phrase appears anywhere in
   > the log or the ledger.

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

### Phase 24 (hybrid search, reranking, eval harness) — also load-bearing

**D1 — Keyword search is provider-native; zero new dependencies.** Under `postgres` it is a
`tsvector` generated column (`to_tsvector('english', payload text)`) with a GIN index and
`ts_rank_cd` — Postgres full-text search, which `Npgsql` already reaches. Under `local` it is
[InvertedIndex](src/InferHub.Shared/Vector/InvertedIndex.cs), a BM25 (`k1=1.2, b=0.75`)
dictionary that sits beside the `FlatIndex` in every collection. **`InvertedIndex` is derived, never
authoritative** — rebuilt from the raw store on startup and updated on every upsert/delete under the
same collection write-lock as the `FlatIndex`, exactly so the two can never diverge. Pulling in
Lucene to rank a few thousand chunks would be the wrong trade. Rule 5 survived again: `SearchKeywordAsync`
is the seam, both providers implement it, and nothing new was added.

**D2 — Fusion is Reciprocal Rank Fusion, not score blending.**
[HybridSearch](src/InferHub.Shared/Vector/HybridSearch.cs) fuses the two result lists by *rank*
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
[IReranker](src/InferHub.Shared/Vector/IReranker.cs), the one implementation) hands the top
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

### Phase 33 (Qdrant vector connector) — also load-bearing

**D1 — A third `IVectorStore`, and it is an *external* provider — so it is phase-20 §2 again.** Node
replication and self-healing are off; `NullVectorQueryRouter` stands in; the store publishes its own
`vector.collection.created` / `.dropped`. The one predicate every call site branches on is
`VectorStoreProviderExtensions.IsExternal` (postgres **or** qdrant), not `IsPostgres` — see "Vector
providers". `VectorCompositionTests.QdrantProviderRegistersQdrantStoreAndNoMeshServices` pins it.

**D2 — Zero dependency, by hand, on purpose.** See rule 5. `QdrantClient` is a hand-rolled REST
connector over `HttpClient`; the gRPC client was considered and declined. Do not add it.

**D3 — Qdrant point ids are UUIDs; the real id lives in the payload. This is the load-bearing
gotcha.** Qdrant accepts **only** an unsigned int or a UUID as a point id, and InferHub ids are
neither. So [QdrantIdMap](src/InferHub.Coordinator/Vector/Qdrant/QdrantIdMap.cs) maps each real id to
a deterministic `UUIDv5`, and the real id — with payload and metadata — rides in the point payload
under reserved `__*` keys (`__id`, `__payload`, `__meta`, `__seq`, `__ts`); reads unpack it, so
nothing above the store ever sees the UUID. **Determinism is what keeps re-ingest idempotent**
(phase 23 D5): the same real id addresses the same point and therefore *replaces*.
`QdrantIdMapTests` asserts determinism and no collisions over a large sample. A metadata filter maps
to a Qdrant `must` match on `__meta.<key>`, which excludes points lacking the key — the same
"null-metadata never matches" rule `FlatIndex` honours.

**D4 — Scan sorts client-side, because Qdrant scrolls by its own point id (a UUID), not by the
InferHub id the contract promises.** `ScanAsync` materialises the filtered set and orders by real id
in memory, then windows by `afterId` + `limit`. Correct, at the cost of reading the whole filtered
set per call — fine for the per-document scans that dominate ingestion (small sets), and the reason
`DeleteByFilterAsync` counts first (Qdrant's delete-by-filter returns no count, and the contract
must). Phase 35's migration adds a with-vectors scan; keep this ordering discipline there too.

**D5 — Keyword search is coarse under qdrant until v3.2, and honest about it.** Qdrant's full-text
index is a filter, not a ranking, so `SearchKeywordAsync` scrolls a **bounded** slice
(`KeywordScanCap`) and ranks by term-overlap in the chunk text — enough to give hybrid a real second
branch, explicitly not BM25. Phase 34 replaces it with server-side sparse-vector fusion. Records with
no text payload contribute nothing, the same stance `ChunkText` takes.

**Rule 5 survived again.** Phase 33 added **zero** new dependencies.

### Phase 34 (Qdrant-native hybrid search) — also load-bearing

**D1 — Under qdrant, hybrid fusion moves into the engine; the seam grows a capability, not a
method.** Qdrant's Query API fuses a dense and a sparse (lexical) vector server-side by RRF in one
round trip. That is a better hybrid than the hub fusing a real dense branch with the phase-33 coarse
keyword branch — so a new capability interface,
[IServerSideHybridSearch](src/InferHub.Shared/Vector/IServerSideHybridSearch.cs), carries it.
Only `QdrantVectorStore` implements it; `RetrievalPipeline` prefers it when
`store is IServerSideHybridSearch h && await h.SupportsServerSideHybridAsync(collection)` and **falls
back to hub RRF otherwise** (a dense-only 3.1 collection, or `local`/`postgres`). It is deliberately
**not** a method on `IVectorStore` — the seam already carries three engines and only one can fuse
server-side; the other two must not grow a method they would have to fake. Same "one implementation
behind a seam" shape as `IReranker`. Keyword mode needs no pipeline fork: the store's
`SearchKeywordAsync` is a real sparse-vector search on a hybrid-capable collection and the coarse
scroll on a dense-only one. `RetrievalPipelineTests` pins both the server-side fork and the fallback
with a decorator store, crossing no wire.

**D2 — The sparse vector is hub-computed and IDF is Qdrant's job, so rule 5 held again — zero
dependencies.** [SparseVector](src/InferHub.Coordinator/Vector/Qdrant/SparseVector.cs) turns text
into `{indices, values}` where indices are a stable FNV-1a hash of each token and values are raw term
frequencies. The tokens are **exactly** `InvertedIndex.Tokenize`'s, so "the lexical view of a chunk"
means the same thing under `local` and `qdrant` (`SparseVectorTests` pins that parity). No sparse
model, no corpus statistics threaded through the hub: the collection's sparse vector is declared with
`modifier: idf`, so **Qdrant applies the inverse-document-frequency weighting server-side**. A rare
hash collision merely conflates two terms — acceptable and honest for a lexical branch.

**D3 — Hybrid-capable collections are named-vector collections; 3.1 collections stay dense-only, by
design.** A collection created on 3.2+ has a **named** dense vector (`"dense"`) plus a named sparse
vector (`"sparse"`), so every points/search/retrieve call branches on `CollectionMeta.Hybrid`: a
hybrid collection addresses `dense` by name and writes `{dense, sparse}`, a 3.1 collection uses the
unnamed shape. `QdrantClient.GetCollectionAsync` reads both wire shapes (unnamed `vectors:{size,…}`
vs named `vectors:{dense:{…}}` + `sparse_vectors:{sparse:{…}}`) and reports the flag. A collection
created on 3.1 keeps answering **vector** queries after upgrade and its keyword search stays coarse
until it is re-created or migrated (phase 35) — `QdrantVectorStoreTests` pins the dense-only
compatibility path. The default retrieval mode is still `vector`, so a deployment that sends no
headers and changes no config is byte-identical to 3.1.

**Rule 5 survived again.** Phase 34 added **zero** new dependencies: the sparse vector is hub-computed
over `System.Text.Json`, and the Query API is more hand-rolled REST on the existing `QdrantClient`.

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

### Phase 36 (the node supervises its own Ollama) — also load-bearing

**D1 — The supervisor only ever touches a *local* Ollama, and disables itself out loud otherwise.**
It is registered only when `Ollama:Supervisor:Enabled` **and** `Backend:Type=ollama` **and**
`Ollama:Endpoint` is loopback; any other combination registers
[OllamaSupervisorDisabledNotice](src/InferHub.Node/Backends/Supervision/OllamaSupervisor.cs) instead,
which logs one line naming why. This is not tidiness. A shared Ollama serving four nodes, restarted
because *one* node's network hiccuped past `ProbeTimeout`, is a four-node outage caused by the node
with the worst link; an OpenAI-compatible upstream is somebody else's server entirely. A process may
only be restarted by something on the same machine that can see it actually wedged. (Compare
phase-32 D3, where a *local* deadline was also the only honest one — but there demoting was correct
because the alternative was two hubs serving; here the alternative is us breaking a healthy server
for other people.) The rule covers the container case for free: a node image cannot restart an
Ollama on its host, and its endpoint is by definition not loopback.
`NodeCompositionTests` fails if any of the three conditions stops being enforced.

> **Amended in phase 39.** That last sentence described the only node image that existed. The
> **bundled** image (`:ollama`) runs Ollama *inside* the container on `127.0.0.1`, so the loopback
> gate is satisfied — and satisfied for exactly the right reason rather than by luck: an address
> inside the container's own network namespace **cannot** be somebody else's server, which is the
> hazard the gate was written against. The rule is unchanged and now covers a case it was not
> written for. See phase 39 D3.

**D2 — The two `HttpClient`s pointed at Ollama are NOT redundant. Do not consolidate them.**
`Ollama:RequestTimeout` is **five minutes** on purpose — a cold 70B load blows through
`HttpClient`'s 100s default and the coordinator waits 300s. Probing over that client would mean a
**wedged** Ollama — the exact case this phase exists for — takes five minutes to produce one failed
probe, and three of those to cross the threshold: a quarter of an hour before the node lifts a
finger. So the probe has its own named client with `ProbeTimeout` (5s) against `GET /api/version`.
If a future reader sees two clients for one server and merges them, this phase silently stops
working while every test still passes.

**D3 — Three states, not two, because the cure differs.** `Healthy` (answered) → nothing;
`Unreachable` (the socket never opened) → **start**; `Wedged` (socket opened, nothing came back, or
a 5xx) → **stop, then start**. `start` on a wedged process fails with the port already bound and the
log then blames the wrong thing, while `stop` on a dead one is a no-op that hides a genuine config
error (wrong port, wrong host) behind a cheerful "restarted Ollama". A single failed probe decides
nothing: a state is declared only after `UnhealthyThreshold` (3) **consecutive** failures, and any
success resets the counter.

> **"Connection refused throws `HttpRequestException`, a wedge throws `TaskCanceledException`" is
> wrong, and only a real socket found it.** On Windows a closed loopback port is silently dropped
> rather than refused: the connect hangs to `ConnectTimeout` and surfaces as *exactly the same* bare
> `TaskCanceledException` a wedged server produces — so the first implementation classified a
> stopped Ollama as wedged and answered it with a stop that had nothing to stop. `OllamaProbe`
> therefore performs the TCP connect itself in a `ConnectCallback` and stamps the request when a
> socket is actually established, with pooling off so the stamp always describes *this* probe.
> "Did the socket open?" is now a fact rather than an inference from an exception. A stub
> `HttpMessageHandler` can only echo the exception a test author already believed in, which is why
> `OllamaSupervisorTests` uses a real closed port and a real accept-and-never-answer `TcpListener`
> for those two cases — keep it that way.

**D4 — Restart is rate-limited and gives up loudly; in-flight work is deliberately not protected.**
`MaxRestartAttempts` (3) inside `RestartWindow` (10 min), `RestartBackoff` (10s, doubling), then a
readiness wait up to `ReadyTimeout` (2 min — a service that starts by loading a model is slow, not
broken). Past the budget it **stops restarting**, logs once at Error, and **keeps probing**, so a
recovery is still noticed and the node comes back on its own. A node that kills Ollama every fifteen
seconds forever never lets a model finish loading, which replaces a fixable outage with an
unfixable one. And a restart *does* kill a streaming job: waiting for `inFlight == 0` would let
exactly one stuck request pin the node in a broken state forever — the failure mode this phase
exists to end — and after three failed probes over ~45s that stream was not going to finish anyway.
The cost is logged rather than hidden, by `CoordinatorConnection` (which owns the count) via the
`Restarting` event.

**D5 — A service manager wins over spawning, and privileges are a first-class error.** Discovery
order is service (`Ollama` / `ollama.service`) → binary on `PATH` → nothing. Spawning `ollama serve`
next to a service-managed install gets you **two** servers fighting over `:11434`, and the one that
loses is the one whose logs the operator is reading. An access-denied result is returned as a typed
`ProcessControlResult`, never thrown — "Access is denied" from a `Process.Start` deep in a hosted
service is a support ticket, and the Windows-service host under a restricted account is exactly
where it bites (`deploy/windows/README.md` says so).

**D6 — Auto-install is a *second*, separate opt-in.** `Enabled=true` consents to restarting a
process; it does not consent to downloading and executing an installer. So `AutoInstall` is its own
key, default false — the phase-22 D5 shape. It fires **only** on discovery finding nothing (install
is a diagnosis, not a retry), **once per process lifetime** (a failing install retried on a timer is
a machine downloading the same installer every fifteen seconds), from a configurable `InstallUrl` so
an air-gapped fleet points at its own mirror, with the exact command logged **before** it runs.

**D7 — The empty model report is deliberate, and the alternative was considered and rejected.**
A broken backend reports zero models; `NodeRegistry.ReportModels` replaces the list wholesale, so
that is what unroutes the node. Preserving the last known good list would leave the coordinator
happily routing inference at a node whose backend is wedged — turning a node-local fault into
client-visible timeouts. What this phase added is the *reason* (one Warning naming the state, so
"no models" no longer reads the same as "this box has nothing installed") and a `Recovered` event
`CoordinatorConnection` subscribes to, so recovery pushes a fresh report instead of waiting out
`ModelRefreshInterval` (up to 60s of a healthy node sitting out of the fleet).

**D8 — One platform seam, and rule 1 is intact.** `Process`, `sc.exe`, `systemctl` and the installer
appear in exactly one class each ([OllamaProcessControl](src/InferHub.Node/Backends/Supervision/OllamaProcessControl.cs),
[OllamaInstaller](src/InferHub.Node/Backends/Supervision/OllamaInstaller.cs)); the supervisor is a
state machine over three interfaces and a `TimeProvider`, with no I/O of its own and none in any
constructor. Nothing in the node's generic path (`Worker`, `InferenceExecutor`, `IInferenceBackend`)
learns that a supervisor exists — the one consumer, `CoordinatorConnection`, sees
[IBackendSupervisor](src/InferHub.Node/Backends/Supervision/IBackendSupervisor.cs), which is named
for the node rather than for Ollama and is always registered (as `NoBackendSupervisor` when
supervision is off) so no caller branches on the feature existing.

**Deliberately not in this phase:** the coordinator does **not** learn "backend unhealthy" as a
typed signal — no `Heartbeat` field, no `/api/status` health column, no console change. That is a
contract change plus `NodeRegistry` plus `StatusEndpoint` plus two static files and their tests, and
the fleet already stops routing to a broken node through the empty model report. Recorded so the
omission is a decision rather than an oversight; it is the obvious next phase if the logs turn out
not to be enough.

**Rule 5 survived again.** Phase 36 added **zero** new dependencies: `System.Diagnostics.Process`,
`Socket` and `HttpClient` all ship in the shared framework.

### Phase 37 (solo mode — the node serves its own API) — also load-bearing

**D1 — Phase-26 D1 is amended, narrowly, and the amendment is written *there* as well as here.**
The mesh protocol is still outbound-only; what solo mode adds is a **client-facing** surface, the
same category as the coordinator's `/v1`, serving the node's own clients. The hub never dials a
node, no job or model command arrives over the local API, and the two surfaces share no prefix, no
token set and no handler. **`ModelCommandExecutor` stayed hub-driven on purpose** — model management
in solo mode is `ollama pull` in the terminal you are already sitting at, and exposing it would
double the surface that has to stay in parity for nothing.

**D2 — Solo mode is the hub's formatting layer over the node's executor, with routing removed.**
The coordinator does `HTTP → translate → [admit → route → queue → dispatch → SignalR] → node →
InferenceExecutor`; solo is the same line with the bracket deleted. Both ends were already shared:
the translators live in `InferHub.Shared/OpenAi/` (phase-22 D1) and `InferenceExecutor` already
consumes an Ollama-shaped job — **which is only true because rule 6 made the internal protocol
Ollama JSON from the start.** That decision has looked pedantic more than once; it is the reason
this phase was small. **Do not grow routing, admission, queueing or failover into `LocalApi/`.**
*Rejected on purpose:* running the coordinator in-process as an "embedded hub" with a loopback
self-node. It reuses more code and inverts the project dependency, dragging `Npgsql` and `PdfPig`
into the node image to serve a fleet of one.

**D3 — Rule 2 amended, rule 5 intact, image size unchanged.** See rule 2 above.
`WebApplicationBuilder` implements `IHostApplicationBuilder`, so `AddInferHubNode` needed **no
signature change** and `NodeCompositionTests` still guards one composition root;
[NodeHostFactory](src/InferHub.Node/NodeHostFactory.cs) picks the builder for both hosts so the
console and Windows-service entry points cannot drift on host shape either. The node image already
finalled on `mcr.microsoft.com/dotnet/aspnet`, so nothing grew.

**D4 — Off by default, loopback by default, and it refuses to serve a LAN anonymously.** A
non-loopback bind with no `LocalApi:ApiKeys` and no `AllowAnonymous` **fails startup naming the
keys**. That is deliberately stricter than phase-35 D4 (a keyless remote Qdrant only warns), and
the asymmetry is the point: there the exposure was data the operator had already chosen to store
and refusing would have been us overruling them about their own network; here it is **arbitrary
compute on somebody's GPU**, the default is safe, and the first sign of trouble is a bill. Note
that the **container binds a wildcard** (`ENV LocalApi__Urls=http://+:8080`), so a containerised
solo node needs a key or the explicit override — by design.

**D5 — The surface is defined by subtraction, and `/api/status` does not fake a fleet.** Solo serves
the client-facing routes and nothing that needs a fleet or a store; admin, console, `/metrics`,
ingestion and the vector data plane are 404. `/api/status` returns a **smaller, different document**
with a `mode: "solo"` discriminator rather than the hub's document with zeros in the fleet fields —
a dashboard reading `nodesEvicted: 0` from a process with no concept of nodes is worse than one that
gets nothing.

**D6 — What may move to `InferHub.Shared` is decided by one line: is it ASP.NET?** The frame
*bodies* are shared ([IOpenAiStreamFormatter](src/InferHub.Shared/OpenAi/OpenAiStreamFormatters.cs),
`OpenAiSse`, `OllamaNdjson`, `OpenAiErrorEnvelope`,
[NodeErrorText](src/InferHub.Shared/Contracts/NodeErrorText.cs)); the ten lines that write them to a
response and flush are **duplicated on purpose**, per host. A divergent `finish_reason` is a bug
users hit; a divergent `WriteAsync` call is not. `InferenceCore.ReadableNodeError` now delegates to
`NodeErrorText` — solo is the deployment most likely to surface a raw Ollama error, since there is
no hub between the user and the backend.

**D7 — `SoloParityTests` is the point of the test suite, and it must cross the wire on both sides.**
It drives the same request bodies through a real Kestrel hub and a real Kestrel solo node over the
same scripted payloads and compares what a client actually receives, normalising only the ids and
timestamps that are minted per request. Handler-level comparison would prove the handlers agree and
say nothing about the response — the same lesson as `NodeHubStreamingTests`. It covers both dialects
× blocking/streaming plus tool calls, vision, an unwrapped upstream refusal and an unknown model,
and `TheComparisonActuallyDetectsADifference` guards the guard. **Verified by breaking the node's
SSE terminator on purpose: four parity tests went red.**

**D8 — A retrieval header in solo mode is a 501 that names the limitation, never a plain answer.**
Phase-31 D4's reasoning transfers exactly: answering without the context the caller asked for,
silently, is the wrong failure. A developer moving a working RAG app onto a solo node and getting
confident, fluent, **ungrounded** answers files a bug three weeks later that starts "the model got
worse". Retrieval parity is the obvious next phase and is a non-goal here because the vector store,
`IngestionPipeline` and `RetrievalPipeline` live in the coordinator, and moving them would drag
`Npgsql` and `PdfPig` into an image rule 5 scopes them out of by name.

> **Superseded in phase 38 — the non-goal is now a feature, and the reasoning above was half
> right.** A standalone node *does* retrieve, over the same headers, with the same augmented prompt
> and the same `X-InferHub-Sources`. The pipelines moved to `InferHub.Shared` rather than being
> copied, and neither `Npgsql` nor `PdfPig` came with them: the postgres/qdrant providers stayed in
> the coordinator (only `local` runs on a node) and PDF is a clean **415** for exactly that reason.
> **The 501 is still what you get with the corpus off**, which is the default, so this paragraph
> describes a real state rather than history. See phase 38 below — and note D1 there: retrieval is
> refused *at startup* on a node that is also meshed.

**D9 — `Node:MaxConcurrency` is *enforced* in solo mode and advisory everywhere else.** One key with
two behaviours is normally a smell; it is right here because the key's meaning — "this many at once
is what this box can take" — is unchanged and only the enforcer moved, from the hub that is no
longer there to the node that is. Over the cap: wait `LocalApi:MaxWaitSeconds`, then **503 +
`Retry-After`**, the same status and header as the hub's `RequestQueue` (phase-25 D5), so a client's
retry logic behaves identically against either. Unset means **no gate object at all**, not a gate
nobody can exhaust.

**D10 — The coordinator connection is optional, and "neither" fails loudly.** `Coordinator:Enabled`
(default true) off means no `HubConnection`, no heartbeat, no reconnect loop — and no required
coordinator URL, which today's validator demands unconditionally. A solo node on a train must not
need a URL it will never dial. Both off is a **startup failure naming both keys**: a node that
neither joins a mesh nor serves anyone is a process burning a GPU box for nothing.

*Recorded deviation from the phase brief:* `/api/tags` moved from an inline lambda in the
coordinator's `Program.cs` into `MapInferenceEndpoints`. It is an inference-surface route and
belongs there — and concretely, the parity suite must compare the **real** hub handler against the
node's, which it cannot do for a route that only exists inside the composition root. Behaviour is
unchanged apart from the log category.

> **v3.5.0 shipped with solo mode dead on arrival in Docker, and v3.5.1 fixed it the same day.**
> `docker run -e LocalApi__Enabled=true` failed at boot with *"LocalApi:Urls must be absolute
> http(s) URLs (got 'http://+:8080')"* — the image's own default. **Kestrel accepts `http://+:port`
> and `http://*:port`; `Uri.TryCreate` does not**, and the validator used `Uri` alone. Listen
> addresses now go through `LocalApiOptions.TryParse`, which parses them the way Kestrel accepts
> them and reports "is this a wildcard?" **separately** from "did this parse?" — conflating those
> two questions is exactly how it shipped. A wildcard is still the most exposed address there is,
> so D4's keyless refusal is unchanged.
>
> The galling part: the wildcard forms were handled correctly in `BindsLoopbackOnly` **one method
> away**, and simply forgotten in the validator beside it. The unit suite, the parity suite and a
> live from-source solo node answering real inference from a real Ollama were all green, and none
> of them touched the one address the container ships. Same shape as v2.5.1 and v3.0.1:
> [[verify-published-artifact-not-just-tests]] — pull the image and run it, every time.
> `KestrelsWildcardAddressesAreValidAddresses` pins the container's exact configuration.

**Rule 5 survived again.** Phase 37 added **zero** new dependencies — ASP.NET Core is a
`FrameworkReference` — and in fact removed one: the now-redundant `Microsoft.Extensions.Hosting`
package reference on the node.

### Phase 38 (RAG in solo mode) — also load-bearing

**D1 — Solo retrieval exists *only* where there is no coordinator, and the two-on case is a startup
failure. This is rule 4, not tidiness, and it is the decision the whole phase turns on.**
`LocalApi:Retrieval:Enabled=true` requires `Coordinator:Enabled=false`;
[LocalRetrievalOptionsValidator](src/InferHub.Node/Configuration/NodeConfigurationValidation.cs)
fails the host naming **both** keys otherwise. A meshed node already holds **derived** copies of the
hub's collections — phase-15 replicas, on disk under `Vector:ReplicaDirectory`, maintained by the
hub pushing down. Give that same process an **authoritative** store of its own and one node contains
two vector authorities: a locally ingested document is invisible to the fleet, a collection name
that exists in both places has two different sets of chunks under it, and `ReplicationCoordinator`
will overwrite a collection the operator believes they own. There is no configuration of that which
is safe, so it is not offered.

> **Amended in phase 44, and narrowly.** The startup failure above is **unchanged** — a node that
> sets `LocalApi:Retrieval:Enabled` by hand while meshed still refuses to boot, and
> `NodeOwnedCollectionTests` holds that line. What phase 44 added is the one case this decision could
> not express: a corpus the **hub assigned**, and therefore *knows about*. The hub records an owner
> per collection name ([CollectionOwnership](src/InferHub.Coordinator/Vector/CollectionOwnership.cs)),
> refuses to create a node-owned name centrally, and excludes it from replication and healing — so
> the node holds one authority and zero derived copies **under names the hub has recorded as not its
> own**. The sentence that survives both phases: *one authority per collection name, and the hub
> knows who it is.* See phase-44 D1.

**It refuses rather than silently disabling, and that asymmetry with phase-36 D1 is deliberate.**
A disabled supervisor costs an operational nicety and one log line is enough. What is switched off
here is *grounding*, and a node that quietly answers ungrounded is precisely the phase-31 D4 /
phase-37 D8 failure: confident, fluent, wrong, with no signal, and a bug report three weeks later
that starts "the model got worse". An explicit opt-in that cannot be honoured must be loud.

**D2 — The retrieval core *moved* to `InferHub.Shared`. Not one line of it is reimplemented.**
`IVectorStore`, `LocalVectorStore`, `InvertedIndex`, `HybridSearch`, `ChunkText`,
`RetrievalPipeline` + its contracts, `IEmbeddingDispatcher`, `IReranker`, `IVectorQueryRouter`,
`IServerSideHybridSearch`, `RerankPrompt`, the options POCOs and all of `Ingestion/` except
`PdfTextExtractor.cs`. What stayed in the coordinator: the external providers (Postgres, Qdrant),
`ReplicationCoordinator`/`HealingService`/`ReplicaRegistry`, `VectorQueryRouter`, `LlmReranker` and
`EmbeddingDispatcher` (both dispatch on the fleet), `Metrics`, every endpoint, every options
**validator**, and `PdfTextExtractor`. Two `GlobalUsings.cs` files (coordinator, tests, migrate) are
why the move touched no consuming file.

Retrieval has a dozen decisions — fusion, k clamping, `OnMissing`, rerank fallback, the context
template, the stale-chunk sweep, the `partial` verdict, the content-hash short circuit — and every
one of them diverging silently produces *plausible answers*, which is the failure nobody notices.
**Rejected: a smaller reimplementation on the node.** Less churn today, and the trade this repo has
refused four times (phase-21 D3, phase-22 D1, phase-34 D1, phase-37 D6).

**D3 — The two host couplings became seams, not packages.** See rule 2 above. `IVectorLog` +
`NullVectorLog`, and `IRetrievalMetrics` + `NullRetrievalMetrics` (the coordinator's `Metrics`
implements it and is otherwise untouched). The **log message templates are unchanged**, so the
hub's structured output is what it was before the code changed projects.

**D4 — `local` is the only provider on a node, and `VectorStore:*` is not the key.** The node reads
[LocalApi:Retrieval:*](src/InferHub.Node/Configuration/LocalRetrievalOptions.cs), which projects
onto the shared `VectorStoreOptions` with `Provider` pinned to `local`. Rule 5 scopes `Npgsql` to
the coordinator by name; a node with an external database *and* no coordinator is a shape nobody
asked for; and a node reading `VectorStore:*` would silently pick up a coordinator's section on a
box that has both files. **Same Docker permissions trap for the fourth time** (phase-21 D7,
phase-30 D3): the default `./data/retrieval` resolves to `/app/data`, so the node image sets
`ENV LocalApi__Retrieval__DataDirectory=/data/retrieval` under the existing `chown app:app /data`.

**D5 — No PDF on a node, and the refusal is a 415 that names the limitation.** `PdfPig` is rule 5's
second recorded exception, scoped to the coordinator *by name*. `TextExtractor` already delegates
PDF to `IPdfTextExtractor` and refuses when none is registered; the node registers none, and
`LocalIngestionEndpoints` turns that into a message saying to convert the file or use a hub.
Phase-23 D4 refused OCR because a bad extraction *succeeds quietly* and fills a corpus with
plausible nonsense — a PDF ingested as its raw bytes would be the same failure with fewer excuses.
`NodeCompositionTests` fails if `IPdfTextExtractor` ever appears on the node.

**D6 — Embedding runs on the node's own backend, through the seam ingestion already had.**
[LocalEmbeddingDispatcher](src/InferHub.Node/Retrieval/LocalEmbeddingDispatcher.cs) is
`IEmbeddingDispatcher` over `InferenceExecutor` — phase-37 D2's framing a second time. Everything
above it is unchanged: the bounded batch fan-out, the per-batch retry that deliberately does **not**
retry `NoEmbeddingNodeException`, the `partial` verdict, and phase-31 D5's deferred auto-provision
that *measures* the dimension from the first embedded batch. Solo mode auto-provisions on first
ingest because the node's own config is the grant — the same reasoning phase-31 D5 applied to a
client's collection scope.

**D7 — Reranking works and is still never a dependency.** `RerankPrompt.Build`/`ParseScores`/`Apply`
are shared; [LocalReranker](src/InferHub.Node/Retrieval/LocalReranker.cs) runs them on the node's
own backend. Phase-24 D4 verbatim, and it matters more here: a solo box is one wedged Ollama away
from every rerank failing, so no model, a timeout, prose, or a wrong-length array **all return the
candidates untouched**.

**D8 — `/api/status` grows a retrieval block and still does not fake a fleet.** Phase-37 D5 stands:
a smaller, different document. The block is what a solo operator can act on — is it on, which
embedding model, which mode, and the collections with their record counts — and **no** replica
counts, no under-replication gauge, no queue. `enabled: false` is the honest answer for a node with
the corpus off, and it is what explains a 501 to whoever is reading the status page.

**D9 — Two things on the node are hand-copied on purpose, and they are the parity risk.**
`LocalRetrievalHeader` (the `X-InferHub-Retrieve*` parser, minus the hub's client-scope check —
a node has no tenancy) and the ten lines that serialise `X-InferHub-Sources`. Same phase-37 D6 line:
the *content* is shared, the host's own plumbing is not. `SoloRetrievalParityTests` drives the same
corpus and question at both hosts over real Kestrel and compares the **augmented prompt**, the
**sources header** and the **`/search` ranking in all three modes** — because those are what a
client sees. `TheComparisonActuallyDetectsADifference` guards the guard, with `k=1` deliberately:
at the default k the whole test corpus fits in every context block and the assertion would pass
without comparing a *choice*.

*Recorded deviation from the phase brief:* collection **lifecycle** on a node rides
`/api/collections` (list, create, get, drop) rather than the hub's admin path, because solo mode has
no admin surface (phase-37 D5). Most deployments never call it — the first ingest provisions.

**Rule 5 survived again.** Phase 38 added **zero** new dependencies, and `InferHub.Shared.csproj`
is unchanged. `Npgsql`, `Pgvector` and `PdfPig` appear nowhere in the node's dependency tree.

### Phase 39 (the bundled node image) — also load-bearing

**D1 — A container needs the *driver*, not a CUDA toolkit, so the base image did not change.**
`libcuda.so.1` / `libnvidia-ml.so.1` are the **driver**, injected at `docker run` by the NVIDIA
container runtime when `--gpus` is passed; `libcudart` / `libcublas` / `libggml-cuda` are the
**runtime**, and they already ship inside Ollama's own tarball. So
[Dockerfile.ollama](src/InferHub.Node/Dockerfile.ollama) finals on the same
`mcr.microsoft.com/dotnet/aspnet:10.0` as the plain node image. **`nvidia/cuda` as a base was
considered and rejected**: a third copy of a runtime Ollama does not load, ~2 GB, and a CUDA minor
version we do not choose. The split is also what makes CPU mode free — the same tarball carries the
CPU kernels, so "no card" is the same image with nothing injected into it.

**D2 — It is a *second image*, which is the only shape in which rule 5 survives.** A 1.4 GB
compressed Ollama bundle fails "no new heavy dependencies" in every form except an opt-in artifact
nobody else pulls. `inferhub-node` is unchanged — multi-arch, ~340 MB, no Ollama;
`inferhub-node:ollama` is amd64 and ~4 GB. **One image with a bundled-mode flag is rejected**: the
layers are there whether the flag is on or not, so every coordinator+node compose stack would grow
by 4 GB for a feature it does not use. No `.csproj` changed — this dependency is a `curl` in a
Dockerfile, not a package anything compiles against. `BundledNodeTests` fails if the bundle ever
leaks into the plain Dockerfile. **The tag is `:ollama`, not `:gpu`**, because the image runs
perfectly well with no card and naming it after the accelerator would make the majority of its uses
look like a workaround; `:gpu` is published as an alias of the same digest.

**D3 — The supervisor *is* the init system, and restarting the bundled Ollama is the point.** The
container runs `dotnet InferHub.Node.dll` as PID 1 and `ollama serve` as its child. No s6, no
supervisord, no entrypoint script — phase 36's `OllamaProcessControl` already discovers the binary,
spawns it, pumps its output into the node's log and restarts it (`Unreachable` → start, `Wedged` →
stop-then-start) under a budget. A wedged Ollama *inside* a container is worse than on a host,
because nobody is sitting at a terminal on it: the container looks alive, `/health` answers, and
every request hangs. Phase-36 D1's loopback gate is satisfied naturally here and **its container
sentence is corrected in place above** — do not re-simplify it.

**D4 — Two supervisor gaps only a container exposes, both behind keys that default to today's
behaviour.** `Ollama:Supervisor:StartAtBoot` — probe once at startup and, **only on `Unreachable`**,
start immediately instead of idling three probe intervals waiting to discover what we already know.
Never on `Wedged`: the threshold exists to avoid misdiagnosing running-but-slow, and starting
something that already holds the port fails in a way that makes the log blame the wrong thing.
`Ollama:Supervisor:StopOnShutdown` — `IOllamaProcessControl.StopSpawnedAsync` kills **only what this
supervisor spawned**, by handle, never the by-name sweep `StopAsync` deliberately does when
remedying a wedge. Off by default because on a desktop the operator may still be using that Ollama;
on in the image, where the child is SIGKILLed the instant PID 1 exits and a `docker stop` mid-pull
leaves a partial blob.

**D5 — "Is there a GPU?" is answered by loading `libcuda.so.1`, and this is the finding a future
reader will otherwise undo.** Every recipe on the internet checks for `/dev/nvidia*`. **Under WSL2 —
Docker Desktop on Windows, the most common GPU-with-Docker setup there is — those device nodes do
not exist**; the GPU arrives via `/dev/dxg` and the driver libraries are injected from
`/usr/lib/wsl/lib`. Observed directly:
`docker run --rm --gpus all …/runtime-deps:10.0 sh -c 'ls /dev | grep -i nvidia'` prints nothing
while `libcuda.so.1` is present. A device-node check would report "no GPU" on a machine about to run
CUDA happily. So [CudaDeviceProbe](src/InferHub.Node/Backends/CudaDeviceProbe.cs) `TryLoad`s the
driver and calls `cuInit` / `cuDeviceGetCount` / `cuDeviceGetName` through `NativeLibrary` (never a
class-scope `DllImport`, which would fault the first time anything touched the type on a machine
with no driver). Every failure path returns "no devices" and **nothing throws** — this runs on every
node at startup and must never be why one fails to boot. Rule 1 holds: it is a *machine* capability,
not an Ollama one.

**D6 — The image announces what it found; it does not refuse. Refusing is opt-in.** An earlier draft
of the phase had it fail to start without a GPU. **That was reversed.** CPU is a legitimate mode —
embedding models, small models, a vector-store-only box — and refusing would have made two of the
image's three documented modes impossible. The danger was never CPU, it was **silence**: four
gigabytes of CUDA runtime, a dropped `--gpus` flag, two tokens a second, and an afternoon spent
blaming the model. So [GpuReport](src/InferHub.Node/Backends/GpuReport.cs) logs what it saw in both
directions in the first lines of `docker logs`, solo `/api/status` carries a `gpu` block, and
`Ollama:RequireGpu` (**default false, including in the image**) is there for the operator who wants
the guarantee. This follows **phase-35 D4** (warn — a keyless remote Qdrant is the operator's own
risk) rather than **phase-37 D4** (refuse — a keyless LAN inference port is everyone's). The line is
whether the bad outcome is the operator's own: a slow box is theirs, an open GPU is not. The `gpu`
block stays **off `/health`**, which is unauthenticated (phase-37 D5) — a box's hardware inventory is
not for anyone who can reach the port. *Considered and declined:* reporting which device Ollama
actually chose per model; `ollama ps` knows, but reaching it means an Ollama-specific method on
`IInferenceBackend`, which is rule 1. The docs point at `docker exec … ollama ps`.

**D7 — No model is baked in, pulled at boot, or managed by a new key.** An image with a model is a
9 GB image that is wrong for everyone who wanted a different one; pulling at boot reaches the
internet from a GPU box nobody said it could (phase-36 D6 refused that shape for the installer); and
a `PreloadModels` key is model management on the node, which phase-37 D1 declined. `docker exec …
ollama pull` is the interface, and `OLLAMA_MODELS=/data/ollama` is why the pull survives the
container — **the difference between a multi-GB re-download per `docker run` and none**, which is
why the volume is documented as required rather than optional. In a *mesh*, the bundled node reports
`SupportsModelManagement=true` (phase-26 D3) over an Ollama it genuinely controls, so
`/api/admin/models/{model}/ensure` pulls into it from the console — the first time that has been
true of a container.

**D8 — The container's surface is InferHub's API.** No `EXPOSE 11434`, no `OLLAMA_HOST` override:
publishing Ollama's own port would put an unauthenticated inference endpoint beside one that refuses
to start without a key (phase-37 D4), on the same GPU. Every `OLLAMA_*` env var **passes through
with no config surface of ours**, because the supervisor spawns a child that inherits the
environment — do not mirror them into `OllamaOptions`.

**D9 — amd64 only, NVIDIA only, Ollama pinned and checksummed.** `ARG OLLAMA_VERSION` +
`ARG OLLAMA_SHA256`, verified at build; a floating `latest` would mean two builds of the same
InferHub tag contain different inference engines, making "it worked in 3.7.0" unanswerable.
`-rocm` and `-mlx` are not downloaded; arm64 is excluded because CUDA-in-a-container there means
Jetson, which needs the jetpack bundles and hardware to test on. **Do not prune `lib/ollama/`** — it
ships more than one CUDA major to match old and new drivers, and a pruned bundle does not fail
loudly, it falls back to CPU on somebody's older driver and looks like a slow model, which is D6's
failure with the evidence removed.

**D10 — Vector-store-only is a supported mode and one key, not a fourth image.**
`Ollama:Supervisor:Enabled=false` and nothing ever starts the bundled binary, because the supervisor
is the only thing in the image that would. The node serves the corpus, `/api/vector/{c}` takes
**client-supplied vectors** (so no embedder is needed at all), and it reports zero models — the
honest answer, which makes a chat request fail cleanly rather than hang. Two limits stated in the
docs rather than discovered: document *ingestion* needs an embedder (bring vectors, or point
`Backend:Type=openai` at one elsewhere), and it is still the 4 GB image — somebody who only ever
wants this should use the plain one, which does solo retrieval identically at 340 MB.

**Rule 5 survived again** — in the only way it could. Zero new `PackageReference`s, no `.csproj`
touched, `InferHub.Shared.csproj` still empty, and the plain images unchanged in size.

### Phase 40 (the capability seam) — also load-bearing

**D1 — The unit of routing is `(capability, model)`, and a node that declares nothing is read as
today's node.** `NodeCapability(Kind, Models)` rides on `NodeRegistration` and `NodeModels`, both
optional. **Null means "not declared" and resolves to `chat` + `embed` over every reported model —
byte-for-byte the pre-v3.8 semantics.** An *empty* list is a declaration that this node serves
nothing, and the two must never be conflated. The default is materialised in exactly one place,
[NodeCapabilityResolver](src/InferHub.Coordinator/Services/NodeCapabilityResolver.cs), and cached on
the registry entry, which is why **no call site anywhere branches on "is this an old node"**. A
required field with a migration was rejected for phase-33 D3's reason: a fleet is upgraded one box
at a time, and a contract that only works when everything is on the new version has an outage in it.
`CapabilityRoutingTests.ANodeThatDeclaresNothingIsRoutedExactlyAsBefore` is the load-bearing test in
the suite — not the new behaviour, the old one.

**D2 — Capabilities are declared by composition, never guessed, and the only knob is subtractive.**
[BackendCapabilities](src/InferHub.Node/Capabilities/BackendCapabilities.cs) derives them from the
backend's own model list; `Node:Capabilities:Disabled` narrows it. **Nothing infers what a model is
*for*.** Ollama's `/api/tags` does not say, and a name heuristic ("it has 'embed' in it") is
phase-29 D5's capability registry by the back door — built, believed, and wrong for somebody. So an
embedding-only box is expressible in one line of config and is not detected. A hand-maintained
*additive* list was rejected too: it drifts the day someone runs `ollama pull`, and a node that
claims a capability it does not have is worse than one that claims none, because the hub routes to
it and the client gets the error.

**D3 — Work with no Ollama shape gets its own contract; rule 6 is bounded rather than weakened.**
See rule 6 above. `InferenceJob.Kind` with a foreign body was rejected — two lines today, and every
future reader has to discover which jobs the rule is true of. What is *not* duplicated is the
machinery: when phase 41 dispatches a `ToolJob` it goes through the same job registry, the same
stream plumbing and the same failover, and its hub method **must not declare a `CancellationToken`
parameter** (the client-to-server binder trap that hung every stream for several releases). *The
`ToolJob`/`ToolResult` records themselves are deliberately **not** in 3.8 — see the deviation note
below.*

**D4 — A capability nobody provides is a `503` with `Retry-After`; a model nobody holds is still the
`404`.** Phase-25 D4 makes "not allowed" and "does not exist" indistinguishable, and that is an
*authorization* answer. "This model is here but nobody will chat with it" is a *fleet-state* answer,
the same category as saturation — so it gets the saturation shape. Admission runs first, so the 503
can never be used to probe for a model a client's scope hides: it only ever reflects models the
caller already reaches. `Retry-After: 30` is a hint and is documented as one — a node with the
capability may connect at any time, or never.

**D5 — Solo mode enforces the same key at the edge, because one key must not mean two things.**
Phase-37 D9's shape (`Node:MaxConcurrency` is advisory in a mesh and enforced in solo): the meaning
is unchanged and only the enforcer moves, from the router that is not there to the node that is.
Same 503, same `Retry-After`, in both dialects. **Edge only** — solo retrieval still embeds its own
corpus with `embed` disabled, because the node's own corpus is not somebody sending it work.

*Recorded deviations from the phase brief, on purpose:*
- **The `ToolJob` / `ToolChunk` / `ToolResult` / `ToolAttachment` records are deferred to phase 41.**
  The brief listed them here as "contracts only, nothing dispatches them". That is the same category
  the phase's own non-goals refuse for the audio endpoints ("a documented feature that does not
  work"), and a *shipped* wire contract is harder to reshape than one that never shipped — phase 41
  will have a real consumer to shape it. D3's reasoning stands and is recorded above; only the
  records wait.
- **`NodeCapability` carries `Kind` + `Models` and not the brief's `Streaming` / `MaxConcurrency`.**
  Nothing in this phase reads either, and a wire field nobody reads is a field that is wrong by the
  time somebody does.
- **The node declares on the model report, not at registration.** At registration it has not asked
  the backend what it holds, and asking first would mean a node with a dead backend never registers
  at all — the opposite of phase-36 D7, which exists so a broken box is *visible* and unrouted. The
  field is on `NodeRegistration` too, for a node whose capabilities do not follow from a backend.
  A message that carries no declaration never erases one, so the re-register-then-re-report order of
  a reconnect cannot open a window where an embed-only node takes chat.

**Rule 5 survived again.** Phase 40 added **zero** new dependencies and `InferHub.Shared.csproj` is
still empty.

### Phase 41 (the tool runtime) — also load-bearing

**D1 — The node speaks a *process protocol*, not Python. This is rule 5 in its strongest form, and
it is the decision the phase exists to get right.** The obvious move is Python.NET or CSnakes: call
`faster-whisper` in-process, no serialisation, no child to supervise. It was rejected on three
grounds, and the second is the one that would have hurt. It is a **native binding** — the heaviest
class of dependency there is — pinning `InferHub.Node`, a project whose dependency list is *two*
packages, to a CPython ABI. **One bad `import` takes the node down**: a segfault in a native
extension loaded into this process is not an exception you catch, it is a process that vanishes
mid-stream taking every in-flight inference job with it, whereas a child process that segfaults is a
log line and a restart. And it **forecloses the general case** — a tool that is a Go binary, an
`ffmpeg` invocation or a vendor's CLI is free here and impossible there. So
[ToolWorkerProcess](src/InferHub.Node/Tools/ToolWorkerProcess.cs) knows how to start a process,
write a line, read a line and kill it, and nothing in the node knows what language a worker is in.
`python/` exists because that is where the libraries are, not because the runtime is.

**D2 — Opt in twice, and the second key is a list rather than a boolean.** `Tools:Enabled` (default
**false**) consents to the feature; `Tools:Allowed` names the manifest ids that may start. A
manifest on disk that is not in the list is **loaded, logged and never run** — nothing is
discovered-and-executed, and `ToolSecurityTests` asserts the log line, because "I put the file there
and nothing happened" is otherwise a silent afternoon. This is phase-36 D6's shape (`Enabled` ≠
`AutoInstall`) with a sharper reason: **phase 43 lets a coordinator turn a node's capabilities on
and off, and `Tools:Allowed` is the ceiling it can never raise.** One boolean would collapse "the
operator enabled tools" and "the hub may run any tool present on this box" into a single consent,
which is a coordinator compromise away from fleet-wide RCE. The list *is* the grant, exactly as
phase-31 D1's `Collections` scope and phase-22 D5's `Fallback:ModelMap` are.

**D3 — The manifest declares capability, command and limits; `command` is an argv array and there is
no shell, ever.** A command line assembled by concatenation is one quoting bug away from being an
injection point, and the values around it (model names) come from requests — so
`ProcessStartInfo.ArgumentList` only, and **nothing from a request ever reaches the argv**: model,
options and paths all travel in the protocol, over stdin, after the process is running. A manifest
whose `command` is a *string* is refused **by name**, because every shell, CI config and Docker
`CMD` accepts one and a `JsonException` about a token type would never tell an operator which field.

**The child's environment is built, not inherited.** `ProcessStartInfo.Environment` is pre-populated
from this process, so it is **cleared** first and a short list added back (`PATH`, `HOME`, `LANG`,
`LC_ALL`, `TMPDIR`, `USER`, `SHELL`, plus what Windows needs to start a process at all) followed by
the manifest's `env`. The node's environment holds `Auth__NodeEnrollmentSecret`,
`LocalApi__ApiKeys__0` and whatever else the deployment set; handing all of it to a third-party
script is a credential leak wearing a convenience's clothes, and unlike most leaks it is invisible
because the script never has to *do* anything for the exposure to be real.
`ToolSecurityTests.AWorkerDoesNotInheritAVariableTheNodeHasAndTheManifestDidNotName` runs a real
child process and asks it; a stubbed `Process` would echo whatever the test author already believed.

**D4 — Workers are warm and pooled, not spawned per request, and `MaxWorkers` defaults to 1.**
`faster-whisper` spends seconds loading weights; per-request spawn would put that on every
transcription and thrash a card. The default of **1** is deliberate: a second Whisper process on the
same GPU is two copies of the weights and a memory error at the worst possible moment, so
parallelism is raised knowingly. Requests past the cap **wait** up to `Tools:QueueMaxWaitSeconds`
and then get **503 + `Retry-After`** — the same status and header as phase-25 D5's `RequestQueue`
and phase-37 D9's local gate, so a client's retry logic behaves identically whichever limit it hit.

**D5 — One JSON object per line, and bytes go through files rather than the pipe.** Frames are
`hello`/`ready`/`request`/`chunk`/`result`/`error`/`log`/`ping`/`pong` on stdin/stdout;
[ToolProtocol](src/InferHub.Shared/Contracts/ToolProtocol.cs) is the whole of it. **`stderr` is not
protocol** — it is pumped into the node's log under the tool's id, because that is where a Python
traceback goes and a traceback is the single most useful thing a tool author ever sees. Binary is
written to a per-request scratch directory and the frame carries a **path**; base64 over stdio was
rejected because it is 4/3 the bytes and materialises the payload as a string in *both* runtimes at
once (a 25 MB audio file → ~33 MB of .NET string + ~33 MB of Python `str` + the decoded copies), for
a handoff both sides' libraries would rather do with a path anyway.

The scratch directory is deleted in a `finally`, **always** — after success and after every failure
— and a worker that names an output file **outside** it is refused and logged rather than read: that
would turn "a tool ran" into "a tool exfiltrated a file through the client-facing API".

> **`Tools:ScratchDirectory` is the fifth instance of the container permissions trap** (phase-21 D7,
> the node id, phase-30 D3, phase-38 D4). The default stays relative so bare metal and Windows work,
> and **both** node Dockerfiles set `Tools__ScratchDirectory=/data/tools/scratch` under the existing
> `chown app:app /data`.

**D6 — A tool failure is a failed job, never a failed node, and never a hung one.** Every level has a
deadline and a bound: `startTimeoutSeconds` for `hello`→`ready`, `requestTimeoutSeconds` per request,
a `ping`/`pong` probe for idle workers, kill on timeout, and a restart budget with backoff **lifted
from `OllamaSupervisor` rather than re-derived** (3 attempts per 10 minutes, 10s doubling — phase-36
D4). Past the budget the pool **stops starting workers, logs once at Error, withdraws its
capabilities, and keeps probing** every `RecoveryProbeInterval`, so a tool that recovers is noticed
without a restart and one that does not cannot spin.

The withdrawal is the phase-36 D7 mechanism reused, not a health field invented: empty capabilities
in the next model report is what unroutes the node, and `IToolRuntime.CapabilitiesChanged` pushes
that report immediately the way `IBackendSupervisor.Recovered` does — otherwise the hub keeps routing
transcriptions at a node that stopped transcribing for up to `ModelRefreshInterval`.

**A worker that failed its request is terminated, not disposed politely.** The five-second grace on
`DisposeAsync` exists so a *cooperative* worker can close a half-written file; one that blew its
deadline is by definition not cooperating, and the pool's slot is released only once the process is
gone — so a polite wait is five seconds of the *next* caller's queue budget. Found by the test: the
follow-up request after a wedge failed until `TerminateAsync` existed.

**D7 — This is process isolation, not a sandbox, and the docs say so in those words.** A worker runs
as the node's user, with the node's filesystem and the node's network. Dropping the environment (D3)
removes the most obvious credential leak and that is the honest extent of it. **A tool you did not
write and did not read has your box.** Stating that plainly *is* the decision — the alternative,
implying safety by listing mitigations, is how somebody ends up running a random
`whisper-plus-telemetry.py` from a gist on a machine holding their fleet's enrollment secret. Real
isolation (a container per tool, seccomp, a user namespace) is deferred and named; the current answer
is to run untrusted tools in their own container and point a manifest at it, which the protocol
permits because a "process" that is `docker exec` is still a process.

**D8 — Solo mode gets tools on the same day, because it is the same executor.** Phase-37 D2's framing
a third time: the hub's endpoints are a formatting layer over the node's executor with routing
deleted. `ToolExecutor` is driven by `CoordinatorConnection` in a mesh and by `LocalApi/` in solo, and
neither knows about the other. A solo bundled node that transcribes with one `docker run` is where
this track is heading; splitting the local path across releases would mean building it twice.

*Recorded deviations from the phase brief, on purpose:*

- **A client-facing `POST /api/tools/{capability}` shipped on the hub too, not only in solo mode.**
  The brief named the solo route and left the mesh with dispatch but no way to invoke it, which is a
  tool runtime that is furniture — and the phase's own acceptance criterion says the echo worker
  round-trips "to a client". It is **generic on purpose** and phase 42's `/v1/audio/*` will sit
  *beside* it rather than replace it: an operator who writes their own tool needs a call InferHub did
  not have to know about in advance. It is under `/api`, which `BearerApiKeyMiddleware` already
  guards — *verified*, not assumed (phase-21 D2).
- **`ToolResult` carries `RetryAfterSeconds`.** Without it the edge would have to *sniff the error
  text* to choose between a 502 and a 503, which is precisely the inference phase-29 D6 refuses to
  make. The node states the fact; the edge renders it. It has a reader in both hosts today.
- **`IToolDispatcher` is a second interface on the same `Dispatcher`**, not four more methods on
  `IDispatcher`. Phase-34 D1's shape: one implementation, and nine existing test doubles are not made
  to fake methods they never call. Tool jobs go through the same job registry, the same in-flight
  accounting and the same `FailForConnection`.
- **A worker may *narrow* its manifest's capabilities at handshake and may never widen them.** A
  Whisper worker that finds one of the two model files it was promised should stop advertising the
  other; a script that could *add* capabilities to its own node would be deciding what traffic the
  fleet sends it. Same ceiling logic as D2.
- **The generic route refuses more than one returned file with a 501 naming the limitation**, rather
  than returning the first and dropping the rest — a lie with a 200 on it. One attachment is returned
  as bytes; none is returned as JSON.
- **`ToolEndpoints` asks the *capability declarations*, not the backend model list, whether a model
  exists at all.** A tools-only node reports zero Ollama models, so the phase-40 D5 "503 vs 404"
  split would have called every one of its models non-existent.

**Rule 5 survived again.** Phase 41 added **zero** new dependencies: `System.Diagnostics.Process` and
`System.Text.Json` ship in the shared framework, `InferHub.Shared.csproj` is still empty, and there is
no Python in any `.csproj` — the reference library in `python/` is copied or vendored, never packaged.

### Phase 42 (STT and TTS for real) — also load-bearing

**D1 — The client surface is OpenAI's audio API, exactly, and this is the phase-21 argument again.**
`POST /v1/audio/transcriptions` (multipart) and `POST /v1/audio/speech` (JSON). Every SDK in every
language already speaks it, so pointing an existing app at your own GPU is a base-URL change;
inventing `/api/tts` would be a second dialect whose only merit is that we designed it. Unlike chat
and embeddings there is **no Ollama dialect for audio**, so there is exactly one client shape and
the node-facing side is a `ToolJob` (phase-40 D3) rather than a translation.

**A worker always answers with the verbose shape — text, segments, duration — and the edge formats
every `response_format` out of it.** `srt` and `vtt` are string formatting on the hub
([TranscriptFormatter](src/InferHub.Shared/Audio/Transcript.cs)), phase-28 D1's Prometheus reasoning
applied to two subtitle formats that are forty lines between them. The alternative — telling the
worker which format to produce — would put SRT timestamp arithmetic inside every worker anybody ever
writes, in whatever language they wrote it in, and the day two workers disagreed about a comma the
bug would look like a model problem. `CultureInfo.InvariantCulture` on the timestamps is
load-bearing for the same reason it is in `PrometheusFormatter`: a decimal comma makes a WebVTT file
silently invalid on exactly the machines nobody runs CI on.

**A format that cannot be produced is a `400` naming the ones that can, never a substitution.** A
caller who asked for mp3 and got a wav has a corrupted file with a confident content type and finds
out in a media player three days later. The edge refuses an unknown value up front; a worker that
cannot encode (no `ffmpeg` on the box) answers with `ToolErrorCodes.UnsupportedFormat` and the edge
renders the 400 **from the code, never from the message** — phase-29 D6's refusal, and the same
shape as phase-41's `RetryAfterSeconds`. That is why `ToolFrame` grew a `code` field.

**D2 — `faster-whisper` for STT, `piper` for TTS, pinned, both CPU-viable.** Chosen for the reason
phase 39 shipped a CPU mode: most boxes that will run this have no spare card, and a TTS that needs
one is a TTS most of this project's users cannot run. Both are permissively licensed, self-hosted,
and phone nowhere. **Pinned by version rather than by hash**, which is a deliberate step down from
phase-39 D9's checksummed tarball and is argued in `python/requirements-tools.txt` at the pins: one
URL with an upstream sha256 is a different shape from a per-platform transitive closure, and a hash
list that is subtly wrong fails a build with something that reads like a network error, after which
the next person deletes the hashes.

**D3 — A third image, not a flag, and the other three are untouched.** `inferhub-node:tools` =
`:ollama` + a Python venv + the two workers (~6 GB). Phase-39 D2 verbatim: the wheels are in a layer
whether a flag is on or off, so a flag would grow every existing coordinator+node stack by ~1.5 GB
for a feature it does not use. `BundledNodeTests.NeitherOfTheOlderImagesLearnedAboutPython` fails if
that leaks, and `TheToolsImagePinsTheSameOllamaAsTheBundledOne` fails if the two images drift on
engine version. An operator on the plain image installs Python themselves and points a manifest at
it — the runtime does not care where the interpreter came from.

**D4 — Weights download on first use, behind a *third* opt-in.** `Tools:AllowModelDownload`, default
**false**, `true` in the `:tools` image. It is not redundant with the other two for the same reason
the second was not redundant with the first (phase-41 D2, phase-36 D6): `Enabled` consents to
running tools, `Allowed` consents to running *these* tools, and this consents to one of them
**reaching the internet from a box whose operator may have deliberately air-gapped it** — the reach
phase-39 D7 refused to do at boot. Choosing the `:tools` image *is* that consent, exactly as
choosing `:ollama` is the consent to run an Ollama. With it off, a worker that needs missing weights
fails the **job** naming the key and the exact pre-fetch command, and the node keeps serving
everything else. The cache is under `/data`, on the volume, so it happens once rather than once per
`docker run`.

The flag reaches the worker as `INFERHUB_ALLOW_MODEL_DOWNLOAD`, **stated** into the child's
environment rather than inherited — which is the only way it could, since phase-41 D3 clears that
environment first. `ToolSecurityTests` drives a real child process and asks it.

**Voices are not fetched at all.** There is no default voice that is right for everyone, and a
confident answer in the wrong language is worse than a refusal.

**D5 — Audio is content, and none of it is kept.** Rule 7 in its most literal form yet: a
transcription request is a recording of somebody's voice and the result is what they said. The hub
buffers the upload for the dispatch and drops it — no temp file, no cache; the node writes it into
the per-request scratch directory deleted in a `finally` (phase-41 D5); **nothing containing audio
bytes or transcript text is logged at any level**, and the line that *is* written carries the model,
the duration and the outcome — not the filename the caller chose, which is metadata about somebody's
day. `AudioPrivacyTests` runs a transcription through a real mesh with a capturing logger at `Trace`
and fails if a known phrase from the fixture appears anywhere in the log or the ledger — the harder
version of `UsageLedgerTests.NoPromptOrCompletionTextExistsAnywhereInTheUsagePath`, which asks
whether a field exists rather than whether a phrase leaked.

**D6 — Concurrency is the tool's, not the fleet's.** `maxWorkers` defaults to 1 (phase-41 D4), so a
node transcribes one file at a time unless an operator raises it knowingly. Because routing is per
`(capability, model)` since phase 40, **a node busy transcribing is still a candidate for chat** —
"my chat got slow when someone uploaded a podcast" is the failure phase 40 landing first prevents,
and it is worth saying in the release notes because nobody can see it working.

**D7 — Usage is metered in the unit the work is actually in.** `UsageRecord` grew `Units` (a double)
and `UnitKind` (`tokens` | `audio_seconds` | `characters`), appended with defaults that describe
today's rows, so every existing consumer and every row already in a Postgres ledger keeps meaning
what it meant. Transcription meters **audio seconds** measured off the decoded file by the worker
(not derived from the upload's byte count, which a variable-bitrate encoding would make a guess);
speech meters **input characters**, counted at the edge because the edge already knows and should
not have to trust a third-party script for a number that appears on somebody's bill.

Phase-25 D3 is unchanged and is why this is safe: these are counts computed from what was processed,
and there is deliberately no field that could hold a sample. Client limits gained
`AudioSecondsPerDay` and `CharactersPerDay` — **separate budgets, each consuming only its own unit**,
because a client whose only limit is `TokensPerDay` could otherwise transcribe a library for free.

> **The Postgres migration is additive and must stay that way.** `ADD COLUMN … DEFAULT` has not
> rewritten a table since PostgreSQL 11, so a ledger with two years of chat in it gains two columns
> in milliseconds — and it runs through `ConcurrentDdl` because two hubs may boot together
> (phase-32 D7). Old rows get `units = 0, unit_kind = 'tokens'`, which is why `UsageAggregate` reads
> the **token columns** for tokens and `units` only for the two new kinds. `UsageAggregate` also
> gained two separate columns rather than one `units` sum: a client that chatted and transcribed has
> rows in two units under one model grouping, and a single sum would add seconds to tokens and
> produce a number wrong in a way no reader can detect.

*Recorded deviations from the phase brief, on purpose:*

- **The worker error `code` field is new machinery the brief did not name.** The brief asked for a
  400 on an unproducible format without saying how the edge would know, and the only alternatives
  were sniffing the error text (phase-29 D6 refuses it) or hard-coding the format matrix on the hub
  (which would be wrong the day a worker gains `ffmpeg`). The node states the kind; the edge renders
  it. Deliberately a very short list — a code nobody renders is a code that is wrong by the time
  somebody reads it.
- **A manifest capability with an empty `models` list is an open set** — the one widening anywhere
  in the tool runtime, and it is bounded: the **kind** is still the manifest's to grant, and every
  name a worker reports for it corresponds to a file the operator put on the box. Piper's models are
  voice files dropped into a directory, and no list written in advance survives the first new voice
  — the drift phase-40 D2 refuses for backend models. `models` *omitted* is still a mistake, so the
  two are distinguished by null-versus-empty rather than collapsed.
- **`/v1/audio/*` sits beside `/api/tools/{capability}`, not over it.** The generic route stays for
  the operator who writes their own tool, exactly as phase 41's deviation note promised.
- **The requirements are version-pinned, not hash-pinned.** See D2.
- **No `/v1/audio/translations`.** One flag on the same worker; shipping an untested surface to look
  complete is how a feature list starts lying.

> **v3.10.0 was dead on arrival, in three separate ways, and v3.10.1 fixed it the same night.**
> The fifth time (v2.5.1, v3.0.1, v3.5.1, phase-32 D7) — every one found by pulling the published
> image, none by a test. The three that are worth carrying forward as rules:
>
> 1. **SignalR's default `MaximumReceiveMessageSize` is 32 KB, and exceeding it kills the
>    connection rather than failing the message.** Every real `/v1/audio/speech` through a
>    coordinator was a 500 that also dropped the node. Phase 41 had verified attachments across a
>    real wire — with a **16-byte** file, four orders of magnitude under the cap, which proved the
>    plumbing and nothing about the size.
>    [NodeHubLimits](src/InferHub.Coordinator/Hubs/NodeHubLimits.cs) now *derives* the wire cap from
>    `Tools:MaxAttachmentBytes` (base64 is 4/3, plus an envelope), because two numbers that have to
>    agree are two numbers that will not. **When a phase adds bytes to the wire, test a payload past
>    32 KB or the wire test is decoration.**
> 2. **The interpreter that builds a venv must be the interpreter that runs it.** The venv was built
>    in a `debian:trixie-slim` stage (Python 3.13) and copied into the `aspnet:10.0` runtime (3.12);
>    site-packages live under `lib/python3.13/` and nothing was importable. Everything *looked*
>    right — manifests loaded, `/api/status` answered — and the first transcription would have died
>    on an `import`. It is now built in the final stage, and `Dockerfile.tools` **asserts the import
>    at build time**, which is the only reason this cannot ship again.
> 3. **An open model set has to start a worker eagerly, or it deadlocks.** Nothing declares the
>    capability until a worker reports; no worker starts until a request is routed; nothing routes
>    to an undeclared capability. A TTS node with a voice on its volume refused `speak` forever.
>
> And one that is a design lesson rather than a bug: **a `libcuda` the driver injects is not a CUDA
> runtime.** CTranslate2 needs `libcublas`/`libcudart`, which phase-39 D1 got for free because
> Ollama ships its own — they were already in the image, just off the worker's loader path. The
> Whisper manifest's `env` points `LD_LIBRARY_PATH` at them, and the worker now **falls back to the
> CPU loudly** rather than failing the job: phase-39 D6's line, that a card which cannot be used is
> the operator's problem while a failed job is everyone's.

> **A validator written for a hand-edited file is wrong for an image.** `Tools:Allowed` refused a
> blank entry, on the good reasoning that a blank id hides a typo behind an index. But an array that
> arrives from an image's environment cannot have an element *removed* — `-e Tools__Allowed__1=` is
> the only lever `docker run` gives you — so the `:tools` image could not be run with one tool, or
> with none: `-e Tools__Enabled=false` failed startup and no second flag helped. Blanks are ignored
> now. Nothing is hidden by it: a manifest not in the list is still loaded and still logged **by
> name** as not started, which is the signal the strict check was standing in for.

**Rule 5 survived again.** Phase 42 added **zero** new `PackageReference`s, `InferHub.Shared.csproj`
is still empty, and there is no Python in any `.csproj` — the Python is a `pip install` in one
Dockerfile, which is the same category as phase-39's `curl`.
`BundledNodeTests.NoProjectReferencesPythonAndTheSharedProjectIsStillEmpty` asserts both.

### Phase 43 (node profiles) — also load-bearing

**D1 — The node's config is a ceiling the hub cannot raise, and the clamp runs *on the node*. This
is the decision the second half of the tools-and-fleet track turns on.**
[NodeProfileClamp](src/InferHub.Node/Profiles/NodeProfileClamp.cs) is a pure function from
`(LocalCeiling, NodeProfile?)` to `(effective, applied[], refusals[], ensure[], remove[])`. A profile
may **narrow** what a node does and may never widen it: it can switch a capability off but cannot
re-open one `Node:Capabilities:Disabled` closed; it can stop a tool but cannot introduce a manifest,
a command, an interpreter or a path (phase-41 D2's `Tools:Allowed` is the grant, and the hub is not a
grantor); it can lower `MaxConcurrency` but not raise it, because that number is a statement about
hardware the operator owns.

**A clamp that runs on the hub is a clamp an attacker skips by not being the hub.** The whole point
is that a compromised or misconfigured coordinator cannot turn a fleet of GPU boxes into
fleet-wide RCE. The hub *also* validates a little, for a better error message; the node's copy is the
one that is load-bearing, and `ProfileClampTests` drives the node's real application path with
hostile profiles — a tool id that is a path, an interpreter, a shell one-liner.

**No new authority over data, either.** `models.remove` deletes weights, which is destructive — but
phase 26 already gave the hub `DELETE /api/admin/nodes/{id}/models/{model}`, so profiles add no
authority there that a coordinator did not have. If a future field would give the hub something a
node's own config never granted, it does not belong in a profile.

**D2 — Desired state, not commands, because a node reconnects.** Two directions, and both exist so
the hub does not have to remember who has which revision:
- **Push** — `NodeProfileCoordinator.ReassertAsync` re-evaluates every connected node after a write
  or a delete and sends `ApplyNodeProfile` / `ClearNodeProfile` down the outbound connection
  (phase-26 D1 — the hub still never dials a node).
- **Pull** — the node invokes `RequestNodeProfile` right after `Register`, and a hub older than
  v3.11 answering with an error is a **debug log and a node that runs its own configuration**, not a
  failed registration (phase-40 D1's mixed-fleet rule).

Convergence is idempotent **by revision**: applying the same `(name, revision)` twice changes nothing
and says so, which is what makes the reconnect path safe to run unconditionally — otherwise a
rebooted node would re-pull forty gigabytes of weights on every reconnect. Revisions are monotonic
per profile and **never reused, including across a delete and a re-create under the same name**.

**Model commands are not awaited.** A profile arrives during registration and a pull is minutes;
waiting would hold up the connection meant to carry its progress. They go down the phase-26
`ModelCommand` path — the one that already exists — and the state reports them as `pending`.

**D3 — Profiles are rule 4's third recorded exception, and the reasoning is phase-30 D2's.**
`Fleet:Profiles:Persistence` = `none` (default) | `file` | `postgres`. Rule 4 survives because **a
lost profile costs the fleet reverting to the operator-configured default on each box** — never a
wrong answer, and never a capability nobody granted. The node's own config remains the authority for
what is *possible*; a profile is only a preference over it. If a future change ever makes a profile
the authority for something a node cannot re-derive locally, that reasoning has stopped being true
and the design has drifted — stop.

`FileProfileStore` is `FileAffinityStore`'s discipline (append log + compacted snapshot, atomic
move), with one difference: it **flushes to disk on every write**, because a profile is not a hint.
`/data/profiles` is the **sixth** instance of the container permissions trap — the default stays
relative for bare metal, the image sets the absolute path.

**D4 — Selectors are exact matches, and two matching profiles is an error the hub reports.**
`{nodeId}` or `{labels}` with **every** pair matching; no glob, no expression language (phase-31 D1's
footgun, aimed here at a security-relevant boundary). A node matched by two profiles is **not**
merged and **not** resolved by creation order: the hub sends nothing, the node keeps its last applied
profile, and `/api/status` and the console show `conflict`. **A selector that names nothing matches
nothing**, never everything — `PUT` refuses one with a 400.

**D5 — Every application is an audit event.** `profile.apply:{name}@{rev}` by the admin caller on a
push; `profile.refused:{name}@{rev} (n)` by `node` when refusals come back. Same category as
cordon/uncordon — an admin action against one node — unlike phase-22 D5's cloud-burst events, which
were kept out of the audit log precisely because they were not.

**D6 — A node that cannot honour a profile keeps running, loudly.** Refusals are per item, never
all-or-nothing. A profile is never a startup dependency and **never restarts the node** — a switched
-off tool pool is *suspended* (workers stopped, capabilities withdrawn through phase-36 D7's
mechanism, `ToolWorkerPool.Suspended` excluded from candidate selection) and a later profile resumes
it in place. A node that rebooted on a hub instruction is a node an operator cannot keep up, and
in-flight jobs would die for a config change.

**The effective concurrency cap lands on the registry entry via `ReportProfileState`**, not by a
re-registration: `NodeRegistry.SetEffectiveConcurrency` sets it and `NodeSnapshot.MaxConcurrency`
resolves `effective ?? registered`, so the saturation check reads the right number on the next
dispatch.

> *Test-harness lesson, not a product one:* a hub that throws while activating `NodeHub` (a missing
> DI registration — `Dispatcher` needs `ThroughputTracker`) refuses the handshake, and the node's
> retry loop is **correct** to keep trying every `Coordinator:RetryDelay` forever. In a suite with
> `builder.Logging.ClearProviders()` that reads as a hang and then a dead test host. If a new mesh
> fixture never registers, check the hub's composition before suspecting the wire.

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

### Phase 46 (text to image — Stable Diffusion on the fleet) — also load-bearing

**The capability seam took a whole new modality with no protocol change**, which is what phase 40 was
built to make possible and is worth saying out loud: `image` is one more `NodeCapability` kind, a
`ToolJob` carries the request, and neither `InferenceJob`, the dispatcher, the router nor the mesh
learned anything.

**D1 — The client surface is OpenAI's Images API, and the extensions are headers.**
`POST /v1/images/generations` on both hosts. `steps`, `guidance` and `seed` travel as
`X-InferHub-Image-*` headers (phase-24 D5's shape, including "an unknown value is a 400, never a
silent fallback") because a body field would collide with whatever OpenAI adds next and would make a
typed SDK's request object wrong. `seed` is *also* a body field, because it is the first thing anyone
reaches for. **`negative_prompt` is a body field and never a header**, and that is rule 7 rather than
taste: it is the caller's own words, and a header is the one part of a request every proxy in the
path writes down by default.

**D2 — `image` is its own capability kind, and the model name is a *recipe id*.** A client sends
`sdxl`, not `stabilityai/stable-diffusion-xl-base-1.0`. A repo id is a location: it contains a slash
every router, path and metrics label has an opinion about, and it changes when a model is re-hosted —
which is not hypothetical, because `runwayml/stable-diffusion-v1-5` was **withdrawn** and one of the
two shipped recipes points at its replacement. Generating is deliberately not editing; phase 50 adds
`image-edit` as a second kind rather than a per-model operation list, because the router filters on
`(kind, model)` and nothing else.

**D3 — A recipe is a model; a manifest is a tool.** `python/recipes/*.json` (repo, **pinned
revision**, pipeline class, aspect buckets, defaults, `cpuViable`) beside
`python/manifests/diffusion.json`. Two files on purpose: the manifest is the operator's ceiling and
is what `Tools:Allowed` names, while recipes are a catalogue the tool reads — collapsing them would
make every new model an entry in `Tools:Allowed`, and a phase-43 profile could then not enable a
model the operator had not pre-named. **A recipe with no `revision` is skipped and logged by name**;
without a pin, "which weights were in 3.14.0" has no answer (phase-39 D9, asked of a Hugging Face
repo instead of a tarball).

**D4 — The response budget is bytes, not a count, and it is clamped by the attachment cap.** `n` is
bounded by `Images:MaxBatch` (4) *and* by an upper-bound estimate — `width × height × 4 × n`,
uncompressed RGBA, which no three-channel PNG can exceed — checked at the edge **before a step
runs**, with the refusal naming the largest `n` that fits. `ImageEdgeOptions.Resolve` then takes the
**smaller** of `Images:MaxResponseBytes` and `Tools:MaxAttachmentBytes`, because the attachment cap
is what sizes `NodeHubLimits.ReceiveSizeFor`, and exceeding a SignalR message cap **tears the
connection down** rather than failing the message. That is the v3.10.0 bug, and an operator raising
one key without the other must not be able to reproduce it. `ImageWireSizeTests` pushes 3 MB across a
real wire and asserts **the node is still registered afterwards** — which is the assertion a green
suite got wrong last time, when phase 41 "proved" attachments with a 16-byte file.

**D5 — There is no URL, and that is rule 4 and rule 7 pointing at the same refusal.**
`response_format=url` is a `400` naming `b64_json`. Serving a URL means the hub keeps the bytes;
keeping the bytes means an image store, a retention window, a deletion endpoint and a question about
whose pictures those are. Considered and rejected: writing them under `/data` and serving them from
the solo API — twenty lines, a nicer demo, and every solo node quietly becomes an image archive on
somebody's laptop (phase-23 D2 refused the same trade for uploaded documents).

**D6 — The hub never decodes a pixel, so there is no image library.** Everything it knows comes from
the worker's result frame. Nothing measures, resizes, re-encodes or validates a raster — which is
what keeps rule 5 intact through a phase that is *about* images. What the edge does validate is the
**request** (the `WIDTHxHEIGHT` grammar, the 64–4096 bounds, the multiple-of-8 rule, the byte
budget), because that is cheap, it is the caller's error, and catching it saves a minute of GPU.

> **Recorded deviation from the phase brief:** the brief had the edge validate a size against the
> recipe's aspect buckets. It cannot — a recipe is a file on the node and the hub has no model
> catalogue until phase 48. The **worker** is the authority, answers `invalid_request` with the
> buckets named, and `ImageRenderer` renders the 400 without reading the message (phase-29 D6). One
> round trip to find out; the alternative is publishing a catalogue over the mesh, which is a phase.

**D7 — CPU is claimed per recipe, from a measurement, never as a checkmark.** `sd15` at 512² is tens
of seconds on a modern core; `sdxl` at 1024² is minutes. So `cpuViable` is a **recipe** field, and on
a CPU-only node the recipes without it are simply **not declared** — the hub never routes to them
(41 D6's withdraw-on-failure, applied before the first failure). `Tools:Image:RequireGpu` defaults
**true** and the worker refuses to start with no CUDA, naming the key to unset; a tool that loads
happily and then serves four-minute requests is a node the fleet keeps routing to, and every caller
pays for the discovery. `Tools:Image:AllowSlowCpu` is the third step for somebody who has read both
numbers.

**D8 — No bundled safety classifier, and if one is ever added it fails the job rather than returning
a black square.** `diffusers`' `StableDiffusionSafetyChecker` returns a **black image** on a
positive, which is disqualifying on its own: the operator gets a bug report rather than a policy
signal, and the failure is indistinguishable from a broken VAE, a bad seed or an OOM. Bundling one
would also mean shipping a model that decides what a self-hosted box may produce — which is precisely
the decision people run a self-hosted box to keep. The docs say so plainly, in the same voice as
41 D7's not-a-sandbox sentence.

**D9 — The fifth image does not stack, and has no Ollama in it.** `inferhub-node:diffusion` is built
from the **plain** node plus PyTorch and diffusers (~9 GB), not from `:tools`. Three reasons, in
order: stacking reaches ~15 GB and every pull pays for it; a card running a diffusion pipeline has no
room for a chat model beside it, so bundling one would ship a combination the docs would have to tell
people not to use; and **the mesh is the composition mechanism** — run `:diffusion` on the card and
`:ollama` next to it, and phase-40 routing sends `image` to one and `chat` to the other. A combined
`:all` image is deferred and named. `BundledNodeTests` asserts the four older images never learned
about torch, and that this one's venv **imports at build time** — the v3.10.0 failure, turned into a
`docker build` step rather than a thing to remember.

**Metering: `megapixel_steps`, and deliberately not "images".** `width × height × steps / 1e6`,
summed over what the worker actually produced (a recipe may clamp either, and metering the *asked*
figures would bill for work not done). A 512² image at 4 steps and a 2048×1024 one at 30 steps are
both "one image" and the second is **47×** the work; a counter that bills them the same is wrong in a
way that scales with how much somebody uses the expensive path. `ClientLimits.MegapixelStepsPerDay`
rejects with phase-25 D4's shapes. **Phase 46 needed no Postgres migration** — phase 42's generic
`units` + `unit_kind` pair took a fourth unit, which is the payoff of not having added a column per
unit.

**Rule 7, in a form it had not met: a prompt is content.** A transcript is content because it is what
somebody said; a prompt is content because it is what somebody *wanted*, and the picture is the
answer. Nothing logs a prompt at any level, on either host; the log line carries the model, the image
count, the megapixel-steps and the outcome. `ImagePrivacyTests` asserts it against a capturing logger
at `Trace` and checks the scratch directory is empty after success **and** after failure.

**Rule 5 survived again.** **Zero** new `PackageReference`, no image library anywhere in C#, and
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`. PyTorch is a
subprocess, exactly as phase-41 D1 requires.

> ### v3.14.1 — what v3.14.0 got wrong, and the two mechanisms it needed
>
> **v3.14.0 was dead on arrival for `sdxl` on a fresh volume.** Found by pulling the published image
> and running it, which is the sixth time (v2.5.1, v3.0.1, v3.5.1, v3.10.0, the phase-32 D7 note).
>
> **Bug 1 — `variant` is not `dtype`, and conflating them doubled every download.** `dtype` is what
> the weights are cast to in memory; `variant` is which **files** are fetched. These repos carry
> both `unet/diffusion_pytorch_model.safetensors` (fp32, 10.3 GB for SDXL) and
> `…fp16.safetensors` (5.1 GB), and `torch_dtype=float16` alone takes the fp32 one and casts it
> down. The recipe said `"dtype": "float16"`, the docs said "~7 GB fp16", and 13 GB landed in the
> cache. **Both recipes now carry `"variant": "fp16"`**, `_from_pretrained` is the one place a
> pipeline is constructed so serving and prefetching cannot drift, and a repo without the variant
> falls back **loudly**.
>
> **Bug 2 — weights were fetched inside the request that first named the model.** The manifest
> allows 900 s and that budget included the download: two consecutive `sdxl` calls each returned
> **502 after 899.99 s**. Phase 48 D4 in the roadmap says *"weights are pulled by an explicit
> command, never lazily inside a request"* — written for FLUX at 24 GB, and phase 46 shipped the
> lazy path anyway on a model big enough to hit it.
>
> The fix is that **a recipe is declared only once its weights are proven loadable**, with a
> background thread fetching and the worker re-declaring as each lands. Readiness is a marker file,
> because the obvious checks lie: `snapshot_download(local_files_only=True)` and
> `DiffusionPipeline.download(local_files_only=True)` both return happily with the UNet entirely
> absent (verified against a half-downloaded cache). Only `from_pretrained(local_files_only=True)`
> asks the question the next request asks — and it *is* the load, so the prefetch does it once and
> records the answer.
>
> **Two node-side mechanisms, and one of them had been dead code since v3.9.0.**
>
> 1. **A late `ready` re-declares** (`ToolWorkerProcess.CapabilitiesRedeclared` →
>    `ToolWorkerPool.RefreshCapabilities`). `ExecuteAsync`'s default branch used to discard it with
>    a comment naming it. The narrowing clamp is unchanged and still applied on the node: a worker
>    cannot widen its own grant by re-declaring any more than it could at handshake.
> 2. **The maintenance loop now pings idle workers.** Phase-41 D6 specified a ping/pong liveness
>    probe, `PingAsync` was written for it, and **nothing ever called it**. It matters here because
>    an idle worker has nobody reading its stdout, so a late `ready` sits in the pipe until
>    something drains it. **The probe takes the concurrency slot** — without that, maintenance
>    holding the only worker out of the idle stack would let a concurrent request start a second
>    process, which is two copies of a multi-gigabyte model on one card.
>
> `ToolRedeclarationTests` drives all of it through a real child process, including the guard on the
> guard (a worker that never re-declares must not make the node re-report twice a minute forever).

### Phase 47 (jobs, progress, cancel) — also load-bearing

**D1 — The async surface is InferHub's own, under `/api`, and that is not a second dialect.**
OpenAI has no asynchronous Images API to adopt. Phase-21's rule is "adopt the dialect clients already
speak", and where there is none the rule does not say "invent an OpenAI-shaped one" — it says what
phase-40 D3 said about `ToolJob`: **work with no existing shape travels as its own honest contract.**
So `/api/images/jobs` and its four companions, in the house style of
`/api/admin/models/{model}/ensure`, under the `/api` prefix `BearerApiKeyMiddleware` already guards.
**Considered and rejected: a `background: true` field on `/v1/images/generations`** returning a
non-OpenAI body — one route answering two incompatible shapes depending on a flag, which every typed
SDK gets wrong. The repo has refused this trade five times (21 D3, 22 D1, 34 D1, 38 D2, 40 D3).

**`/v1/images/generations` became "submit a job and wait for it", and nothing about it changed for a
caller.** Both surfaces queue in one line and are metered by one code path; two paths to a GPU with
two ideas of fairness is how a fleet grows a fast lane nobody documented. `ImageSyncCompatTests` pins
the envelope field-for-field. What is new is a bound: past `Images:SyncMaxWaitSeconds` (120) it is a
`503` naming the job id and the async route, and **the job keeps running** — cancelling it because an
HTTP client got bored is the caller's decision.

> **The refactor lost a status once, and the suite caught it.** Flattening a failed job to a `502`
> at the sync edge dropped the `400` a worker's `invalid_request` earns and the `503` a busy tool
> earns — phase-29 D6's inference by the back door, reached by *discarding* information rather than
> by guessing. `ImageJobFailure` now rides on the record: the node states the kind, `ImageRenderer`
> decides the status, and the job carries that decision to whichever surface asks.

**D2 — Progress travels on the existing streams; there is no new transport.** Worker → node:
`progress` frames (`{step, totalSteps}`), a new frame type and **additive** — a worker written
against 3.14 never sends one and behaves exactly as it did. Node → hub: the same `ToolChunk` contract
phase 41 built, on an ordinary `NodeHub.ToolJobProgress` invocation. Hub → client: SSE framed by
hand, exactly as phase-21's SSE and phase-28's exposition format are, because a stream of six-field
objects does not justify a dependency. Phase-26 D2's precedent is exact (a model pull relays progress
on an existing SSE channel) with one deliberate difference: image progress is **client-facing**, so
it does not go on `/api/admin/stream` — an admin channel carrying tenants' job ids is an
authorization mistake waiting for somebody to notice.

*`progress` is a separate frame type from `chunk`, and the distinction is load-bearing:* a chunk is a
partial **answer**, and putting "7 of 28" into the body of a streaming tool response would hand a
client that expects content something that is not. `ToolExecutor.StreamAsync` names it explicitly for
a sharper reason — the default branch there is the *terminal* one, so a progress frame falling
through would end the stream at step one.

**Image jobs dispatch *blocking*, not streaming, and that is not a contradiction.** A streaming tool
response deliberately carries no attachments (phase-41's `StreamAsync` refuses them), and an image
*is* an attachment. So the answer is one `ToolResult` on the result path and the progress arrives out
of band — `Dispatcher.toolProgress` is checked only after `pendingToolStreams` misses, so every job
with no sink registered takes the 3.14 path byte for byte.

**D3 — Cancel is cooperative first and a kill only as a last resort, because the weights cost a
minute.** A `cancel` frame (node → worker) carrying the request id; the worker honours it from its
per-step callback and answers `error` coded `cancelled`, and **is then still alive and still holding
its weights**. Past `Tools:CancelGraceSeconds` (20) it is terminated and restarted — the existing
path. **Killing first would be simpler and is wrong**: it punishes the *next* caller for the first
one's change of mind, and the punishment grows with every model the catalogue gains. Phase-41
deviation 6 established the opposite instinct for a worker that blew its deadline — that one is by
definition not cooperative — and this is the complementary case, stated so the two do not read as a
contradiction later.

**Cancellation is best-effort and the API says so.** `running → cancelling → cancelled | succeeded`
is legal, and `ImageJobTests` asserts the `succeeded` outcome as **legal, not flaky**. Discarding a
finished image to honour a state name would be worse than reporting what happened.

*The node's cancel plumbing is the part that is easy to get subtly wrong:* `ToolExecutor.RunAsync`
drives the read loop on a token that is **not** the caller's. The caller's token sends the frame and
arms the grace clock; only when that expires does the hard token fire and take the worker down.
Wiring the caller's token straight through would make every cancel a kill with extra steps.

**D4 — Per-step progress is free; previews are not, and are off.** `{step, totalSteps}` costs a
callback. A preview costs a VAE decode per step (~10–15% of the run) and produces intermediate
*content*, which is rule 7's business. *Recorded deviation: the preview opt-in is **not** in 3.15.*
The brief specified `X-InferHub-Image-Preview: every-n` and `Images:AllowPreview`; shipping a header
whose only effect is more of what a caller already gets, on a fixture with no VAE to decode, would be
a documented feature nobody could verify — the category phase 40's non-goals refuse by name. The
refusal is what survives here: previews are content, they are off, and a phase that adds them adds
them with a worker that actually produces one.

**D5 — The queue is per capability and it is FIFO, deliberately.** A queued image job is a **202 with
a queue position**, not a wait-then-503: the client already accepted asynchrony, so making it retry
would be strictly worse than telling it where it is in line. FIFO, not shortest-job-first (a stream
of 4-step requests would starve a 50-step one invisibly) and not fair-share-by-client (needs a tenant
weight phase 25's client model does not have). `Images:Jobs:MaxQueueDepth` (32) bounds it and a full
queue is `503` + `Retry-After`, the same status and header as every other limit here.

*Recorded deviation: the queue is `ImageJobRegistry`'s own, not a path added to `RequestQueue`.*
`RequestQueue` answers "wait for a model's fleet to free a declared concurrency slot, then 503",
which is a different question with a different answer shape; the fairness this phase needs is "one
image job per capable node, head-of-line". Grafting a 202 onto a class whose whole contract is a
`QueueOutcome` enum would have made both harder to read. The pump is event-driven — it runs on every
submission, completion, cancel and loss rather than on a timer — so a freed GPU is used in
milliseconds instead of at the next tick.

**D6 — The job store is in memory, bounded, expiring and read-once. This is what keeps rule 4
whole.** See the note at rule 4 above for why it is not a fourth exception. The byte budget is
enforced **on insert**, not on a timer: a timer means the ceiling is a suggestion for one sweep
interval, and one sweep interval of 4096² batches is how a hub gets OOM-killed. Eviction sets the job
to `expired` with a reason so a late arrival is a `410` that says what happened rather than a `404`
that reads like a bug.

*Recorded deviation: `ImageJobStore` lives in `InferHub.Shared`, not in `InferHub.Coordinator` where
the brief put it.* Phase-38 D2's reasoning: solo mode serves the same five routes, and two
implementations of "when do the bytes go away" is one answer too many. It is a plain class with no
ASP.NET and no logging package, so rule 2 holds and **`InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`**. What stayed per host is everything that needs a fleet —
routing, dispatch, metering, the ledger — which is exactly the line phase 38 drew.

**D7 — A node failure mid-job is a failed job with a reason, and never a silent retry.** Phase-21
D3's pre-stream failover retries a request that has not produced output; an image job that died at
step 22 has produced none and is *technically* retryable — and retrying it would silently double the
GPU-minutes and the ledger units for one request. So **no automatic retry once a job is `running`**:
it fails `node_lost` and the client decides. A job still `queued` is re-routed, because nothing has
been spent. There is deliberately **no hook for this**: `NodeHub.OnDisconnectedAsync` already faults
the pending dispatch through `Dispatcher.FailForConnection`, and that lands in the one `catch` that
owns the decision. `ImageJobTests` takes a real node away mid-job and asserts both the reason **and
an empty ledger** — a silent retry would have doubled those numbers rather than left them empty.

**The echo worker grew a slow, cancellable, progress-emitting image mode** (`--image-step-ms`,
`--ignore-cancel`), and that is what makes the whole job model testable with no GPU and no weights —
phase-41's echo-worker discipline, and the reason this phase's suite runs in CI. Two things only a
**real child process** can produce are exactly what this phase is about: a frame arriving *while* a
request is in flight, and a worker that is still warm afterwards. The Python reference library gained
the same (`request.progress`, `request.raise_if_cancelled`, `Cancelled`), and its loop now reads with
`readline()` rather than `for line in stdin` — **the iterator protocol keeps a read-ahead buffer, and
the frame two readers would lose between them is the cancel.**

### Phase 48 (the catalogue: six models, quantized, budgeted) — also load-bearing

**Four more recipes, and every one of them needed something phase 46 did not have.**
`flux-schnell` (12B) and `qwen-image` (20B + an 8.3B text encoder) **do not fit a 24 GB card at
bf16** — 33 GB and 60 GB — so they exist only because of nf4. `sd35-medium` and `sdxl-turbo` fit
fine and need a *licence decision that is not ours to make*.

**D1 — The VRAM budget is declared, not detected, and the worker's reading is a cross-check.**
`Node:Vram:BudgetMiB` is a number the operator sets and `Node:Vram:ReserveMiB` (2048) is what is
held back for the inference backend and the display. **Considered and rejected: detecting VRAM and
defaulting the budget to it.** It works on bare-metal Linux, is wrong under WSL2 — where this
project's own GPU box lives, and where there are no `/dev/nvidia*` device nodes, the host's
`nvidia-smi` cannot see the VM's VRAM, and the only reliable signal a GPU exists is that
`libcuda.so.1` loads (phase-39 D5) — is wrong on a shared card, and is wrong the moment somebody
else's process is on the GPU. **A budget that is usually right is worse than one that is explicitly
absent**, because the first failure is an OOM inside somebody's job rather than a startup message.
The worker reports `torch.cuda.mem_get_info()` on its `ready` frame purely so `ToolWorkerPool`
can **log a disagreement** past a 10% band; nothing routes, budgets or admits on it. Unset (0) means
no gate and v3.15's behaviour exactly.

**D2 — The budget is an admission gate on the node, before the job starts — and it is consulted
*after* the worker slot is taken.** That ordering is the trick: only then is "what is in flight" a
fact rather than a guess. [VramBudget](src/InferHub.Node/Tools/VramBudget.cs) is **pure**
(`budget, reserve, residents, candidate → admit | wait | refuse`) for `NodeProfileClamp`'s reason —
it is the piece whose off-by-one costs somebody an OOM at 2am, and a pure function is the piece a
test can pin exhaustively. **Only what is *in use* counts against a candidate**: an idle pipeline is
freed by the worker *before* it allocates the next one, so the peak is never both models at once,
and a model somebody is mid-job on is never evicted — over the budget the request **waits** on the
existing tool queue and then gets `503` + `Retry-After`, the same status and header as every other
limit here. `Refuse` and `Wait` must not collapse: one is "come back shortly", the other is "this
box will never run that", and the second is also why such a recipe is **never declared** (41 D6's
withdraw-on-failure, applied before the first failure).

> **`ImageResidency` mirrors the worker's own LRU policy rather than measuring anything**, so the
> two agree without a round trip. Where they can differ is a load that *fails*: the node then
> believes the new model is resident when nothing is, which errs toward **refusing** work rather
> than toward an OOM. That asymmetry is the right one. An idle hint clears only the idle entries —
> anything a lease still covers stays, or the gate would admit a second model onto a busy card.

**D3 — Switching recipes swaps weights inside a warm worker; it does not restart it.** Loading FLUX
is 40–90 s and a restart pays the interpreter and the import of torch on top of that, on every
alternation. `Tools:Image:ResidentRecipes` (default **1**) allows more than one resident where the
budget permits — the default is 1 for phase-41 D4's reason, that the expensive default is the one
nobody realises they chose. **Idle unloading is the worker's decision, not the node's**: the node
sends an `idle` hint frame after `idleTimeoutSeconds` and the worker frees its VRAM and stays alive.
A node-side unload would be the node reaching into a tool's internals (41 D1).

> **`ToolWorkerPool.WorkerFloor` is a bug fix as much as a feature.** An open model set forces an
> eager worker because nothing declares such a capability until a worker reports (the v3.10.0
> deadlock) — and until v3.16 the maintenance pass would happily **retire that very worker** after
> `idleTimeoutSeconds`, leaving a pool that still declares models with no process able to re-declare
> when one lands, and killing a prefetch in flight. The last worker of an open-set pool is now kept
> and hinted instead. Hinted **once** per idle period, not every tick.

**D4 — Weights are pulled by an explicit command, never lazily inside a request.** FLUX is ~24 GB on
the wire and Qwen-Image is larger; a lazy first-use download blows `requestTimeoutSeconds`
(v3.14.0 shipped exactly that and every first `sdxl` call was a 502 after 899.99 s), and raising the
timeout to cover it means every genuinely wedged job also takes forty minutes to fail. So phase 26's
model-command channel is extended: `ModelCommand` gains a nullable `Tool` — **null means the
inference backend**, which is every command that existed before v3.16 — and
`POST /api/admin/nodes/{id}/tools/{tool}/models/{recipe}/pull` sends it down the node's own outbound
connection, with progress relayed on the existing `/api/admin/stream`. No new transport; the
coalescing, the reused-command-id behaviour and the "no persistent state" property all come with it.
**`warm` is refused for a tool model** rather than given an invented meaning — residency is already
decided by `ResidentRecipes` and the idle hint, and a third opinion is a third thing to be wrong.

**The progress carries no percentage, deliberately.** `huggingface_hub` gives no download callback,
and a denominator a worker would have to guess is a number a dashboard would happily plot
(phase-28 D5). It reports how many mebibytes have landed instead.

> `IToolRuntime.AcquireToolAsync` is **the one path that does not go through a capability**, and it
> has to be: a pull exists precisely because the model is not there, so it is not declared, so
> `AcquireAsync` would answer "this node does not provide it" — correctly, and uselessly. The
> ceiling is intact, because what is addressed is the *tool*, which `Tools:Allowed` named. It takes
> an ordinary worker lease, so a pull queues behind a generation and vice versa: a node does not
> quietly grow a second lane to the GPU.

**D5 — A non-permissive licence needs a fourth opt-in, named per model.** A recipe with
`license.permissive != true` is **loaded, logged by name and not started** unless its licence id is
in `Tools:Image:AcceptedLicenses`, with the log line naming the licence and linking to it. It is not
redundant with the other three (41 D2, 42 D4): `Enabled` is the feature, `Allowed` is *these tools*,
`AllowModelDownload` is reaching the internet, and none of them says "and I accept the Stability AI
Non-Commercial Research Community License". **A list, not a boolean** — `sd35-medium` is free for
most people who will run it and `sdxl-turbo` is not usable commercially at all, so one flag would let
somebody who read one licence enable both. **A recipe that says nothing is treated as *not*
permissive**: one that forgot to say is one nobody has read the licence of, and the other default
would make the consent opt-out by accident of a missing field. Enforced **twice** — the node refuses
to declare it (so the hub never routes at one) and the worker refuses to download or load it (the
lock on the process that would actually do those things, which a solo caller meets directly).

> *Recorded deviation from the brief:* the key names **licence ids**, not recipe ids. What is being
> accepted is a licence — you read it once and it covers every model under it — and the key's name
> says so. Both shipped non-permissive recipes have distinct licence ids, so the two readings behave
> identically for this catalogue; the refusal prints the exact string to add and a link to the text.

**D6 — Quantization is a recipe field with three values and a stated cost.** `none | int8 | nf4` via
`diffusers`' native `bitsandbytes` integration, applied to the components `quantizeComponents`
names — which for Qwen-Image **has to include the text encoder**, because 8.3B left at bf16 is the
difference between fitting a 24 GB card and not. **It is a recipe field rather than a request
parameter because it changes what the model *is*:** two requests to `qwen-image` that quantized
differently produce different images from the same seed, and a per-request knob would make
reproducibility a function of a header nobody logged. An operator who wants both ships two recipes
with two ids. `vramMiB` is the **quantized** figure (what the gate admits against) and
`vramUnquantizedMiB` is documentation, because "Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB"
are both true sentences about different recipes. **One mechanism** — GGUF, Nunchaku and TensorRT are
each faster on some model on some card and each is a second thing to reason about when a picture
comes out worse than expected.

**The node reads recipe files, and that is not the node learning about diffusion.**
[ImageRecipeCatalogue](src/InferHub.Node/Tools/ImageRecipeCatalogue.cs) parses exactly three things —
id, licence, VRAM — and never `repo`, `pipeline`, `variant`, `dtype` or the aspect buckets. Two
consumers need the answer with **no worker running**: `NodeProfileClamp` is pure and must refuse an
oversized or unlicensed recipe synchronously, and the decision not to *fetch* an unlicensed model has
to precede the process that would fetch it. What the node learns is licences and megabytes, which are
facts about the box. A recipe with no `revision` is skipped by name here exactly as it is in the
worker — a catalogue that counted a model the worker will never offer would budget VRAM for something
that cannot run.

**Profiles gain `imageRecipes`, the third thing a hub can narrow and the first whose ceiling is
arithmetic.** `false` narrows and always works; `true` is honoured only for a recipe the box has,
has accepted the licence of, and has the VRAM for — refused otherwise with the numbers in the
message. 43 D1 is unchanged: the hub cannot make a node accept a licence, find weights or grow a
card. A narrowed recipe **stops being declared**; the pool keeps running, so switching `sdxl-turbo`
off does not take `sdxl` down with it (phase-43 D6's in-place shape).

**Rule 5 survived again.** **Zero** new `PackageReference`, `InferHub.Shared.csproj` still an empty
`<Project Sdk="Microsoft.NET.Sdk">`, and `bitsandbytes` is a line in `requirements-diffusion.txt` —
the same category as phase-39's `curl`. It arrived **with its first consumer**, which is exactly why
phase 46 refused to carry it: a pinned dependency nothing imports is a pin nobody can tell is wrong
until the release that needs it.

### Phase 49 (qwen-360-diffusion: adapters and 360° panoramas) — also load-bearing

**The model this whole track was asked for, and it needed four things phase 48 did not have:** an
adapter stack in the recipe format, a projection that survives to the client, a 2:1 refusal that says
why, and a seam number nobody repairs.

**D1 — Adapters are a recipe field, and a recipe with adapters is a distinct model id.**
`adapters[]` carries a repo, its **own pinned revision**, a `weightFile`, a scale and its own
licence — a permissive base with a non-permissive LoRA on it is not a permissive model.
`qwen-image` and `qwen-360` are **two recipe ids over one base**, not one model with a flag: the
router keys on `(capability, model)` and nothing else (40 D1), so a client asking for `qwen-image`
must never receive a panorama, and a `loraScale` header would make what you get depend on a header —
the reproducibility problem 48 D6 already refused for quantization.

What the *worker* does about two recipes sharing a base is an optimisation and is **not part of the
contract**: `donor_for` finds a resident pipeline with the same `base_key` (repo, revision, variant,
dtype, quantization), `unload_lora_weights` + `load_lora_weights` swaps the adapter in seconds rather
than the 40–90 s a 20B reload costs, and **any failure falls through to a full load** having
discarded the pipeline whose adapter state is now unknown. Adapters are applied inside
`_from_pretrained` — the one place a pipeline is constructed (v3.14.1) — so the background prefetch
*proves* the LoRA loadable exactly as a request will load it, and a mismatched one is never declared
rather than failing on somebody's first request. The readiness marker carries an adapter fingerprint
for the same reason: trusting a marker written for different weights would serve the wrong model
under the right id.

**D2 — The trigger phrase is appended when missing, and the response says it happened.** Three
options, and two were rejected out loud. *Silently rewriting the prompt* — no: this repository's
most-repeated sentence is that nothing is silently substituted, and a prompt is the user's own words.
*Refusing a prompt without the trigger* — no: pedantry about a model whose entire purpose is one
thing, and it makes the first request everybody sends a `400`. So it is appended when absent, and
`prompt_augmented` plus the phrase travel in the response. `autoTrigger: false` turns it off and the
flag is reported **either way**, for a recipe that has a trigger — a client that had to infer
"nothing happened" from a missing key is a client guessing about its own prompt. A recipe with *no*
trigger reports neither, because a permanent `false` on every SDXL response is a field that means
nothing.

**The trigger is a recipe constant and therefore not content**, which is what makes it loggable and
is worth having: "why does this not look like a panorama" is almost always "the trigger did not
apply", and a diagnosis nobody can see is not one. The prompt it was appended to is still never
written anywhere — `ImagePrivacyTests` asserts the augmented form is absent from the logs too, since
a worker echoing the rewritten prompt back into a payload the hub logs would leak the original with
three words on the end of it.

**D3 — A 2:1 aspect is enforced, and the refusal names the reason rather than only the list.**
360° of longitude over 180° of latitude is exactly two to one. A wrong size on a flat recipe gives
you duplicated limbs — visibly bad. A non-2:1 equirectangular render gives you a picture that looks
perfectly fine and wraps wrongly, and the person who finds out is wearing a headset three days later.
*Recorded deviation:* the brief listed this under `ImageRenderer`, and the edge still cannot do it —
phase-46 D6's deviation is unchanged, a recipe is a file on the node and the hub has no catalogue.
The **worker** writes the sentence and `ImageRenderer` renders the 400 without reading it (29 D6).

**D4 — Projection is a declared property of a result, on every surface, including `flat`.**
`ImageProjections` in `InferHub.Shared`; the response body per image, the job document, and
`X-InferHub-Image-Projection` on the content route — which is the one request with no JSON to carry
it. **A flat recipe reports `flat` rather than omitting the field**, and that is a deliberate
exception to phase-28 D5's "absence is a fact": there, absence meant nothing had been *measured*;
here the field is a declaration, and an omitted one is indistinguishable from a node too old to have
an opinion. A client that has to tell those apart has learnt nothing. Nothing infers a projection
from an aspect ratio — a 2048×1024 photograph and a 2048×1024 panorama are the same pixels.

**D5 — The seam is measured and reported, never repaired.** Mean absolute difference between the
first and last columns — adjacent once wrapped — over 255. Two numpy operations on an array the VAE
already produced, which is why it is unconditional: a metric behind a flag is a metric nobody has.
Over `Tools:Image:SeamWarnThreshold` (0.08) the result carries a `seam` warning, and it is a
**warning on a 200**: phase-35 D4 against phase-37 D4 again, because a visible seam is the operator's
own aesthetic judgement and failing a two-minute job over a threshold would be the tool overriding
the person. **And it is not repaired** — upstream's `fix_seam` is a second generation pass with its
own cost and its own artifacts, and running it unasked would bill somebody for a decision they never
made. `seam_delta` returns `None` rather than raising on anything unexpected: a measurement that
could fail a two-minute job is worse than no measurement.

**The viewer is hand-written WebGL, and rule 3 is why.** `wwwroot/pano.js` — a sphere, a texture and
two matrices, no npm, no bundler. three.js from a CDN would also put a third-party script on an admin
console that holds cordon and model-pull rights, which is a worse trade than an afternoon of
`gl.texImage2D`. It picks its renderer from the **declared projection**, never from the aspect ratio,
and a browser with no WebGL gets the flat image and a sentence saying so rather than a black
rectangle (39 D6's instinct, in a canvas).

*Recorded deviations, on purpose:*
- **`ImageRenderer.Envelope` is now the one place the OpenAI Images envelope is written**, and it
  builds dictionaries rather than anonymous types. Three surfaces produced it by hand, and the
  global `WhenWritingNull` policy is how the hub came to emit `revised_prompt: null` while a solo
  node **omitted** it — for three releases, with a parity suite running. Which keys are *present* is
  part of this contract, so it is spelled rather than inherited from a serializer option.
- **`qwen-image` gained `guidanceParameter: "true_cfg_scale"`.** Qwen's MMDiT has two guidance
  inputs and this pipeline's real classifier-free guidance is the second one; passing the wrong one
  does not error, it produces a picture that is plausible and not what the recipe was tuned for.
  Upstream's own `run_qwen_image_nf4.py` uses `true_cfg_scale`, and phase 48's verification never
  generated a Qwen image at all, so no observed behaviour changed under it.
- **The LoRA's quantization variant does not have to match the base's.** Settled from upstream's
  reference script rather than assumed: it pairs an **nf4 base** with the **int8-trained** adapter,
  and the int4-trained ones exist for fp8 transformers, where downcasting artifacts are the problem.
  Written into the recipe's `notes` field, because "which loading path" is exactly the thing a later
  reader will otherwise re-derive wrongly.
- **The console panel holds its own client key**, like the documents panel: image jobs are guarded
  by `Auth:ApiKeys` and the admin key the rest of the console uses will not open one. There is no
  *list* of image jobs — the hub has no route for one, and inventing a client-scoped listing here
  would be phase 51's job done in the wrong phase.

**Rule 5 survived again.** **Zero** new `PackageReference`, no npm, no CDN script, no image library —
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`, and the only thing
that ever decodes a pixel is the browser.

### Phase 50 (editing: img2img, inpainting, variations) — also load-bearing

**Bytes travel hub → node for the first time.** Phase 40 built the attachment path and phase 42 used
it in one direction only; an edit is the first thing in this project's history that sends a
multi-megabyte payload *down* the mesh connection. `ImageEditTests` pushes 3 MB across a real wire
and asserts **the node is still registered afterwards** — the v3.10.0 assertion, in the direction
nobody had tested.

**D1 — `image-edit` is its own capability kind, and the catalogue splits by a recipe field.**
A recipe declares `operations: ["generate"]` or `["generate", "edit", "variation"]`; the worker
declares `image` for the generators and `image-edit` for the rest. **Both frames are always sent,
empty ones included** — an empty list is a declaration that this node serves nothing under that kind,
and omitting the frame would leave a previous declaration standing on a node that can no longer
honour it. A second kind rather than a per-model operation list, because the router filters on
`(kind, model)` and nothing else (40 D1): teaching it to read a nested operation set means teaching
the affinity, the queue and the saturation logic the same thing. It is also a real distinction —
FLUX.1-schnell has no official inpainting pipeline and SDXL does.

**The 503 names the recipes that *can* edit**, and that is not the model catalogue phase-46 D6
refused: it is the fleet's own capability declarations, which the hub already holds. "No" with no
alternative sends somebody to the docs; "no, but these" is actionable.

`CapabilityKinds.IsImageKind` is what everything that reasons about a *recipe* asks — the licence
gate, the VRAM budget, the residency map. **Editing and generating are separate for routing and not
for any of those**, because an edit loads exactly the same weights (`from_pipe` reuses the
components). A node that applied its licence gate to `image` only would happily edit with a model
whose licence nobody accepted.

**D2 — OpenAI's mask convention is inverted from the library's, and the conversion happens in the
worker. This is the decision the phase turns on.** OpenAI's edits API treats a **fully transparent**
pixel as the area to edit; `diffusers` takes a mask where **white** is the area to inpaint. Getting
it backwards does not error — it edits everything *except* what the caller selected, which reads as
a broken model.

> **Recorded deviation from the phase brief.** The brief put a `MaskConverter` in `InferHub.Shared`.
> It cannot live there: converting one convention to the other means reading an alpha channel out of
> a PNG and writing a greyscale one back, and **nothing in this codebase's C# ever decodes a pixel**
> (phase-46 D6) — there is no image library on the hub, by design and by invariant, and hand-rolling
> a PNG decoder to avoid taking one would be the same mistake with more code. So
> [MaskConventions](src/InferHub.Shared/Images/MaskConvention.cs) decides what the two conventions
> **are** and what a caller may say; the inversion happens where PIL already is. It is named
> `MaskConventions` rather than `MaskConverter` because a converter that converts nothing is a lie in
> a name — the same correction phase 46 made to `Metrics.RecordAudioUnits`.

The consequence is the one phase-46 D6 and phase-49 D3 already accepted twice: **a mask with no alpha
channel costs one round trip to find out**, because the edge cannot open it. The worker answers
`invalid_request` and `ImageRenderer` renders the 400 without reading the message (phase-29 D6).
Under OpenAI's convention a fully opaque "mask" selects **nothing**, which no caller has ever
intended — reading it as "edit everything" would be a silent substitution of the most destructive
possible interpretation, and reading it as "edit nothing" would return the input with a 200 on it.
`X-InferHub-Mask-Convention: openai | luminance` lets a caller who already has a white-is-edit mask
say so; an unknown value is a `400` that names both **and says which is which**, because two words
whose difference is invisible until you look at the picture are not a helpful list.

**A mask is never rescaled.** A mask names *which pixels*, so a mask whose size differs from the
image's is a `400` naming both sizes rather than a resize — scaling somebody's selection lands the
edit next to what they chose, which looks like a bad model rather than a bad mask.

**D3 — `strength` is a header, and what is metered is the steps it actually ran.** OpenAI's edits API
has no `strength` and image-to-image without one is meaningless, so `X-InferHub-Image-Strength`
(0–1), phase-46 D1's shape. Absent, the **recipe's** `defaults.strength` applies — the edge has none
to invent and deliberately omits the field rather than guessing, because a number chosen at the edge
would be the edge deciding how far an edit moves away from somebody's photograph.

`diffusers` enters the schedule at `int(steps × strength)`, so 30 steps at 0.6 denoises for 18 — and
**18 is what the worker reports, what the progress frames count to, and what the ledger gets**.
Metering the asked-for 30 would bill for work nobody did, which is phase-42 D7's "the unit the work
is in" applied to a knob rather than to a modality.

**D4 — Input attachments ride the existing path and are capped in both grains.** Each part is bounded
by `Tools:MaxAttachmentBytes` (what the *node* enforces, so a request that passed the edge and failed
at the node is impossible) and the picture and mask **together** by `Images:MaxRequestBytes` — a
separate key because the two directions are separate risks with separate arithmetic: outbound is `n`
renders of a declared size, inbound is one upload somebody else chose the size of. Both refusals are
`413`s naming their key, at the edge, before anything is buffered onward.

**The caller's filename is dropped and the parts travel as `image` and `mask`.** What somebody called
a file on their disk is metadata about their day (phase-42 D5); what the worker needs is the *role*.

**A variation takes no prompt, and a prompt on one is a `400` naming the other route.** OpenAI's
variations API has no prompt field, and `/v1/images/edits` *without* a mask is already "img2img with
a prompt" — so accepting one here would be a second dialect for a convenience that exists. Ignoring
it would be worse: a caller whose prompt vanished silently would conclude the model ignores prompts.
A mask on a variation is refused for the same reason.

**`POST /api/images/jobs` takes JSON or multipart, and that is not phase-47 D1's refused flag.** D1
refused `background: true` because it made one route answer two incompatible *response* shapes; here
the response is the same job document either way and the request shape is decided by `Content-Type`,
which is what content types are for. **A multipart submission must name its `operation`** — defaulting
it would let a typo turn a variation into an edit, and this is InferHub's own contract where ceremony
is cheaper than a silent substitution.

*Recorded deviations, on purpose:*
- **`ImageRenderer.Generation` became `ImageRenderer.Render`**, and `ImageJobRegistry` /
  `LocalImageJobRunner` now hold an `IImageRequest` rather than an `ImageGenerationRequest`. Phase
  47's queue, progress, cancel, retention and metering are identical for an edit, and a second job
  path would be two ideas of fairness on one GPU — the thing phase-47 D1 built the shared path to
  prevent. `busyNodes` is deliberately **not** split by capability either: the resource a node has
  exactly one of is the card.
- **The edge does not check that a recipe supports the operation**, only that the fleet declares the
  capability. The worker refuses by name for a solo caller who reaches it directly. Phase-46 D6's
  deviation, unchanged: a recipe is a file on the node.
- **The multipart reading is hand-copied per host** (`ImageEndpointSupport.ReadEditAsync` and
  `LocalImageForm`). Phase-37 D6's line: the ten lines that touch `IFormCollection` are plumbing,
  and every *sentence* comes from `InferHub.Shared`. `ImageParityTests` grew five arms, including
  the mask refusals, because that copy is the parity risk.
- **The echo worker reads the input files for real** and checks the mask's alpha channel and its
  dimensions out of a genuine IHDR. A test fixture may decode a PNG; the hub may not. A stub that
  agreed with itself would prove nothing about the one thing this phase adds.

**Rule 5 survived again.** **Zero** new `PackageReference`, no image library anywhere in C#, and
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.

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

## Writing a plan (the shape every file in `plan/` has)

**When asked for a new plan, write it in this shape without being told.** One phase →
`plan/phase-NN-short-slug.md`. A multi-phase track → one roadmap file,
`plan/roadmap-vX.Y-to-vX.Z-slug.md`, with a `Status:` line *per phase* inside it. Either way index it
in `plan/00-overview.md`. Plans are written for whoever implements them next with no memory of this
conversation, so a decision without its rejected alternative is a decision that gets undone.

**Header block** — title `# Phase NN — <the claim, in a sentence> (vX.Y.Z)`, then `Status: TODO`,
target version, **Size** (S/M/L + days), repo link, the file's own path, and a `>` callout naming the
prior `CLAUDE.md` decisions to read first (by number: "phase-36 D1/D3", not "the supervisor phase").

**§1 Goal** — what is true today and why it is not enough, in the repo's own words, with the file
paths. Then the shape of the change, with real commands or payloads a reader can run. Then
**Non-goals**, each written as *a decision with its reason*, never a bare list.

**§2 Design decisions** — `### D1 — <a full sentence that states the claim>`, numbered, each with:
the reasoning, the **alternative that was considered and rejected** and why, and — where it applies —
which rule (1–7) it brushes and what keeps the rule true. Mark the load-bearing one out loud ("this
is the decision the phase turns on"). The heading is the claim, so a reader skimming only headings
gets the design.

**§3 Tasks** — `- [ ]` checkboxes in dependency order, each naming a **real path** and what goes in
it. Order them so a failure is attributable: the thing that can break in isolation lands first.
Always include the CLAUDE.md block, the `appsettings.json` commented keys, README, and the
`plan/00-overview.md` row as tasks.

**§4 Acceptance criteria** — checkboxes, and they must include: *a deployment that changes no config
behaves identically to the previous version*, **zero new `PackageReference` / `InferHub.Shared.csproj`
still empty**, and `dotnet test` green. Anything that cannot be established from source says so and
points at §5.

**§5 Release ritual** — bump `<Version>` → `.claude/release-notes-vX.Y.Z.md` → tag → GitHub release
→ **pull the published image and run it** (an enumerated list of what to check on the target box —
this is not optional, see the D7 note in "Phase 21") → flip `Status:` + the overview row → static
site `inferhub.devart.solutions` (`#idocs_*` anchors, changelog row, "What's next") → blog post
(**slug, EN title, excerpt angle, draft-first**, `list_posts` before creating — the connector is
insert-only and the slug locks) → FB + X.

**§6** — appended *after* the release run: the verification results, with the observed numbers, the
exact host, and anything that did not run said out loud rather than omitted.

**A roadmap file adds**, before the phases: *Where we are* (shipped phases, current `<Version>`, the
seam being extended), *Overview* table (phase | version | theme | size | status), *What this
delivers*, *Why this order* (a paragraph per adjacency, arguing the dependency), *Invariants that
survive all phases*; and after them: *Sequencing notes* and *Deferred (tracked, not in this track)*.
Each phase inside it keeps §1–§5 as `###` subsections and is a **separate release** — separate tag,
notes, site edit, post. Do not batch them.

**House voice**: state the failure the decision prevents, concretely ("a client reads `error.message`
and gets a wall of backslashes"). Prefer a rejected alternative to an adjective. Never write a caveat
that a later phase makes false without deleting it everywhere — see the phase-35 note.

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
