# InferHub v2.6.0 — Hybrid search, reranking & an eval harness

**Phase 24.** Pure vector search is excellent at "what is this about" and poor at "find the exact
thing I named" — ask for an error code, a SKU, or a surname and cosine similarity returns the general
topic, not the line you wanted. This release adds keyword search, hybrid (keyword + vector) retrieval
fused by rank, an opt-in reranker, and — because "this improved retrieval" is a claim, not a feeling —
an evaluation harness that measures whether any of it helped on your corpus.

Everything here is **per-request and off by default**: a deployment that sends no new headers and
changes no config behaves byte-identically to v2.5.

## What's new

- **Retrieval modes** via `X-InferHub-Retrieve-Mode: vector | keyword | hybrid` (an unknown value is a
  `400`, not a silent fallback).
  - `keyword` — classic BM25 over the same chunks. Provider-native: an in-memory inverted index under
    `local`, Postgres full-text (`tsvector` + GIN + `ts_rank_cd`) under `postgres`.
  - `hybrid` — runs both branches and fuses them by **Reciprocal Rank Fusion** (by rank, not by
    blending scores that live on different scales). This is the one you usually want.
- **Reranking** via `X-InferHub-Rerank: true` — an opt-in pass that hands the top candidates to a chat
  model already on your fleet with a scoring prompt and reorders them. It costs a round trip, so it is
  off unless asked, and hard-capped by `Retrieval:RerankCandidates` / `Retrieval:RerankTimeoutSeconds`;
  past the timeout the un-reranked order is kept. A dedicated cross-encoder fits behind the same seam
  later.
- **`POST /api/collections/{c}/search`** — a query playground endpoint: run a query in a mode and get
  the ranked chunks back directly. The admin console gains a panel that shows what every mode retrieves
  for a query, side by side.
- **`tools/InferHub.Eval`** — a standalone eval harness (not built into the images). Point it at a
  live coordinator with a golden set and it reports Recall@k, MRR, nDCG@k and median latency for every
  mode.

## Zero new dependencies

Keyword search is provider-native — `Npgsql` already reaches Postgres full-text, and the `local` BM25
index is a dictionary. The reranker reuses a model already on the fleet. Nothing was added.

## Defaults are unchanged

`Retrieval:Mode` defaults to `vector` and `Retrieval:Rerank` to `none`. No headers, no config: identical
to v2.5. A feature that silently changes existing results is a regression wearing better clothes, and
the test suite asserts the default equals vector-only — plus the exact-term case (an error code) that
vector search misses and hybrid recovers.

## Measured, not asserted

The blog post's numbers must come from the harness on a real corpus, run against a live coordinator with
an embedding fleet. This release ships the harness; the numbers get filled in from a real run (see the
harness README — and its warning that a golden set written by the model you are about to evaluate is a
mirror, not evidence).

## Config

New keys under `VectorStore:Retrieval:` — `Mode`, `CandidatesPerBranch`, `Rerank`, `RerankModel`,
`RerankCandidates`, `RerankTimeoutSeconds`. All optional; see `appsettings.json` and the README.

## Tests

457 total (442 passed, 15 skipped — the gated Postgres integration tests, as always). New coverage:
`InvertedIndexTests`, `HybridSearchTests`, `RerankerTests`, the mode/rerank pipeline tests including the
byte-identical-default regression, and Postgres keyword SQL shape tests. Verified live end-to-end against
a real coordinator: keyword retrieval through the HTTP surface (header → pipeline → BM25 →
`X-InferHub-Sources`), vector/hybrid degrading cleanly to `424` without an embedding node, and mode
validation returning `400`/`404`.
