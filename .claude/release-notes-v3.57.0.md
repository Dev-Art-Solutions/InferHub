# InferHub v3.57.0 — a CPU-only node edits a picture, and an edit is billed for the steps it runs

v3.56 gave a CPU-only node a fast way to draw a picture (`lcm-dreamshaper`, 4 steps) but not to
change one. The only recipe that could edit on a CPU was `sd15`, at 30 steps. v3.57 lets the fast
recipe edit too. Doing that showed that edits on this model would have been billed for half the work
they did.

## What's new

### `lcm-dreamshaper` takes `/v1/images/edits` and `/v1/images/variations`

```bash
curl http://localhost:5080/v1/images/edits -H "Authorization: Bearer $KEY" \
  -F model=lcm-dreamshaper -F image=@lighthouse.png \
  -F prompt="the same lighthouse in winter, snow" \
  -H 'X-InferHub-Image-Strength: 0.75'
```

- **Image-to-image and variations.** The edit pipeline is derived from the one already loaded and
  shares its weights, so editing needs no second load and no extra memory.
- A 512×512 edit took **about 10 s at 16 threads** on a CPU, measured through the worker's own
  code on the pinned `diffusers==0.36.0`.
- **The default strength is 0.75.** At 0.5 the picture keeps its composition and only starts to
  follow the prompt. At 1.0 you get a new picture.
- **No mask.** `diffusers` has no inpainting pipeline for this kind of model. An edit that sends a
  `mask` is a **400** naming the recipe, and it is refused *before* the 4 GB model loads, not a
  minute later. `sd15` still inpaints on a CPU.
- **A variation has no prompt to hold the subject.** At 0.75, our test lighthouse kept its sunset
  and its coast and came back as a castle. Send a lower strength if you want more of the same thing.

### An edit is billed for the steps that actually ran

For edits, InferHub meters the denoising steps that ran rather than the steps you asked for. On the
SD-family models, strength 0.6 at 30 steps runs 18 of them, and 18 is what gets billed.

**This model does not work that way.** It uses strength to decide where in the schedule to start,
then runs every step: 4 steps at strength 0.25, 0.5, 0.75 and 1.0 all ran 4, counted one by one.
Billed the SD way, an edit at 0.5 would have been charged 2 steps for 4, with a progress bar reading
"3 of 2". Recipes can now say this (`"strengthKeepsSteps": true`), so `lcm-dreamshaper` reports and
bills 4 at any strength.

**The worker now checks its own arithmetic.** It counts the steps each pipeline actually runs. If
that differs from what was billed, the node logs `STEP COUNT MISMATCH` with the pipeline's name.
The next model whose step arithmetic nobody measured will show up in the log on its first edit,
instead of being billed wrong unnoticed.

Two new optional recipe fields, both documented in `python/recipes/README.md`:

| Field | Meaning | Absent |
|---|---|---|
| `"inpaint": false` | this model has no inpainting pipeline: a masked edit is refused before loading | the model can inpaint |
| `"strengthKeepsSteps": true` | strength picks where to start, not how many steps run | steps run = `int(steps × strength)` |

**Nothing changes for any other recipe.** Only `lcm-dreamshaper` sets either field.

## How it is tested

- `CpuImageEditTests` (Node) drive the shipped worker's `edit()` and its per-image loop with a
  stand-in pipeline that runs a chosen number of steps. They cover: the recipe declared for
  editing on a CPU, an LCM edit billed and reported at 4 steps with progress 1/4…4/4, a variation
  at the default strength, the mismatch logged by name when a pipeline runs more steps than were
  billed, and a masked edit refused before the model loads while `sd15` gets past the check.
- Mutation checks: ignoring the new step rule, skipping the mask check, or silencing the mismatch
  check each turns exactly one test red.
- **Run for real**, outside a container: the worker's own `edit()` against the real model on a CPU.
  An edit at 0.5, an edit at the default, a variation, the masked refusal (8 ms) and the negative
  prompt refusal all behaved as described, with no mismatch logged. With the new rule switched off,
  the same run logged `ran 4 steps and 2 were reported`, so the bug was real and the check catches it.
- `dotnet test InferHub.sln`: **1742 passed, 47 skipped** (Shared 202, Coordinator 820, Node 244,
  Mesh 476).

Zero new `PackageReference`, `InferHub.Shared.csproj` is still empty, no new pip requirement, and
no C# changed.

## Checked on the published image

`ghcr.io/dev-art-solutions/inferhub-node:3.57.0-diffusion` was pulled from GHCR and checked first:
it carries `edit_steps` in the worker and `"inpaint": false` / `"strengthKeepsSteps": true` in the
recipe. It was then run as a solo node with no GPU (`Tools__Image__RequireGpu=false`) on an empty
volume, and called over HTTP.

| Step | Result |
|---|---|
| startup | `device: cpu`; `lcm-dreamshaper` fetched in the background, then `offering recipes: lcm-dreamshaper (editing: lcm-dreamshaper; …)`. It is declared for editing, so routing reaches it |
| `/v1/images/edits`, strength 0.5, seed 7 | **200** in 24.1 s; worker: `strength 0.50 (4 of 4 steps) … generate 23.5s` |
| `/v1/images/edits`, no strength header (default), seed 7 | **200** in 23.3 s; `strength 0.75 (4 of 4 steps)` |
| `/v1/images/edits`, strength 0.25 | **200**; `strength 0.25 (4 of 4 steps)` |
| `/v1/images/variations`, seed 7 | **200** in 22.6 s; `variation … strength 0.75 (4 of 4 steps)` |
| the three seeded pictures, compared with the run outside the container | the same pictures: largest pixel difference **1/255**, mean 0.000 |
| an edit with a `mask` | **400** in 32 ms: `'lcm-dreamshaper' edits without a mask only: its pipeline (LatentConsistencyModelPipeline) has no inpainting variant…` |
| an edit with a `negative_prompt` | **400** in 17 ms (the v3.56 refusal, now on the edit route too) |
| `STEP COUNT MISMATCH` in the node log | **none**, across all four runs |

About 23 s per edit in the container is at the shipped manifest's 4 threads. The ~10 s above is at
16 threads.

**Found while doing this, not an InferHub bug:** Docker Desktop's engine died twice during the
check, the second time with the keepalive loop running, and needed a full restart each time.

## Not established, said out loud

- **The hub's usage ledger for an LCM edit.** The check ran on a solo node, which keeps no ledger.
  The `steps` that the hub meters come from the worker's result frame, which said 4, and the tests
  cover that path. A coordinator was not in the loop.
- **An `lcm-dreamshaper` edit on a graphics card.**
- **Variation quality** beyond the one picture looked at. A lower default strength for variations
  was considered and not made.
