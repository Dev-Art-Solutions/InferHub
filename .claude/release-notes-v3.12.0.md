# InferHub v3.12.0 — a corpus on every node, assigned from one place

Retrieval has been the hub's, or a standalone node's, and never both. From v3.12 a coordinator can
turn it on for a node, choose the vector engine, and have the box bring the corpus up — no file edited
on the machine, no restart, and inference never stops.

```bash
curl -X PUT http://localhost:5080/api/admin/profiles/edge-boxes \
  -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' -d '{
    "selector": { "labels": { "site": "sofia" } },
    "retrieval": {
      "enabled": true,
      "provider": "qdrant",
      "url": "http://qdrant.sofia.internal:6333",
      "credentialRef": "sofia-qdrant",
      "collections": ["site-sofia-docs"],
      "embeddingModel": "nomic-embed-text"
    }
  }'
```

## One authority per collection name, and the hub knows who it is

v3.6 refused to let a meshed node hold its own corpus. A node with hub-derived replicas *and* an
authority under the same names is a collection with two truths, and a replication pass waiting to
overwrite one of them.

**That rule is not relaxed.** A node that sets `LocalApi:Retrieval:Enabled` by hand while meshed still
refuses to boot, and the test that proves it is in this release.

What changed is that the hub can now be the one who *assigns* the corpus, and can therefore be the one
who *knows*:

- The hub records an **owner** per collection name: `hub`, or `node:{id}`.
- A hub-side create of a node-owned name is a **409 naming the owner**, so the disjointness is
  structural rather than a convention somebody has to remember.
- **Replication and healing never target a node-owned collection.** There is nothing hub-side to
  derive replicas from; a placement pass over one would push an empty snapshot at its owner, which is
  the hub deleting somebody's corpus while reporting success.

## The client API does not move

A client posts to `/api/collections/{c}/documents` and searches at `/api/collections/{c}/search`
exactly as before, whether the chunks are in the hub's store or on a box in another city. The hub sees
a node-owned collection and dispatches the work to its owner over the connection the node already
opened — so client scoping, quotas and every response shape are the ones you already had, and there is
no second ingestion path to keep in step with the first.

Two refusals are said out loud rather than left to be discovered:

- **PDF is a 415 on a node-owned collection.** `PdfPig` ships with the coordinator only, and a hub that
  extracted the text and shipped chunks would be a second ingestion path with different chunking and a
  different failure mode.
- **An owner that is not connected is a 503 naming the node** — never a quiet answer from the hub's own
  store. A confident answer from the wrong corpus is the failure nobody notices.

## The engine, the secret and the disk stay the operator's

A profile names a `provider` and a `credentialRef`. It cannot carry a **secret**: the ref is a *name*,
resolved on the node against `LocalApi:Retrieval:Credentials:{ref}`, and a name the box does not have
is a refusal naming the key rather than a quiet fall back to an unauthenticated connection to your
Qdrant. A hub that pushed keys down the link would be a secret distributor — credentials in profile
persistence, in an admin API response, and on every node the selector matched.

There is no field anywhere in a profile for a data directory either.

`postgres` is refused **by name**, with the reason: that connector needs `Npgsql`, which is
coordinator-scoped by design. A node runs `local` or `qdrant`.

## Zero new dependencies, and the reason is a decision from v3.1

The Qdrant connector was hand-rolled over `HttpClient` in v3.1 because the official client is gRPC and
would have dragged protobuf into the coordinator. That is exactly what let `QdrantVectorStore`,
`QdrantClient`, `QdrantIdMap` and `SparseVector` **move into `InferHub.Shared`** for this release — so
a node runs the *same* store, not a second one, and `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`. A dependency declined two releases ago is the reason a feature was
free today.

## Also in this release

- The retrieval routes on a node are **mapped unconditionally** and answer **501** with the sentence
  they have always had while no corpus runs. They were 404s through v3.11; ASP.NET cannot map an
  endpoint after the application has started, and a node that restarted itself on a hub instruction is
  refused by v3.11's own rules.
- Stopping a corpus **drains**: a request already retrieving finishes against the store it started on.
- A start that fails — unreachable engine, unresolvable credential — leaves the node with **no corpus
  and a reported refusal**, and a node that goes on serving chat.
- `/api/status` carries a per-node corpus block: provider, status, the collections with their record
  counts, and the last error. It is what the node **reported**; the hub never queries a node's corpus
  to build a status page, because that would make `/api/status` a synchronous dependency on a box that
  may be asleep.

## Compatibility

Additive throughout. **A fleet that assigns no corpus behaves exactly as v3.11** — ownership is empty,
so every collection is the hub's and every code path is the one v3.11 took. A v3.11 node against a
v3.12 hub registers and serves normally; a v3.12 node against an older hub gets no answer to its corpus
report, logs it at debug, and runs its own configuration.

One behaviour change to note if you script against a node: with retrieval off, `/api/collections`,
`/api/collections/{c}/documents` and `/api/vector/{c}/…` answer **501** rather than 404.

`dotnet test`: **994 passed, 0 failed, 46 skipped** (was 975 at v3.11.0).

## Images

```
ghcr.io/dev-art-solutions/inferhub-coordinator:3.12.0
ghcr.io/dev-art-solutions/inferhub-node:3.12.0
ghcr.io/dev-art-solutions/inferhub-node:3.12.0-ollama
ghcr.io/dev-art-solutions/inferhub-node:3.12.0-tools
```
