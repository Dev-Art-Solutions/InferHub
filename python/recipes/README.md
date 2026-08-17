# Image recipes (phase 46, catalogued in phase 48, adapters in phase 49, editing in phase 50)

A **recipe** is a model the diffusion worker can run. A **manifest** (`../manifests/diffusion.json`)
is the *tool* — the argv, the environment, the timeouts, and the thing `Tools:Allowed` names.

They are two files on purpose. The manifest is the **operator's ceiling**: `Tools:Enabled` consents
to the feature and `Tools:Allowed` names the tools that may start, and phase 43's node profiles can
narrow that but never widen it. Recipes are a **catalogue** the tool reads. Collapsing them would
make every new model a new entry in `Tools:Allowed`, and a profile could then not enable a model the
operator had not pre-named — which sounds like the right ceiling and is the wrong one: the operator
consented to running the diffusion tool, and *which of its models are on* is exactly what a profile
is for.

## What ships

| Recipe | Params | Steps | VRAM | Licence | Operations | Runs out of the box? |
|---|---|---|---|---|---|---|
| `sdxl` | 2.6B UNet | 30 | ~8 GB, fp16 | CreativeML OpenRAIL++-M | generate, **edit, variation** | yes |
| `sd15` | 0.9B | 30 | ~4 GB, fp16 | CreativeML OpenRAIL-M | generate, **edit, variation** | yes — and the only CPU-viable one |
| `flux-schnell` | 12B | **4** | ~12 GB at nf4 (**~33 GB** at bf16) | Apache-2.0 | generate | **no — HF token**, gated repo |
| `qwen-image` | 20B + 8.3B text encoder | 30 | ~19 GB at nf4 (**~60 GB** at bf16) | Apache-2.0 | generate | yes |
| `sd35-medium` | 2.5B MMDiT | 40 | ~16 GB, bf16 | Stability AI Community | generate | **no — licence + HF token** |
| `sdxl-turbo` | 2.6B | **1** | ~8 GB, fp16 | Stability AI Non-Commercial | generate | **no — accept the licence** |
| `qwen-360` | 20B + a rank-128 LoRA | 25 | ~19.5 GB at nf4 | Apache-2.0 base, **MIT** adapter | generate | yes — and it produces 360° panoramas |
| `wan-t2v-1.3b` | 1.3B DiT + **11B text encoder** | 30 | ~15.5 GB, bf16 | Apache-2.0 | generate (**video**) | yes — 480p, 2–5 s, ~29 GB to download |
| `wan-t2v-14b-720p` | 14B DiT + the same encoder | 30 | **~24 GB at nf4** (~50 GB at bf16) | Apache-2.0 | generate (**video**) | **not on a 24 GB card** — 720p, 2–5 s, ~75 GB to download |
| `cogvideox-2b` | 1.6B DiT + 4.7B T5 | 50 | ~16 GB, fp16 | Apache-2.0 | generate (**video**) | yes — 720×480, **8 fps**, one 6 s offer, ~13 GB to download |

**`wan-t2v-14b-720p` is the first recipe this project ships that does not fit a 24 GB card**, and
that is the VRAM gate working rather than a packaging mistake: a node with 24 GB does not declare it,
so the hub never routes to it and nobody discovers the ceiling inside a four-minute job. See "the
declared figure is the largest pair" below.

Two of those numbers are the point of this phase. `flux-schnell` and `qwen-image` **do not fit a
24 GB card at bf16** — 33 GB and 60 GB respectively — and nf4 is what makes them one-card models.
`sd35-medium` and `sdxl-turbo` fit fine and need a *licence decision*, which is not ours to make.

**`flux-schnell` and `sd35-medium` are *gated* on Hugging Face, and that is a different axis from the
licence.** FLUX is Apache-2.0 — so this project's licence gate lets it straight through, correctly —
and Hugging Face *still* requires you to accept terms on the model page and present a read token.
Accepting a licence in `Tools:Image:AcceptedLicenses` tells **this node** it may run a model; a token
is how **Hugging Face** decides whether to hand the weights over. Set
`Tools:Image:HuggingFaceToken`, which has to be that key rather than an `HF_TOKEN` environment
variable because the node clears a worker's environment before spawning it (phase-41 D3).

