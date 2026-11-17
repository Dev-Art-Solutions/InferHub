id: 6afc3678faff475a7f9bfe33 (EN visible, BG hidden) — posted 2026-11-17
slug: inferhub-3-104-a-sealed-node-updates-through-its-coordinator
title: InferHub 3.104: a sealed node updates through its coordinator
excerpt: A sealed InferHub node talks to nothing outside its network except its coordinator. Until now that also meant it could not update itself. In 3.104 the coordinator keeps node releases the way it keeps models, and a sealed node updates only from those.

<p>A sealed node in InferHub is a machine that talks to nothing outside its own network except its coordinator. We added it for sites that keep their data in the building. Since 3.82, everything such a node needs comes through the coordinator: model files from Hugging Face, Ollama models, the model catalogue. Since 3.84, the coordinator can also keep a fixed set of models on its own disk, fetched once or carried in on a drive, and give its nodes nothing else.</p>

<p>One thing still had to come from outside: new versions of InferHub itself. A Windows node can update itself, but it reads the list of releases from GitHub, and GitHub is outside the network. So a sealed node refused to start with updates switched on. The site updated each node by hand, or ran its own copy of GitHub's release list. A closed site is exactly the kind of site that needs security fixes and has the fewest people to install them.</p>

<h2>Releases in the supply</h2>

<p>In 3.104 the coordinator's supply holds node releases as well as models. An admin asks the coordinator for a release:</p>

<pre><code>POST /api/admin/supply
{ "release": "latest" }</code></pre>

<p>The coordinator reads the release list, downloads the Windows setup, and checks it against the SHA-256 published beside it. It keeps the setup only if the two match. A site with no internet at all can carry the two files in on a drive and import them instead.</p>

<p>On each sealed node, one setting points the update check at the coordinator:</p>

<pre><code>"Update": {
  "Source": "coordinator",
  "Check": true,
  "AllowFromHub": true
}</code></pre>

<p>The node now asks its coordinator which releases exist. The coordinator answers in the same shape as GitHub, so the node's update code did not change. When an admin presses Update in the console, the node downloads the setup from the coordinator, checks it again, and installs it.</p>

<h2>The admin decides which version</h2>

<p>The coordinator never passes GitHub's release list through to a node. A node sees only the releases an admin put in the supply. This matters most for nodes set to update automatically. Without this rule, every new tag on GitHub would become their next install the moment we published it. A closed site wants to make that decision itself, and now it does.</p>

<p>We should be clear about one limit. The checksum comes from the same release as the setup. It catches a broken or truncated download. It does not protect against a release that was bad from the start.</p>

<h2>A bug we found on the way</h2>

<p>Since 3.89 the coordinator can write a plan for a new node: a one-time token and the exact command to install it. For a sealed Windows node, that command did not say how to handle updates. The installer then used its default, which is to update automatically from GitHub, and the sealed node refused to start with that setting. So the plan described a node that could not boot. Our tests checked the plan and the boot rules separately, never together. The plan now asks for updates from the coordinator, and a new test runs the plan through the boot rules.</p>

<h2>How we checked it</h2>

<p>From source, with the real GitHub release list: the coordinator supplied 3.103.0, a 35.6 MB setup whose hash matched the published one. A sealed node built to report version 3.102.0 asked only its coordinator and showed 3.103.0 as available. The whole setup, downloaded through the coordinator, had the published hash.</p>

<p>Then on the published images. The 3.104.0 coordinator supplied 3.104.0 itself, the setup our release workflow had attached a few minutes earlier, and the copy it served had the same hash as the release's own checksum file. A 3.103.0 sealed node, with no changes and only its update address pointed at the coordinator, reported 3.104.0 as available. A 3.104.0 sealed node reported that it was up to date. Older sealed nodes can use this today.</p>

<p>What we have not done: installed an update from the supply on a real Windows service, and run the installer with the new setting (we compiled it, but did not run it). Docker nodes can see that an update exists, but you still update them from your own image registry.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.104.0">v3.104.0 on GitHub</a>. Documentation: <a href="https://inferhub.devart.solutions/#idocs_sealed_updates">a sealed node's updates</a>.</p>
