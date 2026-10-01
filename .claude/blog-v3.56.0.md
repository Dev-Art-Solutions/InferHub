slug: inferhub-3-56-a-picture-on-a-cpu-and-a-negative-prompt-nobody-heard
id: 6abede5f0bb5c39555cba58a (EN-visible, BG-hidden; rendered, checked)
title_en: InferHub 3.56: a picture on a CPU in seconds, and the negative prompt nobody was listening to
excerpt_en: We added a 4-step image model that runs on a CPU. Running it showed that a negative prompt a model cannot use is ignored without a word, and two models in our catalogue had been doing that since 3.16. Now it is a 400 that says why.

<p>Until today, the only image model an InferHub node would offer without a graphics card was Stable Diffusion 1.5. It works, but at 30 steps a 512×512 picture takes tens of seconds on a CPU, and every other model in the catalogue wants a card.</p>

<p>3.56 adds <strong><code>lcm-dreamshaper</code></strong>. It is a Latent Consistency Model distilled from Dreamshaper 7, which is itself a fine-tune of SD 1.5. It is the same size as SD 1.5 and needs <strong>4 steps instead of 30</strong>. A node with no GPU offers it with no extra setting.</p>

<pre><code>{ "model": "lcm-dreamshaper", "prompt": "a lighthouse on a rocky coast at sunset, oil painting", "size": "512x512" }</code></pre>

<p>On a 64-core machine, a 512×512 picture took 27 seconds at 4 CPU threads, 9 seconds at 16 and 6 seconds at 32. In the published container at its default 4 threads it took about 19 seconds. It drew the lighthouse we asked for, and the same seed produced the same picture inside and outside the container.</p>

<p>One small honesty note: the model's repository is tagged MIT, but its weights are distilled from two models under the CreativeML OpenRAIL-M licence, whose use restrictions carry over. We declare the stricter one.</p>

<h2>The negative prompt nobody was listening to</h2>

<p>A negative prompt is how you say "and no text in the picture". It only works when the model runs a second, "unconditional" pass each step to steer away from it. That pass is called classifier-free guidance. If a model is not running it, the negative prompt has nothing to act on.</p>

<p>We assumed the new model would reject a negative prompt with an error, because its pipeline has no parameter for one. It does not reject it. It accepts the argument and silently ignores it. So we asked the same question of every other model in the catalogue, by reading the pinned library's code:</p>

<ul>
<li><strong><code>sdxl-turbo</code></strong> only runs the guidance pass when guidance is above 1, and its default guidance is 0.0. It had ignored every negative prompt it was ever sent since 3.16.</li>
<li><strong><code>flux-schnell</code></strong> only uses a negative prompt under a separate setting that our recipe never set. It had always ignored them.</li>
</ul>

<p>In both cases the caller got a normal 200 and a picture, possibly of exactly what they had asked to leave out, with nothing to say their words had been dropped.</p>

<h2>Now it is a 400 that says why</h2>

<p>The worker now refuses such a request before the model even loads, in two cases. The first is a model that has no negative prompt at all (<code>lcm-dreamshaper</code> and <code>flux-schnell</code>, marked in their recipes). The second is a request whose guidance is 1 or less, where the model would skip the pass. The message names the model and what would make it work:</p>

<pre><code>a negative_prompt only acts when guidance is above 1, and this request's guidance is 1 ('sd15' defaults to 7.5). Raise guidance or drop the negative prompt; it would otherwise be ignored.</code></pre>

<p>On the published image both refusals came back in under 20 milliseconds. This is a deliberate change in behaviour: a request that used to get a 200 while its negative prompt was ignored now gets a 400. Everything else is unchanged.</p>

<h2>What we have not checked</h2>

<p>We have not run <code>lcm-dreamshaper</code> on a graphics card, so its memory figure is arithmetic. We also did not render a picture with <code>sdxl-turbo</code> or <code>flux-schnell</code> to watch a negative prompt being ignored. That conclusion comes from reading the exact library version we ship.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.56.0">v3.56.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_cpu_image">a picture on a CPU</a> and <a href="https://inferhub.devart.solutions/#idocs_negative_prompt">negative prompts</a>.</p>
