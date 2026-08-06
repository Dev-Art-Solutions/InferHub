# Social copy — InferHub v3.15.0

**Not posted.** There is no FB/X connector; Iliya posts these by hand.

> **The demo is the SSE stream.** A short screen recording of a progress bar filling in a terminal
> while `nvidia-smi` runs beside it is worth more than any of the copy below — it shows the one thing
> the release is about (you can *see* the job) and the one thing a screenshot cannot (it moves).
> Second-best: the same recording with a `DELETE` halfway through, then a second job starting
> instantly — that visibly demonstrates "the worker kept its weights", which is the decision people
> will argue with.

---

## Facebook — main post

**InferHub 3.15 is out: what a two-minute job should look like.**

3.14 put Stable Diffusion on your own fleet behind OpenAI's Images API. It worked — but there was no
way to *watch* one. You sent a request, held a connection open, and the only two things you could
learn were "it worked" and "something timed out", usually from a proxy in the path with a status
nobody could act on.

An SDXL render at 50 steps is not a request. It is a job.

Now you submit and get an id and a place in line. You watch `queued → running(step 7/28) →
succeeded` stream over SSE. You collect the image once. Or you change your mind, and *that* is the
part I'd defend hardest:

**Cancel does not kill the worker.** A cancel frame goes down, the worker honours it from its
per-step callback, answers "cancelled" — and is still alive, still holding its weights. Killing the
process would have been three lines. It is also wrong: those weights took tens of seconds to load,
and twelve to twenty gigabytes taking a minute once the model catalogue grows. Killing it to abandon
*one* job punishes the *next* caller, who did nothing, and it gets strictly worse with every model
added. A worker that ignores the ask for 20 seconds is by definition not cooperating, and that one
gets terminated.

Cancellation is best-effort and the API says so out loud. A job cancelled at step 27 of 28 may finish
anyway, and if it does — you get your image. Discarding a real result to honour a state name would be
worse than telling you what actually happened. The test suite asserts that as *legal*, not as a flake
to retry away.

Three more decisions worth reading:

**There is no `background: true` flag.** OpenAI has no async Images API to adopt, and where there is
no dialect to adopt we do not invent an OpenAI-shaped one. A flag that makes one route return two
incompatible shapes is something every typed SDK gets wrong. `/v1/images/generations` stays exactly
OpenAI-compatible — internally it just became "submit a job and wait for it", so both surfaces queue
in the same line and are metered by the same code rather than the fleet growing a fast lane nobody
documented.

**Results live in memory for five minutes and nowhere else.** Read once, LRU-evicted against a byte
ceiling enforced on insert rather than on a timer, and nothing touches disk — no temp file, no spill
under pressure, no cache directory. A restart forgets every job. That is not a limitation waiting to
be fixed; it is what keeps "no persisted state" true, and it means "where are my pictures kept" has
the answer "nowhere, for five minutes" instead of a data-retention conversation.

**A node that vanishes mid-job is never silently retried.** A job that died at step 22 produced no
output, so it is technically retryable — and retrying it would silently double the GPU-minutes and
the billed units for one request. It fails with a reason and you decide.

Zero new dependencies. No new model. Nothing to do to upgrade.

Docs: https://inferhub.devart.solutions
Code: https://github.com/Dev-Art-Solutions/InferHub

---

## Facebook — short variant

InferHub 3.15: image generation gets a clock.

Submit → get an id and a place in line. Watch `running(step 7/28)` stream over SSE. Collect the
image once. Or cancel — and the worker keeps its weights, so the *next* caller doesn't pay for your
change of mind.

Results live in memory for five minutes and nowhere else. Nothing touches disk. A restart forgets
everything, on purpose.

`/v1/images/generations` is unchanged for anyone who never reads this.

https://inferhub.devart.solutions

---

## X / Twitter — thread

**1/**
InferHub 3.15 is out.

3.14 put Stable Diffusion on your own fleet. 3.15 gives it a clock: an SDXL render at 50 steps is not
a request, it's a job.

Submit → id + place in line. Watch `queued → running(step 7/28) → succeeded` over SSE. Collect once.
Or cancel.

**2/**
There is no `background: true` flag.

OpenAI has no async Images API to adopt, and where there's no dialect to adopt we don't invent an
OpenAI-shaped one. One route returning two incompatible shapes depending on a flag is something every
typed SDK gets wrong.

**3/**
`/v1/images/generations` is unchanged.

Internally it became "submit a job and wait for it" — so a sync call and an async one queue in the
same line and are metered by the same code.

Two paths to a GPU with two ideas of fairness is how you grow a fast lane nobody documented.

**4/**
Cancel does not kill the worker.

The worker honours it from its per-step callback, answers `cancelled`, and stays alive holding its
weights.

Killing it would be 3 lines. It's also wrong: it punishes the NEXT caller with a fresh multi-GB
weight load, and gets worse with every model you add.

**5/**
Cancellation is best-effort and the API says so.

Cancelled at step 27 of 28? It may still succeed — and if it does, you get your image.

Discarding a finished result to honour a state name would be worse than telling you what happened.
The test asserts that as legal, not flaky.

**6/**
Results live in memory for 5 minutes and nowhere else.

Read-once. LRU against a byte ceiling enforced ON INSERT, not on a timer — a timer means the ceiling
is a suggestion for one sweep interval.

Nothing touches disk. Ever. No temp file, no spill, no cache dir.

**7/**
A restart forgets every job.

That's not a limitation waiting to be fixed. It's what keeps "no persisted state" true — and it's why
"where are my pictures kept" answers "nowhere, for five minutes" instead of starting a data-retention
conversation.

**8/**
The queue is FIFO and deliberately not clever.

Shortest-job-first would let a stream of 4-step requests starve a 50-step one indefinitely — and the
starvation would be invisible. No error, no timeout, just a job that never runs.

**9/**
A node that vanishes mid-job is never silently retried.

Died at step 22 → produced no output → technically retryable. We don't. That would double the
GPU-minutes and the billed units for one request.

The test kills a real node and asserts an EMPTY ledger.

**10/**
Zero new dependencies. No new model. Solo mode gets the same five routes the same day.

Nothing to do to upgrade.

https://inferhub.devart.solutions

---

## X / Twitter — single post

InferHub 3.15: image jobs with per-step progress over SSE, and a cancel that does NOT kill the
worker — it keeps its weights, so the next caller doesn't pay for your change of mind.

Results live in memory 5 min and nowhere else. Nothing touches disk.

https://inferhub.devart.solutions

---

## LinkedIn variant

We shipped InferHub 3.15 today. The feature is small to describe — asynchronous image jobs — and the
interesting part is the three things we refused to do.

We did not add a `background: true` flag to the existing OpenAI-compatible endpoint, because one
route returning two incompatible response shapes depending on a flag is something every typed SDK
gets wrong, and it turns "is this API compatible?" into a question with a footnote.

We did not kill the worker on cancel. Those weights took tens of seconds to load; killing the process
to abandon one job would punish the *next* caller for the first one's change of mind, and the cost
grows with every model in the catalogue. So cancellation is a cooperative frame with a 20-second
grace, and the axe only after that.

We did not retry a job whose node disappeared mid-run. It produced no output, so it is technically
retryable — and retrying it would silently double the GPU-minutes and the billed units for one
request. The job fails with a reason and the caller decides.

There is a fourth: results live in memory for five minutes and nothing ever touches disk. A restart
forgets every job. That is deliberate — it keeps "where are my pictures kept" a question with a short
answer rather than a data-retention policy somebody has to own.

https://inferhub.devart.solutions
