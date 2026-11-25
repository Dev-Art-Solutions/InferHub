id: 6b06dbdb68fc43092c4fe759
slug: inferhub-3-114-the-whole-split-model-track-in-one-command
title: InferHub 3.114: every claim about split models, checked in one command

<p>Over the last seven releases InferHub learned to run one model across several machines in more ways. A member can load its layers from its own disk. The coordinator can place a group itself. A request can survive a member disappearing. A small draft model can speed up a group whose machines are far apart. A group can answer several requests at once. And the coordinator shows who gave how much. Each release was checked on its own published images, on its own day. Until now nothing had run all of it on one release.</p>
<p>3.114 does. One command pulls the published images of a version, checks that they were built from the commit you name, runs every lab and prints PASS or FAIL for each claim:</p>
<pre><code>LAB_MODEL_DIR=lab-models VERIFY_BIG=1 VERIFY_REVISION=&lt;commit&gt; deploy/lab/shards/verify-lab.sh 3.114.0
  PASS  shard-b loaded from its own disk
  PASS  the member is the near one, not the two 20 ms away
  PASS  the same text as without a draft
  PASS  a stopped member exits 0
  PASS  the 70B group runs
== 57 held, 0 did not.</code></pre>

<h2>What it checks</h2>
<ul>
<li><strong>The split model itself</strong>, from earlier releases: a group an admin defined, a member leaving and a spare standing in, a planned stop, community groups, and machines with no direct path between them.</li>
<li><strong>Members load from their own disk</strong>, and a request that was running when a member was killed still answers, with the same text.</li>
<li><strong>The coordinator places the group itself.</strong> It picks the machine that holds the file as the head and the nearby machine as the member, and it says why it left the far ones out.</li>
<li><strong>A draft model on the head</strong>, and <strong>four requests at once</strong>, all answered.</li>
<li><strong>Who gave how much</strong>: a member stops, the spare stands in, and the count follows both.</li>
<li><strong>A 70B model that no single machine can hold</strong>, split over three, behind <code>VERIFY_BIG=1</code>.</li>
</ul>
<p>One rule runs across the sections. At temperature 0 the group must give the same answer however it was put together: loaded over the network, from the members&rsquo; disks, placed by the coordinator, or with a draft model.</p>

<h2>The run</h2>
<table>
<thead><tr><th>Published images</th><th>Checks</th><th>Held</th></tr></thead>
<tbody>
<tr><td>3.113.0</td><td>57</td><td>56</td></tr>
<tr><td>3.114.0</td><td>57</td><td><strong>57</strong></td></tr>
</tbody>
</table>
<p>The one check 3.113.0 did not pass: a machine in a group, stopped with <code>docker stop</code>, ended in an error instead of exiting cleanly. In 3.114 it exits cleanly.</p>
<p>It needs an 8B model and a 1B draft on disk, Docker and about an hour of CPU (the 70B adds a 39&nbsp;GB file and three 18&nbsp;GiB containers). That is why it is a command a maintainer runs, not a CI job.</p>

<h2>What we have not shown</h2>
<p>Everything runs on one PC, in Docker. So these are still open, for the whole track:</p>
<ul>
<li>GPUs in a group.</li>
<li>Machines of several owners, and a real network or router. Every delay between machines here is simulated.</li>
<li>A draft model or several requests at once on a group the coordinator placed itself. We checked each one separately.</li>
<li>A head that disappears in the middle of an answer.</li>
</ul>
