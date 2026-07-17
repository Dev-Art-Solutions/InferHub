# InferHub v2.8.0 — Fleet operations: remote model management & measured routing

Two unglamorous features that decide whether a mesh is pleasant to run: the hub can now **act** on
the fleet's models, and the router finally knows that a 4090 and a laptop are not the same node.

## Remote model management

The coordinator has always seen every model on every node and been able to do nothing about any of
them. Now it can pull, delete and warm a model on any node — from the console or the admin API, with a
progress bar, over the connection the node already holds open. **The node still has no inbound surface**;
commands travel down the same outbound SignalR channel inference jobs already use.

- `POST /api/admin/nodes/{id}/models/{model}/pull` — stream a pull, progress relayed on the existing
  SSE `/api/admin/stream` as `model-progress` events.
- `DELETE /api/admin/nodes/{id}/models/{model}` — delete a model from one node.
- `POST /api/admin/nodes/{id}/models/{model}/warm` — load a model into memory ahead of first use.
- `POST /api/admin/models/{model}/ensure?replicas=N` — pull onto the N most suitable capable nodes that
  don't already have it, skipping cordoned ones, and **report what it decided and why**.
- `GET /api/admin/models` — the fleet-wide model × node matrix.

A duplicate command for the same node+model coalesces onto the running one. Every command lands in the
audit log. And **not every backend can do this**: an OpenAI-compatible upstream (vLLM, llama.cpp, a
hosted provider) has its model fixed at launch, so it declares `SupportsModelManagement = false` and the
console greys out its controls — a backend asked to do the impossible refuses with a clean error frame,
never a 500.

## Measured routing

Since 1.0 the router has picked the "least-busy" node — fewest jobs in flight. That is a fine proxy on a
uniform fleet, and InferHub's whole premise is that your fleet is *not* uniform. A 4090 and a laptop with
an eGPU both report one job in flight; they will not finish at the same time.

Nodes now carry a measured throughput — an EWMA of tokens/second per model, computed from the
`eval_count`/`eval_duration` every completed job already reported. **No new measurement plumbing.** With
`Router:Strategy=throughput`, routing considers expected completion time rather than raw queue depth. A
node with **no** measurement is treated as *average*, never as slow — otherwise a fresh node never earns
a measurement and stays frozen out. Sticky conversation affinity still wins where it applies; throughput
is a tiebreak among candidates, not a replacement for the thing that was already right.

**The default is unchanged, bit for bit.** `Router:Strategy=least-busy` is the default; measured routing
is opt-in for this release. Measured tokens/sec is on `/api/status` per node and on the status page.

## Config

`Router:Strategy` — `least-busy` (default) | `throughput`. Nothing else changed; see `appsettings.json`.

## Tests

520 total (503 passed, 17 skipped — the gated Postgres integration tests, as always). New coverage:
`ModelCommandTests` (pull/delete/warm progress, coalescing, a backend that cannot manage refuses cleanly
— not a 500), `PlacementTests` (ensure skips cordoned and already-present nodes, counts a non-manageable
holder toward N, and says so when candidates run short), `ThroughputRoutingTests` (a fast node wins, an
unmeasured node is not starved, affinity still wins, and `least-busy` is byte-identical to v2.7).

Verified live end-to-end against a real coordinator + Ollama node: warm/pull/delete relayed over the SSE
stream as `model-progress` frames (including a clean error frame for a missing model, no stack trace);
the model matrix; `ensure` reporting a shortfall honestly; and measured tokens/sec appearing on
`/api/status` after real chat traffic.

## Zero new dependencies

Rule 5 holds: `HttpClient`, SignalR and `System.Text.Json` did all of it.
