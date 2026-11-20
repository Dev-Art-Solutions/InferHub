slug: inferhub-3-108-a-member-loads-from-its-own-disk
title: InferHub 3.108: a machine in a group loads its share of the model from its own disk

<p>InferHub can split one model across several machines: a head runs it, and each member holds some of its layers. Until this release the head sent every member its share over the network on each first load. For the 70B model of 3.102 that was 12&nbsp;GiB a member, which takes over half an hour on a home uplink. A member that already had the model file on its disk got no use out of it.</p>
<p>Now it does. A member that holds the file reads its own layers from it before the head starts, and the head sends it only what it always sends: the small tensors.</p>

<h2>What we measured</h2>
<p>An 8B model split over three machines in Docker on one PC, a first load every time:</p>
<table>
<thead><tr><th></th><th>Members without the file</th><th>Members with the file</th></tr></thead>
<tbody>
<tr><td>Sent to the two members</td><td>2&nbsp;868&nbsp;MiB</td><td>473&nbsp;MiB</td></tr>
<tr><td>Until the first answer, at 50&nbsp;Mbit/s and 30&nbsp;ms</td><td>582&nbsp;s</td><td>165&nbsp;s</td></tr>
<tr><td>Text</td><td>the whole model&rsquo;s</td><td>the whole model&rsquo;s</td></tr>
</tbody>
</table>
<p>Both runs wrote the same text, character for character, as the whole model in one container.</p>

<h2>How, without changing llama.cpp</h2>
<p>Before it sends a tensor larger than 10&nbsp;MiB, llama.cpp&rsquo;s RPC client asks the worker whether it already has a tensor with that hash. A worker running with its cache answers from a file named after the hash. This release has the member write those files itself, from its own copy of the model: the exact bytes of each tensor, named the way the worker looks them up. Tensors of 10&nbsp;MiB or less are always sent, and that is the 473&nbsp;MiB that remains.</p>
<p>The coordinator tells each member which layers it will hold and waits until it has said whether it has the file and finished reading it in. The group's status says where each member's layers came from:</p>
<pre><code>"loads": [
  "shard-b: from its own disk (1039 MiB in its cache)",
  "shard-c: over the network ('llama31-8b.gguf' is not in /models)"
]</code></pre>
<p>A member without the file is loaded over the network as before. Every tensor is found by the hash of its own bytes, so a wrong file can only cause a miss. It cannot change an answer.</p>

<h2>Where the file comes from</h2>
<p>From the coordinator, the way it has delivered models since 3.63: an admin gives a node a Hugging Face link, and a sealed node downloads it through the coordinator's relay and supply, never from the internet directly. The member looks for the file where that download puts it.</p>

<h2>What this does not show</h2>
<p>It does not show the file delivered through the coordinator in the lab, which mounts it on each machine. It does not show a 70B filled this way, a GPU worker, or a llama.cpp build other than b12182, whose cache this relies on. Everything ran on one PC, with the network simulated.</p>
<p>Next on this track: the coordinator assembles a group by itself, from the machines&rsquo; offers and the round trips between them.</p>
