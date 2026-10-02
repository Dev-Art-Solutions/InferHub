slug: inferhub-3-57-editing-on-a-cpu-and-the-steps-we-would-have-billed-wrong
id: 6ac02e4cc0fb8f20160dfe7c (EN-visible, BG-hidden; rendered, checked)
title_en: InferHub 3.57: editing a picture on a CPU, and the steps we would have billed wrong
excerpt_en: The 4-step CPU image model can now edit and vary a picture. Counting its steps one by one showed that it runs all of them at any strength, so an edit at 0.5 would have been billed for half the work it did.

<p>3.56 gave a node with no graphics card a fast way to <em>draw</em> a picture: <code>lcm-dreamshaper</code>, 4 steps instead of 30. It could not <em>change</em> one. The only model that could edit on a CPU was Stable Diffusion 1.5, at 30 steps.</p>

<p>3.57 lets the fast model edit too, through the same OpenAI-shaped <code>/v1/images/edits</code> and <code>/v1/images/variations</code> routes as every other model. The edit reuses the model that is already loaded, so there is no second load and no extra memory. A 512×512 edit took about 10 seconds at 16 CPU threads. In the published container, at its default 4 threads, it took about 23 seconds and produced the same picture for the same seed.</p>

<p>We asked it for "the same lighthouse in winter, snow". At strength 0.5 the picture kept its composition and the rocks started to turn white. At the default, 0.75, it was clearly winter. At 1.0 it was a new picture.</p>

<h2>No mask</h2>

<p>An edit with a mask means "change only this part". That needs an inpainting pipeline, and the library we use has none for this kind of model. So an edit that sends a mask to <code>lcm-dreamshaper</code> gets a 400 that names the model and suggests <code>sd15</code>, which can inpaint. The refusal comes before the 4 GB model is loaded, not a minute later.</p>

<h2>The steps we would have billed wrong</h2>

<p>InferHub bills image work by the denoising steps that actually ran. For an edit, "strength" decides how much of the original picture to keep. On the Stable Diffusion models, a lower strength also means fewer steps: strength 0.6 at 30 steps runs 18 of them, and 18 is what gets billed.</p>

<p>We assumed this model worked the same way. Before writing any code we counted its steps one by one. At 4 steps and strength 0.25, 0.5, 0.75 and 1.0, it ran 4 every time. This model uses strength to decide <em>where to start</em> in its schedule, and then it runs every step anyway.</p>

<p>So an edit at 0.5 would have been billed 2 steps for 4, with a progress bar that ended at "3 of 2". We switched the fix off and ran the real model again, and that is exactly what happened. Now the model's recipe says that strength does not skip steps, and an edit is billed for all 4.</p>

<h2>A check for the next one</h2>

<p>We found this only because we looked. The next model might not be looked at so closely. So the worker now counts the steps each model actually runs and compares that with what it billed. If they differ, the node logs <code>STEP COUNT MISMATCH</code> with the pipeline's name, on the first edit, instead of billing wrong without anyone noticing.</p>

<h2>One thing we did not fix</h2>

<p>A variation is an edit with no prompt: "give me more like this". With nothing in the prompt to hold the subject, strength alone decides how much survives. At the default 0.75, our lighthouse kept its sunset and its coast and came back as a castle. If you want the same subject, send a lower strength.</p>

<h2>What we have not checked</h2>

<p>We have not run an edit with this model on a graphics card.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.57.0">v3.57.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_cpu_image_edit">editing on a CPU</a>.</p>
