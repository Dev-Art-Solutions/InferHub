# python/ — agent context

**Scope: `python/`.** The tool workers the node spawns as child processes, the protocol reference
library, and the image recipe catalogue.

> **Read the root `CLAUDE.md` first**, then `src/InferHub.Node/CLAUDE.md` phase 41 — **the node
> speaks a process protocol, not Python**, and nothing on the .NET side knows this directory
> exists. A worker written in Go or a shell script wrapping `ffmpeg` is exactly as valid; Python is
> simply where `faster-whisper`, `piper` and `diffusers` live.

**None of this is a `PackageReference` and none of it is in any `.csproj`** — it is a `pip install`
in one Dockerfile, in the same category as phase-39's `curl` of an Ollama tarball. Rule 5 depends
on that staying true.

## Where the detail already lives

Two files in this directory are the real reference and are longer than this one:
`inferhub_worker/__init__.py` (the protocol, with the three rules a worker author needs) and
`recipes/README.md` (every recipe field, and why each exists). This file holds the decisions the
.NET side and the workers **share**.

## Related context

- The runtime that spawns these: `src/InferHub.Node/CLAUDE.md`
- The conventions this worker applies: `src/InferHub.Shared/CLAUDE.md` (50 D2 names them here)

## Decisions recorded here

### Phase 49 (qwen-360-diffusion: adapters and 360° panoramas) — also load-bearing

**The model this whole track was asked for, and it needed four things phase 48 did not have:** an
adapter stack in the recipe format, a projection that survives to the client, a 2:1 refusal that says
why, and a seam number nobody repairs.

**D1 — Adapters are a recipe field, and a recipe with adapters is a distinct model id.**
`adapters[]` carries a repo, its **own pinned revision**, a `weightFile`, a scale and its own
licence — a permissive base with a non-permissive LoRA on it is not a permissive model.
`qwen-image` and `qwen-360` are **two recipe ids over one base**, not one model with a flag: the
router keys on `(capability, model)` and nothing else (40 D1), so a client asking for `qwen-image`
must never receive a panorama, and a `loraScale` header would make what you get depend on a header —
the reproducibility problem 48 D6 already refused for quantization.

What the *worker* does about two recipes sharing a base is an optimisation and is **not part of the
contract**: `donor_for` finds a resident pipeline with the same `base_key` (repo, revision, variant,
dtype, quantization), `unload_lora_weights` + `load_lora_weights` swaps the adapter in seconds rather
than the 40–90 s a 20B reload costs, and **any failure falls through to a full load** having
discarded the pipeline whose adapter state is now unknown. Adapters are applied inside
`_from_pretrained` — the one place a pipeline is constructed (v3.14.1) — so the background prefetch
*proves* the LoRA loadable exactly as a request will load it, and a mismatched one is never declared
rather than failing on somebody's first request. The readiness marker carries an adapter fingerprint
for the same reason: trusting a marker written for different weights would serve the wrong model
under the right id.

**D2 — The trigger phrase is appended when missing, and the response says it happened.** Three
options, and two were rejected out loud. *Silently rewriting the prompt* — no: this repository's
most-repeated sentence is that nothing is silently substituted, and a prompt is the user's own words.
*Refusing a prompt without the trigger* — no: pedantry about a model whose entire purpose is one
thing, and it makes the first request everybody sends a `400`. So it is appended when absent, and
`prompt_augmented` plus the phrase travel in the response. `autoTrigger: false` turns it off and the
flag is reported **either way**, for a recipe that has a trigger — a client that had to infer
"nothing happened" from a missing key is a client guessing about its own prompt. A recipe with *no*
trigger reports neither, because a permanent `false` on every SDXL response is a field that means
nothing.

**The trigger is a recipe constant and therefore not content**, which is what makes it loggable and
is worth having: "why does this not look like a panorama" is almost always "the trigger did not
apply", and a diagnosis nobody can see is not one. The prompt it was appended to is still never
written anywhere — `ImagePrivacyTests` asserts the augmented form is absent from the logs too, since
a worker echoing the rewritten prompt back into a payload the hub logs would leak the original with
three words on the end of it.

