<!-- published 2026-10-12 as ID 6acbfc7a46092b8fe238649c, EN visible / BG hidden -->
slug: inferhub-3-67-hard-limits-per-runtime
title: InferHub 3.67: the node sets the limits, the coordinator may only go lower
excerpt: A node can now cap what each runtime it starts may use — CPU, cores, memory, VRAM and which GPUs — for Ollama, llama.cpp, colibri, Strata and its tool workers. The operating system holds what it can, a watchdog holds the rest, and the node says which. The coordinator sees the caps and can only set lower ones.

<p>One GPU box often runs several model runtimes at once now: Ollama, a couple of <code>llama-server</code>s, colibri, Strata, speech and image workers. Until 3.67 nothing stopped any one of them from taking the whole machine. The node's resource cap from 3.47 is soft: it stops the node taking new work while the box is busy, and it limits nothing that is already running. And the coordinator could neither see nor change any of it.</p>

<p>3.67 gives each runtime hard limits, set by the node, visible to the coordinator, and never raised by it.</p>

<h2>Limits per runtime, set on the node</h2>

<p>The node's configuration names a runtime and what it may use:</p>

<pre><code>"Node": { "ResourceLimits": { "Engines": {
  "llamacpp": { "CpuPercent": 50, "Cores": 8, "MemoryMiB": 16384 },
  "strata":   { "VramMiB": 20000, "Gpus": "0" },
  "tools":    { "CpuPercent": 25, "Gpus": "none" }
} } }</code></pre>

<p>The runtimes are <code>ollama</code>, <code>llamacpp</code>, <code>colibri</code>, <code>strata</code> and <code>tools</code>. A limit covers every process of that runtime together, so three <code>llama-server</code>s share one budget and Ollama's runners count against Ollama. Any other name stops the node at startup, because a limit written against a typo is a limit that does not exist.</p>

<h2>Held by the operating system where it can be</h2>

<p>On Windows each runtime goes into its own Job Object. That gives a real CPU cap, a set of cores, and a memory limit: an allocation past it simply fails inside the engine. All three can change while the runtime keeps running. On Linux the cores are set on every thread, and CPU and memory go into a cgroup when the node is allowed to make one.</p>

<p>No operating system caps GPU memory. So the node reads it every few seconds, per process, and stops a runtime that goes over. Its own supervisor starts it again, and the node counts the breach with the numbers. The GPU list is the set of devices the runtime is started with, and changing it restarts the runtime.</p>

<p>The node reports how each limit is held: by the OS, at launch, by the watchdog, or not at all, with the reason. It never says a limit holds when it doesn't. And it only limits processes it starts itself. An Ollama running as its own service, or somebody else's server, is not the node's to cap.</p>

<h2>The coordinator sees them and may only go lower</h2>

<p>The coordinator's console has a new <strong>Runtime limits</strong> panel. For every node and runtime it shows the node's own limit, the coordinator's, what is in force, how it is held, and what the runtime is using right now. An admin can set the coordinator's limits there, or through <code>/api/admin/nodes/{id}/limits</code>.</p>

<p>In force is the smaller of the two, field by field. A value above the node's own is refused with the node's number. A profile written around that is refused by the node itself, which keeps its own value: the check that matters runs on the box, not on the hub. Where the node set no limit, the coordinator may set one, because limiting is narrowing.</p>

<p>On the box, the node's own page shows the same table and edits the node's limits. A change is in force within a few seconds, with no restart.</p>

<h2>Checked on a real card</h2>

<p>On a Windows 11 box with an RTX 3090 Ti, the node ran a <code>llama-server</code> with a small model, limited to four cores, 10% of the CPU and 4 GB of memory:</p>

<ul>
<li>Windows itself reported the server running on exactly the four cores, and on two after a change on the node's page, with no restart.</li>
<li>The coordinator asked for 50% of the CPU and was refused, with the node's 10% in the answer.</li>
<li>With a 100 MiB VRAM limit, the node read 255 MiB on the card and stopped the server.</li>
<li>With no GPU allowed, the server restarted on the CPU and its VRAM went from 206 MiB to zero.</li>
</ul>

<p>The same checks ran again against the published 3.67.0 binaries, and the console panel was opened on the published coordinator.</p>

<p>That run also found a gap before release. The Vulkan build of llama.cpp ignores <code>CUDA_VISIBLE_DEVICES</code>, so a GPU limit that set only that variable would have been a limit on paper. The node now sets the Vulkan and ROCm variables too.</p>

<h2>What isn't proven yet</h2>

<p>The Linux cgroup path has not run on a machine that allows it. A default container and the CI runner don't, and there the node falls back and says so. GPU memory inside a container may be reported against the host's process ids, which match nothing the node started, so the VRAM limit there may never trip. Both are written down in the release notes.</p>

<p>Still no new dependencies. Everything above is the operating system's own interfaces, called directly.</p>

<p><a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.67.0">InferHub 3.67.0 on GitHub</a> · <a href="https://inferhub.devart.solutions/#idocs_engine_limits">Documentation</a></p>
