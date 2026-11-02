slug: inferhub-3-87-a-certificate-per-node
id: 6ae878500614fd394476afa7 (EN visible, BG hidden)
title: InferHub 3.87: a certificate per node, and one node revoked on its own
excerpt: Nodes used to share one enrollment secret. Now the coordinator issues each node its own certificate for a key that never leaves the node, checks a fresh signature on every connection, and can revoke one node, which drops it at once.

<p>Every InferHub node has proved itself to its coordinator with the same enrollment secret. That was simple, and it had two problems. Anyone who had the secret could join as a node. And taking one machine out, because it was sold, stolen or simply no longer trusted, meant changing the secret on the coordinator and on every other machine.</p>

<p>InferHub 3.87 lets the coordinator issue each node its own certificate.</p>

<pre><code class="json">Identity__Enabled=true          # on the coordinator; off by default
Identity__Require=true          # optional: no certificate, no connection</code></pre>

<h2>Enrolling a node</h2>

<p>An admin asks the coordinator for a one-time token, with a label such as <code>gpu-3</code>. The token is shown once; the coordinator keeps only its hash. The node is started with <code>Coordinator__EnrollmentToken</code> set to it.</p>

<p>The node makes its own key, sends only the public half with the token, and gets back a certificate for its node id, valid for 90 days by default. The token is spent. The private key never leaves the node. The node renews its certificate by itself once two thirds of its life have passed, and if it was offline past the end, a new token lets it enroll again.</p>

<p>The coordinator is the certificate authority. It creates its own key on first use, and prints its fingerprint; the node prints the same fingerprint when it enrolls, so an operator can check the node trusts the right coordinator.</p>

<h2>Every connection is signed</h2>

<p>From then on the node's connection to the coordinator, and every download a sealed node makes through the coordinator's relay, carries the certificate and a fresh signature over the node id, the current time and a random nonce. The coordinator accepts it only if it issued that certificate and has not revoked it, the time is within five minutes of its own clock, and it has never seen that nonce. A copied set of headers fails the second time it is used. A node also cannot use its certificate to register as a different node.</p>

<h2>Revoking one node</h2>

<p>One admin call revokes a node's certificate. The node is disconnected at once and every retry is refused with the reason. A certificate that fails is never rescued by a correct shared secret sent beside it, and with <code>Identity:Require</code> on, the shared secret alone is refused too. Without <code>Require</code>, nodes that have not enrolled yet keep connecting with the secret, so a fleet can move over one node at a time.</p>

<h2>Why not mutual TLS</h2>

<p>The obvious way to do this is TLS client certificates. We did not, on purpose: that needs TLS all the way to the coordinator, and many deployments put a proxy, a tunnel or a load balancer in front of it that ends TLS first. A signed proof in the request headers works through all of them, on every platform a node runs on. The certificates are ordinary X.509, so moving to TLS client authentication later would not need anyone to enroll again.</p>

<h2>Checked live</h2>

<ul>
<li>A coordinator with <code>Identity:Require</code> refused a node that had only the shared secret, on every retry, naming the setting.</li>
<li>A node enrolled with a one-time token, registered, and answered a chat through the coordinator.</li>
<li>The same node, sealed to its network, pulled a model through the coordinator's relay with every request signed by its certificate; the audit trail named the node by id.</li>
<li>Revoked with a reason, it was gone from the fleet in 161 milliseconds, and each retry was refused naming the revocation and the reason.</li>
</ul>

<h2>What it does not do yet</h2>

<p>Each coordinator in a high-availability pair has its own certificate authority, so a node enrolled with one is refused by the other. The Windows setup does not ask for the token yet; set it in the service's environment or through the node's settings. The node's key is a file protected by file permissions, not a TPM. And a renewal has been tested, but not by waiting sixty days.</p>

<p>Node certificates are off by default, and with them off nothing changes. The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.87.0">v3.87.0 release</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_node_certificates">inferhub.devart.solutions</a>.</p>
