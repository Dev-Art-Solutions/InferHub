# InferHub v3.47.0 / v3.47.1 — `Node:ResourceLimits`, a node-local CPU/GPU performance ceiling

User-requested (Bulgarian): an optional, node-local cap on how much of a box's own CPU and GPU it
lets itself be routed to consume — for example "no more than 80% of CPU, 80% of GPU" — that overrides
everything else, plus a local program with a UI to configure it.

**`v3.47.0` tagged with a known defect, found within minutes by CI itself: `build-and-test` failed.**
Not a functional bug — the feature was live-verified against a real running node before the tag went
out (see below) — but `src/InferHub.Coordinator/CLAUDE.md`'s own phase-82 note pointed at
`InferHub.Node/CLAUDE.md` instead of `src/InferHub.Node/CLAUDE.md`, which `ContextContractTests`'
`EveryCrossAreaPointerResolves` exists specifically to catch. The doc edit was made *after* the last
local `dotnet test` run of the day, so it never got the chance to fail locally first. **`v3.47.1`,
tagged the same day, is the one to run** — one-line fix, full suite green (1631+ tests) before the
tag.

## What shipped

**The one ceiling in this codebase with no matching field on a hub-sent `NodeProfile` at all.**
Every other node-side limit — `Tools:Allowed`, `Node:MaxConcurrency`, `Node:Vram` — a coordinator can
still *narrow further* than the node's own configuration. `Node:ResourceLimits` isn't on the wire:
a coordinator, trusted or compromised, cannot set it, raise it, or even see it. Unset (the default)
is byte-identical to v3.46.

- **Soft cap.** Set `Node:ResourceLimits:MaxCpuPercent` and/or `MaxGpuPercent` (1-100) and the node
  polls its own usage every `PollInterval` (default 5s, hand-rolled — `GetSystemTimes` on Windows,
  `/proc/stat` on Linux, zero new packages). `SustainedPolls` consecutive over-cap polls (default 3)
  withdraws the node from new placement — a meshed node the same way an unhealthy backend does
  (`Heartbeat.ResourceThrottled`, mirroring phase 69's `BackendHealth` exactly: still holds its
  models, just stops being routed to), a standalone node with the same `503` + `Retry-After` shape
  its concurrency gate already uses. `RecoverPolls` clean polls (default 2) bring it back. Nothing
  already running is ever touched. `MaxGpuPercent` needs an NVIDIA driver NVML can see — Linux only
  today, same platform scope as this project's existing CUDA detection; set with none visible, the
  node says so once at boot and the GPU half never trips.
- **Hard cap.** `HardCpuCapPercent` is a second, independent, harder mechanism: a real Windows Job
  Object CPU rate limit applied to this node's tool-worker child processes. Windows only today, and
  it needs a restart to change — re-configuring a job object's rate after processes are already
  inside it is not attempted.
- **A local configuring page**, served by the node itself rather than a separate program:
  `GET`/`POST /api/admin/resource-limits` under the existing `LocalApi` (loopback-guarded exactly
  like every other route on that host — needs `LocalApi:Enabled=true`). It shows live CPU/GPU
  readings and the current throttle state, and a save reaches the running node within one poll
  interval with **no restart**, written to an optional `node.local.json` beside `appsettings.json`
  rather than into that hand-authored file.

## Verified live, not just by the unit suite

A solo node run from source with `Node:ResourceLimits:MaxCpuPercent=1`:

```
warn: InferHub.Node.Resources.ResourceMonitor[0]
      Resource cap tripped: CPU 95% > Node:ResourceLimits:MaxCpuPercent (1%). This node will not
      accept new placement until it recovers.
```

```
$ curl -i -X POST http://localhost:5099/api/chat -d '{"model":"x","messages":[...]}'
HTTP/1.1 503 Service Unavailable
Retry-After: 15
{"error":"node is over its own configured resource cap: CPU 95% > Node:ResourceLimits:MaxCpuPercent (1%); retry in 15s"}
```

Then, with no restart, `POST /api/admin/resource-limits` raising the cap to 100 reached the running
governor within one poll — the next chat request passed the gate — and the underlying
`node.local.json` write and reload were confirmed on disk, at the exact path the admin page itself
resolves against (`AppContext.BaseDirectory`, pinned explicitly rather than left to the host's
default content root — the two disagreeing, found live, is why the file is pinned at all).

## Re-verified against the published image itself

Pulled `ghcr.io/dev-art-solutions/inferhub-node:3.47.1` and ran it as a real container. **One
methodology gotcha, not a code defect:** driving CPU load on the Docker Desktop *host* (Windows)
never moved the reading, because `/proc/stat` inside the container reflects the Linux VM Docker
Desktop runs containers in, not the Windows host's own task manager — the two are different machines
as far as this cap is concerned. A busy loop run *inside* the container (`docker exec`) reproduced it
immediately: `cpuPercent` climbed to 6%, the cap tripped, and a live chat request against the running
image got the same `503` + `Retry-After: 15` the from-source check did. Worth remembering for anyone
verifying this on Docker Desktop for Windows/Mac: the load has to be inside the container's own
namespace to register.

## Scope, stated rather than implied

- The local configuring page rides on `LocalApi`, which is off by default (phase 37). A purely
  meshed node reaches it only after turning that on — loopback-only by default, so doing so for this
  alone is safe — or edits `node.local.json` by hand; the format is identical either way.
- GPU utilization polling and the hard CPU cap are each scoped to one platform today (Linux and
  Windows respectively) — named as such in `appsettings.json`'s own comment, not silently absent.
- Not yet run: a real two-node mesh with one node throttled, verifying the coordinator actually
  reroutes a request to the other node rather than only refusing placement in isolation (the unit
  suite covers `FindNodesWithModel`'s narrowing directly; the end-to-end reroute was not driven live).
