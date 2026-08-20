# Blog post for v3.29.0

**Slug:** `inferhub-3-29-the-upstream-gets-a-name`
**Title (EN):** InferHub 3.29 — the cloud upstream gets a name
**Visibility:** EN visible, BG hidden (the house default)
**Image:** none.

**Excerpt (EN):** Since 2.4 InferHub has had exactly one cloud upstream and it has had no name.
3.29 turns it into a map of named providers — each with its own key, its own models and its own
trigger — and refuses to start if two of them claim the same model.

---

## Content (EN) — HTML, entity-escaped at `create_post` time

```html
<p>Since 2.4 this coordinator has been able to forward a request to a cloud upstream when the fleet
could not serve it. One upstream. One <code>BaseUrl</code>, one <code>ApiKey</code>, one
<code>ModelMap</code>, and no name for any of it.</p>

<p>That works right up until you have two. An OpenAI key and an OpenRouter key on one hub could not
be expressed at all, and neither could the ordinary wish to have <code>llama3</code> burst to one
place and a coding model burst to another. 3.29 is that feature with an id on each one.</p>

<pre><code>{
  "Providers": {
    "openai":     { "BaseUrl": "https://api.openai.com/v1",
                    "ModelMap": { "llama3": "gpt-4o-mini" } },
    "openrouter": { "BaseUrl": "https://openrouter.ai/api/v1",
                    "Trigger": "no-node-or-saturated",
                    "ModelMap": { "big-code": "qwen/qwen3-coder" } }
  }
}</code></pre>

<p>Keys go in the environment, never in the file. Everything the older feature promised still holds:
off unless configured, only models you mapped are ever eligible, nothing is stored, and every
response says where it came from.</p>

<h2>One model, one provider — and startup fails otherwise</h2>

<p>Two enabled providers mapping the same model does not resolve to the first one. It fails the host
at startup, naming the model and both providers.</p>

<p>Taking the first is what most gateways do, and it is the wrong default here. It makes the single
most consequential choice in this product — whose servers see somebody's prompt — depend on the order
your JSON keys happened to bind in, through three layers of configuration. The other tempting
reading, that a duplicate means "try the second if the first refuses", is worse: retrying a prompt at
a second vendor is a second disclosure of that prompt, and a decision like that should be argued for
on purpose rather than arriving as the side effect of a typo.</p>

<p>An unknown provider <code>Type</code> fails startup too, naming the ones that exist. A provider
nobody could understand must not quietly become a request that fails hours later in front of a
user.</p>

<h2>The old section did not get a second code path</h2>

<p>A hub still configured through the original <code>Fallback:</code> block is not running last
release's code beside this one. That block is projected onto a provider at resolve time, so there is
exactly one dispatch path — the branch nobody is developing is the branch that keeps working right up
until it silently does not. The existing test suite, which is mostly a suite about when an upstream
must <em>not</em> fire, now runs against the new code and is precisely the assertion that nothing
changed: the same header value, the same status payload, the same metric names.</p>

<h2>What you can see</h2>

<p>A named provider tags its responses <code>X-InferHub-Served-By: provider:&lt;id&gt;</code>;
the legacy section still says <code>fallback</code>, because a header that quietly changes meaning is
worse than one with two spellings. <code>/api/status</code> grows a <code>providers</code> array —
present whether or not anything has been dispatched, so "is this thing sending my prompts anywhere"
is still a question the status page answers rather than one you go and read a config file for. Its
<code>credential</code> field reads <code>configured</code> or <code>absent</code>, and never a
character of the key. <code>/metrics</code> grows a per-provider counter beside the existing total,
which still counts every request the fleet did not serve.</p>

<h2>What this is the first of</h2>

<p>Underneath sits a small interface: Ollama JSON in, Ollama JSON out, five methods. One
implementation today. Anthropic's own <code>/v1/messages</code> is 3.31 and Gemini's
<code>:generateContent</code> is 3.32 — both hand-rolled, because the whole dependency surface of
this coordinator is three feature-scoped packages and we are not adding two SDKs to send JSON over
HTTPS. Routing a model to a provider <em>by preference</em>, while the fleet is up, is 3.33.</p>

<p>Two things worth saying plainly. First: <strong>no live provider was called</strong> by anything
in this release. Every dialect in this track is tested against recorded payloads, because a test that
needs somebody's API key is a test CI cannot run and everyone learns to skip — and it would bill a
card on every commit. The real keys come out once, by hand, on the verification day that closes the
track. Second: a deployment that changes no configuration behaves byte-identically to 3.28, headers
and status payload included. Zero new dependencies, and there will be none in the seven releases
after this one either.</p>

<p>Release notes and the docs section are on GitHub and
<a href="https://inferhub.devart.solutions/#idocs_providers">inferhub.devart.solutions</a>.</p>
```

---

## Facebook

InferHub 3.29 is out.

Since 2.4 the coordinator could burst to a cloud upstream when the fleet had nothing to serve a
model. One upstream, with no name. 3.29 makes it a map of named providers — each with its own key,
its own models, its own trigger — so an OpenAI key and an OpenRouter key can live on one hub.

The decision worth reading: two providers mapping the same model **fails startup** instead of taking
the first one. Which vendor receives somebody's prompt is not a thing this hub will decide by JSON
key ordering. And reading a duplicate as a failover pair would be a second disclosure of the same
prompt, arriving by typo.

A deployment that changes no config behaves byte-identically to 3.28. Zero new dependencies.

First of eight: OpenRouter, Anthropic and Gemini each in their own dialect, then routing to a
provider by preference rather than by failure.

https://inferhub.devart.solutions

## X

InferHub 3.29: the cloud upstream gets a name — and there can be more than one.

Two providers mapping the same model doesn't take the first. It fails startup, naming both.

Whose servers see your prompt is not a thing to decide by JSON key ordering.

Zero new dependencies. 1/8.

https://inferhub.devart.solutions
