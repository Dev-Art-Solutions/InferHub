# Social — v3.6.0

## Facebook

InferHub 3.6: RAG on one machine — and the release is more interesting for what it refuses to do.

3.5 let a single machine drop the coordinator entirely. It stopped one step short: a retrieval header got a clean 501, because the vector store lived in the hub. But the deployment that most wants RAG is exactly that one — a folder of documents and one GPU, not a fleet.

So a standalone node now ingests, indexes and grounds its own answers. Same headers, same augmented prompt, same X-InferHub-Sources citations a coordinator produces. Vector, keyword and hybrid modes, plus the optional reranker, and a /search endpoint so you can see what actually retrieves before you trust it.

Here is the part I'd argue is the real feature: turn retrieval on while the node is still in a mesh and it refuses to start, naming both keys.

A meshed node already holds vector replicas — derived copies of the hub's collections. Give that same process an authoritative corpus of its own and you have two vector authorities in one node: a locally ingested document invisible to the fleet, one collection name with two different sets of chunks under it, and replication that will overwrite the one you think you own. There is no safe configuration of that, so it isn't offered.

And it refuses rather than quietly switching the feature off — because what would be switched off is grounding. Silently ungrounded answers are confident, fluent, wrong, and produce a bug report three weeks later that starts "the model got worse".

None of the retrieval code was reimplemented, either. The whole pure core moved into the shared library and both hosts compose the same objects; a parity test drives the same corpus and question at a real hub and a real solo node over real HTTP and compares the prompt, the citations and the ranking.

No PDF on a node (a clean 415 — a bad extraction doesn't fail, it succeeds quietly and poisons the corpus), no external vector databases. Off by default. Still zero new dependencies, ninth phase running.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.6.0

## X

InferHub 3.6: RAG with no coordinator.

A standalone node now ingests, indexes and grounds its own answers — same headers, same augmented prompt, same citations as the hub. Vector / keyword / hybrid, plus reranking.

The best part is a refusal: turn it on while the node is still in a mesh and it won't start. A meshed node already holds replicas derived from its hub; a second authoritative corpus in the same process is two sources of truth for one collection name.

It refuses instead of quietly disabling, because what gets disabled is grounding — and silently ungrounded answers are the bug you find three weeks later.

The core moved into the shared library rather than being copied. No PDF on a node (415, never a bad extraction). Zero new deps.

https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.6.0

## LinkedIn / longer

Shipped InferHub 3.6 — retrieval on a standalone node.

The feature is straightforward: a machine with no coordinator can now ingest documents, index them and ground its own answers, with the same headers and the same citation format the coordinator uses.

The design decision I'd actually defend is the refusal. Retrieval requires the coordinator connection to be off, and turning both on fails startup. A node in a mesh already holds derived replicas of the hub's collections; an authoritative corpus in the same process would mean two sources of truth for one collection name. Refusing is the only way the invariant stays true — and refusing *loudly* matters, because the alternative failure is silent: a system that answers ungrounded looks exactly like a system that answers correctly, right up until someone checks.

The other half was resisting a copy-paste. Retrieval has a dozen decisions in it, and two hosts drifting on any of them produces plausible answers rather than errors. So the pure core moved into the shared library instead, and both hosts run the same objects — with a parity test that drives real HTTP at both and compares what a client actually receives.

Zero new dependencies, ninth release running.
