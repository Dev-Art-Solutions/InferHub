# InferHub v3.113.0: who gave how much to groups

Phase 148, the seventh of the shared-compute track (142–149). A group runs on memory and compute that its members' owners set aside.
Until now nothing said how much each node gave. Once a member left, or a spare had stood in for an hour, its part was gone from every view.
The coordinator now keeps that count and shows it. It is shown and never enforced: no credit, no give-to-get.

```
GET /api/admin/shards/contribution
{ "since": "2026-11-24T22:00:58Z", "sampleSeconds": 10, "nodes": [
  { "name": "shard-c", "connected": false, "offerMiB": null, "groupsNow": [], "seconds": 70, "mibHours": 59.729,
    "requests": 6, "promptTokens": 108, "generatedTokens": 256, "shareTokens": 113.8, "groups": [ … ] } ] }
```

## What changed

- **Memory:** a node's offer (`Node:Shards`, reserved whole for groups since 134) is credited for as long as a group its head reports
  `running` lists it among its workers. The coordinator samples every 10 s and reports MiB·hours. A sampler that did not run for longer
  credits at most 30 s, never the whole gap.
- **Tokens:** the `prompt_eval_count` and `eval_count` of every answer a group's head returned go to every worker the head lists for that
  group, because every token crossed every worker. Only an answer from the node that reports the group running counts.
- **Share:** those tokens × the layers the worker held ÷ the group's layers (137's split; the output layer is not counted).
- A node that left keeps its row with `connected: false`. Everything is in the coordinator's memory and starts again with it (`since`).
- The console has a new *Contribution to groups* table under *Shard tunnels*.
- The lab: `deploy/lab/shards/contribution-lab.sh` (on top of `tunnel-lab.sh`: chats, a member stopped, the spare in, three reads).

Only the coordinator changed; the node image is the same code as 3.112.0. Zero new packages; `InferHub.Shared.csproj` still empty; nothing
persisted on the coordinator; no new configuration. A coordinator with no groups lists the nodes that offer memory, all at zero.

## What was measured

The 8B over a head (shard-a), two members (shard-b, shard-c) and a spare (shard-d), 3 GiB offered each, CPU, one PC. Coordinator built from
source, nodes on the published `inferhub-lab-node:3.112.0`. The head split the model 11 : 11 : 10 layers.

| Read | shard-a | shard-b | shard-c | shard-d |
|---|---|---|---|---|
| After start (2 answers) | 2 · 168 tok · share 57.8 | 2 · 168 · 57.8 | 2 · 168 · 52.5 | 0 |
| After 4 chats | 6 · 364 · 125.1 | 6 · 364 · 125.1 | 6 · 364 · 113.8 | 0 |
| shard-c stopped, the spare in, 4 chats | 10 · 560 · 192.5 | 10 · 560 · 192.5 | 6 · 364 · 113.8, `connected: false` | 4 · 196 · 61.2 |

Memory followed the time each node spent in the running group: 100 s and 85.3 MiB·h for the head and shard-b, 70 s for shard-c (until it
stopped), 30 s for the spare. Every chat answered 200 in 4.9–5.9 s. Files in `docs/research/runs/2026-11-25-contribution-source/`.

## Not established, said out loud

- **Machines of several owners**, which is what the number is for. One PC, one owner.
- A real network (none was shaped in this run; the count does not depend on it).
- Numbers across a coordinator restart: they start again, by design. A durable record would be the audit trail's (125).
- A draft model (146) works on the head's CPU and is not counted as anyone's share.
- In a cluster each coordinator counts what passed through it (132); nothing sums them.
- The spare's first sample after it joined: it is credited from the first 10-second sample that sees the group running with it, so up to
  10 s of a stint can be missed at each change.

## Checks

- `dotnet test InferHub.sln`: 3 020 passed, 70 skipped, 2 failed. The two are `ChatGptBackendTests`, which fail only on this machine:
  the node test folder picks up the coordinator's `appsettings.json`. Nothing in this release touches them.
