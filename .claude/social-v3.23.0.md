# Social — v3.23.0

Post manually. **No image** — and that is not laziness. The obvious visual here is a before/after
panorama, and no repaired panorama has been rendered on a card. Posting a synthetic one, or an
illustrative one, would be the claim this release is careful not to make.

The hook is the refusal, not the feature: *we measured a flaw for six releases and would not fix it.*
That is true of every product decision anybody has argued about, and InferHub is just where it got
written down.

## Facebook

Six releases ago we added 360° panoramas to InferHub, and with them a number: how far the left edge of a generated panorama is from its right edge.

That number matters because a 360° image wraps. If the two edges do not match, there is a seam — and the interesting part is that it does not look broken. Flat on a screen it looks perfectly fine. Somebody finds out three days later, wearing a headset, when the wall of a room does not meet itself.

So we measured it, reported it on every panorama, and over a threshold attached a warning. Not an error — a warning on a successful response, because a slightly visible seam is the operator's own aesthetic judgement and failing a two-minute render over a metric is the tool overriding the person.

And then nothing happened. For six releases we handed people a number and no way to act on it.

The reason was written down at the time, and it is worth reading again: a repair is a second generation pass the caller did not ask for, did not watch, and would be billed for.

Every clause of that is about consent. None of it is about repair being wrong.

So this release adds the asking, and changes nothing else. Two gates: the operator says what mechanism is permitted on their box, off by default, and the request chooses within it. Send no header and the response is byte-for-byte what the previous version returned, down to the headers. No threshold ever triggers a repair — a number that decides to spend somebody's GPU is the same overriding with a helpful expression on.

The part we are most pleased with is which mechanism is the default one.

The obvious repair is the expensive one: roll the join into the middle of the picture and let the model repaint that band. It is better, and it costs a second render.

The default is not that. It is a wrapped feather: halve the mismatch between the two edge columns and ramp each half back to zero over about 2% of the width, so the two edges meet exactly in the middle. It runs on the array the model already produced — milliseconds, no VRAM, no steps, nothing on the bill.

Because if the only answer to "my panorama has a line in it" costs a full second render, that is exactly the bill we refused to hand anybody in the first place.

And what the cheap one cannot do is written in the docs rather than discovered: it closes a difference in brightness, not a difference in structure. A seam cutting through a doorway comes back with no visible step in tone and the doorway still not lining up. That is what the expensive one is for.

One rule we would keep in anything that "improves" something automatically: measure, repair, measure again, and throw the repair away if the number did not improve. You get the original back, plus the mechanism you asked for and both numbers. A pass that quietly made your picture worse is the single outcome nobody would ever think to check for.

Last thing, because it costs one sentence to be honest: the inpainting path was verified by reading the pinned library's source, not by rendering anything. No repaired panorama has been looked at on a card for this release. A synthetic seam is a unit test, not evidence, and we would rather say that here than let a demo imply otherwise.

https://inferhub.devart.solutions

Blog: https://devart.solutions/blog/inferhub-3-23-we-measured-a-flaw-for-six-releases

## X / Twitter

**One post, not a thread.** Counted: 222 characters of text plus a link (X counts any link as 23
whatever its length), so **245 of 280**.

For six releases we measured a flaw in every 360° render we produced, reported it, and refused to
fix it — because a repair nobody asked for is a bill nobody agreed to.

v3.23 adds the asking. Not the fixing. The asking.

https://inferhub.devart.solutions
