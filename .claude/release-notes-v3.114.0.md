# InferHub v3.114.0: the shared-compute track, checked in one command

Phase 149 closes the shared-compute track (142–149). Seven releases built it, and each was checked on its own images on its own
day. Nothing had run all of it on one release, and 140's verifier stopped at 139. Now one command runs the whole of 133–148 on
the images a person pulls and prints PASS or FAIL for each claim:

```sh
LAB_MODEL_DIR=lab-models VERIFY_BIG=1 VERIFY_REVISION=<commit> deploy/lab/shards/verify-lab.sh 3.114.0
```

## What changed

- `deploy/lab/shards/verify-lab.sh` has new sections 5–10:
  - members that load their layers from their own disk (143), and a request that survives a member vanishing, then the member
    taking its place back (145);
  - the coordinator placing the group itself (144): the head holds the file, the member is the near one, the placement says why
    the far ones were left out;
  - a draft model on the head (146);
  - four requests at once, with the head reporting the group's own four slots (147);
  - the contribution count (148), with a member stopped and the spare standing in;
  - behind `VERIFY_BIG=1`, 142's 70B over three 18 GiB containers.
- At temperature 0 the group must give the same first answer whether it loaded over the network, from the members' disks, was
  placed by the coordinator, or ran with a draft.
- `contribution-lab.sh` prints the stopped member's exit code. If the group does not run again, it keeps the containers' logs.
- **Fixed: a shard node stopped with an unhandled exception.** `ShardSeeder`, `ShardWorker`, `ShardTunnel`, `ShardGroupHost` (node)
  and `ShardRelay` (coordinator) are each registered as themselves and as hosted services. The DI container disposed each one
  twice. The second dispose cancelled a token source the first had already disposed. On 3.113.0, `docker stop` on any node with
  `Node:Shards` ended in `ObjectDisposedException` with exit code 139, and the rest of the container's disposal was skipped. The
  second dispose now finds nothing to cancel. `ShardShutdownTests` runs the shipped wiring: start, stop, dispose the container.

No new configuration, zero new packages, `InferHub.Shared.csproj` still empty, nothing persisted. A deployment that changes no
config behaves the same, except that a shard node now exits 0 when stopped.

## What was run

The published 3.113.0 images (revision `0f3fbf0`), the lab node built on the published node, on one PC (Docker, 64 CPUs).
Models: Llama 3.1 8B Q4_K_M, Llama 3.2 1B as the draft, WizardLM 70B for section 10. The verifier printed **56 held, 1 did not**:

| Section | Claim | 3.113.0 |
|---|---|---|
| 1–4 (140) | private group, member leaving and drain, community, relay | 28 / 28 |
| 5 (143, 145) | own disk; same text; request through a member's death; place taken back | 10 / 10 |
| 6 (144) | auto placement: head shard-a, member shard-d, far ones left out with a reason; same text | 5 / 5 |
| 7 (146) | draft on the head's CPU; same text as without | 2 / 2 |
| 8 (147) | four slots reported; four chats together all answered | 2 / 2 |
| 9 (148) | tokens counted, spare at zero until it stands in, gone member keeps its row; **a stopped member exits 0** | 7 / 8 |
| 10 (142) | a 70B no node holds runs and answers through the hub | 2 / 2 |

The one that failed is the shutdown crash above: exit code 139. In section 9 the spare stood in and the group ran again after
16 s. 148's one-off case, where the spare's group did not run again within 300 s, did not recur.

**On the published 3.114.0 images** (revision `dbd9d49`, required by the verifier): **57 held, 0 did not**. The stopped member exited 0, and the spare stood in after 16 s.

Transcripts: `docs/research/runs/2026-11-25-verify-3.113.0/` and `…-verify-3.114.0/`.

## What was not established

The whole track in one list:

- GPUs in a group.
- Machines of several owners, and a real network. Every round trip is `tc netem` on one PC.
- A real router's NAT, loss and idle timeouts.
- A 70B on anything but CPU containers of one box.
- A draft model or several slots on a group the coordinator placed itself. Each was checked on its own.
- A head that vanishes mid-answer. 145's retry covers members.
- 148's numbers across a coordinator restart. They start again, by design.
