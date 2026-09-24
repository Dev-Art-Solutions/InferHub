<h2>One card, one service at a time: InferHub v3.50 gives your desktop GPU back between jobs</h2>

<p>Every InferHub node so far has assumed its card belongs to it. Ollama keeps a chat model resident for its own <code>keep_alive</code>. Each tool (Whisper for transcription, Piper for speech, the diffusion worker for images and video) keeps a warm worker process, so the next request does not pay for loading weights again. On a dedicated GPU box that is the right default.</p>

<p>It is the wrong default on a desktop. There the one card is also the owner's: for a game, for a render, for whatever they were doing before they started a node. The request that led to this release was simple: <em>generate an audio file, then a video, then use an LLM, on one card with limited VRAM, and have the card free again in between.</em> With warm workers and a resident chat model, those three services compete for the same memory and the card is never fully free.</p>

<h3>What v3.50 adds</h3>

<p>An opt-in mode, <code>Node:OnDemand</code>. It is off by default, and a node that does not set it behaves exactly as v3.49 did.</p>

<pre><code>"Node": {
  "OnDemand": { "Enabled": true, "ReleaseAfterSeconds": 30, "SwitchWaitSeconds": 300 }
}</code></pre>

<p>With it on, the local Ollama and every tool take the card <strong>one at a time</strong>:</p>

<ol>
  <li>A request arrives for a service that does not hold the card. It <strong>waits</strong> until the holder's in-flight work finishes. Nothing is evicted mid-job: a video that is rendering finishes rendering.</li>
  <li>The holder is released. Ollama unloads the models the node loaded. A tool's worker processes are <strong>stopped</strong>, not just told to free their weights, because a live Python process keeps a CUDA context of a few hundred megabytes that only its exit gives back.</li>
  <li>The newcomer starts, loads its model and runs.</li>
  <li>Once a service has been quiet for <code>ReleaseAfterSeconds</code>, it is released even if nobody else asked. The default of 30 seconds keeps a conversation from reloading its model between every message. <code>0</code> releases the moment the last request ends.</li>
</ol>

<p>Two rules keep it fair. Requests for the service that already holds the card share it, so two chats still run side by side. And once somebody is waiting for the card, new requests for the holder queue <em>behind</em> them. Without that, a steady stream of chat messages would keep the LLM on the card forever and a video job would never start. A request that waits longer than <code>SwitchWaitSeconds</code> gets the same <code>503</code> with a <code>Retry-After</code> as every other limit in InferHub, so a client's retry logic does not need to know which limit it hit.</p>

<p>The coordinator notices none of this. The node keeps declaring the same capabilities. A tool whose worker was stopped is started again by its next request, and the new worker reports its models exactly as it does at boot.</p>

<h3>The check that changed the design</h3>

<p>The first version released Ollama the simplest way: ask it which models are loaded and unload all of them. Before the first live run we looked at the real machine, and Ollama had a 22&nbsp;GB model loaded. Another program on the same box had loaded it through the same Ollama. A node that unloaded everything would have dropped that model, and the other program would have paid for loading 22&nbsp;GB again on its next request.</p>

<p>A desktop's Ollama is shared. So the node now records the model of every request it sends and, on release, unloads only those. It checks them against Ollama's list of loaded models first, treating <code>llama3</code> and <code>llama3:latest</code> as the same model, so a model that is not loaded is never loaded just to be dropped.</p>

<p>The live runs then did what they should, first from source and then on the published <code>inferhub-node:3.50.0</code> image itself, pulled from the registry and run with the mode on and a five-second linger. A streamed chat loaded a small model, and readings of Ollama's loaded list every two seconds showed it gone four seconds after the answer and still gone twenty seconds later. An embedding request, sent with a model name that had no tag, was unloaded the same way. The 22&nbsp;GB model belonging to the other program stayed where it was the whole time.</p>

<h3>What it costs</h3>

<p>Cold starts. Every switch pays a model load: seconds for Whisper or Piper, a minute or more for FLUX or a large LLM. Alternating services request by request is slow by design. This mode is for a box that does one kind of job at a time and should be idle in between. On a machine with a card to spare, leave it off.</p>

<h3>What is verified and what is not</h3>

<p>The full suite passes: 1,649 tests, with 17 new ones for this release. They cover sharing, the order of a switch, the linger, first-come-first-served across services, the timeout, a release that fails, a streamed answer holding the card until its last chunk, and a real worker process that is gone after its request and back for the next one. The Ollama path was checked live against a real Ollama, on the published image, as described above.</p>

<p><strong>Not yet established:</strong> a real Whisper, Piper or diffusion worker switching on a GPU. The tool path is tested with a real child process, but not a CUDA one, and we would rather say that than imply it.</p>

<p>Release notes and images: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.50.0">InferHub v3.50.0 on GitHub</a>. Documentation: <a href="https://inferhub.devart.solutions/#idocs_on_demand">A card that is also your desktop GPU</a>.</p>
