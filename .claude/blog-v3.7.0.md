# Blog post — v3.7.0

**Slug:** `inferhub-3-7-a-node-and-its-ollama-in-one-container`
**Title (EN):** `InferHub 3.7: a node and its Ollama, in one container`
**Excerpt (EN):** `The node image never needed a GPU, because the node never did the computing — it shelled out to an Ollama on the host. So you installed one, guessed at host.docker.internal, and kept two things alive on one machine. Now there is a second image with Ollama inside it: pass --gpus all to use your card, leave it off to run on the CPU, or turn the inference off entirely and keep just the vector store.`

**Publish visible in one shot** (`isVisible_en: true`, `isVisible_bg: false`) — the connector is
insert-only and the slug locks on creation, so a hidden draft cannot be flipped later. `list_posts`
first.

**No `curl -H 'Authorization: …'` snippets in the HTML** — the blog sits behind a Cloudflare WAF that
blocks the request when the post body contains them.

---

```html
<p>The InferHub node image never needed a GPU, and that was honest: the node does not do the computing. It translates a request, hands it to Ollama, and formats what comes back. The model runs in Ollama, and Ollama ran on your host.</p>

<p>Which made the single-machine story worse than it should have been. Install Ollama, install InferHub, work out whether the container reaches the host at <code>host.docker.internal</code> or <code>172.17.0.1</code>, get it wrong once, and then keep two separate things alive on one box. In 3.7 there is a second image with Ollama inside it.</p>

<pre><code class="bash">docker run -d --name inferhub --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$INFERHUB_API_KEY" \
  -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:ollama

docker exec inferhub ollama pull llama3.2</code></pre>

<p>Then point any OpenAI client at <code>http://localhost:5081/v1</code>. Version 3.5 removed the coordinator from a single-machine deployment; this removes the host install. One container is now a complete inference endpoint, with retrieval included if you want it.</p>

<h3>Three modes, and none of them refuses to start</h3>

<p>Pass <code>--gpus all</code> and it runs on your card. Leave it off and it runs on the CPU. Set <code>Ollama__Supervisor__Enabled=false</code> and no inference process starts at all — the node comes up as a vector store, which is a real deployment: the <code>/api/vector</code> data plane takes vectors you supply, so it needs no model. It then reports zero models, which is the honest answer, and a chat request fails cleanly instead of hanging.</p>

<p>An earlier draft of this release did something different: it refused to start without a visible GPU. The reasoning felt right at the time — a silent CPU fallback is exactly the kind of quiet wrongness this project keeps designing against. It was still the wrong call, and it got reversed before release. CPU is a legitimate mode rather than a misconfiguration. Embedding models run on it perfectly well, small models are fine, and a vector store needs no model at all; refusing would have made two of the three modes impossible.</p>

<p>The danger was never the CPU. It was <strong>silence</strong> — pulling four gigabytes of CUDA runtime, dropping a <code>--gpus</code> flag somewhere, getting two tokens a second, and spending an afternoon blaming the model. So the node says what it found, in its first log lines, both ways:</p>

<pre><code class="bash">info: CUDA: 1 device(s) visible to this process — NVIDIA GeForce RTX 3090 Ti.
info: CUDA: no devices visible to this process; inference will run on the CPU.
      In a container, pass '--gpus all' to use a card.</code></pre>

<p>and it is on <code>/api/status</code> as a <code>gpu</code> block. If you want the guarantee rather than the report — a fleet node whose whole purpose is the card, where a flag falling out of a unit file should be loud — <code>Ollama__RequireGpu=true</code> turns a missing device into a startup failure that names <code>--gpus all</code>. It is off by default, including in this image.</p>

<h3>The supervisor turned out to be an init system</h3>

<p>The container runs two processes: the node as PID 1, and <code>ollama serve</code> as its child. There is no s6, no supervisord, no entrypoint script with an ampersand and a <code>wait</code>. Version 3.4 gave the node a supervisor for its own local Ollama — discover the binary, start it, pump its output into the log, probe it on a short deadline of its own, and restart it when it dies or wedges, under a budget that gives up loudly rather than looping forever. That is an init system. It just had not been asked to be one before.</p>

<p>It matters more in a container than on a host, because nothing else in there is watching. A wedged Ollama leaves the container looking perfectly alive: the health endpoint answers, the process list looks right, and every inference request hangs. So we verified it the only way that counts — by killing Ollama inside a running container and watching the node bring it back:</p>

<pre><code class="bash">Ollama at http://127.0.0.1:11434/ is Unreachable after 3 consecutive failed probes.
Restarting Ollama (Unreachable) via Binary '/usr/local/bin/ollama' — attempt 1 of 3 in this 00:10:00 window.</code></pre>

<h3>Two things worth knowing if you write GPU Dockerfiles</h3>

<p><strong>A container needs the driver, not a CUDA toolkit.</strong> These are two different things from two different places. <code>libcuda.so.1</code> is the driver, version-locked to the host kernel module, and the NVIDIA container runtime injects it at <code>docker run</code> when you pass <code>--gpus</code>. The CUDA <em>runtime</em> — cuBLAS, cuDART, the compute kernels — is version-locked to the application, and it already ships inside Ollama's own tarball. So this image finals on the same stock <code>dotnet/aspnet</code> base as the plain one. An <code>nvidia/cuda</code> base would have added a third copy of a runtime nothing loads, at about two gigabytes, and pinned us to a CUDA minor version we did not choose. It is also why CPU mode is free: the same tarball carries the CPU kernels.</p>

<p><strong><code>NVIDIA_DRIVER_CAPABILITIES</code> defaults to <code>utility</code>, and <code>utility</code> does not include <code>compute</code>.</strong> An image that forgets to set it gets a working <code>nvidia-smi</code> — the card is listed, the driver version is right, every diagnostic you would reach for says yes — and no <code>libcuda</code> at all. Inference then runs on the CPU, silently, at a fraction of the speed. It is the worst shape a bug can have. The image sets <code>compute,utility</code>, and a test reads the Dockerfile and fails if <code>compute</code> ever disappears from that line, because the failure it prevents is invisible everywhere else.</p>

<p>And a third, for anyone detecting GPUs inside containers: <strong>under WSL2 — which is Docker Desktop on Windows — <code>/dev/nvidia*</code> does not exist.</strong> The GPU arrives through <code>/dev/dxg</code> and the driver libraries are injected from <code>/usr/lib/wsl/lib</code>. Every recipe that checks for device nodes reports "no GPU" on what is probably the most common GPU-with-Docker setup there is. InferHub loads <code>libcuda.so.1</code> and asks the driver how many devices it can see — the same question the inference engine asks a moment later, which is the only question that actually matters.</p>

<h3>What is in it, and what is not</h3>

<p>It is about 4 GB. The plain <code>inferhub-node</code> image is unchanged at around 340 MB, still multi-arch, still with no Ollama in it — that is deliberate, because a "bundled mode" flag on a single image would have grown every coordinator-plus-node stack by four gigabytes for a feature it does not use. amd64 and NVIDIA only; arm64 would mean Jetson-specific bundles and hardware to test them on. The tag is <code>:ollama</code> rather than <code>:gpu</code>, because the image runs perfectly well without a card and naming it after the accelerator would make most of its uses look like a workaround.</p>

<p>No model is baked in and none is pulled at boot. An image with a model in it is a nine-gigabyte image that is wrong for everyone who wanted a different model, and pulling at startup means reaching the internet from a machine nobody said could. <code>docker exec … ollama pull</code> is the interface, and mounting a volume at <code>/data</code> is required in practice rather than optional, because that is where the pulled models live.</p>

<p>Ollama's own port is not published. The container's surface is InferHub's API, which refuses to start without a key when it is reachable from outside the box; putting an unauthenticated inference endpoint next to it, on the same GPU, would undo that for nothing. Every <code>OLLAMA_*</code> environment variable passes straight through, because the supervisor spawns a child that inherits the environment — so keep-alive, parallelism and flash attention are all one <code>-e</code> away and none of them needed a configuration surface of ours.</p>

<p>It is also a good mesh node. Point <code>Coordinator__Url</code> at a hub instead, and the container reports that it can manage models — over an Ollama it genuinely controls — so the console can pull models into it. That has not been true of a container before.</p>

<h3>Verified on the hardware, not asserted</h3>

<p>Packaging is where this project has shipped its worst bugs: four images that were dead on arrival while every test was green, each one found by pulling the published artifact and running it. So all three modes were run against a real RTX 3090 Ti before the tag went out — GPU inference with Ollama reporting the model at 100% GPU, CPU inference reporting 100% CPU, the vector-store mode with no Ollama process in the container at all, retrieval end to end with real citations, the restart, the volume surviving a container replacement, and the GPU requirement both refusing and passing.</p>

<p>Zero new dependencies, as for the eleven releases before it. No project file changed; the dependency here is a <code>curl</code> in a Dockerfile, in an image nobody has to pull. Upgrading from 3.6 changes nothing — the plain images are the same, and all three new configuration keys default to today's behaviour.</p>

<p>Code on <a href="https://github.com/Dev-Art-Solutions/InferHub" style="color:#00d4ff;text-decoration:underline;">GitHub</a>, docs at <a href="https://inferhub.devart.solutions/#idocs_bundled" style="color:#00d4ff;text-decoration:underline;">inferhub.devart.solutions</a>.</p>
```

