slug: inferhub-3-65-strata-reads-pictures
title: InferHub 3.65: Strata reads pictures — per size, refused at once when it can't, added from the hub
excerpt: A Strata size now tells the hub whether it reads pictures. A picture sent to one that can't is refused in milliseconds instead of after a minutes-long load, the hub can add the image encoder to a size already installed, and a node's refusal finally reaches the client as a 400, not a 502.

<p>InferHub 3.64 made a node serve a <a href="https://github.com/Niko1221/Strata">Strata</a> install — Qwen3.8-Flash-Next, a 125B model on one gaming GPU — as a catalogue the coordinator picks from. It shipped chat only, and its notes said why: a size's pictures were "a setup choice the node cannot see".</p>

<p>That was wrong. When Strata's setup prepares a size with its image encoder, it writes the encoder into that size's config, and Strata's server starts the encoder from exactly that. The node had the answer on disk the whole time. 3.65 reads it.</p>

<h2>Every size says whether it reads pictures</h2>

<p>The hub's status now carries it per model, and the console's Strata panel shows a <strong>pictures</strong> chip next to a size that has the encoder:</p>

<pre><code>{ "name": "strata-coder-iq1_m", "state": "loaded",   "images": true  }
{ "name": "strata-q2_0",        "state": "unloaded", "images": false }</code></pre>

<p>A chat with an <code>image_url</code> part goes to a size with <code>"images": true</code> like any other chat.</p>

<h2>A picture it can't read is refused at once</h2>

<p>Before, a picture sent to a size without the encoder did something expensive and then failed. The node started that size — one to three minutes of mapping tens of gigabytes — and on the default of one loaded size at a time, it stopped whatever was loaded first. Only then did Strata look at the request and refuse the picture.</p>

<p>Now the node checks the size's config before it starts anything. On our test box the same picture was refused in 82 ms, and in 35 ms when streamed. No server was started and nothing was evicted. The error names the call that fixes it.</p>

<h2>The hub can add pictures to a size</h2>

<p>An install from the hub now says whether it wants pictures:</p>

<pre><code>POST /api/admin/nodes/{id}/strata/install
{ "model": "strata-coder-iq1_m", "vision": "yes" }</code></pre>

<p><code>"yes"</code> puts the encoder on the GPU, <code>"cpu"</code> runs it on the processor, and <code>"no"</code> leaves it out. If you leave the field out, the node's own <code>Strata:Install:Vision</code> setting decides, as it did before. In the console it is a <strong>with pictures</strong> box next to <strong>Install on node</strong>.</p>

<p>Asked for a size that is already installed without pictures, the node runs Strata's setup again for that size. Setup skips everything that is already there and downloads only the encoder, about 0.9 GB.</p>

<p>There was one trap, and we found it by reading setup's code before running it. When setup runs again for a size, it answers every question again with its own recommendation. It only reuses an earlier config's answers when it is adopting a new copy of Strata. So our Coder, set up with a 32K context, would have come back at 128K, the size setup recommends for a 24 GB card. Nothing would have said so. The node now passes the config's own context and KV-cache choice back to setup, so the only change to the size is the encoder.</p>

<p>The node won't do this while that size is loaded. Setup rewrites the config the running server was started from, and that server would carry on without the encoder anyway. Unload the size first.</p>

<h2>A refusal is a 400 now, not a 502</h2>

<p>The first time we sent the picture through the hub, the refusal came back as a <strong>502</strong>. That status means "the server failed". Every failed job from a node had always reached the client that way, including a request the model simply can't take.</p>

<p>A node's refusal now carries its status. The hub's OpenAI endpoint, and a standalone node's, answer it as a <strong>400</strong> <code>invalid_request_error</code>, whether the request was streamed or not. Any other node failure is still a 502, and so is every failure from a node older than 3.65.</p>

<h2>What we ran</h2>

<p>We used a real Strata install on an RTX 3090 Ti with 256 GB of RAM, and the hub and node from this release. Both sizes on the box had been installed without pictures.</p>

<ul>
<li>We sent a 448×448 picture (a red circle, a blue square, the text "OK 42") to the Q2_0 size: <strong>400 in 82 ms</strong>, and 35 ms streamed, with nothing loaded.</li>
<li>Adding pictures to the installed Coder through the hub took about two minutes. Setup reported both model files as already downloaded, fetched the 0.91 GB encoder, and kept <strong>32K context and 8-bit KV</strong>. The config gained the encoder and nothing else.</li>
<li>The same picture to the Coder through the hub: <strong>200 in 15 s</strong> — <em>"The picture shows a red circle in the upper left and a blue square in the lower right on a white background, with the black text "OK 42" appearing in the upper right."</em></li>
<li>Streamed, "How many shapes are in this picture?": first byte in 40 ms, answer <strong>2</strong>.</li>
<li>Neither the hub's log nor the node's contains the prompts or any part of the picture.</li>
</ul>

<p>The first Coder attempt timed out at the hub's 300 seconds. Another program on the box had loaded a 23 GB model onto the same card, and Strata was down to 0.1 tokens a second, though it had already encoded the picture. The numbers above were measured with that program paused.</p>

<p>Then we checked what was actually published. We pulled the 3.65.0 coordinator and node images from GitHub's registry, confirmed both were built from the release's commit, and ran their binaries against the same Strata install. The picture to Q2_0 was refused with a 400 in 5 ms. Asking to add pictures to the Coder again ran nothing, because it already had them. The picture to the Coder came back described correctly in 42 s, including 16 s to load the model from cold.</p>

<h2>What we didn't establish</h2>

<ul>
<li>The encoder on the CPU (<code>"vision": "cpu"</code>). Only the GPU encoder was run.</li>
<li>Pictures on the original, Swift and Unsloth versions. Only the Coder was run.</li>
<li>A fresh install with pictures, as opposed to adding them to a size already installed.</li>
<li>An engine's own refusal inside a job, such as a prompt too long for the context, is still a 502. Only the node's own refusals carry a status in this release.</li>
</ul>
