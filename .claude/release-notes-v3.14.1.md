# InferHub v3.14.1 — the first `sdxl` call now works

**v3.14.0 was dead on arrival for `sdxl` on a fresh volume.** Found the way this class of thing is
always found: by pulling the published image and running it. If you pulled `:3.14.0-diffusion`,
pull `:3.14.1-diffusion`.

## What was broken

**The first request for a model whose weights were not on the box downloaded them *inside the
request*.** The manifest allows 900 seconds and that budget included the fetch, so the first `sdxl`
call on a fresh volume returned **502 after 899.99 s** — and so did the second, because the
download had not finished. `sd15` worked, at 79.8 s, only because it is small enough.

**And the download was twice the size it should have been.** `dtype` is what weights are cast to in
memory; `variant` is which *files* are fetched. These repos carry both
`unet/diffusion_pytorch_model.safetensors` (fp32, 10.3 GB for SDXL) and `…fp16.safetensors`
(5.1 GB), and passing `torch_dtype=float16` alone takes the **fp32** file and casts it down. The
recipe said `"dtype": "float16"`, the README said "~7 GB fp16", and 13 GB landed in the cache.

## What changed

**A recipe is declared only once its weights are proven loadable.** A background thread does the
fetching, and the worker re-declares as each model lands. On a fresh volume the node now starts with
nothing offered and fills in:

```
[diffusion] offering recipes: none yet (fetching: sd15, sdxl)
[diffusion] fetching weights for 'sd15' from stable-diffusion-v1-5/…@451f4fe16113 (variant=fp16)
[diffusion] 'sd15' is ready; offering recipes: sd15
[diffusion] 'sdxl' is ready; offering recipes: sd15, sdxl
```

**No request ever waits on a download**, and the fleet never routes at a model that is not there.

**Both recipes carry `"variant": "fp16"`**, so SDXL is a ~7 GB download rather than ~14 GB. A repo
without the variant falls back to the default files **loudly** — silently doubling somebody's
download is the thing that field exists to stop.

Readiness is a marker file, and the reason is worth stating: the obvious checks lie.
`snapshot_download(local_files_only=True)` and `DiffusionPipeline.download(local_files_only=True)`
both return **success against a cache whose UNet is entirely absent** — verified against a
half-downloaded one. Only `from_pretrained(local_files_only=True)` asks the question the next
request will ask, and it is also the load, so the prefetch does it once and records the answer.

## A worker can now change its mind, and a probe that never ran finally does

Two node-side mechanisms carry this, and one of them had been dead code for five releases.

**A late `ready` re-declares.** A worker whose answer to "what can you do" changes while it runs
sends a fresh `ready`; the node re-applies the narrowing clamp and re-reports to its coordinator. It
is additive on the wire, and the clamp is unchanged — a worker still cannot widen its own grant.

**The maintenance loop now pings idle workers.** Phase 41 specified a ping/pong liveness probe,
`PingAsync` was written for it, and **nothing ever called it between v3.9.0 and v3.14.1**. It
matters here because an idle worker has nobody reading its stdout, so a late `ready` sits in the
pipe until something drains it — and it earns its original keep too, retiring a worker that has
wedged without exiting rather than leaving it for the next caller's queue budget. The probe takes
the concurrency slot, because otherwise maintenance holding the only worker out of the idle stack
would let a concurrent request start a second process: two copies of a multi-gigabyte model on one
card.

## Also

- **The `:diffusion` image is 12.1 GB**, not the ~9 GB the docs claimed. Corrected everywhere.
- If you would rather have the weights before the container ever runs, the log now prints the exact
  command — including the `--include` patterns the variant needs:

  ```bash
  huggingface-cli download stabilityai/stable-diffusion-xl-base-1.0 \
    --revision 462165984030d82259a11f4367a4eed129e94a7b \
    --include "*.fp16.safetensors" "*.json" "*.txt" "*/*"
  ```

## What v3.14.0 got right, verified on the card

Recorded because a bugfix release is where it is easiest to lose track of what was already proven,
against `:3.14.0-diffusion` on an RTX 3090 Ti (driver 591.86, Docker 27.3.1, WSL2):

the venv imports in the published image (the v3.10.0 trap is structurally absent — the venv's Python
is the runtime's Python); `sd15` at 512×512 in **79.8 s** cold and **2.0 s** warm, decoded to its
raster and looked at; the environment leak check (`Coordinator__EnrollmentSecret` absent from the
worker's `/proc/<pid>/environ`); all three GPU-gate cases (refuses without CUDA, offers only `sd15`
with `RequireGpu=false`, offers both with `AllowSlowCpu=true`); every edge refusal — `url`, a
malformed size, `n` over the cap, an unknown header value, a missing prompt, a model this node does
not provide, and `401` with no key; the scratch tree **empty** after two successes, two timeouts and
eight refusals; the node still serving with exactly one worker after both timeouts; and
`docker stop` in under 1.2 s with no orphaned Python.

`dotnet test`: **1080 passed, 0 failed, 46 skipped**.
