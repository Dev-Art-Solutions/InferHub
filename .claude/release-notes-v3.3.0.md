# InferHub v3.3.0 — getting Qdrant into production, and your data out of wherever it is

Two things close here. **`inferhub-migrate`** copies a populated vector store from one provider to
another — `local` ↔ `postgres` ↔ `qdrant`, any pair, either direction — which deletes a caveat every
release since v2.2 has carried. And Qdrant grows the knobs a real deployment wants and a demo does
not: quantization, on-disk vectors, payload indexing, and a startup warning when you point at a
remote Qdrant with no API key.

That finishes the Qdrant track: it went in as a connector (v3.1), grew server-side hybrid search
(v3.2), and now runs in production with a way to get your data into it from wherever it currently
lives — all three at **zero new dependencies**.

## The caveat this deletes

Since the vector store became pluggable, every release said the same thing: switching providers on a
populated deployment meant re-ingesting from the original documents. That is awkward advice from a
system that deliberately **does not keep** your original documents (phase-23 D2: chunk text, a
content hash and metadata — not the file). With a third backend on the table it stopped being
tolerable.

```bash
# Dry run first: what would be copied, and where. Writes nothing.
dotnet run --project tools/InferHub.Migrate -- \
  --from local:./data/vectors --to qdrant:http://localhost:6333 --dry-run

# Then for real.
dotnet run --project tools/InferHub.Migrate -- \
  --from local:./data/vectors --to qdrant:http://localhost:6333
```

Each side is a provider shorthand (`local:…`, `postgres:…`, `qdrant:…`) or the path to a JSON config
file with a `VectorStore` section — a coordinator's own `appsettings.json` will do, which is the
honest way to migrate with the exact settings the hub uses. `--collection` narrows it to one,
`--batch-size` and `--parallel` tune throughput, `--from-key` / `--to-key` carry a Qdrant API key.

**What it does and does not do**, because these are the decisions that matter:

- It creates each collection on the target with the **same dimension and distance**. A target
  collection that already exists with a *different* shape is **skipped with a reason**, not
  half-filled — the first case fails per record, and the second would *succeed* and silently rank
  differently.
- **Re-running is safe.** Chunk ids are deterministic (v2.5), so a second run overwrites rather than
  duplicating, and an interrupted run is resumed by running it again.
- **It never deletes.** A record in the target that is not in the source is left alone: a migration
  tool that removes data nobody asked it to remove is a worse failure than one that leaves a stale
  record behind.
- It reports the **target's own record count** per collection and **exits non-zero** if any collection
  was skipped or came up short. "The upserts returned" is not the same claim as "the data is there."
- It is a **standalone console tool and is not in the images**, like the eval harness. Moving data
  between stores is an operator's deliberate action, never something a running coordinator should do
  to itself — a hub that copied itself into another engine would be a second write path and, for as
  long as the copy ran, a second truth.

Migrating *into* Qdrant creates collections in the current v3.2+ hybrid shape, so this is also how a
dense-only collection created on v3.1 gains server-side hybrid search — without re-ingesting a single
document.

## The interface was one method short

The vector store could scan metadata without the embeddings, which is exactly right for finding a
document's chunks and exactly wrong for copying them. The alternative — a `GetAsync` per record —
is a round trip per chunk against stores that answer a page in one, which is not a tool anybody would
run on a million chunks.

So `IVectorStore` grew **`ScanWithVectorsAsync`**: the same filter semantics, the same id ordering and
the same exclusive `afterId` cursor as `ScanAsync`, with the vectors. All three providers implement
it, and it is held to the same parity test as everything else — two providers that disagreed here
would give a migration between them a different corpus on the far side. `ScanAsync` stays the default
for everything else: paying for vectors you are about to discard is the waste it was introduced to
avoid.

## Found by running it: Qdrant normalises cosine vectors

The new parity arm failed the first time it met a real Qdrant 1.12.4. `[0.1, 0.9, 0]` went in and
`[0.1104…, 0.9938…, 0]` came back — **Qdrant stores the unit-normalised vector in a `Cosine`
collection**, and stores exactly what you sent under `Dot` and `Euclid`. A stubbed client would have
echoed back what it was handed and this would have shipped unnoticed; only the live server has this
behaviour to find.

