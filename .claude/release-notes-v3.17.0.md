# InferHub v3.17.0 — 360° panoramas from a 20B model, on one card

Phase 49 of the image track, and it is the model this whole track was asked for.
[`ProGamerGov/qwen-360-diffusion`](https://huggingface.co/ProGamerGov/qwen-360-diffusion) is a
**rank-128 LoRA, MIT licensed**, over Qwen-Image's 20B MMDiT, and it produces **equirectangular**
panoramas — 360° of longitude across, 180° of latitude down, left edge continuing into the right.

```bash
curl http://localhost:5080/api/images/jobs \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"qwen-360","prompt":"a Bulgarian mountain monastery courtyard at golden hour, photograph","size":"2048x1024"}'
```

```json
{
  "created": 1785000000,
  "data": [{ "b64_json": "iVBORw0…", "size": "2048x1024", "seed": 42,
             "projection": "equirectangular", "seam_delta": 0.014, "revised_prompt": null }],
  "prompt_augmented": true,
  "trigger": "360 degree panorama with equirectangular projection"
}
```

## A 2:1 aspect is not a suggestion

360° over 180° is exactly two to one. A size outside a flat recipe's buckets gives you duplicated
limbs and doubled horizons — visibly bad, and everybody knows what happened. A **non-2:1
equirectangular render looks perfectly fine** and wraps wrongly, and the person who discovers that is
wearing a headset three days later.

So it is a `400`, and unlike every other size refusal it says *why*:

```
this recipe renders 360-degree equirectangular panoramas, which are always 2:1 (360 degrees of
longitude over 180 of latitude), so it cannot render 1024x1024. It was trained on: 2048x1024,
1536x768, 1024x512. A non-2:1 render does not fail — it wraps wrongly in a viewer, which is why
this is refused rather than attempted.
```

## The trigger phrase is appended, not demanded and not silently inserted

The model wants "equirectangular" or "360 panorama" in the prompt. Three options were on the table:

1. **Rewrite the prompt silently.** Rejected — nothing in InferHub is silently substituted, and a
   prompt is your own words.
2. **Refuse a prompt without the trigger.** Rejected — pedantry about a model whose entire purpose is
   one thing, and it makes the first request everybody sends a `400`.
3. **Append when absent, and say so.** Shipped: `prompt_augmented: true` and the phrase that was
   added. `"autoTrigger": false` in the recipe turns it off, and the flag is reported either way, so
   a client never has to infer what happened to its own prompt.

The trigger is a **recipe constant**, so unlike your prompt it may appear in a log — which matters,
because "why does this not look like a panorama" is almost always "the trigger did not apply", and a
diagnosis nobody can see is not one. The prompt it was appended to is still written nowhere.

## The seam is measured, reported, and never repaired

`seam_delta` is the mean absolute difference between the image's first and last columns — the pair
that becomes adjacent once it is wrapped onto a sphere — normalised to 0–1. It costs two numpy
operations on an array the VAE already produced, which is why it is unconditional rather than behind
a flag: a metric nobody switches on is a metric nobody has.

Over `Tools:Image:SeamWarnThreshold` (default `0.08`) the result carries `"warnings": ["seam"]`. It
is a **warning on a `200`**, never a failure: a slightly visible seam is your own problem and your own
aesthetic judgement, and failing a two-minute job over a threshold would be the tool overriding you
about a picture only you can see.

And it is **not repaired**. Upstream ships a roll-and-inpaint fix and it is a good tool to reach for
on purpose — but running it unasked is a second generation pass with its own cost, its own artifacts
and its own line on your bill, for a decision you never made.

## Projection is metadata, and it reaches the client on every surface

A 2048×1024 panorama and a 2048×1024 landscape photograph are the same bytes in the same shape.
Every viewer on the internet guesses from the aspect ratio, and it is wrong for both of them.

So the worker **declares** it, and it travels: on each image in the response body, on the job
document, and as `X-InferHub-Image-Projection` on `GET /api/images/jobs/{id}/content/{index}` — which
is the one request that has no JSON to carry it.

**A flat recipe reports `"flat"` rather than omitting the field.** An absent projection is
indistinguishable from a node too old to have an opinion, and a client that has to tell those apart
has learnt nothing.

## A viewer, in 330 lines of hand-written WebGL

`/console.html` → **360° viewer**. Paste a job id, and it picks its renderer from the declared
projection rather than from the shape of the image. Drag to look, scroll to zoom, arrow keys when
focused, and a **Flat** toggle — because being able to see the raw equirectangular frame is how you
tell whether a wrong-looking picture is the model or the viewer.

No npm, no bundler, no three.js from a CDN. Design rule 3 is build-free UI, and a third-party script
on an admin console that holds cordon and model-pull rights is a worse trade than an afternoon of
`gl.texImage2D`. A browser with no WebGL gets the flat image and a sentence saying so, rather than a
black rectangle.

## `qwen-image` and `qwen-360` are two model ids over one base

Not one model with a scale parameter. The router keys on `(capability, model)` and nothing else, so a
client asking for `qwen-image` must never receive a panorama — and a `loraScale` header would make
what you get depend on a header, which is the reproducibility problem quantization is already a recipe
field to avoid.

What the *worker* does about it is an optimisation and not part of the contract: if the base is
already resident it unloads one LoRA and loads the other, which is seconds rather than the 40–90 s a
20B reload costs. Any failure in that path falls back to a full load.

An adapter carries its **own pinned revision** — a second repository is a second pin — and its own
licence, because a permissive base with a non-permissive LoRA on it is not a permissive model. The
readiness marker includes an adapter fingerprint, so moving an adapter's revision re-proves the
recipe rather than trusting a marker written for different weights.

## Two facts settled from upstream rather than guessed

The phase brief flagged both, and getting either wrong produces images that are *plausible and not
panoramic* — the failure the seam check exists to catch a cousin of.

- **The loading path** is the ordinary `Qwen/Qwen-Image` repo with a bitsandbytes nf4 config, then
  `load_lora_weights(repo, weight_name=…)`. Not a pre-merged quantized checkpoint.
- **The LoRA's quantization variant does not have to match the base's.** Upstream's own nf4 script
  pairs an nf4 base with the **int8-trained** adapter; the int4-trained ones exist for fp8
  transformers, where downcasting artifacts are the problem. The brief guessed int4, and the guess
  was wrong.

Both are written into the recipe's `notes` field, because "which loading path" is exactly what the
next reader will otherwise re-derive incorrectly.

## Also in this release

- **`ImageRenderer.Envelope` is now the one place the OpenAI Images envelope is written.** Three
  surfaces built it by hand, and a shared null-ignoring serializer policy meant the **hub emitted
  `revised_prompt: null` while a solo node omitted it** — for three releases, with a parity suite
  running. Which keys are present is part of the contract, so they are spelled out now.
- **`qwen-image` gained `guidanceParameter: "true_cfg_scale"`.** Qwen's MMDiT has two guidance
  inputs and this pipeline's real classifier-free guidance is the second one; passing the wrong one
  does not error, it just produces something the recipe was not tuned for.
- `Tools:Image:SeamWarnThreshold` (`0.08`) — new key, and `0` silences the warning without silencing
  the measurement.

## Compatibility

Additive. A v3.16 deployment that changes no config behaves identically, with one deliberate
addition: every image in a `/v1/images/generations` response now carries `"projection"`, which is
`"flat"` for all six pre-existing recipes. Zero new `PackageReference`, no npm, no CDN script, and
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">` — the only thing that
ever decodes a pixel is your browser.

`dotnet test`: **1154 passed, 0 failed, 48 skipped.**

## Not yet verified

**The published `:3.17.0-diffusion` image has not been run on the 3090 Ti box at the time of
writing**, and the numbers that matter most are exactly the ones no test can produce: a 2048×1024
panorama opened in the viewer and *looked at* — the seam, the horizon, the poles — the same file in
an off-the-shelf 360° viewer, the adapter swap timed against a full recipe swap, and an observed
`seamDelta` on a real render. v3.14.0 and v3.16.0 were both dead on arrival with a thousand tests
passing, so nothing about the model's output is claimed here until it has been.
