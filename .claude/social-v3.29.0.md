# Social copy — v3.29.0

Post by hand. Blog post: <https://blog.devart.solutions/blog/inferhub-3-29-the-upstream-gets-a-name>
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.29.0>

---

## Facebook

InferHub 3.29 is out.

Since 2.4 the coordinator has been able to forward a request to a cloud upstream when the fleet had
nothing to serve a model — your GPU box being asleep becomes degradation rather than an outage. One
upstream. One base URL, one key, one model map, and no name for any of it.

That works right up until you have two.

An OpenAI key and an OpenRouter key on the same hub could not be expressed at all. Neither could the
ordinary wish to send one model to one vendor and another model somewhere cheaper. 3.29 is that
feature with an id on each one: a map of named providers, each with its own credential, its own
models and its own trigger.

The decision worth reading is what happens when two of them claim the same model.

It fails startup. Named, both of them, in the error.

Taking the first is what most gateways do, and it is the wrong default here: it makes the single most
consequential choice in this thing — whose servers see somebody's prompt — depend on the order your
JSON keys happened to bind in, through three layers of configuration. The other tempting reading,
that a duplicate means "try the second one if the first refuses", is worse. Retrying a prompt at a
second vendor is a second disclosure of that prompt. That deserves an argument, not a typo.

Two more things, said plainly.

**No live provider was called** by anything in this release. Every dialect in this track is tested
against recorded payloads, because a test that needs somebody's API key is a test CI cannot run — so
it becomes a test everybody learns to skip, and it bills a card on every commit. The real keys come
out once, by hand, on the verification day that closes the track.

**A deployment that changes no configuration behaves byte-identically to 3.28** — same header value,
same status payload, same metric names. The old config section is not kept beside the new path; it is
projected onto it, so there is one dispatch path rather than two. The branch nobody is developing is
the branch that keeps working right up until it silently does not.

This is the first of eight. OpenRouter next, then Anthropic and Gemini each speaking their own
dialect — hand-rolled, because the entire dependency surface of this coordinator is three
feature-scoped packages and we are not adding two SDKs to send JSON over HTTPS. Then providers a
request can be *routed to* rather than fall into, a console for all of it, the same four reachable
from a node, and a day where the real keys come out.

Zero new dependencies. There will be none in the other seven either.

---

## X / Twitter

**Thread**

1/ InferHub 3.29: the cloud upstream gets a name — and there can be more than one.

Since 2.4 there was exactly one. One base URL, one key, one model map, no name.

That works right up until you have two.

2/ Now it is a map:

"openai":     { llama3 → gpt-4o-mini }
"openrouter": { big-code → qwen/qwen3-coder }

Each with its own credential, its own models, its own trigger. Keys in the environment, never in the
file.

3/ The decision worth reading: two enabled providers mapping the same model does not take the first.

It fails the host at startup, naming the model and both providers.

4/ Taking the first is what most gateways do.

It makes the most consequential choice in the product — whose servers see somebody's prompt — depend
on the order your JSON keys happened to bind in, through three layers of config binding.

5/ The other tempting reading is worse.

"A duplicate means try the second if the first refuses" = a second disclosure of the same prompt to a
second vendor.

That deserves an argument. Not a typo.

6/ The old `Fallback:` section did not get a second code path.

It is projected onto a provider at resolve time. One dispatch path.

The branch nobody is developing is the branch that keeps working right up until it silently does not.

7/ So the existing suite — mostly a suite about when an upstream must NOT fire — now runs against the
new code, and is exactly the assertion that nothing changed.

Same header value. Same status payload. Same metric names.

8/ No live provider was called by anything in this release.

A test that needs somebody's API key is a test CI cannot run, so it becomes a test everyone learns to
skip — and it bills a card on every commit.

Recorded payloads. Real keys once, by hand, at the end of the track.

9/ First of eight: OpenRouter, then Anthropic's /v1/messages and Gemini's :generateContent in their
own dialects, hand-rolled.

Then routing to a provider by preference rather than by failure.

Zero new dependencies — and none in the other seven either.

---

## The one-liner, if only one thing gets posted

Two providers claiming the same model now fails startup instead of picking one, because whose servers
see your prompt is not a thing to decide by JSON key ordering — and reading a duplicate as a failover
pair would make a second disclosure of that prompt arrive by typo.
