# InferHub v3.36.0 — the node was up, its Ollama was dead, and the hub told you the model did not exist

A node whose inference backend has fallen over stays connected. It answers the heartbeat, the
console draws it green, and the only thing that has changed is that it cannot do the one job it is
for. InferHub has known about that failure since v3.4 — the node probes its own Ollama and restarts
it — and the coordinator was **deliberately** never told, in writing, as a phase-36 non-goal.

That deferral held for thirty-three releases. What broke it is not routing. It is what a client
hears.

## The bug that was hiding behind a working mechanism

Since v3.4 a broken backend makes the node report **zero models**, and that is what stops the
coordinator dispatching to it. It works. But at the hub, *a node that reports zero models* and *a
node with nothing installed* are the same thing — so the model disappears from the registry and the
request comes back:

```
404  model 'llama3' not found
```

For a fleet that has `llama3`, on a box three feet from a server that needs restarting. That answer
sends an operator to `ollama pull` weights that are already on the disk, and it is the most
expensive kind of wrong: confident, specific, and about the wrong thing.

## What v3.36 changes

The node already had a **typed** verdict — `healthy`, `unreachable`, `wedged`, declared only after
`UnhealthyThreshold` consecutive failed probes. It now travels on the heartbeat, and the hub does
four things with it:

- **Stops routing there**, the same way it already skips a cordoned node.
- **Keeps the model discoverable.** `/api/tags` and `/v1/models` still list it — a client that
  cannot *see* a model cannot be told why it is unavailable, and `digest`/`size` are facts about a
  file on a box that stay true while the server is down.
- **Refuses with the right sentence:**
  `503 every node holding model 'llama3' reports an unhealthy inference backend`, with
  `Retry-After`. Never a 404, which means *no such model*.
- **Shows which state it is**, on `/api/status`, on the node row beside `online` (the connection
  genuinely is up), and on the console's **Needs attention** strip — with the fix rather than a
  status word: *nothing is listening* is a server to start, *accepts connections and never answers*
  is one to stop first.

Recovery needs nothing but the next heartbeat. No re-registration, no reconnect, no restart of
anything on the hub.

## Watching is not restarting

`Ollama:Supervisor:Enabled` is off by default and loopback-only, and it stays exactly that: bouncing
a shared Ollama because *one* node's link hiccuped is a four-node outage caused by the node with the
worst network. But **asking** a server whether it is alive needs neither consent nor locality — it is
what the next request does anyway.

So the two halves split. **`Ollama:Supervisor:Watch` is new and defaults to `true`**: any
`ollama`-typed node watches and reports, loopback or not; only restarting keeps the old gates. The
cost, stated rather than buried: such a node now makes one cheap local call every `ProbeInterval`
that it did not make before. `Watch: false` turns it off and the node is byte-identical to v3.35 —
which is the right setting for the vector-store-only mode, the one deployment that has an `ollama`
backend type and deliberately no Ollama.

**A vendor-typed node is never probed.** A poll every fifteen seconds against Anthropic, Gemini or
OpenRouter is a **billed request**, and there is no free liveness endpoint we may assume across four
vendors. Those nodes report nothing and route exactly as they always have.

## Two questions that used to be one

`FindNodesWithModel` answered *"who can serve this?"* and *"who has this?"* with one query, because
until now a node that was up was a node that worked. Three callers ask the second question and now
say so:

- **Model placement.** A sick node still has the model on disk; counting it as absent would pull
  twenty gigabytes onto another box to replace one that will be back in a minute.
- **Model discovery.** A model must not vanish from `/api/tags` because a server is wedged.
- **The refusal itself.** Telling *no such model* from *cannot serve it right now* is the whole
  point.

The request queue keeps the default: waiting for a slot on a node that cannot answer is a queue that
has stopped meaning anything.

## Absent is not sick

The field is nullable and **null is "no opinion"** — a node older than v3.36, a node with the watch
off, a vendor-typed node. All three route exactly as they did. An upgrade that read silence as
"unhealthy" would empty a fleet the moment the hub was updated ahead of its nodes, which is the
normal order, so it is pinned across a real SignalR connection with a real three-field payload.

New series: `inferhub_node_backend_health{node,state}` at a constant 1, and a node with no opinion
emits **nothing** — `state="healthy" 0` would read as a measurement that came back bad.

## Also in this release

`src/InferHub.Coordinator/CLAUDE.md` was at 1076 lines of its 1100 budget, so the phase-32
multi-coordinator decisions moved whole and unedited into
`src/InferHub.Coordinator/Cluster/CLAUDE.md`. Third time this has happened, same answer each time:
move a coherent subtree, never raise the budget.

**Tests:** 1 496 passed, 48 skipped, green as a solution.

## What was not established, said out loud

- **No real Ollama was killed under a real node for this release yet.** The mesh suite drives a real
  Kestrel hub and a real SignalR connection and sends real heartbeats, which is what proves the
  contract, the refusal and the mixed-fleet case. What it does not prove is the *node* end of the
  loop: that a genuinely dead local server crosses the threshold and produces that heartbeat on a
  published image. That is this release's own artefact check.
- **The console's new pill and strip row were not rendered in a browser as part of the suite** —
  `ConsoleContractTests` proves the payload carries what the page reads, which is a different claim.
- **No number here is a measurement.** How long a fleet takes to notice a dead backend is
  `UnhealthyThreshold × ProbeInterval` plus a heartbeat, by construction; nobody has timed it.
- **The watch adds a request the node did not make before.** It is one `GET` to a local server on a
  five-second deadline every fifteen seconds. That is stated, not benchmarked.

## Upgrading

Nothing to change. A hub upgraded ahead of its nodes behaves exactly as v3.35 until a v3.36 node
connects. `Ollama:Supervisor:Watch=false` is the way back to v3.35 behaviour on a node that wants it.
