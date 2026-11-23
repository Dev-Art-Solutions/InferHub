# v3.111.0 — social copy

Blog: https://blog.devart.solutions/blog/inferhub-3-111-a-draft-model-on-the-head

## Facebook

InferHub 3.111: a model split across machines far apart gets faster with a small helper model on the head. The small model guesses a few tokens ahead, and one pass across all the machines checks them at once, so the network's delay is paid once per pass instead of once per token. The answer is the big model's own.

It had to earn its place first: we only ship such a change if it is at least 1.25× faster on every prompt. In our lab (an 8B model over three machines, a 1B helper, CPU only), at a 120 ms round trip it was 1.33–1.74× faster through InferHub, with the same text. On a local network it gains little, so it stays off by default.

https://blog.devart.solutions/blog/inferhub-3-111-a-draft-model-on-the-head

## X

InferHub 3.111: a split model across machines far apart, with a small draft model on the head. It guesses, one pass checks several tokens.

Lab, 120 ms round trip: 1.33–1.74× faster, the same text. Off by default.

https://blog.devart.solutions/blog/inferhub-3-111-a-draft-model-on-the-head