**D3 — A 2:1 aspect is enforced, and the refusal names the reason rather than only the list.**
360° of longitude over 180° of latitude is exactly two to one. A wrong size on a flat recipe gives
you duplicated limbs — visibly bad. A non-2:1 equirectangular render gives you a picture that looks
perfectly fine and wraps wrongly, and the person who finds out is wearing a headset three days later.
*Recorded deviation:* the brief listed this under `ImageRenderer`, and the edge still cannot do it —
phase-46 D6's deviation is unchanged, a recipe is a file on the node and the hub has no catalogue.
The **worker** writes the sentence and `ImageRenderer` renders the 400 without reading it (29 D6).

**D4 — Projection is a declared property of a result, on every surface, including `flat`.**
`ImageProjections` in `InferHub.Shared`; the response body per image, the job document, and
`X-InferHub-Image-Projection` on the content route — which is the one request with no JSON to carry
it. **A flat recipe reports `flat` rather than omitting the field**, and that is a deliberate
exception to phase-28 D5's "absence is a fact": there, absence meant nothing had been *measured*;
here the field is a declaration, and an omitted one is indistinguishable from a node too old to have
an opinion. A client that has to tell those apart has learnt nothing. Nothing infers a projection
from an aspect ratio — a 2048×1024 photograph and a 2048×1024 panorama are the same pixels.

**D5 — The seam is measured and reported always, and repaired only on request** *(amended in phase
55 — the original clause read "never repaired", and the amendment is one word wide)*. Mean absolute
difference between the first and last columns — adjacent once wrapped — over 255. Two numpy
operations on an array the VAE already produced, which is why it is unconditional: a metric behind a
flag is a metric nobody has. Over `Tools:Image:SeamWarnThreshold` (0.08) the result carries a `seam`
warning, and it is a **warning on a 200**: phase-35 D4 against phase-37 D4 again, because a visible
seam is the operator's own aesthetic judgement and failing a two-minute job over a threshold would be
the tool overriding the person. **Nothing repairs unasked** — a repair run without being asked bills
somebody for a decision they never made, and **no threshold triggers one**, which is the half of this
decision phase 55 leaves exactly as it was. What phase 55 added is the asking; see its block below.
`seam_delta` returns `None` rather than raising on anything unexpected: a measurement that could fail
a two-minute job is worse than no measurement.

