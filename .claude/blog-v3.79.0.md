slug: inferhub-3-79-will-it-fit
id: 6adf55d178b988b67893c16e (EN visible, BG hidden)
title: InferHub 3.79: will it fit? Choose a context, get the engine
excerpt: Paste a model and say how much context you need. The node tells you which of its engines can run it, where it would sit, and why, with the numbers. Then it installs it there. Strata and colibri plan their own GPU placement, so they are asked.

<p>A node could already download a model from a link and install Strata's sizes. What nobody told you was whether the model would run. A 111 GB GGUF on a 24 GB card downloaded for an hour and then either crawled or did not load. Nobody asked how much context you needed either, and that is often what decides it. The same 80-billion-parameter mixture-of-experts fits with its experts in RAM at 32K tokens and has to be split across the bus at 256K.</p>

<p>InferHub 3.79 answers first. You paste a model and choose a context, with no minimum: the context you need is the one you pick. The node says which of its engines can run the model, where the model would sit, and why, with the numbers. Then it installs the model on the engine you pick, with that context.</p>

<pre><code class="json">POST /api/control/fit
{ "link": "unsloth/Qwen3-Next-80B-A3B-Instruct-GGUF", "context": 32768 }

POST /api/control/fit/install
{ "link": "…", "engine": "llamacpp", "variant": "Q5_K_S", "context": 32768 }</code></pre>

<h2>Arithmetic, not a lookup table</h2>

<p>The node reads the model itself: the first few megabytes of its GGUF file with a Range request (from Hugging Face or from Ollama's registry), or a checkpoint's <code>config.json</code>. It never downloads the weights to answer.</p>

<p>From those it works out the weights, the KV cache for your context, and a working reserve. It compares the total with the NVIDIA cards, the RAM, and the free disk where that engine keeps its models, each lowered by the limits the owner set for that runtime. The KV cache comes from the model's own layers and heads, which is where a table of famous models would be wrong:</p>

<ul>
<li>Qwen3-Next keeps a cache in only 12 of its 48 layers. Its metadata does not say so, so the node counts the layers from the tensors.</li>
<li>DeepSeek-style models cache a compressed latent, not every head.</li>
<li>Ollama gives each parallel slot a whole context, so its cache is counted once per slot.</li>
</ul>

<p>An engine is offered only if it can load the model at all. Ollama and llama.cpp run a GGUF, colibri a mixture-of-experts checkpoint, and Strata only its own repos. Only llama.cpp keeps a mixture-of-experts' experts in RAM while the rest sits on the card, which is often the fastest way to run a model too big for the card.</p>

<h2>Strata and colibri are asked, not second-guessed</h2>

<p>Strata and colibri decide their own GPU placement, version by version, so the node does not copy their rules into its arithmetic. It asks them:</p>

<ul>
<li><strong>Strata:</strong> the node runs Strata's own size table and recommendation for this PC, the same rules its setup uses, with the RAM and VRAM lowered to your limits. Strata's verdict for each size is the answer, and its recommended size comes first.</li>
<li><strong>colibri:</strong> for a model already on the node, the node runs <code>coli plan</code>. The plan says what goes on the card (the dense layers and the hot experts), what stays in RAM, and what is read from disk.</li>
</ul>

<p>colibri on the GPU is now one setting, <code>Colibri:Serve:Vulkan=true</code>. On an RTX 3090 Ti, GLM-5.3-Flash (321 billion parameters) put 4.3 GiB of dense layers and a 17 GiB expert tier on the card, and answered warm in 8 seconds, against 25 seconds on the CPU.</p>

<p>The context goes with the install, to wherever each engine keeps it:</p>

<ul>
<li>Strata's setup gets <code>--context</code>.</li>
<li>A llama.cpp model gets <code>ctx-size</code> in its preset.</li>
<li>colibri gets <code>--ctx</code> at every launch.</li>
<li>An Ollama model gets <code>num_ctx</code> on every request that does not set its own.</li>
</ul>

<h2>Checked on the card</h2>

<p>We stopped everything else using the card and compared each estimate with what the engine actually took. The estimate is meant to be an upper bound, and it was, usually by 0.6 to 1 GB:</p>

<ul>
<li>Ollama, llama3.1 8B at 64K: 14.1 GB estimated, 13.3 GB on the card.</li>
<li>llama.cpp: its own fit tool agreed with every placement. An 8B at 32K was all on the card. A 35B mixture-of-experts at 64K was all on the card, at the edge, as the estimate said. A 70B was split, with 47 of its 80 layers on the card, against the estimate's 60 per cent.</li>
<li>Strata's Coder took 21.8 GB of the card, against an estimate of 22.5 GB.</li>
</ul>

<p>Running it also found two things:</p>

<ul>
<li><strong>Unloading a model a request had loaded stopped nothing.</strong> This was true for both colibri and Strata, and GLM-5.3 kept 23 GB of the card after an "unload". It now stops.</li>
<li><strong>colibri's own plan was low by about 1 GB.</strong> Its expert tier keeps filling the card down to a 1 GiB reserve, so options from a plan now count the card that way.</li>
</ul>

<p>The published 3.79.0 binaries, run on the same box, gave the same answers: colibri's plan, Strata's verdicts, a model installed with the context it was asked for, and a Strata model unloaded with the card given back.</p>

<p>Models with sliding-window layers, such as gpt-oss, are still counted at the full context and come out about 2 GB over. That is said out loud in the notes, along with AMD and Apple GPUs, which the node does not read yet.</p>

<h2>In the app</h2>

<p>InferHub Desktop 0.4.0 has <strong>Check fit…</strong> beside Pull on the Models tab. The dialog asks for the context first, then shows one row per engine: its best size, the other sizes that fit, the node's reasons, and an Install button.</p>

<p>Next on this track is a catalogue of the newest and most popular models, sorted by what fits your machine, and a coordinator allowed to do the same for a node on a machine far away.</p>

<p><a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.79.0">Release notes</a> · <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/desktop/v0.4.0">InferHub Desktop 0.4.0</a> · <a href="https://inferhub.devart.solutions/#idocs_will_it_fit">Documentation</a></p>
