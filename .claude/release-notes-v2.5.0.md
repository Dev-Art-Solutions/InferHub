# InferHub v2.5.0 — Document ingestion pipeline

**Phase 23.** The vector store has existed since v1.5 and inline retrieval since v2.0. What InferHub
never had was a way to *fill* the store with anything but pre-computed vectors or hand-pasted text.
That made RAG a primitive rather than a feature. This release closes it: documents in — text,
Markdown, HTML, JSON, PDF text layer — chunks and embeddings out, embedded on the GPU fleet through
the dispatcher that already exists.

## What's new

- **`POST /api/collections/{c}/documents`** — upload via `multipart/form-data` or JSON. The
  coordinator extracts the text, chunks it (paragraph → sentence → character, with overlap), embeds
  the chunks on the fleet, and upserts them into the vector store.
- **`GET /api/collections/{c}/documents`** — list documents: chunk count, bytes, content hash,
  ingested-at, status.
- **`GET /api/collections/{c}/documents/{id}/chunks`** — the chunks in order, with page numbers.
- **`DELETE /api/collections/{c}/documents/{id}`** — removes every chunk of that document.
- **Console: a Documents panel.** Pick a collection, drop a file, watch the chunk count climb,
  preview chunks, delete a document.
- **`/api/status`** gains a per-collection `ingestion` block: documents ingested, chunks embedded,
  failures, last ingest, embedding model.

All four endpoints are guarded by the **client** key (`Auth:ApiKeys`), not the admin key. Ingesting
is a client action, and forcing an admin key on it would push people toward using one key for
everything.

## ⚠ Breaking: `X-InferHub-Sources` changed shape

It used to carry bare chunk ids:

```
X-InferHub-Sources: ["5d981c…","0b72c7…"]
```

It now carries objects that name where each chunk came from:

```
X-InferHub-Sources: [{"id":"5d981c…","documentId":"employee-handbook.pdf","page":1},
                     {"id":"0b72c7…","documentId":"policy.md"}]
```

A chunk id alone identifies the row we retrieved but tells the reader nothing about *where the
answer came from*, and a citation that cannot name a document and a page is not a citation.
`documentId` and `page` are omitted (not null) for records written straight through `/api/vector`,
which never had a document. If you parse this header, you need to update.

## Decisions worth reading

- **No OCR, and there never will be.** A PDF whose text layer yields under ~50 characters per page
  is **rejected** with an error saying it looks like a scan. Bolting on an OCR pass would produce
  something that *usually* works — and a bad extraction does not fail. It succeeds, quietly, and
  fills the corpus with near-gibberish that retrieves plausible nonsense, surfacing months later as
  a model that is subtly and unaccountably wrong. If a document genuinely needs OCR, that is a
  decision its owner should make deliberately, with a tool they chose, before it reaches InferHub.
- **Your file is not kept.** Chunk text, a content hash, metadata. Not the document. A retrieval
  system that quietly becomes a document store has two sources of truth and a data-retention
  question its owner never agreed to answer.
- **Re-ingesting is idempotent.** Chunk ids are `sha256(documentId + ":" + chunkIndex)`, so a
  revision replaces its chunks in place rather than layering a second copy underneath the first —
  and a shorter revision has its orphaned tail chunks swept, because a stale chunk retrieves as
  confidently as a live one. Identical bytes twice: the second call does no work and says
  `"status": "unchanged"`.
- **A partial ingest is a failure and says so.** Lose the fleet mid-document and the response is
  **HTTP 500** with `"status": "partial"` and the chunk counts. The chunks that landed are real and
  visible, the document lists as `partial`, and re-posting the same bytes *resumes* rather than
  no-ops — the content-hash short-circuit deliberately does not fire on a partial document, because
  "you already have this" would be a lie about a document that is half-missing.
- **Embedding runs through the fleet**, batched, with at most `Ingestion:EmbeddingBatchSize` chunks
  in flight, so a 300-page manual queues behind itself instead of starving interactive chat. The
  coordinator grows no embedding path of its own.

## Under the hood

`IVectorStore` gained two operations, implemented by **both** providers and proven to agree by
`VectorProviderParityTests`:

- `ScanAsync(collection, filter, limit, afterId)` — metadata scan ordered by id, **without** the
  embeddings (a new `VectorEntry` record: "not fetched" must not be confusable with "not there").
- `DeleteByFilterAsync(collection, filter)` — bulk delete by metadata. The filter must be non-empty.

These are what let ingestion keep its promise that it **writes to the vector store and nowhere
else**: there is no documents table, no blob directory, no second lifecycle. A document *is* the set
of chunks sharing a `documentId` in their metadata. Rule 4 ("one source of truth per deployment")
survives untouched.

`LocalVectorStore.DeleteByFilterAsync` deliberately loops over the ordinary per-id delete rather than
bulk-removing under the lock: the per-id path is what raises `RecordDeleted`, and that is the only
way a deletion reaches the node replicas. A faster bulk delete would leave every node in the fleet
still serving the chunks of a document the hub thinks is gone.

## Dependencies

**One new package: `PdfPig` (0.1.15, Apache-2.0).** The second recorded exception to the
no-new-dependencies rule, after `Npgsql`/`Pgvector` in phase 20. It is coordinator-only (never in
`InferHub.Shared` or `InferHub.Node`), lives behind `IPdfTextExtractor`, is referenced by exactly one
file, and no code path reaches it unless a PDF is actually uploaded. Hand-rolling a PDF text-layer
parser is a bad use of a week.

## Verification

- **418 tests, 403 passed, 15 skipped** (the skips are the gated Postgres integration tests).
  `PdfExtractionTests` builds **real** PDFs with PdfPig's writer and parses them back — a stub would
  prove only that the seam is wired, and the parts this dependency was spent on are exactly the ones
  a stub cannot reach.
- **Real end-to-end**, run against a live coordinator + a live node (`Backend:Type=openai`) over real
  SignalR, against a real HTTP server speaking the OpenAI dialect. Verified: a Markdown document and
  a real two-page PDF ingested; identical bytes re-posted → `unchanged` with zero embed calls; a
  scanned PDF → `422` with the "this looks like a scan" error; chunks carrying their page number;
  a `/v1/chat/completions` call with `X-InferHub-Retrieve` answered from the PDF with
  `X-InferHub-Sources` naming the document **and page 1**; the chunks replicating to the node and the
  retrieval query being served **from the node replica**, not the hub; document delete removing every
  chunk of one document and none of the other; the `/api/status` ingestion block.

## Upgrade

Nothing to do unless you parse `X-InferHub-Sources` (see above). Ingestion is inert unless
`VectorStore:Enabled=true`, and every `Ingestion` key has a working default.
