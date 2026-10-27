# InferHub.Node/Backends — agent context

**Scope: `src/InferHub.Node/Backends/`.** The engines a node drives that are not "an Ollama and a
dialect": colibri and its Brio, since phase 95 a node running several engines at once —
`Backend:Engines`, ollama + llama.cpp + colibri side by side, started and stopped by the hub — since
96 a llama.cpp engine that is all of llama.cpp: a router over many GGUFs, managed from the hub — and
since 97 a colibri catalogue: many converted models, one loaded on request, idle ones freed, the hub picking —
since 98 Hugging Face links the node downloads, and since 99 Strata: an install's sizes as the same kind of
catalogue (`Backends/Catalog/`, shared with colibri), installed from a link by Strata's own setup — since 100
with pictures, per size.

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

### Phase 98 (a Hugging Face link from the hub: the node downloads it once, llama.cpp serves a GGUF, colibri a converted checkpoint)

Files: `Backends/HuggingFace/` (`HuggingFaceOptions` + `HuggingFaceTargets` + validator, `HuggingFaceClient`,
`HuggingFaceStore`, `ColibriConverter`), `MultiBackend.RestartAsync`, `ModelCommandExecutor` (engine
`huggingface`, `ManagesModels`), `Dockerfile.colibri` (the converter venv); `src/InferHub.Shared/HuggingFace/HfReference.cs`
(the link, parsed the same on both hosts); the hub's half is `POST /api/admin/nodes/{id}/huggingface` and the
console's "Download to node". Contract: `ModelCommand.EngineHuggingFace`.

**D1 — `HuggingFace:Enabled` is the consent, and each engine's own directory is the store** (load-bearing).
Off by default (39 D7: a GPU box reaches the internet when its operator says so). **colibri does not
read GGUF** (v1.12.1: its own format, made by `coli convert` from a safetensors MoE checkpoint), so one
file never feeds both engines: a GGUF goes to `<llama.cpp router Serve:ModelsDir>/<name>/` — a
sub-directory is one model in llama.cpp's convention (measured, b11417: listed under the directory's
name), so split files and an `mmproj` travel together — and a checkpoint is converted into
`<Colibri:Serve:ModelsDir>/<name>`. `.inferhub-source.json` (repo, commit, files) marks what the store
made: a second pull is "already downloaded", and a delete touches only a marked directory, never one the
operator put there. `HuggingFace:LlamaCppEngine` names the router when two have a `ModelsDir`; an engine
named `huggingface` is refused (the reserved command engine). *Rejected:* a separate store tree linked into
both engines — nothing reads the same bytes twice, so it would be a second place to clean up.

**D2 — GGUF: the node downloads, resumably and verified, then restarts the router.** Commit from
`/api/models/{repo}/revision/{rev}` (the download pins to it), files from `/tree/{commit}?recursive=true`
(size, LFS sha256; paged by `Link`), bytes from `/{repo}/resolve/{commit}/{path}` into `.part.partial`
with `Range` resume, size and sha256 checked (a mismatch deletes the file), renamed only when every file
is whole — the router never lists a half model. Selection: a file link takes that file and its split
siblings; `quant` takes the files whose name carries it as a whole token; one model in the repo is taken;
several and no quant is refused naming them; an `mmproj` comes along. **The router reads its directory at
launch only** (measured), so the engine restarts after a download and around a delete (95 D5's drain; on
Windows a mapped file cannot be deleted). Byte progress on the phase-26 frames, `MaxConcurrentDownloads` (2).

**D3 — a checkpoint: `coli convert`, one at a time.** No `.gguf`, and `config.json` beside `*.safetensors`
→ `python coli convert --repo <repo> --model <dir>/<name>` (name: the repo, lowercased); colibri picks its
family's converter and downloads the shards itself through `huggingface_hub` (`HF_TOKEN`, `HF_ENDPOINT`,
`HF_HOME=<ModelsDir>/.hf-cache` passed; never `COLI_DEBUG`). Its output lines are the progress; a failure
removes the half-written directory and reports the last lines. Main branch only (the converter takes no
revision). The `:colibri` image builds the converter venv at `/opt/colibri/mio_env` (where `coli` looks
first) in the final stage, torch CPU pinned, imports asserted at build. A checkpoint on a node without a
catalogue is refused saying llama.cpp needs a GGUF. *Rejected:* the node downloading the checkpoint and
pointing the converter at it — colibri's converters fetch with `hf_hub_download(local_dir=…)`, which does
not read a cache, so the shards would cross the network twice.

**D4 — the phase-26 pull, engine `huggingface`.** The hub's `/huggingface` route parses the link
(`HfReference`: huggingface.co / hf.co URLs, `/tree|blob|resolve/`, `owner/repo[:quant]`; any other host,
`..` or a backslash is refused) and sends the canonical form; the node parses it again. Delete:
`DELETE /api/admin/nodes/{id}/models/{name}?engine=huggingface`. Warm/unload go to the engine. A node with
the store declares model management even when no engine can (`ManagesModels`).

**Four things the live run found, all fixed:** `coli convert` under the image's bare `python3` could not
import `huggingface_hub`, misread the family and ran GLM-5.2's converter on an OLMoE — the converter now
runs under `<launcher dir>/mio_env` (`ColibriProcessConverter.Interpreter`, as `coli` itself prefers); the
converter writes `config.json` early and the catalogue listed a half-converted model — conversion goes to
`.converting-<name>` (not a model name, never listed) and is renamed when whole; on a fresh volume the
router's `ModelsDir` did not exist and the engine sat `failed` — the store creates its targets at startup
(`HuggingFaceStartup`, registered before the engines); the factory logged every request including signed CDN
redirects — the client has no loggers.

