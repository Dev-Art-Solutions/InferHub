# Social — v3.18.0

Post manually. Lead with the **mask**, not with "we added editing". Everybody who has ever wired up
an inpainting API has had this bug, and the reason it is memorable is that it does not error — the
picture comes back with a 200 on it and everything except your selection has been repainted.

**The demo to attach is a before/after pair**, and the mask should be visible somewhere in it: a
photo, the mask, and the result, so the caption's claim is checkable at a glance.

## Facebook

InferHub 3.18: image editing — and the mask convention everybody gets backwards.

Inpainting, image-to-image and variations, on your own card, through OpenAI's edits API. An app that already edits images moves over with a base-URL change.

Here is the part worth writing down.

OpenAI's edits API treats a fully TRANSPARENT pixel as the area to edit. The diffusers inpainting pipelines take a mask where WHITE is the area to inpaint. Those are opposite.

Get it backwards and nothing errors. You get a perfectly valid image, with a 200 on it, in which everything EXCEPT the region you selected has been repainted. It reads as a broken model rather than a backwards mask, and you find out by looking at the picture — usually after blaming the model, then the prompt, then the strength.

So there are two refusals rather than a guess:

A mask with no alpha channel is a 400. Under OpenAI's convention a fully opaque image selects nothing, which nobody has ever intended — and both ways of being "helpful" are worse than refusing. Reading it as "edit everything" is a silent substitution of the most destructive possible interpretation. Reading it as "edit nothing" hands you your own picture back and charges you for it.

A mask whose size differs from the image is a 400 naming both, not a rescale. A mask names WHICH PIXELS, and scaling somebody's selection puts the edit next to what they chose.

And the conversion happens in the worker, not in the shared library where the plan put it — because inverting a mask means reading an alpha channel, which is decoding a pixel, and nothing in InferHub's C# ever decodes a pixel. There is no image library on the hub, by design. The cost is one round trip to find out a mask is wrong. We have taken that trade twice before.

Two more things this release gets right and most don't:

Strength is a header, and you are billed for the steps that ACTUALLY RAN. diffusers enters the schedule at int(steps × strength), so 30 steps at 0.6 denoises for 18 — and 18 is what lands in the usage ledger. Billing the asked-for 30 would charge for work nobody did.

Not every model can edit, and it says so before you wait. Editing is its own capability: of the seven models that ship, two edit — FLUX.1-schnell has no official inpainting pipeline and SDXL does. An edit against a generate-only model is a 503 that names the ones that can, and the same model still generates perfectly well.

It is also the first thing in this project's history that sends megabytes DOWN the mesh rather than up it — so the test asserts the node is still registered afterwards, not merely that a response came back. We learned that one the expensive way in 3.10.

Zero new dependencies, still. Fifth release of the image track.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.18.0

**Counts below are measured, not estimated**, against 280 with the link at t.co's flat 23
characters. The first draft of this file guessed them and three of the four were over — the single
post by 4, 2/4 by 12, 3/4 by 10.

## X / Twitter — single post (276/280; the link counts as 23 under t.co)

InferHub 3.18: image editing on your own card.

OpenAI's mask says TRANSPARENT = edit here. diffusers says WHITE = inpaint here. Opposite.

Get it backwards and nothing errors: you get a 200, everything except your selection repainted.

No alpha = 400.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.18.0

## X / Twitter — thread (each tweet under 280; link only on 4/4)

**1/4** (255)

InferHub 3.18 ships image editing — inpainting, img2img, variations — on OpenAI's edits API, against your own card.

The interesting part isn't the feature. It's the mask.

OpenAI: TRANSPARENT pixels are the area to edit.
diffusers: WHITE pixels are the area to inpaint.

**2/4** (269)

Those are opposite, and getting it backwards DOES NOT ERROR.

You get a valid image, 200, everything except your selection repainted. It reads as a broken model.

So a mask with no alpha is a 400: "edit everything" is the most destructive reading of an empty selection.

**3/4** (247)

The conversion lives in the worker, not the shared library, because inverting a mask means reading an alpha channel — and nothing in our C# decodes a pixel. No image library on the hub, by design.

Cost: one round trip to learn your mask is wrong.

**4/4** (270 incl. the link)

Also: you're billed for the steps that RAN. diffusers enters the schedule at int(steps × strength), so 30 at 0.6 is 18 — and 18 is what the ledger gets.

And FLUX can't inpaint, so it says so up front instead of after 40s.

Zero new deps.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.18.0