It is **safe**. Cosine similarity is scale-invariant, so a cosine collection round-tripped through
Qdrant returns the same ids in the same order with the same scores — there is a test that does
exactly that round trip and asserts it. But it is real: a cosine collection migrated *out of* Qdrant
carries normalised vectors, so anyone diffing raw floats across a migration will see different
numbers and must not conclude the copy is broken. The parity test now asserts the honest thing per
metric — verbatim under `dot`/`l2`, same direction and unit length under `cosine` — and the README
and the tool's own README say so out loud.

## Production Qdrant

Three `VectorStore:Qdrant:` settings, all applied when a collection is **created** (existing
collections are untouched, which is another reason migrating is useful):

| Key | Default | What it buys |
|---|---|---|
| `Quantization` | `none` | `scalar` (int8, ~4× less vector memory) or `binary` (~32×, materially lossy). |
| `OnDisk` | `false` | Dense vectors on disk instead of RAM, for a collection bigger than the memory you'll give it. |
| `PayloadIndexKeys` | `["documentId"]` | A Qdrant payload index per metadata key. |

Quantization is a **memory-for-recall trade, not a free win** — quantized vectors rank
approximately. The documentation says so and points at the [eval harness](../tools/InferHub.Eval)
rather than offering an adjective: a store that ranks approximately and is described as "faster" is a
store that lies about relevance. Measure it on your own corpus.

`PayloadIndexKeys` defaults to `documentId` because every ingestion scan and filtered delete filters
on it, and an unindexed payload filter is a full scan — cheap on a demo collection, the difference
between a second and a minute on a real one. The index is built on `__meta.<key>`, the same payload
path the connector's filters use; an index on any other path would be built, reported healthy and
never used. An index Qdrant refuses is logged and skipped rather than failing the create.

## A remote Qdrant with no API key warns at startup

Qdrant ships unauthenticated. That is fine on localhost and a data leak anywhere else — the point
payload holds the chunk *text*, so anything that can reach the port can read your corpus and delete
it. Point the coordinator at a non-loopback `Url` with no `ApiKey` and it now says so at startup, in
that many words.

It is a **warning, not a refusal**, on purpose: a private network with its own controls is a
legitimate deployment, and refusing to boot would be us overruling an operator about their own
network. (Contrast v3.0's split-brain fence, where demoting *was* correct — there the alternative was
two hubs both serving, which is a correctness failure. Here the alternative is someone else's risk
assessment.) TLS is just an `https://` `Url`.

## Tests

New or extended coverage:

- `MigrateTests` (no server) — a local → local copy is faithful (ids, vectors, payloads, metadata),
  a query against the target returns the same top-k, a dry run writes nothing, a re-run converges
  rather than duplicating, a dimension *or* distance mismatch is refused with a reason, `--collection`
  narrows the copy, a missing collection fails loudly, and the spec parser maps every shorthand
  (including a `postgres://` URI, whose scheme separator is not the shorthand's).
- `MigrateTests` (gated) — `local → qdrant` and `local → postgres` copy a populated collection and
  return the same top-k and scores as the source, with metadata intact so the document model still
  works on the far side.
- `VectorProviderParityTests` / `QdrantVectorStoreTests` — `ScanWithVectorsAsync` agrees across all
  three providers: same records, same order, same cursor and filter semantics, and the vectors
  verbatim (`dot`/`l2`) or normalised-but-same-direction (`cosine`, per the finding above), plus a
  cosine round trip through Qdrant that keeps the ranking and the scores.
- `QdrantClientTests` (no server) — the exact JSON for scalar and binary quantization, the `on_disk`
  flag on the dense vector, the payload-index request, and that **defaults change nothing on the
  wire** (no `quantization_config`, no `on_disk`).
- `VectorStoreOptionsValidatorTests` — every quantization mode validates, an unknown one fails, an
  empty payload-index entry fails, an empty list is allowed.

## Zero new dependencies

Rule 5 holds again. The migration tool references the coordinator (to compose real stores through the
one composition root rather than reimplementing three connectors) and **ships nothing inward**; a
clean `git diff` on every shipped `.csproj`.
