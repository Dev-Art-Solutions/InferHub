slug: inferhub-3-81-models-from-the-coordinator
id: 6ae167d4269b9607b802e8eb (EN visible, BG hidden)
title: InferHub 3.81: models from the coordinator, on the owner's terms
excerpt: A node on a far-away VM has no owner sitting at it. Its coordinator can now browse, check fit and install a model there, only where the owner said yes at install and within the disk the owner allowed. The browser found what the tests could not.

<p>InferHub 3.79 and 3.80 gave a node's owner two tools in the desktop app: <strong>Check fit</strong> (will this model run here, at this context, and on which engine) and <strong>Browse models</strong> (what is trending, with what fits first). That is fine when someone is sitting at the node. A node on a VM in another city has nobody sitting at it.</p>

<p>InferHub 3.81 lets that node's coordinator do the same three things: browse, check fit and install. But it can do them only where the owner said yes at install, and only within the disk the owner allowed.</p>

<pre><code class="json">GET  /api/admin/nodes/{id}/fit/catalog?source=all&amp;sort=trending&amp;kind=text
POST /api/admin/nodes/{id}/fit          {"link": "unsloth/Qwen3-8B-GGUF", "context": 32768}
POST /api/admin/nodes/{id}/fit/install  {"link": "…", "context": 32768, "engine": "llamacpp", "variant": "Q4_K_M"}</code></pre>

<h2>The hub asks; the node answers</h2>

<p>Each call goes down the connection the node opened, to that node only. The answer is the node's own reply to the same question from its own app, passed through unchanged. The hub keeps no copy of the estimate. A second opinion would drift the first release after.</p>

<p>The node decides every request by its owner's keys, and it reads them on each request. <code>Node:Management:AllowFromHub</code> is off by default, and the Windows setup asks. While it is off, all three calls are a 403 with the node's reason, and nothing reaches the network. If the owner switches it in the app or the settings file, the next request follows, with no restart.</p>

<h2>The owner's disk</h2>

<p>An install from the hub can be tens of gigabytes, on somebody else's disk. So the owner sets two numbers:</p>

<ul>
<li><code>MaxModelGiB</code>: the largest model the hub may install (no cap by default).</li>
<li><code>KeepFreeGiB</code>: the space an install must leave free where that engine keeps its models (20 GiB by default).</li>
</ul>

<p>If an install would break either one, the node refuses it with the numbers. Live, a 39.6 GiB <code>llama3.1:70b</code> was refused: "at most 30 GiB per model". The estimate says this ahead of time, for each option, so the console shows an install the owner would refuse as unavailable before anyone clicks.</p>

<p>If the node cannot measure the disk, it refuses the install while a floor is set. The owner asked for something the node cannot check, and saying no is the honest answer.</p>

<h2>The install is the pull you already know</h2>

<p>The node checks the chosen option and the context against its own estimate and terms, then names the pull. The hub sends that pull as a model command, so its progress shows in the console like any other pull. The context goes where each engine keeps it. Live, a Hugging Face GGUF installed through the hub landed with 16384 in its marker. The llama.cpp router served it at <code>n_ctx</code> 16384, and a chat through the hub answered "Paris" in 2.3 seconds.</p>

<h2>What the browser found</h2>

<p>All the tests passed, including one that runs a real coordinator and a real node against each other. Then the console was opened in a browser, and every node was listed as "older than 3.81". The page reads its node list from <code>/api/admin/nodes</code>, and that route did not carry the owner's terms. The tests call the API, not the page, so they could not see it. A second problem showed up the same way: "installing…" and the refusals were written to a note line that the console rewrites every few seconds. The panel now has its own line. Both were fixed before release.</p>

<h2>Not covered, said out loud</h2>

<ul>
<li>The new keys do not cover the older hub commands: pull by name, a Hugging Face link and a Strata install. Those behave as before, and <code>HuggingFace:Enabled</code> still gates every download from Hugging Face.</li>
<li>The console shows the terms the node had when it connected. The node's own answers are always current.</li>
<li>The live check ran the hub and the node on one machine, over a real connection. A node on another machine was not tried.</li>
</ul>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.81.0">v3.81.0</a>. Documentation: <a href="https://inferhub.devart.solutions/#idocs_hub_models">Models from the coordinator</a>.</p>
