# Social copy — v3.27.0 (phase 59, console/metrics/docs for the video track)

**No image, deliberately.** Third release running with the same reason: the obvious visual is a frame
of a video, and nothing has been rendered on a card. A screenshot of a panel driven by a fake worker
would be a picture of a fixture.

**Blog post is live:** https://devart.solutions/blog/inferhub-3-27-a-refusal-nobody-can-see

---

## Facebook

**InferHub 3.27 — a refusal nobody can see**

Two releases ago InferHub learned to render video. One release ago it got three models. Both times
the same sentence went into the release notes: *a video recipe refused for its licence or for the
card is invisible at the hub*.

That is what 3.27 fixes, and the reason it mattered is worth being precise about.

When a node decides it cannot run a model — the licence was never accepted, the weights do not fit
the VRAM budget you declared — it simply does not declare it. That is correct routing: the fleet
never sends work at a model that cannot run. It is also the worst possible diagnostic, because at
the coordinator it looks exactly like a model nobody installed, a model still downloading, and a
typo. **Four causes, four different fixes, one symptom: nothing.**

Now every video recipe on the fleet reports with a reason — in **one list with the pictures**, with a
`media` field, because the four reasons we wrote for images turned out to be exactly the right four
for a clip. Two lists would have been two mailboxes to keep in step. What actually differs is
rendering, so the console does the splitting.

**The most expensive refusal we ship is now visible.** `wan-t2v-14b-720p` wants 24 GB and a 24 GB
card offers about 22.5 after its reserve — so such a node never declares it and nobody meets the
ceiling four minutes into a render. The row says `over-budget` and names the arithmetic. That is the
ceiling working, not a fault.

**The new Video panel speaks `/v1/videos`** — the same routes your SDK calls. OpenAI's Videos API,
unlike its Images API, is asynchronous by construction, so there was nothing to invent, and a console
driving the real surface fails when your integration would. The one exception is listing, which that
dialect refuses on purpose: a video id *is* the capability to fetch the bytes.

**And a budget we had been metering and never checking.** A clip is billed in two units —
megapixel-steps (~970 for five seconds, against an image's ~31) and seconds. Only the first was ever
enforced. `VideoSecondsPerDay` closes it, both are checked, and the 402 names the one that ran out,
because "megapixel-step budget exhausted" sends you to raise the wrong knob.

Nothing was rendered and nobody has opened the panel against a fleet with a card in it. The next
release ships **no feature at all** and instead pulls every published image and drives the whole
fleet end to end. That is where the first sentence about how the video *looks* will come from.

Still zero new dependencies.

https://inferhub.devart.solutions/#idocs_console_video

#selfhosted #opensource #dotnet #AI #video #observability

---

## X / Twitter

**Post (269 characters X-weighted, URL counted as 23)**

InferHub 3.27: a node that cannot run a model simply does not declare it.

Correct routing. Worst possible diagnostic — at the hub that is identical to a model nobody
installed, one still downloading, and a typo.

Four causes, one symptom: nothing. Now it says which.

https://inferhub.devart.solutions/#idocs_console_video

**Optional thread**

2/ The fix is one list, not two. Video recipes ride in the same report as images with a `media`
field — the four reasons we wrote for pictures were already the right four for a clip. What actually
differs is rendering, so the console splits them.

3/ The most expensive refusal we ship is now visible: wan-t2v-14b-720p wants 24 GB, a 24 GB card
offers ~22.5 after its reserve, so such a node never declares it. That is the ceiling working, and
the row says so rather than showing an error.

4/ The Video panel talks to /v1/videos — the same routes your SDK calls — because OpenAI's Videos
API is async by construction. A console driving the real surface fails when your integration would.

5/ Listing is the one route that dialect refuses, on purpose: a video id IS the capability to fetch
the bytes. So there is one route of our own, client-scoped, and the panel holds a key you paste.

6/ Metrics got a `media` label rather than an `inferhub_video_*` family. Two families means every
fleet-refusal query written twice and one of them forgotten. Existing dashboards keep working.

7/ And a budget we metered but never checked: a clip is billed in megapixel-steps AND seconds, and
only the first was a gate. VideoSecondsPerDay closes it; the 402 names the unit that ran out.

8/ Nothing was rendered. Nobody has opened the panel against a real card. Next release ships no
feature at all and instead drives every published image end to end — that is where the sentence
about how it looks comes from.
