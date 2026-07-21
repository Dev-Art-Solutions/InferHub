# InferHub v2.13.0 — Client-scoped collections (RAG multi-tenancy)

v2.7 gave an API key an identity, a budget and a bill. RAG collections stayed fleet-global: any
client key that could ingest could, in principle, read or write any collection. For a single owner
that is fine. For an agency running one mesh behind several end-clients it is a data-isolation gap.
This release closes it — without a second store, a second truth, or a migration.

## What changed

- **`Auth:Clients[].Collections` scopes a key to a set of collections.** Each entry is an exact
  collection name or a single trailing-`*` prefix (`tenant-a-*`). **Absent or empty = every
  collection**, which is what every key could do before v2.13, so an existing config is unchanged.
- **The scope is enforced on every path that names a collection**: document ingest, list, get,
  chunks, delete; `POST /api/collections/{c}/search`; the raw `/api/vector/{c}/*` data plane; and
  the `X-InferHub-Retrieve` inline-RAG header on **both** `/api` and `/v1`.
- **Out of scope is `404`, not `403`** — byte-identical to a collection that does not exist, with the
  same sentence. The check runs *before* the store is consulted, so a name outside your scope reads
  the same whether or not it exists. A tenant is never told another tenant's collections are there.
- **Provisioning is just ingesting.** A scoped client posting a document to a collection inside its
  scope that doesn't exist yet creates it. No separate create ceremony, no admin round trip per
  tenant. Unscoped clients keep the pre-v2.13 contract: collections are an admin's to create.
- **Admin keys stay fleet-wide.** `GET /api/admin/vector/collections` gains a `scopes` block naming
  which clients can reach each collection — the view that makes a tenancy misconfiguration visible
  before a tenant finds it.

## Invariants

- **Rule 4 holds.** This is an **authorization filter over the one vector store**, not a store per
  tenant. One collection namespace, one source of truth; a scope only decides which names a key may
  say.
- **One place decides.** `CollectionAccessPolicy` answers "may this key touch this collection", and
  the route groups carry it as a filter rather than each handler carrying a copy — the ingestion
  group alone has five routes, and the one that gets forgotten is the isolation hole.
- **A scoped-out retrieval header is an error, not a passthrough.** It deliberately does not go
  through `Retrieval:OnMissing`: answering without the context the caller asked for, silently, is the
  wrong failure on a tenancy boundary.
- **Auto-provision does not guess the dimension.** Phase 23 refused to auto-create collections for
  two reasons — it would guess the dimension, and it would route around the admin scope that owns
  collection lifecycle. The second dissolves for a client whose config *names* its scope; that list
  **is** the provisioning grant. The first doesn't, so creation is deferred until the first batch
  comes back embedded and the dimension is **measured** from the vectors. An embed that never
  succeeds therefore leaves no empty collection behind to be misread as provisioned.
- **Rule 5 holds: zero new dependencies.**
- **Backwards compatible.** A config with only `Auth:ApiKeys`, or clients with no `Collections`,
  behaves byte-identically to v2.12.

## Upgrading

Nothing to do. Scoping is opt-in per client:

```jsonc
"Auth": {
  "Clients": [
    { "Id": "acme",   "Collections": ["acme-*", "shared-glossary"] },
    { "Id": "globex", "Collections": ["globex-docs"] },
    { "Id": "internal" }                          // no list = all collections, as before
  ]
}
```

569 tests green (17 Postgres-gated, skipped without a database).
