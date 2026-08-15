# deploy/ — agent context

**Scope: `deploy/`, and the five Dockerfiles under `src/`.** What ships, what is inside each image,
and the one trap this repository has fallen into five separate times.

> **Read the root `CLAUDE.md` first.**

## The five images

| Image | Size | Arch | Inside |
|---|---:|---|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | the hub. No GPU, no engine |
| `inferhub-node` | ~340 MB | amd64 + arm64 | the node alone. Solo mode and vector-only boxes too |
| `inferhub-node:ollama` | ~4 GB | amd64 | + Ollama, supervised as the node's own child |
| `inferhub-node:tools` | ~6 GB | amd64 | + Python, `faster-whisper`, `piper` |
| `inferhub-node:diffusion` | ~12 GB | amd64 | + PyTorch, `diffusers`, `bitsandbytes`, seven recipes |

**`:diffusion` deliberately does not stack** — it is built from the *plain* node, with no Ollama, no
Whisper and no Piper in it (46 D9). Stacking reaches ~15 GB and every pull pays for it, and a card
running a diffusion pipeline has no room for a chat model beside it, so bundling one would ship a
combination the docs would then have to tell people not to use. **The mesh is the composition
mechanism**: run `:diffusion` on the card and `:ollama` beside it, and capability routing sends
`image` to one and `chat` to the other.

## The permissions trap, five times found and seven paths headed off

**In a container, `/app` is not writable, and a fresh named volume inherits its mount point's
ownership from the image.** Every image runs `USER app`; a volume mounted at a path the image does
not contain is created **root-owned**, and the container cannot write it.

It has been found five times, always by pulling the published image and running it, never by a test:

| Found in | What was writing where |
|---|---|
| v2.5.1 | `LocalVectorStore` and the node's `ReplicaStore` → `/app/data` |
| v2.10.0 | `FileNodeIdentity` → `/app/.inferhub-node-id`, **broken since v2.3.0** |
| phase 30 | the file affinity store → `/app/data/affinity` |
| phase 38 | node retrieval → `/app/data/retrieval` |
| phase 41 | tool scratch → `/app/data/tools/scratch` |
| phase 43 | node profiles → `/app/data/profiles` |
| phase 56 | durable image jobs → `/app/data/images` |

The last two were **headed off rather than found**: the default stays relative so bare metal and
Windows work, and every image sets the absolute path. Phase 56's is the first of them that holds
**user content** — a finished picture, for `Images:Jobs:RetentionSeconds` — so a deployment that
turns it on wants the volume at `/data` for the retention window to mean anything across a
`docker run`.

The fix is always the same two lines, and **neither may be "simplified" away**:

```dockerfile
RUN mkdir -p /data && chown app:app /data      # makes the VOLUME case work
ENV VectorStore__DataDirectory=/data/vectors   # makes the BARE IMAGE case work
```

**When you fix a permissions bug, grep for every write path — not the one that reported it.** That
is the specific mistake v2.5.1 made, and it hid the other half for five releases.

## `ASPNETCORE_URLS` does not work here; set `Urls`

`appsettings.json` pins `"Urls"`, and that layer **overrides** the `ASPNETCORE_`-prefixed provider,
which loads into host config first. An image honouring `ASPNETCORE_URLS` binds loopback and answers
nobody. The images set `ENV Urls=http://+:8080`, which is layered after `appsettings.json` and
actually wins. Verified at runtime, not assumed. (21 D6.)

**And `http://+:8080` is a valid address that `Uri.TryCreate` rejects** — v3.5.0 shipped solo mode
dead on arrival in Docker over exactly that, against the image's own default. Listen addresses go
through `LocalApiOptions.TryParse`, which parses them the way Kestrel accepts them and reports "is
this a wildcard?" separately from "did this parse?".

## Pull the published image and run it

**This is a release step, not a suggestion.** Six releases were dead on arrival with a green suite
behind them — v2.5.1, v3.0.1, v3.5.1, v3.10.0, v3.14.0 and v3.16.0 — and every one was found this
way. The verification runs live in the phase files under `plan/`, with the observed numbers and the
exact host.

**Ask the image, not the dashboard.** GitHub Actions has reported a finished run as queued for
hours; the honest question is whether the manifest is on GHCR.

## Related context

- What runs inside them: `src/InferHub.Coordinator/CLAUDE.md`, `src/InferHub.Node/CLAUDE.md`
- The workers in `:tools` and `:diffusion`: `python/CLAUDE.md`
- The bundled-image decisions in full: `src/InferHub.Node/CLAUDE.md` (phase 39, 42 D3, 46 D9)
