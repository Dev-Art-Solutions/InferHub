# Image recipes (phase 46)

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

| Recipe | Repo | Size on disk | Licence | CPU |
|---|---|---|---|---|
| `sdxl` | `stabilityai/stable-diffusion-xl-base-1.0` | ~7 GB at fp16 | CreativeML OpenRAIL++-M | no — minutes per image |
| `sd15` | `stable-diffusion-v1-5/stable-diffusion-v1-5` | ~2 GB at fp16 | CreativeML OpenRAIL-M | yes, at 512² |

FLUX.1-schnell and Qwen-Image are **phase 48**, with the quantization path they need: 12B and 20B
respectively, and neither fits a 24 GB card at bf16.

## The fields

```jsonc
{
  "id": "sdxl",                    // the name a CLIENT sends as `model`. Not the repo id.
  "repo": "stabilityai/…",         // where the weights come from
  "revision": "4621659840…",       // REQUIRED. A commit sha, never a branch.
  "pipeline": "StableDiffusionXLPipeline",   // a class name in `diffusers`
  "variant": "fp16",               // WHICH FILES to download. Not the same thing as dtype.
  "license": { "id": "…", "permissive": true },
  "defaults": { "steps": 30, "guidance": 5.0, "size": "1024x1024" },
  "sizes": ["1024x1024", "1152x896", …],     // the aspect buckets, exactly
  "maxSteps": 75,
  "vramMiB": 8000,
  "dtype": "float16",
  "cpuViable": false
}
```

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

## Adding one

Drop a `.json` file in this directory and restart the tool. Nothing is fetched on your behalf unless
`Tools:AllowModelDownload` is true (the `:diffusion` image sets it, because choosing that image *is*
the consent — the same reasoning by which `:ollama` sets `Ollama__Supervisor__Enabled`). With it
off, the log names the recipe and prints the exact `huggingface-cli download` line, including the
`--include` patterns its variant needs.

A broken recipe is skipped and logged; it never fails the tool, and it never takes the node's
inference offline.