**The viewer is hand-written WebGL, and rule 3 is why.** `wwwroot/pano.js` — a sphere, a texture and
two matrices, no npm, no bundler. three.js from a CDN would also put a third-party script on an admin
console that holds cordon and model-pull rights, which is a worse trade than an afternoon of
`gl.texImage2D`. It picks its renderer from the **declared projection**, never from the aspect ratio,
and a browser with no WebGL gets the flat image and a sentence saying so rather than a black
rectangle (39 D6's instinct, in a canvas).

*Recorded deviations, on purpose:*
- **`ImageRenderer.Envelope` is now the one place the OpenAI Images envelope is written**, and it
  builds dictionaries rather than anonymous types. Three surfaces produced it by hand, and the
  global `WhenWritingNull` policy is how the hub came to emit `revised_prompt: null` while a solo
  node **omitted** it — for three releases, with a parity suite running. Which keys are *present* is
  part of this contract, so it is spelled rather than inherited from a serializer option.
- **`qwen-image` gained `guidanceParameter: "true_cfg_scale"`.** Qwen's MMDiT has two guidance
  inputs and this pipeline's real classifier-free guidance is the second one; passing the wrong one
  does not error, it produces a picture that is plausible and not what the recipe was tuned for.
  Upstream's own `run_qwen_image_nf4.py` uses `true_cfg_scale`, and phase 48's verification never
  generated a Qwen image at all, so no observed behaviour changed under it.
- **The LoRA's quantization variant does not have to match the base's.** Settled from upstream's
  reference script rather than assumed: it pairs an **nf4 base** with the **int8-trained** adapter,
  and the int4-trained ones exist for fp8 transformers, where downcasting artifacts are the problem.
  Written into the recipe's `notes` field, because "which loading path" is exactly the thing a later
  reader will otherwise re-derive wrongly.
- **The console panel holds its own client key**, like the documents panel: image jobs are guarded
  by `Auth:ApiKeys` and the admin key the rest of the console uses will not open one. There is no
  *list* of image jobs — the hub has no route for one, and inventing a client-scoped listing here
  would be phase 51's job done in the wrong phase.

**Rule 5 survived again.** **Zero** new `PackageReference`, no npm, no CDN script, no image library —
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`, and the only thing
that ever decodes a pixel is the browser.

### Phase 50 (editing: img2img, inpainting, variations) — also load-bearing

**Bytes travel hub → node for the first time.** Phase 40 built the attachment path and phase 42 used
it in one direction only; an edit is the first thing in this project's history that sends a
multi-megabyte payload *down* the mesh connection. `ImageEditTests` pushes 3 MB across a real wire
and asserts **the node is still registered afterwards** — the v3.10.0 assertion, in the direction
nobody had tested.

**D1 — `image-edit` is its own capability kind, and the catalogue splits by a recipe field.**
A recipe declares `operations: ["generate"]` or `["generate", "edit", "variation"]`; the worker
declares `image` for the generators and `image-edit` for the rest. **Both frames are always sent,
empty ones included** — an empty list is a declaration that this node serves nothing under that kind,
and omitting the frame would leave a previous declaration standing on a node that can no longer
honour it. A second kind rather than a per-model operation list, because the router filters on
`(kind, model)` and nothing else (40 D1): teaching it to read a nested operation set means teaching
the affinity, the queue and the saturation logic the same thing. It is also a real distinction —
FLUX.1-schnell has no official inpainting pipeline and SDXL does.

**The 503 names the recipes that *can* edit**, and that is not the model catalogue phase-46 D6
refused: it is the fleet's own capability declarations, which the hub already holds. "No" with no
alternative sends somebody to the docs; "no, but these" is actionable.

`CapabilityKinds.IsImageKind` is what everything that reasons about a *recipe* asks — the licence
gate, the VRAM budget, the residency map. **Editing and generating are separate for routing and not
for any of those**, because an edit loads exactly the same weights (`from_pipe` reuses the
components). A node that applied its licence gate to `image` only would happily edit with a model
whose licence nobody accepted.

**D2 — OpenAI's mask convention is inverted from the library's, and the conversion happens in the
worker. This is the decision the phase turns on.** OpenAI's edits API treats a **fully transparent**
pixel as the area to edit; `diffusers` takes a mask where **white** is the area to inpaint. Getting
it backwards does not error — it edits everything *except* what the caller selected, which reads as
a broken model.

> **Recorded deviation from the phase brief.** The brief put a `MaskConverter` in `InferHub.Shared`.
> It cannot live there: converting one convention to the other means reading an alpha channel out of
> a PNG and writing a greyscale one back, and **nothing in this codebase's C# ever decodes a pixel**
> (phase-46 D6) — there is no image library on the hub, by design and by invariant, and hand-rolling
> a PNG decoder to avoid taking one would be the same mistake with more code. So
> [MaskConventions](src/InferHub.Shared/Images/MaskConvention.cs) decides what the two conventions
> **are** and what a caller may say; the inversion happens where PIL already is. It is named
> `MaskConventions` rather than `MaskConverter` because a converter that converts nothing is a lie in
> a name — the same correction phase 46 made to `Metrics.RecordAudioUnits`.

The consequence is the one phase-46 D6 and phase-49 D3 already accepted twice: **a mask with no alpha
channel costs one round trip to find out**, because the edge cannot open it. The worker answers
`invalid_request` and `ImageRenderer` renders the 400 without reading the message (phase-29 D6).
Under OpenAI's convention a fully opaque "mask" selects **nothing**, which no caller has ever
intended — reading it as "edit everything" would be a silent substitution of the most destructive
possible interpretation, and reading it as "edit nothing" would return the input with a 200 on it.
`X-InferHub-Mask-Convention: openai | luminance` lets a caller who already has a white-is-edit mask
say so; an unknown value is a `400` that names both **and says which is which**, because two words
whose difference is invisible until you look at the picture are not a helpful list.

**A mask is never rescaled.** A mask names *which pixels*, so a mask whose size differs from the
image's is a `400` naming both sizes rather than a resize — scaling somebody's selection lands the
edit next to what they chose, which looks like a bad model rather than a bad mask.

**D3 — `strength` is a header, and what is metered is the steps it actually ran.** OpenAI's edits API
has no `strength` and image-to-image without one is meaningless, so `X-InferHub-Image-Strength`
(0–1), phase-46 D1's shape. Absent, the **recipe's** `defaults.strength` applies — the edge has none
to invent and deliberately omits the field rather than guessing, because a number chosen at the edge
would be the edge deciding how far an edit moves away from somebody's photograph.

`diffusers` enters the schedule at `int(steps × strength)`, so 30 steps at 0.6 denoises for 18 — and
**18 is what the worker reports, what the progress frames count to, and what the ledger gets**.
Metering the asked-for 30 would bill for work nobody did, which is phase-42 D7's "the unit the work
is in" applied to a knob rather than to a modality.

**D4 — Input attachments ride the existing path and are capped in both grains.** Each part is bounded
by `Tools:MaxAttachmentBytes` (what the *node* enforces, so a request that passed the edge and failed
at the node is impossible) and the picture and mask **together** by `Images:MaxRequestBytes` — a
separate key because the two directions are separate risks with separate arithmetic: outbound is `n`
renders of a declared size, inbound is one upload somebody else chose the size of. Both refusals are
`413`s naming their key, at the edge, before anything is buffered onward.

**The caller's filename is dropped and the parts travel as `image` and `mask`.** What somebody called
a file on their disk is metadata about their day (phase-42 D5); what the worker needs is the *role*.

**A variation takes no prompt, and a prompt on one is a `400` naming the other route.** OpenAI's
variations API has no prompt field, and `/v1/images/edits` *without* a mask is already "img2img with
a prompt" — so accepting one here would be a second dialect for a convenience that exists. Ignoring
it would be worse: a caller whose prompt vanished silently would conclude the model ignores prompts.
A mask on a variation is refused for the same reason.

**`POST /api/images/jobs` takes JSON or multipart, and that is not phase-47 D1's refused flag.** D1
refused `background: true` because it made one route answer two incompatible *response* shapes; here
the response is the same job document either way and the request shape is decided by `Content-Type`,
which is what content types are for. **A multipart submission must name its `operation`** — defaulting
it would let a typo turn a variation into an edit, and this is InferHub's own contract where ceremony
is cheaper than a silent substitution.

*Recorded deviations, on purpose:*
- **`ImageRenderer.Generation` became `ImageRenderer.Render`**, and `ImageJobRegistry` /
  `LocalImageJobRunner` now hold an `IImageRequest` rather than an `ImageGenerationRequest`. Phase
  47's queue, progress, cancel, retention and metering are identical for an edit, and a second job
  path would be two ideas of fairness on one GPU — the thing phase-47 D1 built the shared path to
  prevent. `busyNodes` is deliberately **not** split by capability either: the resource a node has
  exactly one of is the card.
- **The edge does not check that a recipe supports the operation**, only that the fleet declares the
  capability. The worker refuses by name for a solo caller who reaches it directly. Phase-46 D6's
  deviation, unchanged: a recipe is a file on the node.
- **The multipart reading is hand-copied per host** (`ImageEndpointSupport.ReadEditAsync` and
  `LocalImageForm`). Phase-37 D6's line: the ten lines that touch `IFormCollection` are plumbing,
  and every *sentence* comes from `InferHub.Shared`. `ImageParityTests` grew five arms, including
  the mask refusals, because that copy is the parity risk.
- **The echo worker reads the input files for real** and checks the mask's alpha channel and its
  dimensions out of a genuine IHDR. A test fixture may decode a PNG; the hub may not. A stub that
  agreed with itself would prove nothing about the one thing this phase adds.

**Rule 5 survived again.** **Zero** new `PackageReference`, no image library anywhere in C#, and
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.

### Phase 55 (the seam can be repaired, because somebody asked) — also load-bearing

**D1 — Repair is asked for per request, and the default answer is still no.** `Tools:Image:SeamRepair`
(`off` default, then `blend`, `diffuse`, `any`) is what an *operator* permits;
`X-InferHub-Image-Seam-Repair` chooses within it. Two gates in phase-41 D2's shape, and the ceiling
is the node's — in solo mode that node is also the edge, and the answer is the same one.
**Considered and rejected: a recipe field that repairs every panorama from that recipe.** It reads as
a property of the model and is in fact a property of somebody's budget: the same recipe serving a
preview grid and a hero shot wants different answers, and a recipe cannot tell them apart. **A
threshold that triggers a repair was rejected in the same breath** — that is 49 D5 with the consent
removed and a helpful expression on.

**`diffuse` does not imply `blend`.** The four values name mechanisms rather than tiers, so an
operator who thinks a feathered band is worse than an honest seam can permit only the real repair;
`any` is how "both" is said. `SeamRepairModes.Permits` is the one place that decides it.

**D2 — The default mechanism is not a generation pass at all.** `blend` halves the jump between the
two columns the metric compares and ramps each half back to zero over ~2% of the width, so the
extreme columns meet exactly in the middle: numpy, milliseconds, no VRAM, **no steps and nothing
added to the ledger**. `diffuse` is upstream's roll-and-inpaint and is the expensive, better one.
**Considered and rejected: shipping only `diffuse`** — it makes the answer to "my panorama has a line
in it" cost a second render every time, which is the bill 49 D5 refused to hand anybody.

**The trade `blend` makes is stated rather than hidden, and the honest statement is narrower than
the brief's.** It closes a **tonal** discontinuity, not a **structural** one: because the feather is
exact at the join, the number essentially always falls, and a seam cutting through a doorway comes
back with no visible step in brightness and the doorway still not lining up. Saying "it softens the
seam" would be describing a cross-fade — which was the other implementation, and it ghosts.

**D3 — `diffuse` was established against the pinned library before it shipped, and it is a refusal
that names the reason if it ever cannot run.** `diffusers==0.36.0`'s
`AUTO_INPAINT_PIPELINES_MAPPING` maps `qwenimage` → `QwenImageInpaintPipeline`, whose constructor
takes the *same five components* `QwenImagePipeline` does — so `AutoPipelineForInpainting.from_pipe`
re-derives it over the resident weights at no VRAM cost, LoRA included, exactly as phase-50 D1's
`derived_pipeline` already does for an edit. That path is reused rather than rebuilt, and its
existing failure is exactly D3's requirement: a pipeline family diffusers cannot re-derive fails
**naming the class**, and `diffuse` is never quietly served as `blend`.

> **What was *not* established: any of this on a GPU.** The mapping and the constructors were read
> out of the pinned wheel; no panorama has been rendered, no repair has been looked at, and no
> before/after pair has been measured on real weights. The brief asked for a GPU box and there was
> none. See the release notes, which say the same thing where somebody looking for a number will
> find it.

**D4 — A repair that does not improve the number is reported and discarded.** Measure, repair,
measure again, keep the original if the delta did not fall — and report `seam_repair`, both numbers,
and the `seam` warning the image still earns. **`seam_delta` is always the current image's**, so a
client that never heard of this phase reads the field it already knew and gets a true answer about
the bytes it holds; `seam_delta_before` is what the same measurement said first. **Two equal numbers
are a real outcome**, not a bug: the pass ran and did not help. A repair that quietly made a seam
worse is the one outcome nobody would ever go looking for.

**D5 — `blend` adds nothing to the ledger and `diffuse` adds its real steps, in the existing unit.**
`megapixel_steps` already means what a pass costs, so a `diffuse` repair is `int(steps × 0.4)` more
of them rather than a unit of its own — one number an operator has to understand. **A discarded
repair is still metered**: the GPU did the work, and a bill that depends on whether the outcome was
good is a bill nobody can predict. `blend` runs no steps and is metered as none, because inventing a
charge for two numpy calls would make the cheapest path look like a decision.

**D6 — The repair's steps are in the progress total from the first frame.** `total_steps` includes
them before step one goes out. **Considered and rejected: emitting the repair as a second progress
run** — a bar that reaches 100% and then starts again is a bar that has lied once.

*Recorded deviations, on purpose:*
- **The operator's ceiling is enforced in the worker, not in a node-side clamp**, which is where the
  brief's task list put it. Nothing on the node parses an image payload — `ImageRecipeCatalogue`
  reads three fields out of *recipe files* and the runtime has never seen a request body — so a
  clamp there would mean teaching the node to read diffusion requests to enforce a key the process
  that spends the steps can enforce itself. This is 48 D5's shape with the redundant half removed:
  the node **states** the ceiling into the child's environment (41 D3 clears it first), and the
  worker refuses **naming the key**. `ToolOptions` holds the key and its validator; `SeamRepairModes`
  holds what the words mean.
- **The header is parsed for edits as well as generations.** `run_batch` is literally the same loop
  for both, so honouring it on one route and dropping it on the other would be a difference nobody
  could explain. Today's catalogue has no equirectangular editor, so what it buys is that the day one
  lands, nothing has to be remembered.
- **The content route's three seam headers are emitted only when a repair was asked for.** Gating
  them is what lets "a request with no header is answered exactly as v3.22 answered it" cover the
  header list too, which is the only version of that claim worth making.
- **The echo worker implements the same `blend`, and a deliberately bad repair behind
  `--image-repair-worse`.** D4's discard has no natural failing case for `blend` — the feather
  matches the extreme columns by construction — and the mechanism that genuinely can fail needs a
  card. Rather than leave the rule untested, the fixture provides a mechanism that fails and the
  assertion is on the *rule*.

**Rule 5 survived again.** **Zero** new `PackageReference` — `numpy` was already in
`requirements-diffusion.txt` — and `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`.

### Phase 57 (video: one model, and four facts that were read rather than guessed) — also load-bearing

**The same worker, the same loader, the same gates.** A recipe says `"media": "video"` — **absent
means `image`**, which is why the seven recipes that predate this phase changed by zero bytes
(40 D1's "null is today's behaviour", third use) — and `capability_frames` declares it under a third
kind. **Considered and rejected: `video_worker.py`.** A second worker is a second copy of the
readiness marker, the eviction, the licence gate, the VRAM budget and the `local_files_only`
prefetch, every one of which is about *weights on a card* and none of which is about what comes out
of the pipeline; it would also put two multi-gigabyte processes on one GPU the first time somebody
enabled both.

**D1 — Four facts about `Wan-AI/Wan2.1-T2V-1.3B-Diffusers` were established from the pinned wheel and
the repo's own configs, and each one produces a plausible non-failure when it is wrong.**

1. **The VAE loads separately, in `float32`, under a `bfloat16` transformer.** Upstream's example
   does exactly that; a uniform bf16 load does not error, it is the difference between a video and
   noise, discovered four minutes into a job. `vaeClass` **names** the class rather than inferring
   it — inferring which VAE a repo wants is 29 D5's capability registry by the back door.
2. **`flow_shift` is a scheduler setting, not a call argument, and this repo already sets it.**
   Passing it to `__call__` is a `TypeError`. Upstream's example sets 5.0 by hand because it is
   written for the **14B 720p** model; the 1.3B repo ships `flow_shift: 3.0` — the 480p value — in
   `scheduler/scheduler_config.json`. So `schedulerFlowShift` matches what is checked in, and the
   override exists for the 720p entry phase 58 will add.
3. **Height and width divide by 16, and frames sit on a 4k+1 grid.** `check_inputs` and
   `prepare_latents` respectively. Both are recipe data, so 58 inherits them rather than re-deriving.
4. **"1.3B" names the transformer only.** The text encoder is UMT5-XXL at ~11B, every weight in the
   repo is fp32 with **no fp16 variant**, and the download is **~29 GB**. That is v3.14.1's
   `variant`-is-not-`dtype` lesson in the one shape where there is nothing to fix — there is no
   variant to ask for — so `vramMiB` (15 500) is sized from the encoder and the disk cost is
   documented rather than discovered.

**D2 — A node that cannot encode does not declare `video` at all.** `diffusers.utils.export_to_video`
**warns and silently drops to an OpenCV `mp4v` writer** when `imageio` is absent: a container nobody
chose, from a code path nobody read, on a job that already cost minutes of card. So `can_encode_video`
is asked once per declaration (46 D7's withdraw-before-the-first-failure), `imageio` +
`imageio-ffmpeg` are pinned, and `Dockerfile.diffusion` **asserts the import at build time** —
46 D9's `docker build` step rather than a thing to remember. `imageio-ffmpeg` vendors a **static
ffmpeg binary in the wheel**, so "which encoder was in 3.25.0" has a version for an answer rather
than whatever the distribution mirror held that week (39 D9).

**D3 — `generate_video` is not `run_batch`, deliberately.** That loop is per-image and every argument
it takes — `n`, the per-image seed derivation, the seam measurement, the projection — is about a
still. Sharing it would mean six `if video` branches inside the function that is hardest to read, to
save a loop that runs once. **`fps` is not a caller's knob** (57's non-goals): re-timing the frames
at encode changes how fast the world moves in the clip, and the only honest setting is the trained
one until somebody measures the others.

**The refusals are the worker's, and they name the list.** A size outside `sizes` and a duration
outside `durations` are both `invalid_request` — 46 D6's deviation unchanged, because a recipe is a
file on the node and the hub has no catalogue. The duration refusal says *why* it is a refusal rather
than a rounding: "a frame count is fixed by the model's latent grid".

**Rule 5 survived again**, and rule 7 met its fourth kind of content: the prompt is not logged, and
**no intermediate frame is ever decoded or written** — the temptation is stronger here than anywhere,
because a first frame would make a lovely progress thumbnail and it is a picture of what somebody
asked for.

### Phase 58 (the video catalogue: a second model, a third, and a ceiling that refuses one)

> **Moved here in this phase, from 48 D6 in `src/InferHub.Node/CLAUDE.md` (52 D2).**
> **Quantization is a recipe field with three values and a stated cost:** `none | int8 | nf4` through
> `diffusers`' native `bitsandbytes` integration, applied to the components `quantizeComponents`
> names — which for Qwen-Image **has to include the text encoder**, because 8.3B left at bf16 is the
> difference between fitting a 24 GB card and not, and which for `wan-t2v-14b-720p` includes it for
> the same reason one model larger. **It is a recipe field rather than a request parameter because it
> changes what the model *is*:** two requests to `qwen-image` that quantized differently produce
> different images from the same seed, and a per-request knob would make reproducibility a function
> of a header nobody logged. An operator who wants both ships two ids. `vramMiB` is the **quantized**
> figure the node's gate admits against and `vramUnquantizedMiB` is documentation, because
> "Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are both true sentences about different
> recipes. **One mechanism** — GGUF, Nunchaku and TensorRT are each faster on some model on some card
> and each is a second thing to reason about when a picture comes out worse than expected.
>
> **`bitsandbytes` arrived with its first consumer**, which is why phase 46 refused to carry it: a
> pinned dependency nothing imports is a pin nobody can tell is wrong until the release that needs
> it. It is a line in `requirements-diffusion.txt`, in phase-39's `curl` category, and no
> `PackageReference` (rule 5).

**A catalogue of one proves nothing about its own fields.** 57 shipped every field video needed and
exactly one recipe using them, so `fps` fell back to 16 because the only recipe *was* 16, and
`schedulerFlowShift`'s override had never overridden anything. `wan-t2v-14b-720p` and `cogvideox-2b`
are what make each of them able to be wrong.

**D1 — `fps` is required and the 16 it fell back to is deleted.** A default here is a guess about
somebody else's model: CogVideoX-2b is **8**, and encoding its 49 frames at 16 does not error — the
clip plays at double speed and the model reads as bad at motion. That is 57 D4's *plausible
non-failure* reintroduced by a convenience. `load_recipes` now refuses to offer a video recipe with
no `fps`, none with an empty `durations` list, and none whose `defaults.seconds` is outside that list
— the last of which would refuse every request from the caller who named no duration, which is the
caller trusting the recipe. All three are 41 D6's withdraw-*before*-the-first-failure, and each names
the field to add. **Considered and rejected: validating on first use** — the first use is inside
somebody's job, minutes in.

**D2 — The 14B repository ships `flow_shift: 3.0` too, so the override is load-bearing rather than
defensive.** 57 D4 built `schedulerFlowShift` for "the 720p entry phase 58 will add, where 5.0 is
wanted and the repo may not say so". It was read rather than assumed:
`Wan2.1-T2V-14B-Diffusers/scheduler/scheduler_config.json` is byte-identical to the 1.3B's on that
field, and 3.0 is the **480p** value, while upstream's own 720p example passes 5.0 by hand. Without
the override a 720p render gets the wrong sigma schedule, which does not error and does not obviously
look wrong.

**D3 — `vaeTiling`, because a video job's peak allocation is at decode and it lands after all the
expensive minutes.** The loop holds a latent; the VAE then materialises every frame at full
resolution at once. `AutoencoderKLWan` and `AutoencoderKLCogVideoX` both expose `enable_tiling` in
the pinned wheel, and CogVideoX's example pairs it with `enable_slicing`, so `enable_vae_tiling` asks
for both and skips whichever the class lacks. **Considered and rejected: always tiling** — it trades
tile seams for headroom, which is the second kind of seam this project would then own, and 49 D5's
lesson is that a trade like that belongs to whoever asked for it. A failure to enable it is logged
and not fatal: losing a load over an optimisation is worse than the OOM it avoids being possible.

**D4 — `cogvideox-2b` offers one size and one duration, and that is the model rather than a
default.** `transformer/config.json` has `use_rotary_positional_embeddings: false` with
`sample_height: 60`, `sample_width: 90`, `sample_frames: 49` — learned positional embeddings sized
for exactly one grid — and the model card says *"720 x 480, no support for other resolutions
(including fine-tuning)"*. 49 frames at 8 fps is **6.125 s**, so the offer is labelled `6` and the
response reports 6.125 (57 D5, and the gap is wider here than Wan's hundredth of a second, which is
the point). The refusal a caller meets reads *"it offers: 6"* — a one-entry list is a catalogue, and
a range would be a lie.

**What was not established: anything on a GPU.** No clip has been rendered from either recipe, no
weight has been downloaded, and both `vramMiB` figures are arithmetic over the repositories' own file
sizes plus an activation allowance nobody has measured. Every other claim here is read from the
pinned wheel or from the models' checked-in configs. See the release notes, where somebody looking
for a number will find the same sentence.

### Phase 60 (the verification day) — three things the worker was wrong about, all found by running it

Recorded here rather than in `src/InferHub.Node/CLAUDE.md` because all three are about the
**worker** — the node's half is one environment variable — and because that file is at its budget.

**D1 — `offered()` has a third gate: the VRAM budget, which reaches the worker as
`INFERHUB_IMAGE_VRAM_BUDGET_MIB`.** The node has refused an oversized recipe by name since 48, and
the worker **fetched its weights anyway**, because `offered()` gated on licence and `cpuViable` only.
On a 24 GB box the default catalogue therefore queued ~75 GB for `wan-t2v-14b-720p` — a recipe the
node had announced at startup it would never offer. Not a routing hole (the hub is never told) and
not a licence hole: a disk-and-bandwidth one, invisible until a volume filled. This is 48 D5's shape
for the other gate — *the node decides what it declares, the worker is the lock on the process that
would actually spend the resource* — and the fetch planner is the third place that spends one.
**Absent, not zero, when nothing was declared** (28 D5): a worker reading `0` cannot tell "no card"
from "nobody said", and one of those answers refuses the whole catalogue.

**D2 — `diffusers` uses `ftfy` and `peft` without declaring either, and both fail at *request* time.**
`WanPipeline.prompt_clean` calls `ftfy.fix_text` unconditionally under an
`if is_ftfy_available(): import ftfy`, so **every** video request died with
`NameError: name 'ftfy' is not defined` *after* the weights had loaded — minutes of card, spent, for
a library nobody asked for. `load_lora_weights` **is** the PEFT backend, so `qwen-360` raised
`ValueError: PEFT backend is required for this method` during the background prove and simply never
became offerable; the node said nothing louder than "fetching". Both are now pinned in
`requirements-diffusion.txt` and **asserted at build time** beside torch and the encoder, which is
the only thing that stops the v3.10.0 shape recurring. Three releases shipped without them because
nothing had ever run a video request or a LoRA recipe against a published image.

**D3 — A manifest that does not name a kind throws that kind away, so `diffusion.json` names
`video`.** `ToolWorkerPool.Narrow` iterates the **manifest's** capabilities and drops anything a
worker reports outside them — the manifest is the operator's ceiling (41 D2), which is right. Phase
57 added the `video` kind, 58 catalogued three video recipes, 59 built a console for them, and the
manifest was never told: the worker declared `video: wan-t2v-1.3b`, the node discarded it, and **no
clip could be generated through any published image**. `BundledNodeTests` now keys on the shipped
recipes' `media` rather than on a list in the test, so a fourth modality fails on the day its first
recipe lands.

### Phase 70 (piper streams) — one chunk per sentence, split so it fits

`PiperVoice.synthesize()` yields **one `AudioChunk` per sentence** and `synthesize_wav` is nothing
more than that loop writing into a `wave` file — so the streaming path is the same synthesis with a
different sink, not a second engine. Read out of the library rather than the docs, which is the
phase-57 habit.

Two things are deliberate and both are about somebody else's constraints:

- **The samples go out headerless whatever the caller asked for.** The 44-byte wav header is the
  edge's, written once from the rate on the first chunk (70 D4), because only the edge knows whether
  `wav` or `pcm` was asked for and only the first chunk knows the rate. A worker that wrote its own
  header would produce one per chunk.
- **The split is `_CHUNK_BYTES` (16 KiB of PCM), not the sentence.** "One sentence" is not a size: a
  caller may send four hundred words with no full stop in them, and a frame over the node's
  `ToolProtocol.MaxChunkPayloadBytes` fails the job (70 D2). 16 KiB is ~21.8 KB once base64 and the
  envelope are on it — under SignalR's own 32 KB default — and ~0.37 s of audio at 22.05 kHz, which
  is the latency granularity a caller actually feels.

Every frame carries `sampleRate`, `sampleWidth` and `channels`, not only the first, so the edge can
refuse a worker that changes its mind halfway instead of concatenating two rates. A format that
cannot be streamed is refused here as well as at the edge — `/api/tools/speak` forwards a payload
verbatim, and a worker that only works when somebody else validated for it has a hole in it.
