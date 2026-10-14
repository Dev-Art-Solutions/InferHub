<!-- live 2026-10-14 as ID 6acf902d292aa013e4969c9d (EN visible); not created by this session — found already published from this draft, content matches -->
slug: inferhub-3-69-pictures-voices-and-transcripts-from-openai-and-google
title: InferHub 3.69: pictures, voices and transcripts from OpenAI and Google
excerpt: A coordinator or a node with no GPU can now make pictures, speak text and transcribe audio through OpenAI or Google with an API key, on the same routes a GPU node answers. A ChatGPT sign-in reads pictures in a chat but cannot make them; OpenAI does not allow it.

<p>Until now every picture, voice and transcript in InferHub came from a model running on a node with a GPU. The cloud providers on the coordinator could chat and nothing else.</p>

<p>3.69 lets OpenAI and Google do that work too, with an API key. A box with no GPU can answer the image and audio routes, and a client cannot tell the difference: same routes, same response shape, same usage units.</p>

<h2>What about ChatGPT sign-in?</h2>

<p>3.68 added sign-in with ChatGPT, so it was the first thing we checked. OpenAI's rules for apps that use a ChatGPT plan list image generation, audio input and transcription as <strong>not supported</strong>. So a ChatGPT sign-in can't make a picture or a voice, and InferHub doesn't try.</p>

<p>What it can do is <strong>read</strong> a picture. A picture in an OpenAI-style chat request reaches ChatGPT as an image input. That already worked in 3.68, and it is now tested end to end. Making pictures, voices and transcripts needs an API key.</p>

<h2>On the coordinator</h2>

<p>Add a <code>Media</code> map to an OpenAI or a Gemini provider:</p>

<pre><code>"Providers": {
  "openai": {
    "Type": "openai-compatible",
    "BaseUrl": "https://api.openai.com/v1",
    "Media": {
      "image":      { "gpt-image": "gpt-image-1.5" },
      "speak":      { "tts": "gpt-4o-mini-tts" },
      "transcribe": { "whisper": "whisper-1" }
    }
  },
  "google": {
    "Type": "gemini",
    "Media": {
      "image": { "imagen": "imagen-4.0-generate-001" },
      "speak": { "gemini-tts": "gemini-2.5-flash-preview-tts" }
    }
  }
}</code></pre>

<p>The name on the left is what clients ask for. The name on the right is the vendor's model. A model only goes to a vendor if it is in this map, and each name belongs to one provider.</p>

<p>When both a node and a vendor could answer, the provider's <code>Policy</code> decides. By default the vendor answers only when no node offers that model, so adding a key does not quietly move work off your own hardware.</p>

<p>If a vendor call fails, InferHub does <strong>not</strong> try again on a node. The vendor may already have charged for it, and you shouldn't pay twice for one request.</p>

<h2>On a node</h2>

<p>A node gets the same map in its own <code>CloudMedia</code> section, with its own key. It offers those models to the coordinator like any other tool, so the coordinator routes to it and its profile can switch them off. It works in solo mode too. The coordinator never sends a node a key.</p>

<h2>Refused before anything is sent</h2>

<p>Some requests a vendor would quietly get wrong. Those are refused up front with a message saying what works, and nothing leaves your machine:</p>

<ul>
<li>a negative prompt (neither vendor takes one)</li>
<li>a mask sent to Gemini, which edits by instruction</li>
<li>a Gemini voice as mp3 (Gemini returns raw audio, which InferHub can wrap as WAV but not encode as mp3)</li>
<li>a picture size that isn't one of Gemini's shapes</li>
<li>streaming a cloud voice</li>
</ul>

<p>Settings like <code>steps</code> or <code>seed</code> don't change what a cloud picture shows, so they aren't refused. The response lists them under <code>warnings</code> so you know they were ignored.</p>

<h2>Counted the same way</h2>

<p>Usage limits keep working. A cloud picture counts one step per megapixel, a voice counts characters, and a transcript counts the seconds the vendor reports. When the vendor reports no length, a transcript counts zero rather than a guess.</p>

<h2>Found before release</h2>

<p>Before tagging, we ran a real coordinator and a real node, configured the way an operator would, against a stand-in for both vendors. That found one bug: a request refused before anything was sent was being counted as a failure of the provider. Now only requests that actually reached a vendor are counted.</p>

<h2>Not yet tested against the real vendors</h2>

<p>We had no OpenAI or Google key, so no request reached either vendor before this release. Every request is built from their documentation and was checked against the stand-in. Your first real call will be the first one.</p>

<p>Next up is cloud video from Google's Veo. OpenAI shut down its Sora video API in September, so there is no OpenAI video to add.</p>
