# InferHub v3.73.1 — an unload the hub hears at once

A patch to v3.73.0, found by running its published images.

- **A llama.cpp unload now waits for the router to finish it.** llama.cpp b11417 answers `POST /models/unload` while
  the model's instance is still stopping. v3.73.0 re-reported the node's models straight after, the router still listed
  the model as `loaded`, and the hub kept that model's slot count (`slotsByModel`) until the next refresh, up to a
  minute later. `UnloadAsync` now polls the router's listing until the model is no longer `loaded` or `loading`, as a
  warm already waited for `loaded`, bounded by the engine's `TimeoutSeconds`. An instance slow to stop ends the wait
  with a warning, not the command: the unload was accepted.

Nothing else changed. A stale count was a preference, never a refusal; no request was answered differently because of
it.

## Checked

The published 3.73.0 coordinator with a node built from this commit, both engines, the real llama.cpp router: three
rounds of warm then unload, and the hub's `slotsByModel` cleared **1.7, 1.8 and 1.8 s** after each unload. On the
published 3.73.0 node it still read `{qwen: 4}` at 20 s.

## Tests

`LlamaCppRouterTests.AnUnloadReturnsOnlyOnceTheRouterStopsListingTheModelAsLoaded` — the stub keeps the model `loaded`
for three polls after the unload; red with the wait removed. `AnInstanceSlowToStopEndsTheWaitNotTheUnload` — a status
that never changes ends the wait and the command still succeeds.

`dotnet test InferHub.sln`: **2 309 passed / 63 skipped**. Zero new `PackageReference`.
