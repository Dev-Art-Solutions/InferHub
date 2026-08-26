# Social copy — v3.35.0 (phase 67, the node speaks all four dialects)

**Status: unposted.** No connector — posted by hand. **No image**: the honest picture of this release
is a four-line config file, and a screenshot of JSON is not a picture.

Blog post: <https://blog.devart.solutions/blog/inferhub-3-35-a-node-with-no-gpu>
(ID 6a8e30d041130ded903c0620, EN-visible / BG-hidden)
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.35.0>

---

## Facebook

InferHub 3.35 is out, and it ends a small absurdity.

Six releases taught the coordinator to speak four cloud vendors — OpenAI, OpenRouter, Anthropic and
Gemini, the last three each through their own hand-rolled dialect, at zero new dependencies. All of
that code lives in the shared library that the *node* also compiles against. And the node could only
reach two of them, because `Backend:Type` was `ollama` or `openai`.

So a machine with no GPU that you wanted to run as *a node backed by Claude* could not be configured
at all — while the exact client it needed sat compiled into the same binary.

Now it can:

    { "Backend": { "Type": "anthropic" },
      "Upstream": { "MaxTokens": 4096,
                    "Models": { "Include": ["claude-sonnet-5"] } } }

That node joins your mesh, reports one model, and answers jobs your hub cannot tell apart from a box
with a card in it. Turn the coordinator off and the same process is a private, authenticated,
RAG-capable OpenAI-compatible front end to that vendor, out of one container.

**The part that took the thinking was refusing the obvious symmetry.** The tempting move is to give
the node the hub's provider map — same config, same policies, consistent. It is also a second router,
on the one host in this system that has never had one: two policy vocabularies, two steer headers,
two places a prompt's destination gets decided, and the day they disagree, a node quietly overruling
the hub that dispatched to it about which vendor sees somebody's text. So the asymmetry is the
design: **the hub chooses, the node serves.** Want two vendors? Configure them on the coordinator, or
run two nodes.

Two things doing it properly cost.

**The config section is `Upstream:` now, not `OpenAi:`.** A key called `OpenAi:AnthropicVersion` is
the kind of key somebody screenshots. The old section still binds and is projected onto the new one,
so nobody has to touch a file — but writing the same key in *both* with different values fails
startup and names both, because which upstream receives a prompt should not depend on which section a
configuration binder happened to apply last.

**And a backend now declares what it can do.** Anthropic publishes no embeddings API. Until this
release the node's capability declaration was a constant — chat and embed, always — so an
Anthropic-backed node would have claimed `embed`, the hub would have routed an embedding job to it,
and the caller would have got a 501 *after* the hop, from inside a failed job, wearing a 502. Now the
backend says which kinds it serves, and that request is a **503 naming the missing capability at the
coordinator, before anything moves.**

One more: a node on a cloud vendor with no allowlist **refuses to boot**. OpenRouter lists 419 model
ids; a node reporting its upstream's catalogue would be telling your router it can hold a
conversation with an image model, and the router would believe it — that is what a model report *is*.

Said out loud: no live vendor endpoint was called by anything in this release. Every assertion runs
against a stub or a payload recorded from the vendors' own docs. The published container *was* pulled
and driven the same evening — eleven checks, all passing, including that neither embedding refusal
ever reached the upstream. But the real keys come out on the next release, one per vendor, by hand.

Self-hosted, MIT, zero new dependencies for the fifteenth release running.

---

## X / Twitter (5 posts)

*Counted: 180 / 261 / 272 / 274 / 270. No backticks — X does not render them and they cost characters.*

**1/**
InferHub 3.35 is out.

Six releases taught the hub to speak four cloud vendors. The node could reach two of them — while the other two clients sat compiled into the same binary. 🧵

**2/**
A machine with no GPU that you wanted to run as "a node backed by Claude" could not be configured at all.

Backend:Type was ollama or openai. That's it. Now it's ollama, openai, openrouter, anthropic or gemini — the same code the hub drives, composed once more.

**3/**
The part that took thinking was refusing the obvious symmetry: giving the node the hub's provider map.

That's a second router, on the one host that never had one. Two vocabularies, two steer headers, and a node that can overrule the hub about which vendor sees your text.

**4/**
So: the hub chooses, the node serves.

And a backend now DECLARES what it can do. Anthropic publishes no embeddings API — so that node says chat, not embed, and an embedding request is a 503 naming the capability at the hub, before the hop. Not a 501 buried in a failed job.

**5/**
A node on a cloud vendor with no allowlist refuses to boot.

OpenRouter lists 419 model ids. A node reporting its upstream's catalogue is telling your router it can hold a conversation with an image model — and the router believes it. That's what a model report IS.

---

## Single-post X variant

*237 characters.*

InferHub 3.35: a node with no GPU in it.

Backend:Type is now ollama, openai, openrouter, anthropic or gemini — the hub's four dialects, composed on the node.

What it refused to become: a second router. The hub chooses, the node serves.
