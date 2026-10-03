slug: inferhub-3-58-a-model-bigger-than-your-ram-and-the-errors-that-looked-like-answers
id: 6ac18794d74b2be3ebc66b2f (EN-visible, BG-hidden; rendered, checked)
title_en: InferHub 3.58: a model bigger than your RAM, and the errors that looked like answers
excerpt_en: A node can now drive colibri, which runs Mixture-of-Experts models that do not fit in memory by reading their experts from disk. Running a real one through InferHub found that a refused stream had been reaching OpenAI clients as an empty success, from every backend.

<p><a href="https://github.com/JustVugg/colibri">colibri</a> is an open-source inference engine with an unusual idea. A Mixture-of-Experts model uses only a small part of its weights for each token: a router picks a few "experts" out of hundreds. colibri keeps the always-used part of the model in RAM and reads the experts from disk only when the router asks for them. A 744-billion-parameter model then needs about 25 GB of RAM and a fast SSD, not a rack of GPUs. A small one, OLMoE with 7 billion parameters, runs in 8 GB.</p>

<p>3.58 makes colibri a backend for an InferHub node. There is a new image, <code>inferhub-node:colibri</code>, about 410 MB. You mount a model directory, and the node starts the engine itself, starts it again if it exits, watches its health, and tells the hub when the model is ready. If you already run colibri yourself, you set the backend type to <code>colibri</code> and point the node at it.</p>

<h2>Why not just call it an OpenAI server?</h2>

<p>colibri's gateway speaks the OpenAI API, so a node could already have been pointed at it as a generic OpenAI backend. We ran a real engine through a real node and a real hub to see what that would get wrong. It got three things wrong.</p>

<p><strong>Every request failed.</strong> Our OpenAI client sends its request body in chunks without saying how long it is. vLLM, llama.cpp and the cloud vendors all accept that. colibri's gateway is built on Python's standard <code>http.server</code>, which needs the length up front. It answered every request with "Request body must be between 1 and 4194304 bytes". A colibri node now sends every request with its length. No test stub would have found this, because a stub accepts whatever it is given.</p>

<p><strong>Too many requests were dropped, not queued.</strong> colibri runs one generation at a time per context slot. We sent it twelve requests at once, directly. Seven got answers. The other five got a dropped connection, not the "too busy" error its documentation describes. A colibri node now tells the hub how many requests it can run at once, so the hub queues the rest. Through a node, all of them got answers.</p>

<p><strong>Embeddings would have been sent to a box that cannot do them.</strong> colibri has no embeddings endpoint. A colibri node says it does chat only, so the hub refuses an embedding request before it reaches the node.</p>

<h2>The errors that looked like answers</h2>

<p>To test how a refusal reaches a client, we sent colibri something it does not support: a request with a frequency penalty, as a stream, through the OpenAI-compatible endpoint. We got back <code>200 OK</code> and a stream with no text that ended normally with <code>"finish_reason": "stop"</code>. A client would show an empty answer. Nothing in the response said that anything had gone wrong.</p>

<p>This was not a colibri problem. When any backend refuses a streamed job, the node sends the hub an error message in the same shape Ollama uses. Our Ollama-compatible endpoint passed that message through correctly. Our OpenAI-compatible endpoint treated it as the last chunk of a normal answer. That was true on the hub and on a node running on its own, for every backend.</p>

<p>Now, if nothing has been sent to the client yet, the response is a <code>502</code> with the backend's own message, the same as a request that does not stream:</p>

<pre><code class="json">{"error": {"message": "the OpenAI-compatible upstream returned 400 BadRequest: Token penalties are not supported yet.", "type": "api_error", "param": null, "code": null}}</code></pre>

<p>If the refusal comes after the first words have already been sent, the stream ends with an error event, which the official OpenAI SDKs turn into an exception.</p>

<h2>One more thing we found on the way</h2>

<p>When the node starts the engine itself, the engine is still loading when the node first connects to the hub. The node correctly reports "I could not ask", not "I have no models". But nothing told it to report again once the engine was up. The model only appeared at the next routine refresh, about a minute later. Now the first good health check after a failed one triggers a fresh report. In the published image the model was ready to use 17 seconds after the container started.</p>

<h2>What we have not checked</h2>

<p>colibri can keep several conversations in separate "KV slots", so that each one can reuse its earlier context instead of reprocessing it. A colibri node can pin each conversation to a slot. Only colibri's GLM-5.2 and 5.3 engines accept more than one slot, and those models are 372 GB and more. We could not run one, so this part is tested against a stand-in and not against a real engine. The default is one slot, and with one slot the node sends nothing extra.</p>

<p>We also measured that the small model we did run, OLMoE, reused nothing between requests: the same request sent twice in a row took a minute both times. On a CPU, colibri is a way to run models you otherwise could not run at all. It is not a way to run them fast. On a 32-core machine, OLMoE produced about 26 tokens a second once it started, and a 3,300-token prompt took about a minute before the first word.</p>

<p>The image has colibri's CPU engine only, not its GPU builds. colibri's Brio mode, which scores a fixed list of answers instead of writing text, is not exposed yet. It needs its own API, and that is a separate release.</p>

<p>The release notes have every measurement: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.58.0">InferHub v3.58.0</a>. The setup is in the <a href="https://inferhub.devart.solutions/#idocs_colibri">documentation</a>.</p>
