# InferHub v3.16.0 — six image models, and the VRAM arithmetic nobody writes down

Phase 48 of the image track. v3.14 made a fleet generate pictures; v3.15 gave that work a clock.
**v3.16 makes it a catalogue** — four more models, two of which do not fit a 24 GB card at all
without quantization, and two of which need a licence decision that is not ours to make.

## The catalogue

| Recipe | Params | Steps | VRAM | Unquantized | Licence | Out of the box? |
|---|---|---:|---:|---:|---|---|
| `sdxl` | 2.6B UNet | 30 | ~8 GB fp16 | — | CreativeML OpenRAIL++-M | yes |
| `sd15` | 0.9B | 30 | ~4 GB fp16 | — | CreativeML OpenRAIL-M | yes — the only CPU-viable one |
| `flux-schnell` | 12B | **4** | ~12 GB nf4 | **~33 GB** | Apache-2.0 | yes |
| `qwen-image` | 20B + 8.3B text encoder | 30 | ~19 GB nf4 | **~60 GB** | Apache-2.0 | yes |
| `sd35-medium` | 2.5B MMDiT | 40 | ~16 GB bf16 | — | Stability AI Community | **accept the licence** |
| `sdxl-turbo` | 2.6B | **1** | ~8 GB fp16 | — | Stability AI Non-Commercial | **accept the licence** |

Two of those numbers are the release. `flux-schnell` and `qwen-image` **do not fit a 24 GB card at
bf16** — 33 GB and 60 GB — and nf4 is what makes them one-card models. Both figures are in the table
because "Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are both true sentences about
different recipes, and a table that gives one is lying to somebody.

## The VRAM budget is declared, not detected

`Node:Vram:BudgetMiB` is a number **you** set. `Node:Vram:ReserveMiB` (2048) is what is held back for
the inference backend and the display. Unset means no gate and v3.15's behaviour exactly.

A recipe that cannot fit `Budget − Reserve` is **not declared**, so the fleet never routes at it and
nobody spends a request finding out. One that would fit but does not *right now* — something else is
mid-job on the card — waits on the tool queue and then gets the same `503` + `Retry-After` as every
other limit here. **Never an out-of-memory error inside somebody's job**, which is the failure this
replaces.

It is declared rather than detected because a node cannot reliably measure the card it is on. Under
WSL2 — the most common GPU-with-Docker setup there is, and where this project's own GPU box lives —
there are no `/dev/nvidia*` device nodes, the host's `nvidia-smi` cannot see the VM's VRAM, and the
only reliable signal that a GPU exists at all is that `libcuda.so.1` loads. A node that guessed would
guess wrong on the exact platform its author develops on, and **a budget that is usually right is
worse than one that is explicitly absent**: the first failure is an OOM at 2am rather than a startup
message. The worker reports what it measures and the node logs a disagreement; it never overrides
you.

## Quantization is a property of the model, not of the request

`"quantization": "none" | "int8" | "nf4"` in the recipe, through `diffusers`' native `bitsandbytes`
integration, applied to the components the recipe names. For `qwen-image` that has to include the
**text encoder**: 8.3B left at bf16 is the difference between fitting and not.

It is a recipe field rather than a header because it changes what the model *is*. Two requests to
`qwen-image` that quantized differently would produce different images from the same seed, and a
per-request knob would make reproducibility a function of a header nobody logged. Want both? Ship
two recipes with two ids.

One mechanism, deliberately: GGUF, Nunchaku and TensorRT are each faster on some model on some card,
and each is a second thing to reason about when a picture comes out worse than expected.

## A licence you have not read is a model this node will not start

`sd35-medium` and `sdxl-turbo` are **loaded, logged by name and not started** until their licence id
is in `Tools:Image:AcceptedLicenses`. The log line names the licence and links to the text.

This is a **fourth** opt-in and it is not redundant with the other three: `Tools:Enabled` consents to
the feature, `Tools:Allowed` consents to *these tools*, `Tools:AllowModelDownload` consents to
reaching the internet, and none of them says "and I accept the Stability AI Non-Commercial Research
Community License". It is a **list** rather than a boolean for the same reason `Tools:Allowed` is:
`sd35-medium` is free for most people who will run it and `sdxl-turbo` is not usable commercially at
all, so one flag would let somebody who read one licence enable both.

A recipe that says nothing about its licence is treated as **not** permissive — one that forgot to
say is one nobody has read the licence of.

**None of this is legal advice.** It is a refusal to make that call on your behalf, silently.

## Weights arrive by an explicit pull

FLUX is ~24 GB on the wire and Qwen-Image is larger. A lazy first-use download blows
`requestTimeoutSeconds` — v3.14.0 shipped exactly that, and every first `sdxl` call was a 502 after
899.99 seconds — and raising that timeout to cover a 24 GB download means every genuinely wedged job
also takes forty minutes to fail.

