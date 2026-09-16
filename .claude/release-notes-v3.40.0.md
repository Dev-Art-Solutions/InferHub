# InferHub v3.40.0 — a hub query fans out to several corpora and fuses the results

Phase 75. `POST /api/retrieve/federated` answers a query against several collections in one call —
hub-owned and node-owned, mixed — instead of one request per collection merged by hand. Phase 44's
own non-goal named this outright: "a hub query that fans out to several node-owned corpora and fuses
the results is a whole track." This is that track, kept to one phase because it turned out to need no
new authority model, no new dispatch path and no new store: it is a caller of the paths `/search`
already has.

## What's new

- **`POST /api/retrieve/federated`** — same query shape as `/api/collections/{c}/search`, plus a
  `collections` array. Each name is searched by the exact single-collection path: hub-owned through
  `RetrievalPipeline`, node-owned dispatched to its owner (44 D5) — nothing about how one collection
  is searched changed.
- **Cross-collection ranking by Reciprocal Rank Fusion**, keyed by `(collection, id)` rather than the
  bare id `HybridSearch.Fuse` already uses within one collection — two unrelated collections' chunk
  `"1"` are different records and must not silently merge into one scored entry.
- **A `sources[]` array names every collection's outcome** — `ok`, `timeout`, `unavailable` or
  `not_found` — with a match count and elapsed time. A federated answer never says less than it knows:
  "3 matches, one source timed out" is a real answer, and hiding that would make partial coverage look
  like completeness.
- **A collection outside the caller's scope reports `not_found`, identical to a genuinely missing
  one** (31 D3's principle, applied per name). An owner that is offline reports `unavailable` and is
  never silently answered from the hub's own store of the same name (31 D4's failure mode).
- **A 400 above 16 collections per call.** Arbitrary and generous for the ranking use case this is
  for; fan-out otherwise has no bound and a client could turn a read into an N-way thundering herd.

## What was not established

- **No load test.** The per-collection budget (4s default) and the 16-collection ceiling are
  reasoned defaults, not numbers measured against a real fleet under concurrent federated load. If a
  deployment's nodes answer slower than 4s under their own normal load, every federated call to them
  will read as `timeout` even though a direct `/search` would have succeeded — that trade has not been
  measured, only argued.
- **No de-duplication of identical content across collections.** Two corpora holding the same
  document produce two entries; this was named a non-goal in the brief rather than an oversight, but
  it means a federated result can look redundant in a way a single-collection one never does.
- **This phase is v3.40.0, but the previous release (v3.39.0, phase 74 — per-node model routing and
  vector-collection assignment) shipped without a `.claude/release-notes-v3.39.0.md` of its own.** That
  gap predates this phase and is called out here rather than papered over by silently backfilling one
  after the fact from memory of what the phase "must have" done.

## Verification

`dotnet test tests/InferHub.Tests.Coordinator --filter FullyQualifiedName~FederatedFusionTests` (4/4)
and `dotnet test tests/InferHub.Tests.Mesh --filter FullyQualifiedName~FederatedRetrievalTests` (4/4)
both green — real fan-out across a hub-owned and a node-owned collection over a real Kestrel host,
scope exclusion, a disconnected owner reported rather than papered over, and the RRF keying pinned
against the collision it exists to prevent. `dotnet build InferHub.sln` succeeds. **The rest of the
solution's test slice was not run for this release** — per the release instruction, only the slice
this phase touched.
