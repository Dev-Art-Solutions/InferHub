# Social — v3.8.0

Post manually. Lead with the operator-visible consequence — "your embedding node stops getting chat
traffic" — not with the architecture.

## Facebook

InferHub 3.8: a node that says what it can do.

Until this release a node advertised one thing about itself — a list of model names — and the coordinator routed on that alone. That worked for three years because of an assumption nobody had written down: that every model on a node does the same kind of work.

It doesn't. A box holding only nomic-embed-text was a perfectly good candidate for a chat request naming that model. The router had no way to know otherwise, so it dispatched, and the error came back from the backend seconds later.

So routing now asks (capability, model) instead of just model. Your embedding-only node stops getting chat traffic.

The part I'd defend hardest is what it does NOT do: nothing guesses what a model is for. Ollama doesn't say, and inferring it from the name — "it has embed in it" — would be a lookup table that gets built, believed, and is wrong for somebody. InferHub already declined to keep a list of which models accept images, for the same reason: the model refuses and we forward the refusal.

So a node declares chat + embed over whatever its backend reports, and the one thing it can't work out for itself is one line of config:

  "Node": { "Capabilities": { "Disabled": ["chat"] } }

Subtractive only — you can narrow what a node is used for, you can't make it claim something it hasn't got. Disable both and it refuses to start, because a node routed for nothing is a machine burning power for nothing. Typo the name and it also refuses to start, rather than silently disabling nothing on a box whose owner thinks they just moved the traffic off it.

A capability nobody provides is a 503 with Retry-After, naming it — the same shape as "everyone is busy", because it's the same kind of fact. A model nobody holds is still the old 404. "Not found" must not start meaning "not right now".

Upgrading: nothing to do. A node that declares no capabilities is read exactly as it was before, so a 3.7 node against a 3.8 coordinator serves normally and a fleet upgrades one box at a time. The load-bearing test in the new suite isn't about the new behaviour at all — it's the one that pins the old.

Zero new dependencies, tenth phase running.

This is the first release of a track: routing had to learn that "which model" and "what kind of work" are two different questions before a node can run anything that isn't a language model. Next up — a tool runtime that lets a node drive a supervised subprocess, and then speech-to-text and text-to-speech.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.8.0

## X / Twitter

InferHub 3.8 — a node now says what it can *do*, not just which models it holds.

Routing asks (capability, model). Your embedding-only box stops getting chat traffic.

Nothing guesses what a model is for: Ollama doesn't say, and a name heuristic is a lookup table that's wrong for somebody. One subtractive key instead:

  Node:Capabilities:Disabled: ["chat"]

A capability nobody has → 503 + Retry-After. A model nobody holds → still the old 404.

A 3.7 node against a 3.8 hub routes exactly as before, so you upgrade one box at a time. Zero new deps.

First release of a track: next is a tool runtime, then STT/TTS.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.8.0
