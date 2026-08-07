# Social copy — InferHub v3.17.0

**Not posted.** There is no FB/X connector; Iliya posts these by hand.

> **This is the single most shareable release in the whole track, and it needs the picture.**
> A 2048×1024 `qwen-360` render — the monastery courtyard, or anything with a strong horizon and a
> visible sky — with the wall time and the seed in the caption.
>
> - **Facebook** accepts 360° photos natively. Upload it as one. Nothing else in this project has a
>   post format built for it.
> - **X** does not. Post the flat equirectangular frame — which is itself an arresting image most
>   people have never looked at closely — plus a link to the viewer on the docs page.
> - **Second-best if no render exists yet:** a screen capture of the viewer, mid-drag, over the
>   test pattern. It shows the wrap and the poles, and it is honest about being a pattern.
>
> **Do not post a panorama the fleet has not actually generated.** Every number in this copy that
> refers to output is deliberately absent until the published image has been run.

---

## Facebook — main post

**InferHub 3.17 is out: 360° panoramas, from a 20B model, on one card.**

This is the model the whole image track was aimed at. `qwen-360-diffusion` is a rank-128 LoRA — MIT
licensed — over Qwen-Image's 20B multimodal diffusion transformer, and it produces **equirectangular**
panoramas: 360° of longitude across the frame, 180° of latitude down it, left edge continuing into
the right. Two-megapixel output on a consumer card, because nf4 quantization makes a 20B transformer
and its 8.3B text encoder fit where they otherwise would not.

Four decisions in it, each with an obvious alternative that is worse.

**1. A 2:1 aspect is not a suggestion, and a wrong one is refused.**

360 degrees over 180 is exactly two to one. That is what "equirectangular" means.

Ask a flat model for a size outside its buckets and you get duplicated limbs and doubled horizons. It
looks broken, everyone knows immediately, and that failure is *honest*.

Ask an equirectangular model for a non-2:1 size and you get a picture that looks **completely fine**.
Flat, on a monitor, it is a perfectly plausible landscape. It is only wrong when it is wrapped onto a
sphere — and the person who finds that out is wearing a headset, three days later, wondering why the
world tilts.

So it is a `400` that explains itself rather than just listing alternatives. The extra sentence is
the point: a refusal that lists sizes teaches you which sizes exist; one that names the failure
teaches you why you would not have seen it until much later.

**2. The trigger phrase is appended, not demanded and not silently inserted.**

The model activates on "equirectangular" / "360 panorama". Three options, two wrong:

Rewrite your prompt silently — no. The most-repeated sentence in this codebase is that nothing is
silently substituted, and a prompt is your own words.

Refuse a prompt without the trigger — also no. That is pedantry about a model whose entire purpose is
one thing, and it makes the first request everybody sends an error for a reason they could not have
known.

So: appended when absent, and the response **says so** — a flag plus the exact phrase that was added.
Turn it off in the recipe and the flag is still reported, because a client that has to infer "nothing
happened to my prompt" from a missing key is a client guessing.

The nice second-order bit: the trigger is a *recipe constant*, the same string for every caller — a
fact about the model, not about a person. That is what makes it safe to log, and logging it matters,
because "why does this not look like a panorama" is almost always "the trigger did not apply", and a
diagnosis nobody can see is not a diagnosis. Your prompt is still written nowhere, at any log level.

**3. The seam is measured and never repaired.**

A 360° image wraps: its left and right columns are *adjacent* once it is on a sphere. Every
equirectangular result now carries the mean absolute difference between them, 0–1. Two numpy
operations on an array the VAE already produced — which is why it is unconditional rather than behind
a flag. A metric nobody switches on is a metric nobody has.

Over a threshold you get a **warning on a 200**, never a failure. A slightly visible seam is your own
problem and your own aesthetic judgement, and failing a two-minute job over a metric would be the
tool overruling a person about a picture only that person can see.

And it is not fixed for you. Upstream ships a roll-and-inpaint repair and it is a genuinely good tool
to reach for — deliberately. Running it unasked is a second generation pass with its own cost, its
own artifacts and its own line on your bill, for a decision you never made and never saw happen.

**4. Projection is metadata now, and "flat" is a real answer.**

A 2048×1024 panorama and a 2048×1024 landscape photograph are *the same bytes in the same shape*.
Nothing in the pixels tells them apart. Every viewer on the internet guesses from the aspect ratio,
which is wrong for every 2:1 photograph anybody ever took.

