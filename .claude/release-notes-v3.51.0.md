# InferHub v3.51.0 — the hub routes to the node whose card is already warm

v3.50 added `Node:OnDemand`, which lets a node's one card serve chat, speech, images and video in
turn. The coordinator did not know about it. It picked the least busy node that held the model. In a
fleet of two on-demand nodes, where node A's card held diffusion and node B's held Ollama, a chat
could land on A. There it waited for the image job to drain, for the diffusion worker to stop and for
the chat model to load, up to `SwitchWaitSeconds`, while B could have answered at once. Then A's own
image queue waited for the switch back, so one misrouted request cost two switches.

v3.51 tells the hub who holds each card and lets the router avoid the switch where it can.

## What's new

An on-demand node now sends its card's state on every heartbeat, and again as soon as the card
changes hands:

```json
"onDemand": { "holder": "tool:diffusion", "warmFor": ["image", "video"], "switching": false, "waiting": 0 }
```

When more than one node can serve a request, the router prefers, in this order:

1. **Warm**: the node's card already holds the service this request needs.
2. **Free**: the node's card is idle.
3. **Cold**: another service holds the card, or it is mid-switch.

The existing strategy (least-busy or throughput, plus conversation affinity) then runs inside the
best group that has any nodes. **It is a preference and never a refusal.** If the only node that
holds the model is busy with something else, the request still goes there and waits, exactly as it
did in v3.50.

- **A node that does not run on demand counts as always warm.** A fleet with no on-demand nodes has
  one group, and its routing is exactly v3.50's. The same holds for a node older than v3.51, which
  sends no state at all.
- **Conversation affinity holds only inside the chosen group.** A conversation whose node gave its
  card to diffusion moves to a warm node. A warm KV cache is worth less than a whole weight reload.
- **The node decides what "warm" means.** It maps its own services to capabilities (`tool:diffusion`
  means `image` and `video`; `ollama` means whatever its backend serves). The hub never learns what a
  tool is.
- **The hub never tells a node to take or release its card.** It does not prefetch or evict.
  `Node:OnDemand` stays the node operator's decision, like `Node:ResourceLimits`.

It is visible in three places:

- `/api/status` gains an `onDemand` object per node. It is `null` for a node that does not run on
  demand. A solo node's `/api/status` reports the same object.
- The console's node row shows `gpu <holder>`, `gpu free` or `gpu switching`.
- A new metric, `inferhub_node_gpu_holder{node,holder}` = 1, appears only for on-demand nodes.
  `holder` is `ollama`, `tool:<id>`, `none` or `switching`.

**There is no new setting.** The behaviour is on whenever `Node:OnDemand` is.

## Verified

- `dotnet test InferHub.sln`: Shared 193, Coordinator 798 (43 skipped), Node 225 (3 skipped),
  Mesh 451 (1 skipped), 0 failed. New tests:
  - `OnDemandRoutingTests` (11), including `NoOnDemandStateMeansOneTierAndTodaysPick`.
  - `GpuArbiterTests` (+3: snapshot, "off reports nothing", `Changed` on transitions only).
  - `PrometheusMetricsTests` (+1).
  - `OnDemandRoutingMeshTests` (3, over a real SignalR hub, including a node that sends the pre-3.51
    heartbeat).
- **Live, from source**: a coordinator and two meshed nodes, both with `Node:OnDemand` on, against
  this box's Ollama. An echo-tool request went to node A, and the hub showed `tool:echo` as A's
  holder about a second later, through the out-of-band heartbeat. Three chats with different
  messages all went to node B. **Node A's log shows no wait for the GPU.** A model another program
  had loaded through the same Ollama stayed resident throughout.

**Not verified:**

- A real CUDA diffusion or Whisper worker switching. The non-Ollama service in the live run was the
  echo tool, a real child process but not a GPU one. This is the same open item as v3.50.
- A v3.51 node against a v3.50 hub. It relies on SignalR's JSON protocol ignoring the unknown
  member, which it does, but the pairing was not run.
- The console pill in a browser. The payload it reads was checked, not the rendering.

One flake was seen once: v3.50's `OnDemandToolTests` failed a single time while all four test
projects ran in parallel. It passed alone and in a full rerun of its project. It waits 15 s for a
real worker process to exit, and machine load is the suspect. It is not proven unrelated.
