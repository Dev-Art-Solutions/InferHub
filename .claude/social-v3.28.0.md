# Social copy — v3.28.0

Post by hand. Blog post: <https://blog.devart.solutions/blog/inferhub-3-28-we-shipped-video-three-times>
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.28.0>

---

## Facebook

InferHub 3.28 is out, and it ships no feature at all.

It is the release the whole video track deferred to: the day we pull every published image onto one
box with a real graphics card and drive it end to end. Six releases had each verified only the slice
they touched, on purpose, with the full check saved for one day at the end.

That day found that **video had never worked**. Not once, on any image we published.

3.25 shipped the capability. 3.26 gave it a catalogue of three models. 3.27 built it a console panel,
a metrics label and a usage budget. Every test passed the whole way — and no clip could be generated
through any of them, because the tool manifest in the image declared "image" and "image-edit" and had
never been told about "video". The node kept only the kinds the manifest named, which is exactly what
it should do, and quietly discarded what the worker was offering.

The tests never caught it because the tests declare their own manifests. They were all correct about
the code and silent about the file we ship.

With that fixed, it failed twice more in the same hour: two Python packages that diffusers uses and
does not declare, neither of them in our image. One killed every video request after the weights had
loaded. The other meant the 360° panorama model from 3.17 could never load at all.

Then the good part. A five-second clip: 81 frames, 378 seconds, and the response reporting 5.06
seconds rather than the 5 that was asked for. The first panorama anyone here has looked at. And the
seam repair we shipped in 3.23 without ever having seen one took a real seam from 0.04342 to zero
while touching 78 of 2048 columns — against a prediction of 80, made from numpy with no card in the
machine.

One finding no metric could have made: the panorama model's default of 25 steps is visibly
under-denoised, and the seam number moves by 0.0016 between that and a clean render at 50. A green
suite and a well-formed response describe the bad one as a success.

Written up in full, including the two things we did not fix and the one we now owe.

---

## X / Twitter

**Thread**

1/ InferHub 3.28 ships no feature.

It is the day we pulled every published image onto one box with a real GPU and drove it — the check
six releases had deferred.

It found that video had never worked. Not once, on any image we published.

2/ 3.25 shipped the capability.
3.26 catalogued three models.
3.27 gave it a console panel, a metrics label, a usage budget.

Every test green throughout.

No clip could be generated through any of them.

3/ The tool manifest in the image declared `image` and `image-edit`. It never learned about `video`.

A node keeps only the kinds its manifest names — correctly; that manifest is the operator's ceiling.
So the worker announced `video: wan-t2v-1.3b` and the node dropped it.

4/ Why no test caught it:

The tests declare their own manifests.

Every one correct about the code. Every one silent about the file we ship.

5/ Fixed it. Next request loaded 29 GB of weights, ran 2.5 minutes, died:

`NameError: name 'ftfy' is not defined`

diffusers imports it behind `if is_ftfy_available()` and calls it unconditionally. Not in our image.

6/ An hour later, on the panorama path:

`ValueError: PEFT backend is required for this method`

`peft` isn't in our image either — so the 360° model from 3.17 could never load at all. It failed in
a background prove, so the only symptom was a node saying "fetching" forever.

7/ Then the good part.

5-second clip: 81 frames, 378 s, 982 KB of H.264, response reporting `seconds: 5.06` — the measured
duration, not the label asked for.

First panorama anyone here has looked at.

8/ And the seam repair from 3.23, shipped without ever seeing a panorama:

before 0.04342 → after 0
78 of 2048 columns touched, rest bit-identical

3.23 predicted 80, from numpy, with no card in the machine.

9/ The finding no metric could make:

25 steps (the default) gives a visibly under-denoised panorama. 50 steps, same seed, clean.

`seam_delta` moves by 0.0016 between them.

A green suite and a well-formed envelope call the bad one a success.

10/ Not fixed, on purpose: the hub's dispatch deadline is one number for chat and for a six-minute
render. A per-capability deadline is new config — a feature — and this release ships none.

Documented with the measured figures instead.

11/ And what we owe: this release changes the images, and the day's matrix ran against the previous
ones. Confirming the fixes are in the artifact is 3.29's first job.

The price of fixing on the day we meant only to measure. Named, not buried.

---

## The one-liner, if only one thing gets posted

We spent a day verifying six releases and discovered that a feature we had shipped three times, given
a catalogue, a console panel and a metrics label, had never once been reachable — while every test
stayed green, because the tests describe the code and not the file in the image.
