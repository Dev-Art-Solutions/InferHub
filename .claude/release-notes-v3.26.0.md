# v3.26.0 — the video catalogue, and a ceiling that finally refuses something

Phase 58 of the v3.22–v3.28 track. v3.25 shipped the video seam and **one** model, which meant every
field the seam needed was exercised by exactly one file. This release adds the second and the third,
and each one makes a field able to be wrong in a way the first could not.

| Recipe | Geometry | Clock | VRAM | Download |
|---|---|---|---|---|
| `wan-t2v-1.3b` | 832×480 / 480×832 | 16 fps, 2–5 s | ~15.5 GB, bf16 | ~29 GB |
| **`wan-t2v-14b-720p`** | 1280×720 / 720×1280 | 16 fps, 2–5 s | **~24 GB at nf4** (~50 GB at bf16) | ~75 GB |
| **`cogvideox-2b`** | 720×480 only | **8 fps**, one 6 s offer | ~16 GB, fp16 | ~13 GB |

All three are Apache-2.0, so none needs a licence decision. No new config key was added and no route
changed: a deployment that changes nothing behaves exactly as v3.25 did.

## `fps` is now required, and the 16 it used to fall back to is deleted

CogVideoX-2b runs at **8 frames per second** where Wan runs at 16. A worker that fell back to 16
would have encoded its 49 frames at twice their rate — which is not an error and does not fail
anything. It is a clip that plays at double speed, from a model that then reads as bad at motion.

That is the same class of failure v3.25 spent a day removing from Wan (a bf16 VAE gives you noise, a
`flow_shift` of 3.0 at 720p gives you the wrong schedule) reintroduced by a convenience. So a video
recipe with **no `fps`**, an **empty `durations`** list, or a **`defaults.seconds` outside its own
list** is skipped and logged by name *before the capability is declared* — the last of those refuses
every request from the caller who named no duration at all, which is the caller trusting the recipe.

`cogvideox-2b` is why the rule exists and it is also why one offer is enough: its
`transformer/config.json` has `use_rotary_positional_embeddings: false` with `sample_frames: 49`, and
its model card says *"720 x 480, no support for other resolutions (including fine-tuning)"*. 49
frames at 8 fps is **6.125 s**, so `seconds: 6` is the label and 6.125 is what the response reports.
The refusal for anything else reads `it offers: 6`.

## The 14B repository ships `flow_shift: 3.0` as well — so the override is load-bearing

v3.25 added `schedulerFlowShift` for "the 720p entry the next phase will add, where 5.0 is wanted and
the repo may not say so". It was checked rather than assumed:
`Wan2.1-T2V-14B-Diffusers/scheduler/scheduler_config.json` is **byte-identical to the 1.3B's** on
that field, and 3.0 is the 480p value, while upstream's own 720p example passes 5.0 by hand. Without
the override a 720p render gets the wrong sigma schedule, which does not error and does not obviously
look wrong.

## `vaeTiling`, because a video job's peak lands after all the expensive minutes

The denoise loop holds a latent; the VAE then materialises **every frame at full resolution at
once** — for 81 frames at 720p, the largest single allocation in the job, and it arrives at the end.
Both `AutoencoderKLWan` and `AutoencoderKLCogVideoX` expose `enable_tiling()` in the pinned
`diffusers==0.36.0`, and CogVideoX's own example pairs it with `enable_slicing()`, so a recipe that
asks for tiling gets both where they exist. It is per recipe rather than always on: tiling trades
tile seams for headroom, and this project's position on seams is that the trade belongs to whoever
asked for it. A failure to enable it is logged and never fatal.

## The first recipe that does not fit a 24 GB card

`wan-t2v-14b-720p` declares **24 000 MiB** against a 24 GB card's 22 528 MiB of headroom at the
default reserve. A node with 24 GB therefore **never declares it**, the hub never routes to it, and
nobody discovers the ceiling four minutes into a render. `VramBudget.Fits` has existed since v3.16
and had never withheld a shipped recipe from a target card until now.

Two consequences worth stating:

- **A recipe id is a model *and* a geometry.** The gate is handed one number, once, before any caller
  exists — so `vramMiB` is sized at the **largest** `(size, seconds)` pair the recipe offers. A
  recipe for a model's cheap corner is a second id with a shorter list, exactly as an operator who
  wants two quantizations ships two ids.
- **A video recipe with no `vramMiB` is not declared at all.** For an image recipe, silence still
  means "admit rather than guess" — the miss is 4–8 GB and inventing a number would refuse a model
  the operator can see on the box. For video the same silence admits a 24 GB model onto a 12 GB card,
  and *that* failure is an out-of-memory error inside somebody's job rather than a line in a log at
  startup. `ImageRecipeCatalogue` reads a fourth field, `media`, to tell them apart — and still never
  reads `fps`, `durations`, `repo` or `pipeline`.

