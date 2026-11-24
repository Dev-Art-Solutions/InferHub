# v3.112.0 — social copy

Blog: https://blog.devart.solutions/blog/inferhub-3-112-several-requests-at-once-on-a-group

## Facebook

InferHub 3.112: a model split across several machines now answers several people at once. Its engine puts their requests into the same pass across the machines. Until now, the coordinator could count the group as one slot and hand it one request at a time.

Measured in our lab (an 8B model over three machines, CPU only): four requests together gave 2.2× the tokens per second at 0 ms and 2.35× at a 120 ms round trip, with the same answers. Through InferHub, four chats sent together finished in 15 s instead of 28.5 s. Each request is a little slower than alone; everyone together is faster.

https://blog.devart.solutions/blog/inferhub-3-112-several-requests-at-once-on-a-group

## X

InferHub 3.112: a model split across machines serves several requests in one pass, and the coordinator now knows it.

Lab, 4 requests at once: 2.2× the tokens/s at 0 ms, 2.35× at 120 ms. Four chats: 15 s instead of 28.5 s.

https://blog.devart.solutions/blog/inferhub-3-112-several-requests-at-once-on-a-group
