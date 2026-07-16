# InferHub v2.7.0 — Clients, quotas & usage accounting

**Phase 25.** `Auth:ApiKeys` has been a flat list of strings since 0.6: every key anonymous,
unlimited, and indistinguishable from every other key. Fine for one person and their own GPUs;
not fine the moment a second party is behind one of those keys. This release gives a key an
identity, a budget, and a bill — and it does so **without storing a single prompt anywhere**.

Everything is **backwards compatible**: a config with only the flat `Auth:ApiKeys` list runs
byte-identically to v2.6, its keys resolving as anonymous unlimited clients.

## What's new

- **Named clients** — `Auth:Clients` is a list of `{ Id, Key, Limits }`. Every limit is
  nullable (`null` = unlimited): `MaxConcurrent`, `RequestsPerMinute`, `TokensPerMinute`,
  `TokensPerDay`, `AllowedModels`. A duplicate key across two clients fails startup — key →
  client attribution must never depend on list order.
- **Usage accounting** — every completed request is metered per client and per model:
  requests, prompt tokens, completion tokens, and whether it was served by cloud burst (the one
  that costs actual money). Embeddings and document ingestion count too. The counts were already
  in every response since 2.3/2.4 — this release attaches a name to numbers we already had.
  - `GET /api/admin/usage?from&to&clientId&model` — aggregates you could put on an invoice.
  - `GET /api/admin/clients` — configured clients with live window consumption against limits.
  - A **Clients & usage** console panel with a date range and CSV export.
- **Honest, standard rejection** — over a rate limit → `429` with a window-accurate
  `Retry-After`; over the daily budget → `402 Payment Required` with `Retry-After` pointing at
  UTC midnight (checked *before* the rate limits — "waiting a minute will help" would be a
  lie). A model outside a client's allowlist → `404` **byte-identical to a model that does not
  exist**. On `/v1` these use the OpenAI error envelope (`rate_limit_exceeded`,
  `insufficient_quota`), so SDK retry logic works out of the box.
- **Bounded queueing** — when every node holding a model is at its declared `MaxConcurrency`,
  a request waits up to `Queue:MaxWaitSeconds` (default 30), at most `Queue:MaxDepth` waiting
  (default 64), then `503` + `Retry-After`. Nodes with no declared cap never queue. Queue depth
  and median wait are on `/api/status` and the status page — a queue you cannot see is a queue
  you will not notice filling. With cloud burst on `no-node-or-saturated`, saturation overflows
  to the upstream **instead of** queueing; the precedence is explicit and tested.
- **Optional durable ledger** — `Usage:Persistence=postgres` writes each record to an
  append-only table over **its own** connection string (deliberately independent of the vector
  store's). Default stays `none`: in-memory, reset on restart, like every other counter.

## Counts, never text

A usage record is a client id, a model, a kind, two integers, a fallback flag and a timestamp.
It does not contain the prompt, the completion, a hash of either, or a "representative sample"
— and there is **no flag to change that**, because a flag is an invitation. Streaming counts
come from the terminal chunk; a stream that never delivers it (mid-stream disconnect) records
nothing rather than guessing. A test pins the record's shape.

## Zero new dependencies

The durable ledger reuses `Npgsql`, already a recorded dependency since phase 20. Everything
else is `System.Text.Json` and a dictionary.

## Config

New sections: `Auth:Clients`, `Usage` (`Persistence`, `Postgres:*`), `Queue` (`MaxWaitSeconds`,
`MaxDepth`). All optional; see `appsettings.json` and the README's
[Clients, quotas & usage](https://github.com/Dev-Art-Solutions/InferHub#clients-quotas--usage-v27).

## Tests

501 total, all passing with the Postgres gate open (the 19 gated integration tests now include
the usage ledger). New coverage: `ClientRegistryTests`, `AdmissionControlTests`,
`UsageLedgerTests` (including the mid-stream-disconnect contract and the record-shape pin),
`QueueTests` (including the fallback-vs-queue precedence and the uncapped-node regression), and
`PostgresUsageLedgerTests`. Verified live end-to-end: a real coordinator + real node over
SignalR against an OpenAI-dialect upstream — blocking and streaming usage metered, `429`/`402`/
`404`/`503` each observed on the wire with correct envelopes per dialect, one client's
saturation leaving another untouched, and the Postgres ledger surviving a coordinator restart.
