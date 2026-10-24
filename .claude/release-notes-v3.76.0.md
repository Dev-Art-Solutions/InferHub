# InferHub v3.76.0 — a node of your own

Phases 112–114, and InferHub Desktop 0.1.0 (phase 115) on its own tag. A group of friends pools their machines into
one network, and each machine is still somebody's computer. Until now a node was driven from the hub, or from a file
and a restart on the box. v3.76 makes a node a unit of its own:

- **A control API on the node** (`/api/control`, phase 112): what it is doing, its log, its settings, a restart, its
  models and engines. Everything the hub can tell a node to do, the node's owner can now do on the box.
- **The owner comes first** (phase 113): `Node:Sharing:Mode` = `always`, `idle` or `paused`. A paused node stays in
  the network and the hub routes around it; `idle` pauses while this machine runs its own jobs.
- **A private corpus on a networked node** (phase 114): documents only this machine's local API reads, and the hub
  never learns exist.
- **InferHub Desktop** (phase 115, `desktop/v0.1.0`): a control panel for Windows, Linux and macOS that attaches to
  the node on this machine or runs its own.

## The control API

On the node's local API (`LocalApi:Enabled=true`), under `/api/control`:

| Route | What |
|---|---|
| `GET status` | node, version, network connection, sharing, engines, models, jobs now (local and network apart), limits, whether a restart is possible and what would do it |
| `GET activity`, `GET events` (SSE) | jobs running and the last 200 finished — kind, model, local or network, timing, outcome; never a prompt |
| `GET logs`, `GET logs/stream` (SSE) | the node's own log, the last 2000 lines, kept in memory |
| `GET`/`PUT settings`, `GET settings/file` | the editable keys with type, live or restart, effective value (secrets masked); `{"set":{…},"remove":[…]}` written to `node.local.json` |
| `PUT sharing` | `always` · `idle` · `paused`, live |
| `POST restart` | drain, then exit with code **75** for the supervisor; `409` on a bare console (`?force=true` overrides) |
| `POST models/{name}/pull\|warm\|unload`, `DELETE models/{name}` | the hub's model commands, from here |
| `POST engines/{name}/start\|stop`, `POST catalog/{colibri\|strata}/{model}/load\|unload` | the same for engines and catalogues |

- **Never anonymous.** Every route needs `Authorization: Bearer <token>`, loopback included. The token is
  `Control:Token`, or one generated at boot into `<Node:DataDirectory>/control.token`. Off-machine callers are refused
  unless `Control:AllowRemote=true`, and still need it. `LocalApi:ApiKeys` do not open these routes.
- **A settings change is validated before it is written.** Only keys on the node's list are accepted (a typo is a
  400 naming it); the merged configuration goes through the node's own validators; a refusal leaves the file
  untouched and lists why. The write is atomic and keeps the previous file as `node.local.json.bak`.
  `Node:LocalSettingsFile` (or `INFERHUB_NODE_LOCAL_SETTINGS`) moves the file.
- **A restart is an exit the supervisor understands.** The Windows service (the setup's SCM recovery), systemd
  (`Restart=on-failure`), a container with a restart policy, InferHub Desktop (`INFERHUB_SUPERVISOR=desktop`).
  The node never re-launches itself: it would escape the service manager and the Job Object that holds its limits.

## Sharing

`Node:Sharing:Mode` (default `always`) and `Node:Sharing:IdleSeconds` (120), both live. The node reports
`ReportSharingState` at registration and on every change; the hub records it on the node and its router skips a
paused node the way it skips a throttled one. `/api/status` shows `sharing` per node. A running network job is never
cancelled — only new ones wait.

**Against a 3.75 or older coordinator a pause does not hold.** That hub has no `ReportSharingState`; the node logs it
once, keeps taking that hub's jobs, and its status says `sharing.held: false`. A job that raced the report (one
hub round-trip) runs; the node does not refuse it.

## A private corpus

`LocalApi:Retrieval:Private=true` lets local retrieval run on a networked node. Its store is its own directory, read
and written only through the local API; the node sends no corpus report, takes no replica into it, and refuses a hub
profile's retrieval section or a non-local corpus source while private. Its `DataDirectory` must not equal, sit inside
or contain `Vector:ReplicaDirectory` — the node refuses to start, naming both keys. Without `Private`, a networked
node with local retrieval still refuses to start, exactly as before.

## Also

- `Microsoft.AspNetCore.SignalR.Client` 10.0.8 → **10.0.13** (and `Microsoft.Extensions.Hosting.WindowsServices`
  with it): CVE-2026-70159, a denial of service in `SignalR.Common` ≤ 10.0.12, published 2026-10-13. The
  coordinator's images take the patched runtime from `aspnet:10.0`.
- The node's log no longer carries the `HttpClient` and ASP.NET request lines below Warning in its control-API ring —
  a 3 s status poll otherwise filled it with its own requests. The console output is unchanged.

## Found by running it

The desktop app drove a node it started itself (InferHub Desktop, embedded mode, the machine's real Ollama):

- **The first restart from the app took 79 s and exited with a crash code**, so the app did not start it again. Two
  causes: the open event and log streams held the host open until the shutdown timeout, and a hosted service threw
  `TaskCanceled` on its way out, which replaced exit code 75. The streams now end on `ApplicationStopping`, and the
  restart forces `Environment.Exit(75)` once the host has stopped (45 s fallback). After the fix: **8 s**, no
  exceptions, and the setting changed before it (`Node:Sharing:Mode=paused`) was in force after.
- **The control token was created on first use**, so an app reading the file before its first call found nothing. It
  is created at boot now.
- **`node.local.json`'s path was process-wide**: two hosts in one test process wrote each other's file. It is per host.

## Tests

`NodeControlTests` (34), `PrivateCorpusTests` (3), two `NodeRegistryTests`, `SharingMeshTests` (3 — a real hub and
node over SignalR: the state at registration; `idle` pausing for a local job at the hub's router and resuming after;
an owner's pause reaching the router live, and a network job never pausing it),
`DesktopCoreTests` (8). Full suite green: Shared 371, Coordinator 894, Node 591, Mesh 522, Desktop 8.

## Not established, said out loud

- **A restart under the Windows service and under systemd was not run live.** The service's restarter ends the
  process and relies on the setup's `sc failure … restart/5000` — configured since 3.66, not exercised by this phase.
  The desktop supervisor is the one verified.
- **A pause against a 3.75 hub** is covered by the node's fallback code path, not a live old hub.
- **The private corpus** was tested in process; not against Qdrant or Postgres as its provider.
- The control API's model commands were verified against Ollama; the engine and catalogue routes are the same calls
  the hub makes and were exercised by tests only.

## Upgrade

Nothing to do. `Control` is on wherever the local API is on, and needs the token; `Node:Sharing:Mode` defaults to
`always`, which is 3.75's behaviour. Update the coordinator first if any node will use `paused` or `idle`.
