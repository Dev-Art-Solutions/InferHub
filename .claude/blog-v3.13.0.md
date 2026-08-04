# Blog draft — v3.13.0 (NOT YET POSTED)

The MCP connector to blog.devart.solutions was down for the whole release window
(`Missing sessionId parameter`, ~6 retries over 25 minutes). That error rejects **before**
the insert, so the slug is still free and nothing partial landed.

Create it with **one** `create_post` call, after a `list_posts` to confirm absence:

- **slug**: `inferhub-3-13-one-box-chat-rag-and-speech`
- **title_en**: `InferHub 3.13: one box, one container — chat, RAG and speech, configured from one place`
- **isVisible_en**: `true`, **isVisible_bg**: `false`
- **excerpt_en**: Six releases added capabilities, a tool runtime, speech, node profiles and per-node corpora — and the console still showed a table of nodes and models. So every "I turned it on and nothing happened" was a support conversation. 3.13 makes it a row, with the reason attached.

The `content_en` below is **already entity-escaped**, which is how this connector stores HTML
(see the note in CLAUDE-adjacent memory) — pass it verbatim, do not "fix" the escaping, and do
not use HTML entities like `&mdash;` in it (literal characters only, so one round of escaping
is unambiguous). No shell commands anywhere in the body: the blog sits behind a Cloudflare WAF
that blocks any request whose payload contains one.

## content_en

