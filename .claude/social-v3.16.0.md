# Social copy — InferHub v3.16.0

**Not posted.** There is no FB/X connector; Iliya posts these by hand.

> **The demo is a grid.** One prompt through all six recipes, laid out in a 3×2, with the wall time
> and the VRAM reading under each. That single image *is* the release: it shows the catalogue, it
> shows that a 20B model and a 1-step model live on the same card, and it makes the two numbers in
> the table concrete in a way no paragraph does.
>
> Second-best, and cheaper to produce: a screenshot of the startup log with the two refusal lines in
> it — the unaccepted licence and the recipe that does not fit the budget — because those are the two
> behaviours people will argue with, and both are one sentence each on screen.

---

## Facebook — main post

**InferHub 3.16 is out: six image models, and the VRAM arithmetic nobody writes down.**

3.14 put Stable Diffusion on a self-hosted fleet. 3.15 gave that work a clock. 3.16 turns two models
into a catalogue of six — and every one of the four new ones needed something the first release did
not have.

**Two of them do not fit a 24 GB card at all.** FLUX.1-schnell is 12B and wants about 33 GB at bf16.
Qwen-Image is a 20B transformer with an 8.3B text encoder beside it — about 60 GB. nf4 quantization
is the only reason they run on a consumer card at all: ~12 GB and ~19 GB. Both figures are in the
docs, because "Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are both true sentences about
different recipes, and a table that gives you one of them is lying to somebody.

Four decisions underneath that, each with an obvious alternative that would have been worse:

**1. The VRAM budget is declared, not detected.** The obvious design asks the card. It works on
bare-metal Linux and it is wrong under WSL2 — Docker Desktop on Windows, the most common
GPU-with-Docker setup there is — where there are no `/dev/nvidia*` device nodes at all, the host's
`nvidia-smi` cannot see the VM's VRAM, and the only reliable signal that a GPU exists is that the
driver library loads. It is also wrong on a shared card. So you set a number and we hold some back
for your chat model and your display. The sentence worth carrying out of this release: **a budget
that is usually right is worse than one that is explicitly absent.** A detected budget that is wrong
fails as an out-of-memory error inside somebody's job at 2am. A declared one that is wrong fails as a
startup message read by the person who typed it. (The worker still reports what it measures, and the
node logs the two side by side when they disagree. It never overrides you.)

A model that cannot fit is *not declared* — the fleet never routes at it, so nobody spends a request
finding out. One that would fit but does not right now waits, and then gets a 503 with `Retry-After`.
What it never produces is an OOM inside a job. That is the failure this whole thing exists to
replace.

**2. Quantization belongs to the model, not to the request.** The tempting alternative is a header.
It is wrong, and the reason is reproducibility: quantization changes what the model *is*, so two
requests to `qwen-image` that quantized differently produce different images from the same seed. A
per-request knob would make reproducibility a function of a header nobody logged, and "I got this
picture yesterday and cannot get it again" is a bug report with no evidence in it. Want both? Ship
two recipes with two ids.

**3. Two of the six will not start until you accept their licence.** SD 3.5 Medium is under the
Stability AI Community licence; SDXL-Turbo is non-commercial outright. Both are loaded, logged by
name, and left unstarted until their licence id is in your config. It is the fourth opt-in in the
tool runtime and it is not redundant with the other three — none of them says "and I accept the
Stability AI Non-Commercial Research Community License". It is a **list** rather than a boolean,
because one of those two is free for most people who will ever run it and the other is not usable
commercially at all: a single flag would let somebody who read one licence enable both. None of it is
legal advice. It is a refusal to make that call for you, *silently*.

**4. Weights arrive because you asked for them.** FLUX is ~24 GB on the wire. A lazy first-use
download blows the request timeout — and that is not hypothetical, because 3.14.0 shipped exactly
that: on a fresh volume the first SDXL call spent the whole 900-second budget downloading and
returned a 502. Twice. Found by pulling the published image and running it. So a pull is now an
explicit admin command on the model-command channel the fleet has had since 2.8, with progress on the
existing SSE stream. No new transport, and the console gets a progress bar for free.

Also: switching models swaps weights inside the warm process rather than restarting it, so you pay
the load and not the Python interpreter and the import of torch on every alternation — and after an
idle period the node hints the worker to free its VRAM and stay alive.

Zero new NuGet packages, as ever. Nothing in C# decodes a pixel or quantizes a tensor.

Docs: https://inferhub.devart.solutions
Code: https://github.com/Dev-Art-Solutions/InferHub

---

## Facebook — short variant

InferHub 3.16: six image models on one card.

FLUX.1-schnell wants ~33 GB at bf16. Qwen-Image — a 20B transformer plus an 8.3B text encoder —
wants ~60 GB. Neither fits a 24 GB card. nf4 gets them to ~12 GB and ~19 GB, and both numbers are in
the docs because a table that gives you one of them is lying to somebody.

The VRAM budget is a number **you** set, not one we detect. Detecting it works on bare-metal Linux
and is wrong under WSL2, where there are no `/dev/nvidia*` nodes to look at. A budget that is usually
right is worse than one that is explicitly absent: the first failure is an OOM at 2am rather than a
startup message.

Two of the six won't start until you accept their licence. That is a list, not a boolean.

https://inferhub.devart.solutions

---

## X / Twitter — thread

