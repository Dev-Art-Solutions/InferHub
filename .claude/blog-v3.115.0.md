id: 6b082728fbb7d277f95ea3c6
slug: inferhub-3-115-picture-and-video-models-from-a-link
title: InferHub 3.115: paste a link to a picture model, get a recipe that runs it

<p>Since 3.79 an InferHub node answers a pasted link to a text model: which of its engines can run it, with what context, and the arithmetic behind the answer. 3.115 does the same for picture and video models. These are the diffusers repositories on Hugging Face, the ones with a <code>model_index.json</code>.</p>
<p>A node does not run pictures with a general estimate. It runs them with <strong>recipes</strong>: one small file per model. A recipe pins a repository and a commit, and gives the sizes the model was trained at, its default steps, and the VRAM it was measured to need. So a link to a picture model gets its answer from the recipes.</p>
<pre><code>POST /api/control/fit          {"link": "Lykon/dreamshaper-8"}
POST /api/control/fit/install  {"link": "Lykon/dreamshaper-8", "engine": "diffusion"}</code></pre>

<h2>Three answers</h2>
<ul>
<li><strong>A recipe already names the repository.</strong> The SDXL base model is the <code>sdxl</code> recipe, and installing it is that recipe's download.</li>
<li><strong>A recipe runs the same pipeline.</strong> This is the common case: a fine-tune. Dreamshaper 8 is a <code>StableDiffusionPipeline</code>, like Stable Diffusion 1.5. So the node writes a recipe for it from <code>sd15</code>, with that recipe's sizes, steps and dtype, and the fine-tune's own commit, licence and weights. VRAM is <code>sd15</code>'s figure, scaled by the weights' size. When a model card names its base model, the recipe for that base is the template: Juggernaut XL names SDXL, so it gets <code>sdxl</code>'s settings.</li>
<li><strong>No recipe runs it.</strong> Then the node says so, and names the pipelines it does run. Kolors and Stable Cascade get that answer today.</li>
</ul>

<h2>What it will not do</h2>
<ul>
<li>It never runs code from the repository. A repository with Python files in it is refused, and so is one whose weights are only pickled <code>.bin</code> files, which can run code when they are loaded. A recipe the node writes loads safetensors only.</li>
<li>It writes nothing when you only look. The recipe is written when you install, to <code>/data/recipes</code> on the container, so an update keeps it.</li>
<li>It does not skip the owner's switches. The answer names each one that is missing: the image tool, model downloads, a licence the owner has not accepted, or a Hugging Face token for a gated model.</li>
</ul>
<p>A coordinator can do the same for a node far away, if the node's owner allowed it at install. The recipe is then written only after the owner's disk limits allow the download.</p>

<h2>On a real card</h2>
<p>On an RTX 3090 Ti, the node looked up Dreamshaper 8 in 3 seconds and wrote its recipe. It then installed the model and made a 512&times;512 picture in 5 seconds. The card used 3.4&nbsp;GB, and the recipe had said 4&nbsp;GB. Dreamshaper 7 was installed through a coordinator and generated through it.</p>

<h2>What we have not shown</h2>
<ul>
<li>A video fine-tune written and run.</li>
<li>A quantized template (FLUX, Qwen-Image) scaled to a fine-tune.</li>
<li>A gated repository end to end.</li>
</ul>
<p>With this release the "models that fit" track is complete. It covers text and pictures, from the app or from the coordinator, from a list or from a link.</p>