## Rules 5 and 7

**Zero new `PackageReference`** — `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`, and both new models are two JSON files and about eighty lines of
Python. Rule 7 is untouched: nothing new is logged, no new field could hold a prompt, and no
intermediate frame is decoded or written.

## Tests

The phase's slice, `tests/InferHub.Tests.Node`: **151 passed / 3 skipped**. `Tests.Shared`:
**126 passed**. `dotnet build InferHub.sln`: clean, 0 warnings.

New assertions, all in `RecipeCatalogueTests`: a video recipe with no `vramMiB` is skipped and an
image recipe with none is not; `media` is read and defaults to image; the shipped catalogue is the
ten recipes above; every one fits a 24 GB card **except the one named in the test**, which is
asserted to be refused there and admitted on a 48 GB card; and every shipped video recipe's `sizes`
and `defaults.size` parse through `VideoSizes` with every frame count on the 4k+1 grid — a recipe
offering a size the *edge* refuses would be a model nobody could call.

## What was NOT established, said out loud

- **Nothing was run on a GPU.** No clip has been rendered from either new recipe, no weight has been
  downloaded (the 14B's first pull is ~75 GB), and nobody has watched anything. That is the
  published-image check's job and phase 60's.
- **Both `vramMiB` figures are arithmetic, not measurements.** They are weight counts from the
  repositories' own file listings — 53 GB of fp32 transformer and 20.8 GB of fp32 text encoder for
  the 14B; 3.15 + 8.87 + 0.8 GB for CogVideoX — converted to nf4/fp16 and given an **activation
  allowance nobody has measured**. If 24 000 turns out to be wrong it is wrong in the direction of
  refusing a card that would have coped, which is the survivable direction.
- **CogVideoX's card documents "from 4 GB" and this recipe declares 16 000 MiB.** That is not a
  contradiction: upstream's figure is a sequential-CPU-offload path this worker deliberately does not
  take, because keeping weights resident is what stops the second request re-paying the transfer.
- **The full solution suite was not run for this release**, by request. The declared slice and
  `Tests.Shared` were, and CI runs the rest on the tag.
- **Still no console panel and no per-recipe video status at the hub** — a video recipe refused for
  its licence or its budget is still invisible from the coordinator. Phase 59.
- **No image-to-video, no caller-chosen fps, no audio, and no 480p entry for the 14B.** The last is
  new and is named rather than forgotten: the same weights at a second geometry is two recipe ids
  over one loaded pipeline, and the residency map keys on the id, so one pipeline would be counted
  twice against one card. That is a phase, not a JSON file.

## Housekeeping, for whoever opens phase 59

`src/InferHub.Node/CLAUDE.md` is at **exactly 1 100 lines, its budget**. This release paid for its
own two paragraphs by moving 48 D6's quantization argument and its `bitsandbytes` note into
`python/CLAUDE.md`, where the recipe format and the dependency actually live. **The next phase to
touch that file has to move something out before it can add a sentence** — which is the budget
working as designed, and is written here so it is not discovered as a red test.

## Published-image check

**Not performed, and this phase does change an image.** The recipes are baked into
`inferhub-node:*-diffusion` (`COPY python/recipes/ /opt/inferhub/recipes/`), so `3.26.0`'s diffusion
image is a different image from `3.25.0`'s and the ritual applies.

What the image build *does* check for us, at `docker build` time and therefore on CI for this tag:
every recipe file parses, and **every `pipeline` and `vaeClass` any recipe names exists in the pinned
`diffusers`** — which is exactly the assertion `cogvideox-2b` needs, since it is the first recipe to
name `CogVideoXPipeline`. A tag whose image builds has proved that much.

All five images published green on the tag, and the diffusion one was asked what it is rather than
the dashboard: `inferhub-node:3.26.0-diffusion` resolves to
`sha256:feb3cb8efae2…` and its config carries
`org.opencontainers.image.revision = afb7fa45a8f065bc8e11b486dcbc2ae8626ec9c3` — the phase commit —
with `Tools__Image__RecipeDirectory=/opt/inferhub/recipes` intact. **So the build-time assertions ran
against these two recipes on the published artifact**: the JSON parses, and `CogVideoXPipeline`
exists in the pinned `diffusers` — the first recipe in this project to name it.

What has **not** been done is pulling that image onto the GPU box and watching it declare
`cogvideox-2b`, decline `wan-t2v-14b-720p` on a 24 GB card, and render something. Reading a manifest
is not running a container. That needs the host, and it is the first item on phase 60's day.
