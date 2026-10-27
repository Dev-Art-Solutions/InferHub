slug: inferhub-3-80-browse-models-that-fit
id: 6ae0a1e9269b9607b802e596 (EN visible, BG hidden)
title: InferHub 3.80: browse models that fit
excerpt: What is trending and popular on Hugging Face and in Ollama's library, with what fits your machine first. Each model opens Check fit, where you choose the context and install it. Along the way, we found that a repo's own summary is not always the model.

<p>InferHub 3.79 answered "will this model fit?" But you had to arrive with a link, and the link came from a browser tab sorted for somebody else's machine. Today the top of Hugging Face's trending list is a 200 GB DeepSeek. On a 24 GB card that is an hour-long download and a disappointment.</p>

<p>InferHub 3.80 lists what is trending and popular on Hugging Face and in Ollama's library, with what fits this machine first. Each model opens Check fit, where you choose the context and install it.</p>

<pre><code class="json">GET /api/control/fit/catalog?source=all&amp;sort=trending&amp;kind=text&amp;q=qwen&amp;limit=30</code></pre>

<h2>Ordered by where the weights would sit</h2>

<p>Each model gets a class: on the card, card + RAM (a split, or a mixture-of-experts with its experts in RAM), RAM on a machine without a card, size unknown, or too big. The list is ordered by that class, then by each source's own order, so Hugging Face and Ollama interleave. The machine is the same one Check fit sees: the same usable VRAM and RAM, lowered by the owner's limits.</p>

<p>The list does not claim a context, and every line says so. <code>qwen2.5:32b</code> is "on the card" on a 24 GB card at 18 GiB of weights. Open it in Check fit at 32K and it splits, because Ollama's parallel slots each hold the whole context. That is the intended division of work: the list finds candidates, and Check fit does the arithmetic for the context you choose.</p>

<p>Too-big models are not hidden. "Popular, and not for this box" is an answer too. If you only want the ones that fit, there is a filter.</p>

<h2>A repo's summary is not the model</h2>

<p>The first live run listed a 200 GB DeepSeek as "67 MiB, on the card". Hugging Face's summary of that repository counted 115 million parameters, which were its LoRA's, not the model's.</p>

<p>So the node now reads each repository's file list: one small API call per repo, six at a time, kept for ten minutes. It never reads the model. Every quant is sized by its real bytes, grouped the way a download groups them. The next run found three more files that looked like small quants of a 200 GB model:</p>

<ul>
<li>a LoRA,</li>
<li>an importance matrix (<code>imatrix</code>),</li>
<li>speculative-decoding drafts (<code>mtp-…Q8_0.gguf</code>, <code>…-MTP-draft.gguf</code>).</li>
</ul>

<p>None of them is counted as a quant now. A quant that carries its MTP layers (<code>IQ4_XS-MTP-00001-of-00003</code>) is the whole model, so it stays. An Ollama model is sized by its tags (<code>7b</code>, <code>32b</code>, <code>8x7b</code>) at 4-bit. Each entry opens at the largest size of its best class, and the other sizes are listed with their own.</p>

<h2>Each source on its owner's terms</h2>

<p>Hugging Face is asked only when the node may reach it (<code>HuggingFace:Enabled</code>), as for a download. Ollama's library is asked only on a node with an Ollama engine. If one source is off or fails, the answer says why, and the other is still listed. Nothing downloads from the list: an entry is a link for Check fit.</p>

<p>Ollama's library has no API, so the node reads its web page. If that page changes shape, the node says so instead of showing an empty list.</p>

<h2>In the desktop app</h2>

<p>InferHub Desktop 0.5.0 has <strong>Browse models…</strong> beside Check fit on the Models tab: choose the source, the order (trending, downloads, newest), text or vision, a search, and "fits only". Each row shows where the model would sit and how big it is, a box of its other sizes, and <strong>Check fit…</strong>.</p>

<h2>Not established</h2>

<ul>
<li>Ollama's library has no trending order, so its popular one is used.</li>
<li>A side file named like none of those four patterns would still be listed as a quant.</li>
<li>safetensors checkpoints for colibri are not listed, because most are dense models no engine here runs. A pasted link still reaches Check fit.</li>
<li>The hub cannot browse for a node yet. That is the next phase, switched on at the node's install.</li>
</ul>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.80.0">v3.80.0</a> and <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/desktop/v0.5.0">Desktop 0.5.0</a>. Documentation: <a href="https://inferhub.devart.solutions/#idocs_browse_models">Browse models that fit</a>.</p>
