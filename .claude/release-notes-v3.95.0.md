# InferHub v3.95.0: nodes reach each other's workers through a tunnel the coordinator allows

Phase 135, the third of the sharded-models track. In 3.94 a node could offer a slice of its memory to groups, and ran
llama.cpp's worker for it, on its own machine only. Nothing else could reach it, by design: llama.cpp RPC has no
authentication. Now nodes reach each other's workers through their own **tunnel**: TLS, both ends showing the certificate
their coordinator issued, each accepting only the nodes the coordinator listed. The console shows how long a round trip
takes between every pair.

```text
node A: llama-server --rpc 127.0.0.1:41873  ──TLS, both certificates──▶  node B :50053 ──▶ 127.0.0.1:50052 ggml-rpc-server
                    (A's door to B)                        B accepts only the fingerprints its coordinator sent it
```

Nothing changes for a node that does not offer memory, or that has no certificate.

## What it does

- **The coordinator decides who may connect.** It sends every node its peers: other nodes connected to it whose worker
  runs, whose tunnel listens, that hold an active certificate it issued (phase 127), in the same organisation (130). Each
  peer comes with the SHA-256 fingerprint of its certificate **from the coordinator's own record**, never from what the
  node reported. The tunnel's TLS checks it at both ends: the node being dialled refuses a certificate not on its list, and
  the dialling node refuses a peer that is not the certificate listed for that address.
- **Revoke closes the door at once.** A revoke aborts the node's connection (127). The coordinator then sends everyone a
  new list without it, and each node cuts the connections that node still had open.
- **A local door per peer.** For each peer, the node opens `127.0.0.1:<port>`, shown in `GET /api/control/shards` as
  `peers[].local`. A `llama-server` on that node uses those as its `--rpc` endpoints. The RPC stream passes through
  untouched, so llama.cpp needs nothing new.
- **Round trips, measured through the tunnel**, every 30 seconds and at once when the list changes: five pings, the
  median. The console has a new **Shard tunnels** table, node × node, green under 2 ms, amber under 40 ms, red above or
  with the error.
- **Where a peer is reached:** `Node:Shards:TunnelAdvertise` (`host:port`), or by default the address the node's
  coordinator connection comes from, with `Node:Shards:TunnelPort` (50053). Set it by hand behind NAT or in a container
  network.

## Measured in the lab

`deploy/lab/shards/tunnel-lab.sh`: a coordinator built from this tree with node certificates on, three nodes in Docker
enrolled with one-time tokens, each offering 3 GiB, each limited to 4 CPUs and 4 GB. A `llama-server` in shard-a split
Llama 3.1 8B Q4 over its own worker and its doors to the other two.

| Setup | Round trip | Generate | Text vs the whole model |
|---|---:|---:|---|
| whole model, one container | — | 6.71 tok/s | — |
| through the tunnels, unshaped | 0.25–0.39 ms | **6.29 tok/s** | identical |
| through the tunnels, 15 ms each way on all three | ~30 ms | 2.93 tok/s | identical |
| 3.93's lab, plain RPC, unshaped / 15 ms | — | 6.42 / 2.29 tok/s | identical |

- **TLS costs about 2 %.** The shaped run is faster than 3.93's because the head here sits beside one of the workers, so
  a third of the layers never cross the network. The coordinator's group plan will use that: the head is a member.
- **The table follows the network.** With only shard-c shaped (15 ms one way), a↔b stayed at 0.3 ms and every pair with c
  read 15.4 ms.
- **Revoke:** revoking shard-b dropped it from both other lists within seconds; shard-a then refused it by name
  (*certificate 0d6189fd0774… is not one the coordinator listed*).

## Found while building it

- **The tunnel started before the certificate existed.** A node enrolls when it first connects to its coordinator, a few
  seconds after its services start, so the first lab run showed every tunnel *off: no node certificate* next to a
  certificate fingerprint. The tunnel now waits for the certificate, then applies the list it may already hold; the
  coordinator does not send the same list twice.
- **A worker in a small container crawled.** Without `-t`, llama.cpp's worker runs as many threads as the host has cores;
  in a 4-CPU container the split ran at 2.5 tok/s instead of 6.3. Set `Node:Shards:Threads` to the container's CPUs.
- **A test that failed one run in ten** found that a measurement interrupted by the node stopping made the stop itself
  throw. Stopping is not a peer's failure any more.

## Not established, said out loud

- **A head does not survive a lost worker.** When shard-b was revoked, the head using its worker died: llama.cpp aborts
  when a worker goes away. A group that survives a member leaving is phase 137.
- **No node behind NAT.** The default address is the one the coordinator sees, which behind NAT is the router's. A relay
  is phase 139.
- **Not across an active-active cluster.** Each coordinator lists only the nodes connected to it.
- **No real WAN.** netem adds delay and a rate limit, not loss or jitter.
- **The console table was read as JSON**, not looked at in a browser.
- **The door is the machine's.** Anything on a node can use its local doors to its peers. Between nodes of one private
  cloud that is the premise; community mode (138) revisits it.

## Upgrading

Nothing to do. A node without `Node:Shards` or without a certificate opens no tunnel. A coordinator older than 3.95
sends no peers, and a node older than 3.95 ignores them.
