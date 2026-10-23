slug: inferhub-3-75-engines-that-know-their-limits
title: InferHub 3.75: we held the engines to their limits, and never told them what the limits were
excerpt: colibri planned 147.5 GB of cache on a runtime limited to 8. Strata filled the card past its limit, was stopped, and filled it again. Since 3.75 the node tells them — and running GLM-5.3-Flash, all 321B of it, on one box found two ways Strata 0.2 had been quietly beating us.

<p>InferHub 3.67 gave every runtime on a node hard limits: CPU, cores, memory, VRAM and which GPUs. They are held from the outside — a Windows Job Object or a Linux cgroup, processor affinity, and for GPU memory a watchdog that stops a runtime that goes over. That works for anything that simply uses what it uses.</p>

<p>Two of our engines do not. <a href="https://github.com/JustVugg/colibri">colibri</a> and <a href="https://github.com/Niko1221/Strata">Strata</a> both size themselves from what they can see, and a Job Object is not something they can see.</p>

<h2>What they did with limits they did not know about</h2>

<p>colibri 2.0, on a runtime limited to 8&nbsp;GB, wrote this line at startup:</p>

<pre><code>[cache] 64 slots/layer of 64 experts: 147.5 GB budget (88% of what the OS still offers)</code></pre>

<p>It planned for the machine, not for its limit, and would have met the limit as a failed allocation in the middle of a request.</p>

<p>Strata filled the graphics card down to its own reserve. Under a 12&nbsp;GB limit it used 14.9, 18.2, then 21.9&nbsp;GB. The watchdog stopped it, its supervisor started it, and it filled the card again. That loop is what 3.67 was designed to do with a runtime over its limit. It is also useless.</p>

<h2>So the node tells them</h2>

<p>Since 3.75, when a node starts colibri or Strata it passes the limits in each engine's own words, and keeps holding them exactly as before:</p>

<ul>
<li><strong>colibri</strong> gets <code>--ram</code> (90% of the memory limit; its launcher hands that to each model family in its own variable), <code>OMP_NUM_THREADS</code> for the cores, and its own Vulkan and CUDA switches for the GPU and its memory. Never <code>--gpu</code> or <code>--vram</code>: on a build without CUDA those flags make colibri exit, so a limit would have become an engine that never starts.</li>
<li><strong>Strata</strong> gets a larger <code>--vram-reserve-mib</code> (card size minus the limit, read from the driver), its CPU worker list, and a lower RAM budget for the models that have one. Its engine reads options from its config file, not from the command line, so the node starts it on a copy of that file. The original is never written.</li>
</ul>

<p>A limit that changes what a runtime was told restarts it. The limits report has a new field, <code>told</code>, that lists what each runtime was given.</p>

<p>With limits on: colibri planned 7.0&nbsp;GB and used 7.3. When the coordinator narrowed it to 4&nbsp;GB, it restarted with <code>--ram 3</code> and used 3.5. Strata answered at 11.7&nbsp;GB of its 12, with no stop.</p>

<h2>Two things Strata 0.2 was doing that we did not know about</h2>

<p>Neither showed up in a test. Both showed up on the first real run.</p>

<p><strong>The person's settings win.</strong> Strata 0.2 keeps the options you set by hand in a second list, <code>user_args</code>, and applies it on top of the generated ones at every start. Our raised reserve went into the generated list, lost to a hand-set 964&nbsp;MiB, and the watchdog stopped Strata at 14.9&nbsp;GB. The node now writes what it tells into both lists.</p>

<p><strong>A child process that could not be born.</strong> With a core limit on, Strata could not start its image encoder. Windows refused with <em>Access is denied</em>. The cause took longer to find than the fix. At startup, Strata's server gives its own process an owner-only default security descriptor, so that files it creates are private. A process with that descriptor cannot start a child inside a Job Object that has an affinity limit. Each half alone is fine. Together, <code>CreateProcess</code> fails every time. We reproduced it in fifteen lines of Python with no InferHub involved. Strata's core limit on Windows is now set on each process instead of on the job, and re-applied every few seconds. Every other runtime keeps the job's.</p>

<h2>A 321-billion-parameter model on one box</h2>

<p>The point of colibri is running a mixture-of-experts model bigger than your RAM by reading the experts from disk. We tried the biggest one it supports, <a href="https://huggingface.co/zai-org/GLM-5.3-Flash">GLM-5.3-Flash</a>: 328&nbsp;GB of weights on Hugging Face, converted once to colibri's format, 194.7&nbsp;GB, on a hard disk.</p>

<p>Served through a coordinator and a node with a 96&nbsp;GB memory limit, colibri was told <code>--ram 86</code> and held 57.6&nbsp;GB:</p>

<ul>
<li>first answer, cold, with 195&nbsp;GB coming off the disk: "Paris", 6 minutes 16 seconds;</li>
<li>a warm one, streamed: "Red, blue, yellow", 25 seconds, the model's own reasoning included.</li>
</ul>

<p>One request sent right after the cold load ran past five minutes and was cancelled. That is the node's default timeout for an upstream call. A stream is not cut that way. For a model this size on a disk, raise it.</p>

<h2>colibri 2.0 and Strata 0.2</h2>

<p>The node's colibri image now carries colibri 2.0. 2.0 removed its <code>/v1/brio</code> route. Its one decision API is now System One: typed questions about a piece of text, with a probability for every allowed answer. InferHub serves it at <code>/v1/systemone</code>, on the coordinator and on a single node. <code>/v1/brio</code> still works against an older colibri and says where to go against a new one.</p>

<p>Strata moved to its 0.2 line, and the sizes the coordinator can install grew from 9 to 21, including Qwen3.6-35B-A3B, the first Strata model that is not Qwen3.8-Flash-Next.</p>

<h2>Not established</h2>

<ul>
<li>A GPU colibri: 2.0's Vulkan tier was not run. The card here was busy with another project for most of the day.</li>
<li>Two GPUs, for either engine.</li>
<li>The new Strata sizes, installed through the coordinator. The list was checked against Strata's own setup script.</li>
<li>System One against a dedicated decision model. Only OLMoE answered, through its token probabilities.</li>
</ul>

<p>The release is <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.75.0">v3.75.0 on GitHub</a>, and the docs are at <a href="https://inferhub.devart.solutions/#idocs_engines_told">inferhub.devart.solutions</a>.</p>
