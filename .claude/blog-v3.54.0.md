slug: inferhub-3-54-a-deadline-for-each-kind-of-work
id: 6abc183a35593e061e9bea74 (EN-visible, BG-hidden; rendered with real <p>, checked)
title_en: InferHub 3.54: a deadline for each kind of work
excerpt_en: A chat answer and a five-second video clip used to share one deadline, so a hub that made video had to give chat half an hour too. InferHub 3.54 sets the deadline per capability, and a job that runs out now says which deadline it hit.

<p>When an InferHub hub sends a job to a node, it waits a limited time for the answer. Until now that limit was one number, <code>Dispatcher:TimeoutSeconds</code>, and it applied to everything: a chat answer, an embedding, a transcription, a picture, a video clip. The default is 300 seconds.</p>

<p>That is fine for chat and too short for video. In 3.28 we timed a real five-second clip on a 3090 Ti: about 143 seconds to load the model cold, then about 330 seconds to generate. At the default, the clip dies while its model is still loading. The advice since then was "raise it to 1800". That works, but it also means a stuck chat request now waits half an hour before anyone gives up on it.</p>

<h2>One deadline per capability</h2>

<p>3.54 lets you set the deadline for each capability separately:</p>

<pre><code>"Dispatcher": {
  "TimeoutSeconds": 300,
  "Deadlines": { "video": 3600, "image": 900 }
}</code></pre>

<p>Chat and embeddings keep 300 seconds. A clip gets an hour. A cold 360° panorama gets fifteen minutes. The key is the capability the job was routed on (<code>chat</code>, <code>embed</code>, <code>transcribe</code>, <code>speak</code>, <code>image</code>, <code>video</code> and so on), or a custom tool's own capability name. Any capability you do not name keeps <code>TimeoutSeconds</code>.</p>

<p><strong>Nothing changes unless you set it.</strong> The shipped config has no deadlines, so a hub that upgrades without touching its config behaves exactly as before. We did not make <code>video: 3600</code> the default, even though it is the right number, because that would change your hub without you reading about it first. A value below 1 stops the hub at startup, and the error names the key.</p>

<h2>A timeout that says which clock ran out</h2>

<p>When 3.28's clip failed, the only message was <code>The operation has timed out.</code>, at progress 0. There were three different deadlines it could have been, and finding the right one took three attempts. Now the message names the capability, the seconds and the config key:</p>

<pre><code>the hub's dispatch deadline for video (300 s, Dispatcher:TimeoutSeconds) expired before the node answered</code></pre>

<p>A failed video or image job carries this in <code>error.message</code>, and the hub logs one warning line with the same three facts.</p>

<h2>Two older bugs, found by the new tests</h2>

<p>Before this release, no test in the project had ever let a dispatch deadline actually run out. The first tests that did found two bugs that had been there since the tool routes were added:</p>

<ul>
<li><strong>The tool and audio routes answered a timeout with an empty 500.</strong> Chat has always answered 504. Nothing on <code>/api/tools/*</code> or <code>/v1/audio/*</code> caught the timeout, so the caller got a server error with no body. They now answer <strong>504</strong> with the message above, and <code>/v1/audio/*</code> uses OpenAI's error format with <code>code: "deadline_exceeded"</code>.</li>
<li><strong>The in-flight gauge never came back down.</strong> When a blocking job timed out or its caller disconnected, the hub fixed its routing count but not its metrics. So <code>inferhub_requests_in_flight</code> on <code>/metrics</code> went up by one for every such job and stayed there. Those jobs are now counted as failed, exactly once.</li>
</ul>

<p>We checked both on the published images. On 3.53.0, a tool job past its deadline came back as a 500 with zero bytes, and the in-flight gauge still read 1 seven seconds later. On 3.54.0, the same request came back as a 504 at 3.007 seconds, naming <code>Dispatcher:Deadlines:echo</code>. We read <code>/metrics</code> every few seconds for twenty seconds, including after the node's late answer arrived: in-flight stayed at 0 and failed at 1. On the same hub, a chat answer that took 3.5 seconds came back with a 200, so chat was not held to the tool's two-second deadline.</p>

<h2>What we have not checked</h2>

<p>We have not rendered a real clip under a real <code>video: 3600</code>. These deadlines were tested with a test worker on a short clock, not on a graphics card. The node's own time limits (<code>Ollama:RequestTimeout</code>, <code>Upstream:TimeoutSeconds</code>, a tool manifest's <code>requestTimeoutSeconds</code>) still apply. A hub deadline longer than the node's gains nothing, because the node gives up first. The shipped video manifest already allows an hour.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.54.0">v3.54.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_dispatch_deadlines">one deadline per capability</a>.</p>
