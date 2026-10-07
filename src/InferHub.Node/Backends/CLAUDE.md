# InferHub.Node/Backends — agent context

**Scope: `src/InferHub.Node/Backends/`.** The engines a node drives that are not "an Ollama and a
dialect": colibri and its Brio, since phase 95 a node running several engines at once —
`Backend:Engines`, ollama + llama.cpp + colibri side by side, started and stopped by the hub — since
96 a llama.cpp engine that is all of llama.cpp: a router over many GGUFs, managed from the hub — and
since 97 a colibri catalogue: many converted models, one loaded on request, idle ones freed, the hub picking.

> **Read the root `CLAUDE.md` first, then `src/InferHub.Node/CLAUDE.md`.** That file still owns
> `IInferenceBackend`, the dialects a backend speaks (67), the supervisor and backend health (36, 69),
> profiles (43) and on-demand (85). The rules that bind hardest here are **rule 1** (nothing
> engine-specific escapes this folder) and **43 D1** (the node's config is the ceiling).

**Split out in phase 95**, for 62's and 67's reason: the node's file was at 1079 of its 1100 lines,
and the engine blocks (93, 94) were the largest coherent subtree the rest of the node has nothing to
do with. They moved verbatim.

## Related context

- The backend seam, the dialects, health, profiles, on-demand: `src/InferHub.Node/CLAUDE.md`
- The hub that starts and stops engines and routes their models: `src/InferHub.Coordinator/CLAUDE.md`
- The `:colibri` image: `deploy/CLAUDE.md`

### Phase 93 (`Backend:Type=colibri` — a disk-streaming MoE engine, launched and watched by the node)

Files: `Backends/Colibri/` (`ColibriOptions`, `ColibriSlot` + `ColibriRequestHandler`,
`ColibriWatcher`, `ColibriServe`), `UpstreamBackend.cs`, `NodeHostBuilderExtensions.cs` (`AddColibri`),
`Dockerfile.colibri`. [colibri](https://github.com/JustVugg/colibri) (Apache-2.0) keeps an MoE's dense
part in RAM and streams the routed experts from disk; `coli serve` is its OpenAI gateway.

**D1 — its own type, not `openai` pointed at it.** That node would declare `embed` (colibri has no
`/v1/embeddings`, so a job fails after the hop — 67 D4), watch nothing (69 D4 exempts `openai`) and
use one KV slot. `colibri` is the OpenAI dialect, `[chat]`, base URL defaulting to colibri's
`127.0.0.1:8000/v1`. Two arms in `UpstreamBackend`'s switches (67 D2). *Measured:* the hub refuses an
embedding before the hop — as a **404** ("no node is advertising embedding model"), not the 503 the
brief predicted, because the model is known for chat.

**D2 — a conversation's KV slot is a function of its opening.** With `Colibri:KvSlots > 1`,
`cache_slot = FNV-1a(messages through the first user turn, or a prompt's first 512 chars) mod
KvSlots`. Every turn re-sends its opening (rule 7), so it lands on its slot with no table, and a hash
survives a restart as colibri's `.coli_kv` does. A collision costs a re-prefill, never an answer.
*Rejected:* the hub's conversation key on `InferenceJob` (rule 6); a node-side LRU (state a restart
loses). **Not established on a real engine:** only GLM-5.2/5.3 accept more than one slot (v1.12.1's
family registry; 372 GB+), and the engine we ran, OLMoE, keeps nothing between requests at all
(`[PREFIX] no reuse: held=0`, identical request twice: ~63 s each). Default `KvSlots=1` sends nothing.

**D3 — `KvSlots` is the node's `MaxConcurrency` unless `Node:MaxConcurrency` is written.** The hub
queues on that number (phase 9); without it the hub fans out and colibri's gateway — *measured*,
12 parallel requests straight at it — answered 7 and **dropped 5 connections**, never the documented
429. Through a node: serialized, all 200. Solo gets the `LocalConcurrencyGate` for the same reason.

**D4 — the watcher declares on 69's threshold and never restarts.** `GET /health` at the gateway's
root, classified by `OllamaProbe.ClassifyAsync` (now public — the Windows connect quirk is not to be
rediscovered per backend), factory logging removed from the probe client. A 744B prefill is
indistinguishable from a wedge. **Widened by a real boot:** `Recovered` also fires when a healthy
probe follows *any* failure, not only a declared outage — the launched engine is still loading when
the node registers, its listing is "could not ask", and the node sat unroutable until the 60 s
refresh. Measured after: routable 15 s after `docker run`.

**D5 — the node launches `coli serve` only when `Colibri:Serve:Model` is set**, loopback, relaunching
on exit (2 s doubling to 60 s, reset after a minute up) and never because it is slow. A missing
directory is one warning and no process. `Serve:Model` + `Upstream:BaseUrl` fails startup naming
both. The child inherits `COLI_*` **except `COLI_DEBUG`**, which tees the rendered prompt to stderr —
and this node logs that stream (rule 7; verified with `COLI_DEBUG=2`: a warning, no prompt in the log).
The gateway's own access-log line for the watcher's probe is dropped; everything else is pumped.

**D6 — every body carries a `Content-Length` (found against the real gateway, invisible to stubs).**
The shared client's `JsonContent` goes chunked; Python's `http.server` answers that with `400 Request
body must be between 1 and 4194304 bytes` — every chat, every stream. `ColibriRequestHandler`
re-sends each POST as a sized `StringContent` (and adds D2's slot). *Rejected:* changing the shared
client — a wire change to every vendor and the hub's providers to fix one gateway.

**D7 — a refused stream is a refusal on the OpenAI surface, on both hosts.** Found here, true of every
backend: a node fails a streamed job with `{"error":…,"done":true}`, and the chat/completion formatters
rendered that terminal chunk as an empty `finish_reason=stop` under a 200 — a refusal a client reads
as an empty answer (the Ollama surface passed the line through and was right). `OpenAiSse.TryReadFailure`
now catches it in `LocalSseResult` and the hub's `OpenAiStreamingResult`: **before the first byte, the
blocking path's own 502 with the OpenAI envelope; after it, an `error` frame** the OpenAI SDKs raise
on. A node *disconnect* mid-stream still truncates with `stop` — it carries no sentence to report.

Tests: `ColibriBackendTests`, `SoloStreamRefusalTests` (Node), `OpenAiStreamingTests` (Coordinator),
`BundledNodeTests` (pin, checksum, matrix entry). Measurements: `.claude/release-notes-v3.58.0.md`.


### Phase 94 (Brio — a closed question answered with a distribution, through `/v1/brio`)

Files: `Backends/IClosedSetScorer.cs`, `Backends/Colibri/BrioClient.cs`, `UpstreamBackend.cs`,
`Tools/ToolExecutor.cs`, `LocalApi/LocalBrioEndpoints.cs`; the hub's half is
`src/InferHub.Coordinator/OpenAi/BrioEndpoints.cs`, the renderer both call `src/InferHub.Shared/Brio/`.
colibri's Brio reads each allowed option's log-probability after a shared prefix and answers with a
distribution and an entropy — "I don't know" is a number. Forms: `options`, `questions`, `schema`.

**D1 — a `ToolJob` with capability `score`, declared by the colibri backend.** No Ollama shape, so
40 D3; `ToolJob` already is that contract. colibri declares `[chat, score]` (amends 93 D1's `[chat]`);
`Node:Capabilities:Disabled: ["score"]` turns it off. `IClosedSetScorer` is registered on a colibri
node only, and `ToolExecutor` sends a `score` job there before any tool runtime is asked — so the
mesh path and solo reach it without learning it exists. *Rejected:* an `InferenceJob.Kind` (40 D3);
a tool worker (a second client of the engine's one slot).

**D2 — the surface is colibri's own `/v1/brio`, passed through.** The edge (`BrioRequest`) reads the
model and that exactly one form is present; the engine validates the rest and its 400 keeps its
sentence. `/api/tools/score` works too, unmetered like every generic tool call.

**D3 — billed in tokens: `usage.total_tokens` as prompt, completion 0, kind `score`**, against the
chat quota (same engine). Admission before routing; a failed job and a 200 with no `usage` (→ 502)
are never billed. Solo takes a `LocalConcurrencyGate` slot like a chat (37 D9).

**D4 — the node adds nothing to the body.** With no `cache_slot` the gateway hashes `state` into a
slot itself — 93 D2 keyed on what does not change, where the KV lives. 93 D6's `Content-Length` holds.

**D5 — the node states the failure, the edge renders it.** 429 → `Retry` (503 + `Retry-After`, not
retried here, 93 D3); 404 → `model_not_found` (404); other 4xx → `invalid_request` (400); 5xx,
unreachable, timeout → 502. Logs carry model, form, count, tokens — never state, question or option.

Tests: `BrioContractTests` (Shared), `BrioScorerTests` (Node), `BrioMeshTests` (Mesh — real hub,
SignalR, node, and a gateway over a socket; solo too). Measurements: `.claude/release-notes-v3.59.0.md`.

### Phase 95 (`Backend:Engines` — ollama, llama.cpp and colibri on one node, started and stopped by the hub)

Files: `Backends/Engines/` (`EngineOptions`, `BackendOptionsValidator`, `MultiBackend` + `Engine`,
`MultiBackendComposition` + `MultiBackendHost`, `EngineProcess`, `ChildProcessJob`, `LlamaCppServe`,
`IEngineControl` + `IModelKinds`), `Profiles/NodeProfileClamp.cs` (`ClampBackends`),
`CoordinatorConnection.cs` (`ReportBackendState`). The hub's half: `NodeBackendRegistry`,
`NodeBackendToggle`, `POST /api/admin/nodes/{id}/backends/{name}/start|stop`, the console's Engines
panel, `engines` on `/api/status` nodes. Contract: `NodeBackendState`, `NodeProfile.Backends`.

**D1 — several engines behind one `IInferenceBackend`, routed by model** (load-bearing). `MultiBackend`
lists every running engine, builds model → engine, and sends each request where its `model` was
reported; the connection, solo mode, retrieval and model commands hold one backend as always.
`Backend:Engines` non-empty replaces `Backend:Type`; empty is byte-identical to v3.59. A `Type` of
anything but the shipped default `ollama` beside it fails startup (the `:colibri` image sets
`colibri`: clear it with `Backend__Type=`). A model two engines report goes to the engine whose
**name sorts first** — .NET configuration binds a section's keys sorted, so "file order" is not
observable — logged once. *Rejected:* a model-name prefix per engine (changes every client's model id).

**D2 — engines are the ones you can start and stop: `ollama`, `llamacpp`, `colibri`, `openai`.** A
vendor stays a single `Backend:Type`. At most one `ollama` and one `colibri` — each *is* its section
(`Ollama:`, `Colibri:`), unchanged; `llamacpp`/`openai` repeat (one `llama-server` per GGUF). Each
engine is the class that already drives its type, a second *instance* not a second driver; Ollama
keeps phase 85's on-demand decorator. Per-model kinds (`IModelKinds`) feed the declaration, so a
colibri model is `chat`+`score`, an `Embeddings: true` llama.cpp model `embed` only (`llama-server
--embeddings` serves nothing else), and a kind no model serves is not declared. `llamacpp` is also a
plain single `Backend:Type` (OpenAI dialect, `127.0.0.1:8080/v1`, chat+embed).

**D3 — the node launches `llama-server` when `Serve:Model` is set**, loopback, `--alias` (default the
file's stem) as the routed name — 93 D5's consent and relaunch loop, now `EngineProcess`, which can
stop without the host stopping. colibri engines launch through `Colibri:Serve`. Prompt-logging flags
(`-v`, `--verbose`, `-lv`, `--log-verbosity`…) in `Serve:Arguments` fail startup (rule 7: the node
logs the child's output). **Windows: every child joins one Job Object with `KILL_ON_JOB_CLOSE`** —
found by killing a live node, which left `llama-server` holding its port and RAM. A bare-metal Linux
node killed with `-9` still orphans its engines (a container's die with it); not fixed.

**D4 — the hub starts and stops engines through the profile; the node's list is the ceiling.**
`NodeProfile.Backends` {name: bool}: `false` always works, `true` only for a name in the box's
`Backend:Engines` — being listed is the grant, `Autostart` only the boot default, so starting a listed
engine is not widening (43 D1). Names are tokens (`[A-Za-z0-9][A-Za-z0-9_.-]{0,63}`), never a path or
command; a profile carries no binary, model or port. Desired state (43 D2): a stopped engine stays
stopped across a reboot of either side. `NodeBackendToggle` is `NodeModelToggle`'s read-modify-write
(`node:{id}` profile when none matches), refusing for a better message a node with no engines (409) or
a name it never reported (404) — the clamp is the copy that is load-bearing. **A meshed node waits up
to 15 s for its profile before autostarting** — found live: booting straight into `Autostart`
launched an engine the hub had stopped, loaded its weights, and killed it a second later.

**D5 — a stop leaves the routing table first, drains, then kills.** New work is refused at once and
the hub is told (`Changed` → model report); in-flight requests get `Backend:StopDrain` (30 s), then a
launched process is killed; a stopped Ollama engine unloads only the models this node sent to it
(85's reasoning — the desktop's Ollama is shared). A start is announced the moment the engine answers
a listing (polled every 2 s, up to 15 min), so a loading `llama-server` reads `starting`, not lost.

**D6 — no single backend health on a multi-engine node.** 69 routes a whole node on one verdict;
one engine down must not unroute the rest, so the supervisor is `NoBackendSupervisor` and each engine
reports `running | starting | stopped | unreachable | failed` in its own block (the 44 D6 mailbox).
A dead engine withdraws only its models; every running engine silent is "could not ask" (null), not
zero. `Node:MaxConcurrency` is the operator's — colibri's `KvSlots` is not folded in (93 D3) here.

Tests: `EngineTests` (Node), `EngineMeshTests` (Mesh — real hub, SignalR, node and two llama-server
stand-ins over sockets). Live run with a real Ollama, real `llama-server` and real colibri:
`.claude/release-notes-v3.60.0.md`.

### Phase 96 (a llama.cpp engine is the whole of llama.cpp: a router, its model management, its native routes, its reranker)

Files: `Backends/Engines/` (`LlamaCppBackend` + `RouterModel`, `LlamaCppServe` router argv and INI,
`EngineOptions` `ModelsDir`/`Presets`/`MaxLoaded`/`Router`/`Reranking`, `LlamaCppPresetOptions`, the
validator, `MultiBackend.Manager` + `IBackendToolJobs`), `IInferenceBackend.UnloadAsync`,
`ModelCommandExecutor`, `Tools/ToolExecutor.cs`, `LocalApi/LocalLlamaCppEndpoints.cs`; the hub's half is
`src/InferHub.Coordinator/OpenAi/LlamaCppEndpoints.cs` (`/v1/llamacpp/{op}`, `/v1/rerank`) and
`AdminEndpoints` (`unload`, `?engine=`); the contracts both read are `src/InferHub.Shared/LlamaCpp/`.
Like Brio's (94), the edge files sit outside this folder because they are the client surface; nothing
in them knows how the engine is launched or listed (rule 1).

**D1 — `Serve:ModelsDir` or `Serve:Presets` launches one `llama-server` in router mode** (load-bearing).
No `-m`: b11417 then lists a directory and a preset INI, spawns one child per model on first use, LRU
past `--models-max` (`MaxLoaded`), and every route proxies by `model`. `Serve:Model` beside either fails
startup; a v3.60 config is unchanged except that it now also declares `llamacpp`. The node writes the
INI (temp dir, fresh per launch) from `Presets`, because only it knows which model embeds (D2); keys
are plain tokens, `host`/`port`/`alias`/`model`/verbosity are refused (rule 7, and the router owns
them). *Measured:* a `Serve:Arguments` flag reaches every child **and wins over a preset key**.
*Rejected:* an operator-written INI path (the node would have to parse it to declare kinds); LLamaSharp
or P/Invoke (a native library in the node's process is a package and a crash that takes the node with
it — rule 5, 41 D1). Windows: a router's children are in the node's Job Object (95 D3) — measured, a
killed node left no `llama-server` behind.

**D2 — kinds are configuration.** A preset with `Embeddings` is `embed`, with `Reranking` `rerank`,
anything else (a file in the dir, a pulled repo) `chat`; every model also `llamacpp`. The router's
listing says nothing about pooling. A model `downloading` is not listed. **Found live:** a preset's
`HfRepo` lands in llama.cpp's cache and the router lists the cache too, so the reranker reappeared under
its repo name — as a chat model. A cache entry equal to a preset's `HfRepo`, or a dir entry that is a
preset's `Model`, is not listed; the preset's name is the one.

**D3 — the router's own endpoints manage its models.** pull = `POST /models` (llama.cpp's Hugging Face
download, `hf.co/` stripped) then poll `/models` each second until it is not `downloading`; **no byte
counts** — the router reports none and the frame says so. delete only where `can_remove` (a downloaded
repo; a dir file or preset is refused naming the key). warm = `/models/load`, then wait for `loaded`;
anything else after one poll of grace is a failed load (a child can die before the first poll sees
`loading`). **`unload` is a new `ModelCommand` kind** (`/models/unload`; Ollama `keep_alive: 0`).
`ModelCommand.Engine` / `?engine=` names the engine; without it a model some engine reports goes
there, and a pull with two engines able to pull is refused naming both — `owner/model` is an Ollama
name and a Hugging Face repo. The hub unescapes `%2F` in the route's model.
**Two wrong-since-earlier things the live run found:** a `Backend:Engines` node told the hub "cannot
manage models" at registration because no engine was *running* yet (95 waits for the profile) —
management is now declared from configuration and a stopped engine refuses in a sentence; and since
26 a pulled model was not routable (a deleted one stayed routable) until the next refresh — the node
now reports its models when a pull or delete finishes (measured: routable 14 s after the pull began).

**D4 — the native surface is one `ToolJob` kind, `llamacpp`, behind an allowlist.** POST `completion`,
`infill`, `tokenize`, `detokenize`, `apply-template`, `embedding`; GET `props?model=`. Body passed
through (94 D2), the engine's 4xx keeps its sentence, `stream: true` is a 400 naming `/v1/completions`.
Not `/slots` (prompt text — rule 7), `/metrics`, `POST /lora-adapters` (shared state). Billed from
`tokens_evaluated`/`tokens_predicted`, kind `llamacpp`. Same routes on solo. The node's backend answers
these and `rerank` before any tool runtime (`IBackendToolJobs`, 94 D1's order).

**D5 — `/v1/rerank` is the fleet's.** Jina/Cohere shape, phase 80's `{query, documents}` → `{scores}`
job, so a cross-encoder tool worker answers it unchanged and `Retrieval:RerankModel` can name a
llama.cpp reranker. 80's exact-length rule: a reranker that skipped a document fails.

**D6 — Ollama's samplers reach llama.cpp under its names** (`top_k`, `min_p`, `typical_p`,
`repeat_penalty`, `repeat_last_n`, `mirostat*`, `num_keep`→`n_keep`) — a flag on `OpenAiUpstreamClient`
set for `llamacpp` only; OpenAI proper 400s an argument it does not know.

Tests: `LlamaCppRouterTests` (Node), `LlamaCppContractTests` (Shared), `LlamaCppMeshTests` (Mesh — real
hub, SignalR, node and a b11417-shaped router over a socket). Live run against a real b11417 router,
real Ollama and a real Hugging Face download: `.claude/release-notes-v3.61.0.md`. Image check (`:colibri`, the
Linux build launched in the container): the same, end to end. **Not established:** a multimodal model
through the router (no `mmproj` on this box). Two container traps, recorded in the notes rather than
fixed: the plain image lacks `libgomp`, and `Serve:Port`'s 8080 is the images' local API port.

### Phase 97 (a colibri catalogue: many converted models, the one a request names loaded, idle ones freed, the hub picking)

Files: `Backends/Colibri/` (`ColibriCatalog` + `IColibriControl`/`IColibriLauncher`/`ColibriProcessLauncher`,
`ColibriCatalogHost` + `ColibriCatalogComposition`, `ColibriOptions` catalogue keys and validator,
`ColibriServe.StartInfo` per model), `Engines/IEngineControl.cs` (`IEngineLifecycle`), `MultiBackend`
(lifecycle, `SupportsPull`), `Profiles/NodeProfileClamp.cs` (`ClampColibri`), `NodeProfileApplier`,
`CoordinatorConnection` (`ReportColibriState`). The hub's half: `NodeColibriRegistry`, `NodeColibriToggle`,
`POST /api/admin/nodes/{id}/colibri/models/{model}/load|unload` and `/colibri/on-demand/enable|disable`, the
console's Colibri models panel, `colibri` on `/api/status` nodes. Contract: `NodeColibriState`, `NodeProfile.Colibri`.

**D1 — a catalogue is a second shape beside `Serve:Model`, one `coli serve` per loaded model** (load-bearing).
`Serve:ModelsDir` (sub-directories holding a `config.json`, named by directory, rescanned on every listing)
and `Serve:Models` {name: dir} make the backend a `ColibriCatalog`, single or as a `Backend:Engines` colibri
engine (the same DI singleton either way). `coli serve` takes exactly one `--model` (v1.12.1), so there is
no engine router to drive as in 96: each loaded model gets a port from `Serve:Port` up, its own
`EngineProcess` (95 D3's loop and Job Object) and its own `UpstreamBackend`, so 93 D2/D6 and 94 hold per
model unchanged. `Serve:Model` or `ModelId` beside a catalogue fails startup (the `:colibri` image sets
`Model`: clear it); a v3.61 config is byte-identical. The whole node has no 69 health verdict
(`NoBackendSupervisor`): an unloaded model is not a down engine. *Rejected:* one process relaunched with
another `--model` per request — two models could never be resident together.

**D2 — the listing is the catalogue; a request loads its model, evicting the least recently used idle one.**
Loaded → go; a slot free → launch and wait for `/health` (`Serve:LoadTimeout`); full → stop the LRU model
with nothing in flight and not pinned **before** launching (its RAM and port are free first); all busy →
wait; all pinned → refuse naming them. A process that exits before it answers is a failed load with the
exit in the sentence and `failed` in the report, retried on the next request. A dying process still
counts against `MaxLoaded` until it is gone. A pull is refused in a sentence (`coli convert` is hours of
CPU, not a download) — `IInferenceBackend.SupportsPull`, so an unnamed pull on an ollama+colibri node
still goes to Ollama rather than being refused as ambiguous (96 D3). Warm/unload commands load and stop.

**D3 — on demand stops idle models; a pinned one never is.** `Serve:OnDemand` + `IdleUnload` (10 min):
a sweep (`IdleUnload/4`, 1–30 s) kills the process tree of an unpinned model idle that long — 85 D2's
reason, a hint keeps the memory.

**D4 — the hub selects through the profile: `colibri.loaded` (the pinned set) and `colibri.onDemand`.**
Desired state (43 D2): pins load when the catalogue starts and after a reboot of either side; a model
dropped from the set is unloaded, and stopped before the next pin loads. The clamp: names from this
box's catalogue only (refusals name what it has), at most `MaxLoaded`; `onDemand` either way. No block
means the box's `Preload`/`OnDemand`. `NodeColibriToggle`'s **load on a full node is a switch** (the oldest
pin gives way — on the default one slot, "this model instead of that one"). An unload command for a pinned
model is refused naming the profile. A meshed single-backend node waits ≤15 s for its profile before
loading `Preload` (95 D4's finding).

**D5 — `NodeColibriState` on 44 D6's mailbox**: `loaded|loading|unloaded|failed`, pinned, in flight, idle
seconds, last error, mode, `MaxLoaded`, `running`; on the model loop, after a profile, and on every load
or stop. A hub older than v3.62 drops it with a debug line.

Tests: `ColibriCatalogTests` (Node — every model a real socket via `FakeColibriLauncher` in `Tests.Common`),
`ColibriCatalogMeshTests` (Mesh — real hub, SignalR, node; route an unloaded model, switch, on-demand,
a hostile pin refused by both). Live run with real colibri: `.claude/release-notes-v3.62.0.md`.
