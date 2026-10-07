# InferHub v3.62.0 — many colibri models on one node, loaded on request, freed when idle, picked from the hub

Since v3.58 a colibri node ran one `coli serve --model <dir>`: one model, chosen at boot, in RAM for as
long as the node ran. A box with three converted MoE models was three nodes, or an SSH session and a
restart every time you wanted a different one.

v3.62 gives a colibri node a **catalogue**. Every converted model in a directory is listed to the hub;
the one a request names is loaded; at most `MaxLoaded` are in RAM at once; on demand an idle one is
stopped to give its memory back; and the coordinator picks which models stay loaded.

## What's new

### A catalogue instead of one model

```bash
docker run -d --name inferhub-colibri \
  -e Coordinator__Url=http://hub:5080 -e Coordinator__EnrollmentSecret=... \
  -e Colibri__Serve__Model= \
  -e Colibri__Serve__ModelsDir=/models \
  -e Colibri__Serve__OnDemand=true \
  -v /nvme/colibri:/models \
  ghcr.io/dev-art-solutions/inferhub-node:colibri
```

- Every sub-directory of `Colibri:Serve:ModelsDir` holding a `config.json` is a model, named after the
  directory; `Serve:Models {name: dir}` adds more. The directory is read again on every listing, so a
  model converted while the node runs appears on the next refresh.
- Every model is routable whether it is loaded or not. A request for one that is not loaded launches
  its own `coli serve` (ports from `Serve:Port` up) and waits for it (`Serve:LoadTimeout`, 15 min).
- `Serve:MaxLoaded` (default **1**) is how many may be loaded at once. When a different model is asked
  for, the least recently used one with nothing in flight is **stopped first** and only then is the new
  one launched — two models never share the box's RAM by accident.
- `Serve:OnDemand=true` stops a model idle for `Serve:IdleUnload` (10 min). The process is killed, so
  the memory really comes back.
- `Serve:Preload` names models loaded at boot. `Serve:Model` still works exactly as in v3.58; it cannot
  be set beside a catalogue (the `:colibri` image sets it — clear it with `Colibri__Serve__Model=`).
- Works as a colibri engine under `Backend:Engines` too: stopping that engine stops every loaded model.

### The coordinator picks

- The console's new **Colibri models** panel: every model on every colibri node with its state
  (`loaded`, `loading`, `unloaded`, `failed`), whether it is pinned, in flight and idle time; **Load** /
  **Unload** per model and **On demand** / **Keep loaded** per node.
- `POST /api/admin/nodes/{id}/colibri/models/{model}/load|unload`,
  `POST /api/admin/nodes/{id}/colibri/on-demand/enable|disable`, or in a profile
  `"colibri": {"loaded": ["olmoe"], "onDemand": true}`. They are written to the node's profile, so a
  choice survives a reboot of either side.
- A pinned model is never evicted or idled out. **Load on a full node is a switch**: on the default one
  slot, "this model instead of that one" — the old one is stopped before the new one starts.
- The node is the ceiling: a name it does not have, or more pins than `MaxLoaded`, is refused by the
  node naming what it has.
- Warm and unload from **Model management** work too. A pull is refused in a sentence — a colibri model is
  made with `coli convert`, not downloaded — and on a node with Ollama beside colibri an unnamed pull
  still goes to Ollama.
- `/api/status` nodes carry a `colibri` block (null for a node without a catalogue).

## Verified against the real engine

Windows 11 host, Docker Desktop 27.3.1. A coordinator built from this commit, and a node image built
from it (`Dockerfile.colibri`, colibri v1.12.1), with OLMoE int8 (7 GB) mounted twice — as `olmoe` and
`olmoe-b` — `MaxLoaded: 1`, `OnDemand: true`, `IdleUnload: 00:01:30`.

- The node started with nothing loaded and reported both models; the hub listed both as `chat, score`,
  `unloaded`.
- `/v1/chat/completions` on `olmoe` through the hub: the node launched `coli serve --model /models/olmoe`,
  the engine answered `/health` after **5.9 s**, and the hub answered "Paris" in **32.6 s** end to end.
  The engine process held ~6.4 GB RSS.
- `olmoe-b` next: the log shows `Stopping colibri model 'olmoe' … to load 'olmoe-b'` **before**
  `Loading colibri model 'olmoe-b'`, one `coli serve` process afterwards, "Paris" in 31.0 s.
