# Social copy — v3.32.0 (phase 64, Gemini's own dialect)

**Status: unposted.** No connector — posted by hand. No image: the visual would be a screenshot of
two token counts.

Blog post: *(filled in after publishing)*
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.32.1> — **point people at
3.32.1, not 3.32.0.** Running the published 3.32.0 image found a real defect (a streamed response
that is not SSE finished as an empty answer) and a docs overstatement (Gemini embeddings are
implemented but the hub does not route to them until 67). Both fixed/corrected the same evening.

---

## Facebook

InferHub 3.32 is out. It adds Gemini's own `:generateContent` dialect — and reading Google's
documentation on the day turned up something the plan had wrong: that endpoint is now their
**legacy** one.

Their newer Interactions API is GA and recommended for new projects. `generateContent` "remains
fully supported", with no removal date published. We targeted it anyway, deliberately, and the
reasons are worth saying out loud:

• It is the surface Vertex AI and every gateway in between also speak, so a `BaseUrl` override still
reaches them.
• What Interactions adds — a `steps[]` of thoughts and tool calls, and server-side conversation
state — is exactly the stateful, agentic behaviour InferHub does not do. We would pay for the richer
surface and throw away the part that makes it rich.
• It changed its own schema under a dated header three months ago. That is a poor thing to pin a
dialect to when your tests are recorded payloads.

What the native dialect actually buys is the numbers, and this is the part that surprised us:

**Cached tokens live *inside* Gemini's prompt count and *beside* Anthropic's.** Google counts cached
content within `promptTokenCount`; Anthropic reports it as a separate number next to the input
count. So the identical-looking "just add them up" is wrong in opposite directions — it
double-counts one vendor and invents a total for the other. Neither is adjusted. Each dialect
reports what its vendor calls the prompt, and that is now a written rule rather than a coincidence.

**Thinking tokens are billed as output but are not in the answer.** Gemini models think by default,
and report the thinking separately from what they said. `eval_count` carries the answer's tokens
alone, because that is what a client reading that field means — so your invoice is larger than that
number, and there is a `ThinkingBudget` knob to close the gap rather than a sum we invented.

Three more, each found by reading rather than guessing: streaming usage is cumulative (second vendor
running — so "take a snapshot, never sum" is now the house rule); `?alt=sse` is not optional,
because without it the endpoint answers with a JSON array rather than events at all; and a prompt
the safety filters refuse arrives as a **200 with no candidates**, which is the third success status
in this track that is not one.

Gemini also has embeddings and the dialect speaks them — though the hub does not route anything to a
provider's embed endpoint yet; that comes with the node-side providers.

Then we pulled the published container and drove it, which is how every release here ends. Three
checks passed on the artefact: 165 not 305, 7 not 19, 15 not 26. The fourth failed — a refused
prompt delivered as a plain body on the *streaming* endpoint came back as an empty answer marked
finished, because our reader skips anything that is not a `data:` line and that body had none. It is
the same "success that isn't one" the release spends a section refusing, arriving through a door we
had not checked. Fixed in **3.32.1** the same evening, along with a sentence of ours it proved wrong:
we had written that a non-SSE body would make the reader hang until the timeout. It did not hang. It
answered immediately and wrongly, which is worse.

No live provider was called by any test — every payload in the suite is a recorded one. The real
keys come out on the track's verification day, which is a phase of its own for exactly that reason.

Self-hosted, MIT, zero new dependencies for the twelfth release running. Take 3.32.1.

---

## X / Twitter (5 posts)

**1/**
InferHub 3.32 adds Gemini's own `:generateContent` dialect.

Reading their docs on the day found something our plan had wrong: it is now the **legacy** endpoint.
Their Interactions API is the recommended one.

We targeted the legacy one anyway. On purpose. 🧵

**2/**
Why: Interactions is a stateful, agentic surface — `steps[]` of thoughts and tool calls, server-side
conversation state.

That is exactly what a hub that retains nothing does not do. We'd pay for the richer API and discard
what makes it rich.

`:generateContent` is also what Vertex speaks.

**3/**
The fact that made the phase worth it:

Cached tokens sit *inside* Gemini's prompt count and *beside* Anthropic's.

So "just add them up" is wrong in **opposite directions** — double-counting one vendor, inventing a
total for the other.

Neither is adjusted.

**4/**
Then we ran the published image, as always.

Three checks passed. The fourth: a refused prompt on the streaming endpoint came back as an *empty
answer marked finished* — our reader skips non-`data:` lines and that body had none.

Same bug the release refuses, through a door we hadn't checked. Fixed in 3.32.1.

**5/**
It also proved one of our own sentences wrong.

We'd written that a non-SSE body would make the reader hang until timeout. It didn't hang — it
answered instantly and wrongly. Worse.

Corrected everywhere it was written. Take 3.32.1. Zero new deps, 12 releases running.

---

## Single-post X variant

InferHub 3.32 speaks Gemini's own `:generateContent` — and reading Google's docs found it is now
their *legacy* endpoint. Targeted deliberately: Interactions is the agentic, stateful surface a hub
that retains nothing doesn't do.

Best fact: cached tokens live *inside* Gemini's prompt count and *beside* Anthropic's. Same addition,
wrong in opposite directions. So neither is adjusted.
