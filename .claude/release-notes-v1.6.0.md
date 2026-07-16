Text in, vectors out. v1.6.0 connects the embedding loop: a node with an embedding
model generates the vectors (it has the GPU), the coordinator stores and searches
them. After this release you can push documents as plain text and query the store
with plain text — no client-side embedding step — while raw vectors stay a
first-class path.

## What's new

- **`EmbedAsync` on `IInferenceBackend` + `OllamaBackend`.** OllamaClient is bumped to
  **2.0.0** to pick up the new `/api/embed` surface (batch-capable, the modern
  alternative to the legacy `/api/embeddings`).
- **New `embed` job kind** threaded through `InferenceExecutor`. The dispatcher and
  router are kind-agnostic, so routing reuses the existing model-aware logic — the
  coordinator picks a node advertising the requested embedding model.
- **`POST /api/embed`** (client scope, Ollama-shaped) — a drop-in embeddings endpoint
  that works for any existing Ollama embeddings client and reaches the whole node
  pool through the hub.
- **`POST /api/embeddings`** — the legacy `{model, prompt} → {embedding: [...]}`
  shape is translated to the modern batch form internally, so callers that haven't
  migrated keep working.
- **Text ingestion on the vector store.** `POST /api/vector/{collection}/upsert` and
  `/query` now accept `text` (+ optional `model`) instead of a raw `vector`. The
  coordinator embeds the text on a node and uses the resulting vector. Raw-vector
  callers keep working unchanged.
- **`POST /api/vector/{collection}/retrieve`** — convenience RAG read that does the
  full text → embed → search → matched-payload round trip in one call.
- **`VectorStore:DefaultEmbeddingModel`** kicks in when a text upsert/query omits
  `model` (default `nomic-embed-text`). The collection's dimension is validated
  against the returned embedding length — a mismatch is a clear 400 with a message
  naming the gap.
- **Actionable errors.** When no node is advertising the requested embedding model,
  the response is a 404 naming the model rather than a hung request.

## Compatibility

- The raw-vector path from v1.5 is unregressed: existing clients sending
  `{id, vector, ...}` continue to work without changes. The new `text`/`model` fields
  are additive.
- `VectorStore:Enabled=false` (still the default) means none of the new vector
  endpoints are mounted; existing inference deployments are byte-for-byte unchanged.
- The OllamaClient bump is the only dependency change. `1.0.0 → 2.0.0` keeps the
  inference surface we use; `Embed`, `GetVersion`, and `GetRunningModels` are added.

## What's next

Phase 15 (`v1.7.0`) takes the next step: replicating each collection's index across
the connected nodes so queries can be served by a node, not only by the coordinator.
Self-healing, the console surface, and inline retrieval on `/api/chat` and
`/api/generate` follow.
