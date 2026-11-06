slug: inferhub-3-91-one-identity-across-the-cluster
id: 6aed479fa96c219dd6b920c3 (EN visible, BG hidden)
title: InferHub 3.91: the coordinator that takes over now lets in every machine the old one certified
excerpt: In a pair of coordinators, node certificates and organisations now live in the shared database, so a failover keeps every certified machine connected. Leadership can also return on its own to the coordinator that should lead.

<p>Since InferHub 3.0 two coordinators can share one database: one leads, the other waits, and if the leader fails the other takes over within seconds. Since 3.87 every machine in the network can carry its own certificate from the coordinator, and since 3.90 that certificate also says which organisation the machine belongs to.</p>

<p>Those two features did not fit together. The certificate authority, the list of certificates it had issued, the unused enrollment tokens and the revocations were all files on the coordinator that issued them. If a site required certificates, a failover handed leadership to a coordinator that knew none of its machines, and every one of them was refused. The failover worked, and the site was down anyway.</p>

<p>InferHub 3.91 fixes that, and adds failback.</p>

<pre><code class="json">Cluster__Enabled=true                   # the same on every coordinator
Cluster__PreferredInstance=hub-a        # optional: the coordinator that leads whenever it is healthy
Cluster__FailbackAfterSeconds=60        # how long it must be back before it gets the lead again</code></pre>

<h2>One record, in the database both coordinators already share</h2>

<p>In a pair, the certificate authority and everything it recorded now live in the same database the pair already uses for leadership. A coordinator that takes over verifies every certificate the old one issued, knows every machine's organisation, and sees every revocation as soon as it is made.</p>

<p>Only the leading coordinator writes the record. The waiting one shows it but refuses changes: a new token, a revocation or an enrollment asked of it gets "unavailable, ask the active coordinator". Every write also carries a version number, so if two coordinators ever write at the same moment, the one that lost reads again and redoes its change, and nothing is overwritten. Two coordinators starting at the same moment on an empty database end up with one certificate authority, not two.</p>

<p>Upgrading a pair that already uses certificates keeps them. The first coordinator to start on 3.91 moves its own authority and record into the database, so the machines it certified stay certified. We first designed the record to start empty, and that design would have quietly made a new authority on upgrade and locked out every machine. The test for the move fails without it.</p>

<h2>Failback</h2>

<p>When a coordinator fails, the other takes over. Before 3.91 it then kept leading, even after the first one came back, until something else went wrong. Many sites have one coordinator that should lead: the bigger machine, or the one in the right room. Now you can name it.</p>

<p>Every coordinator now records that it is alive, by the database's clock. While the preferred coordinator is alive, the others do not take the lead. After a failover, the coordinator that took over gives the lead back only once the preferred one has been alive for a set time without a break. If it drops out in between, the count starts again, so an unreliable link moves leadership once, not back and forth. Machines follow, as they do on any failover. Leave the setting empty and nothing changes.</p>

<h2>Downloads follow too</h2>

<p>A machine sealed to its network (3.82) downloads models only through its coordinator. It used the first coordinator in its list, so after a failover its downloads went to the one that had just failed. Now they go through the coordinator it is connected to.</p>

<h2>Checked live</h2>

<p>Two coordinators on one database, certificates required, hub-a preferred, and a sealed machine that lists both:</p>

<ul>
<li>hub-b waited and said why: "the preferred coordinator 'hub-a' is alive". A token asked of hub-b was refused; hub-a issued it.</li>
<li>The machine enrolled through hub-a, and hub-b listed its certificate at once.</li>
<li>We stopped hub-a. hub-b took over and let the machine in with the certificate hub-a had issued. A model download then went through hub-b, although hub-a is first in the machine's list.</li>
<li>We started hub-a again. Twenty seconds later, as configured, hub-b gave the lead back, and four seconds after that the machine was on hub-a.</li>
</ul>

<p>The full test run against a real database also found an older fault. Several coordinators starting at once could deadlock while preparing the usage ledger, and the retry added in 3.0.1 did not cover a deadlock. It does now.</p>

<p>Still one coordinator serves at a time; both serving at once is a later step. The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.91.0">v3.91.0 release</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_cluster_identity">inferhub.devart.solutions</a>.</p>
