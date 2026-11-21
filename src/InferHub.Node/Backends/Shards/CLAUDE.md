# InferHub.Node/Backends/Shards — agent context

**Scope: `src/InferHub.Node/Backends/Shards/`, the shard contracts in `src/InferHub.Shared/Fit/` (`GroupFit`, `ShardOffer`, `ShardSeed`, `ShardPlacement`), the hub's
`Services/NodeShardRegistry.cs` and `Services/ShardRelay.cs`, and the lab in `deploy/lab/shards/`.** One model split by layers across
several nodes: the group estimate, a node's shard worker, the mTLS tunnel between nodes, the groups a coordinator assembles
(private cloud and community), the relay for nodes with no direct path — and since 142 the track that takes it past the 8B lab:
a 70B no node holds, (143) a member that loads its layers from its own copy of the file, and (144) a group the coordinator places itself.

> **Read the root `CLAUDE.md` first, then `src/InferHub.Node/Backends/CLAUDE.md`** (118's estimate, 95's `EngineProcess`, 120's
> management jobs — all used here). The invariants that bind hardest: **the RPC port is never reachable off the node**
> (llama.cpp RPC has no authentication), **a sealed node never joins a community group**, and since 142 **weights travel hub → node
> only** — a member that lacks a model file gets it through its coordinator's relay or supply (122–124), never from another node
> and never from the internet by itself.

**Split out in phase 142**, for 95's reason: the Backends file was at 1082 of its 1100 lines and the shard blocks (133–140) were
the largest coherent subtree the engines have nothing to do with. They moved verbatim.

## Related context

- The engines, the fit estimate and hub management: `src/InferHub.Node/Backends/CLAUDE.md`
- Node certificates (127), organisations (130), the sealed node and the relay it downloads through (122–124): `src/InferHub.Node/Control/CLAUDE.md`
- The engine measured against other engines, and why it stays (141): `deploy/CLAUDE.md`

## Decisions recorded here

### Phase 133 (a model no node holds, split by layers across several — the group estimate, and the shard lab that measured it)

Files: `src/InferHub.Shared/Fit/GroupFit.cs`, `ManagementJob.Machine` (`Fit/Management.cs`) answered by `Fit/HubManagement.cs`,
hub `Services/GroupFitService.cs`, `POST /api/admin/fit/group` in `Endpoints/ManagementEndpoints.cs`, the console's *Across
nodes*; the lab in `deploy/lab/shards/` (its README holds the measurements). Opens the sharded-models track (133–140).

**D1 — the group estimate is arithmetic in `InferHub.Shared`, over facts each node gave about itself.** One node reads the
model (its own 118 estimate, reader and cache — the first that can; a 400 from it is the link's fault and ends the
request); every other node is asked only `machine` (its `MachineFacts`, no network). *Rejected:* every node reading the GGUF
header — N Range reads, N opinions of one file. *Rejected:* the hub probing hardware — it has no such facts (120 D1).

**D2, load-bearing — whole layers, in proportion, the KV with its layers; alone is 118's own answer.** Per layer: weights ÷
block count + that layer's share of the KV at the context (spread over every layer — conservative for a hybrid); per member
118's working reserve once. A member offers 118's usable VRAM **plus** usable RAM for llama.cpp (the owner's 102 limits
applied): an `rpc-server` takes a device list, card and CPU. **Found live, two nodes from source:** counting the card alone,
each 256 GB box offered 23 GiB, so no group ever held what one node could not — and on paper a 16 GB card with 32 GB of RAM
holds more alone (118 adds them) than three such cards together. **Alone is 118's estimate for llama.cpp on that node** (card and RAM together, its disk check included), so the
two never disagree; a node that holds it alone is the answer, never a group. Otherwise members are taken largest first until
the layers fit, spread in proportion and never past a member's maximum; the head is the first member with free disk for
the file (RPC loads from the head and sends the weights). **Found live:** one node with the memory for every layer whose own
estimate says no (its disk) came out a "group of one" — it is now not a fit, with that node's own reason. *Rejected:* a byte
split — llama.cpp splits by layer, and a byte plan promises a fit a layer boundary breaks.

**D3 — every plan says what a split costs in words; the number is the lab's, here.** `why` names the members, that every
token visits each worker in turn, and which members run layers on their CPU. No tok/s constant in code — one box's network is
not everyone's. **The lab (2026-11-08, b12182 with `GGML_RPC=ON`, CPU, an 8B Q4 over three 2.5 GiB workers):** whole model
6.70 tok/s; split unshaped 6.42, LAN (0.7 ms) 6.05, internet (30 ms, 50 Mbit/s) **2.29**, far (120 ms) 0.59 — the same text,
character for character, at temperature 0 on every profile. A token costs nine to thirteen round trips with three workers
(RPC is a star through the head), so the round trip, not bandwidth, is the price: fewer, larger members. A reload over the
internet profile with the workers' `-c` cache took 180 s. **Track gate: passed** (internet above 1 tok/s).

