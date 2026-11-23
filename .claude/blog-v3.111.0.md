slug: inferhub-3-111-a-draft-model-on-the-head
title: InferHub 3.111: a small model that makes a split model faster across the internet

<p>InferHub can split one model across several machines: a head runs it, and each member holds some of its layers. Every token visits each member in turn, so when the machines are far apart, the round trip is what a token costs. Across a 120&nbsp;ms round trip, an 8B model split over three machines in our lab managed one token a second.</p>
<p>3.111 lets the head run a second, small model of the same family next to the big one. The small model guesses a few tokens ahead, and one pass across the group checks all of them at once. The round trips are paid once per pass instead of once per token. This is llama.cpp&rsquo;s own speculative decoding, and at temperature 0 the text is the big model&rsquo;s own.</p>
<pre><code>"ShardGroups": {
  "llama-8b-split": {
    "Head": "shard-a", "Members": ["shard-b", "shard-c"],
    "Model": "llama31-8b.gguf", "Draft": "llama32-1b.gguf"
  }
}</code></pre>

<h2>What it does</h2>
<ul>
<li><strong>The draft is a file beside the model</strong> in the head&rsquo;s models directory, and it must share the model&rsquo;s vocabulary (Llama 3.2 1B for Llama 3.1 8B). <code>DraftTokens</code> (6 by default) is how many tokens it guesses per pass.</li>
<li><strong>It runs on the head&rsquo;s own processor.</strong> Left alone, llama.cpp would have put the draft where the big model&rsquo;s layers are, on the members, and every guess would then have crossed the network.</li>
<li><strong>A head without the file runs the group without the draft.</strong> The group&rsquo;s status says so. A missing draft costs speed, never the answer.</li>
<li><strong>It is off by default.</strong> On a local network it gains little, and it is not free.</li>
</ul>

<h2>Measured before it was allowed in</h2>
<p>In 3.101 we set a rule for any change to how a split model runs: it ships only if it is at least 1.25&times; faster on every prompt at some round trip, with the whole model&rsquo;s text. We tried four settings for how much the draft guesses, at a 30&nbsp;ms round trip. None of them was more than 1.05&times; faster. The best one went to the full test: an 8B model split over three containers on one PC, CPU only, three runs of each prompt.</p>
<table>
<thead><tr><th>Round trip (short / long prompt)</th><th>Without a draft</th><th>With the 1B draft</th></tr></thead>
<tbody>
<tr><td>llama.cpp&rsquo;s own split, 0&nbsp;ms</td><td>6.64 / 6.26 tokens/s</td><td>7.24 / 6.49 (1.09&times; / 1.04&times;)</td></tr>
<tr><td>llama.cpp&rsquo;s own split, 30&nbsp;ms</td><td>2.96 / 2.48</td><td>3.06 / 2.77 (1.03&times; / 1.12&times;)</td></tr>
<tr><td>llama.cpp&rsquo;s own split, 120&nbsp;ms</td><td>0.62 / 0.62</td><td><strong>0.89 / 0.86 (1.44&times; / 1.39&times;)</strong></td></tr>
<tr><td>An InferHub group through the coordinator, 0&nbsp;ms</td><td>6.18 / 6.08</td><td>6.60 / 7.25 (1.07&times; / 1.19&times;)</td></tr>
<tr><td>An InferHub group through the coordinator, 120&nbsp;ms</td><td>1.01 / 0.98</td><td><strong>1.34 / 1.71 (1.33&times; / 1.74&times;)</strong></td></tr>
</tbody>
</table>
<p>The text was the whole model&rsquo;s on every run. It passed the rule at 120&nbsp;ms, and only there. On these processors, a pass that checks five tokens costs nearly as much as two passes, and the draft needs the head&rsquo;s cores between passes. Saving round trips pays off only when round trips are most of the cost.</p>

<h2>What it costs</h2>
<ul>
<li>0.75&nbsp;GiB more memory on the head, for the small model.</li>
<li>About 30&nbsp;% more traffic per token: a pass carries every guessed token, accepted or not.</li>
<li>1.8 to 3.3 seconds more before the first token of a long prompt, because the small model reads the prompt too.</li>
</ul>

<h2>What we have not shown</h2>
<ul>
<li><strong>Graphics cards.</strong> A card checks several tokens for nearly the price of one, which can make the gain larger, or make the small model on the head&rsquo;s processor the slow part. Everything here ran on CPUs.</li>
<li><strong>A real network.</strong> Our lab adds delay and nothing else: no lost packets and no bandwidth limit. On a slow home upload, 30&nbsp;% more traffic matters.</li>
<li><strong>Other models.</strong> Only the 8B with this 1B. A 70B needs a small model with its own vocabulary, and how often its guesses are right is not measured.</li>
<li><strong>Several requests at once.</strong> That is the next release: a group that serves several requests at once, measured first.</li>
</ul>
<p>The method, the settings we tried and every run&rsquo;s files are in <code>docs/research/draft-model-on-the-head.md</code> in the repository.</p>
