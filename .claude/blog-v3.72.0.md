<!-- live 2026-10-20 as ID 6ad7336f7c8865e950bbdd61 (EN visible, BG hidden), created by this session -->
slug: inferhub-3-72-a-free-slot-for-the-model
title: InferHub 3.72: a free slot for the model
excerpt: Ollama runs one request per model at a time unless you tell it otherwise. The coordinator now knows that, and sends a chat to a node that can start it now instead of one where it would wait behind the same model.

<p>3.71 let a node run the coordinator's jobs side by side. Its release notes ended with a question we had not answered: what does a real Ollama do with all those jobs at once? 3.72 starts with the answer and fixes what it showed.</p>

<h2>Ollama runs one request per model at a time</h2>

<p>We sent six requests at once to a warm <code>qwen2.5:0.5b</code> on one Ollama, 200 tokens each. They finished at 0.9, 1.4, 2.0, 2.5, 3.0 and 3.5 seconds, one after another.</p>

<p>That is Ollama's default. <code>OLLAMA_NUM_PARALLEL</code> sets how many requests one loaded model runs at once, and when you do not set it, it is 1. Extra requests wait in Ollama's own queue. Different models do run at the same time.</p>

<h2>Why the coordinator got it wrong</h2>

<p>The coordinator spread requests by how many jobs each node had. Take two nodes that both hold <code>qwen2.5</code>. Node A is answering a <code>qwen2.5</code> chat. Node B is busy with a different model. Both have one job, so both look equally busy, and the coordinator alternates between them.</p>

<p>Every other new <code>qwen2.5</code> chat went to node A. There it waited inside Ollama for the one slot, while node B's copy of the same model sat idle.</p>

<p>Before 3.71 the node made the same mistake for a different reason: it ran one job at a time no matter what. 3.71 removed that reason. This release removes the mistake.</p>

<h2>What changed</h2>

<p>A node now tells the coordinator how many requests one model can run at once. An Ollama node reads <code>OLLAMA_NUM_PARALLEL</code> from its own environment, and says 1 when it is not set, as Ollama does. You can set the number yourself with <code>Node:ModelSlots</code>.</p>

<p>The coordinator counts its own jobs per node and per model. When a request comes in, it looks first at the nodes that have a free slot for that model, and only then at which node is least busy.</p>

<p>It is a preference, never a refusal. When every node holding the model is full, the request goes where it went before, and Ollama queues it as it always did. A single node routes exactly as in 3.71. So does a fleet whose backends say nothing about slots, which is every backend except Ollama.</p>

<p>You can see it in <code>GET /api/status</code>: <code>nodes[].modelSlots</code>, and <code>nodes[].modelsInFlight</code> while the coordinator has jobs out.</p>

<h2>Checking it</h2>

<p>We ran a coordinator and two nodes from source, against the Ollama on this machine. A long <code>qwen2.5</code> story went to node A and a long <code>gemma:2b</code> story went to node B. Both nodes showed one job each. Then we sent four short <code>qwen2.5</code> requests, one after another. All four went to node B, the node with a free <code>qwen2.5</code> slot. With both nodes idle, the same requests alternated A, B, A, B, as before.</p>

<p>Our first two tries at that check proved nothing. We asked the models to count to 3000 and to repeat a word forever. Both stopped after a few tokens, so the short requests found two idle nodes. A check whose setup did not hold is not a pass, so we changed the prompts and ran it again.</p>

<p>Then we did the same with the published 3.72.0 images: we pulled the coordinator and the node from the GitHub container registry, and ran their binaries with one coordinator and two nodes. The result was the same. With both nodes idle, the short requests alternated. With both nodes busy, all four went to the node with the free slot, and both counters were back at zero afterwards.</p>

<h2>What we have not established</h2>

<ul>
<li><strong>The time saved.</strong> Both nodes here shared one Ollama, so a request sent to node B still waited behind node A's story in the same model. The check proves the routing choice. Measuring the time it saves needs two machines.</li>
<li><strong>An Ollama the node cannot see into.</strong> If Ollama runs as a separate service with its own <code>OLLAMA_NUM_PARALLEL</code>, the node cannot read that value and says 1. Set <code>Node:ModelSlots</code> to fix it. A number that is too low only weakens the preference. Nothing is refused because of it.</li>
<li><strong>Work the coordinator did not send.</strong> If something else talks to the same Ollama, such as a client using the node directly, the coordinator does not count it.</li>
</ul>

<p>The release is <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.72.0">v3.72.0 on GitHub</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_model_slots">inferhub.devart.solutions</a>.</p>
