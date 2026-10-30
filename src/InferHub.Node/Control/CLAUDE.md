# InferHub.Node/Control — agent context

**Scope: `src/InferHub.Node/Control/`, `LocalApi/LocalControlEndpoints.cs`, `Configuration/NodeLocalSettings.cs`,
`Configuration/SealedMode.cs`, `Backends/SealedOllama.cs`, and the hub's `Services/RelayCache.cs`, `Services/RelaySupply*.cs`
and `Services/SupplyJobs.cs`.**
A node as a unit of its own: the control API a desktop app (or `curl`) drives, the owner's sharing modes, a
corpus the fleet never sees, and a node sealed to its network (122). **Started here in phase 112** rather than in `src/InferHub.Node/CLAUDE.md`, which was at
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


### Phase 116 (the owner's terms: sharing hours and a daily allowance)

`Node:Sharing:Hours` (lines, [SharingHours](SharingHours.cs)), `Node:Sharing:TimeZone`,
`Node:Sharing:DailyNetworkMinutes` — all live, all applied by `NodeSharing.Recompute` after `paused` and `idle`'s
local checks, in that order: the reason names the first that holds, `ResumesAtUtc` when it lifts.

**D1, load-bearing — the terms are more reasons for 113's pause, nothing else.** No new wire method, no new hub
state: a closed window or a spent allowance is `Paused=true` with a reason and `NodeSharingState.ResumesAtUtc`
(an appended, nullable field — a 3.76 hub ignores it and still holds the pause). Every routing path, the status
and the console pill read the one flag.

**D2 — a window belongs to the day it starts on; touching windows merge.** `fri 22:00-06:00` is Friday night; an
end at or before the start crosses midnight, `24:00` is midnight at the end. `IsOpen` builds the occurrences from
yesterday to eight days ahead in the owner's zone, merges overlapping and touching spans, and answers open/closed
plus the next change (null: no change within the week — `daily all day`). A start in a spring-forward gap opens
at the first minute the clock shows. Lines that do not parse fail startup and are a 400 from the control API.

**D3 — the allowance stops new work, never running work.** Network seconds today = finished mesh jobs (today's part
of each) + the running ones so far; per job, so two at once spend twice as fast; local work never counts. A timer
looks again when the running jobs would spend what is left, at midnight, and at the next window edge.

**D4 — a restart is not a second day.** Each finished mesh job writes `{day, seconds}` to
`<DataDirectory>/sharing-usage.json` (only while an allowance is set); a node starting on the same day reads it.
A job cut short by a stop or a crash is not counted — the error is in the network's favour by minutes, never in a
fresh allowance.

### Phase 117 (models kept for the owner's machine)

`Node:Sharing:Models` — `{Include, Exclude}`, `ModelFilterOptions` like `Node:Models`, live. `NodeSharing.Shared`
narrows a model list; `SharedModelsChanged` fires when the lists' contents change (normalised, so a re-save of the
same names or another term changing fires nothing).

**D1, load-bearing — a kept model is absent from the network, not paused.** The one place it acts is
`CoordinatorConnection.ReportModelsAsync`: `Node:Models` first (what the node serves), then `Shared` (what the
network is told). Not reported → no hub of any version routes to it, lists it or names it under a capability
(`BackendCapabilities.Declare` reads the narrowed list). A *kind* stays declared while any shared model is
served under it — an Ollama node keeping its only embedding model still declares `embed` over its chat models, as
Ollama itself would answer (found live; the first draft claimed the kind was withdrawn). No wire
field, no hub change — 114 D1's private corpus applied to a model. *Rejected:* a per-model flag on the report for
the hub to skip — an old hub would route straight through it, and the console would show models nobody may use.

**D2 — the change reaches the hub at once.** `SharedModelsChanged` → the same re-report a recovered backend sends
(`OnBackendRecovered`). A job that raced the report runs, as 113 D2: the window is one round-trip.

**D3 — the local side never sees the list.** The local API, `/api/control/models` and `/status` list every model;
the control API adds `shared` per model (left out for one `Node:Models` leaves out — found live, it read `true`) and
`sharing.models` (both lists). `PUT /api/control/sharing {models:{include,
exclude}}` — a list left out keeps its value, `[]` clears it, `{}` is a 400; a name in both lists fails startup and
the PUT. The two lists live under one key so the file never holds half a change.

**Not covered:** a hub profile can still pin a kept catalogue model (it loads; the network still cannot route to it),
and tool/image recipes are not models — `Tools:Allowed` and the profile decide those.
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

### Phase 118 (two more routes: `/fit`, `/fit/install`)

