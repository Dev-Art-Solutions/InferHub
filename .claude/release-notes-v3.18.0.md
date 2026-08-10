# InferHub v3.18.0 — image editing, and the mask convention everybody gets backwards

Phase 50 of the image track. Inpainting, image-to-image and variations, on OpenAI's edits API —
multipart and all — so pointing an existing app at your own card stays a base-URL change.

```bash
curl http://localhost:5080/v1/images/edits -H "Authorization: Bearer $KEY" \
  -F model=sdxl -F image=@room.png -F mask=@mask.png \
  -F prompt="a tall window with morning light" \
  -H 'X-InferHub-Image-Strength: 0.75' \
  | jq -r '.data[0].b64_json' | base64 -d > edited.png
```

Three shapes, and the difference between them is what you send:

| Route | Sends | Is |
|---|---|---|
| `POST /v1/images/edits` with a `mask` | image + mask + prompt | **inpainting** — the masked region only |
| `POST /v1/images/edits` with no mask | image + prompt | **img2img** — the whole picture, `strength` far |
| `POST /v1/images/variations` | image | **more of this picture**, no prompt at all |

## The mask convention everybody gets backwards

**OpenAI's edits API treats a fully *transparent* pixel as the area to edit. `diffusers` takes a mask
where *white* is the area to inpaint. These are opposite.**

```
   your mask (OpenAI)              what the pipeline wants
   ┌──────────────────┐            ┌──────────────────┐
   │▓▓▓▓▓▓░░░░░▓▓▓▓▓▓▓│            │      █████       │
   │▓▓▓▓▓▓░░░░░▓▓▓▓▓▓▓│   ──────▶  │      █████       │
   │▓▓▓▓▓▓░░░░░▓▓▓▓▓▓▓│            │      █████       │
   └──────────────────┘            └──────────────────┘
    ▓ opaque = keep                  █ white = inpaint
    ░ transparent = EDIT THIS        (blank = keep)
```

Get it backwards and nothing errors. It edits everything *except* what you selected, which reads as a
broken model rather than a backwards mask — and you find out by looking at the picture.

So there are two refusals rather than a guess:

- **A mask with no alpha channel is a `400`.** Under OpenAI's convention a fully opaque image selects
  nothing, which nobody has ever intended. Reading it as "edit everything" would be a silent
  substitution of the most destructive possible interpretation; reading it as "edit nothing" would
  hand you your own picture back with a `200` on it.
- **A mask whose size differs from the image is a `400` naming both.** A mask names *which pixels*,
  so it is never rescaled — the edit would land next to what you chose.

Already have a white-is-edit mask? `X-InferHub-Mask-Convention: luminance`. An unknown value is a
`400` that names both **and says which is which**, because two words whose difference is invisible
until you look at the result are not a helpful list.

**The conversion happens in the worker, and that is a design constraint rather than a convenience.**
Turning one convention into the other means reading an alpha channel out of a PNG, and *nothing in
InferHub's C# ever decodes a pixel* — there is no image library on the hub, by design. So the shared
library owns what the conventions **are**; the inversion happens where PIL already is. The cost is
one round trip to find out a mask is wrong, which is the same trade this project already took for
aspect buckets in v3.14 and for the 2:1 refusal in v3.17.

## Strength is a header, and what you are billed is the steps that ran

OpenAI's edits API has no `strength`, and image-to-image without one is meaningless.

```
X-InferHub-Image-Strength: 0.75      # 0 keeps your picture, 1 ignores it
```

Absent, the recipe's `defaults.strength` applies — 0.75 for both editable recipes. Out of range is a
`400`. A header rather than a body field for the same reason `steps` and `guidance` are: additive by
construction, so it cannot collide with whatever OpenAI adds next.

**`diffusers` enters the schedule at `int(steps × strength)`**, so 30 steps at 0.6 denoises for 18 —
and 18 is what the worker reports, what the progress frames count up to, and what the ledger gets.
Billing the asked-for 30 would charge for work nobody did.

## Not every model can edit, and the refusal names the ones that can

Editing is its own capability, `image-edit`, declared per recipe:

| Recipe | `operations` |
|---|---|
| `sdxl`, `sd15` | `generate`, `edit`, `variation` |
| everything else | `generate` |

FLUX.1-schnell has no official inpainting pipeline; SDXL does. So:

```json
{ "error": { "code": "capability_unavailable", "message":
  "no node currently provides 'image-edit' for model 'flux-schnell'. Models on this fleet that do: sd15, sdxl" } }
```

That is fleet state, not authorization — the distinction v3.8 wrote down. And it is a **second
capability kind** rather than a per-model operation list, because the router filters on
`(capability, model)` and nothing else: teaching it to read a nested operation set would mean
teaching the affinity, the queue and the saturation logic the same thing.

A generate-only recipe still generates. A capability refusal that took the model offline entirely
would be a much bigger claim than the one being made.

## Editing is a job like any other

Same queue, same per-step progress, same cooperative cancel, same five-minute in-memory retention,
same read-once collection. The async route takes multipart too:

```bash
curl http://localhost:5080/api/images/jobs -H "Authorization: Bearer $KEY" \
  -F operation=edit -F model=sdxl -F image=@room.png -F mask=@mask.png \
  -F prompt="a tall window with morning light"
```

JSON generates; multipart edits. A multipart submission that names no `operation` is a `400` naming
both — this is InferHub's own contract, where ceremony is cheaper than a silent substitution.

**This is not the `background: true` flag v3.15 refused.** That one made a single route answer two
incompatible *response* shapes depending on a field. Here the response is the same job document
either way, and the request shape is decided by `Content-Type`, which is what content types are for.

## Bytes travel down the mesh for the first time

Every attachment InferHub has ever moved went node → hub. An edit is the first thing that sends
megabytes the other way, so the release carries the v3.10.0 assertion in the direction nobody had
tested: a 3 MB input image crosses a real SignalR connection and **the node is still registered
afterwards**, and still serving.

- `Images:MaxRequestBytes` (25 MB) caps the picture and the mask **together** — a `413` at the edge,
  before anything is buffered onward.
- Each part is additionally capped by `Tools:MaxAttachmentBytes`, because that is what the node
  enforces, and a request that passed the edge and failed at the node is a worse error.
- **The filename you chose never leaves the edge.** The parts travel as `image` and `mask`: what you
  called a file on your disk is metadata about your day.
- Nothing is retained. The bytes are held for the dispatch, written into a per-request scratch
  directory the node deletes in a `finally`, and dropped.

## Upgrading

Nothing to do. Every change is additive and every new key defaults to today's behaviour: a v3.17
deployment that changes no config behaves identically on v3.18. A recipe with no `operations` field
means `["generate"]`, so a catalogue written before this release declares exactly what it declared.

## Not in v3.18, and said out loud

ControlNet, IP-Adapter and reference-image conditioning — each is a per-base-model zoo of auxiliary
weights with its own preprocessors, and every preprocessor is image processing. An outpainting
helper: it is inpainting with a canvas you prepare, and preparing it on the hub would mean decoding
a pixel. Multi-image edit chains. And FLUX inpainting, which has no official pipeline to wrap.

---

**Zero new dependencies**, no image library anywhere in the C#, and `InferHub.Shared.csproj` is still
an empty `<Project Sdk="Microsoft.NET.Sdk">`. `dotnet test`: 1188 passed, 0 failed, 48 skipped.
