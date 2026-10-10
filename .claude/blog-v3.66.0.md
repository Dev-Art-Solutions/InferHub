<!-- published 2026-10-10 as ID 6aca0c5c8b57c193e899c4bd -->
slug: inferhub-3-66-a-windows-node-that-keeps-itself-current
title: InferHub 3.66: a Windows node from one setup, and a node that keeps itself current
excerpt: A Windows node is now one setup that asks how to run it and installs the service. Its settings live where no update can overwrite them, and the node keeps itself current — by itself, when an admin presses Update in the console, or by hand on the box.

<p>Until now, putting an InferHub node on a Windows GPU box took six steps. You built it yourself, copied the files into <code>Program Files</code>, edited <code>appsettings.json</code> there, put the enrollment secret in a machine-wide environment variable, and ran an install script from an elevated prompt. Then the next update overwrote the <code>appsettings.json</code> you had edited. And nothing told anyone that a node was three releases behind.</p>

<p>3.66 replaces all of that with one setup, and gives the node a way to stay current.</p>

<h2>One setup that asks how to run the node</h2>

<p>Every release from 3.66 has <code>InferHub-Node-Setup-3.66.0-win-x64.exe</code> attached. It is self-contained, so the box needs no .NET runtime. It installs the node as a Windows service called <strong>InferHubNode</strong>, which starts with Windows and restarts if it fails. Along the way it asks:</p>

<ul>
<li><strong>Mode</strong>: join a coordinator (its URL and enrollment secret), or run solo with the node's own local API (its address and an API key).</li>
<li><strong>This node</strong>: a name, labels such as <code>gpu=3090, room=lab</code>, a cap on concurrent jobs, and a VRAM budget.</li>
<li><strong>Ollama</strong>: where it answers.</li>
<li><strong>Service account</strong>: LocalSystem, or a least-privilege virtual account.</li>
<li><strong>Updates</strong>: how the node should stay current. More on that below.</li>
</ul>

<p>The setup checks what would otherwise fail later. A solo node listening beyond the machine without an API key would refuse to start, so the wizard stops you there instead.</p>

<h2>Settings an update can't overwrite</h2>

<p>The answers don't go into <code>Program Files</code>. They go into one file under <code>C:\ProgramData\InferHub\Node</code>, which only SYSTEM and Administrators can read, since it holds the secret. The node reads that file after its shipped defaults and before environment variables.</p>

<p>That is what makes updates safe. An update replaces every file in <code>Program Files</code> and your settings stay where they are. Running the setup again shows your previous answers, and leaving the secret empty keeps the current one.</p>

<h2>A node that keeps itself current</h2>

<p>The setup asks one question about updates, with four answers:</p>

<ul>
<li><strong>Automatically.</strong> The node checks for a new release every six hours. When one appears, it waits for its running jobs to finish (up to 30 minutes, then it goes anyway) and installs the release itself.</li>
<li><strong>When an admin says.</strong> The node reports the new release to the coordinator, and an admin installs it from the console.</li>
<li><strong>Report only.</strong> The console shows it; you update the node on the box.</li>
<li><strong>Never.</strong> Nothing is checked.</li>
</ul>

<p>Installing works the same way in every case. The node downloads the new setup, checks it against the SHA-256 published beside it, and runs it silently. The setup stops the service, replaces the files and starts the service again. It starts the service again if the update fails, too, so a bad update never leaves the node down. After the restart the node says what happened: <code>updated 3.65.99 → 3.66.0</code>, or the failure and where the setup's log is.</p>

<h2>When automatic is off</h2>

<p>The coordinator's console has a new <strong>Versions &amp; updates</strong> panel. It shows every node's version, the newest release it could move to, how that node is set to update, and what its last update came to. <strong>Check</strong> asks a node to look now; <strong>Update</strong> installs the release. The same two actions are on the admin API, and every node's state is on the status endpoint:</p>

<pre><code>{
  "current": "3.65.99",
  "available": "3.66.0",
  "state": "available",
  "auto": false,
  "allowFromHub": true,
  "canApply": true
}</code></pre>

<p>On the box itself there is a Start-menu entry, <em>Check for InferHub Node updates</em>, that does the same whatever the node was set to.</p>

<p>The node decides, not the hub. If its operator chose "report only" or "never", the console refuses the Update button with that reason. If the command arrives anyway, the node refuses it as well.</p>

<h2>Who can update, and what the checksum is for</h2>

<p>Only a node installed by the setup and running as LocalSystem can install an update. A node under the virtual account can't run a setup. Neither can one copied by hand or running in Docker, whose updater is pulling a new image. These nodes still report new releases, and the console shows why they can't install them.</p>

<p>About the checksum: it is published in the same release as the setup. So it catches a broken or cut-off download, but not a hostile release. A node that updates itself runs what is published. If that is more trust than you want to give, choose "report only" and update by hand. The setup is not code-signed yet, so Windows SmartScreen will warn you the first time you run it.</p>

<p>All three update settings are off in the node's shipped configuration. A node in Docker or on a developer machine contacts no one unless you turn them on.</p>

<h2>Measured on a real box</h2>

<p>We built two setups from this release: an "old" 3.65.99 and the real 3.66.0. We installed the old one as a service on a Windows 11 machine, joined to a real coordinator, and served the new one from a local copy of GitHub's release list.</p>

<ul>
<li>The silent install created the service under LocalSystem, set to delayed automatic start. The node joined the coordinator under the name and job cap it was given. The settings file held exactly the answers, and an ordinary user could not open it.</li>
<li>Pressing Update in the coordinator took the node from 3.65.99 to 3.66.0 in about 10 seconds. Its name, labels and secret were kept.</li>
<li>Reinstalled as 3.65.99 with automatic updates, the node updated itself a minute after starting, with no one touching it.</li>
<li>Set to "report only", the coordinator refused Update with the node's own reason, and the Start-menu path updated it on the box.</li>
<li>A release whose checksum did not match was deleted without being run, and the service kept serving.</li>
<li>Installed under the virtual account, the node joined and explained that it cannot install updates itself.</li>
<li>After the release was published, a 3.65.99 node asked GitHub itself, found 3.66.0, and installed the published setup in 14 seconds. It came back reporting the exact commit the release was tagged from.</li>
</ul>

<p>That run also found three problems, fixed before release. Checking for updates from an ordinary prompt couldn't read the protected settings, so it now asks for elevation. A console left open after starting the setup would have held the file the setup needed to replace. And the build output carried a developer settings file and a test data folder into the setup.</p>

<h2>What we didn't check</h2>

<p>We drove every wizard page through the setup's silent-install parameters, which fill the same pages, but nobody clicked through the wizard by hand. Nobody has opened the console's new panel in a browser either. Its fields are covered by a contract test and its buttons call the endpoints we exercised. There is no ARM64 setup yet, and the coordinator still updates the way it always has: a new image.</p>

<p>The full setup and update guide is in the <a href="https://inferhub.devart.solutions/#idocs_windows_setup">documentation</a>, and the release is on <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.66.0">GitHub</a>.</p>
