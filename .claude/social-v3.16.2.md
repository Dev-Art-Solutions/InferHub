# Social copy — InferHub v3.16 (post as **3.16.2**)

**Not posted.** No FB/X connector; Iliya posts these by hand. Supersedes `social-v3.16.0.md`, which
was written before the verification run and says FLUX runs out of the box. It does not — gated repo.

> **The image to attach.** The six-recipe grid named in the phase brief is **not available**:
> `flux-schnell` and `sd35-medium` are gated behind a Hugging Face token, and `qwen-image` is a
> ~40 GB download, so none of the three has been generated. What exists and is real:
> `.claude/social-v3.14.0-sdxl-lighthouse.png` (SDXL 1024², seed 42, 8 s warm on a 3090 Ti), or a
> screenshot of the startup log showing the licence refusal and the VRAM refusal side by side —
> two sentences that make the whole release concrete.
>
> **Do not quote the nf4 VRAM figures as measured.** ~12 GB for FLUX and ~19 GB for Qwen-Image are
> *declared* recipe values. Everything in the "measured" block below was observed on the published
> image on an RTX 3090 Ti.

---

## Facebook

**InferHub 3.16: six image models, and the VRAM arithmetic nobody writes down.**

3.14 put Stable Diffusion on a self-hosted fleet. 3.15 gave that work a clock. 3.16 turns two models
into a catalogue of six — and the two most interesting ones do not fit the hardware most people have.

FLUX.1-schnell is 12B and wants about 33 GB at bf16. Qwen-Image is a 20B transformer with an 8.3B
text encoder beside it — about 60 GB. Neither fits a 24 GB consumer card. nf4 quantization brings
them to roughly 12 GB and 19 GB, and both figures are in the docs for every model, because
"Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are both true sentences about different
configurations and a table that gives you one of them is lying to somebody.

Three decisions underneath that, each with an obvious alternative that would have been worse.

**The VRAM budget is declared, not detected.** The obvious design asks the card how much memory it
has. It works beautifully on bare-metal Linux. It is wrong under WSL2 — Docker Desktop on Windows,
the most common way anyone runs a GPU in a container — where there are no /dev/nvidia* device nodes
at all, the host's nvidia-smi cannot see the VM's VRAM, and the only reliable signal a GPU exists is
that the driver library loads. It is also wrong on a shared card. So you set a number, and we hold
some back for your chat model and your display.

The sentence worth carrying out of this release: **a budget that is usually right is worse than one
that is explicitly absent.** A detected budget that is wrong fails as an out-of-memory error inside
somebody's job at two in the morning. A declared one that is wrong fails as a startup message read
by the person who typed it. A model that cannot fit is not even advertised, so the fleet never routes
at it; one that would fit but does not right now waits and then gets a 503. What it never produces is
an OOM inside a job.

**Quantization belongs to the model, not to the request.** The tempting alternative is a header — let
the caller choose. It is wrong, and the reason is reproducibility: quantization changes what the
model *is*, so two requests that quantized differently produce different images from the same seed.
A per-request knob would make reproducibility a function of a header nobody logged.

**Two of the six will not start until you accept their licence.** SD 3.5 Medium and SDXL-Turbo are
loaded, logged by name, and left unstarted until their licence id is in your config. It is a list
rather than a boolean, because one of them is free for most people who will ever run it and the other
is not usable commercially at all — a single flag would let somebody who read one licence enable
both. None of it is legal advice. It is a refusal to make that call for you, silently.

Measured on an RTX 3090 Ti, against the published image: SDXL at 1024×1024 and 30 steps is 20 s cold
and 8 s warm. Switching models swaps weights inside the warm process — about 2 s, against ~22 s to
restart the worker. Cancelling a job keeps the weights, so the next one starts instantly.

Zero new NuGet packages, as ever. Nothing in C# decodes a pixel or quantizes a tensor.

**Use 3.16.2, not 3.16.0** — and that story is in the comments.

Docs: https://inferhub.devart.solutions
Code: https://github.com/Dev-Art-Solutions/InferHub

### Facebook — the follow-up comment (or a second post)

The reason to use 3.16.2 is that 3.16.0 answered roughly every *second* image request and hung the
rest, and I only found it by pulling the published image and running it on a real card.