**v3.63.1, found on the published image: a model command no longer holds the node's connection.**
SignalR hands a client the hub's calls one at a time and awaits each handler; the model-command handler
awaited the whole command, so a conversion (or, since 26, a long Ollama pull) stopped the node receiving
jobs. `CoordinatorConnection` now runs it off the dispatch (`Offload`). Jobs stayed one at a time per node
until **phase 107** ran them off it too, which also found the hub had never sent a cancel for a blocking job —
see that block in `src/InferHub.Node/CLAUDE.md`.

Tests: `HfReferenceTests` (Shared), `HuggingFaceStoreTests` (Node — a Hub-shaped socket in `Tests.Common`:
resume by `Range`, sha256, gated 401, quants, mmproj, conversion via a fake converter), `HuggingFaceMeshTests`
(Mesh — link → converted → routable → chat answered). Live run: `.claude/release-notes-v3.63.0.md`.

### Phase 99 (Strata: an install served as a catalogue, its sizes installed from a Hugging Face link by its own setup)

Files: `Backends/Catalog/` (`ModelCatalog` + `ICatalogControl`/`ICatalogLauncher`/`ICatalogProcess`/`ICatalogServeOptions`,
`ModelCatalogHost`), `Backends/Strata/` (`StrataOptions` + validator, `StrataCatalog` + `StrataServe` +
`StrataProcessLauncher`, `StrataInstaller` + `StrataSetupRunner`, `StrataComposition`), `ColibriCatalog` (now a
subclass), `BackendOptions.Strata`, `ModelCommandExecutor` (Strata links), `HuggingFaceTargets.Strata`,
`Profiles/NodeProfileClamp.cs` (`ClampCatalog`), `CoordinatorConnection` (`ReportStrataState`); `src/InferHub.Shared/Strata/StrataModels.cs`.
The hub's half: `NodeStrataRegistry`, `NodeStrataToggle` (both on `NodeCatalogRegistry`/`NodeCatalogToggle`),
`/api/admin/nodes/{id}/strata/models/{m}/load|unload`, `/strata/on-demand/enable|disable`, `/strata/install`,
the console's Strata models panel, `strata` on `/api/status` nodes. Contract: `NodeCatalogState` (+ `installable`), `NodeProfile.Strata`.
[Strata](https://github.com/Niko1221/Strata) (MIT) runs Qwen3.8-Flash-Next (125B MoE) on one GPU plus RAM; one
model in nine sizes over four families at 0.1.41 (21 over seven, Qwen3.6 among them, since 0.2.2 — 111), each prepared for the box by its `setup.py` into a `strata-<size>.json`
that `serve/server.py --config` serves (OpenAI dialect).

**D1 — 97's catalogue is `ModelCatalog`, and Strata is its second user** (load-bearing). Admission, LRU,
pins, on-demand and the state report moved verbatim out of `ColibriCatalog`; a subclass supplies its name,
section, process name, kinds, `Scan()` (name → path), `IsReadyAsync` and an optional `Installable`. Colibri's
sentences are byte-identical and its tests ran unchanged. Contract types renamed engine-neutral
(`NodeCatalogState`, `NodeCatalogModel`, `CatalogProfile`, `CatalogToggleOutcome`) — the wire is the same JSON;
each engine keeps its own hub method, so an older hub drops only the report it does not know. *Rejected:* a
second copy of the 1 000 lines; folding Strata into colibri's section (two engines, two `Serve:` blocks).

**D2 — a Strata model is a config, named by its file.** `strata-*.json` in `Strata:ConfigDir` (default `Root`,
where setup writes them), kept only when it is a JSON object with `exe` and an `args` list — Strata's own
#549 test, so `*.shared-settings.json`, a user's file and a half-written config are skipped. The name is the
file's (`strata-coder-iq1_m`): every config's `model_name` is the same family name. Chat only (no embeddings;
pictures were called a setup choice the config does not reveal — wrong, see 100 D1). Pull and delete refuse in a sentence: an install is
D4's link, and sizes share files only setup can tell apart. `Backend:Type=strata` without `Root` is a plain
upstream at Strata's `127.0.0.1:8080/v1`.

**D3 — one `server.py` per loaded config, loopback, nothing that keeps prompts.** `--engine strata --config
<cfg> --host 127.0.0.1 --port <Serve:Port+i>` from `Root`, under Strata's `.venv` when it exists. Rule 7:
`STRATA_DEBUG` (raw model text on stdout) is removed from the child, and `--api-monitor` (the last 100 prompts
for a web page) is refused in `Serve:Arguments` with the node-owned flags. **Ready is `/health` 200 and not
`"loaded": false`** — Strata answers health while a lazy engine loads. The config's own `api_key` (first of
`"k1,k2"` or a list) is the client's key unless `Strata:ApiKey` is written, which reaches the child as
`STRATA_API_KEY`, never argv. Port 8095 (8080 is the images' local API, v3.61's trap); one slot; 20 min load.
`Serve:Engine=mock` is Strata's own canned engine — a node's wiring checked before a 70 GB download.

