# InferHub.Node/Control — agent context

**Scope: `src/InferHub.Node/Control/`, `LocalApi/LocalControlEndpoints.cs`, `Configuration/NodeLocalSettings.cs`.**
A node as a unit of its own: the control API a desktop app (or `curl`) drives, the owner's sharing modes, and a
corpus the fleet never sees. **Started here in phase 112** rather than in `src/InferHub.Node/CLAUDE.md`, which was at
its 1100-line budget.

> **Read the root `CLAUDE.md` first**, then `src/InferHub.Node/CLAUDE.md` — phase 37 (solo mode: the local surface,
> loopback, no fake fleet), 38 D1 (one vector authority) and 101 (the settings file, a process that ends to be
> replaced) are the ground this stands on, and they stay there.

## Related context

- The app that drives this API: `desktop/CLAUDE.md` (phase 115)
- The engines and catalogues the control routes reach (95, 97, 99, 102, 111): `src/InferHub.Node/Backends/CLAUDE.md`
- The hub's half of sharing — `NodeRegistry.RecordSharing`, the routing skip: below, phase 113 D1

## Decisions recorded here

### Phase 112 (a node has a control API) — amends 37 D1

One surface, under `/api/control`: `status`, `activity`, `events` (SSE), `logs` (+ `logs/stream`), `settings`
(GET/PUT, + `settings/file`), `sharing`, `restart`, `models/{name}/pull|warm|unload` and DELETE,
`engines/{name}/start|stop`, `catalog/{colibri|strata}/{model}/load|unload`. The node keeps its two single-purpose
pages (`/api/admin/resource-limits`, `/engine-limits`) and gains no third: the UI is the app's (115).

**D1, load-bearing — never anonymous, loopback by default.** Every control route needs `Authorization: Bearer
<token>`, on loopback too: it rewrites what runs on the box. The token is `Control:Token` or, unset, generated once
into `<DataDirectory>/control.token` (owner-only on Unix) **at boot**, not on first use — the app on the same box
reads the file before it has ever called. Compared by hash with `FixedTimeEquals`. A non-loopback caller is refused
unless `Control:AllowRemote`, and then still needs the token. The routes exist only where the local API does;
`Control:Enabled=false` removes them. `LocalApiAuthMiddleware` skips `/api/control` — the token is its gate, and
*rejected:* reusing `LocalApi:ApiKeys` — a key that may spend GPU time is not a key that may rewrite the config.
A Windows service's token sits in `%ProgramData%\InferHub\Node`, readable by administrators only; the app asks for
it to be pasted rather than the file being widened.

