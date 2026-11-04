slug: inferhub-3-89-a-closed-site-checked-and-planned
id: 6aeb06b41c9f31eb7b7e7763 (EN visible, BG hidden)
title: InferHub 3.89: a closed site, checked and planned from one place
excerpt: Seven releases each added one piece of a site that keeps its models and its traffic inside. Now the coordinator checks them together, names the nodes and settings that fall short, and plans each new node down to its install command.

<p>Over the last seven releases InferHub gained the pieces of a closed site: a site whose models, documents and requests stay inside its own network. A node can be sealed so it reaches nothing outside but its coordinator. The coordinator relays its downloads, can supply models itself, issues each node its own certificate, keeps an audit trail, runs essential work first, and gives each node the firewall rules that match it.</p>

<p>Each of those is a separate setting. Nothing told an operator whether all of them were in place, which node was the weak one, or how to add a new machine without getting one of them wrong. InferHub 3.89 does both.</p>

<pre><code class="json">GET  /api/admin/site                              the checklist and every node's state
POST /api/admin/site/nodes  {"name": "gpu-03"}    a plan for one node, with a one-time token</code></pre>

<p>The same is available as a page, <code>/site.html</code>, on the coordinator.</p>

<h2>The checklist</h2>

<p>Eight lines are required. There must be an admin key. Node certificates must be on and required. The relay must be on, since sealed nodes download only through it. No cloud provider may be configured. And every connected node must be sealed, hold an active certificate, and report that its own machine cannot reach outside.</p>

<p>Four more lines are recommended: models only from what an admin supplied, the audit trail, priority classes, and keys required even on the coordinator's own machine. The site is ready when every required line holds. A recommended line never blocks it.</p>

<p>Each failing line names the nodes that fail it and the setting that fixes it, so the answer to "what is still missing" is a list of things to do rather than a red light. The checklist only reads. It changes nothing, because several of these settings need a restart and two of them are decisions for each machine's owner.</p>

<h2>A plan per node</h2>

<p>Adding a node is one call with its name. The coordinator creates a one-time enrollment token labelled with that name and returns the node's settings three ways: the silent command line for the Windows setup, a <code>docker run</code> command, and environment lines for a Linux service. All three are built from the same settings, so they cannot disagree. The plan ends with what to do next: print and apply the firewall rules on the node, then look at the checklist again.</p>

<p>The Windows setup now takes such a plan: it has two new silent parameters, <code>/EnrollmentToken=</code> and <code>/Sealed=yes</code>. And the installed Windows service can now print its own firewall rules, which in 3.88 only the console version could.</p>

<h2>Checked live</h2>

<p>A coordinator was set up as a closed site. With no node connected, the checklist showed five of eight required lines passing. We asked it for a plan for a node called <code>gpu-site-1</code> and started a node with exactly the environment lines from the plan. It enrolled with the token, registered as sealed and certified, and answered a chat. The checklist then showed seven of eight. The one failing line was the node's egress, naming <code>gpu-site-1</code>, because the test machine is connected to the internet. That is the line the firewall rules from 3.88 exist for.</p>

<p>Not checked: a silent install of the Windows setup with the new parameters (the setup was built, and the lines it writes were fed to the node's own <code>configure</code> command, which stored them correctly), and the page clicked in a browser.</p>

<p>The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.89.0">v3.89.0 release</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_closed_site">inferhub.devart.solutions</a>.</p>
