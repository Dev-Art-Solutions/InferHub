# InferHub v3.94.0: a node offers a slice of its memory to groups

Phase 134, the second of the sharded-models track. In 3.93 the hub could say which nodes *together* would hold a model
none holds alone. But nothing on a node said "this much is for groups", nothing ran to take it, and the node's own models
still counted every byte. Now the owner sets an amount, and the node runs llama.cpp's RPC worker for it and keeps that
memory away from its own models.

```jsonc
// node.local.json, the app's Settings tab, or PUT /api/control/settings, then restart the node
"Node": { "Shards": { "Enabled": true, "RamGiB": 24 } }
```

Off by default. A node that changes no configuration behaves exactly as in 3.93: nothing is launched, nothing is reported.

## What it does

- **A supervised worker.** The node runs llama.cpp's own `ggml-rpc-server`, found beside its llama.cpp engine's
  `llama-server`, or named in `Node:Shards:Executable`. The Windows release of llama.cpp and winget ship it. The node
  restarts it if it exits (backing off from 2 s to 60 s), and a node that is stopped or killed takes it along.
- **Loopback only, with no setting to change it.** The worker always gets `-H 127.0.0.1`. llama.cpp RPC has no
  authentication, so other nodes will reach it only through InferHub's own tunnel (the next phase), never through an
  open port. There is deliberately no `Host` key.
- **"Running" means it answered.** Every two seconds until it answers, the node sends the worker the handshake a llama.cpp
  head sends first (`HELLO`). The answer carries the worker's **RPC protocol**, which is what has to match across a group:
  winget's b10424 answers **5.0.0**, a recent build (b12182) **8.0.1**, and a head refuses a worker of another version.
  The group estimate keeps the members on the protocol with the most memory offered and names the others.
- **The offer is reserved.** The node's own estimate (*Check fit*, 118) subtracts it from every engine, and says so:
  *"24 GiB of RAM is offered to groups (Node:Shards) — not counted for this node's own models"*. The group estimate
  counts **only** the offer for that node (`offer:ram`). A node that offered nothing is still counted whole, as in 3.93,
  and the plan names it.
- **Where it shows:** the node's row in the console (*offers groups 24 GiB RAM · RPC 5.0.0 · running*, red with the
  worker's own words when it cannot run), `shards` on `GET /api/admin/nodes`, `GET /api/control/shards` on the node, and
  the five keys in the desktop app's Settings tab (the tab is built from the node's settings schema, so no app release).
- **The lab** gained `deploy/lab/shards/node.Dockerfile`: the published node image with the lab's llama.cpp beside it,
  for the next phases.

## Live, from source (2026-11-08)

- shard-a offered 24 GiB of RAM. Its worker was found beside winget's `llama-server.exe`, answered the handshake with
  protocol 5.0.0, and the hub showed it `running`.
- A `llama-server --rpc 127.0.0.1:50060 --device RPC0 -ngl 999` on the same machine loaded Qwen2.5 0.5B **through the
  node's worker**. The worker grew from 32 to 443 MB and cached the tensors, and the model answered at 95 tok/s.
- With shard-b offering nothing, DeepSeek V3.1 Q3_K_S planned layers 0–55 on shard-b (whole) and 56–60 on shard-a
  (23,343 of its 24,576 MiB offer). In 3.93 the Q4 and Q5 variants fit the two machines together. They no longer fit,
  because shard-a now gives what its owner offered, not its whole machine.
- Killing the node outright took the worker with it.

## The published images

`ghcr.io/dev-art-solutions/inferhub-node:3.94.0` and `inferhub-coordinator:3.94.0`, both labelled revision `1ad823e`. The
node image was extended with the lab's llama.cpp (`deploy/lab/shards/node.Dockerfile`, b12182) and run with
`Node__Shards__Enabled=true`, `RamGiB=2` against the published coordinator:

- The coordinator's `/api/admin/nodes` showed the node's worker `running`, protocol **8.0.1**, endpoint `127.0.0.1:50052`.
- A `llama-server --rpc 127.0.0.1:50052 --device RPC0 -ngl 999` in the same container loaded Qwen2.5 0.5B through it (the
  worker at 419 MB) and answered at 90 tok/s.
- From another container on the same Docker network, `pub-shard:50052` refused the connection: the worker is not
  reachable off its machine.

## Found while building it

- **On Windows the worker's banner never arrived while it ran.** The first design read `Starting RPC server vX.Y.Z` and
  the device lines from the worker's output. On Windows, stdout to a pipe is fully buffered, so the banner arrives only
  when the worker exits. "Running" is now the handshake answered. The banner is still read where it arrives (it lists the
  devices).
- **llama.cpp's default port was taken.** Port 50052 was held on this machine by an unrelated `LicenseService.exe`. Windows
  also reserves port ranges (here 50172–50271). Both appear as the worker's own *Failed to create server socket*, in the
  node row, instead of a bare exit code. Pick another `Node:Shards:Port`. The ranges are listed by
  `netsh interface ipv4 show excludedportrange protocol=tcp`.
- **"Has no card" was wrong for a node that has one.** A node with a 3090 Ti that offered only RAM was described as a
  node with no card. It now reads *"offered groups RAM only: its layers run on its CPU"*.

## Not established, said out loud

- **The worker does not enforce the slice.** `rpc-server` has no memory flag (checked in b10424 and b12182), and a head
  sees the machine's whole memory (live: `RPC0: 127.0.0.1:50060 (261985 MiB …)`). What a member holds is the plan's
  layer split, which the hub will send when it runs a group (136). Until then, the offer is a figure the arithmetic keeps.
- **No other machine reached a worker.** That is the tunnel, next phase.
- **A card was not offered.** Only the CPU device ran. `VramGiB` and `Devices` (`-d Vulkan0,CPU`) are wired but were not
  run live.
- **The console row was read as JSON**, not looked at in a browser.

## Upgrading

Nothing to do. `Node:Shards` is off by default. A hub older than 3.94 ignores the report (the node logs a debug line),
and a node older than 3.94 sends none.
