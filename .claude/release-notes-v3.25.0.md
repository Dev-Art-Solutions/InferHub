# v3.25.0 — the fleet renders video, on an API we did not invent

Phase 57 of the v3.22–v3.28 track. A node with a card can now be asked for a short clip, and the
client surface is **OpenAI's Videos API** rather than one of ours.

```bash
curl -s http://localhost:5080/v1/videos -H "Authorization: Bearer $KEY" \
     -H 'Content-Type: application/json' \
     -d '{"model":"wan-t2v-1.3b","prompt":"a paper boat on a puddle, slow dolly in","seconds":5}'
# {"id":"video_8f3c…","object":"video","status":"queued","progress":0,"seconds":5,"size":"832x480"}

curl -s http://localhost:5080/v1/videos/video_8f3c…            # poll: status + progress
curl -s http://localhost:5080/v1/videos/video_8f3c…/content -o out.mp4   # the bytes, once
curl -sX DELETE http://localhost:5080/v1/videos/video_8f3c…    # cancel and drop
```

## Why this one is adopted and the image one was invented

v3.15 built `/api/images/jobs` on an explicit premise: *"OpenAI has no asynchronous Images API to
adopt"*, and this project's rule is to speak the dialect clients already speak and to invent only
where there is none. **Video has one, and it is asynchronous by construction** — create, poll,
fetch, delete. So we adopted it and added nothing: `client.videos.create(...)` in any OpenAI SDK
works against a hub that can serve it.

Two of that dialect's routes are **501s that name the reason**, not 404s that read as "an old
server". `GET /v1/videos` enumerates a client's jobs and this coordinator holds no such index — the
id it returns *is* the capability to fetch the bytes. `POST /v1/videos/{id}/remix` needs the request
kept after the job ends, which v3.24 forbids in the sentence it was built on.

The status mapping loses exactly one distinction, deliberately: `cancelling` renders as
`in_progress`, because a job asked to stop is still running and may finish. `progress` is capped at
**99** until the job is terminal — a client that sees 100 and stops polling has stopped one round
trip before the bytes exist.

## It is the same job model, which is why phase 56 came first

The queue, the per-step progress, the cooperative cancel that leaves the worker holding its weights,
the read-once retention, the `node_lost` refusal to silently retry and v3.24's optional durability
are the **same code** the image jobs run. `Images:Jobs:*` configures both and
`Images:MaxResponseBytes` bounds both. **There is no `Videos:` config section** — a deliberate
deviation from this phase's own task list, because two keys for one wire ceiling are two numbers an
operator can raise independently, which is the v3.10.0 connection-tearing bug rebuilt.

A video id will not open an image route and an image id will not open a video route: both are the
same `404` an unknown id earns.

**The names stay `ImageJob*`, and the rename is refused in writing.** They are wrong by one word. A
phase that adds a modality is perfect cover for "while I was in there", and a bisect holding a new
pipeline *and* four hundred renamed references is a bad afternoon. Phase 59 opens those files anyway.

## The model, and four things about it that a brief written ahead would have got wrong

`wan-t2v-1.3b` = `Wan-AI/Wan2.1-T2V-1.3B-Diffusers`, **Apache-2.0**, 832×480 or 480×832, 2–5 s at
16 fps. Every one of these was read out of the pinned `diffusers==0.36.0` wheel and the model's own
configs, and every one produces a *plausible non-failure* when it is wrong:

1. **The VAE loads separately, in `float32`, under a `bfloat16` transformer.** A uniform bf16 load
   does not error — it is the difference between a video and noise, four minutes in.
2. **`flow_shift` is a scheduler setting, not a call argument — and this repo already sets it.**
   Upstream's example sets 5.0 by hand because it is written for the 14B 720p model; the 1.3B repo
   ships `flow_shift: 3.0` (the 480p value) in `scheduler_config.json`. *This release's first draft
   said the opposite, before the config was read.*
