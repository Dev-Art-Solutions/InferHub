slug: inferhub-3-55-a-bulgarian-voice-on-a-cpu
id: 6abd6e3b0bb5c39555cba334 (EN-visible, BG-hidden; rendered with real <p>, checked)
title_en: InferHub 3.55: a Bulgarian voice that runs on a CPU, fetched by name
excerpt_en: InferHub's only Bulgarian voice needed its own 9 GB image and a GPU. 3.55 adds a 63 MB one that runs on the CPU, and a way to get any catalogued voice onto a node by naming it: pinned, hash-checked, and offered without a restart.

<p>Until today InferHub had one Bulgarian voice, <code>bg-tts-v5</code>, added in 3.49. It sounds good and has two speakers. It is also a 250-million-parameter model with its own ~9 GB image, and it needs a GPU to be usable. That is a lot to ask of somebody who wants a Bulgarian sentence read aloud on a small box.</p>

<p>The Piper project has since published <strong><code>bg_BG-dimitar-medium</code></strong>, a 63 MB Bulgarian voice (MIT, trained on CC0 data). It runs in the <code>:tools</code> image that already does speech, on the CPU. On the machine we built this on, it read 5.2 seconds of Bulgarian in 1.5 seconds.</p>

<h2>The voice was never the hard part</h2>

<p>Piper could already speak with any voice you put on the box. Getting it there was the problem. Until now you opened a shell inside the container, downloaded two files from a branch that can change under you, checked nothing, and restarted. 3.55 replaces all of that with one setting:</p>

<pre><code>Tools__Speech__Voices__0=bg_BG-dimitar-medium</code></pre>

<ul>
<li><strong>The id is looked up in a small catalogue that ships in the image.</strong> Each entry pins a commit of <code>rhasspy/piper-voices</code> plus the size and sha256 of the voice's two files.</li>
<li><strong>Nothing is installed until it matches.</strong> Both files are downloaded and checked first, and only then renamed into place, config last. If one file does not match, the log shows both hashes and neither file is installed, not even the one that was right.</li>
<li><strong>No restart.</strong> The download runs in the background. The worker tells the node it is fetching, then offers the voice as soon as it lands, and no request waits for it.</li>
<li><strong>Nothing is fetched unless you name it.</strong> The catalogue ships entries, not a default voice. A default voice would be a default language, and a confident answer in the wrong one is worse than none. <code>Tools:AllowModelDownload</code> still has the last word: with it off, the node logs the two URLs to fetch by hand.</li>
<li><strong>A voice id becomes a file name, so it is checked.</strong> Anything other than letters, digits, <code>_</code> and <code>-</code> stops the node at startup, naming the key.</li>
</ul>

<h2>Checked on the published image</h2>

<p>We pulled <code>inferhub-node:3.55.0-tools</code> from GHCR and started it on an empty volume with no GPU, setting only environment variables. The worker logged "fetching", then "ready", within seconds. The voice directory held exactly the two files and nothing half-written. A 79-character Bulgarian sentence came back as 5.1 seconds of 22 kHz audio. The first call took 1.24 s because it loaded the voice, and the second took 0.24 s. Streamed, the first byte arrived in 0.11 s. After a restart the voice was offered at once and nothing was downloaded again. With <code>../evil</code> as the voice id, the node refused to start and named the key.</p>

<p>The tests run the real worker against a local web server standing in for Hugging Face. We also broke the code on purpose twice, renaming before checking and not reporting the download, and each time a test went red.</p>

<h2>What we have not checked</h2>

<p>How it sounds to a Bulgarian listener. The phonemes are Bulgarian and the audio is real speech, but it is a medium-quality, single-speaker voice. <code>bg-tts-v5</code> still sounds better. It is now the choice for a box with a card, not the only Bulgarian voice there is.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.55.0">v3.55.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_voice_catalogue">voices by name</a>.</p>
