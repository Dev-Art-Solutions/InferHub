id: 6afaf3596bbffea5b2464f6b (EN visible, BG hidden) — posted 2026-11-16
slug: inferhub-3-103-a-long-answer-is-a-job-you-can-follow
title: InferHub 3.103: a long answer is a job you can follow
excerpt: Some answers take minutes: a big model, a model split across several machines, a busy fleet. InferHub 3.103 lets you send such a request as a job. You get an id at once, and you can watch the job while the coordinator and the machine running it both report how far it is.

<p>InferHub answers a chat the way most model servers do. The program that asks opens a connection and waits on it until the answer is complete. For an answer that takes a few seconds, that works well.</p>

<p>Some answers take minutes. In the last release we ran a 70B model across three machines. It needs minutes to load and then writes a few words a second. A connection held open that long is fragile: a proxy in the middle closes it, a laptop goes to sleep, and the work is lost. The program that asked also knows nothing while it waits. It cannot tell a model that is still loading from a machine that has stopped.</p>

<p>InferHub 3.103 adds a second way to ask: send the request as a job.</p>

<h2>How a job works</h2>

<p>You send the same chat request to a new address, <code>/api/jobs</code>. The coordinator answers at once with an id. It then runs the request exactly as it would have run the chat, and it keeps a status for it. You ask for the status with the id whenever you want, or you keep one connection open and receive each change.</p>

<p>A status looks like this:</p>

<pre><code>{
  "id": "8755d357-7f7a-493b-984b-09eeb1c3797e",
  "model": "qwen2.5:0.5b",
  "status": "running",
  "stage": "preparing",
  "node": { "name": "live150-node", "reportedAt": "2026-11-16T11:40:18Z", "pieces": 0 },
  "attempts": 1,
  "pieces": 0
}</code></pre>

<p>The status is one of five words: queued, running, succeeded, failed or cancelled. The stage says more. A job that is waiting says what it is waiting for. A running job is first sent, then preparing, then generating. When it has finished, the status has the exact number of tokens and the speed.</p>

<p>The answer is at a second address. If the job has finished, you get the answer the chat would have given. If it is still running, you get the text written so far. So a program that lost its connection can come back and read on.</p>

<h2>Two sides update the status</h2>

<p>The coordinator knows when it accepted the job, which machine it sent it to, and how many pieces of the answer have come back. That is not enough. While a model loads, no piece comes back, and from the coordinator the job looks the same as a job on a machine that has frozen.</p>

<p>So the machine that runs the job reports on it too. It reports when it receives the job, when its model writes the first piece, and every two seconds in between and after. The word "preparing" in a status comes from the machine itself. The status also shows when the machine last reported. If that time stops moving, the machine has gone quiet. If it keeps moving, the model is still working.</p>

<p>The owner of a machine can see the same jobs on the machine's own control address. The coordinator's console has a new Jobs panel that lists every job with its client, model, stage, machine and progress.</p>

<p>The status never holds the request or the answer. It holds names, counts and times.</p>

<h2>A model split across machines</h2>

<p>This is the case we built the feature for. When the model of a job is one model split across several machines, the status also carries the group:</p>

<pre><code>"stage": "waiting",
"detail": "group 'llama-8b-split' is starting on shard-a: starting",
"group": {
  "name": "llama-8b-split", "state": "starting",
  "members": [
    { "node": "shard-a", "layers": 11 },
    { "node": "shard-b", "layers": 11, "expectMiB": 1528, "sentMiB": 247 },
    { "node": "shard-c", "layers": 10, "expectMiB": 1389, "sentMiB": 225 }
  ]
}</code></pre>

<p>Each member has its layers, the size of its share of the model, and how much of that share it has received so far.</p>

<p>A job also behaves differently from a chat when the group is not ready. If one machine of a group leaves, the coordinator puts the group together again with a spare machine. That takes from seconds to a minute. A chat that arrives in that time gets an error that says "try again later". A job waits. It stays queued, says why, and runs when the group is ready.</p>

<p>We tested this with the images we published. We ran one coordinator and four machines as Docker containers on one PC, with an 8B model split across three of them. A job through the group answered in 10 seconds, at about 6.6 tokens a second, with the group in its status. Then we stopped one machine and sent a job in the same second. The job waited 40 seconds as queued. Its status showed the reason and the members receiving their shares again. Then it ran and gave its answer.</p>

<h2>What else is different about a job</h2>

<ul>
<li>A job waits when the fleet is full, or when the owners of the machines are not sharing at the moment. It waits up to ten minutes by default.</li>
<li>A job has its own time limit: one hour by default. A chat has five minutes.</li>
<li>A job is never sent twice. If a machine disappears after it wrote part of an answer, the job fails and says so. You decide whether to send it again.</li>
<li>You can cancel a job. The machine that runs it is told to stop.</li>
<li>A job belongs to the client that sent it. Another client cannot see it.</li>
</ul>

<h2>The four client libraries</h2>

<p>The Python, TypeScript and Go clients have the job calls in new releases: Python 1.1.1 on PyPI, TypeScript 1.1.1 on npm and Go 1.1.1. Each can send a job, follow its status, wait for it, read the answer and cancel it. We installed each one from its public registry and ran it against the split model.</p>

<p>That test found a mistake in our own first release of the day. The clients work out how much of a group has loaded. For a group that was already running, the number said 16%. The count it uses is only meaningful while a group is starting. The clients now give no number for a running group. This is why each client is at 1.1.1 and not 1.1.0.</p>

<p>The C# client has the same calls in version 1.8.1. It is in the repository and tagged, but it is not on NuGet yet: our publishing key had expired. It will appear there when the key is renewed.</p>

<h2>What this release does not do</h2>

<ul>
<li>Jobs live in the coordinator's memory. Nothing is written to disk. A finished job and its answer stay for ten minutes, and a restart of the coordinator loses every job.</li>
<li>A machine that runs alone, without a coordinator, has no jobs.</li>
<li>The OpenAI-style addresses have no jobs. Jobs use InferHub's own address.</li>
<li>A job cannot use document search. A request that asks for it is refused, not answered without it.</li>
<li>We tested on one PC with containers and an 8B model. We did not run a 70B model for this release, and we have not tested graphics cards in a group.</li>
</ul>

<p>The release is <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.103.0">v3.103.0 on GitHub</a>. The documentation is at <a href="https://inferhub.devart.solutions/#idocs_jobs">inferhub.devart.solutions</a>.</p>