**D4 — an install is Strata's setup, reached by 98's link.** `StrataModels` maps a link to one of the four
repos (size from `:quant`, a file's name, or the repo's only size; a revision is refused — setup pins its own)
to `--family`/`--model`; anything else stays the GGUF store's. The executor hands a Strata link to
`StrataInstaller`, which runs `setup.py --setup --yes --no-start --no-browser --family F --model S --host
127.0.0.1 --vision no` (+ `DataDir`, `Install:Context`, `Install:Arguments`; node-owned flags refused), one at
a time, `HF_TOKEN`/`HF_ENDPOINT` from `HuggingFace:`. **Setup draws its bar with `\r` alone**, so the runner
reads characters, not lines; each percent is a frame with bytes. Success is the config listed afterwards, not
exit 0. `HuggingFace:Enabled` is the consent (98 D1) and also what makes the state carry `installable`, which
the hub's `/strata/install` maps a name back to a link from. *Rejected:* the node downloading the GGUFs and
passing `--gguf-dir` — a second resume/checksum implementation, and setup's pack step still after it.

Tests: `StrataModelsTests` (Shared), `StrataCatalogTests` (Node — `FakeStrata`, `FakeSetupRunner`, the fake
launcher's Strata health), `StrataMeshTests` (Mesh — real hub, SignalR, node: route, install from a link, pin,
refused delete). Live run with a real Strata install on a real GPU: `.claude/release-notes-v3.64.0.md`.

### Phase 100 (Strata pictures: each size says whether it reads them, a picture it cannot read is a 400 before a load, the hub adds the encoder)

Files: `StrataCatalog` (`HasImageEncoder`, `HasPictures`, `Refuse`), `ModelCatalog` (`ImagesOf`, `Refuse` →
`Admit` before `EnsureLoadedAsync`; `CatalogException.Status`), `StrataInstaller` (`vision`, `Settled`,
`AddingPictures`, `KeptChoices`), `InferenceExecutor` (a `CatalogException` with a status is
`InferenceResult.Refused`, its stream chunk carries `status`), `ModelCommandExecutor` (`command.Vision`).
Shared: `NodeCatalogModel.Images`, `ModelCommand.Vision` (+ `IsKnownVision`/`WantsImages`),
`InferenceResult.Status` (+ `HttpStatusOf`, `Refused`), `OpenAiSse.TryReadFailure(…, out status)`. Hub:
`StrataInstallRequest.Vision`, `ModelCommandCoordinator.SendAsync(vision:)` (in the coalescing key),
`InferenceCore` and `OpenAiStreamingResult` answer a refusal's 4xx; solo: `LocalOpenAiEndpoints`, `LocalSseResult`.
Console: a "pictures" chip on a model, a **with pictures** box beside Install.

**D1 — whether a size reads pictures is in its config** (load-bearing; 99 D2 said it was not). Setup writes
a `vision` object (encoder exe, mmproj, gpu) and `--vision` into `args` for a size set up with images, and
`server.py` starts the encoder from exactly that. `HasImageEncoder` = either; unreadable = null. It rides
`NodeCatalogModel.images` (null for colibri, and from a v3.64 node).

**D2 — a picture for a size without the encoder is refused before its server starts.** Strata refuses it
itself ("started without the vision encoder"), but only after a load of one to three minutes that, on the
one-slot default, evicted the size that was loaded. `ModelCatalog.Admit` asks the subclass's `Refuse` before
admission; `HasPictures` = an `images` entry (the node's own shape, phase 64) or an `image_url`/`image`/
`input_image` part, checked only when the body contains "image" at all. The sentence names the install call
that fixes it. *Rejected:* refusing at the hub — it has the state, but a v3.64 node's state has no `images`,
and the node is where the config is.

**D3 — a node refusal keeps its 4xx.** Every failed node job was a 502 (`InferenceCore`); a request the model
cannot take is the caller's, so `InferenceResult.Status` (and `status` on a stream's failure chunk) carries
it, and the hub and the solo edge answer it as `invalid_request_error`. Only a `CatalogException` with
`Status` sets it today; an engine's own 4xx inside a job is still a 502 (not widened here). Absent = 502, so
an older node is unchanged; a non-4xx value is ignored. *Found on the live run:* the first refusal through
the hub was a 502.

