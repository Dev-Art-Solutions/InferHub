# Social — v3.19.0

Post manually. **Character counts below are measured, not estimated** (see the check at the bottom).
v3.18's first draft guessed them and three of four were over.

Lead with **the gap the console found**, not with "we shipped a console". "A model you configured is
not being served and nothing tells you" is a problem other people building fleets have right now;
"we added a panel" is not.

**Lead image: the panorama from 3.17.** The walkthrough is the body; the picture is what stops the
scroll.

## Facebook

InferHub 3.19: a fleet that makes pictures — and one page to run it from.

Six releases put text-to-image, async jobs, a seven-model catalogue, 360° panoramas and editing on a self-hosted fleet. This one makes them operable by somebody who did not write them.

The console we shipped in 3.13 answers "I turned it on and nothing happened". The image track produces a different confusion, and it needed a different answer: "it is running and I cannot tell how far along."

So the Images panel is job-centric. Every job with its place in line, a step bar at n of m, elapsed, which node has it, what it cost — and a cancel button that is honest about being cooperative: a job cancelled at step 27 of 28 may still finish, and if it does you get the picture.

But here is the part worth the release, and we did not know it was there until we built the panel.

A recipe whose licence you have not accepted, or one too big for the VRAM budget you declared, is NOT DECLARED by the node. That is deliberate: the fleet never routes at it, so nobody spends a request finding out and nobody gets an out-of-memory error inside a two-minute job at 2am.

It is also the worst possible diagnostic. At the hub, a model held back for its licence is indistinguishable from a model that does not exist, from one whose weights are still downloading, from one too big for the card, and from a typo in a config file.

Four causes. Four completely different fixes. One symptom: nothing.

So the node now reports every recipe it holds WITH A REASON — and the order of the checks is the order of the fixes. A recipe that is both unlicensed and oversized reports "unlicensed", because telling somebody to buy a bigger card for a model they may not be allowed to run is the wrong advice in the wrong order.

Three of those reasons go on the "needs attention" strip. The fourth — "weights still fetching" — deliberately does not: that is a fleet working correctly, and a strip that fires on every cold start is a strip people learn to close.

Two more things this release refuses to do:

The gallery is your browser's. Thumbnails live in that tab and vanish on reload. No server-side gallery, no history endpoint, no thumbnail cache — because a console gallery is exactly the pressure that turns a bounded in-memory job store into an image archive, and that request sounds harmless right up until it ends with a retention policy and a question about whose pictures those are.

And a node with no declared VRAM budget emits NO VRAM metrics at all — not a zero. "budget_mib=0" reads as "this box has no VRAM", which is a different and false statement from "nobody declared a budget on this box".

Six releases, a whole new modality, and still zero new dependencies: PyTorch is a child process, not a package.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.19.0

## X / Twitter — single post (275/280)

InferHub 3.19: six releases of image generation, operable from one page.

The find: a model held back for its licence or its VRAM is NOT DECLARED. Right routing — at the hub it looks identical to a typo.

Four causes, four fixes, one symptom: nothing.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.19.0

## X / Twitter — thread

**1/4** (260)

InferHub 3.19 closes the image track: a console for six releases of text-to-image, jobs, panoramas and editing.

3.13's console answers "I turned it on and nothing happened."

This track needed a different answer: "it's running and I can't tell how far along."

**2/4** (266)

The find, and we didn't know it was there until we built the panel:

A recipe held back for its licence or its VRAM budget is NOT DECLARED. Right routing — nobody spends a request finding out.

Worst diagnostic. At the hub it's identical to a model nobody installed.

**3/4** (260)

Four causes, four different fixes, one symptom: nothing.

So the node reports every recipe with a reason — and the ORDER of the checks is the order of the fixes.

Both unlicensed and oversized? It says "unlicensed". Buy-a-bigger-card is the wrong advice first.

**4/4** (260, incl. the link)

Also refused: a server-side gallery. Thumbnails live in your browser tab and vanish on reload.

That request is exactly what turns a bounded in-memory job store into an image archive.

Six releases, zero new deps. torch is a subprocess.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.19.0

## The count check

Run this before posting; it is the same one v3.18's file gained after the fact.

```
py - <<'PY'
import re
raw = open('social-v3.19.0.md', encoding='utf-8').read()
LINK = 'https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.19.0'
cost = lambda t: len(t) - (len(LINK) - 23) * t.count(LINK)   # t.co counts every link as 23

single = raw[raw.index('## X / Twitter — single post'):raw.index('## X / Twitter — thread')]
single = single.split('\n', 1)[1].strip()
print('single: %3d %s' % (cost(single), 'OK' if cost(single) <= 280 else 'OVER'))

thread = raw[raw.index('## X / Twitter — thread'):raw.index('## The count check')]
for i, p in enumerate(re.split(r'\*\*\d/4\*\*[^\n]*\n', thread)[1:], 1):
    p = p.strip()
    print('%d/4   : %3d %s' % (i, cost(p), 'OK' if cost(p) <= 280 else 'OVER by %d' % (cost(p) - 280)))
PY
```
