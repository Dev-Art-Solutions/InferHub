# InferHub v3.38.0 — Postgres becomes a vector provider a node can run

A solo node — no coordinator, one process — could already run its own vector store and ground its
own answers, and since v3.12 that store could be an external Qdrant instead of the node's own disk.
`postgres` was the one provider refused by name, with the reason spelled out at the refusal: `Npgsql`
is a package, and `InferHub.Shared` — the plain class library a node and a coordinator both build
against — cannot carry one without breaking the "zero dependencies" claim that has held for eight
phases running.

That reasoning was correct about `InferHub.Shared`. It was never actually about the node.

```jsonc
"LocalApi": {
  "Enabled": true,
  "Retrieval": {
    "Enabled": true,
    "Provider": "postgres",
    "Postgres": { "ConnectionString": "Host=db;Database=inferhub;Username=inferhub;Password=..." }
  }
},
"Coordinator": { "Enabled": false }
```

## What actually moved

`PostgresVectorStore`, `PostgresSchema` and the bootstrap DDL (extension, schema, registry table,
the hybrid-search keyword-index backfill) now live in a **new** project, `InferHub.Shared.Postgres` —
not an addition to `InferHub.Shared` itself, which still ships as an empty `<Project Sdk=...>` with
zero `PackageReference`s. That distinction is the whole design: Qdrant moved into `InferHub.Shared`
directly in v3.12 because its connector was hand-rolled over `HttpClient` and cost nothing. Postgres
needs `Npgsql`/`Pgvector` at the class itself — pgvector's columns are typed — so it gets its own
project instead, referenced by both the coordinator and the node. One store, not two, between the two
hosts; the coordinator's own behaviour and its own test suite are unchanged by the move.

The node's `RetrievalHost` gained a third provider branch alongside `local` and `qdrant`: it builds
its own `NpgsqlDataSource`, and the bootstrap sequence doubles as the reachability probe — an
unreachable database, a role that cannot `CREATE EXTENSION`, or a bad connection string all fail the
corpus **start**, never the first query, exactly as an unreachable Qdrant already did.

## One asymmetry worth knowing

Qdrant on a node supports a hub-assigned `credentialRef`: the profile names a credential, the node
resolves the secret, and the hub never sees it. Postgres does not get this. A connection string
already carries its own password field, and there is no clean place to graft a resolved secret onto
one written in the node's own configuration — so `CredentialRef` alongside `Provider=postgres` is a
startup refusal naming the mismatch, and `Postgres:ConnectionString` on the node is the whole answer.

## The cost, named

This is the first phase to add a real `PackageReference` to `InferHub.Node` on purpose: `Npgsql` and
`Pgvector`, the coordinator's own versions. `InferHub.Shared.csproj` is unchanged — still empty, still
zero packages, still the claim every phase before this one could make unconditionally. `InferHub.Node.csproj`
can no longer make that claim, and it says so rather than going quiet about it.

## Not established at ship time

The published node image has not yet been pulled and run against a real Postgres+pgvector as part of
this release — the code path is covered by the existing gated integration tests (the same
`deploy/postgres/` fixture the coordinator's own Postgres suite uses) and by unit tests of the new
refusal shapes, but the "pull the image, ingest, restart, ingest again" check phases 37/38 ran for
`local` and 44 ran for `qdrant` has not yet been run for `postgres`. Do it before calling this
finished in practice, not just in the suite.

## Also in this release

Nothing else. `dotnet test` is green across all four projects.
