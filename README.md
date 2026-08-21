# InferHub

**A self-hosted, Ollama-compatible inference mesh.**
Run the gateway where you have no GPU. Run worker nodes where you do.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Built on OllamaClient](https://img.shields.io/badge/built%20on-OllamaClient-2b8a3e.svg)](https://github.com/Dev-Art-Solutions/OllamaClient)

---

## The problem

GPUs and servers rarely live in the same place. The machine that is always on — a small
VPS, a home server — usually has no GPU. The machines that *do* have a GPU — a desktop, a
gaming rig, a workstation — are often behind a home router and not reachable from outside.

InferHub closes that gap. A lightweight **coordinator** runs on the always-on, GPU-less
host and speaks a familiar Ollama-style API. The actual work runs on **nodes** that sit on
your GPU machines, reach *out* to the coordinator, and pull jobs down. No port forwarding,
no exposing your desktop to the internet.

In short: one stable address in front, a pool of GPUs behind it.

## How it works

```
                                  ┌──────────────────────────────┐
   client (Ollama-compatible)     │          Coordinator         │
   curl / app / IDE plugin  ─────▶│  Ollama-style HTTP API        │
        (Bearer token if remote)  │  /api/tags /api/generate      │
                                  │  /api/chat                    │
                                  │                               │
                                  │  SignalR node hub  ◀──────────┼──── persistent outbound
                                  └───────────────┬───────────────┘     connection from nodes
                                                  │
                         dispatch (model-aware)   │   stream tokens back
                          ┌───────────────────────┼───────────────────────┐
                          ▼                       ▼                       ▼
                   ┌────────────┐          ┌────────────┐          ┌────────────┐
                   │   Node A   │          │   Node B   │          │   Node C   │
                   │  + Ollama  │          │  + Ollama  │          │  + Ollama  │
                   │  (GPU)     │          │  (GPU)     │          │  (GPU)     │
                   └────────────┘          └────────────┘          └────────────┘
```

- **Coordinator** — an ASP.NET Core service. It exposes an Ollama-compatible HTTP API,
  authenticates remote callers with a Bearer token, keeps track of which nodes are online
  and which models each one holds, and routes every request to a capable node.
- **Node** — a small .NET worker. It opens a persistent outbound connection to the
  coordinator (so it works fine behind NAT), reports which models it can serve, then
  receives prompts, runs them against its local inference backend, and streams the result
  back.
- **Pluggable backends** — each node runs an LLM backend behind a small abstraction
  (`IInferenceBackend`). The first backend is **Ollama**, driven by our own
  [OllamaClient](https://github.com/Dev-Art-Solutions/OllamaClient) library. The mesh
  isn't tied to it — other backends (vLLM, llama.cpp, OpenAI-compatible servers) can slot
  in later without touching the coordinator.

Because the API is Ollama-shaped, existing Ollama clients, scripts, and editor plugins can
point at the coordinator and keep working — they just reach a whole pool of GPUs instead
of one.

## Status

**InferHub 3.15** makes a two-minute image behave like a two-minute job. v3.14 put Stable Diffusion
on the fleet behind OpenAI's Images API; the thing it did not have was any way to watch one. Now
`POST /api/images/jobs` returns an id and a place in line, `GET …/events` streams
`queued → running(step 7/28) → succeeded` over SSE, `GET …/content/0` collects the bytes **once**,
and `DELETE` changes your mind — cooperatively, so the worker keeps its weights and the next caller
does not pay for your cancel. Results live for five minutes and, unless you turn on
[v3.24's durability](#a-job-can-survive-a-restart-if-you-say-so-v324), **nothing touches disk**:
no temp file, no cache directory, no URL. `/v1/images/generations` is unchanged for anyone who never
reads this paragraph — internally it became "submit a job and wait for it", so both surfaces queue in
the same line and are metered by the same code. **Zero new dependencies, no new model, and a
deployment that changes no config behaves exactly as it did on 3.14.**

| Phase | Theme | Version |
|------:|-------|---------|
| 1 | Foundation & coordinator skeleton (done) | `v0.1.0` |
| 2 | Node ↔ coordinator link (done) | `v0.2.0` |
| 3 | Model discovery (pluggable backend) (done) | `v0.3.0` |
| 4 | Routing & blocking generation (done) | `v0.4.0` |
| 5 | End-to-end streaming (done) | `v0.5.0` |
| 6 | Authentication & security (done) | `v0.6.0` |
| 7 | Conversations & smart routing (done) | `v0.7.0` |
| 8 | Resilience, observability & 1.0 (done) | `v1.0.0` |
| 9 | Typed, validated node configuration (done) | `v1.1.0` |
| 10 | Coordinator admin API (done) | `v1.2.0` |
| 11 | Management console UI (done) | `v1.3.0` |
| 12 | Live updates & console hardening (done) | `v1.4.0` |
| 13 | Vector store foundation (done) | `v1.5.0` |
| 14 | Embeddings & retrieval (done) | `v1.6.0` |
| 15 | Replication across nodes (done) | `v1.7.0` |
| 16 | Durability & self-healing (done) | `v1.8.0` |
| 17 | Console & observability (done) | `v1.9.0` |
| 18 | Retrieval-augmented inference (done) | `v2.0.0` |
| 19 | Windows-service deployment (done) | `v2.1.0` |
| 20 | PostgreSQL + pgvector connector (done) | `v2.2.0` |
| 21 | OpenAI-compatible API & Docker distribution (done) | `v2.3.0` |
| 22 | OpenAI-compatible node backend & cloud burst (done) | `v2.4.0` |
| 23 | Document ingestion pipeline (done) | `v2.5.0` |
| 24 | Hybrid search, reranking & eval harness (done) | `v2.6.0` |
| 25 | Clients, quotas & usage accounting (done) | `v2.7.0` |
| 26 | Fleet operations — model management & measured routing (done) | `v2.8.0` |
| 27 | Streaming `tool_calls` deltas (done) | `v2.9.0` |
| 28 | Observability export — Prometheus `/metrics` (done) | `v2.10.0` |
| 29 | Multimodal (vision) passthrough (done) | `v2.11.0` |
| 30 | Stable-node affinity + optional persistence (done) | `v2.12.0` |
| 31 | Client-scoped collections (RAG multi-tenancy) (done) | `v2.13.0` |
| 32 | Multi-coordinator — standby hub & warm failover (done) | `v3.0.0` |
| 33 | Qdrant vector connector (done) | `v3.1.0` |
| 34 | Qdrant-native hybrid search (done) | `v3.2.0` |
| 35 | Qdrant production knobs + cross-provider migration (done) | `v3.3.0` |
| 36 | Node supervises its own Ollama (done) | `v3.4.0` |
| 37 | Solo mode — the node serves its own API (done) | `v3.5.0` |
| 38 | RAG in solo mode — a standalone node retrieves for itself (done) | `v3.6.0` |
| 39 | Bundled node image — Ollama in the container, GPU or CPU (done) | `v3.7.0` |
| 40 | Capabilities — a node declares what it can *do* (done) | `v3.8.0` |
| 41 | Tool runtime — supervised subprocess workers (done) | `v3.9.0` |
| 42 | Speech — STT and TTS behind the OpenAI audio API (done) | `v3.10.0` |
| 43 | Node profiles — the hub configures, the node clamps (done) | `v3.11.0` |
| 44 | Hub-assigned retrieval — a corpus on every node (done) | `v3.12.0` |
| 45 | The console, the metrics and the docs for the whole track (done) | `v3.13.0` |
| 46 | Text to image — Stable Diffusion on the fleet (done) | `v3.14.0` |
| 47 | Work measured in minutes — jobs, progress, cancel (done) | `v3.15.0` |

**What's next.** The Qdrant track is finished: a connector (v3.1), server-side hybrid fusion (v3.2),
and production knobs plus a migration tool (v3.3) — all three at zero new dependencies. v3.4 through
v3.7 turned to the node — supervising its own backend, serving the hub's API, retrieving for itself,
and shipping as [one container](#a-node-and-its-ollama-in-one-container-v37) that carries its own
Ollama and uses your card, your CPU, or neither. v3.8 started the current track: routing asks *what a
node can do*, not just *what it holds* — the seam a node needs before it can run something other than
a language model. v3.9 is that something, a [tool runtime](#tools-on-a-node-v39) that lets a node
drive **supervised child processes** (Python, in practice) with two consents, a restart budget and no
new dependencies; v3.10 is the first real tool, [Whisper and Piper](#speech-in-speech-out-v310)
behind the OpenAI audio API on a `:tools` image; and v3.11 turns the same track outward, letting the
hub [configure the fleet](#configure-the-fleet-not-the-boxes-v311) within what each operator already
allowed, and v3.12 uses that seam for the thing it was shaped for: [a corpus on every
node](#a-corpus-on-every-node-v312), assigned from one place, with ownership recorded so the hub
cannot overwrite what it granted. **v3.13 closes the track**: the
[console](#driving-all-of-it-from-one-page-v313), the Prometheus series and the docs pass that make
six releases' worth of capabilities, tools, audio, profiles and per-node corpora operable by someone
who did not write them. **v3.14 opened the image track** — [text to image](#text-to-image-v314) on
OpenAI's Images API, a fifth `:diffusion` image, and the capability seam carrying a whole new
modality with no protocol change at all — and **v3.15 gave it a clock**: [async
jobs](#a-job-that-takes-two-minutes-v315) with per-step progress, cooperative cancellation and
results that live for five minutes and are read once. **v3.16 makes it a
[catalogue](#the-catalogue-v316)**: six models at the time, quantization that fits a 20B transformer
and its 8.3B text encoder on one consumer card, a **VRAM budget you declare rather than one we
guess**, hub-driven weight pulls, and a licence consent per model — because two of them are not ours
to accept for you. **v3.17 ships the model the track was asked for**:
[360° panoramas](#360-panoramas-v317) from a rank-128 LoRA over that 20B base, with the projection
*declared* rather than guessed from an aspect ratio, a measured seam, and a hand-written WebGL
viewer. **v3.18 lets you change a picture** rather than only make one:
[inpainting, img2img and variations](#editing-a-picture-v318) as their own capability kind, with the
mask convention converted where a pixel may actually be read and a strength knob that bills the steps
it ran. **v3.19 closes the track** the way v3.13 closed the last one:
[one page](#one-box-one-card-a-picture-a-panorama-an-edit-v319) that shows what each node can
generate, what it is generating *right now and at which step*, what it refused and *why*, and a
cancel button — plus the series to alert on and a walkthrough somebody can follow top to bottom.

Six releases, a whole new modality, and still **zero new dependencies**: PyTorch is a child process,
not a package.

**v3.23 lets you close that seam** — [and only if you ask](#360-panoramas-v317). We measured the flaw
for six releases and refused to fix it, because a repair nobody asked for is a second pass they did
not watch and would be billed for. What changed is the asking: an operator permits a mechanism, a
request chooses one, and the cheap one costs no steps at all.

**v3.24 lets a job survive the hub that made it** — [if you say so](#a-job-can-survive-a-restart-if-you-say-so-v324).
A restart used to turn a job id from thirty seconds ago into a `404` byte-identical to one that never
existed. Writing the job down is a file write; the release is the list of things durability may *not*
do — extend retention, survive a read, resume your job (nothing durable holds a prompt), or go in the
database.

**v3.25 makes the fleet render video** — [`POST /v1/videos`](#text-to-video-v325), and it is
OpenAI's own API rather than one we invented. Phase 47 built InferHub's async image surface because
OpenAI has no asynchronous *Images* API to adopt; it has an asynchronous *Videos* API, so this
release adopts it and adds nothing. One model — `wan-t2v-1.3b`, Apache-2.0, 480p, 2–5 seconds — over
**the same queue, the same cancel, the same read-once retention and the same optional durability**
v3.15 and v3.24 already built. Still zero new `PackageReference`s: the encoder is a static ffmpeg
binary inside a Python wheel, reached through the same child process as everything else.

**v3.26 gives that surface a catalogue** — [three models instead of one](#three-models-since-v326-and-four-things-worth-knowing-about-all-of-them).
`wan-t2v-14b-720p` at 720p and `cogvideox-2b` at **8 fps** are what make the fields a catalogue of one
could not test: `fps` is now required and its old fallback of 16 is gone, because encoding
CogVideoX's 49 frames at twice their rate is not an error — it is a clip that plays at double speed.
The 14B entry is also the first recipe this project ships that **does not fit a 24 GB card**, which
is the VRAM gate working: such a node never declares it, so nobody meets the ceiling mid-render.
**v3.30 adds OpenRouter, and it cost no new dialect — which is the point.**
[`Type: "openrouter"`](#openrouter-v330) speaks the same OpenAI wire format the seam already spoke,
so what the type actually buys is the part that was never the dialect: a base URL you need not type,
a model map checked at boot against OpenRouter's `vendor/model` id shape, and attribution headers
that are **absent unless you set them**, because they list your deployment on a vendor's public
rankings and that is not a thing this hub volunteers on your behalf. Two bugs fell out of reading
their docs, and both were live for *every* OpenAI-compatible upstream: an `error.code` that is a
number instead of a string made every failure arrive as a wall of raw JSON, and an error that
happened mid-stream ended the response quietly — 200, and it looked finished.

**v3.29 gives the cloud upstream a name — and lets there be more than one of them.**
[Named providers](#named-providers-v329) turns the single anonymous `Fallback:` upstream into a map:
OpenAI with one key, OpenRouter with another, a vLLM box on your own network with none, each with its
own models and its own trigger. The consent model is untouched — a model absent from every `ModelMap`
is still never sent anywhere — and **one model may be claimed by exactly one enabled provider**, with
a second claim failing startup by name, because which vendor receives a prompt is not something this
hub will decide by configuration ordering. A deployment that keeps its `Fallback:` block behaves
byte-identically, header included. This is the first of eight releases that end with Anthropic and
Gemini speaking their own dialects and a request being *routed* to a provider rather than falling
into one; zero new `PackageReference`s, and there will be none in the other seven either.

Still on the table beyond that: teaching the **coordinator** about backend health as a typed signal
(a status column and an alert, rather than a line in the node's log), **active-active**
multi-coordinator load sharing, an **OTLP push** exporter behind an explicit opt-in, and a dedicated
cross-encoder reranker behind the existing `IReranker` seam. A fourth vector backend (Milvus,
Weaviate) is the same shape as the third and will ship on real demand rather than for the comparison
matrix.

## Quick start

### Docker (recommended)

```bash
cp deploy/docker/.env.example deploy/docker/.env    # set three keys
docker compose -f deploy/docker/docker-compose.yml up -d
```

> **In Docker there is no loopback exemption.** Requests from your host arrive over the
> bridge network, not loopback, so the API keys are mandatory — unlike the from-source path
> below. See [deploy/docker/README.md](deploy/docker/README.md).

Images are published to GHCR for `linux/amd64` and `linux/arm64`:
`ghcr.io/dev-art-solutions/inferhub-coordinator` and `.../inferhub-node`.

#### Which image do I pull? (v3.13)

Four artifacts with no chooser is how somebody pulls 6 GB to run a 340 MB workload, or pulls the
small one and wonders where the audio went. All tags are under
`ghcr.io/dev-art-solutions/`.

| Pull this | Size | Arch | When it is the right one |
|---|---:|---|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | The always-on host. No GPU, no inference engine — it routes. |
| `inferhub-node` | ~340 MB | amd64 + arm64 | You already run Ollama, vLLM, LM Studio or a hosted OpenAI-compatible endpoint and want a node in front of it. Also the right one for **solo mode** and for a **vector-store-only** box. |
| `inferhub-node:ollama` | ~4 GB | amd64 | You want *one* `docker run` on a GPU box with nothing installed on the host. Ollama runs inside the container, supervised. `:gpu` is an alias of the same digest — it works fine with no card. |
| `inferhub-node:tools` | ~6 GB | amd64 | The above, **plus speech**: Python, `faster-whisper` and `piper`, so `/v1/audio/transcriptions` and `/v1/audio/speech` work out of the box. |
| `inferhub-node:diffusion` | ~12 GB | amd64 | **Text to image** (v3.14+) and **editing** (v3.18+): PyTorch, `diffusers`, `bitsandbytes` and seven recipes — SDXL, SD 1.5, FLUX.1-schnell, Qwen-Image, SD 3.5 Medium, SDXL-Turbo and [`qwen-360`](#360-panoramas-v317) — so `/v1/images/generations`, [`/edits` and `/variations`](#editing-a-picture-v318) work out of the box. **You need a card.** |

Three rules of thumb that save the mistake each way:

- **Do not pull `:tools` for chat.** It is `:ollama` plus ~1.5 GB of Python wheels you will never
  load. Every image above it does chat identically.
- **Do not pull `:ollama` to point at an Ollama you already have.** The bundled one would sit idle
  next to it — or worse, fight it for a port. Use the plain image and set `Ollama:Endpoint`.
- **`:diffusion` is the one image that does not stack**, and that is deliberate: it has no Ollama,
  no Whisper and no Piper in it. A card running a diffusion pipeline has no room for a chat model
  beside it, so bundling one would ship a combination we would then have to tell you not to use.
  Want both? Run two containers and let the coordinator route `image` to one and `chat` to the
  other — that is what capability routing is for.

Whichever node image you choose, **mount a volume at `/data`**. Model weights, the node's stable id,
tool scratch and any corpus live there; without it every `docker run` re-downloads gigabytes.

### From source

```bash
# On the always-on host (no GPU needed)
dotnet run --project src/InferHub.Coordinator

# On each GPU machine (with Ollama already running locally)
dotnet run --project src/InferHub.Node

# From anywhere, talk to it like Ollama (remote calls need a Bearer token)
curl http://your-coordinator:5080/api/chat \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -d '{"model":"llama3","messages":[{"role":"user","content":"Hello!"}],"stream":false}'
```

### Just a node — solo mode (v3.5+)

Both of the above run two processes so that one client can reach one backend. On a single
machine that is all ceremony and no routing, so since v3.5 the node can serve the same API
itself — no coordinator, no enrollment secret, no internet.

```bash
dotnet run --project src/InferHub.Node \
  --LocalApi:Enabled=true --Coordinator:Enabled=false
```

```python
# before — the fleet
client = OpenAI(base_url="http://hub.example:5080/v1", api_key=KEY)
# after — just the node
client = OpenAI(base_url="http://localhost:5081/v1", api_key=KEY)
```

That is the whole migration. Same request bodies, same responses, same streaming, same errors,
both dialects. Full details, and what solo mode deliberately does *not* do, are in
[Solo mode](#solo-mode--just-the-node-v35).

### One container, with or without a GPU (v3.7+)

The same thing again with nothing installed on the host — the bundled image carries Ollama
inside it:

```bash
docker run -d --name inferhub --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$INFERHUB_API_KEY" \
  -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:ollama

docker exec inferhub ollama pull llama3.2
```

Drop `--gpus all` and it runs on the CPU instead. See
[A node and its Ollama in one container](#a-node-and-its-ollama-in-one-container-v37).

## Solo mode — just the node (v3.5+)

InferHub had exactly one shape until v3.5: a coordinator somewhere always-on, and nodes on GPU
machines dialling out to it. That shape is right when you have a fleet and it is overhead when
you have one machine — and one machine is how most people start.

```jsonc
// src/InferHub.Node/appsettings.json
{
  "Coordinator": { "Enabled": false },
  "LocalApi": {
    "Enabled": true,
    "Urls": "http://localhost:5081"
  }
}
```

A solo node is the hub's formatting layer sitting directly on the node's executor, with the
routing layer removed — the hub translates a request, routes it, queues it, dispatches it over
SignalR and formats what comes back; solo does the first and last and skips the middle, because
there is nothing to route to. Both ends were already shared code, which is why tool calls,
vision and both dialects work here without being reimplemented.

### What it serves

| Route | Solo | Notes |
|---|:--:|---|
| `POST /api/generate`, `/api/chat` | ✅ | Blocking and streaming (NDJSON). |
| `POST /api/embed`, `/api/embeddings` | ✅ | |
| `GET /api/tags`, `/api/version` | ✅ | `/api/tags` honours `Node:Models:Include/Exclude`, the same filter the node reports to a hub. |
| `POST /v1/chat/completions`, `/v1/completions` | ✅ | Blocking and streaming (SSE). |
| `POST /v1/embeddings`, `GET /v1/models` | ✅ | |
| `GET /health` | ✅ | Open and unauthenticated, like the hub's. Reports `mode: "solo"`. |
| `GET /api/status` | ⚠️ | A **smaller, different** document with a `mode` discriminator — one node, its models, its in-flight count. No fleet metrics. |
| `X-InferHub-Retrieve` header | ⚙️ | **501** unless you turn on [retrieval](#retrieval-on-a-standalone-node-v36) (v3.6+). |
| `/api/collections/*`, `/api/vector/*` | ⚙️ | 404 unless retrieval is on (v3.6+). |
| `/api/admin/*`, `/metrics`, `/console` | ❌ | 404 — all need a fleet. |

`/api/status` deliberately does not return the hub's document with zeros in the fleet fields: a
dashboard reading `nodesEvicted: 0` from a process that has no concept of nodes is worse than
one that gets a 404 for a key that was never there.

### Retrieval on a standalone node (v3.6+)

A machine with a folder of documents and one GPU is the deployment that most wants RAG, so since
v3.6 a standalone node ingests, indexes and grounds its own answers — the same headers, the same
augmented prompt and the same `X-InferHub-Sources` citations as a coordinator, because it is the
same code rather than a second copy of it.

```jsonc
"Coordinator": { "Enabled": false },
"LocalApi": {
  "Enabled": true,
  "Retrieval": { "Enabled": true, "DefaultEmbeddingModel": "nomic-embed-text" }
}
```

```bash
# ingest
curl localhost:5081/api/collections/handbook/documents \
  -H 'content-type: application/json' \
  -d '{"id":"leave-policy","text":"Employees accrue 25 days of annual leave each year."}'

# see what retrieves, before you trust it
curl localhost:5081/api/collections/handbook/search \
  -H 'content-type: application/json' -d '{"query":"how much annual leave?"}'

# ground an answer
curl localhost:5081/api/chat -H 'X-InferHub-Retrieve: handbook' \
  -H 'content-type: application/json' \
  -d '{"model":"llama3","messages":[{"role":"user","content":"how much annual leave?"}],"stream":false}'
```

That adds `/api/collections/{c}/documents` (ingest, list, chunks, delete), the
`/api/collections/{c}/search` playground, the `/api/vector/{c}` data plane, and
`X-InferHub-Retrieve` on both dialects. Vector, keyword and hybrid modes all work, as does the
optional LLM reranker.

**It requires `Coordinator:Enabled=false`, and says so by refusing to start otherwise.** A node in
a mesh already holds vector replicas *derived* from its coordinator; giving that same process an
authoritative corpus of its own would put two sources of truth under one collection name — a
locally ingested document invisible to the fleet, and a hub that will happily overwrite it. There
is no safe configuration of that, so it is not offered, and it fails loudly rather than quietly
switching grounding off.

**No PDF.** The PDF text extractor ships with the coordinator only, so a PDF upload gets a clean
**415** telling you to convert the file or use a hub — never a silently bad extraction that fills
your corpus with plausible nonsense. **No external vector databases** either: the local store is
the only provider on a node. The corpus lives in `LocalApi:Retrieval:DataDirectory` (in Docker,
`/data/retrieval` — mount a volume or it is ephemeral).

### Two more things it deliberately will not do

**No admin API and no console.** There is one node and you are sitting at it. Model management
stays hub-driven; in solo mode it is `ollama pull` in the terminal you already have open.

**It is not a second kind of coordinator.** A solo node serves its own clients and nothing else.
Nothing about the fleet changes: the coordinator gained no code, meshed nodes behave exactly as
before, and the outbound-only rule that lets a GPU box sit behind NAT with no inbound rule is
untouched — solo mode adds a surface for *your own* clients, which is why it needs no hub at all.

### Auth, and the one place it refuses to boot

Off by default, and loopback when on. Loopback callers need no key (matching the hub's
`Auth:RequireAuthForLoopback`), so local `curl` just works.

**A non-loopback address with no keys fails startup, naming the key.** That is stricter than
InferHub usually is about somebody else's network, and the asymmetry is intentional: an
unauthenticated inference endpoint hands arbitrary compute on your GPU to anyone who can reach
the port, and the first sign of it is a bill or a melted card. `LocalApi:AllowAnonymous=true` is
the explicit override for a trusted network, and it warns on every boot.

This bites in a container, by design — the image binds a wildcard, so a containerised solo node
needs a key:

```bash
docker run -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
           -e LocalApi__ApiKeys__0=your-key \
           -e Ollama__Endpoint=http://host.docker.internal:11434/ \
           -p 5081:8080 ghcr.io/dev-art-solutions/inferhub-node
```

### Concurrency

`Node:MaxConcurrency` has always been *advisory* — a number the coordinator's router respects.
In solo mode nobody is respecting it, so it is **enforced locally**: over the cap a request waits
up to `LocalApi:MaxWaitSeconds` and then gets `503` + `Retry-After`, the same status and header
as the hub's queue, so existing client retry logic behaves identically. Unset means unbounded,
exactly as it does today.

### Both at once

`Coordinator:Enabled` and `LocalApi:Enabled` are independent. A fleet node with the local API on
is legitimate — useful for curling a node directly while debugging. Both **off** is a startup
failure naming both keys: a node that neither joins a mesh nor serves anyone is a typo.

## A node and its Ollama in one container (v3.7+)

Solo mode removed the coordinator. This removes the other half: the Ollama you had to install on
the host, reach through `host.docker.internal` or `172.17.0.1`, and keep alive yourself.

```bash
docker run -d --name inferhub --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$INFERHUB_API_KEY" \
  -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:ollama

docker exec inferhub ollama pull llama3.2
```

Then point any OpenAI client at `http://localhost:5081/v1`.

### Three modes, one image

**None of them refuses to start**, and the node says in its first log lines which one it is in.

| | How | What runs |
|---|---|---|
| **GPU inference** (+ RAG) | `--gpus all` | node + Ollama on CUDA |
| **CPU inference** (+ RAG) | leave `--gpus` off | node + Ollama on the CPU — right for embedding models and small ones |
| **Vector store only** | `-e Ollama__Supervisor__Enabled=false` | node + corpus, **no Ollama process at all** |

The third is worth explaining: `/api/vector/{collection}` takes **client-supplied vectors**, so a
node with no inference process is still a complete vector store. It reports zero models, which is
the honest answer — a chat request fails cleanly instead of hanging. Ingesting *documents* does
need an embedder, so in that mode either bring your own vectors or point `Backend:Type=openai` at
an embedding upstream elsewhere. And it is still the 4 GB image: if you only ever want this, the
plain `inferhub-node` image does solo retrieval identically at 340 MB.

### Did it get the card?

The node answers that in its first log lines, both ways:

```
info: CUDA: 1 device(s) visible to this process — NVIDIA GeForce RTX 3090 Ti.
info: CUDA: no devices visible to this process; inference will run on the CPU.
      In a container, pass '--gpus all' to use a card.
```

and on `/api/status` as a `gpu` block. For which device a *loaded model* actually ended up on,
`docker exec inferhub ollama ps` is the ground truth — that is Ollama's own accounting and it is
not something InferHub second-guesses.

`Ollama:RequireGpu=true` turns a missing card into a **startup failure** naming `--gpus all`. It
is off by default, including in this image, because CPU is a supported mode rather than a
misconfiguration. Turn it on for a node whose whole purpose is the card, so a `--gpus` flag
falling out of a unit file is not a silent fiftyfold slowdown.

### What is in it, and what is not

- **~4 GB** (the plain `inferhub-node` image is ~340 MB and does not change). **amd64 only**, and
  **NVIDIA only** — no ROCm, no Intel, no Apple, and arm64 would mean Jetson-specific bundles.
- **No model is baked in and none is pulled at boot.** `docker exec … ollama pull` is the
  interface. **Mount a volume at `/data`** or every `docker run` re-downloads them.
- **Ollama's own port is not published.** The container's surface is InferHub's API, which
  requires a key. `-e OLLAMA_HOST=0.0.0.0 -p 11434:11434` is yours to decide.
- **Every `OLLAMA_*` variable passes through** — `OLLAMA_KEEP_ALIVE`, `OLLAMA_NUM_PARALLEL`,
  `OLLAMA_FLASH_ATTENTION` and the rest are one `-e` away.
- **The node keeps its Ollama alive**, using the [supervisor](#keeping-the-local-ollama-alive-v34):
  it starts it at boot, probes it, and restarts it if it dies or wedges. That matters more in a
  container than on a host, because nothing else in there is watching.
- On **Windows with Docker Desktop** this works through WSL2 — note that `/dev/nvidia*` does not
  exist there, so InferHub detects the GPU by loading the driver rather than by looking for device
  nodes.

It is also a good **mesh** node: set `Coordinator__Url` instead of `Coordinator__Enabled=false`,
and the hub can pull models into it from the console, because the Ollama it manages is genuinely
its own.

```bash
docker compose -f deploy/docker/compose.ollama.yml up -d
```

## What a node is for (v3.8+)

Until v3.8 a node advertised **a list of model names**, and the coordinator routed on that alone.
That is a routing key with one dimension, and it quietly assumes every model on a node does the same
kind of work. It does not: a box holding only `nomic-embed-text` was a perfectly good candidate for
a chat request naming that model, and the error arrived from the backend, after a dispatch.

Since v3.8 a node also declares **capabilities** — what it can *do* — and the unit of routing is the
pair `(capability, model)`:

```bash
curl -s localhost:5080/api/status | jq '.capabilities, .nodes[] | {name, capabilities}'
# { "capability": "chat",  "nodes": 2, "models": ["llama3.2", "qwen2.5"] }
# { "capability": "embed", "nodes": 3, "models": ["llama3.2", "nomic-embed-text", "qwen2.5"] }

curl -s localhost:5080/v1/models | jq '.data[] | {id, capabilities}'
# { "id": "nomic-embed-text", "capabilities": ["embed"] }
```

**Nothing is guessed.** Ollama does not say what a model is for, and inferring it from the name
would be a lookup table that is wrong for somebody. A node declares `chat` + `embed` over everything
its backend reports, and the one thing it cannot work out for itself — that this box is *for*
embeddings — is one line of config:

```jsonc
// src/InferHub.Node/appsettings.json, or Node__Capabilities__Disabled__0=chat
"Node": { "Capabilities": { "Disabled": ["chat"] } }
```

That node then never receives a chat job. A client that asks for one anyway gets a **`503` with
`Retry-After`** naming the capability — not a `404`, because the model is there and this is a fact
about the fleet right now, the same category as every node being busy. A model that genuinely is not
on the fleet is still the `404` it has always been.

In **solo mode** the same key is enforced by the node itself, with the same status and the same
header, because one key must not mean two different things depending on how the node is deployed.
(Its own corpus is exempt: solo RAG still embeds with `embed` disabled, since the node's own
documents are not somebody sending it work.)

**A v3.7 node against a v3.8 coordinator is routed exactly as before** — no declaration means chat
and embed over everything, which is precisely the old behaviour, so a fleet can be upgraded one box
at a time.

This is the seam the next few releases need: a node that can run a speech model, or anything else
that is not a language model, has to be able to say so before anything can be routed to it.

## Tools on a node (v3.9+)

A **tool worker** is a child process a node starts, supervises, talks to over a line protocol, and
restarts when it dies. It is how a node does work its inference backend cannot — transcription,
speech, OCR, whatever you write — and it is off by default.

```jsonc
// the manifest, in Tools:ManifestDirectory
{
  "id": "whisper",
  "capabilities": [ { "kind": "transcribe", "models": ["whisper-small"] } ],
  "command": ["/opt/inferhub/venv/bin/python", "-u", "/opt/inferhub/tools/whisper_worker.py"],
  "env": { "HF_HOME": "/data/tools/hf" },
  "maxWorkers": 1
}
```

```jsonc
// and the two consents on the node
"Tools": { "Enabled": true, "Allowed": [ "whisper" ] }
```

The node then declares `transcribe` as a capability, the coordinator routes
`(transcribe, whisper-small)` to it, and it is reachable the same way on a hub and on a solo node:

```bash
curl localhost:5080/api/tools/transcribe -H "Authorization: Bearer $KEY" \
  -F model=whisper-small -F file=@meeting.m4a
```

**`python/README.md` is the worker author's document** — the protocol, the manifest reference, and
what the node does when a worker misbehaves. `python/inferhub_worker/` is a ~150-line reference
implementation you copy or vendor; it is deliberately not a package.

### Why a subprocess and not a library

Because the libraries are Python, and the alternative — Python.NET, CSnakes, an embedded interpreter
— is a **native binding**: it pins the node to a CPython ABI, and **one bad `import` takes the node
down**, because a segfault in a native extension loaded into this process is not an exception you
catch, it is a process that vanishes mid-stream taking every in-flight inference job with it. A child
process that segfaults is a log line and a restart. It also means a tool that is a Go binary, an
`ffmpeg` invocation or a vendor's CLI works here for free.

InferHub added **zero** dependencies for this. The node knows how to start a process, write a line,
read a line and kill it. It does not know what Python is.

### Opt in twice

`Tools:Enabled` consents to the feature. `Tools:Allowed` names the manifest ids that may actually
run — a manifest on disk that is not in the list is loaded, logged and never started. The two are
not redundant: since v3.11 a coordinator can turn a node's tools and capabilities off through a
[node profile](#configure-the-fleet-not-the-boxes-v311), and **`Tools:Allowed` is the ceiling it can
never raise.** A single switch would make "the operator enabled tools" and "the hub may run any tool
present on this box" the same consent.

### This is not a sandbox

Said plainly, because the alternative is implying safety by listing mitigations.

A worker runs **as the node's user, with the node's filesystem and the node's network.** The node
drops its own environment before spawning — a worker gets `PATH`, `HOME`, `LANG`, `LC_ALL`, `TMPDIR`,
`USER`, `SHELL` and whatever the manifest's `env` names, and never
`Coordinator__EnrollmentSecret` or `LocalApi__ApiKeys__0` — and that is the honest extent of the
isolation. **A tool you did not write and did not read has your box.**

If you want real isolation, run the tool in its own container and point a manifest at it: a
"process" that is `docker exec` is still a process, and the protocol does not care.

### What happens when a tool misbehaves

| | |
|---|---|
| Never finishes starting | Killed at `startTimeoutSeconds`, counted against the restart budget |
| Overruns `requestTimeoutSeconds` | Killed; the **job** fails; the node keeps serving inference |
| Dies mid-request | Clean error; the next request starts a fresh worker |
| Fails to start 3× in 10 minutes | The pool gives up, logs once at Error, **withdraws its capabilities from the node's registration** so the coordinator stops routing that work here — and keeps probing every minute, so a fix is noticed without a restart |
| Every worker busy | Waits `Tools:QueueMaxWaitSeconds`, then **503 + `Retry-After`** — the same shape as every other saturation refusal here |

A tool failure is a failed **job**, never a failed node. Attachments are capped at 25 MB
(`Tools:MaxAttachmentBytes`, a 413 at the edge), written to a per-request scratch directory that is
deleted in a `finally` — after success and after failure — and never logged.

### Uploads larger than 25 MB (v3.21)

Set **`Tools:MaxStreamedBytes`** on the coordinator *and* on a node, and an upload past
`Tools:MaxAttachmentBytes` **streams through the hub** instead of being buffered in it: the node
pulls the bytes over the connection it already opened, 64 KB at a time, straight into the scratch
file the worker reads. The hub never holds the body, and raising the key moves Kestrel's own
30 MB request-body ceiling with it — so the 413 you get always names a key you can raise.

It is **off by default (0)**, and with it off a deployment behaves exactly as v3.20 did. Three
things are worth knowing before you turn it on:

- **A streamed job is not retried on another node.** The body has been read and a client's socket
  cannot be rewound, so a node lost mid-upload is a `502` naming `node_lost`, and the caller decides.
  This is a real step down in reliability on a path you opt into by size.
- **Send your form fields before the file part.** The request is routed before the bytes arrive, so
  a `model` that turns up after the file is a `400` that says so. `curl -F model=… -F file=@…` and
  the OpenAI SDKs already do this. A solo node has nothing to route and accepts any order.
- **It applies to `POST /api/tools/{capability}` and `/v1/audio/transcriptions`.** The image routes
  keep the 25 MB cap: an image job is answered *before* it runs, so its bytes would have to outlive
  the request that carried them — which is the image store this project has refused three times.

A fleet with no node that can take a streamed upload answers `503` naming that as the reason,
rather than quietly falling back to buffering and failing at 25 MB.

**v3.9 shipped the machinery and no tool.** The test suite drives a real child process that echoes.
**v3.10 puts Whisper and Piper on it** — see below.

## Speech in, speech out (v3.10+)

Two endpoints, on OpenAI's audio API exactly, against your own hardware:

```bash
# transcription
curl http://localhost:5080/v1/audio/transcriptions \
  -H "Authorization: Bearer $KEY" \
  -F file=@meeting.m4a -F model=whisper-small -F response_format=verbose_json

# speech
curl http://localhost:5080/v1/audio/speech \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"en_US-amy-medium","input":"InferHub can talk now.","response_format":"wav"}' \
  --output out.wav
```

Every SDK in every language already speaks this, so pointing an existing app at your own GPU is a
base-URL change. The same two routes are served by a **solo node with no coordinator at all**, and
by one container:

```bash
docker run -d --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$KEY" -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:tools
```

`--gpus` left off is CPU Whisper — roughly real time for `small` on a modern core — and the worker's
first log line says which one it got.

### The five images

*Which one to pull, and the three mistakes worth avoiding, are in
[Which image do I pull?](#which-image-do-i-pull-v313). This is what is inside each.*

| Tag | Size | Arch | What is in it |
|---|---|---|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | The hub |
| `inferhub-node` | ~340 MB | amd64 + arm64 | A node. No inference engine — point it at an Ollama or an OpenAI-compatible server |
| `inferhub-node:ollama` | ~4 GB | amd64 | The same node with Ollama inside it (v3.7+) |
| `inferhub-node:tools` | ~6 GB | amd64 | The same again, plus Python, `faster-whisper` and `piper` (v3.10+) |
| `inferhub-node:diffusion` | ~12 GB | amd64 | The **plain** node plus PyTorch, `diffusers`, `bitsandbytes` and seven recipes (v3.14+). Does not stack — no Ollama inside |

The first three are **unchanged** by v3.10. The Python is ~1.5 GB and it is in a layer whether a
flag is on or off, so a flag would grow every existing coordinator+node stack for a feature it does
not use. An operator on the plain image can still run these workers by installing Python themselves
and pointing a manifest at it (`python/requirements-tools.txt` is what the image installs) — the
runtime does not care where the interpreter came from.

### Formats

| | |
|---|---|
| Transcription | `json` (default), `text`, `srt`, `vtt`, `verbose_json` |
| Speech | `wav`, `pcm` natively; `mp3`, `opus`, `flac` where the worker has `ffmpeg` (the `:tools` image does) |

`srt` and `vtt` are formatted at the edge from the segments Whisper produces anyway, so a worker
author never writes a subtitle timestamp. **A format that cannot be produced is a `400` naming the
ones that can** — never a silent substitution, because a caller who asked for mp3 and got a wav has
a corrupted file with a confident content type and finds out in a media player three days later.

### Models and voices

**No weights are baked into the image.** Whisper fetches on first use into `/data/tools/hf`, on the
volume, so it happens once rather than once per `docker run`. That fetch is a reach onto the
internet, so it is behind a **third** opt-in: `Tools:AllowModelDownload` (default `false`, set
`true` in the `:tools` image, because choosing that image is the consent). With it off, a worker
that needs missing weights fails the **job** with the exact pre-fetch command in the message, and
the node keeps serving everything else.

Voices are not fetched, because no default voice is right for everyone and a confident answer in the
wrong language is worse than a refusal. Drop a Piper `.onnx` + `.onnx.json` pair into
`/data/tools/voices` and restart; the model name is the file's stem.

```bash
docker exec inferhub sh -c 'mkdir -p /data/tools/voices && cd /data/tools/voices && \
  curl -fsSLO https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx && \
  curl -fsSLO https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx.json'
```

### None of it is kept

Design rule 7 at its most literal: a transcription request is a recording of somebody's voice and
the answer is what they said.

- The hub buffers the upload for the dispatch and drops it. No temp file, no cache.
- The node writes it into a per-request scratch directory that is deleted in a `finally`, always.
- **Nothing containing audio bytes or transcript text is logged, at any level.** The line for a
  transcription carries the model, the duration and the outcome — not the filename you chose.
- The usage ledger gains a **duration**, never a transcript.

`AudioPrivacyTests` runs a transcription through a real mesh with a capturing logger and fails if a
known phrase from the fixture appears anywhere in the logs or the ledger.

### Usage and quotas

Audio has no token count, so metering it as tokens would mean inventing a number. A usage row now
carries `units` and a `unitKind` — `tokens`, `audio_seconds` or `characters` — and the token fields
are untouched, so every existing consumer and every existing row keeps working. Two new client
limits go with it:

```jsonc
"Auth": { "Clients": [ { "Id": "acme", "Key": "…",
  "Limits": { "AudioSecondsPerDay": 3600, "CharactersPerDay": 200000 } } ] }
```

They are separate budgets on purpose: a client whose only limit is `TokensPerDay` could otherwise
transcribe a library for free. Over one is the same `402` + `Retry-After` to UTC midnight as the
token budget.

### One transcription at a time

`maxWorkers` defaults to 1 — a second Whisper process on the same card is two copies of the weights
and an out-of-memory error at the worst possible moment. Because routing is per **capability**
(v3.8), a node busy transcribing is still a candidate for chat: "my chat got slow when someone
uploaded a podcast" is the failure that prevents.

**Not in v3.10, and said out loud:** streaming TTS (chunked audio needs a concatenable format and a
client-side contract), diarization, speaker labels, voice cloning, and `/v1/audio/translations`.
The last is one flag on the same worker and can land whenever somebody asks.

## Text to image (v3.14+)

**Stable Diffusion on your own card, through the API your app already calls.**

```bash
curl http://localhost:5080/v1/images/generations \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"sdxl","prompt":"a lighthouse in a storm, oil painting","size":"1024x1024","n":1}' \
  | jq -r '.data[0].b64_json' | base64 -d > out.png
```

…the same on a solo node with no coordinator, and the same inside one container:

```bash
docker run -d --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$KEY" -v inferhub-images:/data -p 5083:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:diffusion
```

It is OpenAI's Images API, so pointing an existing app at your own hardware is a base-URL change.
The capability seam took the whole modality with **no protocol change**: `image` is one more
capability kind, and the router already knew how to find `(capability, model)`.

### The catalogue (v3.16)

| Recipe | Params | Steps | VRAM | Unquantized | Licence | Out of the box? |
|---|---|---:|---:|---:|---|---|
| `sdxl` | 2.6B UNet | 30 | ~8 GB fp16 | — | CreativeML OpenRAIL++-M | yes |
| `sd15` | 0.9B | 30 | ~4 GB fp16 | — | CreativeML OpenRAIL-M | yes — the only CPU-viable one |
| `flux-schnell` | 12B | **4** | ~12 GB nf4 | **~33 GB** | Apache-2.0 | **needs an HF token** — gated repo |
| `qwen-image` | 20B + 8.3B encoder | 30 | ~19 GB nf4 | **~60 GB** | Apache-2.0 | yes |
| `sd35-medium` | 2.5B MMDiT | 40 | ~16 GB bf16 | — | Stability AI Community | **licence + HF token** |
| `sdxl-turbo` | 2.6B | **1** | ~8 GB fp16 | — | Stability AI Non-Commercial | **accept the licence** |
| `qwen-360` | 20B + a rank-128 LoRA | 25 | ~19.5 GB nf4 | **~60 GB** | Apache-2.0 base, **MIT** adapter | yes — see [360° panoramas](#360-panoramas-v317) |

Two of those numbers are the whole point. **`flux-schnell`, `qwen-image` and `qwen-360` do not fit a
24 GB card at bf16** — 33 GB and 60 GB — and nf4 quantization is what makes them one-card models.
Both figures are in the table because "Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are both
true sentences about different recipes, and a table that gives one is lying to somebody.

**Two repositories are *gated* on Hugging Face, and that is a different thing from the licence.**
`black-forest-labs/FLUX.1-schnell` and `stabilityai/stable-diffusion-3.5-medium` both require you to
accept terms on the model page and present a read token, regardless of what their licence says —
FLUX is Apache-2.0 and still gated. Accepting a licence in `Tools:Image:AcceptedLicenses` tells
*this node* it may run a model; a token is how *Hugging Face* decides whether to hand the weights
over. Set `Tools:Image:HuggingFaceToken`, and note it has to be **that key** rather than an
`HF_TOKEN` environment variable, because the node clears a worker's environment before spawning it.

Without it the fetch fails with a message that says exactly this, rather than a bare `401`:

```
[diffusion] could not fetch 'flux-schnell': GatedRepoError: this repository is GATED on Hugging Face.
            Accept the terms at https://huggingface.co/black-forest-labs/FLUX.1-schnell with the
            account you are using, then set Tools:Image:HuggingFaceToken to a read token.
```

**The `model` you send is a recipe id, not a Hugging Face repo id.** A repo id is a location: it has
a slash in it that every router and metrics label has an opinion about, and it changes when a model
is re-hosted. That is not hypothetical — `runwayml/stable-diffusion-v1-5` was withdrawn, and `sd15`
points at where those weights live now.

Recipes are `python/recipes/*.json` — a repo, a **pinned commit sha**, the variant, the pipeline
class, the aspect buckets, the VRAM figure, the licence and the quantization. Drop one in and restart
the tool. A recipe with no pinned revision is skipped and logged, because "which weights were in
3.16.0" has to have an answer.

### Quantization is a property of the model, not of the request

`"quantization": "none" | "int8" | "nf4"`, through `diffusers`' native `bitsandbytes` integration,
applied to the components the recipe names. For `qwen-image` that has to include the **text
encoder**: 8.3B left at bf16 is the difference between fitting on a 24 GB card and not.

It is a recipe field rather than a header because it changes what the model *is*. Two requests to
`qwen-image` that quantized differently would produce different images from the same seed, and a
per-request knob would make reproducibility a function of a header nobody logged. Want both? Ship two
recipes with two ids — which is honest, and is also how you will describe it to your users.

**One mechanism, deliberately.** GGUF, Nunchaku and TensorRT are each faster on some model on some
card, and each is a second thing to reason about when a picture comes out worse than expected.

### A licence you have not read is a model this node will not start

Two of the seven are not permissively licensed. They are **loaded, logged by name and not started**
until their licence id is in `Tools:Image:AcceptedLicenses`:

```
[diffusion] not offering 'sdxl-turbo': its licence is 'sai-nc-community', which is not permissive
            and is not in Tools:Image:AcceptedLicenses. Read it at https://huggingface.co/…/LICENSE.md
            and, if you accept it, add "sai-nc-community" to that list.
```

This is a **fourth** opt-in, and it is not redundant with the other three: `Tools:Enabled` consents
to the feature, `Tools:Allowed` consents to *these tools*, `Tools:AllowModelDownload` consents to
reaching the internet, and none of them says "and I accept the Stability AI Non-Commercial Research
Community License". It is a **list** rather than a boolean for the same reason `Tools:Allowed` is:
`sd35-medium` is free for most people who will run it and `sdxl-turbo` is not usable commercially at
all, so one flag would let somebody who read one licence enable both.

A recipe that says nothing about its licence is treated as **not** permissive — a recipe that forgot
to say is one nobody has read the licence of.

**None of this is legal advice.** It is a refusal to make that call on your behalf, silently.

### The VRAM arithmetic, written down

`Node:Vram:BudgetMiB` is a number **you** set; `Node:Vram:ReserveMiB` (2048) is what is held back for
the inference backend and the display. Unset means no gate and v3.15's behaviour exactly.

| Card | `BudgetMiB` | Headroom | Runs |
|---|---:|---:|---|
| 8 GB | `8192` | 6144 | `sd15` |
| 12 GB | `12288` | 10240 | `sd15`, `sdxl`, `sdxl-turbo` |
| 24 GB | `24576` | 22528 | all seven |
| 24 GB, with `:ollama` holding an 8B model beside it | `24576`, reserve `8192` | 16384 | all but `qwen-image` and `qwen-360` |

A recipe that cannot fit is **not declared**, so the fleet never routes at it and nobody spends a
request finding out. One that would fit but does not *right now* — something else is mid-job on the
card — waits on the tool queue and then gets the same `503` + `Retry-After` as every other limit
here. Never an out-of-memory error inside somebody's job, which is the failure this replaces.

**Declared, not detected**, and that is the decision this section turns on. A node cannot reliably
measure the card it is on: under WSL2 — the most common GPU-with-Docker setup there is — there are no
`/dev/nvidia*` device nodes, the host's `nvidia-smi` cannot see the VM's VRAM, and the only reliable
signal that a GPU exists at all is that `libcuda.so.1` loads. A node that guessed would guess wrong on
the exact platform this project is developed on. A budget that is usually right is worse than one that
is explicitly absent, because the first failure is an OOM at 2am rather than a startup message.

The worker reports what it measures and the node **logs a disagreement**; it never overrides you.

### Switching models swaps weights; it does not restart anything

Loading FLUX is 40–90 seconds. A pool that restarted the process per recipe would pay the interpreter
and the import of torch on top of that, on every alternation. So the worker frees the old pipeline,
empties the cache, loads the new one, and reports the swap in the result's `timing` block — so a slow
request has a visible reason rather than being the one somebody remembers as "it was slow that time".

`Tools:Image:ResidentRecipes` (default **1**) allows more than one resident where the budget permits:
a 48 GB card genuinely can hold SDXL and FLUX together and should not thrash. The default is 1
because the expensive default is the one nobody realises they chose.

After `idleTimeoutSeconds` the node sends the worker an **idle hint** and the worker frees its VRAM
and stays alive. What to free is the worker's business — the node knows nothing about torch, and a
node-side unload would be the node reaching into a tool's internals.

### Weights arrive by an explicit pull, or in the background

FLUX is ~24 GB on the wire and Qwen-Image is larger. Since v3.16 you pull them **deliberately**, on
the model-command channel the fleet has had since v2.8:

```
POST   /api/admin/nodes/{nodeId}/tools/diffusion/models/flux-schnell/pull
DELETE /api/admin/nodes/{nodeId}/tools/diffusion/models/flux-schnell
```

Progress relays on the existing `/api/admin/stream` as `model-progress` events, so the console gets a
progress bar for free — along with the coalescing (a second pull of the same model rides the first)
and the property that a hub restart forgets in-flight commands like everything else.

The progress carries **no percentage**, deliberately: Hugging Face gives no download callback, and a
denominator we would have to guess is a number a dashboard would happily plot. What it reports is a
fact — how many mebibytes have landed.

A generation request for a recipe whose weights are absent is a **failed job** naming both that
command and the `huggingface-cli` one. Never a forty-minute wait.

On a fresh volume the node also fetches in the background and starts with `capabilities: []`, filling
in as each model lands:

```
[diffusion] offering recipes: none yet (fetching: sd15, sdxl)
[diffusion] fetching weights for 'sd15' from stable-diffusion-v1-5/…@451f4fe16113 (variant=fp16)
[diffusion] 'sd15' is ready; offering recipes: sd15
[diffusion] 'sdxl' is ready; offering recipes: sd15, sdxl
```

**No request ever waits on a download.** A recipe is declared only once its weights are proven
loadable, so the fleet never routes at a model that is not there — and the node tells its
coordinator the moment one becomes available, without a restart.

That is v3.14.1, and v3.14.0 got it wrong: it fetched inside the request that first named the model,
so the first `sdxl` call on a fresh volume spent the whole 900-second request budget downloading and
returned a `502`. If you want the weights before the container ever runs:

```bash
huggingface-cli download stabilityai/stable-diffusion-xl-base-1.0 \
  --revision 462165984030d82259a11f4367a4eed129e94a7b \
  --include "*.fp16.safetensors" "*.json" "*.txt" "*/*"
```

With `Tools:AllowModelDownload=false` that command is exactly what the log tells you to run.

### Sizes are a list, not a range

SDXL was trained on fixed aspect buckets. A size outside them **does not fail** — it produces
duplicated limbs and doubled horizons, which reads as "this model is bad" rather than "you asked for
1000×1000". So a size the recipe does not have is a `400` naming the ones it does:

| Recipe | Sizes |
|---|---|
| `sdxl` | `1024x1024`, `1152x896`, `896x1152`, `1216x832`, `832x1216`, `1344x768`, `768x1344` |
| `sd15` | `512x512`, `512x768`, `768x512`, `640x640` |
| `flux-schnell` | `1024x1024`, `1152x896`, `896x1152`, `1216x832`, `832x1216`, `1344x768`, `768x1344` |
| `qwen-image` | `1328x1328`, `1664x928`, `928x1664` |
| `sd35-medium` | `1024x1024`, `1152x896`, `896x1152`, `1216x832`, `832x1216` |
| `sdxl-turbo` | `512x512` |
| `qwen-360` | `2048x1024`, `1536x768`, `1024x512` — **all 2:1**, and that is not a coincidence |

Qwen-Image publishes a 4:3 bucket at 1472×1140, and 1140 is not a multiple of 8 — the edge refuses it
before the request reaches a node. Rather than ship a size that 400s or quietly round it to a
neighbour, `qwen-image` offers the three buckets that are expressible.

### Steps, guidance and seed

InferHub extensions, as headers — additive by construction, so they cannot collide with whatever
OpenAI adds next. **An unknown value is a `400`, never a silent fallback.**

| Header | Range |
|---|---|
| `X-InferHub-Image-Steps` | 1–150, capped further by the recipe's `maxSteps` |
| `X-InferHub-Image-Guidance` | 0–50 |
| `X-InferHub-Image-Seed` | any non-negative integer — also accepted as `"seed"` in the body |

`negative_prompt` is a **body** field, deliberately: it is your own words, and a header is the one
part of a request every proxy in the path writes into a log.

Every returned image carries the `seed` that produced it, so the one of four you liked is
reproducible without re-rolling the other three.

### 360° panoramas (v3.17)

`qwen-360` is [`ProGamerGov/qwen-360-diffusion`](https://huggingface.co/ProGamerGov/qwen-360-diffusion)
— a **rank-128 LoRA**, MIT licensed, over Qwen-Image's 20B MMDiT — and it produces **equirectangular**
panoramas whose left edge continues into their right edge.

> **Raise the step count. Measured in v3.28**: at the recipe's default of **25** steps a 2048×1024
> panorama comes out visibly under-denoised — a fine mottled speckle over stone, sky and ground
> alike. At **50** (`X-InferHub-Image-Steps: 50`, the recipe's `maxSteps`) with the same seed it is
> gone: 181 s instead of 107 s, and twice the megapixel-steps on the meter. **`seam_delta` moves by
> 0.0016 between those two images**, so nothing in the pipeline will tell you — this is a judgement
> the metric cannot make for you. Note that `steps`, `guidance` and `seed` travel as
> `X-InferHub-Image-*` **headers**; only `seed` is also read from the body.

```bash
curl http://localhost:5080/api/images/jobs \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"qwen-360","prompt":"a Bulgarian mountain monastery courtyard at golden hour, photograph","size":"2048x1024"}'
```

The response, once it succeeds, carries the projection on every image:

```json
{
  "created": 1785000000,
  "data": [{ "b64_json": "iVBORw0…", "size": "2048x1024", "seed": 42,
             "projection": "equirectangular", "seam_delta": 0.014, "revised_prompt": null }],
  "prompt_augmented": true,
  "trigger": "360 degree panorama with equirectangular projection"
}
```

**A 2:1 aspect is not a suggestion.** 360° of longitude over 180° of latitude is exactly two to one,
and a render at any other ratio does not fail — it produces a panorama that looks perfectly fine flat
and wraps wrongly in a viewer, which somebody discovers three days later wearing a headset. So a
non-2:1 size for this recipe is a `400` that says *why*, not only which sizes exist.

**The trigger phrase is appended, not demanded and not silently inserted.** The model wants
"equirectangular" or "360 panorama" in the prompt. Refusing a prompt without it would make the first
request everybody sends a `400`; rewriting one silently would be the one thing nothing in InferHub
does. So it is appended when absent and the response says so — `prompt_augmented`, plus the phrase.
Turn it off with `"autoTrigger": false` in the recipe; the flag is reported either way. The trigger is
a **recipe constant**, so unlike your prompt it may appear in a log — which matters, because "why does
this not look like a panorama" is almost always "the trigger did not apply".

**The seam is measured on every panorama, and repaired only if you ask.** `seam_delta` is the mean
absolute difference between the first and last columns — the pair that becomes adjacent once the
image is wrapped — normalised to 0–1. Over `Tools:Image:SeamWarnThreshold` (default `0.08`) the
result carries a `"warnings": ["seam"]` entry. It is a warning and never a failure: a slightly
visible seam is your own problem and your own aesthetic judgement.

Since **v3.23** you can close it, through two gates. The operator sets `Tools:Image:SeamRepair` on
the node — `off` by default, then `blend`, `diffuse` or `any` — and the request picks within that:

```bash
curl http://localhost:5080/v1/images/generations \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -H 'X-InferHub-Image-Seam-Repair: blend' \
  -d '{"model":"qwen-360","prompt":"a lighthouse at dusk","size":"2048x1024"}'

# → 200  "seam_delta": 0.011, "seam_delta_before": 0.134, "seam_repair": "blend"
```

**Nothing repairs by default, and no threshold ever triggers one.** The threshold decides whether to
*warn*; a number that decides to spend your GPU is the tool overriding you with a helpful expression
on. Send no header and the response is what v3.22 sent, byte for byte.

- **`blend`** is a wrapped feather across the join: numpy on the array the VAE already produced —
  milliseconds, no VRAM, **no steps and nothing on your bill**. It closes a *tonal* discontinuity and
  not a *structural* one: a seam through a doorway comes back with no visible step in brightness and
  the doorway still not lining up. That is what the other one is for.
- **`diffuse`** rolls the join into the middle of the picture and inpaints a band of it — upstream's
  `fix_seam`, over the same resident weights. It costs `int(steps × 0.4)` more steps, metered in
  `megapixel_steps` and included in `total_steps` from the first progress frame, so the bar never
  restarts.

`diffuse` does **not** imply `blend`: these name mechanisms rather than tiers, so an operator who
thinks a feathered band is worse than an honest seam can permit only the real repair. `any` is how
you say both. **A repair that does not lower the number is discarded and says so** — you get the
original image, the mechanism, and two equal numbers, because a pass that quietly made your panorama
worse is the one outcome nobody would ever go looking for. `seam_delta` is always the image you were
handed; `seam_delta_before` is what it measured first. On the content route the same three facts
arrive as `X-InferHub-Image-Seam-Repair`, `-Seam-Delta` and `-Seam-Delta-Before`.

**Where the projection turns up:** in the response body per image, on the job document
(`GET /api/images/jobs/{id}`), and as `X-InferHub-Image-Projection` on the content route — which is
the one request that has no JSON to read it from. A flat recipe reports `"flat"` rather than omitting
the field, because an absent projection is indistinguishable from a node too old to have one.

**A viewer ships with it**, at `/console.html` → *360° viewer*: paste a job id, and it picks its
renderer from the declared projection rather than from the aspect ratio — which is what everything
else does, and is wrong for every 2:1 landscape photo. It is
[`wwwroot/pano.js`](src/InferHub.Coordinator/wwwroot/pano.js), ~330 lines of hand-written WebGL, no
npm, no CDN script, no three.js.

**`qwen-image` and `qwen-360` are two model ids over one base.** Not one model with a scale
parameter: the router keys on `(capability, model)` and a client asking for `qwen-image` must never
receive a panorama. The worker keeps the base resident and swaps only the LoRA when you alternate
between them, which is seconds rather than the 40–90 s a 20B reload costs — an optimisation, invisible
to the contract, and reported in the result's timing block.

### You need a card, and this says so rather than being quietly slow

`Tools:Image:RequireGpu` defaults to **true**: with no reachable CUDA device the worker refuses to
start and names the key to unset. A tool that loads happily on a CPU and then serves four-minute
requests is a node the fleet keeps routing to, and every caller pays for the discovery.

Unset it and only recipes marked `cpuViable` are offered — `sd15` at 512×512, and not `sdxl`.
`Tools:Image:AllowSlowCpu=true` offers the rest anyway: your hardware, your call. There is no
"CPU ✅" anywhere in this README for the feature as a whole, because that tick would be true of one
recipe and a lie about the other.

The worker's first log line names the device it picked. Four gigabytes of CUDA and a silent CPU
fallback is an afternoon of blaming the model.

### No URL, no gallery, no prompt in a log

- `response_format=url` is a **`400`** naming `b64_json`. Serving a URL means the hub keeps the
  bytes, and keeping the bytes means an image store, a retention window and a question about whose
  pictures those are that we have not agreed to answer.
- **A prompt is content.** Nothing logs one, at any level, on either host. The line for a generation
  carries the model, the image count, the megapixel-steps and the outcome.
- The node writes images into a per-request scratch directory that is deleted in a `finally`,
  always — after success and after failure.
- **There is no bundled safety classifier**, and that is a decision: `diffusers`' checker returns a
  *black image*, which is indistinguishable from a broken VAE, a bad seed or an OOM, so the operator
  gets a bug report instead of a policy signal. This box generates what you ask it to generate; the
  policy is yours.

`ImagePrivacyTests` runs a generation through a real mesh with a capturing logger and fails if the
prompt appears anywhere in the logs or the ledger.

### Batches are bounded by bytes, not by count

`n` is capped by `Images:MaxBatch` (4) *and* by an upper-bound byte estimate checked **before a step
runs**, with the refusal naming the largest `n` that fits at that size. The budget is clamped by
`Tools:MaxAttachmentBytes`, because that cap is what sizes the mesh's SignalR message limit — and
exceeding a SignalR limit tears the node's connection down rather than failing the message. Raising
one without the other gets you no change, deliberately.

A batch runs on **one** node: fanning it across the fleet would mean four different seeds' worth of
scheduling for a request you think is atomic, and a partial failure would have no honest status.

### Usage and quotas

Metered in **megapixel-steps** — `width × height × steps / 1e6`, from what the worker actually
produced. Not "images": a 512² render at 4 steps and a 2048×1024 one at 30 steps are both one image,
and the second is 47× the work.

```jsonc
"Auth": { "Clients": [ { "Id": "acme", "Key": "…",
  "Limits": { "MegapixelStepsPerDay": 5000 } } ] }
```

Over it is a `402` with `Retry-After` pointing at UTC midnight, the same shape as every other budget.

## Editing a picture (v3.18+)

**Inpainting, image-to-image and variations — on OpenAI's edits API, multipart and all.**

```bash
curl http://localhost:5080/v1/images/edits -H "Authorization: Bearer $KEY" \
  -F model=sdxl -F image=@room.png -F mask=@mask.png \
  -F prompt="a tall window with morning light" \
  -H 'X-InferHub-Image-Strength: 0.75' \
  | jq -r '.data[0].b64_json' | base64 -d > edited.png
```

Three shapes, and the difference between them is what you send:

| Route | Sends | Is |
|---|---|---|
| `POST /v1/images/edits` with a `mask` | image + mask + prompt | **inpainting** — change the masked region only |
| `POST /v1/images/edits` with no mask | image + prompt | **img2img** — change the whole picture, `strength` far |
| `POST /v1/images/variations` | image | **more of this picture**, no prompt at all |

### The mask convention everybody gets backwards

**OpenAI's edits API treats a fully *transparent* pixel as the area to edit. `diffusers` takes a mask
where *white* is the area to inpaint. These are opposite.**

```
   your mask (OpenAI)              what the pipeline wants
   ┌──────────────────┐            ┌──────────────────┐
   │▓▓▓▓▓▓░░░░░▓▓▓▓▓▓▓│            │      █████       │
   │▓▓▓▓▓▓░░░░░▓▓▓▓▓▓▓│   ──────▶  │      █████       │
   │▓▓▓▓▓▓░░░░░▓▓▓▓▓▓▓│            │      █████       │
   └──────────────────┘            └──────────────────┘
    ▓ opaque = keep                  █ white = inpaint
    ░ transparent = EDIT THIS        (blank = keep)
```

Get it backwards and nothing errors — it edits everything *except* what you selected, which reads as
a broken model rather than a backwards mask. So the conversion is explicit, it happens in the worker
(the only thing in the path that may open a PNG), and there are two refusals rather than a guess:

- **A mask with no alpha channel is a `400`.** Under OpenAI's convention a fully opaque image selects
  nothing, which nobody has ever intended; reading it as "edit everything" would be a silent
  substitution of the most destructive possible interpretation.
- **A mask whose size differs from the image is a `400` naming both.** A mask names *which pixels*,
  so it is never rescaled — the edit would land next to what you selected.

Already have a white-is-edit mask? Say so:

```
X-InferHub-Mask-Convention: luminance      # openai (default) | luminance
```

An unknown value is a `400` that names both **and says which is which**, because two words whose
difference is invisible until you look at the picture are not a helpful list.

### Strength is a header, and it is the whole knob

OpenAI's edits API has no `strength`, and image-to-image without one is meaningless.

```
X-InferHub-Image-Strength: 0.75      # 0 keeps your picture, 1 ignores it
```

Absent, the recipe's `defaults.strength` applies (0.75 for both editable recipes). Out of range is a
`400`. It is a header rather than a body field for the same reason `steps` and `guidance` are:
additive by construction, so it cannot collide with whatever OpenAI adds next.

**What gets metered is the steps that actually ran.** `diffusers` enters the schedule at
`int(steps × strength)`, so 30 steps at 0.6 denoises for 18 — and 18 is what the ledger gets. Billing
the asked-for 30 would charge for work nobody did.

### Not every model can edit

Editing is its own capability, `image-edit`, declared per recipe from its `operations` field:

| Recipe | `operations` |
|---|---|
| `sdxl`, `sd15` | `generate`, `edit`, `variation` |
| everything else | `generate` |

FLUX.1-schnell has no official inpainting pipeline; SDXL does. So an edit against a generate-only
recipe is a `503` that **names the ones that can**:

```json
{ "error": { "code": "capability_unavailable", "message":
  "no node currently provides 'image-edit' for model 'flux-schnell'. Models on this fleet that do: sd15, sdxl" } }
```

That is a fleet-state answer rather than an authorization one, which is the distinction v3.8 wrote
down. A second capability kind rather than a per-model operation list, because the router filters on
`(capability, model)` and nothing else — and teaching it to read a nested operation set would mean
teaching the affinity, the queue and the saturation logic the same thing.

### Editing is a job like any other

Same queue, same per-step progress, same cooperative cancel, same five-minute in-memory retention.
The async route takes multipart too, and names its operation:

```bash
curl http://localhost:5080/api/images/jobs -H "Authorization: Bearer $KEY" \
  -F operation=edit -F model=sdxl -F image=@room.png -F mask=@mask.png \
  -F prompt="a tall window with morning light"
```

JSON generates; multipart edits. A multipart submission that names no `operation` is a `400` naming
both — this is InferHub's own contract, where ceremony is cheaper than a silent substitution.

### What comes in is content too

`Images:MaxRequestBytes` (25 MB) caps the picture and the mask **together**, refused with a `413` at
the edge before anything is buffered onward; each part is additionally capped by
`Tools:MaxAttachmentBytes`. Nothing is retained: the bytes are held for the dispatch, written into a
per-request scratch directory the node deletes in a `finally`, and dropped. **The filename you chose
never leaves the edge** — the parts travel as `image` and `mask`, because what you called a file on
your disk is metadata about your day.

**Not in v3.18, and said out loud:** ControlNet, IP-Adapter and reference-image conditioning (each is
a per-base-model zoo of auxiliary weights with its own preprocessors, and every preprocessor is image
processing); an outpainting helper (it is inpainting with a canvas you prepare, and preparing it on
the hub would mean decoding a pixel); multi-image edit chains; and FLUX inpainting, which has no
official pipeline to wrap.

## A job that takes two minutes (v3.15+)

An SDXL render at 50 steps is not a request, it is a job. Before v3.15 you sent one and held a
connection open, and the only two things you could learn were "it worked" and "something timed out"
— usually from a proxy in the path, with a status nobody could act on.

```bash
# submit
ID=$(curl -sS http://localhost:5080/api/images/jobs -H "Authorization: Bearer $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"sdxl","prompt":"a lighthouse in a storm","size":"1024x1024"}' | jq -r .id)

# watch  (SSE: queued → running(step 7/28) → succeeded)
curl -N http://localhost:5080/api/images/jobs/$ID/events -H "Authorization: Bearer $KEY"

# collect (read-once, in memory, expiring)
curl -sS http://localhost:5080/api/images/jobs/$ID/content/0 -H "Authorization: Bearer $KEY" -o out.png

# or change your mind
curl -X DELETE http://localhost:5080/api/images/jobs/$ID -H "Authorization: Bearer $KEY"
```

A submitted job answers `202` with a **place in line**, not a wait-then-503: you already accepted
asynchrony, so making you retry would be strictly worse than telling you where you are. The states
are `queued → running → succeeded | failed | cancelled | expired`, with `cancelling` in between, and
the transition table is data both hosts and the tests read from the same place.

### There is no `background: true` flag, and that is on purpose

OpenAI has no asynchronous Images API to adopt, and where there is no dialect to adopt this project
does not invent an OpenAI-shaped one — work with no existing shape travels as its own honest contract
under `/api`. A `background: true` field on `/v1/images/generations` returning a non-OpenAI body
would make one route answer two incompatible shapes depending on a flag, which every typed SDK gets
wrong, and would turn "is this endpoint OpenAI-compatible?" into a question with a footnote.

`/v1/images/generations` is **unchanged**. Internally it became "submit a job and wait for it", so a
synchronous call and an asynchronous one queue in the same line and are metered by the same code —
the alternative, two paths to a GPU with two ideas of fairness, is how a fleet grows a fast lane
nobody documented. Past `Images:SyncMaxWaitSeconds` (120) it answers `503` naming the job id and the
async route; the job **keeps running**, because throwing away a minute of GPU because an HTTP client
got bored is your decision, not the hub's.

### Cancel does not kill the worker

A `DELETE` sends a `cancel` frame down to the worker, which honours it from its per-step callback and
answers with an error coded `cancelled`. Then it is **still alive, still holding its weights**.

Killing it would be simpler and is wrong. A diffusion worker's weights took tens of seconds to load —
and twelve to twenty gigabytes taking a minute or more once the catalogue grows — so killing it to
abandon one job punishes the *next* caller for your change of mind, and the punishment gets worse
with every model added. A worker that has not answered within `Tools:CancelGraceSeconds` (20) is by
definition not cooperating, and *that* one is terminated and restarted.

**Cancellation is best-effort and this says so.** A job cancelled at step 27 of 28 may still succeed,
and if it does you get your image. Discarding a finished result to honour a state name would be worse
than telling you what actually happened.

### Results live for five minutes, in memory by default

- `Images:Jobs:RetentionSeconds` (300) — a finished job's record and bytes are dropped this long
  after completion, read or not.
- `Images:Jobs:MaxRetainedBytes` (512 MB) — a global ceiling, LRU-evicting **completed** results and
  never an in-flight one, enforced **on insert** rather than on a timer. An evicted job reads as
  `expired` with a reason, so arriving late is a `410` that says what happened rather than a `404`
  that looks like a bug.
- **Read-once by default** — a delivered image is dropped immediately. `Images:Jobs:KeepAfterRead`
  exists for a console's benefit and is the setting that makes the hub briefly an image cache.
- **Nothing touches disk by default.** No temp file, no spill under memory pressure, no cache
  directory. Under pressure the answer is eviction and a `503` on submit, not a file. A restart
  forgets in-flight and completed jobs, exactly like every other counter on the hub.

### A job can survive a restart, if you say so (v3.24)

`Images:Jobs:Persistence=file` writes a finished job's record and its bytes under
`Images:Jobs:DataDirectory`, so a deploy no longer turns a job id from thirty seconds ago into a
`404`. It is **off by default**, and turning it on is answering a data-retention question rather than
flipping a performance switch — which is why it was argued as the *fourth* exception to this
project's "no persisted state" rule rather than added quietly. What that argument bought you:

- **Durability does not extend retention.** The window is applied *on load*: a hub that was down for
  an hour comes back and deletes everything past `RetentionSeconds` before it serves the first
  request. Restarting is never a way to keep a picture longer than you allowed.
- **Read-once means read-once from the disk too.** Delivery, eviction and expiry each unlink the file
  in the same operation that drops the bytes, so the API's answer and the directory's contents cannot
  disagree.
- **An interrupted job is never resumed.** It comes back `failed` with reason `hub_restarted` and a
  sentence saying so, because **nothing durable holds your prompt** — there is no field for one, and
  a re-dispatch would have needed it. Submitting again is your call, exactly as it is when a node
  disappears mid-job.
- **There is no `postgres` here, deliberately.** Image bytes are not row data, and half a gigabyte of
  PNGs in a `bytea` column is WAL amplification per render plus a database dump that now contains
  pictures.
- **Under `Cluster:Enabled` this is per instance.** A promoted standby does not hold the old
  primary's pictures. The hubs share a Postgres, not a directory.

The container images set `Images__Jobs__DataDirectory=/data/images`, so mounting a volume at `/data`
is what makes it survive a `docker run` as well as a restart.

### The queue is FIFO and it is not clever

A GPU running diffusion is a resource there is exactly one of, so the hub gives each capable node one
image job at a time and takes the queue in order. Shortest-job-first would let a stream of four-step
requests starve a fifty-step one indefinitely and the starvation would be invisible; fair-share needs
a notion of tenant weight this project's client model does not have. `Images:Jobs:MaxQueueDepth` (32)
bounds it, and a full queue is `503` + `Retry-After` — the same status and header as every other
limit here, so your retry logic behaves identically whichever one it hit.

### A node that disappears mid-job fails the job, and never retries it

An image job that died at step 22 produced no output, so it is *technically* retryable — and
retrying it would silently double the GPU-minutes and the ledger units for one request. It fails with
`node_lost` and you decide. A job still `queued` when its node goes away has spent nothing and is
simply routed again.

### Every route is yours only

A job id belonging to another client is a `404`, byte-identical to one that does not exist — never a
`403`. On a surface whose ids are only knowable by having been issued one, the difference between
"not yours" and "not there" *is* the isolation boundary.

### Solo mode gets the same five routes

A standalone node serves `/api/images/jobs` itself, with the same bodies and the same statuses — one
job at a time, because it is a box with a card in it. The deployment least likely to have a proxy
that tolerates a two-minute request is the one somebody is running on a laptop.

## Text to video (v3.25+)

A node with a card and the `:diffusion` image can render short clips, and the API is **OpenAI's
Videos API** — not one of ours:

```bash
curl -s http://localhost:5080/v1/videos   -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json'   -d '{"model":"wan-t2v-1.3b","prompt":"a paper boat on a puddle, slow dolly in","seconds":5}'
# {"id":"video_8f3c…","object":"video","model":"wan-t2v-1.3b","status":"queued","progress":0,
#  "seconds":5,"size":"832x480","created_at":1755400000}

curl -s http://localhost:5080/v1/videos/video_8f3c… -H "Authorization: Bearer $KEY"
# …"status":"in_progress","progress":42…

curl -s http://localhost:5080/v1/videos/video_8f3c…/content -H "Authorization: Bearer $KEY" -o out.mp4
```

`GET /v1/videos/{id}` polls, `/content` fetches the bytes **once**, and `DELETE` cancels and drops
it. That is the whole surface.

> **Measured in v3.28, on a 3090 Ti, and worth knowing before you first run one.** A five-second
> `wan-t2v-1.3b` clip at 832×480 is **~143 s of cold model load plus ~340 s of generation** — 378 s
> end to end, 81 frames, 982 KB of H.264. Two things follow. **Raise
> `Dispatcher:TimeoutSeconds` to 1800**: the default of 300 covers tool jobs too and gives up while
> the model is still loading. And the first pull is **~29 GB**, because "1.3B" names the transformer
> only — the UMT5 text encoder is ~11B and every weight in that repository is fp32 with no fp16
> variant.

### Why this one is adopted and the image one was invented

[v3.15](#a-job-that-takes-two-minutes-v315) built `/api/images/jobs` because OpenAI has no
asynchronous *Images* API to adopt, and this project's rule is to speak the dialect clients already
speak and to invent only where there is none. Video has one, and it is asynchronous by construction
— create, poll, fetch, delete — so `client.videos.create(...)` in any OpenAI SDK works against a
hub that can serve it.

Two of that dialect's routes are **501s that say why** rather than 404s that read like an old
server. `GET /v1/videos` would enumerate your jobs, and this coordinator holds no such index — the
id it handed you *is* the capability to fetch the bytes. `POST /v1/videos/{id}/remix` needs the
request kept after the job ends, and nothing durable here holds a prompt.

### It is the same job model, so it is the same everything else

The queue, the per-step progress, the cooperative cancel that leaves the worker holding its weights,
the five-minute read-once retention and [v3.24's optional
durability](#a-job-can-survive-a-restart-if-you-say-so-v324) are the code the image jobs already
run. `Images:Jobs:*` configures both, and `Images:MaxResponseBytes` bounds both — one wire, one
ceiling, clamped by `Tools:MaxAttachmentBytes` as always.

A video id will not open an image route and an image id will not open a video route: both are the
same `404` an unknown id earns.

### Three models since v3.26, and four things worth knowing about all of them

| Recipe | Geometry | Clock | VRAM | Download |
|---|---|---|---|---|
| `wan-t2v-1.3b` | 832×480 / 480×832 | 16 fps, 2–5 s | ~15.5 GB, bf16 | ~29 GB |
| `wan-t2v-14b-720p` | 1280×720 / 720×1280 | 16 fps, 2–5 s | **~24 GB at nf4** (~50 GB at bf16) | ~75 GB |
| `cogvideox-2b` | 720×480 only | **8 fps**, one 6 s offer | ~16 GB, fp16 | ~13 GB |

All three are **Apache-2.0**, so none of them needs a licence decision.
**`wan-t2v-14b-720p` does not fit a 24 GB card**, and that is the VRAM gate doing its job rather
than an oversight: a node with 24 GB never declares it, so the hub never routes to it and nobody
finds the ceiling four minutes into a render. A recipe's figure is sized at the **largest**
`(size, seconds)` pair it offers, because the gate is handed one number before any caller exists.

**`cogvideox-2b` is why `fps` is a required field.** It runs at 8 where Wan runs at 16, and a
worker that fell back to 16 would encode its 49 frames at twice their rate — not an error, a clip
that plays at double speed. 49 frames at 8 fps is **6.125 s**, so `seconds: 6` is the label and
6.125 is what comes back.

- **"1.3B" names the transformer only.** The text encoder beside it is UMT5-XXL at ~11B, every
  weight in the repo is stored fp32 with **no fp16 variant**, and the first pull is **~29 GB**. The
  recipe declares 15 500 MiB of VRAM for that reason, not for 1.3B of it.
- **Sizes divide by 16, not by 8.** `840x480` is a perfectly good *image* size and an invalid video
  one; the refusal says so at the edge rather than four minutes into a job.
- **`seconds` is a label, and the response reports the measurement.** A latent video pipeline puts
  frames on a 4k+1 grid, so `seconds: 5` means 81 frames and 81 frames at 16 fps is **5.06 s** —
  which is what comes back. A duration the model does not offer is refused naming the list, never
  rounded to the nearest one.
- **`fps` is not yours to set — and since v3.26 the recipe must state it.** It is the rate the model
  was trained at; re-timing the frames at encode changes how fast the world moves. A recipe that does
  not declare it is skipped and logged by name rather than assumed to be 16.

### Usage, and the two units

A video meters **both**: `megapixel_steps` — `width × height × frames × steps / 1e6`, because a
video transformer denoises the whole latent stack on every step — and `video_seconds`. The first is
the card's real cost and spends the same `MegapixelStepsPerDay` budget an image does, because it is
the same card: a 5-second 832×480 clip at 30 steps is about **970** megapixel-steps against an SDXL
image's 31. The second is the number a human asks about, and neither can be derived from the other.
`inferhub_video_seconds_total{kind,model}` is on `/metrics`, and emits nothing at all on a fleet
that has never rendered one.

**Since v3.27 both units are also gates.** `VideoSecondsPerDay` is the budget in the unit a person
sizes; `MegapixelStepsPerDay` remains the one that describes the card. A submission is refused if
*either* is spent, with a `402` naming which — before v3.27 only the megapixel-step budget was
checked, so a fleet that meant "an hour of video a day" had no way to say it.

### Watching one from the console

`/console` has a **Video** panel: submit a prompt, watch the row (queue position, step *n* of *m*,
elapsed, which node), cancel, and play the clip in the page. It speaks `/v1/videos` — the same routes
an SDK calls — for everything except listing, which that dialect refuses on purpose; the listing is
`GET /api/videos/jobs` and is client-scoped, so the panel holds a **client** key rather than the
admin key. A fetched clip is consumed: it lives in the browser tab and the hub has dropped its copy.

Underneath it, **Video recipes on the fleet** answers "why can I not use that model" for clips the
way the Images panel does for pictures — the licence, the card, a profile, or weights that are not
there yet. `wan-t2v-14b-720p` showing `over-budget` on a 24 GB box is the ceiling working, not a
fault, and the row says so.

### What the video track does not do yet

**No image-to-video** — a second capability and a second input path, and it is named rather than
forgotten. **No caller-chosen fps.** **No audio**: Wan2.1 T2V produces none, and a silent track
added to look complete is a lie in a container. **No 480p entry for the 14B** — the same weights at a
second geometry means two recipe ids over one loaded pipeline, which the residency map would count
twice against one card; that is a phase, not a JSON file. **No listing on `/v1/videos`** — an id is
the capability to fetch the bytes, so the dialect's enumeration stays a `501` that says why, and the
console's listing is a client-scoped route of our own. **And no video has been watched**: every claim above
about these models comes from their own configs, their model cards and the pinned `diffusers` wheel,
not from a card.

## Configure the fleet, not the boxes (v3.11+)

Twenty nodes means twenty `appsettings.json` files and twenty restarts. A **node profile** is the
coordinator saying what a node should be doing — and the node deciding whether it may:

```bash
curl -X PUT http://localhost:5080/api/admin/profiles/gpu-boxes \
  -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' -d '{
    "selector": { "labels": { "tier": "gpu" } },
    "capabilities": { "embed": false },
    "tools": { "whisper": true, "piper": false },
    "models": { "ensure": ["llama3.2"], "remove": [] },
    "maxConcurrency": 4
  }'
```

Every node whose labels match applies what it can, and reports back what it would not do and why.
`GET /api/admin/nodes/{id}/profile` is the answer to "I wrote that and the box still does what it
did before".

### The hub can only ever narrow a node, and the check runs on the node

This is the design decision the feature exists around, and most systems in this space get it the
other way round. A profile can switch a capability **off**; it cannot re-open one the box's own
`Node:Capabilities:Disabled` closed. It can **stop** a tool; it cannot introduce one — a tool id that
is not already in that node's `Tools:Allowed` is refused by name, and there is no field anywhere in a
profile for a command, a path or an interpreter. It can **lower** `MaxConcurrency`; raising it is
refused, because that number is a statement about hardware you own and the coordinator does not.

The clamp that enforces all of this runs **on the node**, not on the hub. A clamp on the hub is a
clamp an attacker skips by not being the hub: the point is that a compromised or misconfigured
coordinator cannot turn a fleet of GPU boxes into fleet-wide remote code execution. What a profile
can do is bounded by what you already put in the file on the machine.

### Desired state, so a rebooted node fixes itself

A profile is not a command. A node asks for its profile every time it registers, so a box that was
being rebuilt when you wrote one converges on the way back in, with no operator action and nothing
for the hub to remember. Re-applying the same revision changes nothing and says so — which is why
that reconnect is safe to run unconditionally instead of re-pulling forty gigabytes of weights.

Refusals are **per item**: a profile asking for one impossible thing and four possible ones applies
the four and reports the one. A profile is never a startup dependency and **never restarts a node** —
switching a tool off stops its workers in place, and switching it back on starts them again.

### Selectors, conflicts and persistence

Selectors are exact: a `nodeId`, or a set of `labels` of which **every** pair must match. No globs,
no expression language — a pattern dialect pointed at a security boundary is how somebody's node ends
up matched by a rule that reads correct. A selector that names nothing matches **nothing**, and is
refused with a 400 rather than quietly applying to every box you own.

If two profiles match one node, neither is sent. The node keeps what it last applied and `/api/status`
and the console show it as `conflict` until you fix the selectors — silent precedence is how a node
ends up in a state no single document explains.

Profiles are in memory by default and a coordinator restart forgets them; set
`Fleet:Profiles:Persistence` to `file` or `postgres` to keep them (`postgres` is what an HA pair
wants, so both hubs read one fleet configuration). Losing them is survivable by design: every node
falls back to its own configuration, which is never a wrong answer and never a capability nobody
granted.

## A corpus on every node (v3.12+)

Retrieval used to be the hub's, or a standalone node's, and never both. From v3.12 the coordinator can
**turn retrieval on for a node, choose its vector engine, and have the node bring it up** — without
editing a file on the box and without restarting it:

```bash
curl -X PUT http://localhost:5080/api/admin/profiles/edge-boxes \
  -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' -d '{
    "selector": { "labels": { "site": "sofia" } },
    "retrieval": {
      "enabled": true,
      "provider": "qdrant",
      "url": "http://qdrant.sofia.internal:6333",
      "credentialRef": "sofia-qdrant",
      "collections": ["site-sofia-docs"],
      "embeddingModel": "nomic-embed-text"
    }
  }'
```

The node starts the corpus while it keeps answering chat. Switch it off and the retrieval routes go
back to 501 with the sentence they have always had, the in-flight retrievals drain rather than fault,
and the corpus is still there when you switch it back on.

### One authority per collection name, and the hub knows who it is

v3.6 refused to let a meshed node hold its own corpus, because a node with hub-derived replicas *and*
an authority under the same names is a collection with two truths and a replication pass waiting to
overwrite one of them. **That rule is not relaxed** — a node that sets `LocalApi:Retrieval:Enabled` by
hand while meshed still refuses to boot.

What changed is that the hub can now be the one who *assigns* the corpus, and can therefore be the one
who *knows*:

- The hub records an **owner** per collection: `hub`, or `node:{id}`.
- A hub-side create of a node-owned name is a **409 naming the owner** — not a mystery conflict.
- **Replication and healing never target a node-owned collection.** There is nothing hub-side to
  derive replicas from, and pushing an empty snapshot at the owner would be the hub deleting somebody's
  corpus while reporting success.

### The client API does not move

A client posts to `/api/collections/{c}/documents` and searches at `/api/collections/{c}/search`
exactly as before, whether the chunks are in the hub's store or on a box in another city. The hub
sees a node-owned collection and dispatches the work to its owner over the connection the node already
opened — so client scoping, quotas and the response shapes are all the ones you already had.

Two things are said out loud rather than left to be discovered:

- **PDF is a 415 on a node-owned collection.** The PDF extractor ships with the coordinator only, and
  a hub that extracted the text and shipped chunks would be a second ingestion path with different
  chunking and a different failure mode. Convert to text or Markdown, or use a hub-owned collection.
- **An owner that is not connected is a 503 naming the node**, never a quiet answer from the hub's own
  store. A confident answer from the wrong corpus is the failure nobody notices.

### The engine, the secret and the disk stay the operator's

A profile names `provider` and `credentialRef`. It cannot name a **secret**: `credentialRef` is a
*name*, resolved on the node against `LocalApi:Retrieval:Credentials:{ref}`, and a name the box does
not have is a refusal naming the key — never a fall back to an unauthenticated connection to your
Qdrant. There is no field anywhere in a profile for a data directory, either.

`postgres` is refused **by name** on a node, with the reason: the Postgres connector needs `Npgsql`,
which is coordinator-scoped by design. A node runs `local` (its own disk) or `qdrant` — and the Qdrant
connector costs a node nothing, because it was hand-rolled over `HttpClient` back in v3.1 rather than
taking the official gRPC client.

A start that fails — unreachable engine, unresolvable credential, wrong dimension — leaves the node
with **no corpus and a reported refusal**, visible on `/api/status` and in the console, while the node
goes on serving chat.

## One box, one container: chat, RAG and speech (v3.13+)

Six releases add up to one story, and the sections above tell it in pieces. Here it is once, top to
bottom. **A coordinator on a small always-on host, one GPU box, and everything else configured from
the hub** — no file edited on the GPU machine after the first `docker run`, no restart, and inference
never stops while you do it.

**1. The hub.** Anywhere; no GPU.

```bash
docker run -d --name inferhub-hub \
  -e Auth__AdminApiKeys__0="$ADMIN_KEY" \
  -e Auth__ApiKeys__0="$CLIENT_KEY" \
  -e Auth__NodeEnrollmentSecret="$ENROLL" \
  -v inferhub-hub:/data -p 5080:8080 \
  ghcr.io/dev-art-solutions/inferhub-coordinator
```

**2. The GPU box.** One container. It carries Ollama *and* the speech workers, dials out to the hub,
and needs no inbound rule of its own. The labels are what a profile will select on in step 4.

```bash
docker run -d --name inferhub-node --gpus all \
  -e Coordinator__Url=http://hub.example:5080 \
  -e Coordinator__EnrollmentSecret="$ENROLL" \
  -e Node__Labels__role=gpu \
  -e Tools__Enabled=true -e Tools__Allowed__0=whisper -e Tools__Allowed__1=piper \
  -v inferhub-node:/data \
  ghcr.io/dev-art-solutions/inferhub-node:tools

docker exec inferhub-node ollama pull llama3.2
```

`Tools:Allowed` is the operator's grant and **the ceiling the hub can never raise** — step 4 can
switch those two tools off, and can never introduce a third.

**3. Chat, already.** Point anything that speaks Ollama or OpenAI at the hub.

```bash
curl http://hub.example:5080/v1/chat/completions \
  -H "Authorization: Bearer $CLIENT_KEY" -H 'Content-Type: application/json' \
  -d '{"model":"llama3.2","messages":[{"role":"user","content":"Hello!"}]}'
```

**4. Give the box a corpus — from the hub, with no file edited on the box.**

```bash
curl -X PUT http://hub.example:5080/api/admin/profiles/gpu-boxes \
  -H "Authorization: Bearer $ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{
        "name": "gpu-boxes",
        "selector": { "labels": { "role": "gpu" } },
        "maxConcurrency": 2,
        "retrieval": {
          "enabled": true, "provider": "local",
          "collections": ["handbook"], "embeddingModel": "all-minilm"
        }
      }'
```

The node pulls the profile, **clamps it against its own configuration**, brings the corpus up at
runtime, and reports what it applied and what it refused. Ingest and search stay on the hub's own
API — the hub dispatches them to the owner:

```bash
curl -X POST http://hub.example:5080/api/collections/handbook/documents \
  -H "Authorization: Bearer $CLIENT_KEY" -F file=@handbook.md

curl http://hub.example:5080/v1/chat/completions \
  -H "Authorization: Bearer $CLIENT_KEY" -H 'Content-Type: application/json' \
  -H 'X-InferHub-Retrieve: handbook' \
  -d '{"model":"llama3.2","messages":[{"role":"user","content":"What is our refund window?"}]}'
```

**5. Speech, on the same box, with no extra deployment.** The `:tools` image already carries the
workers; routing is per `(capability, model)`, so **a node busy transcribing is still a candidate for
chat**.

```bash
curl -X POST http://hub.example:5080/v1/audio/transcriptions \
  -H "Authorization: Bearer $CLIENT_KEY" \
  -F file=@meeting.m4a -F model=whisper-small -F response_format=srt
```

**6. Watch all of it on one page.** `http://hub.example:5080/console.html`, admin key in the bar at
the top.

### Driving all of it from one page (v3.13+)

The console is plain HTML, CSS and JavaScript served by the coordinator — no build step, no bundler,
no framework, and nothing to install. It reads `/api/status` and `/api/admin/*` and adds no API of
its own.

| Panel | What it answers |
|---|---|
| **Needs attention** | Everything that is *not* doing what it was told, with the reason. Above the fold, hidden when there is nothing to say. |
| **Capabilities** | Node × capability, plus a fleet row: how many boxes serve `chat`, `embed`, `transcribe`, `speak`, and over how many models. |
| **Tools** | Per node and manifest: allowed or not, running / suspended / stopped / not-allowed, live workers, requests, and the last error in the worker's own words. |
| **Node retrieval** | Which node hosts which corpus, on which engine, with how many records — and why it is not running, when it is not. |
| **Node profiles** | The profile book, an editor, apply and delete — and a table of which boxes took which revision, and what each refused. |

**Desired beside effective, always.** A profile that says `maxConcurrency: 8` against a box whose
own config caps it at 2 is not an error and not a silent no-op: the node applies the 2, reports the
refusal with the key that stopped it, and the console shows both. That is the whole design —
[the hub can only ever narrow a node](#the-hub-can-only-ever-narrow-a-node-and-the-check-runs-on-the-node),
so "it did not take" needs "and here is what stopped it" beside it or it reads as a bug.

A worked example of the confusion this removes: you drop `whisper.json` into the manifest directory
and nothing happens. The node logs it, but the node is a box you are not tailing. The console shows a
**not-allowed** row and one sentence — *the manifest is on the box but `Tools:Allowed` does not name
it* — which is the difference between one config line and an afternoon.

### What `/metrics` gained

The [Prometheus endpoint](#prometheus-metrics-v210) grew the series this track needs:

| Series | Labels |
|---|---|
| `inferhub_capability_nodes`, `inferhub_capability_models` | `capability` |
| `inferhub_tool_requests_total` | `node`, `tool`, `outcome` |
| `inferhub_tool_workers` | `node`, `tool`, `state` |
| `inferhub_tool_pool` | `node`, `tool`, `state` |
| `inferhub_audio_seconds_total`, `inferhub_audio_characters_total` | `kind`, `model` |
| `inferhub_profile_state` | `profile`, `state` |
| `inferhub_node_corpus_records` | `node`, `collection` |

**Absence stays absence.** A capability nobody serves, a tool nobody loaded, a profile nobody wrote
and a corpus nobody assigned each produce **no series at all** rather than a zero — the same rule the
per-node throughput gauges have followed since v2.10. A dashboard reading `transcription capacity: 0`
on a fleet that was never asked to transcribe would page somebody at three in the morning about a
feature nobody turned on.

The two audio counters are deliberately separate: a transcription is metered in **seconds** and a
synthesis in **characters**, and one summed `units` series would add the two into a number nobody can
tell is wrong. `inferhub_profile_state{state="refused"}` and `{state="conflict"}` are the two worth
alerting on — both mean a box is not doing what your fleet configuration says it should.

## One box, one card: a picture, a panorama, an edit (v3.19+)

The image track is six releases and the sections above tell it in pieces. Here it is once, top to
bottom, on **one GPU box beside the fleet you already have** — and the point of putting `:diffusion`
in its own container is that this box does nothing else.

**1. The node.** A card, a volume, and the same enrollment secret your other nodes use.

```bash
docker run -d --name inferhub-diffusion --gpus all \
  -e Coordinator__Url=http://hub:8080 \
  -e Coordinator__EnrollmentSecret="$ENROLL" \
  -e Node__Vram__BudgetMiB=24576 \
  -v inferhub-diffusion:/data \
  ghcr.io/dev-art-solutions/inferhub-node:diffusion
```

It comes up in seconds and declares **nothing**. That is correct: weights are fetched on a background
thread and a recipe is offered only once it is proven loadable, so the fleet never routes at a model
that is not there. Watch it fill in:

```
[diffusion] device: cuda (NVIDIA GeForce RTX 3090 Ti), 23285 MiB free of 24563 MiB
[diffusion] offering recipes: none (editing: none) (fetching: sd15, sdxl)
[diffusion] 'sd15' is ready; offering recipes: sd15 (editing: sd15)
[diffusion] 'sdxl' is ready; offering recipes: sd15, sdxl (editing: sd15, sdxl)
```

**2. A picture**, through the API your app already calls — the hub routes `image` to this box and
`chat` to whatever else you have:

```bash
curl http://hub:5080/v1/images/generations -H "Authorization: Bearer $CLIENT_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"sdxl","prompt":"a lighthouse in a storm, oil painting","size":"1024x1024"}' \
  | jq -r '.data[0].b64_json' | base64 -d > out.png
```

**3. A slow one, as a job.** A 50-step render is not a request. Submit it, watch the steps, and
cancel it if you change your mind — the worker stays warm either way:

```bash
ID=$(curl -sS http://hub:5080/api/images/jobs -H "Authorization: Bearer $CLIENT_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"qwen-360","prompt":"a mountain monastery courtyard at golden hour","size":"2048x1024"}' | jq -r .id)

curl -N http://hub:5080/api/images/jobs/$ID/events -H "Authorization: Bearer $CLIENT_KEY"
curl -sS http://hub:5080/api/images/jobs/$ID/content/0 -H "Authorization: Bearer $CLIENT_KEY" -o pano.png
```

**4. Look around it.** `/console.html` → **360° viewer**: paste the job id. It picks its renderer from
the projection the worker *declared*, not from the aspect ratio.

**5. Change it.** A mask whose transparent pixels are the region to edit:

```bash
curl http://hub:5080/v1/images/edits -H "Authorization: Bearer $CLIENT_KEY" \
  -F model=sdxl -F image=@room.png -F mask=@mask.png \
  -F prompt="a tall window with morning light" \
  -H 'X-InferHub-Image-Strength: 0.75' | jq -r '.data[0].b64_json' | base64 -d > edited.png
```

**6. Operate it from one page.** `/console.html` → **Images**: live jobs with a step bar and a cancel
button, every recipe on the fleet with **why** it is or is not offered, and the card's arithmetic —
budget, reserve, what is resident, what the worker actually measured.

That last one is the part worth clicking before you need it. A recipe whose licence you have not
accepted, or one too big for the budget you declared, is **not offered** — which is the right routing
behaviour and, without this panel, indistinguishable from a model nobody installed.

Here is that table on a box sharing its card with a chat container, so the reserve is 8192 rather
than the 2048 the dedicated run above uses:

| Recipe | Offered for | Why not |
|---|---|---|
| `sdxl` | generate, edit | — |
| `sd15` | generate, edit | — |
| `sdxl-turbo` | — | licence `sai-nc-community` is not permissive and is not in `Tools:Image:AcceptedLicenses` |
| `qwen-image` | — | wants 19000 MiB; `BudgetMiB: 24576` minus `ReserveMiB: 8192` leaves 16384 |

On the dedicated box above — 24576 with the default 2048 held back — **every recipe in the catalogue
fits**, and the only row that would say anything is `sdxl-turbo`'s licence.

**Nothing about it is a gallery.** Thumbnails in that panel live in your browser tab and vanish on
reload; the hub drops a result on delivery or after five minutes, whichever comes first. There is no
history endpoint and there is not going to be one — that is the same refusal as "no URL in the
response", one layer up.

### The image series

| Series | Labels | Present when |
|---|---|---|
| `inferhub_image_queue_depth`, `inferhub_image_jobs_active`, `inferhub_image_retained_bytes` | — | **always, at zero** — a hub with a queue and nothing in it is saying something |
| `inferhub_image_jobs_total` | `recipe`, `media`, `outcome` | a recipe has finished at least one job |
| `inferhub_image_job_seconds` (histogram) | `recipe`, `media` | ditto — buckets at 1/5/15/60/300s, from **submission**, so queue time counts |
| `inferhub_image_megapixel_steps_total` | `kind`, `model` | work has been metered |
| `inferhub_image_recipe` | `node`, `recipe`, `media`, `reason` | a node reports recipes of either medium |
| `inferhub_node_vram_budget_mib`, `_reserve_mib`, `_resident_mib` | `node` | **a budget is declared** |
| `inferhub_node_vram_measured_mib` | `node` | the worker reported a reading |

`inferhub_image_recipe{reason="unlicensed"}` and `{reason="over-budget"}` are the two to alert on:
both mean a model you configured is not being served and nothing else in the fleet will tell you.

**`media` is a label rather than a second family of series (v3.27).** These counters have included
video since v3.25 with nothing to separate it; a four-minute clip and a nine-second picture in one
histogram make both unreadable. Existing queries keep working and now sum both media, which is the
honest arithmetic — add `media="image"` or `media="video"` to split them. `inferhub_image_recipe`
gained the same label in the release that started reporting video recipes at all: before it, a clip
model refused for its licence or the card was simply missing from the hub's view.

**A node with no declared VRAM budget emits no VRAM series at all** — not a zero. Undeclared is not
"this box has no card"; it means nobody set `Node:Vram:BudgetMiB` and there is no gate on that box.

## OpenAI-compatible API

Everything else in this ecosystem speaks the OpenAI wire format and exposes exactly one knob
for pointing somewhere new: a base URL. Set it to your coordinator's `/v1`.

| Endpoint | Notes |
|---|---|
| `POST /v1/chat/completions` | Blocking and SSE streaming. Maps to the `chat` job kind. |
| `POST /v1/completions` | Legacy text completion. Maps to `generate`. |
| `POST /v1/embeddings` | `float` and `base64` encodings (the Python SDK asks for base64 by default). |
| `GET /v1/models`, `GET /v1/models/{id}` | The models your nodes advertise. |

```python
from openai import OpenAI

client = OpenAI(base_url="http://your-coordinator:5080/v1", api_key="YOUR_API_KEY")

stream = client.chat.completions.create(
    model="llama3",
    messages=[{"role": "user", "content": "Explain NAT traversal in two sentences."}],
    stream=True,
)
for chunk in stream:
    print(chunk.choices[0].delta.content or "", end="")
```

Retrieval comes along for free — OpenAI clients let you set default headers, so a grounded
answer over your own collection is a two-line change to an existing app:

```python
client = OpenAI(
    base_url="http://your-coordinator:5080/v1",
    api_key="YOUR_API_KEY",
    default_headers={"X-InferHub-Retrieve": "my-collection"},
)
```

### Vision (v2.11+)

Send an image the way the OpenAI SDK does and it reaches whichever GPU in the fleet is holding
a vision model (`llava`, `llama3.2-vision`, `qwen2-vl`, `moondream`, …):

```python
client.chat.completions.create(
    model="llava",
    messages=[{"role": "user", "content": [
        {"type": "text", "text": "What is in this image?"},
        {"type": "image_url", "image_url": {"url": f"data:image/png;base64,{b64}"}},
    ]}],
)
```

**The image must be inlined as a `data:` URL.** A remote `http(s)` image URL is rejected with a
`400` that says why, and that is deliberate: fetching a caller-supplied URL would make the
coordinator issue outbound requests to arbitrary hosts (an SSRF surface) and would pull
third-party bytes through a hop whose whole design is to retain nothing. Every OpenAI SDK can
inline a file in one line; the coordinator is not going to be a fetcher.

Images pass through in flight, exactly like text — nothing is stored, nothing is logged, and
usage stays counts-only (an image contributes to the prompt tokens the node itself reports; we
do not measure pixels). Vision works through an OpenAI-backed node too (vLLM, LM Studio, a
hosted provider): the image is re-emitted as a data URL on the way upstream, with its media type
sniffed from the bytes rather than guessed.

A text-only model handed an image will refuse at the node, and that refusal is forwarded as-is —
there is no capability registry here, because Ollama is the source of truth for what a model
accepts.

**Where the translation is lossy — stated plainly rather than papered over:**

- `n > 1` is **rejected** with a `400`, not quietly served once.
- Tool calls are mapped in **both blocking and streaming** modes. A streamed call arrives as
  one `delta.tool_calls` frame with `index: 0` (Ollama emits the whole call at once, not
  incrementally — we do not fabricate OpenAI-style argument-fragment streaming it never
  produced), and the terminal frame resolves `finish_reason: tool_calls`. The full loop —
  streamed call → tool result → grounded answer — works with the OpenAI SDK's string-form
  arguments.
- `logprobs`, `logit_bias` and `user` are **accepted and ignored** (logged at debug).
- Audio and video content parts are **rejected**, not silently dropped — a model should
  never answer confidently about something it was never sent.

Errors on `/v1/*` use the OpenAI envelope (`{"error": {"message", "type", "param", "code"}}`),
because an SDK reads `error.message` and would otherwise surface a useless "unknown error".

## Inference backends

A node runs one inference backend behind the `IInferenceBackend` seam. The coordinator does
not know or care which — it hands the node an Ollama-shaped job and gets an Ollama-shaped
response back, whatever ran it.

| `Backend:Type` | Drives | Notes |
|---|---|---|
| `ollama` (default) | Ollama | One machine, one model at a time, minimal ceremony. |
| `openai` | **vLLM**, **llama.cpp server**, **LM Studio**, **TGI**, hosted providers | Anything speaking the OpenAI wire format. |

`openai` is one implementation covering all of them, because they all converged on the same
dialect. For anyone serving more than a couple of users off one GPU, vLLM's continuous
batching is the reason this exists.

```jsonc
// src/InferHub.Node/appsettings.json
{
  "Backend": { "Type": "openai" },
  "OpenAi": {
    "BaseUrl": "http://localhost:8000/v1",     // required; the node refuses to start without it
    "TimeoutSeconds": 300,
    "Models": {
      "Include": [ "meta-llama/Llama-3.1-8B-Instruct" ]
    }
  }
}
```

Set `OpenAi:ApiKey` through the environment (`OpenAi__ApiKey`) or user-secrets — never in
`appsettings.json`.

> **Against a hosted provider, `Models:Include` is effectively mandatory.** A hosted catalogue
> is hundreds of models the node cannot actually serve; report them all and the coordinator
> will happily route anything to it. vLLM and llama.cpp report only what they are serving, so
> there the allowlist is optional.

`Digest` and `SizeBytes` come back `null` — an OpenAI-compatible server reports a model name
and nothing else, and inventing values would be worse than admitting it. `/api/tags` and the
console render nulls as `—`.

### Cloud burst (v2.4+)

When the router finds no node for a model, the coordinator can forward the request to a
configured OpenAI-compatible upstream instead of returning `404`. A GPU box that is switched
off becomes degradation rather than an outage.

```jsonc
// src/InferHub.Coordinator/appsettings.json
{
  "Fallback": {
    "Enabled": true,
    "BaseUrl": "https://api.openai.com/v1",
    "Trigger": "no-node",                      // or "no-node-or-saturated"
    "ModelMap": { "llama3": "gpt-4o-mini" },   // ← the map is the consent
    "AllowedModels": []                        // empty = every mapped model
  }
}
```

> **⚠️ This feature can send a user's prompt to a third party.** Doing that by surprise, because
> someone's desktop was asleep, is a betrayal rather than a feature — so it is fenced in on
> purpose:
>
> - **Off by default.** `Fallback:Enabled` is `false`; an upgrade changes nothing.
> - **Mapped models only.** A model absent from `ModelMap` is never sent upstream, ever. There
>   is no wildcard.
> - **Always tagged.** Every fallback response carries `X-InferHub-Served-By: fallback`;
>   node-served responses carry `node`. Check the header, not your assumptions.
> - **Always counted.** `/api/status` and the status page report cloud burst *and its counter*
>   whether it is on or off, so "is this thing sending my prompts anywhere?" is answerable
>   without reading a config file.
> - **Never stored.** The coordinator forwards in flight and streams straight through. It
>   retains neither the prompt nor the answer — the same rule that has governed conversations
>   since 0.7.

Set `Fallback:ApiKey` via `Fallback__ApiKey` or user-secrets. With fallback disabled, a request
for a model no node holds returns exactly the `404` it always has.

### Named providers (v3.29+)

One upstream with no name works until you have two. `Providers:` is the same feature with an id on
each one — its own credential, its own models, its own trigger:

```jsonc
{
  "Providers": {
    "openai":     { "BaseUrl": "https://api.openai.com/v1",
                    "ModelMap": { "llama3": "gpt-4o-mini" } },
    "openrouter": { "Type": "openrouter",
                    "Trigger": "no-node-or-saturated",
                    "ModelMap": { "big-code": "qwen/qwen3-coder" } }
  }
}
```

Keys go in the environment: `Providers__openrouter__ApiKey`. Everything the warning above promises
still holds, and two rules are new:

- **One model, one provider.** Two enabled providers mapping the same model **fails startup**,
  naming the model and both providers. Picking the first is what most gateways do; it would make the
  most consequential choice here — whose servers see a prompt — depend on the order your JSON keys
  happened to bind in.
- **`Type` must be one this release knows** — `openai-compatible` (OpenAI, vLLM, LM Studio, TGI) or
  `openrouter`. A typo fails startup rather than silently disabling a provider.

#### OpenRouter (v3.30+)

`Type: "openrouter"` is the **same dialect** — OpenRouter speaks OpenAI's wire format, which is
exactly why it was the first provider added after the seam. What the type buys is the part that is
*not* the dialect:

- **You need not type the base URL.** `https://openrouter.ai/api/v1` is the default; set `BaseUrl`
  anyway if you reach it through a proxy of your own.
- **The model map is checked at startup** against OpenRouter's id shape — `vendor/model`, optionally
  `~`-prefixed for a floating alias (`~openai/gpt-mini-latest`) and `:variant`-suffixed (`:free`,
  `:batch`). `gpt-4o-mini` is a real OpenAI id and has never been an OpenRouter one; catching that
  at boot beats catching it as a `400` on the one request your fleet could not serve.
- **Attribution is yours to give.** `Referer` and `Title` become `HTTP-Referer` and
  `X-OpenRouter-Title`, which list an app on OpenRouter's **public** rankings. They are sent only if
  you set them — InferHub does not put your deployment on somebody's public page for you.

```jsonc
"openrouter": {
  "Type": "openrouter",
  "Referer": "https://mesh.example.com",   // optional, and absent by default
  "Title": "Example mesh",
  "ModelMap": { "big-code": "qwen/qwen3-coder" }
}
```

Token counts come back from OpenRouter's own `usage` block and land in the ledger like any other.
Its `cost` field is deliberately **not** read: a number InferHub did not measure does not belong in
the same column as ones it did. `openrouter/auto` works, and the hub cannot tell you which model
actually answered — it reports the name you asked for.

Responses served by a named provider carry `X-InferHub-Served-By: provider:<id>`; a deployment
configured through `Fallback:` still gets `fallback`, unchanged. `/api/status` grows a `providers`
array — reported whether or not anything has been dispatched, with `credential` reading
`configured` or `absent` and never a character of the key itself — and `/metrics` grows
`inferhub_provider_dispatched_total{provider}` beside the existing total, which still counts every
request the fleet did not serve.

> **A provider is still consulted only when the fleet cannot serve.** Routing a model to a provider
> *by preference*, while the fleet is up, is v3.33. What v3.29 changed is who "the upstream" is, not
> when it is asked.

## Authentication & configuration

InferHub keeps secrets out of source. Configure them at runtime via environment variables
or .NET user-secrets — `appsettings.json` only ships empty placeholders.

**Three independent token sets**

| Token set | Used by | Coordinator config key |
|---|---|---|
| Client API keys | Inference callers (`/api/generate`, `/api/chat`, `/api/tags`) | `Auth:ApiKeys` |
| Admin API keys | The management console & `/api/admin/*` (cordon/drain/deregister) | `Auth:AdminApiKeys` |
| Node enrollment secret | Worker nodes joining the SignalR hub | `Auth:NodeEnrollmentSecret` |

Client and admin scopes are checked separately — an admin key cannot run inference
unless it is also listed in `Auth:ApiKeys`, and vice versa.

### Clients, quotas & usage (v2.7+)

A flat key list is fine until a second party is behind one of the keys. `Auth:Clients` gives
a key an identity and, optionally, limits:

```jsonc
{
  "Auth": {
    "Clients": [
      {
        "Id": "acme-marketing",
        // Key via env: Auth__Clients__0__Key=sk-... — never in a config file.
        "Limits": {
          "MaxConcurrent": 4,
          "RequestsPerMinute": 60,
          "TokensPerMinute": 100000,
          "TokensPerDay": 2000000,
          // v3.10. Audio has no token count, so these are separate budgets — otherwise a client
          // whose only limit is TokensPerDay could transcribe a library for free.
          "AudioSecondsPerDay": 3600,
          "CharactersPerDay": 200000,
          // v3.14 and v3.27. Pictures and clips are metered on the same card, so a clip spends
          // MegapixelStepsPerDay too — and a clip is ~970 of them per five seconds, which is why
          // it also has a budget in the unit a human sizes.
          "MegapixelStepsPerDay": 200000,
          "VideoSecondsPerDay": 600,
          "AllowedModels": ["llama3", "nomic-embed-text"]
        }
      }
    ]
  }
}
```

Every limit is nullable; `null` means unlimited. The flat `Auth:ApiKeys` list keeps working —
its entries are anonymous clients with no limits, so **an existing config runs unchanged**.

What happens at the boundary, per status code:

| Situation | Response |
|---|---|
| Over `MaxConcurrent`, `RequestsPerMinute` or `TokensPerMinute` | `429` with a window-accurate `Retry-After` |
| Over `TokensPerDay`, `AudioSecondsPerDay`, `CharactersPerDay`, `MegapixelStepsPerDay` or `VideoSecondsPerDay` | `402 Payment Required`, `Retry-After` pointing at UTC midnight. Each unit has its own budget and consumes only its own. A video is metered in two units and **both** are checked (v3.27); the 402 names the one that ran out. |
| Model outside `AllowedModels` | `404`, byte-identical to a model that does not exist |
| Every capable node at its declared cap, longer than `Queue:MaxWaitSeconds` | `503` with `Retry-After` |

On `/v1` the rejections use the OpenAI error envelope (`rate_limit_error` /
`insufficient_quota`), so SDK retry logic does the right thing out of the box.

**Usage accounting.** Every completed request is metered per client and per model — requests,
prompt tokens, completion tokens, and whether it was served by [cloud burst](#cloud-burst-v24)
(the one that costs actual money). Embeddings and [document ingestion](#document-ingestion-v25)
count too: a client that ingests a 500-page manual has consumed the fleet. A usage record is
a client id, a model, a kind, some counts, a unit name, a flag and a timestamp. **It never contains
the prompt, the completion, the audio, the transcript, a hash of any of them, or a "sample" — and
there is no flag to change that.** Streaming counts come from the terminal chunk; a stream that
never delivers it (a mid-stream disconnect) records nothing rather than guessing.

Since v3.10 the unit is named, because audio has none of the old ones: `tokens`, `audio_seconds` or
`characters`. The token columns are untouched, so a row written by v3.9 still means what it meant,
and the aggregate reports each unit in its own column rather than summing seconds into tokens.

```bash
# Aggregates you could put on an invoice (admin scope):
curl -H "Authorization: Bearer $ADMIN_KEY" \
  "http://localhost:5080/api/admin/usage?from=2026-07-01T00:00:00Z&clientId=acme-marketing"

# Configured clients with live window consumption:
curl -H "Authorization: Bearer $ADMIN_KEY" http://localhost:5080/api/admin/clients
```

The console has a matching **Clients & usage** panel with a date range and CSV export.

By default the counters are in-memory and a restart resets them — honest, and useless for
billing. Set `Usage:Persistence=postgres` (with its own `Usage:Postgres:ConnectionString`,
deliberately independent of the vector store's) to write each record to an append-only table.

**Queueing.** When every node holding a model is at its declared `MaxConcurrency`, a request
waits in a bounded queue instead of failing instantly: up to `Queue:MaxWaitSeconds` (default
30), at most `Queue:MaxDepth` waiting (default 64), then `503`. Nodes that declared no cap
never queue. If cloud burst is enabled with `Trigger=no-node-or-saturated`, saturation
overflows to the upstream *instead of* queueing. Queue depth and median wait are on
`/api/status` and the status page.

### Multi-tenant collections (v2.13+)

A key with an identity and a budget still had the run of every RAG collection in the mesh.
For one owner that is fine; for an agency serving several end-clients out of one InferHub it
is a data-isolation gap. `Auth:Clients[].Collections` closes it:

```jsonc
{
  "Auth": {
    "Clients": [
      { "Id": "acme",     "Collections": ["acme-*", "shared-glossary"] },
      { "Id": "globex",   "Collections": ["globex-docs"] },
      { "Id": "internal" }                       // no list = every collection, as before
    ]
  }
}
```

Each entry is an exact collection name or a single trailing-`*` prefix — enough for
provisioning a tenant a namespace, and small enough that nobody has to reason about a glob
dialect on an isolation boundary. **A client with no `Collections` list may touch everything,
which is what every key could do before v2.13, so an existing config is unchanged.**

The scope is enforced on *every* path that names a collection — document ingest, list, get,
chunks, delete, `POST /api/collections/{c}/search`, the raw `/api/vector/{c}/*` data plane,
and the `X-InferHub-Retrieve` inline-RAG header on both `/api` and `/v1`.

**A collection outside your scope returns `404`, not `403`** — byte-identical to a collection
that does not exist, and the check runs before the store is consulted, so a name outside your
scope reads the same whether or not it exists. A tenant never learns another tenant's
collections are there.

**Provisioning is just ingesting.** A scoped client posting a document to a collection inside
its scope that doesn't exist yet creates it — dimension measured from the first embedded
batch, distance from `VectorStore:Distance`. There is no separate create ceremony and no
admin round trip per tenant. Unscoped clients keep the old behaviour: collections are an
admin's to create.

Admin keys stay fleet-wide, as they always have been. `GET /api/admin/vector/collections`
gains a `scopes` block naming which clients can reach each collection — the view that makes a
tenancy misconfiguration visible before a tenant finds it.

```bash
# acme's key, acme's corpus:
curl -H "Authorization: Bearer $ACME_KEY" \
  -F file=@handbook.pdf http://localhost:5080/api/collections/acme-hr/documents

# acme's key, globex's corpus — indistinguishable from a collection that isn't there:
curl -H "Authorization: Bearer $ACME_KEY" \
  http://localhost:5080/api/collections/globex-docs/documents
# 404 {"error":"collection 'globex-docs' does not exist"}
```

One vector store, one source of truth: this is an authorization filter over the single store,
not a store per tenant.

**Coordinator — client, admin & enrollment secrets**

```bash
# Linux / macOS
export Auth__ApiKeys__0="sk-client-token-1"
export Auth__ApiKeys__1="sk-client-token-2"
export Auth__AdminApiKeys__0="sk-admin-token"
export Auth__NodeEnrollmentSecret="shared-node-secret"

# Windows PowerShell
$env:Auth__ApiKeys__0 = "sk-client-token-1"
$env:Auth__AdminApiKeys__0 = "sk-admin-token"
$env:Auth__NodeEnrollmentSecret = "shared-node-secret"
```

Or with user-secrets (development):

```bash
dotnet user-secrets --project src/InferHub.Coordinator set "Auth:ApiKeys:0" "sk-client-token-1"
dotnet user-secrets --project src/InferHub.Coordinator set "Auth:AdminApiKeys:0" "sk-admin-token"
dotnet user-secrets --project src/InferHub.Coordinator set "Auth:NodeEnrollmentSecret" "shared-node-secret"
```

**Node — enrollment secret**

```bash
export Coordinator__EnrollmentSecret="shared-node-secret"
# or
dotnet user-secrets --project src/InferHub.Node set "Coordinator:EnrollmentSecret" "shared-node-secret"
```

**Loopback policy.** By default, requests originating from `127.0.0.1` / `::1` skip the
Bearer-token check — handy for local testing. Set `Auth__RequireAuthForLoopback=true` to
require a token even for loopback. The same switch applies to both inference and admin
routes. **Remote (non-loopback) requests always require a valid token**, regardless of
this setting.

**Open endpoints.** `/health` is unauthenticated so monitoring systems can poll it.

**Production.** Always run the coordinator behind HTTPS (a reverse proxy like Caddy /
nginx, or Kestrel TLS). Bearer tokens are sensitive — don't send them over plain HTTP.

### All configuration keys

Every coordinator setting lives in `appsettings.json` (or overridden via env vars / user
secrets). Defaults are listed below — sensible for a single-host deployment.

| Key | Default | Purpose |
|---|---|---|
| `Urls` | `http://localhost:5080` | Address the coordinator listens on. |
| `NodeRegistry:TimeoutSeconds` | `30` | Heartbeat-miss window before a node is evicted and its in-flight jobs are failed. |
| `NodeRegistry:ReaperIntervalSeconds` | `5` | How often the reaper sweeps for stale nodes. |
| `Dispatcher:TimeoutSeconds` | `300` | Per-job wall-clock timeout (streaming, blocking **and tool jobs, which means video**). A five-second `wan-t2v-1.3b` clip measured ~143 s of cold load + ~330 s of generation on a 3090 Ti, so **raise this to 1800 if you generate video** — at 300 the hub gives up while the model is still loading. |
| `Router:AffinitySlidingMinutes` | `10` | Sticky-conversation idle expiry. |
| `Router:AffinityLoadBreakThreshold` | `2` | Extra in-flight jobs the sticky node may have before affinity is broken in favour of a less-busy node. |
| `Router:Strategy` | `least-busy` | How capable nodes are ranked (v2.8): `least-busy` (default, unchanged) or `throughput` (measured tokens/sec, EWMA, load-adjusted). Affinity still wins. See [Fleet operations](#fleet-operations-v28). |
| `Affinity:Persistence` | `none` | Sticky-conversation persistence (v2.12): `none` (in-memory, reset on restart) or `file` (survives a coordinator restart). Affinity keys on the stable node id either way, so a node reconnecting keeps its warm conversations. |
| `Affinity:DataDirectory` | `./data/affinity` | Where the `file` store writes (append-log + periodic snapshot). Container image overrides to `/data/affinity`. |
| `Affinity:SnapshotEveryOps` | `256` | Ops appended before the log is compacted to a snapshot. |
| `Fleet:Profiles:Persistence` | `none` | Node-profile persistence (v3.11): `none` (profiles live as long as this coordinator), `file`, or `postgres` (what an HA pair wants — both hubs read one fleet configuration). A lost profile reverts every node to its own config. See [Configure the fleet](#configure-the-fleet-not-the-boxes-v311). |
| `Fleet:Profiles:DataDirectory` | `./data/profiles` | Where the `file` store writes. Container image overrides to `/data/profiles`. |
| `Fleet:Profiles:Postgres:ConnectionString` | — | Required when `Persistence=postgres`. Set via env or user-secrets, never `appsettings.json`. Its own connection string, deliberately not coupled to the vector store's. |
| `Auth:ApiKeys` | `[]` | Accepted client Bearer tokens (constant-time compared). Anonymous, unlimited. |
| `Auth:Clients` | `[]` | Named clients: `{Id, Key, Limits, Collections}`. See [Clients, quotas & usage](#clients-quotas--usage-v27). |
| `Auth:Clients[].Collections` | `null` | Collections this client may touch; exact names or a `prefix*`. `null` = all. See [Multi-tenant collections](#multi-tenant-collections-v213). |
| `Auth:AdminApiKeys` | `[]` | Accepted admin Bearer tokens guarding `/api/admin/*`. Separate from `ApiKeys`. |
| `Auth:RequireAuthForLoopback` | `false` | Force loopback callers to present a token too (applies to client and admin scopes). |
| `Auth:NodeEnrollmentSecret` | _(empty)_ | Shared secret nodes present when joining the hub. Empty disables enrollment. |
| `Fallback:Enabled` | `false` | Cloud burst. See the warning in [Cloud burst](#cloud-burst-v24) before turning this on. |
| `Fallback:BaseUrl` | _(empty)_ | OpenAI-compatible upstream to burst to. |
| `Fallback:ApiKey` | _(empty)_ | Bearer token for the upstream. Env / user-secrets only. |
| `Fallback:Trigger` | `no-node` | `no-node`, or `no-node-or-saturated` to also burst when every capable node is at its declared `MaxConcurrency`. |
| `Fallback:ModelMap` | `{}` | Local model name → upstream model name. **Only mapped models are ever sent upstream.** |
| `Fallback:AllowedModels` | `[]` | Narrower allowlist within the map. Empty = every mapped model. |
| `Fallback:TimeoutSeconds` | `300` | Per-request timeout against the upstream. |
| `Providers:<id>:Enabled` | `true` | Named cloud providers (v3.29). Set `false` to park one without deleting its map. |
| `Providers:<id>:Type` | `openai-compatible` | Or `openrouter` (v3.30). An unknown one fails startup, naming both. |
| `Providers:<id>:BaseUrl` | _(empty)_ | Required and absolute when the provider is enabled — except under `openrouter`, which supplies its own and still accepts an override. |
| `Providers:<id>:Referer` | _(empty)_ | `openrouter` only (v3.30). Sent as `HTTP-Referer`. It lists you on OpenRouter's public rankings, so it is never defaulted. |
| `Providers:<id>:Title` | _(empty)_ | `openrouter` only (v3.30). Sent as `X-OpenRouter-Title`. Same reasoning as `Referer`. |
| `Providers:<id>:ApiKey` | _(empty)_ | Env (`Providers__<id>__ApiKey`) / user-secrets only. |
| `Providers:<id>:Trigger` | `no-node` | Per provider, not per hub. Same two values as `Fallback:Trigger`. |
| `Providers:<id>:ModelMap` | `{}` | Local → upstream model name. **One model may be mapped by exactly one enabled provider; a second mapping fails startup.** |
| `Providers:<id>:AllowedModels` | `[]` | Narrower allowlist within that provider's map. |
| `Providers:<id>:TimeoutSeconds` | `300` | Per-request timeout against that provider. |
| `Metrics:OpenScrape` | `false` | Whether `GET /metrics` is reachable without an admin key. See [Prometheus `/metrics`](#prometheus-metrics-v210). |
| `Cluster:Enabled` | `false` | Warm-standby HA (v3.0). Off = byte-identical to v2.13. Requires `VectorStore:Provider=postgres`. See [High availability](#high-availability-v30). |
| `Cluster:InstanceId` | _(machine name)_ | Names this hub in the lease row, the logs, `/api/status` and `/metrics`. |
| `Cluster:ConnectionString` | _(empty)_ | Required when `Enabled`. Env / user-secrets only. Its own key, independent of the vector store's and the ledger's. |
| `Cluster:LeaseName` | `default` | Separates two unrelated meshes sharing one database. |
| `Cluster:LeaseTtlSeconds` | `15` | Worst-case failover delay, and the window inside which a partitioned old primary fences itself. |
| `Cluster:RenewIntervalSeconds` | `5` | Renew cadence. Must be ≤ a third of the TTL; startup fails otherwise. |
| `Queue:MaxWaitSeconds` | `30` | How long a request may wait for a saturated fleet before `503`. |
| `Queue:MaxDepth` | `64` | How many requests may wait at once. Past it, an immediate `503`. |
| `Images:MaxBatch` | `4` | v3.14. The absolute ceiling on `n`, whatever the size. A batch runs on **one** node. Bound by both hosts from the same section, so a request refused on a hub is refused identically on a solo node. |
| `Images:MaxResponseBytes` | `26214400` | v3.14. An upper-bound estimate (`w × h × 4 × n`) checked *before a step runs*, with the refusal naming the largest `n` that fits. **Clamped by `Tools:MaxAttachmentBytes`** at use, because that cap sizes the mesh's SignalR message limit and exceeding a SignalR limit tears a node's connection down rather than failing a message. Raising one without the other gets you no change, deliberately. **v3.25: it bounds a video too** — there is no `Videos:` section, because two keys for one wire ceiling are two numbers you could raise independently. A clip's size cannot be estimated from its geometry (that is an encoder's output), so what the edge checks instead is that the recipe offers the `(size, seconds)` pair you asked for. |
| `Images:SyncMaxWaitSeconds` | `120` | v3.15. How long `/v1/images/generations` waits for its own job before a `503` naming the async route. **The job keeps running** and the message carries its id — discarding a minute of GPU because an HTTP client got bored is your call, not the hub's. See [A job that takes two minutes](#a-job-that-takes-two-minutes-v315). |
| `Images:Jobs:RetentionSeconds` | `300` | v3.15. How long a finished job's record and image bytes survive, read or not. With `Images:Jobs:Persistence=none` (the default) nothing persists across a restart and **nothing touches disk**. |
| `Images:Jobs:Persistence` | `none` | v3.24. `none` (byte-identical to v3.23) or `file`. Design rule 4's **fourth** recorded exception, and the one that stores user content — so it is off until you turn it on. With `file`, a finished job survives a restart for the rest of its retention window and not one second longer (the window is applied *on load*); a job that was in flight comes back `failed` with reason `hub_restarted` and is **never resumed**, because nothing durable holds a prompt. No `postgres`: image bytes are not row data. An unrecognised value fails startup rather than falling back to `none`, which would silently drop every job on the next restart. |
| `Images:Jobs:DataDirectory` | `./data/images` | v3.24. Where `file` writes: one `{id}.json` record and one `{id}.{n}.bin` per image, unlinked the moment the API says the picture is gone. The images set `/data/images` under their existing `/data` volume. Per instance under `Cluster:Enabled` — a promoted standby does not hold the old primary's pictures. |
| `Images:Jobs:MaxRetainedBytes` | `536870912` | v3.15. Global ceiling on retained results, LRU-evicting **completed** ones and never an in-flight one. Enforced **on insert**, not on a timer — a timer means the ceiling is a suggestion for one sweep interval. An evicted job reads as `expired` with a reason, so arriving late is a `410` that says what happened rather than a `404` that looks like a bug. |
| `Images:Jobs:KeepAfterRead` | `false` | v3.15. Off: a delivered image is dropped immediately. On is the setting that makes this hub briefly an image cache, in those words. |
| `Images:MaxRequestBytes` | `26214400` | v3.18. What one **edit** may send in — the picture and the mask together — refused with a `413` at the edge before anything is buffered onward. A separate key from `MaxResponseBytes` because the two directions are separate risks: outbound is `n` renders of a size you declared, inbound is one upload somebody else chose the size of. Each part is *also* capped by `Tools:MaxAttachmentBytes`. |
| `Images:Jobs:MaxQueueDepth` | `32` | v3.15. How many image jobs may wait. FIFO, deliberately: shortest-job-first would let a stream of 4-step requests starve a 50-step one invisibly. Full is `503` + `Retry-After`, the same shape as every other limit here. |
| `Usage:Persistence` | `none` | `none` (in-memory, reset on restart) or `postgres` (append-only table). |
| `Usage:Postgres:ConnectionString` | _(empty)_ | Required when `Persistence=postgres`. Env / user-secrets only. Independent of the vector store's. |
| `Usage:Postgres:Schema` / `:Table` | `inferhub` / `usage_records` | Where the ledger lives. Created on first use. |

### Node configuration

Every node setting lives in `src/InferHub.Node/appsettings.json` and is validated at startup
— a bad value (non-URL coordinator, negative interval, `MaxConcurrency < 1`) stops the
process with a message naming the offending key. Env-var / user-secrets overrides work as
usual (`Coordinator__EnrollmentSecret`, `Node__Name`, etc.).

| Key | Default | Purpose |
|---|---|---|
| `Coordinator:Enabled` | `true` | v3.5. `false` runs the node with no mesh at all — no connection, no heartbeat, and **no required URL**. Off together with `LocalApi:Enabled` fails startup. See [Solo mode](#solo-mode--just-the-node-v35). |
| `Coordinator:Url` | `http://localhost:5080/` | Coordinator base URL (must be absolute http/https). Not required when `Coordinator:Enabled=false`. |
| `Coordinator:Endpoints` | `[]` | HA coordinator list (v3.0). Empty = just `Url`. The node walks the list on each failed connect; a standby refuses the handshake, so rotation is how it finds the leader. See [High availability](#high-availability-v30). |
| `Coordinator:EnrollmentSecret` | _(empty)_ | Shared secret matching the coordinator's `Auth:NodeEnrollmentSecret`. |
| `Coordinator:HeartbeatInterval` | `00:00:10` | How often the node pings the coordinator. |
| `Coordinator:ModelRefreshInterval` | `00:01:00` | How often the node re-reports its model list. |
| `Coordinator:RetryDelay` | `00:00:05` | Wait between reconnect attempts. |
| `Node:Name` | _(machine name)_ | Friendly node name shown in the status page. |
| `Node:DataDirectory` | `null` | Directory for writable node state (the `.inferhub-node-id` file). `null` = next to the executable (content root). Set to e.g. `C:\ProgramData\InferHub\Node` when running as a Windows service under a least-privilege account. |
| `Node:MaxConcurrency` | `null` | Advisory in-flight cap reported to the coordinator (null = unbounded). |
| `Node:Labels` | `{}` | Free-form key/value pairs surfaced on `GET /api/nodes`. |
| `Node:Models:Include` | `[]` | Whitelist of model names to advertise (empty = all). |
| `Node:Models:Exclude` | `[]` | Names dropped before reporting. |
| `Node:Capabilities:Disabled` | `[]` | v3.8. What this node is **not** routed for — `["chat"]` makes it an embeddings-only box. Subtractive only; disabling both `chat` and `embed` fails startup. See [What a node is for](#what-a-node-is-for-v38). |
| `Node:Vram:BudgetMiB` | `0` | v3.16. Total VRAM to plan around, in MiB. **0 = no gate**, which is v3.15's behaviour exactly. **Declared, not detected**: under WSL2 there are no `/dev/nvidia*` nodes and the host's `nvidia-smi` cannot see the VM's VRAM, so a node that guessed would guess wrong on the most common GPU-with-Docker setup there is. A recipe that cannot fit `Budget − Reserve` is not declared; one that would fit but does not right now waits and then gets a `503` + `Retry-After`. |
| `Node:Vram:ReserveMiB` | `2048` | v3.16. Held back for the inference backend and the display — really about the **second** thing on the card, an `:ollama` container beside the `:diffusion` one. A reserve at or above the budget fails startup: that is not strict, it is a configuration that can never admit anything. |
| `Backend:Type` | `ollama` | Inference backend selector: `ollama` or `openai`. See [Inference backends](#inference-backends). |
| `Ollama:Endpoint` | `http://localhost:11434/` | Local Ollama URL (absolute http/https). Used when `Backend:Type=ollama`. |
| `Ollama:RequestTimeout` | `00:05:00` | Timeout for a single Ollama call. Matches the coordinator's `Dispatcher:TimeoutSeconds`; raise it for very large models whose cold load is slow. |
| `Ollama:Supervisor:*` | _(off)_ | Keeps the local Ollama alive (v3.4). See [Keeping the local Ollama alive](#keeping-the-local-ollama-alive-v34) for the full table. |
| `LocalApi:Enabled` | `false` | v3.5. Serve the hub's client-facing API from this node. See [Solo mode](#solo-mode--just-the-node-v35). |
| `LocalApi:Urls` | `http://localhost:5081` | Where the local API listens. Not 5080, so a hub and a node can share a laptop. |
| `LocalApi:ApiKeys` | `[]` | Bearer tokens for the local API. **Required** on a non-loopback address unless `AllowAnonymous`. |
| `LocalApi:AllowAnonymous` | `false` | Explicit consent to serve a non-loopback address with no keys. Warns on every boot. |
| `LocalApi:RequireAuthForLoopback` | `false` | Same meaning as the coordinator's key of that name. |
| `LocalApi:MaxWaitSeconds` | `30` | How long a request waits for a concurrency slot before `503`. Only bites when `Node:MaxConcurrency` is set. |
| `LocalApi:Retrieval:Enabled` | `false` | v3.6. RAG configured **on this node, by this node**. **Requires `Coordinator:Enabled=false`; both on fails startup** — that refusal is unchanged in v3.12, which grants a meshed node a corpus only through a profile the hub recorded. See [Retrieval on a standalone node](#retrieval-on-a-standalone-node-v36) and [A corpus on every node](#a-corpus-on-every-node-v312). |
| `LocalApi:Retrieval:DataDirectory` | `./data/retrieval` | Where the corpus lives, for the `local` provider. `/data/retrieval` in the image — mount a volume or it is ephemeral. **No profile can set this**: where bytes land on a box is the operator's. |
| `LocalApi:Retrieval:Provider` | `local` | v3.12. `local` (this box's disk) or `qdrant`. `postgres` is refused **by name** with the reason — `Npgsql` is coordinator-scoped (design rule 5). |
| `LocalApi:Retrieval:Url` | _(empty)_ | v3.12. Where the external engine is. Required for `qdrant` unless a profile supplies one. |
| `LocalApi:Retrieval:CredentialRef` | _(empty)_ | v3.12. Which entry of `Credentials` to authenticate the engine with. |
| `LocalApi:Retrieval:Credentials:{name}` | — | v3.12. Credential name → secret, on **this** box (env: `LocalApi__Retrieval__Credentials__sofia-qdrant`). A coordinator profile can *name* one of these; it can never add one, and a name this node does not have is a refusal rather than an unauthenticated connection. |
| `LocalApi:Retrieval:Qdrant:*` | — | v3.12. The hub's `VectorStore:Qdrant:*` keys (`CollectionPrefix`, `TimeoutSeconds`, `HnswM`, `HnswEfConstruct`, `Quantization`, `OnDisk`, `PayloadIndexKeys`, …), same names and meanings — the node runs the same connector, moved into the shared library rather than rewritten. |
| `LocalApi:Retrieval:Distance` | `cosine` | `cosine`, `dot` or `l2`, for collections this node creates. |
| `LocalApi:Retrieval:DefaultEmbeddingModel` | `nomic-embed-text` | Resolved against this node's own backend. |
| `LocalApi:Retrieval:Retrieval:*` | — | The phase-24 retrieval keys (`DefaultK`, `MaxRecords`, `OnMissing`, `Mode`, `CandidatesPerBranch`, `Rerank`, `RerankModel`, `RerankCandidates`, `RerankTimeoutSeconds`, `Template`), same names, meanings and defaults as the hub's `VectorStore:Retrieval:*`. |
| `LocalApi:Retrieval:Ingestion:*` | — | The phase-23 ingestion keys (`MaxChars`, `OverlapChars`, `MaxDocumentBytes`, `EmbeddingBatchSize`, `EmbeddingModel`, `MaxRetriesPerBatch`), same as the hub's `Ingestion:*`. |
| `OpenAi:BaseUrl` | _(empty)_ | Upstream OpenAI-compatible server, e.g. `http://localhost:8000/v1`. **Required when `Backend:Type=openai`** — the node refuses to start without it rather than booting and 500ing on every job. |
| `OpenAi:ApiKey` | _(empty)_ | Bearer token for the upstream. Env (`OpenAi__ApiKey`) or user-secrets only. |
| `OpenAi:TimeoutSeconds` | `300` | Timeout for a single upstream call. Same reasoning as `Ollama:RequestTimeout`. |
| `OpenAi:Models:Include` | `[]` | Allowlist of upstream models to advertise. Effectively mandatory against a hosted provider. |
| `OpenAi:Models:Exclude` | `[]` | Names dropped before reporting. |
| `Tools:Enabled` | `false` | v3.9. Consents to the tool runtime existing. See [Tools on a node](#tools-on-a-node-v39). |
| `Tools:Allowed` | `[]` | v3.9. Manifest ids that may actually run — the second consent, and the ceiling a coordinator can never raise. A manifest not named here is loaded, logged and never started. Naming tools while `Tools:Enabled` is false **fails startup**. |
| `Tools:ManifestDirectory` | `tools` | Where `*.json` manifests are read from. A manifest that fails to load is logged and skipped, never fatal. |
| `Tools:ScratchDirectory` | `data/tools/scratch` | Per-request working directories, deleted in a `finally` — after success and after failure. `/data/tools/scratch` in the images. |
| `Tools:MaxAttachmentBytes` | `26214400` | 25 MB, matching the OpenAI audio API. Over it is a `413` at the edge naming the limit. Also read by the **coordinator** for its own edge. This is the *buffered* ceiling — the bytes arrive on the job. |
| `Tools:MaxStreamedBytes` | `0` (off) | v3.21. The ceiling for an upload that **streams through** the hub instead of being buffered in it. Must be `0` or at least `MaxAttachmentBytes`. Setting it on a node is what makes that node declare `SupportsStreamedAttachments`, which is what the hub filters on before sending it a streamed job. See [Uploads larger than 25 MB](#uploads-larger-than-25-mb-v321) for the trade — no failover, and fields before the file. |
| `Tools:StreamChunkBytes` | `65536` | v3.21. Coordinator-side. How much of a streamed attachment travels in one frame. |
| `Tools:QueueMaxWaitSeconds` | `30` | How long a request waits for a free worker before `503` + `Retry-After`. |
| `Tools:CancelGraceSeconds` | `20` | v3.15. How long a worker gets to honour a `cancel` frame before it is terminated and restarted. Killing it immediately would be simpler and is wrong: a diffusion worker holds weights that took tens of seconds to load, so killing it to abandon **one** job punishes the **next** caller, and it gets worse with every model your catalogue gains. See [A job that takes two minutes](#a-job-that-takes-two-minutes-v315). |
| `Tools:AllowModelDownload` | `false` | v3.10. May a worker fetch weights it does not have? The **third** consent — `Enabled` is the feature, `Allowed` is these tools, this is reaching the internet from a box you may have air-gapped. `true` in the `:tools` image. With it off, a worker that needs missing weights fails the **job** naming this key and the pre-fetch command. |
| `Tools:MaxStartAttempts` | `3` | Start attempts per `RestartWindow` before a tool's pool gives up, withdraws its capabilities and drops to probing. |
| `Tools:RestartWindow` | `00:10:00` | The budget's window. |
| `Tools:RestartBackoff` | `00:00:10` | Wait before the second and later attempts; doubles each time. |
| `Tools:RecoveryProbeInterval` | `00:01:00` | How often a pool that gave up tries one worker anyway. A success restores its capabilities without a restart. |
| `Tools:MaintenanceInterval` | `00:00:30` | How often idle workers are retired, given-up pools are probed and idle workers are hinted to free what they hold. |
| `Tools:Image:RequireGpu` | `true` | v3.14. The image worker refuses to start with no reachable CUDA device, and names the key to unset. A tool that loads happily on a CPU and then serves four-minute requests is a node the fleet keeps routing to. |
| `Tools:Image:AllowSlowCpu` | `false` | v3.14. Offer the CPU-hostile recipes on a CPU-only box anyway. Your hardware, your call — loud when on. |
| `Tools:Image:RecipeDirectory` | _(worker default)_ | v3.14. Where `python/recipes/*.json` live; `/opt/inferhub/recipes` in the `:diffusion` image. Since v3.16 the **node** reads it too, for three fields only — id, licence, VRAM — because the profile clamp must refuse an oversized or unlicensed recipe with no worker running. |
| `Tools:Image:AcceptedLicenses` | `[]` | v3.16. Licence ids you have read and accepted. A recipe whose `license.permissive` is not `true` is loaded, logged by name and **not started** until its id is here — `sd35-medium` needs `stabilityai-ai-community`, `sdxl-turbo` needs `sai-nc-community`. The **fourth** consent, and a list rather than a boolean so accepting one licence never enables another. A blank entry is ignored, which is how you clear one that came from an image. Not legal advice — a refusal to make that call for you, silently. |
| `Tools:Image:SeamWarnThreshold` | `0.08` | v3.17. Above this, an equirectangular result carries a `seam` warning — the mean absolute difference between its first and last columns, 0–1. A warning and never a failure. `0` silences the warning, not the measurement. It decides whether to *warn* and nothing else: it never triggers a repair. |
| `Tools:Image:SeamRepair` | `off` | v3.23. Which seam repairs a caller may **ask** for here: `off`, `blend`, `diffuse`, `any`. The ceiling `X-InferHub-Image-Seam-Repair` chooses within — nothing repairs by default. `blend` is numpy and costs no steps; `diffuse` is an inpainting pass and costs `int(steps × 0.4)` of them. `diffuse` does not imply `blend`; `any` is both. |
| `Tools:Image:ResidentRecipes` | `1` | v3.16. How many models may be on the card at once. Switching recipes swaps weights inside the **warm** process; more than one resident stops a box that alternates from thrashing. The default is 1 because the expensive default is the one nobody realises they chose. |

### Keeping the local Ollama alive (v3.4+)

Ollama is a young server moving quickly, and sometimes it wedges: the process is alive, the
port still accepts connections, and nothing ever comes back. Until v3.4 that took the whole
node out of the fleet and left it there — connected, heartbeating, reporting no models,
waiting for somebody to notice. The node now notices instead.

```jsonc
"Ollama": {
  "Endpoint": "http://localhost:11434/",
  "Supervisor": {
    "Enabled": true,
    "AutoInstall": false
  }
}
```

It probes `GET /api/version` every `ProbeInterval`, and after `UnhealthyThreshold`
**consecutive** failures it acts. One slow answer is not a fault; a machine that reacts to a
single missed probe spends its life reacting.

| State | How it looks | What it means | What the node does |
|---|---|---|---|
| healthy | answered inside `ProbeTimeout` | fine | nothing |
| unreachable | the socket never opened | not running | **start** it |
| wedged | the socket opened, nothing came back (or a 5xx) | running but stuck | **stop**, then start |

Collapsing the last two is the bug this distinction exists to avoid: `start` on a wedged
process fails on a port that is already bound, and the log then confidently reports a restart
that never happened.

**Restarts are budgeted.** `MaxRestartAttempts` (3) inside `RestartWindow` (10 min), with
`RestartBackoff` (10s, doubling) between them, and a wait of up to `ReadyTimeout` (2 min) for
the restarted server to answer — a service that starts by loading a model is slow, not broken.
Past the budget the node **stops restarting**, logs it once at Error, and **keeps probing**, so
a recovery (a human fixing the driver, the machine finishing whatever it was choking on) is
still noticed and the node re-reports its models on its own. A supervisor that restarts a
server every fifteen seconds never lets a model finish loading, which replaces a fixable outage
with an unfixable one.

**Loopback only.** If `Ollama:Endpoint` is not loopback, or `Backend:Type=openai`, the
supervisor logs one line at startup naming why and never probes again. A shared Ollama serving
four nodes, restarted because *one* node's network hiccuped past `ProbeTimeout`, is a four-node
outage caused by the node with the worst link — and an OpenAI-compatible upstream (vLLM, a
hosted provider) is not ours to bounce at all. The same rule covers containers for free: a node
image cannot restart an Ollama on its host, and its endpoint is by definition not loopback.

**Auto-install is a second, separate opt-in.** `Enabled` consents to restarting a process; it
does not consent to downloading and running an installer. `AutoInstall` fires only when
discovery finds neither a service nor a binary — "not installed", never "not answering" —
**once per process lifetime**, with the exact command written to the log before it runs.
`InstallUrl` points an air-gapped or policy-managed fleet at its own mirror instead of us
reaching the internet from their GPU box.

> **A restart kills whatever was streaming through that node**, and there is no way around it.
> Waiting for the work to drain first would be worse: a single stuck request would pin the node
> in a broken state indefinitely, which is the exact failure this feature exists to end. By the
> time a restart happens, Ollama has not answered a trivial version check in three quarters of
> a minute — that stream was not going to finish. The log line says how many requests were in
> flight, so the cost is recorded rather than hidden.

While the backend is broken the node keeps reporting **zero** models. That is what stops the
coordinator routing inference at it, and it is deliberate: preserving the last known good list
would turn a node-local fault into client-visible timeouts. The report now says *why* it is
empty, so "no models" no longer reads the same as "this box has nothing installed".

| Key | Default | Purpose |
|---|---|---|
| `Ollama:Supervisor:Enabled` | `false` | Turns supervision on. Loopback + `Backend:Type=ollama` only. |
| `Ollama:Supervisor:ProbeInterval` | `00:00:15` | How often to probe. |
| `Ollama:Supervisor:ProbeTimeout` | `00:00:05` | The probe's own deadline. Deliberately **not** `Ollama:RequestTimeout` — that one waits five minutes for a cold 70B load, and probing over it would take a quarter of an hour to notice a wedge. |
| `Ollama:Supervisor:UnhealthyThreshold` | `3` | Consecutive failures before acting. Any success resets the count. |
| `Ollama:Supervisor:ReadyTimeout` | `00:02:00` | How long a restarted Ollama gets to answer. |
| `Ollama:Supervisor:MaxRestartAttempts` | `3` | Restarts allowed per window. |
| `Ollama:Supervisor:RestartWindow` | `00:10:00` | The window the budget applies over. |
| `Ollama:Supervisor:RestartBackoff` | `00:00:10` | Wait before the second and later attempts; doubles each time. |
| `Ollama:Supervisor:AutoInstall` | `false` | Install Ollama when it is genuinely absent. A separate consent. |
| `Ollama:Supervisor:InstallUrl` | _(official)_ | Mirror to install from. |
| `Ollama:Supervisor:ServiceName` | _(discover)_ | Override the discovered service (`Ollama` / `ollama.service`). |
| `Ollama:Supervisor:ExecutablePath` | _(discover)_ | Override the `ollama` binary found on `PATH`. |

A service manager always wins over spawning: if the `Ollama` Windows service or an
`ollama.service` systemd unit exists, the node restarts it through `sc.exe` / `systemctl`
rather than running `ollama serve` itself — two servers fighting over `:11434` is a worse
outage than the one being fixed. A node running under a **restricted account cannot control a
machine-wide service**; that is reported as one line naming the privilege rather than a stack
trace. See [deploy/windows/README.md](deploy/windows/README.md).

### Running a node as a Windows service

For an always-on GPU box, run the node as a native Windows service — auto-start on boot,
restart-on-failure recovery, and logging to the Windows Event Log. The service host
(`src/InferHub.Node.WindowsService`) is a thin wrapper that composes the exact same node
services through the shared `AddInferHubNode` root, so it behaves identically to
`dotnet run --project src/InferHub.Node`; only the packaging differs. Dev/console and
Linux node paths are unchanged.

```powershell
# 1. Publish a self-contained single-file host (no .NET runtime needed on the box)
dotnet publish src/InferHub.Node.WindowsService -c Release -r win-x64

# 2. Copy the publish output to C:\Program Files\InferHub\Node, then set the coordinator
#    URL in appsettings.json and the enrollment secret as a machine env var
[Environment]::SetEnvironmentVariable('Coordinator__EnrollmentSecret','shared-node-secret','Machine')

# 3. Install + start the service (run elevated)
./deploy/windows/install-service.ps1 `
  -BinaryPath "C:\Program Files\InferHub\Node\InferHub.Node.Service.exe" -DelayedStart
```

The install script configures automatic (or delayed-auto) start, restart-on-failure
recovery, and a writable data directory (`Node:DataDirectory`, default
`C:\ProgramData\InferHub\Node`) so the node identity file survives under a least-privilege
account. Full runbook — including update/uninstall and virtual-account setup — is in
[deploy/windows/README.md](deploy/windows/README.md).

> The Linux equivalent is the same host pattern with `builder.Services.AddSystemd()` and a
> `.service` unit file — same composition root, different lifetime integration.

## Docker

```bash
cp deploy/docker/.env.example deploy/docker/.env    # set three keys
docker compose -f deploy/docker/docker-compose.yml up -d
```

Published images, built for `linux/amd64` and `linux/arm64` on every `v*` tag, running as a
non-root `app` user:

```
ghcr.io/dev-art-solutions/inferhub-coordinator:2.3.0   (also :2.3, :latest)
ghcr.io/dev-art-solutions/inferhub-node:2.3.0
```

> **⚠ In Docker there is no loopback exemption.** InferHub skips authentication for loopback
> callers, which is why the from-source quickstart lets you `curl localhost` with no key.
> Inside Docker your requests are *not* loopback — they arrive over the bridge network from
> outside the container — so the compose stack **requires real API keys**. That is the safer
> default and we left it alone, but it surprises people coming from bare metal.

The GPU nodes usually want to stay off Docker: they live next to a local Ollama and the node
process is happier native there (that's what the Windows-service host is for). A
containerized coordinator with native nodes dialing out to it is the shape most deployments
end up in. A Postgres overlay (`deploy/docker/compose.postgres.yml`) swaps the vector store
to pgvector. Full runbook: [deploy/docker/README.md](deploy/docker/README.md).

## Vector store

InferHub's vector store is **provider-backed** and off by default — flip
`VectorStore:Enabled` to turn it on, and pick a backend with `VectorStore:Provider`:

- **`local`** (default) — embedded and file-backed in the coordinator, replicated to the GPU
  fleet and self-healing. Zero external services; plain-file backups.
- **`postgres`** (v2.2+) — an external **PostgreSQL + pgvector** database: HNSW-indexed ANN
  search, real transactions, ordinary database backups, and shared access from other apps.
  See [PostgreSQL + pgvector](#postgresql--pgvector-v22) below.
- **`qdrant`** (v3.1+) — an external **Qdrant** database, over its REST API. HNSW search, payload
  filtering, and Qdrant's own snapshots/backups. See [Qdrant](#qdrant-v31) below.

Every endpoint, header, and client call is identical across providers. Embeddings and inline
retrieval always run on the fleet — only the storage engine changes.

### Local provider

The local store is embedded and file-backed. Two layers:

- **Raw store** — an append-only op log (upserts + tombstones) plus periodic compacted
  snapshots, one directory per collection under `VectorStore:DataDirectory`. Plain files
  an operator can copy. This is the **source of truth**.
- **Index** — a queryable structure built from the raw store. In-memory on the hub;
  replicated to node holders (see below). Rebuildable at any time from the raw store.

**Replication & self-healing.** When more than one node is online, each collection is
replicated across `VectorStore:ReplicationFactor` holders (capped at the connected node
count). The coordinator pushes the initial snapshot and forwards subsequent ops over the
existing SignalR link. If a holder drops, the healing loop re-pushes from the raw store to
restore the factor; if the **last** holder drops, the hub-local index keeps answering
reads and the next eligible node is seeded from raw. Node replicas are derived and
disposable — the hub's raw store is the durability anchor.

**Where work happens.** Coordinator orchestrates (owns the raw store, places replicas,
routes queries, heals); nodes compute (embedding + generation on the GPU). Vector search
runs hub-local by default, or on a node replica when one exists.

**Data-plane endpoints** (client scope, `Auth:ApiKeys`):

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/vector/{collection}/upsert` | Upsert a record. Accepts a raw `vector` or a `text` field (embedded on a node). |
| `POST` | `/api/vector/{collection}/query` | Top-k search. Same body shape as upsert — raw `vector` or `text`. |
| `POST` | `/api/vector/{collection}/retrieve` | Convenience RAG read (text → embed → search → matches). |
| `GET`  | `/api/vector/{collection}/{id}` | Fetch a single record. |
| `DELETE` | `/api/vector/{collection}/{id}` | Tombstone a record. |
| `POST` | `/api/embed` (+ `/api/embeddings`) | Drop-in Ollama-shaped embeddings endpoint (independent of the store). |

**Admin-plane endpoints** (admin scope, `Auth:AdminApiKeys`, audited):

| Method | Path | Purpose |
|---|---|---|
| `GET`  | `/api/admin/vector/collections` | List collections + placement (holders per collection). |
| `GET`  | `/api/admin/vector/collections/{collection}` | Detail: collection info, placement, under-replicated flag, per-collection query stats. |
| `POST` | `/api/admin/vector/collections` | Create a collection (`{ "name", "dimension", "distance"? }`). |
| `DELETE` | `/api/admin/vector/collections/{collection}` | Drop a collection. |
| `POST` | `/api/admin/vector/collections/{collection}/rebuild` | Force a heal pass — re-push from the raw store to restore the factor. Returns `409` under the postgres provider (nothing to re-push). |

**Live events.** `GET /api/admin/stream` (the same SSE stream used by the console) now
carries vector lifecycle events alongside node snapshots: `vector.collection.created`,
`vector.collection.dropped`, `vector.replica.assigned`, `vector.replica.lost`,
`vector.heal.started`, `vector.heal.completed`. Each event carries a monotonic
`sequence` and a `data` blob (holder connection id, node id, reason, before/after counts,
etc.). The management console renders these in the "Vector activity" feed.

**Status JSON.** `GET /api/status` grows a `vector` block when the store is enabled — a
`provider` tag (`local` \| `postgres`) plus per-collection record count, dimension, distance,
target vs live replicas, holder node ids, and an `underReplicated` flag. Metrics gains
`vectorReplicasHealed` / `vectorRebuildsFromRaw` / `vectorUnderReplicated` counters plus
`perCollection` query stats (`queries`, `queryLatencyAvgMs`). Under postgres the replica
fields are zeroed and the three heal/replica counters stay flat — there are no node replicas
to count.

### PostgreSQL + pgvector (v2.2+)

Set `VectorStore:Provider` to `postgres` and point the coordinator at a PostgreSQL that has
the [pgvector](https://github.com/pgvector/pgvector) extension. **Pick it when** you already
run Postgres, you want ANN search + transactions + ordinary backups + other apps reading the
same table, or the dataset has outgrown flat-exact search. **Stay local when** you want zero
external services, plain-file backups, and search replicas living on the GPU fleet.

**Schema.** One registry table (`{schema}.collections`) plus one table per collection,
`{schema}.{prefix}{collection}`, with `id text`, `embedding vector(N)`, `payload jsonb`,
`metadata jsonb`, `seq_no bigint`, `updated_at`. A pgvector ANN index (HNSW by default) is
built per the collection's distance metric, and a GIN index backs metadata filters. Score
sign-conventions match the local provider exactly (`cosine`/`dot` higher-is-better,
`l2` lower-is-better), so clients see identical rankings and numbers.

**Honest trade-offs.** Postgres owns durability, so under this provider:

- **no node replication, no self-healing, no node-served vector reads** — search runs in
  Postgres, and the coordinator holds no vector state on disk;
- the **rebuild** admin endpoint returns `409` (nothing to re-push);
- the `vectorReplicasHealed` / `vectorRebuildsFromRaw` / `vectorUnderReplicated` metrics stay
  at zero, and the status `vector` block zeroes the replica fields;
- pgvector's ANN index tops out at **2000 dimensions** — above that, the collection still
  works but falls back to exact scan (logged at creation).

The mesh is intact: **embeddings and inline retrieval still run on the GPU nodes** — only the
storage engine changed.

**Moving an existing deployment here** — see [Migrating between vector
providers](#migrating-between-vector-providers-v33): `inferhub-migrate` copies a populated `local`
or `qdrant` store into Postgres without re-ingesting the original documents.

**Walk-through** (compose stack in [`deploy/postgres/`](deploy/postgres/docker-compose.yml)):

```bash
# 1. A Postgres with pgvector
docker compose -f deploy/postgres/docker-compose.yml up -d

# 2. Point the coordinator at it (env, not appsettings.json) and enable postgres
export VectorStore__Enabled=true
export VectorStore__Provider=postgres
export VectorStore__Postgres__ConnectionString="Host=localhost;Database=inferhub;Username=inferhub;Password=inferhub"
dotnet run --project src/InferHub.Coordinator

# 3. Same API as ever — create, upsert, query
curl -X POST http://localhost:5080/api/admin/vector/collections \
  -d '{"name":"docs","dimension":3,"distance":"cosine"}'
curl -X POST http://localhost:5080/api/vector/docs/upsert \
  -d '{"id":"a","vector":[1,0,0],"metadata":{"lang":"en"}}'
curl -X POST http://localhost:5080/api/vector/docs/query \
  -d '{"vector":[1,0,0],"k":3}'
```

### Qdrant (v3.1+)

Set `VectorStore:Provider` to `qdrant` and point the coordinator at a [Qdrant](https://qdrant.tech).
**Pick it when** you already run Qdrant or want its payload model, filtering and quantization
roadmap. The connector speaks Qdrant's **REST API by hand** — no client package, no gRPC — so, unlike
the pgvector provider, Qdrant adds **zero new dependencies** to InferHub.

**How records map.** Qdrant accepts only an integer or a UUID as a point id, and InferHub ids are
neither (a chunk id is a SHA-256, a document id might be a filename). So each id becomes a
deterministic `UUIDv5` point id, and the real id — with the payload and metadata — is stored in the
point payload. Because the UUID is deterministic, re-ingesting a document still **replaces** its
chunks rather than duplicating them. Nothing you send or read back ever exposes the UUID. Score
sign-conventions match the local provider exactly, proven by a parity test that runs one dataset
through all three engines.

**Honest trade-offs.** Qdrant owns durability, so like Postgres:

- **no node replication, no self-healing, no node-served vector reads** — search runs in Qdrant;
- the **rebuild** admin endpoint returns `409`; the replica metrics stay at zero and the status
  `vector` block reports `"provider":"qdrant"` with zeroed replica fields.

The mesh is intact: **embeddings and inline retrieval still run on the GPU nodes.**

**Hybrid search runs inside Qdrant (v3.2+).** A collection created on 3.2 or later carries a named
dense vector **and** a sparse (lexical) vector, so a `hybrid` retrieval is one Query API round trip
that Qdrant fuses by reciprocal rank fusion — a dense embedding and an exact-term branch ranked at
once, in the engine, instead of the hub stitching two searches together. The sparse vector is
computed on the coordinator from the same tokenizer the local BM25 index uses (so `local` and
`qdrant` agree on the lexical view of a chunk) and is IDF-weighted by Qdrant server-side, so it still
adds **no dependency**. `keyword` mode is now a real sparse-vector search too, not the coarse
phase-33 filter. Default retrieval is still `vector`, so a deployment that sends no headers is
unchanged. A collection created on **3.1 stays dense-only** — it keeps answering vector queries after
you upgrade, and its keyword search stays coarse until it is re-created or re-populated through
`inferhub-migrate` (v3.3), which creates the target collection in the current shape.

**Production knobs (v3.3+).** A Qdrant that is more than a demo wants three things a default
collection does not have, and they are all `VectorStore:Qdrant:` settings applied when a collection
is created:

- **Quantization** — `scalar` stores vectors as int8 (roughly 4× less vector memory), `binary` as one
  bit per dimension (roughly 32×, and materially lossy). This is a **memory-for-recall trade, not a
  free win**: quantized vectors rank approximately. Measure the loss on your own corpus with the
  [eval harness](tools/InferHub.Eval) before deciding it is acceptable — that is what it is for.
- **On-disk vectors** — `OnDisk=true` keeps dense vectors on disk instead of in RAM. For a collection
  larger than the memory you will give it, this is the difference between running and not; the cost
  is disk reads on the search path. Pairs naturally with quantization, whose small vectors stay
  resident.
- **Payload indexing** — `PayloadIndexKeys` (default `["documentId"]`) builds a Qdrant payload index
  per metadata key at collection creation. Ingestion's document scans and filtered deletes are all
  payload filters, and an unindexed payload filter is a full scan: cheap on a demo collection, the
  difference between a second and a minute on a real one.

Existing collections are untouched by any of these — they apply at creation, so re-create or migrate
a collection to adopt them.

**Remote Qdrant needs a key, and InferHub says so.** Qdrant ships unauthenticated, which is fine on
localhost and a data leak anywhere else: anything that can reach the port can read and delete your
vectors *and the chunk text stored with them*. Point the coordinator at a non-loopback `Url` with no
`ApiKey` and it **warns at startup** with that sentence. It is a warning and not a refusal on
purpose — a private network with its own controls is a legitimate deployment, and refusing to boot
would be us overruling an operator about their own network. TLS is just an `https://` `Url`.

**Walk-through** (compose stack in [`deploy/qdrant/`](deploy/qdrant/docker-compose.yml)):

```bash
# 1. A Qdrant
docker compose -f deploy/qdrant/docker-compose.yml up -d

# 2. Point the coordinator at it (ApiKey via env, never appsettings.json)
export VectorStore__Enabled=true
export VectorStore__Provider=qdrant
export VectorStore__Qdrant__Url="http://localhost:6333"
dotnet run --project src/InferHub.Coordinator

# 3. Same API as ever — create, upsert, query
curl -X POST http://localhost:5080/api/admin/vector/collections \
  -d '{"name":"docs","dimension":3,"distance":"cosine"}'
curl -X POST http://localhost:5080/api/vector/docs/upsert \
  -d '{"id":"a","vector":[1,0,0],"metadata":{"lang":"en"}}'
curl -X POST http://localhost:5080/api/vector/docs/query \
  -d '{"vector":[1,0,0],"k":3}'
```

### Migrating between vector providers (v3.3+)

Every release since the store became pluggable carried the same caveat: switching providers on a
populated deployment meant re-ingesting from the original documents — awkward advice from a system
that deliberately **does not keep** your original documents. [`inferhub-migrate`](tools/InferHub.Migrate)
deletes that caveat. It reads every chunk *and vector* out of one provider and writes them into
another: `local` ↔ `postgres` ↔ `qdrant`, any pair, either direction.

```bash
# Dry run first: what would be copied, and where.
dotnet run --project tools/InferHub.Migrate -- \
  --from local:./data/vectors --to qdrant:http://localhost:6333 --dry-run

# Then for real.
dotnet run --project tools/InferHub.Migrate -- \
  --from local:./data/vectors --to qdrant:http://localhost:6333
```

Each side is either a **provider shorthand** (`local:./data/vectors`, `qdrant:http://localhost:6333`,
`postgres:Host=…`) or the **path to a JSON config file** holding a `VectorStore` section — a
coordinator's own `appsettings.json` will do, which is the honest way to migrate with the exact
settings the hub uses. A secret on a command line ends up in shell history, so prefer the file form
when a connection string or an API key is involved (`--from-key` / `--to-key` exist for the Qdrant
key when you'd rather not).

| Flag | Purpose |
|---|---|
| `--from` / `--to` | Provider shorthand or JSON config path. **Required.** |
| `--collection <name>` | Copy just this one collection (default: all of them). |
| `--dry-run` | Report the plan and write nothing. |
| `--batch-size <n>` | Records read per page (default 256). |
| `--parallel <n>` | Concurrent upserts into the target (default 4). |
| `--from-key` / `--to-key` | Qdrant API key for that side. |

**What it does and does not do.**

- It creates each collection on the target with the **same dimension and distance**, then streams
  records across in pages. A target collection that already exists with a *different* shape is
  **skipped with a reason**, not half-filled.
- **Re-running is safe.** Chunk ids are deterministic (v2.5), so a second run overwrites rather than
  duplicating, and an interrupted run is resumed by simply running it again.
- **It never deletes.** A record in the target that is not in the source is left alone: a migration
  tool that removes data nobody asked it to remove is a worse failure than one that leaves a stale
  record behind.
- It reports the target's own record count per collection at the end, and **exits non-zero** if any
  collection was skipped or came up short. "The upserts returned" is not the same claim as "the data
  is there."
- It is a **separate console tool and is not in the images** — moving data between stores is an
  operator's deliberate action, never something a running coordinator should do to itself.
- Migrating *into* Qdrant creates collections in the current (v3.2+) hybrid shape, so this is also
  how a dense-only collection created on v3.1 gains server-side hybrid search.

**One surprise worth naming:** Qdrant stores the **unit-normalised** vector in a `cosine` collection
(under `dot` and `l2` it stores exactly what you sent). So a cosine collection migrated *out of*
Qdrant carries normalised vectors — same direction, length 1. That is safe, because cosine is
scale-invariant and the target returns the same ids in the same order with the same scores; it is
called out here only so that nobody diffing raw floats across a migration concludes the copy broke.

### Document ingestion (v2.5+)

The vector store has existed since v1.5 and inline retrieval since v2.0, but until v2.5 you had to
*fill* the store yourself — with pre-computed vectors, or text pasted in by hand. Ingestion closes
the loop: upload a document and the coordinator extracts its text, chunks it, embeds the chunks **on
the GPU fleet** (the same dispatcher that serves `/api/embed` — the coordinator has no embedding path
of its own), and writes them to whichever vector store you configured.

```bash
# The collection must exist first — its dimension has to match your embedding model.
curl -X POST http://your-coordinator:5080/api/admin/vector/collections \
  -H "Authorization: Bearer YOUR_ADMIN_KEY" \
  -d '{"name":"handbook","dimension":768,"distance":"cosine"}'

# Then upload. Text, Markdown, HTML, JSON, PDF.
curl -X POST http://your-coordinator:5080/api/collections/handbook/documents \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -F file=@employee-handbook.pdf
```

```json
{ "documentId": "employee-handbook.pdf", "collection": "handbook", "status": "ingested",
  "chunks": 214, "chunksEmbedded": 214, "bytes": 1048576, "contentHash": "48096003…" }
```

| Endpoint | Purpose |
|---|---|
| `POST /api/collections/{c}/documents` | Upload. `multipart/form-data` (`file`, optional `id`, `metadata`, `model`) **or** JSON (`{ id?, text, metadata? }`). |
| `GET /api/collections/{c}/documents` | List: id, chunk count, bytes, content hash, ingested-at, status. |
| `GET /api/collections/{c}/documents/{id}/chunks` | The chunks themselves, in order, with page numbers. |
| `DELETE /api/collections/{c}/documents/{id}` | Removes every chunk of that document. |

These are guarded by the **client** key (`Auth:ApiKeys`), not the admin key. Ingesting is a client
action, and forcing an admin key on it would push people toward using one key for everything.

**Four decisions worth knowing about, because they are the ones that would have been easy to get
wrong:**

- **⚠ There is no OCR, and there never will be.** A PDF whose text layer is empty or near-empty is
  **rejected** with an error saying it looks like a scan. It would have been easy to bolt on an OCR
  pass that *usually* works — but a bad extraction does not fail. It succeeds, quietly, and fills
  your corpus with near-gibberish that retrieves plausible nonsense, surfacing months later as a
  model that is subtly and unaccountably wrong. If a document genuinely needs OCR, that is a
  decision its owner should make deliberately, with a tool they chose, before it reaches InferHub.
- **Your file is not kept.** Chunk text, a content hash and metadata. Not the document. A retrieval
  system that also quietly becomes a document store has two sources of truth and a data-retention
  question its owner never agreed to answer. If you need the original, you already have it.
- **Re-ingesting is idempotent.** Chunk ids derive from the document id and the chunk index, so
  uploading a revision *replaces* its chunks rather than layering a second copy underneath the
  first — including sweeping the tail chunks when the revision is shorter. Upload the identical
  bytes twice and the second call does no work and returns `"status": "unchanged"`.
- **A partial ingest is a failure and says so.** If the fleet goes away mid-document, the response is
  **HTTP 500** with `"status": "partial"` and the chunk counts. The chunks that landed are real and
  visible, the document lists as `partial`, and re-posting the same bytes *resumes* rather than
  no-ops. A half-ingested document that claims success is worse than a failure.

Ingestion is provider-agnostic: it works identically under the file-backed local store (where the new
chunks replicate to your nodes through the usual path) and under PostgreSQL + pgvector. The admin
console gained a **Documents** panel — pick a collection, drop a file, watch the chunk count climb,
preview chunks, delete a document.

**Configuration** (`Ingestion` section; all optional):

| Key | Default | Purpose |
|---|---|---|
| `MaxChars` | `1200` | Target chunk size in characters. |
| `OverlapChars` | `150` | Tail context repeated at the head of the next chunk. |
| `MaxDocumentBytes` | `26214400` | Upload ceiling (25 MB). Above ~30 MB you must also raise Kestrel's `MaxRequestBodySize`. |
| `EmbeddingBatchSize` | `16` | Chunks embedded per batch — and the cap on chunks in flight, so a 300-page PDF cannot starve interactive chat. |
| `EmbeddingModel` | *(empty)* | Falls back to `VectorStore:DefaultEmbeddingModel`. |
| `MaxRetriesPerBatch` | `3` | Attempts per batch before the document is marked `partial`. |

### Retrieval-augmented inference

`/api/chat` and `/api/generate` accept optional headers that opt a normal request
into retrieval. Without a header the request is byte-for-byte unchanged — same body,
same routing, same streaming contract.

| Header | Purpose |
|---|---|
| `X-InferHub-Retrieve` | Collection name to retrieve from. Presence enables RAG. |
| `X-InferHub-Retrieve-K` | Top-k override (clamped to `VectorStore:Retrieval:MaxRecords`). |
| `X-InferHub-Retrieve-Model` | Embedding model override (defaults to `VectorStore:DefaultEmbeddingModel`). |
| `X-InferHub-Retrieve-Mode` | `vector` (default) \| `keyword` \| `hybrid`. Unknown value → `400`. **(v2.6+)** |
| `X-InferHub-Rerank` | `true` to run an opt-in LLM rerank pass over the candidates. **(v2.6+)** |

The coordinator extracts the query text (last user message for chat; `prompt` for
generate), dispatches an embed job to a node, searches the collection (node replica
if available, hub-local otherwise), assembles an augmented prompt via
`VectorStore:Retrieval:Template` (the literal `{context}` placeholder is replaced by
the retrieved records rendered as `[id] text` — text is drawn from `payload.text`,
then `payload.content`, then the raw payload), and dispatches the generation to a
node. The response is Ollama-shaped and unaltered; the retrieved sources come back
as a JSON array in `X-InferHub-Sources`.

**⚠ `X-InferHub-Sources` changed shape in v2.5.0.** It used to carry bare chunk ids
(`["a1b2…", "c3d4…"]`); it now carries objects that name where each chunk came from:

```
X-InferHub-Sources: [{"id":"5d981c…","documentId":"employee-handbook.pdf","page":1},
                     {"id":"0b72c7…","documentId":"policy.md"}]
```

A chunk id on its own identifies the row we retrieved but tells the reader nothing
about *where the answer came from*, and a citation that cannot name a document and a
page is not a citation. `documentId` and `page` are omitted — not null — for records
written straight through `/api/vector`, which never had a document.

**Where work happens.** Only orchestration and hub-local search live on the
coordinator. Embedding and generation are both dispatched to nodes — the mesh does
the heavy compute, exactly as with a bare `/api/chat` call. Rule #7 stays intact:
the augmented request body is assembled in-flight and forgotten.

**Failover.** Pre-stream failover still covers the generation job. A failed embed
or search surfaces via `VectorStore:Retrieval:OnMissing`: `error` returns
`424 Failed Dependency` with a message; `passthrough` runs the original request
unchanged and omits the sources header.

Example — a chat call grounded in the `docs` collection:

```bash
curl http://your-coordinator:5080/api/chat \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "X-InferHub-Retrieve: docs" \
  -H "X-InferHub-Retrieve-K: 4" \
  -d '{"model":"llama3","messages":[{"role":"user","content":"What is InferHub?"}],"stream":false}'
```

### Hybrid search & reranking (v2.6+)

Pure vector search is excellent at "what is this about" and poor at "find the exact
thing I named" — ask for an error code, a SKU, or a surname and cosine similarity
returns the general topic, not the line you wanted. v2.6 adds two answers, both
per-request and both off by default:

- **Retrieval modes** via `X-InferHub-Retrieve-Mode`: `keyword` is classic BM25 over
  the same chunks; `hybrid` runs vector **and** keyword and fuses the two result lists
  by **Reciprocal Rank Fusion** (by rank, not by blending scores that live on different
  scales). Hybrid is the one you usually want — it recovers the exact-match case without
  giving up the semantic one. Keyword search is provider-native (Postgres full-text under
  `postgres`, an in-memory BM25 index under `local`, an IDF-weighted sparse vector under
  `qdrant`) and added **zero dependencies**. Under `qdrant` (v3.2+) the fusion itself runs
  **server-side** — one Query API round trip instead of two branches fused on the hub.
- **Reranking** via `X-InferHub-Rerank: true`: an opt-in pass that hands the top
  candidates to a chat model already on your fleet with a scoring prompt and reorders
  them. It costs a round trip, so it is off unless asked, hard-capped by
  `Retrieval:RerankCandidates` and `Retrieval:RerankTimeoutSeconds` — past the timeout the
  un-reranked order is kept. Nothing is retained (rule #7).

```bash
curl http://your-coordinator:5080/v1/chat/completions \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "X-InferHub-Retrieve: handbook" \
  -H "X-InferHub-Retrieve-Mode: hybrid" \
  -d '{"model":"llama3","messages":[{"role":"user","content":"What does error E-4021 mean?"}]}'
```

Defaults stay `vector` with no rerank, so a deployment that sends no headers and changes
no config behaves **byte-identically to v2.5**. And "hybrid improves retrieval" is an
empirical claim, so v2.6 ships an evaluation harness in the same release —
[`tools/InferHub.Eval`](tools/InferHub.Eval) runs a golden set against a live coordinator in
every mode and reports Recall@k, MRR, nDCG@k and latency. Run it on your own corpus; that is
the only number that is about you.

There is also a query playground in the admin console (and `POST /api/collections/{c}/search`
behind it) that shows what each mode retrieves for a query, side by side — the most useful
thing to look at when a corpus is retrieving badly.

### Vector configuration

Coordinator keys (all under `VectorStore:`):

| Key | Default | Purpose |
|---|---|---|
| `VectorStore:Enabled` | `false` | Master switch. Off = no persisted state, old contract. |
| `VectorStore:Provider` | `local` | Backend: `local` (file-backed, replicated) \| `postgres` (external pgvector, **v2.2+**) \| `qdrant` (external, **v3.1+**). |
| `VectorStore:DataDirectory` | `./data/vectors` | Raw store + snapshots on the coordinator. Local provider only. |
| `VectorStore:Distance` | `cosine` | Default similarity metric (`cosine` \| `dot` \| `l2`). |
| `VectorStore:ReplicationFactor` | `2` | Target node replicas per collection (capped at connected node count). Local provider only. |
| `VectorStore:DefaultEmbeddingModel` | `nomic-embed-text` | Model used when a text upsert/query omits one. |
| `VectorStore:SnapshotEveryOps` | `5000` | Ops appended before a compacted snapshot is written. |
| `VectorStore:Retrieval:DefaultK` | `4` | Top-k when a request opts into retrieval. |
| `VectorStore:Retrieval:MaxRecords` | `8` | Hard cap on injected records per request. |
| `VectorStore:Retrieval:OnMissing` | `error` | `error` \| `passthrough` when retrieval can't run. |
| `VectorStore:Retrieval:Template` | _(see below)_ | Prompt template applied to retrieved context; must contain `{context}`. |
| `VectorStore:Retrieval:Mode` | `vector` | Default mode: `vector` \| `keyword` \| `hybrid`. **(v2.6+)** |
| `VectorStore:Retrieval:CandidatesPerBranch` | `20` | Candidates each branch fetches before RRF fusion in hybrid mode. **(v2.6+)** |
| `VectorStore:Retrieval:Rerank` | `none` | Default reranker: `none` \| `llm`. **(v2.6+)** |
| `VectorStore:Retrieval:RerankModel` | _(request model)_ | Chat model for the LLM reranker. **(v2.6+)** |
| `VectorStore:Retrieval:RerankCandidates` | `20` | Max candidates sent to the reranker in one round trip. **(v2.6+)** |
| `VectorStore:Retrieval:RerankTimeoutSeconds` | `20` | Reranker timeout; past it the un-reranked order is used. **(v2.6+)** |
| `VectorStore:Healing:DebounceMilliseconds` | `750` | Debounce for fleet-change-driven heal passes. |
| `VectorStore:Healing:IdleSweepSeconds` | `15` | Idle interval refreshing the under-replicated gauge. |

Postgres provider keys (all under `VectorStore:Postgres:`, used only when `Provider=postgres`) **(v2.2+)**:

| Key | Default | Purpose |
|---|---|---|
| `VectorStore:Postgres:ConnectionString` | _(empty)_ | Npgsql connection string. **Required.** Set via env (`VectorStore__Postgres__ConnectionString`) or user-secrets — never commit it. |
| `VectorStore:Postgres:Schema` | `inferhub` | Schema holding the registry and per-collection tables (`^[a-z_][a-z0-9_]*$`). |
| `VectorStore:Postgres:TablePrefix` | `vec_` | Prefix for per-collection tables (`^[a-z_][a-z0-9_]*$`). |
| `VectorStore:Postgres:AutoCreateExtension` | `true` | Run `CREATE EXTENSION IF NOT EXISTS vector` at startup. Set `false` if a DBA pre-installed it. |
| `VectorStore:Postgres:AutoCreateSchema` | `true` | Run `CREATE SCHEMA IF NOT EXISTS` at startup. |
| `VectorStore:Postgres:Index` | `hnsw` | ANN index: `hnsw` \| `ivfflat` \| `none` (exact scan). |
| `VectorStore:Postgres:HnswM` | `16` | HNSW `m` build parameter. |
| `VectorStore:Postgres:HnswEfConstruction` | `64` | HNSW `ef_construction` build parameter. |
| `VectorStore:Postgres:EfSearch` | `40` | Per-query `hnsw.ef_search` (higher = better recall, slower). |
| `VectorStore:Postgres:CommandTimeoutSeconds` | `30` | Npgsql command timeout. |
| `VectorStore:Postgres:MaxPoolSize` | `20` | Max pool size, applied if the connection string doesn't set one. |

Qdrant provider keys (all under `VectorStore:Qdrant:`, used only when `Provider=qdrant`) **(v3.1+)**:

| Key | Default | Purpose |
|---|---|---|
| `VectorStore:Qdrant:Url` | _(empty)_ | Qdrant REST base URL, e.g. `http://localhost:6333`. **Required.** `https://` for TLS. |
| `VectorStore:Qdrant:ApiKey` | _(empty)_ | Sent as the `api-key` header. Set via env (`VectorStore__Qdrant__ApiKey`) or user-secrets — never commit it. A non-loopback `Url` without one **warns at startup**. **(v3.3+)** |
| `VectorStore:Qdrant:TimeoutSeconds` | `30` | HTTP timeout. |
| `VectorStore:Qdrant:CollectionPrefix` | `inferhub_` | Prefix on the Qdrant collection name, so a shared Qdrant can host other apps' collections (`^[a-z_][a-z0-9_]*$`). |
| `VectorStore:Qdrant:UpsertBatchSize` | `128` | Points per upsert request. |
| `VectorStore:Qdrant:OverFetchMultiplier` | `4` | Filtered-ANN over-fetch multiple, trimmed back to `k`. |
| `VectorStore:Qdrant:HnswM` | `16` | HNSW `m` build parameter for new collections. |
| `VectorStore:Qdrant:HnswEfConstruct` | `64` | HNSW `ef_construct` build parameter for new collections. |
| `VectorStore:Qdrant:EfSearch` | _(Qdrant default)_ | Per-query HNSW `ef` (higher = better recall, slower). |
| `VectorStore:Qdrant:Quantization` | `none` | `none` \| `scalar` (int8, ~4× less vector memory) \| `binary` (~32×, lossy). A memory-for-recall trade — measure it. Applied at collection creation. **(v3.3+)** |
| `VectorStore:Qdrant:OnDisk` | `false` | Store dense vectors on disk instead of RAM. Applied at collection creation. **(v3.3+)** |
| `VectorStore:Qdrant:PayloadIndexKeys` | `["documentId"]` | Metadata keys to build a payload index on at collection creation. Empty list = index nothing. **(v3.3+)** |

Node keys (all under `Vector:`, only used when the node holds a replica — local provider):

| Key | Default | Purpose |
|---|---|---|
| `Vector:ReplicaDirectory` | `./data/vector-replicas` | Where a node persists assigned replicas so a restart doesn't require a full re-push. |

**Scaling note.** The local index is a flat (exact) cosine/dot/l2 search — small,
zero-dependency, correct. When a dataset outgrows flat exact, the `IVectorStore` seam is where
an approximate-nearest-neighbour strategy plugs in — and as of v2.2 that seam is **proven by a
second implementation**: the `postgres` provider serves HNSW-indexed ANN search from pgvector.
The dependency was a conscious, provider-scoped decision, not smuggled in.

**Multi-coordinator note.** Under the `postgres` provider the durable truth already lives
outside any one coordinator, which is what makes [high availability](#high-availability-v30)
possible from v3.0. Under `local` the raw store is per-hub: cross-hub raw-store replication is
still future work, so a `local` deployment stays single-coordinator.

## Status & observability

A read-only status page lives at `/` (and `/status`). It auto-refreshes and shows the
fleet — connected nodes, their reported models, live in-flight counts, eviction history,
and failover stats. For fleet operations (cordon, drain, deregister) use the
[management console](#management-console--admin-api) at `/console`.

`GET /api/status` returns the same data as JSON:

```json
{
  "coordinatorVersion": "1.0.0",
  "nowUtc": "2026-06-18T12:00:00+00:00",
  "uptimeSeconds": 4321.5,
  "nodes": [ { "nodeId": "...", "name": "...", "inFlight": 0, "localInFlight": 0, "modelCount": 4, "ageSeconds": 1.2 } ],
  "models": [ { "name": "llama3", "digest": "...", "size": 4661211808 } ],
  "metrics": {
    "requestsTotal": 142, "requestsInFlight": 1, "requestsCompleted": 138,
    "requestsFailed": 3, "failoversAttempted": 2, "failoversSucceeded": 2,
    "nodesEvicted": 1, "perNode": [ … ]
  }
}
```

`GET /health` stays unauthenticated for monitoring.

### Prometheus `/metrics` (v2.10+)

`GET /metrics` returns the Prometheus text exposition format. It **measures nothing new** —
every number was already on `/api/status` and the status page; this gives them a history, a
graph and an alert. `/api/status` is unchanged.

```bash
curl -H "Authorization: Bearer $INFERHUB_ADMIN_KEY" http://localhost:5080/metrics
```

```
# HELP inferhub_node_tokens_per_second Measured decayed tokens/second per node and model (EWMA).
# TYPE inferhub_node_tokens_per_second gauge
inferhub_node_tokens_per_second{node="gpu-1",model="llama3"} 42.5
```

What is exposed, all namespaced `inferhub_*`:

| Area | Series |
|---|---|
| Fleet | `requests_total`, `requests_in_flight`, `requests_completed_total`, `requests_failed_total`, `failovers_attempted_total`, `failovers_succeeded_total`, `nodes_evicted_total`, `openai_requests_total`, `uptime_seconds`, `build_info{version}` |
| Cloud burst | `fallback_dispatched_total`, `fallback_last_model{model}`, and since v3.29 `provider_dispatched_total{provider}`, `provider_last_model{provider,model}` — the same events, attributed |
| Per node (`node=`) | `node_up{node,name}`, `node_cordoned`, `node_models`, `node_local_in_flight`, `node_seconds_since_heartbeat`, `node_requests_total`, `node_requests_in_flight`, `node_requests_completed_total`, `node_requests_failed_total`, `node_tokens_per_second{node,model}` |
| Vector (`collection=`) | `vector_replicas_healed_total`, `vector_rebuilds_from_raw_total`, `vector_under_replicated`, `collection_queries_total`, `collection_query_latency_avg_ms`, `collection_documents_ingested_total`, `collection_chunks_embedded_total`, `collection_ingestion_failures_total` |
| Queue | `queue_depth`, `queue_queued_total`, `queue_admitted_total`, `queue_timed_out_total`, `queue_rejected_total`, `queue_wait_median_ms` |
| Named clients (`client=`) | `client_requests_in_flight`, `client_requests_last_minute`, `client_tokens_last_minute`, `client_tokens_today`, and `client_limit_*` for each configured limit |
| Cluster (`instance=`, v3.0+) | `cluster_active` (1 = holds the lease), `cluster_fence` (acquisition counter — a change means leadership moved). Absent entirely unless `Cluster:Enabled`. Alert on `sum(inferhub_cluster_active) != 1`. |
| Capabilities (`capability=`, v3.13+) | `capability_nodes`, `capability_models` |
| Tools (`node=`, `tool=`, v3.13+) | `tool_requests_total{outcome}`, `tool_workers{state}`, `tool_pool{state}` |
| Audio (`kind=`, `model=`, v3.13+) | `audio_seconds_total` (transcription), `audio_characters_total` (synthesis) |
| Profiles / node corpora (v3.13+) | `profile_state{profile,state}`, `node_corpus_records{node,collection}` |

**Auth: the admin key, not a client key.** `/metrics` is operational like `/health`, but
unlike `/health` it exposes node names, model names, client ids and the shape of your
traffic — so it sits behind `Auth:AdminApiKeys` by default and is deliberately *not* under
the bearer inference guard. A scraper is not a client, and giving a monitoring system a token
that can spend GPU time would be the wrong trade. On a trusted network, `Metrics:OpenScrape=true`
drops the guard on this one endpoint (it does not open `/api/admin/*`).

Counts only, never content: the client series come from the in-memory admission windows, the
same source `/api/admin/clients` reads. The usage ledger is append-only history and is never
read to drive anything.

Absence is meaningful. An unmeasured `(node, model)` has **no** `node_tokens_per_second`
series rather than a zero — the router treats an unmeasured node as *average*, and a 0 on a
dashboard would be a lie that invites an alert. Same for a client limit that is unset, for
the queue's median before anything has ever queued, and (v3.13+) for a capability nobody serves, a
tool nobody loaded, a profile nobody wrote and a corpus nobody assigned.

A ready-made Prometheus + Grafana overlay with a starter dashboard ships in
[`deploy/docker/compose.observability.yml`](deploy/docker/compose.observability.yml) — see the
[deploy runbook](deploy/docker/README.md#observability--prometheus--grafana).

Written by hand, like the NDJSON and SSE framing before it: the exposition format is
`# HELP` / `# TYPE` / `name{labels} value`, and `prometheus-net` would have been a permanent
dependency in exchange for code that fits on a screen. **Zero new dependencies.**

## Management console & admin API

A browser console at `/console` (alias for `/console.html`) lets an operator drive the
fleet — not just watch it. It is built from the same dark-theme HTML/CSS/JS as the status
page (no build toolchain, no React) and uses the same admin endpoints any script can call.

**What you can do**

- **Cordon / Uncordon** — flip a node out of (or back into) the routing pool. A cordoned
  node finishes its in-flight jobs and refuses new ones; the router silently skips it
  when picking candidates.
- **Drain** — cordon, then wait for the node's local in-flight count to reach zero. The
  console implements this client-side as cordon + poll, so the request stays fast and the
  server never holds a long-lived connection.
- **Deregister** — force-disconnect a node and drop it from the registry. If the worker
  process is still running it will reconnect cleanly and re-register.
- **Live updates** — the console subscribes to `GET /api/admin/stream`
  (Server-Sent Events) and reflects node connect / disconnect / cordon changes in ~1s
  without a refresh. If the stream drops it transparently falls back to polling.
- **Last-action audit** — each row shows the most recent admin action (who, when), kept
  in memory by the coordinator.

**Admin endpoints (under `/api/admin`, scoped to `Auth:AdminApiKeys`)**

| Method | Path | Purpose |
|---|---|---|
| `GET`  | `/api/admin/nodes` | Richer node snapshot: cordon state, labels, max-concurrency, last action. |
| `GET`  | `/api/admin/stream` | SSE stream of fleet changes; keepalive every ~10s. |
| `POST` | `/api/admin/nodes/{nodeId}/cordon` | Stop routing new jobs to this node. |
| `POST` | `/api/admin/nodes/{nodeId}/uncordon` | Restore the node to the routing pool. |
| `POST` | `/api/admin/nodes/{nodeId}/deregister` | Force-disconnect and drop the node. |

Example — cordon a node from the CLI:

```bash
curl -X POST http://your-coordinator:5080/api/admin/nodes/<nodeId>/cordon \
  -H "Authorization: Bearer YOUR_ADMIN_KEY"
```

**Admin key handling in the browser.** The console prompts for the admin key once per
tab, keeps it in a JS variable for the session only — **never** in `localStorage` or
`sessionStorage` — and sends it as `Authorization: Bearer …` on admin calls. A 401
re-prompts; read-only stats keep rendering either way.

## Resilience & failover

InferHub is built to keep running while nodes come and go.

- **Heartbeat eviction.** Nodes that miss heartbeats past `NodeRegistry:TimeoutSeconds`
  are dropped from the registry; their in-flight jobs fail with a clear error and their
  sticky-conversation mappings are cleared.
- **Pre-stream failover.** If the chosen node drops *before* the first chunk is
  produced, the coordinator transparently retries the request on another capable node
  (when one exists). This applies to both blocking and streaming calls.
- **No silent retry mid-stream.** Once chunks have started flowing, the coordinator does
  **not** retry — the client already has partial output. Instead the stream ends with a
  final error chunk so callers don't hang.
- **Job timeout.** `Dispatcher:TimeoutSeconds` caps how long any one job can hold a
  request open.

### High availability (v3.0+)

Everything above keeps the mesh running while *nodes* come and go. The **coordinator** was the
remaining single point of failure. From v3.0 you can run a second one as a warm standby.

**What it does.** Two (or more) coordinators share one Postgres. A lease row elects exactly one
**active** hub; the rest run **standby** — they answer `/health`, `/api/status`, `/metrics` and
`/api/admin/*`, and return `503` + `Retry-After` on every inference route. Nodes connect through a
list of hubs and land on whoever holds the lease. When the active hub dies, the standby takes the
lease within the TTL and the mesh serves again: no manual promotion, no data migration, no
gossip — the durable state was already shared.

**What it does not do,** stated plainly: no active-active load sharing (one hub serves at a time),
and no clustering of the `local` vector provider. Those are the rest of the HA track, not this
release.

```jsonc
// Both coordinators, differing only in InstanceId. Requires VectorStore:Provider=postgres.
"Cluster": {
  "Enabled": true,
  "InstanceId": "hub-a",
  "ConnectionString": "",   // env Cluster__ConnectionString — the SAME database on both
  "LeaseTtlSeconds": 15,
  "RenewIntervalSeconds": 5
}
```

| Config key | Default | What it does |
|---|---|---|
| `Cluster:Enabled` | `false` | Off = byte-identical to v2.13: no lease, no connection, no role. |
| `Cluster:InstanceId` | machine name | Names this hub in the lease row, the logs, and `/metrics`. |
| `Cluster:ConnectionString` | *(required when enabled)* | Its own key, like the usage ledger's — HA without a Postgres vector store, or the reverse, are both reasonable. |
| `Cluster:LeaseName` | `default` | Separates two unrelated meshes sharing one database. |
| `Cluster:LeaseTtlSeconds` | `15` | Worst-case failover delay **and** the fencing window — the same number, on purpose. |
| `Cluster:RenewIntervalSeconds` | `5` | Must be ≤ a third of the TTL, or one slow round-trip demotes a healthy leader. Startup fails if it is not. |

**What a client sees.** Every response from a clustered hub carries `X-InferHub-Role:
active|standby`, `GET /health` gains a `role` field, and inference against a standby is a `503`
with `Retry-After` in the caller's own dialect (the OpenAI error envelope on `/v1`). Put a load
balancer or DNS failover in front — **the hub does not become one** — and drain on the role, not
on `/health`: a standby is *healthy*, it is just not leading, and an orchestrator told otherwise
will restart-loop the instance that is supposed to be waiting quietly.

**Split-brain.** A coordinator that has not *proved* it holds the lease within the TTL demotes
itself, measured on its own clock, whether or not it can reach anything to ask. So a partition
that cuts a primary off from Postgres stops it serving rather than letting two hubs both answer.
The trade is deliberate: an unreachable database takes the mesh down, and an inference request the
mesh cannot attribute to a single leader is worse than a `503` the front routes elsewhere. Watch
`inferhub_cluster_active` — summed across hubs it should be exactly `1`.

The node side is one key:

```jsonc
"Coordinator": {
  "Endpoints": [ "http://hub-a:8080/", "http://hub-b:8080/" ]
}
```

Leave it empty and the node uses `Coordinator:Url` exactly as before. A standby refuses the
SignalR handshake with a `503`, so rotating the list is how a node finds the current leader.

A two-coordinator compose overlay and a failover runbook live in
[`deploy/docker/`](deploy/docker/README.md#high-availability--a-warm-standby-coordinator).

## Fleet operations (v2.8)

The coordinator can manage models across the fleet without an SSH session — over the same outbound
connection the nodes already hold open, so the node still needs no inbound port.

```bash
# pull a model onto one node (progress streams on the admin SSE feed)
curl -X POST http://your-coordinator:5080/api/admin/nodes/$NODE_ID/models/llama3.2/pull \
  -H "Authorization: Bearer $ADMIN_KEY"

# ensure a model is held by 3 suitable nodes (skips cordoned ones, tells you what it decided)
curl -X POST "http://your-coordinator:5080/api/admin/models/llama3.2/ensure?replicas=3" \
  -H "Authorization: Bearer $ADMIN_KEY"

# the fleet-wide model × node matrix
curl http://your-coordinator:5080/api/admin/models -H "Authorization: Bearer $ADMIN_KEY"
```

| Endpoint | What it does |
|---|---|
| `POST /api/admin/nodes/{id}/models/{model}/pull` | Pull a model onto a node; progress relayed as `model-progress` SSE events. |
| `DELETE /api/admin/nodes/{id}/models/{model}` | Delete a model from a node. |
| `POST /api/admin/nodes/{id}/models/{model}/warm` | Load a model into memory ahead of first use. |
| `POST /api/admin/models/{model}/ensure?replicas=N` | Pull onto the N most suitable capable nodes lacking it; reports its decision. |
| `GET /api/admin/models` | Fleet-wide model × node matrix, with sizes. |

Not every backend can manage models: an **OpenAI-compatible** node (vLLM, llama.cpp, a hosted
provider) has its model fixed at launch, so it advertises that it cannot, the endpoints refuse it
cleanly (never a 500), and the console greys out its controls. A duplicate command for the same
node+model coalesces onto the running one, and every command is audited. All of this is on the
**Model management** panel in the [console](#management-console--admin-api).

## Conversations & routing

InferHub stores **no conversation content**. Clients send the full message history on every
turn, exactly like Ollama — the coordinator only decides *which node* runs the work.

- **Least-busy by default.** When several nodes hold the requested model, the coordinator
  picks the one with the lowest in-flight job count and breaks ties round-robin.
- **Measured routing (v2.8, opt-in).** Set `Router:Strategy=throughput` and the coordinator
  ranks capable nodes by *expected completion time* — a decayed average (EWMA) of measured
  tokens/second per model, adjusted for in-flight load — instead of raw queue depth. A 4090 and a
  laptop both reporting one job in flight are no longer treated as equal. A node with **no**
  measurement is treated as *average*, never as slow, so a fresh node still earns traffic. The
  default is `least-busy` and is unchanged bit-for-bit; measured tokens/sec appears on `/api/status`
  per node. See [Fleet operations](#fleet-operations-v28).
- **Sticky conversations.** Successive turns of the same chat prefer the same node, which
  keeps that model's KV-cache warm. Affinity expires after ~10 idle minutes. Affinity wins over
  the throughput strategy — a warm model on a slower node usually beats a cold one on a faster node.
- **Affinity guard.** If the sticky node is far busier than the least-busy alternative,
  the coordinator breaks affinity to avoid piling up on one machine.
- **Graceful fallback.** If the sticky node disconnects, the next turn transparently
  routes to another capable node and the mapping is refreshed.

**Tagging a conversation.** By default the coordinator hashes the opening system/user
message to detect a continuing thread without any client cooperation. Clients that want
explicit control can attach a stable id to every turn:

```bash
curl http://your-coordinator:5080/api/chat \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "X-InferHub-Conversation: my-chat-7f3a" \
  -d '{"model":"llama3","messages":[...],"stream":false}'
```

The header value is opaque — any stable string identifying the conversation works.

## Built with

- [.NET 10](https://dotnet.microsoft.com/) (C#)
- [ASP.NET Core](https://learn.microsoft.com/aspnet/core/) + [SignalR](https://learn.microsoft.com/aspnet/core/signalr/) for the coordinator and the node link
- A pluggable node backend (`IInferenceBackend`); the first one is **Ollama**, via
  [OllamaClient](https://github.com/Dev-Art-Solutions/OllamaClient)
- [Ollama](https://github.com/ollama/ollama) on each node (for the Ollama backend)

## License

MIT — see [LICENSE](LICENSE).

---

Made by [Dev Art Solutions](https://devart.solutions). We build production-ready AI and
agent systems. Say hello: hello@devart.solutions
