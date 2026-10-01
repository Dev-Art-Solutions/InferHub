# InferHub v3.56.0 — a picture on a CPU in seconds, and a negative prompt that would be ignored is refused

Until now the only image recipe a CPU-only node would offer was `sd15`: 30 steps, tens of seconds
for a 512×512 picture, and everything else wanted a graphics card. v3.56 adds a small, fast one —
and adding it found a bug that had been in the catalogue since v3.16.

## What's new

### `lcm-dreamshaper` — 4 steps, on a CPU

```json
{ "model": "lcm-dreamshaper", "prompt": "a lighthouse on a rocky coast at sunset, oil painting", "size": "512x512" }
```

- `SimianLuo/LCM_Dreamshaper_v7`, pinned to a commit. It is a Latent Consistency Model distilled
  from Dreamshaper 7, an SD 1.5 fine-tune: the same 0.9B UNet as `sd15`, but **4 steps instead of
  30**, and no second "unconditional" pass per step.
- Offered on a CPU-only node (`Tools:Image:RequireGpu=false`) with no other setting.
- Sizes 512×512 (the default) and 768×768. Generate only — no edit or variation.
- The download is about 4.3 GB. The repo has no half-precision files, and its ONNX copies and
  single-file checkpoint are never fetched.
- **Licence:** declared as CreativeML OpenRAIL-M. The repo is tagged MIT, but its weights are
  distilled from Dreamshaper 7 and SD 1.5, both OpenRAIL-M, whose use restrictions carry over to
  derivatives. We declare the stricter reading. Both are permissive in this catalogue, so nothing is
  gated.

Measured on a 64-core machine, through the worker's own download-and-load code under the pinned
`torch 2.9.1+cpu`:

| Threads | 512×512, 4 steps |
|---:|---:|
| 4 (what the shipped `diffusion.json` sets with `OMP_NUM_THREADS=4`) | 27 s |
| 16 | 9.2 s |
| 32 | 5.9 s |

768×768 took 36 s at 8 threads.

### A negative prompt that would be ignored is now a 400

A negative prompt only does anything when the model runs classifier-free guidance. When it does
not, the pipeline drops the negative prompt without an error or a warning, and you get a 200 and a
picture of exactly what you asked to leave out. Running the new model is what showed this, and it
was not only the new model:

- **`lcm-dreamshaper`** has no negative prompt at all. Its pipeline accepts the argument and ignores
  it.
- **`flux-schnell`** only uses one under a setting (`true_cfg_scale`) its recipe never sets, so it
  has always ignored them.
- **Every other pipeline** skips the guidance pass when guidance is 1 or less. **`sdxl-turbo`'s
  default guidance is 0.0**, so it ignored every negative prompt it was ever sent.

Now the worker refuses such a request, before the model loads, with a message that says why and what
would make it work: drop the negative prompt, or (for the second case) raise
`X-InferHub-Image-Guidance` above 1. Recipes declare "no negative prompt" with
`"negativePrompt": false`; `lcm-dreamshaper` and `flux-schnell` have it. This applies to text to
image, edits and video.

**This is the one behaviour change in the release.** A request that used to get a 200 while its
negative prompt was silently ignored now gets a 400. Requests without a negative prompt, and
requests whose negative prompt was actually used, are unchanged.

Also fixed: when a pipeline rejected an argument, the worker retried without the progress callback
**and** without the negative prompt. It now drops only the callback.

## How it is tested

- `NegativePromptTests` (Node) run the shipped `diffusion_worker.py`'s own functions: both
  refusals by name, `sdxl-turbo` refused at its default guidance and accepted at 2.0, `qwen-image`
  accepted at 4.0, the retry keeping the negative prompt, and the new recipe offered on a CPU.
- Mutation checks: removing the recipe check, the retry's refusal, or the guidance rule each turns
  a test red.
- The facts behind the rule were read from the pinned `diffusers==0.36.0` for every pipeline class
  the catalogue uses (SD, SDXL, SD3, FLUX, Qwen, Wan, CogVideoX, and the inpainting/img2img ones).
  LCM ignoring the argument was measured by calling it.
- `dotnet test InferHub.sln`: **1738 passed, 47 skipped** (Shared 202, Coordinator 820, Node 240,
  Mesh 476).

Zero new `PackageReference`, `InferHub.Shared.csproj` is still empty, and no new pip requirement.

## Checked on the published image

*Pending: this section is filled in once the tag's images are published and pulled.*

## Not established, said out loud

- **`lcm-dreamshaper` on a graphics card.** The 3.5 GB VRAM figure is arithmetic, not a
  measurement.
- **`sdxl-turbo` and `flux-schnell` ignoring a negative prompt in an actual render.** That was read
  from the pinned source code, not generated.
