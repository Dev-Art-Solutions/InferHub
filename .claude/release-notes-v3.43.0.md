# InferHub v3.43.0 — the auto-scaler learns to turn a model back off

Phase 78. Since v3.41, `AutoScalerService` could enable a model on a node where demand justified it —
but nothing ever turned one back off. A model an operator (or the scaler itself) enabled stays enabled
forever, holding whatever VRAM/RAM the node's own supervisor keeps resident for it, long after the
pressure that justified enabling it is gone. This release adds the other direction.

## What's new

- **`Metrics.RecordModelServed`/`LastServedUtc`** — a new node-attributed signal: which `(node, model)`
  pair was actually routed to, and when. Recorded at the one place both values are known together
  (`InferenceCore.DispatchAsync`'s two `router.Route` success points), it is the signal phase 76's own
  brief (D2) named as missing when it declined to build scale-in at the time.
- **`AutoScalerService` now ticks a second direction.** For every enabled, routable `(node, model)`
  pair, if it has not actually served a request in `AutoScaling:ScaleIn:IdleMinutes` — and at least one
  *other* healthy, uncordoned node still routes that model — it is disabled, through the exact same
  `NodeModelToggle.SetEnabledAsync` path a human's console toggle and scale-out both already use.
- **Never the last copy.** A pair is skipped regardless of idle time if it is the only node fleet-wide
  currently routing that model — an idle model with zero traffic is exactly what "nobody needs it right
  now" looks like until the one client who does asks.
- **A fresh restart is never read as idleness.** `AutoScaling:ScaleIn:MinUptimeMinutes` (default 30)
  withholds scale-in entirely until the process has run at least that long.
- **One shared cooldown, both directions.** `AutoScaling:CooldownMinutes` — already scale-out's guard
  against flapping — now also blocks a pair from being disabled again inside the window after it was
  toggled either way, so bursty traffic that merely looks idle between bursts cannot flap the fleet.
- **A third, independent switch.** `AutoScaling:ScaleIn:Enabled` (default `false`) gates scale-in on top
  of scale-out's own `AutoScaling:Enabled` — a fleet already running scale-out in production sees no new
  writes on upgrade until an operator opts in to this direction too.
- **Backfilled a documentation gap from v3.41.** The `AutoScaling` section never made it into
  `appsettings.json`'s commented defaults or the README when scale-out shipped. Both now document the
  whole feature, both directions, in one place.

## What was not established

- **Not live-verified against a real fleet.** Phase 76 was verified against a real coordinator + Ollama
  node the same evening it shipped; this release was not — no two-node fleet with one model idle-driven
  on purpose was run this session. What exists is: the pure decision logic
  (`AutoScalerService.ShouldScaleIn`, `RoutableNodeCountByModel`) under unit test, and the full
  regression slice green. The tick's actual wiring — `registry.Snapshot`, `Metrics.LastServedUtc`,
  `NodeModelToggle.SetEnabledAsync`'s write and its log line — was not watched happen against a running
  process before this tag.
- **No console panel**, same as scale-out — an operator reads what the scaler did from the audit log
  (`model.disable:{model}`, actor `auto-scaler`) and `GET /api/admin/nodes`.
- **No per-node idle signal survives a coordinator restart.** `LastServedUtc` lives in memory only, so a
  restarted coordinator treats every pair as unserved since the moment it opened (`MinUptimeMinutes` is
  what stops that from immediately disabling a quiet fleet).

## Verification

`dotnet test tests/InferHub.Tests.Coordinator` — regression plus seven new focused cases in
`AutoScalerScaleInTests.cs`: the last-routable-copy guard, the idle/never-served gate against process
start, and the shared cooldown checked from both directions. **757 passed, 43 skipped, 0 failed** —
unchanged shape from phase 76's own 745 (the difference is phase 77's coverage landing between the two).

**Published-image check:** pending — recorded once the `3.43.0` images are built and pulled (this file
is updated in place, or the gap is named the way v3.42's release notes named its own outstanding drill).