3. **Sizes divide by 16 and frames sit on a 4k+1 grid.** `840x480` is a perfectly good *image* size
   and an invalid video one. `seconds: 5` names 81 frames, and 81 frames at 16 fps is **5.0625 s** —
   which is what the response reports. A duration the recipe does not offer is refused naming the
   list, never rounded: a caller who asks for six seconds and silently gets five has a clip that is
   fine and wrong.
4. **"1.3B" names the transformer only.** The text encoder is UMT5-XXL at ~11B, every weight in the
   repo is fp32 with **no fp16 variant**, and the first pull is **~29 GB**. `vramMiB` is 15 500 for
   that reason. This is v3.14.1's variant-is-not-dtype lesson in the one shape where there is nothing
   to fix — there is no variant to ask for — so it is documented rather than discovered.

## The encoder refuses to be silently wrong

`diffusers.utils.export_to_video` **warns and drops to an OpenCV `mp4v` writer** when `imageio` is
absent: a container nobody chose, from a code path nobody read, on a job that already cost minutes of
card. So `imageio` + `imageio-ffmpeg` are pinned, `Dockerfile.diffusion` **asserts the import at
build time**, and a worker that cannot encode does not declare `video` at all. `imageio-ffmpeg`
vendors a static ffmpeg binary inside the wheel, so "which encoder was in 3.25.0" has a version for
an answer rather than whatever the distribution mirror held that week.

## Metering: two units, and only one of them is a quota

`megapixel_steps` (`width × height × frames × steps / 1e6`) **and** `video_seconds`, on the same job
— phase 42's audio precedent, where a transcription meters seconds and a synthesis characters. A
video transformer denoises the whole latent stack every step, so pixels × steps is literally what the
card spent: a 5-second 832×480 clip at 30 steps is ~**970** megapixel-steps against an SDXL image's
31. That is why it spends the *same* `MegapixelStepsPerDay` budget — it is the same card. There is
deliberately **no `VideoSecondsPerDay`**: a quota knob for a one-model catalogue is a key that is
wrong by the time the catalogue lands.

`AdmissionControl` had to learn the new kind, and that is worth naming: its `default` branch counts
whatever reaches it as **tokens**, so without a case five seconds of video would have quietly spent
five tokens of somebody's per-minute budget.

## Rules 5 and 7