`POST /api/control/fit {link, context}` and `POST /api/control/fit/install {link, engine, variant, context, force}`
live in `LocalApi/LocalControlEndpoints.Fit.cs`, behind 112 D1's token like every route here; the install is 112 D6's
model command with an engine and `ModelCommand.Context`. The decisions are in `src/InferHub.Node/Backends/CLAUDE.md`
(Phase 118).

### Phase 122 (a sealed node) — `Node:Sealed`, the hub's `Relay`

One setting that makes a node reach nothing outside its own network but its coordinator. What it would have fetched
— a Hugging Face file, an Ollama pull, the 119 catalogue, the 118 estimate's registry reads — it asks the hub for, at
`{hub}/node/relay/{hf|ollama-registry|ollama-library}/{path}` with its enrollment secret
([NodeRelayEndpoints](../../InferHub.Coordinator/Services/NodeRelayEndpoints.cs), `Relay:Enabled`, off by default).

**D1 — local is an address you can see is not the internet.** Loopback, RFC 1918, link-local, 100.64/10 (a Tailscale
mesh), fc00::/7, a single-label name (a container, a LAN host) and `.local`/`.lan`/`.internal`/`.home.arpa`. A dotted
public name is outside even if it resolves to 10.x today: the setting is a promise about tomorrow too. *Rejected:*
resolving the name at boot — it answers for the moment of the check and costs a DNS query to the outside.

