# Blog post for v3.35.0

**Slug:** `inferhub-3-35-a-node-with-no-gpu` (DRAFT — not yet published)
**Title (EN):** InferHub 3.35: a node with no GPU in it
**Visibility:** EN visible, BG hidden (the house default)
**Image:** none — the visual would be a screenshot of a config file.

**Excerpt (EN):** Six releases taught the coordinator to speak four vendors' dialects. This one takes
the same code to the other end of the mesh: a node whose backend is Claude or Gemini, meshed or solo.
The interesting part is what it refused to become — a second router — and the two things doing it
properly cost.

---

## Content (EN) — HTML, entity-escaped at `create_post` time

```html
<p>Since 3.29 an InferHub coordinator can name four cloud vendors, give each its own credential,
reach each through its own hand-rolled dialect, and route to them by policy. Since 3.34 you can see
all of that on a page.</p>

<p>A <em>node</em> could reach two of them. <code>Backend:Type</code> was <code>ollama</code> or
<code>openai</code>, and the Anthropic and Gemini clients — both already written, both already
sitting in the shared library the node compiles against — were reachable only from the hub. So a
machine with no GPU that somebody wanted to run as <em>a node backed by Claude</em> could not be
configured at all. 3.35 is that sentence deleted.</p>

<pre><code>{
  "Backend": { "Type": "anthropic" },
  "Upstream": {
    "MaxTokens": 4096,
    "Models": { "Include": ["claude-sonnet-5"] }
  }
}</code></pre>

<p>That node registers with your hub, reports one model, and answers jobs the coordinator cannot tell
apart from a box with a card in it. Turn the coordinator off instead and the same process is a
private, authenticated, RAG-capable OpenAI-compatible front end to that vendor, in one
<code>docker run</code>.</p>

<h2>The part that is a release rather than three</h2>

<p>None of the wire work is new. The Anthropic dialect is 3.31's, the Gemini one is 3.32's,
OpenRouter's configuration is 3.30's, and they all live behind one five-method interface that 3.29
extracted precisely so this could happen. The node composes the same objects the hub does.</p>

<p>What made it a release was refusing the obvious symmetry.</p>

<h2>One node is one upstream</h2>

<p>The tempting move is to give the node the hub's <code>Providers:</code> map. It reads as
consistency, and it is a second router — on the one host in this system that has never had one. Two
policy vocabularies. Two steer headers. Two places a prompt's destination gets decided. And, the day
they disagree, a node quietly overruling the hub that dispatched to it about which vendor sees
somebody's text.</p>

<p>So the asymmetry is the design: <strong>the hub chooses, the node serves.</strong> If you want two
vendors, configure them on the coordinator, where the router has lived since 3.33 — or run two nodes,
which costs a container.</p>

<p>The same reasoning killed a model map on the node. The hub has one because it routes; a node
reports what it serves, so the allowlist is the whole consent and the vendor's own id is the model
name.</p>

<h2>The section is called <code>Upstream:</code> now</h2>

<p>It was <code>OpenAi:</code>, which was already a near-miss for OpenRouter and becomes an outright
lie the moment it has to carry <code>AnthropicVersion</code>. <code>OpenAi:AnthropicVersion</code> is
the kind of key somebody screenshots.</p>

<p>The old section still binds. It is projected onto the new one at startup, so every node configured
since 2.4 is byte-identical and nobody has to touch a file. What is <em>not</em> allowed is writing
the same key in both sections with different values: that fails startup and names both paths, because
which upstream receives a prompt should not be decided by which section a configuration binder
happened to apply last.</p>

<h2>A backend now says what it can do</h2>

<p>Anthropic publishes no embeddings API. Until this release the node's capability declaration was a
constant — <code>chat</code> and <code>embed</code>, always, on the reasonable-sounding theory that
both endpoints belong to the same server. A cloud vendor broke that theory.</p>

<p>Left alone, an Anthropic-backed node would have declared <code>embed</code>, the hub would have
routed an embedding job to it, and the caller would have received a <code>501</code> — after the
hop, from inside a failed job, wearing a <code>502</code>. So a backend now <em>declares</em> its
capability kinds, and an embedding request against such a fleet is a <code>503</code> naming the
missing capability <strong>at the coordinator, before anything moves</strong>. On a solo node the
same request is a <code>501</code> that says why.</p>

<p>That refusal is deliberately kept separate from the one you get when an operator has switched a
capability off. That one is a <code>503</code> and worth retrying; this one is a fact about the
upstream and never will be. Two refusals that mean different things should not share a status code.</p>

<h2>A vendor node with no allowlist will not boot</h2>

<p>OpenRouter lists 419 model ids. Gemini lists around fifty, embedding-only and image models among
them. A node that reported its upstream's catalogue would be telling your router it can hold a
conversation with an image model, and the router would believe it — that is what a model report
<em>is</em>.</p>

<p>So the three cloud types require an allowlist, and the startup failure names the key.
<code>openai</code> is deliberately not held to this: it is usually one vLLM serving one model, and
breaking every such deployment to protect it from a catalogue it does not have is not a trade.</p>

<p>We also declined to ship a default allowlist per vendor. A model list checked into a repository is
wrong by the time it ships — the same reason this track has refused to carry a price table.</p>

<h2>Still zero new dependencies</h2>

<p>No new package. The shared library is still an empty project file. Four vendors, two hosts, three
hand-rolled dialects, and the dependency surface has not moved since 2023.</p>

<h2>What has not been established</h2>

<p>No live vendor endpoint was called by anything in this release. Every assertion here runs against
a stub or a payload recorded from the vendors' own documentation on the day each dialect was written.
That is a deliberate rule for this track: a test that needs somebody's API key is a test CI cannot
run, which makes it a test everyone learns to skip, and it bills a card on every commit.</p>

<p>Which means "the node speaks Anthropic" is, today, a claim about translation rather than about a
conversation anybody has had. The next release is the one where the real keys come out — one per
vendor, driven by hand, with the numbers written down.</p>

<p>What <em>was</em> checked, the same evening the tag went up, is the published container rather
than a test host: pull the image, point it at a stub upstream, and ask it. It sent
<code>POST /v1/messages</code> with <code>x-api-key</code> and <code>anthropic-version</code> and
<strong>no <code>Authorization</code></strong>, supplied the <code>max_tokens</code> the vendor
requires, lifted a <code>system</code> turn to the top level, and kept the stub's 140 cached tokens
out of the prompt count. It reported one model out of the two offered, declared <code>chat</code> and
not <code>embed</code>, and answered both embedding routes with a <code>501</code> that
<strong>never reached the upstream at all</strong> — the stub's counter is the proof. Both startup
refusals fired with their own sentences, and a node configured the old way was unchanged down to
which credential header it used. Eleven checks; the vendor was ours, everything between the
container and it was not.</p>
```