So the worker **declares** it, and the declaration reaches you everywhere the image does — in the
response body, on the job document, and as a header on the raw-bytes route, which is the one request
with no JSON to carry it. A flat model reports `"flat"` rather than staying quiet, because an omitted
projection is indistinguishable from a node too old to have an opinion.

**And a viewer ships with it** — in the admin console and on the docs page. ~330 lines of
hand-written WebGL. No npm, no bundler, no three.js from a CDN: a third-party script on an admin page
that holds cordon and model-pull rights is a worse trade than an afternoon of `gl.texImage2D`.

Zero new dependencies, as ever. The only thing in the whole system that ever decodes a pixel is your
browser.

Docs: https://inferhub.devart.solutions/#idocs_images_360
Code: https://github.com/Dev-Art-Solutions/InferHub

---

## Facebook — short variant

InferHub 3.17: 360° panoramas on your own card.

A rank-128 MIT LoRA over Qwen-Image's 20B transformer, producing equirectangular output at
2048×1024 — with three decisions worth stealing:

A 2:1 aspect is enforced, and the refusal says *why*. A wrong ratio here does not look broken. It
looks fine, and it wraps wrongly, and you find out in a headset three days later.

The trigger phrase is appended when your prompt lacks it, and the response tells you it happened.
Rewriting a prompt silently is the one thing we never do; refusing one for a missing magic word makes
everybody's first request an error.

The seam is measured and never repaired — a warning on a 200, because a visible seam is your own
aesthetic judgement, and a repair is a second generation pass you did not ask for.

https://inferhub.devart.solutions/#idocs_images_360

---

## X / Twitter — thread

