slug: inferhub-3-61-all-of-llama-cpp
title_en: InferHub 3.61: all of llama.cpp — one router, models pulled from Hugging Face, and its own routes through the fleet
id: 6ac582f689927765fb687a0e (EN-visible, BG-hidden; rendered, checked)
excerpt_en: 3.60 could launch llama.cpp's server, one process per model. 3.61 runs its router instead: one process over many GGUFs, models pulled, loaded and unloaded from the hub, and grammars, infill and reranking through the fleet. The run against the real thing found three bugs before release.

<p>In 3.60 an InferHub node learned to launch llama.cpp's own server, <code>llama-server</code>. It did it the simple way: one process per GGUF file. One model, fixed when the process starts, serving chat or embeddings and nothing else. Twelve models on a box meant twelve processes on twelve ports, all twelve in memory at once. A new model meant logging into the box. And most of what llama.cpp can do beyond chat was out of reach of anyone talking to the hub.</p>

<p>3.61 runs llama.cpp the way llama.cpp now wants to be run, and lets the hub manage it.</p>

<h2>One router instead of a process per model</h2>

<p>Recent <code>llama-server</code> builds have a router mode. Started without a model, it lists a directory of GGUFs and a set of presets, starts each model the first time somebody asks for it, and unloads the least recently used one when too many are loaded. The node now launches it like that:</p>

<pre><code>"gguf": { "Type": "llamacpp",
          "Serve": { "Executable": "/opt/llama/llama-server",
                     "ModelsDir": "/models",
                     "MaxLoaded": 2,
                     "Presets": {
                       "nomic":       { "Model": "/models/nomic-embed-text.gguf", "Embeddings": true },
                       "jina-rerank": { "HfRepo": "gpustack/jina-reranker-v1-tiny-en-GGUF:Q8_0", "Reranking": true },
                       "qwen-long":   { "Model": "/models/qwen2.5-7b.gguf", "Settings": { "ctx-size": "32768" } } } } }</code></pre>

<p>Every file in the directory is a model under its own name. A preset gives a model a name and says what it is for. That second part matters: the router's own listing says whether a model is loaded, but not whether it is an embedding model or a reranker. So the node does not guess. An embedding preset is declared as one, a reranker preset as one, and everything else chats.</p>

<h2>Models managed from the hub</h2>

<p>The hub could already pull, delete and warm models on an Ollama node. A llama.cpp router can now do the same, through the router's own endpoints:</p>

<ul>
<li><strong>Pull</strong> a repository from Hugging Face by name, for example <code>bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M</code>. llama.cpp downloads it itself, and the node tells the hub the moment it is done.</li>
<li><strong>Warm</strong> a model, so the first real request does not wait for it to load.</li>
<li><strong>Unload</strong> it again. This is a new command, and it works for Ollama too.</li>
<li><strong>Delete</strong> a downloaded repository. A file somebody put in the models directory is refused: that file belongs to whoever runs the box, not to the hub.</li>
</ul>

<p>On a node that runs both Ollama and a llama.cpp router, a pull could go to either, and <code>owner/model</code> is a valid name in both worlds. The node does not guess. Without an engine name it refuses and names both engines, and the console now has a field for the engine and a button for unload.</p>

<h2>llama.cpp's own routes, through the fleet</h2>

<p>llama.cpp can do things the OpenAI API has no field for. They now go through the hub and through a standalone node, to whichever node holds the model:</p>

<ul>
<li><code>/v1/llamacpp/completion</code>, which takes a GBNF grammar, so the model can only produce text that matches it;</li>
<li><code>/v1/llamacpp/infill</code>, the fill-in-the-middle route editor plugins use for code completion;</li>
<li><code>/v1/llamacpp/tokenize</code>, <code>detokenize</code>, <code>apply-template</code>, <code>embedding</code>, and <code>props</code>.</li>
</ul>

<p>The body is passed through untouched and the engine's errors keep their own words. Some things are deliberately left out. <code>/slots</code> answers with the prompt a slot last held, and nothing in InferHub hands one caller's prompt to another. Changing a model's adapters is a setting every other caller shares. And there is no streaming on these routes, because the ordinary completion routes already stream from the same engine.</p>

<p>There is also <code>/v1/rerank</code>, in the shape Jina and Cohere use. It is answered by a llama.cpp reranker or by the cross-encoder tool InferHub already had, so retrieval can use either. And Ollama's sampler settings that llama.cpp understands, such as <code>top_k</code>, <code>min_p</code>, <code>repeat_penalty</code> and <code>mirostat</code>, now reach it.</p>

<h2>What running it found</h2>

<p>Everything above was tested, and the tests passed. Then it ran against a real llama.cpp router, a real Ollama, a real hub and a real download from Hugging Face, and three things were wrong.</p>

<ul>
<li><strong>A node with several engines had never offered model management.</strong> It told the hub whether it could manage models once, when it registered. That happens before its engines have started, so the answer was always no. This had been true since 3.60, for Ollama too. The node now answers from its configuration.</li>
<li><strong>A pulled model waited up to a minute before the hub would route to it</strong>, and a deleted one stayed routable just as long. The node only reported its models on a timer. This had been true since model commands first shipped. It now reports them as soon as a pull or a delete finishes: the pulled repository was routable 14 seconds after the pull started.</li>
<li><strong>A reranker downloaded by a preset appeared twice</strong>: once under its preset name, and once under its repository name, because the router also lists llama.cpp's download cache. The second entry would have been declared a chat model. It is no longer listed.</li>
</ul>

<p>None of the three shows up in a test that uses a stand-in for the engine. All three show up the first time a real node runs it.</p>

<p>The same ran once more from the published images, with llama.cpp's Linux build started by the node inside the <code>:colibri</code> container: the download, the routes, the pull, the unload and the delete all behaved the same way. One practical note from that run: inside a container, give the router its own port, because llama.cpp's default is the port the node already uses there.</p>

<p>Not established yet: a vision model through the router, and download progress in bytes, which llama.cpp does not report.</p>

<p>The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.61.0">v3.61.0 release</a>, and the configuration is on the <a href="https://inferhub.devart.solutions/#idocs_llamacpp">documentation site</a>.</p>
