# InferHub v3.7.0 — a node and its Ollama, in one container

The node image never needed a GPU, because the node never did the computing. It translates,
dispatches and formats; the model runs in Ollama, and Ollama ran on your host. So the deploy story
for a single machine was: install Ollama, install InferHub, guess whether the container reaches the
host at `host.docker.internal` or `172.17.0.1`, get it wrong once, and keep two things alive.

v3.7 ships a second image with Ollama inside it.

```bash
docker run -d --name inferhub --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$INFERHUB_API_KEY" \
  -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:ollama

docker exec inferhub ollama pull llama3.2
```

Then point any OpenAI client at `http://localhost:5081/v1`. Solo mode (3.5) removed the
coordinator; this removes the host install. One container is now a complete inference endpoint,
with RAG (3.6) if you want it.

## Three modes, one image — and none of them refuses to start

| | How | What runs |
|---|---|---|
| **GPU inference** (+ RAG) | `--gpus all` | node + Ollama on CUDA |
| **CPU inference** (+ RAG) | leave `--gpus` off | node + Ollama on the CPU |
| **Vector store only** | `-e Ollama__Supervisor__Enabled=false` | node + corpus, **no Ollama process at all** |

An earlier draft of this release had the image *refuse* to start without a visible GPU, on the
grounds that a silent CPU fallback is the kind of quiet wrongness this project keeps designing
against. That was wrong and it was reversed. CPU is a legitimate mode, not a misconfiguration —
embedding models and small models run on it perfectly well, and a vector store needs no model at
all. The danger was never the CPU. It was **silence**: pulling four gigabytes of CUDA runtime,
dropping a `--gpus` flag, getting two tokens a second and spending the afternoon blaming the model.

So the node says what it found, in its first lines, either way:

```
info: CUDA: 1 device(s) visible to this process — NVIDIA GeForce RTX 3090 Ti.
info: CUDA: no devices visible to this process; inference will run on the CPU.
      In a container, pass '--gpus all' to use a card.
```

and on `/api/status`:

```json
"gpu": { "cuda": true, "devices": 1, "names": ["NVIDIA GeForce RTX 3090 Ti"] }
```

If you want the guarantee rather than the report, `Ollama__RequireGpu=true` makes a missing card a
startup failure that names `--gpus all`. It is off by default, including in this image.

The third mode is worth a sentence: `/api/vector/{collection}` takes **client-supplied vectors**, so
a node with no inference process is still a complete vector store. It reports zero models, which is
the honest answer — a chat request fails cleanly rather than hanging. Ingesting *documents* needs an
embedder, so there, bring your own vectors or point `Backend:Type=openai` at one elsewhere.

## The node keeps its own Ollama alive

The container runs two processes: the node as PID 1, and `ollama serve` as its child. There is no
s6, no supervisord, no entrypoint script — the v3.4 supervisor already knew how to do this job, and
it turns out an init system is what it was. It starts Ollama at boot, probes it every 15 seconds on
a short deadline of its own, and restarts it when it dies or wedges, under a budget that gives up
loudly rather than looping forever.

That matters more in a container than on a host, because nothing else in there is watching: a wedged
Ollama leaves the container looking alive, `/health` answering, and every request hanging. Verified
the only way it can be — by killing the process inside a running container and watching the node
bring it back:

```
Ollama at http://127.0.0.1:11434/ is Unreachable after 3 consecutive failed probes.
Restarting Ollama (Unreachable) via Binary '/usr/local/bin/ollama' — attempt 1 of 3 in this 00:10:00 window.
```

Two small supervisor keys came with it, both off by default so nothing about an existing node
changes: `Ollama:Supervisor:StartAtBoot` (start immediately when nothing is listening, instead of
waiting out three probes — for a deployment that *owns* its Ollama) and
`Ollama:Supervisor:StopOnShutdown` (stop the one we spawned, and only that one).

## Two things worth knowing if you write GPU Dockerfiles

**A container needs the driver, not a CUDA toolkit.** `libcuda.so.1` is injected at `docker run` by
the NVIDIA container runtime; the CUDA *runtime* Ollama links against ships inside Ollama's own
tarball. So this image finals on the same stock `dotnet/aspnet` base as the plain one. An
`nvidia/cuda` base would have added a third copy of a runtime nothing loads, at ~2 GB, and pinned a
CUDA minor we do not choose. It is also why CPU mode is free — the same tarball carries the CPU
kernels.

**`NVIDIA_DRIVER_CAPABILITIES` defaults to `utility`, which gives you a working `nvidia-smi` and no
`libcuda`.** That is the worst kind of bug: every diagnostic looks right, the card is listed, and
inference silently runs on the CPU. The image sets `compute,utility`, and a test reads the
Dockerfile and fails if `compute` ever disappears from that line.

And a third, for anyone detecting GPUs in containers: **under WSL2 — Docker Desktop on Windows —
`/dev/nvidia*` does not exist.** The GPU arrives through `/dev/dxg` and the driver libraries are
injected from `/usr/lib/wsl/lib`. Every recipe that checks for device nodes reports "no GPU" on what
is probably the most common GPU-with-Docker setup there is. InferHub loads `libcuda.so.1` and asks
the driver instead, which is the same question the inference engine asks a moment later.

## What is in it, and what is not

- **~4 GB.** The plain `inferhub-node` image is unchanged at ~340 MB, still multi-arch, still with
  no Ollama in it. That is deliberate: a bundled flag on one image would have grown every
  coordinator+node stack by 4 GB for a feature it does not use.
- **amd64 and NVIDIA only.** No ROCm, no Intel, no Apple; arm64 would mean Jetson-specific bundles
  and hardware to test on.
- **The tag is `:ollama`**, because the image runs perfectly well without a card. `:gpu` is an alias
  for the same digest.
- **No model is baked in and none is pulled at boot.** `docker exec … ollama pull` is the interface.
  **Mount a volume at `/data`** or every `docker run` re-downloads them.
- **Ollama's port is not published.** The container's surface is InferHub's API, which requires a
  key. `-e OLLAMA_HOST=0.0.0.0 -p 11434:11434` is yours to decide.
- **Every `OLLAMA_*` variable passes through** — the supervisor spawns a child that inherits the
  environment, so `OLLAMA_KEEP_ALIVE`, `OLLAMA_NUM_PARALLEL` and the rest need no config surface of
  ours.
- It is also a good **mesh** node: point `Coordinator__Url` at a hub and it reports
  `SupportsModelManagement=true` over an Ollama it genuinely controls, so the console can pull
  models into it. That has never been true of a container before.

`docker compose -f deploy/docker/compose.ollama.yml up -d` is the compose version.

## Verified on the hardware, not asserted

Packaging releases are where this project has shipped its worst bugs — four images that were dead on
arrival while every test was green. So all three modes were run against a real RTX 3090 Ti (driver
591.86, Docker 27.3.1, WSL2): GPU inference with `ollama ps` reporting `100% GPU`, CPU inference
reporting `100% CPU`, the vector-store mode with no Ollama process in the container, RAG end to end
with real citations, the restart, the volume surviving a container replacement, and `RequireGpu`
refusing and passing. Details are in the phase file.

**Zero new dependencies**, as for the eleven releases before it: no `.csproj` changed, and
`InferHub.Shared` is still an empty project file. The dependency here is a `curl` in a Dockerfile,
in an image nobody has to pull.

Upgrading from 3.6 changes nothing: the plain images are the same, and all three new configuration
keys default to today's behaviour.
