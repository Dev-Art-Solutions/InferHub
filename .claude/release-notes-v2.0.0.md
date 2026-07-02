The RAG mesh GA. v2.0.0 wires the vector store straight into inference: a client
can opt a normal `/api/chat` or `/api/generate` call into retrieval by naming a
collection, and the coordinator embeds the query on a node, searches the store,
assembles an augmented prompt, and dispatches the generation to a node — grounded
answers with orchestration on the always-on hub and every heavy step on the fleet.
Retrieval is per-request opt-in via headers; without the header, `/api/chat` and
`/api/generate` behave exactly as v1.x, so nothing changes for existing callers.

## What's new

- **Opt-in retrieval headers on `/api/chat` and `/api/generate`.** Three new,
  Ollama-body-preserving headers:
  - `X-InferHub-Retrieve: <collection>` — presence enables RAG for the request.
  - `X-InferHub-Retrieve-K: <int>` — top-k override (clamped to
    `VectorStore:Retrieval:MaxRecords`).
  - `X-InferHub-Retrieve-Model: <model>` — embedding model override (defaults to
    `VectorStore:DefaultEmbeddingModel`).
- **`RetrievalPipeline` on the hub.** New service composes the RAG loop end to end:
  extract the query text (last user message for chat; `prompt` for generate) →
  dispatch an `embed` job to a node → search the collection (node replica when
  available via `IVectorQueryRouter`, hub-local otherwise) → assemble an augmented
  prompt on the hub → return the rewritten request JSON + source ids to the
  caller. The endpoint dispatches the generation on the augmented body. Only
  orchestration and hub-local search stay on the coordinator; embedding and
  generation both run on nodes.
- **Retrieved sources on the response.** The endpoint sets
  `X-InferHub-Sources` — a JSON array of the retrieved record ids — so the Ollama
  body stays pure and callers get provenance without parsing the response.
- **Configurable prompt template.** New `VectorStore:Retrieval:Template` key —
  the literal `{context}` placeholder is replaced by the concatenated retrieved
  records (each rendered as `[id] text`; text is drawn from `payload.text`, then
  `payload.content`, then the raw payload). The default template asks the model
  to answer from the context and admit when it cannot. Validated on startup: the
  template must be non-empty and contain `{context}`.
- **Failover surface.** When retrieval cannot run (store disabled, missing
  collection, no embedding node, empty query text),
  `VectorStore:Retrieval:OnMissing` decides: `error` returns
  `424 Failed Dependency` with an actionable message; `passthrough` runs the
  original request unchanged and omits the sources header. Pre-stream failover on
  the generation job is unchanged.
- **Rule #7 stays intact.** The augmented request body is assembled in-flight and
  forwarded to the node — nothing about the message or the retrieved context is
  retained on the coordinator. Called out explicitly in `CLAUDE.md`.

## Compatibility

- **No breaking changes for existing callers.** A `/api/chat` or `/api/generate`
  call without the retrieval header is byte-for-byte identical to v1.x: same
  routing, same streaming, same body.
- With `VectorStore:Enabled=false` (still the default) the retrieval pipeline is
  not registered; a request that sends the retrieval header falls back to the
  configured `OnMissing` — `error` returns `424`, `passthrough` runs the original
  request unchanged.
- The Ollama-shaped response body is unaltered; source ids ride on the
  `X-InferHub-Sources` header.
- No new NuGet packages. `dotnet test` green: 197 tests.

## What's next

The vector / RAG mesh track (phases 13–18) is complete. Beyond this milestone,
the roadmap points at multi-coordinator clustering, persisted routing affinity,
richer audit trails, and additional inference backends (vLLM, llama.cpp).
Re-ranking, multi-collection fusion, query rewriting, and tool-calling-style
retrieval are separate tracks — future work.