`"gated": true` in a recipe is documentation only — nothing reads it. The failure it describes is
reported when the fetch actually happens, naming the model page and the key rather than surfacing a
bare `401` that reads as "the model is gone".

> Found by running the published v3.16.0 image: FLUX's fetch failed with
> `GatedRepoError: 401 Client Error` and a request id, while the docs said it ran out of the box.

## The fields

```jsonc
{
  "id": "flux-schnell",            // the name a CLIENT sends as `model`. Not the repo id.
  "repo": "black-forest-labs/…",   // where the weights come from
  "revision": "741f7c3ce8b3…",     // REQUIRED. A commit sha, never a branch.
  "pipeline": "FluxPipeline",      // a class name in `diffusers`
  "variant": "fp16",               // WHICH FILES to download. Not the same thing as dtype.
  "dtype": "bfloat16",             // what they are cast to in memory
  "license": { "id": "Apache-2.0", "permissive": true, "url": "https://…" },
  "gated": true,                   // documentation only: the repo also needs HF_TOKEN
  "operations": ["generate"],      // phase 50. generate | edit | variation. Absent = ["generate"].
  "defaults": { "steps": 4, "guidance": 0.0, "size": "1024x1024",
                "strength": 0.75 },          // phase 50, and only meaningful for edit/variation
  "sizes": ["1024x1024", "1152x896", …],     // the aspect buckets, exactly
  "maxSteps": 8,
  "vramMiB": 12000,                // the QUANTIZED figure — what the node budgets against
  "vramUnquantizedMiB": 33000,     // documentation only: what it would need without nf4
  "quantization": "nf4",           // none | int8 | nf4
  "quantizeComponents": ["transformer", "text_encoder_2"],
  "cpuViable": false,

  // Phase 49, and all five are optional.
  "adapters": [                    // LoRAs stacked on the base, in order
    { "name": "qwen-360",
      "repo": "ProGamerGov/qwen-360-diffusion",
      "revision": "f63e5de6…",     // REQUIRED here too — a second repo is a second pin
      "weightFile": "qwen-360-diffusion-int8-bf16-v1.safetensors",
      "scale": 1.0 }
  ],
  "projection": "equirectangular", // flat (default) | equirectangular
  "trigger": "360 degree panorama with equirectangular projection",
  "autoTrigger": true,             // append the trigger when the prompt lacks it
  "guidanceParameter": "true_cfg_scale",  // default: guidance_scale

  // Phase 57, and all six are optional. Absent `media` means `image`, which is why the seven
  // recipes that predate video did not change by a byte.
  "media": "video",                // image (default) | video
  "vaeClass": "AutoencoderKLWan",  // loaded SEPARATELY, at `vaeDtype`, and passed in
  "vaeDtype": "float32",           // NOT the transformer's dtype. See below — this one is load-bearing.
  "schedulerFlowShift": 3.0,       // re-derives the scheduler from its config. 3.0 = 480p, 5.0 = 720p.
  "fps": 16,                       // the rate the model was TRAINED at. Not a caller's knob, and
                                   // since phase 58 REQUIRED — there is no default.
  "durations": [                   // whole seconds → frame counts. REQUIRED. See below.
    { "seconds": 2, "frames": 33 },
    { "seconds": 5, "frames": 81 }
  ],

  // Phase 58, and it applies to image recipes too — it is video that needs it.
  "vaeTiling": true                // decode in tiles: the peak allocation is at DECODE, not denoise
}
```

### A video recipe is an image recipe with `media` and a clock

Everything above still applies — the pin, the licence gate, the VRAM budget, `sizes`, `maxSteps`,
`cpuViable`. What video adds is four facts that were read out of `diffusers==0.36.0` and the model's
own configs rather than guessed, because **each one produces a plausible non-failure when it is
wrong**:

