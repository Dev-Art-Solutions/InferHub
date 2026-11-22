# InferHub v3.110.0: a request that survives a member leaving

Phase 145, the fourth of the shared-compute track (142–149). 137 made a group survive a member leaving, but not the request that
was running when it left: a member that vanishes takes llama.cpp's head down with it (RPC aborts), so that request failed with the
engine's error, and every request in the next minute got a 503 *reassembling*. A client saw an error from a group that answered
again a minute later. Now one setting makes the coordinator hold such a request until the group runs again:

```json
"ShardGroups": { "llama-8b-split": { "Head": "shard-a", "Members": ["shard-b", "shard-c"], "Spares": ["shard-d"],
                                     "Model": "llama31-8b.gguf", "RetrySeconds": 180 } }
```

```sh
LAB_MODEL_DIR=lab-models LAB_RETRY=180 LAB_SETTLE=30 LAB_MEMBER_DISK=1 deploy/lab/shards/tunnel-lab.sh
deploy/lab/shards/retry-lab.sh
```

## What changed

- **`RetrySeconds` (default 0, no change).** A request on the group that fails before the caller received anything waits at the
  coordinator until the group serves again, and is sent again, as often as that happens before the time runs out. Counted: a
  non-streaming failure that is not a 4xx, a stream whose first chunk is the node's error, a head that dropped. After the time,
  the first error as it was. A refusal (4xx) is never retried; a stream that already sent text is never retried (a second answer
  would be spliced onto the first).
- **The wait does not race the disconnect.** A killed member takes the head down in seconds, sometimes before the coordinator has
  heard that the member left, so the group can still look healthy. The retry needs the group serving again *and* either an
  interruption the coordinator saw since the failure or 5 s.
- **A request that arrives while the group reassembles waits too**, instead of 137's 503 (which it still gets if the group serves
  nobody when the time is up). It holds its admission place while it waits.
- **A member that comes back takes its slot back from its spare after `SettleSeconds` (30).** Found reading the source: a private
  group is computed afresh on every look, so a member was back in its slot the moment it could take part — and 137's notes said
  the opposite ("no fail-back"). A member that dropped and returned cost two reloads. Now the spare keeps the slot until the member
  has held still, the status says *shard-d stands in for shard-c, which is back: it takes its place back in 29 s*, and a timer
  looks again. A group that was not running when the member returned takes it at once (no reload to save). When two spares are
  configured, the one standing in keeps the slot if an earlier one returns.
- **`FailBack: true` on an auto group** places it again when a node its placement lost is back and settled (*placed again:
  shard-c is back*). Off by default: 144's rule stands, a kept placement moves only when it cannot run.
- **`substitutions` on `GET /api/admin/shards/groups`**, and a row per group with a stand-in in the console. Found in the lab:
  137 computed it and never showed it.

No node change, no wire change, zero new packages.

## What was measured

The 8B (Llama 3.1 Q4) over four nodes in Docker on one PC, 4 CPUs and a 3 GiB offer each, CPU only, built from source; shard-a the
head, shard-b and shard-c the members, shard-d the spare; every node holds the file (`LAB_MEMBER_DISK=1`). `RetrySeconds` 180.

| Moment | What a client got |
|---|---|
| A 128-token request, alone | 200 in 20.8 s |
| The same request, `docker kill shard-c` 6 s into it | **200 in 41.3 s, the same text**: the hub took the group back, shard-d stood in from its own cache, the head reloaded, the request went again |
| A short request sent 5 s after the kill | 200 after 9.5 s (3.109: a 503 *reassembling*) |
| `docker start shard-c` | re-registered; status *takes its place back in 29 s*; shard-c had its slot 30 s later |
| A short request sent during that reload | 200 after 11.6 s |
| The 128-token request with shard-c back | 200 in 20.8 s, the same text |

The hub's log for the killed request: *failed before the caller received anything (An error occurred while sending the
request.); it waits up to 172 s for the group to run again* … *Sending job … on shard group llama-8b-split again, to shard-a*.

## What is not established

- **A real network and a real loss.** A killed container closes its sockets at once, so the coordinator hears it in milliseconds.
  A pulled cable or a sleeping laptop leaves it waiting for SignalR's timeout (30 s by default), and that wait counts against
  `RetrySeconds`.
- **A 70B within `RetrySeconds`.** The 8B reloaded in about 15 s from the spare's cache; a 70B reload is minutes (142). Size the
  setting to the model.
- **Community groups.** Their members are still the largest offers on every look, so a larger community node arriving moves one
  — a reload the hub does not hold back.
- **A request that died after its first token** fails as before; a stream cannot be retried once it has said something.
- **GPUs**, more than four nodes, several requests in flight at once on a reassembling group (each waits on its own; nothing was
  measured at load).

## Tests

`ShardGroupRetryTests` (new, 6): a blocking request and a stream retried after the group runs again, a dropped head waited for, a
refusal / a started stream / `RetrySeconds` 0 returned as they came, the first error when the group does not come back, a request
arriving during reassembly waits. `ShardGroupsTests` (+6): a member's return waits `SettleSeconds`; a group not sent takes it at
once; the standing spare keeps the slot; interruptions counted and the retry window; an auto group with and without `FailBack`.
