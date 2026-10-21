<!-- live 2026-10-21 as ID 6ad8799b3541b2486c944757 (EN visible, BG hidden), created by this session -->
slug: inferhub-3-73-slots-the-engine-reports
title: InferHub 3.73: slots the engine reports
excerpt: In 3.72 the coordinator learned that a model can only run so many requests at once, but only Ollama told it how many, and only from configuration. llama.cpp can be asked. Now it is, for every loaded model, without loading anything.

<p>3.72 taught the coordinator to send a request to a node with a free slot for its model. It had one number per node, and only an Ollama node supplied one, read from <code>OLLAMA_NUM_PARALLEL</code>. 3.73 lets an engine that can answer the question answer it, model by model.</p>

<h2>llama.cpp knows its own number</h2>

<p>Ollama runs one request per model unless you set <code>OLLAMA_NUM_PARALLEL</code>. llama.cpp is the other way round. <code>llama-server</code> picks its number of slots itself, and on our test machine it picked 4 for a small <code>qwen2.5</code> model. It also tells you: <code>GET /props</code> answers <code>total_slots: 4</code>.</p>

<p>Until now a llama.cpp model counted as "no opinion" at the coordinator, which then balanced it by load alone. That is the engine that can run the most at once, ignored because it had not been asked.</p>

<h2>Asking without loading</h2>

<p>On a node, llama.cpp usually runs as a router: one server that loads models when they are asked for. In router mode, <code>/props?model=qwen</code> gives a model's own numbers. The catch is that the router loads any model a request names, and a question about a model is a request. Asked plainly, it would load an unloaded model just to say how many slots it would have.</p>

<p>So the node asks only about models that are already loaded, and adds <code>autoload=false</code>. For an unloaded model that answers "model is not loaded" and loads nothing. We checked. The node keeps the answer while the model stays loaded and forgets it when the model unloads, because a reload can come with different settings.</p>

<p>The number travels with the model to the coordinator, which uses it before the node's own. It is a fact about one model on one node, so it never shows up in the fleet's model list. <code>/api/tags</code> looks exactly as it did.</p>

<h2>Two smaller changes that make it work</h2>

<ul>
<li><strong>A node with several engines</strong>, such as an Ollama and a llama.cpp on one box, now declares its Ollama's number as the default for the models nothing answered for. You can still override that default with <code>Node:ModelSlots</code>, and an engine's own answer overrides both.</li>
<li><strong>Loading or unloading a model from the coordinator re-reports the node's models at once.</strong> Before, the coordinator heard about it at the next refresh, up to a minute later. Pulls and deletes already worked this way.</li>
</ul>

<h2>Checking it</h2>

<p>We ran a coordinator and one node with both engines: the Ollama on this machine, and a llama.cpp router the node started itself, with one small model, not loaded. The node registered with Ollama's number, 1, and nothing per model.</p>

<p>We loaded the model from the coordinator. 2.5 seconds later the coordinator showed <code>qwen: 4</code>. We unloaded it, and the number disappeared. Then we waited a full refresh interval, 65 seconds, and checked that the router still listed the model as unloaded. It did, so the node's regular check had not loaded it by asking.</p>

<h2>The published image disagreed about the unload</h2>

<p>Then we pulled the published 3.73.0 images and ran the same check. The load worked the same way: <code>qwen: 4</code> at the coordinator 2.6 seconds later. The unload did not. Twenty seconds after we unloaded the model, the coordinator still showed <code>qwen: 4</code>, and the number only went away at the next regular refresh.</p>

<p>The reason is in llama.cpp's log. It answers the unload request while the model is still stopping. The node re-reported its models straight away, as this release intended, but at that moment the router still listed the model as loaded. Our run from source had looked fine only because of timing.</p>

<p>The stale number only ever affected which node the coordinator preferred, and only for up to a minute. Nothing was refused because of it. But our release notes said an unload is reported at once, and it was not, so we fixed it. In 3.73.1 an unload waits until llama.cpp stops listing the model as loaded, the same way a load already waited for it to be loaded. We ran warm and unload three times on the fix: the number cleared 1.7 to 1.8 seconds after each unload.</p>

<p>Then we pulled the published 3.73.1 images and ran warm and unload three times again. The number appeared 2.6 to 2.7 seconds after each load and cleared 1.8 to 1.9 seconds after each unload.</p>

<h2>A test that failed in its cleanup</h2>

<p>One test about Strata models failed once in a full run. It failed in its cleanup, while deleting its own folder, because a file in it was still open. Model commands run in the background on a node, and this release makes a model switch re-read the model list, which re-reads Strata's config files. That read could still be finishing just after the node stopped. In real use nobody deletes a live model folder, so the fix went in the test: it now retries the delete for a moment. This release made the failure more likely, so we are counting it as ours, not as unrelated.</p>

<h2>What we have not established</h2>

<ul>
<li><strong>Other llama.cpp builds.</strong> Everything here was read from build b11417's answers, including how long it takes to stop a model after it says it will.</li>
<li><strong>Two llama.cpp nodes routing live.</strong> Our live run had one node. Choosing between nodes by these numbers is the same routing step as 3.72, and the tests cover it.</li>
<li><strong>A model whose settings set its own number of slots.</strong> It is read and re-read on reload the same way, but we did not run one.</li>
</ul>

<p>The release is <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.73.1">v3.73.1 on GitHub</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_engine_slots">inferhub.devart.solutions</a>.</p>
