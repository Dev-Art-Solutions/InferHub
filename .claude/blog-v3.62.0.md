slug: inferhub-3-62-many-colibri-models-one-node
title_en: InferHub 3.62: many colibri models on one node — loaded when asked for, freed when idle, picked from the hub
id: 6ac69e668e9d4cc6f10fd138 (EN-visible, BG-hidden; rendered, checked)
excerpt_en: A colibri node used to run one model, chosen at boot, in RAM for as long as the node ran. 3.62 gives it a catalogue: every converted model listed, the one a request names loaded, idle ones stopped to give the memory back, and the coordinator choosing which stay loaded.

<p>colibri runs Mixture-of-Experts models that do not fit in memory. It keeps the dense part of the model in RAM and reads the experts from disk as they are needed, so a model that needs hundreds of gigabytes runs on a machine with a fraction of that. InferHub has driven it since 3.58. Until now, though, an InferHub node ran exactly one colibri model: the one in its configuration, started when the node started, in RAM for as long as the node ran. Three converted models meant three nodes, or logging into the box and restarting it to switch.</p>

<p>3.62 changes that. A colibri node now holds a catalogue.</p>

<h2>A directory of models, each loaded when it is asked for</h2>

<p>Point the node at a directory of converted models. Every sub-directory that holds a converted model becomes a model under its directory's name:</p>

<pre><code>"Colibri": {
  "Serve": {
    "ModelsDir": "/models",
    "MaxLoaded": 1,
    "OnDemand": true,
    "IdleUnload": "00:10:00"
  }
}</code></pre>

<p>Every model in the catalogue is listed to the hub, loaded or not, so a client can ask for any of them. When a request names a model that is not running, the node starts colibri's server for it and waits until it answers. colibri's server holds exactly one model, so the node runs one server per loaded model, each on its own port.</p>

<p><code>MaxLoaded</code> is how many may be in memory at once, and it defaults to one. When a different model is asked for and the slots are full, the node picks the least recently used model that is not busy and stops it <em>before</em> it starts the next one. Two models never end up sharing the machine's memory by accident.</p>

<h2>On demand: give the memory back</h2>

<p>With <code>OnDemand</code> on, a model that has had no requests for <code>IdleUnload</code> is stopped. Not paused, not hinted: the process is ended, which is the only way the memory actually comes back to the machine. The next request for it starts it again.</p>

<h2>The coordinator chooses what stays loaded</h2>

<p>The console has a new panel, <strong>Colibri models</strong>. It shows every model on every colibri node: loaded, loading, unloaded or failed, whether it is pinned, how many requests it is serving and how long it has been idle. Each model has <strong>Load</strong> and <strong>Unload</strong>, and each node has <strong>On demand</strong> and <strong>Keep loaded</strong>.</p>

<p>Load pins a model: it is loaded now, and it is never evicted or stopped for being idle. On a node that may hold one model, Load is a switch: the old model is stopped, then the new one starts. Unload unpins it and stops it.</p>

<p>These buttons write the node's profile, the same mechanism the coordinator already uses to start and stop engines. So the choice is desired state, not a one-off command. If the node or the coordinator restarts, the pinned model is loaded again. A profile can say the same thing directly:</p>

<pre><code>"colibri": { "loaded": ["olmoe"], "onDemand": true }</code></pre>

<p>As with everything a coordinator asks of a node, the node decides what it allows. A profile can only pick from the models the node actually has, by name, and never more than <code>MaxLoaded</code> of them. Anything else is refused by the node, with the list of what it does have.</p>

<h2>What running it showed</h2>

<p>It ran against the real engine: colibri 1.12.1 with OLMoE, in a node image built from the release, through a real coordinator. Only one converted model was available, so the catalogue held the same model under two names. That is enough to see the behaviour, because loading, switching and stopping all act on names and processes.</p>

<ul>
<li>The node started with nothing loaded and both models listed.</li>
<li>The first request started the model, which answered its health check after 5.9 seconds; the reply came back in 32.6 seconds, most of it the CPU reading the prompt.</li>
<li>A request for the second model stopped the first before starting the second. There was only ever one engine process in the container.</li>
<li>A model pinned from the coordinator stayed loaded past the idle timeout. An unpinned one was stopped about 90 seconds after its last request, with the timeout set to 90 seconds.</li>
<li>A request for another model while the only slot was pinned was refused in a quarter of a second, with a sentence saying which model was pinned and what to change.</li>
<li>Brio, colibri's scoring route, loaded its model on demand and answered.</li>
</ul>

<p>Two small things were fixed on the way. The console showed idle time frozen at whatever it was when the node last reported, up to a minute earlier; it now counts forward. And removing a pin was logged by the node as "nothing to change"; it now says what it did.</p>

<p>Not established yet: two genuinely different models, and two models loaded side by side with the real engine. Each colibri server sizes its cache from the memory that is free when it starts, so two loaded at once do not split the memory evenly. That is worth knowing before you raise <code>MaxLoaded</code>.</p>

<p>The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.62.0">v3.62.0 release</a>, and the configuration is on the <a href="https://inferhub.devart.solutions/#idocs_colibri_catalogue">documentation site</a>.</p>
