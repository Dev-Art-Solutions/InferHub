slug: inferhub-3-71-jobs-side-by-side-and-a-cancel-that-arrives
title: InferHub 3.71: jobs side by side, and a cancel that arrives
excerpt: A node now runs the jobs the coordinator sends it at the same time instead of one after another. Getting there found that a cancelled chat, picture or video had never actually stopped on the node, and that an Ollama node's streams arrived in one piece at the end. All three are fixed.

<p>3.71 started as one small change and found three older problems. All four are in this release.</p>

<h2>A node runs its jobs side by side</h2>

<p>A node connects to the coordinator over SignalR. The .NET SignalR client hands the coordinator's calls to the node one at a time, and waits for each one to finish before it hands over the next.</p>

<p>The node's job handlers waited for the whole job. So a node ran one job at a time, whatever <code>Node:MaxConcurrency</code> it said it could take. A second chat sent to a node that was already answering one waited in line, even when the GPU had room for both.</p>

<p>Now each job runs off that line. A node takes as many jobs at once as the coordinator sends it. The coordinator still holds each node to its <code>MaxConcurrency</code>, as before. If you want one at a time back, set <code>Node:MaxConcurrency</code> to 1.</p>

<h2>A cancel now reaches the node</h2>

<p>Making jobs run side by side broke three tests about cancelling pictures and videos. We looked at why, and found something older.</p>

<p>When a caller went away from a job that was not streaming, the coordinator marked the job cancelled and never told the node. That covers a chat whose client hung up, and a picture or a video you deleted halfway through. The node kept going to the last token or the last step.</p>

<p>The cause was the order in which .NET runs cancellation callbacks. The coordinator's "tell the node" callback was registered on the caller's cancellation token. When the caller cancelled, the coordinator's wait for the result ran first and finished the method, and that removed the "tell the node" callback before it could run. The tests passed anyway, because they only checked what the coordinator said about the job.</p>

<p>Now the coordinator tells the node itself, every time. A stream that the caller leaves before its first chunk had the same problem, and it is fixed the same way.</p>

<p>Pictures and videos got one more change. The coordinator used to treat a node as free the moment you cancelled, and sent the next picture to a worker that was still stopping. Now a cancel asks the node to stop and waits for its answer. A picture cancelled at its last step may still come back finished. The API has always said that could happen. Now it can.</p>

<h2>An Ollama node's stream now streams</h2>

<p>We checked all of this against a real coordinator, a real node and the Ollama on our test box. A cancelled chat now stopped on the node. A cancelled stream did not.</p>

<p>The reason had nothing to do with cancelling. Through the coordinator, the first line of a streamed answer arrived at 6.3 seconds of a 6.3-second answer. Straight from Ollama it arrived at 0.45 seconds. A node with no coordinator did the same, and so did the node from before this release. So the stream had never really streamed.</p>

<p>The client library the node uses to talk to Ollama read the whole streamed answer before it returned the first line. Because nothing came back early, every stream a caller left was still "before the first chunk", which is the case the coordinator never cancelled.</p>

<p>The node now reads Ollama's answer line by line as Ollama writes it. It sends the same request and gets back the same chunks. Only the timing changed: the first line now arrives at 0.016 seconds on a warm model. A model download's progress should now move while it runs, too, because it goes through the same reader. We didn't run a download for this release, so we haven't seen that yet.</p>

<h2>What changes without a config change</h2>

<ul>
<li>Two chats sent to one node both run now. A local Ollama decides for itself how many it runs at once, with <code>OLLAMA_NUM_PARALLEL</code>.</li>
<li>A cancelled chat, picture or video stops on the node. A worker that ignores the request is stopped after <code>Tools:CancelGraceSeconds</code>.</li>
<li>Streams from an Ollama node arrive as they are written.</li>
<li>If you send a burst of jobs to a tool with only one worker, the extra jobs now wait in that tool's own queue for up to 30 seconds (<code>Tools:QueueMaxWaitSeconds</code>). After that they get <code>503</code> with <code>Retry-After</code>, the answer a node without a coordinator has always given. Before, they waited out of sight until the coordinator gave up on them.</li>
</ul>

<h2>What we did not check</h2>

<ul>
<li>We tested two jobs at once on one small model. We haven't tested how Ollama copes with many jobs at once from a busy fleet.</li>
<li>The fix for streaming is in InferHub. The client library itself is unchanged, and still waits for the whole answer for anyone else who uses it.</li>
<li>A cancel that reaches a node before its job does is still lost. The coordinator always sends the job first on the same connection, so the gap is very small.</li>
</ul>

<p>The release notes have the details, and the docs have a new section: <a href="https://inferhub.devart.solutions/#idocs_jobs_side_by_side">Jobs side by side</a>.</p>