**Zero new `PackageReference`.** `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`, nothing in C# decodes a frame, and the encoder is a `pip
install` in one Dockerfile reached as a child process — exactly where PyTorch has been since v3.14.

**Rule 7 met its fourth kind of content.** A video is content, and so is the frame nobody sees: a
decoded first frame would make a lovely progress thumbnail and it is a picture of what somebody
asked for. Nothing decodes or writes one. `VideoJobTests` drives a real request through a real mesh
with a capturing logger at `Trace` and fails if the prompt appears in the log or the ledger.

## Tests

`dotnet test InferHub.sln`: **1 305 passed / 48 skipped**, no failures.
The phase's own slice — `Tests.Node` (148/3 skipped) and `Tests.Mesh` (395/2 skipped) — is green.
New: `VideoContractTests` (14 in `Tests.Shared`), `VideoJobTests` (8, real mesh, real child
process), `SoloVideoTests` (3), three new `OpenAiAuthTests` rows.

*One flake worth writing down rather than hiding:* on one full-solution run
`ToolUploadTests.AnUploadPastTheStreamedCeilingIsRefusedNamingTheStreamedKey` failed with a bare
socket error, and passed in isolation and on an immediate full re-run of the same project. The new
video suites add real child processes to `Tests.Mesh`, so this box is doing more at once than it was
on v3.24; it is not a claim about the product and it is not being called one.

## What was NOT established, said out loud

- **No video has been rendered on a GPU and none has been watched.** Every claim about Wan2.1 above
  comes from its configs and from the pinned wheel; the test worker writes a real ISO-BMFF container
  whose samples are padding, which proves the surface and proves nothing about the picture. That is
  the published-image check's job and phase 60's.
- **The ~29 GB download has not been performed**, so the first-pull experience is unmeasured.
- **A video recipe refused for its licence or its VRAM budget is invisible at the hub.**
  `NodeToolState.Images` was deliberately not widened — it is what the v3.19 Images panel renders and
  what `ConsoleContractTests` pins — so until phase 59 the diagnostic gap v3.19 closed for images is
  open for video. This is a known cost, not an oversight.
- **No console panel, no per-recipe video status, no SSE.** All three are phase 59.
- **No image-to-video, no caller-chosen fps, no audio track.** Named as non-goals, not forgotten.

## Published-image check

Done, on both images this phase touches. Both carry revision **`781ccd5`**, the phase commit, and
all five tags are anonymously pullable with **no manual flip** — Gotcha 1 for the twelfth time.

**`inferhub-node:3.25.0` (352 MB), solo mode, every video route driven through a real container.**
This is the check that exists because v3.5.0 shipped solo mode *mapped and unreachable* in Docker:

| | |
|---|---|
| `POST /v1/videos` with no key | **401** — phase-21 D2 checked, not assumed |
| `POST /v1/videos` with a key | **503 + `Retry-After: 30`**, `capability_unavailable`, naming the model |
| `GET /v1/videos` | **501**, with the sentence about holding no index |
| `POST /v1/videos/{id}/remix` | **501**, with the sentence about nothing durable holding a prompt |
| `GET`/`DELETE /v1/videos/{id}`, `/content` | **404** — mapped, reachable, and scoped |
| `size: "840x480"` | **400** naming the /16 grid *at the edge*, before any dispatch |
| `GET /api/images/jobs` | **200** — the image surface is untouched |

**`inferhub-node:3.25.0-diffusion` (12.5 GB) — the four claims a green suite cannot make.**

- **D8 holds on the artifact.** `imageio 2.37.0` + `imageio-ffmpeg 0.6.0`, and
  `imageio.plugins.ffmpeg.get_exe()` resolves to a **76 MB static `ffmpeg-linux-x86_64-v7.0.2`
  inside the wheel** — which is the whole reason there is no `apt-get install ffmpeg` beside it.
  `export_to_video` imports, so the OpenCV fallback path is unreachable. `can_encode_video()` → `True`.
- **`WanPipeline` and `AutoencoderKLWan` both resolve** in `diffusers 0.36.0`, which is the
  build-time assertion re-run against the shipped venv rather than against the build log.
- **The recipe is the one that was written**: `Wan-AI/Wan2.1-T2V-1.3B-Diffusers` at `0fad780a534b`,
  Apache-2.0 and permissive, `AutoencoderKLWan` at **float32** under a **bfloat16** transformer,
  `schedulerFlowShift: 3.0`, `vramMiB: 15500`. **`media == video` for exactly one recipe** and the
  other seven read as `image` — 40 D1's "absent means today's behaviour", confirmed on disk.
- **`capability_frames` emits three kinds**, `video: ['wan-t2v-1.3b']` beside `image` and
  `image-edit`, and the worker's own refusals answer with the list: `seconds=5 → (81 frames, 16 fps)`,
  `seconds=3 → 49`, and **`seconds=6` → refused naming `2, 3, 4, 5`** with the latent-grid reason.
  `840x480` is refused naming the buckets.
- **The CPU and licence gates cover video, which is 57 D3's whole point.** On a CPU-only box the
  offer collapses to `['sd15']` and the log says *"not offering 'wan-t2v-1.3b' on a CPU-only node"*
  — so the hub is never routed at a card that is not there. With a card and nothing accepted, the
  offer is the six permissive recipes **including** `wan-t2v-1.3b`, with `sd35-medium` and
  `sdxl-turbo` named and dropped.

**Still not done, and still said out loud: nothing was rendered.** This box has no CUDA, so no
weights were fetched, no frame was decoded and no clip was watched. Everything above is about the
*catalogue and the edge* on the published artifact; everything about the *picture* is phase 60's.
