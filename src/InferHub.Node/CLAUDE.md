# InferHub.Node — agent context

**Scope: `src/InferHub.Node/`.** The GPU-side worker: backends, the Ollama supervisor, solo mode,
node profiles and node-owned retrieval. **The tool runtime and the media phases that ride it moved
to `src/InferHub.Node/Tools/CLAUDE.md` in phase 67** (D6).

> **Read the root `CLAUDE.md` first.** The rules that bind hardest here are **rule 1** (nothing
> Ollama-specific escapes `Backends/`) and **rule 2 as amended in phase 37** (ASP.NET appears on
> the node, confined to `LocalApi/`, and nowhere else).

## Related context

- The tool runtime, STT/TTS, the catalogues and the VRAM budget: `src/InferHub.Node/Tools/CLAUDE.md`
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
  `UpstreamBackend` (phase 22 as `OpenAiBackend`, renamed and widened in 67) drives everything
  else through an `IUpstreamDialect` from `InferHub.Shared`: `Backend:Type` is `openai`
  (vLLM, llama.cpp's server, LM Studio, TGI), `openrouter`, `anthropic` or `gemini`.
  `IInferenceBackend.Endpoint` is what the node reports at registration: before phase 22 it
  hard-coded `Ollama:Endpoint`, so an OpenAI-backed node would have advertised `localhost:11434`
  while talking to something else entirely. `IInferenceBackend.Kinds` is phase 67's second member,
  and it exists because Anthropic has no embeddings API.
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


### Phase 67 (the node speaks all four dialects) — also load-bearing

**D1 — `Backend:Type` gains three vendor values and the node stays exactly one upstream deep.**
`ollama` · `openai` · `openrouter` · `anthropic` · `gemini`, chosen at boot — the phase-22 shape
widened rather than replaced, so everything downstream of `IInferenceBackend` (the executor, the
worker, solo mode, profiles, the supervisor guard) is untouched. **Considered and rejected: giving
the node the hub's `Providers:` map.** It reads as symmetry and it is a second router: two policy
vocabularies, two steer headers, two places a prompt's destination is decided, and a node that can
disagree with the hub that dispatched to it. The asymmetry is the design — *the hub chooses, the
node serves* — and it is rule 1 read forwards. There is likewise **no `ModelMap` on the node**: the
hub has one because it routes, this box reports what it serves, so the allowlist is the whole
consent and the vendor's own id is the model name.

**D2 — One backend class per seam, not per vendor.** `OpenAiBackend` became
[UpstreamBackend](src/InferHub.Node/Backends/UpstreamBackend.cs), driving an `IUpstreamDialect`
(61 D3) — which is what this backend has always been since 22 D1: Ollama JSON in, Ollama JSON out,
over somebody else's HTTP. A fourth vendor costs two arms in two switches, one for the dialect and
one for the credential, **because the credential is part of the dialect** (63 D1: a Bearer token
sent to Anthropic or Gemini is a 401 that reads like a bad key). **Considered and rejected: three
more implementations of `IInferenceBackend`**, each a copy of the same 120 lines — four copies of
the disposal contract phase 22 got wrong once. **Rejected: keeping the old name** for a class that
drives Anthropic; that is the sentence 61 D2 refused to leave for the next reader, and here it cost
one rename inside one project.

**D3 — The section is `Upstream:`; `OpenAi:` still binds, projected onto it, and a disagreement
between them fails startup naming both.** 61 D2's projection and 65 D1's conflict check, one host
over. Both sections bind to one options object with `Upstream:` layered second, so a node written
against any release since v2.4 is byte-identical — and a key written in both with different values
is refused rather than resolved by binder order, because which upstream receives a prompt is not
that kind of decision. What the new name buys is that the vendor keys can exist at all:
`Upstream:MaxTokens` (63 D2), `Upstream:AnthropicVersion`, `Upstream:ThinkingBudget` (64 D6),
`Upstream:Referer` / `Upstream:Title` (62 D2, absent by default — they put a deployment on
OpenRouter's *public* rankings). **Rejected: carrying them under `OpenAi:`** —
`OpenAi:AnthropicVersion` is the kind of key somebody screenshots.

**D4 — A backend *declares* the capability kinds it serves, and Anthropic declares `chat` alone.**
`IInferenceBackend.Kinds`, taken by `BackendCapabilities.Declare` in place of the constant
`[chat, embed]` that file used to hold. It is `SupportsModelManagement`'s own argument one member
down — *a backend that throws when asked to do the impossible is a seam nobody trusts twice* — and
the payoff is 40 D1's for the fourth time: an embedding request against an Anthropic-backed fleet is
a **503 naming `embed`** at the hub, before the hop, instead of a 501 inside a failed job. In solo
mode `/api/embed`, `/api/embeddings` and `/v1/embeddings` answer **501** through
`LocalApiEndpoints.BackendCannot`, which is deliberately *not* merged with `CapabilityDisabled`:
that one is an operator's subtraction and is worth retrying, this one is a fact about the upstream
and never will be. **Rejected: deriving it from the type inside `BackendCapabilities`** — a
capability registry keyed on a vendor string, in the one file whose whole point (40 D2) is that
nothing there guesses what a model is for. `Node:Capabilities:Disabled` stays subtractive over the
result, unchanged.

**D5 — A vendor-typed node with no `Models:Include` refuses to boot; `openai` is untouched.**
OpenRouter lists 419 ids and Gemini around fifty, embed-only and image ones among them; a node that
reported the catalogue would be telling the hub it can chat with an image model, and the router
would believe it. Either `Upstream:Models:Include` or `Node:Models:Include` satisfies it — both are
the operator's sentence. **`openai` keeps today's behaviour exactly**, empty allowlist included: it
is usually one vLLM serving one model, and breaking those deployments to protect them from a
catalogue they do not have is not a trade. **Rejected: a default allowlist per vendor** — a model
list checked into a repository is wrong by the time it ships, which is why the track refused a price
table for the same reason.

**D6 — This file was at 1099 of 1100, so the tool-runtime phases moved whole into
`src/InferHub.Node/Tools/CLAUDE.md`.** 62 D6, one project over, and the same arithmetic: a phase
cannot land its decisions in a file with one line of headroom. **41, 42, 48, 55, 56 and 57/58** were
the largest coherent subtree the provider track had nothing to do with, and they moved unedited.
**Rejected: compressing phase 41**, and **rejected: raising the budget** — a limit raised on first
contact is not a limit (52 D5).

**Rule 5 survived again.** Phase 67 added **zero** new `PackageReference`s — the three dialects were
already in `InferHub.Shared`, which is still an empty `<Project Sdk="Microsoft.NET.Sdk">`, and the
only new file there is [UpstreamDefaults](src/InferHub.Shared/Upstream/UpstreamDefaults.cs), three
base URLs both hosts now read from one place instead of two.
