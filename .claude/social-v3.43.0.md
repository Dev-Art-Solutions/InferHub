# Social copy — v3.43.0 (phase 78: auto-scaler scale-in)

Blog: https://blog.devart.solutions/blog/inferhub-3-43-the-scaler-turns-a-model-back-off
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.43.0

## X / Twitter

Since v3.41, InferHub's auto-scaler could turn a disabled model back on where demand justified it.
It never turned one back off.

v3.43 fixes that — and only when at least one other node still serves the model, so it can never
disable the last copy fleet-wide.

https://blog.devart.solutions/blog/inferhub-3-43-the-scaler-turns-a-model-back-off

## Facebook / LinkedIn

**InferHub's auto-scaler now scales in, not just out.**

Since v3.41, a background loop on the coordinator watched for demand pressure on a disabled-but-held
model and turned it back on automatically. What it never did was the opposite: once enabled, a model
stayed enabled forever, holding VRAM long after the reason it got turned on was gone.

v3.43 adds a new per-node "was this actually used recently" signal and a second tick that disables an
idle model — through the exact same profile-toggle path a human's own console click uses. The one hard
rule: it will never disable the last node still routing a model fleet-wide, no matter how idle. An idle
model with zero traffic looks identical to one nobody has needed yet, right up until someone asks.

Off by default, on top of scale-out's own off-by-default switch — an existing fleet sees no new writes
until an operator opts in to this direction too.

Full writeup: https://blog.devart.solutions/blog/inferhub-3-43-the-scaler-turns-a-model-back-off