- **`vaeDtype` is not `dtype`.** Wan loads `AutoencoderKLWan` at **float32** under a **bfloat16**
  transformer, exactly as upstream's example does. A uniform bf16 load does not error; it is the
  difference between a video and noise, discovered four minutes into a job. `vaeClass` names the
  class rather than inferring it, because inferring which VAE a repo wants is a capability registry
  by the back door (29 D5).
- **`schedulerFlowShift` is a scheduler setting, not a call argument.** Passing it to `__call__` is a
  `TypeError`. This repo already ships `flow_shift: 3.0` in `scheduler/scheduler_config.json`, so the
  field matches what is checked in; the mechanism exists for a 720p entry, where 5.0 is wanted.
- **`sizes` sit on a 16 grid, not an 8 grid.** `WanPipeline.check_inputs` requires both sides
  divisible by 16 where every image pipeline here downsamples by 8, so `840x480` is a perfectly good
  *image* size that a video pipeline refuses.
- **`durations` maps whole seconds to frame counts, and the two do not divide evenly.** A latent
  video pipeline puts frames on a **4k+1** grid — `(num_frames - 1) // 4 + 1` latent frames — so 81
  frames at 16 fps is **5.0625 s**, and OpenAI's Videos API takes `seconds` as small integers. The
  list is therefore the *labels*, the response reports the **measured** duration, and an unoffered
  value is refused naming the list rather than rounded to the nearest one: a caller who asks for six
  seconds and silently gets five has a clip that is fine and wrong.

**`fps` is not a caller's knob**, deliberately. Re-timing the frames at encode changes how fast the
world moves in the clip, and the only honest setting is the trained one until somebody measures the
others.

### `fps` and `durations` are required of a video recipe, and there is no default for either

Phase 58 deleted the 16 that `fps` used to fall back to. Wan is 16 and **CogVideoX-2b is 8**; a
fallback is a guess about somebody else's model, and the clip it produces does not fail — it plays
at double speed, which reads as a model that is bad at motion. A video recipe missing either field
is **skipped and logged by name**, before the capability is declared, and so is one whose
`defaults.seconds` is not in its own `durations` list — that one refuses every request from the
caller who named no duration at all, which is the caller who was trusting the recipe.

### The declared `vramMiB` is the largest pair the recipe offers, and an id is a model *and* a geometry

A video model's cost is dominated by the `(size, seconds)` pair: 1280×720 at 81 frames is 2.25× the
latent of 832×480 at the same length. The node's gate is handed **one** number, once, when the
capability is declared — minutes before any caller exists and without ever reading a request body —
so the figure is sized at the **largest** pair the recipe offers, and a recipe for the cheap corner
of a model is a second recipe id with a shorter `sizes`/`durations` list. That is the same shape as
quantization: an operator who wants both ships two ids.

**A video recipe with no `vramMiB` at all is not declared.** For an image recipe, silence still means
"admit rather than guess" — the miss is 4–8 GB and inventing a number would refuse a model the
operator can see on the box. For video the same silence admits a 24 GB model onto a 12 GB card, and
the failure arrives as an out-of-memory error four minutes into somebody's job instead of as a line
in a log at startup.

### `vaeTiling` exists because the peak is at decode

The denoise loop holds a latent; the VAE then materialises **every frame at full resolution at
once**, which for 81 frames at 720p is the largest single allocation in the job — and it lands after
all the expensive minutes. `AutoencoderKLWan` and `AutoencoderKLCogVideoX` both expose
`enable_tiling()` (CogVideoX's own example pairs it with `enable_slicing()`, so both are called where
they exist). It is asked for per recipe rather than always on, because tiling trades a quality risk —
the seams between tiles — and that trade belongs to whoever wrote the recipe. A pipeline whose VAE
has neither method logs and decodes in one piece.

> **"1.3B" names the transformer only.** The text encoder beside it is UMT5-XXL at ~11B, every weight
> in the repo is stored **fp32 with no fp16 variant**, and the download is **~29 GB**. That is the
> `variant`-is-not-`dtype` lesson in the one shape where there is nothing to fix — there is no
> variant to ask for — so `vramMiB` is sized from the encoder and the disk cost is written down here
> rather than discovered on a slow connection.

