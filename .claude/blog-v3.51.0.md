<h2>InferHub v3.51: the hub sends the work to the card that is already warm</h2>

<p>The last release, v3.50, let one card run several services in turn. With <code>Node:OnDemand</code> on, the local Ollama and every tool (speech, transcription, images, video) take the card one at a time. Whoever holds it is released between jobs, so a desktop GPU is free again when nothing is running.</p>

<p>That solved it for one box. With two such boxes behind one coordinator, it created a new problem, because the coordinator did not know about any of it. It chose a node the way it always had: the least busy one that held the model. Suppose node A's card is busy with diffusion and node B's has its chat model loaded. A chat request could land on A. There it waited for the image job to finish, for the diffusion worker to stop and for the chat model to load, up to five minutes, while B could have answered at once. Then A's next image job had to wait for the switch back. One badly placed request cost two switches.</p>

<p>The node knew the answer the whole time. It was just never asked.</p>

<h3>What v3.51 adds</h3>

<p>An on-demand node now tells the coordinator who holds its card. It sends this with every heartbeat, and once more straight away when the card changes hands, so the coordinator is not a heartbeat interval behind:</p>

<pre><code>"onDemand": { "holder": "tool:diffusion", "warmFor": ["image", "video"], "switching": false, "waiting": 0 }</code></pre>

<p>When more than one node could serve a request, the router now sorts them into three groups and uses the first group that has anyone in it:</p>

<ol>
  <li><strong>Warm</strong>: the card already holds the service this request needs.</li>
  <li><strong>Free</strong>: the card is idle.</li>
  <li><strong>Cold</strong>: another service holds the card, or it is in the middle of a switch.</li>
</ol>

<p>Inside that group, nothing changes. The router still picks the least busy node (or the fastest one, if you use the throughput strategy), and a conversation still sticks to its node, as long as that node is in the group.</p>

<h3>A preference, not a filter</h3>

<p>This was the most important rule in the design. <strong>The router never refuses a request because of what a card is doing.</strong> If the only node that holds the model is busy with something else, the request still goes there and waits, exactly as in v3.50. The new logic only chooses between nodes that could all do the job. It never removes the last one.</p>

<p>The same idea covers upgrades. A node that does not run on demand, or one older than v3.51 that sends no card state at all, counts as <em>always warm</em>. Its card is not shared between services, so from the router's point of view it never needs a switch. A fleet with no on-demand nodes therefore has a single group, and it routes exactly as it did before. There is a test named after that sentence.</p>

<h3>The node decides what "warm" means</h3>

<p>The coordinator routes on capabilities (<code>chat</code>, <code>embed</code>, <code>image</code>, <code>transcribe</code>…). The node thinks in services: <code>ollama</code>, <code>tool:diffusion</code>, <code>tool:whisper</code>. Only the node knows that its diffusion worker does images and video, or that its backend does chat but not embeddings. So the node does the translation and sends the list of capabilities. The coordinator never learns what a tool is, and there is no second copy of that mapping to drift out of date.</p>

<p>The coordinator also never tells a node to take or give back its card. There is no "warm up node A for video" command and no eviction. Who uses the card on a desktop is the owner's decision, set in the node's own configuration, and a coordinator that could evict a service could also evict the owner's work.</p>

<p>There is no new setting. It works whenever <code>Node:OnDemand</code> is on.</p>

<h3>Where you can see it</h3>

<ul>
  <li><code>/api/status</code> has an <code>onDemand</code> object for each node. It is <code>null</code> for a node that does not run on demand. A solo node's status page shows the same object.</li>
  <li>The console shows <code>gpu ollama</code>, <code>gpu free</code> or <code>gpu switching</code> next to each node.</li>
  <li><code>/metrics</code> has <code>inferhub_node_gpu_holder{node,holder}</code>, only for on-demand nodes.</li>
</ul>

<h3>What we checked</h3>

<p>The full test suite passes, with 18 new tests. They cover each group winning over the next, a cold node still getting the request when it is the only one, a conversation leaving a node whose card went to another service, and a request that names no capability being left alone. Three of them run against a real coordinator over a real SignalR connection, including one where a node sends the older heartbeat format.</p>

<p>Then we ran it live twice: first from source, then with the published <code>3.51.0</code> images pulled from the registry and started as three containers (a coordinator and two on-demand nodes, both using the same Ollama on the host). We sent a tool request, which could only go to node A, so A's card went to that tool. The coordinator showed the new holder within a second. Then we sent three chats with different messages. <strong>All three went to node B, and node A never waited for its card.</strong> A 35B model that another program had loaded into the same Ollama stayed loaded throughout.</p>

<p><strong>Not yet established:</strong> a real GPU worker (diffusion or Whisper) switching on a card. The tool in the live runs was a real process, but not a GPU one, which is the same gap v3.50 left open. We also did not run a v3.51 node against a v3.50 coordinator, and we checked the console's data but did not look at the page in a browser.</p>

<p>Release notes and images: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.51.0">InferHub v3.51.0 on GitHub</a>. Documentation: <a href="https://inferhub.devart.solutions/#idocs_on_demand_routing">Routing to a warm card</a>.</p>
