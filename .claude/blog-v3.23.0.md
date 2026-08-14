# Blog post — v3.23.0

- **slug**: `inferhub-3-23-we-measured-a-flaw-for-six-releases`
- **title (EN)**: `InferHub 3.23: we measured a flaw for six releases and refused to fix it`
- **published**: 2026-08-14. EN visible in one shot, BG hidden (the connector is insert-only and the
  slug locks; a hidden draft cannot be flipped).
- **Cloudflare WAF**: no shell commands in the body — a `curl` in the HTML gets the *request*
  blocked, not the command. The header goes in prose instead.
- Content stored **entity-escaped**, like every prior post.
- `list_posts` run first; slug confirmed free; **one** `create_post`.

## The angle

**Lead with the refusal, not the feature.** "We added seam repair" is a changelog line. "We put a
number on a flaw in every panorama for six releases, told you about it, and deliberately gave you no
way to fix it — and here is why that was right" is an argument, and the feature falls out of the end
of it.

The spine:

1. A 360° panorama wraps. If its left edge does not continue into its right edge there is a visible
   join — and the failure is not that it looks broken. It looks *fine*, flat, on a screen. Somebody
   discovers it three days later wearing a headset.
2. Since 3.17 we measured that and reported it. Over a threshold, a warning on a `200`, because a
   visible seam is the operator's own aesthetic judgement and failing a two-minute render over a
   metric is the tool overriding the person.
3. **And then nothing happened.** We handed somebody a number and no way to act on it.
4. **The refusal was about consent.** *A roll-and-inpaint fix is a second generation pass the caller
   did not ask for, did not watch, and would be billed for.* Read it again: every clause is about
   who decided, not about repair being wrong.
5. So 3.23 adds the asking, and keeps everything else: two gates, off by default, and **no threshold
   ever triggers a repair** — the same sentence, still true.
6. **The cheap mechanism is the default one, and that is the design.** `blend` is numpy on an array
   that already exists: no steps, no VRAM, nothing on the bill. Shipping only the expensive
   mechanism would have made the answer to "my panorama has a line in it" cost a second render every
   time — which is exactly the bill the original refusal declined to hand anybody.
7. **What `blend` cannot do, said out loud**: it closes a *tonal* discontinuity, not a *structural*
   one. A seam through a doorway comes back with no visible step in brightness and the doorway still
   not lining up.
8. **A repair that does not help is discarded and reported** — two equal numbers and the mechanism.
   A pass that quietly made a picture worse is the one outcome nobody would ever look for.
9. **The honest ending**: the inpainting path was established by reading the pinned library rather
   than by rendering anything, and no repaired panorama has been looked at on a card. A synthetic
   seam is a unit test, not evidence.

## The numbers that may be used

| | |
|---|---|
| `blend` cost | 0 steps, 0 MiB VRAM, ~milliseconds |
| `diffuse` cost | `int(steps × 0.4)` extra steps, in `megapixel_steps` |
| default ceiling | `off` |
| tests | 1 255 passed, 48 skipped |

## What must NOT go in

- **No before/after seam numbers from a real render.** There are none. The only numbers measured
  came from synthetic rasters in the test suite.
- No claim that a panorama was looked at, or that the repair "works well" — nobody has seen one.
- No `curl` block (WAF).
- Nothing about phases 56–60 as shipped.
