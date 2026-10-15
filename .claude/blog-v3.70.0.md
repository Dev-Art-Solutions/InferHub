<!-- live 2026-10-15 as ID 6ad0c059c432db8875fb6fd3 (EN visible, BG hidden), created by this session -->
slug: inferhub-3-70-how-close-to-the-limit-and-video-from-google
title: InferHub 3.70: how close you are to the limit, and video from Google
excerpt: The console now shows how much of a ChatGPT plan's five-hour and weekly windows is used, an API key's rate limits, and the month's spend against a budget, for the coordinator and for each node. And a box with no GPU can render video through Google's Veo and Gemini Omni.

<p>3.70 is two things. The first is a panel that answers "how close am I to the limit?" for every cloud provider InferHub talks to. The second is video from Google, on a box with no GPU.</p>

<h2>Provider limits</h2>

<p>Until now you found out a provider was at its limit when a request failed. A ChatGPT sign-in showed "paused" after OpenAI had already said no.</p>

<p>The console now has a <strong>Provider limits</strong> panel. It lists every cloud provider, the coordinator's own and each node's own, side by side. Each node holds its own keys and its own ChatGPT sign-in, so each has its own limits, and the panel never adds them up.</p>

<p>For each provider it shows what the vendor says:</p>

<ul>
<li><strong>A ChatGPT sign-in:</strong> how much of the five-hour window and the weekly window is used, and when each one resets.</li>
<li><strong>An OpenAI or Anthropic API key:</strong> the requests and tokens per minute used, from the headers on every answer.</li>
<li><strong>Billing:</strong> this month's spend against a budget you set, when the vendor will tell you what you spent.</li>
<li><strong>A refusal:</strong> the vendor's last "no" in its own words, such as <code>insufficient_quota</code>, until the next request goes through.</li>
</ul>

<p>It updates on every answer and every five minutes. The Refresh button asks right away.</p>

<pre><code>"Providers": {
  "openai": {
    "Type": "openai-compatible",
    "Billing": { "MonthlyBudgetUsd": 50, "WarnPercent": 80 }
  }
}</code></pre>

<p>A node has the same setting for its own key, and reports its numbers to the coordinator.</p>

<h2>Where the numbers come from</h2>

<p>We show only what a vendor said. We don't guess a bill from token counts, because a guess that looks exact is worse than a dash.</p>

<ul>
<li><strong>OpenAI and Anthropic</strong> tell you what you spent only through an admin key. Give InferHub one, and it shows the month's spend. Without one, the panel shows your budget and says it can't see the spend.</li>
<li><strong>OpenRouter</strong> tells any key what it spent, including a limit set on the key.</li>
<li><strong>Google</strong> tells an API key nothing. A spending cap in Google Cloud shows up only as the refusal it causes.</li>
<li><strong>ChatGPT sign-in</strong> has no official usage API. InferHub reads the same signals OpenAI's own Codex app reads. If they are missing, the panel says so.</li>
</ul>

<p>A budget that runs out turns the row red. InferHub doesn't block requests on it. The vendor's own cap is the one that says no.</p>

<h2>Video from Google</h2>

<p>The video routes, <code>/v1/videos</code>, now work on a box with no GPU. Map a name to Veo or Gemini Omni on a Google provider:</p>

<pre><code>"Providers": {
  "google": {
    "Type": "gemini",
    "Media": {
      "video": { "veo": "veo-3.1-generate-preview", "omni": "gemini-omni-1.1-flash" }
    }
  }
}</code></pre>

<p>The clip is queued, shows progress, and is downloaded once, the same as a clip from a GPU node. A node can do the same with its own Google key.</p>

<ul>
<li><strong>Veo</strong> takes a few minutes. InferHub asks Google every ten seconds and fetches the clip when it's ready. It makes 4, 6 or 8 second clips, and 1080p only at 8 seconds.</li>
<li><strong>Gemini Omni</strong> answers in one call and picks its own length. InferHub reads the real length from the file.</li>
<li>A length or size the vendor doesn't make is refused before anything is sent, with a sentence that lists what works.</li>
<li>Your key goes only to Google's own server. Google hands the file over from another address, and InferHub fetches it from there without the key.</li>
</ul>

<p>There is no OpenAI video. OpenAI shut down Sora 2 and its video API on September 24, 2026.</p>

<h2>What we haven't checked</h2>

<p>We have no real ChatGPT account's numbers yet. We tested against stand-ins built from the vendors' documentation, and we haven't rendered a real Veo or Omni clip. The release notes list exactly what is and isn't known.</p>

<p>The release and the full notes are on <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.70.0">GitHub</a>. The docs are at <a href="https://inferhub.devart.solutions/#idocs_provider_limits">inferhub.devart.solutions</a>.</p>
