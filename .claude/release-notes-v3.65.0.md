# InferHub v3.65.0 — Strata reads pictures: per size, refused before a load when it cannot, added from the hub

v3.64 made a node serve a [Strata](https://github.com/Niko1221/Strata) install as a catalogue and shipped it
**chat only**, saying a size's pictures were "a setup choice the node cannot see". That was wrong: Strata's
setup writes the image encoder into the config of a size set up with images, and its server starts the
encoder from exactly that. v3.65 reads it.

## What's new

```
GET  /api/status                                → nodes[].strata.models[]: {"name": "strata-coder-iq1_m", ..., "images": true}
POST /api/admin/nodes/{id}/strata/install       {"model": "strata-coder-iq1_m", "vision": "yes"}
POST /v1/chat/completions                       {"model": "strata-q2_0", "messages": [{"role": "user", "content": [..., {"type": "image_url", ...}]}]}
                                                → 400 at once, when strata-q2_0 has no image encoder
```

- **Every Strata model says whether it reads pictures** — `"images": true|false` on the hub's status and a
  **pictures** chip in the console's Strata panel.
- **A picture for a size without the encoder is a 400 in milliseconds**, naming the call that fixes it.
  Before, the node started the size (one to three minutes; on the one-slot default that also stopped the
  size that was loaded) and Strata then refused the picture itself.
- **Pictures are chosen per install**: `"vision": "yes"` (the encoder on the GPU), `"cpu"` or `"no"`; left
  out, the node's `Strata:Install:Vision` decides, as before. The console has a **with pictures** box.
- **On a size already installed without pictures, `"vision": "yes"` adds them**: the node runs Strata's
  setup again for that size, which skips everything already there and fetches only the encoder (~0.9 GB).
  Setup answers every question afresh for a size it is not adopting, so the node passes the config's own
  context and KV precision back — the size comes out the same except for the encoder. Refused while that
  size is loaded (unload it first).
- **A node's refusal keeps its 4xx.** Every failed job used to reach a client as a 502 — "the server
  failed" — including requests the model simply cannot take. A refusal now carries its status on the job
  result and on a stream's failure chunk; the hub's and a solo node's OpenAI edges answer it as a 400
  `invalid_request_error`. A failure without one is still a 502, so an older node behaves as before.
- The encoder's download bar (`vision encoder:  0.45 / 0.91 GB`) reaches the command feed as percent.

The wire changes are three optional fields — `NodeCatalogModel.images`, `ModelCommand.vision`,
`InferenceResult.status` — each absent from and ignored by a v3.64 peer.

## Measured — a real Strata install on this box (RTX 3090 Ti 24 GB, 256 GB RAM, Windows 11)

Strata `fb58e0d`, hub and node from source, the two sizes installed in v3.64 (Coder IQ1_M, Q2_0 — both
without pictures):

- The hub's status listed both with `"images": false`.
- A 448×448 PNG (a red circle, a blue square, the text "OK 42") sent to `strata-q2_0` through the hub's
  `/v1/chat/completions`: **400 in 82 ms**, streamed **400 in 35 ms**; no Strata process was started.
  (The first try, before the status change, was a 502 — which is how D3 was found.)
- `POST …/strata/install {"model": "strata-coder-iq1_m", "vision": "maybe"}` → 400 naming the three values.
- `{"model": "strata-coder-iq1_m", "vision": "yes"}` → 202; the node ran setup for the installed Coder:
  both shards "already downloaded", the encoder downloaded (0.91 GB), the pack and draft layer kept, and
  **`context: 32768 tokens`, `KV cache: 8-bit` — the config's own**, not setup's 128K recommendation for
  this card. About two minutes. The config gained `--vision`, `--vram-reserve-mib 700` and the `vision`
  block, nothing else; the hub listed the Coder with `"images": true`.
- The picture to the Coder through the hub: **HTTP 200 in 15.1 s** — *"The picture shows a red circle in
  the upper left and a blue square in the lower right on a white background, with the black text "OK 42"
  appearing in the upper right."* Streamed ("How many shapes?"): first byte in **40 ms**, answer **2**, 17 s.
- Neither the hub's nor the node's log contains either prompt or any of the picture's base64.

The first Coder attempt shared the card with a 23 GB Ollama model another program on this box loads
(GPU at 100 %) and hit the hub's 300 s request timeout while Strata was still thinking at 0.1 tok/s; the
picture had already been encoded (`strata-vision.exe` ran). The numbers above are with that program
paused. The Coder had loaded during the contention, so its expert cache was sized with ~6 GB of VRAM free.

## Not established

- `"vision": "cpu"` (the encoder on the CPU) — only `yes` (GPU) was run.
- Pictures on the qwen, Swift and Unsloth families — only the Coder's encoder was installed and used.
- A fresh install with pictures (as opposed to adding them to an installed size).
- An engine's own 4xx inside a job (Strata's or llama.cpp's "context too long") is still a 502; only the
  node's own refusals carry a status in this release.
- Removing pictures from a size.

## The published artifact

Docker Desktop was not running on this box, and no image carries Strata anyway, so — as for v3.64 — the
check pulled `inferhub-coordinator:3.65.0` and `inferhub-node:3.65.0` (linux/amd64) from GHCR anonymously,
extracted their `/app` layers and ran **those DLLs** natively against the real Strata install and GPU. Both
images' `org.opencontainers.image.revision` label is `bba53a7`, the tag commit. This tests the published code,
not the images' OS layer.

- The published hub reported `3.65.0` and listed `strata-coder-iq1_m` with `"images": true` and
  `strata-q2_0` with `"images": false`.
- The picture to `strata-q2_0`: **400 in 5 ms**, blocking and streamed; the node logged `refused (400)`;
  no Strata process was started.
- `/strata/install {"model": "strata-coder-iq1_m", "vision": "yes"}` → 202, and the node did not run
  setup: the size already has the encoder.
- The picture to the Coder: **HTTP 200 in 41.8 s**, of which the cold load was 16.3 s — *"The picture
  contains a red circle in the upper left and a blue square in the lower right, with the black text
  "OK 42" appearing in the upper right."* The card was not shared this time.
- Neither published process's log contains the prompt or any of the picture's base64; no Strata process
  was left after the node was stopped.
