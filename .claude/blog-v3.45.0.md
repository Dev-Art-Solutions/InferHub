# Blog post — v3.45.0

Slug: `inferhub-3-45-a-reranker-that-is-not-a-chat-model`
Title (EN): **InferHub 3.45 — a reranker that isn't a chat model pretending to be one**
EN visible / BG hidden. Author: Admin.

Excerpt (EN): The reranker InferHub shipped in v2.6 works by prompting a chat model to score
passages it was never trained to rank. v3.45 adds the thing the seam was always designed for: a
real cross-encoder — and running it for the first time found a deadlock a green test suite could
never have seen.

> **No shell commands in the HTML** — the blog sits behind a Cloudflare WAF that blocks the request,
> not the command. JSON bodies only.

---

## content_en

<p>InferHub has had exactly one reranker since v2.6: hand the top candidates to a chat model already on the fleet, with a prompt asking it to score each one, and parse a JSON array out of whatever comes back. It works — the eval numbers in the <a href="https://inferhub.devart.solutions/#idocs_hybrid">hybrid search docs</a> show it recovering literal-identifier lookups vector search misses — but it is also the most variable number on that page: the same pass <em>hurt</em> retrieval with a weak 8B model and was <em>perfect</em> with a stronger one, at roughly ninety times the latency of the hybrid fusion it sits behind. A general chat model is not a cross-encoder, and asking it to behave like one has always cost a full round trip to find out.</p>

<p>The interface this reranker sits behind, <code>IReranker</code>, has said since the day it was written that this was coming: "so a dedicated cross-encoder can slot in later without touching the pipeline." v3.45 is that release.</p>

<h2>What it does</h2>

<p><code>Retrieval:Rerank=cross-encoder</code> routes reranking to <code>sentence-transformers</code>' <code>CrossEncoder</code>, which loads BAAI's <code>bge-reranker</code> family directly — a model actually trained to take a query and a passage and produce one relevance score, not a chat model improvising a job description. It runs as a <strong>tool worker</strong>, the same shape InferHub's speech-to-text and text-to-speech tools have used since v3.9 and v3.10: a supervised child process, talking one JSON object per line, restarted if it dies, never a native binding inside the coordinator or the node.</p>

<pre><code class="json">"Retrieval": {
  "Rerank": "cross-encoder",
  "RerankModel": "bge-reranker-v2-m3"
},
"Tools": { "Enabled": true, "Allowed": ["rerank"] }</code></pre>

<p>Four models ship in the manifest: a small English one for a CPU box, two English-only BAAI sizes, and a multilingual one. It lives in the existing <code>:tools</code> image next to Whisper and Piper — no new image to pull — though it is the first dependency in that image to bring in <code>torch</code>, and by a wide margin the largest thing that requirements file has ever taken on. Nobody pays for it who doesn't turn the mode on.</p>

<h2>The part that made this easy</h2>

<p>Every InferHub tool worker before this one moved at least one file — audio in, audio out, an image. This is the first one that doesn't: the request is <code>{"query": "...", "documents": ["...", ...]}</code> and the answer is <code>{"scores": [...]}</code>, both plain JSON with nothing to attach. That turned out to matter more than expected — the generic <code>POST /api/tools/{capability}</code> route InferHub has had since v3.9 already spoke exactly this shape, so this release needed <strong>no new HTTP endpoint at all</strong>. Every earlier tool needed one, because a file needs a different wire shape than JSON does. Reranking is the first one that was already JSON-shaped by nature.</p>

<p>The scoring itself didn't need new code either. The existing LLM reranker's stable sort-by-score function had already been written generically — nothing about it assumed the scores came from a chat completion — so the same function reorders a cross-encoder's output unchanged.</p>

<h2>What's still on the fleet's own terms</h2>

<p>A chat model named in <code>RerankModel</code> under this mode simply never routes — there's no fallback to "use whatever chat model the request already named," because that's meaningless for a model that was never a chat model. The reranker treats it exactly like any other "no node holds this" failure: original order kept, logged, nothing thrown. Reranking has always been an improvement InferHub is willing to skip, never a dependency it's willing to break retrieval over.</p>

<h2>The deadlock a green test suite could never have seen</h2>

<p>The .NET side of this — route, dispatch, parse the reply, fall back to the original order on any failure — is unit-tested against a stub tool dispatcher, eight cases, all green. None of that touches the actual Python worker. So it got run for real: a venv with <code>sentence-transformers</code>, the worker driven directly through its own protocol — <code>hello</code>, <code>ready</code>, a real <code>request</code> frame.</p>

<p>It deadlocked. Every time, on the first request.</p>

<p><code>faulthandler.dump_traceback</code> against every thread pinned it exactly: the model library was imported lazily on first use, inside the function that handles a request — which runs on its own thread, so the worker's main loop stays free to notice a cancel while a job is in flight. That import was pulling in <code>numpy</code>'s compiled extension for the first time, and it hung hard inside CPython's own import machinery. The identical import from an ordinary background thread with nothing else going on finishes in a few seconds. A main thread parked in a blocking read <em>plus</em> a second thread loading a large native extension for the first time, at once, is what triggers it — a loader-lock hazard, not a bug in the scoring code, and not one InferHub's other tool workers have ever hit, because they've only ever run on Linux.</p>

<p>The fix is also just better design: import the library once, at boot, on the main thread — before the worker ever spawns a thread to handle anything — instead of paying that cost lazily on whoever's first request happens to land. It sidesteps the whole class of hazard rather than working around one instance of it, and it means the worker's "I'm ready" actually means ready.</p>

<p>After the fix: a leave-policy passage scored <code>7.46</code> against "how much annual leave do employees get," an unrelated one about the office kitchen scored <code>-11.23</code> — correctly ranked. A request naming a chat model that isn't a reranker got refused by name, exactly the failure the .NET-side fallback is built to catch. A second call for an already-loaded model came back in 26 milliseconds.</p>

<h2>What's still open</h2>

<p>What's verified is the worker's own protocol, directly. What's still not run is the whole stack wired together — a real coordinator, a real node, an HTTP request through <code>/api/tools/rerank</code>, on a real collection. That's the next thing to check before leaning on this in production, and it's said here rather than left implied.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.45.0">v3.45.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_cross_encoder">A dedicated cross-encoder reranker</a>.</p>
