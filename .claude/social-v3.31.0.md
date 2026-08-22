# Social copy — v3.31.0 (phase 63, Anthropic's own dialect)

**Status: unposted.** No connector — posted by hand. No image: the visual would be a screenshot of
a JSON body.

Blog post: <https://blog.devart.solutions/blog/inferhub-3-31-the-counts-were-cumulative> (ID 6a88eb260c6b951079f18622, EN-visible / BG-hidden, one create_post, no connector outage)
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.31.0>

---

## Facebook

InferHub 3.31 is out, and it is the release where reading the vendor's documentation was the whole job.

You could always point InferHub at Anthropic's OpenAI-compatibility endpoint. What you got back was
that layer's idea of your usage: Anthropic reports four token counts, the shim flattens them into
two, and then "why does my usage page disagree with my invoice" is the first question anybody asks.

So 3.31 speaks their own `/v1/messages` — hand-rolled, still zero new packages. Four wire facts came
out of the docs, and two of them contradicted our own plan:

• The streamed token counts are **cumulative**, and the very first event already reports one output
token before a token exists. A dialect that added the frames up would report 24 tokens for a
15-token answer. So they are taken, never summed.
• There is **no `[DONE]` sentinel** — the stream ends at `message_stop`, and an unknown event type
has to be skipped rather than fatal, because their versioning policy says new ones will arrive.
• `max_tokens` is **required** by that API and Ollama has no equivalent, so it is declared per
provider and your own `num_predict` always wins.
• An error arriving *after* the response headers is raised mid-stream instead of ending the response
as a 200 that looks finished.

No live provider was called by any test — every payload in the suite is a recorded one. The real
keys come out on the track's verification day, which is a phase of its own for exactly that reason.

Self-hosted, MIT, zero new dependencies for the eleventh release running.

---

## X / Twitter (4 posts)

**1/**
InferHub 3.31: we stopped using Anthropic's OpenAI-compatibility endpoint and wrote their own
`/v1/messages` dialect by hand.

The reason is the number. They report 4 token counts; the compat layer flattens them into 2. Then
your usage page disagrees with your invoice.

**2/**
Four wire facts came out of their docs. Two contradicted our own plan.

The streamed usage counts are *cumulative*. And the first event already says `output_tokens: 1`
before a token exists.

Sum the frames and you report 24 tokens for a 15-token answer. So: taken, never summed.

**3/**
Two more:

There is no `[DONE]` sentinel — the stream ends at `message_stop`, and an unknown event type must be
skipped, because their versioning policy says new ones are coming.

`max_tokens` is required and Ollama has no equivalent. So it's declared per provider; your
`num_predict` wins.

**4/**
No test in the repo calls a live provider. Every payload is a recorded one — a captured
`message_start`, a captured `overloaded_error`, a captured 400.

A test that needs somebody's API key is a test CI can't run, so it's a test everyone learns to skip.
The real keys come out once, on their own day.

MIT, self-hosted, zero new packages.

---

## X — single post (267 chars with the link counted as t.co's 23)

InferHub 3.31 speaks Anthropic's own /v1/messages, not their OpenAI-compat endpoint.

Their streamed token counts are cumulative — sum them and you report 24 tokens for a 15-token answer.

Taken, never summed. MIT, self-hosted, zero new deps.

https://blog.devart.solutions/blog/inferhub-3-31-the-counts-were-cumulative
