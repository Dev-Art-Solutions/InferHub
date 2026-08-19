# v3.28.0 — the verification day

**What this is.** Phase 60's whole deliverable (60 D1): every published v3.27.0 image pulled onto one
box, driven, and the numbers written down — including the ones that came back wrong. Six releases
(v3.22.0 → v3.27.0) each verified only the slice it touched, by design (track D2); this is the day
that pays the rest of the bill.

**The plan said nothing here would be fixed here. That was overturned on the day**, because three of
the findings are shipped features that do not work at all rather than defects around them — see the
brief's struck-through first non-goal. Every fix below is verified against the published artifact it
was found on, and the one finding left open says so and why.

## The box, once

| | |
|---|---|
| CPU | AMD Ryzen Threadripper PRO 5975WX (32 cores) |
| GPU | NVIDIA GeForce RTX 3090 Ti — 24 564 MiB, driver 591.86 |
| OS | Windows 11 Pro 26200 |
| Docker | 27.3.1, Linux containers |
| .NET | 10.0.301 |
| Date | 2026-08-19 |

Single observations on one box with one card. Not a benchmark (60 D1's non-goal).

## 1. The full solution suite, by hand (60 D6)

`dotnet test InferHub.sln`, everything in parallel as CI does.

| Project | Passed | Skipped | Duration |
|---|---|---|---|
| `InferHub.Tests.Shared` | 129 | 0 | 0.5 s |
| `InferHub.Tests.Coordinator` | 643 | 43 | 2 s |
| `InferHub.Tests.Node` | 151 | 3 | 32 s |
| `InferHub.Tests.Mesh` | 395 | 2 | 1 m 1 s |
| **Total** | **1 318** | **48** | **67 s wall** |

Green as a solution, not only as four slices. The 48 skips are the Postgres/Qdrant gated integration
tests and the three `PlanFolderFact` context checks that only run where `plan/` exists.

## 2. The published tags, resolved from the registry (60 D2)

Digests asked of GHCR, not read out of `docker-publish.yml` — 60 D2's whole point.

| Tag | Index digest |
|---|---|
| `inferhub-coordinator:3.27.0` | `sha256:059cac5b0698ab38ae24e2c23f2d91fc1e84111b933c6fd742d53fc7f1b54d84` |
| `inferhub-coordinator:latest` | **same** ✓ |
| `inferhub-node:3.27.0` | `sha256:aeddd8b7027b9fe34837945e0037fe505d446f0cbf8865a6d353ba0f913d63c2` |
| `inferhub-node:latest` | **same** ✓ |
| `inferhub-node:3.27.0-ollama` | `sha256:b857a856854376d58215f83a0f4e1d293968be83722ec6d62478b1f91c5e171d` |
| `inferhub-node:ollama` / `:gpu` | **same** ✓ (both aliases) |
| `inferhub-node:3.27.0-tools` | `sha256:f206e79dca3fc035315194ea0afe6a1ac2a2241bc4c134f22a1564ef36185e63` |
| `inferhub-node:tools` | **same** ✓ |
| `inferhub-node:3.27.0-diffusion` | `sha256:6642e1c05980f89ed5d90904ba3461c6dc5772c1623463e127e63d3fcf28fe45` |
| `inferhub-node:diffusion` | **same** ✓ |

**`:latest` is the 352 MB base node, not the 12.5 GB diffusion image.** That is the v3.16.1 fix
holding, checked from outside for the first time on a tag push that published all five flavours.

`org.opencontainers.image.version` on the three pulled images reads `3.27.0`, `3.27.0` and
`3.27.0-tools`; all carry revision `bb59b13` — the phase-59 commit. The label was asked, not the
dashboard.

## 3. Coordinator `3.27.0`, alone

| Check | Result |
|---|---|
| `/health` | `{"status":"ok","version":"3.27.0"}` |
| `/api/status` with a **client** key | 200, full fleet document |
| `/api/status` with an **admin** key | **401 `invalid bearer token`** — see finding F1 |
| `/api/status` with no key, off-loopback | 401 `missing bearer token` |
| `/metrics` with an admin key | 200, 142 lines |
| `/metrics` with no key | 401 |
| `/console.html` | 200 |
| `/api/admin/nodes` with an admin key | 200 |

## 4. The mesh floor — base node `3.27.0`

Registered against the published coordinator over SignalR, no inbound rule, and reported
**22 of 22 models from the ollama backend as chat, embed**. `/api/status` lists it with its
capabilities. This is the floor everything below stands on and it holds.

## 5. Tools node `3.27.0-tools`

Registered and reported **chat, embed, transcribe**. The recipe/tool mailbox (45 D3) arrives at the
hub intact:

| Tool | State at the hub | Note |
|---|---|---|
| `whisper` | `running`, allowed | declares `transcribe` over six whisper sizes |
| `piper` | `running`, allowed | declares **nothing** on a fresh volume — correct, see below |
| `diffusion` | `not-allowed` | `Tools:Allowed` named only whisper and piper; the ceiling holds |

**`piper` offering nothing is right, and it says why**:

```
[tool piper] [piper] no voices found under /data/tools/voices, so this worker offers nothing.
Download one .onnx + .onnx.json pair from https://huggingface.co/rhasspy/piper-voices into that directory.
```

A TTS call at that point is a **404 `model 'piper' not found`**, not a 500 and not a hang. `en_US-amy-medium`
(63 MB) was then installed per the README's own command.

## 6. Metrics: the `media` label on the published image (59)

```
inferhub_image_recipe{node="…",recipe="sdxl",media="image",reason="not-ready"} 1
inferhub_image_recipe{node="…",recipe="wan-t2v-1.3b",media="video",reason="not-ready"} 1
```

Both values of `media` reachable on one series, from a real node, on the published image. Phase 59
D1's mailbox widening is confirmed outside the fixtures for the first time.


## 7. Audio, end to end and round trip (42)

`piper` needs a **restart** after a voice is dropped in, exactly as the README says — it scans
`/data/tools/voices` at worker start and the diffusion worker's re-declare-on-fetch has no
counterpart here. After the restart:

```
[tool piper] [piper] offering voices: en_US-amy-medium (formats: wav, pcm, mp3, opus, flac)
```

| Step | Result |
|---|---|
| `POST /v1/audio/speech`, 32 characters | 200, **114 220 B RIFF wav, 1.40 s** |
| `POST /v1/audio/transcriptions` on that wav, `whisper-base`, first call | 200, **117.4 s** — the model downloads inside the request |
| Same call, model cached | 200, **0.37 s** |
| What came back | `{"text":"Phase 60, the verification day."}` |

Synthesised "Phase sixty, the verification day."; transcribed "Phase 60, the verification day." A
full TTS → STT round trip through the published hub and the published tools image.

**Both audio meters are live and separate** (42's two units, observed rather than reasoned for the
first time):

```
inferhub_audio_seconds_total{kind="transcribe",model="whisper-base"} 2.589
inferhub_audio_characters_total{kind="speak",model="en_US-amy-medium"} 34
```

**A note on the per-tool counters, because they look wrong before they look right.**
`inferhub_tool_requests_total{tool="whisper"}` read `0` for more than a minute after a successful
transcription. It is **reporting lag, not a lost count** — `NodeToolState` travels on the node's
model-refresh loop. Sampled every 30 s across a third request: `2, 2, 3, 3, 3, 3`. The total is
always right within about a minute; a dashboard built on these should not alert on a single scrape.
Checked rather than assumed, because a lost count here would be a real defect and it is not one.

## 8. `sdxl` on the published `:diffusion` image

The node comes up declaring nothing and fills in, as documented:

```
[diffusion] device: cuda (NVIDIA GeForce RTX 3090 Ti), 23285 MiB free of 24563 MiB
[diffusion] offering recipes: none (editing: none; video: none) (fetching: sdxl, wan-t2v-1.3b)
[diffusion] 'sdxl' is ready; offering recipes: sdxl (editing: sdxl; video: none)
```

| | |
|---|---|
| Weights fetched | 6 778 MiB for `stabilityai/stable-diffusion-xl-base-1.0@462165984030`, variant fp16 |
| Download rate on this line | ~12 MB/s |
| Load | **2.6 s** (cold), 0.0 s thereafter — resident |
| Generate, 1024×1024, 30 steps | **9.2 s** first, then 9.1 / 8.7 / 8.7 s |
| `POST /v1/images/generations` wall-clock | **11.9 s**, 2 223 939 B of JSON, 1 542 560 B of PNG |
| Response fields | `size`, `seed`, `projection: "flat"`, `revised_prompt: null` |

`projection` is **reported** on a flat image rather than omitted — 49 D4's deliberate exception to
28 D5, confirmed on the wire.

**Looked at** (60 D3). Prompt: *"a lighthouse in a storm, oil painting"*. It is a competent SDXL
oil painting — a white lighthouse and its keeper's cottage on a green headland, a storm front across
the top two thirds, and heavy surf breaking on rock in the foreground. Brushwork and canvas texture
are present, the horizon is level, the building's perspective holds together. Nothing about it says
"something in the pipeline is broken", which is the question this row exists to answer.

**There is a one-refresh-cycle window where the hub 404s a model the node has already declared.** The
first `sdxl` request after `'sdxl' is ready` returned
`{"error":{"message":"model 'sdxl' not found","code":"model_not_found"}}`; the same request a minute
later returned the picture above. That is the model-refresh loop, the same cadence as §7's counters
— worth knowing before diagnosing it as a routing bug.

### VRAM: declared against observed (60 D4)

| Recipe | Declared `vramMiB` | Observed on the card |
|---|---|---|
| `sdxl` | 8 000 | card total **19 906 MiB** in use with `sdxl` resident and idle, against a **5 149–5 524 MiB** baseline from everything else on this desktop — i.e. a **~14.5 GiB** delta, high-water 17 989 MiB *during* generation |

**The declaration is low, and this number is not clean enough to change it on.** Two reasons it
overstates the requirement, both real: PyTorch's caching allocator keeps freed blocks rather than
returning them, so "used" is a high-water mark and not a working set; and this card is also driving a
Windows desktop, so the baseline is not zero and is not constant. What can be said with confidence is
**the direction** — `sdxl` at 1024×1024 costs materially more than the 8 000 MiB it declares, and
`diffusers` upcasting the VAE to fp32 at decode (the pipeline warns about it in the log) is the
obvious candidate for the spike.

Recorded, **not corrected** (60 D4). A `vramMiB` edited in the release that measured it is a number
nobody can audit, and this measurement wants a headless box before it changes anything.

The hub's own series agree with the declaration side of that:

```
inferhub_node_vram_budget_mib{...}   23000
inferhub_node_vram_reserve_mib{...}   2048
inferhub_node_vram_measured_mib{...} 24563   <- the worker's cross-check, never a budget (48 D1)
inferhub_node_vram_resident_mib{...}     0   <- read while idle
```

## 9. Async image jobs and the durable archive (47, 56) — on the published image

`Images__Jobs__Persistence=file`, `DataDirectory=/data/images`, `RetentionSeconds=300`. The directory
is created at boot and is empty until a job finishes.

| Check | Result |
|---|---|
| `POST /api/images/jobs` | **202**, `{"state":"running", …}` with the node id |
| Poll to completion | `succeeded`, 30/30 steps, ~9 s |
| On disk | `{id}.json` (477 B) + `{id}.0.bin` (1 355 812 B) |
| The **synchronous** `/v1/images/generations` request | also archived, as `expired` / `delivered`, **no `.bin`** — read-once already unlinked it |
| `units` on both records | **31.457** megapixel_steps — the ≈31 the v3.25 notes predicted for an SDXL image |

**Rule 7 / 56 D3, checked on the disk rather than in a test:**

```
grep -ril "barn|lighthouse|sunset|storm" /data/images   ->   NO PROMPT TEXT FOUND
```

The durable record holds id, client, model, count, three timestamps, state, node, units,
`promptAugmented`, warnings, and per-image `mediaType`/`size`/`seed`/`projection`/`steps`. There is
no field a prompt could occupy.

**Restart behaviour — and the two paths turn out to be different, which the notes do not say:**

| What was done | What came back |
|---|---|
| `docker restart` (graceful) with a job in flight | `failed`, **`reason: "node_lost"`** — *"node '…' disappeared while this job was running"* |
| `docker kill` (SIGKILL) then `start`, job in flight | `failed`, **`reason: "hub_restarted"`** — *"the hub restarted while this job was in flight; it was not resumed"* |
| A `succeeded` job across either restart | survives, still `succeeded`, bytes still on disk and still fetchable |
| `.tmp` strays after both | **none** — v3.24.1's fix holds on the published image |

Both in-flight outcomes are `failed` and neither is resumed, which is what 56 D3 promises. But a
**graceful** shutdown gets far enough to mark the job `node_lost` before it dies, so `hub_restarted`
is the *crash* path specifically. Worth knowing before somebody greps their logs for the wrong string.
## 10. The first clip this project has produced, and the first anybody has watched (57, 58, 59)

Four attempts. The first three produced nothing at all — see F3, F4, F5 and F6, which are what those
attempts were. The fourth, with the manifest's `video` kind, `ftfy`, `peft`, a video-sized tool
deadline and `vaeTiling: true`:

```
POST /v1/videos  {"model":"wan-t2v-1.3b","size":"832x480","seconds":5,"seed":424242}
completed in 378 s
```

| | |
|---|---|
| Worker's own line | `81 frames at 832x480, 16 fps (5.0625s), 30 steps on cuda (load 21.7s, generate 340.8s)` |
| Response `seconds` | **5.06** — the measured duration, not the label the caller sent |
| Bytes | **982 330 B**, `video/mp4`, `ftypisom … avc1` — a real H.264 elementary stream, not the echo worker's padded container |
| VRAM peak | **18 540 MiB** |
| Progress | reached 99 and only then 100, as `VideoRenderer.Progress` documents |

**57 D5 confirmed on the wire.** `seconds: 5` names an offer of 81 frames; 81 at 16 fps is 5.0625 s
and the response says `5.06`. The label was not echoed back.

### Watched (60 D3)

Frames 0, 20, 40, 60 and 80 extracted and looked at. A white paper boat on wet cobblestones in
rain, in a blue night palette, with a street receding into fog.

**The motion is coherent and directional**: the boat starts left and close to camera, crosses to
centre, and recedes up the street — it is being carried, not morphing in place. The cobbles flow
consistently underneath it, the wet-stone reflections stay attached to the stones, and the rain
streaks are continuous across the whole run. That is the failure mode a 1.3B text-to-video model is
expected to have and this clip does not: no frame-to-frame identity drift, no melting geometry.

**No tile seams visible.** No horizontal or vertical banding, no discontinuity in the cobble
texture, no lighting step on a grid. Recorded as observed rather than as measured — there is no
seam metric for a tiled decode the way there is for a panorama (49 D5), and inventing one here
would be a number nobody asked for.

**The comparison promised in F6 has no "before", and that is the finding rather than a gap.**
The untiled configuration never produced bytes on this hardware — three attempts, roughly fifty
minutes of card, zero frames. So the tile seams are assessed on the tiled clip alone; the
alternative is not a worse picture, it is no picture.

### `wan-t2v-1.3b`'s `vramMiB`, corrected from a measurement (60 D4)

| | MiB |
|---|---|
| Declared before today | 15 500 (arithmetic over repository file sizes plus an unmeasured allowance) |
| Measured peak, untiled | **24 287** — of a 24 564 card, and it never finished |
| Measured peak, tiled | **18 540** |
| Baseline from other processes on this card | ~850 |
| **Declared now** | **18 000** — the tiled peak less the baseline, plus a small margin |

60 D4 said a wrong figure would be recorded and not quietly corrected. It is corrected, because the
figure that made it wrong was a misconfiguration this release also fixes: **24 287 describes a
recipe missing the flag phase 58 built for it**, and acting on that number would have refused the
flagship video model on the 24 GB card the catalogue was written for. The arithmetic is in the
recipe's own `notes`, so the next reader can check it rather than trust it. 18 000 still fits the
22 528 MiB headroom of a 24 GB card at the default reserve, which was verified by restarting the
node and watching it declare the recipe.

## 11. The first panorama, and the seam (49, 55)

`qwen-360` became offerable for the first time once `peft` was present (F4). Three renders, one seed,
one prompt — *"a lantern-lit stone courtyard at dusk, arches on all sides, cobbles underfoot"*.

| Render | Steps | Wall | `seamDelta` | Bytes |
|---|---|---|---|---|
| default | 25 | 107 s (load 80.8 s + generate 98.7 s) | **0.04498** | 2 566 504 |
| same seed | 50 | 181 s (generate 167.3 s) | **0.04342** | 2 527 899 |
| same seed, `X-InferHub-Image-Seam-Repair: blend` | 50 | 242 s (load 68.3 s + generate 169.6 s) | **0 **, from `seamDeltaBefore: 0.04342` | 2 529 603 |

**Phase 49's envelope, confirmed on the wire.** `projection: "equirectangular"` on the body;
`promptAugmented: true` with `trigger: "360 degree panorama with equirectangular projection"` — the
phrase was appended and *reported*, never silently inserted (49 D2); `megapixelSteps` 52.43 at 25
steps and 104.86 at 50, which is `2048×1024×steps/1e6` exactly.

**Phase 55 D4, confirmed on a real panorama rather than on numpy.** The repair ran, the delta fell
0.04342 → 0, so it was **kept** rather than discarded, and the content route carried all three
headers — `X-InferHub-Image-Seam-Repair: blend`, `X-InferHub-Image-Seam-Delta: 0`,
`X-InferHub-Image-Seam-Delta-Before: 0.04342` — emitted only because a repair was asked for.

**What the blend actually touched, measured pixel by pixel** against the unrepaired render of the
same seed:

```
columns changed: 78 of 2048   (a wrap-around feather: 0..~38 and ~2010..2047)
max per-pixel channel-sum delta: 176 of 765
mean delta over the changed columns: 8.58
```

Phase 55 measured "80 of 2048 columns touched" on a synthetic raster with no card. On a real
panorama it is **78**, the rest bit-identical. That is as close to a prediction confirmed as this
project has come.

### Looked at (60 D3)

**The 50-step render is a real 360° courtyard.** Stone arches on every side, lanterns lit inside the
vaulting, an iron gate, a dusk-blue sky, cobbles underfoot. The equirectangular geometry is right:
the ground fills the lower half and stretches toward the nadir, the sky compresses toward the
zenith, and the horizontal wrap is continuous — the arch that leaves the right edge is the arch that
enters the left.

**The blended one is indistinguishable by eye from the unblended one**, which is the point: 78
columns at a mean channel-sum delta of 8.58 is a tonal correction, not a cross-fade. **No smearing at
the seam**, no soft band, no loss of the stonework's texture at either edge. 55 D2's distinction —
a feather closes a *tonal* discontinuity, and the rejected cross-fade is what smears detail — holds
on the only kind of image that could have falsified it.

### The default of 25 steps is visibly under-denoised, and no metric in the system says so

This is the finding that justifies 60 D1 by itself.

At **25 steps** — the recipe's default — every surface in the panorama carries a pervasive mottled,
speckled texture: the stone walls, the cobbles and the sky alike, as though a fine noise layer sat
over the whole frame. Window rows smear, the arches are lumpy, and the nadir is a featureless mush.
At **50 steps**, same seed, same prompt, it is *gone*: stone reads as stone, the vaulting resolves,
the lanterns are crisp, the sky is smooth.

**The seam metric moved by 0.00156 between those two images.** Nothing else in the pipeline measures
anything. A green suite, a passing seam check and a well-formed envelope all describe the 25-step
render as a success, and it is not one — which is exactly why 49 D5 measures rather than repairs and
why this phase's deliverable is a person looking at a picture.

**Not changed here.** Unlike `vaeTiling`, 25 steps is not broken — it is a cost default, and doubling
it doubles both the wall-clock and the meter (52 → 105 megapixel-steps per panorama). Recorded, with
both renders described, for whoever owns that trade.

### A near-miss worth recording, because the next reader will hit it

`{"steps": 50}` in the request **body** is silently ignored: `steps`, `guidance` and `seed` travel as
`X-InferHub-Image-*` headers by design (`ImageRequests` D1 — a body field would collide with whatever
OpenAI adds to its own schema next), and `seed` is *also* accepted in the body because it is the
first thing anyone reaches for.

The render came back at 25 steps with an identical `megapixelSteps`, and was nearly written up here
as "the `steps` parameter is dropped". It is not; the request was wrong. What made it obvious was
that **the same seed produced a byte-identical file** — 2 566 504 bytes twice, identical
`seamDelta` — which is also, incidentally, the first confirmation this project has that a seed is
reproducible on a real model.

## 12. Declared VRAM against observed, in one place (60 D4)

Every figure in `vramMiB` was arithmetic before today — repository file sizes converted to the
recipe's dtype plus an activation allowance nobody had measured (48 D1 declares rather than detects,
for WSL2's sake). Three of them have now met a card.

| Recipe | Declared | Observed peak | Note |
|---|---|---|---|
| `sdxl` | 8 000 | **17 989** at 1024×1024 / 30 steps, against a 5 149–5 524 baseline | ~12.7 GiB delta; `diffusers` upcasts the VAE to fp32 at decode and warns about it in the log |
| `qwen-360` | 19 500 | **24 191** at 2048×1024 / 25 steps | nf4 base + a rank-128 LoRA; completed, but close to the card |
| `wan-t2v-1.3b` | 15 500 → **18 000** | **18 540** tiled, **24 287** untiled | the only one corrected today, and F6 is why |
| `sd15`, `sdxl-turbo`, `flux-schnell`, `sd35-medium`, `qwen-image`, `cogvideox-2b` | — | **not measured** | not fetched; the disk on this box held three models and the largest three of these are 30–60 GB each |
| `wan-t2v-14b-720p` | 24 000 | **not measured, by design** | 60 D5: verified as a refusal, and no byte was fetched |

**Only `wan-t2v-1.3b` was corrected, and the asymmetry is deliberate.** Its number was wrong *because
of a misconfiguration this release also fixes*, so there is a measurement of a correct configuration
to replace it with. `sdxl` and `qwen-360` are simply low, on a card that is also driving a Windows
desktop, measured through PyTorch's caching allocator — which keeps freed blocks rather than
returning them, so "used" is a high-water mark and not a working set. Changing those on this evidence
would be trading one unaudited number for another; they are recorded here and want a headless box.

**The direction is the same in all three cases: the declarations are low, not high.** 48 D2's claim is
that over-budget is "503 + `Retry-After`, and never an OOM". A declaration that understates the model
is the direction that can still surprise a card — worth knowing before somebody sets
`ResidentRecipes` above 1.

## Findings

### F1 — The console cannot read `/api/status` on any containerised hub. `console.js` sends no credential on that call.

`src/InferHub.Coordinator/wwwroot/console.js:208`:

```js
const fetchStatus = async () => {
  const res = await fetch("/api/status", { headers: { "Accept": "application/json" } });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
```

No `Authorization` header — not the admin key it holds, not the client key it also holds. `/api/status`
is guarded by the **client**-key scope (`BearerApiKeyMiddleware` guards `/api` except `/api/admin`),
so the call succeeds only where the loopback exemption applies.

**Inside a container it never applies.** The hub sees the bridge gateway (`::ffff:172.19.0.1`), not a
loopback address, so a browser on the host hitting a published port gets:

```
Rejected request to /api/status from ::ffff:172.19.0.1: missing bearer token
```

That is the console's 5-second poll — nodes, uptime, metrics, the needs-attention strip, and the
Images and Video panels 51 and 59 built on top of it. It works on a `dotnet run` hub on the same
machine, which is exactly where every console screenshot this project has taken was made.

An admin key does not rescue it either: an admin key is **not** a client key, so pasting one into the
console's key bar and having the call authenticate at all was never possible.

Present since the console read `/api/status` at all — the shape dates to phase 11 (v1.3.0) and the
line was last touched in phase 23. **Not fixed here** (60 D1).

### F2 — The worker downloads weights for a recipe the node has already refused. `wan-t2v-14b-720p` costs ~75 GB on a box that can never run it.

60 D5 expected the 14B to be refused *before any byte moved*. Half of that is true.

The **node** refuses it correctly and instantly, before any fetch, naming the arithmetic:

```
Image recipe 'wan-t2v-14b-720p' needs 24000 MiB and this node budgets 20952 MiB for models
(Node:Vram:BudgetMiB 23000 minus Node:Vram:ReserveMiB 2048), so it is not offered.
Image catalogue: 3 recipe(s) under /recipes. VRAM budget 23000 MiB, reserve 2048 MiB, at most 1 recipe(s) resident.
```

The **worker**, in the same container, seconds later:

```
[diffusion] catalogue: 3 recipe(s), 3 offerable on this box, at most 1 resident at a time
[diffusion] offering recipes: none (editing: none; video: none) (fetching: sdxl, wan-t2v-1.3b, wan-t2v-14b-720p)
```

`offered()` in `python/tools/diffusion_worker.py:362` has **two** gates — licence and `cpuViable` —
and no VRAM gate. `BaseWorkerEnvironment()` in `src/InferHub.Node/Configuration/ToolOptions.cs:133`
passes the licence grant, the GPU flags, the recipe directory, the resident count, the seam
threshold and the seam ceiling to the child — and **not the VRAM budget**. So the clamp that decides
what the node *declares* has no counterpart in the process that decides what to *download*.

`fetch_order()` sorts by declared `vramMiB` ascending, so the largest model is fetched last: on this
run the 14B was third in the queue and the download was stopped by removing the recipe. An operator
who runs `:diffusion` as the README documents it — default recipe directory, all eleven recipes — is
queued for a ~75 GB fetch of a model their node has already announced it will never offer.

It is not a licence hole and not a routing hole: the hub is never told about the recipe, so nothing
is ever dispatched to it. The cost is disk, bandwidth and a fetch queue held open behind a model
that cannot run. **Not fixed here** (60 D1).

*Note for whoever fixes it:* 48 D5's phrasing — "enforced on the node *and* in the worker" — is about
licences, and the ToolOptions comment calls that defence in depth by name. The VRAM budget was never
given the same treatment, and 48 D2 places `VramBudget.Evaluate` deliberately at the node, *after*
the worker slot. Neither decision is wrong; what is missing is that the worker's *prefetch* planner
is a third place that spends a resource on a recipe's behalf.
### F3 — The `video` capability was unreachable on every published image. The manifest never named the kind.

**This is the day's largest finding, and it is the one "nothing was rendered" was hiding.**

v3.25.0 shipped the video seam, v3.26.0 catalogued three video models, v3.27.0 gave them a console
panel, metrics and a second usage unit. **No clip could be generated through any of them.**

The worker declares the kind correctly — `offering recipes: sdxl (editing: sdxl; video: wan-t2v-1.3b)`
— and the node throws it away. `ToolWorkerPool.Narrow` iterates the **manifest's** capabilities and
keeps only kinds present there, which is right: the manifest is the operator's ceiling (41 D2). The
shipped `/opt/inferhub/tools/manifests/diffusion.json` declared:

```json
"capabilities": [ { "kind": "image", "models": [] }, { "kind": "image-edit", "models": [] } ]
```

No `video`. So the hub's capability list read `['image', 'image-edit']` on a node whose worker had
just announced a video model, and `POST /v1/videos` answered
`{"code":"model_not_found"}` — forever, on any box, with the weights sitting on its disk.

Every phase-57 and phase-59 test passes because the fixtures declare their own manifests.

**Fixed and verified on the published image:** `video` added to `python/manifests/diffusion.json`;
the patched manifest mounted over the published container's; `/api/status` then read
`node caps: ['image', 'image-edit', 'video']` and `video 1 ['wan-t2v-1.3b']`.
`BundledNodeTests.TheDiffusionManifestDeclaresAKindForEveryMediaItsRecipesShip` keys on the shipped
recipes' `media` field rather than on a list in the test, so the next modality fails on the day its
first recipe lands.

### F4 — `diffusers` uses two packages it does not declare, and neither is in the image. Both fail at request time.

With F3 fixed, the first real video request got as far as loading the model and then:

```
{"error":{"code":"worker_error","message":"NameError: name 'ftfy' is not defined"}}
```

`diffusers/pipelines/wan/pipeline_wan.py` imports `ftfy` under `if is_ftfy_available():` and then
calls `ftfy.fix_text(text)` **unconditionally** in `prompt_clean`. So every video request that has
ever been made to this image died — after the weights loaded, minutes of card spent, naming a
library nobody asked for.

The second, found in the same hour on the panorama path:

```
[diffusion] could not fetch 'qwen-360': ValueError: PEFT backend is required for this method.
```

`load_lora_weights` **is** the PEFT backend. `qwen-360` — the whole of phase 49 — could not load at
all, so it never became offerable, and the only signal was the node continuing to say `fetching`.

Neither package is in `requirements-diffusion.txt`. That file already carries the argument for why
`imageio-ffmpeg` is pinned *and asserted at build time* — "without it it does not fail, it warns and
silently drops to an OpenCV writer" — and these two are the same lesson with a louder failure.

**Fixed and verified on the published image:** `ftfy==6.3.1` and `peft==0.17.1` pinned, and
`Dockerfile.diffusion` now asserts `is_ftfy_available()` / `is_peft_available()` beside its torch
assertion. Both were installed into the running published container to prove the fix before the
pins were written.

### F5 — Both deadlines a video job passes through are shorter than a five-second clip. One is the hub's default, one is the diffusion tool's own.

Three attempts, three different walls, and the third is the one worth remembering.

| Attempt | Died at | Killed by |
|---|---|---|
| 1 | **302 s**, `progress: 0` | `Dispatcher:TimeoutSeconds`, default **300** |
| 2 | **900 s**, `progress: 0` | `requestTimeoutSeconds: 900` in `diffusion.json`, with `qwen-360`'s prefetch quantizing a 20B model on the same card |
| 3 | **900 s**, `progress: 99` | the same 900 s — on a **clean card**, after all 30 steps, **inside the VAE decode** |

Attempt 3 is the finding. The denoising loop finished; the clip died in the decode, nine minutes of
card spent, at the last thing it had to do.

`Dispatcher:TimeoutSeconds` covers tool jobs as well as inference, and 300 s does not cover the cold
model load alone — `wan-t2v-1.3b` took **142.9 s** to load (14.4 s warm, once the weights were in the
page cache). Phase 57 shipped an asynchronous surface, so nobody is holding a socket; what nothing in
57, 58 or 59 revisited is the *dispatch* deadline, which is the one chat has had since v1.x.

**Fixed on the tool side.** `requestTimeoutSeconds` in `python/manifests/diffusion.json` goes
**900 → 3600**: a manifest that declares `video` needs a deadline sized for one.
`AManifestThatServesVideoAllowsAVideoLengthRequest` keys the floor on the manifest declaring the
kind, so a tool that only draws pictures is not made to wait an hour on a wedge.

**Not fixed on the hub side, and this is the finding left open.** One number covers every kind of
job; raising it to cover video hands a wedged *chat* job the same half hour. The right answer is a
per-capability deadline, which is config surface — a feature, and this release ships none (the one
surviving non-goal). What was done instead is documentation where somebody meets it: the measured
figures and a recommended 1800 are now in the `//Dispatcher` comment in `appsettings.json` and in the
README's config table. **Every video number in this file was measured with
`Dispatcher__TimeoutSeconds=1800`**, so none of them is a default-configuration result.

### F6 — `wan-t2v-1.3b` is the only video recipe that does not set `vaeTiling`, and that omission is the 24 GB peak.

Measured on a clean card, one clip, `832x480`, 81 frames, 30 steps:

| Phase of the job | VRAM in use |
|---|---|
| idle, worker up | ~850 MiB |
| denoising, all 30 steps | **17 829 MiB** |
| VAE decode | **24 265 MiB** — of a 24 564 MiB card |

Against a declared `vramMiB` of **15 500**.

The peak is not the model. It is the decode materialising all 81 frames at full resolution at once,
which is **exactly what phase 58 D3 wrote `vaeTiling` for**:

> *"a video job's peak allocation is at decode and it lands after all the expensive minutes. The loop
> holds a latent; the VAE then materialises every frame at full resolution at once."*

`cogvideox-2b` sets `vaeTiling: true`. `wan-t2v-14b-720p` sets `vaeTiling: true`.
**`wan-t2v-1.3b` does not** — it was written in phase 57, the field arrived in phase 58, and 58 set it
on the two recipes it authored without going back to the one that already existed. The same species
as F3: a later phase adds a mechanism and does not apply it to the earlier artifact.

**So `vramMiB` was not corrected to 24 265.** That number describes a misconfigured recipe, and
acting on it would have refused the flagship video model on the 24 GB card the catalogue was sized
for — over a missing boolean. `vaeTiling: true` is set, the clip re-measured, and the declaration
corrected from the tiled peak.

**The cost, stated rather than absorbed.** Phase 58 D3 rejected *always* tiling because it trades
tile seams for headroom, and 49 D5's lesson is that such a trade belongs to whoever asked for it.
Setting it here makes that choice on the operator's behalf — for a model that otherwise does not
finish on its catalogued hardware. So the seam it buys is measured the way 49 D5 measures the other
one: the same prompt and the same seed, tiled and untiled, both watched.

## The fixes, and how each was verified

Every one was checked against the artifact it was found on, not only against the suite.

| # | Fix | Verified by |
|---|---|---|
| F1 | `console.js` `fetchStatus` sends the admin key (client key as fallback); `BearerApiKeyMiddleware` accepts an admin key on `/api/status` only | Patched coordinator, `RequireAuthForLoopback=true`, off-loopback: admin **200**, client **200**, junk **401**, no key **401**, and admin on `/v1/chat/completions` still **401** |
| F2 | `INFERHUB_IMAGE_VRAM_BUDGET_MIB` reaches the worker; `offered()` gains a third gate | Published image, patched worker: `catalogue: 4 recipe(s), 3 offerable`, and *"not offering 'wan-t2v-14b-720p': it needs 24000 MiB and this node budgets 20952 MiB … Its weights are not fetched either"* |
| F3 | `video` kind in `python/manifests/diffusion.json` | Published image, patched manifest: hub reads `video 1 ['wan-t2v-1.3b']` |
| F4 | `ftfy==6.3.1`, `peft==0.17.1` pinned + asserted at build | Installed into the published container; the video request got past `prompt_clean` and rendered |
| F5 | `requestTimeoutSeconds` 900 → 3600 in `diffusion.json` (tool side); measured figures + a recommended 1800 documented in `appsettings.json` and the README (hub side) | Clip completed in 378 s where 900 s had killed it at step 30 of 30; the hub side is **not** fixed and says so |
| F6 | `vaeTiling: true` on `wan-t2v-1.3b`, the field phase 58 D3 built and set on the other two video recipes | Peak 24 287 → **18 540 MiB**, decode >22 min → seconds, and the first clip this project has produced; `vramMiB` 15 500 → 18 000 from the measurement |

**Suite after the fixes: 1 327 passed, 48 skipped** (was 1 318 / 48). Nine new tests, named for what
they hold rather than for the code they call:
`TheDiffusionManifestDeclaresAKindForEveryMediaItsRecipesShip`,
`TheDiffusionImagePinsAndAssertsTheBackendsDiffusersDoesNotDeclare`,
`TheModelVramBudgetIsStatedIntoTheWorkersEnvironmentAndIsAbsentWhenUndeclared`,
`StatusAcceptsAnAdminKeyAsWellAsAClientKey`, `StatusStillRejectsATokenThatIsNeitherScope`, and
`AnAdminKeyStillCannotRunInference` over four routes.

## Addendum — the artifact check this release said it could not do for itself, done the same day

Written after publication. The release notes and the blog post both state that v3.28.0's own images
had not been pulled, and that confirming the fixes in the artifact was v3.29.0's first task. That was
true when both were written; `docker-publish` finished 14 minutes after the tag, so it was done the
same evening instead. **The GitHub release carries this addendum; the blog post does not and cannot —
the connector is insert-only and its slug locks. The post describes the state at publication, which
is what it claimed to describe.**

`ghcr.io/dev-art-solutions/inferhub-node:3.28.0-diffusion` → `sha256:33b7ff30bc095357586ce24e155dec9a8b9337a7900de16a05a5534d7fa5b6bf`,
`org.opencontainers.image.revision` = `79bee9d` — the phase-60 commit. `:diffusion` resolves to the
same digest, `:latest` is still the 352 MB base node, coordinator `3.28.0` and `:latest` agree.

**Read out of the image, with nothing mounted:**

```
manifest:  kinds ['image', 'image-edit', 'video']    requestTimeoutSeconds 3600
packages:  ftfy 6.3.1 | peft 0.17.1 | diffusers is_ftfy_available() and is_peft_available() both True
recipe:    wan-t2v-1.3b  vramMiB 18000  vaeTiling true
worker:    INFERHUB_IMAGE_VRAM_BUDGET_MIB present in diffusion_worker.py
```

**Run as an operator would, full default catalogue, no mounts of any kind:**

```
not offering 'wan-t2v-14b-720p': it needs 24000 MiB and this node budgets 20952 MiB for models
(Node:Vram:BudgetMiB minus Node:Vram:ReserveMiB). Its weights are not fetched either.
catalogue: 10 recipe(s), 7 offerable on this box, at most 1 resident at a time
offering recipes: qwen-360, sdxl (editing: sdxl; video: wan-t2v-1.3b)
```

F2 fires from the shipped worker with the budget from the shipped node binary; F3 reaches the hub,
which reads `video ['wan-t2v-1.3b']`. The licence gate is unchanged — `sd35-medium` and `sdxl-turbo`
are still refused by name with the text to accept.

**F1, against the published coordinator** (`3.28.0`, off-loopback): admin key **200**, client key
**200**, an unknown token **401**, and an admin key on `/v1/chat/completions` still **401**.

**A clip, from the artifact:** 362 s, 81 frames, `seconds: 5.06`, VRAM peak 19 197 MiB (a prefetch of
four other recipes was running on the same card), **982 330 bytes — byte-identical to the clip
rendered before the release from mounted files, at the same seed.** Frame 40 extracted and looked at:
the same paper boat on the same wet cobbles. Determinism across a rebuilt container and a different
image, which nothing had checked before.

**One observation, not a defect.** A fresh node with the default recipe directory fetches every
offerable recipe at once — here `cogvideox-2b`, `flux-schnell`, `qwen-image` and `sd15` began
downloading behind the ones already cached, and the disk went from 88 GB free to 74 while this ran.
It is documented behaviour ("weights are fetched on a background thread and a recipe is offered only
once it is proven loadable") and the cost is real: an operator meets it when the volume fills, not
when they read the sentence.
