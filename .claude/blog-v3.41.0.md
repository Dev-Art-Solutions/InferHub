# Blog post — v3.41.0

Slug: `inferhub-3-41-the-fleet-turns-a-model-back-on-by-itself`
Title (EN): **InferHub 3.41 — the fleet turns a disabled model back on by itself**
EN visible / BG hidden. Author: Admin.

Excerpt (EN): v3.39 gave an admin a switch to disable one model on one node. Nothing watched the
fleet and flipped it back — until now, and the first thing the coordinator tried to trigger on
turned out to be the wrong signal entirely.

> **No shell commands in the HTML** — the blog sits behind a Cloudflare WAF that blocks the request,
> not the command. JSON bodies only.

---

## content_en

<p><a href="https://inferhub.devart.solutions/#idocs_model_routing">v3.39</a> gave an admin a lever: disable one model on one node, without touching disk, without a restart. Useful for freeing VRAM on purpose, or taking a model out of rotation while you test something else on the box. What it didn't have was a way back. A client kept asking for that exact model, got a clean <code>503</code>, and the fix — if the model was still sitting there, disabled, one API call away — needed a human to notice.</p>

<p>v3.41 is that human, automated. A background loop watches for a disabled model drawing real demand and re-enables it, through the same VRAM check the admin endpoint already runs. And along the way it caught its own bug before shipping — the kind a green test suite has no way to see.</p>

<h2>What it does</h2>

<p>Every few seconds the coordinator counts how many "can't serve this model right now" refusals each model has drawn since the last check. Past a threshold, it looks for one node that:</p>

<ul>
<li>already has the model on disk,</li>
<li>is healthy and not cordoned,</li>
<li>does not currently declare it as routable — meaning it's disabled, not just busy,</li>
</ul>

<p>and runs it through the exact VRAM precheck a human's <code>POST .../enable</code> call hits. A pass re-enables it. A refusal is logged and skipped — <strong>the auto-scaler never overrides the precheck</strong>. It has no <code>force=true</code> to reach for.</p>

<h2>The wrong signal, caught before a tag</h2>

<p>The first version of this read a different number: the usage ledger's count of requests that fell back to a cloud provider. The reasoning felt sound — a disabled model can't be served locally, so surely those requests fall back the same way a model nobody holds at all does.</p>

<p>They don't. Reading the actual routing code before shipping turned up why: the check that decides whether to burst to a cloud provider asks "does any node <em>hold</em> this model," not "does any node currently <em>route</em> it" — and a disabled model is still held, just not routable. That check never once looked at the thing v3.39's disable toggle changes. The fallback counter would have sat at zero forever, for exactly the one case this feature exists to fix — a trigger that could never fire.</p>

<p>The real refusal was already there, a few lines further into the same code path: a <code>503</code> that says, in words, "no node currently provides this capability for this model." It was logged and nothing else. v3.41 adds the counter that was missing, taken from that exact line, and points the auto-scaler at it instead.</p>

<p>Caught by reading the code the trigger depends on, not by trusting that "fallback" was the right word for what was needed. A green build and a full passing test suite said nothing about whether the condition could ever be true — the same lesson this project keeps re-learning about a published image applies one layer earlier, to the logic before the image is even built.</p>

<h2>What it won't do</h2>

<p><strong>No downloads.</strong> A candidate has to already hold the model. Pulling one onto a node that never had it is a multi-minute job with its own manual flow already; doing that unattended, on a threshold, is a different and much riskier feature.</p>

<p><strong>No scale-in.</strong> There's no signal anywhere in this codebase that says which node's idle copy of a model to turn back off — the counter that drives scale-out doesn't carry a node dimension, on purpose, the same way the VRAM precheck refuses to pretend it measured a model's footprint instead of estimating it. Inventing a scale-in trigger from silence would be exactly that kind of guess.</p>

<p><strong>No moving collections between nodes.</strong> This is what the phase started as — reassign an unhealthy node's vector collection to a healthier one — and it doesn't survive contact with how collection ownership actually works here: a node-owned collection's vectors live only on that node. Reassigning ownership moves a label, not the data, and would leave a collection silently empty on its new "owner." Real inter-node replication for node-owned corpora is a different, larger feature.</p>

<h2>Off, then quiet, then real</h2>

<p>Two switches, checked in order. <code>AutoScaling:Enabled</code> defaults to <code>false</code>. Turn it on, and <code>AutoScaling:DryRun</code> still defaults to <code>true</code> — it only logs "would enable X on node Y" until dry-run is turned off too. A fleet that changes nothing behaves exactly as 3.40 did.</p>

<h2>Verified twice, the same way</h2>

<p>Once against a real coordinator and a real Ollama-backed node built from source, once again against the published images. Same sequence both times: disable a model, ask for it, get refused with the real <code>503</code>, watch the next tick's dry-run log name the decision correctly, then with dry-run off watch it actually re-enable the model — and get a real answer from it on the next request, with nobody touching anything in between.</p>

<h2>Still not established</h2>

<p>Only tried against a single node, so "pick the least-loaded of several candidates" is checked by reading the code, not by watching it choose. And the node used for both runs declared no VRAM budget, so every precheck along the way took the "nothing to check" branch — the "this would not fit" refusal branch is exercised by 3.39's own tests, not by this release's live runs.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.41.0">v3.41.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_auto_scaling">Auto-scaling</a>.</p>
