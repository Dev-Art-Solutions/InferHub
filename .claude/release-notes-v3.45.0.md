# InferHub v3.45.0 — a dedicated cross-encoder reranker, not a chat model asked to score

Phase 80. `IReranker` has had exactly one real implementation since v2.6: `LlmReranker`/`LocalReranker`
prompt a chat model already on the fleet with a scoring prompt and parse a JSON array out of its
free text. It works, but it costs a full chat round trip for a task the model was never trained for
— the eval table in `#idocs_hybrid` on the docs site shows exactly how high-variance that pass is,
from *hurting* retrieval with a weak model to being *perfect* with a strong one. The interface's own
doc comment has said since day one that the seam exists "so a dedicated cross-encoder... can slot in
later without touching the pipeline." This release does it.

## What's new

- **A real cross-encoder, not a prompted one.** `Retrieval:Rerank=cross-encoder` routes reranking to
  `sentence-transformers`' `CrossEncoder`, which loads BAAI's `bge-reranker` family directly, running
  as a **tool worker** — the exact shape Whisper and Piper already use (v3.9/v3.10), not a native
  binding inside the coordinator or the node.
- **No new HTTP route.** The generic `POST /api/tools/{capability}` (phase 41) already speaks
  JSON-in/JSON-out with no attachments, which is exactly this job's shape — a first for a tool worker
  in this project, since every one before it (audio, images) carries at least one file.
- **New `CapabilityKinds.Rerank`, and a new `CrossEncoderReranker`/`LocalCrossEncoderReranker` pair**
  on the hub and the solo node, dispatching a `ToolJob` (`{"query","documents"}` → `{"scores":[...]}`)
  instead of an `InferenceJob`. `RerankPrompt.Apply` — already generic, score-based reordering — is
  reused unchanged; every failure mode (no node, timeout, a wrong-length score array, a worker that
  does not recognise the model) returns the candidates untouched, the same contract the LLM path
  already followed.
- **Four models ship in the new `rerank` manifest**: `ms-marco-minilm-l6` (small, English, a
  reasonable CPU default), `bge-reranker-base`, `bge-reranker-large` and `bge-reranker-v2-m3`
  (multilingual). It lives in the existing `:tools` image alongside Whisper and Piper — no new image.
- **Off by default, zero new .NET dependencies (rule 5).** `Retrieval:Rerank` unset, `none`, or `llm`
  behaves exactly as it did before this release. The one new dependency, `sentence-transformers`, is
  Python — a line in `requirements-tools.txt` — and it is the first dependency in that file to pull
  in `torch`, by far the largest single addition the `:tools` image has taken since it existed.

## A real bug, found by actually running the worker

`rerank_worker.py` was driven directly through its real process protocol — `hello` → `ready`,
`request` → `result`/`error` — against a real `sentence-transformers` install, not left at "unit
tests pass." First run: **it deadlocked, every time, on the first request.** `faulthandler.dump_traceback(all_threads=True)`
pinned it exactly: the lazy `from sentence_transformers import CrossEncoder` inside `load()`, called
on the per-request thread `Worker._handle` spawns for every job, hung hard inside CPython's import
machinery loading numpy's compiled `multiarray` extension. The same import from an ordinary
background thread with nothing else going on completes in a few seconds; a main thread parked in a
blocking stdin read *plus* a second thread pulling in a large native extension for the first time is
what triggers it — a loader-lock class of hazard, not a scoring bug, and one Whisper/Piper have never
hit because the `:tools` image is Linux-only (this is very likely Windows-specific; unconfirmed on
Linux itself).

**Fix:** `sentence_transformers`/`CrossEncoder` moved to a top-level, main-thread import — paid once
at worker boot, before `Worker.run()` ever spawns a request thread — instead of lazily inside
`load()`. That sidesteps the whole hazard class rather than papering over one instance of it, and it
means the worker's `ready` handshake genuinely means ready rather than "ready, plus a surprise
multi-second import for whoever's first request."

After the fix, verified end to end against the real protocol: a real relevance score (a leave-policy
passage scored 7.46 against "how much annual leave," an unrelated one -11.23 — correct ranking), a
bad-model request refused `invalid_request` exactly as `CrossEncoderReranker`'s .NET-side fallback
expects, and a second call for an already-loaded model answered in 26 ms — cache hit, no reload.

## What was not established

- **Live verification through the full .NET stack** — a real coordinator + node + `:tools` image,
  `POST /api/tools/rerank` over HTTP, `Rerank=cross-encoder` compared against `Rerank=none` on a real
  collection. What was verified above is the worker's own process protocol directly; the .NET-side
  contract is unit-tested against a stub dispatcher (8 cases in `CrossEncoderRerankerTests.cs`,
  mirroring `RerankerTests.cs`'s coverage of `LlmReranker`) — the two have not yet been run wired
  together.
- **No node-side unit test for `LocalCrossEncoderReranker`** — matching an existing gap rather than
  opening a new one: `LocalReranker` itself has never had a direct unit test either (the solo-retrieval
  tests inject their own `NoReranker` stub instead of exercising the real implementation).

See `plan/phase-80-cross-encoder-reranker.md` for the full decision record (D1–D5).

## Verification

`dotnet build InferHub.sln` — clean. `dotnet test InferHub.sln` — full regression green:
`InferHub.Tests.Coordinator` 765/765 (up from 79's 757 — the 8 new cases), `InferHub.Tests.Node`
189/189, `InferHub.Tests.Mesh` 441/441. `rerank_worker.py` driven directly through its real protocol
against a real CPU install of `sentence-transformers` — see above.

**Published-image check:** pending — the `Dockerfile.tools` build-time import assertion now also
checks `sentence_transformers` and compiles `rerank_worker.py`, but the published `:tools` image
itself has not yet been pulled and exercised this release.
