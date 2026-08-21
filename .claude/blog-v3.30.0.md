# Blog post for v3.30.0

**Slug:** `inferhub-3-30-two-bugs-that-were-never-about-openrouter` (PUBLISHED)
**Title (EN):** InferHub 3.30: adding OpenRouter found two bugs that were never about OpenRouter
**Visibility:** EN visible, BG hidden (the house default)
**Image:** none.

**Excerpt (EN):** OpenRouter speaks OpenAI's wire format, so adding it cost no new dialect — which is
the point. What it did cost was three things that were never the dialect, and reading their
documentation instead of assuming turned up two bugs that had been true of every OpenAI-compatible
upstream since 2.4.

---

## Content (EN) — HTML, entity-escaped at `create_post` time

```html
<p>3.29 turned the coordinator's single anonymous cloud upstream into a map of named providers. 3.30
adds the first new one, and it is deliberately the cheapest possible: OpenRouter <em>is</em> OpenAI's
wire format, so the same client object serves both and no second dialect was written.</p>

<p>Which raises a fair question. If you could already point an <code>openai-compatible</code> provider
at <code>https://openrouter.ai/api/v1</code> and have it work — and you could — what is the release
for?</p>

<p>For everything about them that is <em>not</em> the dialect.</p>

<pre><code>"Providers": {
  "openrouter": {
    "Type": "openrouter",
    "Referer": "https://mesh.example.com",
    "Title": "Example mesh",
    "ModelMap": { "big-code": "qwen/qwen3-coder",
                  "fast": "~openai/gpt-mini-latest" }
  }
}</code></pre>

<h2>Three things a hand-typed base URL cannot buy</h2>

<p>The first is the base URL itself, which you now do not type. Set <code>BaseUrl</code> anyway if you
reach OpenRouter through a proxy of your own — a default that cannot be replaced is a wall, not a
convenience.</p>

<p>The second is that your model map is checked at boot. Every OpenRouter model id is
<code>vendor/model</code>, optionally prefixed with <code>~</code> for a floating alias and suffixed
with <code>:free</code>, <code>:batch</code> or <code>:thinking</code>. So
<code>"fast": "gpt-4o-mini"</code> is a plausible-looking line that cannot work: that is a real OpenAI
id and has never been an OpenRouter one. Left to run, it is a 400 you discover weeks later, on the one
request your fleet could not serve — which is the worst possible moment to find out. Now it is a
startup failure that names the value and the shape.</p>

<p>It is a <em>shape</em> check and nothing else. Validating the map against OpenRouter's live model
listing was the obvious alternative and it makes booting your hub depend on a vendor being up; a
checked-in list of vendor names is a guess that is usually right, which is the worst kind. The risk
the shape check leaves is real and worth stating: the day OpenRouter ships an id with no slash in it,
this refuses a valid configuration. That is a one-line fix behind an error message that says what it
wanted.</p>

<h2>The third one is the interesting one</h2>

<p>OpenRouter accepts two optional headers, <code>HTTP-Referer</code> and
<code>X-OpenRouter-Title</code>. They identify your app — on OpenRouter's <strong>public</strong>
rankings pages.</p>

<p>It would have been easy, and mildly useful to us, to fill those in with InferHub's own name and
URL by default. Every deployment that configured an OpenRouter provider would then have quietly
contributed to a public leaderboard entry for this project.</p>

<p>That is free marketing paid for with somebody else's infrastructure showing up on a vendor's
public page because they configured a model. So both fields are plain configuration, absent by
default, and nothing is sent when they are absent. If you want to be listed, say so.</p>

<h2>Two bugs, and neither was about OpenRouter</h2>

<p>This project has a rule for adding a vendor: read their current documentation on the day, do not
assume the compatibility layer holds. Twice before, that turned up facts that contradicted our own
first draft. It did again.</p>

<p><strong>An error code is a number there and a string at OpenAI.</strong> OpenAI writes
<code>"code": "rate_limit_exceeded"</code>; OpenRouter writes <code>"code": 429</code>. Our error
envelope declared that field as a string, so parsing the whole envelope threw, the code caught its own
exception, and fell back to printing the raw response body. The one sentence telling you what to fix —
<em>"Rate limit exceeded: free-models-per-day"</em> — arrived buried in the JSON it came in.</p>

<p>This project already has a decision about exactly that failure, written when a llama.cpp refusal
arrived through Ollama double-encoded and a client read a wall of backslashes instead of a sentence.
The same failure had been reachable by a different route this whole time.</p>

<p><strong>An error that arrives after the response headers ended the stream quietly.</strong> Once a
streaming response has started, a failure cannot come back as a status code; it comes as a frame in
the stream. OpenRouter sends one carrying a top-level <code>error</code> and
<code>finish_reason: "error"</code>. Our parser read that as an ordinary terminal chunk — so a request
that died at token 40 returned <strong>200, and looked finished</strong>. A truncated answer presented
as a complete one is the worst shape a failure can take, because nobody goes looking.</p>

<p>It now raises mid-stream, which both callers already knew how to handle: the hub writes a terminal
error chunk, and a node running against an upstream engine carries it back as a failed job. Two
existing contracts reused, none added.</p>

<p>Both bugs were live for <em>every</em> OpenAI-compatible upstream since 2.4 — vLLM, LM Studio, TGI,
OpenAI itself. Both are fixed for all of them. Adding a vendor found them; neither belonged to that
vendor.</p>

<h2>Said out loud</h2>

<p>No live provider was called by anything in this release. Every dialect in this track is tested
against recorded payloads — a captured 429 with a numeric code, a captured mid-stream error frame —
because a test that needs somebody's API key is a test CI cannot run, so it becomes a test everyone
learns to skip, and it bills a card on every commit. Both fixes were confirmed to <em>fail</em>
without the fix before being kept. The real keys come out once, by hand, on the verification day that
closes the track.</p>

<p>Nothing reads OpenRouter's <code>cost</code> field, which they return on every response. Per-token
cost accounting needs a price table, and a number this hub did not measure does not belong in the same
column as ones it did. Token counts <em>are</em> read and land in the ledger, because the dialect
never stopped reading them.</p>

<p><code>openrouter/auto</code> works, and the hub cannot tell you which model actually answered — it
reports the name you asked for. Relatedly, OpenRouter's own choice of which host serves a given model
is the vendor's routing and not this hub's; nothing here turns it on or off.</p>

<p>And a deployment that changes no configuration behaves byte-identically to 3.29.</p>

<p>Zero new dependencies. Next is Anthropic's own <code>/v1/messages</code> — typed SSE events,
<code>system</code> at the top level, hand-rolled — and a provider that declares <code>chat</code> and
no <code>embed</code>, which the routing seam has already known how to answer since 3.8.</p>
```
