# Blog post for v3.26.0

**Slug:** `inferhub-3-26-a-catalogue-of-one-proves-nothing`
**Title (EN):** InferHub 3.26 — a catalogue of one proves nothing
**Visibility:** EN visible, BG hidden (the house default)
**Image:** none. The obvious visual is still a frame of a video, and no video has been rendered.

**Excerpt (EN):** InferHub 3.26 adds two video models to the one 3.25 shipped — and every field the
video seam introduced turned out to be untested, because a catalogue of one cannot disagree with
itself. One of the new models runs at 8 fps, which is how we found out our fallback of 16 was a bug
waiting for a second model.

---

## Content (EN) — HTML, entity-escaped at `create_post` time

```html
<p>Last release we shipped video generation and exactly one model. Every field the recipe format
needed for video — the frame rate, the duration list, the VRAM figure, the scheduler override — was
therefore exercised by a single file that agreed with itself about everything.</p>

<p>InferHub 3.26 adds two more, and each one makes a field able to be wrong in a way one recipe
could not.</p>

<table>
  <tr><th>Recipe</th><th>Geometry</th><th>Clock</th><th>VRAM</th><th>Download</th></tr>
  <tr><td>wan-t2v-1.3b</td><td>832×480</td><td>16 fps, 2–5 s</td><td>~15.5 GB</td><td>~29 GB</td></tr>
  <tr><td>wan-t2v-14b-720p</td><td>1280×720</td><td>16 fps, 2–5 s</td><td>~24 GB at nf4</td><td>~75 GB</td></tr>
  <tr><td>cogvideox-2b</td><td>720×480 only</td><td>8 fps, one 6 s offer</td><td>~16 GB</td><td>~13 GB</td></tr>
</table>

<p>All three are Apache-2.0, so none of them needs a licence decision from you.</p>

<h2>The 8 fps model found a bug that could not fail a test</h2>

<p>CogVideoX-2b runs at <strong>8 frames per second</strong>. Wan runs at 16, and our worker treated
16 as a default when a recipe did not say. Put those together and a 49-frame clip gets encoded at
twice its rate.</p>

<p>Nothing errors. The file plays. It is simply twice as fast as the model intended, and the
conclusion a person draws is "this model is bad at motion" rather than "the encoder used the wrong
number". That is the same shape of failure the previous release spent a day removing from Wan — a
VAE loaded at the wrong precision gives you noise rather than an exception — reintroduced by a
convenience.</p>

<p>So the fallback is gone. A video recipe with no frame rate, an empty duration list, or a default
duration that is not in its own list is <strong>skipped and logged by name</strong> before the node
declares the capability at all. The failure moved from inside somebody's four-minute job to a line
in a log at startup, which is the only move that matters.</p>

<h2>The 14B model shipped the same config value as the small one</h2>

<p>The previous release added a scheduler override for a value that upstream's own 720p example sets
by hand, and wrote down that it existed "for the 720p entry the next release will add, where the repo
may not say so".</p>

<p>The repo does not say so. Wan2.1-T2V-14B ships <code>flow_shift: 3.0</code> in its scheduler
config — byte-identical to the 1.3B's, and 3.0 is the 480p value. So the override is not a
defensive convenience for a config that might disagree; it is the only thing standing between a 720p
render and the wrong sigma schedule, which does not error and does not obviously look wrong.</p>

<h2>A video job's peak allocation is at the end</h2>

<p>The denoising loop holds a compact latent. The VAE then materialises <em>every frame at full
resolution at once</em> — for 81 frames at 720p, the largest single allocation in the job, arriving
after all the expensive minutes.</p>

<p>So a recipe can ask for tiled decoding. Per recipe rather than always on: tiling trades tile seams
for headroom, and this project's position on seams is that the trade belongs to whoever asked for
it.</p>

<h2>The first model we ship that your card may not be told about</h2>

<p><code>wan-t2v-14b-720p</code> declares 24 GB of VRAM. A 24 GB card has about 22.5 GB of headroom
once the default reserve is held back — so a node with one <strong>never declares the model</strong>,
the hub never routes to it, and nobody discovers the ceiling four minutes into a render.</p>

<p>That gate has existed for ten releases and had never withheld anything we shipped. Two things
follow from it:</p>

<ul>
  <li><strong>A recipe id is a model and a geometry.</strong> The gate is handed one number, once,
  before any caller exists — so the figure is sized at the largest size-and-duration pair the recipe
  offers. A recipe for a model's cheap corner is a second id with a shorter list.</li>
  <li><strong>A video recipe that declares no VRAM figure is not declared at all.</strong> An image
  recipe with the same silence still is. The difference is what the silence costs: a few gigabytes
  for a picture, and a whole card for a clip.</li>
</ul>

<h2>What we did not do</h2>

<p><strong>Nothing ran on a GPU.</strong> Both VRAM figures are arithmetic over the repositories' own
file listings plus an activation allowance nobody has measured, and no clip from either new model has
been watched. Every other claim above was read out of the pinned library or the models' checked-in
configs.</p>

<p>No image-to-video. No caller-chosen frame rate. No audio. No 480p entry for the 14B — the same
weights at a second geometry means two recipe ids over one loaded pipeline, and our residency map
would count it twice against one card, so it is a release rather than a JSON file. And still no
console panel for video, which means a model refused for its licence or its budget is invisible from
the coordinator. That is the next one.</p>

<p>Still zero new dependencies: two JSON files and about eighty lines of Python.</p>

<p><a href="https://inferhub.devart.solutions/#idocs_video_catalogue">Docs</a> ·
<a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.26.0">Release notes</a></p>
```
