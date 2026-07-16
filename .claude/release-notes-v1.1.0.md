Node configuration is now fully declarative — everything lives in `appsettings.json`,
validated at startup, with model filtering, labels and concurrency hints.

## What's new

- **Typed options.** `Coordinator`, `Node`, `Ollama` and `Backend` sections each bind
  to a dedicated options class. No more scattered `configuration["..."]` reads.
- **Validated at startup.** Bad values (non-URL coordinator, negative interval,
  `MaxConcurrency < 1`) stop the node with a message naming the offending key.
- **Intervals are now config, not constants.** `Coordinator:HeartbeatInterval`,
  `ModelRefreshInterval` and `RetryDelay` used to be hard-coded `TimeSpan`s in
  `CoordinatorConnection`; they're now `TimeSpan` keys with the previous values as
  defaults.
- **Model filter.** `Node:Models:Include` / `Exclude` (case-insensitive, exclude
  wins) restrict what each node advertises. Empty/empty = current behaviour.
- **Labels and concurrency hints.** `Node:Labels` (free-form key/value) and
  `Node:MaxConcurrency` (advisory cap) reach the coordinator and show up on
  `GET /api/nodes`.

## Compatibility

- Default `appsettings.json` keeps behaviour identical to v1.0.0.
- `NodeRegistration` gained `Labels` and `MaxConcurrency` as optional/nullable
  fields, so older nodes still register cleanly.
- Env-var overrides still work: `Coordinator__EnrollmentSecret`, `Node__Name`, etc.

See the new "Node configuration" table in the README for every key and default.