- Hub **Load** of `olmoe` (admin route): profile `node:{id}` revision 1 with `colibri.loaded: [olmoe]`;
  the node stopped `olmoe-b`, loaded `olmoe` in 5.3 s, reported it `loaded`, pinned.
- `olmoe-b` while the only slot was pinned: **502 in 0.23 s** — "every one of this node's 1 colibri
  slot(s) holds a model the hub pinned (olmoe); unpin one or raise Colibri:Serve:MaxLoaded to load
  'olmoe-b'" — on both `/v1` and `/api/chat`.

- Hub **Unload** of `olmoe`: profile revision 2 with an empty pin set; the node logged `Stopped colibri
  model 'olmoe' (the hub unpinned it); its RAM is free.` and no `coli serve` was left in the container.
- On demand: with `olmoe` **pinned**, it stayed loaded at 121 s idle (`IdleUnload` 90 s). Unpinned,
  `olmoe-b` loaded by a chat at 19:08:59 was stopped at 19:10:32 — `idle for 00:01:30, on demand` — and
  the container had no engine process left.
- Hub **Keep loaded** (`on-demand/disable`): profile revision 3, the node logged `colibri on-demand off`
  and reported `onDemand: false`.
- `POST /v1/brio` on `olmoe` while nothing was loaded: the catalogue loaded it and answered "Thursday"
  (p = 0.9988) in 32.8 s.
- No prompt text in the hub's or the node's log.

**Found by the live run, fixed before this release:** the console showed a model's idle time frozen at
the value of the node's last report (up to a minute old); it now counts forward from the report's
timestamp. And a profile revision that only *removed* pins was logged by the node as "nothing to
change"; it now says `colibri: no model pinned`.

## Not established — said out loud

- **Two different models.** Only one converted model exists on the test box; the catalogue was two names
  for the same weights. Switching, pinning and idling are per name and per process, so this is what they
  act on, but a second family's load time was not measured.
- **`MaxLoaded` above 1 against the real engine** (two `coli serve` side by side): covered by the tests
  with sockets, not run with Python. Note that each `coli serve` sizes its expert cache from what the OS
  still offers when it starts (it logged a 116.8 GB budget, 88 %, on this 125 GB box), so two loaded at
  once do not split the RAM evenly; cap them with colibri's own settings if that matters.
- **A cold load longer than the hub's dispatch deadline** (300 s by default): the request then fails at the
  hub while the model keeps loading on the node. Warm it first, or raise `Dispatcher:Deadlines:chat`.
- The refusal when every slot is pinned is a **502** (a failed job), not a 503 with `Retry-After`: waiting
  does not help until somebody changes a pin.

## The published-image check

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.62.0` and `inferhub-node:3.62.0-colibri`, both with
`org.opencontainers.image.revision` = `45604b6` (the tag), on a Docker network with keys on (host to
container is not loopback). The node: `Colibri__Serve__Model=` cleared, `ModelsDir=/models` with OLMoE
mounted as `olmoe` and `olmoe-b`, `OnDemand=true`, `IdleUnload=00:01:30`.

- The hub listed both models `unloaded`, kinds `chat, score`. A chat without a key: 401.
- Chat on `olmoe`: 200 "Paris" in 30.2 s (cold). Chat on `olmoe-b`: 200 in 30.2 s, with `olmoe` stopped
  first — one `coli serve` in the container afterwards.
- Pin `olmoe` from the hub: 401 without the admin key, 200 with it; profile revision 1, the node stopped
  `olmoe-b` (idle 0.7 s) and loaded `olmoe` in 5.3 s. `olmoe-b` while pinned: 502 with the sentence.
- Unpin: revision 2, the node logged `colibri: no model pinned` and `Stopped colibri model 'olmoe' (the hub
  unpinned it)`; no engine process left. A chat then loaded `olmoe-b` (200, 30.7 s), and on demand stopped
  it `idle for 00:01:44` (the sweep runs every quarter of `IdleUnload`).
- No prompt text in either container's log.

## Upgrading

Nothing to change. A node with one `Colibri:Serve:Model` behaves as before. A v3.62 node against an older
hub works; the hub just has no Colibri panel for it.
