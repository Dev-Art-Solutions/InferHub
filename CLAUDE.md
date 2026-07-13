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
5. **No new heavy dependencies.** The dependency surface is deliberately minimal (ASP.NET Core,
   SignalR, OllamaClient on the node, xunit for tests). The one recorded exception is phase 20's
   **`Npgsql` + `Pgvector`**, which back the `postgres` vector provider: coordinator-only (never
   in `InferHub.Shared` or `InferHub.Node`), and no connection is opened unless
   `VectorStore:Enabled=true` **and** `VectorStore:Provider=postgres`. That was a conscious,
   provider-scoped decision — the rule still holds for everything else. Add packages reluctantly.
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

## Auth model (three independent token sets)

| Scope | Config key | Guards |
|---|---|---|
| Inference clients | `Auth:ApiKeys` | `/api/generate`, `/api/chat`, `/api/tags`, etc. |
| Admins | `Auth:AdminApiKeys` | Everything under `/api/admin/*`. |
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