**D2, load-bearing — refuse to start, and name every door at once.** [SealedModeValidator](../Configuration/SealedMode.cs)
reads configuration (38 D1's cycle) and fails boot with one sentence per door: a `chatgpt`/vendor engine or one at a
public `BaseUrl`, an Ollama outside the network, `LocalApi:Urls` on a wildcard or a public address (its own public
API — loopback or one private address is fine), a corpus elsewhere, `HuggingFace:Enabled` with no hub and no local
mirror, `Ollama:Supervisor:AutoInstall`, `Update:Check|Auto|AllowFromHub` from GitHub, `Tools:AllowModelDownload`,
any `CloudMedia` provider. All of them, so it is fixed in one edit, not one restart per key. *Rejected:* quietly
switching those features off — a node that "starts fine" and then cannot do what its config says is 37 D8's silent
failure.

**D3 — the relay knows three upstreams by name, follows redirects itself and streams.** A URL in the request would make
the hub an open proxy with its address on it. A Hugging Face resolve and a registry blob both redirect to a signed CDN
URL; handing that back would send the node to the CDN, so the hub follows (and .NET drops `Authorization` on the way —
the node's own HF token reaches Hugging Face, never the CDN). `Range` passes through: Ollama asks for a blob in parts.
A path cannot move the host (`Target` checks scheme and authority). Logged per request with the path, never the query.

**D4 — a floor under all of it: every factory client of a sealed node passes `SealedEgressHandler`.** Registered with
`ConfigureHttpClientDefaults`; anything to a non-local host that is not the coordinator throws before a socket opens,
and a request to the relay gets the secret. The boot check covers the keys; this covers a code path nobody listed.
**Not covered:** a redirect followed *inside* a primary handler from a local server to the outside, and the SignalR
connection (it is the coordinator). The Ollama and Python children inherit `OLLAMA_NO_CLOUD=1`, `HF_HUB_OFFLINE=1`
and `TRANSFORMERS_OFFLINE=1`, set on the node's own process at composition.

**D5 — an Ollama's cloud is not this node's.** Ollama 0.40 lists a cloud model with `remote_host` and says on
`/api/status` whether its cloud is off. Sealed, the listing is read raw, cloud models are not reported and a request
for one is refused before Ollama sees it (a `…cloud` tag not yet pulled too). An Ollama this node did not start (a
service, the desktop app) does not inherit `OLLAMA_NO_CLOUD`; the node logs an error once naming the variable.

**D6 — an Ollama pull is still Ollama's, through a registry on loopback.** [SealedOllama](../Backends/SealedOllama.cs)
opens a one-pull registry on 127.0.0.1 (plain sockets: `HttpListener` needs an http.sys reservation, Kestrel stays in
`LocalApi/` by rule 2), pulls `127.0.0.1:port/ns/model:tag` with `insecure`, copies it to the asked name and deletes
the loopback one — the blobs are shared, the copy is a manifest. **Found by running it:** a blob GET must redirect to a
*different host*, or Ollama follows it and fails "no Location header"; the redirect goes to `localhost`, so there is an
IPv6 listener too (Windows dials `localhost` as `::1` only). *Rejected:* assembling the model from blobs via
`/api/create` — a second implementation of Ollama's manifest, wrong the day a layer type ships. Only the default
registry (`hf.co/…` names are refused), and only an Ollama on this machine's loopback.

**D7 — what downloads by itself is refused, not relayed.** A llama.cpp router pull (llama-server fetches from Hugging
Face itself) and a Strata install (its setup does) end in one terminal frame naming the way that works: a GGUF as a
Hugging Face link, which the store downloads through the relay. The hub logs a warning when a sealed node registers
and its relay is off; the console shows a neutral `sealed` pill (`sealed` on `/api/status` and `/api/admin/nodes`).

### Phase 123 (the hub keeps what it relayed) — `Relay:Cache`

[RelayCache](../../InferHub.Coordinator/Services/RelayCache.cs) beside the relay: blobs under
`<Relay:Cache:DataDirectory>/blobs/<sha256>`, written to `partial/` first. Nothing on the node changed — a 3.82 node gets
it the day its hub turns it on. Why: after 122 every byte a sealed node fetches crosses the hub, and crosses again for
the next node; a fleet behind a slow uplink is exactly the fleet that sealed its nodes.

**D1, load-bearing — the upstream is asked every time; the cache replaces only the body, keyed by the sha256 the
upstream named.** The relay's client no longer follows redirects itself (`AllowAutoRedirect=false`, `FollowAsync` by
hand, dropping `Authorization` off the first host as .NET did — 122 D3 holds). The first hop is the upstream's: a
registry blob is addressed by its digest in the path, a Hugging Face resolve names its LFS file in `X-Linked-Etag`
(checked live on Xet storage, 2026-10-30). A hit needs a 2xx/3xx first hop, so a gated repo still refuses a node
without access, and `main` still resolves today. *Rejected:* keying by URL — a `main` that moved serves yesterday's
file, and one node's token would open a gated file for every other node. *Rejected:* skipping the first hop for a
digest-addressed blob — the cheapest request in the pull is the one that carries the access decision.

**D2 — hashed while written, kept only on a match.** `RelayFill` hashes every byte; a whole blob whose hash differs is
deleted and counted (`discarded`), a partial one simply deleted. The node's own bytes are relayed as before either way —
98's sha256 check and Ollama's digest check are what refuse a bad download; the hub only declines to keep it. A
restart deletes `partial/` and indexes `blobs/` by name without re-hashing: a blob got there only after a match.

**D3 — a whole GET is kept as it passes; a ranged miss is relayed as before and the hub fetches the whole blob once,
beside it.** Ollama asks for a blob in parts (found again live: a 206 for every layer, even one of 89 bytes), so keeping only
what passed would keep nothing. `FillInBackground` repeats the whole GET from the first hop (a signed CDN URL may
expire); one fill per digest at a time, a second node's parts in the meantime pass through. The hub's uplink carries a
first pull twice — once for the node, once for itself. *Rejected:* making the first node wait for the whole blob before
its first range — a 20 GB wait before byte one of a request that asked for 100 MB.

**D4 — rule 4's sixth exception: off by default, bounded, and losing it costs a download.** `Relay:Cache:Enabled`
false; `MaxGiB` (200) evicts least recently served (the file's write time is touched on a hit, so the order survives a
restart); a blob that would leave less than `KeepFreeGiB` (20) free, or bigger than the cache, or of unknown length, is
not kept (`skipped`). Not content in rule 7's sense — public model weights by hash — but a gated repo's weights are the
gate's owner's, which is why D1 asks the gate every time.

**D5 — visible where the relay already is.** `X-InferHub-Relay-Cache: hit|miss` on a cacheable blob, absent otherwise;
`relay.cache` on `/api/status` and `inferhub_relay_cache_*` on `/metrics`, absent unless the cache is on (28 D5). Logged
per kept, evicted and discarded blob by a 12-character digest prefix. HEAD is always relayed live.

Tests: `RelayCacheTests` (Coordinator — digests, keep/discard/abandon, single flight, eviction order, a restart),
`RelayCacheMeshTests` (Mesh — a gated HF file kept as it passes then served while the first hop is still asked and the
CDN is not; a refused first hop over a held blob; an Ollama blob in parts fetched whole once; corrupt bytes; off).

### Phase 124 (models supplied through the coordinator) — `Relay:Supply`

[RelaySupply](../../InferHub.Coordinator/Services/RelaySupply.cs) beside the relay and its cache: `blobs/<sha256>`,
`models/<id>.json`, `partial/`. [SupplyJobs](../../InferHub.Coordinator/Services/SupplyJobs.cs) fills it — `POST
/api/admin/supply {ollama | huggingFace, revision, include}` fetches, `POST /api/admin/supply/import {path, name}` imports
— and the console has a panel for both while `relay.supply` is on `/api/status`. Why: after 123 the hub keeps what it
relayed, but it is still a relay — a model is fetched at the moment a node needs it, and a hub with no internet has
nothing to relay. An air-gapped site could not run model management at all.

**D1, load-bearing — a supplied model is the admin's decision, so the relay answers it without asking the upstream.**
The opposite of 123 D1, and the reason it is a separate set rather than a cache setting: what decides access is the
admin who fetched it (with the hub's own `HuggingFaceToken` for a gated repo), and what `main` means is what it meant
that day. Checked before the cache and before `Target`, after `Relay:Enabled` — supply without the relay reaches no node.
*Rejected:* a "never ask" switch on the cache — it would serve a gated file to every node because one node had access,
which is the hole 123 D1 closed. *Rejected:* the node fetching from a hub-side model store by a new route — every
sealed path (98's store, 118, the loopback registry) already speaks Hugging Face and the registry through the relay, so
the hub answering those paths is zero node change and a 3.82 node gets it.

**D2 — every byte is checked before it is supplied; a model is whole or not supplied.** An Ollama blob against its
digest (and the manifest against `Docker-Content-Digest` where the registry names it), a Hugging Face LFS file against
its sha256, a small file against its git oid (`sha1("blob {size}\0" + bytes)`), sizes always. Written to `partial/`,
moved in only on a match, deleted otherwise; the job fails naming the file. `Save` refuses a record with a blob missing,
and a restart skips one whose blob has gone (logged, the record kept for the admin to fetch again).

**D3 — the upstream's answers are recorded, not re-derived.** The manifest's bytes as a blob (served with its media
type and digest), the commit the revision pinned, the files with their sha256, the model info 118 reads. The relay
replays them on the paths a node asks: `v2/{ns}/{model}/manifests/{tag|digest}` and `/blobs/`, `api/models/{repo}`
(+`/revision/{rev}`) — the stored info with `sha` set to the commit — `api/models/{repo}/tree/{rev}` and
`{repo}/resolve/{rev}/{file}`, `rev` being the name or the commit. The tree lists **every** file with its sha256 as
`lfs.oid`, so 98's check covers a small file too. A blob is answered only under a repo whose record names it. A tag
does not move: fetching again replaces the record and deletes blobs nothing else needs.

**D4 — `Only` closes the last door: what is not supplied is a 404 naming the key, never a fetch.**
`X-InferHub-Relay-Supply: not-supplied` on it; the node's Hugging Face client and the loopback registry read that
header and end the pull with the hub's sentence rather than Ollama's "file does not exist". The admin's own fetch still
reaches the upstreams — which may be a mirror beside the hub (`Relay:HuggingFace`/`OllamaRegistry`, 122). The 119
catalogue and the library pages are not models; under `Only` a sealed node's browse fails, and `GET /api/admin/supply`
is the list. *Rejected:* `Only` falling through to the cache — that is a fetch.

**D5 — an offline import reads what the internet side already produces, and only under `ImportDirectory`.** Three
layouts, detected: an Ollama models dir (`manifests/registry.ollama.ai/…`, and `manifests-v2/ollama.com/…` — **found on
this box**: Ollama 0.40 writes a new pull there, the same manifest), a Hugging Face cache (`models--owner--repo`, `refs/`
→ commit, `snapshots/<commit>/`; a file whose cache blob is named by a sha256 must match it), and another hub's supply
dir (records re-hashed blob by blob). Copied, never moved — the disk is somebody's. The request's path is relative;
`..`, a rooted path or a drive is a 400: an admin key is not a shell on the hub host. *Rejected:* an export endpoint —
the supply dir is already the bundle. *Rejected:* an upload route — a 20 GB multipart body through Kestrel, when the
disk is already on the machine.

**D6 — rule 4's seventh exception: off by default, never evicted, removed by the admin.** `Relay:Supply:Enabled` false:
no directory, no header, no status field, no series, and `/api/admin/supply` says it is off (404). Unlike 123 D4 there
is no `MaxGiB` — evicting a supplied model under `Only` would take it off the fleet; `KeepFreeGiB` (20) refuses a fetch
or an import before its first byte instead. `DELETE /api/admin/supply/{id}` removes one. Jobs are in memory (a restart
forgets jobs, never models). Visible: `X-InferHub-Relay-Supply: supplied`, `relay.supply` on `/api/status` (models, bytes,
only, served, servedBytes, refused, running), `inferhub_relay_supply_*` on `/metrics`; the relay's log says `from the
supply` per request.

Tests: `RelaySupplyTests` (Coordinator — names, globs, the import root, lookup per path, whole-or-nothing, a restart,
orphan blobs, off), `RelaySupplyMeshTests` (Mesh — fetched from a fake registry and Hugging Face behind a counting CDN,
then served with the upstream stopped, pulled by the node's own `HuggingFaceClient`; `Only` refusing with the upstream
untouched; corrupt bytes never supplied; an Ollama dir in both layouts and a Hugging Face cache imported; off).
