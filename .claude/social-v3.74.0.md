# v3.74.0 social copy (unposted)

## Facebook

InferHub 3.74: a free slot worth waiting for.

Two nodes, each able to run one request for a model at a time, and three chats at once. The third used to go straight into one node's queue, and if the other node finished first, it sat idle while the chat waited.

Now the coordinator can hold that chat and send it to whichever node frees up first. One setting, Queue:SlotWaitSeconds, off by default. It never refuses: if no slot frees in time, the request goes on exactly as before. And requests leave one at a time, so one freed slot takes one request, not all of them.

https://blog.devart.solutions/blog/inferhub-3-74-a-free-slot-worth-waiting-for

## X

InferHub 3.74: when every node holding a model is full, the coordinator can hold the next chat and send it to the first node that frees a slot, instead of queueing it inside one engine. Off by default, never a 503.

https://blog.devart.solutions/blog/inferhub-3-74-a-free-slot-worth-waiting-for
