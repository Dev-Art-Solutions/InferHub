<!-- live 2026-10-22 as ID 6ad9c5dc0a47e5ca637f0da8 (EN visible, BG hidden), created by this session -->
slug: inferhub-3-74-a-free-slot-worth-waiting-for
title: InferHub 3.74: a free slot worth waiting for
excerpt: When every node holding a model is already running all it can of it, the coordinator used to send the next chat anyway, into one engine's queue. Now it can hold the chat and send it to whichever node frees up first.

<p>3.72 taught the coordinator to send a chat to a node with a free slot for its model, and 3.73 let an engine say how many slots it has. That covers the case where some node has room. 3.74 is about the case where none does.</p>

<h2>The chat that waited in the wrong place</h2>

<p>Take two nodes, each able to run one request for a model at a time, and three chats arriving together. The first two go to one node each. The third finds both full.</p>

<p>Until now it went anyway, to the least busy node, and waited inside that node's engine. That decided its fate on arrival. If the other node finished first, it sat idle while the third chat waited behind a request on the first. The coordinator knew both counts and threw the choice away.</p>

<h2>Waiting at the coordinator instead</h2>

<p>One setting, off by default:</p>

<pre><code>"Queue": { "SlotWaitSeconds": 20 }</code></pre>

<p>With it set, a chat that finds every holder full waits at the coordinator, for up to that many seconds, and goes to the first node that frees a slot. With it left at 0, nothing changes: the request is sent at once, as before, and the status page looks exactly as it did.</p>

<h2>It never says no</h2>

<p>InferHub already has a queue that refuses: when every node is at its declared <code>MaxConcurrency</code>, a request waits up to <code>Queue:MaxWaitSeconds</code> and then gets a 503. That is right there, because a node past its cap refuses the work.</p>

<p>A full slot is different. The engine does not refuse, it queues, and before this release the request was answered. So a slot wait that runs out, or finds too many requests already waiting, sends the request on exactly as before. You get a slow answer at worst, never a refusal you did not get yesterday.</p>

<p>It also never sends the request to a cloud provider. A full slot means "this engine will queue it", not "the fleet cannot answer", and paying a vendor for that is your call to make with <code>MaxConcurrency</code>, not ours.</p>

<h2>One slot, one request</h2>

<p>The obvious version of this has a bug. When a slot frees, every waiting request sees it at the same moment, and all of them go to it. Three requests land on the one slot that freed, and two of them queue inside that engine anyway.</p>

<p>So requests for a model leave one at a time, in the order they arrived. Only the first in line checks for a free slot, and the next is let go only once the previous one has been counted against its node. One freed slot lets exactly one request go. There is a test that fails if that ordering is removed, and it failed when we removed it.</p>

<h2>Checking it</h2>

<p>We ran a coordinator and two nodes, each declaring one slot, over the Ollama on our test machine, and fired three chats 0.3 seconds apart. With the setting at 20 seconds, the first two went to one node each, the status page showed one request waiting at the coordinator, and the third went to the node whose chat had finished first. With the setting at 0, the third was sent at once to a node that was already busy, and the status page had no new field at all.</p>

<p>Then we did the same with the published 3.74.0 binaries pulled from the registry. The third chat sat at the coordinator for the whole eleven seconds the first one took, then went to the node that had just finished it. With the setting at 0 it went straight to a busy node.</p>

<p>Both nodes shared one Ollama, so the chats took the same time either way: one engine runs one request at a time however the coordinator orders them. What the run shows is where and when the third chat was sent. The time it saves needs two separate engines, and we did not measure that.</p>

<h2>Try it</h2>

<p>Set <code>Queue:SlotWaitSeconds</code> on the coordinator. <code>GET /api/status</code> then shows <code>queue.slotWait</code>, with how many are waiting now, how many waited, how many got a slot and how many were sent on anyway. The same numbers are on <code>/metrics</code> as <code>inferhub_queue_slot_*</code>.</p>

<p>Release notes and images: <a href="https://github.com/dev-art-solutions/InferHub/releases/tag/v3.74.0">v3.74.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_slot_wait">Waiting for a free slot</a>.</p>