---

## Facebook

> InferHub 3.7: a node and its Ollama, in one container.
>
> The node image never needed a GPU, because the node never did the computing — it shelled out to
> an Ollama you installed on the host, reached through host.docker.internal or 172.17.0.1, and kept
> alive yourself. Now there is a second image with Ollama inside it:
>
> docker run --gpus all ... ghcr.io/dev-art-solutions/inferhub-node:ollama
>
> Leave --gpus off and it runs on the CPU. Turn the supervisor off and it is a vector store with no
> inference process at all. Three modes, one image, and none of them refuses to start.
>
> An earlier draft *did* refuse to start without a GPU. We reversed it: CPU is a legitimate mode,
> and the danger was never the CPU — it was silence. So the node tells you what it found, in its
> first log lines, either way.
>
> Two things we learned packaging it, useful to anyone writing a GPU Dockerfile:
> NVIDIA_DRIVER_CAPABILITIES defaults to "utility", which gives you a working nvidia-smi and no
> libcuda — an image where every diagnostic looks right and inference silently runs on the CPU.
> And under WSL2 there are no /dev/nvidia* device nodes at all, so every "check for device nodes"
> recipe reports no GPU on the most common Docker+GPU setup there is.
>
> All three modes verified on a real 3090 Ti before tagging. Still zero new dependencies.
> 👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.7.0

## X

> InferHub 3.7: a node and its Ollama, in one container.
>
> docker run --gpus all … inferhub-node:ollama → an OpenAI-compatible endpoint on your card.
> Drop --gpus and it runs on CPU. Drop the supervisor and it's a vector store. Three modes, one
> image, none of them refuses to start.
>
> Two packaging traps we hit, free to anyone writing a GPU Dockerfile:
> • NVIDIA_DRIVER_CAPABILITIES defaults to "utility" — working nvidia-smi, no libcuda, silent CPU
> • under WSL2 there is no /dev/nvidia* at all, so device-node detection reports "no GPU"
>
> Verified on a real 3090 Ti before tagging. Zero new deps.
>
> https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.7.0
