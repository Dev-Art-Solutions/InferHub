# InferHub v3.23.0 — the seam can be repaired, because somebody asked

Since v3.17 every 360° panorama has come back with a number on it: `seam_delta`, the mean absolute
difference between the image's first and last columns — the pair that becomes adjacent the moment
the picture is wrapped onto a sphere. Over `Tools:Image:SeamWarnThreshold` the result carries a
`seam` warning, and then nothing happened. We measured the flaw for six releases, told the operator
their panorama had a visible join, and handed them no way to close it inside the product.

That refusal is worth reading again, because this release keeps most of it:

> *A roll-and-inpaint fix is a second generation pass the caller did not ask for, did not watch, and
> would be billed for.*

Every clause of that is about **consent**, not about repair being wrong. This release supplies the
asking.

```bash
curl http://localhost:5080/v1/images/generations \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -H 'X-InferHub-Image-Seam-Repair: blend' \
  -d '{"model":"qwen-360","prompt":"a lighthouse at dusk","size":"2048x1024"}'

# → 200  "seam_delta": 0.011, "seam_delta_before": 0.134, "seam_repair": "blend"
```

**Send no header and nothing changed** — the same body, the same warning, the same headers on the
content route, byte for byte. `SeamMetricTests.WithNoHeaderNothingAboutTheResponseChanged` asserts
that rather than assuming it.

## Two gates, and the default is still no

`Tools:Image:SeamRepair` on the node is what an **operator** permits — `off` by default, then
`blend`, `diffuse`, `any` — and the header chooses within it. It is `Tools:Allowed`'s shape one level
down: a ceiling the caller cannot raise. A deployment that changes no configuration cannot be made to
spend a single step by a header alone, and the refusal names the key.

**No threshold ever triggers a repair.** The threshold decides whether to *warn*; a number that
decides to spend somebody's GPU is the tool overriding the person with a helpful expression on. That
is the half of the original refusal that survives this release completely intact.

**`diffuse` does not imply `blend`.** The four values name mechanisms rather than tiers, so an
operator who thinks a feathered band is worse than an honest seam can permit only the real repair.
`any` is how "both" is said.

## The cheap one is the default mechanism, and that is the point

**`blend`** halves the jump between the two columns the metric compares and ramps each half back to
zero over about 2% of the width, so the extreme columns meet exactly in the middle and the wrap is
continuous. It is numpy on the array the VAE already produced: **milliseconds, no VRAM, no steps, and
nothing added to the ledger.** Shipping only the expensive mechanism would have made the answer to
"my panorama has a line in it" cost a second render every time — which is precisely the bill the
original refusal declined to hand anybody.

**What `blend` does not do, said plainly:** it closes a *tonal* discontinuity, not a *structural*
one. A seam cutting through a doorway comes back with no visible step in brightness and the doorway
still not lining up. That is what `diffuse` is for. (The brief predicted this would "smear detail
across the band" — that describes a cross-fade, which was the other implementation and which ghosts.
The feather does not smear; it simply cannot make structure continue.)

**`diffuse`** rolls the join into the middle of the picture and inpaints a band of it — upstream's
`fix_seam`, over the same resident weights. It costs `int(steps × 0.4)` more steps, metered in
`megapixel_steps` rather than in a unit of its own, and included in `total_steps` **from the first
progress frame**: a bar that reaches 100% and then starts again is a bar that has lied once.

## A repair that does not help is discarded, and says so

The worker measures, repairs, measures again, and **keeps the original if the number did not fall** —
reporting the mechanism, both numbers, and the `seam` warning the image still earns. Two equal
numbers are a real outcome: the pass ran and did not help. A pass that quietly made somebody's
panorama worse is the one outcome nobody would ever go looking for.

`seam_delta` is always the **current** image's, so a client that has never heard of this release
reads the field it already knew and gets a true answer about the bytes it was handed.
`seam_delta_before` is what the same measurement said first, and it is **absent** when no repair was
asked for — absence stays absence.

The same three facts reach a client on every surface: the response body, the job document, and
`X-InferHub-Image-Seam-Repair` / `-Seam-Delta` / `-Seam-Delta-Before` on the content route, which is
the one request that has no JSON to carry them.

## What was established, and what was not

**`diffuse` is possible on the pinned library, and that was checked before it was built.**
`diffusers==0.36.0`'s `AUTO_INPAINT_PIPELINES_MAPPING` maps `qwenimage` → `QwenImageInpaintPipeline`,
whose constructor takes the *same five components* `QwenImagePipeline` does — so
`AutoPipelineForInpainting.from_pipe` re-derives it over the resident weights at no extra VRAM, LoRA
included. That is the path phase 50's `derived_pipeline` already uses for an edit, and it is reused
rather than rebuilt, which also means its existing failure is the right one: a pipeline family
diffusers cannot re-derive fails **naming the class**. `diffuse` is never quietly served as `blend`.

> **None of that was run on a GPU.** The mapping and the constructor signatures were read out of the
> pinned wheel. **No panorama has been rendered for this release, no repaired seam has been looked
> at, and no before/after pair has been measured on real weights.** The brief asked for a GPU box
> before anything else was built, and there was not one. Every number in this document about the
> *mechanism* is arithmetic; every number a test produced came from a synthetic raster. **A synthetic
> seam is not evidence — it is the unit test.** Phase 60 is the verification day, and this is on its
> list.

**The published images were pulled and run — for everything that does not need a card.** Both
`inferhub-node:3.23.0` and `inferhub-coordinator:3.23.0` pulled anonymously (no visibility flip, as
usual) and carry revision `f1d8947`, which is the tag. On them, in a container:

