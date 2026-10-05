slug: inferhub-3-59-a-distribution-not-a-sentence
title_en: InferHub 3.59: a closed question answered with a distribution, not a sentence
id: 6ac2ef6dfc3715b8b9836ece (EN-visible, BG-hidden; rendered, checked)
excerpt_en: colibri has a mode where the model does not write an answer at all. It reads how likely each answer you allow is, and returns all of them with a measure of how unsure it is. 3.59 puts that on the fleet as POST /v1/brio.

<p>Ask a language model "should this pull request be merged?" and it answers in a sentence. The sentence always sounds sure. If the model is close to a coin toss between "merge" and "request changes", the reply looks exactly as confident as one where it is not in doubt at all. The text tells you what it said, not how much it meant it.</p>

<p><a href="https://github.com/JustVugg/colibri">colibri</a>, the engine InferHub 3.58 learned to drive, has a second mode called <strong>Brio</strong> that answers differently. You give it a document, a question and the answers you allow. The model generates nothing. The engine reads how likely the model finds each allowed answer and normalises over those answers only. You get back every option's probability and an entropy, a single number for how spread out they are. "The model does not know" becomes something you can read.</p>

<p>3.58 left Brio out on purpose. It is not chat, so it needed its own API rather than an extra field on a chat request. 3.59 adds that API.</p>

<h2>What it looks like</h2>

<p>The route is colibri's own, <code>POST /v1/brio</code>, on the hub and on a standalone node. The body is passed to the engine unchanged:</p>

<pre><code>{"model": "olmoe",
 "state": "The pull request removes the retry loop around the file upload and adds no test.",
 "question": "What should the reviewer do?",
 "options": ["merge", "request changes", "close"]}</code></pre>

<p>This is a real answer from OLMoE, a 7B Mixture-of-Experts model, on a CPU, through a hub and a colibri node:</p>

<pre><code>{"object": "brio.choice", "answer": "request changes", "entropy": 0.394847,
 "choices": [{"option": "request changes", "p": 0.8817},
             {"option": "merge", "p": 0.0851},
             {"option": "close", "p": 0.0332}],
 "usage": {"prompt_tokens": 32, "completion_tokens": 0, "read_tokens": 4, "total_tokens": 36}}</code></pre>

<p>There are three forms, and a request uses exactly one of them. <code>options</code> asks one question. <code>questions</code> asks several about the same document, and the engine reads the document once. <code>schema</code> takes a list of fields and their allowed values, and fills in a JSON object one field at a time. That JSON cannot come out malformed, because the model never writes it. We gave it a support message, "my order arrived broken, second time, I want my money back today", and got <code>{"sentiment": "negative", "intent": "refund", "urgency": "high"}</code>, each field with a probability above 0.99.</p>

<h2>How it travels through the fleet</h2>

<p><strong>A node says it can do it.</strong> A colibri node now declares two capabilities, <code>chat</code> and a new one, <code>score</code>. No other backend declares <code>score</code>. If no node in the fleet can take a Brio request, the hub answers 503 and names <code>score</code>, so you know what is missing. A model nobody has is still a 404.</p>

<p><strong>It does not pretend to be chat.</strong> Every inference job inside InferHub has the shape of an Ollama request. Brio has no such shape, so it travels as the same kind of job that already carries speech and pictures. The dispatcher, failover and per-capability deadlines handle it without learning anything new.</p>

<p><strong>It is billed for what the engine read.</strong> Brio generates no tokens. So the usage record counts everything the engine read, the document plus each option, as prompt tokens, against the same quota as chat, because it is the same engine on the same machine. A refused or failed request costs nothing. One case we guard against on purpose: a "successful" reply with no usage block in it is treated as an error, not as a free answer.</p>

<p><strong>The engine's own errors keep their meaning.</strong> If you give it one option, or the same option twice, its refusal comes back as a 400 in its own words. If its queue is full, the client gets a 503 with a Retry-After. The node does not quietly retry, because a second queue in front of the engine's own would be invisible to the hub.</p>

<p><strong>Nothing you send is logged.</strong> A document someone wants judged is the most private thing in the request. Neither the hub nor the node logs the document, the question or the options. The log line has the model, the form, how many options there were, and the token count. We checked this by searching every log from the test run for phrases from the documents, and found none.</p>

<h2>What we checked, and what we did not</h2>

<p>We ran colibri with OLMoE in a container, with a hub, a node and a standalone node in front of it. Then we did it again with the published 3.59 image. All three forms worked through the hub and on the standalone node, and both gave the same probabilities to four decimal places. Three answers were billed 177 prompt tokens and zero completion tokens. The first question to a cold engine took 26 seconds; once warm, answers took between one and twelve seconds on a 32-core CPU.</p>

<p>The real engine found no bug in this release. The release's own CI run did find one in older code. When a streamed job ran out its deadline, the hub told the waiting caller before it told the node to stop. Only a test could notice that order, but it was wrong, and it is fixed.</p>

<p>Some things this release does not show. colibri can keep several separate contexts, and it pins a Brio request to one of them by the document it is about. OLMoE supports only one context, so we did not see that work. colibri reports that asking several questions in one request is 5.7 times cheaper than asking them separately; we did not measure that. And whether OLMoE's judgements are <em>good</em> is a different question. This release is about getting the answer and its uncertainty through the fleet unchanged.</p>

<p>The release notes are on <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.59.0">GitHub</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_brio">inferhub.devart.solutions</a>.</p>
