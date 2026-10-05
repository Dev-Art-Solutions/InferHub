slug: inferhub-3-60-three-engines-one-node
title_en: InferHub 3.60: Ollama, llama.cpp and colibri on one node, and the hub decides which are running
id: 6ac3afb45a1fa97c305284c9 (EN-visible, BG-hidden; rendered, checked)
excerpt_en: A node used to run exactly one inference engine. 3.60 lets one node run Ollama, llama.cpp and colibri side by side, and lets the coordinator start and stop each of them. Running it against the real engines found two bugs before release.

<p>Until now an InferHub node ran exactly one inference engine. That was fine while every box had one job. It stopped being fine on the kind of machine people actually have: an Ollama with a few models pulled, a GGUF file somebody wants to serve with llama.cpp's own server, and, since 3.58, a colibri engine for a Mixture-of-Experts model too big for the RAM. That was three nodes on one machine, and nothing on the coordinator could turn any of them off without somebody logging into the box.</p>

<p>3.60 lets one node run several engines at once, by name, and lets the coordinator decide which of them are running.</p>

<h2>What it looks like</h2>

<p>The node's configuration names its engines:</p>

<pre><code>"Backend": {
  "Engines": {
    "ollama":  { "Type": "ollama" },
    "qwen":    { "Type": "llamacpp",
                 "Serve": { "Executable": "/opt/llama/llama-server",
                            "Model": "/models/qwen2.5-7b-instruct.Q4_K_M.gguf" } },
    "nomic":   { "Type": "llamacpp", "Embeddings": true, "Autostart": false,
                 "Serve": { "Executable": "/opt/llama/llama-server",
                            "Model": "/models/nomic-embed-text.gguf", "Port": 8081 } },
    "colibri": { "Type": "colibri", "Autostart": false }
  }
}</code></pre>

<p>The node still shows up on the hub as one node. Each request goes to the engine that reported its model: an Ollama model to Ollama, the GGUF to <code>llama-server</code>, the MoE to colibri. Clients change nothing; they ask for a model by name, as before.</p>

<p>When an engine has a <code>Serve</code> block, the node starts <code>llama-server</code> itself, on loopback, with the file's name as the model name, and starts it again if it exits. colibri is launched the way 3.58 launched it. An engine with an address instead is one that already runs next to the node.</p>

<p>Each model is declared under its own engine's capabilities. A colibri model can chat and answer <a href="https://blog.devart.solutions/blog/inferhub-3-59-a-distribution-not-a-sentence">Brio</a> questions. An embedding <code>llama-server</code> can only embed, because that is all it does when started for embeddings. So when somebody asks the colibri model for an embedding, the hub refuses before the request leaves it, instead of sending it to an engine that would fail.</p>

<h2>The hub starts and stops engines</h2>

<p>There are two new admin routes, <code>start</code> and <code>stop</code> per engine, and an Engines panel on the console with a button on each row. Stopping an engine does three things in order. Its models stop being routed at once. Requests that are already running get 30 seconds to finish. Then the node kills the process it launched, or, for Ollama, unloads only the models this node loaded there. The machine's other Ollama models are left alone, because that Ollama is often somebody's desktop.</p>

<p>The instruction is written into the node's profile, the desired-state mechanism InferHub has had since 3.11. So a stopped engine stays stopped after the node reboots, and after the hub restarts.</p>

<p>The node's own list is the limit, and the node checks it, not the hub. The hub can start an engine the node lists but does not start at boot, and it can stop any of them. It cannot add an engine, or name a binary, a model file or a port, because the instruction has no field for any of those. A name the node does not list is refused by the node, whatever the hub sent. That is the rule every hub instruction has followed since profiles shipped: a coordinator that is wrong, or compromised, must not be able to make a fleet of GPU boxes run something their owners never configured.</p>

<p>Each engine also reports what it is doing: running, starting, stopped, unreachable or failed, with the reason. One engine going down withdraws only that engine's models. The rest of the node keeps serving.</p>

<h2>What the real engines found</h2>

<p>The suite passed before any real engine was involved. Then we ran one node with four engines on a Windows box: the machine's Ollama, two <code>llama-server</code> processes launched by the node, and colibri with OLMoE. Chat, streaming, embeddings and Brio all went through the hub to the right engine. Stopping <code>llama-server</code> from the hub made its process disappear, and its model then answered 404. That part worked first time.</p>

<p>Two things did not.</p>

<p><strong>A node that is killed does not get to clean up.</strong> Stopping a node gracefully stops its engines. Killing it, the way a crash or a task manager does, left <code>llama-server</code> running, still holding its port and its memory. On restart the node would have tried to launch a second copy on a port that was taken. On Windows every engine the node launches now joins a Job Object that the operating system closes when the node dies, and closing it kills the engines. We killed the node again: no engine processes left.</p>

<p><strong>A node booted into the wrong state for a moment.</strong> We had stopped one engine from the hub, then restarted the node. The node started all its "start at boot" engines straight away, loaded the model, and a second later received its profile and killed the engine again. Nothing broke, but loading a large model to throw it away is minutes of disk and memory for nothing. A node connected to a hub now waits up to 15 seconds for its profile before starting anything. After the fix, the stopped engine was never launched on boot.</p>

<p>We repeated the check on the published container image: the node launched the Linux <code>llama-server</code> and colibri inside the container, the hub stopped and started them, and after the container restarted, only the engine the profile wanted came back. Neither container logged any prompt text.</p>

<h2>What we did not establish</h2>

<ul>
<li>On Linux there is no Job Object. A bare-metal Linux node that is killed outright still leaves its engines running. In a container they die with the container.</li>
<li>Everything ran on a CPU. Two engines sharing one GPU is untested, and the on-demand GPU switching from 3.50 covers only the Ollama engine.</li>
<li>No image ships <code>llama-server</code> yet. The node drives one you install, or launches one you mount.</li>
<li>If two engines report the same model name, the engine whose name sorts first wins. The order in your configuration file is not visible to the process, because .NET hands configuration keys back sorted. It is logged once.</li>
</ul>

<p>A node without the new section behaves exactly as before. The release notes are on <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.60.0">GitHub</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_engines">inferhub.devart.solutions</a>.</p>
