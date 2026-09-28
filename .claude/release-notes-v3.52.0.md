# InferHub v3.52.0 — one image for a one-card box

v3.50 added `Node:OnDemand`, which lets one card serve chat, speech, images and video in turn. It was
built for a desktop whose single GPU should run audio, then video, then an LLM, and be free in
between. **No published image could do that.** `:tools` had Ollama, Whisper, Piper and the reranker.
`:diffusion` had images and video, and no Ollama. To get all of it on one card you had to run both
containers, and then you had two nodes, each with its own GPU arbiter, each sure the card was its
own. That is the collision on-demand exists to prevent.

v3.52 adds a sixth node image, `inferhub-node:all`.

## What's new

```sh
docker run -d --name inferhub --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0=your-key \
  -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:all
```

or `docker compose -f deploy/docker/compose.all.yml up -d`.

- **Everything `:tools` and `:diffusion` hold, in one node**: Ollama, Whisper, Piper, the
  cross-encoder reranker, and every diffusion recipe (images, edits, video).
- **`Node:OnDemand` is on in this image, and only in this image.** With it off, `:all` would be the
  combination v3.14 refused to ship: a resident chat model and a diffusion pipeline fighting over
  one card. The node's own default stays `false`, and every other image is unchanged. If your card
  can hold everything at once, `-e Node__OnDemand__Enabled=false` turns it off.
- **One Python environment, not two.** Both requirement files are resolved together, so PyTorch is
  installed once. Every import check either parent image runs at build time also runs here. The
  image is ~11 GB on disk and ~6.5 GB to pull, which is smaller than `:tools` plus `:diffusion`.
- amd64 only, NVIDIA only, `:all` and `:3.52.0-all`. It does not touch `:latest`.

**When not to use it:** with several cards or several boxes, keep composing `:tools` and `:diffusion`
through a coordinator. Capability routing sends each request to the right node, and since v3.51 it
prefers the card that is already warm.

## A bug this found, and fixed: on-demand + diffusion never offered a model on a fresh volume

The diffusion worker downloads weights on a background thread and offers a recipe only once it has
landed (v3.14.1). An on-demand node stops a tool's worker right after it answers at startup (v3.50).
**That stop killed the download.** On a fresh volume, an on-demand node with diffusion offered no
image model at all, forever. v3.50 and v3.51 were verified against a host whose weights were already
downloaded, which is why nobody saw it. Running `:all` on an empty volume found it in the first minute.

The fix is a small, optional field on the worker protocol. A worker's `ready` frame may now list what
it is still fetching:

```json
{ "type": "ready", "protocol": 1, "capabilities": [...], "fetching": ["sd15", "sdxl"] }
```

A node that is freeing the card keeps a worker that is still fetching, logs why, and stops it once a
later `ready` says the list is empty. The worker sends that empty list after a failed download too,
so a model that never lands cannot pin the process. A worker that never sends the field behaves
exactly as before, and so does a node that does not run on demand.

Covered by a new test that drives a real worker process (it fails without the fix), and checked on
the image itself: on an empty volume `sd15` was fetched (2.0 GB), offered, and then its worker was
stopped.

## Verified

- **Locally, on the image built from this commit, on the CPU, fresh volume:** the `sd15` fetch
  survived and landed as above. Then one container served
  chat (`qwen2.5:0.5b`) → image (`sd15`, 512×512) → transcription (`whisper-tiny`) → chat. `/api/status`
  showed the holder move `ollama` → `tool:diffusion` → `tool:whisper` → `ollama`, and the log shows
  each newcomer waiting for the previous holder to release the card before starting.
- Test slices: Shared 193, Node 230 (+5), Mesh 452 (+1), Coordinator 798. Zero new
  `PackageReference`; `InferHub.Shared.csproj` still empty.
- **Published image:** see the addendum below.

## Not established, said out loud

- **A 3.52 `:all` node inside a mesh**, behind a coordinator. Only solo mode was run.
- **Whether the one venv's CUDA pieces coexist on other cards.** `ctranslate2` (Whisper) and
  PyTorch (diffusion) share one environment. The build proves both import; only the GPU run in the
  addendum shows them working on a card, and only on one card.
- **Proving an nf4 recipe loadable (`flux-schnell`, `qwen-image`) places modules on the card
  outside the arbiter.** That was already true of model pulls in v3.50 (they take no GPU lease); the
  background fetch is a second place it happens. Stated, not fixed.
- **Phase 85's `OnDemandToolTests` flaked once again** while a `docker build` was loading the
  machine, and passed three times alone and in a full Mesh rerun. Its 15-second wait on a real
  worker process is still the suspect. Not proven unrelated.
