# InferHub.Node/Backends — agent context

**Scope: `src/InferHub.Node/Backends/`.** The engines a node drives that are not "an Ollama and a
dialect": colibri and its Brio, and since phase 95 a node running several engines at once —
`Backend:Engines`, ollama + llama.cpp + colibri side by side, started and stopped by the hub.

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
