id: (not yet posted)
slug: inferhub-3-117-tools-without-a-language-engine
title: InferHub 3.117: a node with no language engine still offers its tools

<p>An InferHub node does not have to run a language model. The <code>:tools</code> image speaks and transcribes, and the <code>:diffusion</code> image draws pictures. Each runs its own worker process, with no Ollama involved.</p>
<p>Joined to a coordinator without an Ollama beside it, though, such a node offered nothing. It registered and showed up in the coordinator's status, with an empty list of what it could do. No speech, transcription or picture request was ever sent to it. A node running on its own, with no coordinator, was not affected.</p>

<h2>What changed</h2>
<p>There were two reasons, one on each side.</p>
<ul>
<li><strong>The node now reports what it can do, even when its language engine does not answer.</strong> The report that lists its models also carries its tools, and the node used to skip that report when the engine could not be asked. Now it sends the report anyway. It repeats the model list it last sent, or an empty one, together with its tools as they are at that moment.</li>
<li><strong>The coordinator now sends a tool its work while the language engine is down.</strong> An unhealthy engine used to take the whole node out of rotation, tools included. Now the engine's health only affects the engine's own models. A chat for a model on that node still gets the answer that names the broken backend, and a speech request goes to the speech worker.</li>
</ul>
<p>There is nothing to configure. A node whose engine answers behaves exactly as before.</p>

<h2>Measured</h2>
<p>We used the published images: a coordinator and a <code>-tools</code> node on one network, with the node's Ollama address pointing at a closed port.</p>
<ul>
<li><strong>3.116:</strong> the coordinator listed no capabilities for the node, and a speech request got a 404.</li>
<li><strong>3.117:</strong> the coordinator listed <code>rerank</code>, <code>speak</code> and <code>transcribe</code>. A speech request came back as a WAV file, while the node reported its language engine as unreachable.</li>
</ul>

<h2>What we have not shown</h2>
<ul>
<li>A <code>:diffusion</code> node on a GPU drawing a picture through the coordinator with no Ollama. The change is the same for every tool, but this check used the speech worker.</li>
</ul>