```
&lt;p&gt;Six releases ago InferHub could route chat to a fleet of GPU boxes. Since then it learned what a node can &lt;em&gt;do&lt;/em&gt; rather than just what it holds, how to run supervised child processes, how to transcribe and speak, how to take its configuration from a coordinator, and how to host a corpus per node.&lt;/p&gt;

&lt;p&gt;And the console still showed a table of nodes and models.&lt;/p&gt;

&lt;p&gt;That gap has a specific cost, and it is not aesthetic. Every one of those features can be switched on and quietly not happen — a manifest sitting on a box that the operator never added to the allow-list, a profile item the node clamped, a corpus that failed to start. In all three cases the box behaves exactly as it did before. There is nothing to see. So the operator writes to us, and we ask them to read a log on a machine they are not tailing.&lt;/p&gt;

&lt;p&gt;&lt;strong&gt;InferHub 3.13 makes it a row.&lt;/strong&gt; It closes the tools-and-fleet track by making six releases’ worth of capability operable by somebody who did not write it.&lt;/p&gt;

&lt;h2&gt;Needs attention, above the fold&lt;/h2&gt;

&lt;p&gt;The strip sits directly under the auth bar, before the fleet counters, and it is hidden entirely when there is nothing to say. Each row names the thing that is not happening and, crucially, &lt;em&gt;the reason&lt;/em&gt; rather than a status word:&lt;/p&gt;

&lt;ul&gt;
  &lt;li&gt;&lt;strong&gt;tool · gpu-box · &lt;code&gt;whisper&lt;/code&gt;&lt;/strong&gt; — the manifest is on the box but &lt;code&gt;Tools:Allowed&lt;/code&gt; does not name it, so it was never started&lt;/li&gt;
  &lt;li&gt;&lt;strong&gt;profile · gpu-box · &lt;code&gt;gpu-boxes@1: tool:echo&lt;/code&gt;&lt;/strong&gt; — &lt;code&gt;Tools:Allowed&lt;/code&gt; on this node does not name ‘echo’; that list is the operator’s grant and a profile cannot add to it&lt;/li&gt;
  &lt;li&gt;&lt;strong&gt;corpus · edge-box · &lt;code&gt;qdrant&lt;/code&gt;&lt;/strong&gt; — connection refused reaching the engine&lt;/li&gt;
&lt;/ul&gt;

&lt;p&gt;Those are real strings from a real three-node fleet, not mock-ups. The first one is the case worth dwelling on: dropping a manifest into a directory and having nothing happen is the single most common confusion this track produces, and the fix is one line of configuration. Without the row it is an afternoon.&lt;/p&gt;

&lt;h2&gt;Desired beside effective, everywhere&lt;/h2&gt;

&lt;p&gt;The design decision the second half of this track turns on is that &lt;strong&gt;a coordinator can only ever narrow a node, and the check that enforces it runs on the node&lt;/strong&gt;. A profile can switch a capability off but cannot re-open one the box’s own config closed. It can stop a tool but cannot introduce one. It can lower a concurrency cap but never raise it, because that number is a statement about hardware somebody else owns.&lt;/p&gt;

&lt;p&gt;That is the right design — a compromised coordinator must not become fleet-wide remote code execution — and it has a direct consequence for a user interface: &lt;strong&gt;refusals are normal&lt;/strong&gt;. A profile asking for a concurrency of 8 against a box capped at 2 is not an error and not a silent no-op. The node applies the 2 and reports the refusal naming the key that stopped it.&lt;/p&gt;

&lt;p&gt;So every panel in 3.13 shows what the hub asked for beside what the node is actually doing. “It did not take” without “and here is what stopped it” reads as a bug in the product, and it is not one.&lt;/p&gt;

&lt;h2&gt;Four more panels&lt;/h2&gt;

&lt;table&gt;
  &lt;tr&gt;&lt;th&gt;Capabilities&lt;/th&gt;&lt;td&gt;Node × capability, plus a fleet row: how many boxes serve chat, embed, transcribe, speak, and over how many models. A capability with zero nodes is the difference between a 503 with a Retry-After and a 404, and this is where you see it.&lt;/td&gt;&lt;/tr&gt;
  &lt;tr&gt;&lt;th&gt;Tools&lt;/th&gt;&lt;td&gt;Per node and manifest: allowed or not; running, suspended, stopped or not-allowed; live workers; requests and failures; and the last error in the worker’s own words.&lt;/td&gt;&lt;/tr&gt;
  &lt;tr&gt;&lt;th&gt;Node retrieval&lt;/th&gt;&lt;td&gt;Which node hosts which corpus, on which engine, with how many records — and why it is not running, when it is not.&lt;/td&gt;&lt;/tr&gt;
  &lt;tr&gt;&lt;th&gt;Node profiles&lt;/th&gt;&lt;td&gt;The profile book, an editor, apply and delete, and a table of which boxes took which revision and what each refused.&lt;/td&gt;&lt;/tr&gt;
&lt;/table&gt;

&lt;p&gt;The profile editor is a textarea over the profile’s own JSON rather than a form of checkboxes. A profile is a small document with an open-ended capability map and a retrieval block; a form would need rewriting for every field a later release adds, and the JSON is what an operator wants to paste into a ticket anyway.&lt;/p&gt;

&lt;pre&gt;&lt;code&gt;{
  "name": "gpu-boxes",
  "selector": { "labels": { "role": "gpu" } },
  "maxConcurrency": 2,
  "retrieval": {
    "enabled": true,
    "provider": "local",
    "collections": ["handbook"],
    "embeddingModel": "all-minilm"
  }
}&lt;/code&gt;&lt;/pre&gt;

&lt;p&gt;Write that and the boxes labelled &lt;code&gt;role=gpu&lt;/code&gt; bring a corpus up while they go on answering chat — no file edited on the machine, no restart.&lt;/p&gt;

&lt;h2&gt;A green pill for a tool that cannot run&lt;/h2&gt;

&lt;p&gt;One thing only running it found. A worker pool that has failed to start but has not yet exhausted its restart budget reports itself as &lt;code&gt;running&lt;/code&gt;, which is &lt;em&gt;correct&lt;/em&gt; — it has not given up and it will keep trying. It also holds no worker and will fail every request it is declared for.&lt;/p&gt;

&lt;p&gt;The first version of the panel showed that as a green pill with an error message in a column to the right. That is a small lie told confidently, which is precisely what this release exists to stop. It now reads &lt;code&gt;running · no worker&lt;/code&gt; in amber and appears on the strip. The &lt;code&gt;lastError&lt;/code&gt; field is also &lt;em&gt;cleared&lt;/em&gt; on a successful start, so it means “the most recent thing that happened to this pool was a failure” rather than “something once went wrong here” — a permanent warning is a column operators learn to ignore.&lt;/p&gt;

&lt;h2&gt;The hub did not know, and now it does&lt;/h2&gt;

&lt;p&gt;Until this release the only thing a coordinator learned about a node’s tools was the capability declaration folded into its model report. A manifest present but not allowed, a pool a profile had suspended, and a pool that had given up were &lt;strong&gt;all the same thing at the hub: nothing&lt;/strong&gt; — and each of those has a different fix.&lt;/p&gt;

&lt;p&gt;Nodes now report their tool runtime up the connection they already opened, on the same refresh loop that carries the model list and again immediately after a profile touches it. The hub records it and &lt;strong&gt;never asks for it&lt;/strong&gt;. That direction matters: a status page that dialled the fleet to build itself could not answer when the fleet is what is broken. A stale block is the honest failure mode, and the timestamp says so.&lt;/p&gt;

&lt;p&gt;A node still running 3.12 against a 3.13 hub is fine. It reports nothing, and the panel says so rather than claiming the box has no tools.&lt;/p&gt;

&lt;h2&gt;Series to alert on, and the zeros we did not emit&lt;/h2&gt;

&lt;p&gt;The Prometheus endpoint gained capability counts, per-tool request and worker gauges, audio volume, profile state and node corpus sizes. The rule they all follow is the one this project has had since 2.10: &lt;strong&gt;absence stays absence&lt;/strong&gt;. A capability nobody serves, a tool nobody loaded, a profile nobody wrote and a corpus nobody assigned each produce &lt;em&gt;no series at all&lt;/em&gt;, not a zero.&lt;/p&gt;

&lt;p&gt;A dashboard reading &lt;code&gt;transcription capacity: 0&lt;/code&gt; on a fleet that was never asked to transcribe pages somebody at three in the morning about a feature nobody turned on. A zero is a claim, and it should only be made when it is true.&lt;/p&gt;

&lt;p&gt;The two audio counters are deliberately separate — a transcription is metered in seconds and a synthesis in characters, and a single summed &lt;code&gt;units&lt;/code&gt; series would add the two into a number nobody can tell is wrong.&lt;/p&gt;

&lt;p&gt;Which is a nice principle to hold, and we broke it in 3.13.0. Scraping the published image turned up four permanent zeros per not-allowed manifest per node: worker and request counts for a pool that &lt;em&gt;does not exist&lt;/em&gt;, synthesised only to fill the record. The series that says the tool is present and not running already carried the whole of what was true. Fixed in &lt;strong&gt;3.13.1&lt;/strong&gt;, same day. It is worth saying out loud that the bug shipped inside the code that argues against it, and that a green test suite did not see it — the scrape of a real published artifact did.&lt;/p&gt;

&lt;h2&gt;Four images, and a chooser&lt;/h2&gt;

&lt;p&gt;There are four now — a coordinator, a plain node, a node with Ollama inside it, and one that also carries the speech workers — ranging from 120 MB to 6 GB. Four artifacts with no decision table is how somebody pulls six gigabytes to run a 340-megabyte workload, or pulls the small one and wonders where the audio went. The README and the docs site now have that table, plus the two rules of thumb that save the mistake in each direction: do not take the speech image for chat, and do not take the bundled one to sit next to an Ollama you already run.&lt;/p&gt;

&lt;p&gt;The README also tells the track’s story once, top to bottom, instead of in five feature sections: a coordinator on a small always-on host, one GPU box, chat, then a corpus and speech configured from the hub.&lt;/p&gt;

&lt;h2&gt;Upgrading&lt;/h2&gt;

&lt;p&gt;Nothing to do. No new configuration keys, no behaviour change on any request path, no new dependency, and the console is still plain HTML, CSS and JavaScript with no build step. A deployment that changes no config behaves exactly as it did on 3.12.&lt;/p&gt;

&lt;p&gt;The five releases this one summarises: &lt;a href="https://blog.devart.solutions/inferhub-3-8-a-node-that-says-what-it-can-do"&gt;3.8, a node that says what it can do&lt;/a&gt;; &lt;a href="https://blog.devart.solutions/inferhub-3-9-the-node-can-run-your-python"&gt;3.9, the node can run your Python&lt;/a&gt;; &lt;a href="https://blog.devart.solutions/inferhub-3-10-speech-in-and-speech-out"&gt;3.10, speech in and speech out&lt;/a&gt;; &lt;a href="https://blog.devart.solutions/inferhub-3-11-configure-the-fleet-not-the-boxes"&gt;3.11, configure the fleet not the boxes&lt;/a&gt;; and &lt;a href="https://blog.devart.solutions/inferhub-3-12-a-corpus-on-every-node"&gt;3.12, a corpus on every node&lt;/a&gt;.&lt;/p&gt;

&lt;p&gt;InferHub is MIT-licensed and self-hosted: &lt;a href="https://github.com/Dev-Art-Solutions/InferHub"&gt;github.com/Dev-Art-Solutions/InferHub&lt;/a&gt;, docs at &lt;a href="https://inferhub.devart.solutions"&gt;inferhub.devart.solutions&lt;/a&gt;.&lt;/p&gt;
```