An earlier release taught workers to run a request on a background thread so a cancel could arrive
mid-flight. The C# test worker kept one read loop. The Python reference library grew a *second*
reader that honoured cancel and ping and discarded everything else — and what it discarded was the
next request. Five sequential SDXL calls: one answered.

It only reproduces on a worker slow enough for that second reader to be blocked when the result goes
out. An echo answers in microseconds and finishes first, so nothing is lost. SDXL takes nine seconds.
That is why the audio workers were fine and why all 1141 tests passed.

The part that stings: the regression test I wrote for it **passed against the broken library**,
because I wrote it with instant requests. It now sends 600 ms ones and asserts the progress frames
that prove they take time.

Two smaller things from the same session: FLUX.1-schnell turns out to be a gated repository — it is
Apache-2.0, so our own licence gate correctly lets it through, and Hugging Face still wants a token —
and the background prefetch walked models alphabetically, so a 40 GB download queued ahead of two
already-cached ones and the node declared nothing while it ran.

Eighth time in this project that pulling the published artefact has caught something a green test
suite could not.

---

## X / Twitter — thread

**1/**
InferHub 3.16 is out: two image models became six.

FLUX.1-schnell (12B) wants ~33 GB at bf16. Qwen-Image — a 20B transformer plus an 8.3B text encoder —
wants ~60 GB.

Neither fits a 24 GB card. nf4 gets them to ~12 and ~19.

**2/**
Both numbers are in the docs for every model.

"Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are both true sentences about different
configurations. A table that gives you one of them is lying to somebody.

**3/**
The VRAM budget is DECLARED, not detected.

Asking the card works on bare-metal Linux. Under WSL2 — Docker Desktop on Windows — there are no
/dev/nvidia* device nodes at all, and the host's nvidia-smi can't see the VM's VRAM.

**4/**
So you set a number.

A budget that's usually right is worse than one that's explicitly absent: the first failure is an OOM
inside somebody's job at 2am, instead of a startup message read by the person who typed it.

**5/**
A model that can't fit isn't advertised at all — the fleet never routes at it, so nobody spends a
request finding out.

One that would fit but doesn't right now waits, then 503 + Retry-After.

Never an OOM inside a job.

**6/**
Quantization is a property of the recipe, never of the request.

Two calls to qwen-image that quantized differently produce different images from the same seed. A
per-request knob makes reproducibility depend on a header nobody logged.

**7/**
Two of the six won't start until you accept their licence.

A list, not a boolean: SD 3.5 Medium is free for most people who'll run it, SDXL-Turbo isn't usable
commercially at all. One flag would let somebody who read one licence enable both.

**8/**
Measured on a 3090 Ti, published image:

SDXL 1024², 30 steps — 20s cold, 8s warm.
Switching models reloads inside the warm process: ~2s, vs ~22s to restart the worker.
Cancel keeps the weights — the next job starts at load 0.0s.

**9/**
Now the part I'd rather not write. 3.16.0 dropped every other image request.

An earlier release gave the Python worker a second stdin reader so cancels could arrive mid-job. It
discarded everything that wasn't cancel or ping — including the next request.

**10/**
Five sequential SDXL calls: one answered.

It only reproduces on a slow worker — an instant handler finishes before that second reader blocks.
So the audio workers were fine, and all 1141 tests passed.

Found by pulling the published image and running it.

**11/**
Worse: the regression test I wrote for it PASSED against the broken library, because I wrote it with
instant requests.

It now sends 600ms ones and asserts the progress frames that prove they take time.

Use 3.16.2.

https://inferhub.devart.solutions

---

## X / Twitter — single post

InferHub 3.16: six image models on one card. FLUX.1-schnell and Qwen-Image don't fit 24 GB at bf16
(~33 and ~60 GB); nf4 gets them to ~12 and ~19.

The VRAM budget is a number you SET, not one we detect — under WSL2 there are no /dev/nvidia* nodes
to look at, and a budget that's usually right is worse than one that's absent.

https://inferhub.devart.solutions

## X / Twitter — the bug, standalone

Shipped a release where image generation answered every *other* request.

A worker had two readers on one stdin; the second ate the next request frame.

Only reproduces on a slow worker, so 1141 tests passed.

And my regression test passed against the broken code until I made the requests slow.
