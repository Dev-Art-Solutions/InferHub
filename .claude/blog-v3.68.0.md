<!-- published 2026-10-13 as ID 6ace27f1ff77e728ce2bd22c, EN visible / BG hidden -->
slug: inferhub-3-68-sign-in-with-chatgpt
title: InferHub 3.68: sign in with ChatGPT, on the coordinator and on a node
excerpt: InferHub can now run requests on a ChatGPT plan after a browser sign-in, with no API key. The coordinator and each node sign in separately, as their own accounts, and never share a token. A remote coordinator is signed in from the browser on your own computer, through a one-time pairing code.

<p>Until now InferHub reached OpenAI one way: with an API key. OpenAI also lets an open-source, self-hosted app use a person's <strong>ChatGPT plan</strong> through its Responses API, after that person signs in in a browser. No key, no separate bill.</p>

<p>3.68 adds this as a new provider type. It sits beside the existing OpenAI provider and does not change it. A provider or node set up with an API key works exactly as before.</p>

<h2>Two separate sign-ins</h2>

<p>The coordinator and a node can each use a ChatGPT plan, and they are kept apart on purpose.</p>

<ul>
<li><strong>The coordinator</strong> signs in as one account and offers it as a cloud provider, next to OpenAI, OpenRouter, Anthropic and Gemini.</li>
<li><strong>A node</strong> signs in as its own account, which can be the same one or a different one, and offers its models like any other backend.</li>
</ul>

<p>Each keeps its own sign-in, its own host id and its own encrypted store. The coordinator never receives a node's tokens. All a node tells the coordinator is which models its account can use.</p>

<h2>On the coordinator</h2>

<p>It is one more provider in the configuration:</p>

<pre><code>"Providers": {
  "plan": {
    "Type": "chatgpt",
    "ModelMap": { "chatgpt": "@selected", "sol": "gpt-5.5" }
  }
}</code></pre>

<p>The map still decides which models may go to ChatGPT at all. A value is a model from the account's own list, or <code>@selected</code> for the model you pick in the console.</p>

<p>Signing in has one catch. The sign-in returns to <code>127.0.0.1</code>, which is the computer running the browser, not the server. If the coordinator runs on another machine, the browser can't hand anything back to it directly.</p>

<p>So the console gives you a <strong>one-time pairing code</strong>, and you run a small helper on your own computer:</p>

<pre><code>inferhub-signin --hub https://hub.example.com
Pairing code from the coordinator's console: ihp_…
Waiting for the browser on http://127.0.0.1:1234/auth/callback …
The coordinator's provider 'plan' is signed in and may use the ChatGPT plan.</code></pre>

<p>The helper opens your browser, you sign in, and the result goes to the coordinator once, over HTTPS. The coordinator checks the sign-in itself against OpenAI's keys and against a value it chose for this pairing, so a sign-in from anywhere else won't pass. The code works once and expires in ten minutes. The helper ships with the release for Windows, Linux and macOS.</p>

<h2>On a node</h2>

<p>A node can run ChatGPT on its own, or as one engine next to Ollama:</p>

<pre><code>"Backend": { "Engines": {
  "ollama": { "Type": "ollama" },
  "plan":   { "Type": "chatgpt" }
} }</code></pre>

<p>You sign it in at the node itself with <code>chatgpt login</code>. On Windows that works with the installed service too: you run it as yourself, your browser opens, and the service picks up the sign-in without a restart. The service never opens a browser on its own.</p>

<h2>What it will not do</h2>

<ul>
<li><strong>It never falls back to a paid API.</strong> A ChatGPT provider with an API key in its configuration doesn't start.</li>
<li><strong>Signed in is not enough.</strong> Using the plan is a permission you grant during sign-in. Without it the account shows as signed in and every request is refused with a 403 that says why.</li>
<li><strong>Errors keep their meaning.</strong> A plan usage limit comes back as a 429 and pauses that account for a few minutes. A region or permission problem is a 403. A session that ended is a 401.</li>
<li><strong>Tokens stay put.</strong> They are encrypted on disk and never appear in a URL, the console, the status page or a log.</li>
</ul>

<h2>What we did not test</h2>

<p>We checked every part against OpenAI's real token, key and Responses endpoints, and ran the whole flow end to end against a stand-in for OpenAI. We did not sign in a real ChatGPT account before the release. Whether OpenAI lets a self-hosted app use a plan, and especially from a remote server, is OpenAI's decision. The sign-in shows what was granted.</p>

<p>Running it also found a bug before release: the new console panel asked for the admin key with a pop-up as soon as the page loaded. Now it only asks when you click something.</p>

<p><a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.68.0">InferHub 3.68.0 on GitHub</a> · <a href="https://inferhub.devart.solutions/#idocs_chatgpt">Documentation</a></p>
