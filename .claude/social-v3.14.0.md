# Social — v3.14.0 / v3.14.1

Post manually. Lead with **what did not have to change**, not with "we added image generation".
Everybody adds image generation. Almost nobody gets to say a whole new modality went in without
touching the router, and that is the half other people building a mesh are deciding right now.

The demo is the picture. Attach the SDXL lighthouse (1024×1024, seed 42, 8.8 s) to both — a post
about image generation with no image in it is a post nobody stops scrolling for.

## Facebook

InferHub 3.14: Stable Diffusion, answered by your own card.

Point an app that already calls OpenAI's Images API at your own hardware, change the base URL, and SDXL answers from a GPU you own. That is the feature. Here is the part I actually want to write about.

Six releases ago, in 3.8, I changed how the mesh routes: from "which node holds this model name" to "which node can DO this, with this model". At the time it was an unglamorous release — nothing new became possible, misrouting just stopped.

Here is everything 3.14 needed from the mesh to add text-to-image:

One more capability kind. The string "image".

That is the list. The request travels as the same tool job a transcription does. The dispatcher didn't change. The router didn't change. The affinity, the queue, the saturation logic, the failover, the node protocol — none of them learned that images exist. I have been writing these for long enough to have opinions about which arguments were worth having, and this is the clearest evidence I've got that that one was.

Two models ship: SDXL and SD 1.5, both fp16, no quantization, so SDXL fits an 8 GB card. On a 3090 Ti, SDXL at 1024×1024 and 30 steps is 8.8 seconds warm. FLUX and Qwen-Image are next — 12B and 20B, neither fits 24 GB at bf16, and leading with one would have meant shipping the client dialect, a VRAM budget and a quantization path together, where the first bug has three plausible causes.

A PROMPT IS CONTENT. A transcript is content because it's what somebody said; a prompt is content because it's what somebody WANTED, and the picture is the answer. So nothing logs one, at any level — a test hunts the whole trace-level log and the usage ledger for it. There is no URL in the response and there won't be: serving a URL means the hub keeps the bytes, and keeping the bytes means an image store with a retention question I haven't agreed to answer. The negative prompt travels in the body rather than a header, because a header is the one part of a request every proxy in the path writes down by default.

And there is NO bundled safety classifier. The one the library ships returns a black image on a positive — indistinguishable from a broken VAE, a bad seed or an OOM, so the operator gets a bug report instead of a policy signal. This box generates what you ask it to. The policy is yours.

Then the part where I shipped it broken.

3.14.0 was dead on arrival for SDXL on a fresh volume. The first request for a model whose weights weren't on the box downloaded them INSIDE the request — 900-second budget, 502 at 899.99 seconds, twice. What stings is that I'd already written the rule down: a roadmap I drafted the same week says "weights are pulled by an explicit command, never lazily inside a request". I wrote that thinking about a 24 GB model and then shipped the lazy path on a 7 GB one that was plenty big enough to hit it.

The download was also twice the size it needed to be, and this one is worth knowing if you touch diffusers at all: dtype is what weights are cast to IN MEMORY. variant is which FILES get downloaded. The repos carry both — 10.3 GB and 5.1 GB for SDXL's UNet — and passing only torch_dtype=float16 fetches the big one and casts it down. Same result, twice the bytes.

Neither was visible to the suite, which runs a real child process over a real wire and asserts on every status code and every byte of the envelope — and downloads no models, because a unit suite needing 9 GB of weights simply would not run.

3.14.1: a model is DECLARED only once its weights are proven loadable, a background thread fetches, and the worker re-declares as each one lands. No request ever waits on a download. Node up in 2 seconds offering nothing, SD 1.5 a minute later, SDXL at three and a half.

One more thing fell out of that. Letting a worker say "and now this one too" needed a late declaration to reach the node, and an idle worker has nobody reading its output. The thing that should drain it already existed: in 3.9 I specified a ping/pong liveness probe, wrote the method, documented it — and never called it from anywhere. Dead code for five releases. A specified feature is not a working feature, and the bug that survives a green suite isn't the subtle one. It's the one nothing exercises at all.

Zero new dependencies — nothing in C# ever decodes a pixel.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.14.1

## X / Twitter — single post (263; the link counts as 23 under t.co)

InferHub 3.14: Stable Diffusion on your own card, through OpenAI's Images API.

Everything the mesh needed to gain a whole new modality:

one more capability string.

Router, dispatcher, affinity, queue, node protocol — untouched. That's a 2023 decision paying off.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.14.1

*Alternative, leads with the bug (271):*

Shipped image generation. It was dead on arrival.

The first request for an uncached model downloaded 7 GB *inside* the request. 900s budget → 502 at 899.99s.

I'd written the rule against it the same week, for a bigger model.

Found by pulling the image.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.14.1

## X / Twitter — thread (each under 280; link only on 5/5)

**1/5** (241)

InferHub 3.14: text to image, on OpenAI's Images API, answered by Stable Diffusion on a card you own.

The feature is fine. The interesting part is what the mesh needed to gain a whole new modality:

one more capability string. "image".

**2/5** (267)

That's it. The request travels as the same tool job a transcription does.

Router: unchanged. Dispatcher: unchanged. Affinity, queue, saturation, failover, node protocol: unchanged.

Six releases ago I made routing (capability, model) instead of just model. Unglamorous then.

**3/5** (259)

A prompt is content.

A transcript is content because it's what somebody said. A prompt is what somebody WANTED, and the picture is the answer.

Nothing logs one. No URL in the response — that would mean the hub keeps your bytes. No bundled safety classifier: it returns a *black image*.

**4/5** (276)

Then I shipped it broken.

First request for an uncached model downloaded the weights *inside* the request. 900s budget, 502 at 899.99s, twice.

diffusers gotcha: `dtype` is what weights are cast to in memory. `variant` is which FILES download. Passing only dtype gets you the fp32 ones.

**5/5** (268 incl. link)

Fixed: a model is declared only once its weights are proven loadable. Background fetch, worker re-declares when each lands.

That needed a liveness probe I specified in 3.9, wrote, and never called from anywhere. Dead for 5 releases.

Zero new deps.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.14.1

## LinkedIn / r/selfhosted angle, if you want a third

Lead with the operational number instead of the architecture: *one `docker run`, no coordinator, and
you have an OpenAI-compatible image endpoint on your own hardware in about a minute — SDXL at
1024×1024 in 8.8 s on a 3090 Ti, and the prompt never touches a log.* That framing is the one that
travels in self-hosting communities, where "no protocol change" means nothing and "my prompts do not
leave the building" means everything.