### `id` is not the repo id, and that is a decision

A client sends `"model": "sdxl"`, not `"model": "stabilityai/stable-diffusion-xl-base-1.0"`. A repo
id is a *location*: it contains a slash that every router, path and metrics label has an opinion
about, and it changes when a model is re-hosted. That is not hypothetical — the original
`runwayml/stable-diffusion-v1-5` repository was **withdrawn**, and the weights `sd15` points at now
live under a different owner entirely. The recipe id is the stable name; the repo id is a field
inside it.

### `revision` is required

Without a pin, "which weights were in 3.14.0" has no answer, and two builds of the same InferHub tag
can contain different models. A recipe with no `revision` is **skipped and logged by name** rather
than loaded against `main`. Same reasoning as phase-39 D9's checksummed Ollama tarball and phase-42's
pinned Python.

### `variant` is not `dtype`, and conflating them cost 7 GB a model

`dtype` is what the weights are cast to **in memory**. `variant` is which **files** are downloaded.
These repos carry both `unet/diffusion_pytorch_model.safetensors` (fp32, 10.3 GB for SDXL) and
`unet/diffusion_pytorch_model.fp16.safetensors` (5.1 GB), and passing only `torch_dtype=float16`
takes the **fp32** one and casts it down — twice the download and twice the disk for a result that is
bit-identical once loaded.

v3.14.0 did exactly that. Found by pulling the published image and watching 13 GB land in the cache
for two models documented at ~9 GB together. A repo with no such variant falls back to the default
files and **says so in the log**, because silently doubling somebody's download is the thing this
field exists to stop.

### `sizes` is a list, not a range

SDXL was trained on a fixed set of aspect buckets. A size outside them **does not fail** — it
produces duplicated limbs, doubled horizons and cropped subjects, which reads as "this model is bad"
rather than "you asked for 1000×1000". So a size the recipe does not have is refused with
`invalid_request` naming the ones it does, and the edge renders that as a `400`.

The **edge** validates what it can know without a recipe: the `WIDTHxHEIGHT` grammar, the 64–4096
bounds, the multiple-of-8 rule (every latent-diffusion pipeline downsamples by 8) and the response
byte budget. Whether a *well-formed* size is one this model was trained on is the worker's question,
because a recipe is a file on the node and the hub has no model catalogue until phase 48. It costs
one round trip to find out; the alternative is publishing a catalogue over the mesh, which is a
phase.

**One bucket did not make it.** Qwen-Image publishes a 4:3 bucket at 1472×1140, and 1140 is not a
multiple of 8 — the edge refuses it before the request ever reaches a node. Rather than ship a size
that 400s or silently round it to a neighbour, `qwen-image` offers the three buckets that are
expressible: 1328×1328, 1664×928 and 928×1664.

### `quantization` is a property of the model, never of the request

`none` | `int8` | `nf4`, applied through `diffusers`' native `bitsandbytes` integration to the
components `quantizeComponents` names. For Qwen-Image that has to include the **text encoder**:
8.3B left at bf16 is the difference between fitting on a 24 GB card and not.

It is a recipe field rather than a request parameter because it changes what the model *is*. Two
requests to `qwen-image` that quantized differently would produce different images from the same
seed, and a per-request knob would make reproducibility a function of a header nobody logged. An
operator who wants both ships two recipes with two ids — which is honest, and is also how they will
describe it to their users.

**One mechanism, deliberately.** GGUF, Nunchaku and TensorRT are each faster on some model on some
card, and each is a second thing to reason about when a picture comes out worse than expected.

`vramMiB` is the **quantized** figure, because that is what the node's budget admits against.
`vramUnquantizedMiB` is documentation: "Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are
both true sentences about different recipes, and a table that gives one number is lying to somebody.

### `license.permissive` decides whether the operator has to say yes

A recipe with `"permissive": false` is **loaded, logged by name and not started** unless its licence
id is in `Tools:Image:AcceptedLicenses`. The log line names the licence and links to it.

