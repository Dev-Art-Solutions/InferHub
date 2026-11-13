# InferHub v3.99.0: nodes behind a home router meet at the coordinator's relay

Phase 139 of the sharded-models track. A community is people at home, and a machine at home sits behind a router: it dials
out to its coordinator, and nothing can dial it. Until now such a node was listed as a peer, every round trip to it read
*no answer*, and a group with it in never started.

The one machine both sides already reach is the coordinator. So the coordinator can now run a relay. A node that opted in
dials **out** to it and leaves three connections waiting. A node that wants to reach it dials the relay too and names it.
The relay joins the two connections and copies bytes.

```jsonc
// the coordinator — open this TCP port beside its own
"ShardRelay": { "Enabled": true, "Port": 50054 }
// each node that may use it
"Node": { "Shards": { "Enabled": true, "RamGiB": 3, "TunnelRelay": true } }
```

Nothing changes for a deployment that sets neither key: nothing listens, and no node is told of a relay.

## What it does

- **The relay cannot read what it carries.** The two nodes run the same mTLS tunnel *through* the relay, with the same
  certificates, and each checks the other's thumbprint from the coordinator's list, exactly as on a direct path. The
  relay holds no key to it. Who may reach a worker is still decided in one place.
- **A direct path always wins.** A node tries the direct path first, every time (three seconds to connect when a relay
  is the alternative), and uses the relay only when that fails. The round-trip measurement tries direct again every
  30 seconds, so a pair that can connect never stays on the relay.
- **Both owners say yes.** The relay is off on the coordinator and off on every node by default. A peer is marked as
  reachable through the relay only if its owner set `TunnelRelay`, and the relay is used only when both ends did.
- **A node proves itself to the relay.** The coordinator gives each such node a secret of its own over its hub
  connection. The relay sends a random nonce and the node answers with an HMAC over it. The secret never crosses the
  relay's wire, and one node cannot wait in another's name.
- **The relay joins only nodes the coordinator listed as peers**, and a node that leaves the lists (revoked, disconnected)
  loses its secret, its waiting connections and every connection through the relay, at once.
- **Visible in the console.** The *Shard tunnels* table marks a round trip **relay**, and each tunnel says whether the
  node is waiting at the relay or why not. `GET /api/admin/shards/relay` shows the pairs carried now, the bytes carried
  and which nodes wait. `peers[].via` on a node's shard state is `direct` or `relay`.

## Measured in the lab

`deploy/lab/shards/relay-lab.sh`: a community group of three. shard-a (the head) and shard-b share a network with the
coordinator; shard-c is on a second network that only the coordinator is also attached to. Docker routes nothing between
two bridge networks, so shard-c can dial the coordinator and nobody can dial shard-c.

- Before anything else, the lab shows there is no path: shard-a cannot open shard-c's tunnel port, and shard-c cannot
  resolve shard-a.
- The group came up **with shard-c in it** (`"members":["shard-b","shard-c"]`, running).
- shard-a ↔ shard-b stayed **direct** (0.13–0.24 ms). Every pair with shard-c went through the **relay** (0.35–0.72 ms).
- The group answered **the same text, character for character,** as the same community group on direct tunnels
  (`community-lab.sh`, same day): 64 tokens in 10.5 s end to end.
- The relay carried 1.73 GB in 29 connections. Most of that is the model's layers going to shard-c at load.
- Revoking shard-c: no pair left at the relay, shard-c's place there gone, its three attempts to wait again refused, and
  the next request got `503` *group 'llama-8b-community' is not running: only 1 community node(s) can take part now; the
  group wants 2*.

## The published images

Checked after the tag: the next commit fills this section in.

## Not established, said out loud

- **A real router.** A Docker network that routes nothing stands in for NAT. Symmetric NAT, idle timeouts shorter than the
  relay's 20-second keep-alive, and packet loss were not tried.
- **What the relay costs on a real uplink.** In the lab the coordinator shares a host with the nodes, so the extra hop
  costs almost nothing. On the internet every tensor of a relayed pair goes to the coordinator and back out, and the
  coordinator's bandwidth is the limit. 133 measured what a 30 ms round trip does to a split model (2.29 tok/s); a relay
  adds a second leg to it.
- **The relay port is plain TCP.** What it carries is TLS between the nodes. The hello before that is in the clear: it
  holds node ids and an HMAC, never the secret.
- **A cluster of coordinators.** Each coordinator relays for the nodes connected to it only.
- **No hole punching.** Two nodes that could reach each other with help from a rendezvous service still go through the
  relay. That is a decision: the relay is correct first.
- **The console was read as JSON**, not looked at in a browser.

## Upgrading

Nothing to do. To use the relay: turn it on at the coordinator, open its port, and set `Node:Shards:TunnelRelay` on the
nodes that may use it. A node older than 3.99 ignores the relay and is reached directly or not at all, as before.
