# v3.27.0 — the video track becomes visible

Phase 59 of the v3.22–v3.28 track. v3.25 shipped the video seam and v3.26 a catalogue of three — and
at the hub, none of it could be seen. A video recipe refused for its licence or for the card was
*absent* from what a node reports, which reads exactly like a model nobody installed; the console had
no way to submit or watch a clip; the job counters had been counting video since v3.25 with nothing
on the series to tell it from a picture; and the seconds every finished clip credited to a client
were checked by nothing.

No route changed its behaviour and no key changed its default. A deployment that changes no config
behaves exactly as v3.26 did.

## A video recipe now says why it is not offered

`NodeToolState.Images` carries video recipes, each row with its `media`. The four reasons phase 51
wrote for pictures were already the right four for clips — the licence, the card, a coordinator
profile, or weights that are not there yet — and each has the fix it had before.

**Considered and rejected: a second `videos` array.** It reads tidier and it is two mailboxes to keep
in step, two shapes to drift, and a second copy of a reason list that will be extended once and in one
place. What genuinely differs is *rendering* — v3.25's own comment is right that a clip in a panel
that draws pictures is wrong — so the split lives in the console, which is whose problem it is.

The most expensive refusal this project ships is now visible: `wan-t2v-14b-720p` wants 24 000 MiB
and a 24 GB card offers 22 528 after the default reserve, so a node with one never declares it. The
row says `over-budget` and names the arithmetic. That is the ceiling working, not a fault.

## A Video panel, speaking the dialect an SDK speaks

`/console` gained one: a prompt, a size, a duration, the row updating while it runs (queue position,
step *n* of *m*, elapsed, which node), cancel, and the clip playing in the page. Underneath it,
**Video recipes on the fleet**.

It submits, polls, fetches and cancels over **`/v1/videos`** — the same routes a customer's SDK calls
— because unlike its Images API, OpenAI's Videos API is asynchronous by construction. A console
driving the real surface is worth more than one driving an admin shortcut.

The single exception is listing, which that dialect refuses on purpose, so the hub grew **exactly one
route**: `GET /api/videos/jobs`, client-scoped and capability-scoped. **Considered and rejected:
`GET /api/images/jobs?media=video`** — a query parameter standing in for a scope, over two kinds of
job whose bytes come from two different routes.

And the `501` on `GET /v1/videos` was rewritten in the same commit that made half of it false. It
used to say *"this coordinator holds no client-scoped index of jobs"*. It holds one now. The reason
it keeps is the one that was always load-bearing: **a video id is itself the capability to fetch the
bytes**, so the dialect does not hand a caller a way to enumerate other people's.

One more consequence of one job model with two fetch surfaces: a job's row now names the route *its
own* capability is fetched from. A video row pointing at `/api/images/jobs/{id}/content/0` was a 404
with a plausible shape, which is the failure a panel cannot diagnose.

## `media` is a label, and the `MediaJob*` rename is refused for good

`inferhub_image_recipe`, `inferhub_image_jobs_total` and `inferhub_image_job_seconds` carry
`media="image"|"video"`. Existing queries keep working and now sum both media — which is the honest
arithmetic, since both were already in there unlabelled and a four-minute clip in a picture histogram
makes both unreadable.

**Considered and rejected: `inferhub_video_*` as its own family.** v3.13's audio precedent cuts the
other way: audio is two series because seconds and characters are two *questions*. "Why is this model
not offered" is one question with one answer shape, and two families would mean every fleet-refusal
query written twice and one of them forgotten.

v3.25 deferred the `ImageJob*` → `MediaJob*` type rename to this phase in writing. The answer is
**no, permanently**: these metric names are in other people's dashboards and alert rules, and a
rename breaks every one of them silently to buy a tidiness a label delivers for free.

## `VideoSecondsPerDay` — the meter has had two units since v3.25; now the gate does too

A video is billed in **megapixel-steps** (the card: ~970 for a five-second 832×480 clip at 30 steps,
against an SDXL image's ~31) **and** in **seconds** (the question a person asks). Only the first was
ever checked, so a client whose limits were sized in pictures rendered clips against a figure nobody
sizes in megapixel-steps — v3.10's own rule, *a unit's budget is that unit's own*, failing in the
newest unit.

`TryAdmit` now takes the request's secondary unit and checks both budgets. The request's primary unit
is still checked first — a caller out of both hears about the one the request is principally measured
in — and the `402` names the unit that ran out, because "megapixel-step" would send an operator to
raise the wrong knob.

**There is deliberately no `VideoSecondsPerMinute`.** A clip's seconds arrive in one lump minutes
after admission, so a sliding window would refuse the wrong request. The burst control for a
four-minute job is `MaxConcurrent`, which already exists.

## Rules 5 and 7

**Zero new `PackageReference`** — `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`. Rule 7 is untouched: the new listing carries no prompt, the new
budget counts seconds, and the console's clip lives in a browser tab because the hub dropped its copy
when it was fetched.

## Tests

The phase's slice, `tests/InferHub.Tests.Coordinator`: **643 passed / 43 skipped**. Also run, because
this release changed what they cover: `Tests.Shared` **129 passed**, `Tests.Node` **151 passed / 3
skipped**, and `ConsoleContractTests` from the Mesh slice **8 passed**. `dotnet build InferHub.sln`:
clean, 0 warnings.

New assertions: an exhausted clip budget refuses while the step budget has room, and the 402 names
`video-second`; the step budget still fires first when both are gone; **no `VideoSecondsPerDay` means
no second gate**; an exhausted clip budget does not refuse chat. On the metrics side, a refused video
recipe is the *same* series with `media="video"`, the job counter and its histogram separate clips
from pictures, and a job recorded with no medium is labelled `image` — which is what every job in
that counter was until v3.25. On the view: a video job's row names `capability`, the `/v1/videos`
content route and the **measured** duration; an image job's row is byte-for-byte what it was; and the
two listings see only their own capability while a stranger sees neither.

`ConsoleContractTests` gained `nodes[].tools.images[].media` in its read set and a video recipe in its
fixture — a payload that stopped carrying that field would silently draw every clip in the pictures
table, which is exactly the class of failure that suite exists for.

## What was NOT established, said out loud

- **Nothing was rendered and no clip was watched.** The panel has not been driven against a node with
  a card: every assertion here is against the suite's fixtures and a real hub with a fake worker. The
  video `<video>` element has never had 30 MB of mp4 put into it by this code on a real machine.
- **The full solution suite was not run for this release**, by request. Four slices were (above), and
  CI runs the rest on the tag.
- **The `media` label's effect on an existing dashboard is reasoned, not observed.** Adding a label
  to a series does not break a query that aggregates; a query that matched on an exact label set will
  now match nothing. No Grafana was opened.
- **`VideoSecondsPerDay` is enforced against consumed seconds, never predictively.** A single job may
  overshoot the budget by its own duration — the same lag every other daily budget here has had since
  v2.0, and the same trade: refusing up front would mean trusting an estimate.
- **Still no image-to-video, no caller-chosen fps, no audio, no 480p entry for the 14B**, and no
  listing on `/v1/videos`.

## Published-image check

The coordinator image changes (console, endpoint, metrics) and the node image changes
(`ProcessToolRuntime` reports video recipes). What this phase changed is *reporting*, and the honest
check for it needs a node with a card declaring a video recipe — which is the same thing phase 60's
day needs and does not have here.

What was verified: the tag's five images publish green, and the shape the hub reads is pinned by
`ConsoleContractTests` against a real Kestrel host and a real status payload. **Nobody has opened the
Video panel against a fleet.** That is the first item on phase 60's list, beside v3.26's own.