**1/**
InferHub 3.17 is out: 360° equirectangular panoramas, from a 20B model, on one consumer card.

A rank-128 MIT-licensed LoRA over Qwen-Image, at 2048×1024.

The interesting part isn't the model. It's the four things around it.

**2/**
A 2:1 aspect is enforced, and the refusal says WHY.

360° of longitude over 180° of latitude is exactly two to one. That's what equirectangular means.

**3/**
Here's why that matters more than a normal size check.

Wrong size on a flat model → duplicated limbs, doubled horizons. Obviously broken. Honest failure.

Wrong ratio on a 360 model → a picture that looks COMPLETELY FINE and wraps wrongly.

**4/**
You don't find that out on a monitor.

You find it out in a headset, three days later, wondering why the world tilts.

So it's a 400 that explains itself instead of just listing sizes.

**5/**
The trigger phrase is APPENDED, not demanded, not silently inserted.

The model needs "equirectangular" / "360 panorama" in the prompt. Three options:

silently rewrite → no, a prompt is your words
refuse without it → no, everyone's first request is a 400
append + say so → shipped

**6/**
The response carries a flag AND the exact phrase that was added.

Turn the behaviour off and the flag is still there — a client that has to infer "nothing happened to
my prompt" from a missing key is a client guessing.

**7/**
Second-order detail I like:

the trigger is a RECIPE CONSTANT. Same string for every caller. A fact about the model, not about a
person.

That's what makes it safe to log. And it should be logged: "why isn't this a panorama" is almost
always "the trigger didn't apply".

Your prompt is still written nowhere.

**8/**
The seam is MEASURED and NEVER REPAIRED.

A 360 image wraps — its left and right columns are adjacent on a sphere. Every result carries the
mean absolute difference between them, 0–1.

Two numpy ops on an array the VAE already made. Which is why it's unconditional.

**9/**
A metric nobody switches on is a metric nobody has.

Over the threshold: a WARNING on a 200. Never a failure.

A slightly visible seam is your own aesthetic judgement. Failing a two-minute job over a metric would
be the tool overruling a person about a picture only they can see.

**10/**
And it isn't fixed for you.

Upstream ships a roll-and-inpaint repair. It's good. Reach for it deliberately.

Running it unasked = a second generation pass, with its own cost, its own artifacts and its own line
on your bill, for a decision you never made.

**11/**
Projection is metadata now.

A 2048×1024 panorama and a 2048×1024 landscape photo are THE SAME BYTES IN THE SAME SHAPE. Nothing in
the pixels distinguishes them.

Every viewer guesses from the aspect ratio. That guess is wrong for every 2:1 photo ever taken.

**12/**
So the worker declares it, and it survives to the response body, the job document, and an
X-InferHub-Image-Projection header on the raw-bytes route — the one request with no JSON to carry it.

A flat model reports "flat" rather than omitting the field.

**13/**
That last bit is a deliberate exception to a rule we otherwise keep hard (absence is a fact; an
unmeasured thing emits no series, not a zero).

Different here: this is a DECLARATION, not a measurement. An omitted one reads as "this node has
never heard of projections".

**14/**
A viewer ships with it. ~330 lines of hand-written WebGL, in the console and on the docs page.

No npm. No bundler. No three.js from a CDN — a third-party script on an admin page that holds cordon
and model-pull rights is a worse trade than an afternoon of gl.texImage2D.

**15/**
qwen-image and qwen-360 are TWO model ids over one base, not one model with a scale param.

The router keys on (capability, model). A client asking for qwen-image must never get a panorama.

And a loraScale header would make reproducibility a function of a header nobody logged.

**16/**
Two facts we READ instead of guessing, from upstream's own reference script:

— the loading path is the ordinary repo + an nf4 config, not a pre-merged checkpoint
— the LoRA's quantization variant need NOT match the base's

Our build plan guessed the second one. It was wrong.

**17/**
Bonus bug, found while consolidating the response envelope:

the hub emitted `"revised_prompt": null` and a standalone node OMITTED the key.

For three releases. With a parity suite running.

Which keys are *present* is part of the contract now, spelled out.

**18/**
1154 tests green — which says nothing about whether a panorama is a panorama.

Two releases in this track were dead on arrival with a thousand tests passing.

So no output numbers are claimed until the published image has been pulled and run on a real 3090 Ti.

https://inferhub.devart.solutions/#idocs_images_360

---

## X / Twitter — single post

InferHub 3.17: 360° equirectangular panoramas from a rank-128 MIT LoRA over a 20B model, on one card.

A 2:1 aspect is enforced and the refusal says why — a wrong ratio here doesn't look broken, it looks
fine and wraps wrongly, and you find out in a headset three days later.

The seam is measured and never repaired.

https://inferhub.devart.solutions/#idocs_images_360

---

## LinkedIn variant

We shipped InferHub 3.17 today: 360° equirectangular panoramas, generated on a single consumer GPU by
a rank-128 MIT-licensed LoRA over a 20B diffusion transformer. The model is somebody else's excellent
work. What I want to write about is the three product decisions around it, because each one had an
obvious alternative that would have been worse in a way nobody would have noticed for months.

**A 2:1 aspect ratio is enforced, and the refusal explains itself.** 360 degrees of longitude over
180 of latitude is exactly two to one — that is what "equirectangular" means. The reason this matters
more than an ordinary input check is the *shape of the failure*. Ask an ordinary image model for a
size it was not trained on and you get duplicated limbs and doubled horizons: obviously broken,
immediately, to everyone. Ask a 360 model for a non-2:1 size and you get a picture that looks
completely fine on a monitor and wraps wrongly on a sphere. The person who discovers that is wearing
a headset, three days later. So the refusal names the failure rather than just listing the valid
sizes: one teaches you what exists, the other teaches you what you would not have seen.

**The trigger phrase is appended when the prompt lacks it, and the response says so.** This model
activates on specific words. We could have silently rewritten the prompt — but a prompt is the user's
own words, and nothing in this system is silently substituted. We could have refused prompts without
the phrase — but that makes the first request everybody sends an error for a reason they could not
have known. So it is appended, and the response carries both a flag and the exact phrase that was
added, whether or not anything fired. Worth noting: because the phrase is a constant of the model
rather than anything the user wrote, it is safe to put in a log line — and it should be, because "why
does this not look like a panorama" is almost always "the trigger did not apply", and a diagnosis
nobody can see is not one.

**The seam is measured and never repaired.** A 360 image wraps, so its left and right edges are
adjacent, and a visible discontinuity there is a scar down one side of the world. We report the
number on every result. Over a threshold it becomes a warning — on a successful response, never a
failure — because a slightly visible seam is the operator's own aesthetic judgement, and failing a
two-minute job over a metric would be the tool overruling a person about a picture only that person
can see. We deliberately do not fix it: upstream ships an excellent repair pass, and running it
unasked would be a second generation pass with its own cost and its own artifacts, billed to somebody
for a decision they never made.

There is also a small thing I would rather write about than quietly fix. Consolidating the response
envelope into one place turned up a live inconsistency: the coordinator emitted a null field that a
standalone node omitted entirely. Three releases, with a test suite written specifically to catch
that class of difference. It harmed nobody. It is still exactly the kind of drift that erodes the
claim that two deployment shapes are indistinguishable, and which keys are *present* is now part of
the contract rather than a side effect of a serializer setting.

Zero new dependencies, as with every release in this project.

https://inferhub.devart.solutions/#idocs_images_360
