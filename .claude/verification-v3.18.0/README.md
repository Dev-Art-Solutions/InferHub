# v3.18.0 published-image verification — the pictures

Rendered on the RTX 3090 Ti box, 2026-08-10, from
`ghcr.io/dev-art-solutions/inferhub-node:3.18.0-diffusion` pulled from GHCR. The full run is
recorded in `plan/roadmap-v3.14-to-v3.19-image-generation.md`, phase 50 §7.

| File | What it is |
|---|---|
| `room.png` | `sd15`, 512×512, seed 7 — the picture the edit works from |
| `mask-alpha.png` | the mask, RGBA: **transparent** in a centred rectangle, which is what OpenAI's convention means by "edit here" |
| `edited.png` | the inpaint at `strength: 0.99`, seed 11. The window is exactly the masked rectangle; the bricks, the skirting and the floor are untouched |
| `variation.png` | `/v1/images/variations` on `room.png` — the same room, different bricks and floorboards, no prompt involved |

**`room.png` → `edited.png` is the before/after pair for the social post**, and the mask belongs in
the pair too: without it the claim "only the masked region changed" is not checkable by looking.

The same request against a mesh produced a **byte-identical** `edited.png` (sha256 `6c8f4497…`), and
so did the same mask flattened to RGB and sent under `X-InferHub-Mask-Convention: luminance` — which
is the mask-convention claim as a hash rather than as an opinion.