**D2 — settings go to `node.local.json`, the last layer, and only keys on the list.** 82 D7's file. `PUT {set,
remove}` merges into it atomically (`.tmp` + rename, the previous kept as `.bak`) for keys in
[NodeSettingsSchema](NodeSettingsSchema.cs) only — each with a type, live or restart, secret or not — so a typo is a
400 naming it. Before writing, the merged configuration is bound and run through the node's own `IValidateOptions`;
a refused change leaves the file untouched and the 400 lists the failures. Secrets are write-only (masked on read).
**The file's path is per host** (`LocalSettingsFile`, registered in DI; `INFERHUB_NODE_LOCAL_SETTINGS` →
`Node:LocalSettingsFile` → beside the binaries) — a static path was the first draft and two hosts in one test process
wrote each other's file. The validation root is built over the file *stream* and disposed after use; calling `Load`
on it again re-read a consumed stream.

**D3 — a restart is an exit the supervisor understands, offered only when one is there.** `POST /restart` drains
running work (bounded by `Control:DrainTimeout`) and ends with exit code **75**. `status.restart` says what will
start it again: `INFERHUB_SUPERVISOR` (the desktop app sets `desktop`), systemd's `INVOCATION_ID`, a container
(stated as an assumption about its restart policy), the Windows service (its own `ServiceRestarter`: SCM recovery,
configured by the setup's `sc failure`). On a bare console it is a 409 with the sentence; `?force=true` overrides.
*Rejected:* the node re-executing itself — it would escape the service manager and the Job Object the supervisor
holds (95 D3). **The code must survive the stop:** found live, the first restart from the app took 79 s and exited
with a crash code — the SSE streams held the host open and a hosted service threw `TaskCanceled` on its way out. The
streams are linked to `ApplicationStopping`, and `End()` forces `Environment.Exit(75)` on `ApplicationStopped`, with
a 45 s fallback. After the fix: 8 s, no exceptions.

**D4 — activity is counted where both paths meet, and carries no content.** [NodeActivity](NodeActivity.cs) is told
by the two executors every job passes (`InferenceExecutor`, `ToolExecutor`); the source is an `AsyncLocal` the mesh
connection sets in `RunOffDispatch` (`NodeActivity.FromMesh()`), so a local request and a hub job are told apart
without either path knowing the other. Rule 7: kind, model (the body's top-level `model` only), source, timing,
outcome — never a prompt, never a file name.

**D5 — the log is the node's own, in memory.** [NodeLogRing](NodeLogRing.cs), an `ILoggerProvider` ring of
`Control:LogLines` (2000). `System.Net.Http.HttpClient` and `Microsoft.AspNetCore` below Warning are left out — the
app's own 3 s poll otherwise filled the ring with its own requests.

**D6 — model and engine commands run here exactly as the hub runs them (amends 37 D1).** 37 D1 kept model
management hub-driven ("`ollama pull` in the terminal you are already at"). A node with no hub and no terminal has
neither, so the control routes call the same `ModelCommandExecutor`, `IEngineControl` and catalogue controls the
hub's commands reach — one implementation, two callers. On a meshed node a hub profile's pins still win (43 D1).

### Phase 113 (the owner's machine comes first: always · idle · paused)

`Node:Sharing:Mode` (`always` default, `idle`, `paused`) and `Node:Sharing:IdleSeconds`, both live.
[NodeSharing](NodeSharing.cs) recomputes `Paused` from the mode and, in `idle`, from local activity: it pauses
*before* the first local job runs (`NodeActivity.Begin` raises `Changed` ahead of the work) and resumes
`IdleSeconds` after the last one ends.

**D1, load-bearing — paused means "send me nothing new", said by the node, held by the hub.** The node stays
connected — models discoverable, limits visible — and reports `ReportSharingState(NodeSharingState)` at
registration and on every change. The hub's `NodeRegistry.RecordSharing` stores it on the entry and
`FindNodesWithModel` skips a paused node in the same branch as `ResourceThrottled`; `/api/status` shows it. A hub
older than 3.76 has no such method: the node logs that once, `HubHoldsSharing` goes false, `status.sharing.held` is
false and the app says the coordinator cannot hold a pause — never silently "paused" when it is not. *Rejected:* disconnecting — a reconnect
storm each time the owner opens a chat, and the fleet loses sight of the node's models.

**D2 — a job that raced the report runs (amends the brief).** The brief had the node refuse a mesh job that arrives
while paused. Not built: the window is one hub round-trip, the hub already stops routing on the report, and a
refusal there would turn an owner's pause into a failed request for a stranger. What is not established: an old
hub keeps sending work, and this node takes it — said in the status, above.

**D3 — in-flight work finishes; only new work waits.** Pausing never cancels a running mesh job; the status counts
local and network jobs apart (`status.jobs.local`, `.network`).

### Phase 114 (a private corpus on a meshed node) — amends 38 D1

**D1, load-bearing — private is a store of its own, reachable only through the local API.** 38 D1's sentence with
two words added: *one authority per collection name **per store**, and the hub knows who it is for every store it can
see.* `LocalApi:Retrieval:Private=true` lets local retrieval run on a meshed node; its store lives under its own
`DataDirectory`, the local API's routes read and write only it, and the hub never learns it exists — no corpus
report (`ReportCorpusState` is skipped), no replica, no ownership row. `RetrievalHost` refuses to start a corpus from
a non-local source while private, `ReplicationEnabled` requires `!Private`, and `NodeProfileApplier` refuses a hub
profile's retrieval section. So 38 D1's three failures cannot happen: invisible to the fleet *by intent*, a name in
both places is two collections no single request reads together, and replication writes only the replica directory.

**D2 — it must be asked for, and its directory must be its own.** Without `Private`, a meshed node with local
retrieval still refuses to boot, exactly as before. The validator refuses a private `DataDirectory` equal to, inside
or containing `Vector:ReplicaDirectory`, naming both keys (`Overlaps`).