**D4 — only what the owner allows is asked (120's consent), and every node is listed.** A node that refuses, is older than
3.93 (`'machine' is not a management operation`), or cannot read the model appears in `asked[]` with its sentence; nothing is
installed or started — the endpoint is read-only and audits nothing.

**Not established:** an `rpc-server` offering a card and the CPU together (the lab is CPU-only — D2's capacity assumes it);
GPUs in a group at all; real home networks (NAT, jitter, loss); models above 8B through RPC; more than three workers; a member's
memory reserved for groups (134 — the estimate counts it whole, as `why` says).

### Phase 134 (a node offers a slice of its memory to groups — a supervised llama.cpp RPC worker on loopback, the offer reserved)

Files: `Configuration/NodeOptions.cs` (`ShardOptions`, `Node:Shards`), `Backends/Shards/ShardWorker.cs`, `src/InferHub.Shared/Fit/ShardOffer.cs`
(`ShardOffer`, `NodeShardState`, `ShardBanner`), `MachineFacts.Shards`, `FitEstimator.Ram/Vram`, `GroupFit.Capacity`; `GET /api/control/shards`;
`ReportShardState` → hub `NodeShardRegistry` → `/api/admin/nodes` `shards`; the console's node pill; `deploy/lab/shards/node.Dockerfile`.

**D1, load-bearing — off by default, the offer is GiB, and there is no host key.** `Enabled`, `RamGiB`, `VramGiB`, `Port` (50052),
`Threads`, `Devices` (`-d`), `Cache` (`-c`), `Executable` (default: `ggml-rpc-server[.exe]` or `rpc-server[.exe]` beside a llama.cpp
engine's `Serve:Executable`, or in the PATH directory where a bare `llama-server` resolves). The worker always gets `-H 127.0.0.1`:
llama.cpp RPC has no authentication, so only InferHub's own tunnel (135) may ever reach it. A restart applies it. *Rejected:* a
`Host` key "for the lab" — the one invariant that keeps an unauthenticated tensor port off the network would be a setting.

**D2 — an `EngineProcess`, and "running" is the handshake answered, not the process started.** The worker asks it, every two
seconds until it answers, what a head asks first: `HELLO` (command 14 — `static_assert`ed "must be always 14" in
`ggml-rpc.cpp`), 24 bytes of connection capabilities; the answer's first three bytes are the protocol. Once answered it is not
asked again — a head connected holds the worker's one connection and a second caller would wait, not fail; the process exiting
takes "running" back. **Found live:** on Windows the worker's stdout to a pipe is fully buffered — its banner (`Starting RPC
server vX.Y.Z`, the device lines) arrives only when it exits, so a banner-driven "running" never came. The banner is still read
(where it arrives, it names the devices), and a line with "fail"/"error" becomes the sentence. **Found live:** llama.cpp's default
port 50052 was held on this box by an unrelated `LicenseService.exe`, and Windows reserves ranges (here 50172–50271) — both
appear as the worker's own "Failed to create server socket", in the node row. A node killed outright takes its worker with it (95's job).

**D3 — the group key is the RPC protocol.** Measured: winget b10424 answers 5.0.0, the lab's b12182 8.0.1; a head refuses a
worker of another major. A build number is no key (the lab build, without git, says `build 1`). `GroupFit` keeps the members on
the protocol with the most memory offered and names the rest in `why`. A node that announced none (no worker, or before 3.94) is not filtered.

**D4 — the offer is reserved and is all a group counts.** `FitEstimator.Ram/Vram` lower every engine's usable memory by it, with a
`why` line ("24 GiB of RAM is offered to groups (Node:Shards) — not counted for this node's own models", live). `GroupFit.Capacity`
is the offer (never more than 118 would give the node whole), device `offer:ram` / `offer:card+ram`; a node that offered nothing is
counted whole as in 133, and the plan says which. **The worker does not enforce the slice**: `rpc-server` has no memory flag (b10424
and b12182 `--help`), and a head lists it with the machine's whole memory (live: `RPC0: 127.0.0.1:50060 (261985 MiB …)`). What a
member holds is the plan's `--tensor-split`, which 136 sends; until then the offer is a promise the arithmetic keeps.

**D5 — the hub hears it as other node states.** At registration and on change, only when enabled; filed under the calling
connection's node (as 128's egress), because 136 will trust that row. The app shows the keys in Settings (schema-driven, no app release).

**Live (2026-11-08, from source):** shard-a offering 24 GiB RAM — the worker found beside winget's `llama-server.exe`, running,
protocol 5.0.0; a `llama-server --rpc 127.0.0.1:50060 --device RPC0 -ngl 999` on that machine loaded qwen2.5 0.5B through it (the
worker grew from 32 to 443 MB and cached the tensors) and answered at 95 tok/s. With shard-b offering nothing, DeepSeek V3.1's
Q3_K_S planned shard-b layers 0–55 (whole) + shard-a 56–60 (23 343 of its 24 576 MiB offer); the Q4/Q5 variants that fit both
whole in 3.93 no longer fit — the offer, not the machine, is what shard-a gives.

**Not established:** a worker reached from another machine (135); the slice enforced anywhere but the plan; a card offered
(`VramGiB`, `-d Vulkan0`) — only the CPU device was run; the console pill in a browser (the JSON was read).

### Phase 135 (nodes reach each other's shard workers through an mTLS tunnel the hub allows; the round trips in the console)

Files: `Backends/Shards/ShardTunnel.cs`, `Identity/NodeCredential.cs` (`TlsCertificate()`, `Thumbprint`), `ShardOptions.TunnelPort/
TunnelAdvertise`, the `ShardPeers` handler in `CoordinatorConnection`; Shared `Fit/ShardOffer.cs` (`ShardTunnelState`, `ShardPeer`,
`ShardPeerList`, `ShardPeerState`, `ShardTunnelWire`); hub `Services/NodeShardRegistry.cs` (`ShardPeerPublisher`, the address a
report came from), `NodeHub.ReportShardState`/`OnDisconnectedAsync`; the console's *Shard tunnels*; `deploy/lab/shards/tunnel-lab.sh`.

**D1, load-bearing — the hub decides who may connect, by certificate thumbprint, and both ends check.** `ShardPeerPublisher.Compute`:
a node is a peer while it is connected here, its worker is `running`, its tunnel listens, and it holds an **active certificate in
127's record** — the thumbprint sent is the record's, never what the node reported; peers share the certificate's organisation
(130). Each node gets `ShardPeers` (hub → node) only when its list changed, and an empty list when it stops being anyone's peer.
The TLS server accepts a client certificate only if its SHA-256 is in its latest list; the client accepts the server only if it is
the thumbprint listed for that peer. A list that drops a peer closes its open connections too. Republished on every shard report
and every disconnect, and a revoke aborts the node's connection (127 D5), so **revoke closes the door at once** (lab: seconds).
*Rejected:* trusting any certificate the coordinator's CA signed — a revoked node keeps its file, and there is no CRL. *Rejected:*
SignalR as the transport — 32 KB messages and every tensor through the hub.

**D2 — TLS over TCP on `Node:Shards:TunnelPort` (50053), one mode byte after the handshake:** `R` pipes to the local worker, `P`
echoes 8-byte frames. The node's 127 key and certificate, via a PKCS#12 round trip (Windows' TLS refuses an ephemeral key); the
certificate carries only client-auth EKU, which neither end checks — both decide by thumbprint. No certificate, no tunnel, and the
state says why. **Found in the lab:** a node enrolls when it first connects, after its hosted services start, so the tunnel waits
for the certificate (polls every 2 s) and then applies the list it may already hold — the hub does not send the same list twice.

**D3 — a loopback forward per peer** (`127.0.0.1:<free>`), shown as `peers[].local`; a head on this node uses it as an `--rpc`
endpoint. Anything on this machine can use it — the trust is between machines, the private-cloud premise; 138 revisits it.

**D4 — the round trip is measured through the tunnel**: a TLS connection, five pings, the median, every 30 s and at once on a new
list; in `peers[].rttMs` or `error`. The endpoint is the node's `TunnelAdvertise`, else the address its hub connection came from
(IPv4-mapped unwrapped, IPv6 bracketed) with the port — right on a flat network, wrong behind NAT or a proxy (139).

**The lab (2026-11-09, Docker, b12182, 8B Q4, 4 CPUs a node):** the split over three nodes' tunnels wrote **the same text as the
whole model**, at 6.29 tok/s unshaped against 6.42 for 133's plain RPC — TLS costs ~2 %; on `internet` 2.93 tok/s (133: 2.29 — the
head beside one worker keeps a third of the layers off the network). The matrix read 0.25–0.39 ms; shaping shard-c alone moved
exactly its pairs to 15.4 ms. Revoking shard-b emptied it from both lists and shard-a refused it by name. **Found in the lab:** a
worker without `-t` in a 4-CPU container ran the host's thread count and the split fell to 2.5 tok/s — set `Node:Shards:Threads`.

**Not established:** a node behind NAT (the endpoint from the connection is the router's — 139); nodes on two coordinators of an
active-active cluster (each hub lists its own connections only); a head that survives a lost worker (llama.cpp aborts — 137);
real WAN loss and jitter; the console matrix looked at in a browser (the JSON was read).

### Phase 136 (private cloud: the coordinator assembles a group the admin defined, and the split model answers like any other)

Files: hub `Services/NodeShardRegistry.cs` (`ShardGroupsOptions`/`ShardGroupDefinition`, `ShardPeerPublisher.ComputeGroups` and the
`ShardGroups` send, `ShardGroupStatus`), `GET /api/admin/shards/groups` (`ManagementEndpoints`), `NodeRegistry.ServesOnItsOwn` set in
`Program.cs`; node `Backends/Shards/ShardGroupHost.cs` (`ShardGroupHost`, `ShardGroupBackend`), `ShardOptions.AllowHead/ModelsDir/
HeadExecutable`, the `ShardGroups` handler; Shared `ShardGroupSpec/List/State/Worker`; the console's groups under *Shard tunnels*.

**D1, load-bearing — a group is configuration on the hub; the head is sent it only when every member is ready.**
`ShardGroups:<name>` (Head, Members by name or id, Model, Context). Ready = head and members connected, workers `running`,
tunnels open, every member in the head's 135 list, one RPC protocol. `ComputeGroups` runs inside the 135 publish (every shard
report and disconnect) and sends the head its whole list on change; a group that stops being ready is taken back. The admin
endpoint says per group ready or the first thing missing ("member 'shard-c' is not connected", "…is not one of shard-a's
peers"). *Rejected:* groups stored by an admin API — rule 4, and a group is a fleet decision like a profile.

**D2 — the head's owner consents on its own (`Node:Shards:AllowHead`), and the model is a file name** in `Node:Shards:ModelsDir`
(default: the llama.cpp engine's `Serve:ModelsDir`); a separator, `..` or a rooted path is refused. `HeadExecutable` default:
the llama.cpp engine's `llama-server` path, else the one beside the worker.

**D3 — the head runs `llama-server` under `EngineProcess`:** `-m <file> --rpc <own worker>,<forward per member> --device RPC0..n
-ngl 999 --tensor-split <offers, MiB> -c <context> --host 127.0.0.1 --port <free> --alias <group> [-t Threads]` — every layer on
a worker, the head's own worker first (135: a third of the layers then never cross the network). It waits for every member's
forward; running = `/health` 200, checked every 2 s.

**D4 — the model reaches the catalogue through the head's backend.** On a head only, `ShardGroupBackend` wraps the node's backend:
a request whose `model` is a running group goes to that group's `llama-server` (the same `UpstreamBackend` + `LlamaCppBackend`
shape a llama.cpp engine has), everything else to the node's own; the groups' names are added to the model list as chat
models, and a group change reports the list at once. **Found in the lab, twice:** (1) shard-a's own Ollama was down, so the node
reported its backend unhealthy and the hub refused the group's model with "every node holding model … reports an unhealthy
inference backend" — the hub now treats a node as serving a model it reports as a **running group** whatever its backend says
(`NodeRegistry.ServesOnItsOwn`, from the shard registry); (2) an own engine that lists null makes the node leave the hub's
inventory alone (69), so a group that stopped stayed in the catalogue — once a head has served a group, its list is said even
while its own engine is down.

**The lab (2026-11-10, `tunnel-lab.sh`, three nodes in Docker, b12182):** `POST /v1/chat/completions {"model":"llama-8b-split"}`
on the hub answered from three nodes, **the same text as the whole model** for the same chat request, 64 tokens in 9.8 s end to
end; streaming works. Revoking shard-c made the group not ready, the head stopped it, the model left `/v1/models`, and a request
got `model 'llama-8b-split' not found`. **Found on the published 3.96.0 image (fixed in 3.96.1):** the hub holds a
node's last model list while its engine is unhealthy (69), and that kept a stopped group listed for good; a held list now keeps
the engine's models only — a group's model is known by its digest (`ShardGroupState.Digest`).

**Not established:** a head that keeps answering while a member is replaced (137 — today the group stops and restarts when the
member is back); a fit check before starting (llama.cpp refuses and the state carries the sentence); groups across an
active-active cluster; a GPU in a group; models above 8B through a group.

### Phase 137 (a group survives a member leaving: spares, a 503 with the reason in between, a drain before a planned stop, the layers shown)

Files: hub `Services/NodeShardRegistry.cs` (`ShardGroupDefinition.Spares`, readiness in `ComputeGroups`, `ShardGroupStatus.
Substitutions`, `WhyGroupIsNotServing`, `Watch`), `NodeRegistry.WhyGroupIsNotServing` → `Endpoints/InferenceCore.cs` (the 503);
node `Backends/Shards/ShardGroupHost.cs` (`Acquire`/`Lease`, `stopping`, `DrainAsync`, `Unavailable`, `LayersAsync`); Shared
`ShardLayers.Split`, `ShardGroupLayers`, `ShardGroupState.Draining`; the console's layers; `deploy/lab/shards/survive-lab.sh`.

**D1, load-bearing — a member is ready only if its owner lets it share now; a spare stands in for one that is not.** Ready adds
"not cordoned" and "not paused by its owner" (113/116: idle, paused, out of hours, the allowance used) to 136's list. `Spares` are
tried in order for each member that is not ready, each once, never the head; the status names it ("shard-d stands in for shard-c
(member 'shard-c' is not connected)"). With groups configured the publisher also watches the registry (`Watch`), so a pause or
a cordon is acted on at once, not at the next shard report. *Rejected:* the hub picking any idle node — the members of a private
cloud are the admin's (138 picks from a pool).

**D2 — a configured group that answers nobody is a 503 with Retry-After 15 and the reason**, never 69's 404: in the dispatch path,
after "nobody holds it", `WhyGroupIsNotServing` — *not running: <why>*, *reassembling: <substitution>* or *starting on <head>*.

**D3 — the head drains before it stops a group.** A group leaving `running` goes to `stopping`: no new lease (a request that still
reaches the head is refused "draining", not handed to the node's own engine), the leases in flight finish — up to 60 s — then the
process stops. The same name starts again only after that: two `llama-server`s on one worker would hold the layers twice. Every
request on a group holds a lease (`ShardGroupBackend` → `Acquire`), streams included.

**D4 — the layers are llama.cpp's own rule over the split.** The block count from the head's GGUF header; layer `il` of `n` (the
output is `il = n`) goes to the first device whose cumulative, normalized share exceeds `il / (n + 1)` — `std::upper_bound` in
`llama-model.cpp`. 8B, three equal offers: 0–10, 11–21, 22–31 + output.

**Found in the lab: the node images could not save a setting.** `node.local.json` — what the control API and the app write on a
live change — defaulted to `/app`, which the image's `app` user cannot write, so pausing shard-b through its control API was a 500
(*could not write /app/node.local.json*). Every node Dockerfile now sets `INFERHUB_NODE_LOCAL_SETTINGS=/data/node.local.json` — the
sixth path of the `/app` family the node Dockerfile's own comments list (replicas, node id, retrieval, tool scratch…).

**The lab (2026-11-11, `tunnel-lab.sh` + `survive-lab.sh`, four nodes, shard-d the spare):** revoking shard-c gave every request a
503 with Retry-After 15 and *group 'llama-8b-split' is reassembling: shard-d stands in for shard-c*, for 57 s; then the group
answered with **the same text as before**, the layers moved to shard-d (22–31 + output). Pausing shard-b through its own control
API during a 200-token request: the head showed `draining`, that request completed (200 tokens), the next got a 503 naming why.

**Not established:** a request in flight on a worker that *vanished* (llama.cpp aborts the head; the request fails with its error
— the drain is for planned leaves); choosing a spare by fit; a member coming back while its spare serves (the group stays on the
spare until the list changes again — no fail-back); GPUs; models above 8B.

### Phase 138 (community: a node opts in with the risk stated, the coordinator picks the members, a client accepts per request)

Files: node `ShardOptions.Community/CommunityAcceptRisk/JoinsCommunity/CommunityProblem`, the worker's `community` in its state,
`SealedMode` (the door); hub `ShardPeerPublisher.Compute` (peers across organisations), `ShardGroupDefinition.Mode/Workers`, the pick
in `ComputeGroups`, `IsCommunityGroup` → `NodeRegistry.IsCommunityGroup` → `InferenceCore` (the 403, `CommunityHeader`) and
`/v1/models` (`owned_by`); Shared `NodeShardState.Community`, `ShardGroupSpec/State.Community`; the console's pills;
`deploy/lab/shards/community-lab.sh`.

**D1, load-bearing — three separate yeses: the machine's owner, the admin, the client.** The user's terms (2026-11-08): in community
mode whoever sends a prompt accepts that other nodes may see it; encryption is deferred. A node joins only with `Community` **and**
`CommunityAcceptRisk` in its own configuration — one without the other is ignored and said in its shard state; `Node:Sealed` with
`Community` refuses to start (a door in 122's list), and the hub never counts a node that registered sealed. The admin defines a
`Mode: community` group. The client accepts **per request** with `X-InferHub-Community: accept`, or gets a 403 that says what and how
— both dialects, one check at the top of the dispatch. *Rejected:* a per-key flag alone — the key's owner is not always the person
typing the prompt; the header puts the choice in the request that carries it.

**D2 — community nodes are peers across organisations.** 135's rule (same organisation) **or** both community and neither sealed.

**D3 — the coordinator picks the members.** `Workers: N`; the head must have joined too (it sees every prompt). Members: connected
community nodes, not sealed, ready (137 D1), peers of the head, on its protocol — the largest offers first, ties by name. Fewer than
N is "only k community node(s) can take part now; the group wants N". A member leaving is just the next pick.

**D4 — a client sees it.** `/v1/models` lists the group with `owned_by: inferhub-community`; the console marks the node and the group.

**The lab (2026-11-12, `community-lab.sh`):** shard-a/b/c opted in, shard-d (same offer) did not, shard-s sealed with `Community` on.
The hub picked shard-b and shard-c, never shard-d; shard-s exited naming the door; `/v1/models` said `inferhub-community`; a chat
without the header got the 403 (and `/api/chat` too), with it the answer.

**Not established:** organisations in the lab (cross-organisation peering is covered by `ShardPeersTests`); a client library that
sends the header (none yet); anything that hides a prompt from the machines in a group — there is nothing, by the user's choice.

### Phase 139 (community across NAT: two nodes with no direct path meet at the coordinator's relay, under the same mTLS)

Files: hub `Services/ShardRelay.cs` (`ShardRelayOptions`, `ShardRelay`), `ShardPeerPublisher` (`MayReach`, the `relay` on a peer and
on a list, `Keep`), `GET /api/admin/shards/relay`; node `ShardOptions.TunnelRelay`, `ShardTunnel` (`WaitAtRelay`/`StandbyAsync`,
`DialAsync` direct-then-relay, `via`); Shared `ShardRelayInfo`, `ShardRelayWire`, `ShardPeer.Relay`, `ShardPeerState.Via`,
`ShardTunnelState.Relay/RelayError`; the console's *relay* pills; `deploy/lab/shards/relay-lab.sh`.

**D1, load-bearing — the relay carries 135's mTLS and reads none of it; a direct path is tried first, every time.** A node behind
a home router dials out and cannot be dialled (135 D4's address is the router's). With `ShardRelay:Enabled` the coordinator
listens on `Port` (50054, plain TCP); a node with `Node:Shards:TunnelRelay` keeps three connections dialled out to it, waiting; a
peer that cannot connect directly (3 s) dials the relay and names it; the relay joins the two sockets and copies bytes. The TLS
handshake of 135 then runs *through* the pipe — the same certificates, the same thumbprints — so who may reach a worker is still
135 D1 and nothing else, and the relay sees ciphertext (rule 7). The measurement tries direct again every round, so a pair that
can connect never stays on the relay; `peers[].via` says `direct` or `relay`. *Rejected:* a relay that terminates TLS (a second
place that decides); hole punching (a second protocol, for a speed-up nobody measured the need for); SignalR (the invariant).

**D2 — a node proves itself to the relay with an HMAC over the relay's nonce, keyed by a secret from its hub connection.** The
hub sends each opted-in node `relay {host, port, nodeId, key}` with its peer list (`host` null = the host of the coordinator URL the
node already dials); the relay sends 16 random bytes, the node one line (`IHR1 L|D <id> <target> <hmac>`), the relay one byte — or
`-` and the sentence. A waiting connection gets `.` every 20 s (a NAT forgets silence; 75 s of it and the node redials) and `!`
when paired. The key never crosses the relay's wire. An unknown node and a wrong proof get the same answer. *Rejected:* the node's
certificate at the relay — the hub would need a server certificate nodes can verify, to protect bytes already protected.

**D3 — the relay joins only nodes the hub's last lists made peers; a node that left them loses its place at once.** Every publish
gives the relay the set that may use it (`Keep`): a node not in it loses its key, its waiting connections and each pair it is part
of — revoke still closes the door in seconds, on this path too. At most `MaxPairs` (64) pairs, 8 waiting per node; a dial waits
`WaitSeconds` (5) for the node it asked for.

**D4 — off on both sides, and both owners say yes.** The hub's owner gives a port and bandwidth; a node's owner accepts its tensors
going by way of a third machine. A peer is marked `relay` only if it opted in, and the relay is used only when both ends did. In
memory (rule 4); sockets and `HMACSHA256` from the framework (rule 5). A relay that is enabled and cannot listen fails startup.

**Not established:** real routers (a Docker network that routes nothing stands in for NAT — no symmetric NAT, no idle timeouts
shorter than 20 s, no loss); the relay's cost on a real uplink (in the lab the coordinator is on the same host); a cluster (each
coordinator relays for its own connections only); TLS on the relay port itself (the hello is in the clear — it carries ids and
an HMAC, never the key).

### Phase 140 (verification day: both modes end to end on the published images, in one command)

`deploy/lab/shards/verify-lab.sh <version>` pulls the published coordinator and node, checks the labels (`VERIFY_REVISION`
requires the commit), builds the lab node on the published one, runs `tunnel-lab.sh`, `survive-lab.sh`, `community-lab.sh` and
`relay-lab.sh` and greps their transcripts: PASS or FAIL per claim, exit 0 only when all held. **It is not a second
implementation** — a check and what a person runs by hand are one file. Not a CI job: it needs an 8B model, five containers and
ten minutes of CPU. What the run found and the whole track's "not established" list: `.claude/release-notes-v3.100.0.md`.

### Phase 142 (the gate of the second track: a 70B no node holds answers from a group, and a load says how far it is)

Files: `Backends/Shards/ShardTunnel.cs` (`CopyCounting`, a forward's `sent`), `ShardGroupHost.cs` (`SentByNode`, `Running.WithSent`/
`SentMoved`, `LayersAsync`'s share), Shared `ShardPeerState.SentBytes`, `ShardGroupLayers.ExpectMiB/SentMiB`; the console's group
row; `deploy/lab/shards/tunnel-lab.sh` (knobs). Opens the track `plan/roadmap-v3.102-shared-compute.md` (142–149).

**D1 — the gate passed: a model above every node's memory runs, and it loses less to the network than the 8B did.** Lab
(2026-11-16, one PC, b12182, CPU): WizardLM 70B Q4 (80 layers, 36.2 GiB) over three nodes in 18 GiB containers, each offering 14 GiB,
16 CPUs each — layers 0–26, 27–53, 54–79 + output, 12.4–12.7 GiB held per node. Through the hub: **the same text as the whole
model** (72 tokens, character for character), **1.75 tok/s against 1.87 whole (94 %)** unshaped; with 30 ms of round trip and
50 Mbit/s on every node **1.21 tok/s (65 %)** — the 8B kept 47 % there (141), because a 70B token is mostly computing and the
round trips are the same few. Until it answers: 200 s, 24.5 GiB through the tunnels. A reload over the shaped network with
the members' caches (134's `-c`): 247 s and **about 270 MiB across the network instead of 12 GiB a member** — the cache is keyed by
the tensors' own hash, which is what 143 builds on.

**D2, load-bearing — a load's progress is counted where the bytes leave: at the head's forward, per member, since the group
started.** A 70B is minutes of `starting` (hours on a home uplink) and the state said nothing. Each forward (135 D3) counts what
it carried to its peer; a group remembers the count at its start; `layers[]` carries `expectMiB` (the file's bytes × that
member's share of the blocks) and `sentMiB`. The head's own worker has neither — it is beside the head, not behind a tunnel.
While a group is starting the head reports at most every five seconds, and only if the count moved. The hub passes the state
through untouched, so only the node changed; a head before 3.102 sends neither field and the console shows the layers as before.
*Rejected:* reading `llama-server`'s log — it prints dots, not devices. *Rejected:* asking the workers — a head holds a worker's
one connection (134 D2). **Known limit:** a `llama-server` relaunched inside one group (it died, 95's supervisor restarted it)
keeps the first load's count, so a cached reload reads "sent" above its share; the console caps the figure at the share.

**D3 — the lab's big model lives in a Docker volume.** Found on the published 3.101.0 images: with the file bind-mounted from
Windows the head paged 13.9 GiB of it in twelve minutes (≈20 MB/s through the mount) and had sent no member a byte. From a named
volume the same load is 200 s. `LAB_MODEL_DIR` takes a volume's name as it takes a directory; `LAB_NODE_MEMORY`, `LAB_OFFER_GIB`,
`LAB_CPUS`, `LAB_CONTEXT`, `LAB_WAIT`, `LAB_MAX_TOKENS` size a run. llama.cpp loads the members first and the head's own worker last.

**Not established:** GPUs (CPU only, one host's cores and memory bandwidth shared by every node); a load at a real uplink's speed
(24.5 GiB at 50 Mbit/s is about 70 minutes — computed; the lab's first load ran unshaped); a member leaving a 70B group (137's
lab was not rerun at this size); more than three members; models above 70B; several requests at once.

### Phase 143 (a member loads its layers from its own disk: it fills its worker's cache from its copy of the file, and the head sends hashes)

Files: Shared `Fit/ShardSeed.cs` (`ShardSeedSpec/List/State`, `ShardSeedPlan`), `GgufTensor.Type/Dims/Bytes` + `GgmlTypes` in
`Fit/GgufHeader.cs`, `NodeShardState.Seeds`; node `Backends/Shards/ShardSeeder.cs`, `ShardWorker.CacheRoot` (+ `LLAMA_CACHE` on the
worker), `ShardOptions.CacheDir`, `ShardGroupHost.ModelFile` one directory down, the `ShardSeeds` handler; hub `ShardPeerPublisher.
ComputeAll` (the assignments, the hold, `ShardGroupStatus.Loads`), `loads` on `GET /api/admin/shards/groups`; the console's cache rows;
`tunnel-lab.sh` `LAB_MEMBER_DISK`, `LAB_SHAPE`.

**D1, load-bearing — the hub tells each member its place in the split before the head loads, and waits for it.** Once a group is
otherwise ready, every member is sent `ShardSeeds`: the model's name, the group's weights in the head's order and its index — what
`ShardLayers.Split` (137 D4) needs to name its layers from its *own* file's block count. Its answer is keyed by the hub's
`group|model|index|weights`, so a substitution or a new offer is a new question. A member that reports `seeds` (≥3.108, worker with
`-c`) holds the group back while it has not answered (*waiting for shard-b to say whether it holds …*) and while it fills (*…filling
its cache from its own copy of … (x of y MiB)*); `absent` or `failed` never holds it — that member is loaded over the network, as
before, and `loads[]` says which and why. A member that reports no `seeds` is never waited for. **The cache is content-addressed, so
the worst a wrong file can do is miss** — no answer can change. *Rejected:* the member asking the head which tensors it will get — a
head holds a worker's one connection (134 D2), and the split is the hub's arithmetic already. *Rejected:* the hub sending the file
itself — 122–124 already deliver it (98's download through the relay and supply); this phase only stops the head re-sending it.

**D2 — the gate of the track, read from source and then measured: `rpc-server -c` is filled without a patch.** b12182: the client
asks `SET_TENSOR_HASH` for a weight tensor above `HASH_THRESHOLD` (10 MiB) with the FNV-1a 64 of its bytes; the server answers from
`<cache>/<hash as %016x>`, a file of exactly those bytes. An RPC device reports no host buffer, so the loader sets each tensor whole
(no chunked async upload) and the bytes are the GGUF's own. So the size must be `ggml_nbytes` — a row of `ne[0] / block` blocks of
the type's size (`GgmlTypes`, from `ggml-common.h`'s `static_assert`s) — **not** the gap to the next offset, which carries the
alignment padding (118's `TensorBytes` stays gap-based; its estimate does not care). A type the table does not know is left out:
sent, never wrong. **What stays on the wire is llama.cpp's**: every tensor at or under 10 MiB — for an 8B's 4096-wide layers that
includes `attn_q`/`attn_output` (9 MiB each) and `attn_k`/`attn_v`; for a 70B mostly `attn_k`/`attn_v`.

**D3 — the worker is told its cache directory, and a reader never sees half a tensor.** `CacheRoot` is `Node:Shards:CacheDir`, else
what `rpc-server` picks itself (`LLAMA_CACHE`, `%LOCALAPPDATA%`, `~/Library/Caches`, `$XDG_CACHE_HOME`/`~/.cache`, then
`llama.cpp`), and the worker gets it as `LLAMA_CACHE` — so the two cannot disagree and a cache made before 3.108 is where it was.
`rpc-server` answers a hash from any file of that name (it does not check the size against anything but its buffer), so each tensor
is hashed while it is copied into `.inferhub-<guid>.part` and renamed only when whole. One assignment at a time (one disk).

**D4 — an assignment filled before is not read again.** `<cache>/inferhub-seeds/<model>.<first>-<count>[-out].json` records the file's
length and write time and the hashes; while they match and every hash's file is there with its size, the answer is `ready` at once.
Outside `rpc/`, so llama.cpp never reads it. Free disk is checked first (the share + 1 GiB, the deepest mount holding the cache);
too little is `failed` with the numbers, and the head sends. **Disk cost: that member's share of the model, kept** — `rpc-server`
never evicts its cache, and nothing here does either.

**The lab (2026-11-20, `tunnel-lab.sh`, b12182, CPU, the 8B over three nodes, members holding the file with `LAB_MEMBER_DISK=1`):**
a first load sent shard-b **248 MiB of its 1 528** and shard-c **225 of 1 389** (without the file: 1 287 and 1 581 — 2 868 MiB in
all against 473, −84 %), and the group wrote **the same text as the whole model in one container**, character for character. With
every node shaped from its start to 15 ms and 50 Mbit/s (`LAB_SHAPE`), the first load to an answer took **165 s instead of 582 s**.
Filling the caches (1 039 and 1 355 MiB) is read and written on the member's own disk before the head starts.

**Not established:** the file reaching a member through the hub in the lab — the lab mounts it; delivery is 98 and 122–124
unchanged, and their own tests stand; a 70B filled this way (the arithmetic is the 8B's, and 142's cached reload already sent ~270 MiB
a member); a GPU worker (`-d Vulkan0`) — whether a card's buffer takes the same `SET_TENSOR_HASH` path was not run; a llama.cpp
build other than b12182 (the threshold, the hash and the file name are its code, not a protocol promise).

### Phase 144 (the coordinator places a group itself: the admin names the model, the files, the offers and the round trips do the rest)

Files: Shared `Fit/ShardPlacement.cs` (`ShardModelFile`, `PlacementCandidate`, `ShardPlacement.Place/Need/Holds`), `NodeShardState.
Models/MayHead`; node `Backends/Shards/ShardModelScanner.cs` (reported by `ShardWorker.State`); hub `ShardGroupDefinition.IsAuto/
SettleSeconds`, `ShardPeerPublisher.Place/Broken/Replace/Wake` and `Unavailable` (137 D1's checks, now shared), `ShardGroupStatus.
Placement/Mode`; `mode` + `placement` on `GET /api/admin/shards/groups`, `POST /api/admin/shards/groups/{name}/replace`; the console's
*Group files* column and auto rows; `tunnel-lab.sh` `LAB_AUTO`, `LAB_SLOW`.

**D1, load-bearing — the objective is the head's sum of round trips to its members, then fewest members, then members that hold
the file.** Every token goes to each worker and back in turn (141), so those round trips add up and bandwidth hardly counts. A head
is any node that reports the file and `MayHead`; for each, every subset of its reachable peers on its RPC protocol (exact, over the
12 nearest) is checked against the memory rule and the smallest sum wins, compared to 0.1 ms so jitter is not a preference. **The
memory rule is exact for a split by offers**: each worker uses `(W + K) · offer / Σ` + reserve (133's 512 MiB + context/100), so all
fit when `(W + K) ≤ Σ · (1 − reserve / smallest)`. A head that holds the model alone is told so — a group is never formed for it.
*Rejected:* 138's largest-offers-first — it ignores the matrix the console already draws; a greedy nearest-first — it takes a near
small node and then needs a third.

**D2 — a node reports its group files and its consent to head.** `Models` (name, MiB, layers, KV per token, from the header; a split
GGUF once, by its first part, with every part's bytes) and `MayHead` (`Node:Shards:AllowHead`), found exactly as `ModelFile` finds
them (the directory, then one down). Scanned in the background at most every 30 s, a header re-read only when size or time moved.
Null before 3.109: such a node is never a head (it may still be a member).

**D3 — a placement is kept while it can run, and made only on a fleet that holds still.** Kept in memory (rule 4) while the head
and each member pass 137 D1, the head still holds the file and may head, and the offers still hold the model; when not, the reason
is kept until the next placement says it (*placed again: shard-d is cordoned*). Made only when (a) the candidates, their files,
offers and who may reach whom have not changed for `SettleSeconds` (30) — a timer looks again, nothing else would on a quiet fleet
— and (b) the candidate head has measured a round trip to every peer. **Found in the lab:** without (a), shard-a and shard-b
connected first, the group was placed on them 20 ms apart and kept, before shard-d (0.3 ms) had enrolled. A better number never
moves a running group — a reload is minutes (142); `POST …/replace` does it on purpose.

**The lab (2026-11-21, `LAB_AUTO=1 LAB_SLOW="b c" LAB_MEMBER_DISK=1`, b12182, CPU, the 8B, four nodes offering 3 GiB, only shard-a
may head):** settled 30 s, then placed on **shard-d (0.30 ms)**, leaving out shard-b (20 ms) and shard-c (21 ms) — a + one member
holds the 8B at 2 048 tokens (5.6 of 6 GiB). Containers up to an answer: **80 s**. 64 tokens: **9.9 s** through d. `docker stop
shard-d`: settled 30 s, placed on **shard-c**, which filled its cache from its own copy, running **52 s** after the stop; the same
64 tokens took **11.8–12.3 s**. The text was **identical** through b (the first, wrongly placed run), d and c.

**Not established:** a real network — every round trip here is netem on one PC; that the sum of round trips predicts speed beyond
this one comparison (it is 141's model, not re-measured per placement); more than four nodes or the 12-peer cut; GPUs; a fleet
whose nodes come and go faster than `SettleSeconds` (it would wait as long as they do).
