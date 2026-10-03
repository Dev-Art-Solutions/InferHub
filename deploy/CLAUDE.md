# deploy/ — agent context

**Scope: `deploy/`, and the Dockerfiles under `src/`.** What ships, what is inside each image,
and the one trap this repository has fallen into five separate times.

> **Read the root `CLAUDE.md` first.**

## The images

| Image | Size | Arch | Inside |
|---|---:|---|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | the hub. No GPU, no engine |
| `inferhub-node` | ~340 MB | amd64 + arm64 | the node alone. Solo mode and vector-only boxes too |
| `inferhub-node:ollama` | ~4 GB | amd64 | + Ollama, supervised as the node's own child |
| `inferhub-node:tools` | ~6 GB | amd64 | + Python, `faster-whisper`, `piper` |
| `inferhub-node:diffusion` | ~12 GB | amd64 | + PyTorch, `diffusers`, `bitsandbytes`, seven recipes |
| `inferhub-node:tts-bg` | ~9 GB | amd64 | + PyTorch, `nemo_toolkit`, for `bg-tts-v5` (84). Stacks on nothing |
| `inferhub-node:all` | ~11 GB | amd64 | `:tools` + `:diffusion` in one node, `Node:OnDemand` on (87) |
| `inferhub-node:colibri` | ~410 MB | amd64 | the plain node + python3, libgomp1 and colibri v1.12.1's CPU engine, launched by the node (93) |

**`:diffusion` deliberately does not stack** — it is built from the *plain* node, with no Ollama, no
Whisper and no Piper in it (46 D9). Stacking reaches ~15 GB and every pull pays for it, and a card
running a diffusion pipeline has no room for a chat model beside it, so bundling one would ship a
combination the docs would then have to tell people not to use. **The mesh is the composition
mechanism**: run `:diffusion` on the card and `:ollama` beside it, and capability routing sends
`image` to one and `chat` to the other.

**Phase 87 amended that, and did not reverse it.** D9's second reason, "no room for a chat model
beside it", stopped holding at phase 85: with `Node:OnDemand` on, the services take turns on the card
and the node's `GpuArbiter` enforces the turns. Two containers on one card cannot do that, because
that means two arbiters, each sure it owns the card. So the box with **one card and no mesh** gets
`:all`, and for everyone else the mesh is still the answer. `:diffusion` and `:tools` are unchanged.

- **87 D1 — `:all` sets `ENV Node__OnDemand__Enabled=true`; nothing else does.** The node's default
  stays `false`. With on-demand off, `:all` *is* the combination D9 refused, so the image is not that
  by default. An operator whose card holds everything at once can turn it off. *Rejected:* refusing
  startup with it off. The node cannot tell which image it is in, and a 48 GB card is a real reason.
- **87 D2 — one venv, one `pip install -r tools -r diffusion`.** `requirements-tools.txt` pins no torch
  but `sentence-transformers` pulls one, so two venvs would ship PyTorch twice. Resolving them
  together makes the diffusion pins constraints on the tools side, and every shipped manifest already
  names `/opt/inferhub/venv/bin/python`. **Both parents' build-time import assertions run in `:all`**.
  *Rejected:* two venvs plus a second, rewritten copy of every manifest.
- **87 D3 — a hand-copy, pinned by `BundledNodeTests`.** `:all` cannot `FROM` the published `:tools`
  (the matrix builds in parallel, so it would get the *previous* release's). The tests pin the same
  Ollama version + sha, every import either parent asserts, and OnDemand on in `:all` and nowhere
  else.

**`:colibri` is the plain node plus a few hundred KB of C** (93 D6): the engine and its stdlib-only
gateway, pinned and sha-checked like Ollama (39 D9), no weights (39 D7) — the operator mounts a
converted model at `/models/colibri`. Both runtime packages are measured, not guessed: the aspnet
base is Ubuntu 24.04 and carries neither `python3` nor `libgomp1`, and without the latter every
engine binary fails to load. **The model directory must be readable and writable by uid 1654**:
colibri writes `.coli_usage`/`.coli_kv` beside the weights, and its own converter, run as root,
writes the shards `0600` — found by mounting one (`chown -R 1654:1654 <dir>` is the fix).

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

## Bubblewrap (phase 83), and the two capabilities a sandboxed manifest needs

`:tools` and `:diffusion` both carry `bwrap` now — `apt-get install bubblewrap`, the same category of
dependency as `ffmpeg`/`curl` above, never a `PackageReference` (rule 5). It is **inert** by default:
none of the shipped manifests (`whisper.json`, `piper.json`, `rerank.json`, `diffusion.json`) name a
`sandbox` field, so a `docker run` that changes no manifest is byte-identical to before this phase.

An operator who adds `"sandbox": {"mode": "bubblewrap"}` to a manifest on the volume also has to grant
the **container** — not the process inside it — two capabilities Docker does not include by default,
because `bwrap` builds its own mount and network namespaces and that needs `CAP_SYS_ADMIN` and (for
`--unshare-net`'s loopback setup) `CAP_NET_ADMIN`:

```
docker run --cap-add SYS_ADMIN --cap-add NET_ADMIN ... ghcr.io/dev-art-solutions/inferhub-node:tools
```

Without them a sandboxed tool fails to start and the log names `bwrap`'s own "Operation not
permitted" — it does not fall back to running unsandboxed (`src/InferHub.Node/Tools/CLAUDE.md`,
phase-83 D2). This was found by pulling the published image and running a real sandboxed request
against it, the same discipline as the permissions trap above: a container's *default* capability
set is not the process's own privilege, and the two are easy to conflate until something inside the
sandbox fails for a reason `strace` on the host would never show.

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
- The bundled-image decisions in full: `src/InferHub.Node/CLAUDE.md` (phase 39, 42 D3, 46 D9); 87's are above
- What `:all` turns on: `src/InferHub.Node/CLAUDE.md`'s phase-85 block (`Node:OnDemand`)
