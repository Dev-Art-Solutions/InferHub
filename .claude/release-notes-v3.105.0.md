# InferHub v3.105.0 — the fleet page: nodes grouped, the last hour of traffic, what needs a hand (phase 152)

The console grew one panel per feature for 150 phases, and none of them answers "how is the fleet doing". Nodes have
labels, but an admin could not name a set of nodes, add up their traffic or act on them together. Traffic existed only as
since-boot counters and on `/metrics`, so a rate needed a Prometheus beside the hub. 3.105 adds one page for that:
`/fleet.html`, linked from the console.

## What is new

- **Node groups kept on the coordinator.** A group is a name over node ids, labels (every pair must match), or both; a
  node is in it when either holds, and may be in several groups. `PUT /api/admin/groups/{name}`
  `{"description", "nodes": [...], "labels": {...}}`, `GET`, `DELETE`. An empty group is refused, because it would match
  nothing. **A group changes no routing**: the router, the queues and admission never read it.
- **A cordon for a whole group.** `POST /api/admin/groups/{name}/cordon|uncordon` cordons each connected member with the
  per-node call the console already uses. Each node gets its own audit entry, `"<who> (group <name>)"`. Members named by
  id that are not connected are listed as `absent` and left alone.
- **`GET /api/admin/fleet?groupBy=group|label:<key>|organisation|version|none&minutes=15`**: totals, each group (taking
  work, cordoned, in flight against the declared caps, requests per minute, failure rate, absent members) and each node
  with its state, groups, labels, recent and since-start counts, and a list of what needs attention.
- **The last hour of traffic, without a Prometheus.** The hub samples its own per-node counters every
  `Fleet:Traffic:SampleSeconds` (10) and keeps `Minutes` (60) in memory. `GET /api/admin/traffic` returns about sixty
  buckets per group: completed, failed and in flight (in flight is a bucket's busiest sample). The first sample is only a
  baseline, so a hub that has served for a week does not show a week of requests in its first ten seconds. A bucket from
  before the hub was sampling is `covered: false`, and the page draws it as a gap, not as zero.
- **Needs attention, as sentences.** Warnings: no heartbeat for 20 s, backend unreachable or wedged, throttled, at least
  20 % of at least 5 requests failed in the window, conflicting profiles, a sealed node whose machine reaches the internet.
  An absent group member also counts. Shown but not counted: cordoned, paused by its owner, at its cap, holds no model, an
  update available.
- **The page**: tiles, a traffic chart per group (requests/min, failed/min, in flight; click the legend to hide a series),
  group cards with a sparkline and the cordon buttons, a sortable, filterable node table with per-node cordon, and a group
  editor. You can tick nodes in the table and choose *Group selected…*. It is build-free like the rest of the console. The
  admin key stays in the tab's memory.
- **Groups survive a restart only if you ask.** `Fleet:Groups:Persistence=file` writes one `groups.json` (written whole
  and moved over; a file that does not parse stops the hub rather than being overwritten empty). The image keeps it under
  `/data/groups`. This is rule 4's tenth recorded exception. The default `none` keeps groups as long as the hub.

## What does not change

A hub with no groups routes, queues and admits exactly as in 3.104. The only new running thing is a sampler that reads
two counters every 10 s. No new package; `InferHub.Shared.csproj` is still empty. No node change; any node version
appears on the page.

## Verified

- `FleetTests` (11): membership by id and by labels, the file surviving a restart, the validator, the baseline sample,
  the window, the report by label with its attention lines, absent members, buckets and coverage, the cordon over HTTP
  with its audit entry, and that every field `fleet.js` reads is on the payload.
- Live from source: a hub on :5152 with two nodes on the real Ollama (`site=sofia`, `site=plovdiv`, `MaxConcurrency=2`).
  I created the group `office` from a label plus ids, one of them `old-laptop`, which never connected. 55 real generations
  ran. The group cordon cordoned both nodes and reported `old-laptop` absent. After a hub restart the group came back from
  `groups.json` and the nodes rejoined it. Screenshot: `.claude/verification-v3.105.0-fleet.png`.

## The published image

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.105.0` (revision label `fb9583a`, the tag) run as a container with a named
volume on `/data`, an admin key and `Fleet__Groups__Persistence=file`; two nodes from source dialled it. `/fleet.html` 200, the
admin routes 401 without the key. The group `office` (a label, one id and the absent `old-laptop`) was written to
`/data/groups/groups.json` owned by `app`, so the image's directory is writable, which is the permissions trap (21 D7) this
phase had to avoid. 16 generations, a group cordon of both nodes with `old-laptop` reported absent, then `docker restart`: the
group came back from the volume and both nodes rejoined it.

## The suite

On this machine, the whole solution: Shared 457, Coordinator 1090 (50 skipped), Mesh 559 (3 skipped), Desktop 36, Node 785
(17 skipped). Two Node tests failed, `ChatGptBackendTests.AChatGptNodeComposesItsOwnBackendAndLeavesUpstreamAlone` and
`ASignedOutNodeRefusesWithA401RatherThanA502`. They fail on their own too: the node resolves its ChatGPT directory to a path under
the repository instead of the test's temp directory. This phase changed no node code, and CI was green on 3.104, so it is state on
this box; CI on this tag is the check. `TextJobTests.WithNoQueueTimeA503IsTheJobsFailure` failed once under the full parallel run and
passed 9 of 9 on its own. CI on main (`663032b`) is green, ChatGPT tests included.

The tag's own `build-and-test` was red: the failure-rate sentence used `P0`, which prints `40 %` under the invariant culture
and `40%` here. `663032b` formats it invariantly and the test pins fr-FR. The published 3.105.0 image prints `40 %` in that
sentence; nothing else differs.

## Not established

- A fleet of dozens of nodes on the page. Rendering is plain DOM, re-drawn every 5 s; it was seen with two nodes.
- The page through a reverse proxy, with an admin key over the network (it is the console's key handling, unchanged).
- Latency and tokens per node are not in the history: nothing on the dispatch path records a duration today.
- No `/metrics` series for groups; each coordinator in a cluster keeps its own groups and its own history.

## Found on the way, not changed

With every holder of a model cordoned, `/api/generate` answers `404 model 'x' not found`, not a 503 that says the nodes are
cordoned. That is the behaviour since cordon existed, and 69 D3 fixed the same confusion only for unhealthy backends.