**D4 — pictures are the install's choice; asking for them on an installed size runs setup again.**
`POST …/strata/install {"model", "vision": "yes|cpu|no"}` → `ModelCommand.Vision` → `setup.py --vision`;
absent is `Strata:Install:Vision` (default `no`), so a plain request never re-runs setup on a `yes` node.
An explicit `yes`/`cpu` for an installed size without the encoder is "change settings" in setup's words:
everything present is skipped, the ~0.9 GB mmproj is fetched (its bar is labelled `vision encoder:` —
with a space, which 99's progress regex missed), the config gains the encoder. Refused while that size is
loaded or loading (setup rewrites the config its server started from). Success is the config having the
encoder, not exit 0. **`KeptChoices`:** setup answers every question again for a size — it reuses an
earlier config's answers only when adopting a fresh copy of Strata — so the node passes the config's own
`--max-context` as `--context` and its `--kv` back, unless `Strata:Install` sets them. *Found before the
live run by reading setup:* without it the 32K Coder would have come back at this card's 128K.

Tests: `StrataCatalogTests` (Node: images per config, encoder detection, picture detection, refusal before
launch blocking and streamed, kept choices, add-pictures re-run, loaded refusal, setup that drops the
encoder), `StrataMeshTests` (Mesh: 400 on the job, `vision` across the wire, `images` at the hub),
`OpenAiStreamingTests` + `SoloStreamRefusalTests` (the 4xx on both edges), `StrataModelsTests` (Shared:
optional fields read both ways). Live run: `.claude/release-notes-v3.65.0.md`.

### Phase 102 (hard limits per runtime — the node's ceiling, the hub's narrowing) — also load-bearing

Files: `Resources/Limits/` (`EngineLimitMath`, `EngineLimiter`, `WindowsJobContainer`, `LinuxProcessContainer`,
`ProcessInspector`, `GpuProcessMemory`), `Configuration/NodeOptions.cs` (`Node:ResourceLimits:Engines`),
`Profiles/NodeProfileClamp.cs` (`ClampLimits`), `LocalApi/LocalEngineLimitsAdminEndpoints.cs`; the hub's
`NodeLimitsRegistry`/`NodeLimitsControl`, `/api/admin/nodes/{id}/limits[/{runtime}]` and the console's Runtime
limits panel. User-requested (Bulgarian): the node controls what strata, colibri, ollama, llama.cpp or any model
may use, as a hard limit; the coordinator may set limits too, never above the node's, cannot override them, sees them.

**D1 — the ceiling is the node's and the clamp runs on the node (43 D1, load-bearing).** Unlike 82 D1 this one
*is* on the wire: `NodeProfile.Limits` may only go lower. Per field, a hub value above the node's is refused naming
the key and both numbers and the node's stays; a field the node left unset may be set (narrowing); a GPU list must
be a subset, else the whole list is refused; a name that is no runtime is refused (a typo is a limit that silently
does not exist — the validator fails startup on one in config, too). The limiter recomputes min(node, hub) every
poll, so a ceiling lowered later in `node.local.json` still wins over an older hub value. The hub refuses too, for
a 409 with the node's number; a profile written around it is refused by the node (`EngineLimitMeshTests`).

**D2 — one group per runtime, all its processes together** (`ollama`, `llamacpp`, `colibri`, `strata`, `tools`):
three `llama-server`s share the llamacpp budget. Launch sites call `EngineLimiter.PrepareLaunch`/`Adopt` —
statics like `ChildProcessJob`, because a Job Object belongs to the process. The group's job is joined *after*
the lifetime job, so it nests inside it (Windows 8+) and 95 D3's kill-on-close still holds (killing the live node
took its `llama-server` with it). Only processes this node starts: an Ollama service or someone else's server is
not ours to cap (36 D1); the report counts processes, so "0" says so.

