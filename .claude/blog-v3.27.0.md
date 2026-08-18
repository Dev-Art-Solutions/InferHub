# Blog post for v3.27.0

**Slug:** `inferhub-3-27-a-refusal-nobody-can-see`
**Title (EN):** InferHub 3.27 — a refusal nobody can see
**Visibility:** EN visible, BG hidden (the house default)
**Image:** none. Still the same reason: the obvious visual is a frame of a video, and nothing has
been rendered on a card.

**Excerpt (EN):** InferHub 3.27 makes the video track visible at the hub. Two releases shipped the
capability and a catalogue of three, and the most expensive refusal in the project — a 14B model a
24 GB card is deliberately never offered — was indistinguishable from a model nobody installed.

---

## Content (EN) — HTML, entity-escaped at `create_post` time

```html
<p>Two releases ago InferHub learned to render video. One release ago it got a catalogue of three
models. Both times, the same sentence went into the release notes: <em>a video recipe refused for its
licence or for the card is invisible at the hub</em>.</p>

<p>That is what 3.27 is about, and it is worth being precise about why it mattered. When a node
decides it cannot run a model — the licence was never accepted, the weights do not fit the declared
VRAM budget — it simply does not declare it. That is the right routing behaviour: the fleet never
sends work at a model that cannot run. It is also the worst possible diagnostic, because at the
coordinator that looks exactly like a model nobody installed, a model still downloading, and a
typo.</p>

<p>Four causes. Four different fixes. One symptom: nothing.</p>

<p>We solved that for images in 3.19 — every recipe reported with a reason. Video was deliberately
left out of the same report, because those rows would have landed in a panel that draws pictures.
The cost was stated in the notes rather than discovered later, and this is the release that pays
it.</p>

<h2>One list, two panels</h2>

<p>The obvious fix is a second list for video. We did not take it: two lists are two mailboxes to
keep in step, two shapes to drift apart, and a second copy of a reason list that will be extended
once and in one place. And the four reasons written for pictures turn out to be exactly the right
four for a clip — a licence, the card, a coordinator profile, or weights that are not there yet —
each with the fix it already had.</p>

<p>What genuinely differs is <em>rendering</em>. So the report carries one list with a
<code>media</code> field on each row, and the console does the splitting.</p>

<table>
  <tr><th>Recipe</th><th>Offered</th><th>Why not</th></tr>
  <tr><td>wan-t2v-1.3b</td><td>yes</td><td>—</td></tr>
  <tr><td>cogvideox-2b</td><td>yes</td><td>—</td></tr>
  <tr><td>wan-t2v-14b-720p</td><td>no</td><td>wants 24 000 MiB; a 24 GB card offers 22 528 after its reserve</td></tr>
</table>

<p>That last row is the ceiling working, not a fault — and it is the most expensive refusal this
project ships. A node with a 24 GB card never declares the 14B, the hub never routes at it, and
nobody finds out four minutes into a render. Until this release, nobody found out at all.</p>

<h2>The Video panel speaks the API your SDK speaks</h2>

<p>The console gained a Video panel: a prompt, a size, a duration, the row updating while it runs —
queue position, step <em>n</em> of <em>m</em>, elapsed, which node has it — a cancel button, and the
clip playing in the page.</p>

<p>It submits, polls, cancels and fetches over <code>/v1/videos</code>: the same routes a customer's
SDK calls. OpenAI's Videos API, unlike its Images API, is asynchronous by construction, so there was
nothing for us to invent — and a console driving the real surface is worth considerably more than one
driving an admin shortcut, because it fails when your integration would.</p>

<p>The single exception is listing, which that dialect refuses on purpose: a video id <em>is</em> the
capability to fetch the bytes, so an enumeration route would hand any caller a way to walk other
people's jobs. So the hub grew exactly one route of its own — <code>GET /api/videos/jobs</code>,
scoped to the client and to the capability — and the panel holds a client key you paste rather than
one every SDK holds.</p>

<p>We also went back and rewrote the 501 on <code>GET /v1/videos</code> in the same commit. It used
to say "this coordinator holds no client-scoped index of jobs". It holds one now. A caveat that a
later release makes false is worse than no caveat, so the reason it keeps is the one that was always
load-bearing.</p>

<h2>A label, not a second family of metrics</h2>

<p>The job counters have been counting video since 3.25 with nothing on the series to tell it from a
picture — which means a four-minute clip and a nine-second render share a histogram and make both
unreadable.</p>

<p>The fix is a <code>media</code> label on the three series that already carried both, not a new
<code>inferhub_video_jobs_total</code> family. "Why is this model not offered" is one question with
one answer shape, and two families means every fleet-refusal query gets written twice and one of them
forgotten. Existing dashboards keep working and now sum both media, which is the honest arithmetic
given both were already in there unlabelled.</p>

<p>The internal rename that would have followed — <code>ImageJob</code> to <code>MediaJob</code>
everywhere — is refused for good. These metric names are in other people's dashboards and alert
rules. A rename breaks all of them silently to buy a tidiness a label delivers for free.</p>

<h2>The budget that was being metered and never checked</h2>

<p>A video is billed in two units: megapixel-steps, because it is the same card a picture spends —
roughly 970 for a five-second clip against an image's 31 — and seconds, because that is the number a
person actually asks about.</p>

<p>Only the first was ever <em>checked</em>. The seconds were counted, reported, and enforced by
nothing. So a client whose limits were sized in pictures rendered video against a figure nobody sizes
in megapixel-steps.</p>

<p><code>VideoSecondsPerDay</code> closes it. Both budgets are checked at submission and the 402
names the one that ran out, because "megapixel-step budget exhausted" sends an operator to raise the
wrong knob. There is deliberately no per-minute companion: a clip's seconds arrive in one lump
minutes after the job was admitted, so a sliding window would refuse the wrong request. The burst
control for a four-minute job is the concurrency cap, which already exists.</p>

<h2>What we have not done</h2>

<p>Nothing was rendered and nobody has opened this panel against a fleet with a card in it. Every
claim here is against the test suite and a hub running a fake worker. That is not an oversight — the
next release ships no feature at all and instead pulls every published image and drives the whole
fleet end to end, and that is where the first sentence anyone writes about how the video
<em>looks</em> will come from.</p>

<p>Still zero new dependencies, and a deployment that changes no configuration behaves exactly as it
did on 3.26.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.27.0">v3.27.0</a>
· Docs: <a href="https://inferhub.devart.solutions/#idocs_console_video">the Video panel</a></p>
```
