# Social — v3.5.0

## Facebook

InferHub 3.5: sometimes you just want the node.

Until now, putting an OpenAI-compatible endpoint in front of your local Ollama meant running a coordinator too — a URL, an enrollment secret, two processes — so that one client could reach one backend. On a single machine that is all ceremony and no routing.

Now the node serves the same API itself. Moving between them is one line:

base_url="http://hub:5080/v1" → base_url="http://localhost:5081/v1"

Same bodies, same responses, same streaming, same errors — both the OpenAI and Ollama dialects, including tool calls and vision. No coordinator, no secret, no internet needed.

Keeping the two from drifting is the part that needed care: everything that turns a result into text is shared code, and a parity test drives identical requests through both hosts over real HTTP and compares what the client actually receives. We broke one side on purpose to check it fails.

No retrieval in solo mode — a retrieval header gets a clean 501 rather than a silently ungrounded answer, which is the failure that would otherwise show up three weeks later as "the model got worse".

Off by default, loopback when on, and it refuses to serve a LAN without an API key. Nothing about the fleet changes. Still zero new dependencies.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.5.0

## X

InferHub 3.5: run just the node.

No coordinator, no enrollment secret, no internet. The node serves the same OpenAI + Ollama API the hub does, so moving is one line:

base_url hub:5080/v1 → localhost:5081/v1

Same streaming, same errors, both dialects — guarded by a parity test over real HTTP so the two hosts can't drift.

Off by default, loopback when on. No RAG in solo (501, never a silently ungrounded answer). Zero new deps.

https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.5.0