This is the fourth opt-in, and it is not redundant with the other three: `Tools:Enabled` consents to
the feature, `Tools:Allowed` consents to *these tools*, `Tools:AllowModelDownload` consents to
reaching the internet — and none of them says "and I accept the Stability AI Non-Commercial Research
Community License". It is a **list** rather than a boolean for the same reason `Tools:Allowed` is:
`sd35-medium` is free for most people who will run it and `sdxl-turbo` is not usable commercially at
all, so one `AcceptNonPermissive=true` would let somebody who read one licence enable both.

A recipe that omits the field is treated as **not** permissive. A recipe that forgot to say is a
recipe nobody has read the licence of, and defaulting the other way would make the consent opt-out
by accident of a missing field.

None of this is legal advice. It is a refusal to make a licence decision on the operator's behalf
and silently, which is the only part of it that is ours to get right.

### `cpuViable` is per recipe, and it is why there is no "CPU ✅" anywhere

`sd15` at 512² is tens of seconds on a modern core and is a real answer for a box with no card.
`sdxl` at 1024² is minutes and is a demonstration. A feature matrix that ticks "CPU" for the whole
feature is a lie told in a checkmark, so the flag is on the model and the docs carry both measured
numbers.

On a CPU-only node, recipes without `cpuViable` are **not declared**, so the hub never routes to
them. `Tools:Image:AllowSlowCpu=true` offers them anyway — the operator has read both numbers and it
is their hardware.

### A recipe is declared only when its weights are ready

The worker offers a recipe once it has **proven it loads**, and a background thread does the
fetching. So on a fresh volume the node starts with nothing declared and fills in as models land,
re-declaring each time — the fleet never routes at a model that is not there, and no request ever
waits on a download.

Readiness is a marker file under `$HF_HOME/.inferhub-ready/`, written after a successful load. The
marker exists because the obvious checks lie: `snapshot_download(local_files_only=True)` and
`DiffusionPipeline.download(local_files_only=True)` both return happily with the UNet **entirely
absent** — verified against a half-downloaded cache. Only `from_pretrained(local_files_only=True)`
asks the question the next request will ask, and it is also the load, so the prefetch does it once
and records the answer. A marker without weights self-heals; weights without a marker cost one
background load.

### Weights arrive by an explicit pull, and swapping does not restart anything

FLUX is ~24 GB on the wire and Qwen-Image is larger. A lazy first-use download inside a request
blows `requestTimeoutSeconds` — v3.14.0 shipped exactly that and every first `sdxl` call was a 502
after 900 seconds — and raising the timeout to cover a 24 GB download means every genuinely wedged
job also takes forty minutes to fail. So a pull is an **operator action** on phase 26's model-command
channel:

```
POST /api/admin/nodes/{nodeId}/tools/diffusion/models/flux-schnell/pull
DELETE /api/admin/nodes/{nodeId}/tools/diffusion/models/flux-schnell
```

Progress relays on the existing `/api/admin/stream` as `model-progress`, so the console gets a
progress bar for free. A generation request for a recipe whose weights are absent is a failed job
naming both that command and the `huggingface-cli` one — never a forty-minute wait.

Switching recipes **swaps weights inside the warm process**: free the old pipeline,
`torch.cuda.empty_cache()`, load the new one, and report the swap in the result's `timing` block so
a slow request has a visible reason. Restarting the process per recipe would pay the interpreter and
the import of torch on every alternation, on top of the weights.
`Tools:Image:ResidentRecipes` (default **1**) allows more than one resident where the budget permits.

### The VRAM budget is declared, not detected

`Node:Vram:BudgetMiB` is a number the **operator** sets, and `Node:Vram:ReserveMiB` (default 2048)
is what is held back for the inference backend and the display. A recipe that cannot fit in
`Budget − Reserve` is **not declared**, so the fleet never routes at it; a recipe that would fit but
does not right now *waits* on the tool queue and then gets the same `503` + `Retry-After` as every
other limit here.

