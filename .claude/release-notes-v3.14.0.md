# InferHub v3.14.0 — Stable Diffusion, answered by your own card

v3.8 taught the fleet to route on **what a node can do** rather than on which model names it holds.
This release is the clearest evidence yet that it was worth doing: a whole new modality landed with
**no protocol change at all**. `image` is one more capability kind, the request travels as the
`ToolJob` v3.8 designed, and neither the dispatcher, the router, the affinity nor the mesh learned
anything.

```bash
curl http://localhost:5080/v1/images/generations \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"sdxl","prompt":"a lighthouse in a storm, oil painting","size":"1024x1024","n":1}' \
  | jq -r '.data[0].b64_json' | base64 -d > out.png
```

It is OpenAI's Images API, so pointing an existing app at your own hardware is a base-URL change.
The same route is served by a **standalone node with no coordinator at all**, and by one container:

```bash
docker run -d --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$KEY" -v inferhub-images:/data -p 5083:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:diffusion
```

## Two models, and both of them just run

| Recipe | Repo | Size | Licence | On a CPU |
|---|---|---:|---|---|
| `sdxl` | `stabilityai/stable-diffusion-xl-base-1.0` | ~7 GB fp16 | CreativeML OpenRAIL++-M | no — minutes per image |
| `sd15` | `stable-diffusion-v1-5/stable-diffusion-v1-5` | ~2 GB fp16 | CreativeML OpenRAIL-M | yes, at 512×512 |

Both at fp16, **no quantization**: `sdxl` fits an 8 GB card. That is why these two are first and not
FLUX.1-schnell — it is 12B and does not comfortably fit a 24 GB card at bf16, so leading with it
would have meant shipping the client dialect, a VRAM budget and a quantization path in one release,
and the first bug would have had three plausible causes. FLUX and Qwen-Image are next, with the
machinery they need.

**The `model` you send is a recipe id, not a Hugging Face repo id.** A repo id is a *location*: it
has a slash in it that every router and metrics label has an opinion about, and it changes when a
model is re-hosted. That is not hypothetical — `runwayml/stable-diffusion-v1-5` was withdrawn, and
`sd15` points at where those weights live now. A recipe (`python/recipes/*.json`) carries the repo, a
**pinned commit sha**, the pipeline class and the aspect buckets; one with no pinned revision is
skipped and logged, because "which weights were in 3.14.0" has to have an answer.

## Sizes are a list, not a range

SDXL was trained on fixed aspect buckets, and a size outside them **does not fail** — it produces
duplicated limbs and doubled horizons, which reads as "this model is bad" rather than "you asked for
1000×1000". A size the recipe does not have is a `400` naming the ones it does. Nothing here is ever
silently substituted.

`steps`, `guidance` and `seed` are InferHub extensions and travel as `X-InferHub-Image-*` headers, so
they cannot collide with whatever OpenAI adds next; an unknown value is a `400`, never a silent
fallback. `seed` is also a body field, because it is the first thing anybody reaches for — and every
returned image carries the seed that produced it, so the one of four you liked is reproducible
without re-rolling the other three.

`negative_prompt` is a **body** field and deliberately not a header: it is your own words, and a
header is the one part of a request every proxy in the path writes into a log.

## You need a card, and this release says so rather than being quietly slow

`Tools:Image:RequireGpu` defaults to **true**: with no reachable CUDA device the worker refuses to
start and names the key to unset. A tool that loads happily on a CPU and then serves four-minute
requests is a node the fleet keeps routing to, and every caller pays for the discovery.

Unset it and only recipes marked `cpuViable` are offered — `sd15` at 512×512, and not `sdxl`.
`Tools:Image:AllowSlowCpu=true` offers the rest anyway: your hardware, your call. There is no
"CPU ✅" for the feature as a whole anywhere in the docs, because that tick would be true of one
recipe and a lie about the other.

## No URL, no gallery, and no prompt in a log

A transcript is content because it is what somebody said. **A prompt is content because it is what
somebody wanted**, and the picture is the answer — which makes an image request the most revealing
thing a caller sends this fleet.

- `response_format=url` is a **`400`** naming `b64_json`. Serving a URL means the hub keeps the
  bytes, and keeping the bytes means an image store, a retention window, a deletion endpoint and a
  question about whose pictures those are that we have not agreed to answer.
