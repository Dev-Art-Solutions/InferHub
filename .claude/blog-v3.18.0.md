# Blog post — v3.18.0

- **slug**: `inferhub-3-18-editing-and-the-mask-everybody-gets-backwards`
- **title (EN)**: `InferHub 3.18: image editing, and the mask convention everybody gets backwards`
- **DB id**: `6a7a351668b9288a2511b972`
- **published**: 2026-08-10, **EN visible in one shot** (`isVisible_en: true`), BG hidden
- **Cloudflare WAF**: no shell commands anywhere in the body. There is not a single `curl`,
  `docker run` or `-F` in it — the multipart request is described as a list of parts and the
  responses are shown as JSON. The copy-pasteable commands live on the docs site.
- Content is stored **entity-escaped**; that is correct and is how every prior post is stored.
  (Confirmed against `blog-v3.15.0.md`, which records the same thing.)
- `list_posts` was run first: the slug was free, and **one** `create_post` succeeded.
- The images were **already published** when this went out — `3.18.0-diffusion` was on GHCR 8
  minutes after the tag — so unlike v3.15 this did not need to go out as a draft.

## Excerpt (EN)

Inpainting, image-to-image and variations on your own card, through OpenAI's edits API. The
interesting part is the mask: OpenAI says transparent means "edit this", diffusers says white means
"inpaint this", and those are opposite — so getting it wrong does not error, it edits everything
except what you selected.

## The angles, in the order the post takes them

1. **Three routes, three shapes**, and why a variation takes no prompt (an edit with no mask is
   already img2img with a prompt; a second way to say one thing is a second dialect).
2. **The mask convention** — the diagram, and the sentence that makes it worth a section: *getting
   it backwards does not error*. Two refusals rather than a guess: no alpha channel, and a size
   mismatch that is never rescaled because a mask names which pixels.
3. **Why the conversion is in the worker.** The plan put it in the shared library and the plan was
   wrong: inverting a mask means decoding a pixel, and nothing in InferHub's C# does that. One round
   trip, which is the same trade 3.14 and 3.17 already took.
4. **Strength, and being billed for the steps that ran.** `int(steps × strength)` — 30 at 0.6 is 18,
   and 18 is what the ledger gets.
5. **Not every model can edit**, the 503 that names the ones that can, and why it is a second
   capability kind rather than a nested operation list.
6. **An edit is a job like any other**, and why one route with two content types is not the
   `background: true` flag 3.15 refused.
7. **Megabytes down the mesh for the first time**, with the v3.10 story as the reason the assertion
   is "the node is still registered" rather than "we got a response".
8. **What is not in it**, named: ControlNet/IP-Adapter, outpainting, multi-image chains, FLUX
   inpainting.

## Links in the body

- `https://inferhub.devart.solutions/#idocs_images_edit` — new anchor, shipped in the same session.
- `https://github.com/Dev-Art-Solutions/InferHub`
