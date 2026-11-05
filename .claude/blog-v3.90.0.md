slug: inferhub-3-90-organisations-and-grants
id: 6aec5376765d70ed35c6f225 (EN visible, BG hidden)
title: InferHub 3.90: several organisations on one coordinator, each on its own terms
excerpt: Every node and client now belongs to an organisation, grants say which organisation's clients may use whose machines and models, and a certified node's organisation comes from its certificate, not from what it says about itself.

<p>Since InferHub 3.76 a machine joins a network on its owner's terms: always, only while idle, during set hours, within a daily allowance. Those are terms between one machine and the network. What was missing was the step above: several organisations putting their machines on one coordinator, each deciding which other organisations may use them, and for what.</p>

<p>InferHub 3.90 adds organisations.</p>

<pre><code class="json">Organisations__Enabled=true                        # off by default
Auth__Clients__0__Organisation=lab-a
Organisations__Grants__0__Owner=lab-b              # lab-b's machines...
Organisations__Grants__0__To=lab-a                 # ...may serve lab-a's users
Organisations__Grants__0__Models__0=qwen2.5*       # ...for these models only</code></pre>

<h2>The rule</h2>

<p>Every node and every client belongs to one organisation. A client reaches the machines of its own organisation, and the machines of an organisation that granted it the model and the kind of work it asks for. A grant goes one way: lab-b sharing with lab-a says nothing about lab-a sharing with lab-b, and nothing passes through a third organisation.</p>

<p>For the client, a model only another organisation holds simply does not exist: it is missing from the model list and a request for it is a "not found", the same answer as for a model nobody has. The rule is applied in the one place where every request finds its machines, so it holds for chat, embeddings, tools, speech, images and video alike. Administrators and the fleet status page still see every machine.</p>

<h2>Where trust comes from</h2>

<p>A rule about organisations is only as good as the answer to "which organisation is this machine in". If the machine says so itself, any machine can claim to be anyone's. So the answer comes from its certificate. Since 3.87 each node gets its certificate from the coordinator with a one-time token; now the token can carry an organisation, and the certificate records it. A node that proves that certificate belongs to that organisation, whatever its own configuration says.</p>

<p>We tested exactly that: a node configured to call itself <code>lab-a</code>, enrolled with a token for <code>lab-b</code>, is recorded as <code>lab-b</code>. A node without a certificate can still declare its own organisation, and the status page marks it as declared. The site checklist from 3.89 then asks for certificates to be required, because a declared organisation is a claim nobody checked.</p>

<h2>Two things building it found</h2>

<p>The audit trail from 3.85 records each request's organisation now, but in the first live run every line said nothing. The audit step runs first in the coordinator's pipeline, and the organisation was worked out by a step after it; a value set further down never travels back up. It now takes the organisation from the client the key belongs to, and a test runs the real order of steps.</p>

<p>The second came from checking that every route really goes through the rule. Image and video jobs wait in a queue, and the queue hands them to machines later, from whatever happens to run it next. That later moment is no longer the request, so the organisation would have been missing. Each queued job now carries its submitter's organisation, and a test fails without the fix.</p>

<h2>Checked live</h2>

<p>A coordinator with certificates required, three organisations and one grant from lab-b to lab-a for qwen models; two machines, one per lab, on a real Ollama:</p>

<ul>
<li>lab-b's user always ran on lab-b's machine. lab-a's user ran on lab-a's machine, and, with that machine taken out, on lab-b's for a qwen model only; another model was "not found".</li>
<li>A user from lab-c, with no grant, saw no models at all.</li>
<li>The audit lines named each request's organisation, and both machines came back with their certificates and organisations after the coordinator restarted.</li>
</ul>

<p>Not done yet: separate administrators per organisation, and billing between organisations. Organisations are off by default, and with them off nothing changes. The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.90.0">v3.90.0 release</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_organisations">inferhub.devart.solutions</a>.</p>
