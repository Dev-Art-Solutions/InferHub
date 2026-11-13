# InferHub v3.100.0: verification day — one model across several machines, both modes, checked in one command

Phase 140, the last of the sharded-models track (133–140). Seven releases built it, and each was checked on its own
image on its own day. Nothing had run **all of it on one release**, and each lab script printed what happened for a
person to read. This release adds the command that runs everything on the published images and says pass or fail.

```sh
LAB_MODEL_DIR=D:/models VERIFY_REVISION=<commit> deploy/lab/shards/verify-lab.sh 3.99.0
```

**This release changes no product code.** The 3.100.0 images differ from 3.99.0 only in their version.

## What it does

- **Pulls the published coordinator and node** of the version you name and reads what they say they are. With
  `VERIFY_REVISION` it fails unless both were built from that commit.
- **Builds the lab node on the published node.** The published node carries no llama.cpp (a node uses its owner's), so
  the lab adds its own beside it and changes nothing else.
- **Runs the four labs a person runs by hand** — `tunnel-lab.sh`, `survive-lab.sh`, `community-lab.sh`, `relay-lab.sh` —
  keeps each transcript, and checks it. The verifier is not a second implementation: a check and the thing you run by
  hand are the same file.
- **Prints PASS or FAIL per claim** and exits 0 only when every one held.

## The run: 3.99.0, published images, 28 of 28

Both images carried version 3.99.0 and revision `024da81`. On this box (64 cores, Docker Desktop, an 8B Q4 model, 3 GiB
offered per node, 4 CPUs a node):

| Mode | Claim | Held |
|---|---|---|
| Private cloud (136) | the group runs on its head and the split model answers through the coordinator | yes |
| A member leaves (137) | the gap is a 503 with `Retry-After: 15` and the reason; the spare stands in; **the same text after reassembly**; the head reports who holds which layers | yes |
| A planned stop (137) | a request in flight completes during the drain (200 tokens); the next gets a 503 with the reason | yes |
| Community (138) | the coordinator picked the two community nodes and never the one that did not opt in; the sealed node refused to start naming the door; `/v1/models` says `inferhub-community`; 403 without the header, the answer with it | yes |
| Community across NAT (139) | no path either way between shard-c and the others; the group runs with shard-c in it; a ↔ b direct, every pair with c through the relay; the group answers; a revoke leaves no pair at the relay and the group stops with the reason | yes |

The relayed group gave the same text as the same community group on direct tunnels.

## What the day found

Nothing in the product. Two things in the checking itself, both fixed here:

- **The verifier's first version expected the wrong sentence.** After shard-b's owner pauses it, the next request gets a
  503. The check expected that 503 to name the pause. It names the first thing the group is missing, which at that point
  is the member revoked a minute earlier (*member 'shard-c' is not connected, and no spare can stand in*). The product was
  right and the expectation was not. The check now asks for a 503 that says why the group is not running.
- **`relay-lab.sh` printed an empty table of who reaches whom.** It read `/api/admin/nodes` as an object with a `nodes`
  field; it is an array. The numbers were always there in the JSON. Caught on the first source-built run, before 3.99.0
  was tagged.

## Not established, for the whole track, in one list

The lab is Docker on one PC. It shows that the mechanism works and what latency costs. It does not show these:

- **GPUs in a group.** Every run was on CPUs. A worker offering a card, or a card and RAM together, was never run.
- **Models above 8B.** The point of the track is models no machine holds, and the lab's model fits one machine. The
  containers are limited so that none holds it alone.
- **More than three workers** in one group.
- **Real networks.** `tc netem` adds delay and a rate limit, never loss or jitter. A Docker network that routes nothing
  stands in for a home router; symmetric NAT and short idle timeouts were not tried, and the relay's cost on a real
  uplink was not measured.
- **A worker that vanishes mid-request.** llama.cpp aborts the head, and that request fails with its error. The drain
  covers planned leaves only.
- **Fail-back.** A member that returns while its spare serves stays out until the list changes again.
- **A cluster of coordinators.** Each coordinator lists peers, runs groups and relays for its own connections only.
- **Anything that hides a prompt from the machines in a community group.** There is nothing, by decision. The owner's
  two keys, the admin's group and the client's header make it a choice.
- **The console in a browser.** Through the whole track it was read as JSON.
- **The slice a node offers is not enforced by the worker.** llama.cpp's RPC server has no memory flag; what a member
  holds is the group's tensor split.

## Why it is not a CI job

It needs a 4.4 GiB model, five containers and about ten minutes of CPU. A runner has none of the three, and a check that
is skipped in CI reads as coverage. It is a command a maintainer runs before saying a release works.

## Upgrading

Nothing to do.
