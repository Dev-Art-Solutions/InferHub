# InferHub v3.58.0 — a node drives colibri, and a refused stream stops reading as an empty answer

[colibri](https://github.com/JustVugg/colibri) (Apache-2.0) runs Mixture-of-Experts models that do
not fit in memory. It keeps the dense part in RAM and streams the routed experts from disk as the
router asks for them. A 7B OLMoE runs in 8 GB, and a 744B GLM-5.2 runs in ~25 GB of RAM from a
372 GB directory on an NVMe. No GPU needed. v3.58 makes it a node backend and ships an image in
which the node launches the engine itself.

Running a real engine through a real node found three bugs before this shipped. One of them was in
InferHub's OpenAI surface and affected every backend, not just colibri.

## What's new

### `Backend:Type=colibri` and `inferhub-node:colibri`

```bash
docker run -d --name inferhub-colibri \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false -e LocalApi__ApiKeys__0=your-key \
  -e Colibri__Serve__ModelId=olmoe \
  -v /nvme/olmoe:/models/colibri \
  -p 5081:8080 ghcr.io/dev-art-solutions/inferhub-node:colibri
```

- **The node launches `coli serve`** on 127.0.0.1 inside the container when `Colibri:Serve:Model` is
  set. If the engine exits, the node starts it again (after 2 s, doubling to 60 s). It never kills
  an engine for being slow: a 744B prefill at a few tokens a second looks exactly like a hang.
- **Or drive an engine you started yourself:** `Backend:Type=colibri`, with `Upstream:BaseUrl`
  defaulting to colibri's own `http://127.0.0.1:8000/v1`.
- **The image** is the plain node plus `python3`, `libgomp1` and colibri v1.12.1's CPU engine,
  pinned by sha256. ~410 MB, amd64 only (colibri publishes its Linux engine for x86_64 alone). No
  weights inside: convert a model with colibri's tools and mount it at `/models/colibri`.

What a `colibri` node does that `Backend:Type=openai` pointed at the same engine does not:

- **It declares `chat` only.** colibri has no embeddings endpoint. The hub refuses an embedding
  request before it reaches the node.
- **It watches `GET /health`** and reports the engine's health on the heartbeat, so the hub stops
  routing to a dead engine. It reports and never restarts.
- **It declares its KV slots as its `MaxConcurrency`** (unless you set `Node:MaxConcurrency`
  yourself), so the hub queues requests instead of the engine dropping them.
- **It can pin each conversation to a KV slot** (`Colibri:KvSlots` > 1), using a hash of the
  conversation's opening.
- **It does not pass `COLI_DEBUG` to the engine.** That variable makes colibri print prompts to
  stderr, and the node logs the engine's output. Every other `COLI_*` variable passes through.

### Bugs found by running it

1. **Every request was a 400.** The shared OpenAI client sends its JSON body chunked. colibri's
   gateway is Python's `http.server`, which needs a `Content-Length` and answered *"Request body must
   be between 1 and 4194304 bytes"* to every chat and every stream. vLLM, llama.cpp and the cloud
   vendors all accept chunked bodies, which is why no stub ever caught this. A colibri node now
   sends every body with its length. The shared client is unchanged.
2. **A refused stream looked like an empty answer, on every backend.** When a backend refuses a
   streamed request, the node sends `{"error": "…", "done": true}`. On the OpenAI surface
   (`/v1/chat/completions` and `/v1/completions`, on the hub and on a solo node), that was rendered
   as **HTTP 200 with an empty `finish_reason: "stop"`**. The client saw a successful, empty reply.
   Now, if nothing has been sent yet, the response is **502 with the backend's own message**, the same
   as the blocking path. If the refusal arrives after the first token, it is an `error` event, which
   the OpenAI SDKs raise. The Ollama surface already passed the error through and is unchanged. A
   node that *disconnects* mid-stream still ends the stream with `stop`, because there is no message
   to report.
3. **A launched engine left the node unroutable for a minute after every start.** The node registers
   with the hub while the engine is still loading. Its first model listing fails, so it reports
   nothing, correctly. The first failed health probe was below the outage threshold, so when the
   engine came up nothing triggered a new report, and the model only appeared on the next 60 s
   refresh. Now any healthy probe that follows a failed one triggers a report. Measured on the image:
   **routable 15 s after `docker run`**, down from about 60 s.

## How it is tested

`dotnet test InferHub.sln`: **1 766 passed, 61 skipped, 0 failed** (Shared 202, Coordinator 822/43
skipped, Node 266/17, Mesh 476/1). New tests: `ColibriBackendTests` (the type, the wire, slot
hashing, the watcher on stubs and on a real closed port, the launcher's command line and environment,
the composition), `SoloStreamRefusalTests`, four new cases in `OpenAiStreamingTests`, and three in
`BundledNodeTests` (engine pin and checksum, runtime packages, the publish-matrix entry).

**Against a real engine before tagging** (`inferhub-node:colibri` built locally, colibri v1.12.1,
OLMoE-1B-7B-0125-Instruct converted with colibri's own `convert_olmoe_merged.py` to its int8
container, 7.4 GB; AMD Threadripper PRO 5975WX, CPU only):

| Check | Result |
|---|---|
| Chat through a solo node, blocking | 200, *"The capital of Bulgaria is Sofia."*, 28 s on the first (cold) request |
| OpenAI stream through a solo node | 200, first token **0.27 s**, ~26 tokens/s |
| Chat through a real hub (meshed node) | 200, same answer. Hub status: `capabilities: [chat]`, `maxConcurrency: 1`, `backendHealth: healthy` |
| Embedding through the hub | refused at the hub, never sent to the node: **404** *"no node is advertising embedding model 'olmoe'"* |
| Embedding through a solo node | **501** naming the backend and `embed` |
| A streamed request colibri refuses (`frequency_penalty`), solo and hub | **502** *"Token penalties are not supported yet."* (before the fix: 200 + empty `stop`) |
| 4 parallel through the hub | the hub queued them, colibri never had more than 1 active and 0 queued, all 200 (5.1 / 7.4 / 9.1 / 11.0 s) |
| 12 parallel straight at colibri, no node | **7 answered, 5 connections dropped**, none got the documented 429 |
| SIGKILL the engine under the node | `exited (137)`, relaunched 2 s later, chat answered again |
| `COLI_DEBUG=2` on the container | one warning, the engine process has no `COLI_DEBUG`, prompt absent from the log |
| Prompt phrase in logs (node at Information, hub at Warning) | 0 matches on both |

## Checked on the published image

*(filled in after the tag builds)*

## Not established, said out loud

- **KV-slot pinning has not run on a real engine.** colibri v1.12.1 accepts more than one KV slot
  only for GLM-5.2/5.3 (every other engine refuses to start with `--kv-slots` > 1, and the node logs
  that message). GLM is 372 GB+ and was not run. The hashing and the wire field are tested against a
  stub. The default is one slot, and with one slot the node sends nothing extra.
- **We measured no prefix reuse at all on OLMoE.** With `COLI_KV_PREFIX=1` and its log on, every
  request said `[PREFIX] no reuse: held=0`. The same request sent twice in a row prefilled all
  3 356 tokens both times (~63 s each). Through the gateway, this engine keeps nothing between
  requests, so slots would make no difference to it. Whether GLM's engine reuses a prefix through
  the gateway is colibri's claim, not our measurement.
- **The watcher's declared outage and recovery against the real engine** was seen once, during a
  crash loop (`Unreachable after 3 consecutive failed probes`). The recovery path is unit-tested. A
  short relaunch (2 s) stays under the threshold by design and was never declared.
- **The CUDA, Metal and Vulkan tiers** are not in the image. The release archive is the CPU build.
- **Brio** (`POST /v1/brio`, colibri's closed-set scoring) is not exposed. It returns no Ollama-shaped
  output, so it needs its own API (40 D3). It is a separate phase.
- **The streamed-refusal fix on the hub** was measured with a colibri node. For other backends it
  is the same code path and the same chunk, tested, but not run against each one.
