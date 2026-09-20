# Social copy — v3.45.0 (phase 80: dedicated cross-encoder reranker)

Blog: https://blog.devart.solutions/blog/inferhub-3-45-a-reranker-that-is-not-a-chat-model
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.45.0

## X / Twitter

InferHub's reranker has always been a chat model asked to score passages it was never trained to
rank. v3.45 gives it a real cross-encoder instead — and running it for the first time deadlocked
every request, on a hazard no test suite could have caught.

https://blog.devart.solutions/blog/inferhub-3-45-a-reranker-that-is-not-a-chat-model

## Facebook / LinkedIn

**InferHub's reranker stops pretending to be a chat model — and running it once found a deadlock.**

Since v2.6, "rerank these results" meant prompting whatever chat model was already on the fleet with
a scoring prompt and parsing a JSON array out of its answer. It worked well enough to ship, but the
eval harness told on it: the same pass *hurt* retrieval with a weak model and was *perfect* with a
strong one, at ~90x the latency of the hybrid search it sits behind. A general chat model is not a
cross-encoder.

v3.45 adds the implementation the `IReranker` interface was designed for on day one: a dedicated
cross-encoder (`sentence-transformers`, BAAI's `bge-reranker` family) running as a tool worker — the
same supervised-child-process shape Whisper and Piper have used since v3.9/v3.10. Eight unit tests
covered the .NET side; none of them touch the actual Python worker, so it got run for real — and
deadlocked on the first request, every time. `faulthandler` pinned it to a native-library import
happening on a background thread while the main thread sat in a blocking read — a loader-lock
hazard, not a scoring bug, never hit by the two tool workers before it because they've only ever run
on Linux. The fix (import once, at boot, on the main thread) is also just better design.

After the fix: correct rankings, a bad-model request refused by name exactly as the .NET fallback
expects, a cached model answering in 26ms. Zero new .NET dependencies — the one new dependency is
Python. What's still open, named rather than implied: no live run through the full coordinator+node
stack yet.

https://blog.devart.solutions/blog/inferhub-3-45-a-reranker-that-is-not-a-chat-model
