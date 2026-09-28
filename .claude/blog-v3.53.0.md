slug: inferhub-3-53-a-panorama-back-as-a-cubemap
id: 6abad76a60bc092d75178f54 (EN-visible, BG-hidden; rendered with real <p>, checked)
title_en: InferHub 3.53: a 360° panorama, back as a cubemap
excerpt_en: A 360° panorama is the right format for a photo viewer and the wrong one for a game engine. InferHub 3.53 cuts it into the six square faces Unity, Godot and three.js want, when you ask, billed as the render it came from.

<p>Since 3.17, InferHub can render 360° panoramas on your own card. They come out <em>equirectangular</em>: a 2:1 picture with longitude across and latitude down. That is exactly what a 360° photo viewer wants, and almost nothing else. A game engine's skybox, three.js' <code>CubeTextureLoader</code>, Babylon's <code>CubeTexture</code> and KTX or DDS cube textures all want the same thing instead: <strong>six square faces</strong>, one for each side of a cube.</p>

<p>Until now you converted the panorama yourself. That sounds like a one-liner, and it is the kind of one-liner you get wrong at least once, because every tool documents its face order and orientation somewhere different. A face that is mirrored looks fine on its own and wrong the moment you stand inside it.</p>

<h2>Ask for a cube, get a cube</h2>

<p>3.53 adds one request header, <code>X-InferHub-Image-Reproject: cubemap</code>. The node renders the panorama as before, and then cuts it into a cube before handing it back. For a 2048×1024 render, the response looks like this:</p>

<pre><code>{
  "data": [{
    "b64_json": "iVBORw0KGgo…",
    "size": "2048x1024",
    "projection": "cubemap",
    "seam_delta": 0.014,
    "revised_prompt": null
  }]
}</code></pre>

<p>The PNG itself is 3072×512: six 512×512 faces in a row.</p>

<ul>
<li><strong>The order is <code>+X −X +Y −Y +Z −Z</code></strong>, the OpenGL cube-map order, which three.js, Babylon, KTX and DDS also use. Each face is oriented as the OpenGL cube-map table defines it. That table is the one place the convention is written down, rather than implied by some viewer's behaviour.</li>
<li><strong>Each face is a quarter of the panorama's width.</strong> A panorama has its full width in pixels for 360°, and a face covers 90°, so this keeps the detail at the horizon.</li>
<li><strong>The panorama's centre faces +X</strong>, the same origin three.js uses for its own equirectangular textures. The join, where the left edge meets the right, lands on the vertical centre line of −X.</li>
</ul>

<h2>You are billed for the render, not the strip</h2>

<p>Notice that <code>size</code> above still says 2048×1024. That is deliberate. InferHub meters image work in megapixel-steps, and the steps ran on a 2048×1024 render. If the response reported the strip's size instead, the same render would cost 25% less because of a header that changed nothing on the GPU. So <code>size</code> stays the render's, and <code>"projection": "cubemap"</code> is what tells you the bytes are a strip. The strip's shape follows directly from the size.</p>

<p>The projection reaches you everywhere it did before: in the response, on the async job document, and as <code>X-InferHub-Image-Projection</code> on the job's content route, which is the one request with no JSON to carry it.</p>

<h2>The seam first, then the cube</h2>

<p>Since 3.17 every panorama has reported <code>seam_delta</code>, how far its left edge is from its right edge, and since 3.23 you can ask for that join to be closed. The cube is cut <em>after</em> the seam is measured and, if you asked, repaired. So <code>seam_delta</code> still means what it always meant, and asking for a seam repair together with a cubemap gives you the useful combination: close the join, then cut the cube.</p>

<h2>What it costs, and what it refuses</h2>

<p>The cut is plain array arithmetic on the finished picture: about a second of CPU at the largest size, no VRAM and no diffusion steps. Because it spends nothing that needs limiting, there is no new setting for an operator to learn. A request without the header gets exactly the response 3.52 sent.</p>

<p>It refuses two things, before any GPU time is spent. A flat model (anything that does not render panoramas) gets a <code>400</code>, because a flat picture is not a sphere and cutting one into a cube produces something that looks like a cubemap and is not. An unknown value gets a <code>400</code> that names <code>cubemap</code>.</p>

<p>And if the cut itself ever fails, you still get your picture. The node returns the panorama, declares it <code>equirectangular</code>, and adds a <code>reproject</code> warning. A two-minute render is never thrown away because of its last step, and the response always says what the bytes really are.</p>

<h2>How we know the faces point the right way</h2>

<p>Orientation is the part that is easy to get wrong and hard to see, so the tests do not look at pictures. They feed the real worker code a panorama in which <strong>every pixel's colour is its own view direction</strong>. After the cut, each face pixel's colour decodes straight back to the direction it was sampled from. The tests compare that with the OpenGL table: each face's centre, where its top and right edges point, and that the join appears on −X and nowhere else.</p>

<p>Before trusting those tests, we broke the code on purpose by mirroring one face. Exactly the orientation test failed. The tests also run in CI now, instead of skipping because the build machine had no numpy.</p>

<h2>Checked on the image you would pull</h2>

<p>After the release was tagged, we ran the same check inside the published <code>3.53.0-diffusion</code> image, using the worker file and the numpy and Pillow versions that shipped in it. All six face centres decoded to their exact axes. The cut took 0.1 to 0.6 seconds depending on the size.</p>

<p>That run also found something the tests had missed. The Pillow version the image pins warns that one argument our code used is going away in the next major version. Nothing is broken in 3.53: the pinned version still accepts it. But the node deliberately falls back to the panorama when a cut fails, so after a future Pillow upgrade every cubemap would quietly have become a panorama with a warning on it. The Pillow we had tested with locally does not warn at all, which is how it slipped through. The argument is gone on <code>main</code>, and the tests now run against the pinned Pillow and treat a deprecation as an error.</p>

<h2>What we have not checked</h2>

<p>We have not cut a real <code>qwen-360</code> render on a card. That model needs about 19.5 GB of VRAM, and the cut runs on the finished image, so synthetic panoramas were used instead. We have also not opened a strip in Unity, Godot or three.js. The orientation is checked against the OpenGL table, not by eye.</p>

<p>Also in 3.53: the fix for a small race in 3.52, where a diffusion worker that had finished downloading its weights could stay idle, holding GPU memory, until its next request. This is the first published image with the fix.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.53.0">github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.53.0</a>. Documentation: <a href="https://inferhub.devart.solutions/#idocs_cubemap">inferhub.devart.solutions</a>.</p>
