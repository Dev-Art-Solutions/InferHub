# Social copy — v3.45.0 (phase 80: dedicated cross-encoder reranker)

Blog: https://blog.devart.solutions/blog/inferhub-3-45-a-reranker-that-is-not-a-chat-model
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.45.0

## X / Twitter

InferHub's reranker has always been a chat model asked to score passages it was never trained to
rank. It worked, but the eval numbers show exactly how shaky that is.

v3.45: a real cross-encoder — BAAI's bge-reranker, running as a tool worker, same shape as
Whisper/Piper.

https://blog.devart.solutions/blog/inferhub-3-45-a-reranker-that-is-not-a-chat-model

## Facebook / LinkedIn

**InferHub's reranker stops pretending to be a chat model.**

Since v2.6, "rerank these results" meant prompting whatever chat model was already on the fleet with
a scoring prompt and parsing a JSON array out of its answer. It worked well enough to ship, but the
eval harness told on it: the same pass *hurt* retrieval with a weak model and was *perfect* with a
strong one, at ~90x the latency of the hybrid search it sits behind. A general chat model is not a
cross-encoder.

v3.45 adds the implementation the `IReranker` interface was designed for on day one: a dedicated
cross-encoder (`sentence-transformers`, BAAI's `bge-reranker` family) running as a tool worker — the
same supervised-child-process shape Whisper and Piper have used since v3.9/v3.10, not a native
binding. It's also the first InferHub tool worker whose request and answer are both plain JSON with
no file attached, which meant it needed *zero* new HTTP routes — the existing generic tool endpoint
already spoke exactly this shape.

Off by default, zero new .NET dependencies — the one new dependency is Python. What's still open,
named rather than implied: no live run against a real fleet end to end yet.

https://blog.devart.solutions/blog/inferhub-3-45-a-reranker-that-is-not-a-chat-model
