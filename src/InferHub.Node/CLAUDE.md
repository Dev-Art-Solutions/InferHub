# InferHub.Node — agent context

**Scope: `src/InferHub.Node/`.** The GPU-side worker: backends, the Ollama supervisor, solo mode,
the tool runtime, node profiles and node-owned retrieval.

> **Read the root `CLAUDE.md` first.** The rules that bind hardest here are **rule 1** (nothing
> Ollama-specific escapes `Backends/`) and **rule 2 as amended in phase 37** (ASP.NET appears on
> the node, confined to `LocalApi/`, and nowhere else).

## Related context

- The contracts this node fills in: `src/InferHub.Shared/CLAUDE.md`
- The hub that dispatches to it: `src/InferHub.Coordinator/CLAUDE.md`
- The workers this runtime spawns: `python/CLAUDE.md`
- The four node images: `deploy/CLAUDE.md`

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


## Decisions recorded here

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


### Phase 53 (the node writes a streamed upload) — also load-bearing

**D1's payoff is that the node side is small, and phase-41 D5 is why.** The worker has always been
handed a **path** into a per-request scratch directory, so writing that file from a socket instead of
from a `byte[]` the node was given changes nothing above it: `ToolExecutor.BuildRequest` cannot tell
which path the bytes came from, and the worker protocol did not change by one field. What did change
is that the node's memory no longer grows with the upload — frames are 64 KB and the file is appended
to. See `src/InferHub.Coordinator/CLAUDE.md` for the hub's half (D1–D6, D9).

- [IStreamedAttachmentSource](src/InferHub.Node/Tools/IStreamedAttachmentSource.cs) has two
  implementations that do not know about each other — phase-41 D8's framing a fourth time:
  [HubAttachmentSource](src/InferHub.Node/Tools/HubAttachmentSource.cs) pulls from the hub, and solo
  mode reads the request body directly.
- **The bytes land before the worker slot is taken.** An upload that is still arriving must not hold
  a GPU worker idle while it does — phase-41 D4's slot is the scarcest thing on the box.
- **A stream that ends before one complete attachment fails the job**, rather than running the tool
  on a file that is not there: a worker handed nothing answers cheerfully about the request it *did*
  get, which is a 200 that means nothing.

**`Tools:MaxStreamedBytes` is the node's own ceiling and is enforced here as well as at the hub.**
Phase-41 D2's reason: the box that accepts an upload is not the box that has to write it down, and
each is entitled to its own answer. It is **0 by default (off)**, and setting it is what makes this
node declare `SupportsStreamedAttachments` — one key, so a node cannot advertise something it will
then refuse. The validator rejects a value below `MaxAttachmentBytes`, which could only ever refuse a
request the buffered path would have taken.

**D7 — Solo streams straight into the scratch file, and the one asymmetry is deliberate.**
[LocalUploadPath](src/InferHub.Node/LocalApi/LocalUploadPath.cs) is the coordinator's `UploadPath` /
`StreamedUpload` hand-copied, which is phase-37 D6's line rather than an oversight: the multipart
plumbing is ASP.NET and rule 2 keeps ASP.NET out of `InferHub.Shared`. `SoloUploadParityTests` drives
the same upload at both hosts for exactly that reason. **A solo node accepts fields after the file
where the hub refuses them** — with nothing to route, no decision had to be made before the bytes, so
there is nothing to refuse. The asymmetry is one-directional: everything the hub accepts, solo accepts.

**The scratch `finally` covers an aborted upload too, and that was tested rather than assumed.** A
client that walks away cancels the endpoint, which sends `CancelJob`, which cancels this node's job
token and ends the enumeration — so the half-written file goes with the directory. A second
abort-the-stream mechanism was built for this and **removed** when the test passed without it; see
`Dispatcher.UploadRegistration`.
