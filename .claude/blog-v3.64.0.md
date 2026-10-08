slug: inferhub-3-64-strata-a-125b-model-on-one-gaming-gpu
title: InferHub 3.64: a 125B model on one gaming GPU — its sizes picked, and installed, from the hub
excerpt: Strata runs Qwen3.8-Flash-Next on one graphics card plus system RAM. A node now serves every size Strata installed as a model the fleet can route to, the coordinator picks which one is loaded, and a Hugging Face link installs another size through Strata's own setup.

<p><a href="https://github.com/Niko1221/Strata">Strata</a> runs <strong>Qwen3.8-Flash-Next</strong> — 125 billion parameters, 24,576 experts — on one 12 GB graphics card and 32–64 GB of RAM. The busiest experts sit on the card, all of them in RAM, a lookup table on the SSD. It comes in nine sizes across four versions: the original, UkisAI's Swift 1.5, ISTA-DASLab's Coder, and Unsloth's 4-bit.</p>

<p>InferHub 3.64 makes a node serve a Strata install the way 3.62 made it serve a colibri catalogue. <strong>Every size Strata installed is a model the fleet can route to.</strong> The one a request names is started, the coordinator picks which one stays loaded, and the coordinator can <strong>install another size from Hugging Face</strong>.</p>

<h2>What a node needs</h2>

<pre><code>"Backend":     { "Type": "strata" },
"Strata":      { "Root": "/opt/strata" },
"HuggingFace": { "Enabled": true }</code></pre>

<p><code>Root</code> is a Strata checkout set up with its own installer. <code>HuggingFace:Enabled</code> is only needed if the hub may install more sizes. A box reaches the internet only when its operator says so. Strata can also be one engine among several under <code>Backend:Engines</code>, next to Ollama or llama.cpp.</p>

<h2>Every installed size is a model</h2>

<p>Strata's setup writes one config per size: <code>strata-iq2_xs.json</code>, <code>strata-coder-iq1_m.json</code> and so on. Each one is a model named after its file, and the hub lists it whether it is loaded or not. A request for one makes the node start Strata's own server for that config on a loopback port. The node waits until the server's health check says the model is loaded, then sends the request.</p>

<p>By default one size is loaded at a time, because each holds tens of gigabytes of RAM and most of the GPU. Asking for a different size is a switch: the node stops the first one before it loads the second. With on-demand on, an idle size is stopped to free the memory.</p>

<h2>The coordinator picks</h2>

<p>The console has a new <strong>Strata models</strong> panel. It works like the colibri one: Load, Unload, On demand. It writes the node's profile, so the choice survives a reboot of either side:</p>

<pre><code>POST /api/admin/nodes/{id}/strata/models/strata-coder-iq1_m/load
POST /api/admin/nodes/{id}/strata/on-demand/enable

"strata": { "loaded": ["strata-coder-iq1_m"], "onDemand": true }</code></pre>

<h2>Installed through the coordinator</h2>

<p>A node with <code>HuggingFace:Enabled</code> reports every size Strata knows. The panel's <strong>Install on node</strong> sends one, and so does a Hugging Face link to one of Strata's repos:</p>

<pre><code>POST /api/admin/nodes/{id}/strata/install   { "model": "strata-q2_0" }
POST /api/admin/nodes/{id}/huggingface      { "url": "https://huggingface.co/ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF", "quant": "IQ2_XS" }</code></pre>

<p>The node does not download the files itself. It runs <strong>Strata's own setup</strong>. Setup pins every repo to a commit, checks every file's SHA-256, resumes a cut download, and then does what a plain download cannot: it builds the expert pack and the profile for <em>this</em> machine's RAM and GPU. A folder of GGUF files on its own is something nothing can serve.</p>

<p>One install runs at a time, since two are two 70 GB downloads on one disk. Setup draws its progress bar with a carriage return and no newline, so a line reader would have seen a 30 GB file as one line when it finished. The node reads characters instead, and each percent reaches the console's command feed with its bytes. An install counts as finished when the new config is listed, not when setup exits 0.</p>

<h2>What we ran</h2>

<p>On an RTX 3090 Ti with 256 GB of RAM, Strata's own setup installed the Coder (58 GB). A chat through a real hub started Strata's server. It loaded in 18 seconds (23 GiB of experts in RAM, 15.7 GiB of them cached on the GPU) and answered "Paris". A streamed request had its first byte in 46 ms.</p>

<p>Then we installed a second size, Q2_0, <strong>from the hub</strong>. The node ran setup, the download progress arrived at the hub as percentages, and a chat sent during the install was answered in under four seconds. The new size was listed as soon as setup finished.</p>

<p>For the release we ran the <strong>published</strong> 3.64.0 code. Docker's VM would not come back up on this machine, so we pulled the coordinator and node images from the registry, checked that their revision label was the tag's commit, extracted the application layer and ran those binaries. The published hub served the size it had installed ("Jupiter", HTTP 200) and switched to the Coder from the profile. The node stopped the idle size before it loaded the new one.</p>

<p>Neither log held a word of either prompt. Strata has a debug switch that prints the raw model text, and the node never passes it to the server. Strata's request monitor, which keeps the last hundred prompts for a web page, is refused in the node's configuration.</p>

<h2>Under the hood</h2>

<p>Strata has the same shape as colibri: a Python server per model, tens of gigabytes each. So the rules for which model loads, which one gives way and which ones the hub pinned moved into one engine-neutral catalogue, and colibri and Strata are its two users. Colibri's behaviour and messages are unchanged, and its tests ran untouched. A second copy of those rules would have been the place where they quietly diverged.</p>

<h2>Not established</h2>

<ul>
<li>AMD, Linux and multi-GPU Strata. Only Windows with one NVIDIA card was run.</li>
<li>Two Strata sizes loaded at once. One 3090 Ti holds one.</li>
<li>Pictures. Strata's vision is a setup choice the node cannot see, so the node declares chat only.</li>
<li>Throughput. During the published-binary check a 23 GB model belonging to another program on the same card was reloaded, and the speeds from that run measure the contention, not Strata.</li>
</ul>

<p>InferHub 3.64.0 is on <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.64.0">GitHub</a>. The docs are at <a href="https://inferhub.devart.solutions/#idocs_strata">inferhub.devart.solutions</a>.</p>
