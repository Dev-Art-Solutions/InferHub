# v3.110.0 — social copy

Blog: https://blog.devart.solutions/blog/inferhub-3-110-a-request-that-survives-a-member-leaving

## Facebook

InferHub 3.110: when one model is split across several machines and one of them disappears, the request that was running no longer fails. It waits at the coordinator while a spare takes the missing machine's place, and is sent again.

A request that arrives during that minute waits too, instead of getting an error. When the machine comes back, it takes its place back once it has stayed for 30 seconds, so a flaky connection costs one reload, not two.

In our lab (four machines in Docker), a request was running when we killed one member. It still answered, in 41 s instead of 21 s, with the same text.

https://blog.devart.solutions/blog/inferhub-3-110-a-request-that-survives-a-member-leaving

## X

InferHub 3.110: a machine leaves a split model mid-request, and the request still answers. It waits while a spare stands in, then goes again.

Lab: a member killed 6 s into a request; 200 in 41 s, the same text.

https://blog.devart.solutions/blog/inferhub-3-110-a-request-that-survives-a-member-leaving