It is declared rather than detected because a node cannot reliably measure the card it is on. Under
WSL2 — where this project's own GPU box lives — there are no `/dev/nvidia*` device nodes, the host's
`nvidia-smi` cannot see the VM's VRAM, and the only reliable signal that a GPU exists at all is that
`libcuda.so.1` loads. A budget that is usually right is worse than one that is explicitly absent,
because the first failure is an out-of-memory error inside somebody's job rather than a startup
message. The worker reports `torch.cuda.mem_get_info()` at startup purely so a **disagreement** gets
logged.

### `adapters` make a recipe a distinct model id, never a flag on an existing one

`qwen-image` and `qwen-360` are the same 20B base and one 378 MB rank-128 LoRA, and they are **two
recipe ids**. The router keys on `(capability, model)` and nothing else, so a client asking for
`qwen-image` must never receive a panorama; a `loraScale` header on a shared id would make what you
get depend on a header, which is the reproducibility problem `quantization` is a recipe field to
avoid.

An adapter carries its **own** `revision` — a second repository is a second pin — and its own
`license`, because a permissive base with a non-permissive LoRA on it is not a permissive model.
The readiness marker includes an adapter fingerprint, so moving an adapter's revision re-proves the
recipe rather than trusting a marker written for different weights.

What the *worker* does with two recipes over one base is an optimisation and is not part of the
contract: if the base is already resident it calls `unload_lora_weights` and loads the new one,
which is seconds instead of the 40–90 s a 20B reload costs. Any failure in that path falls back to a
full load, having discarded the pipeline whose adapter state is now unknown.

### `projection`, `trigger` and the seam

`projection` is `flat` unless the recipe says otherwise, and a flat recipe **reports** `flat` rather
than omitting it: an absent field is indistinguishable from a node too old to have an opinion, and a
client that has to tell those apart has learnt nothing. It reaches the caller in the response body,
on the job document, and as `X-InferHub-Image-Projection` on the content route — so a viewer picks a
renderer from a declaration instead of guessing from the aspect ratio, which is what everyone does
today and is wrong for every 2:1 landscape photo.

An equirectangular recipe's `sizes` are all **2:1**, and that is not fussiness. 360° of longitude
over 180° of latitude is exactly two to one, and a render at any other ratio does not fail — it
produces a panorama that wraps wrongly and looks perfectly fine until somebody puts on a headset. So
the refusal for one of those recipes says *why*, not only which sizes exist.

`trigger` is appended to a prompt that does not already contain it, and the response says so
(`prompt_augmented`, plus the phrase). Silently rewriting a prompt is out — nothing in this project
is silently substituted, and a prompt is the user's own words. Refusing a prompt without the trigger
is out too — that is pedantry about a model whose entire purpose is one thing, and it makes the
first request everybody sends a 400. `autoTrigger: false` turns it off; the flag is reported either
way. The trigger **is** a recipe constant, so unlike the prompt it may be logged, which matters:
"why does this not look like a panorama" is almost always "the trigger did not apply".

The **seam** — the mean absolute difference between the first and last columns, 0–1 — is measured on
every equirectangular result and reported as `seamDelta`. Over `Tools:Image:SeamWarnThreshold`
(default 0.08) the result carries a `seam` warning. It is a warning and never a failure.

**Since v3.23 it can also be repaired — but only because somebody asked** (phase 55). Two gates:
`Tools:Image:SeamRepair` on the node says what an operator permits (`off` by default, then `blend`,
`diffuse`, `any`), and `X-InferHub-Image-Seam-Repair` on the request chooses within that. No
threshold ever triggers a repair, and nothing repairs by default — a repair the caller did not ask
for is a second pass they did not watch and would be billed for, which is the whole of the original
refusal and it still stands.

- **`blend`** halves the jump between the two columns the metric compares and ramps each half back to
  zero over ~2% of the width, so the extreme columns meet exactly in the middle. numpy on the array
  the VAE already produced: milliseconds, no VRAM, **no steps and nothing on the bill**. What it does
  is close a *tonal* discontinuity; what it cannot do is make *structure* continue — a seam through a
  doorway comes back with no visible step in brightness and the doorway still not lining up.
