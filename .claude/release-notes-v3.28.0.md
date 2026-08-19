# v3.28.0 — the verification day, and the four things it found that nothing else could

**This release ships no feature.** It is the day every published image was pulled onto one box with a
real card and driven, which is what phases 54–59 each deferred so they could verify only the slice
they touched (track D2). The deliverable is `.claude/verification-v3.28.0.md` — a table per image, a
number per claim, and the ones that came back wrong.

It found that **the video capability had never worked**, and that is what turned a documentation
release into a fixing one.

## The headline

v3.25.0 shipped the video seam. v3.26.0 catalogued three video models. v3.27.0 gave them a console
panel, a `media` metric label and a second usage unit. **No clip could be generated through any of
them**, on any published image, for three releases:

- `python/manifests/diffusion.json` declared `image` and `image-edit` and **not `video`**.
  `ToolWorkerPool.Narrow` iterates the *manifest's* capabilities and drops any kind a worker reports
  outside them — which is correct, because the manifest is the operator's ceiling (41 D2) — so the
  worker's `video: wan-t2v-1.3b` was thrown away and `POST /v1/videos` answered `model_not_found`.
  Every phase-57 and phase-59 test passes because the fixtures declare their own manifests.
- With that fixed, every video request then died with `NameError: name 'ftfy' is not defined`.
  `diffusers` calls `ftfy.fix_text` unconditionally in `WanPipeline.prompt_clean` while importing it
  behind `if is_ftfy_available()`. **`ftfy` is not in the image.**
- And `qwen-360` — the whole of phase 49 — could not load at all: `load_lora_weights` *is* the PEFT
  backend, and **`peft` is not in the image** either. The recipe never became offerable; the only
  signal was the node continuing to say "fetching".

Both packages are now pinned in `requirements-diffusion.txt` and **asserted at build time** beside
torch and the encoder, which is the only thing that stops the v3.10.0 shape recurring.

## What else it found

- **The console could not read the fleet on any containerised hub.** `console.js` sent no credential
  at all on its five-second `/api/status` poll, and that path was client-scoped, so it worked only
  where the loopback exemption applied — a `dotnet run` hub, which is where every console screenshot
  this project has taken was made. `/api/status` now also accepts an **admin** key (granting nothing
  new: an admin key already reads `/api/admin/nodes`), and the console sends one.
- **The worker downloaded weights the node had already refused.** A 24 GB box refuses
  `wan-t2v-14b-720p` at startup by name; the worker's fetch planner had no VRAM gate and queued its
  ~75 GB anyway. The budget now reaches the worker as `INFERHUB_IMAGE_VRAM_BUDGET_MIB` — 48 D5's
  defence-in-depth shape, for the other gate.
- **Both deadlines a video job passes through were shorter than a five-second clip.** The diffusion
  manifest's own `requestTimeoutSeconds` was 900 and killed a render **at step 30 of 30, inside the
  VAE decode**, after nine minutes of card. It is now 3600. The hub's `Dispatcher:TimeoutSeconds`
  default of 300 does not cover the model load alone (142.9 s) — **that one is not fixed**, see below.
- **`wan-t2v-1.3b` was the only video recipe without `vaeTiling`.** It predates the field; phase 58
  added it and set it on the two recipes it wrote. Untiled, the decode materialises all 81 frames at
  once, peaked at **24 287 MiB** of a 24 564 MiB card and **never finished** — three attempts, ~50
  minutes of card, zero frames. Tiled: **18 540 MiB** and 378 s end to end.

## The numbers worth keeping

| | |
|---|---|
| First clip ever rendered and watched | 81 frames, 832×480, 16 fps, `seconds: 5.06`, 982 330 B of H.264 |
| First panorama ever rendered and watched | 2048×1024 equirectangular, `seamDelta` 0.04342 at 50 steps |
| `blend` seam repair, on a real panorama | 0.04342 → **0**, kept; **78 of 2048 columns** touched, rest bit-identical |
| Full suite, by hand, as a solution | **1 328 passed / 48 skipped** |
| `:latest` and every flavour alias | resolve to the right digest — the v3.16.1 fix, checked from outside |
| Durable jobs across a restart | `docker restart` → `node_lost`, `docker kill` → `hub_restarted`, no `.tmp` strays |

Phase 55 predicted "80 of 2048 columns touched" from numpy with no card. On a real panorama it is 78.

`wan-t2v-1.3b`'s `vramMiB` is corrected 15 500 → **18 000** from the tiled measurement, with the
arithmetic in the recipe's own `notes`. `sdxl` (8 000 declared, ~18 000 observed) and `qwen-360`
(19 500 declared, 24 191 observed) are **recorded and not corrected**: their numbers are simply low,
measured on a card also driving a desktop through PyTorch's caching allocator, and swapping one
unaudited figure for another is not an improvement.

## What was NOT established, said out loud

- **`Dispatcher:TimeoutSeconds` is still 300 by default and still too short for video.** One number
  covers every kind of job; raising it hands a wedged *chat* job the same rope. The right answer is a
  per-capability deadline, which is config surface — a feature, and this release ships none. It is
  documented instead, with the measured figures, in `appsettings.json` and the README. **Every video
  number above was measured with `Dispatcher__TimeoutSeconds=1800`.**
- **`qwen-360`'s default of 25 steps is visibly under-denoised at 2048×1024** — every surface carries
  a mottled speckle that is gone at 50 steps with the same seed. **Not changed**: it is a cost
  default, and doubling it doubles the meter. `seamDelta` moved by 0.00156 between those two images,
  so nothing in the pipeline would ever have told anyone.
- **Six recipes were never fetched** — `sd15`, `sdxl-turbo`, `flux-schnell`, `sd35-medium`,
  `qwen-image`, `cogvideox-2b`. Their `vramMiB` figures remain arithmetic.
- **`wan-t2v-14b-720p` was verified as a refusal and never downloaded**, by design.
- **This release's own images have not been pulled and run**, because it changes them and the day's
  matrix ran against v3.27.0. Everything above was verified by mounting the fixed manifest, recipe
  and worker into the published container and by running a patched coordinator from source. **The
  first task of v3.29.0 is to pull `:diffusion` and confirm `video`, `ftfy`, `peft` and the timeout
  are in the artifact.** This is the cost of overturning "nothing is fixed here", and it is stated
  rather than absorbed.
- **No browser was opened.** The console fix is verified at the HTTP layer — the exact call
  `fetchStatus` makes, with and without each key — not by loading the page.