- Nothing logs a prompt, at any level, on either host. The line for a generation carries the model,
  the image count, the megapixel-steps and the outcome.
- The node writes images into a per-request scratch directory deleted in a `finally` — after success
  **and** after failure.
- **There is no bundled safety classifier**, and that is a decision rather than an omission:
  `diffusers`' checker returns a *black image*, which is indistinguishable from a broken VAE, a bad
  seed or an out-of-memory error, so the operator gets a bug report instead of a policy signal. This
  box generates what you ask it to generate; the policy is yours.

## Metered in megapixel-steps, not in images

`width × height × steps / 1e6`, from what the worker actually produced. A 512² render at 4 steps and
a 2048×1024 one at 30 steps are both "one image", and the second is **47×** the work — a counter that
bills them the same is wrong in a way that scales with how much somebody uses the expensive path.
`Limits.MegapixelStepsPerDay` rejects with a `402` and a `Retry-After` pointing at UTC midnight, the
same shape as every other budget.

**No database migration.** v3.10's generic `units` + `unit_kind` pair took a fourth unit with a query
change and no DDL, so a v3.13 ledger reads correctly on v3.14 and vice versa.

## The v3.10.0 wire bug, asserted rather than remembered

v3.10.0 shipped dead on arrival because a 300 KB WAV exceeded SignalR's 32 KB default message size —
which **tears the connection down** rather than failing the message — and phase 41 had "proved"
attachments with a 16-byte file.

An image is where that stops being an edge case: a 1024×1024 PNG is megabytes, every time, on the
happy path. So `n` is bounded by a **byte budget**, not a count: an upper-bound estimate checked
before a single diffusion step runs, with the refusal naming the largest `n` that fits at that size.
The budget is clamped by `Tools:MaxAttachmentBytes`, because that is what sizes the mesh's message
limit — raising one without the other gets you no change, deliberately, so the dangerous combination
is inexpressible rather than merely discouraged.

`ImageWireSizeTests` pushes 3 MB across a real SignalR connection and asserts **the node is still
registered afterwards**, which is the assertion a green suite got wrong last time.

## A fifth image, and it is the one that does not stack

| Tag | Size | Arch | What is in it |
|---|---|---:|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | The hub |
| `inferhub-node` | ~340 MB | amd64 + arm64 | A node. No inference engine |
| `inferhub-node:ollama` | ~4 GB | amd64 | The same node with Ollama inside it |
| `inferhub-node:tools` | ~6 GB | amd64 | The same again, plus Whisper and Piper |
| `inferhub-node:diffusion` | ~9 GB | amd64 | **The plain node plus PyTorch, diffusers, SDXL and SD 1.5** |

`:diffusion` is built from the plain node, not from `:tools`, and has no Ollama in it. Three reasons,
in order: stacking reaches ~15 GB and every pull pays for it; a card running a diffusion pipeline has
no room for a chat model beside it, so bundling one would ship a combination the docs would then have
to tell you not to use; and **the mesh is the composition mechanism** — run `:diffusion` on the card
and `:ollama` next to it, and the coordinator routes `image` to one and `chat` to the other. Which is
what v3.8's capability routing was built for, paying for itself again.

The first four images are **unchanged**, and a test asserts none of them learned about torch.

## Zero new dependencies, again

No `PackageReference` was added. **Nothing in C# decodes a pixel** — the worker produces the bytes,
the edge base64s them, and the hub's only knowledge of an image is what the worker reports. That is
what keeps the dependency rule intact through a release that is *about* images.
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`, and PyTorch is a
subprocess rather than a binding.

`dotnet test`: **1076 passed, 0 failed, 46 skipped**, every pre-existing suite untouched.

## Not in this release, and said out loud

Async jobs with progress and cancellation — every model after these two is slow enough to need them,
and they are next. Also: img2img, inpainting, `/v1/images/edits`, `/v1/images/variations`,
ControlNet, and any model that needs quantization to fit a card.

**And one thing that is not done rather than not planned:** the published `:3.14.0-diffusion` image
has not been pulled and run, no generated PNG has been opened, and no wall time has been measured on
a real card. Everything above is proved by a real child process across a real SignalR wire and by
nothing on a GPU. This project has shipped five green-tested artifacts that were dead on arrival —
treat this tag as unverified until that check has been run.
