# InferHub v3.16.2 — the diffusion worker was dropping every other request

**Upgrade if you generate images.** v3.16.0 and v3.16.1 answer roughly every *second* image request
and hang the ones in between until their deadline. Found on an RTX 3090 Ti by running the published
image, which is the eighth time in this project's history that step has caught something a green
suite could not (v2.5.1, v3.0.1, v3.5.1, v3.10.0, v3.14.0, v3.16.1, the phase-32 D7 note).

## The bug

Phase 47 taught workers to run a request on a background thread so a `cancel` could arrive while it
was in flight. The C# test worker kept **one** read loop that dispatches and carries on. The Python
reference library grew a **second** reader — a "control pump" that read from the same stream while a
request ran, honoured `cancel` and `ping`, and **discarded everything else**.

The frame it discarded was the next `request`.

```python
while worker_thread.is_alive():
    if not self._pump_control_frames(worker_thread):   # blocks on readline()
        break
```

On a worker that answers quickly the thread finishes before that loop is ever entered, so nothing is
lost — which is why the audio workers and every test were fine. On a worker that takes seconds, the
pump is sitting in `readline()` when the result goes out, swallows the next request, and returns just
as the thread ends. That request is gone. The caller waits out `requestTimeoutSeconds`.

SDXL takes about nine seconds. Measured against the shipped v3.16.1 package, five sequential
requests: **1 of 5 answered**. After the fix: **5 of 5**.

It only became reachable in v3.16.0, because the v3.14.1 diffusion image predates the phase-47
library and nothing else exercised a slow Python worker on a real card.

## The fix

One reader, always — the shape the C# worker already had. A request still runs on its own thread, so
`cancel` still arrives mid-flight; the loop goes straight back to reading instead of blocking, and
one-request-at-a-time is kept by joining the previous thread rather than by refusing to read. No
frame is read and thrown away.

## The test, and the guard on the guard

`PythonWorkerProtocolTests` drives `python/examples/echo.py` through the node's real
`ToolWorkerPool`. It skips when no interpreter is on PATH and runs in CI, where `python3` exists.

**The first version of that test passed against the broken library**, because it used instant echo
requests — and an instant handler finishes before the pump can block. It now sends 600 ms requests
and asserts the progress frames that prove it, so an edit that quietly makes them fast fails rather
than turning the test back into decoration. Instant: 5/5 against the *broken* library. 600 ms: 1/5.

The lesson worth keeping: **the fixture and the reference library had different concurrency designs,
and the suite only ever exercised the one that was correct.**

## Two more things the verification run found

**`black-forest-labs/FLUX.1-schnell` is a gated repository, and v3.16.0's docs said it ran out of the
box.** Its *licence* is Apache-2.0 — so InferHub's licence gate lets it through, correctly — and
Hugging Face still requires accepting terms on the model page plus a read token. The fetch failed
with `GatedRepoError: 401 Client Error` and a request id, which reads as "the model is gone".

- The recipe now carries `"gated": true` (documentation).
- The failure names the model page and `Tools:Image:HuggingFaceToken` instead of surfacing a bare
  401. It is deliberately a *different* sentence from the licence refusal: accepting a licence tells
  **this node** it may run a model, and a token is how **Hugging Face** decides whether to hand the
  weights over — telling somebody to edit `AcceptedLicenses` when the fix is a token wastes their
  afternoon.
- README, the recipes README and the site now say so.

**The background prefetch walked models in alphabetical order.** On a box where `sd15` and `sdxl`
were already downloaded and only needed re-proving, a 40 GB `qwen-image` fetch queued ahead of them
and the node declared **nothing** for as long as it ran. "The model appears when it is ready" is not
true if an unrelated large model is in front of it. Already-cached recipes go first now, then by
declared VRAM ascending — the only proxy for download size a recipe carries.

## Measured on the published image, RTX 3090 Ti

Host: RTX 3090 Ti (24 564 MiB), driver 591.86, Docker 27.3.1 / Docker Desktop (WSL2), Windows 11.

| Check | Result |
|---|---|
| Image identity | 12.3 GB, amd64, `USER app` |
| venv in the published image | torch 2.9.1+cu128, diffusers 0.36.0, transformers 4.57.1, bitsandbytes 0.48.1, `PipelineQuantizationConfig` |
| All six recipes' pipeline classes exist | ✅ including `QwenImagePipeline` and `FluxPipeline` |
| Licence gate, node **and** worker | ✅ both refuse `sd35-medium` and `sdxl-turbo` by name, with the licence and a link |
| Accepting one licence | ✅ enables `sdxl-turbo` only; `sd35-medium` still refused |
| A blank entry in `AcceptedLicenses` | ✅ grants nothing |
| VRAM gate at `BudgetMiB=12288` | ✅ `flux-schnell` (12000) and `qwen-image` (19000) not offered, with the arithmetic in the message |
| SDXL 1024×1024, 30 steps, cold | **21.9 s** (12.0 s load) |
| SDXL 1024×1024, warm ×5 | **8–9 s**, `load 0.0s` every time |
| The PNG | ✅ decoded, 1024×1024 RGB, 80 917 distinct colours — and looked at: a lighthouse in a storm |
| Swap `sdxl`→`sd15`→`sdxl` | **2.0 s** and **7.2 s** of load, against ~22 s for a worker restart |
| Cancel mid-job | ✅ job `cancelled`, worker **stays warm**, next request `load 0.0s` |

`dotnet test`: 1141 passed, 0 failed, 48 skipped.

**Not measured, and not claimed:** `flux-schnell`, `qwen-image` and `sd35-medium` were never
generated — the first and third are gated behind a token this run did not have, and the second is a
~40 GB download. Every nf4 VRAM figure in the docs is a **declared** recipe value, not an observed
one.
