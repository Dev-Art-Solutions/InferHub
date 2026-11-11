# InferHub v3.97.0: a group survives a member leaving

Phase 137 of the sharded-models track. In 3.96 a group that lost a member simply ended: the head stopped, the model left
the catalogue, and a client got `model 'llama-8b-split' not found`, the answer that means "no such model". A member whose
owner paused it was not even noticed. Now a spare stands in, the gap is a clear `503` with its reason, a planned stop lets
the requests in flight finish, and the console shows which machine holds which layers.

```jsonc
"ShardGroups": { "llama-8b-split": { "Head": "shard-a", "Members": ["shard-b", "shard-c"], "Spares": ["shard-d"], "Model": "llama31-8b.gguf" } }
```

Without `Spares` and without groups, nothing changes.

## What it does

- **Spares stand in.** For each member that cannot take part, the coordinator uses the first spare that can, each spare
  once, never the head. The head drains and restarts over the new set. `GET /api/admin/shards/groups` names each
  substitution: *shard-d stands in for shard-c (member 'shard-c' is not connected)*.
- **"Can take part" includes the owner's word.** A member that is cordoned, or that its owner paused (by hand, outside its
  sharing hours, idle mode, or its daily allowance used), is not ready, just like one that disconnected. With groups
  configured the coordinator reacts to a pause or a cordon at once, not at the next report.
- **The gap is a 503 with the reason, not a 404.** While a configured group answers nobody, a request for it gets `503`
  with `Retry-After: 15` and one of: *group '…' is not running: <why>*, *is reassembling: <who stands in for whom>*, or *is
  starting on <head>*.
- **A planned stop drains.** When the coordinator takes a group back, the head takes no new request for it, lets the
  requests in flight finish (up to 60 seconds), and then stops it. The same group starts again only after that, because two
  servers on one worker would hold the layers twice.
- **Who holds which layers.** The head reads the model's layer count from its file and applies llama.cpp's own rule for
  dividing layers over a split. The console shows *shard-a 0–10 · shard-b 11–21 · shard-d 22–31 + output*.

## Measured in the lab

`deploy/lab/shards/tunnel-lab.sh` (four nodes in Docker, shard-d the spare) and then `survive-lab.sh`:

- **Revoking shard-c:** every request got `503`, `Retry-After: 15`, *group 'llama-8b-split' is reassembling: shard-d stands
  in for shard-c (member 'shard-c' is not connected)*, for 57 seconds. Then `200`, with **the same text as before the revoke**,
  character for character. The layers moved from shard-c to shard-d.
- **Pausing shard-b through its own control API during a 200-token answer:** the head showed the group `draining`, the
  answer completed with all 200 tokens, and the next request got a `503` saying why.

## Found while building it

- **The node images could not save a setting.** `node.local.json` is the file the node's control API and the desktop app
  write when a setting changes live. It defaulted to `/app`, beside the binaries, which the image's `app` user cannot write.
  So pausing a node in a container returned `500 could not write /app/node.local.json`, and so did every other live change.
  Every node image now keeps that file in `/data` (`INFERHUB_NODE_LOCAL_SETTINGS`).

## Not established, said out loud

- **A worker that vanishes mid-request still kills that request.** llama.cpp stops its head when a worker's connection
  dies. The drain is for planned leaves (a pause, a cordon, the coordinator taking the group back); a crash or a revoke fails
  the requests in flight with the head's error, and the next request waits for the reassembled group.
- **No fail-back.** When the original member returns, the group stays on the spare until something else changes it.
- **Spares are not chosen by fit.** They are used in the order the admin listed them, weighted by their offer.
- **The 57-second gap** is the head reloading an 8B model over the network into the spare's worker. It was not measured with
  GPUs or larger models, or over a real WAN.
- **The console** was read as JSON, not looked at in a browser.

## Upgrading

Nothing to do. Groups without `Spares` behave as in 3.96, except that a paused or cordoned member now takes a group down
(drained), and a request in the gap gets a `503` instead of a `404`.
