# Social copy — v3.34.0 (phase 66, the provider console)

**Status: unposted.** No connector — posted by hand. **No image**: the honest picture of this
release is a console panel with one sentence in it, and a screenshot of an empty table needs a
caption to make sense.

Blog post: <https://blog.devart.solutions/blog/inferhub-3-34-the-panel-that-stays-visible>
(ID 6a8d85c226fd013b491931c9, EN-visible / BG-hidden)
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.34.0>

> **One thing to clean up in the blog admin UI:** the first attempt at this post went out with its
> HTML escaped, so its tags are visible as text. It is live at slug
> `inferhub-3-34-the-panel-that-stays-when-empty` (ID 6a8d857026fd013b49193193) and the connector
> has no delete — **it needs deleting by hand.** The readable one is the slug above.

---

## Facebook

InferHub 3.34 is out, and it ships no new capability at all.

Since 3.29 the mesh can name cloud vendors — OpenAI, OpenRouter, Anthropic, Gemini, the last three
each speaking their own hand-rolled dialect. Since 3.33 a vendor can be the *first* place a model
goes rather than what happens when your fleet fails. Five releases of work, and **none of it was on
the console.** You could read it in an admin-gated JSON payload, or in two metric series that only
show up *after* traffic has already left — so "is this hub configured to send prompts to a vendor"
and "has it" were the same silence.

The failure path was worse. A wrong key, a typo'd base URL or a vendor having a bad afternoon
produced a warning in a log on a host nobody is tailing and a 502 in front of a user. Nothing
counted it, nothing kept the sentence the vendor said — and under a `prefer` policy, a failing
provider looks exactly like a hub answering everything from its own fleet, which is also what a
*working* hub looks like.

So: a **Cloud providers** panel. One row per place a prompt can go — the policy, whether a credential
is set, the models it claims, dispatches, failures, and the upstream's own words on the last one.
*Incorrect API key provided.* is the difference between a minute and an afternoon.

**And it is the one panel on that page that stays visible when it is empty.** A hub with no provider
renders one line: *No cloud provider is configured — nothing leaves your machines.* Every other panel
hides when it has nothing to say; this one must not, because that sentence **is** the feature. A
panel that vanishes when the answer is the reassuring one teaches you to read absence as "I could not
tell", and for cloud burst that is the one state it may never be in.

Two more decisions worth repeating:

**A missing credential is a row, not a startup failure.** An openai-compatible endpoint on your own
network legitimately has no key, so refusing to boot would break a supported deployment to catch a
typo. Against a real vendor it is the purest "I turned it on and nothing happened" — a 401 on the
first prompt, hours later, in front of a user. So it sits on the needs-attention strip instead.

**One new metric series describes rather than measures.** This project refuses a zero counter for a
feature nobody switched on, because a zero reads as traffic that happened. `inferhub_provider_info`
is a constant 1 with labels — it measures nothing, it states configuration, and it is what makes the
*absence* of a dispatch counter readable. Without it, "no vendor configured" and "a vendor configured
that has served nothing" are the same empty scrape, and those are opposite answers.

Said out loud: no test here calls a live provider, deliberately. The failures asserted are a stub
answering 401 and a refused TCP connection, and nobody has opened the panel against a real vendor in
a browser. That is the verification day at the end of this track.

Self-hosted, MIT, zero new dependencies for the fourteenth release running.

---

## X / Twitter (5 posts)

*Counted: 183 / 233 / 254 / 274 / 259. No backticks — X does not render them and they cost characters.*

**1/**
InferHub 3.34 is out.

Five releases teaching a self-hosted mesh to speak four cloud vendors — and none of it was on the console.

The panel we built has a state most panels don't. 🧵

**2/**
Everything about your cloud providers lived in an admin-gated JSON payload, plus two metric series that only appear AFTER traffic has left.

So "is this hub sending prompts to a vendor" and "has it" produced exactly the same silence.

**3/**
The new panel stays visible when it's EMPTY.

A hub with no provider renders one line: "No cloud provider is configured — nothing leaves your machines."

Every other panel hides when it has nothing to say. This one must not: that sentence is the feature.

**4/**
A provider with no API key is a row on the needs-attention strip, not a startup refusal.

A local vLLM endpoint legitimately has no key. Against a real vendor it's the purest "I turned it on and nothing happened" — a 401 on the first prompt, hours later, in front of a user.

**5/**
And one new series measures nothing on purpose.

inferhub_provider_info is a constant 1. It states configuration — so a dashboard can tell "no vendor configured" from "a vendor that has served nothing".

Without it, both are an empty scrape. Opposite answers.

---

## Single-post X variant

*262 characters.*

InferHub 3.34: the provider console.

It's the one panel that stays on the page when it's empty — "No cloud provider is configured, nothing leaves your machines."

A panel that hides when the answer is reassuring teaches you to read absence as "I couldn't tell".
