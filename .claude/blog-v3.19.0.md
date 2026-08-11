# Blog post — v3.19.0

- **slug**: `inferhub-3-19-a-fleet-that-makes-pictures`
- **title (EN)**: `InferHub 3.19: a fleet that makes pictures — chat, RAG, speech and images from one place`
- **DB id**: `6a7b24352ff5bf831dd874f2`
- **published**: 2026-08-11, **EN visible in one shot**, BG hidden
- **Cloudflare WAF**: no shell commands in the body. The recipe/reason table is shown as a plain
  `<pre>` block, not a curl.
- Content is stored **entity-escaped**, which is how every prior post is stored.
- `list_posts` was run first; the slug was free and **one** `create_post` succeeded.

## The angle

**Lead with the gap, not with the console.** "We shipped a panel" is not interesting; "a model you
configured is not being served and nothing in the fleet tells you" is a problem other people
building this have right now.

The post's spine is the four-causes-one-symptom paragraph: a recipe held back for its licence or its
VRAM is *not declared*, which is the right routing behaviour and the worst possible diagnostic,
because at the hub it is indistinguishable from a model nobody installed, one still downloading, one
too big for the card, and a typo. Then: the order of the checks is the order of the fixes.

Second half is the two refusals — no server-side gallery, and no zero-valued VRAM series for a node
that declared no budget — because both are decisions a reader can take away and apply somewhere else.

## What it links

- `https://inferhub.devart.solutions/#idocs_console_images` — the anchor shipped in the same session.
- `https://github.com/Dev-Art-Solutions/InferHub`

## Track summary posts it supersedes

This is the release people read to understand 3.14–3.19, so the top of the post is a six-bullet
table of the track rather than a "what's new". The five earlier posts stay where they are.