**D3 — three ways a limit is held, and the report names which; never "held" when it is not.** *os*: Windows Job
Object (CPU hard cap, affinity, job memory — all re-set live), Linux cgroup v2 `cpu.max`/`memory.max` when this
process may make one (it moves itself into a leaf `inferhub-node` first — cgroup v2's no-internal-processes rule),
`sched_setaffinity` per thread re-applied each poll. *launch*: the GPU list becomes `CUDA_/HIP_/ROCR_/
GGML_VK_VISIBLE_DEVICES` (`-1` = none); the Vulkan llama.cpp ignores the CUDA one — found live; a change stops the
runtime and its own supervisor restarts it under the new list. *watchdog*: GPU memory (no OS caps it) and memory
on a cgroup-less Linux box — read every poll, the runtime stopped above it, a breach counted with the numbers.
Otherwise `none: why`. A limit on a runtime not running yet makes **no** OS object ("applies when it starts") — on
Linux making one moves the node into a cgroup.

**D4 — VRAM per process is read where it really is.** Windows: the `GPU Process Memory(pid_*)\Dedicated Usage`
counters via hand-rolled PDH — NVML under WDDM says "N/A" per process (`nvidia-smi` shows exactly that on this
box). Linux: NVML's running compute processes; in a container those may be host pids, matching nothing — the VRAM
reads 0 and cannot trip; said in the release notes. Rule 5: zero packages.

**D5 — a breach stops the runtime; it does not throttle it.** There is nothing to throttle VRAM with, and a
runtime past its memory is about to fail anyway. The supervisors already relaunch (95's backoff, 36's restart),
so the cost of a limit set too low is a backoff loop that the breach count and `lastBreach` make visible.

Live on this box (3090 Ti, Vulkan `llama-server`, qwen2.5-0.5b): affinity 4 → 2 cores by a `node.local.json`
save with no restart; hub `cpuPercent:50` over the node's 10 → 409 naming 10%; hub `vramMiB:100` → PDH read
255 MiB, stopped; `gpus:[]` → restarted, VRAM 206 → 0 MiB. Tests: `EngineLimitTests` (Node),
`EngineLimitProcessTests` (Mesh — a real Job Object refuses a 400 MB allocation under 150 MB and holds a busy loop
under 1%; Linux affinity and the watchdog on CI), `EngineLimitMeshTests` (Mesh, hub ↔ node).

### Phase 109 (an engine that can be asked says how many requests each model runs at once) — 108's routing is in `src/InferHub.Node/CLAUDE.md`

Files: `Engines/LlamaCppBackend.cs` (`LearnSlotsAsync`, `AskSlotsAsync`, `TotalSlots`), `Engines/MultiBackend.cs`
(`ModelSlots`), `ModelInfo.Slots` in Shared, the hub's `NodeRegistry.GetModelSlots(connection, model)`. 108 gave the hub
one number per node, declared by Ollama only — and llama.cpp, which runs **four** at once by default (`-np -1`, auto;
b11417 `GET /props` → `total_slots: 4`), declared nothing.

**D1 — llama.cpp is asked, per loaded model, with `autoload=false`, once per load (load-bearing).** The router loads any
model a request names (`models_autoload: true`), and `/props?model=X` is a request: asking about an unloaded model would
load it. `&autoload=false` answers `400 model is not loaded` and loads nothing (measured). The answer is kept while the
model stays `loaded` and forgotten when it leaves — a reload may carry another `--parallel` from its preset. A
single-model engine asks `/props` on each listing. Anything but a positive number is no answer. **Rejected: `/slots`**
— off unless `--slots`, and per model on the router anyway.

**D2 — an asked number beats the node's declaration; the fleet list never shows it.** `ModelInfo.slots` is omitted
when null, so a node that asks nothing sends v3.72's bytes. The hub reads `model.slots ?? node.modelSlots` in 108's
`FreeSlotTier`; `DistinctModels` (behind `/api/tags` and `/api/status`'s list) strips it — one node's count is not the
fleet's. `/api/status` `nodes[].slotsByModel` shows what was answered.

**D3 — a node with several engines defaults to its Ollama's number.** `MultiBackend.ModelSlots` = the first engine
that declares one; `Node:ModelSlots` still overrides it at registration; an asked number beats both, because it is a
fact about the running engine and they are statements about configuration. Colibri adds nothing (`KvSlots` is already
`MaxConcurrency`, 93 D3) and Strata publishes no count.

**D4 — a warm or an unload re-reports the models**, like a pull or a delete since 96 — otherwise a freshly loaded
model's count waits out `ModelRefreshInterval`. **v3.73.1:** b11417 answers `POST /models/unload` while the instance is
still stopping, so the re-report listed it `loaded` and the hub kept its count for a refresh — found on the published
3.73.0, cleared by timing alone from source. `UnloadAsync` now polls until it is not `loaded`/`loading` (bounded by
`TimeoutSeconds`; slow to stop ends the wait, not the command). Tests: `LlamaCppRouterTests` (a stub `/props` per model; the call log
proves an unloaded model is never asked), `ModelSlotRoutingTests` (the model's number over the node's; red with D2
reverted).

### Phase 111 (colibri and Strata are told their limits; colibri 2.0's System One; Strata 0.2.2's sizes) — amends 102, 94

Files: `Resources/Limits/EngineLimitTelling.cs` (`IEngineLimitTeller`), `Resources/Limits/GpuCards.cs`,
`EngineLimiter` (Tell in `Prepare`, a `Telling` per root, `told` in the report), `Colibri/ColibriLimitTeller.cs`,
`Strata/StrataLimitTeller.cs`, `Colibri/BrioClient.cs` (`PathFor`), the hub's and solo's `/v1/systemone`,
`src/InferHub.Shared/Brio/SystemOneRequest.cs`, `src/InferHub.Shared/Strata/StrataModels.cs`. User-requested
(Bulgarian): update both engines to their latest, give both a resource limit as the node has it, test with
GLM-5.3-Flash on `E:`.

**D1 — telling never replaces holding (load-bearing).** 102's Job Object, cgroup, affinity and watchdog are
unchanged; a teller only changes the launch, and the report's `told` says what it said beside `enforcement`.
The reason it exists: colibri sizes itself from the free RAM it sees (88%) and Strata fills the card down to its
reserve — neither sees a Job Object, so without it the limit is met as a failed allocation or as 102 D5's
stop-and-restart loop, every start.

**D2 — one teller per runtime, with its engine (rule 1), reached through the limiter.** Registered statically in
`AddResourceGovernance`, called from `EngineLimiter.Prepare`, so every launch site that calls `PrepareLaunch`
is told with no change of its own. A teller that throws is a warning; the runtime starts, held from outside.

**D3 — told stale numbers → started again**, as 102 D3 does for a GPU list: each root keeps
`Describe(Told(effective))` from its launch; a poll that computes another stops it (not a breach) and its
supervisor relaunches it told the new one. A root told nothing (no teller, or adopted before 111) is left alone.

**D4 — colibri is told in variables `coli` reads, never `--gpu`/`--vram`** — those make `coli` exit on a build
without CUDA (every Linux archive and the plain Windows one). Memory → `--ram` 90% in whole GB (the launcher maps
it per family; GLM-5.3 Flash → `GLM53_EXPERT_GB` = G − 8). Cores/CPU → `OMP_NUM_THREADS` (else physical cores).
No GPU → `COLI_CUDA=0`, `COLI_VULKAN=0`. A list → `COLI_VK_DEV` (Vulkan ignores `CUDA_VISIBLE_DEVICES`; an
operator's `COLI_VK_DEV2` becomes the list's second card or goes). VRAM → `COLI_VK_DEVICE_CAP_MB` (every Vulkan
budget reads it) and `CUDA_EXPERT_GB` (limit − 2 GB).

**D5 — Strata is told through `.inferhub-limits-<config>`**, a copy beside the original: its engine reads options
from the config's `args`, not from `server.py`'s line. VRAM → `--vram-reserve-mib` raised by (card − limit per
card), base the config's own reserve else 1536 (the engine's display-card default; an explicit flag also stops
the server's own +700 for a GPU picture reader); card size from NVML (`nvml.dll` works on Windows for totals —
102 D4's "N/A" is per process), unread → watchdog only, said. Cores → `"pool_cores": "1,…,N−1"` (0 is the host
thread's). Memory → only `--resident-budget-gib`, lowered to limit − 8. Nothing to tell → the original config.
No GPU allowed → not started, with the sentence (the CUDA engine stops 3 s after a card-less start). The page's
Settings view edits the copy on such a run — the node binds Strata to loopback, so that is the node's operator.

**D6 — `/v1/systemone`, kind `decide`.** colibri 2.0 removed `/v1/brio`; System One (Jev's shape) is its one
decision API. Same path as 94 (a `ToolJob`, body passed through, billed `input_tokens + output_tokens`); its own
kind so a hub never sends one to a 3.74 node, which would post it to `/v1/brio`. `model` is required at the edge
(the hub routes by it; colibri's "any name" would be a Jev client's `jev-latest`, a 404 here). `/v1/brio` stays
for 1.12 engines; a 2.0 engine's route-404 (no model in the message) becomes "colibri 2.0 removed /v1/brio; send
it to /v1/systemone". *Rejected:* translating Brio to System One — `schema` answered fields in order, each seeing
the earlier ones; System One answers each alone.

**D7 — found live, both on Strata 0.2.** (a) `user_args` are applied over `args` at every start, the person's value
winning: the teller writes a told flag into both, or a user's `--vram-reserve-mib 964` beats the told 13264 and the
watchdog stops Strata at 14943 of 12000 MiB. (b) Strata's server gives its token an owner-only default DACL
(`private_files.adopt_user_defaults`), and a process with one **cannot start a child inside a Job Object that has an
affinity limit** — `CreateProcess` answers ERROR_ACCESS_DENIED, reproduced in fifteen lines of Python with no node. So
`IEngineLimitTeller.CoresOnTheJob` is false for Strata: its core limit is each process's affinity (`SetProcessAffinityMask`,
inherited by children, re-applied each poll like Linux's), reported `os (process affinity, re-applied each poll)`. Every
other runtime keeps the job's. Also: the Job Object no longer switches off a CPU rate it never set (error 87 on every
limit without a CPU share — 102's, noisy and harmless).

**Upstream pins.** `:colibri` image → v2.0.0 (Vulkan engines, no loader in the image → CPU, as 1.12.1). The
Strata family table follows 0.2.2's `setup.py` (unchanged in 0.2.3, the release the live run used): Swift IQ3_S; Unsloth UD-Q5/Q6_K_XL, Q8_0; Gyro-S/M; NVFP4,
NVFP4-NVIDIA, NVFP4-QAD (one repo each, one `--family nvfp4`); Qwen3.6-35B-A3B (own-path, `--family qwen36`).
Tests: `EngineLimitTellerTests`, `EngineLimitTests` (told, restart), `BrioScorerTests`, `BrioMeshTests` (System One).
Measurements: `.claude/release-notes-v3.75.0.md`.

### Phase 118 (will it fit: a link and a context in, this node's engines out — with the arithmetic — and the install with that context)

Files: `src/InferHub.Shared/Fit/` (`GgufHeader`, `ModelFacts`, `MachineFacts`, `FitEstimator`, `ModelLink`),
`src/InferHub.Node/Fit/` (`MachineProbe`, `ModelFactsReader`, `FitService`, `ModelContexts`),
`LocalApi/LocalControlEndpoints.Fit.cs` (`POST /api/control/fit`, `/fit/install`). The app's dialog: `desktop/CLAUDE.md`.

**D1, load-bearing — the estimate is arithmetic the reply shows, and it runs on the node.** Weights (the quant's file
bytes) + KV (context × attention layers × KV width × 2 bytes, f16; × `OLLAMA_NUM_PARALLEL` for Ollama, which gives
each slot the whole context — llama.cpp's router shares one, `kv_unified`) + a working reserve (512 MiB + context/100),
against the cards (NVML, total less 5 % / 512 MiB each), RAM (less a tenth / 4 GiB) and the disk where that engine's
models land — each lowered by the owner's 102 limit for the runtime. Every option carries `why` lines with the
numbers, and `maxContext` / `cardContext` (largest context at all / wholly on the card, binary search in steps of
256, capped at the trained context). **Conservative by design:** measured on a 0.5B at 32K, llama.cpp held 880 MiB
against an estimate of 1692 (the KV part exact). *Rejected:* a table of "model X needs Y GB" — stale the week a model
ships, and silent about the context, which is what moves a MoE between the card and RAM.

**D2 — facts come from the model, read by hand.** A GGUF header by HTTP Range (16 MB, then 128 MB) — from Hugging
Face or from Ollama's registry (`/v2/<owner>/<model>/manifests/<tag>`, the model layer's blob; the config blob's
`model_type: "7.6B"` is the size). Hybrids: a per-layer `head_count_kv` with zeros, else `full_attention_interval`,
else **the tensors** — found live, Qwen3-Next says nothing in its keys and its linear layers carry an `attn_qkv` too,
so a block with `ssm_*` tensors is not attention (12 of 48, not 48). MLA: rank + rope. The experts' share: bytes of
`*_exps` tensors over all, from the gaps between data offsets (no quant block-size table). A checkpoint: `config.json`
(`text_config` for a multimodal one) + the Hub API's `safetensors.total`. A Strata repo: `StrataModels`' own table
(its "48 GB RAM" figures). A repo with neither GGUF nor checkpoint, or a picture model, says what it is and offers nothing.

**D3 — an engine is offered only if it can load the model; ranked by where the model sits.** Ollama and llama.cpp a
GGUF (Ollama not a split one — found live, the bigger Qwen3-Next quants are all split, so Ollama is offered only the
1-bit one), colibri a MoE checkpoint (converted ≈ 1 byte per dense and 0.6 per expert parameter — GLM-5.3-Flash 321B
measured 195 GB; download + converted on disk), Strata only its repos (99 D4 — so `unsloth/Qwen3.6-35B-A3B-MTP-GGUF`,
a plain GGUF, is offered to Strata alone, because that is where the pull goes). Placement score: card 50, experts in
RAM 40 (llama.cpp, a MoE whose dense part + KV fit the card), Strata 35, split 20 (+3 when ≥ 60 % is on the card), RAM
15, CPU 10, from disk 5; + bits a weight rounded, capped at 6; −1 for Q4_0/Q4_1/Q5_0/Q5_1; −15 under 3 bits, −35
under 2 (found live: a 1.7-bit "all on the card, fast" outranked a 4-bit split); +100 when this node can install it.
Ties: the smaller first. An engine the node lacks is still listed, not available, with the sentence why.

**D4 — the chosen context goes with the install, to where each engine keeps it.** `ModelCommand.Context` (appended,
nullable — a node before 3.79 ignores it). Strata: `setup.py --context` (wins over `Strata:Install:Context`).
llama.cpp: the store's marker carries `Context`, and `LlamaCppServe.PresetIni` writes `ctx-size` into that model's
section at every router launch unless the configuration's own preset sets one; a second pull of a model already
here with another context rewrites the marker and restarts the router (live: `n_ctx` 4096 → 32768). Ollama:
`<DataDirectory>/model-contexts.json` (an Ollama name has the `:` configuration keys split on, so not
`node.local.json`), added by `OllamaBackend` as `options.num_ctx` to a request that sets none — the caller's wins
(live: `/api/ps` 6144, then 2048 for a caller asking 2048). colibri: the marker's context becomes `coli serve --ctx` at every launch (D6).

**D5 — the reading is the network's, the decision is the owner's.** `/fit` reaches Hugging Face only under
`HuggingFace:Enabled` (403 otherwise), Ollama's registry from any node (a pull would reach it anyway). `/fit/install`
is the existing pull (112 D6) with the engine named: Ollama `hf.co/<repo>:<quant>`, the rest `engine: huggingface`.
An option that is not available is a 409; one the estimate says does not fit is a 409 unless `force: true`. Facts are
kept ten minutes per link, so a dialog trying contexts asks the Hub once.

**D6 — an engine that plans its own placement is asked, not second-guessed; colibri and Strata use the card their way.**
`Fit/EnginePlanners.cs`. *Strata*: its own `tools/strata_mcp.py` — `model_table(hw)` and `recommend(hw)`, the rules its
setup and app share (RAM + VRAM tiers less 5 GB, the low-RAM mode where the card holds part of the experts, the
context it pre-selects for the card) — run in Strata's own Python with RAM and VRAM lowered to the owner's limits;
its verdict per size is the option (`fits` → card + RAM, medium; low-RAM/`tight` → slow; "…from the SSD" → from
disk), its recommended size ranks first, and a context above its pick says what it costs. VRAM shown = the card less
Strata's 1536 MiB reserve (or the limit). *colibri*: for a checkpoint already in the catalogue (by its repo's name,
`Serve:Models` or `Serve:ModelsDir`), `coli plan --json --model <dir> --ctx N` (+ `--ram`/`--vram` limits,
`COLI_VULKAN` as the node launches it) — its tiers become the option (dense on the device + hot experts, capped at
the plan's VRAM budget; the RAM budget; cold experts on disk; the bottleneck and hit rate in `why`). For one not yet
converted the estimate follows what colibri does with a card: **Vulkan** — the dense chain on the device and a
hot-expert tier beside it; **CUDA** (a `coli_cuda.dll` beside the launcher) — a hot tier of the card less 2 GB; neither —
the CPU, and the option says how to change it. A planner that fails leaves the estimate's option with the reason first.
`Colibri:Serve:Vulkan` (bool?, null = the environment as before; settings key) sets `COLI_VULKAN` for the engines the
node launches; the store's colibri marker now carries the context too. *Rejected:* teaching our arithmetic each engine's
placement rules — both engines already compute them, version by version, and a copy would drift the first release after.

**Measured on the 3090 Ti box (EvoMesh stopped for it).** Ollama, estimate vs `/api/ps` (nvidia-smi delta): llama3.1 8B
@8K 6310 vs 5412 (5677); @64K 14052 vs 13036 (13304); mistral 7B @32K 9106 vs 8224 (8504); gpt-oss 20B @32K 15530 vs
12307 (13371 — half its layers are sliding-window, counted at full context). llama.cpp (winget b10424, Vulkan, `--fit
on`): its own `llama-fit-params` agreed in all three — llama3.1 8B @32K all on the card (estimate 9381, card +8400);
a 35B-A3B qwen35moe @64K all on the card at the edge (estimate 23283 of 23336, card +21603; hybrid 11 of 41 layers);
a 70B Q4_0 @4K split, 47 of 80 layers on the card (estimate 60 % on the card; card +22270). colibri 2.0 CPU build with
`COLI_VULKAN=1`, GLM-5.3-Flash @32K: dense chain 4.26 GiB (45 of 45 layers) + a 17.4 GiB expert tier, card 19 GB after
two answers; cold 533 s from the HDD, warm "Paris" in **8 s against 25 s on the CPU** (111). Its plan said 3.5 GiB dense + up
to 20.3 GiB hot — through the node the card reached 22.1 GB (free less its 1 GiB reserve), so an option from a plan counts
the card that way. Strata 0.2.3, Coder IQ1_M @32K through the node: Strata's own verdict "fits"; estimate 23028 MiB on the
card and 23270 in RAM, card +21834, Strata 26 GB working set; warm 48 tokens in 6 s.

**Found live: a catalogue "unload" stopped nothing for a model a request had loaded** — it only takes the model out of the
pins, and such a model was never pinned (GLM-5.3 kept 23 GB of the card after it). `/api/control/catalog/{engine}/{model}/unload`
now also calls the catalogue's own unload when the model is still loaded (live: Strata's card back to 938 MiB, its processes
gone). The hub's profile path is unchanged: a profile that leaves a request-loaded model out still lets it idle.

**Not established:** AMD and Apple GPUs (NVML only — such a box is estimated as the CPU, and the reply says so); Ollama
running every architecture llama.cpp does (Qwen3-Next was not tried in Ollama); KV quantization (`q8_0` halves the cache —
not counted); sliding-window layers (counted at the full context: gpt-oss over by ~2 GB); colibri's CUDA tier (the CUDA
build here has no GLM-5.3 engine); a context for a colibri model converted by hand (no marker to keep it in).

### Phase 119 (a catalogue: what is trending and popular on Hugging Face and in Ollama's library, ordered by what fits here)

Files: `src/InferHub.Shared/Fit/` (`Catalog` — `CatalogEntry`, `CatalogRough`; `OllamaLibraryPage`, `HuggingFaceListing`),
`src/InferHub.Node/Fit/CatalogService.cs`, `HuggingFaceClient.ListAsync`, `GET /api/control/fit/catalog` in
`LocalApi/LocalControlEndpoints.Fit.cs`. The app's dialog: `desktop/CLAUDE.md`.

**D1, load-bearing — the list is ordered by a rough class from the weights alone, says so, and leaves the arithmetic to
118.** `CatalogRough` classes weights against the usable VRAM and RAM `FitEstimator` itself uses (its `Vram`/`Ram` made
`internal`, not copied — one opinion): `card` (weights + 1 GiB on the cards), `card-and-ram` (split, or experts in RAM for
a MoE), `ram` (no card), `too-big`, `unknown`. The engine whose limit counts is the tighter of the two GGUF engines that
install here. Order: class, then the source's own rank, so the sources interleave. Every `why` ends "weights only — the
context is chosen in Check fit". Live, `qwen2.5:32b` is `card` here (18 GiB) and Check fit at 32K says `split`, which is
the design: the list never claims a context. *Rejected:* 118's estimate per entry — a GGUF header is a 16 MB Range read,
thirty entries half a gigabyte to open a list. *Rejected:* hiding `too-big` — "popular and not for this box" is an
answer; `fitsOnly=true` is the filter.

**D2 — Ollama's library is read from its web page, by hand; a page that changed shape is an error, not an empty list.**
No documented API (`ollama.com/api/tags` is Ollama's cloud models). `OllamaLibraryPage` reads `library?sort=popular|newest&q=`:
one `<a href="/library/<name>" class="group…">` per model, description, indigo capability badges (and a cyan `cloud`),
light-blue size badges (`7b`, `e2b`, `8x7b` → 56B and MoE), pulls (`43.7M`), the `title` date of "Updated". Cloud-only
(a `cloud` badge, no size) is dropped; `embedding` is not a text model; `vision` keeps only vision. Each size is classed
and the entry opens at the largest size of the best fitting class. Zero models from a page with a body (and no search) is
"changed shape" in `sources[].error`. The fixture is the real page of 2026-10-27, trimmed (`tests/InferHub.Tests.Common/OllamaLibraryFixture.cs`).

**D3 — Hugging Face: the listing API for the order, each repo's tree for the bytes.** `api/models?filter=gguf&pipeline_tag=
text-generation|image-text-to-text&sort=trendingScore|downloads|createdAt&expand[]=gguf…`. **Found live: the listing's
`gguf.total` can describe a side file** — AtomicChat's DeepSeek-V4.1-Flash (≈200 GB) listed 115M parameters, its LoRA's,
and came out "67 MiB, on the card". So each repo's tree is read (one JSON call, six at a time, kept ten minutes) and its
quants grouped by 118's `ModelFactsReader.GgufVariants` — the same grouping a pull uses — each classed by its real bytes;
the entry opens at a 4-bit K-quant of the best class, else that class's largest. **Not quants (`IsNotAModel`), each found
live listing a 200 GB model as "on the card":** a path part with `lora`, one with `imatrix`, a file starting `mtp` or
containing `draft` (a speculative-decoding head). A quant carrying its MTP layers (`IQ4_XS-MTP-00001-of-00003`) stays. A
tree that cannot be read falls back to the listing's parameter count, and the `why` says it can describe a side file.
A Strata repo is `unknown` — only Strata's table sizes it (99 D4). *Rejected:* `safetensors` listings for colibri —
mostly dense models no engine here loads; a pasted link still reaches 118.

**D4 — each source with the consent 118 uses, failing alone, kept ten minutes.** Hugging Face only under
`HuggingFace:Enabled`, Ollama's library only with an Ollama engine that pulls; otherwise `sources[]` says why and the
other source still answers (no 403 for the list). A source that fails is `ok:false` with the sentence. Answers (the
listing, the page, each tree) are cached per query for ten minutes. No configuration key: nothing is composed that
reaches the network until `/fit/catalog` is asked.

**D5 — nothing downloads from the list.** An entry is a link for 118's dialog, where the context is chosen and
`/fit/install` runs — so "nothing downloads without the owner's click" has one install path to keep honest.

**Measured live (3090 Ti, 256 GB):** both sources answered in ~1.6 s cold (listing + 25 trees); trending text, top 30:
Ollama's 8B–35B sizes and HF's 4B–33B repos on the card, the 122B/200B MoEs `card-and-ram`, a 263 GB IQ1_S `too-big`.

**Not established:** a trending list for Ollama (it has none — popular is used, and the note says so); whether the
page parser survives Ollama's next redesign (it fails loudly, not silently); repos that name quants in ways
`GgufVariants` does not (they show by file name, as in 118); a side file named like none of the four patterns.