So a pull is an operator action, on the model-command channel the fleet has had since v2.8:

```
POST   /api/admin/nodes/{nodeId}/tools/diffusion/models/flux-schnell/pull
DELETE /api/admin/nodes/{nodeId}/tools/diffusion/models/flux-schnell
```

Progress relays on the existing `/api/admin/stream` as `model-progress` — no new transport — and the
coalescing, the reused command id and the "a hub restart forgets in-flight commands" property all
come with it. The progress carries **no percentage**: `huggingface_hub` gives no download callback,
and a denominator we would have to guess is a number a dashboard would happily plot. It reports how
many mebibytes have landed instead.

`warm` is refused for a tool model rather than given an invented meaning.

## Switching models swaps weights; it does not restart anything

Loading FLUX is 40–90 seconds, and a restart pays the interpreter and the import of torch on top of
that. So the worker frees the old pipeline, empties the cache, loads the new one, and reports the
swap in the result's `timing` block — a slow request has a visible reason.

`Tools:Image:ResidentRecipes` (default 1) allows more than one resident where the budget permits: a
48 GB card genuinely can hold SDXL and FLUX together and should not thrash. The default is 1 because
the expensive default is the one nobody realises they chose.

After `idleTimeoutSeconds` the node sends an **idle hint** and the worker frees its VRAM and stays
alive. What to free is the worker's business — the node knows nothing about torch.

## Profiles can narrow a recipe, and this is the first ceiling that is arithmetic

`imageRecipes: { "sdxl-turbo": false }` in a node profile. Switching off always works; switching on
is honoured only for a recipe the box has, has accepted the licence of, and has the VRAM for —
refused otherwise **with the numbers in the message**. The hub narrows and never widens: it cannot
make a node accept a licence, find weights or grow a card.

## Also in this release

- **The diffusion worker gained v3.15's per-step progress and cooperative cancel**, which it never
  actually had — v3.15 shipped the job model against the test fixture and the real worker had no
  step callback. Two lines inside `callback_on_step_end`, exactly as the reference library
  documents. Worth saying plainly: until now, "cancel keeps the worker warm" was true of the fixture
  and not of SDXL.
- **A latent bug, found while implementing the idle hint.** A pool with an open model set starts one
  worker eagerly because nothing declares such a capability until a worker reports — and the
  maintenance pass would then happily **retire that very worker** after `idleTimeoutSeconds`, leaving
  a node still declaring models with no process able to re-declare when one lands, and killing a
  prefetch mid-flight. The last worker of such a pool is now kept and hinted instead. Present since
  v3.14; nothing had noticed because nothing waits half an hour.
- `Tools:Image:HuggingFaceToken`, because `sd35-medium`'s repository is **gated** and the node clears
  the child's environment before spawn — so `-e HF_TOKEN=…` reaches the node and stops there.
- The `:diffusion` build now asserts the **pipeline classes** exist, not just that the venv imports.
  A recipe naming a `diffusers` class this build does not have now fails `docker build` rather than
  the first request.

## Compatibility

**No breaking change, and a deployment that changes no config behaves identically to v3.15.**
`Node:Vram:BudgetMiB` defaults to 0, which is no gate at all; `Tools:Image:AcceptedLicenses` defaults
to empty, which affects only the two recipes that are new and non-permissive;
`Tools:Image:ResidentRecipes` defaults to 1, which is what the worker already did.
`ModelCommand.Tool` and `ModelCommandProgress.Tool` are appended nullable fields, so a v3.15 hub and
a v3.16 node — and the reverse — both read exactly what the other meant.

**Zero new `PackageReference`.** `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`. `bitsandbytes`, `sentencepiece` and `protobuf` are lines in
`python/requirements-diffusion.txt` — the same category as phase-39's `curl` — and `bitsandbytes`
arrived **with its first consumer**, which is exactly why v3.14 refused to carry it.

`dotnet test`: **1141 passed, 0 failed, 46 skipped** (up from 1102).

## Images

Five, unchanged in shape. `ghcr.io/dev-art-solutions/inferhub-node:3.16.0-diffusion` carries the six
recipes and `bitsandbytes`; the other four never learned about torch.

| Tag | Size | Arch |
|---|---:|---|
| `inferhub-coordinator:3.16.0` | ~120 MB | amd64 + arm64 |
| `inferhub-node:3.16.0` | ~340 MB | amd64 + arm64 |
| `inferhub-node:3.16.0-ollama` | ~4 GB | amd64 |
| `inferhub-node:3.16.0-tools` | ~6 GB | amd64 |
| `inferhub-node:3.16.0-diffusion` | ~12 GB | amd64 |
