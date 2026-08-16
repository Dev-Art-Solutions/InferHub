# Blog post for v3.25.0

**Slug:** `inferhub-3-25-the-api-we-did-not-have-to-invent`
**Title (EN):** InferHub 3.25 — the API we did not have to invent
**Visibility:** EN visible, BG hidden (the house default)
**Image:** none. The obvious visual is a frame of the video, and no video has been rendered.

**Excerpt (EN):** InferHub 3.25 renders video on your own card — and the client API is OpenAI's own
Videos API, not one we designed. Ten releases ago we built our own async image surface because
OpenAI had none to adopt. This time there was one, so we adopted it and added nothing.

---

## Content (EN) — HTML, entity-escaped at `create_post` time

```html
<p>Ten releases ago we shipped an asynchronous API for image generation and wrote down why we had
invented it: <em>OpenAI has no asynchronous Images API to adopt.</em> The house rule is to speak the
dialect clients already speak, and to invent only where there is none.</p>

<p>Video has one. So InferHub 3.25 renders video, and the client surface is OpenAI's Videos API —
create, poll, fetch, delete — with nothing of ours bolted onto it.</p>

<pre><code>POST /v1/videos          {"model":"wan-t2v-1.3b","prompt":"...","seconds":5}
GET  /v1/videos/{id}     status + progress
GET  /v1/videos/{id}/content   the mp4, once
DELETE /v1/videos/{id}   cancel and drop
</code></pre>

<p><code>client.videos.create(...)</code> in any OpenAI SDK now works against a self-hosted mesh with
a consumer card in it.</p>

<h2>Two routes we refused, out loud</h2>

<p><code>GET /v1/videos</code> lists a client's videos. We answer <strong>501, with the reason</strong>:
this coordinator holds no index of your jobs, and building one would hand every caller a way to
enumerate ids that are themselves the capability to fetch the bytes. Keep the id we returned.</p>

<p><code>POST /v1/videos/{id}/remix</code> needs the request kept after the job ends. Nothing durable
here holds a prompt — there is no field for one, deliberately — so there is nothing to remix from.
Also 501, also with the sentence.</p>

<p>A 404 would have been less work and would have read as "this server is out of date". A 501 that
names the reason is a decision you can disagree with.</p>

<h2>It is the same job model, which is why last week's release came first</h2>

<p>The queue, the per-step progress, the cancel that leaves the worker holding its weights, the
five-minute read-once retention and 3.24's optional durability are the code image jobs already run.
That ordering was deliberate: a video job runs for minutes and produces tens of megabytes, so
"the hub forgot your job" gets expensive exactly when video arrives. Doing durability first meant
video inherited it instead of re-arguing it.</p>

<p>There is deliberately <strong>no <code>Videos:</code> config section</strong>. A clip is one
attachment over the wire an image already uses, so two keys for one ceiling would be two numbers an
operator could raise independently — and exceeding the mesh's message limit tears a node's
connection down rather than failing a message. One wire, one ceiling.</p>

<h2>One model, and four things we read rather than assumed</h2>

<p><code>wan-t2v-1.3b</code> is Wan2.1-T2V-1.3B — Apache-2.0, so no licence decision to make — at
832×480, two to five seconds, 16 fps. Four facts about it came out of the pinned <code>diffusers</code>
wheel and the model's own configs, because every one of them produces a <em>plausible non-failure</em>
when it is wrong:</p>

<ul>
  <li><strong>The VAE loads separately, at fp32, under a bf16 transformer.</strong> A uniform bf16
  load does not error. It gives you noise, four minutes in.</li>
  <li><strong><code>flow_shift</code> is a scheduler setting, not a call argument</strong> — and this
  repo already ships the right value. Upstream's example sets it by hand because it is written for a
  different, larger model. Our first draft of this release's notes said the opposite, before we read
  the config.</li>
  <li><strong>Sizes divide by 16, not by 8.</strong> <code>840x480</code> is a perfectly good image
  size and an invalid video one, so it is refused at the edge rather than deep inside a library.</li>
  <li><strong>"1.3B" names the transformer only.</strong> The text encoder beside it is about 11B,
  every weight in the repo is stored fp32 with no fp16 variant, and the first pull is ~29 GB. The
  VRAM figure we declare is sized from the encoder, not from the 1.3B in the name.</li>
</ul>

<h2>Five seconds is 5.06 seconds, and we say so</h2>

<p>A latent video pipeline puts frames on a grid, so <code>seconds: 5</code> means 81 frames and 81
frames at 16 fps is <strong>5.0625 seconds</strong>. The request field is a label for an offer; the
response reports the measurement. A duration the model does not offer is refused <em>naming the
list</em> rather than rounded to the nearest one — because asking for six seconds and silently
getting five leaves you with a clip that is fine and wrong, and you find out days later.</p>

<h2>What it costs, in two numbers</h2>

<p>A video meters <strong>both</strong> megapixel-steps and video-seconds. The first is what the card
actually spent: a video transformer denoises the whole latent stack on every step, so a five-second
832×480 clip at 30 steps is about <strong>970</strong> megapixel-steps against an SDXL image's 31.
That is why it spends the same daily budget an image does — it is the same card. The second is the
number a human asks about, and neither can be derived from the other.</p>

<h2>What 3.25 does not do, and one thing nobody has done</h2>

<p>No image-to-video. No caller-chosen fps. No audio — Wan2.1 T2V produces none, and a silent track
added to look complete is a lie in a container. No console panel yet, which means a video model
refused for its licence or its VRAM budget is currently invisible from the coordinator. Those are
the next two releases.</p>

<p>And the honest one: <strong>no video has been watched.</strong> The test suite drives the whole
surface against a worker that writes a real container with padded samples — every claim about the
API is tested, and no claim about the picture is made. Whether the model produces something worth
watching is a question for the release at the end of this track, which ships no feature at all and
instead pulls every published image and drives the fleet end to end.</p>

<p>Still zero new dependencies. The encoder is a static ffmpeg binary inside a Python wheel, reached
through the same child process as everything else, and nothing in the C# has ever decoded a frame.</p>

<p><a href="https://inferhub.devart.solutions/#idocs_video">Docs</a> ·
<a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.25.0">Release notes</a></p>
```
