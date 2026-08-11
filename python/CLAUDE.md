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

**D5 — The seam is measured and reported, never repaired.** Mean absolute difference between the
first and last columns — adjacent once wrapped — over 255. Two numpy operations on an array the VAE
already produced, which is why it is unconditional: a metric behind a flag is a metric nobody has.
Over `Tools:Image:SeamWarnThreshold` (0.08) the result carries a `seam` warning, and it is a
**warning on a 200**: phase-35 D4 against phase-37 D4 again, because a visible seam is the operator's
own aesthetic judgement and failing a two-minute job over a threshold would be the tool overriding
the person. **And it is not repaired** — upstream's `fix_seam` is a second generation pass with its
own cost and its own artifacts, and running it unasked would bill somebody for a decision they never
made. `seam_delta` returns `None` rather than raising on anything unexpected: a measurement that
could fail a two-minute job is worse than no measurement.

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

