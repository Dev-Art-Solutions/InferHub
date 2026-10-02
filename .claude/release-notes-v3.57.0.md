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

## Not established, said out loud

- **An `lcm-dreamshaper` edit on a graphics card.**
- **Variation quality** beyond the one picture looked at. A lower default strength for variations
  was considered and not made.
