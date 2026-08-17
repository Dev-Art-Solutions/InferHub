# Social copy — v3.26.0 (phase 58, the video catalogue)

**No image, deliberately.** Same reason as last release: the obvious visual is a frame of a video and
nothing has been rendered on a card. A table of VRAM figures is not a picture.

**Blog post is live:** https://devart.solutions/blog/inferhub-3-26-a-catalogue-of-one-proves-nothing

---

## Facebook

**InferHub 3.26 — a catalogue of one proves nothing**

Last release we shipped video generation and exactly one model. Which meant every field the recipe
format had gained for video — frame rate, duration list, VRAM figure, scheduler override — was
exercised by a single file that agreed with itself about everything.

3.26 adds two more, and each one makes a field able to be wrong.

**CogVideoX-2b runs at 8 fps.** Wan runs at 16, and our worker treated 16 as a default when a recipe
did not say. Put those together and a 49-frame clip is encoded at twice its rate. Nothing errors. The
file plays. It is simply twice as fast as the model intended, and what a person concludes is "this
model is bad at motion" rather than "the encoder used the wrong number".

So the fallback is gone: a video recipe with no frame rate, an empty duration list, or a default
duration outside its own list is skipped and logged **by name**, before the node declares the
capability. The failure moved out of somebody's four-minute job and into a line in a log at startup.

**And the 14B model ships the same scheduler value as the small one** — `flow_shift: 3.0`, which is
the 480p number, in a 720p model's own config. The override we added last release "in case the repo
does not say so" turned out to be the only thing between a 720p render and the wrong schedule.

**wan-t2v-14b-720p is also the first model we ship that a 24 GB card is not offered.** It declares
24 GB against about 22.5 GB of headroom, so a node with one never declares it, the hub never routes
to it, and nobody meets the ceiling four minutes in. That gate has existed for ten releases and had
never withheld anything.

Nothing ran on a GPU: both VRAM figures are arithmetic over the model repositories' own file
listings plus an activation allowance nobody has measured, and we say so in the notes.

Still zero new dependencies — two JSON files and about eighty lines of Python.

https://inferhub.devart.solutions/#idocs_video_catalogue

#selfhosted #opensource #dotnet #AI #video #GPU

---

## X / Twitter

**Post (271 characters X-weighted, URL counted as 23)**

InferHub 3.26: two more video models, and the second one found a bug a test could not.

CogVideoX runs at 8 fps. Our fallback was 16. A 49-frame clip then encodes at double speed — no
error, just a model that looks bad at motion.

The fallback is gone.

https://inferhub.devart.solutions/#idocs_video_catalogue

**Optional thread**

2/ A catalogue of one proves nothing about its own fields. Every field the video seam introduced was
exercised by a single recipe that agreed with itself. Two more recipes is the release.

3/ The 14B Wan repo ships flow_shift: 3.0 — the 480p value — in a 720p model. We added the override
last release "in case the repo does not say so". It does not say so.

4/ wan-t2v-14b-720p declares 24 GB and a 24 GB card has ~22.5 GB of headroom, so such a node never
declares it. First time in ten releases that gate has withheld anything we ship.

5/ A video recipe with no VRAM figure is now not declared at all. An image recipe with the same
silence still is. The difference is what the silence costs: a few GB for a picture, a whole card for
a clip.

6/ Nothing ran on a GPU. Both VRAM numbers are arithmetic over the repos' file listings plus an
activation allowance nobody measured, and the release notes say exactly that.