**1/**
InferHub 3.16 is out: two image models became six.

FLUX.1-schnell (12B) wants ~33 GB at bf16. Qwen-Image (20B + an 8.3B text encoder) wants ~60 GB.

Neither fits a 24 GB card. nf4 gets them to ~12 GB and ~19 GB. That's the release.

**2/**
Both numbers are in the docs.

"Qwen-Image needs 19 GB" and "Qwen-Image needs 60 GB" are both true sentences about different
recipes. A table that gives you one of them is lying to somebody.

**3/**
The VRAM budget is DECLARED, not detected.

The obvious design asks the card. It works on bare-metal Linux.

It's wrong under WSL2 — Docker Desktop on Windows — where there are no `/dev/nvidia*` device nodes at
all and the host's nvidia-smi can't see the VM's VRAM.

**4/**
So you set a number.

A budget that is usually right is worse than one that is explicitly absent: the first failure is an
out-of-memory error inside somebody's job at 2am, instead of a startup message read by the person who
typed it.

**5/**
A model that can't fit is NOT DECLARED.

The fleet never routes at it, so nobody spends a request finding out. One that would fit but doesn't
right now waits, then gets a 503 + Retry-After — the same shape as every other limit.

Never an OOM inside a job.

**6/**
The worker still reports what `torch.cuda.mem_get_info()` says, and the node logs the two side by
side when they disagree.

It never adopts the measurement. That would be detecting VRAM after all, with an extra step.

**7/**
Quantization is a property of the RECIPE, never of the request.

Two calls to qwen-image that quantized differently produce different images from the same seed.

A per-request knob makes reproducibility a function of a header nobody logged.

**8/**
One quantization mechanism, deliberately. Not GGUF + Nunchaku + TensorRT.

Each is faster on some model on some card. Supporting three means that when a picture comes out worse
than expected, there are three plausible causes instead of one.

**9/**
Two of the six won't start until you accept their licence.

SD 3.5 Medium and SDXL-Turbo: loaded, logged by name, left unstarted until their licence id is in
your config. The log line links to the text.

**10/**
It's a LIST, not a boolean.

SD 3.5 Medium is free for most people who'll run it. SDXL-Turbo is not usable commercially at all.

One `AcceptNonPermissive=true` would let somebody who read one licence enable both.

Not legal advice — a refusal to make that call for you silently.

**11/**
Weights are pulled deliberately now.

FLUX is ~24 GB on the wire. A lazy first-use download blows the request timeout — 3.14.0 shipped
exactly that: first SDXL call, 900s budget spent downloading, 502. Twice.

Found by pulling the published image.

**12/**
The pull reports MiB landed, not a percentage.

huggingface_hub gives no download callback, so a percentage would come from a denominator we guessed.

A number nobody measured is a number a dashboard will happily plot.

**13/**
Switching models swaps weights inside the WARM process.

A restart per recipe would pay the Python interpreter and the import of torch on top of the weights,
on every alternation. Two clients alternating would spend more time loading than generating.

**14/**
Zero new NuGet packages. Nothing in C# decodes a pixel or quantizes a tensor.

bitsandbytes arrived WITH its first consumer — which is exactly why 3.14 refused to carry it. A
pinned dependency nothing imports is a pin nobody can tell is wrong.

https://inferhub.devart.solutions

---

## X / Twitter — single post

InferHub 3.16: six image models, incl. FLUX.1-schnell and Qwen-Image — neither of which fits a 24 GB
card at bf16 (~33 GB and ~60 GB). nf4 gets them to ~12 and ~19.

The VRAM budget is a number you SET, not one we detect: under WSL2 there are no /dev/nvidia* nodes to
look at, and a budget that's usually right is worse than one that's absent.

https://inferhub.devart.solutions

---

## LinkedIn variant

We shipped InferHub 3.16 today. It adds four image models to a self-hosted inference mesh, and the
part worth writing about is not the models — it is that two of them do not fit the hardware most
people have, and what we decided to do about that.

FLUX.1-schnell is 12B and wants roughly 33 GB at bf16. Qwen-Image is a 20B transformer with an 8.3B
text encoder beside it — about 60 GB. Neither fits a 24 GB consumer card. nf4 quantization brings
them to about 12 GB and 19 GB, and we publish both figures for every model, because "Qwen-Image needs
19 GB" and "Qwen-Image needs 60 GB" are both true sentences about different configurations and a
table that gives you one of them is lying to somebody.

The decision I would defend hardest is that the VRAM budget is **declared rather than detected**. The
obvious design is to ask the card how much memory it has. It works on bare-metal Linux, and it is
wrong under WSL2 — Docker Desktop on Windows, which is the most common way people run a GPU in a
container — because there are no device nodes to enumerate, the host's tooling cannot see the VM's
memory, and the only reliable signal that a GPU exists at all is whether the driver library loads. It
is also wrong on a shared card, and wrong the moment another process is already holding half of it.

So the operator sets a number. The reasoning generalises well past this feature: **a budget that is
usually right is worse than one that is explicitly absent.** A detected budget that is wrong fails as
an out-of-memory error inside somebody's job at two in the morning. A declared budget that is wrong
fails as a startup message read by the person who can fix it.

Two of the six models also refuse to start until their licence is explicitly accepted in
configuration. That is a list rather than a flag, because one of them is free for most people who
will run it and the other is not usable commercially at all — a single "accept non-permissive
licences" boolean would let somebody who read one licence enable both. It is not legal advice and it
does not claim to have read anything on anyone's behalf. It is a refusal to make that decision for
our users silently.

Zero new dependencies, as with every release in this project.

https://inferhub.devart.solutions