| Check | Result |
|---|---|
| Solo node, `X-InferHub-Image-Seam-Repair: inpaint` | **400** with the whole sentence — both mechanisms and what each costs |
| Hub, the same header spelled `INPAINT` | **400**, identical sentence — the parse is one place and case-insensitive |
| Solo node, `blend`, no image capability on the box | **503** `capability_unavailable` + `Retry-After: 30` — a header alone routes nothing and spends nothing |
| Solo node, `off` spelled explicitly | the same **503**: "off" and "absent" are one request |
| `Tools__Image__SeamRepair=blends` | **refuses to boot**: *"Tools:Image:SeamRepair is 'blends'; it must be 'off' (the default), 'blend', 'diffuse' or 'any'."* |
| `Tools__Image__SeamRepair=any` | boots, `/health` 200 |

**What was not run on the artifact is the repair itself**: `:diffusion` is 12.1 GB and needs a card,
so no request has reached a real `qwen-360` through a published image.

**What could be measured without a card was, on the worker's own code.** `seam_blend`, `seam_delta`
and `repair_seam` are imported straight out of `diffusion_worker.py` — they need `numpy` and `PIL`
and nothing else — and run against 2048×1024 rasters:

| Raster | before | after | columns touched |
|---|---:|---:|---:|
| A 0→255 horizontal ramp (the worst case a seam can be) | 1.000000 | **0.000000** | 80 of 2048 |
| Fine noise with a hard tonal step at the join | 0.351284 | **0.000000** | 80 of 2048 |
| A sinusoid whose period is the width (already wrapping) | 0.000000 | 0.000000 | **0** — discarded by D4 |

Two things worth having from that. **The correction reaches 80 columns of 2048 — 3.9% of the width,
both sides of the join together — and the rest of the picture is bit-identical**, which is the claim
"it does not touch the middle" made as a measurement rather than an intention. And **D4's discard
fires on its own**: the already-wrapping raster came back as the original with two equal numbers and
the mechanism named, which is the outcome the rule exists to make visible.

*Measured against the code at the `v3.23.0` tag rather than against `main`, because the artifact is
what the numbers have to describe.* A one-line refinement landed on `main` afterwards — the ramp now
reaches exactly zero at the far edge of the band instead of stopping about 3 grey levels short of it,
which removes a faint step there. It changes **two columns of 2048 and no reported number**, and it
is in v3.24.0, not in this image.

This is still not a panorama. It says the arithmetic does what the docs say on real arrays; it says
nothing about what a repaired doorway looks like.

## Tests

`dotnet test InferHub.sln` — **1 255 passed, 48 skipped**, and the skips are the usual gated ones
(Postgres, Qdrant, a live Ollama). The phase's own slice:

- **`InferHub.Tests.Shared/ImageContractTests`** — the header parse in three spellings, the
  unknown-value refusal naming both mechanisms, the ceiling's exact-match table (including
  `diffuse`↛`blend`), the envelope with a repair and the envelope without one, the discarded-repair
  shape, and the content headers under a `bg-BG` culture.
- **`InferHub.Tests.Mesh/SeamMetricTests`** — no header is byte-identical to v3.22; `blend` lowers a
  synthetic seam and takes the warning with it; D4's discard, through a fixture mechanism that
  deliberately makes the number worse; the operator clamp naming the key; the default ceiling
  permitting nothing; a flat recipe refusing to repair a seam it does not have.
- **`InferHub.Tests.Mesh/ImageParityTests`** — a repaired panorama and a refused mechanism, shaped
  identically on a hub and a solo node. The comparison now reads `projection`, `seam_delta`,
  `seam_delta_before` and `seam_repair`, which is the v3.17 lesson applied before it recurs:
  `revised_prompt` drifted between the two hosts for three releases *with a parity suite running*,
  because the suite was not comparing the field.
- **`InferHub.Tests.Mesh/ToolSecurityTests`** — the ceiling fails startup on a value nobody
  recognises, and reaches the worker's environment.

**The fixture implements the same `blend`, and that is a fixture.** It proves the request reaches the
process, the numbers come back, and the contract holds. It proves nothing about a photograph.

## Deviations from the brief, recorded

- **The operator's ceiling is enforced in the worker, not in a node-side clamp**, which is where the
  brief's task list put it. Nothing on the node parses an image payload — `ImageRecipeCatalogue`
  reads three fields out of *recipe files*, and the tool runtime has never seen a request body — so
  a clamp there would mean teaching the node to read diffusion requests in order to enforce a key
  the process that spends the steps can enforce itself. This is phase-48 D5's shape with the
  redundant half removed: the node **states** the grant into an environment it clears first, and the
  worker refuses **naming the key**. `ToolOptions` holds the key and its validator; `SeamRepairModes`
  holds what the four words mean.
- **The header is parsed for edits as well as generations.** `run_batch` is literally the same loop
  for both, so honouring it on one route and dropping it on the other would be a difference nobody
  could explain. There is no equirectangular editor in the catalogue today; what this buys is that
  the day one lands, nothing has to be remembered.
- **The content route's three seam headers are emitted only when a repair was asked for.** Gating
  them is what lets "a request with no header is answered exactly as v3.22 answered it" cover the
  header list, which is the only version of that claim worth making.
- **`blend`'s stated trade is narrower than the brief's.** See above: the feather is exact at the
  join, so the number essentially always falls and detail is not smeared. Repeating the brief's
  sentence would have been a docs claim the implementation does not make.

## Rules

**Rule 5 survived again.** Zero new `PackageReference` — `numpy` was already in
`requirements-diffusion.txt` — no image library anywhere in the C#, and `InferHub.Shared.csproj` is
still an empty `<Project Sdk="Microsoft.NET.Sdk">`. **Rule 7 is untouched**: what this release adds
to a record is a mechanism name, two floats and a step count.
