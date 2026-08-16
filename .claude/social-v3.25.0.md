# Social copy — v3.25.0 (phase 57, the video seam)

**No image, deliberately.** The obvious visual is a frame of the video, and no video has been
rendered on a card. A screenshot of a JSON job document is not a reason to post a picture.

---

## Facebook

**InferHub 3.25 — the API we did not have to invent**

Ten releases ago we shipped our own asynchronous API for image generation, and wrote down why:
OpenAI had no asynchronous Images API to adopt, and our rule is to speak the dialect clients already
speak and invent only where there is none.

Video has one. So 3.25 renders video on your own card through **OpenAI's Videos API** — create, poll,
fetch, delete — with nothing of ours bolted on. `client.videos.create(...)` in any OpenAI SDK now
works against a self-hosted mesh.

Two of that API's routes are **501s that say why** instead of 404s that read as "your server is old":
listing would enumerate your job ids, and we hold no such index — the id is the capability. Remix
needs the request kept after the job ends, and nothing durable here holds a prompt.

Underneath it is the same job model images have used since 3.15 — same queue, same per-step progress,
same cancel that leaves the worker holding its weights, same read-once retention, same optional
durability from last week's 3.24. That ordering was on purpose.

One model: Wan2.1-T2V-1.3B, Apache-2.0, 480p, 2–5 seconds. Four facts about it we read out of the
pinned library and the model's own config files rather than assuming, because each one fails
*plausibly* rather than loudly — including that "1.3B" names the transformer only: the text encoder
beside it is ~11B and the first download is about 29 GB.

And the honest bit: **no video has been watched.** The suite tests the whole API against a worker
that writes a real container with padded samples. Whether the model makes something worth watching is
a question for the release at the end of this track — the one that ships no feature at all and
instead pulls every published image and drives the fleet end to end.

Still zero new dependencies.

https://inferhub.devart.solutions/#idocs_video

#selfhosted #opensource #dotnet #AI #video #GPU

---

## X / Twitter

**Post (269 characters)**

InferHub 3.25 renders video on your own card.

The API is OpenAI's Videos API — not ours. In 3.15 we invented an async image API because OpenAI had
none. This time there was one, so we adopted it and added nothing.

No video has been watched yet. We say so.

https://inferhub.devart.solutions/#idocs_video

**Optional thread**

2/ Two of that API's routes are 501s that name the reason, not 404s that read as "old server".
Listing would enumerate your job ids and we hold no index — the id *is* the capability. Remix needs
the prompt kept after the job ends, and nothing durable here holds one.

3/ Underneath: the same queue, progress, cancel and read-once retention images have used since 3.15,
plus 3.24's optional durability. Doing durability first meant video inherited it instead of
re-arguing it. That ordering was written down six phases ago.

4/ "1.3B" names the transformer only. The text encoder beside it is ~11B, the weights are fp32 with
no fp16 variant, and the first pull is ~29 GB. We declare the VRAM figure from the encoder, not from
the number in the name.

5/ `seconds: 5` gets you 5.06 seconds, and the response says 5.06. Frames sit on a grid; the request
field is a label for an offer and the response reports the measurement. An unoffered duration is
refused naming the list, never rounded — a clip that is fine and wrong is worse than a 400.