- **`diffuse`** is upstream's roll-and-inpaint (`run_qwen_image_nf4.py`'s `fix_seam`): the join is
  rolled to the middle of the picture and a band of it is repainted through
  `AutoPipelineForInpainting.from_pipe` over the same resident weights. It costs
  `int(steps × 0.4)` more steps, counted in `megapixel_steps` and included in `total_steps` from the
  first progress frame.

The worker measures, repairs, measures again, and **keeps the original if the number did not fall**,
reporting the mechanism and both numbers either way (`seamDelta` is always the image you were handed;
`seamDeltaBefore` is what it was first). Two equal numbers mean the pass ran and did not help — which
also happens to be the only way you would ever find out. A repair asked of a `flat` recipe is a 400:
there is no wrap, so there is no seam.

### `operations` splits the catalogue into two capabilities

A recipe declares `["generate"]`, or `["generate", "edit", "variation"]`. The worker then declares
**two** capability kinds — `image` for the generators and `image-edit` for the ones that also edit —
because the router filters on `(kind, model)` and nothing else, and it is a real distinction:
FLUX.1-schnell has no official inpainting pipeline and SDXL does. A client that asks
`/v1/images/edits` for a generate-only recipe gets a `503` that names the recipes on the fleet that
*can* edit, which is a fleet-state answer rather than an authorization one.

**An absent `operations` means `["generate"]`**, so every recipe written before v3.18 declares
exactly what it declared before.

An edit reuses the loaded pipeline through `AutoPipelineForInpainting.from_pipe` (a mask) or
`AutoPipelineForImage2Image.from_pipe` (no mask), which share the UNet, the VAE and the text
encoders rather than loading a second copy — so editing costs no extra VRAM and no extra seconds,
and needs no second entry in the residency budget.

**`defaults.strength` is what an edit uses when the caller sends no
`X-InferHub-Image-Strength`.** 0 keeps the input, 1 ignores it; 0.75 is the shipped default for both
editable recipes. It matters for the bill as well as the picture: `diffusers` enters the schedule at
`int(steps × strength)`, so 30 steps at 0.6 denoises for 18 — and 18 is what the result frame
reports and what the fleet meters, because metering the asked-for 30 would bill for work nobody did.

### The mask convention is inverted, and it is the one thing here a paragraph cannot fix

OpenAI's edits API treats a **fully transparent** pixel as the area to edit. `diffusers` takes a mask
where **white** is the area to inpaint. These are opposite, and getting it backwards does not error —
it edits everything *except* what you selected, which reads as a broken model.

So the worker converts, and the conversion is one line with a lot of reasoning behind it: the
diffusers mask is the **inverse of the alpha channel**. A mask with no alpha channel is refused
rather than guessed at, because under OpenAI's convention a fully opaque image means "edit nothing",
which no caller has ever intended. A caller who already has a white-is-edit mask says so with
`X-InferHub-Mask-Convention: luminance`; an unknown value is a `400`.

**A mask is never rescaled.** A mask names *which pixels*, and scaling somebody's selection lands
the edit next to what they chose — which looks like a bad model rather than a bad mask. A mask whose
size differs from the image's is a `400` naming both sizes.

## Adding one

Drop a `.json` file in this directory and restart the tool. Nothing is fetched on your behalf unless
`Tools:AllowModelDownload` is true (the `:diffusion` image sets it, because choosing that image *is*
the consent — the same reasoning by which `:ollama` sets `Ollama__Supervisor__Enabled`). With it
off, the log names the recipe and prints the exact `huggingface-cli download` line, including the
`--include` patterns its variant needs.

**Nothing here discovers models.** "Point InferHub at any Hugging Face id" is downloading and
executing third-party weights on request, which is phase-36 D6's refusal wearing a convenience's
clothes. A recipe is a file an operator put on the box.

A broken recipe is skipped and logged; it never fails the tool, and it never takes the node's
inference offline.
